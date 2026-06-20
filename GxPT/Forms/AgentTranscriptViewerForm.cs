using System;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // A popup that renders one child agent's transcript (design sec.14, tier 3). Two modes:
    //  - static: a finished AgentTranscript (from a dispatch record's "View transcript" link), shown read-only.
    //  - live: an AgentLiveStream for a running child (the activity panel's per-row "View transcript"); the
    //    viewer attaches, replays what already happened, then streams the rest - the same rendering the main
    //    chat uses (text deltas into an assistant bubble, tool calls/results as their own rows).
    // Pure UI: the child's activity is shown to the user, never fed back to any model (the context firewall,
    // A3/A7, holds). Reuses ChatTranscriptControl, which auto-themes from settings in its constructor. Code-
    // only (no designer); hosts a single docked transcript control.
    internal sealed class AgentTranscriptViewerForm : Form, IAgentLiveSink
    {
        private readonly ChatTranscriptControl _transcript;
        private readonly AgentTranscript _data;     // static mode (null in live mode)
        private readonly AgentLiveStream _stream;   // live mode (null in static mode)
        private bool _liveAssistantOpen;            // is the last live message an open assistant bubble?

        public AgentTranscriptViewerForm(AgentTranscript transcript)
        {
            _data = transcript;
            Init(BuildTitle(transcript != null ? transcript.Slug : null));
        }

        public AgentTranscriptViewerForm(AgentLiveStream stream)
        {
            _stream = stream;
            Init(BuildTitle(stream != null ? stream.Slug : null) + " (live)");
        }

        private void Init(string title)
        {
            this.Text = title;
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MinimizeBox = false;
            this.MaximizeBox = true;
            this.ShowInTaskbar = false;
            this.ClientSize = new Size(720, 560);
            this.MinimumSize = new Size(360, 240);
            this.KeyPreview = true;

            _transcript = new ChatTranscriptControl();
            _transcript.Dock = DockStyle.Fill;
            this.Controls.Add(_transcript);

            ApplyFontSetting();
        }

        // Populate once the handle exists - the transcript control skips layout until it has a window.
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (_stream != null)
            {
                // Show the task first (the streamed events are the model's output only, not the seed
                // persona/task), then attach: replay what already happened, then stream the rest.
                if (!string.IsNullOrEmpty(_stream.Task))
                    _transcript.AddMessage(MessageRole.User, _stream.Task);
                _stream.Attach(this);
            }
            else
            {
                _transcript.BeginBatchUpdates();
                try { Populate(_data); }
                finally { _transcript.EndBatchUpdates(false); }
                _transcript.ScrollToTop();
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_stream != null) _stream.Detach(this);
            base.OnFormClosed(e);
        }

        // ---------- IAgentLiveSink (called from a worker thread or, during replay, this one) ----------
        public void OnText(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            Ui(delegate
            {
                if (_liveAssistantOpen) _transcript.AppendToLastMessage(delta);
                else { _transcript.AddMessage(MessageRole.Assistant, delta); _liveAssistantOpen = true; }
            });
        }

        public void OnToolCall(string functionName, string callId)
        {
            string fn = !string.IsNullOrEmpty(functionName) ? functionName : "(tool)";
            Ui(delegate { _liveAssistantOpen = false; _transcript.AddMessage(MessageRole.Tool, "Called `" + fn + "`"); });
        }

        public void OnToolResult(string functionName, string resultText, bool isError)
        {
            string res = resultText ?? string.Empty;
            Ui(delegate { _liveAssistantOpen = false; _transcript.AddMessage(MessageRole.Tool, res); });
        }

        public void OnComplete()
        {
            Ui(delegate { _liveAssistantOpen = false; });
        }

        // Marshal an action onto the UI thread (sink callbacks may arrive on the child's worker thread; even
        // the replay is deferred so nothing renders under the stream's lock).
        private void Ui(MethodInvoker a)
        {
            try { if (this.IsHandleCreated && !this.IsDisposed) this.BeginInvoke(a); }
            catch { }
        }

        private static string BuildTitle(string slug)
        {
            return "Agent transcript - " + (!string.IsNullOrEmpty(slug) ? slug : "agent");
        }

        private void ApplyFontSetting()
        {
            try
            {
                double fs = AppSettings.GetDouble("font_size", 0);
                if (fs <= 0 || _transcript == null) return;
                float size = (float)Math.Max(6, Math.Min(48, fs));
                _transcript.Font = new Font(_transcript.Font.FontFamily, size, _transcript.Font.Style);
            }
            catch { }
        }

        // Renders a finished message list. Leading system message is the persona; the first user message is
        // the task; assistant/tool turns follow. Assistant tool calls are shown as a compact Tool-role note.
        private void Populate(AgentTranscript t)
        {
            if (t == null || t.Messages == null) { _transcript.AddMessage(MessageRole.System, "(transcript unavailable)"); return; }

            for (int i = 0; i < t.Messages.Count; i++)
            {
                ChatMessage m = t.Messages[i];
                if (m == null) continue;
                string role = m.Role ?? string.Empty;
                string content = m.Content ?? string.Empty;

                if (role == "user")
                {
                    _transcript.AddMessage(MessageRole.User, content);
                }
                else if (role == "system")
                {
                    if (content.Length > 0) _transcript.AddMessage(MessageRole.System, content);
                }
                else if (role == "tool")
                {
                    _transcript.AddMessage(MessageRole.Tool, content);
                }
                else if (role == "assistant")
                {
                    if (content.Length > 0)
                        _transcript.AddMessage(MessageRole.Assistant, content);
                    if (m.ToolCalls != null)
                    {
                        for (int c = 0; c < m.ToolCalls.Count; c++)
                            _transcript.AddMessage(MessageRole.Tool, FormatToolCall(m.ToolCalls[c]));
                    }
                }
                else if (content.Length > 0)
                {
                    _transcript.AddMessage(MessageRole.Tool, content);
                }
            }
        }

        private static string FormatToolCall(ToolCall call)
        {
            if (call == null) return "(tool call)";
            string name = !string.IsNullOrEmpty(call.Name) ? call.Name : "(tool)";
            string args = call.ArgumentsJson != null ? call.ArgumentsJson.Trim() : string.Empty;
            string head = "Called `" + name + "`";
            if (args.Length == 0 || args == "{}") return head;
            return head + "\n```json\n" + args + "\n```";
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { this.Close(); e.Handled = true; }
        }
    }
}
