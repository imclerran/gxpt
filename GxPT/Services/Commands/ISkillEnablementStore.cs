using System.Collections.Generic;

namespace GxPT
{
    // Per-conversation skills enablement, split out of ISlashCommandContext (issue #119) so the skills
    // commands depend only on the skills surface rather than the whole host facade. Tri-state overrides
    // (null = inherit the global default in skills.json); global-scope changes go straight to
    // SkillEnablement, not through here.
    internal interface ISkillEnablementStore
    {
        bool? GetConversationSkillsFeatureOff();            // null = inherit, true = off, false = on
        void SetConversationSkillsFeatureOff(bool? value);  // persists the active conversation
        IDictionary<string, bool> GetConversationSkillOverrides(); // copy; slug -> force on/off
        void SetConversationSkillOverride(string slug, bool? value); // null clears the slug; persists
        void ResetConversationSkills();                     // clear feature override + all per-skill overrides

        // Bring the Skills MCP server into line with skill enablement (it runs iff any skill is enabled).
        // Call after any skills enablement change - global or per-conversation. A no-op unless the change
        // crosses the on/off boundary.
        void RefreshSkillsServer();
    }
}
