using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mcp35.Core.Security;

namespace ExtensionsMcpServer
{
    /// <summary>A tool-level failure (relayed to the model as isError), never an exception out of a handler.</summary>
    internal sealed class SkillWriteException : Exception
    {
        public SkillWriteException(string message) : base(message) { }
    }

    /// <summary>
    /// The skill authoring file operations (design S16): create a new skill's SKILL.md, write supporting
    /// files/scripts into a skill folder, and partial-update an existing SKILL.md. Targets the WRITABLE
    /// roots only (project `<workdir>/.gxpt/skills`, later user-global); the bundled install dir is never
    /// a write target. Atomic writes, UTF-8 (no BOM), CRLF for `.bat`/`.cmd` (XP cmd.exe).
    /// </summary>
    internal sealed class SkillWriter
    {
        private readonly string _projectRoot; // <workdir>/.gxpt/skills, or null when no workspace
        private readonly string _userRoot;    // %AppData%/GxPT/skills, or null if no user root is configured
        private readonly string _bundledRoot; // <exe>/skills - shipped skills, READ-ONLY; used only to make
                                              // the "can't edit a bundled skill" error accurate (never written)
        private readonly string _defaultScope; // scope used when a call omits it ("project" or "user")

        public SkillWriter(string projectRoot, string userRoot)
            : this(projectRoot, userRoot, null, "project")
        {
        }

        public SkillWriter(string projectRoot, string userRoot, string defaultScope)
            : this(projectRoot, userRoot, null, defaultScope)
        {
        }

        // defaultScope is the scope applied when a tool call omits `scope`. The workdir instance defaults
        // to "project"; the workdir-less instance defaults to "user" (project isn't reachable there). The
        // bundled root is a read-only reference (writes still target project/user only) so a write/edit/
        // delete against a bundled skill reports it as bundled rather than a bare "does not exist".
        public SkillWriter(string projectRoot, string userRoot, string bundledRoot, string defaultScope)
        {
            _projectRoot = projectRoot;
            _userRoot = userRoot;
            _bundledRoot = bundledRoot;
            _defaultScope = string.IsNullOrEmpty(defaultScope) ? "project" : defaultScope;
        }

        // create_skill: a NEW skill (refuses if it exists); assembles a guaranteed-loadable SKILL.md.
        public string CreateSkill(string scope, string slugIn, string name, string description, string body)
        {
            string root = RootFor(scope);
            string slug = RequireSlug(slugIn);
            if (IsBlank(name)) throw new SkillWriteException("name is required");
            if (IsBlank(description)) throw new SkillWriteException("description is required");
            RequireSingleLine(name, "name");
            RequireSingleLine(description, "description");
            if (!WriterIo.NameMatchesSlug(name, slug))
                throw new SkillWriteException(WriterIo.NameSlugMismatchMessage("skill", name, slug, true, "create_skill"));

            string file = Path.Combine(Path.Combine(root, slug), "SKILL.md");
            if (File.Exists(file))
                throw new SkillWriteException("skill '" + slug + "' already exists; use update_skill to change it");

            AtomicWrite(file, BuildSkillMd(name, description, body));
            return "Created skill '" + slug + "'. It will be available on your next message.";
        }

        // write_skill_file: a supporting reference file or script (NOT SKILL.md - that's create/update).
        public string WriteFile(string scope, string slugIn, string relpath, string content)
        {
            string root = RootFor(scope);
            string slug = RequireSlug(slugIn);
            string dir = Path.Combine(root, slug);
            if (!File.Exists(Path.Combine(dir, "SKILL.md")))
                throw NotWritable(slug, scope, false);

            string full;
            try { full = new PathSandbox(dir, "skill folder").Resolve(relpath); }
            catch (SandboxException ex) { throw new SkillWriteException(ex.Message); }

            if (string.Equals(Path.GetFileName(full), "SKILL.md", StringComparison.OrdinalIgnoreCase))
                throw new SkillWriteException("write SKILL.md with create_skill / update_skill, not write_skill_file");

            string text = content != null ? content : string.Empty;
            string ext = (Path.GetExtension(full) ?? string.Empty).ToLowerInvariant();
            if (ext == ".bat" || ext == ".cmd")
            {
                RequireBatchAscii(text);
                text = ToCrlf(text); // XP cmd.exe wants CRLF
            }
            AtomicWrite(full, text);
            return "Wrote " + relpath + " into skill '" + slug + "'.";
        }

