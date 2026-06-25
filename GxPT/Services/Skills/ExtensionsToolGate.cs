using System;
using System.Collections.Generic;

namespace GxPT
{
    // Per-turn visibility of the extensions server tools (skill + agent authoring, plus run_skill_script).
    // The rules, applied to the model's context (manifest + reveal + call):
    //   * The SKILL authoring tools are "owned" by a single meta-skill, the bundled skill-writer: hidden
    //     unless that skill is enabled (a deliberate hardcoded link, NOT a general per-skill tool gate).
    //   * The AGENT authoring tools are owned the same way by the bundled agent-writer skill: hidden
    //     unless agent-writer is enabled. (agent-writer is itself a skill, so enabling it also brings the
    //     shared server up via the usual "any enabled skill" launch rule.)
    //   * run_skill_script is hidden when NO skill is enabled at all - the server may still be running
    //     (it is shared across the workdir), but its tools have no business in a turn with no skills.
    internal static class ExtensionsToolGate
    {
        // The meta-skills that own the authoring surfaces (their slugs / folder names).
        public const string SkillWriterSlug = "skill-writer";
        public const string AgentWriterSlug = "agent-writer";

        // The execution tool - available to any skill that ships a script (not owned by a meta-skill),
        // but still hidden when no skill at all is enabled.
        public static readonly string RunScriptTool = McpConfig.ExtensionsName + "__run_skill_script";

        // Server-qualified skill-authoring/maintenance tool names gated on skill-writer (tier 1 + tier 2).
        // The whole authoring surface belongs to the meta-skill - including the ReadOnly list/validate
        // tools, which are only meaningful while writing skills.
        public static readonly string[] AuthoringTools = new string[]
        {
            McpConfig.ExtensionsName + "__create_skill",
            McpConfig.ExtensionsName + "__write_skill_file",
            McpConfig.ExtensionsName + "__update_skill",
            McpConfig.ExtensionsName + "__edit_skill_file",
            McpConfig.ExtensionsName + "__rename_skill",
            McpConfig.ExtensionsName + "__list_skill_files",
            McpConfig.ExtensionsName + "__delete_skill_file",
            McpConfig.ExtensionsName + "__delete_skill",
            McpConfig.ExtensionsName + "__validate_skill"
        };

        // Server-qualified agent-authoring tool names gated on agent-writer. As with skills, the whole
        // surface (including the ReadOnly read/list/validate tools) belongs to the meta-skill.
        public static readonly string[] AgentAuthoringTools = new string[]
        {
            McpConfig.ExtensionsName + "__create_agent",
            McpConfig.ExtensionsName + "__update_agent",
            McpConfig.ExtensionsName + "__edit_agent",
            McpConfig.ExtensionsName + "__rename_agent",
            McpConfig.ExtensionsName + "__read_agent",
            McpConfig.ExtensionsName + "__list_agents",
            McpConfig.ExtensionsName + "__delete_agent",
            McpConfig.ExtensionsName + "__validate_agent"
        };

        // The tool names to hide this turn: skill-authoring tools unless skill-writer is enabled, agent-
        // authoring tools unless agent-writer is enabled, plus run_skill_script when NO skill is enabled at
        // all (then every skills tool is hidden).
        public static ICollection<string> HiddenTools(IEnumerable<Skill> enabledSkills)
        {
            bool anyEnabled = false, writerEnabled = false, agentWriterEnabled = false;
            if (enabledSkills != null)
            {
                foreach (Skill s in enabledSkills)
                {
                    if (s == null) continue;
                    anyEnabled = true;
                    if (string.Equals(s.Slug, SkillWriterSlug, StringComparison.OrdinalIgnoreCase))
                        writerEnabled = true;
                    if (string.Equals(s.Slug, AgentWriterSlug, StringComparison.OrdinalIgnoreCase))
                        agentWriterEnabled = true;
                }
            }

            // A HashSet so the orchestrator's per-call hidden-tool lookup is O(1) (ordinal, like the
            // server-qualified names it holds).
            HashSet<string> hidden = new HashSet<string>(StringComparer.Ordinal);
            if (!writerEnabled) foreach (string t in AuthoringTools) hidden.Add(t);
            if (!agentWriterEnabled) foreach (string t in AgentAuthoringTools) hidden.Add(t);
            if (!anyEnabled) hidden.Add(RunScriptTool);
            return hidden;
        }
    }
}
