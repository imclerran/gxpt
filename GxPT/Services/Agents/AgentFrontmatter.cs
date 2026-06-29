using System;
using System.Collections.Generic;
using System.Text;

namespace GxPT
{
    // Hand-rolled reader for an agent <slug>.md's leading "--- ... ---" frontmatter block (design A5:
    // net35 has no YAML parser and the repo keeps one JSON lib, so we parse the handful of "key: value"
    // lines ourselves). It is the SkillFrontmatter reader extended for the agent contract: besides
    // `name`/`description` it reads `tools` (an inline list), `max_tier` (an enum, with a default),
    // `model`, and `effort` (low|medium|high, a capability tier mapped to a model in settings). Unknown
    // keys are ignored (forward-compatible) and each known key is
    // first-wins. Everything after the closing delimiter is the body (the agent's system prompt).
    // Lenient: a missing or unterminated block yields no frontmatter and treats the whole text as body;
    // an unrecognized enum value falls back to the default rather than rejecting the agent.
    internal sealed class AgentFrontmatter
    {
        private const char Bom = '\uFEFF';

        public string Name { get; private set; }
        public string Description { get; private set; }

        // Null when `tools:` was absent; a (possibly empty) array when present. See Agent.Tools.
        public string[] Tools { get; private set; }

        public AgentMaxTier MaxTier { get; private set; }
        public string Model { get; private set; }

        // `effort` (low|medium|high); Unset when absent or unrecognized. See Agent.Effort.
        public AgentEffort Effort { get; private set; }

        // Per-agent iteration budget (design A17); 0 => unset (host default). Negative/non-numeric ignored.
        public int MaxTurns { get; private set; }

        public string Body { get; private set; }
        public bool HasFrontmatter { get; private set; }

        private AgentFrontmatter()
        {
            Body = string.Empty;
            Tools = null;
            MaxTier = AgentMaxTier.Write;     // default ceiling (design A5/sec.3)
            Effort = AgentEffort.Unset;       // no effort hint unless declared
            MaxTurns = 0;                     // unset
        }

        public static AgentFrontmatter Parse(string text)
        {
            AgentFrontmatter fm = new AgentFrontmatter();
            if (string.IsNullOrEmpty(text)) return fm;

            string s = text;
            if (s.Length > 0 && s[0] == Bom) s = s.Substring(1);   // strip a UTF-8 BOM

            // Normalize line endings so the delimiter scan is CRLF/CR/LF-agnostic.
            string[] lines = s.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            int i = 0;
            while (i < lines.Length && lines[i].Trim().Length == 0) i++;   // skip leading blank lines

            if (i >= lines.Length || lines[i].Trim() != "---")
            {
                fm.Body = s.Trim();   // no opening delimiter: the whole text is the body
                return fm;
            }

            int start = i + 1;
            int close = -1;
            for (int j = start; j < lines.Length; j++)
            {
                if (lines[j].Trim() == "---") { close = j; break; }
            }
            if (close < 0)
            {
                fm.Body = s.Trim();   // unterminated frontmatter: treat the whole thing as body
                return fm;
            }

            fm.HasFrontmatter = true;
            bool toolsSet = false, tierSet = false, effortSet = false;
            for (int j = start; j < close; j++)
            {
                string line = lines[j];
                if (line.Trim().Length == 0) continue;
                int colon = line.IndexOf(':');
                if (colon < 0) continue;

                string key = line.Substring(0, colon).Trim().ToLowerInvariant();
                string value = line.Substring(colon + 1).Trim();

                // First wins, so a stray duplicate key can't clobber the intended value.
                if (key == "name") { if (fm.Name == null) fm.Name = value; }
                else if (key == "description") { if (fm.Description == null) fm.Description = value; }
                else if (key == "model") { if (fm.Model == null && value.Length > 0) fm.Model = value; }
                else if (key == "tools")
                {
                    if (!toolsSet) { fm.Tools = ParseToolList(value); toolsSet = true; }
                }
                else if (key == "max_tier")
                {
                    AgentMaxTier tier;
                    if (!tierSet && TryParseMaxTier(value, out tier)) { fm.MaxTier = tier; tierSet = true; }
                }
                else if (key == "effort")
                {
                    AgentEffort effort;
                    if (!effortSet && TryParseEffort(value, out effort)) { fm.Effort = effort; effortSet = true; }
                }
                else if (key == "max_turns")
                {
                    int n;
                    if (fm.MaxTurns == 0 && int.TryParse(value.Trim(), out n) && n > 0) fm.MaxTurns = n;
                }
            }

            StringBuilder body = new StringBuilder();
            for (int j = close + 1; j < lines.Length; j++)
            {
                if (body.Length > 0) body.Append('\n');
                body.Append(lines[j]);
            }
            fm.Body = body.ToString().Trim();
            return fm;
        }

        // Parses an inline `tools` value into an array. Null/blank => null (key absent or empty == "not
        // specified"). A bracketed `[a, b]` or a bare `a, b` both split on commas; `[]` => an empty
        // array (an explicit "no tools"). Surrounding quotes on a token are stripped. Wildcards (*,
        // files__*) are kept verbatim - they are expanded against the catalog later (AgentToolResolver).
        internal static string[] ParseToolList(string value)
        {
            if (value == null) return null;
            string v = value.Trim();
            if (v.Length == 0) return null;

            if (v[0] == '[')
            {
                int end = v.LastIndexOf(']');
                v = end >= 1 ? v.Substring(1, end - 1) : v.Substring(1);   // tolerate a missing ']'
            }

            string[] parts = v.Split(',');
            List<string> list = new List<string>();
            for (int k = 0; k < parts.Length; k++)
            {
                string t = parts[k].Trim();
                if (t.Length >= 2 && (t[0] == '"' || t[0] == '\'') && t[t.Length - 1] == t[0])
                    t = t.Substring(1, t.Length - 2).Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list.ToArray();
        }

        // readonly | write | destructive (a few spelling variants tolerated). Returns false for an
        // unrecognized value, so the caller keeps the default.
        internal static bool TryParseMaxTier(string value, out AgentMaxTier tier)
        {
            tier = AgentMaxTier.Write;
            if (value == null) return false;
            switch (value.Trim().ToLowerInvariant())
            {
                case "readonly":
                case "read-only":
                case "read_only":
                    tier = AgentMaxTier.ReadOnly; return true;
                case "write":
                    tier = AgentMaxTier.Write; return true;
                case "destructive":
                    tier = AgentMaxTier.Destructive; return true;
                default:
                    return false;
            }
        }

        // low | medium | high. Returns false for a blank/unrecognized value, so the caller keeps Unset
        // (no effort hint) rather than rejecting the agent.
        internal static bool TryParseEffort(string value, out AgentEffort effort)
        {
            effort = AgentEffort.Unset;
            if (value == null) return false;
            switch (value.Trim().ToLowerInvariant())
            {
                case "low":
                    effort = AgentEffort.Low; return true;
                case "medium":
                case "med":
                    effort = AgentEffort.Medium; return true;
                case "high":
                    effort = AgentEffort.High; return true;
                default:
                    return false;
            }
        }
    }
}
