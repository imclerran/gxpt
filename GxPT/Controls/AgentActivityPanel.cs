using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace GxPT
{
    // A native panel docked at the bottom of the chat area that shows the sub-agents running during a
    // dispatch_agent fan-out (design sec.14). Transient: shown on BeginFanOut, updated per child, hidden
    // on EndFanOut. Display-only - a child's activity is shown to the user here, never fed to the model
    // (A7). All public methods must be called on the UI thread; the dispatcher's IAgentActivityUi callbacks
    // are marshaled there by the host (AgentActivityUiBridge). Kept deliberately simple (one multi-line
    // label) - the live tool-by-tool detail is a later enhancement.
    internal sealed class AgentActivityPanel : Panel
    {
        private const int StateQueued = 0;
        private const int StateRunning = 1;
        private const int StateDone = 2;

        private readonly Label _label;
        private string[] _slugs;
        private int[] _state;

        public AgentActivityPanel()
        {
            this.Dock = DockStyle.Bottom;
            this.Visible = false;
            this.AutoSize = false;
            this.Height = 24;
            this.Padding = new Padding(8, 4, 8, 4);

            _label = new Label();
            _label.Dock = DockStyle.Fill;
            _label.AutoSize = false;
            _label.TextAlign = ContentAlignment.TopLeft;
            this.Controls.Add(_label);
        }

        // Start showing a fan-out of the given agents (in dispatch order), all queued.
        public void BeginFanOut(System.Collections.Generic.IList<string> slugs)
        {
            int n = slugs != null ? slugs.Count : 0;
            _slugs = new string[n];
            _state = new int[n];
            for (int i = 0; i < n; i++) { _slugs[i] = slugs[i]; _state[i] = StateQueued; }
            Render();
            this.Visible = n > 0;
        }

        public void SetRunning(int index) { SetState(index, StateRunning); }
        public void SetDone(int index) { SetState(index, StateDone); }

        public void EndFanOut()
        {
            this.Visible = false;
            _slugs = null;
            _state = null;
        }

        private void SetState(int index, int state)
        {
            if (_state == null || index < 0 || index >= _state.Length) return;
            _state[index] = state;
            Render();
        }

        private void Render()
        {
            if (_slugs == null || _slugs.Length == 0) { _label.Text = string.Empty; return; }

            int done = 0, running = 0;
            for (int i = 0; i < _state.Length; i++)
            {
                if (_state[i] == StateDone) done++;
                else if (_state[i] == StateRunning) running++;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("Sub-agents: ").Append(running).Append(" running, ")
              .Append(done).Append(" of ").Append(_slugs.Length).Append(" done");
            for (int i = 0; i < _slugs.Length; i++)
            {
                string tag = _state[i] == StateDone ? "[done]"
                    : (_state[i] == StateRunning ? "[running]" : "[queued]");
                sb.Append("\r\n   ").Append(_slugs[i]).Append("  ").Append(tag);
            }
            _label.Text = sb.ToString();

            // Header line + one line per agent, bounded so a big fan-out doesn't take the whole pane.
            int lines = 1 + _slugs.Length;
            this.Height = Math.Min(10 + lines * 16, 160);
        }
    }
}
