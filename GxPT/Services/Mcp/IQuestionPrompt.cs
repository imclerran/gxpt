using System.Collections.Generic;

namespace GxPT
{
    // The host's "ask the user a question" seam, called by AskUserTool on the tool-loop worker thread.
    // The real implementation (TranscriptQuestionPrompt) marshals to a docked QuestionPanel and blocks
    // the worker until the user submits; a test can supply a scripted stub. Kept UI-independent (no
    // WinForms types) so it links into the net48 test project, the same split as IToolApprovalPolicy.
    internal interface IQuestionPrompt
    {
        // Show the question and block until the user answers or dismisses it. Never returns null.
        QuestionAnswer Ask(QuestionRequest request);
    }

    // One model-supplied choice. Description is optional (shown under the label when present).
    internal sealed class QuestionOption
    {
        public readonly string Label;
        public readonly string Description;

        public QuestionOption(string label, string description)
        {
            Label = label;
            Description = description;
        }
    }

    // A question to put to the user. Options are already validated and capped (AskUserTool.MaxOptions)
    // by the time this reaches the prompt; the always-on free-text "Other" choice is added by the UI,
    // not carried here. MultiSelect chooses checkboxes (any number) over radio buttons (exactly one).
    internal sealed class QuestionRequest
    {
        public readonly string Question;
        public readonly IList<QuestionOption> Options;
        public readonly bool MultiSelect;

        // 1-based position of this question among the ask_user calls the model issued in the current
        // turn, and the total number of them. Total > 1 means the model asked several questions this
        // turn (multiple ask_user tool calls in one assistant message); the UI shows "Question X of Y"
        // so the back-to-back panels aren't a surprise. Defaults to 1 of 1 (a lone question).
        public readonly int Position;
        public readonly int Total;

        public QuestionRequest(string question, IList<QuestionOption> options, bool multiSelect)
            : this(question, options, multiSelect, 1, 1)
        {
        }

        public QuestionRequest(string question, IList<QuestionOption> options, bool multiSelect,
                               int position, int total)
        {
            Question = question;
            Options = options;
            MultiSelect = multiSelect;
            Position = position;
            Total = total;
        }
    }

    // The user's answer. Dismissed (closed without submitting, or the tab was recycled away) takes
    // precedence: AskUserTool maps it to a sentinel result so the model knows no choice was made.
    // Otherwise Selected holds the chosen option labels and CustomText holds the free-text "Other"
    // entry when the user filled it in (null/empty when they didn't). The UI guarantees an empty
    // custom entry is never returned as a selection, so CustomText is meaningful whenever non-empty.
    internal sealed class QuestionAnswer
    {
        public bool Dismissed;
        public IList<string> Selected;
        public string CustomText;

        public static QuestionAnswer DismissedAnswer()
        {
            return new QuestionAnswer { Dismissed = true, Selected = new List<string>() };
        }
    }
}
