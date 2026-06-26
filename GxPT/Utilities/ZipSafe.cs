using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Ionic.Zip;

namespace GxPT
{
    /// <summary>
    /// Safe wrapper around DotNetZip to mitigate Zip-Slip (path traversal) and related issues.
    /// Targets .NET 3.5 / C# 3.0.
    /// </summary>
    internal static class ZipSafe
    {
        // Regex checks for early failure before extraction
        private static readonly Regex AbsolutePathRegex = new Regex(
            // Drive root (C:), UNC (\\\\server), or root slash (/ or \\)
            @"^([A-Za-z]:|\\\\|/|\\)",
            RegexOptions.Compiled);

        private static readonly Regex TraversalRegex = new Regex(
            // Block only parent directory traversal segments ".."
            @"(^|[\\/])\.\.([\\/]|$)",
            RegexOptions.Compiled);

        private static readonly Regex ColonRegex = new Regex(":", RegexOptions.Compiled);

        // Heuristic: catch percent-encoded '..' (defense-in-depth)
        private static readonly Regex EncodedDotsRegex = new Regex("%2e%2e", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Reserved device names on Windows (blocked at leaf, even with extensions)
        private static readonly HashSet<string> ReservedDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON","PRN","AUX","NUL",
            "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
            "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
        };

