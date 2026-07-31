using System.Collections.Generic;
using System.IO;
using System.Text;
using GxPT;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxPT.Tests.Mcp
{
    // The frozen-defs sub-agent path (design A11): an allowlisted child's MCP tool defs are
    // snapshotted at dispatch and exposed verbatim every iteration, so registry churn during a long
    // parent turn (the mid-turn host rebuild that produced the "cd-only agent" doom loop) cannot
    // change what the child sees. These tests drive McpChatOrchestrator.RunTurn directly with
    // FrozenToolDefs set, the way AgentDispatcher.RunChild configures a child.
    public class FrozenToolDefsTests
    {
        private static McpChatOrchestrator New(ScriptedStreamer s, McpToolRegistry reg, string model)
        {
            return new McpChatOrchestrator(s, reg, null, model, null);
        }

        [Fact]
        public void Frozen_defs_survive_registry_emptying_mid_turn()
        {
            // THE doom-loop regression: the registry empties under a running turn (exactly what a
            // mid-turn RebuildMcpHost teardown did) and the frozen exposure must not collapse.
            RegistryFakeTransport ft;
            var conn = FakeConn.Ready("files", out ft, new ToolDef("read"), new ToolDef("write"));
            var reg = new McpToolRegistry(null);
            reg.AddConnection(conn);
            var names = new List<string>(reg.NamesForWorkdir(null));
            Assert.Equal(2, names.Count);

            // The tool call itself performs the "host rebuild": the catalog empties while the turn runs.
            ft.OnCall = delegate(string name, JObject args)
            {
                reg.RemoveConnection(conn);
                return RegistryFakeTransport.TextResult("contents");
            };

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = New(streamer, reg, "test-model");
            orch.RevealedToolNames = names;
            orch.FrozenToolDefs = reg.FunctionDefsForNames(null, names);

            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.Equal(2, streamer.Calls);
            Assert.False(ui.ToolErrors[0]); // the call before the churn succeeded
            // The SECOND request — after the registry emptied — still exposes both frozen defs.
            Assert.NotNull(streamer.SeenTools[1]);
            Assert.Equal(2, streamer.SeenTools[1].Count);
            var exposed = new List<string>();
            foreach (var def in streamer.SeenTools[1]) exposed.Add((string)def["function"]["name"]);
            Assert.Contains("files__read", exposed);
            Assert.Contains("files__write", exposed);
        }

        [Fact]
        public void Live_derivation_collapses_to_the_recovery_handle_when_registry_empties_mid_turn()
        {
            // Control for the regression test above: WITHOUT freezing, the same mid-turn churn strips
            // the next iteration's tools down to the bare reveal_tools recovery handle (the fail-fast
            // guardrail keeps that one def so the turn can pull tools back in when servers return).
            // This documents the failure mode the freeze fixes — if this test ever starts failing
            // because the full tool set survives, the live path grew real resilience and the freeze
            // may be simplifiable.
            RegistryFakeTransport ft;
            var conn = FakeConn.Ready("files", out ft, new ToolDef("read"), new ToolDef("write"));
            var reg = new McpToolRegistry(null);
            reg.AddConnection(conn);
            ft.OnCall = delegate(string name, JObject args)
            {
                reg.RemoveConnection(conn);
                return RegistryFakeTransport.TextResult("contents");
            };

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = New(streamer, reg, "test-model");
            orch.RevealedToolNames = new List<string>(reg.NamesForWorkdir(null));

            orch.RunTurn(new List<ChatMessage>(), "go", new RecordingUi());

            Assert.Equal(2, streamer.Calls);
            // Every real tool is gone on the next iteration; only the reveal_tools recovery handle
            // remains (the fail-fast guardrail — before it, the array was null and unrecoverable).
            Assert.NotNull(streamer.SeenTools[1]);
            Assert.Single(streamer.SeenTools[1]);
            Assert.Equal("reveal_tools", (string)streamer.SeenTools[1][0]["function"]["name"]);
        }

        [Fact]
        public void Frozen_turn_is_exempt_from_reveal_eviction_on_noncaching_models()
        {
            // Eviction trims RevealedToolNames to the cap on non-caching providers — but a frozen
            // turn's exposure never shrinks with that list, so trimming it would only desync the
            // reveal-enforcement gate from the schemas the model can plainly see.
            var tools = new List<ToolDef>();
            for (int i = 0; i < 30; i++) tools.Add(new ToolDef("t" + i.ToString("00")));
            var conn = FakeConn.Ready("files", tools.ToArray());
            var reg = new McpToolRegistry(null);
            reg.AddConnection(conn);
            var names = new List<string>(reg.NamesForWorkdir(null));
            Assert.True(names.Count > McpChatOrchestrator.RevealEvictionCap);

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = New(streamer, reg, "mistralai/mistral-large"); // non-caching per model catalog
            orch.RevealedToolNames = names;
            orch.FrozenToolDefs = reg.FunctionDefsForNames(null, names);

            orch.RunTurn(new List<ChatMessage>(), "go", new RecordingUi());

            Assert.Equal(30, names.Count); // NOT trimmed to the cap
            Assert.Equal(30, streamer.SeenTools[0].Count); // all frozen defs exposed, no reveal_tools
        }

        [Fact]
        public void Nonfrozen_turn_still_evicts_on_noncaching_models()
        {
            // Guard: the frozen exemption must not have disabled eviction for ordinary turns.
            var tools = new List<ToolDef>();
            for (int i = 0; i < 30; i++) tools.Add(new ToolDef("t" + i.ToString("00")));
            var conn = FakeConn.Ready("files", tools.ToArray());
            var reg = new McpToolRegistry(null);
            reg.AddConnection(conn);
            var names = new List<string>(reg.NamesForWorkdir(null));

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = New(streamer, reg, "mistralai/mistral-large");
            orch.RevealedToolNames = names;

            orch.RunTurn(new List<ChatMessage>(), "go", new RecordingUi());

            Assert.Equal(McpChatOrchestrator.RevealEvictionCap, names.Count); // trimmed
            // reveal_tools + the surviving revealed defs
            Assert.Equal(McpChatOrchestrator.RevealEvictionCap + 1, streamer.SeenTools[0].Count);
        }

        [Fact]
        public void Frozen_turn_out_of_set_call_gets_honest_error_not_reveal_hint()
        {
            // A frozen child has no reveal_tools, so the standard "reveal it first" recovery hint
            // would send the model chasing a tool it cannot call — its own doom-loop seed.
            RegistryFakeTransport ft;
            var conn = FakeConn.Ready("files", out ft, new ToolDef("read"), new ToolDef("write"));
            var reg = new McpToolRegistry(null);
            reg.AddConnection(conn);

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__write", "{}")); // resolvable but out of set
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = New(streamer, reg, "test-model");
            orch.RevealedToolNames = new List<string> { "files__read" };
            orch.FrozenToolDefs = reg.FunctionDefsForNames(null, orch.RevealedToolNames);

            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Contains("not in this agent's tool set", ui.ToolResults[0]);
            Assert.DoesNotContain("reveal_tools", ui.ToolResults[0]);
        }

        [Fact]
        public void Frozen_turn_exposes_no_reveal_tools_and_no_names_manifest()
        {
            // A11: nothing to progressively discover — no reveal_tools def, no <available_tools> tail.
            var conn = FakeConn.Ready("files", new ToolDef("read"));
            var reg = new McpToolRegistry(null);
            reg.AddConnection(conn);

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = New(streamer, reg, "test-model");
            orch.RevealedToolNames = new List<string> { "files__read" };
            orch.FrozenToolDefs = reg.FunctionDefsForNames(null, orch.RevealedToolNames);

            orch.RunTurn(new List<ChatMessage>(), "go", new RecordingUi());

            Assert.Single(streamer.SeenTools[0]);
            Assert.Equal("files__read", (string)streamer.SeenTools[0][0]["function"]["name"]);
            foreach (var m in streamer.SeenMessages[0])
                if (m.Content != null)
                    Assert.DoesNotContain("available_tools", m.Content);
        }
    }

    public class FunctionDefsForNamesTests
    {
        [Fact]
        public void Matches_ExposedFunctionDefs_minus_the_leading_reveal_tools_def()
        {
            var conn = FakeConn.Ready("files", new ToolDef("read"), new ToolDef("write"));
            var reg = new McpToolRegistry(null);
            reg.AddConnection(conn);
            var names = new List<string>(reg.NamesForWorkdir(null));

            var exposed = reg.ExposedFunctionDefs(null, names);
            var frozen = reg.FunctionDefsForNames(null, names);

            Assert.Equal(exposed.Count - 1, frozen.Count);
            Assert.True(reg.IsRevealTools((string)exposed[0]["function"]["name"]));
            for (int i = 0; i < frozen.Count; i++)
                Assert.True(JToken.DeepEquals(exposed[i + 1], frozen[i]),
                    "def " + i + " diverged from ExposedFunctionDefs");
        }

        [Fact]
        public void Null_or_unknown_names_yield_no_defs()
        {
            var conn = FakeConn.Ready("files", new ToolDef("read"));
            var reg = new McpToolRegistry(null);
            reg.AddConnection(conn);

            Assert.Empty(reg.FunctionDefsForNames(null, null));
            Assert.Empty(reg.FunctionDefsForNames(null, new[] { "files__nope", "ghost__tool" }));
        }

        [Fact]
        public void Workdir_scoped_defs_resolve_only_for_their_folder()
        {
            var conn = FakeConn.Ready("files", new ToolDef("read"));
            var reg = new McpToolRegistry(null);
            reg.AddConnection(conn, "C:\\a");
            var names = new[] { "files__read" };

            Assert.Single(reg.FunctionDefsForNames("C:\\a", names));
            Assert.Empty(reg.FunctionDefsForNames("C:\\b", names));
        }
    }

    // AgentDispatcher.RunChild wiring of the freeze: a child gets its parent-derived defs frozen at
    // dispatch, and an EMPTY resolution falls back to live derivation instead of pinning toollessness.
    public sealed class AgentDispatcherFreezeTests : System.IDisposable
    {
        private readonly string _dir;

        public AgentDispatcherFreezeTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "gxpt_freeze_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private Agent WriteAgent(string slug, string desc, string body)
        {
            string file = Path.Combine(_dir, slug + ".md");
            File.WriteAllText(file, "---\nname: " + slug + "\ndescription: " + desc + "\n---\n" + body + "\n",
                              new UTF8Encoding(false));
            AgentCatalog cat = AgentCatalog.Build(_dir, null);
            Agent a;
            cat.TryGet(slug, out a);
            return a;
        }

        private AgentDispatcher Dispatcher(ScriptedStreamer streamer, McpToolRegistry reg, string workdir,
                                           params Agent[] agents)
        {
            return new AgentDispatcher(new List<Agent>(agents), streamer, reg, null,
                "parent-model", workdir, null,
                delegate(string n) { return ToolTier.ReadOnly; }, 25, 60000);
        }

        [Fact]
        public void Child_is_dispatched_with_frozen_defs_and_no_reveal_manifest()
        {
            Agent a = WriteAgent("worker", "Works.", "You work.");
            var conn = FakeConn.Ready("files", new ToolDef("read"));
            var reg = new McpToolRegistry(null);
            reg.AddConnection(conn, "C:\\proj");

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("done"));

            Dispatcher(streamer, reg, "C:\\proj", a)
                .Dispatch("{\"agents\":[{\"name\":\"worker\",\"task\":\"t\"}]}");

            // The child's request exposes the frozen MCP def plus the cd host tool — no reveal_tools.
            var tools = streamer.SeenTools[0];
            Assert.NotNull(tools);
            var exposedNames = new List<string>();
            foreach (var def in tools) exposedNames.Add((string)def["function"]["name"]);
            Assert.Contains("files__read", exposedNames);
            Assert.Contains("cd", exposedNames);
            Assert.DoesNotContain("reveal_tools", exposedNames);
            // Frozen turns carry no <available_tools> names manifest — nothing to discover.
            foreach (var m in streamer.SeenMessages[0])
                if (m.Content != null)
                    Assert.DoesNotContain("available_tools", m.Content);
        }

        [Fact]
        public void Empty_resolution_at_dispatch_falls_back_to_live_derivation_and_recovers()
        {
            // A child dispatched while the registry is empty (servers faulted or mid-reconnect) must
            // NOT be pinned toolless for its whole run: with no frozen defs it re-derives live, so
            // when the registry refills mid-run its tools (reveal_tools + manifest) appear.
            Agent a = WriteAgent("worker", "Works.", "You work.");
            var reg = new McpToolRegistry(null); // empty at dispatch
            var conn = FakeConn.Ready("files", new ToolDef("read"));

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}")); // fails: no registry entry
            streamer.Turns.Add(Chunks.OneToolCall("c2", "files__read", "{}")); // gate: not revealed yet
            streamer.Turns.Add(Chunks.Text("done"));
            // The registry refills between the child's first and second iterations ("server back up").
            streamer.OnCall = delegate(int idx) { if (idx == 1) reg.AddConnection(conn, "C:\\proj"); };

            Dispatcher(streamer, reg, "C:\\proj", a)
                .Dispatch("{\"agents\":[{\"name\":\"worker\",\"task\":\"t\"}]}");

            Assert.Equal(3, streamer.Calls);
            // Third request sees the recovered registry: reveal_tools is back in the exposed defs and
            // the ephemeral tail advertises the recovered tool. A frozen-empty child would have kept
            // the first request's toolless shape forever.
            var lastTools = streamer.SeenTools[2];
            Assert.NotNull(lastTools);
            bool hasReveal = false;
            foreach (var def in lastTools)
                if ((string)def["function"]["name"] == "reveal_tools") hasReveal = true;
            Assert.True(hasReveal, "live derivation did not resume after the registry refilled");
            var msgs = streamer.SeenMessages[2];
            Assert.Contains("files__read", msgs[msgs.Count - 1].Content);
        }
    }
}
