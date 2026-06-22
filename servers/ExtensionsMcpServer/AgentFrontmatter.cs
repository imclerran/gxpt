using System.Text;

namespace ExtensionsMcpServer
{
    // Reads an agent <slug>.md's leading "--- ... ---" frontmatter + body so update_agent can do
    // partial-field edits. A standalone reader: servers don't reference the host assembly, so this is an
    // independent reimplementation of the same parse the host's AgentFrontmatter does (NOT a line-for-line
    // copy). It keeps each declared value as the RAW text after the "key:" so the writer can re-emit an
    // unchanged field verbatim; the host owns the authoritative typed reader and its tests. Lenient: a
    // missing/unterminated block yields no frontmatter and treats the whole text as body.
    internal sealed class AgentFrontmatter
    {
        private const char Bom = '\uFEFF';

        public string Name;
        public string Description;
        public string ToolsRaw;     // raw value after "tools:", or null when the key was absent
        public string MaxTierRaw;   // raw value after "max_tier:", or null
        public string ModelRaw;     // raw value after "model:", or null
        public string MaxTurnsRaw;  // raw value after "max_turns:", or null
        public string Body;

        public static AgentFrontmatter Parse(string text)
        {
            AgentFrontmatter fm = new AgentFrontmatter();
            fm.Body = string.Empty;
            if (string.IsNullOrEmpty(text)) return fm;

            string s = text;
            if (s.Length > 0 && s[0] == Bom) s = s.Substring(1);
            string[] lines = s.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            int i = 0;
            while (i < lines.Length && lines[i].Trim().Length == 0) i++;
            if (i >= lines.Length || lines[i].Trim() != "---")
            {
                fm.Body = s.Trim();
                return fm;
            }

            int start = i + 1;
            int close = -1;
            for (int j = start; j < lines.Length; j++)
            {
                if (lines[j].Trim() == "---") { close = j; break; }
            }
            if (close < 0) { fm.Body = s.Trim(); return fm; }

            for (int j = start; j < close; j++)
            {
                string line = lines[j];
                if (line.Trim().Length == 0) continue;
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string key = line.Substring(0, colon).Trim().ToLowerInvariant();
                string value = line.Substring(colon + 1).Trim();
                // First wins, so a stray duplicate key can't clobber the intended value.
                if (key == "name" && fm.Name == null) fm.Name = value;
                else if (key == "description" && fm.Description == null) fm.Description = value;
                else if (key == "tools" && fm.ToolsRaw == null) fm.ToolsRaw = value;
                else if (key == "max_tier" && fm.MaxTierRaw == null) fm.MaxTierRaw = value;
                else if (key == "model" && fm.ModelRaw == null) fm.ModelRaw = value;
                else if (key == "max_turns" && fm.MaxTurnsRaw == null) fm.MaxTurnsRaw = value;
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
    }
}
