using System;
using System.Collections.Generic;
using System.Text;
using GxPT;
using Newtonsoft.Json.Linq;

namespace GxPT.Tests.Mcp
{
    // Builds ChatCompletionChunks / chunk sequences ("turns") for driving the orchestrator.
    internal static class Chunks
    {
        public static ChatCompletionChunk TextChunk(string content, string finish)
        {
            var c = new ChatCompletionChunk();
            c.choices = new List<ChatCompletionChunk.Choice>();
            var ch = new ChatCompletionChunk.Choice();
            ch.delta = new ChatCompletionChunk.Delta { content = content };
            ch.finish_reason = finish;
            c.choices.Add(ch);
            return c;
        }

        public static ChatCompletionChunk ToolChunk(int index, string id, string name, string args, string finish)
        {
            var c = new ChatCompletionChunk();
            c.choices = new List<ChatCompletionChunk.Choice>();
            var ch = new ChatCompletionChunk.Choice();
            ch.delta = new ChatCompletionChunk.Delta();
            ch.delta.tool_calls = new List<ToolCallDelta>();
            ch.delta.tool_calls.Add(new ToolCallDelta
            {
                index = index,
                id = id,
                type = id != null ? "function" : null,
                function = new FunctionDelta { name = name, arguments = args }
            });
            ch.finish_reason = finish;
            c.choices.Add(ch);
            return c;
        }

        public static ChatCompletionChunk[] Text(string content)
        {
            return new[] { TextChunk(content, "stop") };
        }

        public static ChatCompletionChunk[] OneToolCall(string id, string name, string args)
        {
            return new[] { ToolChunk(0, id, name, args, "tool_calls") };
        }
    }

    // An IChatStreamer that replays scripted chunk sequences, one per StreamChat call, and records
    // what it was asked to send. A Fallback supplies chunks once Turns is exhausted (e.g. an
    // always-tool-call script for the iteration-cap test); an optional error can be injected.
    internal sealed class ScriptedStreamer : IChatStreamer
    {
        public readonly List<ChatCompletionChunk[]> Turns = new List<ChatCompletionChunk[]>();
        public Func<int, ChatCompletionChunk[]> Fallback;
        public string ErrorMessage;   // when set, signal onError instead of streaming
        public int ErrorOnCall = -1;  // -1 = every call; otherwise only this call index
        // When set, reports usage via props.ResponseUsageCallback (mimicking the real client,
        // which reports off the response's final usage-bearing chunk). ServeAs is the provider
        // name; ServeCachedTokens / ServeCacheWriteTokens are the cache counters - both 0
        // simulates a response with no cache activity (non-caching endpoint, or an implicit
        // cacher that doesn't report writes); ServeCost is the billed credits (null = unreported).
        public string ServeAs;
        public int ServeCachedTokens;
        public int ServeCacheWriteTokens;
        public decimal? ServeCost;

        public int Calls;
        public Action<int> OnCall;   // invoked with the call index at the start of each StreamChat
        public readonly List<IList<ChatMessage>> SeenMessages = new List<IList<ChatMessage>>();
        public readonly List<IList<JObject>> SeenTools = new List<IList<JObject>>();
        public readonly List<ClientProperties> SeenProps = new List<ClientProperties>();
        public readonly List<string> SeenModels = new List<string>();

        public void StreamChat(string model, IList<ChatMessage> messages, IList<JObject> tools,
                               ClientProperties props, Action<ChatCompletionChunk> onChunk, Action<string> onError,
                               RequestCancellation cancel)
        {
            int idx = Calls++;
            SeenMessages.Add(messages);
            SeenTools.Add(tools);
            SeenProps.Add(props);
            SeenModels.Add(model);

            // Test hook fired with this call's index, e.g. to simulate the user pressing Stop
            // mid-turn (cancel.Cancel()) after a particular streamed response.
            if (OnCall != null) OnCall(idx);

            if (ServeAs != null && props != null && props.ResponseUsageCallback != null)
            {
                var usage = new ResponseUsage();
                usage.Provider = ServeAs;
                usage.CachedTokens = ServeCachedTokens;
                usage.CacheWriteTokens = ServeCacheWriteTokens;
                usage.Cost = ServeCost;
                props.ResponseUsageCallback(usage);
            }

            if (ErrorMessage != null && (ErrorOnCall < 0 || ErrorOnCall == idx))
            {
                if (onError != null) onError(ErrorMessage);
                return;
            }

            ChatCompletionChunk[] chunks = idx < Turns.Count
                ? Turns[idx]
                : (Fallback != null ? Fallback(idx) : null);
            if (chunks != null)
                foreach (var ch in chunks)
                    if (onChunk != null) onChunk(ch);
        }
    }

    internal sealed class RecordingUi : IToolLoopUi
    {
        public readonly StringBuilder Text = new StringBuilder();
        public readonly List<string> ToolCalls = new List<string>();
        public readonly List<string> ToolResults = new List<string>();
        public readonly List<bool> ToolErrors = new List<bool>();
        public string Error;
        public bool Completed;

        public void AppendTextDelta(string text) { Text.Append(text); }
        public void OnToolCall(string functionName, string argumentsJson, string callId) { ToolCalls.Add(functionName); }
        public void OnToolResult(string functionName, string resultText, bool isError, string callId)
        {
            ToolResults.Add(resultText);
            ToolErrors.Add(isError);
        }
        public void OnError(string message) { Error = message; }
        public void Complete() { Completed = true; }
    }
}
