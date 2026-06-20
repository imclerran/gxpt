using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GxPT;
using GxPT.Tests.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxPT.Tests
{
    public sealed class AgentDispatcherTests : IDisposable
    {
        private readonly string _dir;

        public AgentDispatcherTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "gxpt_dispatch_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        // Writes an agent file under _dir and returns the loaded Agent (so FilePath/body are real).
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

        // A dispatcher with a null registry (child runs a pure text turn) and an allow-all approval (null
        // => the child orchestrator defaults to allow-all).
        private static AgentDispatcher Dispatcher(ScriptedStreamer streamer, params Agent[] agents)
        {
            return new AgentDispatcher(new List<Agent>(agents), streamer, null, null,
                "parent-model", null, null,
                delegate(string n) { return ToolTier.ReadOnly; }, 25, 60000);
        }

        [Fact]
        public void SingleDispatch_RunsChild_ReturnsFinalAnswer()
        {
            Agent a = WriteAgent("explorer", "Explore.", "You explore code.");
            ScriptedStreamer streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("Found it in foo.cs:42"));

            string result = Dispatcher(streamer, a)
                .Dispatch("{\"agents\":[{\"name\":\"explorer\",\"task\":\"find X\"}]}");

            Assert.Contains("## Agent: explorer", result);
            Assert.Contains("Found it in foo.cs:42", result);
        }

        [Fact]
        public void UnknownAgent_ReturnsNote_KnownStillRuns()
        {
            Agent a = WriteAgent("explorer", "Explore.", "You explore.");
            ScriptedStreamer streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("done"));   // served to the one known agent

            string result = Dispatcher(streamer, a)
                .Dispatch("{\"agents\":[{\"name\":\"ghost\",\"task\":\"t\"},{\"name\":\"explorer\",\"task\":\"t\"}]}");

            Assert.Contains("Unknown agent: ghost", result);
            Assert.Contains("done", result);
        }

        [Fact]
        public void BatchDispatch_ConcatenatesLabeledSections()
        {
            Agent a = WriteAgent("a1", "One.", "b1");
            Agent b = WriteAgent("a2", "Two.", "b2");
            ScriptedStreamer streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("answer-1"));
            streamer.Turns.Add(Chunks.Text("answer-2"));

            string result = Dispatcher(streamer, a, b)
                .Dispatch("{\"agents\":[{\"name\":\"a1\",\"task\":\"t\"},{\"name\":\"a2\",\"task\":\"t\"}]}");

            Assert.Contains("## Agent: a1", result);
            Assert.Contains("answer-1", result);
            Assert.Contains("## Agent: a2", result);
            Assert.Contains("answer-2", result);
        }

        [Fact]
        public void EmptyOrMalformed_ReturnsNote()
        {
            AgentDispatcher d = Dispatcher(new ScriptedStreamer());
            Assert.Equal("No agents specified to dispatch.", d.Dispatch("{\"agents\":[]}"));
            Assert.Equal("No agents specified to dispatch.", d.Dispatch("not json"));
            Assert.Equal("No agents specified to dispatch.", d.Dispatch(null));
        }

        [Fact]
        public void MissingTask_ReturnsNote()
        {
            Agent a = WriteAgent("explorer", "Explore.", "body");
            string result = Dispatcher(new ScriptedStreamer(), a)
                .Dispatch("{\"agents\":[{\"name\":\"explorer\"}]}");
            Assert.Contains("No task was provided", result);
        }

        [Fact]
        public void DispatchAgentDef_ShapeAndPredicates()
        {
            AgentDispatcher d = Dispatcher(new ScriptedStreamer());
            JObject def = d.DispatchAgentDef();

            Assert.Equal("function", (string)def["type"]);
            Assert.Equal("dispatch_agent", (string)def["function"]["name"]);
            Assert.NotNull(def["function"]["parameters"]["properties"]["agents"]);
            Assert.True(d.IsDispatchAgent("dispatch_agent"));
            Assert.False(d.IsDispatchAgent("files__read"));
        }

        [Fact]
        public void HasAgents_ReflectsCatalog()
        {
            Assert.False(Dispatcher(new ScriptedStreamer()).HasAgents);
            Assert.True(Dispatcher(new ScriptedStreamer(), WriteAgent("x", "d", "b")).HasAgents);
        }

        // ---- parallel fan-out (phase 7) ----

        // A read-only agent (max_tier: readonly) - eligible for the parallel path.
        private Agent WriteReadOnlyAgent(string slug, string desc)
        {
            string file = Path.Combine(_dir, slug + ".md");
            File.WriteAllText(file,
                "---\nname: " + slug + "\ndescription: " + desc + "\nmax_tier: readonly\n---\nbody\n",
                new UTF8Encoding(false));
            AgentCatalog cat = AgentCatalog.Build(_dir, null);
            Agent a;
            cat.TryGet(slug, out a);
            return a;
        }

        // A streamer whose answer is derived from the request (the task in the last user message), so a
        // parallel batch's results are deterministic regardless of which child runs first.
        private sealed class TaskEchoStreamer : IChatStreamer
        {
            public void StreamChat(string model, System.Collections.Generic.IList<ChatMessage> messages,
                System.Collections.Generic.IList<Newtonsoft.Json.Linq.JObject> tools, ClientProperties props,
                Action<ChatCompletionChunk> onChunk, Action<string> onError, RequestCancellation cancel)
            {
                string task = "";
                for (int i = messages.Count - 1; i >= 0; i--)
                    if (messages[i].Role == "user") { task = messages[i].Content; break; }
                if (onChunk != null)
                    foreach (ChatCompletionChunk ch in Chunks.Text("answer for: " + task)) onChunk(ch);
            }
        }

        [Fact]
        public void ParallelDispatch_ReadOnlyBatch_AllResultsCorrect_InOrder()
        {
            Agent a = WriteReadOnlyAgent("ra", "Read A.");
            Agent b = WriteReadOnlyAgent("rb", "Read B.");
            Agent c = WriteReadOnlyAgent("rc", "Read C.");
            var d = new AgentDispatcher(new System.Collections.Generic.List<Agent> { a, b, c },
                new TaskEchoStreamer(), null, null, "m", null, null,
                delegate(string n) { return ToolTier.ReadOnly; }, 25, 60000);

            string result = d.Dispatch(
                "{\"agents\":[{\"name\":\"ra\",\"task\":\"task-A\"}," +
                "{\"name\":\"rb\",\"task\":\"task-B\"},{\"name\":\"rc\",\"task\":\"task-C\"}]}");

            int ia = result.IndexOf("## Agent: ra");
            int ib = result.IndexOf("## Agent: rb");
            int ic = result.IndexOf("## Agent: rc");
            Assert.True(ia >= 0 && ia < ib && ib < ic);     // order preserved
            Assert.Contains("answer for: task-A", result);
            Assert.Contains("answer for: task-B", result);
            Assert.Contains("answer for: task-C", result);
        }

        [Fact]
        public void RunsInParallel_OnlyWhenMultipleAndAllReadOnly()
        {
            Agent r1 = WriteReadOnlyAgent("r1", "ro");
            Agent r2 = WriteReadOnlyAgent("r2", "ro");
            Agent w = WriteAgent("w", "writes", "b"); // default max_tier = write

            Agent[] all = new Agent[] { r1, r2, w };
            Assert.True(AgentDispatcher.RunsInParallel(all, new System.Collections.Generic.List<int> { 0, 1 }));      // both read-only
            Assert.False(AgentDispatcher.RunsInParallel(all, new System.Collections.Generic.List<int> { 0, 1, 2 }));  // one writer -> serial
            Assert.False(AgentDispatcher.RunsInParallel(all, new System.Collections.Generic.List<int> { 0 }));       // single -> serial
        }

        // ---- observability hooks (phase 7b engine) ----

        private sealed class FakeActivityUi : IAgentActivityUi
        {
            private readonly object _gate = new object();
            public int FanOutStarts, FanOutEnds, Starts, Finishes, Cancellations, LastFanOutCount;
            public int Activities, LastFanOutTaskCount;

            public void OnFanOutStart(System.Collections.Generic.IList<string> slugs,
                                      System.Collections.Generic.IList<string> tasks)
            { lock (_gate) { FanOutStarts++; LastFanOutCount = slugs.Count; LastFanOutTaskCount = (tasks != null ? tasks.Count : 0); } }
            public void OnAgentStart(int index, string slug, string task) { lock (_gate) { Starts++; } }
            public void OnAgentFinished(int index, string slug, bool cancelled)
            { lock (_gate) { Finishes++; if (cancelled) Cancellations++; } }
            public void OnAgentActivity(int index, string lastTool, int toolCount) { lock (_gate) { Activities++; } }
            public void OnFanOutEnd() { lock (_gate) { FanOutEnds++; } }
        }

        [Fact]
        public void ActivityUi_ReportsFanOutAndPerAgentLifecycle()
        {
            Agent a = WriteReadOnlyAgent("ra", "A");
            Agent b = WriteReadOnlyAgent("rb", "B");
            var ui = new FakeActivityUi();
            var d = new AgentDispatcher(new System.Collections.Generic.List<Agent> { a, b },
                new TaskEchoStreamer(), null, null, "m", null, null,
                delegate(string n) { return ToolTier.ReadOnly; }, 25, 60000);
            d.ActivityUi = ui;

            d.Dispatch("{\"agents\":[{\"name\":\"ra\",\"task\":\"t1\"},{\"name\":\"rb\",\"task\":\"t2\"}]}");

            Assert.Equal(1, ui.FanOutStarts);
            Assert.Equal(1, ui.FanOutEnds);
            Assert.Equal(2, ui.LastFanOutCount);
            Assert.Equal(2, ui.LastFanOutTaskCount);   // tier 2: tasks travel with the slugs
            Assert.Equal(2, ui.Starts);
            Assert.Equal(2, ui.Finishes);
        }

        [Fact]
        public void Dispatch_CapturesPerSlotChildTranscripts()
        {
            Agent a = WriteReadOnlyAgent("ra", "A");
            Agent b = WriteReadOnlyAgent("rb", "B");
            var d = new AgentDispatcher(new System.Collections.Generic.List<Agent> { a, b },
                new TaskEchoStreamer(), null, null, "m", null, null,
                delegate(string n) { return ToolTier.ReadOnly; }, 25, 60000);

            d.Dispatch("{\"agents\":[{\"name\":\"ra\",\"task\":\"t1\"},{\"name\":\"rb\",\"task\":\"t2\"}]}");

            AgentTranscript[] ts = d.LastTranscripts;
            Assert.NotNull(ts);
            Assert.Equal(2, ts.Length);
            Assert.NotNull(ts[0]);
            Assert.Equal("ra", ts[0].Slug);
            Assert.Equal("t1", ts[0].Task);
            // The child's message list carries at least the user task and the assistant answer.
            Assert.NotNull(ts[0].Messages);
            bool sawUserTask = false;
            foreach (ChatMessage m in ts[0].Messages)
                if (m != null && m.Role == "user" && m.Content == "t1") sawUserTask = true;
            Assert.True(sawUserTask);
        }

        [Theory]
        [InlineData("abc123", 0)]
        [InlineData("deadbeefcafe", 7)]
        public void TranscriptLinks_RoundTrip(string key, int slot)
        {
            string url = AgentTranscriptLinks.Build(key, slot);
            Assert.True(AgentTranscriptLinks.IsTranscriptLink(url));
            string k; int s;
            Assert.True(AgentTranscriptLinks.TryParse(url, out k, out s));
            Assert.Equal(key, k);
            Assert.Equal(slot, s);
        }

        [Theory]
        [InlineData("https://example.com")]
        [InlineData("gxpt-agent:")]
        [InlineData("gxpt-agent:keyonly")]
        [InlineData("gxpt-agent:key:notanumber")]
        [InlineData("")]
        [InlineData(null)]
        public void TranscriptLinks_RejectsNonLinks(string url)
        {
            string k; int s;
            Assert.False(AgentTranscriptLinks.TryParse(url, out k, out s));
        }

        [Fact]
        public void TranscriptStore_RoundTripsByKeyAndIndex()
        {
            ChatMessage msg = new ChatMessage("assistant", "hi");
            AgentTranscript[] arr = new AgentTranscript[]
            {
                new AgentTranscript("ra", "t0", new System.Collections.Generic.List<ChatMessage> { msg }),
                null
            };
            string key = "store-test-" + System.Guid.NewGuid().ToString("N");
            AgentTranscriptStore.Put(key, arr);

            Assert.Same(arr[0], AgentTranscriptStore.Get(key, 0));
            Assert.Null(AgentTranscriptStore.Get(key, 1));        // null slot
            Assert.Null(AgentTranscriptStore.Get(key, 5));        // out of range
            Assert.Null(AgentTranscriptStore.Get("no-such-key", 0));
        }

        [Fact]
        public void Dispatch_UnknownSlot_HasNullTranscript()
        {
            Agent a = WriteReadOnlyAgent("ra", "A");
            var d = new AgentDispatcher(new System.Collections.Generic.List<Agent> { a },
                new TaskEchoStreamer(), null, null, "m", null, null,
                delegate(string n) { return ToolTier.ReadOnly; }, 25, 60000);

            // slot 0 = unknown agent (no child), slot 1 = real agent
            d.Dispatch("{\"agents\":[{\"name\":\"ghost\",\"task\":\"t0\"},{\"name\":\"ra\",\"task\":\"t1\"}]}");

            AgentTranscript[] ts = d.LastTranscripts;
            Assert.NotNull(ts);
            Assert.Equal(2, ts.Length);
            Assert.Null(ts[0]);             // unknown slot ran no child
            Assert.NotNull(ts[1]);
            Assert.Equal("ra", ts[1].Slug);
        }

        [Fact]
        public void ActivityUi_NoFanOutWhenNothingRunnable()
        {
            var ui = new FakeActivityUi();
            AgentDispatcher d = Dispatcher(new ScriptedStreamer());   // no agents in catalog
            d.ActivityUi = ui;

            d.Dispatch("{\"agents\":[{\"name\":\"ghost\",\"task\":\"t\"}]}");

            Assert.Equal(0, ui.FanOutStarts);   // nothing runnable -> no fan-out announced
            Assert.Equal(0, ui.FanOutEnds);
        }

        [Fact]
        public void ActivityUi_ReportsCancelledFinish_WhenGroupCancelled()
        {
            Agent a = WriteAgent("w", "writes", "b");
            var group = new RequestCancellation();
            group.Cancel();   // user stopped: the child bails and its finish is reported as cancelled
            var ui = new FakeActivityUi();
            AgentDispatcher d = Dispatcher(new ScriptedStreamer(), a);
            d.GroupCancellation = group;
            d.ActivityUi = ui;

            d.Dispatch("{\"agents\":[{\"name\":\"w\",\"task\":\"t\"}]}");

            Assert.Equal(1, ui.Finishes);
            Assert.Equal(1, ui.Cancellations);
        }

        // ---- user-stop wrap-up directive ----

        [Fact]
        public void Dispatch_WhenGroupCancelled_AppendsWrapUpDirective()
        {
            Agent a = WriteAgent("w", "writes", "b");
            var group = new RequestCancellation();
            group.Cancel();   // simulate the user having clicked "Stop agents" (children bail immediately)
            AgentDispatcher d = Dispatcher(new ScriptedStreamer(), a);
            d.GroupCancellation = group;

            string result = d.Dispatch("{\"agents\":[{\"name\":\"w\",\"task\":\"t\"}]}");

            Assert.Contains("stopped the sub-agents", result);
            Assert.Contains("how they would like to proceed", result);
        }

        [Fact]
        public void Dispatch_NotCancelled_NoWrapUpDirective()
        {
            Agent a = WriteAgent("w", "writes", "b");
            ScriptedStreamer streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("all done"));
            AgentDispatcher d = Dispatcher(streamer, a);   // GroupCancellation null

            string result = d.Dispatch("{\"agents\":[{\"name\":\"w\",\"task\":\"t\"}]}");

            Assert.DoesNotContain("stopped the sub-agents", result);
        }
    }
}