        /// <summary>
        /// Extracts a zip file to the given destination directory, enforcing robust containment checks
        /// to prevent path traversal. Rooted/absolute paths are rejected, any ':' is disallowed, dot/dot-dot
        /// segments are rejected, and the canonical combined path must remain inside destination.
        /// The leaf name is validated against Windows reserved device names.
        /// </summary>
        /// <param name="zipPath">Path to the .zip archive.</param>
        /// <param name="destinationDirectory">Target folder to extract into.</param>
        /// <param name="overwriteExisting">If true, existing files are overwritten; else CreateNew is used.</param>
        public static void SafeExtract(string zipPath, string destinationDirectory, bool overwriteExisting)
        {
            if (string.IsNullOrEmpty(zipPath)) throw new ArgumentNullException("zipPath");
            if (string.IsNullOrEmpty(destinationDirectory)) throw new ArgumentNullException("destinationDirectory");
            if (!File.Exists(zipPath)) throw new FileNotFoundException("Zip not found", zipPath);

            // Normalize destination root (ensure trailing separator for prefix checks)
            string destRoot = Path.GetFullPath(destinationDirectory);
            destRoot = TrimTrailingSeparators(destRoot);
            string destRootWithSep = destRoot + Path.DirectorySeparatorChar;

            Directory.CreateDirectory(destRoot);

            // First pass: validate all entries to fail early
            using (var zip = ZipFile.Read(zipPath, new ReadOptions { Encoding = Encoding.UTF8 }))
            {
                foreach (var entry in zip)
                {
                    ValidateEntryPath(entry);
                    // Compute the canonical target path and verify containment
                    GetValidatedTargetPath(entry, destRootWithSep); // throws on invalid
                }
            }

            // Second pass: extract safely using our computed target paths
            using (var zip = ZipFile.Read(zipPath, new ReadOptions { Encoding = Encoding.UTF8 }))
            {
                foreach (var entry in zip)
                {
                    string targetFullPath = GetValidatedTargetPath(entry, destRootWithSep);

                    if (entry.IsDirectory)
                    {
                        Directory.CreateDirectory(targetFullPath);
                        continue;
                    }

                    // Defense-in-depth: ensure no reparse/junction components along the path
                    EnsureNoReparseParents(targetFullPath, destRootWithSep);

                    // Ensure directory exists for the file
                    string dir = Path.GetDirectoryName(targetFullPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    // Use OpenReader -> FileStream to avoid DotNetZip internal path resolution
                    using (var reader = entry.OpenReader())
                    using (var fs = new FileStream(
                        targetFullPath,
                        overwriteExisting ? FileMode.Create : FileMode.CreateNew,
                        FileAccess.Write, FileShare.None))
                    {
                        CopyStream(reader, fs);
                    }
                }
            }
        }

        /// <summary>
        /// Returns true if the archive holds any file entry whose normalized name (forward slashes, no
        /// leading slash) satisfies <paramref name="nameMatches"/>. A best-effort routing helper shared by
        /// the skill and plugin importers: any read failure yields false rather than throwing.
        /// </summary>
        public static bool ContainsEntry(string archivePath, Predicate<string> nameMatches)
        {
            if (string.IsNullOrEmpty(archivePath) || nameMatches == null) return false;
            try
            {
                using (var zip = ZipFile.Read(archivePath, new ReadOptions { Encoding = Encoding.UTF8 }))
                {
                    foreach (ZipEntry entry in zip)
                    {
                        if (entry == null || entry.IsDirectory) continue;
                        string name = (entry.FileName ?? string.Empty).Replace('\\', '/').TrimStart('/');
                        if (nameMatches(name)) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static void ValidateEntryPath(ZipEntry entry)
        {
            if (entry == null) throw new InvalidOperationException("Null zip entry.");
            string name = entry.FileName ?? string.Empty;

            // Quick regex-based rejects
            if (ColonRegex.IsMatch(name))
                throw new InvalidDataException("Entry name contains colon (:), which is not allowed: " + name);

            if (AbsolutePathRegex.IsMatch(name))
                throw new InvalidDataException("Entry has an absolute or rooted path: " + name);

            if (TraversalRegex.IsMatch(name))
                throw new InvalidDataException("Entry contains parent directory traversal ('..'): " + name);

            if (EncodedDotsRegex.IsMatch(name))
                throw new InvalidDataException("Entry contains encoded parent traversal ('%2e%2e'): " + name);

            // Control characters are suspicious in file names
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c < 32)
                    throw new InvalidDataException("Entry name contains control characters: " + name);
            }

            // Leaf device name check (Windows)
            string leaf = name;
            // Normalize separators for leaf extraction only
            leaf = leaf.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            leaf = leaf.TrimEnd(Path.DirectorySeparatorChar);
            string leafName = Path.GetFileName(leaf);
            if (!string.IsNullOrEmpty(leafName))
            {
                string baseName = Path.GetFileNameWithoutExtension(leafName);
                if (!string.IsNullOrEmpty(baseName) && ReservedDeviceNames.Contains(baseName))
                {
                    throw new InvalidDataException("Entry uses a reserved device name: " + leafName);
                }
                // Trailing dot or space in leaf can cause path ambiguity on Windows
                if (leafName.EndsWith(" ") || leafName.EndsWith("."))
                    throw new InvalidDataException("Entry leaf name ends with a space or dot: " + leafName);
            }
        }

        private static string GetValidatedTargetPath(ZipEntry entry, string destRootWithSep)
        {
            string name = entry.FileName ?? string.Empty;
            // Normalize to platform separator, strip any leading separators
            string rel = name.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            rel = TrimLeadingSeparators(rel);

            if (string.IsNullOrEmpty(rel))
                throw new InvalidDataException("Entry has an empty normalized path.");

            // Extra defense: if still rooted somehow, reject
            if (Path.IsPathRooted(rel))
                throw new InvalidDataException("Entry path is rooted after normalization: " + name);

            // Combine and canonicalize
            string combined = Path.Combine(destRootWithSep, rel);
            string full = Path.GetFullPath(combined);

            // Ensure containment
            string root = TrimTrailingSeparators(destRootWithSep);
            string rootWithSep = root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Entry would extract outside destination: " + name);
            }

            // If entry indicates directory (name ends with /), ensure our path is a directory path
            if (entry.IsDirectory)
            {
                // Ensure a trailing separator for directory creation consistency
                full = TrimTrailingSeparators(full) + Path.DirectorySeparatorChar;
            }

            return full;
        }

        // Refuse extracting through junctions/reparse points under destination (defense-in-depth)
        private static void EnsureNoReparseParents(string targetFullPath, string destRootWithSep)
        {
            try
            {
                string stop = TrimTrailingSeparators(destRootWithSep);
                DirectoryInfo dir = new DirectoryInfo(Path.GetDirectoryName(TrimTrailingSeparators(targetFullPath)));
                while (dir != null)
                {
                    string cur = TrimTrailingSeparators(dir.FullName);
                    if (string.Equals(cur, stop, StringComparison.OrdinalIgnoreCase)) break;
                    if (dir.Exists)
                    {
                        FileAttributes a = FileAttributes.Normal;
                        try { a = dir.Attributes; }
                        catch { a = FileAttributes.Normal; }
                        if ((a & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException("Reparse point encountered in path: " + dir.FullName);
                    }
                    dir = dir.Parent;
                }
            }
            catch (Exception ex)
            {
                // If we cannot verify, fail closed
                throw new InvalidDataException("Failed reparse point validation: " + ex.Message);
            }
        }

        private static string TrimLeadingSeparators(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            char ds = Path.DirectorySeparatorChar;
            char ads = Path.AltDirectorySeparatorChar;
            int i = 0;
            while (i < path.Length && (path[i] == ds || path[i] == ads)) i++;
            return i > 0 ? path.Substring(i) : path;
        }

        private static string TrimTrailingSeparators(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            char ds = Path.DirectorySeparatorChar;
            char ads = Path.AltDirectorySeparatorChar;
            int i = path.Length - 1;
            while (i >= 0 && (path[i] == ds || path[i] == ads)) i--;
            return path.Substring(0, i + 1);
        }

        private static void CopyStream(Stream input, Stream output)
        {
            byte[] buffer = new byte[81920]; // 80KB buffer
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
            }
        }
    }
}
