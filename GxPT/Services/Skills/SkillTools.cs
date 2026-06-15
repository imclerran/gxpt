using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mcp35.Core.Security;
using Newtonsoft.Json.Linq;

namespace GxPT
{
    // The skills execution-surface meta-tool (phase 3): open_skill. Host-synthesized and handled inside
    // the orchestrator without an MCP round-trip - the skills analogue of reveal_tools (design S1/S7).
    // open_skill loads a skill's full SKILL.md body on demand, plus the resolved on-disk skill directory
    // and a listing of its bundled assets, so the model can follow the instructions (and, in later
    // phases, read_skill_file / run_skill_script those assets by handle). Reads are fresh each call, so
    // edits to a SKILL.md take effect immediately.
    internal sealed class SkillTools
    {
        public const string OpenSkillName = "open_skill";
        public const string ReadSkillFileName = "read_skill_file";

        // Keep a single skill body from blowing up the context if a SKILL.md is huge.
        private const int MaxBodyChars = 32 * 1024;
        private const int MaxAssetChars = 64 * 1024;
        private const int MaxAssetsScanned = 200; // bound the directory walk
        private const int MaxAssetsShown = 25;    // listed inline on open; the rest are summarized

        // Indexed by slug over the skills ENABLED for this turn (SkillResolve), so open_skill can only
        // load a skill the conversation actually has on. Explicit /use goes through the
        // catalog directly and is not bound by this set.
        private readonly Dictionary<string, Skill> _bySlug;

        // The full discovered catalog (enabled or not). read_skill_file resolves against this, so an
        // author can read a disabled skill's files; open_skill stays scoped to the enabled set above.
        private readonly SkillCatalog _allSkills;

        public SkillTools(IList<Skill> enabledSkills, SkillCatalog allSkills)
        {
            _allSkills = allSkills;
            _bySlug = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);
            if (enabledSkills != null)
            {
                for (int i = 0; i < enabledSkills.Count; i++)
                {
                    Skill s = enabledSkills[i];
                    if (s != null && !string.IsNullOrEmpty(s.Slug)) _bySlug[s.Slug] = s;
                }
            }
        }

        public bool HasSkills
        {
            get { return _bySlug.Count > 0; }
        }

        public bool IsOpenSkill(string functionName)
        {
            return functionName == OpenSkillName;
        }

        public bool IsReadSkillFile(string functionName)
        {
            return functionName == ReadSkillFileName;
        }

        // read_skill_file(slug, relpath): read a file bundled with a skill, by a path relative to the
        // skill folder. ReadOnly - host-handled (no MCP round-trip), so it auto-allows like reveal_tools.
        public JObject ReadSkillFileDef()
        {
            JObject slugP = new JObject(); slugP["type"] = "string";
            JObject relP = new JObject(); relP["type"] = "string";
            JObject props = new JObject();
            props["slug"] = slugP;
            props["relpath"] = relP;
            JObject schema = new JObject();
            schema["type"] = "object";
            schema["properties"] = props;
            schema["required"] = new JArray("slug", "relpath");

            JObject fn = new JObject();
            fn["name"] = ReadSkillFileName;
            fn["description"] = "Read a file bundled with a skill, by the skill's slug and a path relative "
                + "to the skill folder (use the paths from open_skill's listing).";
            fn["parameters"] = schema;

            JObject def = new JObject();
            def["type"] = "function";
            def["function"] = fn;
            return def;
        }

        // Read one bundled asset by relative handle, sandboxed to the skill's own folder (no absolute,
        // no drive, no '..' escape). Only enabled skills are reachable (same set as open_skill).
        public string ReadFile(string slug, string relpath)
        {
            if (string.IsNullOrEmpty(slug)) return "No skill specified.";
            Skill skill;
            // Spans any discovered skill (enabled or not) so a disabled skill's files are still readable.
            if (_allSkills == null || !_allSkills.TryGet(slug, out skill)) return "Unknown skill: " + slug;
            if (string.IsNullOrEmpty(relpath)) return "No file path specified.";

            string full;
            if (!TryResolveAsset(skill.Directory, relpath, out full))
                return "Invalid path: must be relative to the skill folder (no '..', no absolute path).";
            if (!File.Exists(full)) return "File not found: " + relpath;

            try
            {
                string text = File.ReadAllText(full, Encoding.UTF8);
                if (text.Length > MaxAssetChars) text = text.Substring(0, MaxAssetChars) + "\n[file truncated]";
                return text;
            }
            catch
            {
                return "Could not read file: " + relpath;
            }
        }