        // update_skill: partial edit of the main file (null field = leave unchanged).
        public string UpdateSkill(string scope, string slugIn, string name, string description, string body)
        {
            string root = RootFor(scope);
            string slug = RequireSlug(slugIn);
            string file = Path.Combine(Path.Combine(root, slug), "SKILL.md");
            if (!File.Exists(file))
                throw NotWritable(slug, scope, false);

            string existing;
            try { existing = File.ReadAllText(file, Encoding.UTF8); }
            catch (Exception ex) { throw new SkillWriteException("could not read SKILL.md: " + ex.Message); }

            if (!IsBlank(name)) RequireSingleLine(name, "name");
            if (!IsBlank(description)) RequireSingleLine(description, "description");
            // A new name must still reduce to this skill's slug (the slug is the fixed handle/folder name);
            // renaming is create-new + delete-old, not an in-place name swap that diverges name from slug.
            if (!IsBlank(name) && !WriterIo.NameMatchesSlug(name, slug))
                throw new SkillWriteException(WriterIo.NameSlugMismatchMessage("skill", name, slug, false, "create_skill"));

            SkillFrontmatter fm = SkillFrontmatter.Parse(existing);
            // A present-but-blank scalar means "keep" (same as omitting it): passing "" never silently wipes
            // an existing name/description/body. Mirrors update_agent.
            string newName = !IsBlank(name) ? name : fm.Name; // may stay null -> name line omitted (no slug forced)
            string newDesc = !IsBlank(description) ? description : fm.Description;
            string newBody = !IsBlank(body) ? body : fm.Body;
            if (IsBlank(newDesc)) throw new SkillWriteException("description is required");

            AtomicWrite(file, BuildSkillMd(newName, newDesc, newBody));
            return "Updated skill '" + slug + "'. Changes apply on your next message.";
        }

        // rename_skill: change a skill's slug - its folder name and handle. Moves the WHOLE skill folder
        // (SKILL.md plus any supporting files/scripts) within the same writable scope and rewrites SKILL.md's
        // name to stay aligned with the new slug. Refuses if the target slug already exists, or if the source
        // is a bundled (read-only) skill. newName is optional: omitted => a Title Case name derived from the
        // new slug (kept if the current name already aligns); given => it must reduce to the new slug.
        public string RenameSkill(string scope, string slugIn, string newSlugIn, string newName)
        {
            string root = RootFor(scope);
            string oldSlug = RequireSlug(slugIn);
            string newSlug = RequireSlug(newSlugIn);
            string oldDir = Path.Combine(root, oldSlug);
            string oldFile = Path.Combine(oldDir, "SKILL.md");
            if (!File.Exists(oldFile))
                throw NotWritable(oldSlug, scope, false);
            if (string.Equals(oldSlug, newSlug, StringComparison.Ordinal))
                throw new SkillWriteException("the new slug is the same as the current one ('" + oldSlug + "')");
            string newDir = Path.Combine(root, newSlug);
            if (Directory.Exists(newDir))
                throw new SkillWriteException("skill '" + newSlug + "' already exists; choose a different slug or delete it first");

            string existing;
            try { existing = File.ReadAllText(oldFile, Encoding.UTF8); }
            catch (Exception ex) { throw new SkillWriteException("could not read SKILL.md: " + ex.Message); }

            SkillFrontmatter fm = SkillFrontmatter.Parse(existing);
            if (IsBlank(fm.Description))
                throw new SkillWriteException("skill '" + oldSlug + "' has no description; fix it with update_skill before renaming");

            string resolvedName = ResolveRenameName(newName, newSlug, fm.Name);

            // Move the whole folder (assets and scripts come along), then rewrite SKILL.md's name in place.
            try { Directory.Move(oldDir, newDir); }
            catch (Exception ex) { throw new SkillWriteException("could not rename skill folder: " + ex.Message); }
            AtomicWrite(Path.Combine(newDir, "SKILL.md"), BuildSkillMd(resolvedName, fm.Description, fm.Body));
            return "Renamed skill '" + oldSlug + "' to '" + newSlug + "'. Anything that referenced '" + oldSlug
                + "' must now use '" + newSlug + "'.";
        }

        // The display name a rename should write: an explicit newName (validated against the new slug), else
        // the current name if it still aligns, else a Title Case name derived from the new slug.
        private static string ResolveRenameName(string newName, string newSlug, string currentName)
        {
            if (!IsBlank(newName))
            {
                RequireSingleLine(newName, "new_name");
                if (!WriterIo.NameMatchesSlug(newName, newSlug))
                    throw new SkillWriteException(WriterIo.NameSlugMismatchMessage("skill", newName, newSlug, true, "create_skill"));
                return newName;
            }
            if (!IsBlank(currentName) && WriterIo.NameMatchesSlug(currentName, newSlug))
                return currentName;
            return WriterIo.TitleCaseFromSlug(newSlug);
        }

