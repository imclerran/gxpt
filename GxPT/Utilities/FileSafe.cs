using System;
using System.IO;
using System.Text;

namespace GxPT
{
    // Helpers for crash-safe file writes (write to a temp file, then atomically replace),
    // so a crash or power loss mid-write cannot leave a truncated/corrupt file in place.
    // Targets .NET 3.5 / C# 3.0.
    internal static class FileSafe
    {
        // Atomically writes text to 'path' using the given encoding. The content is written
        // to a sibling temp file first, then swapped into place. Falls back to a best-effort
        // direct overwrite if the underlying filesystem does not support the atomic replace.
        public static void WriteAllTextAtomic(string path, string contents, Encoding encoding)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            if (contents == null) contents = string.Empty;
            if (encoding == null) encoding = new UTF8Encoding(false);

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Temp file lives in the same directory to keep the replace on a single volume.
            string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, contents, encoding);

            try
            {
                if (File.Exists(path))
                {
                    // Atomic swap; no backup file requested.
                    File.Replace(tmp, path, null);
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            catch
            {
                // Some filesystems (e.g. certain network shares) don't support Replace/Move.
                // Fall back to a best-effort direct overwrite, then remove the temp file.
                try { File.Copy(tmp, path, true); }
                finally { TryDelete(tmp); }
                return;
            }

            // On success the temp file has been moved/replaced away; clean up just in case.
            TryDelete(tmp);
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }

        // Recursively copies every file and subdirectory from sourceDir into targetDir, overwriting files
        // that already exist. Creates targetDir (and intermediate dirs) as needed. Shared by the skill and
        // plugin importers so a single copy implementation covers both.
        public static void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            string[] files = Directory.GetFiles(sourceDir);
            for (int i = 0; i < files.Length; i++)
                File.Copy(files[i], Path.Combine(targetDir, Path.GetFileName(files[i])), true);
            string[] dirs = Directory.GetDirectories(sourceDir);
            for (int i = 0; i < dirs.Length; i++)
                CopyDirectory(dirs[i], Path.Combine(targetDir, Path.GetFileName(dirs[i])));
        }
    }
}
