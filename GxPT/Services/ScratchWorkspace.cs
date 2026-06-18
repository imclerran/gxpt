using System;
using System.Collections.Generic;
using System.IO;

namespace GxPT
{
    // Per-conversation scratch working directories for the command MCP server when no workspace is
    // set. Each folderless conversation that uses the command server gets its own sandbox at
    // %AppData%/GxPT/scratch/<conversation_id>, which becomes the command server's GXPT_WORKDIR. Only
    // the command server runs here — files/git/msbuild require a real workspace and stay off. Scratch
    // directories are transient: they are removed when the conversation is deleted or when the
    // application closes (whichever comes first), so nothing here is meant to persist. The "scratch"
    // parent folder keeps these dirs from colliding with real GxPT data (Conversations/skills/mcp.json)
    // and lets shutdown wipe them as a group.
    internal static class ScratchWorkspace
    {
        private const string FolderName = "scratch";

        // Directories this process created, so they can all be removed on shutdown. Keyed by full
        // path (OrdinalIgnoreCase) to dedupe repeated EnsureDir calls for the same conversation.
        private static readonly object _gate = new object();
        private static readonly HashSet<string> _created =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // True when the scratch-command feature is on: the command server is enabled AND the user has
        // opted into running it in a scratch dir when no workspace is set. Both gates matter — a
        // scratch dir is only useful if the command server will actually launch in it.
        public static bool IsEnabled()
        {
            try
            {
                return AppSettings.GetBool("mcp_command_enabled", false)
                    && AppSettings.GetBool("mcp_command_scratch_enabled", false);
            }
            catch { return false; }
        }

        // %AppData%/GxPT/scratch (the parent of every conversation's scratch dir).
        public static string Root()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(Path.Combine(appData, "GxPT"), FolderName);
        }

        // The scratch path for a conversation id (NOT created). Null for an empty id.
        public static string PathFor(string conversationId)
        {
            string id = Sanitize(conversationId);
            if (string.IsNullOrEmpty(id)) return null;
            return Path.Combine(Root(), id);
        }

        // The scratch path for a conversation id, creating the directory (and remembering it for
        // shutdown cleanup) if needed. Null when the id is empty or the directory can't be created.
        public static string EnsureDir(string conversationId)
        {
            string path = PathFor(conversationId);
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                lock (_gate) { _created.Add(path); }
                return path;
            }
            catch { return null; }
        }

        // Delete one conversation's scratch directory (best-effort). Safe when it doesn't exist. The
        // caller must first stop the command server bound to it so the directory isn't held open.
        public static void Delete(string conversationId)
        {
            DeletePath(PathFor(conversationId));
        }

        public static void DeletePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch { }
            lock (_gate) { _created.Remove(path); }
        }

        // Remove every scratch directory this process created (application shutdown). Best-effort: a
        // directory still held open by a not-yet-exited child is skipped rather than throwing. Only
        // this process's dirs are touched, so a second running instance's scratch dirs are left alone.
        public static void DeleteAllCreated()
        {
            List<string> paths;
            lock (_gate) { paths = new List<string>(_created); _created.Clear(); }
            for (int i = 0; i < paths.Count; i++)
            {
                try { if (Directory.Exists(paths[i])) Directory.Delete(paths[i], true); }
                catch { }
            }
        }

        private static string Sanitize(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (char c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
            return id;
        }
    }
}
