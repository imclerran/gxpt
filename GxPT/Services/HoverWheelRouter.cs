using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace GxPT
{
    /// <summary>
    /// Application-wide hover-to-scroll router. Ensures mouse wheel scrolls the control
    /// under the cursor (ChatTranscriptControl, ListView, or TextBoxBase) without changing focus.
    /// Target: .NET 3.5 / Windows XP compatible.
    /// </summary>
    internal static class HoverWheelRouter
    {
        private static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            try
            {
                Application.AddMessageFilter(new Filter());
                _installed = true;
            }
            catch { }
        }

        private sealed class Filter : IMessageFilter
        {
            private const int WM_MOUSEWHEEL = 0x020A;
            private const int WHEEL_DELTA = 120; // reference only; we forward raw deltas for precision scrolling

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            private static extern IntPtr WindowFromPoint(System.Drawing.Point p);

            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WM_MOUSEWHEEL) return false;
                // Extract wheel delta from wParam high word safely on 64-bit
                long wparam64;
                try { wparam64 = m.WParam.ToInt64(); }
                catch { return false; }
                int wheelDelta = (short)((wparam64 >> 16) & 0xFFFF);
                Point screenPt = Control.MousePosition;

                // Hit-test the window under the cursor
                IntPtr hwnd = IntPtr.Zero;
                Control hit = null;
                try
                {
                    hwnd = WindowFromPoint(screenPt);
                    if (hwnd == IntPtr.Zero) return false;
                    hit = Control.FromHandle(hwnd);
                    if (hit == null) return false; // not one of our controls
                }
                catch { return false; }

                // Determine the host form and ensure it matches the active form
                Form hostForm = hit.FindForm();
                Form active = Form.ActiveForm;
                if (hostForm == null) return false;
                if (active != null && !ReferenceEquals(hostForm, active))
                {
                    // Respect the active form: do not scroll inactive windows
                    return false;
                }

                // 1) If a ChatTranscriptControl is in the ancestry, route to it
                ChatTranscriptControl transcript = FindAncestorChatTranscript(hit);
                if (transcript != null && transcript.Visible && !transcript.IsDisposed)
                {
                    try
                    {
                        Rectangle screenRect = transcript.RectangleToScreen(transcript.ClientRectangle);
                        if (screenRect.Contains(screenPt))
                        {
                            transcript.HandleHoverWheel(wheelDelta, screenPt, Control.ModifierKeys);
                            return true; // consumed
                        }
                    }
                    catch { }
                }

                // 2) Otherwise forward to a scrollable control in the ancestry. DataGridView is in
                // the list for the conversation-history sidebar (a KryptonDataGridView since the
                // Krypton migration): its native scrollbars are off and it scrolls via its own
                // MouseWheel handler, which only fires if this router forwards hover wheel to it.
                try
                {
                    Control cur = hit;
                    int guard = 0;
                    while (cur != null && guard++ < 10)
                    {
                        if ((cur is ListView || cur is TextBoxBase || cur is ComboBox || cur is UpDownBase || cur is DataGridView) && cur.Visible)
                        {
                            SendMessage(cur.Handle, WM_MOUSEWHEEL, m.WParam, m.LParam);
                            return true;
                        }
                        cur = cur.Parent;
                    }
                }
                catch { }

                return false;
            }

            // No explicit accumulator: raw precision deltas are forwarded, and classic mice send +/-120 multiples

            private static ChatTranscriptControl FindAncestorChatTranscript(Control start)
            {
                try
                {
                    Control cur = start;
                    int guard = 0;
                    while (cur != null && guard++ < 20)
                    {
                        ChatTranscriptControl ctc = cur as ChatTranscriptControl;
                        if (ctc != null) return ctc;
                        cur = cur.Parent;
                    }
                }
                catch { }
                return null;
            }
        }
    }
}
