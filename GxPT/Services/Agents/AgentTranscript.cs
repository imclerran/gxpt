using System.Collections.Generic;

namespace GxPT
{
    // A read-only snapshot of one child agent's run: its slug, the task it was given, and the full message
    // list it produced (system persona + user task + assistant/tool turns). Captured by AgentDispatcher and
    // surfaced to the user via a transient popup viewer (design sec.14, tier 3). This is a UI-only read: the
    // child's messages are shown to the *user*, never fed back to the parent model (the context firewall,
    // A3/A7, holds - only the child's final answer crosses back). The message list is the child's own
    // history, which the dispatcher discards after the run, so this holds the only remaining reference.
    internal sealed class AgentTranscript
    {
        public readonly string Slug;
        public readonly string Task;
        public readonly IList<ChatMessage> Messages;

        public AgentTranscript(string slug, string task, IList<ChatMessage> messages)
        {
            Slug = slug;
            Task = task;
            Messages = messages;
        }

        // How many tools the child ran: each executed tool appends one "tool"-role message to the child's
        // history, so counting them gives the tool-call count shown on the dispatch record.
        public int ToolCallCount
        {
            get
            {
                int n = 0;
                if (Messages != null)
                    for (int i = 0; i < Messages.Count; i++)
                    {
                        ChatMessage m = Messages[i];
                        if (m != null && m.Role == "tool") n++;
                    }
                return n;
            }
        }
    }
}
