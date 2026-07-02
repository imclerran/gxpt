using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Krypton.Toolkit;

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
        private const int StateCancelled = 3;
        private const int Pad = 8;
        private const int RowIndent = 14;

        private string[] _slugs;
        private int[] _state;
        private string[] _models;       // tier 2: per-row resolved model slug, shown inline (author stripped)
        private string[] _tasks;        // tier 2: per-row task, shown as a hover tooltip
        private string[] _lastTool;     // tier 2: most recent tool the child called (null until first call)
        private int[] _toolCount;       // tier 2: how many tool calls the child has made
        private Font _boldFont;

        // Per-row task tooltip (tier 2): the panel is owner-drawn with no child controls, so we drive a
        // single ToolTip manually, retargeting it as the hovered row changes.
        private ToolTip _tip;
        private int _hoverRow = -1;

        // Per-row "View transcript" link (tier 3 "watch live"): _onViewTranscript(row) opens the streaming
        // viewer; _viewRects[i] is the row's link hit-rect (recomputed each paint); _hoverView is the
        // row whose link is hovered (-1 = none).
        private Action<int> _onViewTranscript;
        private Rectangle[] _viewRects;
        private int _hoverView = -1;

        // The "Stop agents" button: a real KryptonButton (themed by the global palette like every other
        // chrome button), right-aligned in the header row. It cancels the fan-out (trips the
        // dispatcher's GroupCancellation via _onStop); _stopping latches after a click so the label
        // reads "Stopping..." and the button disables until the fan-out ends.
        private Action _onStop;
        private KryptonButton _stopButton;
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

            _stopButton = new KryptonButton();
            _stopButton.Text = "Stop agents";
            _stopButton.Visible = false;
            _stopButton.Click += StopButton_Click;
            this.Controls.Add(_stopButton);
        }

        private void StopButton_Click(object sender, EventArgs e)
        {
            if (_stopping) return;
            Action onStop = _onStop;
            _stopping = true;              // latch: label -> "Stopping...", button disabled
            _stopButton.Text = "Stopping...";
            _stopButton.Enabled = false;
            LayoutStopButton();
            Invalidate();
            if (onStop != null) onStop();
        }

        // Right-align the Stop button in the header row, sized to its label. Kept deliberately short
        // (LineH() + 2, top at Pad - 1): centered on the header text with its bottom a few pixels
        // above the first agent row's top (Pad + LineH() + 2), so there's visible breathing room
        // between the button and the first row's "View transcript" link. The label still fits - the
        // button's content area at this height matches the text line exactly.
        private void LayoutStopButton()
        {
            if (_stopButton == null) return;
            try
            {
                int btnH = LineH() + 2;
                int btnW = TextRenderer.MeasureText(_stopButton.Text, this.Font, Size.Empty,
                    TextFormatFlags.NoPadding).Width + 24;
                int x = this.ClientSize.Width - Pad - btnW;
                if (x < Pad) x = Pad;
                _stopButton.SetBounds(x, Pad - 1, btnW, btnH);
            }
            catch { }
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            LayoutStopButton();
        }

        // Start showing a fan-out of the given agents (in dispatch order), all queued. onStop (may be null)
        // is invoked when the user clicks the panel's Stop button.
        public void BeginFanOut(IList<string> slugs, IList<string> tasks, IList<string> models, Action onStop, Action<int> onViewTranscript)
        {
            int n = slugs != null ? slugs.Count : 0;
            _slugs = new string[n];
            _state = new int[n];
            _models = new string[n];
            _tasks = new string[n];
            _lastTool = new string[n];
            _toolCount = new int[n];
            _viewRects = new Rectangle[n];
            for (int i = 0; i < n; i++)
            {
                _slugs[i] = slugs[i];
                _state[i] = StateQueued;
                _models[i] = (models != null && i < models.Count) ? models[i] : null;
                _tasks[i] = (tasks != null && i < tasks.Count) ? tasks[i] : null;
            }
            _onStop = onStop;
            _onViewTranscript = onViewTranscript;
            _stopping = false;
            _hoverRow = -1;
            _hoverView = -1;
            // Match the theme background up front so there's no white flash before the first paint.
            try { this.BackColor = ThemeService.GetColors(IsDark()).AssistantBubbleBack; }
            catch { }
            _stopButton.Text = "Stop agents";
            _stopButton.Enabled = true;
            _stopButton.Visible = _onStop != null && n > 0;
            LayoutStopButton();
            RecalcHeight();
            this.Visible = n > 0;
            Invalidate();
        }

        public void SetRunning(int index) { SetState(index, StateRunning); }
        public void SetDone(int index) { SetState(index, StateDone); }
        public void SetCancelled(int index) { SetState(index, StateCancelled); }

        // Update a row's live activity line (tier 2): the latest tool the child called and the running count.
        public void SetActivity(int index, string lastTool, int toolCount)
        {
            if (_lastTool == null || index < 0 || index >= _lastTool.Length) return;
            _lastTool[index] = lastTool;
            _toolCount[index] = toolCount;
            Invalidate();
        }

        public void EndFanOut()
        {
            this.Visible = false;
            _slugs = null;
            _state = null;
            _models = null;
            _tasks = null;
            _lastTool = null;
            _toolCount = null;
            _viewRects = null;
            _onStop = null;
            _onViewTranscript = null;
            _stopping = false;
            _stopButton.Visible = false;
            _hoverRow = -1;
            _hoverView = -1;
            if (_tip != null) _tip.SetToolTip(this, string.Empty);
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
            LayoutStopButton();
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

            if (_slugs == null || _slugs.Length == 0) return;

            int lineH = LineH() + 2;
            int y = Pad;

            Color cDone = dark ? Color.FromArgb(126, 204, 126) : Color.FromArgb(34, 139, 34);
            Color cRunning = tc.Link;
            Color cQueued = dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(120, 120, 120);
            Color cCancelled = dark ? Color.FromArgb(240, 120, 120) : Color.FromArgb(192, 0, 0);

            int done = 0, running = 0, cancelled = 0;
            for (int i = 0; i < _state.Length; i++)
            {
                if (_state[i] == StateDone) done++;
                else if (_state[i] == StateRunning) running++;
                else if (_state[i] == StateCancelled) cancelled++;
            }
            string header = "Sub-agents: " + running + " running, " + done + " done";
            if (cancelled > 0) header += ", " + cancelled + " cancelled";
            header += " (" + _slugs.Length + " total)";
            TextRenderer.DrawText(g, header, BoldFont(), new Point(Pad, y), tc.UiForeground, TextFormatFlags.NoPadding);
            // (The Stop button is a real KryptonButton child, laid out by LayoutStopButton - not drawn here.)

            y += lineH;

            for (int i = 0; i < _slugs.Length; i++)
            {
                int rowX = Pad + RowIndent;

                // Status tag first: "[status] agent-slug (model-name): X tools - last_tool".
                string tag; Color tagColor;
                if (_state[i] == StateDone) { tag = "[done]"; tagColor = cDone; }
                else if (_state[i] == StateRunning) { tag = "[running]"; tagColor = cRunning; }
                else if (_state[i] == StateCancelled) { tag = "[cancelled]"; tagColor = cCancelled; }
                else { tag = "[queued]"; tagColor = cQueued; }
                TextRenderer.DrawText(g, tag, this.Font, new Point(rowX, y), tagColor, TextFormatFlags.NoPadding);
                int tagW = TextRenderer.MeasureText(g, tag, this.Font, Size.Empty, TextFormatFlags.NoPadding).Width;

                // Agent slug, optionally followed by its model in parentheses (model drawn muted, the same
                // grey as the activity line).
                int x = rowX + tagW + 8;
                TextRenderer.DrawText(g, _slugs[i], this.Font, new Point(x, y), tc.UiForeground, TextFormatFlags.NoPadding);
                int labelW = TextRenderer.MeasureText(g, _slugs[i], this.Font, Size.Empty, TextFormatFlags.NoPadding).Width;
                string model = ShortModel(i);
                if (!string.IsNullOrEmpty(model))
                {
                    string modelText = " (" + model + ")";
                    TextRenderer.DrawText(g, modelText, this.Font, new Point(x + labelW, y), cQueued, TextFormatFlags.NoPadding);
                    labelW += TextRenderer.MeasureText(g, modelText, this.Font, Size.Empty, TextFormatFlags.NoPadding).Width;
                }

                // Live activity line (tier 2): tool count, plus the latest tool while the child runs.
                string activity = BuildActivity(i);
                if (activity.Length > 0)
                {
                    string sep = ": ";
                    int sepW = TextRenderer.MeasureText(g, sep, this.Font, Size.Empty, TextFormatFlags.NoPadding).Width;
                    TextRenderer.DrawText(g, sep, this.Font, new Point(x + labelW, y), tc.UiForeground, TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, activity, this.Font, new Point(x + labelW + sepW, y), cQueued, TextFormatFlags.NoPadding);
                }

                // Per-row "View transcript" link (tier 3 "watch live"), right-aligned. Shown once the child
                // has started (a queued row has no stream yet). Underlined-ish via the theme link color;
                // its rect is hit-tested by the mouse handlers.
                if (_onViewTranscript != null && _state[i] != StateQueued)
                {
                    string vt = "View transcript";
                    int vtW = TextRenderer.MeasureText(g, vt, this.Font, Size.Empty, TextFormatFlags.NoPadding).Width;
                    int vtX = this.ClientRectangle.Width - Pad - vtW - 1;
                    if (vtX < x) vtX = x;
                    Rectangle vr = new Rectangle(vtX, y, vtW, LineH());
                    if (_viewRects != null && i < _viewRects.Length) _viewRects[i] = vr;
                    Color vc = (_hoverView == i) ? tc.UiForeground : tc.Link;
                    TextRenderer.DrawText(g, vt, this.Font, new Point(vtX, y), vc, TextFormatFlags.NoPadding);
                }
                else if (_viewRects != null && i < _viewRects.Length) _viewRects[i] = Rectangle.Empty;
                y += lineH;
            }
        }

        // The muted per-row activity text: "N tools" once the child has called any tool, with the latest
        // tool's short name appended while it is still running (e.g. "3 tools - read"). Empty otherwise.
        private string BuildActivity(int i)
        {
            if (_toolCount == null || i < 0 || i >= _toolCount.Length) return string.Empty;
            int n = _toolCount[i];
            if (n <= 0) return string.Empty;
            string s = n + (n == 1 ? " tool" : " tools");
            if (_state[i] == StateRunning && _lastTool != null && !string.IsNullOrEmpty(_lastTool[i]))
                s += " - " + ShortTool(_lastTool[i]);
            return s;
        }

        // The model name without its author prefix (e.g. "anthropic/claude-opus-4.8" -> "claude-opus-4.8"),
        // so the row stays short. Empty when no model is known for the row.
        private string ShortModel(int i)
        {
            if (_models == null || i < 0 || i >= _models.Length) return string.Empty;
            string m = _models[i];
            if (string.IsNullOrEmpty(m)) return string.Empty;
            int idx = m.IndexOf('/');
            return idx >= 0 && idx + 1 < m.Length ? m.Substring(idx + 1) : m;
        }

        // The unqualified tool name (drop the "server__" prefix) so the activity line stays short.
        private static string ShortTool(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            int idx = name.LastIndexOf("__", StringComparison.Ordinal);
            return idx >= 0 && idx + 2 < name.Length ? name.Substring(idx + 2) : name;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            // The Stop button is a real child control now - the mouse never reaches the panel while
            // over it, so no Stop hit-testing is needed here.
            int vrow = ViewRowAt(e.Location);
            if (vrow != _hoverView) { _hoverView = vrow; Invalidate(); }

            this.Cursor = (vrow >= 0) ? Cursors.Hand : Cursors.Default;

            // Retarget the task tooltip as the hovered row changes (over a row, incl. its View link,
            // shows that row's task).
            int row = RowAt(e.Location);
            if (row != _hoverRow)
            {
                _hoverRow = row;
                string task = (row >= 0 && _tasks != null && row < _tasks.Length) ? _tasks[row] : null;
                EnsureTip().SetToolTip(this, string.IsNullOrEmpty(task) ? string.Empty : task);
            }
        }

        // The row whose "View transcript" link contains the point, or -1.
        private int ViewRowAt(Point p)
        {
            if (_onViewTranscript == null || _viewRects == null) return -1;
            for (int i = 0; i < _viewRects.Length; i++)
                if (_viewRects[i].Width > 0 && _viewRects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverView != -1) { _hoverView = -1; Invalidate(); }
            if (_hoverRow != -1)
            {
                _hoverRow = -1;
                if (_tip != null) _tip.SetToolTip(this, string.Empty);
            }
        }

        // The row index under a client point (the header is the first line; rows follow), or -1 if the
        // point is on the header or below the last row. Mirrors the OnPaint row layout.
        private int RowAt(Point p)
        {
            if (_slugs == null || _slugs.Length == 0) return -1;
            int lineH = LineH() + 2;
            int first = Pad + lineH;            // y of the first agent row (header occupies one line)
            if (p.Y < first) return -1;
            int row = (p.Y - first) / lineH;
            return (row >= 0 && row < _slugs.Length) ? row : -1;
        }

        private ToolTip EnsureTip()
        {
            if (_tip == null)
            {
                _tip = new ToolTip();
                _tip.ShowAlways = true;
                _tip.AutoPopDelay = 30000;     // keep long tasks visible
                _tip.InitialDelay = 400;
                _tip.ReshowDelay = 100;
            }
            return _tip;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int vrow = ViewRowAt(e.Location);
            if (vrow >= 0 && _onViewTranscript != null) _onViewTranscript(vrow);
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
            if (disposing && _tip != null) { _tip.Dispose(); _tip = null; }
            base.Dispose(disposing);
        }
    }
}
