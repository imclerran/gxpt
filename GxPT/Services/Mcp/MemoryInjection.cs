using System;
using System.IO;
using System.Text;

namespace GxPT
{
    // Host side of the memory feature: reads the workspace's primary memory index
    // (<workdir>/.gxpt/memory/memory.md) and wraps it with framing for injection as an ephemeral
    // system message (McpChatOrchestrator.MemorySystemMessageProvider). The MemoryMcpServer is the
    // only writer; this is read-only. All memory framing lives here so a disabled memory system
    // leaves no trace in context (design M5/M6, sec.5).
    internal static class MemoryInjection
    {
        // The shared .gxpt project home (reused by skills/ and agents/); memory lives in its own
        // "memory" subfolder beneath it, kept in sync with MemoryMcpServer's MemoryConfig.
        public const string MemoryDirName = ".gxpt";
        public const string MemorySubDirName = "memory";
        public const string IndexFileName = "memory.md";

        // Cap the read so a hand-edited or runaway index can't bloat the prompt; the store keeps it
        // far smaller than this in practice.
        private const int MaxIndexChars = 16 * 1024;

        // Build the memory system block for a workspace, or null when there is no workspace.
        // Returns framing even when the index is empty, so the model knows the capability exists.
        public static string Build(string workingDir)
        {
            if (string.IsNullOrEmpty(workingDir)) return null;

            string index = ReadIndex(workingDir);

            StringBuilder sb = new StringBuilder();
            sb.Append("# Persistent memory\n\n");
            sb.Append("You have a persistent memory for this workspace that you manage ONLY through the ");
            sb.Append("memory tools (remember / read_memory / update_memory / forget / consolidate). Do ");
            sb.Append("NOT read or write memory by editing files directly, and do not use the file tools ");
            sb.Append("for it — always go through the memory tools, which keep the index consistent.\n\n");
            sb.Append("Use the memory system as something you maintain, not just consult. As you work, ");
            sb.Append("proactively remember durable facts about this workspace — conventions, architecture, ");
            sb.Append("decisions, and hard-won gotchas that may waste effort in the future — the moment you ");
            sb.Append("learn them, rather than only when explicitly asked. Skip transient or easily ");
            sb.Append("re-derived details, and prefer updating or consolidating existing entries over ");
            sb.Append("adding near-duplicates. Record a new fact with `remember`; read the detail behind ");
            sb.Append("an index line with `read_memory`; revise with `update_memory`; remove with ");
            sb.Append("`forget`; and merge related entries with `consolidate` to keep the index small. ");
            sb.Append("Do not mention this memory unless it is relevant.\n\n");

            if (string.IsNullOrEmpty(index))
            {
                sb.Append("Current memories: (none recorded yet)");
            }
            else
            {
                sb.Append("Current memories:\n");
                sb.Append(index);
            }
            return sb.ToString();
        }

        private static string ReadIndex(string workingDir)
        {
            try
            {
                string path = Path.Combine(
                    Path.Combine(Path.Combine(workingDir, MemoryDirName), MemorySubDirName),
                    IndexFileName);
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrEmpty(text)) return null;
                if (text.Length > MaxIndexChars)
                    text = text.Substring(0, MaxIndexChars) + "\n[index truncated]";
                return text.TrimEnd('\n', '\r');
            }
            catch
            {
                return null;
            }
        }
    }
}
