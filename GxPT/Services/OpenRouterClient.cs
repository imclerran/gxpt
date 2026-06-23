using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxPT
{
    internal sealed class OpenRouterClient : IChatStreamer
    {
        private readonly string _apiKey;
        private readonly string _curlPath;

        public OpenRouterClient(string apiKey, string curlPath)
        {
            _apiKey = apiKey ?? string.Empty;
            _curlPath = curlPath;
        }

        public bool IsConfigured
        {
            get { return !string.IsNullOrEmpty(_apiKey) && File.Exists(_curlPath); }
        }

        // Build the request body (Newtonsoft, D16). Emits the messages (with assistant tool_calls and
        // tool-role tool_call_id where present), the optional tools array, and provider options.
        // The emoji-suppression system message is always prepended.
        internal static string BuildRequestBody(string model, IList<ChatMessage> messages, IList<JObject> tools, ClientProperties props)
        {
            var payload = new Dictionary<string, object>();
            payload["model"] = string.IsNullOrEmpty(model) ? "openai/gpt-4o" : model;
            bool streamFlag = (props != null && props.Stream.HasValue) ? props.Stream.Value : false;
            payload["stream"] = streamFlag;

            // Ask OpenRouter to return token-usage accounting (including prompt-cache read counts) -
            // on streaming requests it arrives on the final SSE chunk. This is how cache hits are
            // verified: see the "Usage" log line in StreamRawChunks.
            var usageOpt = new Dictionary<string, object>();
            usageOpt["include"] = true;
            payload["usage"] = usageOpt;

            var msgs = new List<object>();
            // Prepend a system instruction to avoid emoji in all responses.
            var sys = new Dictionary<string, object>();
            sys["role"] = "system";
            sys["content"] = "Do not use emojis or emoticons in any response. Use plain text only.";
            msgs.Add(sys);

            if (messages != null)
            {
                foreach (var m in messages)
                {
                    var mm = new Dictionary<string, object>();
                    mm["role"] = m.Role;

                    if (m.Role == "tool")
                    {
                        // Tool result, keyed back to the assistant's call id.
                        mm["content"] = ContentValue(m);
                        mm["tool_call_id"] = m.ToolCallId;
                    }
                    else if (m.ToolCalls != null && m.ToolCalls.Count > 0)
                    {
                        // Assistant turn requesting tool calls (content may be null alongside calls).
                        mm["content"] = string.IsNullOrEmpty(m.Content) ? null : (object)m.Content;
                        var calls = new List<object>();
                        foreach (var c in m.ToolCalls)
                        {
                            var fn = new Dictionary<string, object>();
                            fn["name"] = c.Name;
                            fn["arguments"] = string.IsNullOrEmpty(c.ArgumentsJson) ? "{}" : c.ArgumentsJson;
                            var call = new Dictionary<string, object>();
                            call["id"] = c.Id;
                            call["type"] = "function";
                            call["function"] = fn;
                            calls.Add(call);
                        }
                        mm["tool_calls"] = calls;
                    }
                    else
                    {
                        mm["content"] = ContentValue(m);
                    }
                    msgs.Add(mm);
                }
            }
            payload["messages"] = msgs;

            // Expose tools (tool_choice defaults to the provider's "auto"; the orchestrator's cap
            // wrap-up sets "none" so the prompt prefix - which starts with the tools array - stays
            // byte-identical to the loop's requests and re-reads the turn's prompt cache).
            if (tools != null && tools.Count > 0)
            {
                var toolList = new List<object>();
                foreach (var t in tools) toolList.Add(t);
                payload["tools"] = toolList;
                if (props != null && !string.IsNullOrEmpty(props.ToolChoice))
                    payload["tool_choice"] = props.ToolChoice;
            }

            // Optionally add provider object if any of the provider properties are set.
            try
            {
                if (props != null)
                {
                    var provider = new Dictionary<string, object>();

                    // data_collection: include if set (true => allow, false => deny)
                    if (props.ProviderDataCollectionAllowed.HasValue)
                        provider["data_collection"] = props.ProviderDataCollectionAllowed.Value ? "allow" : "deny";

                    // zdr (zero data retention): OpenRouter treats the per-request flag as an OR with
                    // account/guardrail settings — it can only ensure ZDR for this request, never
                    // disable it. So we emit "zdr": true only when on, and omit it otherwise.
                    if (props.Zdr.HasValue && props.Zdr.Value)
                        provider["zdr"] = true;

                    // only: include non-empty list
                    if (props.ProviderOnly != null && props.ProviderOnly.Count > 0)
                    {
                        var onlyList = new List<string>();
                        foreach (var s in props.ProviderOnly)
                            if (!string.IsNullOrEmpty(s)) onlyList.Add(s);
                        if (onlyList.Count > 0) provider["only"] = onlyList;
                    }

                    // order: sticky cache-routing preference (see ClientProperties.ProviderOrder).
                    if (props.ProviderOrder != null && props.ProviderOrder.Count > 0)
                    {
                        var orderList = new List<string>();
                        foreach (var s in props.ProviderOrder)
                            if (!string.IsNullOrEmpty(s)) orderList.Add(s);
                        if (orderList.Count > 0) provider["order"] = orderList;
                    }

                    // max_price: include any provided fields
                    var maxPrice = new Dictionary<string, object>();
                    if (props.ProviderMaxPricePrompt.HasValue)
                        maxPrice["prompt"] = props.ProviderMaxPricePrompt.Value;
                    if (props.ProviderMaxPriceCompletion.HasValue)
                        maxPrice["completion"] = props.ProviderMaxPriceCompletion.Value;
                    if (maxPrice.Count > 0) provider["max_price"] = maxPrice;

                    if (provider.Count > 0) payload["provider"] = provider;
                }
            }
            catch { }

            return JsonConvert.SerializeObject(payload);
        }

        // A message's content value. Three cases:
        //
        // 1. No binary attachments, no cache flag → plain string (unchanged path).
        // 2. No binary attachments, cache flag set → one-part text array with cache_control.
        // 3. Binary attachments present → multi-part array: text part (if non-empty) + one
        //    image_url part per image attachment; cache_control on the last part when flagged.
        //
        // The array form is the OpenAI-compatible shape OpenRouter requires for cache_control.
        // Emitted for EVERY model: providers without explicit caching ignore the annotation.
        // Empty text parts are omitted — Anthropic rejects them in content arrays.
        private static object ContentValue(ChatMessage m)
        {
            string text = m.Content != null ? m.Content : string.Empty;

            // Collect image attachments that have been flagged for native carriage by the transform.
            List<AttachedFile> images = null;
            if (m.Attachments != null)
            {
                for (int i = 0; i < m.Attachments.Count; i++)
                {
                    var af = m.Attachments[i];
                    if (af == null) continue;
                    if (af.EffectiveKind == AttachmentKind.Image && !string.IsNullOrEmpty(af.Data))
                    {
                        if (images == null) images = new List<AttachedFile>();
                        images.Add(af);
                    }
                }
            }

            bool hasImages = images != null && images.Count > 0;

            // Case 1: plain string — no images, no cache flag, or empty text with no images.
            if (!hasImages && (!m.CacheControl || text.Length == 0)) return text;

            // Build a content-parts list.
            var parts = new List<object>();

            // Text part (omitted when empty — Anthropic rejects empty text parts in arrays).
            if (text.Length > 0)
            {
                var textPart = new Dictionary<string, object>();
                textPart["type"] = "text";
                textPart["text"] = text;
                parts.Add(textPart);
            }

            // Image parts.
            if (hasImages)
            {
                for (int i = 0; i < images.Count; i++)
                {
                    var af = images[i];
                    var imgPart = new Dictionary<string, object>();
                    imgPart["type"] = "image_url";
                    var imgUrl = new Dictionary<string, object>();
                    imgUrl["url"] = "data:" + (af.MediaType ?? "image/png") + ";base64," + af.Data;
                    imgPart["image_url"] = imgUrl;
                    parts.Add(imgPart);
                }
            }

            // No parts built (empty text, no images) — return plain empty string.
            if (parts.Count == 0) return text;

            // Attach cache_control to the last part so the heavy image sits inside the cached
            // prefix. Respects the 4-breakpoint cap enforced upstream by ApplyCacheBreakpoints.
            if (m.CacheControl)
            {
                var lastPart = parts[parts.Count - 1] as Dictionary<string, object>;
                if (lastPart != null)
                {
                    var cc = new Dictionary<string, object>();
                    cc["type"] = "ephemeral";
                    lastPart["cache_control"] = cc;
                }
            }

            return parts;
        }

        // Providers whose prompt caching (explicit cache_control OR implicit/automatic prefix
        // caching) makes a byte-stable prompt prefix valuable. Gates reveal-set eviction in the
        // orchestrator: on these providers an evicted tool def changes the tools array (position 0
        // of the prompt) and re-bills the conversation's whole transcript at full price, which costs
        // far more than the stale def it saves - so the revealed set stays append-only. Providers
        // not listed here get no caching benefit from a stable prefix, so trimming stale defs is a
        // pure token saving there.
        internal static bool ModelSupportsPromptCaching(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            string m = StripModelAliasPrefix(model);
            return m.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase)  // explicit (cache_control)
                || m.StartsWith("google/", StringComparison.OrdinalIgnoreCase)    // implicit + cache_control
                || m.StartsWith("openai/", StringComparison.OrdinalIgnoreCase)    // automatic (>=1024 tokens)
                || m.StartsWith("deepseek/", StringComparison.OrdinalIgnoreCase)  // automatic
                || m.StartsWith("x-ai/", StringComparison.OrdinalIgnoreCase)      // automatic (Grok)
                || m.StartsWith("qwen/", StringComparison.OrdinalIgnoreCase)      // automatic (context cache)
                || m.StartsWith("minimax/", StringComparison.OrdinalIgnoreCase)   // automatic
                || m.StartsWith("moonshotai/", StringComparison.OrdinalIgnoreCase); // automatic (Kimi)
        }

        // "~"-prefixed model aliases (e.g. "~anthropic/claude-sonnet-latest") resolve to the same
        // vendor as their bare form; strip the marker before prefix-matching.
        private static string StripModelAliasPrefix(string model)
        {
            return model.StartsWith("~", StringComparison.Ordinal) ? model.Substring(1) : model;
        }

        public string CreateCompletion(string model, IList<ChatMessage> messages)
        {
            // Backward-compatible API: defaults to non-stream
            return CreateCompletion(model, messages, null);
        }

        // New overload accepting ClientProperties
        public string CreateCompletion(string model, IList<ChatMessage> messages, ClientProperties props)
        {
            // Ensure stream flag defaults to false for non-stream API
            if (props == null) props = new ClientProperties();
            if (!props.Stream.HasValue) props.Stream = false;
            string body = BuildRequestBody(model, messages, null, props);
            List<string> tempFiles;
            string args = BuildCurlArgs(body, out tempFiles);

            var psi = new ProcessStartInfo
            {
                FileName = _curlPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var p = Process.Start(psi))
            {
                // Explicitly decode stdout/stderr as UTF-8 to avoid mojibake on non-UTF8 system locales
                var utf8 = new UTF8Encoding(false, false);
                string output;
                string err;
                using (var outReader = new StreamReader(p.StandardOutput.BaseStream, utf8, false))
                {
                    output = outReader.ReadToEnd();
                }
                using (var errReader = new StreamReader(p.StandardError.BaseStream, utf8, false))
                {
                    err = errReader.ReadToEnd();
                }
                p.WaitForExit();
                int exitCode = -1;
                try { exitCode = p.ExitCode; }
                catch { }
                // Clean up temp files (request body and auth config)
                CleanupTempFiles(tempFiles);

                // Log a response summary so non-stream calls (e.g. conversation naming) leave a
                // diagnostic trail. The streaming path logs "curl exit=...", but this path was silent
                // after the request, making a failed/empty completion invisible in the log.
                try { Logger.Log("HTTP", "completion exit=" + exitCode + " outLen=" + (output != null ? output.Length : 0) + (string.IsNullOrEmpty(err) ? string.Empty : " stderrLen=" + err.Length)); }
                catch { }

                // If curl signaled an HTTP error but kept the body (with --fail-with-body), log the body and any extracted message for debugging
                try
                {
                    if (!string.IsNullOrEmpty(err) && output.Length > 0)
                    {
                        Logger.Log("HTTP", "curl error body: " + output);
                        try
                        {
                            var msg2 = ExtractErrorMessage(SafeParse(output));
                            if (!string.IsNullOrEmpty(msg2))
                                Logger.Log("HTTP", "error.message: " + msg2);
                        }
                        catch { }
                    }
                }
                catch { }

                if (!string.IsNullOrEmpty(err) && output.Length == 0)
                    throw new Exception("curl error: " + err);
                return output;
            }
        }

        // Fetches the authoritative accounting for a completed generation (GET /api/v1/generation).
        // The streamed usage.cost has been observed billing-accurate (cache pricing included) on
        // some providers, but cache_discount never rides the stream - this record is the Saved
        // figure's only data source, and the billing correction for any provider whose stream
        // estimate differs (the delta-based reconcile is a no-op when they already match). The
        // record can take several seconds to become queryable after the stream ends, so the retry
        // loop is patient; it also keeps polling briefly when total_cost materializes before
        // cache_discount. Failures are logged - a silent failure here looks like "Saved frozen at
        // $0.00" in the UI and is otherwise undiagnosable. Runs blocking curl GETs; call from a
        // background thread. Null when nothing could be fetched.
        public GenerationStats FetchGenerationStats(string generationId)
        {
            if (string.IsNullOrEmpty(generationId) || !IsConfigured) return null;
            string url = "https://openrouter.ai/api/v1/generation?id=" + Uri.EscapeDataString(generationId);
            // Field-measured: records materialize with variable latency clustered around 10-15s
            // after completion, with stragglers beyond - a short schedule permanently loses the
            // stragglers' discounts (there is no later retry). Front-loaded for the fast cases,
            // patient tail for the slow ones (~2 minutes total).
            int[] delaysMs = new int[] { 0, 1000, 2000, 3000, 4000, 10000, 15000, 30000, 60000 };
            GenerationStats best = null;
            HttpGetResult last = null;
            for (int attempt = 0; attempt < delaysMs.Length; attempt++)
            {
                if (delaysMs[attempt] > 0) System.Threading.Thread.Sleep(delaysMs[attempt]);
                last = HttpGetEx(url);
                if (last == null || last.ExitCode != 0 || string.IsNullOrEmpty(last.Body)) continue;
                GenerationStats s = ExtractGenerationStats(SafeParse(last.Body));
                if (s == null) continue;
                best = s;
                if (s.CacheDiscount.HasValue) break;
            }
            try
            {
                if (best != null)
                {
                    Logger.Log("Usage", "generation " + generationId + ": total_cost="
                        + (best.TotalCost.HasValue ? best.TotalCost.Value.ToString() : "?")
                        + " cache_discount=" + (best.CacheDiscount.HasValue ? best.CacheDiscount.Value.ToString() : "?")
                        + (string.IsNullOrEmpty(best.ProviderName) ? string.Empty : " provider=" + best.ProviderName));
                }
                else
                {
                    string detail = last == null
                        ? "no response"
                        : "exit=" + last.ExitCode + " body=" + Snippet(last.Body, 200);
                    Logger.Log("Usage", "generation " + generationId + ": fetch FAILED after "
                        + delaysMs.Length + " attempts (" + detail + ")");
                }
            }
            catch { }
            return best;
        }

        private static string Snippet(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "(empty)";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > max ? s.Substring(0, max) + "..." : s;
        }

        // { "data": { "total_cost": 0.0585, "cache_discount": 0.0559, "provider_name": "Amazon
        // Bedrock", ... } } -> billed cost, net cache savings (negative = write premium), and the
        // serving provider. Null when the payload carries none of them.
        internal static GenerationStats ExtractGenerationStats(JObject root)
        {
            if (root == null) return null;
            JToken data = root["data"];
            if (data == null || data.Type != JTokenType.Object) return null;
            GenerationStats s = new GenerationStats();
            s.TotalCost = AsDecimal(data["total_cost"]);
            s.CacheDiscount = AsDecimal(data["cache_discount"]);
            JToken pn = data["provider_name"];
            if (pn != null && pn.Type == JTokenType.String) s.ProviderName = (string)pn;
            return (s.TotalCost.HasValue || s.CacheDiscount.HasValue || s.ProviderName != null) ? s : null;
        }

        private static decimal? AsDecimal(JToken t)
        {
            if (t == null) return null;
            if (t.Type != JTokenType.Float && t.Type != JTokenType.Integer) return null;
            try { return (decimal)t; }
            catch { return null; }
        }

        // Authenticated GET via curl (the API key rides the temp config file, off the command
        // line, mirroring the POST paths). Returns the exit code and body so callers can log
        // failures usefully (a 404 here usually means the generation record isn't queryable yet).
        private sealed class HttpGetResult
        {
            public int ExitCode = -1;
            public string Body;
        }

        private HttpGetResult HttpGetEx(string url)
        {
            var result = new HttpGetResult();
            var tempFiles = new List<string>();
            try
            {
                var utf8NoBom = new UTF8Encoding(false);
                string configPath = Path.Combine(Path.GetTempPath(), "gxpt_cfg_" + Guid.NewGuid().ToString("N") + ".txt");
                string args;
                try
                {
                    File.WriteAllText(configPath,
                        "header = \"Authorization: Bearer " + EscapeForCurlConfig(_apiKey) + "\"\n", utf8NoBom);
                    tempFiles.Add(configPath);
                    args = "-sS --fail-with-body \"" + url + "\" -K \"" + configPath + "\"";
                }
                catch
                {
                    args = "-sS --fail-with-body \"" + url + "\" -H \"Authorization: Bearer " + _apiKey + "\"";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = _curlPath,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    var utf8 = new UTF8Encoding(false, false);
                    using (var outReader = new StreamReader(p.StandardOutput.BaseStream, utf8, false))
                        result.Body = outReader.ReadToEnd();
                    try { p.StandardError.ReadToEnd(); }
                    catch { }
                    p.WaitForExit();
                    try { result.ExitCode = p.ExitCode; }
                    catch { }
                }
            }
            catch { }
            finally { CleanupTempFiles(tempFiles); }
            return result;
        }

        // IChatStreamer: build the body (with tools) then stream parsed chunks. Used by the
        // McpChatOrchestrator; the assembler reassembles tool_calls and forwards text deltas.
        public void StreamChat(string model, IList<ChatMessage> messages, IList<JObject> tools,
                               ClientProperties props, Action<ChatCompletionChunk> onChunk, Action<string> onError,
                               RequestCancellation cancel)
        {
            if (props == null) props = new ClientProperties();
            if (!props.Stream.HasValue) props.Stream = true;
            string body = BuildRequestBody(model, messages, tools, props);
            StreamRawChunks(body, WrapWithProviderReport(props, onChunk), onError, cancel);
        }

        // Decorates a chunk sink to report (once, on the final usage-bearing chunk) the response's
        // usage accounting via props.ResponseUsageCallback. The provider name arrives on the first
        // chunk but the usage counters only on the last, so the report waits for usage. Identity
        // when no callback is registered.
        private static Action<ChatCompletionChunk> WrapWithProviderReport(
            ClientProperties props, Action<ChatCompletionChunk> onChunk)
        {
            if (props == null || props.ResponseUsageCallback == null) return onChunk;
            Action<ResponseUsage> report = props.ResponseUsageCallback;
            bool[] reported = new bool[1];      // closure-mutable state (C# 3.5: no local functions)
            string[] provider = new string[1];
            string[] genId = new string[1];
            // cache_discount may ride a different chunk than usage; remember the latest seen.
            decimal?[] discount = new decimal?[1];
            return delegate(ChatCompletionChunk chunk)
            {
                if (chunk != null)
                {
                    if (!string.IsNullOrEmpty(chunk.provider)) provider[0] = chunk.provider;
                    if (!string.IsNullOrEmpty(chunk.id)) genId[0] = chunk.id;
                    if (chunk.cache_discount.HasValue) discount[0] = chunk.cache_discount;
                    if (!reported[0] && chunk.usage != null)
                    {
                        reported[0] = true;
                        try { report(BuildResponseUsage(genId[0], provider[0], chunk.usage, discount[0])); }
                        catch { }
                    }
                }
                if (onChunk != null) onChunk(chunk);
            };
        }

        private static ResponseUsage BuildResponseUsage(
            string generationId, string provider, ChatCompletionChunk.UsageInfo u, decimal? cacheDiscount)
        {
            ResponseUsage r = new ResponseUsage();
            r.Id = generationId;
            r.Provider = provider;
            r.PromptTokens = u.prompt_tokens.HasValue ? u.prompt_tokens.Value : 0;
            r.CompletionTokens = u.completion_tokens.HasValue ? u.completion_tokens.Value : 0;
            r.Cost = u.cost;
            r.CacheDiscount = cacheDiscount;
            ChatCompletionChunk.PromptTokensDetails pd = u.prompt_tokens_details;
            if (pd != null)
            {
                if (pd.cached_tokens.HasValue) r.CachedTokens = pd.cached_tokens.Value;
                if (pd.cache_write_tokens.HasValue) r.CacheWriteTokens = pd.cache_write_tokens.Value;
            }
            ChatCompletionChunk.CompletionTokensDetails cd = u.completion_tokens_details;
            if (cd != null && cd.reasoning_tokens.HasValue) r.ReasoningTokens = cd.reasoning_tokens.Value;
            return r;
        }

        public void CreateCompletionStream(string model, IList<ChatMessage> messages, Action<string> onDelta, Action onDone, Action<string> onError)
        {
            // Backward-compatible API: defaults to stream
            CreateCompletionStream(model, messages, null, onDelta, onDone, onError, null);
        }

        // Text-only streaming wrapper over StreamRawChunks (no tools): forwards content deltas.
        // cancel (may be null) lets the caller kill the in-flight request mid-stream.
        public void CreateCompletionStream(string model, IList<ChatMessage> messages, ClientProperties props, Action<string> onDelta, Action onDone, Action<string> onError, RequestCancellation cancel)
        {
            if (props == null) props = new ClientProperties();
            if (!props.Stream.HasValue) props.Stream = true;
            string body = BuildRequestBody(model, messages, null, props);

            bool failed = false;
            StreamRawChunks(body,
                WrapWithProviderReport(props, delegate(ChatCompletionChunk chunk)
                {
                    if (chunk != null && chunk.choices != null)
                    {
                        foreach (var ch in chunk.choices)
                        {
                            string content = (ch != null && ch.delta != null) ? ch.delta.content : null;
                            if (!string.IsNullOrEmpty(content) && onDelta != null) onDelta(content);
                        }
                    }
                }),
                delegate(string err) { failed = true; if (onError != null) onError(err); },
                cancel);

            // A user-initiated stop returns from StreamRawChunks without an error (the connection was
            // dropped on purpose). onDone still fires so the caller finalizes whatever text streamed;
            // it inspects the cancel handle to decide how to treat an empty result.
            if (!failed && onDone != null) onDone();
        }

        // The shared curl + SSE streaming loop. Each "data:" line is parsed (Newtonsoft) into a
        // ChatCompletionChunk and handed to onChunk; if the whole stream yields no chunks, the body
        // is inspected for a JSON error and onError is called. When cancel is non-null its curl
        // process is registered so a Stop click can kill it; a kill is reported as a clean stop (no
        // onError) rather than a request failure.
        public void StreamRawChunks(string body, Action<ChatCompletionChunk> onChunk, Action<string> onError, RequestCancellation cancel)
        {
            List<string> tempFiles;
            string args = BuildCurlArgs(body, out tempFiles);

            var psi = new ProcessStartInfo
            {
                FileName = _curlPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                var proc = new Process();
                proc.StartInfo = psi;
                proc.EnableRaisingEvents = false;
                proc.Start();
                // Register the live process so a Stop click can kill it (dropping the connection,
                // which unblocks the ReadLine loop below). If Cancel already fired, this kills now.
                if (cancel != null) cancel.Attach(proc);

                // Read lines as they arrive (SSE: lines like "data: {...}") with explicit UTF-8 decoding
                var utf8 = new UTF8Encoding(false, false);
                var sr = new StreamReader(proc.StandardOutput.BaseStream, utf8, false);
                var er = new StreamReader(proc.StandardError.BaseStream, utf8, false);
                string line;
                bool sawAnyChunk = false;
                int chunkCount = 0;
                ChatCompletionChunk.UsageInfo usage = null;
                var stdoutBuf = new StringBuilder();
                while ((line = sr.ReadLine()) != null)
                {
                    line = line.Trim();
                    // Buffer all stdout; if the request failed we'll inspect this for a JSON error body.
                    if (line.Length > 0)
                    {
                        try { stdoutBuf.AppendLine(line); }
                        catch { }
                    }
                    if (line.Length == 0) continue;
                    // SSE events are "data: {...}" lines. Anything else (curl's --fail-with-body HTTP
                    // error body, SSE ':' heartbeats, etc.) is NOT a content chunk: skip it here so it
                    // can't be mis-parsed as an empty chunk and silently swallow the failure. It stays
                    // buffered above for post-loop error extraction.
                    bool isData = line.StartsWith("data:");
                    if (!isData) continue;
                    line = line.Substring(5).Trim();
                    if (line == "[DONE]") break;

                    ChatCompletionChunk chunk = null;
                    try { chunk = JsonConvert.DeserializeObject<ChatCompletionChunk>(line); }
                    catch { continue; } // Ignore malformed keepalive/heartbeat lines
                    if (chunk != null)
                    {
                        sawAnyChunk = true;
                        chunkCount++;
                        if (chunk.usage != null) usage = chunk.usage; // final SSE chunk carries usage
                        if (onChunk != null) onChunk(chunk);
                    }
                }
                int exitCode = -1;
                try { proc.WaitForExit(); }
                catch { }
                try { exitCode = proc.ExitCode; }
                catch { }

                string err = null;
                try { err = er.ReadToEnd(); }
                catch { }
                string rawBody = null;
                try { rawBody = stdoutBuf.ToString().Trim(); }
                catch { }
                string extractedMsg = null;
                if (!string.IsNullOrEmpty(rawBody))
                {
                    try { extractedMsg = ExtractErrorMessage(SafeParse(rawBody)); }
                    catch { }
                }

                try { Logger.Log("Stream", "curl exit=" + exitCode + " chunks=" + chunkCount + (sawAnyChunk ? string.Empty : " (no chunks)")); }
                catch { }

                // Prompt-cache telemetry: cached = prompt-prefix tokens served from the provider's
                // cache (discounted); cacheWrite = tokens written to a new entry (write premium).
                // cached=0 on a long conversation means the prefix missed, and repeated large
                // cacheWrite values are the signature of an invalidator re-billing the prefix
                // (tools array changed, non-deterministic serialization).
                try
                {
                    if (usage != null)
                    {
                        ChatCompletionChunk.PromptTokensDetails d = usage.prompt_tokens_details;
                        int? cached = (d != null) ? d.cached_tokens : null;
                        int? written = (d != null) ? d.cache_write_tokens : null;
                        Logger.Log("Usage", "prompt=" + (usage.prompt_tokens.HasValue ? usage.prompt_tokens.Value.ToString() : "?")
                            + " cached=" + (cached.HasValue ? cached.Value.ToString() : "?")
                            + " cacheWrite=" + (written.HasValue ? written.Value.ToString() : "?")
                            + " completion=" + (usage.completion_tokens.HasValue ? usage.completion_tokens.Value.ToString() : "?")
                            + " cost=" + (usage.cost.HasValue ? usage.cost.Value.ToString() : "?") + " (streamed)");
                    }
                }
                catch { }

                // User-initiated stop: killing curl makes it exit non-zero / end without [DONE], which
                // would otherwise look like a failure. Treat it as a clean stop - keep whatever
                // streamed, raise no error - so the caller finalizes the partial answer.
                if (cancel != null && cancel.IsCancelled)
                {
                    try { Logger.Log("Stream", "stream cancelled by user (chunks=" + chunkCount + ")"); }
                    catch { }
                    cancel.Detach();
                    CleanupTempFiles(tempFiles);
                    return;
                }

                // Treat the request as failed if curl reported a non-zero exit (--fail-with-body makes
                // it exit non-zero on every HTTP error), if no content chunks arrived at all, or if the
                // body carried a JSON error payload. Any of these means the user got no real answer.
                bool failed = !sawAnyChunk || exitCode != 0 || !string.IsNullOrEmpty(extractedMsg);
                if (failed)
                {
                    if (!string.IsNullOrEmpty(rawBody))
                        try { Logger.Log("HTTP", "curl error body: " + rawBody); }
                        catch { }
                    if (!string.IsNullOrEmpty(extractedMsg))
                        try { Logger.Log("HTTP", "error.message: " + extractedMsg); }
                        catch { }
                    if (!string.IsNullOrEmpty(err))
                        try { Logger.Log("Stream", "curl stderr: " + err); }
                        catch { }

                    if (onError != null)
                    {
                        string msg = !string.IsNullOrEmpty(extractedMsg)
                            ? extractedMsg
                            : (!string.IsNullOrEmpty(err)
                                ? err
                                : (exitCode != 0 ? ("Request failed (curl exit " + exitCode + ")") : "Request failed"));
                        onError(msg.Trim());
                    }
                    if (cancel != null) cancel.Detach();
                    CleanupTempFiles(tempFiles);
                    return;
                }

                if (cancel != null) cancel.Detach();
                CleanupTempFiles(tempFiles);
            }
            catch (Exception ex)
            {
                Logger.Log("Stream", "Exception: " + ex.Message);
                if (cancel != null) cancel.Detach();
                CleanupTempFiles(tempFiles);
                // A kill mid-read can surface as an IOException here; if the user asked to stop, that's
                // expected - swallow it rather than reporting a request failure.
                if (cancel != null && cancel.IsCancelled) return;
                if (onError != null) onError(ex.Message);
            }
        }

        // Log-friendly copy of the request body: the messages array is replaced wholesale with a
        // one-string placeholder recording how many messages it held - even one transcript message
        // bloats the log too much to be worth keeping. Everything else is left intact.
        internal static string TruncateMessagesForLog(string jsonBody)
        {
            if (string.IsNullOrEmpty(jsonBody)) return jsonBody;
            try
            {
                var obj = JObject.Parse(jsonBody);
                var msgs = obj["messages"] as JArray;
                if (msgs != null && msgs.Count > 0)
                {
                    var truncated = new JArray();
                    truncated.Add("... " + msgs.Count + " message(s) omitted ...");
                    obj["messages"] = truncated;
                }
                return obj.ToString(Formatting.None);
            }
            catch
            {
                // Unparseable body: better an oversized log line than none at all.
                return jsonBody;
            }
        }

        private string BuildCurlArgs(string jsonBody, out List<string> tempFiles)
        {
            tempFiles = new List<string>();

            try { if (Logger.Enabled) Logger.Log("Stream", "Request JSON (messages omitted): " + TruncateMessagesForLog(jsonBody)); }
            catch { }
            try { Logger.Log("Stream", "Request JSON length=" + (jsonBody != null ? jsonBody.Length : 0)); }
            catch { }

            string tempDir = Path.GetTempPath();
            var utf8NoBom = new UTF8Encoding(false);

            // Write the request body to a temp file to avoid command-line length/quoting issues.
            string bodyPath = Path.Combine(tempDir, "gxpt_body_" + Guid.NewGuid().ToString("N") + ".json");
            bool bodyWritten = false;
            try
            {
                File.WriteAllText(bodyPath, jsonBody ?? string.Empty, utf8NoBom);
                bodyWritten = true;
                tempFiles.Add(bodyPath);
            }
            catch (Exception ex)
            {
                // Fallback: if writing fails, pass the body inline as a last resort.
                try { Logger.Log("Stream", "Temp body write failed: " + ex.Message); }
                catch { }
            }

            // Write the Authorization header to a curl config file so the API key never appears
            // on the process command line (where any local process could read it).
            string configPath = Path.Combine(tempDir, "gxpt_cfg_" + Guid.NewGuid().ToString("N") + ".txt");
            bool configWritten = false;
            try
            {
                // curl config file directive: header = "Authorization: Bearer <key>"
                string cfg = "header = \"Authorization: Bearer " + EscapeForCurlConfig(_apiKey) + "\"\n";
                File.WriteAllText(configPath, cfg, utf8NoBom);
                configWritten = true;
                tempFiles.Add(configPath);
            }
            catch (Exception ex)
            {
                try { Logger.Log("Stream", "Auth config write failed: " + ex.Message); }
                catch { }
            }

            var sb = new StringBuilder();
            // -sS: silent but still show errors; --fail-with-body: fail on HTTP errors but keep body on stdout
            sb.Append("-sS --fail-with-body https://openrouter.ai/api/v1/chat/completions ");
            sb.Append("-H \"Content-Type: application/json\" ");
            if (configWritten)
            {
                // -K/--config reads the Authorization header from the temp file (off the command line).
                sb.Append("-K \"").Append(configPath).Append("\" ");
            }
            else
            {
                // Last-resort fallback so requests still work if the temp dir is unwritable.
                sb.Append("-H \"Authorization: Bearer ").Append(_apiKey).Append("\" ");
            }
            sb.Append("-N "); // important for streaming to disable buffering
            if (bodyWritten)
                sb.Append("-d @\"").Append(bodyPath).Append("\"");
            else
                sb.Append("-d \"").Append((jsonBody ?? string.Empty).Replace("\"", "\\\"")).Append("\"");
            try { Logger.Log("Stream", "curl args prepared for request."); }
            catch { }
            return sb.ToString();
        }

        // Escape a value for inclusion inside double quotes in a curl config file.
        // Within double quotes, curl processes the escape sequences \\, \", \t, \n, \r and \v.
        private static string EscapeForCurlConfig(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // Best-effort deletion of temp files created for a request (body and auth config).
        private static void CleanupTempFiles(IEnumerable<string> paths)
        {
            if (paths == null) return;
            foreach (var p in paths)
            {
                try { if (!string.IsNullOrEmpty(p) && File.Exists(p)) File.Delete(p); }
                catch { }
            }
        }

        private static JObject SafeParse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JObject.Parse(json); }
            catch { return null; }
        }

        // Extracts a human-readable error message from common JSON error shapes.
        internal static string ExtractErrorMessage(JObject root)
        {
            if (root == null) return null;
            try
            {
                // { "error": { "message": "..." } } (or error/detail)
                JToken errorObj = root["error"];
                if (errorObj != null && errorObj.Type == JTokenType.Object)
                {
                    string m = AsString(errorObj["message"]);
                    if (!string.IsNullOrEmpty(m)) return m;
                    string e2 = AsString(errorObj["error"]);
                    if (!string.IsNullOrEmpty(e2)) return e2;
                    string d = AsString(errorObj["detail"]);
                    if (!string.IsNullOrEmpty(d)) return d;
                }

                // { "message": "..." }
                string msg = AsString(root["message"]);
                if (!string.IsNullOrEmpty(msg)) return msg;
                // { "detail": "..." }
                string det = AsString(root["detail"]);
                if (!string.IsNullOrEmpty(det)) return det;
            }
            catch { }
            return null;
        }

        private static string AsString(JToken t)
        {
            if (t == null || t.Type != JTokenType.String) return null;
            return (string)t;
        }
    }

    // DTO for configuring request options per-call.
    public sealed class ClientProperties
    {
        // When null, the caller/method will set a sensible default based on the API used.
        public bool? Stream { get; set; }

        // Provider options
        public bool? ProviderDataCollectionAllowed { get; set; }
        // Zero data retention: when true, emits provider.zdr=true so OpenRouter only routes to
        // endpoints with a zero-retention policy. Null/false omits it (no effect on routing).
        public bool? Zdr { get; set; }
        public IList<string> ProviderOnly { get; set; }
        // order: provider preference (sticky cache routing). OpenRouter tries these endpoints first
        // and still falls back to others on failure (allow_fallbacks defaults true) - a preference,
        // not a pin like `only`. Callers put the provider that served the conversation's previous
        // request at the head so routing follows the warm prompt cache (caches are per provider).
        public IList<string> ProviderOrder { get; set; }
        // Invoked (at most once per request, from the stream-reading thread) with the response's
        // usage accounting: serving provider, token counts, cache read/write counts, billed cost,
        // and cache discount. Fires on the final SSE chunk - the one carrying usage - so callers
        // can both gate provider stickiness on demonstrated cache activity and accumulate
        // per-conversation cost; never fires when the endpoint omits usage entirely.
        public Action<ResponseUsage> ResponseUsageCallback { get; set; }
        public decimal? ProviderMaxPricePrompt { get; set; }
        public decimal? ProviderMaxPriceCompletion { get; set; }
        // tool_choice for the request ("none", "auto", ...). Null leaves it at the provider default.
        // Only emitted when a tools array is present. The orchestrator's cap wrap-up uses "none" to
        // forbid further tool calls while keeping the tools array (and so the cached prompt prefix)
        // identical to the loop's requests.
        public string ToolChoice { get; set; }
    }

    // The slice of OpenRouter's generation record (GET /api/v1/generation) used for usage
    // reconciliation - the post-hoc, billed truth that the stream-time estimate converges to.
    public sealed class GenerationStats
    {
        public decimal? TotalCost;     // billed credits (post cache discount / write premium)
        public decimal? CacheDiscount; // net credits saved (+) / extra paid (-) by caching
        public string ProviderName;    // serving endpoint; fallback when the stream omitted it
    }

    // One streamed response's usage accounting, assembled from OpenRouter's final usage-bearing
    // SSE chunk (see ClientProperties.ResponseUsageCallback). Drives sticky cache routing
    // (Provider + the cache counters) and per-conversation cost tracking (Cost/CacheDiscount).
    public sealed class ResponseUsage
    {
        // NOTE - OpenRouter normalizes usage to OpenAI conventions: PromptTokens is the TOTAL
        // prompt size, and CachedTokens / CacheWriteTokens are subsets of it (unlike Anthropic's
        // native API, where input_tokens is the uncached remainder). PromptTokens is therefore the
        // conversation's "context size" gauge as-is; never add the cache counters to it.
        //
        // Cost is the STREAM-TIME figure. Observed billing-accurate (cache pricing included) on
        // Bedrock-served Anthropic models, but CacheDiscount is, in practice, never present on SSE
        // chunks, and other providers' stream estimates aren't guaranteed to match billing. Treat
        // Cost as provisional and reconcile against the authoritative generation record
        // (FetchGenerationStats) keyed by Id - the delta-based reconcile is a no-op when the
        // stream was already accurate.
        public string Id;              // OpenRouter generation id (chunk.id); keys the generation record
        public string Provider;        // serving endpoint (e.g. "Anthropic"); null when not reported
        public int PromptTokens;       // total prompt tokens (cached + written + uncached)
        public int CompletionTokens;   // output tokens, reasoning included
        public int ReasoningTokens;    // of CompletionTokens, how many were reasoning
        public int CachedTokens;       // of PromptTokens, how many were read from the provider's cache
        public int CacheWriteTokens;   // of PromptTokens, how many were written to a new cache entry
        public decimal? Cost;          // credits charged for this request; null when not reported
        public decimal? CacheDiscount; // net credits saved (+) / extra paid (-) by caching; null when not reported
    }
}
