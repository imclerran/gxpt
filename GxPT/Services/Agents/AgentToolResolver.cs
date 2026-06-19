using System;
using System.Collections.Generic;

namespace GxPT
{
    // Resolves the effective tool set a dispatched sub-agent may call: the frontmatter allowlist,
    // intersected with what the PARENT can actually call in this workspace, and capped at the agent's
    // max_tier ceiling (design A11/A19 - the no-escalation core). Pure logic: the per-tool tier is
    // supplied by an injected delegate (the host wires it to ToolClassifier), so this stays
    // net48-testable with no registry/classifier dependency. XP / .NET 3.5 friendly.
    internal static class AgentToolResolver
    {
        // The no-nesting tool: a child never gets dispatch_agent, even if "*" would match it (design A12).
        // Kept here so the resolver itself guarantees the exclusion; the dispatcher reuses the same name.
        public const string DispatchAgentToolName = "dispatch_agent";

        // Returns the subset of parentAvailable the agent may call - in parentAvailable's order, de-duped,
        // never exceeding the parent's reach or the tier ceiling.
        //   agentTools      the frontmatter `tools` allowlist (glob patterns); null => inherit the
        //                   conservative ReadOnly-only default (design A5 fail-safe); a "*" entry => the
        //                   full parent-available set (still tier-capped).
        //   maxTier         the agent's ceiling; tools classified above it are dropped.
        //   parentAvailable server-qualified tool names the parent can call in this workspace.
        //   tierOf          maps a tool name to its tier (host: ToolClassifier). Null => treat every tool
        //                   as Write (cautious: never silently grants a tool a ReadOnly-only agent couldn't
        //                   reach, and never hides a Destructive one).
        public static List<string> Resolve(string[] agentTools, AgentMaxTier maxTier,
                                            IEnumerable<string> parentAvailable,
                                            Func<string, ToolTier> tierOf)
        {
            List<string> result = new List<string>();
            if (parentAvailable == null) return result;

            ToolTier ceiling = ToCeiling(maxTier);
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (string name in parentAvailable)
            {
                if (string.IsNullOrEmpty(name) || seen.ContainsKey(name)) continue;
                seen[name] = true;

                // No nesting: a child can never reach dispatch_agent, regardless of allowlist (design A12).
                if (string.Equals(name, DispatchAgentToolName, StringComparison.OrdinalIgnoreCase)) continue;

                ToolTier tier = tierOf != null ? tierOf(name) : ToolTier.Write;
                if ((int)tier > (int)ceiling) continue;   // tier ceiling

                bool allowed = (agentTools == null)
                    ? (tier == ToolTier.ReadOnly)         // inherit: ReadOnly-only conservative default
                    : MatchesAny(agentTools, name);       // explicit allowlist (globs)

                if (allowed) result.Add(name);
            }
            return result;
        }

        // The tools the parent can call but this agent cannot - what the dispatcher feeds to the child's
        // HiddenToolNames so a directly-named out-of-set tool is refused (design A11). Order and de-dup
        // follow parentAvailable.
        public static List<string> Hidden(string[] agentTools, AgentMaxTier maxTier,
                                           IEnumerable<string> parentAvailable,
                                           Func<string, ToolTier> tierOf)
        {
            List<string> hidden = new List<string>();
            if (parentAvailable == null) return hidden;

            List<string> effective = Resolve(agentTools, maxTier, parentAvailable, tierOf);
            Dictionary<string, bool> keep = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < effective.Count; i++) keep[effective[i]] = true;

            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in parentAvailable)
            {
                if (string.IsNullOrEmpty(name) || seen.ContainsKey(name)) continue;
                seen[name] = true;
                if (!keep.ContainsKey(name)) hidden.Add(name);
            }
            return hidden;
        }

        private static ToolTier ToCeiling(AgentMaxTier maxTier)
        {
            switch (maxTier)
            {
                case AgentMaxTier.ReadOnly: return ToolTier.ReadOnly;
                case AgentMaxTier.Destructive: return ToolTier.Destructive;
                default: return ToolTier.Write;
            }
        }

        internal static bool MatchesAny(string[] patterns, string name)
        {
            if (patterns == null) return false;
            for (int i = 0; i < patterns.Length; i++)
                if (WildcardMatch(patterns[i], name)) return true;
            return false;
        }

        // Glob match with '*' as the only wildcard (matches any run, including empty), case-insensitive.
        // "*" => everything; "files__*" => prefix; "*__read" => suffix; "a*b*c" => ordered segments.
        // A pattern with no '*' is an exact (case-insensitive) match.
        internal static bool WildcardMatch(string pattern, string name)
        {
            if (pattern == null || name == null) return false;
            string p = pattern.Trim();
            if (p.Length == 0) return false;
            if (p == "*") return true;
            if (p.IndexOf('*') < 0)
                return string.Equals(p, name, StringComparison.OrdinalIgnoreCase);

            string[] segs = p.Split('*');
            int pos = 0;
            for (int i = 0; i < segs.Length; i++)
            {
                string seg = segs[i];
                if (seg.Length == 0) continue;   // adjacent or edge '*'

                if (i == 0)
                {
                    if (!name.StartsWith(seg, StringComparison.OrdinalIgnoreCase)) return false;
                    pos = seg.Length;
                }
                else if (i == segs.Length - 1)
                {
                    if (!name.EndsWith(seg, StringComparison.OrdinalIgnoreCase)) return false;
                    if (name.Length - seg.Length < pos) return false;   // must fall after consumed prefix
                }
                else
                {
                    int idx = name.IndexOf(seg, pos, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) return false;
                    pos = idx + seg.Length;
                }
            }
            return true;
        }
    }
}
