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
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

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
            string[] tools, string maxTier, string model, int maxTurns)
        {
            string root = RootFor(scope);
            string slug = RequireSlug(slugIn);
            if (IsBlank(name)) throw new AgentWriteException("name is required");
            if (IsBlank(description)) throw new AgentWriteException("description is required");
            RequireSingleLine(name, "name");
            RequireSingleLine(description, "description");

            string toolsValue = FormatTools(tools);            // null => omit the key
            string tierValue = NormalizeTier(maxTier);          // null => omit (host defaults to write)
            string modelValue = NormalizeModel(model);          // null => omit
            string turnsValue = NormalizeTurns(maxTurns);       // null => omit

            string file = Path.Combine(root, slug + ".md");
            if (File.Exists(file))
                throw new AgentWriteException("agent '" + slug + "' already exists; use update_agent to change it");

            AtomicWrite(file, BuildAgentMd(name, description, toolsValue, tierValue, modelValue, turnsValue, null));
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
                throw NotWritable(slug, false);

            string existing;
            try { existing = File.ReadAllText(file, Encoding.UTF8); }
            catch (Exception ex) { throw new AgentWriteException("could not read agent: " + ex.Message); }

            if (name != null) RequireSingleLine(name, "name");
            if (description != null) RequireSingleLine(description, "description");

            AgentFrontmatter fm = AgentFrontmatter.Parse(existing);
            string newName = name != null ? name : fm.Name;     // may stay null -> name line omitted
            string newDesc = description != null ? description : fm.Description;
            if (IsBlank(newDesc)) throw new AgentWriteException("description is required");

            string toolsValue = tools != null ? FormatTools(tools) : fm.ToolsRaw;
            string tierValue = maxTier != null ? NormalizeTier(maxTier) : fm.MaxTierRaw;
            string modelValue = model != null ? NormalizeModel(model) : fm.ModelRaw;
            string turnsValue = maxTurns > 0 ? NormalizeTurns(maxTurns) : fm.MaxTurnsRaw;
            string newBody = body != null ? body : fm.Body;

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
                throw NotWritable(slug, false);
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
                throw new AgentWriteException("old_string not found in agent '" + slug + "''s body (edit_agent "
                    + "changes the body; use update_agent for the name/description/tools/max_tier)");
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
            string source;
            string file = ResolveExistingFile(slug, out source);
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
            // Add least-specific first so a more-specific root overwrites the source label (newest wins).
            Dictionary<string, string> bySlug = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddRootSlugs(_bundledRoot, "bundled", bySlug);
            AddRootSlugs(_userRoot, "user", bySlug);
            AddRootSlugs(_projectRoot, "project", bySlug);

            List<string> slugs = new List<string>(bySlug.Keys);
            slugs.Sort(StringComparer.OrdinalIgnoreCase);

            StringBuilder sb = new StringBuilder();
            sb.Append("Agents:");
            if (slugs.Count == 0) sb.Append("\n(none)");
            foreach (string s in slugs) sb.Append('\n').Append("- ").Append(s).Append(" (").Append(bySlug[s]).Append(')');
            return sb.ToString();
        }

        // delete_agent (Destructive): remove the whole <slug>.md.
        public string DeleteAgent(string scope, string slugIn)
        {
            string root = RootFor(scope);
            string slug = RequireSlug(slugIn);
            string file = Path.Combine(root, slug + ".md");
            if (!File.Exists(file))
                throw NotWritable(slug, true);

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
            if (tools.Length == 0) return "[]";

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
        // form, or throws on an unrecognized value so the author fixes it at write time.
        private static string NormalizeTier(string maxTier)
        {
            if (maxTier == null) return null;
            string v = maxTier.Trim().ToLowerInvariant();
            if (v.Length == 0) return null;
            switch (v)
            {
                case "readonly":
                case "read-only":
                case "read_only":
                    return "readonly";
                case "write":
                    return "write";
                case "destructive":
                    return "destructive";
                default:
                    throw new AgentWriteException("max_tier must be one of: readonly, write, destructive (got '"
                        + maxTier.Trim() + "')");
            }
        }

        private static bool IsKnownTier(string raw)
        {
            string v = raw != null ? raw.Trim().ToLowerInvariant() : "";
            return v == "readonly" || v == "read-only" || v == "read_only" || v == "write" || v == "destructive";
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
        private AgentWriteException NotWritable(string slug, bool forDelete)
        {
            if (!string.IsNullOrEmpty(_bundledRoot) && File.Exists(Path.Combine(_bundledRoot, slug + ".md")))
                return new AgentWriteException("agent '" + slug + "' is a bundled agent (shipped with the app) "
                    + (forDelete
                        ? "and can't be deleted; bundled agents are read-only"
                        : "and can't be edited in place; create a project/user copy with create_agent (same slug) to override it"));
            return new AgentWriteException(forDelete
                ? "agent '" + slug + "' does not exist"
                : "agent '" + slug + "' does not exist; create_agent first");
        }

        // Read-side resolution: find <slug>.md across all roots, newest-wins (project > user > bundled),
        // matching the host catalog's precedence. Returns the file path (and the source label) or null.
        private string ResolveExistingFile(string slug, out string source)
        {
            source = null;
            string[] roots = new string[] { _projectRoot, _userRoot, _bundledRoot };
            string[] labels = new string[] { "project", "user", "bundled" };
            for (int i = 0; i < roots.Length; i++)
            {
                if (string.IsNullOrEmpty(roots[i])) continue;
                string file = Path.Combine(roots[i], slug + ".md");
                if (File.Exists(file)) { source = labels[i]; return file; }
            }
            return null;
        }

        // Adds each <slug>.md in root to the map under the given source label, overwriting any prior label
        // (callers add least-specific first so the most-specific source wins).
        private static void AddRootSlugs(string root, string label, Dictionary<string, string> bySlug)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            string[] files;
            try { files = Directory.GetFiles(root, "*.md"); }
            catch { files = new string[0]; }
            foreach (string f in files)
            {
                string nm = Path.GetFileNameWithoutExtension(f);
                if (!string.IsNullOrEmpty(nm)) bySlug[nm] = label;
            }
        }

        private static string RequireSlug(string slugIn)
        {
            string slug = SkillSlug.Make(slugIn);
            if (string.IsNullOrEmpty(slug)) throw new AgentWriteException("a valid slug is required");
            return slug;
        }

        // Frontmatter values are single-line by design: a CR/LF would close the "---" block early or
        // inject keys, producing a forged/unloadable agent (the very thing these tools exist to prevent).
        private static void RequireSingleLine(string value, string field)
        {
            if (value != null && (value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0))
                throw new AgentWriteException(field + " must be a single line (no line breaks)");
        }

        private static string NormalizeNewlines(string s, string nl)
        {
            if (s == null) return null;
            string lf = s.Replace("\r\n", "\n").Replace("\r", "\n");
            return nl == "\n" ? lf : lf.Replace("\n", nl);
        }

        private static int CountOccurrences(string text, string sub)
        {
            if (string.IsNullOrEmpty(sub)) return 0;
            int count = 0, idx = 0;
            while ((idx = text.IndexOf(sub, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += sub.Length;
            }
            return count;
        }

        private static string ReplaceFirst(string text, string oldS, string newS)
        {
            int idx = text.IndexOf(oldS, StringComparison.Ordinal);
            if (idx < 0) return text;
            return text.Substring(0, idx) + newS + text.Substring(idx + oldS.Length);
        }

        private static bool IsBlank(string s)
        {
            return s == null || s.Trim().Length == 0;
        }

        // Atomic write (mirrors SkillWriter/MemoryStore): temp file then replace/move; creates parent dirs.
        // A failed write never leaves a half-written or destroyed target; the fallback moves the original
        // aside FIRST and restores it if the swap fails.
        private static void AtomicWrite(string path, string content)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllText(tmp, content, Utf8NoBom);

                if (!File.Exists(path)) { File.Move(tmp, path); return; }

                try { File.Replace(tmp, path, null); return; }
                catch { }

                string bak = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bak";
                File.Move(path, bak); // throws here => original untouched
                try { File.Move(tmp, path); }
                catch { File.Move(bak, path); throw; } // restore the original, then surface the failure
                try { File.Delete(bak); } catch { }
            }
            finally
            {
                if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { } }
            }
        }
    }
}
