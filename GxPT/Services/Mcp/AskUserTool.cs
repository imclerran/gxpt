using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace GxPT
{
    // The "ask the user" execution-surface meta-tool: ask_user. Host-synthesized and handled inside the
    // orchestrator without an MCP round-trip - the question analogue of reveal_tools / open_skill /
    // dispatch_agent. The model proposes a question and 2-4 options; the host shows a docked panel
    // (radio buttons for single-select, checkboxes for multi-select), always offers a free-text "Other"
    // choice, and feeds the user's answer back as the tool result so the model keeps reasoning with it
    // in context. The Q&A persists in the transcript (assistant tool_call + tool result) like any tool.
    //
    // Threading: Ask runs on the tool-loop worker thread and blocks (via IQuestionPrompt) until the user
    // submits - the turn pauses there, which is correct (the user is present), exactly as the approval
    // gate does.
    internal sealed class AskUserTool
    {
        public const string AskUserName = "ask_user";

        // The most model-supplied options the panel renders; a 5th free-text "Other" row is always added
        // by the UI on top of these. A model that sends more than this gets the first MaxOptions (a
        // defensive cap, like AgentDispatcher.MaxAgentsPerCall) rather than an overflowing panel.
        public const int MaxOptions = 4;

        // Tool-result content when the user closes the question without choosing (or the tab was recycled
        // away from this turn). A clear, model-readable sentinel so it stops and asks rather than guessing.
        internal const string DismissedResultText = "[User dismissed the question without answering.]";

        // Returned (as an isError result) when the model's arguments can't form a question - so it can
        // correct the call rather than the host silently doing nothing.
        internal const string InvalidArgsText =
            "[ask_user error: provide a non-empty \"question\" and at least one option in \"options\".]";

        private readonly IQuestionPrompt _prompt;

        public AskUserTool(IQuestionPrompt prompt)
        {
            _prompt = prompt;
        }

        public bool IsAskUser(string functionName)
        {
            return functionName == AskUserName;
        }

        // The OpenAI-style function definition: ask_user({ question, options:[{label,description?}],
        // multi_select? }). The description tells the model an "Other" free-text choice is always added
        // for it, so it shouldn't include one, and steers toward genuine either/or decisions.
        public JObject AskUserDef()
        {
            JObject labelP = new JObject(); labelP["type"] = "string";
            JObject descP = new JObject(); descP["type"] = "string";
            descP["description"] = "Optional short explanation of this option, shown under its label.";
            JObject optionProps = new JObject();
            optionProps["label"] = labelP;
            optionProps["description"] = descP;
            JObject optionSchema = new JObject();
            optionSchema["type"] = "object";
            optionSchema["properties"] = optionProps;
            optionSchema["required"] = new JArray("label");

            JObject optionsArr = new JObject();
            optionsArr["type"] = "array";
            optionsArr["items"] = optionSchema;
            optionsArr["description"] = "Two to four options to offer. Do not add an \"Other\" or "
                + "\"None\" option - a free-text \"Other\" choice is always added for the user.";

            JObject questionP = new JObject();
            questionP["type"] = "string";
            questionP["description"] = "The question to put to the user.";

            JObject multiP = new JObject();
            multiP["type"] = "boolean";
            multiP["description"] = "When true, the user may select more than one option; otherwise "
                + "they pick exactly one. Defaults to false.";

            JObject props = new JObject();
            props["question"] = questionP;
            props["options"] = optionsArr;
            props["multi_select"] = multiP;

            JObject schema = new JObject();
            schema["type"] = "object";
            schema["properties"] = props;
            schema["required"] = new JArray("question", "options");

            JObject fn = new JObject();
            fn["name"] = AskUserName;
            fn["description"] = "Ask the user a multiple-choice question and wait for their answer. Use "
                + "this when you genuinely need the user to decide between options you cannot resolve "
                + "yourself (e.g. which approach to take), not for information you can look up or infer. "
                + "Provide 2-4 concise options; the user can also type their own answer. Their selection "
                + "is returned to you. Prefer continuing on your own when the choice is clear.";
            fn["parameters"] = schema;

            JObject def = new JObject();
            def["type"] = "function";
            def["function"] = fn;
            return def;
        }

        // Parse the call, show the question (blocking), and format the user's answer as the tool result.
        // isError is set for malformed arguments so the model can correct the call. A dismissed prompt
        // is NOT an error (the user deliberately declined) - it returns the dismissed sentinel.
        public string Ask(string argumentsJson, out bool isError)
        {
            isError = false;

            string question;
            bool multiSelect;
            List<QuestionOption> options = ParseArgs(argumentsJson, out question, out multiSelect);
            if (string.IsNullOrEmpty(question) || options.Count == 0)
            {
                isError = true;
                return InvalidArgsText;
            }

            QuestionAnswer answer = _prompt != null
                ? _prompt.Ask(new QuestionRequest(question, options, multiSelect))
                : QuestionAnswer.DismissedAnswer();
            if (answer == null) answer = QuestionAnswer.DismissedAnswer();

            return FormatAnswer(answer, multiSelect);
        }

        // Extracts question / options (capped to MaxOptions, blank labels dropped) / multi_select.
        // Tolerant: anything missing or mistyped simply yields no question/options, which Ask reports
        // as invalid arguments.
        private static List<QuestionOption> ParseArgs(string argumentsJson, out string question,
                                                      out bool multiSelect)
        {
            question = null;
            multiSelect = false;
            List<QuestionOption> options = new List<QuestionOption>();
            if (string.IsNullOrEmpty(argumentsJson)) return options;

            try
            {
                JObject o = JObject.Parse(argumentsJson);

                JToken q = o["question"];
                if (q != null && q.Type == JTokenType.String) question = ((string)q).Trim();

                JToken ms = o["multi_select"];
                if (ms != null && ms.Type == JTokenType.Boolean) multiSelect = (bool)ms;

                JToken opts = o["options"];
                if (opts != null && opts.Type == JTokenType.Array)
                {
                    foreach (JToken item in (JArray)opts)
                    {
                        if (options.Count >= MaxOptions) break; // defensive cap; UI also adds "Other"
                        string label = null, desc = null;
                        if (item != null && item.Type == JTokenType.Object)
                        {
                            JToken l = item["label"];
                            if (l != null && l.Type == JTokenType.String) label = ((string)l).Trim();
                            JToken d = item["description"];
                            if (d != null && d.Type == JTokenType.String) desc = ((string)d).Trim();
                        }
                        else if (item != null && item.Type == JTokenType.String)
                        {
                            // Tolerate a bare string option ("A") as shorthand for { label: "A" }.
                            label = ((string)item).Trim();
                        }
                        if (!string.IsNullOrEmpty(label))
                            options.Add(new QuestionOption(label, string.IsNullOrEmpty(desc) ? null : desc));
                    }
                }
            }
            catch
            {
                // Malformed JSON -> empty/absent question+options; Ask reports invalid arguments.
            }
            return options;
        }

        // The chosen label(s) as the tool message content. Custom ("Other") text is included and marked
        // so the model can tell it apart from a preset option. A dismissed or empty answer returns the
        // dismissed sentinel rather than an empty string the model would have to guess at.
        internal static string FormatAnswer(QuestionAnswer answer, bool multiSelect)
        {
            if (answer == null || answer.Dismissed) return DismissedResultText;

            List<string> picks = new List<string>();
            if (answer.Selected != null)
            {
                foreach (string s in answer.Selected)
                    if (!string.IsNullOrEmpty(s)) picks.Add(s);
            }
            bool hasCustom = !string.IsNullOrEmpty(answer.CustomText);

            if (picks.Count == 0 && !hasCustom) return DismissedResultText;

            if (!multiSelect)
            {
                // Single-select: exactly one of a preset pick or custom text.
                if (hasCustom && picks.Count == 0)
                    return "Selected (custom): " + answer.CustomText.Trim();
                return "Selected: " + picks[0];
            }

            // Multi-select: a labeled list, custom text as its own marked line.
            StringBuilder sb = new StringBuilder();
            sb.Append("Selected:");
            for (int i = 0; i < picks.Count; i++)
                sb.Append("\n- ").Append(picks[i]);
            if (hasCustom)
                sb.Append("\n- (custom): ").Append(answer.CustomText.Trim());
            return sb.ToString();
        }
    }
}
