using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GxPT;
using Mcp35.Client;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxPT.Tests.Mcp
{
    // Fail-fast guardrails (doom-loop fixes, step 4): a toolless turn keeps a recovery handle
    // (reveal_tools), resolution failures distinguish "server down" from "name wrong", and a child
    // whose declared allowlist resolves to nothing fails its dispatch loudly instead of burning its
    // whole budget with host tools alone.
    public class FailFastGuardrailTests
    {
        [Fact]
        public void Toolless_turn_still_exposes_reveal_tools()
        {
            // Registry present but empty (servers faulted / mid-reconnect): the turn must keep the
            // reveal_tools def as its recovery handle. It used to be gated behind hasMcpTools, so
            // exactly the turns that most needed recovery had no handle at all.
            var reg = new McpToolRegistry(null);
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = new McpChatOrchestrator(streamer, reg, null, "test-model", null);
            orch.RunTurn(new List<ChatMessage>(), "go", new RecordingUi());

            Assert.NotNull(streamer.SeenTools[0]);
            Assert.Single(streamer.SeenTools[0]);
            Assert.Equal("reveal_tools", (string)streamer.SeenTools[0][0]["function"]["name"]);
        }

        [Fact]
        public void No_registry_still_means_no_tools()
        {
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("done"));
            var orch = new McpChatOrchestrator(streamer, null, null, "test-model", null);
            orch.RunTurn(new List<ChatMessage>(), "go", new RecordingUi());
            Assert.Null(streamer.SeenTools[0]);
        }

        [Fact]
        public void Unresolved_server_tool_reports_unavailable_when_no_servers_are_connected()
        {
            var reg = new McpToolRegistry(null); // no servers at all
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = new McpChatOrchestrator(streamer, reg, null, "test-model", null);
            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Contains("temporarily unavailable", ui.ToolResults[0]);
            Assert.Contains("Retry", ui.ToolResults[0]);
            Assert.DoesNotContain("Unknown tool", ui.ToolResults[0]);
        }

        [Fact]
        public void Unresolved_tool_names_its_missing_server_when_others_are_up()
        {
            // files is connected, git is not: calling git__status is an outage of the git server,
            // not an unknown name — say which server is missing.
            var conn = FakeConn.Ready("files", new ToolDef("read"));
            var reg = new McpToolRegistry(null);
            reg.AddConnection(conn);

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "git__status", "{}"));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = new McpChatOrchestrator(streamer, reg, null, "test-model", null);
            orch.RevealedToolNames = new List<string>(reg.NamesForWorkdir(null));
            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Contains("'git' server has no tools connected", ui.ToolResults[0]);
            Assert.DoesNotContain("Unknown tool", ui.ToolResults[0]);
        }

        [Fact]
        public void Wrong_name_on_a_present_server_stays_unknown_tool()
        {
            var conn = FakeConn.Ready("files", new ToolDef("read"));
            var reg = new McpToolRegistry(null);
            reg.AddConnection(conn);

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__nope", "{}"));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = new McpChatOrchestrator(streamer, reg, null, "test-model", null);
            orch.RevealedToolNames = new List<string>(reg.NamesForWorkdir(null));
            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Contains("Unknown tool: files__nope", ui.ToolResults[0]);
        }

        [Fact]
        public void Host_tool_names_never_get_the_unavailable_message()
        {
            // open_skill is a host meta-tool name (no server__ prefix); with an empty registry it must
            // still fall through as Unknown, not masquerade as a server outage.
            var reg = new McpToolRegistry(null);
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "open_skill", "{\"names\":[\"x\"]}"));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = new McpChatOrchestrator(streamer, reg, null, "test-model", null);
            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Contains("Unknown tool", ui.ToolResults[0]);
        }
    }

    // The dispatch-time guard: an agent that PROMISES specific tools must not run without them.
    public sealed class EmptyToolsetDispatchTests : IDisposable
    {
        private readonly string _dir;

        public EmptyToolsetDispatchTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "gxpt_emptyset_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private Agent WriteAgent(string slug, string frontmatterExtra, string body)
        {
            string file = Path.Combine(_dir, slug + ".md");
            File.WriteAllText(file, "---\nname: " + slug + "\ndescription: d\n" + frontmatterExtra
                + "---\n" + body + "\n", new UTF8Encoding(false));
            AgentCatalog cat = AgentCatalog.Build(_dir, null);
            Agent a;
            cat.TryGet(slug, out a);
            return a;
        }

        private AgentDispatcher Dispatcher(ScriptedStreamer streamer, McpToolRegistry reg, params Agent[] agents)
        {
            return new AgentDispatcher(new List<Agent>(agents), streamer, reg, null,
                "parent-model", "C:\\proj", null,
                delegate(string n) { return ToolTier.ReadOnly; }, 25, 60000);
        }

        [Fact]
        public void Declared_allowlist_resolving_to_nothing_fails_dispatch_loudly()
        {
            // The plan-implementer disaster: 14 declared tools, none available (registry emptied by a
            // mid-turn rebuild), child burned 40 iterations with only cd. Now the dispatch itself
            // fails with a retryable error, and the child never runs.
            Agent a = WriteAgent("worker", "tools: [files__read, files__write]\n", "You work.");
            var reg = new McpToolRegistry(null); // nothing available
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("should never stream"));

            string result = Dispatcher(streamer, reg, a)
                .Dispatch("{\"agents\":[{\"name\":\"worker\",\"task\":\"t\"}]}");

            Assert.Contains("NOT dispatched", result);
            Assert.Contains("worker", result);
            Assert.Equal(0, streamer.Calls); // the child never ran
        }

        [Fact]
        public void Star_allowlist_with_a_toolless_parent_still_runs()
        {
            // "*" means "whatever the parent has" — an empty result is consistent with it, so the
            // child runs as a text-only agent (no broken promise to report).
            Agent a = WriteAgent("generalist", "tools: [\"*\"]\n", "You answer.");
            var reg = new McpToolRegistry(null);
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("answered"));

            string result = Dispatcher(streamer, reg, a)
                .Dispatch("{\"agents\":[{\"name\":\"generalist\",\"task\":\"t\"}]}");

            Assert.Contains("answered", result);
            Assert.Equal(1, streamer.Calls);
        }

        [Fact]
        public void Declared_allowlist_with_available_tools_dispatches_normally()
        {
            Agent a = WriteAgent("worker", "tools: [files__read]\n", "You work.");
            var conn = FakeConn.Ready("files", new ToolDef("read"));
            var reg = new McpToolRegistry(null);
            reg.AddConnection(conn, "C:\\proj");
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("done"));

            string result = Dispatcher(streamer, reg, a)
                .Dispatch("{\"agents\":[{\"name\":\"worker\",\"task\":\"t\"}]}");

            Assert.Contains("done", result);
            Assert.DoesNotContain("NOT dispatched", result);
        }
    }

    // Step 6: a faulted/closed scoped connection must not leave its workdir key behind — that made
    // EnsureWorkingDir a permanent no-op ("already connected") with zero tools until an app restart.
    public class FaultedConnectionRecoveryTests
    {
        private static List<string> Manifest(McpToolRegistry reg)
        {
            var names = new List<string>();
            foreach (var line in reg.NamesManifestSystemMessage().Split('\n'))
                if (line.StartsWith("- ")) names.Add(line.Substring(2));
            return names;
        }

        [Fact]
        public void Faulted_scoped_connection_can_reconnect_via_EnsureWorkingDir()
        {
            var connector = new FakeServerConnector();
            var reg = new McpToolRegistry(null);
            var host = new McpHost(connector, reg, null, 2000);
            host.Start(new[] { Specs.Scoped("files", true) });
            host.EnsureWorkingDir("C:\\a");
            Assert.Contains("files__files_tool", Manifest(reg));

            connector.Created[0].Dispose(); // fault: StateChanged(Closed) fires into the host

            // The dead set is fully forgotten: registry emptied AND the workdir key released.
            Assert.Empty(Manifest(reg));
            Assert.DoesNotContain("C:\\a", host.ActiveWorkingDirs);

            // ...so the same folder can reconnect (this used to be a silent no-op forever).
            host.EnsureWorkingDir("C:\\a");
            Assert.Equal(2, connector.CreatedNames.FindAll(delegate(string n) { return n == "files"; }).Count);
            Assert.Contains("files__files_tool", Manifest(reg));
            McpServerConnection r; string tool;
            Assert.True(reg.TryResolve("files__files_tool", "C:\\a", out r, out tool));
            Assert.Same(connector.Created[1], r);
        }

        [Fact]
        public void Faulting_one_workdirs_server_leaves_other_workdirs_alone()
        {
            var connector = new FakeServerConnector();
            var reg = new McpToolRegistry(null);
            var host = new McpHost(connector, reg, null, 2000);
            host.Start(new[] { Specs.Scoped("files", true) });
            host.EnsureWorkingDir("C:\\a");
            var connA = connector.Created.Last();
            host.EnsureWorkingDir("C:\\b");
            var connB = connector.Created.Last();

            connA.Dispose();

            Assert.DoesNotContain("C:\\a", host.ActiveWorkingDirs);
            Assert.Contains("C:\\b", host.ActiveWorkingDirs);
            Assert.Equal(ConnectionState.Ready, connB.State);
            McpServerConnection r; string tool;
            Assert.True(reg.TryResolve("files__files_tool", "C:\\b", out r, out tool));
        }
    }
}
