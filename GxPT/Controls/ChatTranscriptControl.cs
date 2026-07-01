// ChatTranscriptControl.cs
// WinForms owner-drawn chat transcript with basic Markdown rendering
// Target: .NET 3.5, Windows XP compatible

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using Krypton.Toolkit;

namespace GxPT
{
    public enum MessageRole { User, Assistant, System, Tool }

    [ToolboxItem(true)]
    public sealed partial class ChatTranscriptControl : UserControl
    {
        // ---------- Layout and rendering ----------
        private const int MarginOuter = 8;
        private const int GapBetweenBubbles = 6;
        private const int BubblePadding = 8;
        private const int BubbleRadius = 8;
        private const int ScrollStep = 40;
        // Configurable maximum bubble width (legacy pixels) and new percentage-based width
        // Note: Layout now prefers BubbleWidthPercent; MaxBubbleWidth remains for backward compatibility.
        private int _maxBubbleWidth = 700; // legacy pixel cap
        private int _bubbleWidthPercent = 90; // percent of usable transcript width
        [Browsable(true)]
        [Category("Layout")]
        [Description("[Legacy] Maximum width, in pixels, for individual message bubbles. If BubbleWidthPercent is set (>0), that is used instead.")]
        [DefaultValue(700)]
        public int MaxBubbleWidth
        {
            get { return _maxBubbleWidth; }
            set
            {
                int v = (value < 1) ? 1 : value;
                if (v == _maxBubbleWidth) return;
                _maxBubbleWidth = v;
                Reflow();
                Invalidate();
            }
        }
        [Browsable(true)]
        [Category("Layout")]
        [Description("Maximum bubble width as a percentage of the usable transcript width (1-100). When > 0, this overrides MaxBubbleWidth.")]
        [DefaultValue(90)]
        public int BubbleWidthPercent
        {
            get { return _bubbleWidthPercent; }
            set
            {
                int v = value;
                if (v < 1) v = 1; if (v > 100) v = 100;
                if (v == _bubbleWidthPercent) return;
                _bubbleWidthPercent = v;
                Reflow();
                Invalidate();
            }
        }
        private const int BulletIndent = 18;
        private const int BulletGap = 8;
        private const int CodeBlockPadding = 6;

        // Horizontal rule ("---" divider): vertical padding above and below the 1px line.
        private const int DividerVPad = 16;
        private const int DividerHeight = DividerVPad * 2 + 1;

        // Edit-diff record layout: a one-line clickable header, then (when expanded) a chromeless
        // diff body indented under it.
        private const int EditDiffHeaderPad = 3;  // vertical padding around the header text
        private const int EditDiffBodyGap = 2;    // gap between header and body
        private const int EditDiffBodyPad = 4;    // padding below the body
        private const int EditDiffScrollSlack = 6; // overflow under this many px doesn't get a scrollbar
        private const int InlineCodePaddingX = 3;
        private const int InlineCodePaddingY = 1;

        // Retry button drawn under a trailing error notice (re-runs the failed turn)
        private const int RetryBtnPadX = 8;   // horizontal padding around the label
        private const int RetryBtnPadY = 3;   // vertical padding around the label
        private const int RetryBtnGapTop = 4; // gap between the error text and the button

        // Bounded central content area width (designer-configurable)
        private int _maxContentWidth = 1000; // default maximum central area width

        [Browsable(true)]
        [Category("Layout")]
        [Description("Maximum width, in pixels, for the centered content area. If the control is narrower, it uses the control width.")]
        [DefaultValue(1000)]
        public int MaxContentWidth
        {
            get { return _maxContentWidth; }
            set
            {
                int v = (value < 1) ? 1 : value; // guard against invalid values
                if (v == _maxContentWidth) return;
                _maxContentWidth = v;
                Reflow();
                Invalidate();
            }
        }
        // Code block UI
        private const int CodeHScrollHeight = 12;      // height of horizontal scrollbar area
        private const int CodeHScrollThumbMin = 24;    // minimum thumb width
        private const int CodeCopyButtonHeight = 14;   // header area for copy button
        private const int CodeCopyButtonPad = 4;       // padding around copy text
        // Compute header height dynamically so the Copy button accommodates current font size
        private int GetCodeHeaderHeight()
        {
            int baseH = (_baseFont != null) ? _baseFont.Height : CodeCopyButtonHeight;
            // If we render language label in bold, account for its height too
            int boldH = (_boldFont != null) ? _boldFont.Height : baseH;
            int textH = Math.Max(baseH, boldH);
            // header height = text height + top/bottom padding, with a sane minimum
            return Math.Max(CodeCopyButtonHeight, textH + CodeCopyButtonPad * 2);
        }

        // Minimum width a code block needs so its header (left-aligned language label + right-aligned
        // Copy button) never overlaps. Matches the layout in DrawBlocks: the label sits at
        // Left + CodeCopyButtonPad, the Copy button ends at Right - CodeCopyButtonPad.
        private int GetCodeMinHeaderWidth(Graphics g, string language)
        {
            int copyTextW, langW = 0;
            using (var fmt = StringFormat.GenericTypographic)
            {
                fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                copyTextW = (int)Math.Ceiling(g.MeasureString("Copy", _baseFont, PointF.Empty, fmt).Width);
                if (!string.IsNullOrEmpty(language))
                {
                    Font labelFont = _boldFont ?? _baseFont;
                    langW = (int)Math.Ceiling(g.MeasureString(language, labelFont, PointF.Empty, fmt).Width);
                }
            }
            int copyW = copyTextW + CodeCopyButtonPad * 2;
            // Gap between label and button so they read as separate items (only when a label is shown).
            int gap = (langW > 0) ? CodeBlockPadding * 2 : 0;
            return 2 * CodeCopyButtonPad + langW + gap + copyW;
        }

        // The on-screen width of a code block: the content width (plus padding), but never narrower
        // than the header needs, and never wider than the available width. Used by the measure, draw,
        // and hit-test passes so all three agree.
        private int GetCodeBoxWidth(Graphics g, int contentNoWrapWidth, int maxWidth, string language)
        {
            int boxW = Math.Min(maxWidth, Math.Max(0, contentNoWrapWidth + 2 * CodeBlockPadding));
            int minHeaderW = GetCodeMinHeaderWidth(g, language);
            return Math.Min(maxWidth, Math.Max(boxW, minHeaderW));
        }

        // ---------- Graphviz "dot" blocks ----------
        // A fenced ```dot block is rendered as an image (via the bundled Graphviz). The same box
        // geometry is needed by the measure, draw, and hit-test passes, so it is computed once here.

        // Map a code-fence language to the Graphviz layout engine it requests, e.g. ```neato renders
        // with the neato engine. dot/graphviz/gv all mean the default hierarchical "dot" engine; neato
        // and fdp are compact force-directed layouts, twopi is radial, circo is circular - the layouts
        // that actually suit code diagrams. The neato_layout plugin also provides sfdp (large-graph) and
        // osage/patchwork (treemap), but those are deliberately not exposed here: they're rarely useful
        // for programming diagrams, so we don't tempt the model into them. Returns false for any other
        // language (rendered as ordinary highlighted code).
        private static bool TryGetGraphEngine(string lang, out string engine)
        {
            engine = null;
            if (string.IsNullOrEmpty(lang)) return false;
            string l = lang.Trim().ToLowerInvariant();
            switch (l)
            {
                case "dot":
                case "graphviz":
                case "gv":
                    engine = "dot"; return true;
                case "neato":
                case "fdp":
                case "twopi":
                case "circo":
                    engine = l; return true;
                default:
                    return false;
            }
        }

        // Languages whose code fences render as a Graphviz graph instead of highlighted source.
        private static bool IsGraphLanguage(string lang)
        {
            string engine;
            return TryGetGraphEngine(lang, out engine);
        }

        // Placeholder body height shown while a graph renders in the background.
        private const int GraphPlaceholderHeight = 40;

