namespace GxPT
{
    // Shared parsing for slash-command arguments, so the same vocabulary isn't maintained in two places
    // (e.g. /toggle-tool on|off and /toggle-skill on|off both recognize the same on/off words).
    internal static class SlashArgs
    {
        // Recognizes an on/off word: true for on/true/1/yes/enable/enabled, false for the off variants,
        // null for anything else (let the caller decide what an unrecognized token means).
        public static bool? ParseOnOff(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            string t = token.Trim().ToLowerInvariant();
            if (t == "on" || t == "true" || t == "1" || t == "yes" || t == "enable" || t == "enabled") return true;
            if (t == "off" || t == "false" || t == "0" || t == "no" || t == "disable" || t == "disabled") return false;
            return null;
        }
    }
}
