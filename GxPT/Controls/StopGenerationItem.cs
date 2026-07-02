using System;
using System.Drawing;
using System.Windows.Forms;
using Krypton.Toolkit;

namespace GxPT
{
    // The status bar's stop button, shown (next to the marquee progress bar) only while the active
    // tab has a model request in flight. Clicking raises Click, which the host wires to the active
    // tab's RequestCancellation so the in-flight curl process is killed (dropping the connection);
    // the streaming/orchestrator paths then finalize the turn cleanly and the indicator is hidden.
    //
    // A ToolStripControlHost wrapping a real KryptonButton, so the button is themed by the global
    // Krypton palette exactly like the rest of the chrome (it was previously owner-drawn in system
    // colors, which read wrong against the themed strip). Either passive state (awaiting a tool
    // approval, or sub-agents running) hides the button and instead draws a flat status label on the
    // hosting panel, in the same color Krypton paints the strip's other labels. Height matches
    // tspGenProgress exactly so the pair reads as one row.
    internal sealed class StopGenerationItem : ToolStripControlHost
    {
        private readonly HostPanel _panel;
        private readonly KryptonButton _button;
        private readonly ToolTip _tip;

        private bool _awaiting;
        private bool _agentsRunning;

        private const int PadX = 8;        // horizontal label padding, matching RetryBtnPadX
        public const int ItemHeight = 15;  // tspGenProgress's height

        private const string StopText = "Stop";
        private const string AwaitingText = "Awaiting user...";
        private const string AgentsText = "Sub-agents running...";

        private const string StopTip = "Stop generating";
        private const string AwaitingTip = "Waiting for tool approval";
        private const string AgentsTip = "Sub-agents are running - use Stop in the panel above to cancel them";

        // Either passive state drops the button and shows a status label instead (the turn isn't
        // stopping from here: it's paused at a prompt, or its work is happening in sub-agents).
        private bool Passive { get { return _awaiting || _agentsRunning; } }

        public StopGenerationItem()
            : base(new HostPanel())
        {
            _panel = (HostPanel)this.Control;

            _button = new KryptonButton();
            _button.Text = StopText;
            // The strip row is only ItemHeight tall; zero content padding so the label fits the
            // slim button without clipping.
            try { _button.StateCommon.Content.Padding = new Padding(0); }
            catch { }
            // Route the hosted control's click out through the ToolStripItem's own Click event, so
            // the form's existing tsiStopGen.Click wiring keeps working unchanged.
            _button.Click += delegate { try { this.PerformClick(); } catch { } };
            _panel.Controls.Add(_button);

            // Hosted controls eat the mouse, so the strip never shows the item's ToolTipText for
            // them; drive a plain ToolTip on the hosted controls instead.
            _tip = new ToolTip();

            this.AutoSize = false;
            UpdateStateVisuals();
        }

        // While a tool-approval (or continuation) prompt awaits the user's decision, the turn is
        // paused at the gate: there is nothing to "Stop", so this item drops its button and shows a
        // passive "awaiting user..." status label instead. The host pauses the marquee and ignores
        // clicks on this item in that state.
        public bool Awaiting
        {
            get { return _awaiting; }
            set { if (_awaiting != value) { _awaiting = value; UpdateStateVisuals(); } }
        }

        // While a dispatch_agent fan-out runs, the turn is busy but the work is in the sub-agents
        // (cancel them with the panel's Stop button); this item shows a passive "Sub-agents
        // running..." label, the host keeps the marquee running, and clicks here are ignored.
        public bool AgentsRunning
        {
            get { return _agentsRunning; }
            set { if (_agentsRunning != value) { _agentsRunning = value; UpdateStateVisuals(); } }
        }

        // Reflect the current state onto the hosted controls: which of button/label shows, the text,
        // the tooltip, and the item width (sized to the current text so nothing ever clips).
        private void UpdateStateVisuals()
        {
            string text = _awaiting ? AwaitingText : (_agentsRunning ? AgentsText : StopText);
            string tip = _awaiting ? AwaitingTip : (_agentsRunning ? AgentsTip : StopTip);

            int w;
            try { w = TextRenderer.MeasureText(text, this.Font).Width + 2 * PadX; }
            catch { w = 44; }

            _panel.Passive = Passive;
            _panel.PassiveText = text;
            _panel.Size = new Size(w, ItemHeight);

            _button.Visible = !Passive;
            _button.SetBounds(0, 0, w, ItemHeight);
            _button.Text = StopText;

            try
            {
                _tip.SetToolTip(_panel, tip);
                _tip.SetToolTip(_button, tip);
            }
            catch { }
            this.ToolTipText = tip;

            this.Size = _panel.Size;
            _panel.Invalidate();
        }

        // The strip's font can change after construction (it is inherited); keep the width fitting
        // the label so "Stop" never clips at other font sizes/DPI.
        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            UpdateStateVisuals();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _tip != null) _tip.Dispose();
            base.Dispose(disposing); // disposes the hosted panel (and with it the button)
        }

        // The hosting panel: transparent over the strip so the themed strip background shows
        // through, and the flat passive status label is drawn directly on it (in the same color
        // Krypton paints the strip's other labels) when the button is hidden.
        private sealed class HostPanel : Panel
        {
            public bool Passive;
            public string PassiveText;

            public HostPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.SupportsTransparentBackColor
                    | ControlStyles.ResizeRedraw
                    | ControlStyles.UserPaint, true);
                BackColor = Color.Transparent;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (!Passive || string.IsNullOrEmpty(PassiveText)) return;
                TextRenderer.DrawText(e.Graphics, PassiveText, this.Font, this.ClientRectangle,
                    KryptonThemeBridge.StatusStripTextColor(),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
        }
    }
}
