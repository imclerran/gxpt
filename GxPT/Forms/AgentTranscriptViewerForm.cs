using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // A transient, read-only popup that renders one child agent's full transcript (design sec.14, tier 3).
    // Opened from a dispatch_agent record's "View transcript" link. Pure UI: it shows the child's own
    // message list to the user; nothing here feeds back into any model (the context firewall, A3/A7, holds).
    // Reuses ChatTranscriptControl for themed Markdown/code rendering; the control auto-themes from settings
    // in its constructor, so the popup matches the active light/dark theme at open time. Code-only (no
    // designer) - it hosts a single docked transcript control.
    internal sealed class AgentTranscriptViewerForm : Form
    {
        private readonly ChatTranscriptControl _transcript;
        private readonly AgentTranscript _data;

        public AgentTranscriptViewerForm(AgentTranscript transcript)
        {
            _data = transcript;
            this.Text = BuildTitle(transcript);
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

        // Populate once the handle exists - the transcript control skips layout until it has a window, so
        // adding messages in the constructor would render nothing.
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _transcript.BeginBatchUpdates();
            try { Populate(_data); }
            finally { _transcript.EndBatchUpdates(false); }
            _transcript.ScrollToTop();
        }

        private static string BuildTitle(AgentTranscript t)
        {
            string slug = (t != null && !string.IsNullOrEmpty(t.Slug)) ? t.Slug : "agent";
            return "Agent transcript - " + slug;
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

        // Renders the child's message list. The leading system message is the agent's persona; the first
        // user message is the task; assistant/tool turns follow. Assistant tool calls are shown as a compact
        // Tool-role note (name + JSON args) so the run reads in order.
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
