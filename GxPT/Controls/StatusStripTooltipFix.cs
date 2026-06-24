using System;
using System.Reflection;
using System.Windows.Forms;

namespace GxPT
{
    // Stops the status-bar tooltips from flickering after the strip is clicked, while keeping the
    // normal (native) ToolStrip tooltips that work fine the rest of the time.
    //
    // Clicking a ToolStrip/StatusStrip latches it into the framework's "modal menu" navigation mode
    // (ToolStripManager.ModalMenuFilter) -- the same state a menu bar uses. A status bar has nothing
    // to navigate, but while it is stuck there the strip continuously re-tracks the mouse; combined
    // with the well-known bottom-docked-ToolStrip tooltip quirk (the native tip is positioned under
    // the cursor, which the strip then reads as a mouse-leave and immediately re-shows), that churn
    // produces the rapid on/off/on/off flashing. The mode only ends when a mouse-down lands on some
    // other window (e.g. the chat transcript) -- exactly the user-visible "click elsewhere to fix it".
    //
    // We replay that teardown ourselves: after each mouse-down on the strip we exit menu mode through
    // the framework's own ModalMenuFilter.ExitMenuMode(). It is internal, so we reach it by
    // reflection; if the lookup ever fails the call degrades to a no-op (the pre-existing flicker),
    // never a throw.
    //
    // Why MouseDOWN, not MouseUp: once menu mode is active its message filter swallows the mouse-up
    // before the strip can raise a MouseUp event, so a MouseUp handler would never run. The mouse-down
    // that ENTERS menu mode still raises normally; we BeginInvoke the exit so it runs after the strip
    // has finished entering menu mode for that click.
    internal static class StatusStripTooltipFix
    {
        private static bool _resolved;
        private static MethodInfo _exitMenuMode;

        internal static void Apply(ToolStrip strip)
        {
            if (strip == null) return;

            // Keep the native item tooltips; only their post-click flicker is the problem.
            strip.ShowItemToolTips = true;

            strip.MouseDown += delegate
            {
                if (!strip.IsHandleCreated) return;
                // Defer past the current click so menu mode is fully established before we drop it.
                try { strip.BeginInvoke((MethodInvoker)ExitMenuMode); }
                catch { }
            };
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
