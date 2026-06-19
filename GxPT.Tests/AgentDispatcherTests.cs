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
    }
}
