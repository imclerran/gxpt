using System.Collections.Generic;

namespace GxPT
{
    // Host side of the agents feature: owns the agents injection text, split by volatility for prompt-
    // cache reuse. The STATIC how-to (Framing) goes in the cached stable head; the DYNAMIC inventory
    // (BuildList, via McpChatOrchestrator.AgentsManifestSystemMessageProvider) goes in the ephemeral
    // tail, after the skills list and before the MCP tool list. Lists ALL discovered agents - there is no
    // per-agent enablement (design A15); the whole block is gated by the single feature toggle
    // (AgentEnablement), so when agents are off the host never sets the provider and neither the head
    // framing nor the tail list appears. XP / .NET 3.5 friendly.
    internal static class AgentInjection
    {
        // The STATIC framing: how to use agents, with no per-conversation data. Lives in the cached
        // stable head (McpChatOrchestrator.BuildStableHead) when the agents feature is on, so its tokens
        // are cached instead of re-sent in every request's ephemeral tail. The dynamic inventory (which
        // agents exist) is BuildList, kept in the tail.
        public const string Framing =
            "# Agents\n\n"
            + "Agents are specialists you can delegate a self-contained task to. Each runs in isolation "
            + "- it does not see this conversation - does the work with its own tools, and returns only "
            + "its final answer. Delegate by calling dispatch_agent with the agent's slug and a complete "
            + "task description; you may dispatch several at once. dispatch_agent is directly callable - "
            + "you do NOT need to reveal it first. Prefer delegating large or parallelizable sub-tasks "
            + "(exploration, research, verification) to keep this conversation focused. Do not mention "
            + "agents unless they are relevant to the request.";

        // The DYNAMIC inventory: the slug/description list over all discovered agents, for the ephemeral
        // tail. Returns null when there are none, so a project with no agents injects nothing.
        public static string BuildList(IList<Agent> agents)
        {
            if (agents == null || agents.Count == 0) return null;
            return "Available agents:\n" + AgentCatalog.BuildManifest(agents);
        }

        // Framing + inventory as one message. No longer used on the request path (framing is in the head,
        // list in the tail); retained as the canonical full manifest for tests and any direct caller.
        public static string BuildManifestMessage(IList<Agent> agents)
        {
            string list = BuildList(agents);
            if (list == null) return null;
            return Framing + "\n\n" + list;
        }
    }
}
