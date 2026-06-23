using System;
using System.IO;
using System.Text;

namespace ExtensionsMcpServer
{
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

        // Normalizes a scope arg to "project"/"user", applying the writer's default for null/blank. (RootFor
        // validates the value; this is the post-validation normalization shared by the writers.)
        public static string NormalizeScope(string scope, string defaultScope)
        {
            string s = (scope == null ? defaultScope : scope.Trim().ToLowerInvariant());
            return s.Length == 0 ? defaultScope : s;
        }

        // The shared "can't write here" message for a missing write/edit/delete target, so both writers word
        // it identically (only the file-shape probe and exception type differ between them). noun is
        // "agent"/"skill"; createTool is "create_agent"/"create_skill". bundled: the slug names a shipped
        // read-only item (takes priority). otherScope: the writable scope where it actually lives, or null.
        // targetScope: the scope this call wrote to. Falls through to the plain "does not exist".
        public static string NotWritableMessage(string noun, string slug, string createTool, bool forDelete,
            bool bundled, string otherScope, string targetScope)
        {
            if (bundled)
                return noun + " '" + slug + "' is a bundled " + noun + " (shipped with the app) "
                    + (forDelete
                        ? "and can't be deleted; bundled " + noun + "s are read-only"
                        : "and can't be edited in place; create a project/user copy with " + createTool
                            + " (same slug) to override it");
            if (!string.IsNullOrEmpty(otherScope))
                return noun + " '" + slug + "' is in the '" + otherScope + "' scope, not '" + targetScope
                    + "'; pass scope:\"" + otherScope + "\" to " + (forDelete ? "delete" : "edit") + " it";
            return forDelete
                ? noun + " '" + slug + "' does not exist"
                : noun + " '" + slug + "' does not exist; " + createTool + " first";
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
