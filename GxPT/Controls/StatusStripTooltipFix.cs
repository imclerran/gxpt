using System;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // Fixes the flickering status-bar tooltips by replacing the ToolStrip's built-in tooltip with a
    // manually driven one.
    //
    // Root cause of the flicker: ToolStrip.ShowItemToolTips positions the native tooltip directly
    // below the cursor (cursor position + cursor height). ssMain is docked at the very bottom of the
    // window, so there is no room below; Windows flips the tooltip up ONTO the cursor. The tooltip
    // window now sits under the pointer, the strip receives a synthetic WM_MOUSELEAVE, hides the
    // tooltip, the pointer is over the strip again, it re-shows -- the rapid on/off/on/off loop the
    // user sees. The framework's UpdateToolTip is internal and sealed against override/reflection, so
    // the position can't be corrected in place; the only reliable fix is to stop using it.
    //
    // This manager turns ShowItemToolTips off and shows a plain ToolTip itself, with two properties
    // that make the loop impossible:
    //   1. It shows a tip only when the cursor moves to a DIFFERENT item, never while hovering within
    //      one -- so there is no per-mouse-move re-show to flicker.
    //   2. It anchors the tip ABOVE the strip, where it can never overlap the cursor, and ignores the
    //      synthetic mouse-leave the tip window would otherwise trigger.
    internal sealed class StatusStripTooltipFix
    {
        private readonly ToolStrip _strip;
        private readonly ToolTip _toolTip;
        private readonly Timer _showTimer;

        private ToolStripItem _hotItem;    // item the cursor is currently over
        private ToolStripItem _shownItem;  // item the tip is currently displayed for
        private Point _lastClientPos;

        internal static void Apply(ToolStrip strip)
        {
            if (strip == null) return;
            // Construction wires the handlers; the instance is kept alive by those subscriptions
            // (and torn down when the strip is disposed).
            new StatusStripTooltipFix(strip);
        }

        private StatusStripTooltipFix(ToolStrip strip)
        {
            _strip = strip;

            // Drop the buggy native tooltips; everything below replaces them.
            _strip.ShowItemToolTips = false;

            _toolTip = new ToolTip();
            _toolTip.ShowAlways = true;

            _showTimer = new Timer();
            // Match the system hover delay so tips feel the same as before, just without the flicker.
            _showTimer.Interval = Math.Max(1, SystemInformation.MouseHoverTime);
            _showTimer.Tick += ShowTimer_Tick;

            _strip.MouseMove += Strip_MouseMove;
            _strip.MouseLeave += Strip_MouseLeave;
            _strip.MouseDown += Strip_MouseDown;
            _strip.Disposed += Strip_Disposed;
        }

        private void Strip_MouseMove(object sender, MouseEventArgs e)
        {
            _lastClientPos = e.Location;

            ToolStripItem item = _strip.GetItemAt(e.Location);
            // Same region as last move: leave any shown tip exactly as-is. This is what kills the
            // flicker -- a stationary or in-region move never re-shows the tip.
            if (item == _hotItem) return;

            _hotItem = item;
            HideTip();
            _showTimer.Stop();
            if (HasTip(item)) _showTimer.Start();
        }

        private void ShowTimer_Tick(object sender, EventArgs e)
        {
            _showTimer.Stop();

            ToolStripItem item = _hotItem;
            if (!HasTip(item) || item == _shownItem) return;
            if (!_strip.IsHandleCreated || !_strip.Visible) return;

            // Anchor the tip above the strip so it can never land under the cursor (the condition
            // that drives the native flicker). y is negative because it is relative to the strip's
            // own top edge, and the strip sits at the bottom of the window.
            Size sz = TextRenderer.MeasureText(item.ToolTipText, SystemFonts.DefaultFont);
            int x = _lastClientPos.X;
            int y = -(sz.Height + 10);

            _toolTip.Show(item.ToolTipText, _strip, x, y, _toolTip.AutoPopDelay);
            _shownItem = item;
        }

        private void Strip_MouseLeave(object sender, EventArgs e)
        {
            // The tip window can raise a leave while the pointer is still physically over the strip;
            // ignore those so we don't hide-then-reshow. Only tear down on a genuine exit.
            Point p = _strip.PointToClient(Cursor.Position);
            if (_strip.ClientRectangle.Contains(p)) return;

            _showTimer.Stop();
            _hotItem = null;
            HideTip();
        }

        private void Strip_MouseDown(object sender, MouseEventArgs e)
        {
            // A click shouldn't strand a tip on screen.
            _showTimer.Stop();
            _hotItem = null;
            HideTip();
        }

        private void Strip_Disposed(object sender, EventArgs e)
        {
            try { _showTimer.Dispose(); } catch { }
            try { _toolTip.Dispose(); } catch { }
        }

        private static bool HasTip(ToolStripItem item)
        {
            return item != null && !string.IsNullOrEmpty(item.ToolTipText);
        }

        private void HideTip()
        {
            if (_shownItem != null)
            {
                _toolTip.Hide(_strip);
                _shownItem = null;
            }
        }
    }
}
