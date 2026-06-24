using System;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // Replaces the StatusStrip's native item tooltips with manually driven ones to stop the flicker.
    //
    // Root cause: the native ToolStrip tooltip is positioned just under the cursor. With the strip at
    // the very bottom of the screen the tip lands on/over the Windows taskbar and under the cursor;
    // the strip then registers a mouse-leave, hides the tip, and immediately re-shows it -- the rapid
    // on/off/on/off loop. The framework's positioning (UpdateToolTip) is internal and cannot be
    // overridden or corrected in place.
    //
    // The fix turns the native tooltips off and shows a plain ToolTip ourselves, with the three
    // things that make the loop impossible:
    //   1. Triggered by per-item MouseHover/MouseEnter (NOT MouseMove). MouseMove fires constantly and
    //      re-shows the tip under the cursor on every pixel of movement, which is itself a flicker
    //      source; hover fires once when the pointer settles on an item.
    //   2. Shown ABOVE the status bar, where it can never overlap the cursor or the taskbar.
    //   3. Shown at most once per item (guarded), and hidden only on a real MouseLeave/MouseDown.
    //
    // ToolTip.Show prerequisites that the earlier attempts got wrong and that are required for it to
    // display at all: Active and ShowAlways true, a positive duration (AutoPopDelay defaults to 0 on a
    // code-created ToolTip, which suppresses display), and on-screen client coordinates.
    internal sealed class StatusStripTooltipFix
    {
        private readonly ToolStrip _strip;
        private readonly ToolTip _toolTip;
        private readonly Timer _hoverTimer;

        private ToolStripItem _hotItem;    // item the pointer is currently over
        private ToolStripItem _shownItem;  // item the tip is currently displayed for
        private Control _tipOwner;         // window Show()/Hide() are anchored to

        internal static void Apply(ToolStrip strip)
        {
            if (strip == null) return;
            // The instance keeps itself alive through the event subscriptions below.
            new StatusStripTooltipFix(strip);
        }

        private StatusStripTooltipFix(ToolStrip strip)
        {
            _strip = strip;

            // Turn off the buggy native tooltips; everything below replaces them.
            _strip.ShowItemToolTips = false;

            _toolTip = new ToolTip();
            _toolTip.Active = true;
            _toolTip.ShowAlways = true;
            _toolTip.UseAnimation = false;
            _toolTip.UseFading = false;
            _toolTip.AutoPopDelay = 5000;
            _toolTip.InitialDelay = 500;
            _toolTip.ReshowDelay = 100;

            // Fallback delay timer in case a given item doesn't raise MouseHover; MouseEnter always
            // fires, so this guarantees the tip appears after the system hover delay regardless.
            _hoverTimer = new Timer();
            _hoverTimer.Interval = Math.Max(1, SystemInformation.MouseHoverTime);
            _hoverTimer.Tick += HoverTimer_Tick;

            foreach (ToolStripItem item in strip.Items) HookItem(item);
            strip.ItemAdded += delegate(object sender, ToolStripItemEventArgs e)
            {
                if (e != null && e.Item != null) HookItem(e.Item);
            };
            strip.Disposed += delegate
            {
                try { _hoverTimer.Dispose(); } catch { }
                try { _toolTip.Dispose(); } catch { }
            };
        }

        private void HookItem(ToolStripItem item)
        {
            item.MouseEnter += Item_MouseEnter;
            item.MouseHover += Item_MouseHover;
            item.MouseLeave += Item_MouseLeave;
            item.MouseDown += Item_MouseDown;
        }

        private void Item_MouseEnter(object sender, EventArgs e)
        {
            _hotItem = sender as ToolStripItem;
            _hoverTimer.Stop();
            if (HasTip(_hotItem)) _hoverTimer.Start();
        }

        // Either trigger (the system hover event, or our fallback timer) shows the tip; the
        // once-per-item guard inside ShowTipFor keeps them from fighting.
        private void Item_MouseHover(object sender, EventArgs e)
        {
            ShowTipFor(sender as ToolStripItem);
        }

        private void HoverTimer_Tick(object sender, EventArgs e)
        {
            _hoverTimer.Stop();
            ShowTipFor(_hotItem);
        }

        private void ShowTipFor(ToolStripItem item)
        {
            if (!HasTip(item) || item != _hotItem || item == _shownItem) return;
            if (!_strip.IsHandleCreated || !_strip.Visible) return;

            Control owner = _strip.FindForm();
            if (owner == null) owner = _strip;
            _tipOwner = owner;

            // Anchor just above the cursor, not above the bar's top edge: the pointer can be anywhere
            // down the ~22px-tall strip, so anchoring to the item top left small tips floating well
            // above the mouse. Lifting from the cursor by the tip height puts the tip's bottom a few
            // pixels above the pointer -- close to the mouse, never under it, never over the taskbar.
            Size sz = TextRenderer.MeasureText(item.ToolTipText, SystemFonts.DefaultFont);
            Point onScreen = Cursor.Position;
            onScreen.Y -= sz.Height + 8;
            Point pt = owner.PointToClient(onScreen);
            if (pt.X < 0) pt.X = 0;
            if (pt.Y < 0) pt.Y = 0;

            _toolTip.Show(item.ToolTipText, owner, pt, 5000);
            _shownItem = item;
        }

        private void Item_MouseLeave(object sender, EventArgs e)
        {
            ToolStripItem item = sender as ToolStripItem;
            if (item == _hotItem)
            {
                _hoverTimer.Stop();
                _hotItem = null;
            }
            HideTip();
        }

        private void Item_MouseDown(object sender, MouseEventArgs e)
        {
            // A click shouldn't strand a tip on screen.
            _hoverTimer.Stop();
            HideTip();
        }

        private static bool HasTip(ToolStripItem item)
        {
            return item != null && !string.IsNullOrEmpty(item.ToolTipText);
        }

        private void HideTip()
        {
            if (_shownItem != null)
            {
                _toolTip.Hide(_tipOwner != null ? _tipOwner : _strip);
                _shownItem = null;
            }
        }
    }
}
