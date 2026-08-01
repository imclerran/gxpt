using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mcp35.Core.Security;
using Newtonsoft.Json;

namespace GxPT
{
    internal static class ConversationStore
    {
        private const string FolderName = "Conversations";
        private const string FileExt = ".json";

        // Newtonsoft (D16): omit null members so non-tool messages keep their compact shape and
        // existing files stay byte-compatible in spirit. Reads remain backward-compatible — the
        // PascalCase property names match the previous JavaScriptSerializer output, and Newtonsoft
        // parses both ISO and the legacy "\/Date(...)\/" timestamps.
        private static readonly JsonSerializerSettings _jsonSettings =
            new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };

        // Per-file metadata cache for ListAll, so repeated sidebar refreshes don't re-read and
        // re-parse every conversation file. Entries are keyed by path and validated against the
        // file's last-write timestamp, so a stale entry is refreshed automatically when the file
        // changes, and entries for deleted files are pruned.
        private static readonly object _listCacheGate = new object();
        private static readonly Dictionary<string, CachedListMeta> _listCache =
            new Dictionary<string, CachedListMeta>(StringComparer.OrdinalIgnoreCase);

        private sealed class CachedListMeta
        {
            public DateTime StampUtc;
            public ConversationListItem Item;
        }

        // Lightweight DTO for listing: only the fields the sidebar needs, so we avoid
        // allocating the (potentially large) message list when reading metadata.
        private sealed class ConversationMetaDto
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string SelectedModel { get; set; }
            public int? ZdrFirstMessageIndex { get; set; }
            public DateTime LastUpdated { get; set; }
        }

        private static string GetRoot()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string root = Path.Combine(appData, "GxPT");
            root = Path.Combine(root, FolderName);
            try { if (!Directory.Exists(root)) Directory.CreateDirectory(root); }
            catch { }
            return root;
        }

        private static string SanitizeId(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
            return id;
        }

        public static string GetPathForId(string id)
        {
            id = SanitizeId(id);
            if (string.IsNullOrEmpty(id)) return null;
            return Path.Combine(GetRoot(), id + FileExt);
        }

        public static string EnsureConversationId(Conversation convo)
        {
            if (convo == null) return null;
            if (!string.IsNullOrEmpty(convo.Id)) return convo.Id;
            convo.Id = Guid.NewGuid().ToString("N");
            return convo.Id;
        }

        public static void Save(Conversation convo)
        {
            if (convo == null) return;
            // Ephemeral (temporary) conversations never touch disk - this is the single chokepoint every
            // save path funnels through, so gating here makes "unsaved" hold regardless of which caller
            // (turn completion, close, detached finalize, etc.) reaches for a save.
            if (convo.Ephemeral) return;
            string json = ToJson(convo);
            if (json == null) return;

            string path = GetPathForId(convo.Id);
            if (string.IsNullOrEmpty(path)) return;
            // Crash-safe write so a failure mid-save can't corrupt the conversation file.
            // UTF-8 without BOM matches the default File.WriteAllText behavior used previously.
            FileSafe.WriteAllTextAtomic(path, json, new UTF8Encoding(false));
        }

        // Serializes a conversation to its on-disk JSON (split out from Save so it is unit-testable
        // without touching the filesystem).
        internal static string ToJson(Conversation convo)
        {
            if (convo == null) return null;
            EnsureConversationId(convo);

            var dto = new ConversationDto
            {
                Id = convo.Id,
                Name = convo.Name,
                SelectedModel = convo.SelectedModel,
                WorkingDir = convo.WorkingDir,
                // Current directory (host `cd`): stored anchor-RELATIVE with '/' separators, omitted
                // at the anchor. Relative keeps "current is within the anchor" structural in the file
                // format itself; an out-of-anchor runtime value (a bug upstream) serializes as null
                // rather than persisting an escape.
                CurrentDir = SerializeCurrentDir(convo.WorkingDir, convo.CurrentDir),
                WorkspaceStripDismissed = convo.WorkspaceStripDismissed,
                ContinuedFromCompaction = convo.ContinuedFromCompaction,
                Zdr = convo.Zdr,
                ZdrFirstMessageIndex = convo.ZdrFirstMessageIndex,
                SkillsFeatureOff = convo.SkillsFeatureOff,
                AgentsEnabled = convo.AgentsEnabled,
                // Omit an empty map so untouched conversations stay clean (NullValueHandling.Ignore).
                SkillOverrides = (convo.SkillOverrides != null && convo.SkillOverrides.Count > 0)
                    ? convo.SkillOverrides : null,
                // Revealed-tool list (prompt caching): persisted so a reopened conversation resumes
                // with the same tools array instead of silently starting cold. Omitted when empty.
                RevealedTools = (convo.RevealedTools != null && convo.RevealedTools.Count > 0)
                    ? convo.RevealedTools : null,
                CacheWarmProvider = string.IsNullOrEmpty(convo.CacheWarmProvider)
                    ? null : convo.CacheWarmProvider,
                // Usage accumulators (status bar): nullable in the DTO so untouched/legacy
                // conversations stay clean (NullValueHandling.Ignore drops the nulls).
                TotalCost = convo.TotalCost != 0 ? (decimal?)convo.TotalCost : null,
                TotalCacheDiscount = convo.TotalCacheDiscount != 0 ? (decimal?)convo.TotalCacheDiscount : null,
                TotalPromptTokens = convo.TotalPromptTokens != 0 ? (long?)convo.TotalPromptTokens : null,
                TotalCachedTokens = convo.TotalCachedTokens != 0 ? (long?)convo.TotalCachedTokens : null,
                TotalCompletionTokens = convo.TotalCompletionTokens != 0 ? (long?)convo.TotalCompletionTokens : null,
                TotalReasoningTokens = convo.TotalReasoningTokens != 0 ? (long?)convo.TotalReasoningTokens : null,
                LastPromptTokens = convo.LastPromptTokens != 0 ? (int?)convo.LastPromptTokens : null,
                LastCachedTokens = convo.LastCachedTokens != 0 ? (int?)convo.LastCachedTokens : null,
                LastCacheWriteTokens = convo.LastCacheWriteTokens != 0 ? (int?)convo.LastCacheWriteTokens : null,
                LastUpdated = convo.LastUpdated,
                Messages = convo.History.Select(m => ToMessageDto(m)).ToList()
            };
            return JsonConvert.SerializeObject(dto, _jsonSettings);
        }

        // ---- current-directory (host `cd`) persistence ----
        //
        // The on-disk form of Conversation.CurrentDir is the path RELATIVE to the workspace anchor
        // (forward slashes), omitted entirely at the anchor. The pair (WorkingDir, CurrentDir) then
        // stays coherent as a unit: the file cannot express a current dir outside its own anchor
        // (Deserialize routes through PathSandbox.Resolve, which also rejects absolute/drive-form
        // input from a hand-edited file), and the stored subpath survives the anchor being re-picked
        // at a moved location. Both directions fail SOFT to null ("at the anchor"): a current dir is
        // a convenience worth dropping, never worth failing a conversation save/load over. Serialize
        // and Revalidate share PathSandbox.RelativeSubdirOrNull for the containment + at-anchor test,
        // so save, load, and the model-facing tail (CurrentDirContextBlock) can't drift on that rule.

        // Runtime absolute -> persisted anchor-relative. Null when at the anchor, when there is no
        // workspace, or when the value does not sit inside the anchor (a bug upstream - never
        // persist an escape).
        internal static string SerializeCurrentDir(string workingDir, string currentDirAbs)
        {
            if (string.IsNullOrEmpty(workingDir)) return null;
            try
            {
                string ignored;
                string rel = new PathSandbox(workingDir, "workspace root")
                    .RelativeSubdirOrNull(currentDirAbs, out ignored);
                return rel == null ? null : rel.Replace('\\', '/');
            }
            catch { return null; }
        }

        // Persisted anchor-relative -> runtime absolute. Null when absent, when there is no
        // workspace, or when the stored value is malformed or escapes the anchor
        // (PathSandbox.Resolve throws on those).
        internal static string DeserializeCurrentDir(string workingDir, string currentDirRel)
        {
            if (string.IsNullOrEmpty(workingDir) || string.IsNullOrEmpty(currentDirRel)) return null;
            try
            {
                var sb = new PathSandbox(workingDir, "workspace root");
                string full = sb.Resolve(currentDirRel.Replace('/', Path.DirectorySeparatorChar));
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                // "." (or an equivalent) resolves to the anchor itself: normalize to null.
                return string.Equals(full, sb.Root, StringComparison.OrdinalIgnoreCase) ? null : full;
            }
            catch { return null; }
        }

        // Adoption-time revalidation of a restored current dir (MainForm.ApplyLoadedWorkingDir):
        // returns the canonicalized absolute path when it is still a live subdirectory of the anchor
        // - within the anchor and not the anchor itself (containment re-checked here via the shared
        // RelativeSubdirOrNull, not just trusted from DeserializeCurrentDir; the layers never assume
        // another is the only defense, see mcp-architecture.md sec.9), and still EXISTING on disk (a
        // worktree pruned or a subdir deleted while the app was closed) - else null ("start at the
        // anchor").
        internal static string RevalidateCurrentDir(string workingDir, string currentDirAbs)
        {
            if (string.IsNullOrEmpty(workingDir)) return null;
            try
            {
                string full;
                string rel = new PathSandbox(workingDir, "workspace root")
                    .RelativeSubdirOrNull(currentDirAbs, out full);
                return (rel != null && Directory.Exists(full)) ? full : null;
            }
            catch { return null; }
        }

        private static MessageDto ToMessageDto(ChatMessage m)
        {
            var dto = new MessageDto
            {
                Role = m.Role,
                Content = m.Content,
                Attachments = (m.Attachments != null && m.Attachments.Count > 0) ? m.Attachments : null,
                ToolCallId = m.ToolCallId
            };
            if (m.ToolCalls != null && m.ToolCalls.Count > 0)
            {
                var calls = new List<ToolCallDto>();
                foreach (var c in m.ToolCalls)
                {
                    if (c == null) continue;
                    calls.Add(new ToolCallDto { Id = c.Id, Name = c.Name, ArgumentsJson = c.ArgumentsJson });
                }
                dto.ToolCalls = calls;
            }
            return dto;
        }

        public static Conversation Load(OpenRouterClient client, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                var dto = JsonConvert.DeserializeObject<ConversationDto>(json);
                return FromDto(client, dto, path);
            }
            catch { return null; }
        }

        public static Conversation LoadFromJson(OpenRouterClient client, string json)
        {
            try
            {
                if (string.IsNullOrEmpty(json)) return null;
                var dto = JsonConvert.DeserializeObject<ConversationDto>(json);
                return FromDto(client, dto, null);
            }
            catch { return null; }
        }

        private static Conversation FromDto(OpenRouterClient client, ConversationDto dto, string path)
        {
            if (dto == null) return null;
            var convo = new Conversation(client)
            {
                Id = dto.Id,
                Name = dto.Name ?? "New Conversation",
                SelectedModel = dto.SelectedModel,
                WorkingDir = dto.WorkingDir,
                // Absent in older files -> null (start at the anchor). Structural validation only
                // (containment via PathSandbox); the folder's EXISTENCE is checked at adoption time
                // (MainForm.ApplyLoadedWorkingDir), the moment the workspace is actually bound.
                CurrentDir = DeserializeCurrentDir(dto.WorkingDir, dto.CurrentDir),
                WorkspaceStripDismissed = dto.WorkspaceStripDismissed,
                ContinuedFromCompaction = dto.ContinuedFromCompaction,
                Zdr = dto.Zdr,
                // Absent in older files -> not latched (-1), never index 0.
                ZdrFirstMessageIndex = dto.ZdrFirstMessageIndex.HasValue ? dto.ZdrFirstMessageIndex.Value : -1,
                SkillsFeatureOff = dto.SkillsFeatureOff,
                AgentsEnabled = dto.AgentsEnabled,
                SkillOverrides = dto.SkillOverrides != null
                    ? new Dictionary<string, bool>(dto.SkillOverrides, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
                // Absent in older files -> empty list (nothing revealed yet).
                RevealedTools = dto.RevealedTools != null
                    ? new List<string>(dto.RevealedTools) : new List<string>(),
                CacheWarmProvider = dto.CacheWarmProvider,
                // Absent in older files -> zeroes (no usage recorded yet).
                TotalCost = dto.TotalCost.HasValue ? dto.TotalCost.Value : 0,
                TotalCacheDiscount = dto.TotalCacheDiscount.HasValue ? dto.TotalCacheDiscount.Value : 0,
                TotalPromptTokens = dto.TotalPromptTokens.HasValue ? dto.TotalPromptTokens.Value : 0,
                TotalCachedTokens = dto.TotalCachedTokens.HasValue ? dto.TotalCachedTokens.Value : 0,
                TotalCompletionTokens = dto.TotalCompletionTokens.HasValue ? dto.TotalCompletionTokens.Value : 0,
                TotalReasoningTokens = dto.TotalReasoningTokens.HasValue ? dto.TotalReasoningTokens.Value : 0,
                LastPromptTokens = dto.LastPromptTokens.HasValue ? dto.LastPromptTokens.Value : 0,
                LastCachedTokens = dto.LastCachedTokens.HasValue ? dto.LastCachedTokens.Value : 0,
                LastCacheWriteTokens = dto.LastCacheWriteTokens.HasValue ? dto.LastCacheWriteTokens.Value : 0,
                LastUpdated = dto.LastUpdated == default(DateTime) && !string.IsNullOrEmpty(path)
                    ? File.GetLastWriteTimeUtc(path)
                    : (dto.LastUpdated == default(DateTime) ? DateTime.Now : dto.LastUpdated)
            };
            if (dto.Messages != null)
            {
                foreach (var m in dto.Messages)
                {
                    if (m == null) continue;
                    string role = m.Role ?? "user";
                    string content = m.Content ?? string.Empty;
                    List<AttachedFile> atts = m.Attachments;
                    // Backward-compat: extract attachments if they were embedded in content with markers
                    string baseContent;
                    List<AttachedFile> parsed;
                    if ((atts == null || atts.Count == 0) && TryExtractAttachmentsFromContent(content, out baseContent, out parsed))
                    {
                        content = baseContent;
                        atts = parsed;
                    }
                    // Add chat message directly to history (no naming trigger needed on load)
                    var cm = new ChatMessage(role, content, atts);
                    cm.ToolCallId = m.ToolCallId;
                    if (m.ToolCalls != null && m.ToolCalls.Count > 0)
                    {
                        var calls = new List<ToolCall>();
                        foreach (var c in m.ToolCalls)
                            if (c != null) calls.Add(new ToolCall(c.Id, c.Name, c.ArgumentsJson));
                        cm.ToolCalls = calls;
                    }
                    convo.History.Add(cm);
                }
            }
            return convo;
        }

        public static List<ConversationListItem> ListAll()
        {
            var list = new List<ConversationListItem>();
            string root = GetRoot();
            if (!Directory.Exists(root))
            {
                lock (_listCacheGate) { _listCache.Clear(); }
                return list;
            }

            string[] files;
            try { files = Directory.GetFiles(root, "*" + FileExt); }
            catch { files = new string[0]; }

            lock (_listCacheGate)
            {
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in files)
                {
                    present.Add(path);
                    try
                    {
                        DateTime stamp;
                        try { stamp = File.GetLastWriteTimeUtc(path); }
                        catch { stamp = DateTime.MinValue; }

                        CachedListMeta cached;
                        if (_listCache.TryGetValue(path, out cached) && cached != null
                            && cached.Item != null && cached.StampUtc == stamp)
                        {
                            // Unchanged since last read: reuse cached metadata, no file I/O.
                            list.Add(cached.Item);
                            continue;
                        }

                        var item = ReadListItem(path);
                        if (item == null) continue;
                        _listCache[path] = new CachedListMeta { StampUtc = stamp, Item = item };
                        list.Add(item);
                    }
                    catch { }
                }

                // Prune cache entries for files that no longer exist.
                if (_listCache.Count > present.Count)
                {
                    var stale = new List<string>();
                    foreach (var kv in _listCache)
                        if (!present.Contains(kv.Key)) stale.Add(kv.Key);
                    for (int i = 0; i < stale.Count; i++) _listCache.Remove(stale[i]);
                }
            }

            // Order by LastUpdated descending
            list = list.OrderByDescending(i => i.LastUpdated).ToList();
            return list;
        }

        private static ConversationListItem ReadListItem(string path)
        {
            try
            {
                string json = File.ReadAllText(path);
                var dto = JsonConvert.DeserializeObject<ConversationMetaDto>(json);
                if (dto == null) return null;
                return new ConversationListItem
                {
                    Id = dto.Id,
                    Name = string.IsNullOrEmpty(dto.Name) ? "New Conversation" : dto.Name,
                    SelectedModel = dto.SelectedModel,
                    Zdr = dto.ZdrFirstMessageIndex.HasValue && dto.ZdrFirstMessageIndex.Value >= 0,
                    LastUpdated = dto.LastUpdated,
                    Path = path
                };
            }
            catch { return null; }
        }

        public static void DeleteById(string id)
        {
            try
            {
                string path = GetPathForId(id);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
            // Sub-agent transcripts persist alongside the conversation; remove them with it.
            try { AgentTranscriptPersistence.DeleteConversation(id); } catch { }
        }

        public static int DeleteAll()
        {
            int count = 0;
            try
            {
                string root = GetRoot();
                if (!Directory.Exists(root)) return 0;
                foreach (var path in Directory.GetFiles(root, "*" + FileExt))
                {
                    try
                    {
                        File.Delete(path);
                        count++;
                    }
                    catch { }
                }
            }
            catch { }
            try { AgentTranscriptPersistence.DeleteAll(); } catch { }
            return count;
        }

        public static void DeletePath(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { }
            // The conversation file is "<id>.json"; remove that conversation's sub-agent transcripts too.
            try { AgentTranscriptPersistence.DeleteConversation(Path.GetFileNameWithoutExtension(path)); }
            catch { }
        }

        internal sealed class ConversationDto
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string SelectedModel { get; set; }
            public string WorkingDir { get; set; }
            // The current directory (host `cd`) relative to WorkingDir, '/'-separated; absent at the
            // anchor and in files from before this field existed. See SerializeCurrentDir.
            public string CurrentDir { get; set; }
            public bool WorkspaceStripDismissed { get; set; }
            public bool ContinuedFromCompaction { get; set; }
            public bool Zdr { get; set; }
            // Nullable so a missing value in older files maps to "not latched" (-1), not index 0.
            public int? ZdrFirstMessageIndex { get; set; }
            // Per-conversation skill overrides (null/absent in older files -> inherit global). Omitted
            // from JSON when null/empty via NullValueHandling.Ignore.
            public bool? SkillsFeatureOff { get; set; }
            public Dictionary<string, bool> SkillOverrides { get; set; }
            // Per-conversation sub-agents feature override (null/absent in older files -> inherit global).
            public bool? AgentsEnabled { get; set; }
            // Revealed MCP tool names (prompt caching; null/absent in older files -> none revealed).
            public List<string> RevealedTools { get; set; }
            // Sticky cache-routing provider: the endpoint that last demonstrated a cache hit
            // (null/absent in older files -> none observed yet).
            public string CacheWarmProvider { get; set; }
            // Usage accumulators (status bar); null/absent in older files -> zero.
            public decimal? TotalCost { get; set; }
            public decimal? TotalCacheDiscount { get; set; }
            public long? TotalPromptTokens { get; set; }
            public long? TotalCachedTokens { get; set; }
            public long? TotalCompletionTokens { get; set; }
            public long? TotalReasoningTokens { get; set; }
            public int? LastPromptTokens { get; set; }
            public int? LastCachedTokens { get; set; }
            public int? LastCacheWriteTokens { get; set; }
            public DateTime LastUpdated { get; set; }
            public List<MessageDto> Messages { get; set; }
        }

        internal sealed class MessageDto
        {
            public string Role { get; set; }
            public string Content { get; set; }
            public List<AttachedFile> Attachments { get; set; }
            // Tool-call loop (phase 4): assistant tool calls + the id a tool result answers.
            public List<ToolCallDto> ToolCalls { get; set; }
            public string ToolCallId { get; set; }
        }

        internal sealed class ToolCallDto
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string ArgumentsJson { get; set; }
        }

        // Extract attachments embedded using the delimiter format:
        // --- Attached File: <name> ---\n<content>\n--- End Attached File: <name> ---
        private static bool TryExtractAttachmentsFromContent(string content, out string baseContent, out List<AttachedFile> attachments)
        {
            baseContent = content ?? string.Empty;
            attachments = null;
            if (string.IsNullOrEmpty(content)) return false;
            const string startPrefix = "--- Attached File:";
            const string endPrefix = "--- End Attached File:";

            var lines = content.Replace('\r', '\n').Split(new[] { '\n' }, StringSplitOptions.None);
            var baseSb = new StringBuilder();
            var atts = new List<AttachedFile>();
            bool inBlock = false;
            string currentName = null;
            var fileSb = new StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!inBlock)
                {
                    if (line != null && line.TrimStart().StartsWith(startPrefix, StringComparison.Ordinal))
                    {
                        // Parse file name
                        string tail = line.Substring(line.IndexOf(startPrefix, StringComparison.Ordinal) + startPrefix.Length).Trim();
                        if (tail.EndsWith("---", StringComparison.Ordinal)) tail = tail.Substring(0, tail.Length - 3).Trim();
                        if (tail.StartsWith(":", StringComparison.Ordinal)) tail = tail.Substring(1).Trim();
                        currentName = tail;
                        inBlock = true;
                        fileSb.Length = 0;
                    }
                    else
                    {
                        baseSb.Append(line ?? string.Empty).Append('\n');
                    }
                }
                else
                {
                    if (line != null && line.TrimStart().StartsWith(endPrefix, StringComparison.Ordinal))
                    {
                        // End of block
                        atts.Add(new AttachedFile(currentName, fileSb.ToString()));
                        inBlock = false;
                        currentName = null;
                        fileSb.Length = 0;
                    }
                    else
                    {
                        fileSb.Append(line ?? string.Empty).Append('\n');
                    }
                }
            }

            if (atts.Count > 0)
            {
                baseContent = baseSb.ToString();
                // Trim trailing newlines added by split/append
                while (baseContent.EndsWith("\n", StringComparison.Ordinal)) baseContent = baseContent.Substring(0, baseContent.Length - 1);
                attachments = atts;
                return true;
            }
            return false;
        }

        internal sealed class ConversationListItem
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string SelectedModel { get; set; }
            public bool Zdr { get; set; } // conversation has latched ZDR (shown with a marker)
            public DateTime LastUpdated { get; set; }
            public string Path { get; set; }
        }
    }
}
