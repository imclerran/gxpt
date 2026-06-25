using System.Collections.Generic;
using GxPT;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxPT.Tests.Mcp
{
    public class McpChatOrchestratorTests
    {
        private static McpToolRegistry RegistryWith(out RegistryFakeTransport ft, string server, params ToolDef[] tools)
        {
            var conn = FakeConn.Ready(server, out ft, tools);
            var reg = new McpToolRegistry(null);
            reg.AddConnection(conn);
            return reg;
        }

        private static McpChatOrchestrator New(ScriptedStreamer s, McpToolRegistry reg)
        {
            var orch = new McpChatOrchestrator(s, reg, null, "test-model", null);
            // ExecuteCall now enforces reveal-before-call. Real turns reveal a tool before invoking it,
            // so pre-reveal the whole catalog here to keep dispatch tests focused on the call path. Tests
            // that need the unrevealed-tool or exposure behavior construct the orchestrator directly or
            // overwrite RevealedToolNames after New().
            if (reg != null) orch.RevealedToolNames = new List<string>(reg.NamesForWorkdir(null));
            return orch;
        }

        [Fact]
        public void Single_tool_call_then_result_then_final_answer()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("file contents"); };

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("call_1", "files__read", "{\"path\":\"a.txt\"}"));
            streamer.Turns.Add(Chunks.Text("Here is the file."));

            var history = new List<ChatMessage>();
            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(history, "read a.txt", ui);

            Assert.True(ui.Completed);
            Assert.Equal("Here is the file.", ui.Text.ToString());
            Assert.Equal(new[] { "files__read" }, ui.ToolCalls.ToArray());
            Assert.Contains("file contents", ui.ToolResults[0]);
            Assert.False(ui.ToolErrors[0]);
            Assert.Equal(2, streamer.Calls);

            // history: user, assistant(+tool_calls), tool(result), assistant(final)
            Assert.Equal(4, history.Count);
            Assert.Equal("user", history[0].Role);
            Assert.Equal("assistant", history[1].Role);
            Assert.NotNull(history[1].ToolCalls);
            Assert.Single(history[1].ToolCalls);
            Assert.Equal("tool", history[2].Role);
            Assert.Equal("call_1", history[2].ToolCallId);
            Assert.Contains("file contents", history[2].Content);
            Assert.Equal("assistant", history[3].Role);
            Assert.Equal("Here is the file.", history[3].Content);
        }

        [Fact]
        public void Scratch_workingdir_resolves_command_tool_and_injects_scratch_note_not_workspace()
        {
            // No user workspace, but a per-conversation scratch dir runs the command server. The
            // orchestrator resolves the scratch-bound tool via ScratchWorkingDir (not WorkingDir) and
            // tells the model about the scratch sandbox - never presenting it as a real workspace.
            RegistryFakeTransport ft;
            var conn = FakeConn.Ready("command", out ft, new ToolDef("run"));
            var reg = new McpToolRegistry(null);
            reg.AddConnection(conn, "C:\\scratch\\abc"); // scoped to the scratch dir
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("ran"); };

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "command__run", "{\"cmd\":\"echo hi\"}"));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = New(streamer, reg);
            orch.WorkingDir = null;
            orch.ScratchWorkingDir = "C:\\scratch\\abc";
            orch.RevealedToolNames = new List<string> { "command__run" }; // pre-revealed so it's exposed

            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "run echo", ui);

            // The call routed to the scratch-bound connection (not "[Unknown tool]").
            Assert.Equal(new[] { "command__run" }, ui.ToolCalls.ToArray());
            Assert.Contains("ran", ui.ToolResults[0]);
            Assert.False(ui.ToolErrors[0]);

            // Stable head carries the scratch note and NO workspace block.
            var msgs = streamer.SeenMessages[0];
            Assert.Equal("system", msgs[0].Role);
            Assert.Contains("operating as an agent", msgs[0].Content);
            Assert.Equal("system", msgs[1].Role);
            Assert.Contains("temporary scratch directory", msgs[1].Content);
            Assert.Contains("C:\\scratch\\abc", msgs[1].Content);
            foreach (var m in msgs)
                if (m.Role == "system" && m.Content != null)
                    Assert.DoesNotContain("running in this workspace directory", m.Content);
            // The manifest tail advertises the scratch command tool.
            Assert.Contains("command__run", msgs[msgs.Count - 1].Content);
        }

        [Fact]
        public void Scratch_note_is_absent_when_no_scratch_dir()
        {
            Assert.Null(McpChatOrchestrator.ScratchSystemMessage(null));
            Assert.Null(McpChatOrchestrator.ScratchSystemMessage(""));
            Assert.Contains("C:\\scratch\\x", McpChatOrchestrator.ScratchSystemMessage("C:\\scratch\\x"));
        }

        [Fact]
        public void Passes_manifest_tail_and_tools_to_streamer()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("hi"));

            New(streamer, reg).RunTurn(new List<ChatMessage>(), "hello", new RecordingUi());

            // Request layout (prompt-caching zones): stable system head, then history, then the
            // ephemeral context tail (a trailing user message carrying the names manifest).
            var msgs = streamer.SeenMessages[0];
            Assert.Equal("system", msgs[0].Role);
            Assert.Contains("operating as an agent", msgs[0].Content); // agentic behavior guidance
            Assert.Equal("user", msgs[1].Role);
            Assert.Equal("hello", msgs[1].Content);
            var tail = msgs[msgs.Count - 1];
            Assert.Equal("user", tail.Role);
            Assert.Contains("Ephemeral context", tail.Content);  // framed as host-appended context
            Assert.Contains("reveal_tools", tail.Content);       // manifest instructs reveal-before-call
            Assert.Contains("files__read", tail.Content);        // and lists tool names
            // exposed tools always lead with reveal_tools
            Assert.Equal("reveal_tools", (string)streamer.SeenTools[0][0]["function"]["name"]);
        }

        [Fact]
        public void Cache_breakpoints_ride_the_stable_head_and_newest_history_message()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("hi"));

            var history = new List<ChatMessage>();
            New(streamer, reg).RunTurn(history, "hello", new RecordingUi());

            var msgs = streamer.SeenMessages[0];
            // breakpoint #1: last message of the stable system head
            Assert.True(msgs[0].CacheControl);
            // breakpoint #2: the newest history message (the user turn), not the ephemeral tail
            Assert.True(msgs[1].CacheControl);
            Assert.False(msgs[msgs.Count - 1].CacheControl);
            // the flag lands on a request-local clone, never on persisted history
            Assert.False(history[0].CacheControl);
        }

        [Fact]
        public void Project_instructions_join_the_stable_head_and_carry_breakpoint_1()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("hi"));

            var orch = New(streamer, reg);
            orch.ProjectInstructions = "AGENTS.md says: always run the tests.";
            orch.RunTurn(new List<ChatMessage>(), "hello", new RecordingUi());

            // Zone A: agent prompt, then the project-instructions block as the last head message
            // (no workspace block here - WorkingDir is unset), so breakpoint #1 rides it and the
            // tools + system head cache as one prefix.
            var msgs = streamer.SeenMessages[0];
            Assert.Equal("system", msgs[0].Role);
            Assert.Contains("operating as an agent", msgs[0].Content);
            Assert.Equal("system", msgs[1].Role);
            Assert.Equal("AGENTS.md says: always run the tests.", msgs[1].Content);
            Assert.False(msgs[0].CacheControl);
            Assert.True(msgs[1].CacheControl);   // breakpoint #1: last message of the stable head
            Assert.Equal("user", msgs[2].Role);  // history starts right after the head
            Assert.Equal("hello", msgs[2].Content);
        }

        [Fact]
        public void Intermediate_breakpoints_bridge_long_tool_fanouts()
        {
            // A single iteration with K tool calls appends ~2K+1 content blocks; beyond Anthropic's
            // ~20-block matcher lookback the next request's end breakpoint can't find the previous
            // cache entry. The two spare breakpoint slots are spaced backward from the end so every
            // inter-breakpoint span stays within the lookback.
            var msgs = new List<ChatMessage> { new ChatMessage("system", "head") };
            int headCount = 1;
            msgs.Add(new ChatMessage("user", "go"));
            var asst = new ChatMessage("assistant", "fanning out");
            asst.ToolCalls = new List<ToolCall>();
            for (int i = 0; i < 12; i++) asst.ToolCalls.Add(new ToolCall("c" + i, "files__read", "{}"));
            msgs.Add(asst);
            for (int i = 0; i < 12; i++)
            {
                var t = new ChatMessage("tool", "result " + i);
                t.ToolCallId = "c" + i;
                msgs.Add(t);
            }

            McpChatOrchestrator.ApplyCacheBreakpoints(msgs, headCount);

            int flags = 0;
            foreach (var m in msgs) if (m.CacheControl) flags++;
            Assert.True(flags >= 3, "expected intermediate flags, got " + flags);
            Assert.True(flags <= 4, "must stay within Anthropic's 4-breakpoint limit, got " + flags);
            Assert.True(msgs[0].CacheControl);                 // stable head
            Assert.True(msgs[msgs.Count - 1].CacheControl);    // newest message

            // No span between consecutive breakpoints may exceed the ~20-block lookback.
            int run = 0, maxRun = 0;
            for (int i = headCount; i < msgs.Count; i++)
            {
                run += McpChatOrchestrator.EstimateContentBlocks(msgs[i]);
                if (msgs[i].CacheControl) { if (run > maxRun) maxRun = run; run = 0; }
            }
            Assert.True(maxRun <= 20, "max inter-breakpoint span " + maxRun + " blocks");
        }

        [Fact]
        public void Content_block_estimate_counts_tool_calls()
        {
            Assert.Equal(1, McpChatOrchestrator.EstimateContentBlocks(new ChatMessage("user", "hi")));
            var toolMsg = new ChatMessage("tool", "result");
            Assert.Equal(1, McpChatOrchestrator.EstimateContentBlocks(toolMsg));
            var asst = new ChatMessage("assistant", "text");
            asst.ToolCalls = new List<ToolCall> { new ToolCall("a", "t", "{}"), new ToolCall("b", "t", "{}") };
            Assert.Equal(3, McpChatOrchestrator.EstimateContentBlocks(asst)); // 2 tool_use + 1 text
            var silent = new ChatMessage("assistant", "");
            silent.ToolCalls = new List<ToolCall> { new ToolCall("a", "t", "{}") };
            Assert.Equal(1, McpChatOrchestrator.EstimateContentBlocks(silent)); // tool_use only
        }

        [Fact]
        public void Sticky_provider_routing_latches_on_a_demonstrated_cache_hit()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("x"); };

            var streamer = new ScriptedStreamer();
            streamer.ServeAs = "Amazon Bedrock";
            streamer.ServeCachedTokens = 1500; // response demonstrates a cache read
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));
            streamer.Turns.Add(Chunks.Text("done"));

            // anthropic/* is a prompt-caching model -> stickiness active
            var orch = new McpChatOrchestrator(streamer, reg, null, "anthropic/claude-sonnet-4.5", null);
            string persisted = null;
            orch.ProviderServed = delegate(string p) { persisted = p; };
            orch.RunTurn(new List<ChatMessage>(), "go", new RecordingUi());

            Assert.Null(streamer.SeenProps[0].ProviderOrder);            // no cache activity observed yet
            Assert.Equal("Amazon Bedrock", streamer.SeenProps[1].ProviderOrder[0]); // iteration 2 follows the cache
            Assert.Equal("Amazon Bedrock", persisted);                   // host persistence hook fired
        }

        [Fact]
        public void Sticky_provider_routing_latches_on_a_cache_write_too()
        {
            // A cache write proves this endpoint caches AND now holds the conversation's warm
            // entry, so explicit-caching providers latch from the very first request - no need to
            // wait for the first hit.
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("x"); };

            var streamer = new ScriptedStreamer();
            streamer.ServeAs = "Anthropic";
            streamer.ServeCacheWriteTokens = 900; // first request: cold write, no read
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = new McpChatOrchestrator(streamer, reg, null, "anthropic/claude-sonnet-4.5", null);
            orch.RunTurn(new List<ChatMessage>(), "go", new RecordingUi());

            Assert.Null(streamer.SeenProps[0].ProviderOrder);
            Assert.Equal("Anthropic", streamer.SeenProps[1].ProviderOrder[0]); // latched off the write
        }

        [Fact]
        public void Uncached_responses_do_not_create_provider_stickiness()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("x"); };

            var streamer = new ScriptedStreamer();
            streamer.ServeAs = "SomeHost";
            streamer.ServeCachedTokens = 0; // served, but no cache activity demonstrated (read or write)
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = new McpChatOrchestrator(streamer, reg, null, "anthropic/claude-sonnet-4.5", null);
            string persisted = null;
            orch.ProviderServed = delegate(string p) { persisted = p; };
            orch.RunTurn(new List<ChatMessage>(), "go", new RecordingUi());

            Assert.Null(streamer.SeenProps[0].ProviderOrder);
            Assert.Null(streamer.SeenProps[1].ProviderOrder); // still load-balanced: no hit, no stick
            Assert.Null(persisted);
        }

        [Fact]
        public void Usage_is_reported_to_the_host_on_every_iteration_for_any_model()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("x"); };

            var streamer = new ScriptedStreamer();
            streamer.ServeAs = "SomeHost";   // non-caching model: no stickiness, but usage still flows
            streamer.ServeCost = 0.01m;
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = New(streamer, reg); // model "test-model"
            var reported = new List<ResponseUsage>();
            orch.UsageReported = delegate(ResponseUsage u) { reported.Add(u); };
            orch.RunTurn(new List<ChatMessage>(), "go", new RecordingUi());

            Assert.Equal(2, reported.Count); // one per loop iteration
            Assert.Equal(0.01m, reported[0].Cost);
            Assert.Equal("SomeHost", reported[1].Provider);
        }

        [Fact]
        public void Confirmed_provider_preference_survives_an_uncached_response()
        {
            // A cached=0 response from the confirmed provider is usually TTL expiry - keeping the
            // preference makes the cache rebuild land on the same provider, so it must not clear.
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("x"); };

            var streamer = new ScriptedStreamer();
            streamer.ServeAs = "Anthropic";
            streamer.ServeCachedTokens = 0;
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = new McpChatOrchestrator(streamer, reg, null, "anthropic/claude-sonnet-4.5", null);
            orch.PreferredProvider = "Amazon Bedrock"; // confirmed on an earlier turn
            orch.RunTurn(new List<ChatMessage>(), "go", new RecordingUi());

            Assert.Equal("Amazon Bedrock", streamer.SeenProps[0].ProviderOrder[0]);
            Assert.Equal("Amazon Bedrock", streamer.SeenProps[1].ProviderOrder[0]); // not cleared
        }

        [Fact]
        public void Sticky_provider_routing_is_off_for_non_caching_models()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("x"); };

            var streamer = new ScriptedStreamer();
            streamer.ServeAs = "SomeProvider";
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = New(streamer, reg); // model "test-model" has no prompt caching
            orch.PreferredProvider = "SomeProvider"; // even a seeded preference is not emitted
            orch.RunTurn(new List<ChatMessage>(), "go", new RecordingUi());

            Assert.Null(streamer.SeenProps[0].ProviderOrder);
            Assert.Null(streamer.SeenProps[1].ProviderOrder);
            // (the usage callback is still registered on non-caching models - cost accounting
            // applies everywhere - but the stickiness gate inside it never fires)
        }

        [Fact]
        public void Multiple_tool_calls_in_one_turn_run_serially()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"), new ToolDef("list"));
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("r:" + name); };

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(new[]
            {
                Chunks.ToolChunk(0, "c1", "files__read", "{}", null),
                Chunks.ToolChunk(1, "c2", "files__list", "{}", "tool_calls")
            });
            streamer.Turns.Add(Chunks.Text("done"));

            var history = new List<ChatMessage>();
            New(streamer, reg).RunTurn(history, "go", new RecordingUi());

            Assert.Equal(new[] { "read", "list" }, ft.CalledTools.ToArray());
            // user, assistant(2 calls), tool, tool, assistant(final)
            Assert.Equal(5, history.Count);
            Assert.Equal(2, history[1].ToolCalls.Count);
            Assert.Equal("tool", history[2].Role);
            Assert.Equal("tool", history[3].Role);
        }

        [Fact]
        public void Reveal_tools_then_follow_up_call_succeeds_locally()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("content"); };

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("r1", "reveal_tools", "{\"names\":[\"files__read\"]}"));
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));
            streamer.Turns.Add(Chunks.Text("final"));

            var history = new List<ChatMessage>();
            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(history, "use the read tool", ui);

            Assert.Equal(3, streamer.Calls);
            Assert.Equal(new[] { "reveal_tools", "files__read" }, ui.ToolCalls.ToArray());
            // reveal_tools is local — only the real tool hit the transport
            Assert.Equal(new[] { "read" }, ft.CalledTools.ToArray());
            // the reveal result lists the requested def
            Assert.Contains("files__read", ui.ToolResults[0]);
            Assert.False(ui.ToolErrors[0]);
            Assert.Equal("final", ui.Text.ToString());
        }

        [Fact]
        public void Hitting_iteration_cap_wraps_up_with_a_model_message()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("x"); };

            var streamer = new ScriptedStreamer();
            for (int i = 0; i < 3; i++) streamer.Turns.Add(Chunks.OneToolCall("c" + i, "files__read", "{}"));
            // The tool-less wrap-up call (after the cap) gets this text.
            streamer.Fallback = delegate(int i) { return Chunks.Text("Summary; how should I proceed?"); };

            // cap 3, no ContinuationDecider => wrap up rather than dead-end.
            var orch = new McpChatOrchestrator(streamer, reg, null, "m", null, 3, 1000);
            var history = new List<ChatMessage>();
            var ui = new RecordingUi();
            orch.RunTurn(history, "loop forever", ui);

            Assert.True(ui.Completed);
            Assert.Equal(4, streamer.Calls);                       // 3 tool iterations + 1 wrap-up
            // The wrap-up keeps the loop's tools array (dropping it would change position 0 of the
            // prompt and forfeit the cached prefix) but forbids further calls via tool_choice "none".
            Assert.NotNull(streamer.SeenTools[3]);
            Assert.Equal("none", streamer.SeenProps[3].ToolChoice);
            Assert.Null(streamer.SeenProps[2].ToolChoice);         // loop iterations leave it default
            // The wrap-up instruction must be a trailing user turn, not a system message: Anthropic
            // hoists in-array system messages out of position, leaving nothing for the model to answer.
            var wrapMsgs = streamer.SeenMessages[3];
            var lastSent = wrapMsgs[wrapMsgs.Count - 1];
            Assert.Equal("user", lastSent.Role);
            Assert.Contains("maximum number of tool calls", lastSent.Content);
            Assert.Equal("assistant", history[history.Count - 1].Role);
            Assert.Equal("Summary; how should I proceed?", history[history.Count - 1].Content);
            Assert.Contains("how should I proceed", ui.Text.ToString());
        }

        [Fact]
        public void Continuing_at_the_cap_grants_another_budget()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("x"); };

            var streamer = new ScriptedStreamer();
            streamer.Fallback = delegate(int i) { return Chunks.OneToolCall("c" + i, "files__read", "{}"); };

            var orch = new McpChatOrchestrator(streamer, reg, null, "m", null, 2, 1000);
            int asked = 0;
            orch.ContinuationDecider = delegate(int n) { asked++; return asked == 1; }; // continue once, then stop

            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.Equal(2, asked);            // asked at the first cap (granted) and the refreshed cap (stopped)
            Assert.Equal(5, streamer.Calls);   // (2 + 2) tool iterations + 1 wrap-up
            Assert.True(ui.Completed);
        }

        [Fact]
        public void Empty_response_is_retried_once_then_proceeds()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("ok"); };

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(new ChatCompletionChunk[0]);                     // empty: no text, no tool calls
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));  // retry yields a tool call
            streamer.Turns.Add(Chunks.Text("done"));

            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.Equal(3, streamer.Calls);   // empty + retry(tool call) + final
            Assert.Equal(new[] { "files__read" }, ui.ToolCalls.ToArray());
            Assert.Equal("done", ui.Text.ToString());
            Assert.True(ui.Completed);
        }

        [Fact]
        public void Empty_response_after_retry_and_nudge_surfaces_a_notice()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(new ChatCompletionChunk[0]);   // initial: empty
            streamer.Turns.Add(new ChatCompletionChunk[0]);   // bare retry: still empty
            streamer.Turns.Add(new ChatCompletionChunk[0]);   // nudge-continue: still empty

            var history = new List<ChatMessage>();
            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(history, "go", ui);

            Assert.True(ui.Completed);
            Assert.Equal(3, streamer.Calls);   // initial + bare retry + nudge, then surface
            Assert.Contains("empty response", history[history.Count - 1].Content.ToLowerInvariant());
            Assert.Contains("empty response", ui.Text.ToString().ToLowerInvariant());
        }

        [Fact]
        public void Empty_after_retry_is_nudged_once_then_recovers()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(new ChatCompletionChunk[0]);   // initial: empty
            streamer.Turns.Add(new ChatCompletionChunk[0]);   // bare retry: still empty
            streamer.Turns.Add(Chunks.Text("recovered"));     // nudge-continue: model answers

            var history = new List<ChatMessage>();
            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(history, "go", ui);

            Assert.Equal(3, streamer.Calls);   // empty + retry + nudge(answer)
            Assert.Equal("recovered", ui.Text.ToString());
            Assert.True(ui.Completed);

            // The nudge rides the third request as its last (user-role) message...
            var nudgeReq = streamer.SeenMessages[2];
            var last = nudgeReq[nudgeReq.Count - 1];
            Assert.Equal("user", last.Role);
            Assert.Equal(McpChatOrchestrator.EmptyResponseNudge, last.Content);

            // ...but it is request-only: never written to history (not rendered or persisted).
            foreach (var m in history)
                Assert.NotEqual(McpChatOrchestrator.EmptyResponseNudge, m.Content);
        }

        [Fact]
        public void Empty_response_recovery_is_skipped_when_cancelled()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));

            var cancel = new RequestCancellation();
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(new ChatCompletionChunk[0]);   // initial: empty
            streamer.Turns.Add(new ChatCompletionChunk[0]);   // retry - must NOT run after cancel
            // Simulate the user pressing Stop the moment the first (empty) response lands.
            streamer.OnCall = delegate(int idx) { if (idx == 0) cancel.Cancel(); };

            var history = new List<ChatMessage>();
            var ui = new RecordingUi();
            var orch = New(streamer, reg);
            orch.Cancellation = cancel;
            orch.RunTurn(history, "go", ui);

            Assert.True(ui.Completed);
            Assert.Equal(1, streamer.Calls);   // no recovery request issued once Stop was pressed
            // A cancelled turn surfaces no empty-response notice.
            foreach (var m in history)
                Assert.False(m.Content != null
                    && m.Content.ToLowerInvariant().Contains("empty response"));
        }

        [Fact]
        public void Tool_isError_result_is_fed_back_and_loop_continues()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string name, JObject args)
            {
                JObject r = RegistryFakeTransport.TextResult("boom");
                r["isError"] = true;
                return r;
            };

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));
            streamer.Turns.Add(Chunks.Text("recovered"));

            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Contains("boom", ui.ToolResults[0]);
            Assert.Equal("recovered", ui.Text.ToString());
            Assert.True(ui.Completed);
        }

        [Fact]
        public void Transport_fault_during_call_surfaces_server_unavailable()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.ThrowTransportOnCall = true;

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));
            streamer.Turns.Add(Chunks.Text("after"));

            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Equal("[Server unavailable.]", ui.ToolResults[0]);
            Assert.Equal("after", ui.Text.ToString());
        }

        [Fact]
        public void Json_rpc_error_during_call_surfaces_tool_error()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.ErrorOnCall = true;

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));
            streamer.Turns.Add(Chunks.Text("after"));

            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.StartsWith("[Tool error:", ui.ToolResults[0]);
        }

        [Fact]
        public void Unknown_tool_is_reported_without_hitting_transport()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__nope", "{}"));
            streamer.Turns.Add(Chunks.Text("ok"));

            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Contains("Unknown tool", ui.ToolResults[0]);
            Assert.Empty(ft.CalledTools);
        }

        [Fact]
        public void Unrevealed_tool_is_blocked_before_approval_and_hits_no_transport()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));
            streamer.Turns.Add(Chunks.Text("ok"));

            var ui = new RecordingUi();
            // Constructed directly (not via New()) so the tool stays unrevealed - the model "called" it
            // straight off the names manifest, the very case the enforcement gate must catch.
            var orch = new McpChatOrchestrator(streamer, reg, null, "test-model", null);
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Contains("reveal_tools", ui.ToolResults[0]); // self-correcting hint, not a malformed run
            Assert.Empty(ft.CalledTools);                       // never reached the transport (nor approval)
        }

        [Fact]
        public void Unoffered_host_tool_is_not_silently_dispatched()
        {
            // No SkillTools/AgentDispatcher/AskUser are configured, so open_skill is NOT among this turn's
            // host tools. Exposure and dispatch-exemption are both driven by AvailableHostTools(), so a
            // host tool that isn't offered must fall through to the normal path ("[Unknown tool]") rather
            // than being silently handled - the two halves can't disagree.
            var reg = new McpToolRegistry(null);
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "open_skill", "{\"names\":[\"x\"]}"));
            streamer.Turns.Add(Chunks.Text("ok"));

            var ui = new RecordingUi();
            var orch = new McpChatOrchestrator(streamer, reg, null, "test-model", null);
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Contains("Unknown tool", ui.ToolResults[0]);
        }

        [Fact]
        public void Malformed_arguments_surface_as_an_error()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{not valid json"));
            streamer.Turns.Add(Chunks.Text("ok"));

            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Contains("Invalid tool arguments", ui.ToolResults[0]);
            Assert.Empty(ft.CalledTools);
        }

        [Fact]
        public void Denied_call_is_not_executed()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));
            streamer.Turns.Add(Chunks.Text("ok"));

            var ui = new RecordingUi();
            var orch = new McpChatOrchestrator(streamer, reg, new DenyAllApprovalPolicy(), "m", null);
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Equal("[Call denied by user.]", ui.ToolResults[0]);
            Assert.Empty(ft.CalledTools);
        }

        [Fact]
        public void Denied_call_forces_text_only_next_call_so_model_stops_and_asks()
        {
            // A user denial at the approval gate must make the model stop and ask how to proceed
            // instead of charging into other tool calls: the loop forces the next model request to
            // tool_choice "none" (the same mechanism as the user-stopped dispatch_agent wrap-up).
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}")); // model calls a tool
            streamer.Turns.Add(Chunks.Text(
                "I tried to read the file but you denied it. How would you like to proceed?"));

            var ui = new RecordingUi();
            var orch = new McpChatOrchestrator(streamer, reg, new DenyAllApprovalPolicy(), "m", null);
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Equal("[Call denied by user.]", ui.ToolResults[0]);
            Assert.Empty(ft.CalledTools);                          // the denied call never ran

            Assert.Equal(2, streamer.Calls);
            Assert.Null(streamer.SeenProps[0].ToolChoice);         // the initial call: normal auto
            Assert.Equal("none", streamer.SeenProps[1].ToolChoice); // after denial: forced text-only

            Assert.True(ui.Completed);
            Assert.Contains("how would you like to proceed", ui.Text.ToString().ToLowerInvariant());
        }

        [Fact]
        public void Denial_auto_denies_the_rest_of_the_same_batch()
        {
            // The model fans out three calls in one turn; the user denies the first. The remaining two
            // must be auto-denied - never executed - even though the policy would otherwise allow them
            // (proving they were halted by the prior denial, not run). The next model call is forced to
            // text-only so the model stops and asks.
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"), new ToolDef("list"), new ToolDef("write"));
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("r:" + name); };

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(new[]
            {
                Chunks.ToolChunk(0, "c1", "files__read", "{}", null),
                Chunks.ToolChunk(1, "c2", "files__list", "{}", null),
                Chunks.ToolChunk(2, "c3", "files__write", "{}", "tool_calls")
            });
            streamer.Turns.Add(Chunks.Text("You denied the first call. How would you like to proceed?"));

            var ui = new RecordingUi();
            // Denies only the first call it sees; would allow the rest. With auto-deny, the rest never
            // reach the policy at all, so nothing hits the transport.
            var orch = new McpChatOrchestrator(streamer, reg, new DenyFirstThenAllowPolicy(), "m", null);
            var history = new List<ChatMessage>();
            orch.RunTurn(history, "go", ui);

            Assert.Empty(ft.CalledTools);                              // first denied, rest auto-denied
            Assert.Equal(3, ui.ToolResults.Count);
            foreach (var r in ui.ToolResults)
                Assert.Equal("[Call denied by user.]", r);            // every call surfaced as denied
            foreach (var e in ui.ToolErrors)
                Assert.True(e);

            Assert.Equal(2, streamer.Calls);
            Assert.Equal("none", streamer.SeenProps[1].ToolChoice);   // next call forced text-only
            Assert.True(ui.Completed);
        }

        [Fact]
        public void RunTurn_overload_does_not_add_a_user_message()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("hi"));

            var history = new List<ChatMessage> { new ChatMessage("user", "already here") };
            New(streamer, reg).RunTurn(history, new RecordingUi());

            Assert.Equal(2, history.Count); // user(existing) + assistant(final); no duplicate user
            Assert.Equal("user", history[0].Role);
            Assert.Equal("already here", history[0].Content);
            Assert.Equal("assistant", history[1].Role);
        }

        [Fact]
        public void RequestMessageTransform_changes_sent_messages_not_history()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("ok"));

            var orch = New(streamer, reg);
            orch.RequestMessageTransform = delegate(IList<ChatMessage> h)
            {
                var outp = new List<ChatMessage>();
                foreach (var m in h) outp.Add(new ChatMessage(m.Role, m.Content + " [T]"));
                return outp;
            };

            var history = new List<ChatMessage>();
            orch.RunTurn(history, "hello", new RecordingUi());

            bool sentTransformed = false;
            foreach (var m in streamer.SeenMessages[0])
                if (m.Role == "user" && m.Content == "hello [T]") sentTransformed = true;
            Assert.True(sentTransformed);             // what's sent is transformed
            Assert.Equal("hello", history[0].Content); // persisted history is untouched
        }

        [Fact]
        public void Streaming_error_stops_the_turn()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));

            var streamer = new ScriptedStreamer();
            streamer.ErrorMessage = "network down";
            streamer.ErrorOnCall = 0;

            var history = new List<ChatMessage>();
            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(history, "go", ui);

            Assert.Equal("network down", ui.Error);
            Assert.False(ui.Completed);
            Assert.Single(history); // only the user message was added
        }
    }

    internal sealed class DenyAllApprovalPolicy : IToolApprovalPolicy
    {
        public ApprovalDecision Check(string functionName, JObject args) { return ApprovalDecision.Deny; }
    }

    // Denies the first call it is asked about and allows every later one. Used to prove that a denial
    // auto-denies the rest of a batch: if auto-deny works, the policy is only ever consulted once (the
    // later calls are halted before reaching it), so none of them execute.
    internal sealed class DenyFirstThenAllowPolicy : IToolApprovalPolicy
    {
        private int _seen;
        public ApprovalDecision Check(string functionName, JObject args)
        {
            return (_seen++ == 0) ? ApprovalDecision.Deny : ApprovalDecision.Allow;
        }
    }
}