        // edit_skill_file (tier 2): targeted string replace in a supporting file (files__edit parity). For
        // SKILL.md the replace is confined to the BODY and the frontmatter is re-assembled, so a granular
        // edit can't corrupt it; the name/description are still edited through update_skill.
        public string EditFile(string scope, string slugIn, string relpath, string oldString, string newString, bool replaceAll)
        {
            string root = RootFor(scope);
            string slug = RequireSlug(slugIn);
            string dir = Path.Combine(root, slug);
            if (!File.Exists(Path.Combine(dir, "SKILL.md")))
                throw NotWritable(slug, scope, false);
            if (IsBlank(oldString)) throw new SkillWriteException("old_string is required");
            if (newString == null) throw new SkillWriteException("new_string is required");

            string full;
            try { full = new PathSandbox(dir, "skill folder").Resolve(relpath); }
            catch (SandboxException ex) { throw new SkillWriteException(ex.Message); }

            if (!File.Exists(full))
                throw new SkillWriteException(relpath + " does not exist in skill '" + slug + "'");

            string text;
            try { text = File.ReadAllText(full, Encoding.UTF8); }
            catch (Exception ex) { throw new SkillWriteException("could not read " + relpath + ": " + ex.Message); }

            // SKILL.md edits target the BODY only: parse off the frontmatter, replace within the body,
            // then re-assemble with the server-managed name/description, so the edit can never corrupt the
            // frontmatter or make the skill unloadable. (Edit the name/description with update_skill.)
            bool isSkillMd = string.Equals(Path.GetFileName(full), "SKILL.md", StringComparison.OrdinalIgnoreCase);
            if (isSkillMd)
            {
                SkillFrontmatter fm = SkillFrontmatter.Parse(text);
                if (IsBlank(fm.Description))
                    throw new SkillWriteException("SKILL.md has no frontmatter description; fix it with update_skill first");

                string body = fm.Body != null ? fm.Body : string.Empty; // Parse already normalizes to LF
                string oldB = NormalizeNewlines(oldString, "\n");
                string newB = NormalizeNewlines(newString, "\n");

                int bodyCount = CountOccurrences(body, oldB);
                if (bodyCount == 0)
                    throw new SkillWriteException("old_string not found in SKILL.md's body. edit_skill_file "
                        + "matches the body exactly, and the body is stored trimmed (no leading/trailing blank "
                        + "lines, no frontmatter) - copy an interior span verbatim, or use update_skill to "
                        + "replace the whole body or change the name/description");
                if (bodyCount > 1 && !replaceAll)
                    throw new SkillWriteException("old_string is not unique in SKILL.md's body (" + bodyCount
                        + " matches); make it unique or set replace_all");

                // Re-assembling through BuildSkillMd canonicalizes the file (frontmatter normalized, body
                // trimmed of surrounding whitespace via the parser) - same normalization as update_skill,
                // and the price of keeping SKILL.md always server-assembled / guaranteed-loadable.
                string newBody = replaceAll ? body.Replace(oldB, newB) : ReplaceFirst(body, oldB, newB);
                AtomicWrite(full, BuildSkillMd(fm.Name, fm.Description, newBody));
                return "Edited SKILL.md body in skill '" + slug + "' ("
                    + (replaceAll ? bodyCount + " replacement" + (bodyCount == 1 ? "" : "s") : "1 replacement") + ").";
            }

            // Match against the file's own line endings: read tools normalize CRLF to LF, so an
            // old_string copied from a read is LF-only even when the file on disk is CRLF (same fix as
            // files__edit). Normalize old/new to the file's dominant newline before matching.
            string nl = DetectNewline(text);
            string oldN = NormalizeNewlines(oldString, nl);
            string newN = NormalizeNewlines(newString, nl);

            int count = CountOccurrences(text, oldN);
            if (count == 0) throw new SkillWriteException("old_string not found in " + relpath);
            if (count > 1 && !replaceAll)
                throw new SkillWriteException("old_string is not unique in " + relpath + " (" + count
                    + " matches); make it unique or set replace_all");

            string updated = replaceAll ? text.Replace(oldN, newN) : ReplaceFirst(text, oldN, newN);

            string ext = (Path.GetExtension(full) ?? string.Empty).ToLowerInvariant();
            if (ext == ".bat" || ext == ".cmd")
            {
                RequireBatchAscii(updated);
                updated = ToCrlf(updated); // XP cmd.exe wants CRLF
            }
            AtomicWrite(full, updated);
            return "Edited " + relpath + " in skill '" + slug + "' ("
                + (replaceAll ? count + " replacement" + (count == 1 ? "" : "s") : "1 replacement") + ").";
        }

