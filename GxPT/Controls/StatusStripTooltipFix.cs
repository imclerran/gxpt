using System;
using System.Reflection;
using System.Windows.Forms;

namespace GxPT
{
    // Works around a long-standing WinForms quirk behind the flickering status-bar tooltips:
    // clicking anywhere on a ToolStrip/StatusStrip puts the strip into the same "modal menu"
    // navigation mode that a real menu bar uses (ToolStripManager.ModalMenuFilter latches onto
    // the strip as the active ToolStrip). A StatusStrip is a passive info bar with nothing to
    // navigate, but while it is stuck in that mode the built-in item tooltips (ShowItemToolTips)
    // fight with the menu-mode mouse tracking: every mouse move over the strip resets the
    // currently-active tooltip item, so the native tooltip hides and re-shows on each move --
    // the rapid on/off/on/off flashing. The mode only tears down when a mouse-down lands on some
    // OTHER window (e.g. the chat transcript), which is why clicking elsewhere reliably ends it.
    //
    // The fix replays that teardown ourselves: after a click on the strip we call the framework's
    // own ModalMenuFilter.ExitMenuMode() -- the exact path a click elsewhere would take -- leaving
    // the strip idle so tooltips show for their normal duration again. That method is internal, so
    // we reach it by reflection; if the lookup ever fails the call degrades to a no-op (the
    // pre-existing flicker), never an exception.
    internal static class StatusStripTooltipFix
    {
        private static bool _resolved;
        private static MethodInfo _exitMenuMode;

        // Wire a strip so a click on it can't leave it stuck in menu mode.
        internal static void Apply(ToolStrip strip)
        {
            if (strip == null) return;
            strip.MouseUp += delegate { ExitMenuMode(); };
        }

        private static void ExitMenuMode()
        {
            try
            {
                MethodInfo m = ResolveExitMenuMode();
                if (m != null) m.Invoke(null, null);
            }
            catch { }
        }

        private static MethodInfo ResolveExitMenuMode()
        {
            if (_resolved) return _exitMenuMode;
            _resolved = true;
            try
            {
                Type filter = typeof(ToolStripManager).GetNestedType(
                    "ModalMenuFilter", BindingFlags.NonPublic);
                if (filter != null)
                {
                    _exitMenuMode = filter.GetMethod("ExitMenuMode",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                        null, Type.EmptyTypes, null);
                }
            }
            catch { _exitMenuMode = null; }
            return _exitMenuMode;
        }
    }
}
