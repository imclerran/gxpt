using System.Collections.Generic;
using System.Text;

namespace GxPT
{
    // Host side of the skills feature (phase 2): frames the always-on skills manifest for injection as an
    // ephemeral system message (McpChatOrchestrator.SkillsManifestSystemMessageProvider), ordered right
    // after the memory block and before the MCP names manifest (design sec.5). All skills framing lives
    // here, so when no skills are present nothing is injected and the feature leaves no trace in context.
    // Root/catalog resolution lives in SkillRoots; this type owns the injection framing plus the shared
    // "is any skill enabled here" enablement helper.
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

        // The phase-2 ephemeral block: framing + the slug/description manifest, built from the skills
        // ENABLED for this conversation (SkillResolve). Returns null when the set is empty, so a
        // skill-less or all-disabled conversation injects nothing (no phantom capability).
        public static string BuildManifestMessage(IList<Skill> enabledSkills)
        {
            if (enabledSkills == null || enabledSkills.Count == 0) return null;

            StringBuilder sb = new StringBuilder();
            sb.Append("# Skills\n\n");
            sb.Append("Skills are reusable procedures available for this conversation, listed below as ");
            sb.Append("`- <slug> [<scope>] - <description>`. When a task matches one, load its full ");
            sb.Append("instructions by calling open_skill({\"names\":[\"<slug>\"]}) and then follow them. ");
            sb.Append("open_skill is directly callable - you do NOT need to reveal it first - and you may ");
            sb.Append("open several skills at once. The <scope> is where the skill lives (user or project; ");
            sb.Append("bundled skills are read-only); pass it as the `scope` argument when editing a skill ");
            sb.Append("so the edit targets the right one. Do not mention skills unless they are relevant ");
            sb.Append("to the request.\n\n");
            sb.Append("Available skills:\n");
            sb.Append(SkillCatalog.BuildManifest(enabledSkills));
            return sb.ToString();
        }
    }
}
