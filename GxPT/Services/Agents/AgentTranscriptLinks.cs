using System;
using System.Globalization;

namespace GxPT
{
    // The custom link scheme that ties a dispatch_agent record's "View transcript" link to a cached child
    // transcript (design sec.14, tier 3). The record body embeds Build(key, slot) as a Markdown link URL;
    // the transcript control recognizes the scheme and, instead of launching a browser, opens the read-only
    // viewer for AgentTranscriptStore.Get(key, slot). Pure string logic, kept out of the WinForms layer so
    // it is unit-testable. The key is the dispatch record's id (a GUID hex or provider call id); the slot is
    // the agent's entry index. Split on the LAST ':' so a key that itself contains ':' still parses.
    internal static class AgentTranscriptLinks
    {
        public const string Scheme = "gxpt-agent:";

        public static string Build(string key, int slot)
        {
            return Scheme + (key ?? string.Empty) + ":" + slot.ToString(CultureInfo.InvariantCulture);
        }

        public static bool IsTranscriptLink(string url)
        {
            return !string.IsNullOrEmpty(url) && url.StartsWith(Scheme, StringComparison.Ordinal);
        }

        public static bool TryParse(string url, out string key, out int slot)
        {
            key = null;
            slot = -1;
            if (!IsTranscriptLink(url)) return false;
            string rest = url.Substring(Scheme.Length);
            int colon = rest.LastIndexOf(':');
            if (colon <= 0 || colon >= rest.Length - 1) return false;
            int s;
            if (!int.TryParse(rest.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out s))
                return false;
            key = rest.Substring(0, colon);
            slot = s;
            return true;
        }
    }
}
