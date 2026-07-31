using System;
using System.Collections.Generic;
using System.IO;
using GxPT;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxPT.Tests.Mcp
{
    // Turn-level behavior of the doom-loop valve after the false-positive and affordance fixes:
    // directory walks (identical calls from different directories) no longer trip it, a genuinely
    // stuck repetition consults DoomLoopContinuationDecider before wrapping up, and malformed tool
    // arguments carry the parser's own diagnosis instead of a bare "not valid JSON".
    public class DoomLoopTurnTests : IDisposable
    {
        private readonly string _anchor;

        public DoomLoopTurnTests()
        {
            _anchor = Path.Combine(Path.GetTempPath(), "doomturn_" + Guid.NewGuid().ToString("N"));
            // anchor/a/b/c for the walk test.
            Directory.CreateDirectory(Path.Combine(Path.Combine(Path.Combine(_anchor, "a"), "b"), "c"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_anchor, true); } catch { }
        }

        private static McpToolRegistry RegistryWith(out RegistryFakeTransport ft, string server, params ToolDef[] tools)
        {
            var conn = FakeConn.Ready(server, out ft, tools);
            var reg = new McpToolRegistry(null);
            reg.AddConnection(conn);
            return reg;
        }

        [Fact]
        public void Directory_walk_up_the_tree_is_not_flagged_as_a_doom_loop()
        {
            // "list ." then "cd .." repeated verbatim while climbing anchor/a/b/c back to the root:
            // seven calls whose name+args form a textbook period-2 pattern, but each pair runs in a
            // DIFFERENT directory. The cwd-salted signature must let the walk finish normally.
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("list"));
            ft.OnCall = delegate(string n, JObject a) { return RegistryFakeTransport.TextResult("[]"); };

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "cd", "{\"path\":\"a/b/c\"}"));
            streamer.Turns.Add(Chunks.OneToolCall("c2", "files__list", "{\"path\":\".\"}"));
            streamer.Turns.Add(Chunks.OneToolCall("c3", "cd", "{\"path\":\"..\"}"));
            streamer.Turns.Add(Chunks.OneToolCall("c4", "files__list", "{\"path\":\".\"}"));
            streamer.Turns.Add(Chunks.OneToolCall("c5", "cd", "{\"path\":\"..\"}"));
            streamer.Turns.Add(Chunks.OneToolCall("c6", "files__list", "{\"path\":\".\"}"));
            streamer.Turns.Add(Chunks.OneToolCall("c7", "cd", "{\"path\":\"..\"}"));
            streamer.Turns.Add(Chunks.Text("walked"));

            var orch = new McpChatOrchestrator(streamer, reg, null, "test-model", null);
            orch.WorkingDir = _anchor;
            orch.RevealedToolNames = new List<string>(reg.NamesForWorkdir(null));
            bool deciderConsulted = false;
            orch.DoomLoopContinuationDecider = delegate(int n) { deciderConsulted = true; return false; };

            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "explore", ui);

            Assert.True(ui.Completed);
            Assert.False(deciderConsulted);        // never even paused
            Assert.Equal(7, ui.ToolCalls.Count);   // the whole walk ran
            Assert.Equal("walked", ui.Text.ToString());
            Assert.Equal(8, streamer.Calls);       // no wrap-up call was appended
        }

        [Fact]
        public void Stuck_repetition_consults_the_decider_and_wraps_when_declined()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string n, JObject a) { return RegistryFakeTransport.TextResult("same"); };

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{\"path\":\"x\"}"));
            streamer.Turns.Add(Chunks.OneToolCall("c2", "files__read", "{\"path\":\"x\"}"));
            streamer.Turns.Add(Chunks.OneToolCall("c3", "files__read", "{\"path\":\"x\"}")); // closes p=1
            streamer.Turns.Add(Chunks.Text("Wrapped summary."));                              // wrap-up call

            var orch = new McpChatOrchestrator(streamer, reg, null, "test-model", null);
            orch.RevealedToolNames = new List<string>(reg.NamesForWorkdir(null));
            int deciderCalls = 0;
            orch.DoomLoopContinuationDecider = delegate(int n) { deciderCalls++; return false; };

            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.Equal(1, deciderCalls);
            Assert.True(ui.Completed);
            Assert.Equal(3, ui.ToolCalls.Count);
            Assert.Contains("Wrapped summary.", ui.Text.ToString()); // the wrap-up text, not more tools
            Assert.Equal(4, streamer.Calls);
        }

        [Fact]
        public void Decider_can_grant_a_continuation_and_the_window_resets()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string n, JObject a) { return RegistryFakeTransport.TextResult("same"); };

            var streamer = new ScriptedStreamer();
            for (int i = 0; i < 6; i++)
                streamer.Turns.Add(Chunks.OneToolCall("c" + i, "files__read", "{\"path\":\"x\"}"));
            streamer.Turns.Add(Chunks.Text("Wrapped."));

            var orch = new McpChatOrchestrator(streamer, reg, null, "test-model", null);
            orch.RevealedToolNames = new List<string>(reg.NamesForWorkdir(null));
            int deciderCalls = 0;
            // First detection: continue (window cleared). Second detection (3 more identical calls
            // re-fill the fresh window): stop.
            orch.DoomLoopContinuationDecider = delegate(int n) { return ++deciderCalls == 1; };

            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.Equal(2, deciderCalls);
            Assert.Equal(6, ui.ToolCalls.Count);   // 3 before each detection
            Assert.Contains("Wrapped.", ui.Text.ToString());
            Assert.Equal(7, streamer.Calls);
        }

        [Fact]
        public void Sub_agent_without_a_decider_still_wraps_up_unattended()
        {
            // No DoomLoopContinuationDecider (how AgentDispatcher builds children): the A18 valve
            // wraps the turn up on its own, exactly as before the affordance existed.
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string n, JObject a) { return RegistryFakeTransport.TextResult("same"); };

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{\"path\":\"x\"}"));
            streamer.Turns.Add(Chunks.OneToolCall("c2", "files__read", "{\"path\":\"x\"}"));
            streamer.Turns.Add(Chunks.OneToolCall("c3", "files__read", "{\"path\":\"x\"}"));
            streamer.Turns.Add(Chunks.Text("Auto-wrapped."));

            var orch = new McpChatOrchestrator(streamer, reg, null, "test-model", null);
            orch.RevealedToolNames = new List<string>(reg.NamesForWorkdir(null));

            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.Completed);
            Assert.Equal(3, ui.ToolCalls.Count);
            Assert.Contains("Auto-wrapped.", ui.Text.ToString());
        }

        [Fact]
        public void Malformed_arguments_error_carries_the_parsers_diagnosis()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{\"path\": \"unterminated"));
            streamer.Turns.Add(Chunks.Text("ok"));

            var orch = new McpChatOrchestrator(streamer, reg, null, "test-model", null);
            orch.RevealedToolNames = new List<string>(reg.NamesForWorkdir(null));
            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Contains("Invalid tool arguments for 'files__read'", ui.ToolResults[0]);
            Assert.Contains("line", ui.ToolResults[0]);      // Newtonsoft's line/position diagnosis
            Assert.Contains("re-issue the call", ui.ToolResults[0]);
            Assert.Empty(ft.CalledTools);
        }

        [Fact]
        public void Non_object_arguments_say_so_explicitly()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "[1,2]"));
            streamer.Turns.Add(Chunks.Text("ok"));

            var orch = new McpChatOrchestrator(streamer, reg, null, "test-model", null);
            orch.RevealedToolNames = new List<string>(reg.NamesForWorkdir(null));
            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Contains("must be a JSON object", ui.ToolResults[0]);
            Assert.Empty(ft.CalledTools);
        }
    }
}
