using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // A native panel docked at the bottom of the chat area that shows the sub-agents running during a
    // dispatch_agent fan-out (design sec.14). Transient: shown on BeginFanOut, updated per child, hidden on
    // EndFanOut. Owner-drawn so it matches the active dark/light theme (ThemeService) and colors each
    // agent's status. Display-only - a child's activity is shown to the user here, never fed to the model
    // (A7). All public methods must be called on the UI thread (the dispatcher's callbacks are marshaled
    // there by AgentActivityUiBridge).
    internal sealed class AgentActivityPanel : Panel
    {
        private const int StateQueued = 0;
        private const int StateRunning = 1;
        private const int StateDone = 2;
        private const int Pad = 8;
        private const int RowIndent = 14;

        private string[] _slugs;
        private int[] _state;
        private Font _boldFont;

        // The "Stop" button: an owner-drawn region in the header that cancels the fan-out (trips the
        // dispatcher's GroupCancellation via _onStop). _stopRect is recomputed each paint and hit-tested
        // by the mouse handlers; _stopping latches after a click so the label reads "Stopping..." and
        // further clicks are ignored until the fan-out ends.
        private Action _onStop;
        private Rectangle _stopRect;
        private bool _stopHover;
        private bool _stopping;

        public AgentActivityPanel()
        {
            this.Dock = DockStyle.Bottom;
            this.Visible = false;
            this.AutoSize = false;
            this.Height = 24;
            // Owner-draw, flicker-free: we fill + border + text ourselves so the panel tracks the theme.
            this.SetStyle(ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint, true);
        }

        // Start showing a fan-out of the given agents (in dispatch order), all queued. onStop (may be null)
        // is invoked when the user clicks the panel's Stop button.
        public void BeginFanOut(IList<string> slugs, Action onStop)
        {
            int n = slugs != null ? slugs.Count : 0;
            _slugs = new string[n];
            _state = new int[n];
            for (int i = 0; i < n; i++) { _slugs[i] = slugs[i]; _state[i] = StateQueued; }
            _onStop = onStop;
            _stopping = false;
            _stopHover = false;
            // Match the theme background up front so there's no white flash before the first paint.
            try { this.BackColor = ThemeService.GetColors(IsDark()).AssistantBubbleBack; }
            catch { }
            RecalcHeight();
            this.Visible = n > 0;
            Invalidate();
        }

        public void SetRunning(int index) { SetState(index, StateRunning); }
        public void SetDone(int index) { SetState(index, StateDone); }

        public void EndFanOut()
        {
            this.Visible = false;
            _slugs = null;
            _state = null;
            _onStop = null;
            _stopping = false;
            _stopHover = false;
            this.Cursor = Cursors.Default;
        }

        private void SetState(int index, int state)
        {
            if (_state == null || index < 0 || index >= _state.Length) return;
            _state[index] = state;
            Invalidate();
        }

        private int LineH() { return this.Font.Height > 0 ? this.Font.Height : 16; }

        private void RecalcHeight()
        {
            // Flexible: header line + one row per agent. The ceiling is sized to the dispatcher's batch
            // maximum (1 + MaxAgentsPerCall rows) at the current font, so the realistic worst case always
            // fits without clipping; it stays only as a defensive bound (the dispatcher caps the batch, so
            // the panel never actually exceeds it).
            int rowH = LineH() + 2;
            int lines = (_slugs != null) ? 1 + _slugs.Length : 1;
            int ceiling = Pad * 2 + (1 + AgentDispatcher.MaxAgentsPerCall) * rowH;
            this.Height = Math.Min(Pad * 2 + lines * rowH, ceiling);
        }

        private Font BoldFont()
        {
            if (_boldFont == null)
                _boldFont = new Font(this.Font, FontStyle.Bold);
            return _boldFont;
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_boldFont != null) { _boldFont.Dispose(); _boldFont = null; }
            RecalcHeight();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            bool dark = IsDark();
            ThemeColors tc = ThemeService.GetColors(dark);
            Graphics g = e.Graphics;

            using (SolidBrush bg = new SolidBrush(tc.AssistantBubbleBack))
                g.FillRectangle(bg, this.ClientRectangle);
            Rectangle border = this.ClientRectangle;
            border.Width -= 1; border.Height -= 1;
            using (Pen pen = new Pen(tc.AssistantBubbleBorder))
                g.DrawRectangle(pen, border);

            if (_slugs == null || _slugs.Length == 0) { _stopRect = Rectangle.Empty; return; }

            int lineH = LineH() + 2;
            int y = Pad;

            Color cDone = dark ? Color.FromArgb(126, 204, 126) : Color.FromArgb(34, 139, 34);
            Color cRunning = tc.Link;
            Color cQueued = dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(120, 120, 120);

            int done = 0, running = 0;
            for (int i = 0; i < _state.Length; i++)
            {
                if (_state[i] == StateDone) done++;
                else if (_state[i] == StateRunning) running++;
            }
            string header = "Sub-agents: " + running + " running, " + done + " of " + _slugs.Length + " done";
            TextRenderer.DrawText(g, header, BoldFont(), new Point(Pad, y), tc.UiForeground, TextFormatFlags.NoPadding);

            // Stop button, right-aligned in the header row.
            if (_onStop != null)
            {
                string btnText = _stopping ? "Stopping..." : "Stop";
                int btnW = TextRenderer.MeasureText(g, btnText, this.Font, Size.Empty, TextFormatFlags.NoPadding).Width + 16;
                int btnX = this.ClientRectangle.Width - Pad - btnW - 1;
                if (btnX < Pad) btnX = Pad;
                _stopRect = new Rectangle(btnX, y, btnW, lineH);

                Color face = (_stopHover && !_stopping) ? tc.CopyHover : tc.CodeBack;
                using (SolidBrush bb = new SolidBrush(face)) g.FillRectangle(bb, _stopRect);
                Rectangle br = _stopRect; br.Width -= 1; br.Height -= 1;
                using (Pen bp = new Pen(tc.AssistantBubbleBorder)) g.DrawRectangle(bp, br);
                TextRenderer.DrawText(g, btnText, this.Font, _stopRect, _stopping ? cQueued : tc.UiForeground,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            else _stopRect = Rectangle.Empty;

            y += lineH;

            for (int i = 0; i < _slugs.Length; i++)
            {
                int rowX = Pad + RowIndent;
                TextRenderer.DrawText(g, _slugs[i], this.Font, new Point(rowX, y), tc.UiForeground, TextFormatFlags.NoPadding);
                int slugW = TextRenderer.MeasureText(g, _slugs[i], this.Font, Size.Empty, TextFormatFlags.NoPadding).Width;

                string tag; Color tagColor;
                if (_state[i] == StateDone) { tag = "[done]"; tagColor = cDone; }
                else if (_state[i] == StateRunning) { tag = "[running]"; tagColor = cRunning; }
                else { tag = "[queued]"; tagColor = cQueued; }
                TextRenderer.DrawText(g, tag, this.Font, new Point(rowX + slugW + 8, y), tagColor, TextFormatFlags.NoPadding);
                y += lineH;
            }
        }

        private bool OverStop(Point p)
        {
            return _onStop != null && !_stopping && _stopRect.Width > 0 && _stopRect.Contains(p);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool over = OverStop(e.Location);
            if (over != _stopHover)
            {
                _stopHover = over;
                this.Cursor = over ? Cursors.Hand : Cursors.Default;
                Invalidate(_stopRect);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_stopHover)
            {
                _stopHover = false;
                this.Cursor = Cursors.Default;
                Invalidate(_stopRect);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && OverStop(e.Location))
            {
                Action onStop = _onStop;
                _stopping = true;          // latch: label -> "Stopping...", further clicks ignored
                _stopHover = false;
                this.Cursor = Cursors.Default;
                Invalidate();
                if (onStop != null) onStop();
            }
        }

        private static bool IsDark()
        {
            try
            {
                string th = AppSettings.GetString("theme");
                return !string.IsNullOrEmpty(th) && th.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _boldFont != null) { _boldFont.Dispose(); _boldFont = null; }
            base.Dispose(disposing);
        }
    }
}
