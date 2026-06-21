using System;
using System.IO;
using System.Linq;
using System.Text;
using Ionic.Zip;

namespace GxPT
{
    internal static class ImportExportService
    {
        // Child-agent transcripts (stored separately from conversations on disk) are bundled into the
        // archive under this folder so they travel with the conversation. Conversation JSONs stay at the
        // archive root; on import this prefix is routed to the transcript store instead of the
        // Conversations folder. Archives written before this existed simply have no such folder.
        private const string TranscriptsArchivePrefix = "AgentTranscripts";

        // Core operations (no UI): throw on failure so callers can handle UX.
        public static void ExportAll(string sourceDir, string archivePath)
        {
            if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir))
                throw new InvalidOperationException("No conversations folder found to export.");
            bool hasAny = false;
            try { hasAny = Directory.GetFiles(sourceDir, "*.json").Length > 0; }
            catch { }
            if (!hasAny)
                throw new InvalidOperationException("There are no saved conversations to export.");

            using (var zip = new ZipFile())
            {
                zip.AlternateEncoding = Encoding.UTF8;
                zip.AlternateEncodingUsage = ZipOption.AsNecessary;
                zip.CompressionLevel = Ionic.Zlib.CompressionLevel.BestCompression;
                zip.AddDirectory(sourceDir, "");
                // Bundle every conversation's agent transcripts (best-effort; absent folder is fine).
                string transcriptsRoot = AgentTranscriptPersistence.RootDir();
                if (!string.IsNullOrEmpty(transcriptsRoot) && Directory.Exists(transcriptsRoot))
                {
                    try
                    {
                        if (Directory.GetFileSystemEntries(transcriptsRoot).Length > 0)
                            zip.AddDirectory(transcriptsRoot, TranscriptsArchivePrefix);
                    }
                    catch { }
                }
                zip.Save(archivePath);
            }
        }

        public static void ImportAll(string zipPath, string targetDir, bool overwriteExisting)
        {
            if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath))
                throw new FileNotFoundException("Archive not found.", zipPath);
            if (string.IsNullOrEmpty(targetDir))
                throw new ArgumentException("Target folder is required.", "targetDir");
            Directory.CreateDirectory(targetDir);

            // Extract to a temp staging folder so we can route the bundled AgentTranscripts subtree to the
            // transcript store while conversation JSONs (at the archive root) go to the Conversations folder.
            string staging = Path.Combine(Path.GetTempPath(), "GxPT-Import-" + Guid.NewGuid().ToString("N"));
            try
            {
                // Staging is fresh, so overwriting within it never clobbers user data; the caller's
                // overwriteExisting choice is honored when merging into the real destinations below.
                ZipSafe.SafeExtract(zipPath, staging, true);

                string transcriptsStaging = Path.Combine(staging, TranscriptsArchivePrefix);
                bool hasTranscripts = Directory.Exists(transcriptsStaging);

                // Conversation files (and any other root content) -> Conversations folder. The bundled
                // AgentTranscripts folder is handled separately and skipped here.
                foreach (string entry in Directory.GetFileSystemEntries(staging))
                {
                    string name = Path.GetFileName(entry);
                    if (hasTranscripts && Directory.Exists(entry) &&
                        string.Equals(name, TranscriptsArchivePrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (Directory.Exists(entry))
                        MergeDirectory(entry, Path.Combine(targetDir, name), overwriteExisting);
                    else
                        File.Copy(entry, Path.Combine(targetDir, name), overwriteExisting);
                }

                // Restore bundled transcripts into the store root (%AppData%/GxPT/AgentTranscripts).
                if (hasTranscripts)
                {
                    string transcriptsRoot = AgentTranscriptPersistence.RootDir();
                    if (!string.IsNullOrEmpty(transcriptsRoot))
                        MergeDirectory(transcriptsStaging, transcriptsRoot, overwriteExisting);
                }
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                catch { }
            }
        }

        public static void ExportSingle(string conversationFilePath, string archivePath)
        {
            ExportSingle(conversationFilePath, archivePath, null);
        }

        // convId lets us bundle just this conversation's agent transcripts (folder name == conversation
        // id). When null/empty or the folder is absent, only the conversation JSON is archived.
        public static void ExportSingle(string conversationFilePath, string archivePath, string convId)
        {
            if (string.IsNullOrEmpty(conversationFilePath) || !File.Exists(conversationFilePath))
                throw new FileNotFoundException("Conversation file not found.", conversationFilePath);
            using (var zip = new ZipFile())
            {
                zip.AlternateEncoding = Encoding.UTF8;
                zip.AlternateEncodingUsage = ZipOption.AsNecessary;
                zip.CompressionLevel = Ionic.Zlib.CompressionLevel.BestCompression;
                zip.AddFile(conversationFilePath, "");
                if (!string.IsNullOrEmpty(convId))
                {
                    string convDir = AgentTranscriptPersistence.ConvDirPath(convId);
                    if (!string.IsNullOrEmpty(convDir) && Directory.Exists(convDir))
                    {
                        try
                        {
                            if (Directory.GetFileSystemEntries(convDir).Length > 0)
                                zip.AddDirectory(convDir, TranscriptsArchivePrefix + "/" + Path.GetFileName(convDir));
                        }
                        catch { }
                    }
                }
                zip.Save(archivePath);
            }
        }

        // Recursively copy every file from sourceDir into destDir, creating subdirectories as needed.
        // File.Copy with overwriteExisting=false throws on a name clash, preserving the prior import
        // semantics (CreateNew) for callers that opt out of overwriting.
        private static void MergeDirectory(string sourceDir, string destDir, bool overwriteExisting)
        {
            Directory.CreateDirectory(destDir);
            foreach (string file in Directory.GetFiles(sourceDir))
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwriteExisting);
            foreach (string sub in Directory.GetDirectories(sourceDir))
                MergeDirectory(sub, Path.Combine(destDir, Path.GetFileName(sub)), overwriteExisting);
        }

        // Helpers
        public static string GetConversationsFolderPath()
        {
            try
            {
                var items = ConversationStore.ListAll();
                var first = items.FirstOrDefault();
                if (first != null && !string.IsNullOrEmpty(first.Path))
                {
                    var dir = Path.GetDirectoryName(first.Path);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        try { Directory.CreateDirectory(dir); }
                        catch { }
                        return dir;
                    }
                }
            }
            catch { }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dirFallback = Path.Combine(Path.Combine(appData, "GxPT"), "Conversations");
            try { Directory.CreateDirectory(dirFallback); }
            catch { }
            return dirFallback;
        }

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            try
            {
                var invalid = Path.GetInvalidFileNameChars();
                var sb = new StringBuilder(name.Length);
                for (int i = 0; i < name.Length; i++)
                {
                    char c = name[i];
                    if (invalid.Contains(c) || c == '\\' || c == '/') sb.Append('_');
                    else sb.Append(c);
                }
                string s = sb.ToString().Trim();
                if (s.Length == 0) return null;
                s = s.TrimEnd(' ', '.');
                if (s.Length == 0) return null;
                return s;
            }
            catch { return name; }
        }
    }
}
