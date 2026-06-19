using System.Collections.Generic;
using System.Text;

namespace GxPT
{
    // Host side of the agents feature: frames the always-on agents manifest for injection as an ephemeral
    // system message (McpChatOrchestrator.AgentsManifestSystemMessageProvider), ordered after the skills
    // block and before the MCP names manifest (design sec.5). Lists ALL discovered agents - there is no
    // per-agent enablement (design A15); the whole block is gated by the single feature toggle
    // (AgentEnablement), so when agents are off the host never sets the provider and the feature leaves no
    // trace in context. XP / .NET 3.5 friendly.
    internal static class AgentInjection
    {
        // The ephemeral block: framing + the slug/description manifest over all discovered agents. Returns
        // null when there are none, so a project with no agents injects nothing even when the feature is on.
        public static string BuildManifestMessage(IList<Agent> agents)
        {
            if (agents == null || agents.Count == 0) return null;

            StringBuilder sb = new StringBuilder();
            sb.Append("# Agents\n\n");
            sb.Append("Agents are specialists you can delegate a self-contained task to. Each runs in ");
            sb.Append("isolation - it does not see this conversation - does the work with its own tools, ");
            sb.Append("and returns only its final answer. Delegate by calling dispatch_agent with the ");
            sb.Append("agent's slug and a complete task description; you may dispatch several at once. ");
            sb.Append("dispatch_agent is directly callable - you do NOT need to reveal it first. Prefer ");
            sb.Append("delegating large or parallelizable sub-tasks (exploration, research, verification) ");
            sb.Append("to keep this conversation focused. Do not mention agents unless they are relevant ");
            sb.Append("to the request.\n\n");
            sb.Append("Available agents:\n");
            sb.Append(AgentCatalog.BuildManifest(agents));
            return sb.ToString();
        }
    }
}
