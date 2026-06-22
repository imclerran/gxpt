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
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly string _projectRoot; // <workdir>/.gxpt/skills, or null when no workspace
        private readonly string _userRoot;    // %AppData%/GxPT/skills, or null if no user root is configured
        private readonly string _defaultScope; // scope used when a call omits it ("project" or "user")

        public SkillWriter(string projectRoot, string userRoot)
            : this(projectRoot, userRoot, "project")
        {
        }

        // defaultScope is the scope applied when a tool call omits `scope`. The workdir instance defaults
        // to "project"; the workdir-less instance defaults to "user" (project isn't reachable there).
        public SkillWriter(string projectRoot, string userRoot, string defaultScope)
        {
            _projectRoot = projectRoot;
            _userRoot = userRoot;
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
                throw new SkillWriteException("skill '" + slug + "' does not exist yet; create_skill first");

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
                throw new SkillWriteException("skill '" + slug + "' does not exist; create_skill first");

            string existing;
            try { existing = File.ReadAllText(file, Encoding.UTF8); }
            catch (Exception ex) { throw new SkillWriteException("could not read SKILL.md: " + ex.Message); }

            if (name != null) RequireSingleLine(name, "name");
            if (description != null) RequireSingleLine(description, "description");

            SkillFrontmatter fm = SkillFrontmatter.Parse(existing);
            string newName = name != null ? name : fm.Name; // may stay null -> name line omitted (no slug forced)
            string newDesc = description != null ? description : fm.Description;
            string newBody = body != null ? body : fm.Body;
            if (IsBlank(newDesc)) throw new SkillWriteException("description is required");

            AtomicWrite(file, BuildSkillMd(newName, newDesc, newBody));
            return "Updated skill '" + slug + "'. Changes apply on your next message.";
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
                throw new SkillWriteException("skill '" + slug + "' does not exist; create_skill first");
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
                    throw new SkillWriteException("old_string not found in SKILL.md's body (the body is what "
                        + "edit_skill_file changes; use update_skill for the name/description)");
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
                throw new SkillWriteException("skill '" + slug + "' does not exist");

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
                throw new SkillWriteException("skill '" + slug + "' does not exist");

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

        // Frontmatter values are single-line by design: a CR/LF would close the "---" block early or
        // inject keys, producing a forged/unloadable SKILL.md (the very thing these tools exist to prevent).
        private static void RequireSingleLine(string value, string field)
        {
            if (value != null && (value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0))
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

        private static string DetectNewline(string text)
        {
            return text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
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

        // Atomic write (mirrors MemoryStore): temp file then replace/move; creates parent dirs.
        // Write content to a temp file, then swap it into place so a failed write never leaves a
        // half-written or destroyed target. A leaked temp is cleaned up in finally (it would otherwise
        // surface in list_skill_files). The fallback path moves the original aside FIRST and restores it
        // if the swap fails, so the original is never deleted before the new content has landed.
        private static void AtomicWrite(string path, string content)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllText(tmp, content, Utf8NoBom);

                if (!File.Exists(path)) { File.Move(tmp, path); return; }

                // Prefer a true atomic replace.
                try { File.Replace(tmp, path, null); return; }
                catch { }

                // Replace can fail (cross-volume, AV, lock). Move the original aside, swap in the new
                // file, and restore the original if that swap fails - so a failed write never loses data.
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
