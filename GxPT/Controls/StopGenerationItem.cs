using System;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // The status bar's stop button, shown (next to the marquee progress bar) only while the active
    // tab has a model request in flight. Clicking raises Click, which the host wires to the active
    // tab's RequestCancellation so the in-flight curl process is killed (dropping the connection);
    // the streaming/orchestrator paths then finalize the turn cleanly and the indicator is hidden.
    //
    // Owner-drawn ToolStripItem (like ContextMeterItem) styled after the transcript's Retry button
    // (a flat fill, a 1px border, a text label) but in SYSTEM colors, not the transcript theme: the
    // strip is system chrome, so the border and "Stop" label use the control foreground and the
    // fills stay neutral (the strip's own Control color at rest, the tab glyph buttons' light-grey
    // hover/press shades). Height matches tspGenProgress exactly so the pair reads as one row.
    internal sealed class StopGenerationItem : ToolStripItem
    {
        private bool _hover;
        private bool _pressed;
        private bool _awaiting;

        private const int PadX = 8;        // horizontal label padding, matching RetryBtnPadX
        public const int ItemHeight = 15;  // tspGenProgress's height

        private const string StopText = "Stop";
        private const string AwaitingText = "Awaiting user...";

        private const string StopTip = "Stop generating";
        private const string AwaitingTip = "Waiting for tool approval";

        public StopGenerationItem()
        {
            this.AutoSize = false;
            this.Text = StopText;
            this.Size = new Size(PreferredWidth(), ItemHeight);
        }

        // While a tool-approval (or continuation) prompt awaits the user's decision, the turn is
        // paused at the gate: there is nothing to "Stop", so this item drops its button chrome and
        // shows a passive "awaiting user..." status label instead. The host pauses the marquee and
        // ignores clicks on this item in that state.
        public bool Awaiting
        {
            get { return _awaiting; }
            set
            {
                if (_awaiting == value) return;
                _awaiting = value;
                _hover = false;
                _pressed = false;
                this.Text = value ? AwaitingText : StopText;
                this.ToolTipText = value ? AwaitingTip : StopTip;
                try { this.Width = PreferredWidth(); }
                catch { }
                Invalidate();
            }
        }

        protected override Size DefaultSize
        {
            get { return new Size(PreferredWidth(), ItemHeight); }
        }

        private int PreferredWidth()
        {
            try { return TextRenderer.MeasureText(this.Text, this.Font).Width + 2 * PadX; }
            catch { return 44; }
        }

        // The strip's font can change after construction (it is inherited); keep the width fitting
        // the label so "Stop" never clips at other font sizes/DPI.
        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            try { this.Width = PreferredWidth(); }
            catch { }
        }

        protected override void OnMouseEnter(EventArgs e) { if (!_awaiting) { _hover = true; Invalidate(); } base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (!_awaiting && e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;

            Rectangle bounds = new Rectangle(0, 0, this.Width, this.Height);

            // Awaiting state: a flat status label, no border/fill, so it reads as passive text rather
            // than a clickable Stop button. Uses ControlText (like the strip's other labels) rather
            // than the lighter GrayText, which next to them reads as thinner/smaller.
            if (_awaiting)
            {
                TextRenderer.DrawText(g, this.Text, this.Font, bounds, SystemColors.ControlText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                return;
            }

            Rectangle border = new Rectangle(0, 0, this.Width - 1, this.Height - 1);

            Color fill = _pressed ? Color.FromArgb(210, 210, 210)
                : (_hover ? Color.FromArgb(230, 230, 230) : SystemColors.Control);
            using (SolidBrush sb = new SolidBrush(fill))
                g.FillRectangle(sb, bounds);
            using (Pen pen = new Pen(SystemColors.ControlText))
                g.DrawRectangle(pen, border);

            TextRenderer.DrawText(g, this.Text, this.Font, bounds, SystemColors.ControlText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }
}
