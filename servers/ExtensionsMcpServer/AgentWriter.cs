using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ExtensionsMcpServer
{
    /// <summary>A tool-level failure (relayed to the model as isError), never an exception out of a handler.</summary>
    internal sealed class AgentWriteException : Exception
    {
        public AgentWriteException(string message) : base(message) { }
    }

    /// <summary>
    /// The agent authoring file operations - the SkillWriter analogue for sub-agents (design A4/phase 10).
    /// An agent is a single flat &lt;slug&gt;.md whose frontmatter (name/description/tools/max_tier/model/
    /// max_turns) is the security contract and whose body is the system prompt. The server assembles the
    /// frontmatter from fields so the model never produces an unloadable agent. Targets the WRITABLE roots
    /// only (project &lt;workdir&gt;/.gxpt/agents, user-global %AppData%/GxPT/agents); the bundled install dir
    /// is never a write target. Atomic writes, UTF-8 (no BOM). Agents have no bundled assets or scripts, so
    /// there is no per-file write/list/delete surface and no execution - just the one &lt;slug&gt;.md.
    /// </summary>
    internal sealed class AgentWriter
    {
        private readonly string _projectRoot; // <workdir>/.gxpt/agents, or null when no workspace
        private readonly string _userRoot;    // %AppData%/GxPT/agents, or null if no user root is configured
        private readonly string _bundledRoot; // <exe>/agents - shipped agents, READ-ONLY (never a write target)
        private readonly string _defaultScope; // scope used when a call omits it ("project" or "user")

        public AgentWriter(string projectRoot, string userRoot, string bundledRoot, string defaultScope)
        {
            _projectRoot = projectRoot;
            _userRoot = userRoot;
            _bundledRoot = bundledRoot;
            _defaultScope = string.IsNullOrEmpty(defaultScope) ? "project" : defaultScope;
        }

        // create_agent: a NEW agent (refuses if it exists); assembles a guaranteed-loadable <slug>.md.
        public string CreateAgent(string scope, string slugIn, string name, string description,
            string[] tools, string maxTier, string model, int maxTurns, string body)
        {
            string root = RootFor(scope);
            string slug = RequireSlug(slugIn);
            if (IsBlank(name)) throw new AgentWriteException("name is required");
            if (IsBlank(description)) throw new AgentWriteException("description is required");
            RequireSingleLine(name, "name");
            RequireSingleLine(description, "description");
            if (!WriterIo.NameMatchesSlug(name, slug))
                throw new AgentWriteException(WriterIo.NameSlugMismatchMessage("agent", name, slug, true, "create_agent"));

            string toolsValue = FormatTools(tools);            // null => omit the key
            string tierValue = NormalizeTier(maxTier);          // null => omit (host defaults to write)
            string modelValue = NormalizeModel(model);          // null => omit
            string turnsValue = NormalizeTurns(maxTurns);       // null => omit

            string file = Path.Combine(root, slug + ".md");
            if (File.Exists(file))
                throw new AgentWriteException("agent '" + slug + "' already exists; use update_agent to change it");

            AtomicWrite(file, BuildAgentMd(name, description, toolsValue, tierValue, modelValue, turnsValue, body));
            return "Created agent '" + slug + "'. It will be available on your next message.";
        }

        // update_agent: partial edit of the frontmatter / body. A null array for tools (or a null scalar)
        // leaves that field unchanged; pass tools: [] to explicitly clear it. max_turns <= 0 leaves it
        // unchanged (clearing it isn't supported - it's a rare need).
        public string UpdateAgent(string scope, string slugIn, string name, string description,
            string[] tools, string maxTier, string model, int maxTurns, string body)
        {
            string root = RootFor(scope);
            string slug = RequireSlug(slugIn);
            string file = Path.Combine(root, slug + ".md");
            if (!File.Exists(file))
                throw NotWritable(slug, scope, false);

            string existing;
            try { existing = File.ReadAllText(file, Encoding.UTF8); }
            catch (Exception ex) { throw new AgentWriteException("could not read agent: " + ex.Message); }

            if (!IsBlank(name)) RequireSingleLine(name, "name");
            if (!IsBlank(description)) RequireSingleLine(description, "description");
            // A new name must still reduce to this agent's slug (the slug is the fixed handle); renaming is
            // create-new + delete-old, not an in-place name swap that would leave name and slug diverged.
            if (!IsBlank(name) && !WriterIo.NameMatchesSlug(name, slug))
                throw new AgentWriteException(WriterIo.NameSlugMismatchMessage("agent", name, slug, false, "create_agent"));

            AgentFrontmatter fm = AgentFrontmatter.Parse(existing);
            // A present-but-blank scalar means "keep" (same as omitting it): only a non-blank value changes
            // a field, so passing "" never silently wipes an existing name/description/model/max_tier/body.
            // Clearing tools is the one explicit clear (pass []); clearing a scalar isn't supported.
            string newName = !IsBlank(name) ? name : fm.Name;     // may stay null -> name line omitted
            string newDesc = !IsBlank(description) ? description : fm.Description;
            if (IsBlank(newDesc)) throw new AgentWriteException("description is required");

            string toolsValue = tools != null ? FormatTools(tools) : fm.ToolsRaw;
            string tierValue = !IsBlank(maxTier) ? NormalizeTier(maxTier) : fm.MaxTierRaw;
            string modelValue = !IsBlank(model) ? NormalizeModel(model) : fm.ModelRaw;
            string turnsValue = maxTurns > 0 ? NormalizeTurns(maxTurns) : fm.MaxTurnsRaw;
            string newBody = !IsBlank(body) ? body : fm.Body;

            AtomicWrite(file, BuildAgentMd(newName, newDesc, toolsValue, tierValue, modelValue, turnsValue, newBody));
            return "Updated agent '" + slug + "'. Changes apply on your next message.";
        }

        // edit_agent: a targeted string replace in an agent's BODY (its system prompt), like edit_skill_file
        // on SKILL.md. The frontmatter is re-assembled from its existing values, so a granular edit can't
        // corrupt the contract; change name/description/tools/max_tier with update_agent.
        public string EditAgent(string scope, string slugIn, string oldString, string newString, bool replaceAll)
        {
            string root = RootFor(scope);
            string slug = RequireSlug(slugIn);
            string file = Path.Combine(root, slug + ".md");
            if (!File.Exists(file))
                throw NotWritable(slug, scope, false);
            if (IsBlank(oldString)) throw new AgentWriteException("old_string is required");
            if (newString == null) throw new AgentWriteException("new_string is required");

            string text;
            try { text = File.ReadAllText(file, Encoding.UTF8); }
            catch (Exception ex) { throw new AgentWriteException("could not read agent: " + ex.Message); }

            AgentFrontmatter fm = AgentFrontmatter.Parse(text);
            if (IsBlank(fm.Description))
                throw new AgentWriteException("agent '" + slug + "' has no frontmatter description; fix it with update_agent first");

            string bodyText = fm.Body != null ? fm.Body : string.Empty; // Parse normalizes to LF
            string oldB = NormalizeNewlines(oldString, "\n");
            string newB = NormalizeNewlines(newString, "\n");

            int count = CountOccurrences(bodyText, oldB);
            if (count == 0)
                throw new AgentWriteException("old_string not found in agent '" + slug + "''s body. edit_agent "
                    + "matches the system-prompt body exactly, and the body is stored trimmed (no leading/"
                    + "trailing blank lines, no frontmatter) - copy an interior span verbatim, use update_agent "
                    + "to replace the whole body, or update_agent for the name/description/tools/max_tier");
            if (count > 1 && !replaceAll)
                throw new AgentWriteException("old_string is not unique in the body (" + count
                    + " matches); make it unique or set replace_all");

            string updatedBody = replaceAll ? bodyText.Replace(oldB, newB) : ReplaceFirst(bodyText, oldB, newB);
            AtomicWrite(file, BuildAgentMd(fm.Name, fm.Description, fm.ToolsRaw, fm.MaxTierRaw,
                fm.ModelRaw, fm.MaxTurnsRaw, updatedBody));
            return "Edited agent '" + slug + "''s body ("
                + (replaceAll ? count + " replacement" + (count == 1 ? "" : "s") : "1 replacement") + ").";
        }

        // read_agent (ReadOnly): the full <slug>.md text. Reads from ANY root - project, user, or the
        // bundled (shipped) agents - newest-wins (project > user > bundled), mirroring the catalog the host
        // shows. So a bundled agent (e.g. explore) is readable even though it can't be edited in place;
        // that's the "base a new agent on an existing one" path. Read is not scope-confined (only writes are).
        public string ReadAgent(string slugIn)
        {
            string slug = RequireSlug(slugIn);
            string file = ResolveExistingFile(slug);
            if (file == null)
                throw new AgentWriteException("agent '" + slug + "' does not exist in any scope (project, user, or bundled)");
            try { return File.ReadAllText(file, Encoding.UTF8); }
            catch (Exception ex) { throw new AgentWriteException("could not read agent: " + ex.Message); }
        }

        // list_agents (ReadOnly): every agent visible to the catalog, across ALL roots (bundled, user,
        // project), each tagged with the source that wins (project shadows user shadows bundled). Discovery
        // is not scope-confined - the author needs to see the shipped agents (e.g. explore) to model on them.
        public string ListAgents()
        {
            // Per slug, show the source the host would dispatch: a described file outranks an undescribed
            // draft, and within that the most-specific root wins (project > user > bundled). Undescribed-only
            // slugs (drafts) are still listed - tagged with their source - so the author can see and fix them.
            Dictionary<string, string> sourceBySlug = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, bool> describedBySlug = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            string[] roots = new string[] { _projectRoot, _userRoot, _bundledRoot }; // most-specific first
            string[] labels = new string[] { "project", "user", "bundled" };
            for (int i = 0; i < roots.Length; i++)
                AddRootSlugs(roots[i], labels[i], sourceBySlug, describedBySlug);

            List<string> slugs = new List<string>(sourceBySlug.Keys);
            slugs.Sort(StringComparer.OrdinalIgnoreCase);

            StringBuilder sb = new StringBuilder();
            sb.Append("Agents:");
            if (slugs.Count == 0) sb.Append("\n(none)");
            foreach (string s in slugs) sb.Append('\n').Append("- ").Append(s).Append(" (").Append(sourceBySlug[s]).Append(')');
            return sb.ToString();
        }

        // delete_agent (Destructive): remove the whole <slug>.md.
        public string DeleteAgent(string scope, string slugIn)
        {
            string root = RootFor(scope);
            string slug = RequireSlug(slugIn);
            string file = Path.Combine(root, slug + ".md");
            if (!File.Exists(file))
                throw NotWritable(slug, scope, true);

            try { File.Delete(file); }
            catch (Exception ex) { throw new AgentWriteException("could not delete agent '" + slug + "': " + ex.Message); }
            return "Deleted agent '" + slug + "'. It is gone from the list on your next message.";
        }

        // validate_agent (ReadOnly): would this agent load, and is its contract well-formed? (mirrors host
        // discovery: a non-empty description is what makes it dispatchable.) Also checks the max_tier enum
        // and that the tools list parses; reports the parsed contract or what is wrong.
        public string ValidateAgent(string slugIn)
        {
            string slug = RequireSlug(slugIn);
            string source;
            string file = ResolveExistingFile(slug, out source);
            if (file == null)
                throw new AgentWriteException("agent '" + slug + "' does not exist in any scope (project, user, or bundled)");

            string text;
            try { text = File.ReadAllText(file, Encoding.UTF8); }
            catch (Exception ex) { throw new AgentWriteException("could not read agent: " + ex.Message); }

            AgentFrontmatter fm = AgentFrontmatter.Parse(text);
            if (fm == null || IsBlank(fm.Description))
                return "INVALID: agent '" + slug + "' would not load - its frontmatter has no 'description'. "
                    + "Add one with update_agent.";

            // max_tier, if present, must be one of the recognized values; an unknown value silently falls
            // back to the default at load, so flag it as a warning here rather than failing outright.
            string tierNote = "";
            if (!IsBlank(fm.MaxTierRaw) && !IsKnownTier(fm.MaxTierRaw))
                tierNote = "; WARNING: max_tier '" + fm.MaxTierRaw.Trim()
                    + "' is not recognized (use readonly | write | destructive) - it will fall back to the default";

            string name = !IsBlank(fm.Name) ? fm.Name : slug;
            string tier = !IsBlank(fm.MaxTierRaw) ? fm.MaxTierRaw.Trim() : "write (default)";
            string tools = fm.ToolsRaw != null ? fm.ToolsRaw : "(none specified)";
            return "OK: agent '" + slug + "' loads (" + source + "). name: " + name + "; description: "
                + fm.Description + "; max_tier: " + tier + "; tools: " + tools + tierNote;
        }

        // ---- frontmatter assembly + field validation ----

        private static string BuildAgentMd(string name, string description, string toolsValue,
            string tierValue, string modelValue, string turnsValue, string body)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("---\n");
            // Omit the name line when there's no name (the catalog falls back to the slug); create_agent
            // requires a name, so created agents always have one.
            if (!IsBlank(name)) sb.Append("name: ").Append(name.Trim()).Append('\n');
            sb.Append("description: ").Append(description.Trim()).Append('\n');
            if (toolsValue != null) sb.Append("tools: ").Append(toolsValue).Append('\n');
            if (!IsBlank(tierValue)) sb.Append("max_tier: ").Append(tierValue.Trim()).Append('\n');
            if (!IsBlank(modelValue)) sb.Append("model: ").Append(modelValue.Trim()).Append('\n');
            if (!IsBlank(turnsValue)) sb.Append("max_turns: ").Append(turnsValue.Trim()).Append('\n');
            sb.Append("---\n\n");
            string b = body != null ? body : string.Empty;
            sb.Append(b);
            if (!b.EndsWith("\n")) sb.Append('\n');
            return sb.ToString();
        }

        // Formats the tool allowlist as an inline list value. null => null (omit the key entirely - the host
        // reads that as "unspecified" and applies a conservative default). An empty array => "[]" (an
        // explicit "no tools"). Each token must be single-line and free of the list delimiters [],.
        private static string FormatTools(string[] tools)
        {
            if (tools == null) return null;

            StringBuilder sb = new StringBuilder();
            sb.Append('[');
            int written = 0;
            for (int i = 0; i < tools.Length; i++)
            {
                string t = tools[i] != null ? tools[i].Trim() : string.Empty;
                if (t.Length == 0) continue;
                if (t.IndexOf('\n') >= 0 || t.IndexOf('\r') >= 0)
                    throw new AgentWriteException("a tool name must be a single line (no line breaks): '" + t + "'");
                if (t.IndexOf(',') >= 0 || t.IndexOf('[') >= 0 || t.IndexOf(']') >= 0)
                    throw new AgentWriteException("a tool name cannot contain '[', ']' or ',': '" + t + "'");
                if (written > 0) sb.Append(", ");
                sb.Append(t);
                written++;
            }
            sb.Append(']');
            return written == 0 ? "[]" : sb.ToString();
        }

        // readonly | write | destructive (a few spelling variants tolerated). Returns the canonical lower
        // form, null for a blank/absent value (the field is optional), or throws on an unrecognized value so
        // the author fixes it at write time.
        private static string NormalizeTier(string maxTier)
        {
            if (maxTier == null || maxTier.Trim().Length == 0) return null;
            string canonical;
            if (TryNormalizeTier(maxTier, out canonical)) return canonical;
            throw new AgentWriteException("max_tier must be one of: readonly, write, destructive (got '"
                + maxTier.Trim() + "')");
        }

        // The single source of accepted tier spellings: canonical lower form + true, or false for a blank/
        // unrecognized value. NormalizeTier (throws on unrecognized) and IsKnownTier (returns the bool) both
        // build on it - one vocabulary, and no exception-as-control-flow on validate_agent's warning path.
        private static bool TryNormalizeTier(string maxTier, out string canonical)
        {
            canonical = null;
            if (maxTier == null) return false;
            switch (maxTier.Trim().ToLowerInvariant())
            {
                case "readonly":
                case "read-only":
                case "read_only":
                    canonical = "readonly"; return true;
                case "write":
                    canonical = "write"; return true;
                case "destructive":
                    canonical = "destructive"; return true;
                default:
                    return false;
            }
        }

        private static bool IsKnownTier(string raw)
        {
            string canonical;
            return TryNormalizeTier(raw, out canonical);
        }

        private static string NormalizeModel(string model)
        {
            if (model == null) return null;
            string v = model.Trim();
            if (v.Length == 0) return null;
            RequireSingleLine(v, "model");
            return v;
        }

        private static string NormalizeTurns(int maxTurns)
        {
            if (maxTurns <= 0) return null;
            return maxTurns.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // ---- internals (mirrors SkillWriter; duplicated rather than shared so SkillWriter is untouched) ----

        private string RootFor(string scope)
        {
            string s = (scope == null ? _defaultScope : scope.Trim().ToLowerInvariant());
            if (s.Length == 0) s = _defaultScope;
            if (s == "project")
            {
                if (string.IsNullOrEmpty(_projectRoot))
                    throw new AgentWriteException("no workspace folder is set for this conversation");
                return _projectRoot;
            }
            if (s == "user")
            {
                if (string.IsNullOrEmpty(_userRoot))
                    throw new AgentWriteException("no user-global agents root is configured for this conversation");
                return _userRoot;
            }
            throw new AgentWriteException("unknown scope '" + scope + "' (use 'project' or 'user')");
        }

        // The "not in a writable scope" error for a write/edit/delete: when <slug>.md isn't in the target
        // writable root. If the slug names a bundled (shipped, read-only) agent, say so and point at
        // create_agent to override it - the bare "does not exist" is misleading when the model can see and
        // read that bundled agent. forDelete tailors the verb (a bundled agent is overridden, not deleted).
        // The "not in a writable scope" error: a bundled shadow (read-only), or it lives in the other
        // writable scope, or it truly doesn't exist. Probes use the agent file shape (<slug>.md); the
        // wording is shared with SkillWriter via WriterIo so the two can't drift.
        private AgentWriteException NotWritable(string slug, string targetScope, bool forDelete)
        {
            bool bundled = !string.IsNullOrEmpty(_bundledRoot) && File.Exists(Path.Combine(_bundledRoot, slug + ".md"));
            string eff = WriterIo.NormalizeScope(targetScope, _defaultScope);
            string otherRoot = eff == "project" ? _userRoot : _projectRoot;
            string otherLabel = eff == "project" ? "user" : "project";
            bool inOther = !bundled && !string.IsNullOrEmpty(otherRoot)
                && File.Exists(Path.Combine(otherRoot, slug + ".md"));
            return new AgentWriteException(WriterIo.NotWritableMessage("agent", slug, "create_agent", forDelete,
                bundled, inOther ? otherLabel : null, eff));
        }

        // Convenience for callers (read_agent) that only need the path, not which scope it came from.
        private string ResolveExistingFile(string slug) { string source; return ResolveExistingFile(slug, out source); }

        // Read-side resolution: the file the HOST would dispatch for this slug - the most-specific root
        // (project > user > bundled) whose <slug>.md declares a non-empty description. A description-less
        // file (a draft) does NOT claim the slug, so it can't shadow a valid agent in a less-specific root
        // (matching AgentCatalog.TryLoad, which skips description-less files). If NO root has a described
        // copy, the most-specific file is returned anyway (a lone/broken draft) so the author can still read
        // it and validate_agent can report why it won't load. `source` is the winning scope label.
        private string ResolveExistingFile(string slug, out string source)
        {
            source = null;
            string[] roots = new string[] { _projectRoot, _userRoot, _bundledRoot };
            string[] labels = new string[] { "project", "user", "bundled" };
            string anyFile = null, anyLabel = null;
            for (int i = 0; i < roots.Length; i++)
            {
                string file = FindInRoot(roots[i], slug);
                if (file == null) continue;
                if (anyFile == null) { anyFile = file; anyLabel = labels[i]; }
                if (HasDescription(file)) { source = labels[i]; return file; }
            }
            source = anyLabel;
            return anyFile;
        }

        // The file in `root` whose normalized slug matches, by the host's filename rule: the canonical
        // "<slug>.md" fast-path (the common case - tool-written names are already canonical and
        // SkillSlug.Make is idempotent, so this avoids enumerating the directory), else a normalized scan
        // (SkillSlug.Make per file, exact ".md" guard, ordinal last-wins) for a hand-placed non-canonical
        // name like "Code Explorer.md". Returns the path or null.
        private static string FindInRoot(string root, string slug)
        {
            if (string.IsNullOrEmpty(root)) return null;
            string canonical = Path.Combine(root, slug + ".md");
            if (File.Exists(canonical)) return canonical;
            if (!Directory.Exists(root)) return null;
            string[] files;
            try { files = Directory.GetFiles(root, "*.md"); }
            catch { return null; }
            Array.Sort(files, StringComparer.Ordinal);
            string match = null;
            foreach (string f in files)
            {
                string ext = Path.GetExtension(f);
                if (ext == null || !ext.Equals(".md", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(SkillSlug.Make(Path.GetFileNameWithoutExtension(f)), slug, StringComparison.Ordinal))
                    match = f; // ordinal last-wins within the root, matching the host catalog
            }
            return match;
        }

        // True if the file's frontmatter declares a non-empty description - what makes the host dispatch it.
        private static bool HasDescription(string file)
        {
            try { return !IsBlank(AgentFrontmatter.Parse(File.ReadAllText(file, Encoding.UTF8)).Description); }
            catch { return false; }
        }

        // Merge one root's agents into the list maps. The most-specific root is added first; a slug is taken
        // if not yet seen, or if this file is described and the stored one isn't (a described file in a
        // less-specific root still outranks a draft above it - matching dispatch). Within a root, last-wins.
        private static void AddRootSlugs(string root, string label,
            Dictionary<string, string> sourceBySlug, Dictionary<string, bool> describedBySlug)
        {
            Dictionary<string, bool> rootMap = ScanRoot(root);
            foreach (KeyValuePair<string, bool> kv in rootMap)
            {
                if (!sourceBySlug.ContainsKey(kv.Key) || (kv.Value && !describedBySlug[kv.Key]))
                {
                    sourceBySlug[kv.Key] = label;
                    describedBySlug[kv.Key] = kv.Value;
                }
            }
        }

        // Scan one root into slug -> hasDescription, normalized exactly like the host AgentCatalog: only
        // files whose extension is exactly ".md" (guarding the Win32 "*.md" wildcard quirk), slug derived
        // via SkillSlug.Make (not the raw name), file list sorted ordinal so a same-root slug collision
        // resolves deterministically (last wins) - the rule AgentCatalog.Build/TryLoad use.
        private static Dictionary<string, bool> ScanRoot(string root)
        {
            Dictionary<string, bool> map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return map;
            string[] files;
            try { files = Directory.GetFiles(root, "*.md"); }
            catch { return map; }
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string f in files)
            {
                string ext = Path.GetExtension(f);
                if (ext == null || !ext.Equals(".md", StringComparison.OrdinalIgnoreCase)) continue;
                string slug = SkillSlug.Make(Path.GetFileNameWithoutExtension(f));
                if (!string.IsNullOrEmpty(slug)) map[slug] = HasDescription(f);
            }
            return map;
        }

        private static string RequireSlug(string slugIn)
        {
            string slug = SkillSlug.Make(slugIn);
            if (string.IsNullOrEmpty(slug)) throw new AgentWriteException("a valid slug is required");
            return slug;
        }

        // Frontmatter values are single-line by design (the CR/LF guard lives in WriterIo so it can't drift
        // from the skill writer's copy).
        private static void RequireSingleLine(string value, string field)
        {
            if (WriterIo.HasLineBreak(value))
                throw new AgentWriteException(field + " must be a single line (no line breaks)");
        }

        private static string NormalizeNewlines(string s, string nl) { return WriterIo.NormalizeNewlines(s, nl); }
        private static int CountOccurrences(string text, string sub) { return WriterIo.CountOccurrences(text, sub); }
        private static string ReplaceFirst(string text, string oldS, string newS) { return WriterIo.ReplaceFirst(text, oldS, newS); }

        private static bool IsBlank(string s)
        {
            return s == null || s.Trim().Length == 0;
        }

        private static void AtomicWrite(string path, string content) { WriterIo.AtomicWrite(path, content); }
    }
}
