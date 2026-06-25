using System;

namespace GxPT
{
    // Shared formatting for tool activity shown in the transcript, so the live stream and the
    // reloaded view render identically. The qualified function name ("web__search") is displayed
    // as "web: search" — this also avoids the double-underscore being mangled by markdown emphasis.
    internal static class McpMarkers
    {
        public static string Display(string functionName)
        {
            return string.IsNullOrEmpty(functionName) ? string.Empty : functionName.Replace("__", ": ");
        }

        // A compact "using <tool>" marker, plain (not italic) for consistency with the collapsible
        // tool records. Display() turns the qualified name into "web: search" with no underscores.
        // ask_user is a host tool that interacts with the user rather than "using" a service, so its
        // in-progress placeholder reads "Asking a question" (replaced by the "Asked a question" record
        // once answered).
        public static string Call(string functionName)
        {
            if (functionName == AskUserTool.AskUserName) return "Asking a question";
            return "using " + Display(functionName);
        }

        // For files__edit / command__run, the generic "using" marker is replaced by a collapsible
        // record, anchored by this content-free sentinel (resolved to the record by the transcript).
        public static string EditDiff(string key)
        {
            return MarkdownParser.EditDiffSentinel(key);
        }

        // True when a tool result is the orchestrator's "denied by user" sentinel, so the transcript
        // renders the call as denied (live and on reload) rather than as an applied edit/diff. The
        // sentinel lives on McpChatOrchestrator (which the test project links; this file does not).
        public static bool IsDenied(string resultText)
        {
            return string.Equals(resultText, McpChatOrchestrator.DeniedResultText, StringComparison.Ordinal);
        }

        // Shown in place of the edit/diff record when a call was denied, so a denied edit reads as
        // "denied" instead of looking like it was applied.
        public static string Denied(string functionName)
        {
            return "denied: " + Display(functionName);
        }
    }

    // Adapts the orchestrator's IToolLoopUi to simple delegates so MainForm can render a tool-call
    // turn as a chrome-less "tool activity" message plus a separate answer bubble: model text ->
    // appendText, tool calls -> onToolCall/onToolResult, Complete/OnError finalize.
    //
    // Tool activity renders in two beats: OnToolCall shows an immediate "using <tool>" placeholder so
    // there's live feedback while the call runs (and the approval gate waits); OnToolResult replaces
    // that placeholder once the outcome is known, so the transcript reflects what actually happened
    // (applied record / denied / errored) rather than the unapproved request. Arguments are stashed at
    // call time since OnToolResult doesn't carry them.
    // (fn, argsJson, resultText, isError, callId) -> replace the placeholder with the outcome. A named
    // delegate, not Action<...>: .NET 3.5's Action only goes up to four type arguments.
    internal delegate void ToolResultCallback(string functionName, string argsJson, string resultText, bool isError, string callId);

    internal sealed class DelegateToolLoopUi : IToolLoopUi
    {
        private readonly Action<string> _appendText;
        private readonly Action<string> _onToolCall;        // (functionName) -> show placeholder
        private readonly ToolResultCallback _onToolResult;  // (fn, argsJson, resultText, isError, callId) -> replace
        private readonly Action _complete;
        private readonly Action<string> _error;

        private string _pendingArgs; // arguments of the in-flight call, awaiting its result

        public DelegateToolLoopUi(Action<string> appendText, Action<string> onToolCall,
                                  ToolResultCallback onToolResult, Action complete, Action<string> error)
        {
            _appendText = appendText;
            _onToolCall = onToolCall;
            _onToolResult = onToolResult;
            _complete = complete;
            _error = error;
        }

        public void AppendTextDelta(string text)
        {
            if (!string.IsNullOrEmpty(text) && _appendText != null) _appendText(text);
        }

        public void OnToolCall(string functionName, string argumentsJson, string callId)
        {
            // Stash args for the result; show the placeholder now. Calls are serial, so one slot fits.
            _pendingArgs = argumentsJson;
            if (_onToolCall != null) _onToolCall(functionName);
        }

        public void OnToolResult(string functionName, string resultText, bool isError, string callId)
        {
            if (_onToolResult != null) _onToolResult(functionName, _pendingArgs, resultText, isError, callId);
            _pendingArgs = null;
        }

        public void OnError(string message)
        {
            if (_error != null) _error(message);
        }

        public void Complete()
        {
            if (_complete != null) _complete();
        }
    }
}

