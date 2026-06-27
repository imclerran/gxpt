using System;
using System.Collections.Generic;
using System.IO;
using GxPT;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxPT.Tests.Mcp
{
    // The host `cd` meta-tool and the per-call current-directory injection: cd moves the conversation's
    // current dir within the workspace anchor (re-validated, transient), and every subsequent
    // workdir-scoped call carries that dir out-of-band as params._meta["gxpt.cwd"].
    public class HostCdTests : IDisposable
    {
        private readonly string _anchor;
        private readonly string _sub;

        public HostCdTests()
        {
            _anchor = Path.Combine(Path.GetTempPath(), "hostcd_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_anchor);
            _sub = Path.Combine(_anchor, "sub");
            Directory.CreateDirectory(_sub);
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

        private static McpChatOrchestrator Orch(ScriptedStreamer s, McpToolRegistry reg)
        {
            var orch = new McpChatOrchestrator(s, reg, null, "test-model", null);
            if (reg != null) orch.RevealedToolNames = new List<string>(reg.NamesForWorkdir(null));
            return orch;
        }

        [Fact]
        public void Cd_moves_current_dir_and_injects_it_into_the_next_call()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("contents"); };

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "cd", "{\"path\":\"sub\"}"));
            streamer.Turns.Add(Chunks.OneToolCall("c2", "files__read", "{\"path\":\"x.txt\"}"));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = Orch(streamer, reg);
            orch.WorkingDir = _anchor;
            string changedTo = "<unset>";
            orch.CurrentDirChanged = delegate(string d) { changedTo = d; };

            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "scope in and read", ui);

            // cd ran locally (no MCP round-trip) and reported the new location.
            Assert.Equal(new[] { "cd", "files__read" }, ui.ToolCalls.ToArray());
            Assert.False(ui.ToolErrors[0]);
            Assert.Contains("Current directory is now `sub`", ui.ToolResults[0]);

            // The current dir moved and the host was notified.
            Assert.Equal(Path.GetFullPath(_sub), orch.CurrentDir);
            Assert.Equal(Path.GetFullPath(_sub), changedTo);

            // Only files__read hit the transport, and it carried the current dir out-of-band.
            Assert.Single(ft.CallParams);
            Assert.NotNull(ft.CallParams[0]["_meta"]);
            Assert.Equal(Path.GetFullPath(_sub), (string)ft.CallParams[0]["_meta"]["gxpt.cwd"]);
        }

        [Fact]
        public void Cd_above_the_anchor_is_rejected_and_current_dir_is_unchanged()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "cd", "{\"path\":\"../..\"}"));
            streamer.Turns.Add(Chunks.Text("ok"));

            var orch = Orch(streamer, reg);
            orch.WorkingDir = _anchor;

            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "try to escape", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Contains("above the workspace root", ui.ToolResults[0]);
            Assert.Null(orch.CurrentDir);
        }

        [Fact]
        public void Cd_with_no_argument_returns_to_the_anchor()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "cd", "{\"path\":\"sub\"}"));
            streamer.Turns.Add(Chunks.OneToolCall("c2", "cd", "{}"));
            streamer.Turns.Add(Chunks.Text("home"));

            var orch = Orch(streamer, reg);
            orch.WorkingDir = _anchor;

            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "in then out", ui);

            // First cd scoped into sub; the second (no arg) returned to the anchor, collapsing the
            // current dir back to null (the floor) and echoing "." as the anchor-relative location.
            Assert.False(ui.ToolErrors[0]);
            Assert.False(ui.ToolErrors[1]);
            Assert.Null(orch.CurrentDir);
            Assert.Contains("`.`", ui.ToolResults[1]);
        }

        [Fact]
        public void Cd_is_not_offered_without_a_workspace()
        {
            // Folderless turn (scratch command server only): there is no anchor, so cd is not a host tool
            // and resolves to nothing.
            RegistryFakeTransport ft;
            var conn = FakeConn.Ready("command", out ft, new ToolDef("run"));
            var reg = new McpToolRegistry(null);
            reg.AddConnection(conn, "C:\\scratch\\abc");

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "cd", "{\"path\":\"sub\"}"));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = new McpChatOrchestrator(streamer, reg, null, "test-model", null);
            orch.WorkingDir = null;
            orch.ScratchWorkingDir = "C:\\scratch\\abc";
            orch.RevealedToolNames = new List<string> { "command__run" };

            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "try cd", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Contains("Unknown tool", ui.ToolResults[0]);
        }
    }
}
