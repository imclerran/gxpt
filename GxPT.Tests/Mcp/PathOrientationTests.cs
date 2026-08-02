using System;
using System.Collections.Generic;
using System.IO;
using GxPT;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxPT.Tests.Mcp
{
    // Path-orientation fixes for the original doom loop: cd accepts absolute in-workspace paths and
    // reports resolved absolute paths in errors and echoes; pwd reports without moving; and
    // reveal_tools is liberal about the `names` arg shape but loud when nothing usable was passed
    // (it used to return a silent [] that read as "those tools don't exist").
    public class PathOrientationTests : IDisposable
    {
        private readonly string _anchor;
        private readonly string _sub;

        public PathOrientationTests()
        {
            _anchor = Path.Combine(Path.GetTempPath(), "pathorient_" + Guid.NewGuid().ToString("N"));
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

        private static McpChatOrchestrator Orch(ScriptedStreamer s, McpToolRegistry reg, string anchor)
        {
            var orch = new McpChatOrchestrator(s, reg, null, "test-model", null);
            if (reg != null) orch.RevealedToolNames = new List<string>(reg.NamesForWorkdir(null));
            orch.WorkingDir = anchor;
            return orch;
        }

        private static string ArgsJson(string key, string value)
        {
            var o = new JObject();
            o[key] = value;
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        [Fact]
        public void Cd_accepts_an_absolute_path_inside_the_workspace()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "cd", ArgsJson("path", _sub)));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = Orch(streamer, reg, _anchor);
            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.False(ui.ToolErrors[0]);
            Assert.Equal(Path.GetFullPath(_sub), orch.CurrentDir);
            Assert.Contains("Current directory is now `sub`", ui.ToolResults[0]);
        }

        [Fact]
        public void Cd_rejects_an_absolute_path_outside_the_workspace()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            string outside = Path.GetFullPath(Path.Combine(_anchor, ".."));
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "cd", ArgsJson("path", outside)));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = Orch(streamer, reg, _anchor);
            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Contains("above the workspace root", ui.ToolResults[0]);
            Assert.Null(orch.CurrentDir);
        }

        [Fact]
        public void Cd_not_found_error_reports_the_resolved_absolute_path()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "cd", "{\"path\":\"missing\"}"));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = Orch(streamer, reg, _anchor);
            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Contains(Path.Combine(Path.GetFullPath(_anchor), "missing"), ui.ToolResults[0]);
        }

        [Fact]
        public void Cd_success_echo_includes_the_absolute_path()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "cd", "{\"path\":\"sub\"}"));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = Orch(streamer, reg, _anchor);
            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.False(ui.ToolErrors[0]);
            Assert.Contains(Path.GetFullPath(_sub), ui.ToolResults[0]);
        }

        [Fact]
        public void Pwd_reports_the_current_dir_without_moving()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "cd", "{\"path\":\"sub\"}"));
            streamer.Turns.Add(Chunks.OneToolCall("c2", "pwd", "{}"));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = Orch(streamer, reg, _anchor);
            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.False(ui.ToolErrors[1]);
            Assert.Contains("`sub`", ui.ToolResults[1]);
            Assert.Contains(Path.GetFullPath(_sub), ui.ToolResults[1]);
            // pwd did NOT reset the current dir (a no-arg cd would have).
            Assert.Equal(Path.GetFullPath(_sub), orch.CurrentDir);
        }

        [Fact]
        public void Pwd_renders_an_out_of_anchor_current_dir_as_the_root_not_a_leaked_path()
        {
            // Defense-in-depth: all setters keep CurrentDir within the anchor, but if one ever slipped,
            // pwd must not print the foreign absolute path labeled "relative to the workspace root"
            // (ToRelative's raw fallback). AnchorRelDisplay clamps it to ".", matching the tail block.
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "pwd", "{}"));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = Orch(streamer, reg, _anchor);
            string outside = Path.Combine(Path.GetTempPath(), "pathorient_outside_" + Guid.NewGuid().ToString("N"));
            orch.CurrentDir = outside; // out-of-anchor (an upstream-bug state)

            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            // The relative field is clamped to ".", never the foreign path presented as relative.
            Assert.Contains("Current directory: `.`", ui.ToolResults[0]);
            Assert.DoesNotContain("`" + outside + "`", ui.ToolResults[0]);
        }

        [Fact]
        public void Pwd_is_exposed_alongside_cd()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = Orch(streamer, reg, _anchor);
            orch.RunTurn(new List<ChatMessage>(), "go", new RecordingUi());

            var names = new List<string>();
            foreach (var def in streamer.SeenTools[0]) names.Add((string)def["function"]["name"]);
            Assert.Contains("cd", names);
            Assert.Contains("pwd", names);
        }

        [Fact]
        public void Reveal_accepts_a_stringified_names_array()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            var streamer = new ScriptedStreamer();
            // The array arrives STRINGIFIED - the exact malformation observed in the doom loop.
            streamer.Turns.Add(Chunks.OneToolCall("r1", "reveal_tools", ArgsJson("names", "[\"files__read\"]")));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = new McpChatOrchestrator(streamer, reg, null, "test-model", null);
            orch.RevealedToolNames = new List<string>();
            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.False(ui.ToolErrors[0]);
            Assert.Contains("files__read", ui.ToolResults[0]); // the def came back
            Assert.Contains("files__read", orch.RevealedToolNames);
        }

        [Fact]
        public void Reveal_accepts_a_single_bare_name()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("r1", "reveal_tools", ArgsJson("names", "files__read")));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = new McpChatOrchestrator(streamer, reg, null, "test-model", null);
            orch.RevealedToolNames = new List<string>();
            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.False(ui.ToolErrors[0]);
            Assert.Contains("files__read", orch.RevealedToolNames);
        }

        [Fact]
        public void Reveal_with_no_usable_names_is_a_loud_error()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("r1", "reveal_tools", "{}"));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = new McpChatOrchestrator(streamer, reg, null, "test-model", null);
            orch.RevealedToolNames = new List<string>();
            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Contains("no tool names given", ui.ToolResults[0]);
        }
    }
}
