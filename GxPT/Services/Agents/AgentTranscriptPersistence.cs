using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace GxPT
{
    // On-disk persistence for child-agent transcripts (the user's "keep them until the conversation is
    // deleted"). Each dispatch_agent call's per-slot transcripts are written to a per-conversation folder
    // under %AppData%/GxPT/AgentTranscripts/<convId>/<recKey>.json, keyed by the dispatch record's id (the
    // model call id). Loaded back into AgentTranscriptStore when the conversation opens, and the whole
    // <convId> folder is removed when the conversation is deleted (ConversationStore.Delete*). A lean DTO is
    // serialized (role/content/tool-call name+args) rather than the live ChatMessage graph. All operations
    // swallow IO errors - persistence is best-effort and never blocks a turn or a delete.
    internal static class AgentTranscriptPersistence
    {
        private const string DirName = "AgentTranscripts";
        private const string FileExt = ".json";

        private static string Root()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(Path.Combine(appData, "GxPT"), DirName);
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        private static string ConvDir(string convId)
        {
            string id = Sanitize(convId);
            return string.IsNullOrEmpty(id) ? null : Path.Combine(Root(), id);
        }

        // Persist one dispatch call's transcripts (slot-indexed; null slots are kept as null placeholders so
        // the on-reload slot indices line up with the record body).
        public static void Save(string convId, string recKey, AgentTranscript[] transcripts)
        {
            if (string.IsNullOrEmpty(recKey) || transcripts == null) return;
            string dir = ConvDir(convId);
            string file = Sanitize(recKey);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file)) return;
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                FileDto dto = ToDto(transcripts);
                File.WriteAllText(Path.Combine(dir, file + FileExt), JsonConvert.SerializeObject(dto));
            }
            catch { }
        }

        // Load every persisted dispatch call for a conversation, keyed by record id, for re-seeding the
        // in-memory store when the conversation opens.
        public static Dictionary<string, AgentTranscript[]> LoadAll(string convId)
        {
            Dictionary<string, AgentTranscript[]> map = new Dictionary<string, AgentTranscript[]>(StringComparer.Ordinal);
            string dir = ConvDir(convId);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return map;
            string[] files;
            try { files = Directory.GetFiles(dir, "*" + FileExt); }
            catch { return map; }
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    string recKey = Path.GetFileNameWithoutExtension(files[i]);
                    FileDto dto = JsonConvert.DeserializeObject<FileDto>(File.ReadAllText(files[i]));
                    AgentTranscript[] arr = FromDto(dto);
                    if (!string.IsNullOrEmpty(recKey) && arr != null) map[recKey] = arr;
                }
                catch { }
            }
            return map;
        }

        public static void DeleteConversation(string convId)
        {
            string dir = ConvDir(convId);
            try { if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { }
        }

        public static void DeleteAll()
        {
            try { string r = Root(); if (Directory.Exists(r)) Directory.Delete(r, true); }
            catch { }
        }

        // ---------- DTO mapping ----------
        private static FileDto ToDto(AgentTranscript[] transcripts)
        {
            FileDto f = new FileDto();
            f.Agents = new List<TranscriptDto>(transcripts.Length);
            for (int i = 0; i < transcripts.Length; i++)
            {
                AgentTranscript t = transcripts[i];
                if (t == null) { f.Agents.Add(null); continue; }
                TranscriptDto td = new TranscriptDto();
                td.Slug = t.Slug;
                td.Task = t.Task;
                td.Messages = new List<MsgDto>();
                if (t.Messages != null)
                    for (int m = 0; m < t.Messages.Count; m++)
                    {
                        ChatMessage cm = t.Messages[m];
                        if (cm == null) continue;
                        MsgDto md = new MsgDto();
                        md.Role = cm.Role;
                        md.Content = cm.Content;
                        md.ToolCallId = cm.ToolCallId;
                        if (cm.ToolCalls != null && cm.ToolCalls.Count > 0)
                        {
                            md.ToolCalls = new List<CallDto>();
                            for (int k = 0; k < cm.ToolCalls.Count; k++)
                            {
                                ToolCall tc = cm.ToolCalls[k];
                                if (tc == null) continue;
                                md.ToolCalls.Add(new CallDto { Id = tc.Id, Name = tc.Name, Args = tc.ArgumentsJson });
                            }
                        }
                        td.Messages.Add(md);
                    }
                f.Agents.Add(td);
            }
            return f;
        }

        private static AgentTranscript[] FromDto(FileDto f)
        {
            if (f == null || f.Agents == null) return null;
            AgentTranscript[] arr = new AgentTranscript[f.Agents.Count];
            for (int i = 0; i < f.Agents.Count; i++)
            {
                TranscriptDto td = f.Agents[i];
                if (td == null) { arr[i] = null; continue; }
                List<ChatMessage> msgs = new List<ChatMessage>();
                if (td.Messages != null)
                    for (int m = 0; m < td.Messages.Count; m++)
                    {
                        MsgDto md = td.Messages[m];
                        if (md == null) continue;
                        ChatMessage cm = new ChatMessage(md.Role, md.Content);
                        cm.ToolCallId = md.ToolCallId;
                        if (md.ToolCalls != null && md.ToolCalls.Count > 0)
                        {
                            cm.ToolCalls = new List<ToolCall>();
                            for (int k = 0; k < md.ToolCalls.Count; k++)
                            {
                                CallDto cd = md.ToolCalls[k];
                                if (cd == null) continue;
                                cm.ToolCalls.Add(new ToolCall(cd.Id, cd.Name, cd.Args));
                            }
                        }
                        msgs.Add(cm);
                    }
                arr[i] = new AgentTranscript(td.Slug, td.Task, msgs);
            }
            return arr;
        }

        // Plain serialization DTOs (kept private; the on-disk shape is an implementation detail).
        private sealed class FileDto { public List<TranscriptDto> Agents; }
        private sealed class TranscriptDto { public string Slug; public string Task; public List<MsgDto> Messages; }
        private sealed class MsgDto { public string Role; public string Content; public string ToolCallId; public List<CallDto> ToolCalls; }
        private sealed class CallDto { public string Id; public string Name; public string Args; }
    }
}