        // Resolves relpath within root, rejecting absolute paths, drive/ADS colons, and '..' escapes.
        // Routed through the canonical Mcp35.Core PathSandbox (shared with the servers) so a hardening
        // fix to the containment primitive lands here too. A relpath that resolves to the folder itself
        // is allowed by the sandbox but harmless: the caller's File.Exists check rejects a directory.
        private static bool TryResolveAsset(string root, string relpath, out string full)
        {
            full = null;
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(relpath)) return false;
            try
            {
                full = new PathSandbox(root, "skill folder").Resolve(relpath);
                return true;
            }
            catch
            {
                full = null;
                return false;
            }
        }

        // The OpenAI-style function definition for open_skill: { names: string[] } (required), mirroring
        // reveal_tools' shape so the model treats the two meta-tools the same way.
        public JObject OpenSkillDef()
        {
            JObject items = new JObject();
            items["type"] = "string";
            JObject namesProp = new JObject();
            namesProp["type"] = "array";
            namesProp["items"] = items;
            JObject props = new JObject();
            props["names"] = namesProp;
            JObject schema = new JObject();
            schema["type"] = "object";
            schema["properties"] = props;
            schema["required"] = new JArray("names");

            JObject fn = new JObject();
            fn["name"] = OpenSkillName;
            fn["description"] = "Load one or more skills' full instructions by slug so you can follow "
                + "them. Pass the slugs from the skills list.";
            fn["parameters"] = schema;

            JObject def = new JObject();
            def["type"] = "function";
            def["function"] = fn;
            return def;
        }

        // Resolve each slug to its rendered skill block (body + directory + asset listing). Unknown slugs
        // become a short note rather than an error, so a partly-wrong batch still returns the rest.
        public string Open(string[] names)
        {
            if (names == null || names.Length == 0) return "No skill names provided.";

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < names.Length; i++)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                string slug = names[i];
                Skill skill;
                if (slug == null || !_bySlug.TryGetValue(slug, out skill))
                {
                    sb.Append("Unknown skill: ").Append(slug != null ? slug : "(null)");
                    continue;
                }
                sb.Append(RenderSkill(skill));
            }
            return sb.ToString();
        }

        // Renders one skill's block (heading + directory + asset listing + body). Shared by open_skill
        // and the explicit /use slash command, which invokes a skill regardless of enablement.
        internal static string RenderSkill(Skill skill)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("# Skill: ").Append(skill.Slug).Append('\n');
            sb.Append("Skill files are at: ").Append(skill.Directory).Append('\n');

            string assets = ListAssets(skill.Directory);
            if (!string.IsNullOrEmpty(assets))
            {
                sb.Append("Bundled files (read one with read_skill_file using its path below):\n");
                sb.Append(assets).Append('\n');
            }

            sb.Append('\n');
            string body = ReadBody(skill);
            sb.Append(!string.IsNullOrEmpty(body) ? body : "(this skill has no instructions)");
            return sb.ToString();
        }

        private static string ReadBody(Skill skill)
        {
            try
            {
                string text = File.ReadAllText(skill.SkillFilePath, Encoding.UTF8);
                SkillFrontmatter fm = SkillFrontmatter.Parse(text);
                string body = fm.Body;
                if (body != null && body.Length > MaxBodyChars)
                    body = body.Substring(0, MaxBodyChars) + "\n[skill truncated]";
                return body;
            }
            catch
            {
                return null;
            }
        }

        // A summarized listing of files under the skill dir (relative, forward-slashed), excluding
        // SKILL.md. Only the first MaxAssetsShown are listed inline; any beyond that are summarized as a
        // count, so opening a skill with many bundled assets doesn't pay the whole tree into context every
        // time (Level-2/3 disclosure boundary). The model reads any of them by path with read_skill_file.
        private static string ListAssets(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
                string[] files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                List<string> rel = new List<string>();
                bool scanCapped = false;
                for (int i = 0; i < files.Length; i++)
                {
                    string full = files[i];
                    if (string.Equals(Path.GetFileName(full), "SKILL.md", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (rel.Count >= MaxAssetsScanned) { scanCapped = true; break; }
                    rel.Add(full.Substring(dir.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace(Path.DirectorySeparatorChar, '/')
                        .Replace(Path.AltDirectorySeparatorChar, '/'));
                }
                if (rel.Count == 0) return null;
                rel.Sort(StringComparer.Ordinal);

                int shown = Math.Min(rel.Count, MaxAssetsShown);
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < shown; i++)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append("- ").Append(rel[i]);
                }
                int more = rel.Count - shown;
                if (more > 0 || scanCapped)
                    sb.Append("\n- ... and ").Append(more).Append(scanCapped ? "+" : "")
                      .Append(" more file(s) - read any by its relative path with read_skill_file");
                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}
