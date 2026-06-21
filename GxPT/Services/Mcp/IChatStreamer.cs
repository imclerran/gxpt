using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxPT
{
    // The chat-streaming seam the orchestrator drives. OpenRouterClient implements this (builds the
    // request body — including the tools array, assistant tool_calls, and tool-role messages — and
    // runs the curl/SSE loop). Abstracting it lets the orchestrator be unit-tested with a scripted
    // stub stream, with no network and no JSON wire format.
    internal interface IChatStreamer
    {
        // cancel (may be null) lets the host kill the in-flight request: the implementation registers
        // its curl process on the handle so a Stop click drops the connection mid-stream.
        void StreamChat(string model, IList<ChatMessage> messages, IList<JObject> tools,
                        ClientProperties props,
                        Action<ChatCompletionChunk> onChunk, Action<string> onError,
                        RequestCancellation cancel);
    }

    // The orchestrator's view of the transcript UI. Phase 4 ships a minimal representation: text
    // deltas stream in, and each tool call/result gets a one-line marker. Rich collapsible rendering
    // is a separate transcript-UI item.
    internal interface IToolLoopUi
    {
        void AppendTextDelta(string text);
        // callId is the model-assigned tool-call id (may be null for some providers); the host uses it as
        // the stable key for a tool record so the live and reloaded views share one identity.
        void OnToolCall(string functionName, string argumentsJson, string callId);
        void OnToolResult(string functionName, string resultText, bool isError, string callId);
        void OnError(string message);
        void Complete();
    }
}
