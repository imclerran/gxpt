using System;
using System.Collections.Generic;
using System.IO;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    // Verifies that child-agent transcripts are bundled into conversation archives and restored on
    // import. Transcripts persist under %AppData%/GxPT/AgentTranscripts/<convId>; tests use a random
    // convId and clean it up so they never touch a real user's stored transcripts.
    public sealed class ImportExportTranscriptTests : IDisposable
    {
        private readonly string _dir;
        private readonly List<string> _convIds = new List<string>();

        public ImportExportTranscriptTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "gxpt_iexport_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            foreach (string id in _convIds)
            {
                try { AgentTranscriptPersistence.DeleteConversation(id); } catch { }
            }
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string NewConvId()
        {
            string id = "iexport-" + Guid.NewGuid().ToString("N");
            _convIds.Add(id);
            return id;
        }

        private static AgentTranscript[] SampleTranscripts()
        {
            var msgs = new List<ChatMessage>
            {
                new ChatMessage("system", "persona"),
                new ChatMessage("user", "do it"),
                new ChatMessage("assistant", "ok"),
            };
            return new AgentTranscript[] { new AgentTranscript("ra", "do it", msgs), null };
        }

        [Fact]
        public void ExportSingle_BundlesTranscripts_RestoredOnImport()
        {
            string convId = NewConvId();
            string rec = "rec1";

            // A conversation file (name == conv id) plus its persisted transcripts.
            string convFile = Path.Combine(_dir, convId + ".json");
            File.WriteAllText(convFile, "{\"Id\":\"" + convId + "\"}");
            AgentTranscriptPersistence.Save(convId, rec, SampleTranscripts());

            string archive = Path.Combine(_dir, "single.gxcv");
            ImportExportService.ExportSingle(convFile, archive, convId);

            // Drop the on-disk transcripts, then import into a fresh Conversations folder.
            AgentTranscriptPersistence.DeleteConversation(convId);
            Assert.False(AgentTranscriptPersistence.LoadAll(convId).ContainsKey(rec));

            string target = Path.Combine(_dir, "imported");
            ImportExportService.ImportAll(archive, target, true);

            Assert.True(File.Exists(Path.Combine(target, convId + ".json")));
            var map = AgentTranscriptPersistence.LoadAll(convId);
            Assert.True(map.ContainsKey(rec));
            Assert.Equal("ra", map[rec][0].Slug);
            Assert.Equal("do it", map[rec][0].Task);
        }

        [Fact]
        public void ExportSingle_WithoutConvId_OmitsTranscripts()
        {
            string convId = NewConvId();
            string convFile = Path.Combine(_dir, convId + ".json");
            File.WriteAllText(convFile, "{}");
            AgentTranscriptPersistence.Save(convId, "rec1", SampleTranscripts());

            string archive = Path.Combine(_dir, "no-transcripts.gxcv");
            ImportExportService.ExportSingle(convFile, archive); // 2-arg overload: no convId

            AgentTranscriptPersistence.DeleteConversation(convId);
            string target = Path.Combine(_dir, "imported2");
            ImportExportService.ImportAll(archive, target, true);

            Assert.True(File.Exists(Path.Combine(target, convId + ".json")));
            Assert.False(AgentTranscriptPersistence.LoadAll(convId).ContainsKey("rec1"));
        }
    }
}
