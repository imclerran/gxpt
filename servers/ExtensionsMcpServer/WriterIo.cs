using System;
using System.IO;
using System.Text;

namespace ExtensionsMcpServer
{
    /// <summary>The mutating operation a "not in a writable scope" error is reported for - it tailors the
    /// verb and the bundled-item guidance (a bundled item is overridden, deleted, or renamed differently).</summary>
    internal enum WriterOp { Edit, Delete, Rename }

    /// <summary>
    /// Shared file/string primitives for the two authoring writers (SkillWriter, AgentWriter), so the
    /// delicate logic lives ONCE. Before this, each writer carried its own copy of the atomic-write
    /// swap-and-restore dance, the CR/LF frontmatter-injection guard, and the edit_* occurrence/replace
    /// helpers - a classic source of one-sided fixes (harden one writer, leave the other behind). Pure /
    /// IO-only and throw no domain exception, so callers keep their own SkillWriteException/AgentWriteException.
    /// </summary>
    internal static class WriterIo
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        // True when a value contains a CR or LF. Frontmatter values are single-line by design: a line break
        // would close the "---" block early or inject keys, forging an unloadable file. Callers throw their
        // own domain exception on true; centralizing the CHECK keeps the guard from drifting between writers.
        public static bool HasLineBreak(string value)
        {
            return value != null && (value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0);
        }

        // True when a human NAME and a kebab SLUG denote the same handle, ignoring the word boundaries that
        // legitimately differ between Title Case / acronyms and kebab-case. Compares the lowercase
        // alphanumeric run of each, so "GitHub Researcher" matches "github-researcher" while "Code Reviewer"
        // does NOT match "code-review". The slug stays the stable identity/handle; the name is its display
        // form, and this keeps the two from drifting (centralized so both writers enforce it identically).
        public static bool NameMatchesSlug(string name, string slug)
        {
            return string.Equals(AlnumLower(name), AlnumLower(slug), StringComparison.Ordinal);
        }

        // The lowercase [a-z0-9] run of a string, all other characters dropped (the boundary-insensitive
        // key NameMatchesSlug compares on).
        private static string AlnumLower(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 'A' && c <= 'Z') sb.Append((char)(c - 'A' + 'a'));
                else if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            }
            return sb.ToString();
        }

        // A display name derived from a kebab slug: each hyphen segment capitalized, joined by spaces
        // ("code-explore" -> "Code Explore"). A reasonable default when a rename supplies no name; acronym
        // casing (e.g. "GitHub") is lost, so callers can pass an explicit name to refine it.
        public static string TitleCaseFromSlug(string slug)
        {
            if (string.IsNullOrEmpty(slug)) return slug;
            string[] parts = slug.Split('-');
            StringBuilder sb = new StringBuilder(slug.Length);
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p.Length == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(char.ToUpperInvariant(p[0]));
                if (p.Length > 1) sb.Append(p.Substring(1));
            }
            return sb.Length == 0 ? slug : sb.ToString();
        }

        // The shared "name and slug must stay aligned" message, worded once so the two writers can't drift.
        // noun is "agent"/"skill"; createTool is "create_agent"/"create_skill". On CREATE both sides are
        // still free, so it offers to fix either; on UPDATE the slug is the fixed identity, so it points at
        // rename_<noun> for a handle change (update only re-cases the display name).
        public static string NameSlugMismatchMessage(string noun, string name, string slug, bool isCreate,
            string createTool)
        {
            string derived = SkillSlug.Make(name);
            if (string.IsNullOrEmpty(derived)) derived = slug;
            if (isCreate)
                return "name '" + name + "' and slug '" + slug + "' don't match - the slug must be the name "
                    + "in kebab-case so the two stay aligned. Either set slug to '" + derived
                    + "', or change name to match slug '" + slug + "'.";
            return "name '" + name + "' doesn't match this " + noun + "'s slug '" + slug + "' - the display "
                + "name must stay aligned with the slug. update_" + noun + " only re-cases the name; to change "
                + "the handle, use rename_" + noun + " (e.g. new_slug '" + derived + "').";
        }

        // Normalizes a scope arg to "project"/"user", applying the writer's default for null/blank. (RootFor
        // validates the value; this is the post-validation normalization shared by the writers.)
        public static string NormalizeScope(string scope, string defaultScope)
        {
            string s = (scope == null ? defaultScope : scope.Trim().ToLowerInvariant());
            return s.Length == 0 ? defaultScope : s;
        }

        // The shared "can't write here" message for a missing edit/delete/rename target, so both writers word
        // it identically (only the file-shape probe and exception type differ between them). noun is
        // "agent"/"skill"; createTool is "create_agent"/"create_skill". op tailors the verb and the bundled
        // guidance. bundled: the slug names a shipped read-only item (takes priority). otherScope: the
        // writable scope where it actually lives, or null. targetScope: the scope this call wrote to.
        public static string NotWritableMessage(string noun, string slug, string createTool, WriterOp op,
            bool bundled, string otherScope, string targetScope)
        {
            if (bundled)
            {
                string tail;
                switch (op)
                {
                    case WriterOp.Delete:
                        tail = "and can't be deleted; bundled " + noun + "s are read-only";
                        break;
                    case WriterOp.Rename:
                        tail = "and can't be renamed; create a project/user copy under the new slug with "
                            + createTool + " instead";
                        break;
                    default: // Edit
                        tail = "and can't be edited in place; create a project/user copy with " + createTool
                            + " (same slug) to override it";
                        break;
                }
                return noun + " '" + slug + "' is a bundled " + noun + " (shipped with the app) " + tail;
            }
            if (!string.IsNullOrEmpty(otherScope))
            {
                string verb = op == WriterOp.Delete ? "delete" : op == WriterOp.Rename ? "rename" : "edit";
                return noun + " '" + slug + "' is in the '" + otherScope + "' scope, not '" + targetScope
                    + "'; pass scope:\"" + otherScope + "\" to " + verb + " it";
            }
            // Only an edit can be resolved by creating the item first; a delete/rename of something absent
            // just doesn't exist.
            return op == WriterOp.Edit
                ? noun + " '" + slug + "' does not exist; " + createTool + " first"
                : noun + " '" + slug + "' does not exist";
        }

        public static string DetectNewline(string text)
        {
            return text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
        }

        public static string NormalizeNewlines(string s, string nl)
        {
            if (s == null) return null;
            string lf = s.Replace("\r\n", "\n").Replace("\r", "\n");
            return nl == "\n" ? lf : lf.Replace("\n", nl);
        }

        public static int CountOccurrences(string text, string sub)
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

        public static string ReplaceFirst(string text, string oldS, string newS)
        {
            int idx = text.IndexOf(oldS, StringComparison.Ordinal);
            if (idx < 0) return text;
            return text.Substring(0, idx) + newS + text.Substring(idx + oldS.Length);
        }

        // Atomic write: temp file then replace/move; creates parent dirs. A failed write never leaves a
        // half-written or destroyed target. The fallback moves the original aside FIRST and restores it if
        // the swap fails, so a failed write never loses data. UTF-8 no BOM.
        public static void AtomicWrite(string path, string content)
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
