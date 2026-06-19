namespace GxPT
{
    // The single agents feature toggle (design A15/sec.7): a per-conversation override layered over a
    // global default stored in settings.json (`agents_enabled`, default false). No per-agent state. The
    // resolution is pure (FeatureEnabled(bool?, bool)); the AppSettings-backed overloads are host
    // conveniences for the send path and a future General settings-page checkbox. XP / .NET 3.5 friendly.
    internal static class AgentEnablement
    {
        // The settings.json key the global default lives under (read/written via AppSettings, the same
        // mechanism as the memory toggle's "mcp_memory_enabled"). Lives in settings.json - not a dedicated
        // agents.json - so a future settings-page checkbox binds to the same value.
        public const string GlobalSettingKey = "agents_enabled";
        public const bool GlobalDefault = false;   // agents lead OFF (they spawn loops + cost)

        // Effective on/off (pure): the conversation override wins when set; otherwise the global default.
        public static bool FeatureEnabled(bool? conversationOverride, bool globalDefault)
        {
            return conversationOverride.HasValue ? conversationOverride.Value : globalDefault;
        }

        // The global default from settings.json (host-side).
        public static bool GlobalEnabled()
        {
            return AppSettings.GetBool(GlobalSettingKey, GlobalDefault);
        }

        // Effective on/off using the live global default (host-side send path).
        public static bool FeatureEnabled(bool? conversationOverride)
        {
            return FeatureEnabled(conversationOverride, GlobalEnabled());
        }
    }
}