        // list_skill_files (tier 2, ReadOnly): every file in the skill folder, by path relative to it.
        public string ListFiles(string scope, string slugIn)
        {
            string root = RootFor(scope);
            string slug = RequireSlug(slugIn);
            string dir = Path.Combine(root, slug);
            if (!File.Exists(Path.Combine(dir, "SKILL.md")))
                throw new SkillWriteException("skill '" + slug + "' does not exist");

            List<string> rels = new List<string>();
            CollectFiles(dir, dir, rels);
            rels.Sort(StringComparer.OrdinalIgnoreCase);

            StringBuilder sb = new StringBuilder();
            sb.Append("Files in skill '").Append(slug).Append("':");
            foreach (string r in rels) sb.Append('\n').Append("- ").Append(r);
            return sb.ToString();
        }

        // delete_skill_file (tier 2, Destructive): remove one supporting file (never SKILL.md).
        public string DeleteFile(string scope, string slugIn, string relpath)
        {
            string root = RootFor(scope);
            string slug = RequireSlug(slugIn);
            string dir = Path.Combine(root, slug);
            if (!File.Exists(Path.Combine(dir, "SKILL.md")))
                throw NotWritable(slug, scope, true);

            string full;
            try { full = new PathSandbox(dir, "skill folder").Resolve(relpath); }
            catch (SandboxException ex) { throw new SkillWriteException(ex.Message); }

            if (string.Equals(Path.GetFileName(full), "SKILL.md", StringComparison.OrdinalIgnoreCase))
                throw new SkillWriteException("cannot delete SKILL.md; use delete_skill to remove the whole skill");
            if (!File.Exists(full))
                throw new SkillWriteException(relpath + " does not exist in skill '" + slug + "'");

            File.Delete(full);
            return "Deleted " + relpath + " from skill '" + slug + "'.";
        }

        // delete_skill (tier 2, Destructive): remove the whole skill folder.
        public string DeleteSkill(string scope, string slugIn)
        {
            string root = RootFor(scope);
            string slug = RequireSlug(slugIn);
            string dir = Path.Combine(root, slug);
            if (!File.Exists(Path.Combine(dir, "SKILL.md")))
                throw NotWritable(slug, scope, true);

            try { Directory.Delete(dir, true); }
            catch (Exception ex) { throw new SkillWriteException("could not delete skill '" + slug + "': " + ex.Message); }
            return "Deleted skill '" + slug + "'. It is gone from the list on your next message.";
        }

        // validate_skill (tier 2, ReadOnly): would this skill's SKILL.md load? (mirrors host discovery:
        // frontmatter must declare a non-empty description). Supporting files/scripts aren't validated.
        public string ValidateSkill(string scope, string slugIn)
        {
            string root = RootFor(scope);
            string slug = RequireSlug(slugIn);
            string file = Path.Combine(Path.Combine(root, slug), "SKILL.md");
            if (!File.Exists(file))
                throw new SkillWriteException("skill '" + slug + "' does not exist");

            string text;
            try { text = File.ReadAllText(file, Encoding.UTF8); }
            catch (Exception ex) { throw new SkillWriteException("could not read SKILL.md: " + ex.Message); }

            SkillFrontmatter fm = SkillFrontmatter.Parse(text);
            if (fm == null || IsBlank(fm.Description))
                return "INVALID: skill '" + slug + "' would not load - its SKILL.md frontmatter has no "
                    + "'description'. Add one with update_skill.";

            string name = !IsBlank(fm.Name) ? fm.Name : slug;
            return "OK: skill '" + slug + "' loads. name: " + name + "; description: " + fm.Description;
        }

        // ---- internals ----

        // The "not in a writable scope" error for a write/edit/delete: when the skill's SKILL.md isn't in
        // the target writable root. If the slug names a bundled (shipped, read-only) skill, say so and point
        // at create_skill to override it - the bare "does not exist" is misleading when the model can see and
        // read that bundled skill. forDelete tailors the verb (a bundled skill is overridden, not deleted).
        // The "not in a writable scope" error: a bundled shadow (read-only), or it lives in the other
        // writable scope, or it truly doesn't exist. Probes use the skill file shape (<slug>/SKILL.md); the
        // wording is shared with AgentWriter via WriterIo so the two can't drift.
        private SkillWriteException NotWritable(string slug, string targetScope, bool forDelete)
        {
            bool bundled = !string.IsNullOrEmpty(_bundledRoot)
                && File.Exists(Path.Combine(Path.Combine(_bundledRoot, slug), "SKILL.md"));
            string eff = WriterIo.NormalizeScope(targetScope, _defaultScope);
            string otherRoot = eff == "project" ? _userRoot : _projectRoot;
            string otherLabel = eff == "project" ? "user" : "project";
            bool inOther = !bundled && !string.IsNullOrEmpty(otherRoot)
                && File.Exists(Path.Combine(Path.Combine(otherRoot, slug), "SKILL.md"));
            return new SkillWriteException(WriterIo.NotWritableMessage("skill", slug, "create_skill", forDelete,
                bundled, inOther ? otherLabel : null, eff));
        }

