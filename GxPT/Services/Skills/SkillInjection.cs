using System.Collections.Generic;

namespace GxPT
{
    // Host side of the skills feature: owns the skills injection text, split by volatility for prompt-
    // cache reuse. The STATIC how-to (Framing) goes in the cached stable head; the DYNAMIC inventory
    // (BuildList, via McpChatOrchestrator.SkillsManifestSystemMessageProvider) goes in the ephemeral
    // tail, ordered after memory and before the MCP tool list. When no skills are enabled, BuildList
    // returns null and the head framing is omitted, so the feature leaves no trace in context. Root/
    // catalog resolution lives in SkillRoots; this type also owns the shared "is any skill enabled here"
    // enablement helper.
    internal static class SkillInjection
    {
        // True when the conversation (its feature/skill overrides) has at least one enabled skill for the
        // given roots: build the catalog, apply the global default + conversation overrides, count > 0.
        // The single shared "is any skill enabled here" check - used by the send-path gate and by the
        // Skills MCP server's enablement - so the build-catalog/resolve computation lives in one place.
        public static bool HasAnyEnabledSkills(string exeDir, string workingDir,
            bool? convFeatureOff, IDictionary<string, bool> convOverrides)
        {
            SkillCatalog cat = SkillRoots.BuildCatalog(exeDir, workingDir);
            return SkillResolve.EnabledSkills(
                cat.Skills, SkillEnablement.LoadGlobal(), convFeatureOff, convOverrides).Count > 0;
        }

        // The STATIC framing: how to use skills, with no per-conversation data. Lives in the cached
        // stable head (McpChatOrchestrator.BuildStableHead) when skills are enabled, so its tokens are
        // cached instead of re-sent in every request's ephemeral tail. The sequencing rule ("read a
        // skill before acting on the task") lives in the always-on agent system prompt, so it is not
        // repeated here. The dynamic inventory (which skills exist) is BuildList, kept in the tail.
        public const string Framing =
            "# Skills\n\n"
            + "Skills are reusable procedures available for this conversation; the enabled ones are "
            + "listed in the host context as `- <slug> [<scope>] - <description>`. When a task matches "
            + "one, open it with open_skill({\"names\":[\"<slug>\"]}) and follow its instructions. "
            + "open_skill is directly callable - you do NOT need to reveal it first - and you may open "
            + "several at once. The <scope> is where the skill lives (user or project; bundled skills are "
            + "read-only); pass it as the `scope` argument when editing a skill so the edit targets the "
            + "right one. Do not mention skills unless they are relevant to the request.";

        // The DYNAMIC inventory: the slug/scope/description list, for the ephemeral tail. Returns null
        // when the enabled set is empty, so a skill-less or all-disabled conversation injects nothing.
        public static string BuildList(IList<Skill> enabledSkills)
        {
            if (enabledSkills == null || enabledSkills.Count == 0) return null;
            return "Available skills:\n" + SkillCatalog.BuildManifest(enabledSkills);
        }

        // Framing + inventory as one message. No longer used on the request path (framing is in the head,
        // list in the tail); retained as the canonical full manifest for tests and any direct caller.
        public static string BuildManifestMessage(IList<Skill> enabledSkills)
        {
            string list = BuildList(enabledSkills);
            if (list == null) return null;
            return Framing + "\n\n" + list;
        }
    }
}
