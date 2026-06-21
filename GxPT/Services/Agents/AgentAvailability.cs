using System;
using System.Collections.Generic;

namespace GxPT
{
    // Runtime per-agent enablement: an agent is only usable when at least one of its allowlisted tools is
    // actually available in the current workspace (and within its max_tier). e.g. web-research is unusable
    // until the web tools are enabled. The host uses IsAvailable to filter the agents it offers the model,
    // and MissingTools to tell the user (status-bar tooltip) what to enable for a disabled agent. Pure logic
    // over AgentToolResolver, so it stays net48-testable.
    internal static class AgentAvailability
    {
        // True when the agent resolves to at least one usable tool given what the parent can call here.
        public static bool IsAvailable(Agent a, IEnumerable<string> parentAvailable, Func<string, ToolTier> tierOf)
        {
            if (a == null) return false;
            return AgentToolResolver.Resolve(a.Tools, a.MaxTier, parentAvailable, tierOf).Count > 0;
        }

        // The agent's declared tool patterns that no currently-available tool matches - "what to enable" for a
        // disabled agent. (a.Tools == null means "inherit read-only", so there's nothing specific to list.)
        public static List<string> MissingTools(Agent a, IEnumerable<string> parentAvailable)
        {
            List<string> missing = new List<string>();
            if (a == null || a.Tools == null) return missing;

            List<string> avail = new List<string>();
            if (parentAvailable != null)
                foreach (string n in parentAvailable)
                    if (!string.IsNullOrEmpty(n)) avail.Add(n);

            for (int i = 0; i < a.Tools.Length; i++)
            {
                string pat = a.Tools[i];
                if (string.IsNullOrEmpty(pat)) continue;
                bool any = false;
                for (int j = 0; j < avail.Count; j++)
                    if (AgentToolResolver.WildcardMatch(pat, avail[j])) { any = true; break; }
                if (!any) missing.Add(pat);
            }
            return missing;
        }
    }
}
