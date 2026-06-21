// DiffPreviewPanel.cs
// Owner-drawn, auto-scrolling panel that renders a syntax-highlighted body (any registered language)
// with an optional header line. Used by the tool approval prompt to show a files__edit diff or a
// command__run command line instead of raw JSON. AutoScroll handles both tall and wide content.
// Target: .NET 3.5, Windows XP compatible.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    internal sealed class DiffPreviewPanel : Panel
    {
        private string _header = string.Empty;
        private string _body = string.Empty;
        private string _language = "diff";
        private bool _dark;
        private Font _monoFont;
        private Color _codeBack = Color.FromArgb(245, 245, 245);
        private Color _foreColor = SystemColors.WindowText;

        private const int Pad = 6;

        public DiffPreviewPanel()
        {
            this.AutoScroll = true;
            this.BorderStyle = BorderStyle.None;
            this.BackColor = _codeBack;
            try { this.DoubleBuffered = true; }
            catch { }
        }

        // Set the content to display. monoFont is owned by the caller; colors come from the active
        // theme. An empty header omits the header line. language is any registered highlighter id
        // (e.g. "diff" for an edit, "batch" for a command).
        public void SetContent(string header, string body, string language, bool dark, Font monoFont, Color codeBack, Color foreColor)
        {
            _header = header ?? string.Empty;
            _body = body ?? string.Empty;
            _language = string.IsNullOrEmpty(language) ? "diff" : language;
            _dark = dark;
            _monoFont = monoFont;
            _codeBack = codeBack;
            _foreColor = foreColor;
            this.BackColor = codeBack;
            UpdateScrollSize();
            this.AutoScrollPosition = new Point(0, 0);
            Invalidate();
        }

        // Re-color the already-set content for a light<->dark switch without rebuilding it: keep the
        // current header/body/language and just swap the palette, re-measure, and repaint.
        public void ReapplyTheme(bool dark, Color codeBack, Color foreColor)
        {
            _dark = dark;
            _codeBack = codeBack;
            _foreColor = foreColor;
            this.BackColor = codeBack;
            UpdateScrollSize();
            Invalidate();
        }

        private int HeaderHeight
        {
            get { return string.IsNullOrEmpty(_header) ? 0 : (this.Font != null ? this.Font.Height : 14) + Pad; }
        }

        private void UpdateScrollSize()
        {
            if (_monoFont == null || string.IsNullOrEmpty(_body)) { this.AutoScrollMinSize = Size.Empty; return; }
            try
            {
                using (Graphics g = CreateGraphics())
                {
                    var colored = SyntaxHighlightingRenderer.GetColoredSegments(_body, _language, _monoFont, _dark);
                    Size content = SyntaxHighlightingRenderer.MeasureColoredSegmentsNoWrap(g, colored);
                    int w = content.Width + 2 * Pad;
                    int h = HeaderHeight + Math.Max(_monoFont.Height, content.Height) + Pad;
                    this.AutoScrollMinSize = new Size(w, h);
                }
            }
            catch { this.AutoScrollMinSize = Size.Empty; }
        }

        // Natural pixel height of the current content (optional header + body + padding), measured
        // independently of the control's own bounds so the host can size the approval panel to fit.
        // Uses an offscreen Graphics so it is valid even before the panel is shown. When the content
        // is wider than availableWidth (>0), it will get a horizontal scrollbar that eats vertical
        // space, so room for that scrollbar is included — otherwise a single wide line (e.g. a long
        // command) gets clipped behind the scrollbar. Pass availableWidth <= 0 to skip that.
        public int GetPreferredContentHeight(int availableWidth)
        {
            int oneLine = (_monoFont != null ? _monoFont.Height : 14);
            if (_monoFont == null || string.IsNullOrEmpty(_body)) return HeaderHeight + oneLine + Pad;
            try
            {
                using (var bmp = new Bitmap(1, 1))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    var colored = SyntaxHighlightingRenderer.GetColoredSegments(_body, _language, _monoFont, _dark);
                    Size content = SyntaxHighlightingRenderer.MeasureColoredSegmentsNoWrap(g, colored);
                    int h = HeaderHeight + Math.Max(_monoFont.Height, content.Height) + Pad;
                    if (availableWidth > 0 && content.Width + 2 * Pad > availableWidth)
                        h += SystemInformation.HorizontalScrollBarHeight;
                    return h;
                }
            }
            catch { return HeaderHeight + oneLine + Pad; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            using (var bg = new SolidBrush(_codeBack))
                g.FillRectangle(bg, this.ClientRectangle);
            if (_monoFont == null) return;

            // Draw everything in content coordinates; AutoScroll moves the world for both axes.
            g.TranslateTransform(this.AutoScrollPosition.X, this.AutoScrollPosition.Y);

            // Optional header line
            if (!string.IsNullOrEmpty(_header))
            {
                Font hf = this.Font ?? _monoFont;
                using (var br = new SolidBrush(_foreColor))
                    g.DrawString(_header, hf, br, new PointF(Pad, Pad / 2f));
            }

            if (string.IsNullOrEmpty(_body)) return;

            // Highlighted body
            SyntaxHighlightingRenderer.EnqueueHighlight(_language, _dark, _body, _monoFont);
            var colored = SyntaxHighlightingRenderer.GetColoredSegments(_body, _language, _monoFont, _dark);
            Size content = SyntaxHighlightingRenderer.MeasureColoredSegmentsNoWrap(g, colored);
            int bodyH = Math.Max(_monoFont.Height, content.Height);
            int bodyW = Math.Max(content.Width, this.ClientSize.Width - 2 * Pad);
            Rectangle textRect = new Rectangle(Pad, HeaderHeight, bodyW, bodyH);
            SyntaxHighlightingRenderer.DrawColoredSegmentsNoWrap(g, colored, textRect, 0);
        }
    }
}