        // A streaming dot block arrives a few characters at a time; rendering each partial would spawn a
        // dot.exe per token and flicker. Only treat a source as renderable once its braces balance and it
        // ends with a closing brace. Until then the fence is shown as ordinary code. This is a heuristic
        // (braces inside quoted labels aren't excluded), which is fine for a completeness gate.
        private static bool IsRenderableGraphSource(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int open = 0, close = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '{') open++;
                else if (ch == '}') close++;
            }
            if (open == 0 || open != close) return false;
            string t = s.TrimEnd();
            return t.Length > 0 && t[t.Length - 1] == '}';
        }

        private struct GraphBlockLayout
        {
            public bool Failed;   // render failed / Graphviz unavailable -> caller falls back to a code block
            public bool Ready;    // Image is available
            public Bitmap Image;  // non-null when Ready
            public int BoxW, BoxH;
            public int HeaderH;
            public int DrawW, DrawH; // image draw size (downscaled to fit width)
        }

        // Compute the on-screen geometry of a dot block. Enqueues a background render if needed and
        // returns either the ready image size, a placeholder size, or Failed (so the caller renders
        // the fence as ordinary code). maxWidth is the available bubble content width.
        private GraphBlockLayout ComputeGraphLayout(Graphics g, CodeBlock c, int maxWidth)
        {
            var L = new GraphBlockLayout();
            L.HeaderH = GetCodeHeaderHeight();
            int minHeaderW = GetCodeMinHeaderWidth(g, c.Language);
            int avail = Math.Max(0, maxWidth - 2 * CodeBlockPadding);

            // Resolve the requested layout engine (dot, neato, ...); non-graph languages never reach here.
            string engine;
            if (!TryGetGraphEngine(c.Language, out engine))
            {
                L.Failed = true;
                return L;
            }

            // Until the (possibly streaming) source looks complete, render it as ordinary code.
            if (!IsRenderableGraphSource(c.Text))
            {
                L.Failed = true;
                return L;
            }

            GraphvizRenderer.EnqueueRender(engine, c.Text);
            GraphvizRenderer.GraphResult res;
            bool have = GraphvizRenderer.TryGetResult(engine, c.Text, out res);

            if (have && (res.Failed || res.Image == null))
            {
                L.Failed = true;
                return L;
            }

            if (have && res.Image != null)
            {
                L.Ready = true;
                L.Image = res.Image;
                int iw = res.Image.Width;
                int ih = res.Image.Height;
                int dw = iw, dh = ih;
                // Downscale to fit the available width, preserving aspect ratio; never upscale.
                if (iw > avail && iw > 0 && avail > 0)
                {
                    dw = avail;
                    dh = (int)Math.Round((double)ih * avail / iw);
                    if (dh < 1) dh = 1;
                }
                L.DrawW = dw;
                L.DrawH = dh;
                L.BoxW = Math.Min(maxWidth, Math.Max(dw + 2 * CodeBlockPadding, minHeaderW));
                L.BoxH = L.HeaderH + dh + 2 * CodeBlockPadding;
                return L;
            }

            // Pending: stable placeholder box until the render completes (OnGraphReady re-measures).
            L.DrawW = 0;
            L.DrawH = GraphPlaceholderHeight;
            int phBodyW = Math.Min(avail, 240);
            L.BoxW = Math.Min(maxWidth, Math.Max(phBodyW + 2 * CodeBlockPadding, minHeaderW));
            L.BoxH = L.HeaderH + GraphPlaceholderHeight + 2 * CodeBlockPadding;
            return L;
        }

        // Draw a dot block as a framed image with a code-style header (language label + Copy button
        // that copies the dot source). The header geometry intentionally matches the code-block path
        // so the existing copy hit-testing/hover/press state works unchanged.
        private void DrawGraphBlock(Graphics g, CodeBlock c, GraphBlockLayout L, int x0, int y, MessageItem owner, int codeIndex)
        {
            int headerH = L.HeaderH;
            Rectangle box = new Rectangle(x0, y, L.BoxW, L.BoxH);

            using (var sb = new SolidBrush(_clrCodeBack))
            using (var pen = new Pen(_clrCodeBorder))
            {
                g.FillRectangle(sb, box);
                g.DrawRectangle(pen, box);
            }

            int headerTop = box.Top;

            // Copy button (top-right) - copies the dot source (c.Text), matching the code-block layout.
            string copyText = "Copy";
            SizeF copySizeF;
            using (var fmt = StringFormat.GenericTypographic)
            {
                fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                copySizeF = g.MeasureString(copyText, _baseFont, PointF.Empty, fmt);
            }
            int copyW = (int)Math.Ceiling(copySizeF.Width) + CodeCopyButtonPad * 2;
            int copyH = headerH;
            Rectangle copyRect = new Rectangle(box.Right - CodeCopyButtonPad - copyW, headerTop, copyW, copyH);

            bool hoverCopy = (_hoverCopyItem == owner && _hoverCopyCodeIndex == codeIndex);
            if (hoverCopy || (owner == _copyPressedItem && codeIndex == _copyPressedCodeIndex))
            {
                bool pressed = (owner == _copyPressedItem && codeIndex == _copyPressedCodeIndex);
                using (var sb = new SolidBrush(pressed ? _clrCopyPressed : _clrCopyHover))
                using (var pen = new Pen(_clrCodeBorder))
                {
                    g.FillRectangle(sb, copyRect);
                    g.DrawRectangle(pen, copyRect);
                }
            }
            using (var brush = new SolidBrush(_clrLink))
            using (var fmt = StringFormat.GenericTypographic)
            {
                fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                var textPt = new PointF(copyRect.X + CodeCopyButtonPad, copyRect.Y + (copyRect.Height - _baseFont.Height) / 2f);
                g.DrawString(copyText, _baseFont, brush, textPt, fmt);
            }

            // Language label (top-left)
            string langLabel = c.Language;
            if (!string.IsNullOrEmpty(langLabel))
            {
                using (var brush = new SolidBrush(ForeColor))
                using (var fmt = StringFormat.GenericTypographic)
                {
                    fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                    var labelFont = _boldFont ?? _baseFont;
                    var langPt = new PointF(box.Left + CodeCopyButtonPad, headerTop + (headerH - labelFont.Height) / 2f);
                    g.DrawString(langLabel, labelFont, brush, langPt, fmt);
                }
            }

            // Header separator line
            using (var pen = new Pen(_clrCodeBorder))
            {
                int headerBottom = headerTop + headerH;
                g.DrawLine(pen, box.Left + CodeBlockPadding, headerBottom, box.Right - CodeBlockPadding, headerBottom);
            }

            int bodyTop = headerTop + headerH + CodeBlockPadding;
            if (L.Ready && L.Image != null)
            {
                int imgX = box.X + (box.Width - L.DrawW) / 2; // center horizontally
                if (imgX < box.X + CodeBlockPadding) imgX = box.X + CodeBlockPadding;
                Rectangle dest = new Rectangle(imgX, bodyTop, L.DrawW, L.DrawH);
                var savedInterp = g.InterpolationMode;
                var savedPixel = g.PixelOffsetMode;
                try
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.DrawImage(L.Image, dest);
                }
                catch { }
                finally
                {
                    g.InterpolationMode = savedInterp;
                    g.PixelOffsetMode = savedPixel;
                }
            }
            else
            {
                string msg = "Rendering graph…";
                using (var brush = new SolidBrush(ForeColor))
                using (var fmt = StringFormat.GenericTypographic)
                {
                    fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                    float mx = box.X + CodeBlockPadding;
                    float my = bodyTop + (GraphPlaceholderHeight - _baseFont.Height) / 2f;
                    g.DrawString(msg, _baseFont, brush, new PointF(mx, my), fmt);
                }
            }
        }

        // Colors (theme-aware); default to light
        private Color _clrAppBack = SystemColors.Window;
        private Color _clrAppText = SystemColors.WindowText;

        private Color _clrUserBack = Color.FromArgb(225, 240, 255);
        private Color _clrUserBorder = Color.FromArgb(160, 190, 220);

        private Color _clrAsstBack = Color.FromArgb(235, 235, 235);
        private Color _clrAsstBorder = Color.FromArgb(200, 200, 200);

        private Color _clrSysBack = Color.FromArgb(255, 250, 220);
        private Color _clrSysBorder = Color.FromArgb(210, 200, 150);

        private Color _clrCodeBack = Color.FromArgb(245, 245, 245);
        private Color _clrCodeBorder = Color.FromArgb(210, 210, 210);
        private Color _clrInlineCodeBack = Color.FromArgb(240, 240, 240);
        private Color _clrInlineCodeBorder = Color.FromArgb(200, 200, 200);
        private Color _clrLink = Color.FromArgb(0, 102, 204);
        private Color _clrError = Color.FromArgb(200, 0, 0); // red error-notice text (theme-adjusted)
        private Color _clrDiffAdd = Color.FromArgb(0, 128, 0);   // edit-record +N count (theme-adjusted)
        private Color _clrDiffDel = Color.FromArgb(200, 0, 0);   // edit-record -N count (theme-adjusted)
        private Color _clrCopyHover = Color.FromArgb(230, 230, 230);
        private Color _clrCopyPressed = Color.FromArgb(210, 210, 210);
        private Color _clrScrollTrack = Color.FromArgb(235, 235, 235);
        private Color _clrScrollThumb = Color.FromArgb(200, 200, 200);
        private Color _clrScrollTrackBorder = Color.FromArgb(210, 210, 210);
        private Color _clrScrollThumbBorder = Color.FromArgb(160, 160, 160);
        private bool _isDarkTheme;

        // ---------- Selection ----------
        private bool _isSelecting;                    // true while dragging
        private bool _hasSelection;                   // true when a selection exists
        private MessageItem _selectionItem;           // selection is scoped to one message
        private Point _selStartVirt;                  // selection anchor (virtual coords)
        private Point _selEndVirt;                    // selection caret (virtual coords)
        private bool _suppressLinkClick;              // avoid link activation after drag
        private Point _mouseDownClient;               // for drag threshold
        private Color _clrSelectionBack = Color.FromArgb(96, 51, 153, 255); // semi-transparent highlight


        // ---------- Fonts ----------
        private Font _baseFont;         // default UI font
        private Font _boldFont;
        private Font _italicFont;
        private Font _boldItalicFont;
        private Font _monoFont;         // code spans
        private Font _h1, _h2, _h3, _h4, _h5, _h6;
        // Cache for dynamically derived styled fonts based on a given base font (preserves size for headings)
        private readonly Dictionary<string, Font> _styledFontCache = new Dictionary<string, Font>();

        private readonly KryptonScrollBar _vbar;
        private int _contentHeight;
        private int _scrollOffset;
        // Queue a deferred reflow to run after current layout (avoids stale viewport sizes)
        private bool _reflowQueued;

        // Bumped whenever something that affects measured bubble sizes but does NOT reassign an item's
        // Blocks list changes (fonts, edit-diff collapse/registration). Each MessageItem caches the
        // generation it was measured under; a mismatch forces re-measurement. See MeasureBubbleCached.
        private int _layoutGeneration;
        // Coalesces the expensive full Reflow during a live resize drag: each WM_SIZE restarts it, so
        // the transcript is re-measured once the drag settles instead of on every pixel.
        private Timer _resizeDebounce;

        // Stick-to-bottom behavior during streaming to avoid calling ScrollToBottom each delta
        private bool _stickToBottom;

        // Use modern ContextMenuStrip instead of legacy ContextMenu
        private readonly ContextMenuStrip _ctx;
        private MessageItem _ctxHit;

        // Raised when the user selects Edit… on a user message via context menu
        public event Action<int, string> UserMessageEditRequested;
        // Raised when the user clicks the Retry button on a trailing error notice
        public event Action RetryRequested;
        // Raised when the user clicks an agent "View transcript" link (the custom gxpt-agent: scheme); the
        // host opens the read-only child transcript viewer instead of launching a browser.
        public event Action<string> AgentTranscriptLinkClicked;
        // Hover/drag state for code block UI
        private MessageItem _hoverCopyItem;
        private int _hoverCopyCodeIndex = -1;
        private MessageItem _copyPressedItem;
        private int _copyPressedCodeIndex = -1;
        // Hover/pressed state for the Retry button on a trailing error notice
        private MessageItem _hoverRetryItem;
        private MessageItem _retryPressedItem;
        private bool _draggingHScroll;
        private MessageItem _dragScrollItem;
        private int _dragScrollCodeIndex = -1;
        private Rectangle _dragScrollTrackRect; // track rect at drag start (virtual coords)
        private int _dragScrollContentWidth;    // content width at drag start
        private int _dragScrollViewportWidth;   // viewport width at drag start
        private int _dragStartMouseX;           // client X at drag start
        private int _dragStartScrollX;
        private MessageItem _hoverScrollItem;
        private int _hoverScrollCodeIndex = -1;
        private int _hoverScrollTableIndex = -1;
        private bool _dragScrollIsTable;        // dragging a table scrollbar vs code
        private bool _hoverScrollIsTable;       // hovering a table scrollbar vs code

        // Accumulator for high-precision (sub-120) vertical wheel deltas
        private double _wheelRemainderY;

        // ---------- Data ----------
        private sealed class MessageItem
        {
            public MessageRole Role;
            public string RawMarkdown;
            public Rectangle Bounds; // bubble bounds, virtual coords
            public int MeasuredHeight;
            public List<Block> Blocks; // parsed markdown
            public List<int> CodeScroll; // per-code-block horizontal scroll offsets
            public List<int> TableScroll; // per-table horizontal scroll offsets
            public List<AttachedFile> Attachments; // optional attachments to show as pills
            public List<Rectangle> AttachmentPillRects; // computed per-draw for hit testing
            // Link hit rectangles captured at draw time (virtual coordinates)
            public List<LinkHit> LinkHits;
            // Edit-diff header hit rectangles captured at draw time (virtual coords) for collapse toggling
            public List<EditDiffHit> EditDiffHits;
            // Edit-diff horizontal scrollbar track rects captured at draw time (virtual coords)
            public List<EditDiffScrollHit> EditDiffScrollHits;
            // Drawn inline text segments for selection/copy (paragraphs, headings, lists, table cells)
            public List<DrawnSeg> DrawnSegments;
            // Unique link run id counter per message (increments when a new link run starts)
            public int LinkRunSeq;
            // When true, a tiny "zdr" tag is drawn in the bubble's top-right corner (zero-retention
            // message). Only meaningful for User/Assistant bubbles; tool blocks never show it.
            public bool ShowZdrTag;
            // Retry button rect captured at draw time (virtual coords); Empty when not drawn
            public Rectangle RetryRect;

            // ---- Cached layout (see MeasureBubbleCached) ----
            // The result of the last MeasureBubble for this item, reused when none of the inputs that
            // affect its size have changed. Avoids re-running GDI text measurement for every block on
            // every Reflow (the dominant cost on long transcripts during resize/visibility changes).
            public bool LayoutValid;       // false until first measured / after invalidation
            public int LayoutWidth;        // the usableWidth this size was measured at
            public Size LayoutSize;        // cached MeasureBubble result
            public List<Block> LayoutBlocks; // Blocks reference the cache was built from (reassigned on any content edit)
            public bool LayoutRetry;       // IsRetryTarget(it) state the cache was built with
            public int LayoutGen;          // _layoutGeneration snapshot (bumped on font/theme/edit-diff changes)
        }

        private readonly List<MessageItem> _items = new List<MessageItem>();
        private MessageItem _hoverAttachItem; private int _hoverAttachIndex = -1;
        private MessageItem _pressAttachItem; private int _pressAttachIndex = -1;

        // ---------- Edit-diff records (collapsible, chromeless; data derived from tool-call args) ----------
        private struct EditDiffHit { public Rectangle Rect; public string Key; }
        private struct EditDiffScrollHit { public Rectangle Track; public Rectangle Body; public string Key; public int ContentWidth; public int ViewportWidth; }

        // Horizontal scroll offset per diff key (UI-thread only); 0 when absent.
        private readonly Dictionary<string, int> _editDiffScroll = new Dictionary<string, int>(StringComparer.Ordinal);
        // Drag state for an edit-diff scrollbar (self-contained; mirrors the code-block scroll drag).
        private bool _draggingEditDiffScroll;
        private string _dragEditDiffKey;
        private Rectangle _dragEditDiffTrack;
        private int _dragEditDiffContentWidth, _dragEditDiffViewportWidth, _dragEditDiffStartMouseX, _dragEditDiffStartScroll;

        private int GetEditDiffScroll(string key)
        {
            int v; return (!string.IsNullOrEmpty(key) && _editDiffScroll.TryGetValue(key, out v)) ? v : 0;
        }
        private void SetEditDiffScroll(string key, int v)
        {
            if (!string.IsNullOrEmpty(key)) _editDiffScroll[key] = Math.Max(0, v);
        }

        // A collapsible "tool record": a header label plus a highlighted body in some language.
        // Used for files__edit (diff), command__run (batch), web__search (plain), and the other tool
        // records — the caller (MainForm) builds the label/body/language per tool. An empty body makes
        // it a non-collapsible one-line label (e.g. "Deleted <path>"). HeaderText excludes the
        // disclosure triangle (added at draw time when there is a body) and the +/- line counts (drawn
        // separately, color-coded, from Added/Removed; -1 means "no counts for this record").
        private sealed class EditDiffData
        {
            public string HeaderText;
            public string Body;
            public string Language;
            // When true the body is rendered with the normal Markdown renderer (DrawBlocks) instead of
            // the syntax-highlighted code path - used for prose-style records like dispatch_agent.
            public bool BodyIsMarkdown;
            public int Added = -1;
            public int Removed = -1;
        }
        private readonly object _editDiffLock = new object();
        private readonly Dictionary<string, EditDiffData> _editDiffs = new Dictionary<string, EditDiffData>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _editDiffCollapsed = new Dictionary<string, bool>(StringComparer.Ordinal);

        // Registers (or refreshes) a tool record, keyed by its (persisted) call id. Called from the
        // streaming worker thread and the history reload path, so it is lock-guarded.
        public void RegisterToolRecord(string key, string headerText, string body, string language)
        {
            RegisterToolRecord(key, headerText, body, language, -1, -1);
        }

        // Overload carrying +/- line counts (added/removed) for an edit record, drawn color-coded next
        // to the header. Pass -1 for records without counts.
        public void RegisterToolRecord(string key, string headerText, string body, string language, int added, int removed)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_editDiffLock)
            {
                _editDiffs[key] = new EditDiffData
                {
                    HeaderText = headerText ?? string.Empty,
                    Body = body ?? string.Empty,
                    Language = language ?? "text",
                    BodyIsMarkdown = !string.IsNullOrEmpty(language)
                        && language.Equals("markdown", StringComparison.OrdinalIgnoreCase),
                    Added = added,
                    Removed = removed
                };
            }
            // The body text/counts for an expanded record changed, which alters its measured height
            // without reassigning any item's Blocks; force re-measure on the next Reflow.
            InvalidateAllLayout();
        }

        private EditDiffData GetEditDiffData(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            lock (_editDiffLock) { EditDiffData d; return _editDiffs.TryGetValue(key, out d) ? d : null; }
        }

        // Collapse state is UI-thread-only; default (absent) = collapsed.
        private bool IsEditDiffCollapsed(string key)
        {
            if (string.IsNullOrEmpty(key)) return true;
            bool c; return _editDiffCollapsed.TryGetValue(key, out c) ? c : true;
        }

        // The header line. With a body, a disclosure triangle precedes the label (collapsible);
        // without a body it is a plain one-line label (e.g. "Deleted <path>").
        private static string BuildEditDiffHeaderText(EditDiffData data, bool collapsed, bool hasBody)
        {
            string label = (data != null && !string.IsNullOrEmpty(data.HeaderText)) ? data.HeaderText : "(record)";
            if (!hasBody) return label;
            // ASCII "+"/"-" toggle: the Unicode disclosure triangles (U+25B8/U+25BE) are absent from
            // Tahoma on Windows XP and render as missing-glyph boxes; "+"/"-" exists in every font.
            return (collapsed ? "+" : "-") + " " + label;
        }

        // The "  (+12 −3)" suffix for an edit record (empty when the record carries no counts). Kept
        // separate from the header label so the +N can paint green and the −N red. The leading spaces
        // separate it from the label.
        private static string BuildEditDiffCountsText(EditDiffData data)
        {
            if (data == null || data.Added < 0 || data.Removed < 0) return string.Empty;
            // ASCII "-" (U+002D), not the Unicode minus sign U+2212 which is missing from Tahoma on XP.
            return "  (+" + data.Added + " -" + data.Removed + ")";
        }

        // Draw one run of the edit-record header at x (advancing x by the run's width) in the given
        // color, so the label and the green/red +N/-N counts can sit on one line.
        private static void DrawHeaderSeg(Graphics g, ref float x, float y, string text, Font font, Color color, StringFormat fmt)
        {
            if (string.IsNullOrEmpty(text)) return;
            using (var b = new SolidBrush(color))
                g.DrawString(text, font, b, new PointF(x, y), fmt);
            x += g.MeasureString(text, font, PointF.Empty, fmt).Width;
        }

        // ---------- Batch update state ----------
        // Coalesce expensive reflow/paint while adding many messages (e.g., when opening history)
        private int _batchDepth;
        private bool _batchNeedsReflow;
        private bool _batchWantsScrollToBottom;
        private int _batchStartIndex = -1;        // index of first item potentially affected in this batch
        private bool _batchAppendOnly = false;    // true when only new items were appended at the end

        // ---------- ctor ----------
        public ChatTranscriptControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);

            ApplyThemeFromSettings();

            _vbar = new KryptonScrollBar();
            _vbar.Orientation = ScrollBarOrientation.VERTICAL;
            _vbar.Width = 17; // keep the docked band the width of a native vertical scrollbar
            _vbar.Dock = DockStyle.Right;
            _vbar.Visible = true;
            // KryptonScrollBar raises Scroll (not ValueChanged) on user interaction; every programmatic
            // Value set already updates _scrollOffset alongside it, so wiring Scroll is sufficient.
            _vbar.Scroll += delegate
            {
                int maxOff = Math.Max(0, _contentHeight - Math.Max(0, ClientSize.Height));
                _scrollOffset = Math.Max(0, Math.Min(maxOff, _vbar.Value));
                Invalidate();
            };
            Controls.Add(_vbar);

            _ctx = new ContextMenuStrip();
            _ctx.Items.Add("Copy Message", null, delegate { if (_ctxHit != null) SafeClipboardSetText(_ctxHit.RawMarkdown ?? string.Empty); });

            _baseFont = this.Font;
            BuildFonts();

            this.AccessibleName = "Chat transcript";
            this.TabStop = true;

            // Coalesces full reflows during an active resize drag (see OnResize).
            _resizeDebounce = new Timer();
            _resizeDebounce.Interval = 75;
            _resizeDebounce.Tick += delegate
            {
                _resizeDebounce.Stop();
                Reflow();
                Invalidate();
            };

            // Listen for async highlight completions and invalidate the relevant region
            try
            {
                SyntaxHighlightingRenderer.SegmentsReady += OnSegmentsReady;
            }
            catch { }

            // Listen for async Graphviz render completions. Unlike highlight, a finished graph changes
            // the block's measured size (placeholder -> natural image size), so we re-measure as well.
            try
            {
                GraphvizRenderer.GraphReady += OnGraphReady;
            }
            catch { }
        }

        // When async highlight for any block completes, repaint to pick up colored segments progressively.
        private void OnSegmentsReady(string key)
        {
            if (!IsHandleCreated) return;
            try
            {
                // Marshal to UI thread
                if (this.InvokeRequired)
                {
                    try { this.BeginInvoke((MethodInvoker)delegate { OnSegmentsReady(key); }); }
                    catch { }
                    return;
                }
                Invalidate();
            }
            catch { }
        }

        // When an async Graphviz render completes, the dot block's size is now known (it was a
        // placeholder before), so invalidate cached layout, re-measure, and repaint.
        private void OnGraphReady(string key)
        {
            if (!IsHandleCreated) return;
            try
            {
                if (this.InvokeRequired)
                {
                    try { this.BeginInvoke((MethodInvoker)delegate { OnGraphReady(key); }); }
                    catch { }
                    return;
                }
                InvalidateAllLayout();
                Reflow();
                Invalidate();
            }
            catch { }
        }

        // Dispose is implemented in the Designer partial; avoid duplicate overrides here.

        // ---------- Batching API ----------
        // Use when adding or updating many messages to avoid per-item reflow/paint.
        public void BeginBatchUpdates()
        {
            _batchDepth++;
            if (_batchDepth == 1)
            {
                try { this.SuspendLayout(); }
                catch { }
                // Mark the starting point for potential append-only reflow
                _batchStartIndex = _items.Count;
                _batchAppendOnly = true;
            }
        }

        public void EndBatchUpdates()
        {
            EndBatchUpdates(false);
        }

        // When scrollToBottom is true, the view will jump to bottom once after the batch finishes.
        public void EndBatchUpdates(bool scrollToBottom)
        {
            if (_batchDepth <= 0) { _batchDepth = 0; return; }
            _batchDepth--;
            if (scrollToBottom) _batchWantsScrollToBottom = true;
            if (_batchDepth == 0)
            {
                try { this.ResumeLayout(false); }
                catch { }
                if (_batchNeedsReflow)
                {
                    // If we only appended new items since BeginBatchUpdates, we can reflow just the tail.
                    if (_batchAppendOnly && _batchStartIndex >= 0 && _batchStartIndex <= _items.Count)
                    {
                        ReflowAppendOnly(_batchStartIndex);
                    }
                    else
                    {
                        Reflow();
                    }
                    if (_batchWantsScrollToBottom) ScrollToBottom();
                    Invalidate();
                }
                _batchNeedsReflow = false;
                _batchWantsScrollToBottom = false;
                _batchStartIndex = -1;
                _batchAppendOnly = false;
            }
        }

        // Public helper to scroll by a wheel delta (positive=away from user, negative=toward)
        public void ScrollByWheelDelta(int wheelDelta)
        {
            try
            {
                if (!_vbar.Enabled) return;
                // Proportional pixel scroll: support precision deltas (e.g., trackpads) with remainder accumulation
                double pixelsD = -(wheelDelta / 120.0) * ScrollStep + _wheelRemainderY;
                int pixels = (int)System.Math.Truncate(pixelsD); // keep sign; leave fractional remainder for next tick
                _wheelRemainderY = pixelsD - pixels;
                if (pixels == 0) return; // nothing to move yet

                int view = System.Math.Max(0, ClientSize.Height);
                int max = System.Math.Max(0, _contentHeight - view);
                _scrollOffset = System.Math.Max(0, System.Math.Min(max, _scrollOffset + pixels));
                SyncVBarValue(_scrollOffset);
                Invalidate();
            }
            catch { }
        }

        // Called by the global router with screen coordinates and modifier keys for precise hover behavior.
        public void HandleHoverWheel(int wheelDelta, Point screenPoint, Keys modifiers)
        {
            try
            {
                Point clientPt = PointToClient(screenPoint);
                if ((modifiers & Keys.Shift) == Keys.Shift)
                {
                    var ui = HitTestCodeUI(clientPt);
                    if (ui.Hit && ui.ContentWidth > ui.ViewportWidth && ui.Item != null)
                    {
                        int hStep = Math.Max(16, ScrollStep);
                        // Proportional horizontal scroll for precision input; round to nearest pixel
                        int deltaX = (int)System.Math.Round(-(wheelDelta / 120.0) * hStep, MidpointRounding.AwayFromZero);
                        if (ui.IsTable)
                        {
                            int idx = ui.TableIndex;
                            if (ui.Item.TableScroll == null) ui.Item.TableScroll = new List<int>();
                            while (ui.Item.TableScroll.Count <= idx) ui.Item.TableScroll.Add(0);
                            int current = ui.Item.TableScroll[idx];
                            int maxScroll = Math.Max(0, ui.ContentWidth - ui.ViewportWidth);
                            int next = Math.Max(0, Math.Min(maxScroll, current + deltaX));
                            ui.Item.TableScroll[idx] = next;
                            Invalidate();
                            return;
                        }
                        else if (ui.CodeIndex >= 0)
                        {
                            int idx = ui.CodeIndex;
                            if (ui.Item.CodeScroll == null) ui.Item.CodeScroll = new List<int>();
                            while (ui.Item.CodeScroll.Count <= idx) ui.Item.CodeScroll.Add(0);
                            int current = ui.Item.CodeScroll[idx];
                            int maxScroll = Math.Max(0, ui.ContentWidth - ui.ViewportWidth);
                            int next = Math.Max(0, Math.Min(maxScroll, current + deltaX));
                            ui.Item.CodeScroll[idx] = next;
                            Invalidate();
                            return;
                        }
                    }

                    // Edit-diff body/scrollbar: shift+wheel scrolls it horizontally.
                    EditDiffScrollHit edh;
                    if (HitTestEditDiffScrollArea(clientPt, out edh) && edh.ContentWidth > edh.ViewportWidth)
                    {
                        int hStep = Math.Max(16, ScrollStep);
                        int deltaX = (int)System.Math.Round(-(wheelDelta / 120.0) * hStep, MidpointRounding.AwayFromZero);
                        int maxScroll = Math.Max(0, edh.ContentWidth - edh.ViewportWidth);
                        SetEditDiffScroll(edh.Key, Math.Max(0, Math.Min(maxScroll, GetEditDiffScroll(edh.Key) + deltaX)));
                        Invalidate();
                        return;
                    }
                }
                // Fallback to normal vertical scroll
                ScrollByWheelDelta(wheelDelta);
            }
            catch { }
        }


        public void RefreshTheme()
        {
            ApplyThemeFromSettings();
            // Themes don't change font metrics, but they can swap the mono font used for code/diff
            // measurement via BuildFonts elsewhere; invalidate to be safe (theme changes are rare).
            InvalidateAllLayout();
            Reflow();
            Invalidate();
        }

        private void ApplyThemeFromSettings()
        {
            // Determine light/dark from legacy setting
            string theme = null;
            try { theme = AppSettings.GetString("theme"); }
            catch { theme = null; }
            bool dark = !string.IsNullOrEmpty(theme) && theme.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
            _isDarkTheme = dark;

            // Resolve current color theme (defaults to Blue) and apply
            ThemeColors t = ThemeService.GetColors(dark);
            _clrAppBack = t.UiBackground;
            _clrAppText = t.UiForeground;
            _clrUserBack = t.UserBubbleBack;
            _clrUserBorder = t.UserBubbleBorder;
            _clrAsstBack = t.AssistantBubbleBack;
            _clrAsstBorder = t.AssistantBubbleBorder;
            _clrSysBack = t.SystemBubbleBack;
            _clrSysBorder = t.SystemBubbleBorder;
            _clrCodeBack = t.CodeBack;
            _clrCodeBorder = t.CodeBorder;
            _clrInlineCodeBack = t.InlineCodeBack;
            _clrInlineCodeBorder = t.InlineCodeBorder;
            _clrLink = t.Link;
            // Error-notice red: a deeper red on light themes, a brighter one on dark so it stays
            // legible against the background. Not part of ThemeColors (no chrome), derived from dark.
            _clrError = dark ? Color.FromArgb(255, 120, 120) : Color.FromArgb(200, 0, 0);
            // +N green / -N red for edit-record line counts, brighter on dark so they stay legible.
            _clrDiffAdd = dark ? Color.FromArgb(120, 200, 120) : Color.FromArgb(0, 128, 0);
            _clrDiffDel = dark ? Color.FromArgb(255, 120, 120) : Color.FromArgb(200, 0, 0);
            _clrCopyHover = t.CopyHover;
            _clrCopyPressed = t.CopyPressed;
            _clrScrollTrack = t.ScrollTrack;
            _clrScrollThumb = t.ScrollThumb;
            _clrScrollTrackBorder = t.ScrollTrackBorder;
            _clrScrollThumbBorder = t.ScrollThumbBorder;

            BackColor = _clrAppBack;
            ForeColor = _clrAppText;
            Invalidate();
        }

        private void BuildFonts()
        {
            DisposeFonts();
            _baseFont = this.Font ?? new Font("Tahoma", 9f);
            _boldFont = new Font(_baseFont, FontStyle.Bold);
            _italicFont = new Font(_baseFont, FontStyle.Italic);
            _boldItalicFont = new Font(_baseFont, FontStyle.Bold | FontStyle.Italic);

            try { _monoFont = new Font("Consolas", _baseFont.Size); }
            catch { _monoFont = new Font("Courier New", _baseFont.Size); }

            _h1 = new Font(_baseFont.FontFamily, _baseFont.Size + 8, FontStyle.Bold);
            _h2 = new Font(_baseFont.FontFamily, _baseFont.Size + 6, FontStyle.Bold);
            _h3 = new Font(_baseFont.FontFamily, _baseFont.Size + 4, FontStyle.Bold);
            _h4 = new Font(_baseFont.FontFamily, _baseFont.Size + 2, FontStyle.Bold);
            _h5 = new Font(_baseFont.FontFamily, _baseFont.Size + 1, FontStyle.Bold);
            _h6 = new Font(_baseFont.FontFamily, _baseFont.Size, FontStyle.Bold);
        }

        private void DisposeFonts()
        {
            if (_boldFont != null) _boldFont.Dispose();
            if (_italicFont != null) _italicFont.Dispose();
            if (_boldItalicFont != null) _boldItalicFont.Dispose();
            if (_monoFont != null) _monoFont.Dispose();
            if (_h1 != null) _h1.Dispose();
            if (_h2 != null) _h2.Dispose();
            if (_h3 != null) _h3.Dispose();
            if (_h4 != null) _h4.Dispose();
            if (_h5 != null) _h5.Dispose();
            if (_h6 != null) _h6.Dispose();
            _boldFont = _italicFont = _boldItalicFont = _monoFont = null;
            _h1 = _h2 = _h3 = _h4 = _h5 = _h6 = null;
            // Dispose any cached styled fonts
            if (_styledFontCache != null && _styledFontCache.Count > 0)
            {
                try
                {
                    foreach (var kv in _styledFontCache) { if (kv.Value != null) kv.Value.Dispose(); }
                }
                catch { }
                _styledFontCache.Clear();
            }
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            BuildFonts();
            InvalidateAllLayout(); // font metrics changed: every cached bubble size is now stale
            Reflow();
            Invalidate();
        }

        // ---------- Public API ----------
        public void AddMessage(MessageRole role, string markdown)
        {
            if (markdown == null) markdown = string.Empty;
            var blocks = MarkdownParser.ParseMarkdown(markdown);
            AddParsedMessage(role, markdown, blocks, null);
        }

        public void AddMessage(MessageRole role, string markdown, List<AttachedFile> attachments)
        {
            if (markdown == null) markdown = string.Empty;
            var blocks = MarkdownParser.ParseMarkdown(markdown);
            AddParsedMessage(role, markdown, blocks, attachments);
        }

        // Number of messages currently in the transcript.
        public int MessageCount { get { return _items.Count; } }

        // Mark (or clear) the tiny "zdr" corner tag on a message by index. Pure paint change — no
        // reflow needed since the tag is drawn inside the bubble's existing padding.
        public void SetMessageZdrTag(int index, bool show)
        {
            if (index < 0 || index >= _items.Count) return;
            if (_items[index].ShowZdrTag == show) return;
            _items[index].ShowZdrTag = show;
            Invalidate();
        }

        // Tag the most recently added user message as ZDR (used live, right after a ZDR send appends
        // the user bubble). No-op if there is no user message.
        public void MarkLastUserMessageZdr()
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i].Role == MessageRole.User)
                {
                    if (!_items[i].ShowZdrTag) { _items[i].ShowZdrTag = true; Invalidate(); }
                    return;
                }
            }
        }

        // Add and return the index of the inserted message (to support targeted updates later)
        public int AddMessageGetIndex(MessageRole role, string markdown)
        {
            AddMessage(role, markdown);
            return _items.Count - 1;
        }

        public void ClearMessages()
        {
            _items.Clear();
            _contentHeight = 0;
            _scrollOffset = 0;
            UpdateScrollbar();
            Invalidate();
        }

        // Remove the last message (e.g. an empty assistant placeholder when a stream fails before any
        // content arrives). Safe no-op when empty.
        public void RemoveLastMessage()
        {
            if (_items.Count == 0) return;
            _items.RemoveAt(_items.Count - 1);
            Invalidate();
            ReflowSoon();
        }

        // Remove trailing error-notice messages (sentinel-only messages added by the form when a turn
        // fails) so a retried turn streams in their place. Safe no-op when none.
        public void RemoveTrailingErrorNotices()
        {
            bool removed = false;
            while (_items.Count > 0 && IsErrorNoticeItem(_items[_items.Count - 1]))
            {
                _items.RemoveAt(_items.Count - 1);
                removed = true;
            }
            if (removed)
            {
                Invalidate();
                ReflowSoon();
            }
        }

        // True when every block in the message is an error notice — the shape ShowTranscriptError
        // produces (a Tool-role message whose markdown is a single error sentinel line).
        private static bool IsErrorNoticeItem(MessageItem it)
        {
            if (it == null || it.Blocks == null || it.Blocks.Count == 0) return false;
            for (int i = 0; i < it.Blocks.Count; i++)
                if (it.Blocks[i] == null || it.Blocks[i].Type != BlockType.Error) return false;
            return true;
        }

        // The Retry button appears only on the transcript's last message and only when that message
        // is an error notice: retrying re-runs the turn that just failed, so once the conversation
        // moves on (or when no handler is wired) older notices never offer it.
        private bool IsRetryTarget(MessageItem it)
        {
            if (RetryRequested == null) return false;
            if (it == null || _items.Count == 0 || _items[_items.Count - 1] != it) return false;
            return IsErrorNoticeItem(it);
        }

        private Size GetRetryButtonSize()
        {
            Size txt = TextRenderer.MeasureText("Retry", _baseFont);
            return new Size(txt.Width + 2 * RetryBtnPadX, _baseFont.Height + 2 * RetryBtnPadY);
        }

        // Replace the content of the last message if it exists
        public void UpdateLastMessage(string markdown)
        {
            if (_items.Count == 0) return;
            var it = _items[_items.Count - 1];
            it.RawMarkdown = markdown ?? string.Empty;
            it.Blocks = MarkdownParser.ParseMarkdown(it.RawMarkdown);
            // reset code scrolls to match blocks
            it.CodeScroll = new List<int>();
            int codes = 0; foreach (var b in it.Blocks) if (b.Type == BlockType.CodeBlock) codes++;
            for (int i = 0; i < codes; i++) it.CodeScroll.Add(0);
            // reset table scrolls to match blocks
            it.TableScroll = new List<int>();
            int tables = 0; foreach (var b in it.Blocks) if (b.Type == BlockType.Table) tables++;
            for (int i = 0; i < tables; i++) it.TableScroll.Add(0);
            // Defer heavy layout to coalesce with other updates
            if (_batchDepth > 0)
            {
                _batchNeedsReflow = true;
                _batchAppendOnly = false; // modified existing item; cannot append-only reflow safely
            }
            else
            {
                Invalidate();
                ReflowSoon();
            }
        }

        // Replace content of a specific message by index; safe no-op if out of range
        public void UpdateMessageAt(int index, string markdown)
        {
            if (index < 0 || index >= _items.Count) return;
            var it = _items[index];
            it.RawMarkdown = markdown ?? string.Empty;
            it.Blocks = MarkdownParser.ParseMarkdown(it.RawMarkdown);
            // reset code scrolls to match blocks
            it.CodeScroll = new List<int>();
            int codes = 0; foreach (var b in it.Blocks) if (b.Type == BlockType.CodeBlock) codes++;
            for (int i = 0; i < codes; i++) it.CodeScroll.Add(0);
            // reset table scrolls to match blocks
            it.TableScroll = new List<int>();
            int tables = 0; foreach (var b in it.Blocks) if (b.Type == BlockType.Table) tables++;
            for (int i = 0; i < tables; i++) it.TableScroll.Add(0);
            // Defer heavy layout to coalesce with other updates
            if (_batchDepth > 0)
            {
                _batchNeedsReflow = true;
                _batchAppendOnly = false; // modified existing item
            }
            else
            {
                Invalidate();
                ReflowSoon();
            }
        }

        // Append text to the last message (useful for streaming); will keep as a single paragraph
        public void AppendToLastMessage(string delta)
        {
            if (delta == null) return;
            if (_items.Count == 0)
            {
                AddMessage(MessageRole.Assistant, delta);
                return;
            }
            var it = _items[_items.Count - 1];
            it.RawMarkdown = (it.RawMarkdown ?? string.Empty) + delta;
            it.Blocks = MarkdownParser.ParseMarkdown(it.RawMarkdown);
            // Defer heavy layout to coalesce with other updates
            if (_batchDepth > 0)
            {
                _batchNeedsReflow = true;
                _batchAppendOnly = false; // modified existing item
            }
            else
            {
                Invalidate();
                ReflowSoon();
            }
        }

        // Add a message with pre-parsed markdown blocks (useful to parse off the UI thread)
        public void AddParsedMessage(MessageRole role, string markdown, List<Block> blocks, List<AttachedFile> attachments)
        {
            if (markdown == null) markdown = string.Empty;
            if (blocks == null) blocks = MarkdownParser.ParseMarkdown(markdown);
            var item = new MessageItem
            {
                Role = role,
                RawMarkdown = markdown,
                Blocks = blocks,
                CodeScroll = new List<int>(),
                TableScroll = new List<int>(),
                Attachments = (attachments != null && attachments.Count > 0) ? new List<AttachedFile>(attachments) : null,
                AttachmentPillRects = null
            };
            int codeCount = 0; foreach (var b in item.Blocks) if (b.Type == BlockType.CodeBlock) codeCount++;
            for (int i = 0; i < codeCount; i++) item.CodeScroll.Add(0);
            int tableCount = 0; foreach (var b in item.Blocks) if (b.Type == BlockType.Table) tableCount++;
            for (int i = 0; i < tableCount; i++) item.TableScroll.Add(0);
            _items.Add(item);

            if (_batchDepth > 0)
            {
                _batchNeedsReflow = true;
                _batchWantsScrollToBottom = true;
                // still append-only if we keep appending to end
            }
            else
            {
                Reflow();
                ScrollToBottom();
                Invalidate();
                ReflowSoon();
            }
        }

        // ---------- Layout ----------
        private void Reflow()
        {
            // A disposed/handle-less control has no graphics surface; CreateGraphics would throw
            // ObjectDisposedException. This can happen when a deferred async rebuild (or a queued
            // reflow) runs after the tab was closed.
            if (!IsHandleCreated || IsDisposed) return;
            using (Graphics g = CreateGraphics())
            {
                int y = MarginOuter;
                int innerWidth = Math.Max(0, ClientSize.Width - _vbar.Width - 2 * MarginOuter);
                // Determine the bounded content area (centered) within which bubbles align
                int areaWidth = Math.Min(innerWidth, _maxContentWidth);
                int areaLeft = MarginOuter + Math.Max(0, (innerWidth - areaWidth) / 2);
                int usableWidth = ComputeUsableBubbleWidth(areaWidth);

                foreach (var it in _items)
                {
                    if (it.CodeScroll == null) it.CodeScroll = new List<int>();
                    if (it.TableScroll == null) it.TableScroll = new List<int>();
                    // ensure length matches number of code blocks
                    int codes = 0; foreach (var b in it.Blocks) if (b.Type == BlockType.CodeBlock) codes++;
                    while (it.CodeScroll.Count < codes) it.CodeScroll.Add(0);
                    if (it.CodeScroll.Count > codes) it.CodeScroll.RemoveRange(codes, it.CodeScroll.Count - codes);
                    // ensure length matches number of table blocks
                    int tables = 0; foreach (var b in it.Blocks) if (b.Type == BlockType.Table) tables++;
                    while (it.TableScroll.Count < tables) it.TableScroll.Add(0);
                    if (it.TableScroll.Count > tables) it.TableScroll.RemoveRange(tables, it.TableScroll.Count - tables);
                    Size bubbleSize = MeasureBubbleCached(g, it, usableWidth);
                    int xLeft;
                    if (it.Role == MessageRole.User)
                    {
                        // User messages: right-aligned, but ensure minimum width
                        int minUserWidth = Math.Min(usableWidth, Math.Max(200, usableWidth / 2));
                        bubbleSize.Width = Math.Max(bubbleSize.Width, minUserWidth);
                        xLeft = areaLeft + areaWidth - bubbleSize.Width;
                    }
                    else
                    {
                        // Assistant/System messages: left-aligned
                        xLeft = areaLeft;
                    }

                    it.MeasuredHeight = bubbleSize.Height;
                    it.Bounds = new Rectangle(xLeft, y, bubbleSize.Width, bubbleSize.Height);
                    y += bubbleSize.Height + GapBetweenBubbles;
                }

                _contentHeight = y + MarginOuter;
            }

            UpdateScrollbar();
        }

        // Compute the max usable bubble width based on percentage of the content area,
        // falling back to the legacy pixel cap if needed.
        private int ComputeUsableBubbleWidth(int areaWidth)
        {
            try
            {
                int perc = _bubbleWidthPercent;
                if (perc > 0)
                {
                    if (perc < 1) perc = 1; if (perc > 100) perc = 100;
                    int byPercent = (int)Math.Round(areaWidth * (perc / 100.0));
                    return Math.Max(1, Math.Min(areaWidth, byPercent));
                }
            }
            catch { }
            // Legacy path
            return Math.Max(1, Math.Min(areaWidth, _maxBubbleWidth));
        }

        // Reflow only newly appended items from startIndex to end, positioning them after existing content.
        // Assumes earlier items' bounds are already valid and control width hasn't changed significantly mid-batch.
        private void ReflowAppendOnly(int startIndex)
        {
            // See Reflow: never touch the graphics surface of a disposed/handle-less control.
            if (!IsHandleCreated || IsDisposed) return;
            if (startIndex < 0) { Reflow(); return; }
            using (Graphics g = CreateGraphics())
            {
                int innerWidth = Math.Max(0, ClientSize.Width - _vbar.Width - 2 * MarginOuter);
                int areaWidth = Math.Min(innerWidth, _maxContentWidth);
                int areaLeft = MarginOuter + Math.Max(0, (innerWidth - areaWidth) / 2);
                int usableWidth = ComputeUsableBubbleWidth(areaWidth);

                int y;
                if (startIndex == 0)
                {
                    y = MarginOuter;
                }
                else
                {
                    // Continue below the last previously laid-out item
                    var prev = _items[startIndex - 1];
                    y = prev.Bounds.Bottom + GapBetweenBubbles;
                }

                for (int idx = startIndex; idx < _items.Count; idx++)
                {
                    var it = _items[idx];
                    if (it.CodeScroll == null) it.CodeScroll = new List<int>();
                    if (it.TableScroll == null) it.TableScroll = new List<int>();
                    // ensure length matches number of code/table blocks
                    int codes = 0; foreach (var b in it.Blocks) if (b.Type == BlockType.CodeBlock) codes++;
                    while (it.CodeScroll.Count < codes) it.CodeScroll.Add(0);
                    if (it.CodeScroll.Count > codes) it.CodeScroll.RemoveRange(codes, it.CodeScroll.Count - codes);
                    int tables = 0; foreach (var b in it.Blocks) if (b.Type == BlockType.Table) tables++;
                    while (it.TableScroll.Count < tables) it.TableScroll.Add(0);
                    if (it.TableScroll.Count > tables) it.TableScroll.RemoveRange(tables, it.TableScroll.Count - tables);

                    Size bubbleSize = MeasureBubbleCached(g, it, usableWidth);
                    int xLeft;
                    if (it.Role == MessageRole.User)
                    {
                        int minUserWidth = Math.Min(usableWidth, Math.Max(200, usableWidth / 2));
                        bubbleSize.Width = Math.Max(bubbleSize.Width, minUserWidth);
                        xLeft = areaLeft + areaWidth - bubbleSize.Width;
                    }
                    else
                    {
                        xLeft = areaLeft;
                    }
                    it.MeasuredHeight = bubbleSize.Height;
                    it.Bounds = new Rectangle(xLeft, y, bubbleSize.Width, bubbleSize.Height);
                    y += bubbleSize.Height + GapBetweenBubbles;
                }

                _contentHeight = y + MarginOuter;
            }

            UpdateScrollbar();
        }

        // Invalidate every item's cached layout. Cheap (just bumps a counter); the next Reflow
        // re-measures lazily. Call when fonts or any non-content input to measurement change.
        // Thread-safe: RegisterToolRecord may run on the streaming worker thread.
        private void InvalidateAllLayout()
        {
            System.Threading.Interlocked.Increment(ref _layoutGeneration);
        }

        // Returns the bubble size for an item, reusing the cached result when none of the inputs that
        // affect it have changed: the usable width, the parsed Blocks (reassigned wholesale on any
        // content edit), the retry-button state, and the global layout generation. This turns a full
        // Reflow over an unchanged transcript from O(blocks) GDI measurement into O(items) comparisons.
        private Size MeasureBubbleCached(Graphics g, MessageItem it, int usableWidth)
        {
            if (it.LayoutValid
                && it.LayoutWidth == usableWidth
                && it.LayoutGen == _layoutGeneration
                && ReferenceEquals(it.LayoutBlocks, it.Blocks)
                && it.LayoutRetry == IsRetryTarget(it))
            {
                return it.LayoutSize;
            }

            Size s = MeasureBubble(g, it, usableWidth);
            it.LayoutSize = s;
            it.LayoutWidth = usableWidth;
            it.LayoutGen = _layoutGeneration;
            it.LayoutBlocks = it.Blocks;
            it.LayoutRetry = IsRetryTarget(it);
            it.LayoutValid = true;
            return s;
        }

        private Size MeasureBubble(Graphics g, MessageItem it, int maxBubbleWidth)
        {
            // Bubble width determined by content width + padding
            int textMax = Math.Max(40, maxBubbleWidth - 2 * BubblePadding);

            int h = BubblePadding; // top padding
            int wUsed = 0;

            // Maintain numbering counters across blocks so ordered lists continue through sublists
            var numberedCounters = new Dictionary<int, int>(); // key: indent level, value: last number emitted

            for (int i = 0; i < it.Blocks.Count; i++)
            {
                var blk = it.Blocks[i];
                // Reset numbering when leaving list context (paragraphs, headings, code)
                if (blk.Type != BlockType.NumberedList && blk.Type != BlockType.BulletList)
                    numberedCounters.Clear();

                Size sz = MeasureBlock(g, blk, textMax, numberedCounters);
                h += sz.Height;

                // Add spacing that matches the drawing code
                if (blk.Type == BlockType.Heading)
                    h += 4;
                else if (blk.Type == BlockType.Paragraph)
                    h += 2;
                else if (blk.Type == BlockType.CodeBlock)
                    h += 4;
                else if (blk.Type == BlockType.Error)
                    h += 2;
                // Lists don't add extra spacing after themselves

                wUsed = Math.Max(wUsed, sz.Width);
            }

            // Trailing error notice: reserve room for the Retry button drawn under the red text
            if (IsRetryTarget(it))
            {
                Size btn = GetRetryButtonSize();
                h += RetryBtnGapTop + btn.Height;
                wUsed = Math.Max(wUsed, btn.Width);
            }

            h += BubblePadding; // bottom padding

            // Attachment pills can be wider than the message text. Fold the widest desired pill
            // width (clamped to the available content width) into the content width so the bubble
            // grows to fit the pills instead of letting them overflow past its right edge.
            wUsed = Math.Max(wUsed, MeasureAttachmentsMaxPillWidth(it, textMax));

            // Compute bubble width based on measured text content
            // Ensure minimum content width for visual consistency
            int wContent = Math.Max(wUsed, 100);
            int bubbleW = Math.Min(maxBubbleWidth, wContent + 2 * BubblePadding);

            // Measure attachment pills using the actual content width the bubble will have
            int contentWidthForAttach = Math.Min(textMax, wContent);
            int attachH = MeasureAttachmentsHeight(it, contentWidthForAttach);
            if (attachH > 0)
            {
                h += attachH + 4; // small gap above pills
            }

            return new Size(bubbleW, Math.Max(24, h));
        }

        private int MeasureAttachmentsHeight(MessageItem it, int contentWidth)
        {
            if (it.Attachments == null || it.Attachments.Count == 0) return 0;
            int x = 0;
            int y = 0;
            int lineH = Math.Max(_baseFont.Height + 6, 18);
            for (int i = 0; i < it.Attachments.Count; i++)
            {
                string name = it.Attachments[i] != null ? (it.Attachments[i].FileName ?? "(file)") : "(file)";
                Size sz = TextRenderer.MeasureText(name, _baseFont, new Size(int.MaxValue / 4, int.MaxValue / 4), TextFormatFlags.NoPadding);
                int pillW = Math.Min(contentWidth, sz.Width + 16);
                int pillH = lineH;
                if (x > 0 && x + pillW > contentWidth)
                {
                    y += pillH + 4;
                    x = 0;
                }
                x += pillW + 6;
            }
            y += lineH;
            return y;
        }

        // Returns the width of the widest attachment pill, clamped to the available content width.
        // Pill width matches DrawAttachmentPills: text width + ~16px padding. Used so the bubble's
        // content width can grow to accommodate pills that are wider than the message text.
        private int MeasureAttachmentsMaxPillWidth(MessageItem it, int contentWidth)
        {
            if (it.Attachments == null || it.Attachments.Count == 0) return 0;
            int maxW = 0;
            for (int i = 0; i < it.Attachments.Count; i++)
            {
                string name = it.Attachments[i] != null ? (it.Attachments[i].FileName ?? "(file)") : "(file)";
                Size sz = TextRenderer.MeasureText(name, _baseFont, new Size(int.MaxValue / 4, int.MaxValue / 4), TextFormatFlags.NoPadding);
                int pillW = Math.Min(contentWidth, sz.Width + 16);
                if (pillW > maxW) maxW = pillW;
            }
            return maxW;
        }

        private Size MeasureBlock(Graphics g, Block blk, int maxWidth, Dictionary<int, int> numberedCounters)
        {
            switch (blk.Type)
            {
                case BlockType.Heading:
                    {
                        var h = (HeadingBlock)blk;
                        Font f = GetHeadingFont(h.Level);
                        return MeasureInlineParagraph(g, h.Inlines, f, maxWidth, true);
                    }
                case BlockType.Paragraph:
                    {
                        var p = (ParagraphBlock)blk;
                        return MeasureInlineParagraph(g, p.Inlines, _baseFont, maxWidth, true);
                    }
                case BlockType.BulletList:
                    {
                        var list = (BulletListBlock)blk;
                        int y = 0;
                        int w = 0;
                        foreach (var item in list.Items)
                        {
                            // measure bullet glyph + indented paragraph
                            int bulletWidth = BulletIndent + (item.IndentLevel * BulletIndent);
                            Size sz = MeasureInlineParagraph(g, item.Content, _baseFont, maxWidth - bulletWidth, true);
                            y += Math.Max(sz.Height, _baseFont.Height);
                            y += 2;
                            w = Math.Max(w, bulletWidth + sz.Width);
                        }
                        return new Size(Math.Min(maxWidth, w), y);
                    }
                case BlockType.NumberedList:
                    {
                        var list = (NumberedListBlock)blk;
                        int y = 0;
                        int w = 0;
                        foreach (var item in list.Items)
                        {
                            // Use original number if provided; otherwise continue sequence per indent level
                            int indent = item.IndentLevel;
                            if (item.Number.HasValue)
                            {
                                // When original number is provided, do not alter counters for this level
                            }
                            else
                            {
                                if (numberedCounters != null && numberedCounters.Count > 0)
                                {
                                    var toRemove = new List<int>();
                                    foreach (var k in numberedCounters.Keys)
                                        if (k > indent) toRemove.Add(k);
                                    for (int r = 0; r < toRemove.Count; r++) numberedCounters.Remove(toRemove[r]);
                                }
                            }
                            int prev = 0; if (numberedCounters != null) numberedCounters.TryGetValue(indent, out prev);
                            int itemNumber = item.Number.HasValue ? item.Number.Value : (prev + 1);
                            if (!item.Number.HasValue && numberedCounters != null) numberedCounters[indent] = itemNumber;
                            // measure number + indented paragraph using saved delimiter (default '.')
                            char delim = item.NumberDelimiter != '\0' ? item.NumberDelimiter : '.';
                            string numberText = itemNumber.ToString() + delim;
                            Size numberSize = TextRenderer.MeasureText(numberText, _baseFont);
                            int numberWidth = numberSize.Width + 4 + (item.IndentLevel * BulletIndent); // 4px gap after number
                            Size sz = MeasureInlineParagraph(g, item.Content, _baseFont, maxWidth - numberWidth, true);
                            y += Math.Max(sz.Height, _baseFont.Height);
                            y += 2;
                            w = Math.Max(w, numberWidth + sz.Width);
                        }
                        return new Size(Math.Min(maxWidth, w), y);
                    }
                case BlockType.CodeBlock:
                    {
                        var c = (CodeBlock)blk;
                        // A dot fence renders as a Graphviz image; measure the image/placeholder box.
                        // A failed render falls through to the ordinary code measurement below.
                        if (IsGraphLanguage(c.Language))
                        {
                            var gl = ComputeGraphLayout(g, c, maxWidth);
                            if (!gl.Failed)
                                return new Size(Math.Max(24, gl.BoxW), Math.Max(gl.HeaderH + 2 * CodeBlockPadding, gl.BoxH));
                        }
                        // Measure colored segments without wrapping to know full content width.
                        // Reuses the caller's Graphics instead of allocating one per code block per reflow.
                        {
                            // Enqueue for async highlight so it gets processed soon; enqueue in top-to-bottom order to get bottom-up processing
                            SyntaxHighlightingRenderer.EnqueueHighlight(c.Language, _isDarkTheme, c.Text, _monoFont);
                            var colored = SyntaxHighlightingRenderer.GetColoredSegments(c.Text, c.Language, _monoFont, _isDarkTheme);
                            Size content = SyntaxHighlightingRenderer.MeasureColoredSegmentsNoWrap(g, colored);
                            int viewportW = Math.Max(0, maxWidth - 2 * CodeBlockPadding);
                            bool needH = content.Width > viewportW;
                            int boxW = GetCodeBoxWidth(g, content.Width, maxWidth, c.Language);
                            int textH = Math.Max(_monoFont.Height, content.Height);
                            int headerH = GetCodeHeaderHeight();
                            int boxH = textH + 2 * CodeBlockPadding + headerH + (needH ? CodeHScrollHeight : 0);
                            return new Size(Math.Max(24, boxW), Math.Max(headerH + 2 * CodeBlockPadding, boxH));
                        }
                    }
                case BlockType.EditDiff:
                    {
                        var ed = (EditDiffBlock)blk;
                        EditDiffData data = GetEditDiffData(ed.Key);
                        bool hasBody = data != null && !string.IsNullOrEmpty(data.Body);
                        bool collapsed = IsEditDiffCollapsed(ed.Key);
                        int headerH = _baseFont.Height + 2 * EditDiffHeaderPad;
                        string headerText = BuildEditDiffHeaderText(data, collapsed, hasBody);
                        string countsText = BuildEditDiffCountsText(data);
                        int headerW = TextRenderer.MeasureText(headerText, _baseFont).Width
                                    + (countsText.Length > 0 ? TextRenderer.MeasureText(countsText, _baseFont).Width : 0);
                        int w = headerW;
                        int h = headerH;
                        if (hasBody && !collapsed && data.BodyIsMarkdown)
                        {
                            Size body = MeasureMarkdownBlocks(g, MarkdownParser.ParseMarkdown(data.Body), maxWidth);
                            h += EditDiffBodyGap + body.Height + EditDiffBodyPad;
                            w = Math.Max(w, body.Width);
                        }
                        else if (hasBody && !collapsed)
                        {
                            // Reuses the caller's Graphics instead of allocating one per diff body per reflow.
                            SyntaxHighlightingRenderer.EnqueueHighlight(data.Language, _isDarkTheme, data.Body, _monoFont);
                            var colored = SyntaxHighlightingRenderer.GetColoredSegments(data.Body, data.Language, _monoFont, _isDarkTheme);
                            Size content = SyntaxHighlightingRenderer.MeasureColoredSegmentsNoWrap(g, colored);
                            int bodyH = Math.Max(_monoFont.Height, content.Height);
                            bool needH = content.Width > maxWidth + EditDiffScrollSlack;
                            h += EditDiffBodyGap + bodyH + (needH ? CodeHScrollHeight : 0) + EditDiffBodyPad;
                            w = Math.Max(w, Math.Min(maxWidth, content.Width));
                        }
                        return new Size(Math.Max(24, Math.Min(maxWidth, w)), h);
                    }
                case BlockType.Error:
                    {
                        var er = (ErrorBlock)blk;
                        return MeasureInlineParagraph(g, BuildErrorInlines(er.Message), _baseFont, maxWidth, true);
                    }
                case BlockType.Divider:
                    {
                        // A full-width thematic break; the line is centered in DividerHeight vertically.
                        return new Size(maxWidth, DividerHeight);
                    }
                case BlockType.Table:
                    {
                        var t = (TableBlock)blk;
                        // Measure each cell as inline paragraphs to compute column widths and total height
                        int cols = Math.Max(0, t.Alignments != null ? t.Alignments.Count : 0);
                        if (cols == 0) return new Size(0, 0);
                        int cellPad = 6;
                        int border = 1;
                        int[] colWidths = new int[cols];
                        int rowHeightHeader = 0;
                        int contentWidth = border; // total intrinsic content width of the table
                        // Header
                        for (int c = 0; c < cols; c++)
                        {
                            var inl = (c < t.Header.Count) ? t.Header[c] : new List<InlineRun>();
                            Size sz = MeasureInlineParagraph(g, inl, _baseFont, int.MaxValue / 4, false);
                            colWidths[c] = Math.Max(colWidths[c], sz.Width);
                            rowHeightHeader = Math.Max(rowHeightHeader, sz.Height);
                        }
                        // Rows
                        int[] rowHeights = new int[Math.Max(0, t.Rows.Count)];
                        for (int r = 0; r < t.Rows.Count; r++)
                        {
                            int rowH = 0;
                            var row = t.Rows[r];
                            for (int c = 0; c < cols; c++)
                            {
                                var inl = (c < row.Count) ? row[c] : new List<InlineRun>();
                                Size sz = MeasureInlineParagraph(g, inl, _baseFont, int.MaxValue / 4, false);
                                colWidths[c] = Math.Max(colWidths[c], sz.Width);
                                rowH = Math.Max(rowH, sz.Height);
                            }
                            rowHeights[r] = rowH;
                        }
                        int totalW = border; for (int c = 0; c < cols; c++) totalW += colWidths[c] + cellPad * 2 + border; contentWidth = totalW;
                        int totalH = border + rowHeightHeader + cellPad * 2 + border; for (int r = 0; r < rowHeights.Length; r++) totalH += rowHeights[r] + cellPad * 2 + border;
                        bool needH = totalW > maxWidth;
                        // Clamp returned width to available max; add h-scroll height when needed
                        return new Size(Math.Min(maxWidth, totalW), totalH + (needH ? CodeHScrollHeight : 0));
                    }
            }
            return Size.Empty;
        }

        // Measures a list of Markdown blocks the same way a message body is measured (MeasureBlock plus
        // the per-block-type trailing spacing in the height loop), so the value matches what DrawBlocks
        // consumes. Used for tool records whose body is rendered as Markdown rather than highlighted code.
        private Size MeasureMarkdownBlocks(Graphics g, List<Block> blocks, int maxWidth)
        {
            int h = 0;
            int w = 0;
            var numberedCounters = new Dictionary<int, int>();
            for (int i = 0; i < blocks.Count; i++)
            {
                Block blk = blocks[i];
                if (blk.Type != BlockType.NumberedList && blk.Type != BlockType.BulletList)
                    numberedCounters.Clear();
                Size sz = MeasureBlock(g, blk, maxWidth, numberedCounters);
                h += sz.Height;
                if (blk.Type == BlockType.Heading) h += 4;
                else if (blk.Type == BlockType.Paragraph) h += 2;
                else if (blk.Type == BlockType.CodeBlock) h += 4;
                else if (blk.Type == BlockType.Error) h += 2;
                w = Math.Max(w, sz.Width);
            }
            return new Size(Math.Min(maxWidth, w), h);
        }

        // Build the inline runs for an error notice. The message is treated as literal text (no
        // markdown), so error strings containing '*', '_', '`' etc. render verbatim. Word wrapping is
        // handled by the normal inline layout (it splits the run on spaces).
        private static List<InlineRun> BuildErrorInlines(string message)
        {
            return new List<InlineRun> { new InlineRun { Text = message ?? string.Empty, Style = RunStyle.Normal } };
        }

        private Size MeasureInlineParagraph(Graphics g, List<InlineRun> runs, Font baseFont, int maxWidth, bool addBottomGap)
        {
            int x = 0;
            int y = 0;
            int lineHeight = baseFont.Height;
            int maxLineWidth = 0;

            foreach (var seg in WordWrapRuns(g, runs, baseFont, maxWidth))
            {
                if (seg.IsNewLine)
                {
                    y += lineHeight + 2; // Match the drawing spacing
                    maxLineWidth = Math.Max(maxLineWidth, x);
                    x = 0;
                    lineHeight = baseFont.Height;
                    continue;
                }

                // track tallest on the current line
                lineHeight = Math.Max(lineHeight, seg.Font.Height);
                x += seg.Rect.Width;
            }

            // add last line
            y += lineHeight + 2; // Match the drawing spacing

            maxLineWidth = Math.Max(maxLineWidth, x);

            return new Size(Math.Min(maxWidth, maxLineWidth), y);
        }

        private struct LayoutSeg
        {
            public bool IsNewLine;
            public int LineWidth;
            public Font Font;
            public string Text;
            public Rectangle Rect;
            public bool IsInlineCode;
            public bool IsLink;
            public string LinkUrl;
            // True when this newline is a hard break from markdown ("\n"), false when it's from word wrapping
            public bool IsHardBreak;
        }

        private Font GetRunFont(RunStyle st, Font baseFont)
        {
            // Derive styled fonts from the provided baseFont so heading sizes are preserved.
            bool b = (st & RunStyle.Bold) != 0;
            bool i = (st & RunStyle.Italic) != 0;
            if (!b && !i) return baseFont;

            // Start from the baseFont's existing style (e.g., headings are already Bold)
            FontStyle fs = baseFont.Style;
            if (b) fs |= FontStyle.Bold;
            if (i) fs |= FontStyle.Italic;

            // Cache by family|size|style to avoid creating too many Font instances
            string key = baseFont.FontFamily.Name + "|" + baseFont.Size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + ((int)fs).ToString();
            Font cached;
            if (_styledFontCache.TryGetValue(key, out cached) && cached != null)
                return cached;

            try
            {
                var derived = new Font(baseFont, fs);
                _styledFontCache[key] = derived;
                return derived;
            }
            catch
            {
                // Fallback to base or prebuilt fonts if derivation fails
                if (b && i) return _boldItalicFont ?? baseFont;
                if (b) return _boldFont ?? baseFont;
                if (i) return _italicFont ?? baseFont;
                return baseFont;
            }
        }

        private IEnumerable<LayoutSeg> WordWrapRuns(Graphics g, List<InlineRun> runs, Font baseFont, int maxWidth)
        {
            // Greedy word wrapping across style runs. The caller supplies the Graphics so layout reuses
            // a single surface (the Reflow/draw Graphics) instead of allocating one per paragraph.
            int x = 0;
            int lineWidth = 0;
            int lineHeight = baseFont.Height;

            {
                // Use typographic metrics to avoid GDI+ extra side bearings padding
                using (var fmt = StringFormat.GenericTypographic)
                {
                    fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                    foreach (var r in runs)
                    {
                        bool isCode = (r.Style & RunStyle.Code) != 0;
                        bool isLink = (r.Style & RunStyle.Link) != 0;
                        Font f = isCode ? _monoFont : GetRunFont(r.Style, baseFont);
                        // Split by spaces, keep separators
                        var parts = SplitWordsPreserveSpaces(r.Text);
                        foreach (string part in parts)
                        {
                            string text = part;
                            if (text == "\n")
                            {
                                // Hard line break from markdown
                                yield return new LayoutSeg { IsNewLine = true, LineWidth = lineWidth, IsHardBreak = true };
                                x = 0; lineWidth = 0; lineHeight = baseFont.Height;
                                continue;
                            }

                            SizeF szF = g.MeasureString(text.Length == 0 ? " " : text, f, PointF.Empty, fmt);
                            int partWidth = (int)Math.Ceiling(szF.Width);
                            int partHeight = (int)Math.Ceiling(szF.Height);

                            // A single token wider than the available width (e.g. a long URL with no
                            // spaces) cannot be placed on one line. Break it at character boundaries so
                            // it word-wraps instead of overflowing the bubble. Normal-sized tokens take
                            // the cheap path below and are never re-measured per character.
                            if (maxWidth > 0 && partWidth > maxWidth)
                            {
                                int idx = 0;
                                while (idx < text.Length)
                                {
                                    // If the current line already has content and not even a single
                                    // character fits in the remaining space, wrap to a fresh line first.
                                    int oneW = (int)Math.Ceiling(g.MeasureString(text.Substring(idx, 1), f, PointF.Empty, fmt).Width);
                                    if (x > 0 && x + oneW > maxWidth)
                                    {
                                        yield return new LayoutSeg { IsNewLine = true, LineWidth = lineWidth, IsHardBreak = false };
                                        x = 0; lineWidth = 0; lineHeight = baseFont.Height;
                                    }

                                    // Greedily grow the chunk to fill the available width (at least one
                                    // character so we always make progress, even in a very narrow bubble).
                                    int count = 1;
                                    int chunkWidth = oneW;
                                    while (idx + count < text.Length)
                                    {
                                        int wNext = (int)Math.Ceiling(g.MeasureString(text.Substring(idx, count + 1), f, PointF.Empty, fmt).Width);
                                        if (x + wNext > maxWidth) break;
                                        count++;
                                        chunkWidth = wNext;
                                    }

                                    string chunk = text.Substring(idx, count);
                                    int chunkHeight = (int)Math.Ceiling(g.MeasureString(chunk, f, PointF.Empty, fmt).Height);
                                    yield return new LayoutSeg
                                    {
                                        IsNewLine = false,
                                        Font = f,
                                        Text = chunk,
                                        Rect = new Rectangle(x, 0, chunkWidth, chunkHeight),
                                        IsInlineCode = isCode,
                                        IsLink = isLink,
                                        LinkUrl = isLink ? r.LinkUrl : null
                                    };

                                    x += chunkWidth;
                                    lineWidth += chunkWidth;
                                    lineHeight = Math.Max(lineHeight, chunkHeight);
                                    idx += count;

                                    // Wrap before placing the remainder of the token on the next line.
                                    if (idx < text.Length)
                                    {
                                        yield return new LayoutSeg { IsNewLine = true, LineWidth = lineWidth, IsHardBreak = false };
                                        x = 0; lineWidth = 0; lineHeight = baseFont.Height;
                                    }
                                }
                                continue;
                            }

                            bool needsBreak = (x > 0 && x + partWidth > maxWidth);
                            if (needsBreak)
                            {
                                // Line wrap break (not a hard markdown break)
                                yield return new LayoutSeg { IsNewLine = true, LineWidth = lineWidth, IsHardBreak = false };
                                x = 0; lineWidth = 0; lineHeight = baseFont.Height;
                            }

                            // emit segment
                            yield return new LayoutSeg
                            {
                                IsNewLine = false,
                                Font = f,
                                Text = text,
                                Rect = new Rectangle(x, 0, partWidth, partHeight),
                                IsInlineCode = isCode,
                                IsLink = isLink,
                                LinkUrl = isLink ? r.LinkUrl : null
                            };

                            x += partWidth;
                            lineWidth += partWidth;
                            lineHeight = Math.Max(lineHeight, partHeight);
                        }
                    }
                }
            }
        }

        private static List<string> SplitWordsPreserveSpaces(string s)
        {
            var parts = new List<string>();
            if (string.IsNullOrEmpty(s)) { parts.Add(""); return parts; }

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\n')
                {
                    if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Length = 0; }
                    parts.Add("\n");
                }
                else if (c == ' ')
                {
                    if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Length = 0; }
                    parts.Add(" ");
                }
                else
                {
                    sb.Append(c);
                }
            }
            if (sb.Length > 0) parts.Add(sb.ToString());
            if (parts.Count == 0) parts.Add("");
            return parts;
        }

        // ---------- Painting ----------
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // Keep the scrollbar geometry responsive and repaint with the current (possibly slightly
            // stale) bounds, but defer the expensive full Reflow. When the window is wider than the
            // capped content area the cached measurements make Reflow cheap; when it is narrower the
            // usable width shrinks every pixel, so coalescing avoids re-measuring the whole transcript
            // on every WM_SIZE during a drag.
            UpdateScrollbar();
            Invalidate();
            if (_resizeDebounce != null)
            {
                _resizeDebounce.Stop();
                _resizeDebounce.Start();
            }
            else
            {
                // Timer not wired yet (very early in construction): fall back to the immediate path.
                Reflow();
                ReflowSoon();
            }
        }

        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            base.SetBoundsCore(x, y, width, height, specified);
            // Ensure scrollbar is updated when bounds change
            if (IsHandleCreated && (specified & (BoundsSpecified.Width | BoundsSpecified.Height)) != 0)
            {
                UpdateScrollbar();
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Ensure scrollbar is properly initialized when handle is created
            ReflowSoon();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                // Ensure scrollbar is updated when control becomes visible
                Reflow();
                ReflowSoon();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);

            // Apply scroll transform to everything
            e.Graphics.TranslateTransform(0, -_scrollOffset);

            int viewTop = _scrollOffset - GapBetweenBubbles;
            int viewBottom = _scrollOffset + ClientSize.Height;

            // Bubbles are laid out top-to-bottom by Reflow, so their bounds are ordered: binary-search
            // the first item that reaches the viewport instead of walking past every bubble above it.
            // On a long transcript scrolled near the bottom this turns an O(items) scan on every paint
            // (and paints happen on every scroll/hover/selection tick) into O(log n + visible).
            for (int i = FindFirstVisibleIndex(viewTop); i < _items.Count; i++)
            {
                var it = _items[i];
                if (it.Bounds.Top > viewBottom) break;
                DrawBubble(e.Graphics, it);
            }

            // Reset transform
            e.Graphics.ResetTransform();
            base.OnPaint(e);
        }

        // Smallest index whose bubble bottom is at or below `top` — i.e. the first item not entirely
        // above the viewport. Returns _items.Count when every item is above `top`. Assumes Bounds are
        // ordered by the sequential Reflow layout (each item's Bottom >= the previous item's Bottom).
        private int FindFirstVisibleIndex(int top)
        {
            int lo = 0, hi = _items.Count; // search the half-open range [lo, hi)
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (_items[mid].Bounds.Bottom < top) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private void DrawBubble(Graphics g, MessageItem it)
        {
            Rectangle r = it.Bounds;

            Color back, border;
            if (it.Role == MessageRole.User) { back = _clrUserBack; border = _clrUserBorder; }
            else if (it.Role == MessageRole.Assistant) { back = _clrAsstBack; border = _clrAsstBorder; }
            else { back = _clrSysBack; border = _clrSysBorder; }

            // Tool-activity messages render as plain text with no bubble chrome (background/border).
            if (it.Role != MessageRole.Tool)
            {
                using (var path = RoundedRect(r, BubbleRadius))
                using (var b = new SolidBrush(back))
                using (var pen = new Pen(border))
                {
                    g.FillPath(b, path);
                    g.DrawPath(pen, path);
                }
            }

            // Content area
            Rectangle content = new Rectangle(r.X + BubblePadding, r.Y + BubblePadding, r.Width - 2 * BubblePadding, r.Height - 2 * BubblePadding);
            // Reset link hit rectangles for this item before drawing
            if (it.LinkHits == null) it.LinkHits = new List<LinkHit>(); else it.LinkHits.Clear();
            // Reset edit-diff header hit rectangles before drawing
            if (it.EditDiffHits == null) it.EditDiffHits = new List<EditDiffHit>(); else it.EditDiffHits.Clear();
            if (it.EditDiffScrollHits == null) it.EditDiffScrollHits = new List<EditDiffScrollHit>(); else it.EditDiffScrollHits.Clear();
            // Reset drawn text segments list (for selection)
            if (it.DrawnSegments == null) it.DrawnSegments = new List<DrawnSeg>(); else it.DrawnSegments.Clear();
            // Reset the retry button hit rect; the error-block draw re-captures it when shown
            it.RetryRect = Rectangle.Empty;
            // Reset link run sequence
            it.LinkRunSeq = 0;
            DrawBlocks(g, content, it.Blocks, it);

            // Draw selection highlight overlay for this message, if active
            if (_hasSelection && _selectionItem == it)
            {
                try { DrawSelection(g, it); }
                catch { }
            }

            // Draw attachment pills at the bottom of content area
            if (it.Attachments != null && it.Attachments.Count > 0)
            {
                DrawAttachmentPills(g, new Rectangle(content.X, r.Bottom - BubblePadding - MeasureAttachmentsHeight(it, content.Width), content.Width, MeasureAttachmentsHeight(it, content.Width)), it);
            }

            // Tiny "zdr" tag in the top-right corner for zero-retention messages. Drawn last (over the
            // content) on a small back-filled chip so it stays legible, sized into the existing
            // BubblePadding gutter so the bubble itself never grows.
            if (it.ShowZdrTag && it.Role != MessageRole.Tool)
                DrawZdrTag(g, r, back, border);
        }

        // Draws the small "zdr" badge just inside the bubble's bottom-right corner, in the bubble's
        // border color over its background color. Bottom-right keeps it clear of the (left-aligned,
        // top-anchored) message text, which the top-right corner could overlap.
        private void DrawZdrTag(Graphics g, Rectangle r, Color back, Color border)
        {
            const string tag = "zdr";
            using (var font = new Font(_baseFont != null ? _baseFont.FontFamily : FontFamily.GenericSansSerif,
                                       6.5f, FontStyle.Regular, GraphicsUnit.Point))
            using (var sf = new StringFormat(StringFormat.GenericTypographic))
            {
                // GDI+ (g.DrawString) honors the paint-time scroll transform; TextRenderer (GDI) does
                // not, which would pin the tag in place while the bubble scrolled.
                SizeF szf = g.MeasureString(tag, font, int.MaxValue, sf);
                int w = (int)Math.Ceiling(szf.Width);
                int h = (int)Math.Ceiling(szf.Height);
                int pad = 2;
                // Sit against the bottom-right border, within the padding gutter.
                int x = r.Right - BubblePadding / 2 - w;
                int y = r.Bottom - h - 1;
                Rectangle chip = new Rectangle(x - pad, y, w + 2 * pad, h);
                using (var bb = new SolidBrush(back))
                    g.FillRectangle(bb, chip);
                using (var fb = new SolidBrush(border))
                    g.DrawString(tag, font, fb, new PointF(x, y), sf);
            }
        }

        private void DrawAttachmentPills(Graphics g, Rectangle bounds, MessageItem it)
        {
            if (it.Attachments == null || it.Attachments.Count == 0) return;
            if (it.AttachmentPillRects == null) it.AttachmentPillRects = new List<Rectangle>(); else it.AttachmentPillRects.Clear();
            int x = bounds.X;
            int y = bounds.Y;
            int maxW = bounds.Width;
            for (int i = 0; i < it.Attachments.Count; i++)
            {
                var af = it.Attachments[i];
                string name = af != null ? (af.FileName ?? "(file)") : "(file)";
                Size sz = TextRenderer.MeasureText(name, _baseFont, new Size(int.MaxValue / 4, int.MaxValue / 4), TextFormatFlags.NoPadding);
                int pillW = Math.Min(maxW, sz.Width + 16);
                int pillH = Math.Max(_baseFont.Height + 6, 18);
                if (x > bounds.X && x + pillW > bounds.Right)
                {
                    x = bounds.X; y += pillH + 4;
                }
                Rectangle pill = new Rectangle(x, y, pillW, pillH);

                bool hover = (_hoverAttachItem == it && _hoverAttachIndex == i);
                bool pressed = (_pressAttachItem == it && _pressAttachIndex == i);

                Color baseBack = _isDarkTheme ? Color.FromArgb(60, 62, 66) : Color.FromArgb(240, 240, 240);
                if (pressed) baseBack = _clrCopyPressed; else if (hover) baseBack = _clrCopyHover;
                using (var sb = new SolidBrush(baseBack))
                using (var pen = new Pen(_isDarkTheme ? _clrScrollThumbBorder : _clrCodeBorder))
                using (var path = RoundedRect(pill, 9))
                {
                    g.FillPath(sb, path); g.DrawPath(pen, path);
                }
                Rectangle textRect = new Rectangle(pill.X + 8, pill.Y + (pill.Height - _baseFont.Height) / 2, Math.Max(0, pill.Width - 10), _baseFont.Height);
                using (var brush = new SolidBrush(ForeColor))
                {
                    using (var fmt = StringFormat.GenericTypographic)
                    {
                        fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                        g.DrawString(name, _baseFont, brush, (PointF)textRect.Location, fmt);
                    }
                }
                it.AttachmentPillRects.Add(pill);
                x += pillW + 6;
            }
        }

        private void DrawBlocks(Graphics g, Rectangle bounds, List<Block> blocks, MessageItem owner)
        {
            int y = bounds.Y;
            int x0 = bounds.X;
            int maxWidth = bounds.Width;
            int codeIndex = 0; // index of code block within this message for scroll state
            // Maintain numbering counters across blocks so ordered lists continue through sublists
            var numberedCounters = new Dictionary<int, int>(); // key: indent level, value: last number emitted

            foreach (var blk in blocks)
            {
                if (blk.Type == BlockType.Heading)
                {
                    var h = (HeadingBlock)blk;
                    Font f = GetHeadingFont(h.Level);
                    // Heading lines are logical starts
                    y += DrawInlineParagraph(g, x0, y, maxWidth, h.Inlines, f, owner, new InlineCopyContext { IsHeadingLine = true, HeadingLevel = h.Level });
                    y += 4;
                    // Block boundary (hard break) for markdown-aware copy
                    if (owner != null) { if (owner.DrawnSegments == null) owner.DrawnSegments = new List<DrawnSeg>(); owner.DrawnSegments.Add(new DrawnSeg { IsNewLine = true, IsHardBreak = true, Rect = new Rectangle(x0, y, 0, 0), Text = null, Font = _baseFont }); }
                    // Reset numbering when leaving list context
                    numberedCounters.Clear();
                }
                else if (blk.Type == BlockType.Paragraph)
                {
                    var p = (ParagraphBlock)blk;
                    // Paragraph lines are logical starts
                    y += DrawInlineParagraph(g, x0, y, maxWidth, p.Inlines, _baseFont, owner, new InlineCopyContext());
                    y += 2;
                    if (owner != null) { if (owner.DrawnSegments == null) owner.DrawnSegments = new List<DrawnSeg>(); owner.DrawnSegments.Add(new DrawnSeg { IsNewLine = true, IsHardBreak = true, Rect = new Rectangle(x0, y, 0, 0), Text = null, Font = _baseFont }); }
                    // Reset numbering when leaving list context
                    numberedCounters.Clear();
                }
                else if (blk.Type == BlockType.BulletList)
                {
                    var list = (BulletListBlock)blk;
                    foreach (var item in list.Items)
                    {
                        int indentX = x0 + (item.IndentLevel * BulletIndent);
                        // bullet glyph
                        using (var b = new SolidBrush(ForeColor))
                        {
                            // simple bullet - different styles for different nesting levels
                            if (item.IndentLevel == 0)
                                g.FillEllipse(b, indentX, y + _baseFont.Height / 2 - 2, 4, 4);
                            else if (item.IndentLevel == 1)
                            {
                                using (var pen = new Pen(ForeColor))
                                    g.DrawEllipse(pen, indentX, y + _baseFont.Height / 2 - 2, 4, 4);
                            }
                            else
                                g.FillRectangle(b, indentX, y + _baseFont.Height / 2 - 1, 3, 3);
                        }
                        int textX = indentX + BulletIndent;
                        string indentSpaces = new string(' ', item.IndentLevel * 2);
                        string bulletPrefix = indentSpaces + "- ";
                        // List item lines are logical starts
                        int used = DrawInlineParagraph(g, textX, y, maxWidth - (textX - x0), item.Content, _baseFont, owner, new InlineCopyContext { LinePrefix = bulletPrefix });
                        y += Math.Max(used, _baseFont.Height) + 2;
                        // Hard break after each bullet item to keep items on separate lines when copying
                        if (owner != null) { if (owner.DrawnSegments == null) owner.DrawnSegments = new List<DrawnSeg>(); owner.DrawnSegments.Add(new DrawnSeg { IsNewLine = true, IsHardBreak = true, Rect = new Rectangle(x0, y, 0, 0), Text = null, Font = _baseFont }); }
                    }
                    // No extra block-level newline here to avoid double-blank line after the list
                }
                else if (blk.Type == BlockType.NumberedList)
                {
                    var list = (NumberedListBlock)blk;
                    foreach (var item in list.Items)
                    {
                        // Use original number if provided; otherwise continue sequence per indent
                        int indent = item.IndentLevel;
                        if (!item.Number.HasValue)
                        {
                            if (numberedCounters.Count > 0)
                            {
                                var toRemove = new List<int>();
                                foreach (var k in numberedCounters.Keys) if (k > indent) toRemove.Add(k);
                                for (int r = 0; r < toRemove.Count; r++) numberedCounters.Remove(toRemove[r]);
                            }
                        }
                        int prev; if (!numberedCounters.TryGetValue(indent, out prev)) prev = 0;
                        int itemNumber = item.Number.HasValue ? item.Number.Value : (prev + 1);
                        if (!item.Number.HasValue) numberedCounters[indent] = itemNumber;
                        int indentX = x0 + (item.IndentLevel * BulletIndent);
                        // number
                        char delim = item.NumberDelimiter != '\0' ? item.NumberDelimiter : '.';
                        string numberText = itemNumber.ToString() + delim;
                        Size numberSize = TextRenderer.MeasureText(numberText, _baseFont);
                        using (var brush = new SolidBrush(ForeColor))
                        {
                            g.DrawString(numberText, _baseFont, brush, indentX, y);
                        }

                        int textX = indentX + numberSize.Width + 4; // 4px gap after number
                        string indentSpaces = new string(' ', item.IndentLevel * 2);
                        string numberPrefix = indentSpaces + numberText + " ";
                        // Numbered list item lines are logical starts
                        int used = DrawInlineParagraph(g, textX, y, maxWidth - (textX - x0), item.Content, _baseFont, owner, new InlineCopyContext { LinePrefix = numberPrefix });
                        y += Math.Max(used, _baseFont.Height) + 2;
                        // Hard break after each numbered item
                        if (owner != null) { if (owner.DrawnSegments == null) owner.DrawnSegments = new List<DrawnSeg>(); owner.DrawnSegments.Add(new DrawnSeg { IsNewLine = true, IsHardBreak = true, Rect = new Rectangle(x0, y, 0, 0), Text = null, Font = _baseFont }); }
                    }
                    // Avoid extra block-level newline to prevent double spacing
                }
                else if (blk.Type == BlockType.EditDiff)
                {
                    var ed = (EditDiffBlock)blk;
                    EditDiffData data = GetEditDiffData(ed.Key);
                    bool hasBody = data != null && !string.IsNullOrEmpty(data.Body);
                    bool collapsed = IsEditDiffCollapsed(ed.Key);
                    int headerH = _baseFont.Height + 2 * EditDiffHeaderPad;

                    // Header row. With a body it's a clickable disclosure header (captured for the
                    // collapse hit test); without one it's a plain non-interactive label. The label is
                    // drawn in the normal text color; an edit record's +N/-N line counts follow it,
                    // color-coded green/red.
                    string headerText = BuildEditDiffHeaderText(data, collapsed, hasBody);
                    using (var fmt = StringFormat.GenericTypographic)
                    {
                        fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                        float hx = x0;
                        float hy = y + EditDiffHeaderPad;
                        DrawHeaderSeg(g, ref hx, hy, headerText, _baseFont, ForeColor, fmt);
                        if (data != null && data.Added >= 0 && data.Removed >= 0)
                        {
                            DrawHeaderSeg(g, ref hx, hy, "  (", _baseFont, ForeColor, fmt);
                            DrawHeaderSeg(g, ref hx, hy, "+" + data.Added, _baseFont, _clrDiffAdd, fmt);
                            DrawHeaderSeg(g, ref hx, hy, " ", _baseFont, ForeColor, fmt);
                            // ASCII "-" (U+002D), not the Unicode minus sign U+2212 which is missing from Tahoma on XP.
                            DrawHeaderSeg(g, ref hx, hy, "-" + data.Removed, _baseFont, _clrDiffDel, fmt);
                            DrawHeaderSeg(g, ref hx, hy, ")", _baseFont, ForeColor, fmt);
                        }
                    }
                    if (hasBody && owner != null)
                    {
                        if (owner.EditDiffHits == null) owner.EditDiffHits = new List<EditDiffHit>();
                        owner.EditDiffHits.Add(new EditDiffHit { Rect = new Rectangle(x0, y, maxWidth, headerH), Key = ed.Key });
                    }
                    y += headerH;

                    // Expanded body. A Markdown record (e.g. dispatch_agent) reuses the normal block
                    // renderer so bold/tables/etc. render as formatting; everything else is the
                    // chromeless highlighted code body below.
                    if (hasBody && !collapsed && data.BodyIsMarkdown)
                    {
                        y += EditDiffBodyGap;
                        List<Block> bodyBlocks = MarkdownParser.ParseMarkdown(data.Body);
                        int bodyH = MeasureMarkdownBlocks(g, bodyBlocks, maxWidth).Height;
                        // Pass the owner so tables (TableScroll state) and copy work; the body's blocks
                        // carry no EditDiff sentinels, so there's no re-entrancy.
                        DrawBlocks(g, new Rectangle(x0, y, maxWidth, bodyH), bodyBlocks, owner);
                        y += bodyH + EditDiffBodyPad;
                    }
                    // Expanded: chromeless highlighted body, clipped to width (with horizontal scroll
                    // for over-wide content).
                    else if (hasBody && !collapsed)
                    {
                        y += EditDiffBodyGap;
                        SyntaxHighlightingRenderer.EnqueueHighlight(data.Language, _isDarkTheme, data.Body, _monoFont);
                        var colored = SyntaxHighlightingRenderer.GetColoredSegments(data.Body, data.Language, _monoFont, _isDarkTheme);
                        Size content = SyntaxHighlightingRenderer.MeasureColoredSegmentsNoWrap(g, colored);
                        int bodyH = Math.Max(_monoFont.Height, content.Height);
                        int viewportW = maxWidth;
                        bool needH = content.Width > viewportW + EditDiffScrollSlack;

                        int scrollX = 0;
                        if (needH)
                        {
                            int maxScroll = Math.Max(0, content.Width - viewportW);
                            scrollX = Math.Max(0, Math.Min(maxScroll, GetEditDiffScroll(ed.Key)));
                            SetEditDiffScroll(ed.Key, scrollX);
                        }

                        Rectangle textRect = new Rectangle(x0, y, viewportW, bodyH);
                        // Neutral backing (theme-aware code-block background) so unchanged/blank lines
                        // read as part of the diff block rather than the transcript background; the
                        // per-line red/green bands paint over this. History-record only — the shared
                        // "diff" highlighter (used by ```diff fences) is unchanged.
                        using (var bg = new SolidBrush(_clrCodeBack))
                            g.FillRectangle(bg, textRect);
                        SyntaxHighlightingRenderer.DrawColoredSegmentsNoWrap(g, colored, textRect, scrollX);
                        y += bodyH;

                        if (needH)
                        {
                            Rectangle track = new Rectangle(textRect.X, textRect.Bottom + 2, textRect.Width, CodeHScrollHeight - 4);
                            using (var trackBrush = new SolidBrush(_clrScrollTrack))
                            using (var trackPen = new Pen(_clrScrollTrackBorder))
                            {
                                g.FillRectangle(trackBrush, track);
                                g.DrawRectangle(trackPen, track);
                            }
                            int thumbW = Math.Max(CodeHScrollThumbMin, (int)Math.Round((double)track.Width * viewportW / Math.Max(1, content.Width)));
                            int trackRange = Math.Max(1, track.Width - thumbW);
                            int maxScroll = Math.Max(0, content.Width - viewportW);
                            int thumbX = track.X + (maxScroll > 0 ? (int)Math.Round((double)scrollX / maxScroll * trackRange) : 0);
                            Rectangle thumb = new Rectangle(thumbX, track.Y, thumbW, track.Height);
                            using (var thumbBrush = new SolidBrush(_clrScrollThumb))
                            using (var thumbPen = new Pen(_clrScrollThumbBorder))
                            {
                                g.FillRectangle(thumbBrush, thumb);
                                g.DrawRectangle(thumbPen, thumb);
                            }
                            if (owner != null)
                            {
                                if (owner.EditDiffScrollHits == null) owner.EditDiffScrollHits = new List<EditDiffScrollHit>();
                                owner.EditDiffScrollHits.Add(new EditDiffScrollHit { Track = track, Body = textRect, Key = ed.Key, ContentWidth = content.Width, ViewportWidth = viewportW });
                            }
                            y += CodeHScrollHeight;
                        }
                        y += EditDiffBodyPad;
                    }
                    numberedCounters.Clear();
                }
                else if (blk.Type == BlockType.Error)
                {
                    // Chrome-less red notice (the containing message is a Tool-role bubble, so no
                    // chrome). Drawn as literal wrapped text in the error color.
                    var er = (ErrorBlock)blk;
                    y += DrawInlineParagraph(g, x0, y, maxWidth, BuildErrorInlines(er.Message), _baseFont, owner,
                                             new InlineCopyContext { ForeOverride = _clrError });
                    y += 2;
                    if (owner != null) { if (owner.DrawnSegments == null) owner.DrawnSegments = new List<DrawnSeg>(); owner.DrawnSegments.Add(new DrawnSeg { IsNewLine = true, IsHardBreak = true, Rect = new Rectangle(x0, y, 0, 0), Text = null, Font = _baseFont }); }
                    // Trailing error notice: a small bordered Retry button under the red text re-runs
                    // the failed turn (the form wires RetryRequested to restart the send).
                    if (owner != null && blk == blocks[blocks.Count - 1] && IsRetryTarget(owner))
                    {
                        y += RetryBtnGapTop;
                        Size btnSz = GetRetryButtonSize();
                        Rectangle btn = new Rectangle(x0, y, btnSz.Width, btnSz.Height);
                        bool retryPressed = (owner == _retryPressedItem);
                        bool retryHover = (owner == _hoverRetryItem);
                        using (var sb = new SolidBrush(retryPressed ? _clrCopyPressed : (retryHover ? _clrCopyHover : _clrCodeBack)))
                        using (var pen = new Pen(_clrCodeBorder))
                        {
                            g.FillRectangle(sb, btn);
                            g.DrawRectangle(pen, btn);
                        }
                        using (var brush = new SolidBrush(_clrLink))
                        using (var fmt = StringFormat.GenericTypographic)
                        {
                            fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                            g.DrawString("Retry", _baseFont, brush, new PointF(btn.X + RetryBtnPadX, btn.Y + RetryBtnPadY), fmt);
                        }
                        owner.RetryRect = btn;
                        y += btnSz.Height;
                    }
                    numberedCounters.Clear();
                }
                else if (blk.Type == BlockType.Divider)
                {
                    // Full-width horizontal rule, centered vertically within DividerHeight.
                    int lineY = y + DividerVPad;
                    using (var pen = new Pen(_clrCodeBorder))
                        g.DrawLine(pen, x0, lineY, x0 + maxWidth, lineY);
                    // Record a selectable segment so the rule round-trips through copy as "---".
                    if (owner != null)
                    {
                        if (owner.DrawnSegments == null) owner.DrawnSegments = new List<DrawnSeg>();
                        owner.DrawnSegments.Add(new DrawnSeg { IsNewLine = true, IsHardBreak = true, Rect = new Rectangle(x0, y, 0, 0), Text = null, Font = _baseFont });
                        owner.DrawnSegments.Add(new DrawnSeg { Rect = new Rectangle(x0, lineY, maxWidth, 1), Text = "---", Font = _baseFont, IsLogicalLineStart = true, LineFirstTextLeft = x0 });
                        owner.DrawnSegments.Add(new DrawnSeg { IsNewLine = true, IsHardBreak = true, Rect = new Rectangle(x0, y + DividerHeight, 0, 0), Text = null, Font = _baseFont });
                    }
                    y += DividerHeight;
                    numberedCounters.Clear();
                }
                else if (blk.Type == BlockType.CodeBlock)
                {
                    var c = (CodeBlock)blk;
                    // A dot fence renders as a Graphviz image; a failed render falls back to code below.
                    GraphBlockLayout graph = new GraphBlockLayout();
                    bool asGraph = false;
                    if (IsGraphLanguage(c.Language))
                    {
                        graph = ComputeGraphLayout(g, c, maxWidth);
                        asGraph = !graph.Failed;
                    }
                    if (asGraph)
                    {
                        DrawGraphBlock(g, c, graph, x0, y, owner, codeIndex);
                        y += graph.BoxH + 4;
                        if (owner != null) { if (owner.DrawnSegments == null) owner.DrawnSegments = new List<DrawnSeg>(); owner.DrawnSegments.Add(new DrawnSeg { IsNewLine = true, IsHardBreak = true, Rect = new Rectangle(x0, y, 0, 0), Text = null, Font = _baseFont }); }
                        codeIndex++;
                        numberedCounters.Clear();
                    }
                    else
                    {
                    // Colored segments and content size without wrapping
                    SyntaxHighlightingRenderer.EnqueueHighlight(c.Language, _isDarkTheme, c.Text, _monoFont);
                    var coloredSegments = SyntaxHighlightingRenderer.GetColoredSegments(c.Text, c.Language, _monoFont, _isDarkTheme);
                    Size contentNoWrap = SyntaxHighlightingRenderer.MeasureColoredSegmentsNoWrap(g, coloredSegments);
                    int viewportW = Math.Max(0, maxWidth - 2 * CodeBlockPadding);
                    bool needH = contentNoWrap.Width > viewportW;
                    int boxW = GetCodeBoxWidth(g, contentNoWrap.Width, maxWidth, c.Language);
                    int textHeight = Math.Max(_monoFont.Height, contentNoWrap.Height);
                    int headerH = GetCodeHeaderHeight();
                    int boxH = textHeight + 2 * CodeBlockPadding + headerH + (needH ? CodeHScrollHeight : 0);
                    Rectangle box = new Rectangle(x0, y, boxW, boxH);

                    // Draw code block background and border
                    using (var sb = new SolidBrush(_clrCodeBack))
                    using (var pen = new Pen(_clrCodeBorder))
                    {
                        g.FillRectangle(sb, box);
                        g.DrawRectangle(pen, box);
                    }

                    // Header area top (flush with top border to remove extra spacing)
                    int headerTop = box.Top;

                    // Copy button (top-right)
                    string copyText = "Copy";
                    SizeF copySizeF;
                    using (var fmt = StringFormat.GenericTypographic)
                    {
                        fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                        copySizeF = g.MeasureString(copyText, _baseFont, PointF.Empty, fmt);
                    }
                    int copyW = (int)Math.Ceiling(copySizeF.Width) + CodeCopyButtonPad * 2;
                    int copyH = headerH; // occupy full header height so top/bottom are flush
                    Rectangle copyRect = new Rectangle(box.Right - CodeCopyButtonPad - copyW, headerTop, copyW, copyH);

                    bool hoverCopy = (_hoverCopyItem == owner && _hoverCopyCodeIndex == codeIndex);
                    // Draw copy background on hover or mouse down
                    if (hoverCopy || (owner == _copyPressedItem && codeIndex == _copyPressedCodeIndex))
                    {
                        bool pressed = (owner == _copyPressedItem && codeIndex == _copyPressedCodeIndex);
                        using (var sb = new SolidBrush(pressed ? _clrCopyPressed : _clrCopyHover))
                        using (var pen = new Pen(_clrCodeBorder))
                        {
                            g.FillRectangle(sb, copyRect);
                            g.DrawRectangle(pen, copyRect);
                        }
                    }
                    // Draw copy text
                    using (var brush = new SolidBrush(_clrLink))
                    using (var fmt = StringFormat.GenericTypographic)
                    {
                        fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                        var textPt = new PointF(copyRect.X + CodeCopyButtonPad, copyRect.Y + (copyRect.Height - _baseFont.Height) / 2f);
                        g.DrawString(copyText, _baseFont, brush, textPt, fmt);
                    }

                    // Language label (top-left)
                    string langLabel = c.Language;
                    if (!string.IsNullOrEmpty(langLabel))
                    {
                        using (var brush = new SolidBrush(ForeColor))
                        using (var fmt = StringFormat.GenericTypographic)
                        {
                            fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                            var labelFont = _boldFont ?? _baseFont;
                            var langPt = new PointF(box.Left + CodeCopyButtonPad, headerTop + (headerH - labelFont.Height) / 2f);
                            g.DrawString(langLabel, labelFont, brush, langPt, fmt);
                        }
                    }

                    // Header separator line
                    using (var pen = new Pen(_clrCodeBorder))
                    {
                        int headerBottom = headerTop + headerH;
                        g.DrawLine(pen, box.Left + CodeBlockPadding, headerBottom, box.Right - CodeBlockPadding, headerBottom);
                    }

                    // Text viewport
                    Rectangle textRect = new Rectangle(box.X + CodeBlockPadding, headerTop + headerH, box.Width - 2 * CodeBlockPadding, textHeight);

                    // Horizontal scrollbar geometry
                    int scrollX = 0;
                    if (owner.CodeScroll != null && codeIndex < owner.CodeScroll.Count)
                        scrollX = owner.CodeScroll[codeIndex];
                    int maxScroll = Math.Max(0, contentNoWrap.Width - textRect.Width);
                    if (scrollX > maxScroll) scrollX = maxScroll;
                    if (scrollX < 0) scrollX = 0;
                    if (owner.CodeScroll != null && codeIndex < owner.CodeScroll.Count)
                        owner.CodeScroll[codeIndex] = scrollX;

                    // Draw text without wrapping with horizontal scroll
                    SyntaxHighlightingRenderer.DrawColoredSegmentsNoWrap(g, coloredSegments, textRect, scrollX);

                    // Record code lines for selection/copy
                    try
                    {
                        using (var fmt = StringFormat.GenericTypographic)
                        {
                            fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                            int lineH = _monoFont.Height;
                            string[] lines = (c.Text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                            for (int li = 0; li < lines.Length; li++)
                            {
                                string line = lines[li];
                                SizeF sz = g.MeasureString(line.Length == 0 ? " " : line, _monoFont, PointF.Empty, fmt);
                                int w = (int)Math.Ceiling(sz.Width);
                                Rectangle lr = new Rectangle(textRect.X - scrollX, textRect.Y + li * lineH, Math.Max(1, w), lineH);
                                if (owner != null)
                                {
                                    if (owner.DrawnSegments == null) owner.DrawnSegments = new List<DrawnSeg>();
                                    owner.DrawnSegments.Add(new DrawnSeg { Rect = lr, Text = line, IsNewLine = false, IsHardBreak = false, Font = _monoFont, LineFirstTextLeft = lr.Left, IsCodeLine = true, CodeBlockIndex = codeIndex, CodeLineIndex = li, CodeLineCount = lines.Length, CodeLanguage = c.Language });
                                    // Insert a hard-break newline after each code line to preserve actual newlines when copying
                                    owner.DrawnSegments.Add(new DrawnSeg { IsNewLine = true, IsHardBreak = true, Rect = new Rectangle(lr.Left, lr.Bottom, 0, 0), Text = null, Font = _monoFont });
                                }
                            }
                        }
                    }
                    catch { }

                    // Draw horizontal scrollbar if needed
                    if (needH && textRect.Width > 0)
                    {
                        Rectangle track = new Rectangle(textRect.X, textRect.Bottom + 2, textRect.Width, CodeHScrollHeight - 4);
                        bool hoverScroll = (_hoverScrollItem == owner && _hoverScrollCodeIndex == codeIndex);
                        Color trackBorder = hoverScroll ? _clrScrollThumbBorder : _clrScrollTrackBorder;
                        Color thumbBorder = hoverScroll ? _clrScrollThumbBorder : _clrScrollThumbBorder;
                        using (var trackBrush = new SolidBrush(_clrScrollTrack))
                        using (var trackPen = new Pen(trackBorder))
                        {
                            g.FillRectangle(trackBrush, track);
                            g.DrawRectangle(trackPen, track);
                        }

                        int thumbW = Math.Max(CodeHScrollThumbMin, (int)Math.Round((double)track.Width * textRect.Width / Math.Max(1, contentNoWrap.Width)));
                        int trackRange = Math.Max(1, track.Width - thumbW);
                        int thumbX = track.X + (maxScroll > 0 ? (int)Math.Round((double)scrollX / maxScroll * trackRange) : 0);
                        Rectangle thumb = new Rectangle(thumbX, track.Y, thumbW, track.Height);
                        using (var thumbBrush = new SolidBrush(_clrScrollThumb))
                        using (var thumbPen = new Pen(thumbBorder))
                        {
                            g.FillRectangle(thumbBrush, thumb);
                            g.DrawRectangle(thumbPen, thumb);
                        }
                    }

                    y += box.Height + 4;
                    if (owner != null) { if (owner.DrawnSegments == null) owner.DrawnSegments = new List<DrawnSeg>(); owner.DrawnSegments.Add(new DrawnSeg { IsNewLine = true, IsHardBreak = true, Rect = new Rectangle(x0, y, 0, 0), Text = null, Font = _baseFont }); }
                    codeIndex++;
                    // Reset numbering when leaving list context
                    numberedCounters.Clear();
                    }
                }
                else if (blk.Type == BlockType.Table)
                {
                    var t = (TableBlock)blk;
                    int cols = Math.Max(0, t.Alignments != null ? t.Alignments.Count : 0);
                    if (cols > 0)
                    {
                        int cellPad = 6;
                        int border = 1;
                        // compute column widths by measuring unbounded to get intrinsic content width
                        int[] colWidths = new int[cols];
                        int headerH = 0;
                        for (int c = 0; c < cols; c++)
                        {
                            var inl = (c < t.Header.Count) ? t.Header[c] : new List<InlineRun>();
                            Size sz = MeasureInlineParagraph(g, inl, _baseFont, int.MaxValue / 4, false);
                            colWidths[c] = Math.Max(colWidths[c], sz.Width);
                            headerH = Math.Max(headerH, sz.Height);
                        }
                        int[] rowHeights = new int[t.Rows.Count];
                        for (int r = 0; r < t.Rows.Count; r++)
                        {
                            int rowH = 0;
                            for (int c = 0; c < cols; c++)
                            {
                                var inl = (c < t.Rows[r].Count) ? t.Rows[r][c] : new List<InlineRun>();
                                Size sz = MeasureInlineParagraph(g, inl, _baseFont, int.MaxValue / 4, false);
                                colWidths[c] = Math.Max(colWidths[c], sz.Width);
                                rowH = Math.Max(rowH, sz.Height);
                            }
                            rowHeights[r] = rowH;
                        }

                        int intrinsicW = border; for (int c = 0; c < cols; c++) intrinsicW += colWidths[c] + cellPad * 2 + border;
                        int viewportW = Math.Max(0, maxWidth);
                        bool needH = intrinsicW > viewportW;
                        int viewW = Math.Min(viewportW, intrinsicW);

                        // Horizontal scroll state (per table)
                        if (owner.TableScroll == null) owner.TableScroll = new List<int>();
                        int tableIndex = 0; // compute table index within this message
                        for (int bi = 0; bi < blocks.Count && !object.ReferenceEquals(blocks[bi], blk); bi++)
                            if (blocks[bi].Type == BlockType.Table) tableIndex++;
                        while (owner.TableScroll.Count <= tableIndex) owner.TableScroll.Add(0);
                        int scrollX = owner.TableScroll[tableIndex];
                        int maxScroll = Math.Max(0, intrinsicW - viewW);
                        if (scrollX < 0) scrollX = 0; if (scrollX > maxScroll) scrollX = maxScroll;
                        owner.TableScroll[tableIndex] = scrollX;

                        // Draw table within a clipped viewport with horizontal offset
                        Rectangle tableViewport = new Rectangle(x0, y, viewW, headerH + cellPad * 2 + 1); // header first; body will extend
                        // compute full table height
                        int tableH = 1 + headerH + cellPad * 2 + 1; for (int r = 0; r < rowHeights.Length; r++) tableH += rowHeights[r] + cellPad * 2 + 1;
                        tableViewport.Height = tableH;

                        Region prevClip = g.Clip;
                        g.SetClip(tableViewport);

                        using (var pen = new Pen(_clrCodeBorder))
                        using (var headerBrush = new SolidBrush(_clrCodeBack))
                        using (var cellBrush = new SolidBrush(BackColor))
                        {
                            int drawX = x0 - scrollX;

                            // Header row background across viewport
                            int headerY = y;
                            int headerHeight = headerH + cellPad * 2;
                            g.FillRectangle(headerBrush, new Rectangle(x0, headerY, viewW, headerHeight + 1));

                            // Draw header cells
                            int x = drawX + 1; // start after left border
                            for (int c = 0; c < cols; c++)
                            {
                                Rectangle cellRect = new Rectangle(x, headerY + 1, colWidths[c] + cellPad * 2, headerHeight);
                                int textX = cellRect.X + cellPad;
                                int avail = colWidths[c];
                                var inl = (c < t.Header.Count) ? t.Header[c] : new List<InlineRun>();
                                DrawInlineParagraph(
                                    g,
                                    textX,
                                    cellRect.Y + (cellRect.Height - _baseFont.Height) / 2,
                                    avail,
                                    inl,
                                    _baseFont,
                                    owner,
                    new InlineCopyContext { IsTableCell = true, TableIndex = tableIndex, TableIsHeader = true, TableRowIndex = -1, TableColIndex = c, TableColumnCount = cols, TableAlignment = (c < t.Alignments.Count ? t.Alignments[c] : TableAlign.Left) });
                                g.DrawRectangle(pen, new Rectangle(cellRect.X - 1, cellRect.Y - 1, cellRect.Width + 1, cellRect.Height + 1));
                                x += cellRect.Width + 1;
                            }
                            int yBody = headerY + headerHeight + 1;
                            // Body rows
                            for (int r = 0; r < t.Rows.Count; r++)
                            {
                                int rowH = rowHeights[r];
                                x = drawX + 1;
                                for (int c = 0; c < cols; c++)
                                {
                                    Rectangle cellRect = new Rectangle(x, yBody, colWidths[c] + cellPad * 2, rowH + cellPad * 2);
                                    g.FillRectangle(cellBrush, cellRect);
                                    int textX = cellRect.X + cellPad;
                                    int avail = colWidths[c];
                                    var inl = (c < t.Rows[r].Count) ? t.Rows[r][c] : new List<InlineRun>();
                                    DrawInlineParagraph(
                                        g,
                                        textX,
                                        cellRect.Y + cellPad,
                                        avail,
                                        inl,
                                        _baseFont,
                                        owner,
                    new InlineCopyContext { IsTableCell = true, TableIndex = tableIndex, TableIsHeader = false, TableRowIndex = r, TableColIndex = c, TableColumnCount = cols, TableAlignment = (c < t.Alignments.Count ? t.Alignments[c] : TableAlign.Left) });
                                    g.DrawRectangle(pen, new Rectangle(cellRect.X - 1, cellRect.Y - 1, cellRect.Width + 1, cellRect.Height + 1));
                                    x += cellRect.Width + 1;
                                }
                                yBody += rowH + cellPad * 2 + 1;
                            }
                        }

                        g.Clip = prevClip;

                        // Draw horizontal scrollbar if needed
                        if (needH && viewW > 0)
                        {
                            Rectangle track = new Rectangle(x0, y + tableH + 2, viewW, CodeHScrollHeight - 4);
                            bool hoverScroll = (_hoverScrollItem == owner && _hoverScrollIsTable && _hoverScrollTableIndex == tableIndex);
                            Color trackBorder = hoverScroll ? _clrScrollThumbBorder : _clrScrollTrackBorder;
                            Color thumbBorder = hoverScroll ? _clrScrollThumbBorder : _clrScrollThumbBorder;
                            using (var trackBrush = new SolidBrush(_clrScrollTrack))
                            using (var trackPen = new Pen(trackBorder))
                            {
                                g.FillRectangle(trackBrush, track);
                                g.DrawRectangle(trackPen, track);
                            }
                            int thumbW = Math.Max(CodeHScrollThumbMin, (int)Math.Round((double)track.Width * viewW / Math.Max(1, intrinsicW)));
                            int trackRange = Math.Max(1, track.Width - thumbW);
                            int thumbX = track.X + (maxScroll > 0 ? (int)Math.Round((double)scrollX / maxScroll * trackRange) : 0);
                            Rectangle thumb = new Rectangle(thumbX, track.Y, thumbW, track.Height);
                            using (var thumbBrush = new SolidBrush(_clrScrollThumb))
                            using (var thumbPen = new Pen(thumbBorder))
                            {
                                g.FillRectangle(thumbBrush, thumb);
                                g.DrawRectangle(thumbPen, thumb);
                            }
                        }
                        y += tableH + (needH ? CodeHScrollHeight : 0);
                        if (owner != null) { if (owner.DrawnSegments == null) owner.DrawnSegments = new List<DrawnSeg>(); owner.DrawnSegments.Add(new DrawnSeg { IsNewLine = true, IsHardBreak = true, Rect = new Rectangle(x0, y, 0, 0), Text = null, Font = _baseFont }); }
                    }
                    y += 2; // small gap after table
                }
            }
        }

        // Carries context for markdown-aware copy behavior per visual line
        private struct InlineCopyContext
        {
            public bool IsHeadingLine;
            public int HeadingLevel;
            public string LinePrefix; // e.g., "- ", "1. ", includes indent spaces
            // Table cell context (when copying from tables)
            public bool IsTableCell;
            public int TableIndex;        // index of table within this message
            public bool TableIsHeader;    // true for header row cells
            public int TableRowIndex;     // 0-based for body rows; undefined for header
            public int TableColIndex;     // 0-based column index
            public int TableColumnCount;  // total columns in the table
            public TableAlign TableAlignment; // alignment for this column
            public Color? ForeOverride;   // non-null forces a foreground color for normal text (e.g. error red)
        }

        private int DrawInlineParagraph(Graphics g, int x, int y, int maxWidth, List<InlineRun> runs, Font baseFont, MessageItem owner, InlineCopyContext ctx)
        {
            int xCursor = x;
            int yCursor = y;
            int lineHeight = baseFont.Height;
            int lineWidth = 0;

            // Collect segments for processing
            var segments = new List<LayoutSeg>();
            foreach (var seg in WordWrapRuns(g, runs, baseFont, maxWidth))
            {
                segments.Add(seg);
            }

            // Draw inline code backgrounds first (for each line)
            int currentLine = 0;
            int lineStartY = yCursor;
            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];

                if (seg.IsNewLine)
                {
                    // Process inline code backgrounds for the current line
                    DrawInlineCodeBackgrounds(g, segments, currentLine, i, x, lineStartY);

                    lineStartY += lineHeight + 2;
                    currentLine = i + 1;
                    lineHeight = baseFont.Height;
                    continue;
                }

                lineHeight = Math.Max(lineHeight, seg.Font.Height);
            }

            // Process the last line
            if (currentLine < segments.Count)
            {
                DrawInlineCodeBackgrounds(g, segments, currentLine, segments.Count, x, lineStartY);
            }

            // Now draw the text
            xCursor = x;
            yCursor = y;
            lineHeight = baseFont.Height;

            bool atVisualLineStart = true;   // first run after any newline (wrap or hard)
            bool atLogicalStart = true;      // first run after block start or hard markdown break
            // Track link run grouping across wraps
            bool prevIsLink = false; string prevLinkUrl = null; int currentLinkRunId = -1;
            foreach (var seg in segments)
            {
                if (seg.IsNewLine)
                {
                    yCursor += lineHeight + 2;
                    xCursor = x;
                    lineWidth = 0;
                    lineHeight = baseFont.Height;
                    // Record line break in drawn segments for selection/copy ordering
                    if (owner != null)
                    {
                        if (owner.DrawnSegments == null) owner.DrawnSegments = new List<DrawnSeg>();
                        owner.DrawnSegments.Add(new DrawnSeg { IsNewLine = true, IsHardBreak = seg.IsHardBreak, Rect = new Rectangle(xCursor, yCursor, 0, 0), Text = null, Font = baseFont });
                    }
                    atVisualLineStart = true;
                    // Only treat hard markdown breaks as logical starts; soft wraps should not reset logical start
                    if (seg.IsHardBreak) atLogicalStart = true;
                    continue;
                }

                Rectangle r = new Rectangle(xCursor, yCursor, seg.Rect.Width, lineHeight);

                if (seg.IsLink)
                {
                    using (var brush = new SolidBrush(_clrLink)) // link color per theme
                    {
                        // Draw text using typographic metrics for better positioning
                        using (var fmt = StringFormat.GenericTypographic)
                        {
                            fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                            g.DrawString(seg.Text, seg.Font, brush, (PointF)r.Location, fmt);

                            // Baseline-aligned underline using font metrics
                            float em = seg.Font.FontFamily.GetEmHeight(seg.Font.Style);
                            float asc = seg.Font.FontFamily.GetCellAscent(seg.Font.Style);
                            float desc = seg.Font.FontFamily.GetCellDescent(seg.Font.Style);
                            float pxPerEm = seg.Font.SizeInPoints * g.DpiY / 72f;
                            float ascentPx = pxPerEm * asc / Math.Max(1f, em);
                            float descentPx = pxPerEm * desc / Math.Max(1f, em);
                            int baselineY = yCursor + (int)Math.Round(ascentPx);
                            // Place underline a bit lower into the descent so it doesn't touch glyph bottoms
                            int underlineY = baselineY + Math.Max(1, (int)Math.Round(descentPx * 0.65f));
                            // Ensure underline stays within the current line box
                            int underlineMax = yCursor + lineHeight - 1;
                            if (underlineY > underlineMax) underlineY = underlineMax;
                            int thickness = Math.Max(1, (int)Math.Round(pxPerEm / 14f));
                            // Compute tight glyph bounds using character ranges to avoid size-dependent overshoot
                            int underlineWidth;
                            RectangleF layoutRect = new RectangleF(r.Left, r.Top, 100000f, lineHeight);
                            using (var fmtTight = (StringFormat)StringFormat.GenericTypographic.Clone())
                            {
                                fmtTight.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                                fmtTight.SetMeasurableCharacterRanges(new CharacterRange[] { new CharacterRange(0, seg.Text != null ? seg.Text.Length : 0) });
                                var regions = g.MeasureCharacterRanges(seg.Text ?? string.Empty, seg.Font, layoutRect, fmtTight);
                                RectangleF tight = (regions != null && regions.Length > 0) ? regions[0].GetBounds(g) : new RectangleF(r.Left, r.Top, 0f, 0f);
                                // Dispose regions to avoid GDI handle leaks
                                if (regions != null)
                                {
                                    for (int ri = 0; ri < regions.Length; ri++)
                                    {
                                        if (regions[ri] != null) regions[ri].Dispose();
                                    }
                                }
                                underlineWidth = Math.Max(0, (int)Math.Round(tight.Width));
                            }
                            using (var pen = new Pen(brush.Color, thickness))
                            {
                                // Snap to whole pixels for crispness
                                g.DrawLine(pen, r.Left, underlineY, r.Left + underlineWidth, underlineY);
                            }
                        }
                    }
                    // Record hit rectangle covering the full line height for reliable clicking
                    if (owner != null)
                    {
                        // Use tight glyph bounds width for the hitbox as well
                        int hitW;
                        RectangleF layoutRect2 = new RectangleF(xCursor, yCursor, 100000f, lineHeight);
                        using (var fmtTight2 = (StringFormat)StringFormat.GenericTypographic.Clone())
                        {
                            fmtTight2.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                            fmtTight2.SetMeasurableCharacterRanges(new CharacterRange[] { new CharacterRange(0, seg.Text != null ? seg.Text.Length : 0) });
                            var regions2 = g.MeasureCharacterRanges(seg.Text ?? string.Empty, seg.Font, layoutRect2, fmtTight2);
                            RectangleF tight2 = (regions2 != null && regions2.Length > 0) ? regions2[0].GetBounds(g) : new RectangleF(xCursor, yCursor, 0f, 0f);
                            if (regions2 != null)
                            {
                                for (int ri = 0; ri < regions2.Length; ri++)
                                {
                                    if (regions2[ri] != null) regions2[ri].Dispose();
                                }
                            }
                            hitW = Math.Max(0, (int)Math.Round(tight2.Width));
                        }
                        var hitRect = new Rectangle(xCursor, yCursor, hitW, lineHeight);
                        if (owner.LinkHits == null) owner.LinkHits = new List<LinkHit>();
                        owner.LinkHits.Add(new LinkHit { Rect = hitRect, Url = seg.LinkUrl });
                    }
                }
                else
                {
                    Color textColor = ctx.ForeOverride.HasValue ? ctx.ForeOverride.Value : ForeColor;
                    using (var brush = new SolidBrush(textColor))
                    {
                        using (var fmt = StringFormat.GenericTypographic)
                        {
                            fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                            g.DrawString(seg.Text, seg.Font, brush, (PointF)r.Location, fmt);
                        }
                    }
                }
                // Record drawn text segment for selection/copy
                if (owner != null && !string.IsNullOrEmpty(seg.Text))
                {
                    if (owner.DrawnSegments == null) owner.DrawnSegments = new List<DrawnSeg>();
                    bool isLink = seg.IsLink;
                    string linkUrl = seg.LinkUrl;
                    if (isLink)
                    {
                        if (!prevIsLink || !string.Equals(prevLinkUrl, linkUrl, StringComparison.Ordinal))
                        {
                            // Start a new link run id unique within this message
                            if (owner != null) currentLinkRunId = owner.LinkRunSeq++;
                        }
                    }
                    owner.DrawnSegments.Add(new DrawnSeg
                    {
                        Rect = r,
                        Text = seg.Text,
                        IsNewLine = false,
                        IsHardBreak = false,
                        Font = seg.Font ?? baseFont,
                        IsHeadingLine = ctx.IsHeadingLine,
                        HeadingLevel = ctx.HeadingLevel,
                        LinePrefix = ctx.LinePrefix,
                        LineFirstTextLeft = atVisualLineStart ? r.Left : 0,
                        IsLogicalLineStart = atLogicalStart,
                        IsTableCell = ctx.IsTableCell,
                        TableIndex = ctx.TableIndex,
                        TableIsHeader = ctx.TableIsHeader,
                        TableRowIndex = ctx.TableRowIndex,
                        TableColIndex = ctx.TableColIndex,
                        TableColumnCount = ctx.TableColumnCount,
                        TableAlignment = ctx.TableAlignment,
                        IsLink = isLink,
                        LinkUrl = isLink ? linkUrl : null,
                        LinkRunId = isLink ? currentLinkRunId : -1
                    });
                }
                xCursor += r.Width;
                lineWidth += r.Width;
                lineHeight = Math.Max(lineHeight, seg.Font.Height);
                atVisualLineStart = false;
                // After emitting first run of a logical line, clear the logical-start flag until a hard break or new block
                atLogicalStart = false;
                prevIsLink = seg.IsLink; prevLinkUrl = seg.LinkUrl;
            }

            // last line height
            yCursor += lineHeight + 2;
            return yCursor - y;
        }

        // Paint selection highlight rectangles for the current message
        private void DrawSelection(Graphics g, MessageItem it)
        {
            if (!_hasSelection || it == null || it.DrawnSegments == null || it.DrawnSegments.Count == 0) return;
            // Selection is defined by two virtual points (anchor and caret)
            Point anchor = _selStartVirt;
            Point caret = _selEndVirt;
            int yTop = Math.Min(anchor.Y, caret.Y);
            int yBot = Math.Max(anchor.Y, caret.Y);
            int anchorX = anchor.X;
            int caretX = caret.X;

            // Determine start and end line rectangles (by Y)
            Rectangle startLine = GetLineBoundsAtY(it, anchor.Y);
            Rectangle endLine = GetLineBoundsAtY(it, caret.Y);
            bool sameLine = (!startLine.IsEmpty && !endLine.IsEmpty && startLine.Top == endLine.Top && startLine.Bottom == endLine.Bottom);
            bool directionDown = caret.Y > anchor.Y; // true when selecting top->bottom

            using (var sb = new SolidBrush(_clrSelectionBack))
            using (var fmt = StringFormat.GenericTypographic)
            {
                fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                foreach (var ds in it.DrawnSegments)
                {
                    if (ds.IsNewLine) continue;
                    Rectangle r = ds.Rect;
                    if (r.Height <= 0 || r.Width <= 0) continue;
                    if (r.Bottom <= yTop || r.Top >= yBot) continue; // outside vertical range

                    int left = int.MinValue;
                    int right = int.MaxValue;
                    if (sameLine)
                    {
                        left = Math.Min(anchorX, caretX);
                        right = Math.Max(anchorX, caretX);
                    }
                    else
                    {
                        // On the anchor (start) line
                        if (LinesOverlap(r, startLine))
                        {
                            if (directionDown) left = anchorX; else right = anchorX;
                        }
                        // On the caret (end) line
                        if (LinesOverlap(r, endLine))
                        {
                            if (directionDown) right = caretX; else left = caretX;
                        }
                    }

                    var snap = SnapToCharRange(g, fmt, ds.Text ?? string.Empty, ds.Font ?? this.Font, r, left, right);
                    if (snap.EndPx > snap.StartPx)
                    {
                        int L = r.Left + snap.StartPx;
                        int R = r.Left + snap.EndPx;
                        g.FillRectangle(sb, Rectangle.FromLTRB(L, r.Top, R, r.Bottom));
                    }
                }
            }
        }

        private static bool LinesOverlap(Rectangle a, Rectangle b)
        {
            if (a.Height <= 0 || b.Height <= 0) return false;
            return a.Top < b.Bottom && b.Top < a.Bottom;
        }

        private Rectangle GetLineBoundsAtY(MessageItem it, int y)
        {
            if (it == null || it.DrawnSegments == null) return Rectangle.Empty;
            foreach (var ds in it.DrawnSegments)
            {
                if (ds.IsNewLine) continue;
                if (y >= ds.Rect.Top && y < ds.Rect.Bottom) return ds.Rect;
            }
            return Rectangle.Empty;
        }

        private int MeasureInlineParagraphHeight(Graphics g, int maxWidth, List<InlineRun> runs, Font baseFont)
        {
            int lineHeight = baseFont.Height;
            int total = 0;
            foreach (var seg in WordWrapRuns(g, runs, baseFont, maxWidth))
            {
                if (seg.IsNewLine)
                {
                    total += lineHeight + 2;
                    lineHeight = baseFont.Height;
                    continue;
                }
                lineHeight = Math.Max(lineHeight, seg.Font.Height);
            }
            total += lineHeight + 2;
            return total;
        }

        private void DrawInlineCodeBackgrounds(Graphics g, List<LayoutSeg> segments, int lineStart, int lineEnd, int x, int y)
        {
            int currentX = x;

            for (int i = lineStart; i < lineEnd; i++)
            {
                var seg = segments[i];

                if (seg.IsInlineCode)
                {
                    // Find the extent of consecutive inline code segments
                    int startX = currentX;
                    int endX = currentX + seg.Rect.Width;
                    int j = i + 1;

                    // Look ahead for more inline code segments
                    while (j < lineEnd && segments[j].IsInlineCode)
                    {
                        endX += segments[j].Rect.Width;
                        j++;
                    }

                    // Draw background for the entire inline code run
                    Rectangle bg = new Rectangle(
                        startX - InlineCodePaddingX,
                        y - InlineCodePaddingY,
                        endX - startX + 2 * InlineCodePaddingX,
                        seg.Font.Height + 2 * InlineCodePaddingY);

                    using (var sb = new SolidBrush(_clrInlineCodeBack))
                    using (var pen = new Pen(_clrInlineCodeBorder))
                    {
                        g.FillRectangle(sb, bg);
                        g.DrawRectangle(pen, bg);
                    }

                    // Advance past the entire run we just consumed. We must add the
                    // full run width (endX - startX), not just the last segment's
                    // width: otherwise currentX is left short by the rest of the run
                    // and any later inline-code background on the same line is shifted
                    // left, overlapping preceding text and ending mid-word.
                    currentX = endX;
                    i = j - 1; // -1 because the loop will increment
                    continue;
                }

                currentX += seg.Rect.Width;
            }
        }

        private Font GetHeadingFont(int level)
        {
            switch (level)
            {
                case 1: return _h1;
                case 2: return _h2;
                case 3: return _h3;
                case 4: return _h4;
                case 5: return _h5;
                default: return _h6;
            }
        }

        // ---------- Scrolling & input ----------
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);

            // If Shift is pressed and we're hovering a horizontally scrollable code block or table,
            // apply wheel to horizontal scroll instead of the transcript vertical scrollbar.
            try
            {
                if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift)
                {
                    var ui = HitTestCodeUI(e.Location);
                    if (ui.Hit && ui.ContentWidth > ui.ViewportWidth && ui.Item != null)
                    {
                        int hStep = Math.Max(16, ScrollStep); // horizontal step per wheel notch
                        int deltaX = (int)System.Math.Round(-(e.Delta / 120.0) * hStep, MidpointRounding.AwayFromZero);
                        if (ui.IsTable)
                        {
                            int idx = ui.TableIndex;
                            if (ui.Item.TableScroll == null) ui.Item.TableScroll = new List<int>();
                            while (ui.Item.TableScroll.Count <= idx) ui.Item.TableScroll.Add(0);
                            int current = ui.Item.TableScroll[idx];
                            int maxScroll = Math.Max(0, ui.ContentWidth - ui.ViewportWidth);
                            int next = Math.Max(0, Math.Min(maxScroll, current + deltaX));
                            ui.Item.TableScroll[idx] = next;
                            Invalidate();
                            return; // handled
                        }
                        else if (ui.CodeIndex >= 0)
                        {
                            int idx = ui.CodeIndex;
                            if (ui.Item.CodeScroll == null) ui.Item.CodeScroll = new List<int>();
                            while (ui.Item.CodeScroll.Count <= idx) ui.Item.CodeScroll.Add(0);
                            int current = ui.Item.CodeScroll[idx];
                            int maxScroll = Math.Max(0, ui.ContentWidth - ui.ViewportWidth);
                            int next = Math.Max(0, Math.Min(maxScroll, current + deltaX));
                            ui.Item.CodeScroll[idx] = next;
                            Invalidate();
                            return; // handled
                        }
                    }

                    // Edit-diff body/scrollbar: shift+wheel scrolls it horizontally.
                    EditDiffScrollHit edh;
                    if (HitTestEditDiffScrollArea(e.Location, out edh) && edh.ContentWidth > edh.ViewportWidth)
                    {
                        int hStep = Math.Max(16, ScrollStep);
                        int deltaX = (int)System.Math.Round(-(e.Delta / 120.0) * hStep, MidpointRounding.AwayFromZero);
                        int maxScroll = Math.Max(0, edh.ContentWidth - edh.ViewportWidth);
                        SetEditDiffScroll(edh.Key, Math.Max(0, Math.Min(maxScroll, GetEditDiffScroll(edh.Key) + deltaX)));
                        Invalidate();
                        return; // handled
                    }
                }
            }
            catch { }

            // Fallback to vertical scroll using proportional precision handling
            ScrollByWheelDelta(e.Delta);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_draggingEditDiffScroll)
            {
                _draggingEditDiffScroll = false; _dragEditDiffKey = null; Capture = false; Invalidate();
                return;
            }
            if (_draggingHScroll)
            {
                _draggingHScroll = false; _dragScrollItem = null; _dragScrollCodeIndex = -1; _dragScrollIsTable = false; Capture = false; Invalidate();
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                if (_isSelecting)
                {
                    // Finish selection; if tiny drag, clear selection
                    Point virt = new Point(e.X, e.Y + _scrollOffset);
                    _selEndVirt = virt;
                    bool tiny = Math.Abs(_selEndVirt.X - _selStartVirt.X) < 3 && Math.Abs(_selEndVirt.Y - _selStartVirt.Y) < 3;
                    _isSelecting = false;
                    if (tiny)
                    {
                        _hasSelection = false; _selectionItem = null; _suppressLinkClick = false; Invalidate();
                    }
                    else
                    {
                        _hasSelection = true; Invalidate();
                    }
                    return;
                }
            }
            if (e.Button == MouseButtons.Right)
            {
                _ctxHit = HitTest(e.Location);
                if (_ctxHit != null)
                {
                    try
                    {
                        // Rebuild context menu items for this hit
                        _ctx.Items.Clear();
                        // Detect if the cursor is over a hyperlink
                        string linkUnderCursor = HitTestLink(e.Location);
                        if (_hasSelection && _selectionItem == _ctxHit)
                        {
                            _ctx.Items.Add("Copy Selection", null, delegate
                            {
                                try
                                {
                                    string sel = ExtractSelectedTextFromItem(_selectionItem);
                                    if (!string.IsNullOrEmpty(sel)) SafeClipboardSetText(sel);
                                }
                                catch { }
                            });
                        }
                        else
                        {
                            _ctx.Items.Add("Copy Message", null, delegate { if (_ctxHit != null) SafeClipboardSetText(_ctxHit.RawMarkdown ?? string.Empty); });
                        }
                        // Offer to copy the link address when right-clicking a hyperlink
                        if (!string.IsNullOrEmpty(linkUnderCursor))
                        {
                            _ctx.Items.Add("Copy Link Address", null, delegate { SafeClipboardSetText(linkUnderCursor); });
                        }
                        if (_ctxHit.Role == MessageRole.User)
                        {
                            _ctx.Items.Add("Edit...", null, delegate
                            {
                                try
                                {
                                    int idx = IndexOfMessageItem(_ctxHit);
                                    if (idx >= 0)
                                    {
                                        var handler = UserMessageEditRequested;
                                        if (handler != null) handler(idx, _ctxHit.RawMarkdown ?? string.Empty);
                                    }
                                }
                                catch { }
                            });
                        }
                    }
                    catch { }

                    _ctx.Show(this, e.Location);
                }
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                // Attachment pill click
                var pill = HitTestAttachmentPill(e.Location);
                if (pill.Item != null && pill.Index >= 0)
                {
                    _pressAttachItem = null; _pressAttachIndex = -1; Invalidate();
                    OpenAttachmentInViewer(pill.Item, pill.Index);
                    return;
                }
                // Edit-diff header click → toggle collapse and re-layout
                string edKey;
                if (HitTestEditDiff(e.Location, out edKey))
                {
                    _editDiffCollapsed[edKey] = !IsEditDiffCollapsed(edKey);
                    InvalidateAllLayout(); // collapse state changes a bubble's height without touching its Blocks
                    Reflow();
                    Invalidate();
                    return;
                }
                // Retry button click → re-run the failed turn
                var retryUp = HitTestRetry(e.Location);
                if (retryUp != null && _retryPressedItem == retryUp)
                {
                    _retryPressedItem = null;
                    Invalidate();
                    var retryHandler = RetryRequested;
                    if (retryHandler != null) retryHandler();
                    return;
                }
                if (_retryPressedItem != null) { _retryPressedItem = null; Invalidate(); }
                // Copy button click
                var ui = HitTestCodeUI(e.Location);
                if (ui.Hit && ui.Which == CodeUiHit.CopyButton && ui.Item != null)
                {
                    var cb = (CodeBlock)ui.Block;
                    // Normalize newlines to CRLF for Windows clipboard
                    string text = cb != null ? cb.Text : string.Empty;
                    if (text == null) text = string.Empty;
                    string normalized = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
                    SafeClipboardSetText(normalized);
                    _copyPressedItem = null; _copyPressedCodeIndex = -1;
                    Invalidate();
                    return;
                }
                // Graph image click → open the pan/zoom viewer (but not when finishing a text drag-select)
                if (ui.Hit && ui.Which == CodeUiHit.GraphImage && ui.Item != null && !_hasSelection)
                {
                    OpenGraphInViewer((CodeBlock)ui.Block);
                    return;
                }
                // Clear any pressed copy state on mouse up
                if (_copyPressedItem != null)
                { _copyPressedItem = null; _copyPressedCodeIndex = -1; Invalidate(); }
                // Link click detection
                if (!_hasSelection && !_suppressLinkClick)
                {
                    string link = HitTestLink(e.Location);
                    if (!string.IsNullOrEmpty(link))
                    {
                        // Agent "View transcript" links are handled in-app (open the read-only viewer),
                        // never launched as a URL.
                        if (AgentTranscriptLinks.IsTranscriptLink(link))
                        {
                            Action<string> h = AgentTranscriptLinkClicked;
                            if (h != null) h(link);
                            return;
                        }
                        try
                        {
                            string supermiumPath = @"C:\\Program Files\\Supermium\\chrome.exe";
                            if (IsWindowsXP() && File.Exists(supermiumPath))
                            {
                                // On XP (no reliable default browser override), prefer Supermium if installed
                                System.Diagnostics.Process.Start(supermiumPath, link);
                            }
                            else
                            {
                                // On newer OS (or if Supermium absent), let shell pick the default handler
                                System.Diagnostics.Process.Start(link);
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _mouseDownClient = e.Location; _suppressLinkClick = false;
                var pill = HitTestAttachmentPill(e.Location);
                if (pill.Item != null && pill.Index >= 0)
                {
                    _pressAttachItem = pill.Item; _pressAttachIndex = pill.Index; Invalidate();
                    return;
                }
                // Edit-diff horizontal scrollbar: thumb drag or track jump.
                EditDiffScrollHit edh;
                if (HitTestEditDiffScroll(e.Location, out edh))
                {
                    Point virt = new Point(e.X, e.Y + _scrollOffset);
                    int trackW = Math.Max(1, edh.Track.Width);
                    int thumbW = Math.Max(CodeHScrollThumbMin, (int)Math.Round((double)trackW * edh.ViewportWidth / Math.Max(1, edh.ContentWidth)));
                    int trackRange = Math.Max(1, trackW - thumbW);
                    int maxScroll = Math.Max(0, edh.ContentWidth - edh.ViewportWidth);
                    int thumbX = edh.Track.X + (maxScroll > 0 ? (int)Math.Round((double)GetEditDiffScroll(edh.Key) / maxScroll * trackRange) : 0);
                    bool onThumb = virt.X >= thumbX && virt.X <= thumbX + thumbW;
                    if (!onThumb)
                    {
                        int clickOffset = Math.Max(0, Math.Min(trackRange, virt.X - edh.Track.X - thumbW / 2));
                        SetEditDiffScroll(edh.Key, (int)Math.Round((double)clickOffset / trackRange * maxScroll));
                    }
                    _draggingEditDiffScroll = true;
                    _dragEditDiffKey = edh.Key;
                    _dragEditDiffTrack = edh.Track;
                    _dragEditDiffContentWidth = edh.ContentWidth;
                    _dragEditDiffViewportWidth = edh.ViewportWidth;
                    _dragEditDiffStartMouseX = e.X;
                    _dragEditDiffStartScroll = GetEditDiffScroll(edh.Key);
                    Capture = true;
                    Invalidate();
                    return;
                }
                // Retry button press on a trailing error notice
                var retryDown = HitTestRetry(e.Location);
                if (retryDown != null)
                {
                    _retryPressedItem = retryDown;
                    Invalidate();
                    return;
                }
                var ui = HitTestCodeUI(e.Location);
                if (ui.Hit)
                {
                    if (ui.Which == CodeUiHit.CopyButton)
                    {
                        _copyPressedItem = ui.Item;
                        _copyPressedCodeIndex = ui.CodeIndex;
                        Invalidate();
                        return;
                    }
                    if (ui.Which == CodeUiHit.ScrollThumb)
                    {
                        _draggingHScroll = true;
                        _dragScrollItem = ui.Item;
                        _dragScrollCodeIndex = ui.IsTable ? -1 : ui.CodeIndex;
                        _dragScrollIsTable = ui.IsTable;
                        _dragScrollTrackRect = ui.ScrollTrackRect;
                        _dragScrollContentWidth = ui.ContentWidth;
                        _dragScrollViewportWidth = ui.ViewportWidth;
                        _dragStartMouseX = e.X;
                        _dragStartScrollX = ui.IsTable ? ui.Item.TableScroll[ui.TableIndex] : ui.Item.CodeScroll[ui.CodeIndex];
                        Capture = true;
                        return;
                    }
                    if (ui.Which == CodeUiHit.ScrollTrack)
                    {
                        // Jump to position where thumb center aligns with click
                        int trackWidth = Math.Max(1, ui.ScrollTrackRect.Width);
                        int thumbW = Math.Max(CodeHScrollThumbMin, (int)Math.Round((double)trackWidth * ui.ViewportWidth / Math.Max(1, ui.ContentWidth)));
                        int trackRange = Math.Max(1, trackWidth - thumbW);
                        int clickOffset = Math.Max(0, Math.Min(trackRange, e.X - ui.ScrollTrackRect.X - thumbW / 2));
                        int maxScroll = Math.Max(0, ui.ContentWidth - ui.ViewportWidth);
                        int newScroll = (int)Math.Round((double)clickOffset / trackRange * maxScroll);
                        if (ui.IsTable) ui.Item.TableScroll[ui.TableIndex] = newScroll; else ui.Item.CodeScroll[ui.CodeIndex] = newScroll;
                        Invalidate();
                        return;
                    }
                }
                // Prepare for a potential selection within a message, but don't start until drag threshold crossed
                var hitItem = HitTest(e.Location);
                if (hitItem != null)
                {
                    _selectionItem = hitItem;
                    Point virt = new Point(e.X, e.Y + _scrollOffset);
                    _selStartVirt = virt; _selEndVirt = virt;
                    // Do NOT set _isSelecting yet; wait for drag threshold in OnMouseMove
                    _hasSelection = false;
                    Invalidate();
                    return;
                }
                else
                {
                    // Clicked outside messages; clear selection
                    _isSelecting = false; _hasSelection = false; _selectionItem = null; Invalidate();
                }
            }
        }

        private static bool IsWindowsXP()
        {
            try
            {
                OperatingSystem os = Environment.OSVersion;
                if (os.Platform == PlatformID.Win32NT && os.Version.Major == 5)
                {
                    // 5.1 = XP, 5.2 = XP x64 / Server 2003
                    return os.Version.Minor == 1 || os.Version.Minor == 2;
                }
            }
            catch { }
            return false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            // An active edit-diff scrollbar drag takes precedence over selection/hover so dragging past
            // the selection threshold doesn't get hijacked into a text selection.
            if (_draggingEditDiffScroll && !string.IsNullOrEmpty(_dragEditDiffKey))
            {
                int ddx = e.X - _dragEditDiffStartMouseX;
                int dtrackWidth = Math.Max(1, _dragEditDiffTrack.Width);
                int dthumbW = Math.Max(CodeHScrollThumbMin, (int)Math.Round((double)dtrackWidth * _dragEditDiffViewportWidth / Math.Max(1, _dragEditDiffContentWidth)));
                int dtrackRange = Math.Max(1, dtrackWidth - dthumbW);
                int dmaxScroll = Math.Max(0, _dragEditDiffContentWidth - _dragEditDiffViewportWidth);
                int ddelta = (int)Math.Round((double)ddx / dtrackRange * dmaxScroll);
                SetEditDiffScroll(_dragEditDiffKey, Math.Max(0, Math.Min(dmaxScroll, _dragEditDiffStartScroll + ddelta)));
                Invalidate();
                return;
            }
            // If mouse is down over a message but we haven't entered selection yet, start selection on threshold
            if (!_isSelecting && _selectionItem != null && (Control.MouseButtons & MouseButtons.Left) == MouseButtons.Left)
            {
                if (Math.Abs(e.X - _mouseDownClient.X) >= 3 || Math.Abs(e.Y - _mouseDownClient.Y) >= 3)
                {
                    // Crossed drag threshold: begin selection
                    _isSelecting = true;
                    _hasSelection = true;
                    _suppressLinkClick = true;
                    Point virtStart = _selStartVirt; // already set on mouse down
                    Point virtNow = new Point(e.X, e.Y + _scrollOffset);
                    _selEndVirt = virtNow;
                    Invalidate();
                    return;
                }
            }
            if (_isSelecting)
            {
                Point virt = new Point(e.X, e.Y + _scrollOffset);
                _selEndVirt = virt;
                if (Math.Abs(e.X - _mouseDownClient.X) >= 3 || Math.Abs(e.Y - _mouseDownClient.Y) >= 3)
                {
                    _suppressLinkClick = true; _hasSelection = true;
                }
                Invalidate();
                return;
            }
            if (_draggingHScroll && _dragScrollItem != null)
            {
                // update scroll based on mouse delta
                int dx = e.X - _dragStartMouseX;
                int trackWidth = Math.Max(1, _dragScrollTrackRect.Width);
                int thumbW = Math.Max(CodeHScrollThumbMin, (int)Math.Round((double)trackWidth * _dragScrollViewportWidth / Math.Max(1, _dragScrollContentWidth)));
                int trackRange = Math.Max(1, trackWidth - thumbW);
                int maxScroll = Math.Max(0, _dragScrollContentWidth - _dragScrollViewportWidth);
                int deltaScroll = (int)Math.Round((double)dx / trackRange * maxScroll);
                int newScroll = Math.Max(0, Math.Min(maxScroll, _dragStartScrollX + deltaScroll));
                if (_dragScrollIsTable)
                {
                    // Find current table index via hit test again or approximate: compute by geometry would be heavy; reuse start scroll index mapping
                    // We'll adjust both code path: On mouse down stored _dragScrollTrackRect and widths; here only need to update the same target list.
                    // Use the closest matching thumb width mapping: we piggybacked _dragStartScrollX from the appropriate list earlier.
                    // For table, we must identify index; we can recompute using HitTestCodeUI at the current mouse to find which table scrollbar is under drag.
                    var tableUi = HitTestCodeUI(new Point(e.X, e.Y));
                    if (tableUi.Hit && tableUi.IsTable) { _dragScrollItem.TableScroll[tableUi.TableIndex] = newScroll; }
                    else { if (_dragScrollItem.TableScroll != null && _dragScrollItem.TableScroll.Count > 0) _dragScrollItem.TableScroll[0] = newScroll; }
                }
                else
                {
                    if (_dragScrollCodeIndex >= 0 && _dragScrollCodeIndex < _dragScrollItem.CodeScroll.Count)
                        _dragScrollItem.CodeScroll[_dragScrollCodeIndex] = newScroll;
                }
                Invalidate();
                return;
            }

            // Hover check for copy button and set cursor
            var ui = HitTestCodeUI(e.Location);
            bool overInteractive = false;
            if (ui.Hit && (ui.Which == CodeUiHit.CopyButton || ui.Which == CodeUiHit.ScrollThumb || ui.Which == CodeUiHit.ScrollTrack || ui.Which == CodeUiHit.GraphImage))
            {
                overInteractive = true;
                if (ui.Which == CodeUiHit.CopyButton)
                {
                    _hoverCopyItem = ui.Item; _hoverCopyCodeIndex = ui.CodeIndex;
                }
                if (ui.Which == CodeUiHit.ScrollThumb || ui.Which == CodeUiHit.ScrollTrack)
                {
                    _hoverScrollItem = ui.Item; _hoverScrollCodeIndex = ui.CodeIndex; _hoverScrollIsTable = ui.IsTable; _hoverScrollTableIndex = ui.TableIndex;
                }
            }
            else
            {
                _hoverCopyItem = null; _hoverCopyCodeIndex = -1;
                _hoverScrollItem = null; _hoverScrollCodeIndex = -1; _hoverScrollIsTable = false; _hoverScrollTableIndex = -1;
            }

            // Hover over the Retry button on a trailing error notice
            var retryHover = HitTestRetry(e.Location);
            if (retryHover != null)
            {
                _hoverRetryItem = retryHover; Cursor = Cursors.Hand; Invalidate(); return;
            }
            else
            {
                _hoverRetryItem = null;
            }

            // Hover over attachment pill
            var pillHit = HitTestAttachmentPill(e.Location);
            if (pillHit.Item != null)
            {
                overInteractive = true; _hoverAttachItem = pillHit.Item; _hoverAttachIndex = pillHit.Index; Cursor = Cursors.Hand; Invalidate(); return;
            }
            else
            {
                _hoverAttachItem = null; _hoverAttachIndex = -1;
            }

            string link = HitTestLink(e.Location);
            if (!string.IsNullOrEmpty(link)) { overInteractive = true; Cursor = Cursors.Hand; return; }

            if (overInteractive)
            {
                // Use standard cursor for scroll bars; hand for copy
                if (ui.Which == CodeUiHit.ScrollThumb || ui.Which == CodeUiHit.ScrollTrack) Cursor = Cursors.Default;
                else if (ui.Which == CodeUiHit.CopyButton || ui.Which == CodeUiHit.GraphImage) Cursor = Cursors.Hand;
                else Cursor = Cursors.Default;
            }
            else
            {
                Cursor = Cursors.Default;
            }
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverCopyItem = null; _hoverCopyCodeIndex = -1;
            _hoverScrollItem = null; _hoverScrollCodeIndex = -1;
            _copyPressedItem = null; _copyPressedCodeIndex = -1;
            _hoverRetryItem = null; _retryPressedItem = null;
            _hoverAttachItem = null; _hoverAttachIndex = -1; _pressAttachItem = null; _pressAttachIndex = -1;
            Invalidate();
        }

        private struct PillHit
        {
            public MessageItem Item; public int Index;
        }
        private PillHit HitTestAttachmentPill(Point clientPt)
        {
            PillHit ph = new PillHit { Item = null, Index = -1 };
            Point virt = new Point(clientPt.X, clientPt.Y + _scrollOffset);
            foreach (var it in _items)
            {
                if (it.AttachmentPillRects == null || it.AttachmentPillRects.Count == 0) continue;
                for (int i = 0; i < it.AttachmentPillRects.Count; i++)
                {
                    var r = it.AttachmentPillRects[i];
                    if (r.Contains(virt)) { ph.Item = it; ph.Index = i; return ph; }
                }
            }
            return ph;
        }

        private void OpenAttachmentInViewer(MessageItem it, int index)
        {
            try
            {
                if (it == null || it.Attachments == null || index < 0 || index >= it.Attachments.Count) return;
                var af = it.Attachments[index]; if (af == null) return;

                bool dark = false;
                try
                {
                    string theme = AppSettings.GetString("theme");
                    dark = !string.IsNullOrEmpty(theme) && theme.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
                }
                catch { dark = false; }

                using (var dlg = new FileViewerForm())
                {
                    dlg.Text = af.FileName ?? "Attachment";
                    dlg.StartPosition = FormStartPosition.CenterParent;
                    dlg.LoadAttachment(af, dark);
                    dlg.ShowDialog(FindForm());
                }
            }
            catch { }
        }

        // Open a rendered dot block in the standalone pan/zoom viewer. Hands the viewer a copy of the
        // cached bitmap (so disposing the viewer can't free the transcript's image) plus the engine and
        // DOT source, which the viewer uses for Copy and for a crisp high-DPI re-render.
        private void OpenGraphInViewer(CodeBlock cb)
        {
            try
            {
                if (cb == null) return;
                string engine;
                if (!TryGetGraphEngine(cb.Language, out engine)) return;
                GraphvizRenderer.GraphResult res;
                if (!GraphvizRenderer.TryGetResult(engine, cb.Text, out res)) return;
                if (res.Failed || res.Image == null) return;

                Bitmap copy;
                try { copy = new Bitmap(res.Image); }
                catch { return; }

                using (var viewer = new GraphImageViewerForm(copy, engine, cb.Text))
                {
                    viewer.StartPosition = FormStartPosition.CenterParent;
                    viewer.ShowDialog(FindForm());
                }
            }
            catch { }
        }

        private MessageItem HitTest(Point clientPt)
        {
            Point virt = new Point(clientPt.X, clientPt.Y + _scrollOffset);
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i].Bounds.Contains(virt))
                    return _items[i];
            }
            return null;
        }

        // Returns true and the diff key if the point is over an edit-diff header row.
        // Hit test the Retry button on a trailing error notice. Only the last message can carry the
        // button (rect captured at draw time, virtual coords), so only it is checked.
        private MessageItem HitTestRetry(Point clientPt)
        {
            if (_items.Count == 0) return null;
            var it = _items[_items.Count - 1];
            if (it.RetryRect.Width <= 0 || !IsRetryTarget(it)) return null;
            Point virt = new Point(clientPt.X, clientPt.Y + _scrollOffset);
            return it.RetryRect.Contains(virt) ? it : null;
        }

        private bool HitTestEditDiff(Point clientPt, out string key)
        {
            key = null;
            Point virt = new Point(clientPt.X, clientPt.Y + _scrollOffset);
            foreach (var it in _items)
            {
                if (it.EditDiffHits == null || it.EditDiffHits.Count == 0) continue;
                if (!it.Bounds.Contains(virt)) continue;
                for (int i = 0; i < it.EditDiffHits.Count; i++)
                {
                    if (it.EditDiffHits[i].Rect.Contains(virt)) { key = it.EditDiffHits[i].Key; return true; }
                }
            }
            return false;
        }

        // Returns true and the scrollbar info if the point is over an edit-diff horizontal scrollbar.
        private bool HitTestEditDiffScroll(Point clientPt, out EditDiffScrollHit hit)
        {
            hit = default(EditDiffScrollHit);
            Point virt = new Point(clientPt.X, clientPt.Y + _scrollOffset);
            foreach (var it in _items)
            {
                if (it.EditDiffScrollHits == null || it.EditDiffScrollHits.Count == 0) continue;
                if (!it.Bounds.Contains(virt)) continue;
                for (int i = 0; i < it.EditDiffScrollHits.Count; i++)
                {
                    if (it.EditDiffScrollHits[i].Track.Contains(virt)) { hit = it.EditDiffScrollHits[i]; return true; }
                }
            }
            return false;
        }

        // Like HitTestEditDiffScroll but matches the diff body too (for shift+wheel over the content).
        private bool HitTestEditDiffScrollArea(Point clientPt, out EditDiffScrollHit hit)
        {
            hit = default(EditDiffScrollHit);
            Point virt = new Point(clientPt.X, clientPt.Y + _scrollOffset);
            foreach (var it in _items)
            {
                if (it.EditDiffScrollHits == null || it.EditDiffScrollHits.Count == 0) continue;
                if (!it.Bounds.Contains(virt)) continue;
                for (int i = 0; i < it.EditDiffScrollHits.Count; i++)
                {
                    var h = it.EditDiffScrollHits[i];
                    if (h.Body.Contains(virt) || h.Track.Contains(virt)) { hit = h; return true; }
                }
            }
            return false;
        }

        private string HitTestLink(Point clientPt)
        {
            // Fast path: use recorded link rectangles captured at draw time
            Point virt = new Point(clientPt.X, clientPt.Y + _scrollOffset);
            foreach (var it in _items)
            {
                if (it.LinkHits == null || it.LinkHits.Count == 0) continue;
                // Only consider items whose bubble intersects the point for quick rejection
                if (!it.Bounds.Contains(virt)) continue;
                for (int i = 0; i < it.LinkHits.Count; i++)
                {
                    var lh = it.LinkHits[i];
                    if (lh.Rect.Contains(virt)) return lh.Url;
                }
            }
            return null;
        }

        private int IndexOfMessageItem(MessageItem it)
        {
            try
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (object.ReferenceEquals(_items[i], it)) return i;
                }
            }
            catch { }
            return -1;
        }

        // Set the KryptonScrollBar's Value to a scroll offset, clamped to its [Minimum, Maximum] range,
        // and force a repaint. KryptonScrollBar's Value ranges over [Minimum, Maximum] (the thumb hits the
        // bottom at Value == Maximum, unlike a WinForms ScrollBar), and a programmatic Value change may not
        // repaint the thumb on its own - so Invalidate here keeps the thumb in sync with wheel/programmatic
        // scrolling.
        private void SyncVBarValue(int offset)
        {
            // KryptonScrollBar doesn't move its thumb on a programmatic Value change; the bridge writes the
            // backing value and forces the thumb reposition so wheel/programmatic scrolls stay in sync.
            KryptonThemeBridge.SetScrollBarValue(_vbar, offset);
        }

        private void ScrollToBottom()
        {
            int view = Math.Max(0, ClientSize.Height);
            int max = Math.Max(0, _contentHeight - view);
            _scrollOffset = max;
            if (_vbar.Enabled) SyncVBarValue(max);
        }

        // Public helper to jump to the top of the transcript (used for help templates)
        public void ScrollToTop()
        {
            _scrollOffset = 0;
            if (_vbar.Enabled) SyncVBarValue(0);
            Invalidate();
        }

        private void UpdateScrollbar()
        {
            int view = Math.Max(0, ClientSize.Height);
            int maxScrollOffset = Math.Max(0, _contentHeight - view);

            // If view is 0 (control not properly sized yet), disable scrollbar
            if (view <= 0)
            {
                _vbar.Enabled = false;
                _vbar.Minimum = 0;
                _vbar.Maximum = 0;
                SyncVBarValue(0);
                _scrollOffset = 0;
                return;
            }

            _vbar.Minimum = 0;
            // KryptonScrollBar's Value ranges over [Minimum, Maximum] (thumb bottom at Value == Maximum),
            // so Maximum is the maximum scroll offset itself - NOT contentHeight-1.
            _vbar.Maximum = maxScrollOffset;
            // Its thumb size is largeChange/Maximum of the track. Use the proportional page size
            // (maxScroll * viewport / content) so the thumb reflects the visible fraction and never fills
            // the whole track (which would push it under the arrow buttons). SmallChange must be
            // <= LargeChange, which can be small when the content only just overflows.
            int page = (_contentHeight > 0)
                ? (int)Math.Round((double)maxScrollOffset * view / _contentHeight)
                : view;
            page = Math.Max(2, Math.Min(Math.Max(2, maxScrollOffset), page));
            _vbar.SmallChange = 1;
            _vbar.LargeChange = page;

            _vbar.Enabled = maxScrollOffset > 0;

            // Stick to bottom during streaming
            if (_stickToBottom && _vbar.Enabled)
            {
                _scrollOffset = maxScrollOffset;
                SyncVBarValue(maxScrollOffset);
                return;
            }

            // Clamp our logical scroll offset to the scrollable range and mirror it onto the bar.
            _scrollOffset = Math.Max(0, Math.Min(maxScrollOffset, _scrollOffset));
            SyncVBarValue(_scrollOffset);
        }

        // Post a deferred reflow/scrollbar refresh so measurements use the final viewport size
        private void ReflowSoon()
        {
            if (!IsHandleCreated) return;
            if (_reflowQueued) return;
            _reflowQueued = true;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    _reflowQueued = false;
                    Reflow();
                    Invalidate();
                });
            }
            catch { _reflowQueued = false; }
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool StickToBottomDuringStreaming
        {
            get { return _stickToBottom; }
            set
            {
                if (_stickToBottom == value) return;
                _stickToBottom = value;
                UpdateScrollbar();
                Invalidate();
            }
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var gp = new System.Drawing.Drawing2D.GraphicsPath();
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        private static void SafeClipboardSetText(string s)
        {
            try
            {
                if (s == null) s = string.Empty;
                // Normalize line endings to CRLF for Windows clipboard
                string normalized = s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
                Clipboard.SetText(normalized);
            }
            catch { /* clipboard busy; ignore */ }
        }

        // --------- Helpers for code block UI hit testing ---------
        private enum CodeUiHit { None, CopyButton, ScrollThumb, ScrollTrack, Text, GraphImage }
        private struct CodeUiInfo
        {
            public bool Hit;
            public CodeUiHit Which;
            public MessageItem Item;
            public Block Block;
            public int CodeIndex;
            public Rectangle ScrollTrackRect;
            public int ContentWidth;
            public int ViewportWidth;
            public bool IsTable; // true when referring to a table scrollbar
            public int TableIndex; // when IsTable
        }

        private CodeUiInfo HitTestCodeUI(Point clientPt)
        {
            var info = new CodeUiInfo { Hit = false, Which = CodeUiHit.None };
            Point virt = new Point(clientPt.X, clientPt.Y + _scrollOffset);

            // Find containing message
            foreach (var it in _items)
            {
                if (!it.Bounds.Contains(virt)) continue;
                int contentX = it.Bounds.X + BubblePadding;
                int contentY = it.Bounds.Y + BubblePadding;
                int contentW = it.Bounds.Width - 2 * BubblePadding;
                int y = contentY;
                int codeIdx = 0;
                using (Graphics g = CreateGraphics())
                {
                    foreach (var blk in it.Blocks)
                    {
                        if (blk.Type == BlockType.Heading)
                        {
                            var h = (HeadingBlock)blk;
                            int used = MeasureInlineParagraphHeight(g, contentW, h.Inlines, GetHeadingFont(h.Level));
                            // Copy button not here; just spacing like DrawBlocks
                            y += used + 4;
                        }
                        else if (blk.Type == BlockType.Paragraph)
                        {
                            var p = (ParagraphBlock)blk;
                            int used = MeasureInlineParagraphHeight(g, contentW, p.Inlines, _baseFont);
                            y += used + 2;
                        }
                        else if (blk.Type == BlockType.BulletList)
                        {
                            var list = (BulletListBlock)blk;
                            foreach (var item in list.Items)
                            {
                                int indentX = contentX + (item.IndentLevel * BulletIndent);
                                int textX = indentX + BulletIndent;
                                int used = MeasureInlineParagraphHeight(g, contentW - (textX - contentX), item.Content, _baseFont);
                                y += Math.Max(used, _baseFont.Height) + 2;
                            }
                        }
                        else if (blk.Type == BlockType.NumberedList)
                        {
                            var list = (NumberedListBlock)blk;
                            // Maintain numbering counters only when items lack explicit numbers
                            var counters = new Dictionary<int, int>();
                            foreach (var item in list.Items)
                            {
                                int indent = item.IndentLevel;
                                if (!item.Number.HasValue)
                                {
                                    if (counters.Count > 0)
                                    {
                                        var toRemove = new List<int>();
                                        foreach (var k in counters.Keys) if (k > indent) toRemove.Add(k);
                                        for (int r = 0; r < toRemove.Count; r++) counters.Remove(toRemove[r]);
                                    }
                                }
                                int prev; if (!counters.TryGetValue(indent, out prev)) prev = 0;
                                int itemNumber = item.Number.HasValue ? item.Number.Value : (prev + 1);
                                if (!item.Number.HasValue) counters[indent] = itemNumber;
                                char delim = item.NumberDelimiter != '\0' ? item.NumberDelimiter : '.';
                                string numberText = itemNumber.ToString() + delim;
                                int indentX = contentX + (item.IndentLevel * BulletIndent);
                                Size numberSize = TextRenderer.MeasureText(numberText, _baseFont);
                                int textX = indentX + numberSize.Width + 4;
                                int used = MeasureInlineParagraphHeight(g, contentW - (textX - contentX), item.Content, _baseFont);
                                y += Math.Max(used, _baseFont.Height) + 2;
                            }
                        }
                        else if (blk.Type == BlockType.Divider)
                        {
                            y += DividerHeight;
                        }
                        else if (blk.Type == BlockType.CodeBlock)
                        {
                            var cb = (CodeBlock)blk;
                            // Dot blocks: only the Copy button is interactive (no horizontal scroll).
                            // Mirror the graph box geometry so the copy hit-rect matches DrawGraphBlock.
                            if (IsGraphLanguage(cb.Language))
                            {
                                GraphBlockLayout gl = ComputeGraphLayout(g, cb, contentW);
                                if (!gl.Failed)
                                {
                                    Rectangle gbox = new Rectangle(contentX, y, gl.BoxW, gl.BoxH);
                                    SizeF gCopySizeF = g.MeasureString("Copy", _baseFont, PointF.Empty, StringFormat.GenericTypographic);
                                    int gCopyW = (int)Math.Ceiling(gCopySizeF.Width) + CodeCopyButtonPad * 2;
                                    Rectangle gCopyRect = new Rectangle(gbox.Right - CodeCopyButtonPad - gCopyW, gbox.Top, gCopyW, gl.HeaderH);
                                    if (gCopyRect.Contains(virt))
                                    {
                                        info.Hit = true; info.Which = CodeUiHit.CopyButton; info.Item = it; info.Block = blk; info.CodeIndex = codeIdx; return info;
                                    }
                                    // The image body (below the header) opens the pan/zoom viewer - only once rendered.
                                    if (gl.Ready && gl.Image != null)
                                    {
                                        Rectangle gBody = new Rectangle(gbox.X, gbox.Top + gl.HeaderH, gbox.Width, gbox.Height - gl.HeaderH);
                                        if (gBody.Contains(virt))
                                        {
                                            info.Hit = true; info.Which = CodeUiHit.GraphImage; info.Item = it; info.Block = blk; info.CodeIndex = codeIdx; return info;
                                        }
                                    }
                                    y += gbox.Height + 4;
                                    codeIdx++;
                                    continue;
                                }
                            }
                            var colored = SyntaxHighlightingRenderer.GetColoredSegments(cb.Text, cb.Language, _monoFont, _isDarkTheme);
                            Size content = SyntaxHighlightingRenderer.MeasureColoredSegmentsNoWrap(g, colored);
                            int viewportW = Math.Max(0, contentW - 2 * CodeBlockPadding);
                            bool needH = content.Width > viewportW;
                            int textH = Math.Max(_monoFont.Height, content.Height);
                            int headerH = GetCodeHeaderHeight();
                            int boxH = textH + 2 * CodeBlockPadding + headerH + (needH ? CodeHScrollHeight : 0);
                            Rectangle box = new Rectangle(contentX, y, GetCodeBoxWidth(g, content.Width, contentW, cb.Language), boxH);

                            // Copy button rect
                            SizeF copySizeF = g.MeasureString("Copy", _baseFont, PointF.Empty, StringFormat.GenericTypographic);
                            int copyW = (int)Math.Ceiling(copySizeF.Width) + CodeCopyButtonPad * 2;
                            int copyH = headerH;
                            int headerTop = box.Top;
                            Rectangle copyRect = new Rectangle(box.Right - CodeCopyButtonPad - copyW, headerTop, copyW, copyH);
                            if (copyRect.Contains(virt))
                            {
                                info.Hit = true; info.Which = CodeUiHit.CopyButton; info.Item = it; info.Block = blk; info.CodeIndex = codeIdx; return info;
                            }

                            // Scrollbar rects
                            Rectangle textRect = new Rectangle(box.X + CodeBlockPadding, headerTop + headerH, box.Width - 2 * CodeBlockPadding, textH);
                            // Report a generic Text hit when hovering over code content (for Shift+Wheel horizontal scroll)
                            if (textRect.Contains(virt))
                            {
                                info.Hit = true; info.Which = CodeUiHit.Text; info.Item = it; info.Block = blk; info.CodeIndex = codeIdx; info.ContentWidth = content.Width; info.ViewportWidth = textRect.Width; return info;
                            }
                            if (needH)
                            {
                                Rectangle track = new Rectangle(textRect.X, textRect.Bottom + 2, textRect.Width, CodeHScrollHeight - 4);
                                int maxScroll = Math.Max(0, content.Width - textRect.Width);
                                int thumbW = Math.Max(CodeHScrollThumbMin, (int)Math.Round((double)track.Width * textRect.Width / Math.Max(1, content.Width)));
                                int trackRange = Math.Max(1, track.Width - thumbW);
                                int scrollX = (codeIdx < it.CodeScroll.Count) ? it.CodeScroll[codeIdx] : 0;
                                int thumbX = track.X + (maxScroll > 0 ? (int)Math.Round((double)scrollX / maxScroll * trackRange) : 0);
                                Rectangle thumb = new Rectangle(thumbX, track.Y, thumbW, track.Height);
                                if (thumb.Contains(virt))
                                {
                                    info.Hit = true; info.Which = CodeUiHit.ScrollThumb; info.Item = it; info.Block = blk; info.CodeIndex = codeIdx; info.ScrollTrackRect = track; info.ContentWidth = content.Width; info.ViewportWidth = textRect.Width; return info;
                                }
                                if (track.Contains(virt))
                                {
                                    info.Hit = true; info.Which = CodeUiHit.ScrollTrack; info.Item = it; info.Block = blk; info.CodeIndex = codeIdx; info.ScrollTrackRect = track; info.ContentWidth = content.Width; info.ViewportWidth = textRect.Width; return info;
                                }
                            }

                            y += box.Height + 4;
                            codeIdx++;
                        }
                        else if (blk.Type == BlockType.Table)
                        {
                            var t = (TableBlock)blk;
                            int cols = Math.Max(0, t.Alignments != null ? t.Alignments.Count : 0);
                            if (cols > 0)
                            {
                                int cellPad = 6; int border = 1;
                                // measure intrinsic widths
                                int[] colWidths = new int[cols];
                                int headerH = 0;
                                for (int c = 0; c < cols; c++)
                                {
                                    var inl = (c < t.Header.Count) ? t.Header[c] : new List<InlineRun>();
                                    Size sz = MeasureInlineParagraph(g, inl, _baseFont, int.MaxValue / 4, false);
                                    colWidths[c] = Math.Max(colWidths[c], sz.Width);
                                    headerH = Math.Max(headerH, sz.Height);
                                }
                                int[] rowHeights = new int[t.Rows.Count];
                                for (int r = 0; r < t.Rows.Count; r++)
                                {
                                    int rowH = 0;
                                    for (int c = 0; c < cols; c++)
                                    {
                                        var inl = (c < t.Rows[r].Count) ? t.Rows[r][c] : new List<InlineRun>();
                                        Size sz = MeasureInlineParagraph(g, inl, _baseFont, int.MaxValue / 4, false);
                                        colWidths[c] = Math.Max(colWidths[c], sz.Width);
                                        rowH = Math.Max(rowH, sz.Height);
                                    }
                                    rowHeights[r] = rowH;
                                }
                                int intrinsicW = border; for (int c = 0; c < cols; c++) intrinsicW += colWidths[c] + cellPad * 2 + border;
                                int viewportW = Math.Max(0, contentW);
                                int tableH = 1 + headerH + cellPad * 2 + 1; for (int r = 0; r < rowHeights.Length; r++) tableH += rowHeights[r] + cellPad * 2 + 1;
                                bool needH = intrinsicW > viewportW;
                                if (needH)
                                {
                                    Rectangle track = new Rectangle(contentX, y + tableH + 2, Math.Min(contentW, intrinsicW), CodeHScrollHeight - 4);
                                    int tableIndex = 0; for (int bi = 0; bi < it.Blocks.Count && !object.ReferenceEquals(it.Blocks[bi], blk); bi++) if (it.Blocks[bi].Type == BlockType.Table) tableIndex++;
                                    if (it.TableScroll == null) it.TableScroll = new List<int>();
                                    while (it.TableScroll.Count <= tableIndex) it.TableScroll.Add(0);
                                    int scrollX = it.TableScroll[tableIndex];
                                    int maxScroll = Math.Max(0, intrinsicW - track.Width);
                                    int thumbW = Math.Max(CodeHScrollThumbMin, (int)Math.Round((double)track.Width * track.Width / Math.Max(1, intrinsicW)));
                                    int trackRange = Math.Max(1, track.Width - thumbW);
                                    int thumbX = track.X + (maxScroll > 0 ? (int)Math.Round((double)scrollX / maxScroll * trackRange) : 0);
                                    Rectangle thumb = new Rectangle(thumbX, track.Y, thumbW, track.Height);
                                    if (thumb.Contains(virt))
                                    { info.Hit = true; info.Which = CodeUiHit.ScrollThumb; info.Item = it; info.Block = blk; info.CodeIndex = -1; info.TableIndex = tableIndex; info.ScrollTrackRect = track; info.ContentWidth = intrinsicW; info.ViewportWidth = track.Width; info.IsTable = true; return info; }
                                    if (track.Contains(virt))
                                    { info.Hit = true; info.Which = CodeUiHit.ScrollTrack; info.Item = it; info.Block = blk; info.CodeIndex = -1; info.TableIndex = tableIndex; info.ScrollTrackRect = track; info.ContentWidth = intrinsicW; info.ViewportWidth = track.Width; info.IsTable = true; return info; }
                                }

                                // Also report content area as a generic Text hit for tables so Shift+Wheel can be used anywhere over the table
                                Rectangle tableRect = new Rectangle(contentX, y, Math.Min(contentW, intrinsicW), tableH);
                                if (tableRect.Contains(virt))
                                {
                                    int tableIndex2 = 0; for (int bi = 0; bi < it.Blocks.Count && !object.ReferenceEquals(it.Blocks[bi], blk); bi++) if (it.Blocks[bi].Type == BlockType.Table) tableIndex2++;
                                    info.Hit = true; info.Which = CodeUiHit.Text; info.Item = it; info.Block = blk; info.CodeIndex = -1; info.TableIndex = tableIndex2; info.ContentWidth = intrinsicW; info.ViewportWidth = Math.Min(contentW, intrinsicW); info.IsTable = true; return info;
                                }

                                y += tableH + (needH ? CodeHScrollHeight : 0) + 2;
                            }
                        }
                    }
                }
            }
            return info;
        }

        private MessageItem GetCurrentMessageFromY(int contentTopY)
        {
            // Helper: find message item whose content starts at contentTopY (approximate by bounds Y)
            foreach (var it in _items)
            {
                if (it.Bounds.Y + BubblePadding == contentTopY) return it;
            }
            return null;
        }

        // ---------- Link hit model ----------
        private struct LinkHit
        {
            public Rectangle Rect;
            public string Url;
        }

        // Drawn text segment for selection
        private struct DrawnSeg
        {
            public Rectangle Rect;   // virtual coords
            public string Text;      // text content
            public bool IsNewLine;   // marks a line break
            public bool IsHardBreak; // true for markdown breaks; false for wrap
            public Font Font;        // font used for measuring
            // Markdown-aware line context
            public bool IsHeadingLine;
            public int HeadingLevel;
            public string LinePrefix;
            public int LineFirstTextLeft;
            public bool IsLogicalLineStart; // true for first run on a logical line (not wrap continuation)
            // Table cell metadata
            public bool IsTableCell;
            public int TableIndex;
            public bool TableIsHeader;
            public int TableRowIndex;
            public int TableColIndex;
            public int TableColumnCount;
            public TableAlign TableAlignment; // alignment for this column (from parser)
            // Code block metadata (when IsCodeLine=true)
            public bool IsCodeLine;
            public int CodeBlockIndex;
            public int CodeLineIndex;
            public int CodeLineCount;
            public string CodeLanguage;
            // Link metadata
            public bool IsLink;
            public string LinkUrl;
            public int LinkRunId;
        }

        // 50% snapping support: maps a pixel span to character-boundary indices and pixel offsets within a run
        private struct CharRangeSnapResult
        {
            public int StartIndex; // inclusive
            public int EndIndex;   // exclusive
            public int StartPx;    // pixels from runRect.Left
            public int EndPx;      // pixels from runRect.Left
        }

        private static CharRangeSnapResult SnapToCharRange(Graphics g, StringFormat fmt, string text, Font font, Rectangle runRect, int leftPxAbs, int rightPxAbs)
        {
            CharRangeSnapResult res = new CharRangeSnapResult { StartIndex = 0, EndIndex = 0, StartPx = 0, EndPx = 0 };
            if (string.IsNullOrEmpty(text) || font == null) return res;

            // Clamp absolute L/R to run bounds
            int absL = (leftPxAbs == int.MinValue) ? runRect.Left : leftPxAbs;
            int absR = (rightPxAbs == int.MaxValue) ? runRect.Right : rightPxAbs;
            int pxL = Math.Max(runRect.Left, Math.Min(absL, runRect.Right));
            int pxR = Math.Max(runRect.Left, Math.Min(absR, runRect.Right));
            if (pxR <= pxL) return res;

            int relL = pxL - runRect.Left;
            int relR = pxR - runRect.Left;

            int len = text.Length;
            float[] cum = new float[len + 1];
            cum[0] = 0f;
            for (int i = 1; i <= len; i++)
            {
                string sub = text.Substring(0, i);
                SizeF sz = g.MeasureString(sub.Length == 0 ? " " : sub, font, PointF.Empty, fmt);
                cum[i] = sz.Width;
            }

            float totalW = cum[len];

            int startIdx;
            if (relL <= 0) startIdx = 0;
            else if (relL >= totalW) startIdx = len;
            else
            {
                int i = 0; while (i < len && cum[i + 1] <= relL) i++;
                float mid = (cum[i] + cum[i + 1]) * 0.5f;
                startIdx = (relL >= mid) ? (i + 1) : i;
            }

            int endIdx;
            if (relR <= 0) endIdx = 0;
            else if (relR >= totalW) endIdx = len;
            else
            {
                int i = 0; while (i < len && cum[i + 1] <= relR) i++;
                float mid = (cum[i] + cum[i + 1]) * 0.5f;
                endIdx = (relR > mid) ? (i + 1) : i;
            }

            if (endIdx < startIdx) endIdx = startIdx;
            int startPx = (int)Math.Round(cum[startIdx]);
            int endPx = (int)Math.Round(cum[endIdx]);

            res.StartIndex = startIdx;
            res.EndIndex = endIdx;
            res.StartPx = startPx;
            res.EndPx = endPx;
            return res;
        }

        // Extract selected text from the current message item
        private string ExtractSelectedTextFromItem(MessageItem it)
        {
            if (!_hasSelection || it == null || it.DrawnSegments == null || it.DrawnSegments.Count == 0) return string.Empty;
            Point anchor = _selStartVirt; Point caret = _selEndVirt;
            int yTop = Math.Min(anchor.Y, caret.Y);
            int yBot = Math.Max(anchor.Y, caret.Y);
            int anchorX = anchor.X;
            int caretX = caret.X;

            Rectangle startLine = GetLineBoundsAtY(it, anchor.Y);
            Rectangle endLine = GetLineBoundsAtY(it, caret.Y);
            bool sameLine = (!startLine.IsEmpty && !endLine.IsEmpty && startLine.Top == endLine.Top && startLine.Bottom == endLine.Bottom);
            bool directionDown = caret.Y > anchor.Y; // true when selecting top->bottom

            var sb = new System.Text.StringBuilder();
            int currentLineTop = int.MinValue;
            bool wroteOnThisLine = false;
            bool emittedPrefixForLine = false;
            int currentLineFirstLeft = int.MaxValue;
            bool currentLineIsHeading = false;
            int currentLineHeadingLevel = 0;
            string currentLinePrefix = null;

            // First pass: determine selection coverage across code blocks and whether non-code is included
            // Also detect selected table cells grouped by table index/row/col
            bool containsNonCode = false;
            var codeInfo = new Dictionary<int, CodeSel>();
            var tableTouched = new Dictionary<int, TableSel>();
            foreach (var ds0 in it.DrawnSegments)
            {
                if (ds0.IsNewLine) continue;
                Rectangle r0 = ds0.Rect;
                if (r0.Height <= 0 || r0.Width <= 0) continue;
                if (r0.Bottom <= yTop || r0.Top >= yBot) continue;
                int left0 = int.MinValue; int right0 = int.MaxValue;
                bool isStartLineRun = LinesOverlap(r0, GetLineBoundsAtY(it, anchor.Y));
                bool isEndLineRun = LinesOverlap(r0, GetLineBoundsAtY(it, caret.Y));
                if (isStartLineRun)
                {
                    if (caret.Y > anchor.Y) left0 = anchorX; else right0 = anchorX;
                }
                if (isEndLineRun)
                {
                    if (caret.Y > anchor.Y) right0 = caretX; else left0 = caretX;
                }
                using (Graphics g0 = CreateGraphics())
                using (var fmt0 = StringFormat.GenericTypographic)
                {
                    fmt0.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                    var snap0 = SnapToCharRange(g0, fmt0, ds0.Text ?? string.Empty, ds0.Font ?? this.Font, r0, left0, right0);
                    int len0 = ds0.Text != null ? ds0.Text.Length : 0;
                    bool anyHit = (snap0.EndIndex > snap0.StartIndex) || (len0 == 0);
                    if (anyHit)
                    {
                        if (ds0.IsCodeLine)
                        {
                            CodeSel ci;
                            if (!codeInfo.TryGetValue(ds0.CodeBlockIndex, out ci))
                            {
                                ci = new CodeSel(ds0.CodeLineCount, ds0.CodeLanguage);
                                codeInfo[ds0.CodeBlockIndex] = ci;
                            }
                            // Mark coverage for this line
                            ci.Any[ds0.CodeLineIndex] = true;
                            bool full;
                            if (len0 == 0)
                            {
                                full = true; // empty lines count as fully selected when intersected
                            }
                            else if (isStartLineRun && isEndLineRun)
                            {
                                full = (snap0.StartIndex == 0) && (snap0.EndIndex >= len0);
                            }
                            else if (isStartLineRun)
                            {
                                full = (snap0.StartIndex == 0);
                            }
                            else if (isEndLineRun)
                            {
                                full = (snap0.EndIndex >= len0);
                            }
                            else
                            {
                                full = true; // interior lines are fully selected
                            }
                            ci.Full[ds0.CodeLineIndex] = ci.Full[ds0.CodeLineIndex] || full;
                        }
                        else if (ds0.IsTableCell)
                        {
                            TableSel ts;
                            if (!tableTouched.TryGetValue(ds0.TableIndex, out ts)) { ts = new TableSel(ds0.TableColumnCount); tableTouched[ds0.TableIndex] = ts; }
                            if (ds0.TableIsHeader)
                            {
                                ts.HeaderCols[ds0.TableColIndex] = true;
                            }
                            else
                            {
                                HashSet<int> cols;
                                if (!ts.Rows.TryGetValue(ds0.TableRowIndex, out cols)) { cols = new HashSet<int>(); ts.Rows[ds0.TableRowIndex] = cols; }
                                cols.Add(ds0.TableColIndex);
                            }
                        }
                        else
                        {
                            containsNonCode = true;
                        }
                    }
                }
            }

            bool hasCode = codeInfo.Count > 0;
            bool multiCodeBlocks = codeInfo.Count > 1;
            bool includeFences = hasCode && (containsNonCode || multiCodeBlocks);
            var fenceOpened = new System.Collections.Generic.HashSet<int>();
            var fenceCanClose = new System.Collections.Generic.HashSet<int>();
            foreach (var kv in codeInfo)
            {
                int idx = kv.Key; var ci = kv.Value;
                bool allLinesSelected = true;
                for (int li = 0; li < ci.LineCount; li++)
                {
                    if (!ci.Any[li] || !ci.Full[li]) { allLinesSelected = false; break; }
                }
                if (allLinesSelected) fenceCanClose.Add(idx);
            }

            using (Graphics g = CreateGraphics())
            using (var fmt = StringFormat.GenericTypographic)
            {
                fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                var emittedLinkRuns = new System.Collections.Generic.HashSet<int>();
                var emittedTables = new System.Collections.Generic.HashSet<int>();
                foreach (var ds in it.DrawnSegments)
                {
                    if (ds.IsNewLine)
                    {
                        if (wroteOnThisLine && ds.IsHardBreak)
                        {
                            sb.Append("\n");
                        }
                        wroteOnThisLine = false;
                        emittedPrefixForLine = false;
                        continue;
                    }
                    Rectangle r = ds.Rect;
                    if (r.Height <= 0 || r.Width <= 0) continue;
                    if (r.Bottom <= yTop || r.Top >= yBot) continue;

                    // If we intersect a table and haven't emitted it yet, output the table markdown now.
                    if (ds.IsTableCell && tableTouched.ContainsKey(ds.TableIndex) && !emittedTables.Contains(ds.TableIndex))
                    {
                        // Build table markdown: header | header cells; separator; then rows that have any selected columns
                        // We don't have direct access to parsed TableBlock here, so reconstruct from DrawnSegments text per cell.
                        // Gather header row text per column
                        var ts = tableTouched[ds.TableIndex];
                        string[] header = new string[Math.Max(1, ds.TableColumnCount)];
                        for (int col = 0; col < header.Length; col++) header[col] = string.Empty;
                        // Accumulate cell text by scanning all drawn segments for this table header col
                        foreach (var d2 in it.DrawnSegments)
                        {
                            if (!d2.IsNewLine && d2.IsTableCell && d2.TableIndex == ds.TableIndex && d2.TableIsHeader && !string.IsNullOrEmpty(d2.Text))
                            {
                                int cix = d2.TableColIndex;
                                header[cix] += d2.Text;
                            }
                        }
                        // Emit a newline before a table if current builder has non-newline tail
                        if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                        sb.Append("|");
                        for (int c = 0; c < header.Length; c++)
                        {
                            if (c > 0) sb.Append("|");
                            sb.Append(" ");
                            sb.Append(header[c].Trim());
                            sb.Append(" ");
                        }
                        sb.Append("|");
                        sb.Append('\n');
                        // Separator row: use alignment markers per column from segments (Left: --- , Center: :---:, Right: ---:)
                        // First, collect an alignment per column by scanning any segment in that column (alignment is consistent per column)
                        TableAlign[] aligns = new TableAlign[header.Length];
                        for (int c = 0; c < aligns.Length; c++) aligns[c] = TableAlign.Left;
                        foreach (var d2 in it.DrawnSegments)
                        {
                            if (d2.IsNewLine) continue;
                            if (!d2.IsTableCell || d2.TableIndex != ds.TableIndex) continue;
                            if (d2.TableColIndex < 0 || d2.TableColIndex >= aligns.Length) continue;
                            aligns[d2.TableColIndex] = d2.TableAlignment;
                        }
                        sb.Append("|");
                        for (int c = 0; c < header.Length; c++)
                        {
                            if (c > 0) sb.Append("|");
                            switch (aligns[c])
                            {
                                case TableAlign.Center:
                                    sb.Append(" :---: ");
                                    break;
                                case TableAlign.Right:
                                    sb.Append(" ---: ");
                                    break;
                                default:
                                    sb.Append(" --- ");
                                    break;
                            }
                        }
                        sb.Append("|");
                        sb.Append('\n');
                        // Body rows: determine min and max row index from touched rows; emit only rows that intersect selection
                        var rowIndices = new List<int>(ts.Rows.Keys);
                        rowIndices.Sort();
                        for (int irow = 0; irow < rowIndices.Count; irow++)
                        {
                            int row = rowIndices[irow];
                            sb.Append("|");
                            for (int c = 0; c < header.Length; c++)
                            {
                                if (c > 0) sb.Append("|");
                                // Append text for this cell by concatenating all DrawnSegments in that cell
                                var cellSb = new System.Text.StringBuilder();
                                foreach (var d2 in it.DrawnSegments)
                                {
                                    if (!d2.IsNewLine && d2.IsTableCell && d2.TableIndex == ds.TableIndex && !d2.TableIsHeader && d2.TableRowIndex == row && d2.TableColIndex == c && !string.IsNullOrEmpty(d2.Text))
                                    {
                                        cellSb.Append(d2.Text);
                                    }
                                }
                                sb.Append(" ");
                                sb.Append(cellSb.ToString().Trim());
                                sb.Append(" ");
                            }
                            sb.Append("|");
                            sb.Append('\n');
                        }
                        emittedTables.Add(ds.TableIndex);
                        wroteOnThisLine = false;
                        emittedPrefixForLine = false;
                        // Skip normal emission for this ds (we already output table); continue to next segment
                        continue;
                    }

                    // If we've already emitted this table, skip its individual cell segments
                    if (ds.IsTableCell && emittedTables.Contains(ds.TableIndex))
                    {
                        continue;
                    }

                    if (r.Top != currentLineTop)
                    {
                        // New visual line encountered; don't emit newline here to avoid wrap breaks
                        currentLineTop = r.Top;
                        wroteOnThisLine = false;
                        emittedPrefixForLine = false;
                        currentLineFirstLeft = (ds.LineFirstTextLeft > 0) ? ds.LineFirstTextLeft : r.Left;
                        currentLineIsHeading = ds.IsHeadingLine;
                        currentLineHeadingLevel = ds.HeadingLevel;
                        currentLinePrefix = ds.LinePrefix;
                    }

                    int left = int.MinValue; int right = int.MaxValue;
                    if (sameLine)
                    {
                        left = Math.Min(anchorX, caretX);
                        right = Math.Max(anchorX, caretX);
                    }
                    else
                    {
                        if (LinesOverlap(r, startLine))
                        {
                            if (directionDown) left = anchorX; else right = anchorX;
                        }
                        if (LinesOverlap(r, endLine))
                        {
                            if (directionDown) right = caretX; else left = caretX;
                        }
                    }

                    var snap = SnapToCharRange(g, fmt, ds.Text ?? string.Empty, ds.Font ?? this.Font, r, left, right);
                    if (snap.EndIndex > snap.StartIndex)
                    {
                        // If a link is selected, emit full markdown once and skip its fragments thereafter
                        if (ds.IsLink && ds.LinkRunId >= 0 && !emittedLinkRuns.Contains(ds.LinkRunId))
                        {
                            // Build full label for this link run
                            var labelSb = new System.Text.StringBuilder();
                            for (int i = 0; i < it.DrawnSegments.Count; i++)
                            {
                                var d2 = it.DrawnSegments[i];
                                if (d2.IsNewLine) continue;
                                if (d2.IsLink && d2.LinkRunId == ds.LinkRunId && !string.IsNullOrEmpty(d2.Text))
                                    labelSb.Append(d2.Text);
                            }
                            string label = labelSb.ToString();
                            string url = ds.LinkUrl ?? string.Empty;
                            if (!string.IsNullOrEmpty(label))
                            {
                                if (!emittedPrefixForLine)
                                {
                                    int effectiveLeftPx;
                                    if (left == int.MinValue) effectiveLeftPx = r.Left; else effectiveLeftPx = Math.Max(r.Left, Math.Min(left, r.Right));
                                    if (ds.IsLogicalLineStart && effectiveLeftPx <= currentLineFirstLeft)
                                    {
                                        if (currentLineIsHeading && currentLineHeadingLevel > 0)
                                        { sb.Append(new string('#', Math.Min(6, Math.Max(1, currentLineHeadingLevel)))); sb.Append(' '); }
                                        if (!string.IsNullOrEmpty(currentLinePrefix)) sb.Append(currentLinePrefix);
                                        emittedPrefixForLine = true;
                                    }
                                }
                                string trimmedLabel = label.Trim();
                                string trimmedUrl = (url ?? string.Empty).Trim();
                                if (string.Equals(trimmedLabel, trimmedUrl, StringComparison.Ordinal))
                                {
                                    // Autolink: label equals URL, emit URL only
                                    sb.Append(trimmedUrl);
                                }
                                else
                                {
                                    // Markdown link with label
                                    sb.Append('[').Append(label).Append(']').Append('(').Append(url).Append(')');
                                }
                                wroteOnThisLine = true;
                                emittedLinkRuns.Add(ds.LinkRunId);
                                continue; // skip normal fragment emit
                            }
                        }
                        // Code fences open/close handling
                        if (ds.IsCodeLine && includeFences && !fenceOpened.Contains(ds.CodeBlockIndex))
                        {
                            // Start a fence for this code block
                            if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                            sb.Append("```");
                            if (!string.IsNullOrEmpty(ds.CodeLanguage)) { sb.Append(ds.CodeLanguage); }
                            sb.Append('\n');
                            fenceOpened.Add(ds.CodeBlockIndex);
                        }
                        else if (!ds.IsCodeLine)
                        {
                            // If leaving a fenced code block that was fully selected, close it before non-code text
                            // Find any opened block that can close; close the last opened one to maintain order.
                            int toClose = -1;
                            foreach (int bidx in fenceOpened) { if (fenceCanClose.Contains(bidx)) toClose = bidx; }
                            if (toClose >= 0)
                            {
                                if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                                sb.Append("```");
                                sb.Append('\n');
                                fenceOpened.Remove(toClose);
                            }
                        }

                        if (!emittedPrefixForLine)
                        {
                            int effectiveLeftPx;
                            if (left == int.MinValue)
                                effectiveLeftPx = r.Left; // middle lines => treat as from line start
                            else
                                effectiveLeftPx = Math.Max(r.Left, Math.Min(left, r.Right));
                            if (ds.IsLogicalLineStart && effectiveLeftPx <= currentLineFirstLeft)
                            {
                                if (currentLineIsHeading && currentLineHeadingLevel > 0)
                                {
                                    sb.Append(new string('#', Math.Min(6, Math.Max(1, currentLineHeadingLevel))));
                                    sb.Append(' ');
                                }
                                if (!string.IsNullOrEmpty(currentLinePrefix)) sb.Append(currentLinePrefix);
                                emittedPrefixForLine = true;
                            }
                        }
                        string part = (ds.Text ?? string.Empty).Substring(snap.StartIndex, snap.EndIndex - snap.StartIndex);
                        if (part.Length > 0)
                        {
                            sb.Append(part);
                            wroteOnThisLine = true;
                        }
                    }
                    else
                    {
                        // If this is a link run already emitted, skip its other fragments entirely
                        if (ds.IsLink && ds.LinkRunId >= 0 && emittedLinkRuns.Contains(ds.LinkRunId))
                        {
                            continue;
                        }
                    }
                }

                // Close any remaining opened fences that represent fully selected blocks
                // Close all remaining opened fences that are eligible to close
                foreach (int bidx in fenceOpened)
                {
                    if (fenceCanClose.Contains(bidx))
                    {
                        if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                        sb.Append("```");
                    }
                }
            }

            // Trim optional trailing newline
            string result = sb.ToString();
            if (result.EndsWith("\n")) result = result.TrimEnd('\n');
            return result;
        }

        private struct TableSel
        {
            public readonly int Cols;
            public readonly bool[] HeaderCols;
            public readonly Dictionary<int, HashSet<int>> Rows; // row -> selected columns
            public TableSel(int cols)
            {
                Cols = Math.Max(1, cols);
                HeaderCols = new bool[Cols];
                Rows = new Dictionary<int, HashSet<int>>();
            }
        }

        private struct CodeSel
        {
            public readonly int LineCount;
            public readonly string Lang;
            public readonly bool[] Any;
            public readonly bool[] Full;
            public CodeSel(int lineCount, string lang)
            {
                LineCount = Math.Max(0, lineCount);
                Lang = lang;
                Any = new bool[Math.Max(1, lineCount)];
                Full = new bool[Math.Max(1, lineCount)];
            }
        }

        private static string SubstringByPixelRange(Graphics g, StringFormat fmt, string text, Font font, int pxStart, int pxEnd)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (pxEnd <= 0) return string.Empty;
            if (pxStart <= 0 && pxEnd == int.MaxValue) return text;

            // Find start index
            int start = 0, end = text.Length;
            if (pxStart > 0)
            {
                int lo = 0, hi = text.Length;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    string sub = text.Substring(0, mid);
                    SizeF sz = g.MeasureString(sub.Length == 0 ? " " : sub, font, PointF.Empty, fmt);
                    if (sz.Width < pxStart) lo = mid + 1; else hi = mid;
                }
                start = Math.Min(lo, text.Length);
            }

            // Find end index
            if (pxEnd != int.MaxValue)
            {
                int lo = start, hi = text.Length;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    string sub = text.Substring(0, mid);
                    SizeF sz = g.MeasureString(sub.Length == 0 ? " " : sub, font, PointF.Empty, fmt);
                    if (sz.Width <= pxEnd) lo = mid; else hi = mid - 1;
                }
                end = Math.Max(lo, start);
            }
            return (end > start) ? text.Substring(start, end - start) : string.Empty;
        }
    }
}

