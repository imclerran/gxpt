using System.Collections.Generic;
using GxPT;
using Xunit;

namespace GxPT.Tests.Mcp
{
    // The user-stop wrap-up: when a dispatch_agent fan-out was cancelled by the user (its GroupCancellation
    // is tripped), the orchestrator forces the next model call to tool_choice "none" so the model produces
    // a text summary + "how should I proceed?" rather than charging into more tool calls.
    public sealed class OrchestratorAgentStopTests
    {
        [Fact]
        public void UserStoppedDispatch_ForcesTextOnlyNextCall()
        {
            // Parent stream: iteration 0 calls dispatch_agent; iteration 1 is the (forced) text wrap-up.
            ScriptedStreamer parent = new ScriptedStreamer();
            parent.Turns.Add(Chunks.OneToolCall("call_1", "dispatch_agent",
                "{\"agents\":[{\"name\":\"x\",\"task\":\"t\"}]}"));
            parent.Turns.Add(Chunks.Text("Here is a summary so far; how would you like to proceed?"));

            // A dispatcher whose group is already cancelled: the child bails immediately, the dispatch
            // returns partial + the stop directive, and the orchestrator should force the wrap-up.
            RequestCancellation group = new RequestCancellation();
            group.Cancel();
            Agent agent = new Agent("x", "x", "d", null, AgentMaxTier.ReadOnly, null, AgentEffort.Unset, 0,
                "nonexistent.md", AgentSource.Bundled);
            AgentDispatcher dispatcher = new AgentDispatcher(new List<Agent> { agent }, new ScriptedStreamer(),
                null, null, "m", null, null, delegate(string n) { return ToolTier.ReadOnly; }, 25, 60000);
            dispatcher.GroupCancellation = group;

            McpChatOrchestrator orch = new McpChatOrchestrator(parent, null, null, "m", null);
            orch.AgentDispatcher = dispatcher;

            List<ChatMessage> history = new List<ChatMessage>();
            history.Add(new ChatMessage("user", "go"));
            orch.RunTurn(history, new RecordingUi());

            Assert.True(parent.Calls >= 2);
            Assert.Null(parent.SeenProps[0].ToolChoice);            // the dispatch call: normal auto
            Assert.Equal("none", parent.SeenProps[1].ToolChoice);   // the wrap-up call: forced text-only

            string last = null;
            for (int i = history.Count - 1; i >= 0; i--)
                if (history[i].Role == "assistant" && !string.IsNullOrEmpty(history[i].Content))
                { last = history[i].Content; break; }
            Assert.Contains("how would you like to proceed", last);
        }
    }
}
