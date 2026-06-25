using System.Collections.Generic;
using GxPT;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxPT.Tests.Mcp
{
    public class AskUserToolTests
    {
        // A scripted IQuestionPrompt: records the request it was shown and returns a canned answer.
        private sealed class StubPrompt : IQuestionPrompt
        {
            public QuestionRequest LastRequest;
            public readonly QuestionAnswer Next;
            public StubPrompt(QuestionAnswer next) { Next = next; }
            public QuestionAnswer Ask(QuestionRequest request) { LastRequest = request; return Next; }
        }

        private sealed class DenyAllPolicy : IToolApprovalPolicy
        {
            public ApprovalDecision Check(string functionName, JObject args) { return ApprovalDecision.Deny; }
        }

        private static QuestionAnswer Pick(params string[] labels)
        {
            return new QuestionAnswer { Selected = new List<string>(labels) };
        }

        private static string TwoOptionArgs()
        {
            return "{\"question\":\"Pick one\",\"options\":[{\"label\":\"A\"},{\"label\":\"B\"}]}";
        }

        // ---- definition ----

        [Fact]
        public void Def_has_name_and_required_fields()
        {
            var tool = new AskUserTool(new StubPrompt(Pick("A")));
            JObject def = tool.AskUserDef();

            Assert.Equal("function", (string)def["type"]);
            JObject fn = (JObject)def["function"];
            Assert.Equal("ask_user", (string)fn["name"]);

            JObject schema = (JObject)fn["parameters"];
            var required = new List<string>();
            foreach (JToken t in (JArray)schema["required"]) required.Add((string)t);
            Assert.Contains("question", required);
            Assert.Contains("options", required);
            Assert.Equal("boolean", (string)schema["properties"]["multi_select"]["type"]);
            Assert.Equal("array", (string)schema["properties"]["options"]["type"]);
        }

        // ---- argument parsing / formatting ----

        [Fact]
        public void Single_select_preset_formats_as_selected_label()
        {
            var stub = new StubPrompt(Pick("B"));
            var tool = new AskUserTool(stub);
            bool isError;
            string result = tool.Ask(TwoOptionArgs(), out isError);

            Assert.False(isError);
            Assert.Equal("Selected: B", result);
            Assert.False(stub.LastRequest.MultiSelect);
            Assert.Equal(2, stub.LastRequest.Options.Count);
        }

        [Fact]
        public void Single_select_custom_text_is_marked()
        {
            var stub = new StubPrompt(new QuestionAnswer { Selected = new List<string>(), CustomText = "my own answer" });
            var tool = new AskUserTool(stub);
            bool isError;
            string result = tool.Ask(TwoOptionArgs(), out isError);

            Assert.False(isError);
            Assert.Equal("Selected (custom): my own answer", result);
        }

        [Fact]
        public void Multi_select_lists_picks_and_custom_line()
        {
            var ans = new QuestionAnswer { Selected = new List<string> { "A", "B" }, CustomText = "C" };
            var stub = new StubPrompt(ans);
            var tool = new AskUserTool(stub);
            bool isError;
            string result = tool.Ask(
                "{\"question\":\"Pick any\",\"multi_select\":true,\"options\":[{\"label\":\"A\"},{\"label\":\"B\"}]}",
                out isError);

            Assert.False(isError);
            Assert.True(stub.LastRequest.MultiSelect);
            Assert.Equal("Selected:\n- A\n- B\n- (custom): C", result);
        }

        [Fact]
        public void Options_are_capped_to_max()
        {
            var stub = new StubPrompt(Pick("o1"));
            var tool = new AskUserTool(stub);
            bool isError;
            tool.Ask(
                "{\"question\":\"Q\",\"options\":[{\"label\":\"o1\"},{\"label\":\"o2\"},{\"label\":\"o3\"},"
                + "{\"label\":\"o4\"},{\"label\":\"o5\"},{\"label\":\"o6\"}]}",
                out isError);

            Assert.Equal(AskUserTool.MaxOptions, stub.LastRequest.Options.Count);
            Assert.Equal("o4", stub.LastRequest.Options[3].Label);
        }

        [Fact]
        public void Bare_string_options_and_descriptions_are_parsed()
        {
            var stub = new StubPrompt(Pick("plain"));
            var tool = new AskUserTool(stub);
            bool isError;
            tool.Ask(
                "{\"question\":\"Q\",\"options\":[\"plain\",{\"label\":\"rich\",\"description\":\"why\"}]}",
                out isError);

            Assert.Equal(2, stub.LastRequest.Options.Count);
            Assert.Equal("plain", stub.LastRequest.Options[0].Label);
            Assert.Null(stub.LastRequest.Options[0].Description);
            Assert.Equal("rich", stub.LastRequest.Options[1].Label);
            Assert.Equal("why", stub.LastRequest.Options[1].Description);
        }

        [Fact]
        public void Dismissed_returns_sentinel_not_error()
        {
            var stub = new StubPrompt(QuestionAnswer.DismissedAnswer());
            var tool = new AskUserTool(stub);
            bool isError;
            string result = tool.Ask(TwoOptionArgs(), out isError);

            Assert.False(isError);
            Assert.Equal(AskUserTool.DismissedResultText, result);
        }

        [Theory]
        [InlineData("{\"options\":[{\"label\":\"A\"}]}")]            // no question
        [InlineData("{\"question\":\"Q\"}")]                          // no options
        [InlineData("{\"question\":\"Q\",\"options\":[]}")]          // empty options
        [InlineData("not json at all")]                               // malformed
        public void Invalid_arguments_set_error(string args)
        {
            // The prompt should never even be shown for invalid arguments.
            var stub = new StubPrompt(Pick("A"));
            var tool = new AskUserTool(stub);
            bool isError;
            string result = tool.Ask(args, out isError);

            Assert.True(isError);
            Assert.Equal(AskUserTool.InvalidArgsText, result);
            Assert.Null(stub.LastRequest);
        }

        // ---- FormatAnswer guards (the panel enforces these in the UI; verify the logic directly) ----

        [Fact]
        public void Empty_custom_with_no_picks_is_dismissed()
        {
            var a = new QuestionAnswer { Selected = new List<string>(), CustomText = "" };
            Assert.Equal(AskUserTool.DismissedResultText, AskUserTool.FormatAnswer(a, false));
            Assert.Equal(AskUserTool.DismissedResultText, AskUserTool.FormatAnswer(a, true));
        }

        // ---- orchestrator integration ----

        [Fact]
        public void Orchestrator_dispatches_ask_user_locally_and_feeds_back_answer()
        {
            // A deny-all approval policy proves ask_user bypasses the approval gate (the user IS the
            // one acting) and an empty registry proves it needs no MCP round-trip.
            var reg = new McpToolRegistry(null);
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "ask_user", TwoOptionArgs()));
            streamer.Turns.Add(Chunks.Text("Going with A."));

            var orch = new McpChatOrchestrator(streamer, reg, new DenyAllPolicy(), "test-model", null);
            orch.AskUser = new AskUserTool(new StubPrompt(Pick("A")));

            var history = new List<ChatMessage>();
            var ui = new RecordingUi();
            orch.RunTurn(history, "decide for me", ui);

            Assert.True(ui.Completed);
            Assert.Equal(new[] { "ask_user" }, ui.ToolCalls.ToArray());
            Assert.Equal("Selected: A", ui.ToolResults[0]);   // not the deny sentinel
            Assert.False(ui.ToolErrors[0]);
            Assert.Equal("Going with A.", ui.Text.ToString());

            // The answer is recorded as a tool-role message so the model reasons with it / it replays.
            ChatMessage toolMsg = history.Find(delegate(ChatMessage m) { return m.Role == "tool"; });
            Assert.NotNull(toolMsg);
            Assert.Equal("Selected: A", toolMsg.Content);

            // ask_user was offered in the tools array on the first request.
            bool offered = false;
            foreach (JObject def in streamer.SeenTools[0])
                if ((string)def["function"]["name"] == "ask_user") offered = true;
            Assert.True(offered);
        }
    }
}
