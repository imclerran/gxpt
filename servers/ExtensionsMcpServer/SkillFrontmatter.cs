using System.Text;

namespace ExtensionsMcpServer
{
    // Reads a SKILL.md's leading "--- ... ---" frontmatter (name/description) + body, so update_skill can
    // do partial-field edits. A standalone reader: servers don't reference the host assembly, so this is an
    // independent reimplementation of the same parse, NOT a line-for-line copy - it deliberately omits the
    // host's HasFrontmatter flag and read-only properties, which the writer doesn't need (it only consumes
    // Name/Description/Body). The parse rules are intended to match; the host owns the authoritative reader
    // and its tests. Lenient: a missing/unterminated block yields no frontmatter and the whole text as body.
    internal sealed class SkillFrontmatter
    {
        private const char Bom = '\uFEFF';

        public string Name;
        public string Description;
        public string Body;

        public static SkillFrontmatter Parse(string text)
        {
            SkillFrontmatter fm = new SkillFrontmatter();
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
                if (key == "name" && fm.Name == null) fm.Name = value;
                else if (key == "description" && fm.Description == null) fm.Description = value;
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
