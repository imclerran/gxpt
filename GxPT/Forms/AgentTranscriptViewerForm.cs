using System;
using System.Drawing;
using System.Windows.Forms;
using Krypton.Toolkit;

namespace GxPT
{
    // A popup that renders one child agent's transcript (design sec.14, tier 3). Two modes:
    //  - static: a finished AgentTranscript (from a dispatch record's "View transcript" link), shown read-only.
    //  - live: an AgentLiveStream for a running child (the activity panel's per-row "View transcript"); the
    //    viewer attaches, replays what already happened, then streams the rest - the same rendering the main
    //    chat uses (text deltas into an assistant bubble, tool calls/results as their own rows).
    // Pure UI: the child's activity is shown to the user, never fed back to any model (the context firewall,
    // A3/A7, holds). Reuses ChatTranscriptControl, which auto-themes from settings in its constructor. Code-
    // only (no designer); hosts a single docked transcript control. KryptonForm for the themed window
    // chrome; the transcript covers the whole client, so the chrome is the only Krypton surface.
    internal sealed class AgentTranscriptViewerForm : KryptonForm, IAgentLiveSink
    {
        private ChatTranscriptControl _transcript;   // set in Init() (a ctor helper), so not readonly
        private readonly AgentTranscript _data;     // static mode (null in live mode)
        private readonly AgentLiveStream _stream;   // live mode (null in static mode)
        private bool _liveAssistantOpen;            // is the last message an open assistant bubble?
        private bool _toolBlockOpen;                // is the last message an open chrome-less tool block?

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
            // Positioned by the caller (centered on the main form) because the window is shown modeless and
            // unowned - CenterParent only applies to owned/dialog windows, so it would otherwise land at the
            // screen's default spot.
            this.StartPosition = FormStartPosition.Manual;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MinimizeBox = false;
            this.MaximizeBox = true;
            // Independent top-level window (no owner), so show it in the taskbar/Alt-Tab - that's how the
            // user gets back to it once the main form has been brought to the front.
            this.ShowInTaskbar = true;
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
            // Show the GxPT app icon in the title bar and taskbar (this window is unowned and shows
            // in Alt-Tab, so without this it carries the generic WinForms icon). Clones the
            // owner/MainForm icon, falling back to the exe's.
            try { PluginImportExportManager.ApplyOwnerIcon(this); }
            catch { }
            if (_stream != null)
            {
                // Seed the persona + task first (the streamed events are the model's output only, not the
                // seed system/user messages), then attach: replay what already happened, then stream the
                // rest. Matches the static viewer, which shows the agent's system prompt unlike the main chat.
                if (!string.IsNullOrEmpty(_stream.Persona))
                    _transcript.AddMessage(MessageRole.System, _stream.Persona);
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
                _toolBlockOpen = false;
                if (_liveAssistantOpen) _transcript.AppendToLastMessage(delta);
                else { _transcript.AddMessage(MessageRole.Assistant, delta); _liveAssistantOpen = true; }
            });
        }

        public void OnToolCall(string functionName, string argumentsJson, string callId)
        {
            string fn = functionName, args = argumentsJson, key = callId;
            Ui(delegate { AddToolMarker(MainForm.EditDiffMarkerOrCall(_transcript, fn, args, key)); });
        }

        // Tool results are hidden, to match the main chat (which never shows raw tool output).
        public void OnToolResult(string functionName, string resultText, bool isError) { }

        public void OnComplete()
        {
            Ui(delegate { _liveAssistantOpen = false; _toolBlockOpen = false; });
        }

        // Append a tool marker (a collapsible-record sentinel or a "Used <tool>" line) the way the main
        // chat does: consecutive tool calls accumulate into one chrome-less block; assistant text breaks it.
        private void AddToolMarker(string marker)
        {
            _liveAssistantOpen = false;
            if (_toolBlockOpen) _transcript.AppendToLastMessage("\r\n" + marker);
            else { _transcript.AddMessage(MessageRole.Tool, marker); _toolBlockOpen = true; }
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

        // Renders a finished message list the same way the main chat does: the persona/task as bubbles,
        // assistant text as bubbles, and each tool call as the same collapsible record (via the shared
        // EditDiffMarkerOrCall). Raw tool results (role "tool") are skipped - the main chat never shows them.
        private void Populate(AgentTranscript t)
        {
            if (t == null || t.Messages == null) { _transcript.AddMessage(MessageRole.System, "(transcript unavailable)"); return; }

            for (int i = 0; i < t.Messages.Count; i++)
            {
                ChatMessage m = t.Messages[i];
                if (m == null) continue;
                string role = m.Role ?? string.Empty;
                string content = m.Content ?? string.Empty;

                if (role == "tool") continue;   // results hidden, like the main chat

                if (role == "user")
                {
                    _transcript.AddMessage(MessageRole.User, content);
                    _liveAssistantOpen = false; _toolBlockOpen = false;
                }
                else if (role == "system")
                {
                    if (content.Length > 0)
                    {
                        _transcript.AddMessage(MessageRole.System, content);
                        _liveAssistantOpen = false; _toolBlockOpen = false;
                    }
                }
                else if (role == "assistant")
                {
                    if (content.Length > 0)
                    {
                        _transcript.AddMessage(MessageRole.Assistant, content);
                        _liveAssistantOpen = false; _toolBlockOpen = false;
                    }
                    if (m.ToolCalls != null)
                        for (int c = 0; c < m.ToolCalls.Count; c++)
                        {
                            ToolCall call = m.ToolCalls[c];
                            if (call == null) continue;
                            AddToolMarker(MainForm.EditDiffMarkerOrCall(_transcript, call.Name, call.ArgumentsJson, call.Id));
                        }
                }
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { this.Close(); e.Handled = true; }
        }
    }
}
