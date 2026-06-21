using System.Collections.Generic;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    // Exercises the pure JSON-to-model path. A null client is fine here: LoadFromJson adds
    // messages directly to History and never triggers naming or network calls.
    public class ConversationStoreTests
    {
        [Fact]
        public void RoundTrip_PreservesSkillOverrides()
        {
            var convo = new Conversation(null);
            convo.Id = "abc";
            convo.SkillsFeatureOff = true;
            convo.SkillOverrides["release-notes"] = true;
            convo.SkillOverrides["noisy"] = false;

            string json = ConversationStore.ToJson(convo);
            var loaded = ConversationStore.LoadFromJson(null, json);

            Assert.Equal(true, loaded.SkillsFeatureOff);
            Assert.True(loaded.SkillOverrides["release-notes"]);
            Assert.False(loaded.SkillOverrides["noisy"]);
        }

        [Fact]
        public void LoadFromJson_MissingSkillFields_DefaultsToInheritAndEmpty()
        {
            string json = "{\"Id\":\"x\",\"Name\":\"N\",\"Messages\":[]}";
            var convo = ConversationStore.LoadFromJson(null, json);

            Assert.Null(convo.SkillsFeatureOff);              // inherit global
            Assert.NotNull(convo.SkillOverrides);             // never null
            Assert.Empty(convo.SkillOverrides);
        }

        [Fact]
        public void RoundTrip_PreservesAgentsEnabled()
        {
            var convo = new Conversation(null);
            convo.Id = "abc";
            convo.AgentsEnabled = true;

            var loaded = ConversationStore.LoadFromJson(null, ConversationStore.ToJson(convo));
            Assert.Equal(true, loaded.AgentsEnabled);

            convo.AgentsEnabled = false;
            loaded = ConversationStore.LoadFromJson(null, ConversationStore.ToJson(convo));
            Assert.Equal(false, loaded.AgentsEnabled);
        }

        [Fact]
        public void LoadFromJson_MissingAgentsEnabled_InheritsGlobal()
        {
            var convo = ConversationStore.LoadFromJson(null, "{\"Id\":\"x\",\"Name\":\"N\",\"Messages\":[]}");
            Assert.Null(convo.AgentsEnabled);   // absent -> inherit the global default
        }

        [Fact]
        public void LoadFromJson_NullOrEmpty_ReturnsNull()
        {
            Assert.Null(ConversationStore.LoadFromJson(null, null));
            Assert.Null(ConversationStore.LoadFromJson(null, ""));
        }

        [Fact]
        public void LoadFromJson_ParsesNameModelAndMessages()
        {
            string json =
                "{\"Id\":\"abc123\",\"Name\":\"My Chat\",\"SelectedModel\":\"openai/gpt-4o\"," +
                "\"Messages\":[" +
                "{\"Role\":\"user\",\"Content\":\"hello\"}," +
                "{\"Role\":\"assistant\",\"Content\":\"hi there\"}]}";

            var convo = ConversationStore.LoadFromJson(null, json);

            Assert.NotNull(convo);
            Assert.Equal("abc123", convo.Id);
            Assert.Equal("My Chat", convo.Name);
            Assert.Equal("openai/gpt-4o", convo.SelectedModel);
            Assert.Equal(2, convo.History.Count);
            Assert.Equal("user", convo.History[0].Role);
            Assert.Equal("hello", convo.History[0].Content);
            Assert.Equal("assistant", convo.History[1].Role);
            Assert.Equal("hi there", convo.History[1].Content);
        }

        [Fact]
        public void LoadFromJson_MissingName_DefaultsToNewConversation()
        {
            string json = "{\"Messages\":[{\"Role\":\"user\",\"Content\":\"x\"}]}";
            var convo = ConversationStore.LoadFromJson(null, json);
            Assert.NotNull(convo);
            Assert.Equal("New Conversation", convo.Name);
        }

        [Fact]
        public void LoadFromJson_ExtractsLegacyAttachmentMarkers()
        {
            // Older saves embedded attachments inline in the message content using markers.
            // LoadFromJson should split them back out into Attachments when no Attachments field exists.
            string content =
                "Here is my question" +
                "\\n--- Attached File: foo.txt ---" +
                "\\nhello world" +
                "\\n--- End Attached File: foo.txt ---";
            string json = "{\"Name\":\"Chat\",\"Messages\":[{\"Role\":\"user\",\"Content\":\"" + content + "\"}]}";

            var convo = ConversationStore.LoadFromJson(null, json);

            Assert.NotNull(convo);
            Assert.Single(convo.History);
            var msg = convo.History[0];
            Assert.Equal("Here is my question", msg.Content);
            Assert.NotNull(msg.Attachments);
            Assert.Single(msg.Attachments);
            Assert.Equal("foo.txt", msg.Attachments[0].FileName);
            Assert.Contains("hello world", msg.Attachments[0].Content);
        }

        // ---- Newtonsoft migration (D16): tool-call persistence + backward compatibility ----

        [Fact]
        public void ToJson_then_Load_roundtrips_tool_calls_and_tool_messages()
        {
            var convo = new Conversation(null);
            convo.Name = "T";
            convo.History.Add(new ChatMessage("user", "read it"));

            var asst = new ChatMessage("assistant", "");
            asst.ToolCalls = new List<ToolCall> { new ToolCall("call_1", "files__read", "{\"path\":\"a\"}") };
            convo.History.Add(asst);

            var tool = new ChatMessage("tool", "file contents");
            tool.ToolCallId = "call_1";
            convo.History.Add(tool);

            string json = ConversationStore.ToJson(convo);
            var reload = ConversationStore.LoadFromJson(null, json);

            Assert.Equal(3, reload.History.Count);

            var a = reload.History[1];
            Assert.NotNull(a.ToolCalls);
            Assert.Single(a.ToolCalls);
            Assert.Equal("call_1", a.ToolCalls[0].Id);
            Assert.Equal("files__read", a.ToolCalls[0].Name);
            Assert.Equal("{\"path\":\"a\"}", a.ToolCalls[0].ArgumentsJson);

            var t = reload.History[2];
            Assert.Equal("tool", t.Role);
            Assert.Equal("call_1", t.ToolCallId);
            Assert.Equal("file contents", t.Content);
        }

        [Fact]
        public void ToJson_omits_tool_fields_for_plain_messages()
        {
            var convo = new Conversation(null);
            convo.History.Add(new ChatMessage("user", "hi"));
            string json = ConversationStore.ToJson(convo);
            Assert.DoesNotContain("ToolCalls", json);
            Assert.DoesNotContain("ToolCallId", json);
        }

        [Fact]
        public void WorkingDir_round_trips()
        {
            var convo = new Conversation(null);
            convo.Name = "W";
            convo.WorkingDir = "C:\\Projects\\report-tool";
            convo.History.Add(new ChatMessage("user", "hi"));

            var reload = ConversationStore.LoadFromJson(null, ConversationStore.ToJson(convo));
            Assert.Equal("C:\\Projects\\report-tool", reload.WorkingDir);
        }

        [Fact]
        public void ContinuedFromCompaction_round_trips()
        {
            var convo = new Conversation(null);
            convo.Name = "C";
            convo.ContinuedFromCompaction = true;
            convo.History.Add(new ChatMessage("system", "summary context"));

            var reload = ConversationStore.LoadFromJson(null, ConversationStore.ToJson(convo));
            Assert.True(reload.ContinuedFromCompaction);
        }

        [Fact]
        public void ContinuedFromCompaction_defaults_false_for_legacy_files()
        {
            string json = "{\"Name\":\"L\",\"Messages\":[]}";
            var convo = ConversationStore.LoadFromJson(null, json);
            Assert.False(convo.ContinuedFromCompaction);
        }

        [Fact]
        public void Load_legacy_file_without_working_dir_is_null()
        {
            var convo = ConversationStore.LoadFromJson(null, "{\"Name\":\"C\",\"Messages\":[]}");
            Assert.Null(convo.WorkingDir);
        }

        [Fact]
        public void WorkspaceStripDismissed_round_trips_and_defaults_false()
        {
            var convo = new Conversation(null);
            convo.WorkspaceStripDismissed = true;
            var reload = ConversationStore.LoadFromJson(null, ConversationStore.ToJson(convo));
            Assert.True(reload.WorkspaceStripDismissed);

            var legacy = ConversationStore.LoadFromJson(null, "{\"Name\":\"C\",\"Messages\":[]}");
            Assert.False(legacy.WorkspaceStripDismissed);
        }

        [Fact]
        public void Zdr_fields_round_trip()
        {
            var convo = new Conversation(null);
            convo.Zdr = true;
            convo.ZdrFirstMessageIndex = 3;
            var reload = ConversationStore.LoadFromJson(null, ConversationStore.ToJson(convo));
            Assert.True(reload.Zdr);
            Assert.Equal(3, reload.ZdrFirstMessageIndex);
        }

        [Fact]
        public void Load_legacy_file_defaults_zdr_off_and_unlatched()
        {
            // Older files have neither field: ZDR off, and the latch must be -1 (not index 0).
            var legacy = ConversationStore.LoadFromJson(null, "{\"Name\":\"C\",\"Messages\":[]}");
            Assert.False(legacy.Zdr);
            Assert.Equal(-1, legacy.ZdrFirstMessageIndex);
        }

        [Fact]
        public void Load_legacy_file_without_tool_fields_has_null_tool_data()
        {
            string json = "{\"Name\":\"C\",\"Messages\":[{\"Role\":\"assistant\",\"Content\":\"hi\"}]}";
            var convo = ConversationStore.LoadFromJson(null, json);
            Assert.Null(convo.History[0].ToolCalls);
            Assert.Null(convo.History[0].ToolCallId);
        }

        [Fact]
        public void Load_parses_legacy_microsoft_date_format()
        {
            // Files written by the old JavaScriptSerializer used "\/Date(ms)\/" timestamps; Newtonsoft
            // must still parse them so reloading pre-migration conversations preserves LastUpdated.
            string json = "{\"Name\":\"C\",\"LastUpdated\":\"\\/Date(1700000000000)\\/\",\"Messages\":[]}";
            var convo = ConversationStore.LoadFromJson(null, json);
            Assert.Equal(2023, convo.LastUpdated.Year);
        }
    }
}
