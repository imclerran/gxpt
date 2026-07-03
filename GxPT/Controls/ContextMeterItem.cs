using System;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // The status bar's context meter: how full the active model's context window is, drawn as a
    // row of discrete "LED" blocks behind a 1px dark-grey border - the look of the classic
    // pre-Vista progress bar, which suits the app. Owner-drawn ToolStripItem rather than a
    // hosted ProgressBar: the themed native control sweeps a highlight across the fill even at
    // a static value (the Vista/7 "pulse"), and un-theming it traded that for handle-recreation
    // bookkeeping - painting a dozen rectangles ourselves needs neither. The blocks' coarse
    // quantization is also a feature: small token fluctuations have to earn a whole block
    // before the meter moves.
    //
    // The item's width is DERIVED from the block geometry (border + gap-sized pad + blocks +
    // gaps) so the blocks sit flush: the spacing between the last block and the border equals
    // the inter-block gap, with no leftover sliver of track at the right end.
    //
    // Fill color tracks how much of the window remains: green while comfortable, yellow once
    // usage crosses 70%, red past 90%.
    internal sealed class ContextMeterItem : ToolStripItem
    {
        private long _used;
        private int _max;

        private const int BlockCount = 13;
        private const int BlockWidth = 5;
        private const int BlockGap = 1;
        // 1px border plus a gap-sized pad on every side.
        private const int Inset = 1 + BlockGap;
        public const int MeterWidth =
            2 * Inset + BlockCount * BlockWidth + (BlockCount - 1) * BlockGap; // 81
        public const int MeterHeight = 15;

        // Fallbacks used only if the theme lookups throw; the live colors are
        // resolved per-paint in OnPaint so the meter follows the active theme.
        private static readonly Color BorderColorFallback = Color.FromArgb(96, 96, 96);
        private static readonly Color TrackColorFallback = Color.FromArgb(252, 252, 252);
        private static readonly Color EmptyBlockColorFallback = Color.FromArgb(228, 228, 228);
        private static readonly Color GreenFill = Color.FromArgb(76, 160, 66);
        private static readonly Color YellowFill = Color.FromArgb(226, 168, 22);
        private static readonly Color RedFill = Color.FromArgb(199, 56, 44);

        public ContextMeterItem()
        {
            this.AutoSize = false;
            this.Size = new Size(MeterWidth, MeterHeight);
        }

        protected override Size DefaultSize
        {
            get { return new Size(MeterWidth, MeterHeight); }
        }

        // Update what the meter shows (UI thread). No-op when nothing changed, so the frequent
        // status-strip syncs (tab switches, per-keystroke model edits) don't repaint.
        public void SetLevel(long usedTokens, int maxTokens)
        {
            if (usedTokens < 0) usedTokens = 0;
            if (_used == usedTokens && _max == maxTokens) return;
            _used = usedTokens;
            _max = maxTokens;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            int w = this.Width, h = this.Height;

            // Theme-aware chrome: the track between blocks takes the chat-transcript
            // background (white / #242424), empty blocks take the window chrome color,
            // and the border takes the status-strip text color - so the meter fits the
            // active theme. The lit green/yellow/red fills stay (they signal usage).
            bool dark = false;
            try { dark = KryptonThemeBridge.IsDarkMode(); }
            catch { }
            Color trackColor, borderColor, emptyColor;
            try { trackColor = ThemeService.GetColors(dark).UiBackground; }
            catch { trackColor = TrackColorFallback; }
            try { borderColor = KryptonThemeBridge.StatusStripTextColor(); }
            catch { borderColor = BorderColorFallback; }
            try { emptyColor = KryptonThemeBridge.StatusStripBackColor(); }
            catch { emptyColor = EmptyBlockColorFallback; }

            using (SolidBrush track = new SolidBrush(trackColor))
                g.FillRectangle(track, 0, 0, w, h);
            using (Pen border = new Pen(borderColor))
                g.DrawRectangle(border, 0, 0, w - 1, h - 1);

            double frac = (_max > 0) ? (double)_used / _max : 0.0;
            if (frac < 0.0) frac = 0.0;
            if (frac > 1.0) frac = 1.0; // a request can overshoot the window (provider trims)
            int lit = (int)Math.Round(frac * BlockCount, MidpointRounding.AwayFromZero);

            Color fill = frac < 0.70 ? GreenFill : (frac < 0.90 ? YellowFill : RedFill);
            int blockHeight = h - 2 * Inset;
            using (SolidBrush on = new SolidBrush(fill))
            using (SolidBrush off = new SolidBrush(emptyColor))
            {
                int bx = Inset;
                for (int i = 0; i < BlockCount; i++)
                {
                    g.FillRectangle(i < lit ? on : off, bx, Inset, BlockWidth, blockHeight);
                    bx += BlockWidth + BlockGap;
                }
            }
        }
    }
}