        private string RootFor(string scope)
        {
            string s = (scope == null ? _defaultScope : scope.Trim().ToLowerInvariant());
            if (s.Length == 0) s = _defaultScope;
            if (s == "project")
            {
                if (string.IsNullOrEmpty(_projectRoot))
                    throw new SkillWriteException("no workspace folder is set for this conversation");
                return _projectRoot;
            }
            if (s == "user")
            {
                if (string.IsNullOrEmpty(_userRoot))
                    throw new SkillWriteException("no user-global skills root is configured for this conversation");
                return _userRoot;
            }
            throw new SkillWriteException("unknown scope '" + scope + "' (use 'project' or 'user')");
        }

        private static string RequireSlug(string slugIn)
        {
            string slug = SkillSlug.Make(slugIn);
            if (string.IsNullOrEmpty(slug)) throw new SkillWriteException("a valid slug is required");
            return slug;
        }

        // Frontmatter values are single-line by design (the CR/LF guard lives in WriterIo so it can't drift
        // from the agent writer's copy).
        private static void RequireSingleLine(string value, string field)
        {
            if (WriterIo.HasLineBreak(value))
                throw new SkillWriteException(field + " must be a single line (no line breaks)");
        }

        // XP cmd.exe reads .bat/.cmd bytes in the OEM/ANSI codepage, not UTF-8, so a non-ASCII character
        // in a script body would run garbled. Keep scripts ASCII-only (the same constraint as our sources).
        private static void RequireBatchAscii(string text)
        {
            if (text == null) return;
            for (int i = 0; i < text.Length; i++)
                if (text[i] > 0x7F)
                    throw new SkillWriteException("batch/cmd scripts must be ASCII - XP cmd.exe reads them "
                        + "in the OEM codepage, so non-ASCII characters would be garbled");
        }

        private static string BuildSkillMd(string name, string description, string body)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("---\n");
            // Omit the name line when there's no name, rather than synthesizing one - so an update/edit of
            // a name-less SKILL.md doesn't silently inject `name: <slug>` (the catalog falls back to the
            // slug for display anyway). create_skill requires a name, so created skills always have one.
            if (!IsBlank(name)) sb.Append("name: ").Append(name.Trim()).Append('\n');
            sb.Append("description: ").Append(description.Trim()).Append('\n');
            sb.Append("---\n\n");
            string b = body != null ? body : string.Empty;
            sb.Append(b);
            if (!b.EndsWith("\n")) sb.Append('\n');
            return sb.ToString();
        }

        private static string ToCrlf(string s)
        {
            return s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        }

        // Newline/edit-matching primitives delegate to WriterIo so they can't drift from AgentWriter's copy.
        private static string DetectNewline(string text) { return WriterIo.DetectNewline(text); }
        private static string NormalizeNewlines(string s, string nl) { return WriterIo.NormalizeNewlines(s, nl); }
        private static int CountOccurrences(string text, string sub) { return WriterIo.CountOccurrences(text, sub); }
        private static string ReplaceFirst(string text, string oldS, string newS) { return WriterIo.ReplaceFirst(text, oldS, newS); }

        private static void CollectFiles(string baseDir, string dir, List<string> rels)
        {
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { files = new string[0]; }
            foreach (string f in files) rels.Add(ToRel(baseDir, f));

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { subs = new string[0]; }
            foreach (string sub in subs) CollectFiles(baseDir, sub, rels);
        }

        private static string ToRel(string baseDir, string full)
        {
            string b = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string rel = full.StartsWith(b, StringComparison.OrdinalIgnoreCase) ? full.Substring(b.Length) : full;
            return rel.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        }

        private static bool IsBlank(string s)
        {
            return s == null || s.Trim().Length == 0;
        }

        // Atomic write (the swap-and-restore logic lives in WriterIo, shared with AgentWriter).
        private static void AtomicWrite(string path, string content) { WriterIo.AtomicWrite(path, content); }
    }
}
