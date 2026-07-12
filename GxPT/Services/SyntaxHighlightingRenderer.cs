// SyntaxHighlightingRenderer.cs
// Syntax highlighting renderer for code blocks and inline code
// Target: .NET 3.5, Windows XP compatible (no external deps)

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // Structure for syntax-highlighted text segments
    public struct ColoredSegment
    {
        public string Text;
        public Color Color;
        public Color? BackColor; // optional background band behind the run (e.g. diff add/remove); null = none
        public Font Font;
        public int StartIndex;
        public int Length;

        public ColoredSegment(string text, Color color, Font font, int startIndex)
            : this(text, color, null, font, startIndex)
        {
        }

        public ColoredSegment(string text, Color color, Color? backColor, Font font, int startIndex)
        {
            Text = text;
            Color = color;
            BackColor = backColor;
            Font = font;
            StartIndex = startIndex;
            Length = text != null ? text.Length : 0;
        }
    }

    public static class SyntaxHighlightingRenderer
    {
        // Until highlighters are prewarmed, avoid touching SyntaxHighlighter at all to prevent UI stalls
        // caused by expensive regex compilation in its static constructor. MainForm marks readiness once
        // prewarm completes on a background thread.
        private static volatile bool _highlighterReady;

        // Async highlight cache and work queue for progressive rendering
        private static readonly Dictionary<string, List<ColoredSegment>> _cache = new Dictionary<string, List<ColoredSegment>>();
        private static readonly Dictionary<string, WorkItem> _pending = new Dictionary<string, WorkItem>();
        private static readonly List<string> _lifo = new List<string>(); // act as a stack (pop from end)
        private static readonly object _lock = new object();
        private static System.Threading.Thread _worker;

        private struct WorkItem
        {
            public string Key; public string Language; public bool Dark; public string Code; public Font Font;
        }

        // Raised on a worker thread when a key finishes; UI should marshal to the UI thread before invalidating
        public static event Action<string> SegmentsReady;

        /// <summary>
        /// Compute a stable small hash for cache keys (FNV-1a 64-bit).
        /// </summary>
        private static string Hash64(string s)
        {
            unchecked
            {
                const ulong offset = 14695981039346656037UL;
                const ulong prime = 1099511628211UL;
                ulong h = offset;
                if (s != null)
                {
                    for (int i = 0; i < s.Length; i++) { h ^= (byte)s[i]; h *= prime; }
                }
                return h.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        private static string MakeKey(string language, bool dark, string code)
        {
            if (language == null) language = string.Empty;
            return (dark ? "D" : "L") + "|" + language + "|" + Hash64(code ?? string.Empty);
        }

        /// <summary>
        /// Enqueue a code block for background highlighting. Uses LIFO so the most recently enqueued items
        /// are processed first. To highlight bottom-up, enqueue in top-to-bottom order.
        /// </summary>
        public static void EnqueueHighlight(string language, bool dark, string code, Font monoFont)
        {
            if (string.IsNullOrEmpty(code)) return;

            string key = MakeKey(language, dark, code);
            lock (_lock)
            {
                if (_cache.ContainsKey(key) || _pending.ContainsKey(key)) return;
                var wi = new WorkItem { Key = key, Language = language, Dark = dark, Code = code, Font = monoFont };
                _pending[key] = wi;
                _lifo.Add(key); // process last enqueued first
                EnsureWorker();
            }
        }

        private static void EnsureWorker()
        {
            if (_worker != null && _worker.IsAlive) return;
            _worker = new System.Threading.Thread(WorkerLoop);
            try { _worker.IsBackground = true; }
            catch { }
            try { _worker.Priority = System.Threading.ThreadPriority.BelowNormal; }
            catch { }
            _worker.Start();
        }

        private static void WorkerLoop()
        {
            while (true)
            {
                WorkItem wi;
                lock (_lock)
                {
                    if (_lifo.Count == 0)
                    {
                        // No more work; stop thread
                        _worker = null;
                        return;
                    }
                    int last = _lifo.Count - 1;
                    string key = _lifo[last];
                    _lifo.RemoveAt(last);
                    wi = _pending[key];
                    _pending.Remove(key);
                }

                try
                {
                    // Compute tokens and convert to colored segments using current theme
                    var tokens = SyntaxHighlighter.Highlight(wi.Language, wi.Code);
                    var segs = new List<ColoredSegment>();
                    if (tokens == null || tokens.Count == 0)
                    {
                        // Store without binding to a specific Font to avoid stale references across theme/font changes
                        segs.Add(new ColoredSegment(wi.Code, SyntaxHighlighter.GetTokenColorForTheme(TokenType.Normal, wi.Dark), null, 0));
                    }
                    else
                    {
                        int lastEnd = 0;
                        for (int i = 0; i < tokens.Count; i++)
                        {
                            var t = tokens[i];
                            if (t.StartIndex > lastEnd)
                            {
                                string gap = wi.Code.Substring(lastEnd, t.StartIndex - lastEnd);
                                segs.Add(new ColoredSegment(gap, SyntaxHighlighter.GetTokenColorForTheme(TokenType.Normal, wi.Dark), null, lastEnd));
                            }
                            var tokColor = SyntaxHighlighter.GetTokenColorForTheme(t.Type, wi.Dark);
                            var tokBack = SyntaxHighlighter.GetTokenBackgroundColorForTheme(t.Type, wi.Dark);
                            segs.Add(new ColoredSegment(t.Text, tokColor, tokBack, null, t.StartIndex));
                            lastEnd = t.StartIndex + t.Length;
                        }
                        if (lastEnd < wi.Code.Length)
                        {
                            string tail = wi.Code.Substring(lastEnd);
                            segs.Add(new ColoredSegment(tail, SyntaxHighlighter.GetTokenColorForTheme(TokenType.Normal, wi.Dark), null, lastEnd));
                        }
                    }

                    lock (_lock)
                    {
                        _cache[wi.Key] = segs;
                    }

                    var ev = SegmentsReady; // copy for thread-safety
                    if (ev != null)
                    {
                        try { ev(wi.Key); }
                        catch { }
                    }
                }
                catch
                {
                    // Swallow and continue; on failure, leave uncached so future calls can retry
                }

                // Yield to keep UI responsive
                try { System.Threading.Thread.Sleep(1); }
                catch { }
            }
        }

        internal static void MarkHighlighterReady()
        {
            _highlighterReady = true;
        }

        internal static bool IsHighlighterReady
        {
            get { return _highlighterReady; }
        }

        // Treat tabs as aligned to fixed stops (columns) when drawing/measuring.
        // We expand tabs into spaces because GenericTypographic ignores tab stops.
        private const int TabSize = 4; // columns

        private static string ExpandTabs(string text, int startColumn)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Build a new string expanding each '\t' to the number of spaces
            // needed to reach the next tab stop based on the current column.
            System.Text.StringBuilder sb = new System.Text.StringBuilder(text.Length);
            int col = startColumn;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '\t')
                {
                    int spaces = TabSize - (col % TabSize);
                    if (spaces == 0) spaces = TabSize;
                    sb.Append(' ', spaces);
                    col += spaces;
                }
                else
                {
                    sb.Append(ch);
                    // Newlines are not expected here (callers split on '\n'), but be safe.
                    if (ch == '\n' || ch == '\r')
                        col = 0;
                    else
                        col++;
                }
            }
            return sb.ToString();
        }

        // ---------- Segment generation ----------
        public static List<ColoredSegment> GetColoredSegments(string code, string language, Font monoFont)
        {
            return GetColoredSegments(code, language, monoFont, false);
        }

        public static List<ColoredSegment> GetColoredSegments(string code, string language, Font monoFont, bool dark)
        {
            var segments = new List<ColoredSegment>();

            if (string.IsNullOrEmpty(code))
                return segments;

            // Check cache first; if present, use it regardless of warm state
            string key = MakeKey(language, dark, code);
            List<ColoredSegment> cached = null;
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out cached))
                {
                    // Rebuild with the current monoFont to avoid stale/Disposed font references
                    var rebuilt = new List<ColoredSegment>(cached.Count);
                    for (int i = 0; i < cached.Count; i++)
                    {
                        var s = cached[i];
                        rebuilt.Add(new ColoredSegment(s.Text, s.Color, s.BackColor, monoFont, s.StartIndex));
                    }
                    return rebuilt;
                }
                // Not cached yet: ensure it's enqueued; draw plain until ready
                if (!_pending.ContainsKey(key))
                {
                    var wi = new WorkItem { Key = key, Language = language, Dark = dark, Code = code, Font = monoFont };
                    _pending[key] = wi; _lifo.Add(key); EnsureWorker();
                }
            }

            // Fallback plain segment; avoid touching SyntaxHighlighter on the UI thread until warmed
            Color fallback = IsHighlighterReady
                ? SyntaxHighlighter.GetTokenColorForTheme(TokenType.Normal, dark)
                : (dark ? Color.FromArgb(0xCD, 0xD6, 0xF4) : SystemColors.WindowText);
            segments.Add(new ColoredSegment(code, fallback, monoFont, 0));
            return segments;
        }

        // ---------- Rendering ----------
        public static void DrawColoredSegments(Graphics g, List<ColoredSegment> segments, Rectangle bounds)
        {
            if (segments.Count == 0)
                return;

            // Use StringFormat.GenericTypographic to minimize GDI+ padding
            using (var stringFormat = StringFormat.GenericTypographic)
            {
                stringFormat.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                stringFormat.Trimming = StringTrimming.None;

                float x = bounds.X;
                float y = bounds.Y;
                float lineHeight = 0;
                float maxWidth = bounds.Width;

                // Calculate line height from the first segment
                if (segments.Count > 0 && segments[0].Font != null)
                    lineHeight = segments[0].Font.Height;

                foreach (var segment in segments)
                {
                    if (string.IsNullOrEmpty(segment.Text))
                        continue;

                    // Handle newlines
                    string[] lines = segment.Text.Split('\n');

                    for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                    {
                        string line = lines[lineIndex];

                        if (lineIndex > 0)
                        {
                            // Move to next line
                            y += lineHeight;
                            x = bounds.X;
                        }

                        if (!string.IsNullOrEmpty(line))
                        {
                            // Expand tabs to spaces based on current column (monospace assumption)
                            // Determine current column from pixel x using space width.
                            SizeF spaceSize = g.MeasureString(" ", segment.Font, PointF.Empty, stringFormat);
                            int startColumn = (int)Math.Floor((x - bounds.X) / Math.Max(1f, spaceSize.Width));
                            string expanded = ExpandTabs(line, Math.Max(0, startColumn));

                            // Measure and draw the text using Graphics.DrawString with GenericTypographic
                            using (var brush = new SolidBrush(segment.Color))
                            {
                                // Check if text fits on current line
                                SizeF textSize = g.MeasureString(expanded, segment.Font, PointF.Empty, stringFormat);

                                if (x + textSize.Width > bounds.Right && x > bounds.X)
                                {
                                    // Word wrap - move to next line
                                    y += lineHeight;
                                    x = bounds.X;
                                    // Recompute startColumn after wrap
                                    startColumn = 0;
                                }

                                // Draw the text
                                g.DrawString(expanded, segment.Font, brush, new PointF(x, y), stringFormat);
                                x += textSize.Width;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Measures the rendered size of the colored segments inside a given width.
        /// This mirrors DrawColoredSegments line-wrap logic so measurement matches drawing.
        /// </summary>
        public static Size MeasureColoredSegments(Graphics g, List<ColoredSegment> segments, int maxWidth)
        {
            if (segments == null || segments.Count == 0)
                return Size.Empty;

            using (var stringFormat = StringFormat.GenericTypographic)
            {
                stringFormat.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                stringFormat.Trimming = StringTrimming.None;

                float x = 0f;
                float y = 0f;
                float lineHeight = segments[0].Font != null ? segments[0].Font.Height : 0f;
                float maxLineWidth = 0f;

                foreach (var segment in segments)
                {
                    if (string.IsNullOrEmpty(segment.Text))
                        continue;

                    var font = segment.Font;
                    string[] lines = segment.Text.Split('\n');
                    for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                    {
                        string line = lines[lineIndex];
                        if (lineIndex > 0)
                        {
                            // new visual line
                            maxLineWidth = Math.Max(maxLineWidth, x);
                            y += lineHeight;
                            x = 0f;
                        }
                        if (line.Length == 0)
                            continue;

                        // Expand tabs to spaces based on current column and measure
                        SizeF spaceSize = g.MeasureString(" ", font, PointF.Empty, stringFormat);
                        int startColumn = (int)Math.Floor(x / Math.Max(1f, spaceSize.Width));
                        string expanded = ExpandTabs(line, Math.Max(0, startColumn));

                        // basic wrap (same as draw path)
                        SizeF textSize = g.MeasureString(expanded, font, PointF.Empty, stringFormat);
                        if (x + textSize.Width > maxWidth && x > 0f)
                        {
                            // wrap
                            maxLineWidth = Math.Max(maxLineWidth, x);
                            y += lineHeight;
                            x = 0f;
                        }
                        x += textSize.Width;
                    }
                }
                maxLineWidth = Math.Max(maxLineWidth, x);
                return new Size((int)Math.Ceiling(Math.Min(maxLineWidth, maxWidth)), (int)Math.Ceiling(y + lineHeight));
            }
        }

        /// <summary>
        /// Measures colored segments without wrapping. Returns the width of the longest line
        /// and the total height as lineCount * lineHeight. Useful for horizontal scrolling viewports.
        /// </summary>
        public static Size MeasureColoredSegmentsNoWrap(Graphics g, List<ColoredSegment> segments)
        {
            if (segments == null || segments.Count == 0)
                return Size.Empty;

            using (var stringFormat = StringFormat.GenericTypographic)
            {
                stringFormat.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                stringFormat.Trimming = StringTrimming.None;

                float maxLineWidth = 0f;
                float currentLineWidth = 0f;
                float lineHeight = segments[0].Font != null ? segments[0].Font.Height : 0f;
                int lineCount = 1; // at least one line

                foreach (var segment in segments)
                {
                    if (string.IsNullOrEmpty(segment.Text))
                        continue;

                    var font = segment.Font;
                    string[] lines = segment.Text.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        if (i > 0)
                        {
                            // new visual line
                            if (currentLineWidth > maxLineWidth) maxLineWidth = currentLineWidth;
                            currentLineWidth = 0f;
                            lineCount++;
                        }
                        if (line.Length == 0)
                            continue;

                        // Expand tabs to spaces based on current column and measure
                        SizeF spaceSize = g.MeasureString(" ", font, PointF.Empty, stringFormat);
                        int startColumn = (int)Math.Floor(currentLineWidth / Math.Max(1f, spaceSize.Width));
                        string expanded = ExpandTabs(line, Math.Max(0, startColumn));

                        SizeF size = g.MeasureString(expanded, font, PointF.Empty, stringFormat);
                        currentLineWidth += size.Width;
                    }
                }

                if (currentLineWidth > maxLineWidth) maxLineWidth = currentLineWidth;
                int width = (int)Math.Ceiling(maxLineWidth);
                int height = (int)Math.Ceiling(lineHeight * Math.Max(1, lineCount));
                return new Size(width, height);
            }
        }

        /// <summary>
        /// Draw colored segments without wrapping. Applies horizontal offset and clips to the viewport.
        /// </summary>
        public static void DrawColoredSegmentsNoWrap(Graphics g, List<ColoredSegment> segments, Rectangle viewport, int scrollX)
        {
            DrawColoredSegmentsNoWrap(g, segments, viewport, scrollX, 0);
        }

        // As above, but also offsets the content vertically by scrollY (for bodies clipped to a fixed
        // number of rows and scrolled vertically). Content above/below the viewport is clipped out.
        public static void DrawColoredSegmentsNoWrap(Graphics g, List<ColoredSegment> segments, Rectangle viewport, int scrollX, int scrollY)
        {
            if (segments == null || segments.Count == 0)
                return;

            System.Drawing.Drawing2D.GraphicsState savedState = g.Save();
            try
            {
                using (var stringFormat = StringFormat.GenericTypographic)
                {
                    stringFormat.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                    stringFormat.Trimming = StringTrimming.None;

                    // Clip to the viewport and draw with a negative offset for scroll
                    g.SetClip(viewport);

                    float x = viewport.X - scrollX;
                    float y = viewport.Y - scrollY;
                    float lineHeight = segments[0].Font != null ? segments[0].Font.Height : 0f;

                    foreach (var segment in segments)
                    {
                        if (string.IsNullOrEmpty(segment.Text))
                            continue;

                        var font = segment.Font;
                        string[] lines = segment.Text.Split('\n');
                        for (int i = 0; i < lines.Length; i++)
                        {
                            string line = lines[i];
                            if (i > 0)
                            {
                                y += lineHeight;
                                x = viewport.X - scrollX;
                            }

                            // Full-width row band (e.g. diff add/remove). Painted at the fixed
                            // viewport left/width — not the scrolled x — so it highlights the whole
                            // visible row regardless of horizontal scroll, then the text draws on top.
                            // Diff color tokens are single-line (see DiffHighlighter), so a banded
                            // segment owns its row; this never paints over a neighbouring run.
                            if (segment.BackColor.HasValue && lineHeight > 0f)
                            {
                                using (var bgBrush = new SolidBrush(segment.BackColor.Value))
                                    g.FillRectangle(bgBrush, new RectangleF(viewport.X, y, viewport.Width, lineHeight));
                            }

                            if (line.Length == 0)
                                continue;

                            using (var brush = new SolidBrush(segment.Color))
                            {
                                // Expand tabs to spaces based on current column and draw
                                SizeF spaceSize = g.MeasureString(" ", font, PointF.Empty, stringFormat);
                                int startColumn = (int)Math.Floor((x - (viewport.X - scrollX)) / Math.Max(1f, spaceSize.Width));
                                string expanded = ExpandTabs(line, Math.Max(0, startColumn));

                                g.DrawString(expanded, font, brush, new PointF(x, y), stringFormat);
                                SizeF size = g.MeasureString(expanded, font, PointF.Empty, stringFormat);
                                x += size.Width;
                            }
                        }
                    }
                }
            }
            finally
            {
                g.Restore(savedState);
            }
        }
    }
}
