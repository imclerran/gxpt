namespace GxPT
{
    // Per-conversation sub-agents enablement, the agents analogue of ISkillEnablementStore (issue #119
    // facet split). The agents feature is a single on/off (design A15) - no per-agent state - so this is
    // just the conversation override: null = inherit the global default in settings.json
    // (AgentEnablement.GlobalSettingKey); true = on; false = off. Global-scope changes go straight to
    // AppSettings, not through here.
    internal interface IAgentEnablementStore
    {
        bool? GetConversationAgentsEnabled();            // null = inherit, true = on, false = off
        void SetConversationAgentsEnabled(bool? value);  // persists the active conversation
    }
}
