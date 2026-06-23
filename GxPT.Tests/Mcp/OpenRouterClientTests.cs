using System.Collections.Generic;
using GxPT;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxPT.Tests.Mcp
{
    // Golden-JSON assertions for the Newtonsoft-migrated request builder (D16), plus chunk-parsing
    // checks proving the streaming DTO maps correctly under Newtonsoft. (The curl/SSE transport
    // itself needs a network and is exercised manually.)
    public class OpenRouterClientTests
    {
        // ---- BuildRequestBody: the no-tools path is unchanged in shape ----

        [Fact]
        public void No_tools_request_has_system_then_messages_and_no_tools_key()
        {
            var messages = new List<ChatMessage> { new ChatMessage("user", "hi") };
            var body = OpenRouterClient.BuildRequestBody("openai/gpt-4o", messages, null, new ClientProperties { Stream = true });
            var o = JObject.Parse(body);

            Assert.Equal("openai/gpt-4o", (string)o["model"]);
            Assert.True((bool)o["stream"]);

            var msgs = (JArray)o["messages"];
            Assert.Equal(2, msgs.Count);
            Assert.Equal("system", (string)msgs[0]["role"]);
            Assert.Contains("Do not use emojis", (string)msgs[0]["content"]);
            Assert.Equal("user", (string)msgs[1]["role"]);
            Assert.Equal("hi", (string)msgs[1]["content"]);

            Assert.Null(o["tools"]);       // absent when none provided
            Assert.Null(o["provider"]);    // absent when no provider options
        }

        [Fact]
        public void Defaults_model_when_empty()
        {
            var body = OpenRouterClient.BuildRequestBody(null, new List<ChatMessage>(), null, new ClientProperties());
            Assert.Equal("openai/gpt-4o", (string)JObject.Parse(body)["model"]);
        }

        // ---- tools / tool_calls / tool messages ----

        [Fact]
        public void Tools_array_is_emitted_when_provided()
        {
            var tools = new List<JObject>
            {
                JObject.Parse("{\"type\":\"function\",\"function\":{\"name\":\"files__read\",\"description\":\"d\",\"parameters\":{\"type\":\"object\"}}}")
            };
            var body = OpenRouterClient.BuildRequestBody("m", new List<ChatMessage>(), tools, new ClientProperties());
            var t = (JArray)JObject.Parse(body)["tools"];
            Assert.Single(t);
            Assert.Equal("function", (string)t[0]["type"]);
            Assert.Equal("files__read", (string)t[0]["function"]["name"]);
        }

        [Fact]
        public void Assistant_tool_calls_are_serialized_with_null_content()
        {
            var asst = new ChatMessage("assistant", "");
            asst.ToolCalls = new List<ToolCall> { new ToolCall("call_1", "files__read", "{\"path\":\"a\"}") };
            var body = OpenRouterClient.BuildRequestBody("m", new List<ChatMessage> { asst }, null, new ClientProperties());

            var msg = (JObject)((JArray)JObject.Parse(body)["messages"])[1];
            Assert.Equal("assistant", (string)msg["role"]);
            Assert.Equal(JTokenType.Null, msg["content"].Type);

            var calls = (JArray)msg["tool_calls"];
            Assert.Single(calls);
            Assert.Equal("call_1", (string)calls[0]["id"]);
            Assert.Equal("function", (string)calls[0]["type"]);
            Assert.Equal("files__read", (string)calls[0]["function"]["name"]);
            Assert.Equal("{\"path\":\"a\"}", (string)calls[0]["function"]["arguments"]);
        }

        [Fact]
        public void Empty_tool_call_arguments_become_object_literal()
        {
            var asst = new ChatMessage("assistant", "text");
            asst.ToolCalls = new List<ToolCall> { new ToolCall("c", "files__list", null) };
            var body = OpenRouterClient.BuildRequestBody("m", new List<ChatMessage> { asst }, null, new ClientProperties());
            var calls = (JArray)((JArray)JObject.Parse(body)["messages"])[1]["tool_calls"];
            Assert.Equal("{}", (string)calls[0]["function"]["arguments"]);
        }

        [Fact]
        public void Tool_role_message_has_tool_call_id()
        {
            var tool = new ChatMessage("tool", "result text");
            tool.ToolCallId = "call_1";
            var body = OpenRouterClient.BuildRequestBody("m", new List<ChatMessage> { tool }, null, new ClientProperties());

            var msg = (JObject)((JArray)JObject.Parse(body)["messages"])[1];
            Assert.Equal("tool", (string)msg["role"]);
            Assert.Equal("result text", (string)msg["content"]);
            Assert.Equal("call_1", (string)msg["tool_call_id"]);
        }

        // ---- provider options (unchanged behavior) ----

        [Fact]
        public void Provider_options_are_emitted()
        {
            var props = new ClientProperties
            {
                ProviderDataCollectionAllowed = false,
                ProviderOnly = new List<string> { "openai" },
                ProviderMaxPricePrompt = 1.5m
            };
            var body = OpenRouterClient.BuildRequestBody("m", new List<ChatMessage>(), null, props);
            var p = (JObject)JObject.Parse(body)["provider"];

            Assert.Equal("deny", (string)p["data_collection"]);
            Assert.Equal("openai", (string)((JArray)p["only"])[0]);
            Assert.Equal(1.5m, (decimal)p["max_price"]["prompt"]);
        }

        [Fact]
        public void Zdr_true_emits_provider_zdr()
        {
            var body = OpenRouterClient.BuildRequestBody("m", new List<ChatMessage>(), null,
                new ClientProperties { Zdr = true });
            var p = (JObject)JObject.Parse(body)["provider"];
            Assert.NotNull(p);
            Assert.True((bool)p["zdr"]);
        }

        [Fact]
        public void Zdr_false_or_unset_omits_provider_zdr()
        {
            // false: OpenRouter's per-request flag can only enable ZDR, so we never emit zdr=false.
            var bodyFalse = OpenRouterClient.BuildRequestBody("m", new List<ChatMessage>(), null,
                new ClientProperties { Zdr = false });
            Assert.Null(JObject.Parse(bodyFalse)["provider"]); // no other provider opts -> whole block absent

            // unset: same.
            var bodyNull = OpenRouterClient.BuildRequestBody("m", new List<ChatMessage>(), null,
                new ClientProperties());
            Assert.Null(JObject.Parse(bodyNull)["provider"]);
        }

        // ---- prompt caching ----

        [Fact]
        public void Usage_accounting_is_always_requested()
        {
            var body = OpenRouterClient.BuildRequestBody("m", new List<ChatMessage>(), null, new ClientProperties());
            Assert.True((bool)JObject.Parse(body)["usage"]["include"]);
        }

        [Fact]
        public void Cache_breakpoint_emits_content_part_with_cache_control_on_supported_model()
        {
            var msg = new ChatMessage("user", "hi");
            msg.CacheControl = true;
            var body = OpenRouterClient.BuildRequestBody(
                "anthropic/claude-sonnet-4.5", new List<ChatMessage> { msg }, null, new ClientProperties());

            var content = ((JArray)JObject.Parse(body)["messages"])[1]["content"];
            Assert.Equal(JTokenType.Array, content.Type);
            Assert.Equal("text", (string)content[0]["type"]);
            Assert.Equal("hi", (string)content[0]["text"]);
            Assert.Equal("ephemeral", (string)content[0]["cache_control"]["type"]);
        }

        [Fact]
        public void Cache_breakpoint_on_tool_message_keeps_tool_call_id()
        {
            var tool = new ChatMessage("tool", "result text");
            tool.ToolCallId = "call_1";
            tool.CacheControl = true;
            var body = OpenRouterClient.BuildRequestBody(
                "anthropic/claude-sonnet-4.5", new List<ChatMessage> { tool }, null, new ClientProperties());

            var msg = (JObject)((JArray)JObject.Parse(body)["messages"])[1];
            Assert.Equal("call_1", (string)msg["tool_call_id"]);
            Assert.Equal("ephemeral", (string)msg["content"][0]["cache_control"]["type"]);
        }

        [Fact]
        public void Cache_breakpoints_are_emitted_for_every_model()
        {
            // Providers without explicit caching ignore the annotation (documented by OpenRouter),
            // while some third-party hosts of auto-caching vendors' models may require it - so the
            // markers are unconditional rather than vendor-gated.
            var msg = new ChatMessage("user", "hi");
            msg.CacheControl = true;
            var body = OpenRouterClient.BuildRequestBody(
                "openai/gpt-4o", new List<ChatMessage> { msg }, null, new ClientProperties());

            var content = ((JArray)JObject.Parse(body)["messages"])[1]["content"];
            Assert.Equal(JTokenType.Array, content.Type);
            Assert.Equal("ephemeral", (string)content[0]["cache_control"]["type"]);
        }

        [Fact]
        public void Unflagged_messages_keep_plain_string_content()
        {
            var body = OpenRouterClient.BuildRequestBody(
                "anthropic/claude-sonnet-4.5", new List<ChatMessage> { new ChatMessage("user", "hi") },
                null, new ClientProperties());
            Assert.Equal("hi", (string)((JArray)JObject.Parse(body)["messages"])[1]["content"]);
        }

        [Fact]
        public void Model_cache_support_is_vendor_prefixed_and_tilde_alias_aware()
        {
            // The caching gate (drives reveal-set eviction and sticky routing).
            Assert.True(OpenRouterClient.ModelSupportsPromptCaching("openai/gpt-4o"));
            Assert.True(OpenRouterClient.ModelSupportsPromptCaching("deepseek/deepseek-chat"));
            Assert.True(OpenRouterClient.ModelSupportsPromptCaching("qwen/qwen3-coder"));
            Assert.True(OpenRouterClient.ModelSupportsPromptCaching("minimax/minimax-m2"));
            Assert.True(OpenRouterClient.ModelSupportsPromptCaching("moonshotai/kimi-k2"));
            Assert.True(OpenRouterClient.ModelSupportsPromptCaching("~anthropic/claude-sonnet-latest"));
            Assert.False(OpenRouterClient.ModelSupportsPromptCaching("mistralai/mistral-large"));
        }

        [Fact]
        public void Tool_choice_is_emitted_only_alongside_tools()
        {
            var tools = new List<JObject>
            {
                JObject.Parse("{\"type\":\"function\",\"function\":{\"name\":\"f\",\"parameters\":{}}}")
            };
            var withTools = OpenRouterClient.BuildRequestBody(
                "m", new List<ChatMessage>(), tools, new ClientProperties { ToolChoice = "none" });
            Assert.Equal("none", (string)JObject.Parse(withTools)["tool_choice"]);

            var withoutTools = OpenRouterClient.BuildRequestBody(
                "m", new List<ChatMessage>(), null, new ClientProperties { ToolChoice = "none" });
            Assert.Null(JObject.Parse(withoutTools)["tool_choice"]);
        }

        [Fact]
        public void Provider_order_is_emitted_for_sticky_cache_routing()
        {
            var props = new ClientProperties { ProviderOrder = new List<string> { "Amazon Bedrock" } };
            var body = OpenRouterClient.BuildRequestBody("m", new List<ChatMessage>(), null, props);
            var p = (JObject)JObject.Parse(body)["provider"];
            Assert.Equal("Amazon Bedrock", (string)((JArray)p["order"])[0]);
        }

        [Fact]
        public void Parses_provider_on_chunk()
        {
            var json = "{\"id\":\"x\",\"provider\":\"Anthropic\",\"choices\":[{\"delta\":{\"content\":\"h\"},\"finish_reason\":null}]}";
            var chunk = JsonConvert.DeserializeObject<ChatCompletionChunk>(json);
            Assert.Equal("Anthropic", chunk.provider);
        }

        [Fact]
        public void Parses_usage_chunk_with_cache_counters_and_cost()
        {
            var json = "{\"id\":\"x\",\"cache_discount\":0.0031,\"choices\":[],"
                + "\"usage\":{\"prompt_tokens\":1200,\"completion_tokens\":80,\"total_tokens\":1280,\"cost\":0.0145,"
                + "\"prompt_tokens_details\":{\"cached_tokens\":1100,\"cache_write_tokens\":90},"
                + "\"completion_tokens_details\":{\"reasoning_tokens\":25}}}";
            var chunk = JsonConvert.DeserializeObject<ChatCompletionChunk>(json);
            Assert.Equal(1200, chunk.usage.prompt_tokens);
            Assert.Equal(80, chunk.usage.completion_tokens);
            Assert.Equal(1280, chunk.usage.total_tokens);
            Assert.Equal(0.0145m, chunk.usage.cost);
            Assert.Equal(1100, chunk.usage.prompt_tokens_details.cached_tokens);
            Assert.Equal(90, chunk.usage.prompt_tokens_details.cache_write_tokens);
            Assert.Equal(25, chunk.usage.completion_tokens_details.reasoning_tokens);
            Assert.Equal(0.0031m, chunk.cache_discount);
        }

        [Fact]
        public void Extracts_generation_stats_for_cost_reconciliation()
        {
            var stats = OpenRouterClient.ExtractGenerationStats(JObject.Parse(
                "{\"data\":{\"id\":\"gen-x\",\"total_cost\":0.0585,\"cache_discount\":0.0559,\"provider_name\":\"Amazon Bedrock\"}}"));
            Assert.NotNull(stats);
            Assert.Equal(0.0585m, stats.TotalCost);
            Assert.Equal(0.0559m, stats.CacheDiscount);
            Assert.Equal("Amazon Bedrock", stats.ProviderName);

            // negative discount = net write premium; still extracted
            var writeHeavy = OpenRouterClient.ExtractGenerationStats(JObject.Parse(
                "{\"data\":{\"total_cost\":0.138,\"cache_discount\":-0.0225}}"));
            Assert.NotNull(writeHeavy);
            Assert.Equal(-0.0225m, writeHeavy.CacheDiscount);
            Assert.Null(writeHeavy.ProviderName);

            // malformed / missing data -> null, nothing extracted
            Assert.Null(OpenRouterClient.ExtractGenerationStats(JObject.Parse("{\"error\":{}}")));
            Assert.Null(OpenRouterClient.ExtractGenerationStats(null));
        }

        // ---- log truncation: the messages array collapses to a single omission marker ----

        [Fact]
        public void TruncateMessagesForLog_replaces_messages_with_count_marker()
        {
            var body = OpenRouterClient.BuildRequestBody("m", new List<ChatMessage>
            {
                new ChatMessage("user", "first"),
                new ChatMessage("assistant", "second"),
                new ChatMessage("user", "last"),
            }, null, new ClientProperties());

            var logged = JObject.Parse(OpenRouterClient.TruncateMessagesForLog(body));
            var msgs = (JArray)logged["messages"];

            // 4 originals (emoji system message + 3) collapse to the marker alone
            Assert.Equal(1, msgs.Count);
            Assert.Equal("... 4 message(s) omitted ...", (string)msgs[0]);

            // everything outside messages is untouched
            Assert.Equal("m", (string)logged["model"]);
        }

        [Fact]
        public void TruncateMessagesForLog_leaves_bad_json_alone()
        {
            Assert.Equal("not json", OpenRouterClient.TruncateMessagesForLog("not json"));
            Assert.Null(OpenRouterClient.TruncateMessagesForLog(null));
        }

        // ---- streaming chunk parsing under Newtonsoft ----

        [Fact]
        public void Parses_streamed_content_chunk()
        {
            var json = "{\"id\":\"x\",\"choices\":[{\"delta\":{\"content\":\"Hello\"},\"finish_reason\":null}]}";
            var chunk = JsonConvert.DeserializeObject<ChatCompletionChunk>(json);
            Assert.Equal("Hello", chunk.choices[0].delta.content);
            Assert.Null(chunk.choices[0].finish_reason);
        }

        [Fact]
        public void Parses_streamed_tool_call_chunk()
        {
            var json = "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"files__read\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}";
            var chunk = JsonConvert.DeserializeObject<ChatCompletionChunk>(json);
            var choice = chunk.choices[0];
            Assert.Equal("tool_calls", choice.finish_reason);
            var tc = choice.delta.tool_calls[0];
            Assert.Equal(0, tc.index);
            Assert.Equal("c1", tc.id);
            Assert.Equal("files__read", tc.function.name);
            Assert.Equal("{}", tc.function.arguments);
        }

        // ---- multimodal image_url content parts (phase 3) ----

        [Fact]
        public void Image_attachment_emits_multipart_image_url_array()
        {
            var msg = new ChatMessage("user", "what is this?");
            msg.Attachments = new List<AttachedFile>
            {
                new AttachedFile { FileName = "photo.png", Kind = AttachmentKind.Image,
                                   MediaType = "image/png", Data = "QUJD" }
            };
            var body = OpenRouterClient.BuildRequestBody("vendor/vision", new List<ChatMessage> { msg },
                null, new ClientProperties());

            var content = ((JArray)JObject.Parse(body)["messages"])[1]["content"];
            Assert.Equal(JTokenType.Array, content.Type);

            var textPart = content[0];
            Assert.Equal("text", (string)textPart["type"]);
            Assert.Equal("what is this?", (string)textPart["text"]);

            var imgPart = content[1];
            Assert.Equal("image_url", (string)imgPart["type"]);
            Assert.Equal("data:image/png;base64,QUJD", (string)imgPart["image_url"]["url"]);
        }

        [Fact]
        public void Image_attachment_with_empty_text_omits_text_part()
        {
            var msg = new ChatMessage("user", "");
            msg.Attachments = new List<AttachedFile>
            {
                new AttachedFile { FileName = "a.png", Kind = AttachmentKind.Image,
                                   MediaType = "image/png", Data = "QUJD" }
            };
            var body = OpenRouterClient.BuildRequestBody("v/m", new List<ChatMessage> { msg },
                null, new ClientProperties());

            var content = ((JArray)JObject.Parse(body)["messages"])[1]["content"];
            Assert.Equal(JTokenType.Array, content.Type);
            Assert.Single(content);
            Assert.Equal("image_url", (string)content[0]["type"]);
        }

        [Fact]
        public void Image_attachment_with_cache_flag_puts_cache_control_on_last_part()
        {
            var msg = new ChatMessage("user", "look");
            msg.CacheControl = true;
            msg.Attachments = new List<AttachedFile>
            {
                new AttachedFile { FileName = "a.png", Kind = AttachmentKind.Image,
                                   MediaType = "image/jpeg", Data = "QUJD" }
            };
            var body = OpenRouterClient.BuildRequestBody("v/m", new List<ChatMessage> { msg },
                null, new ClientProperties());

            var content = ((JArray)JObject.Parse(body)["messages"])[1]["content"];
            var lastPart = content[content.Count() - 1];
            // cache_control lands on the image part (last), not the text part.
            Assert.Equal("image_url", (string)lastPart["type"]);
            Assert.Equal("ephemeral", (string)lastPart["cache_control"]["type"]);
            // Text part has no cache_control.
            Assert.Null(content[0]["cache_control"]);
        }

        [Fact]
        public void Multiple_image_attachments_emit_multiple_image_url_parts()
        {
            var msg = new ChatMessage("user", "compare");
            msg.Attachments = new List<AttachedFile>
            {
                new AttachedFile { FileName = "a.png", Kind = AttachmentKind.Image,
                                   MediaType = "image/png", Data = "QQ==" },
                new AttachedFile { FileName = "b.jpg", Kind = AttachmentKind.Image,
                                   MediaType = "image/jpeg", Data = "Qg==" }
            };
            var body = OpenRouterClient.BuildRequestBody("v/m", new List<ChatMessage> { msg },
                null, new ClientProperties());

            var content = ((JArray)JObject.Parse(body)["messages"])[1]["content"];
            Assert.Equal(3, content.Count()); // text + 2 images
            Assert.Equal("image_url", (string)content[1]["type"]);
            Assert.Contains("QQ==", (string)content[1]["image_url"]["url"]);
            Assert.Equal("image_url", (string)content[2]["type"]);
            Assert.Contains("Qg==", (string)content[2]["image_url"]["url"]);
        }

        [Fact]
        public void Text_only_message_with_no_images_stays_plain_string()
        {
            // No attachments on the message — ContentValue must stay a plain string.
            var msg = new ChatMessage("user", "hello");
            var body = OpenRouterClient.BuildRequestBody("v/m", new List<ChatMessage> { msg },
                null, new ClientProperties());
            var content = ((JArray)JObject.Parse(body)["messages"])[1]["content"];
            Assert.Equal(JTokenType.String, content.Type);
            Assert.Equal("hello", (string)content);
        }

        // ---- native PDF `file` content parts (phase 4) ----

        [Fact]
        public void Pdf_attachment_emits_file_part_with_data_url()
        {
            var msg = new ChatMessage("user", "summarize this");
            msg.Attachments = new List<AttachedFile>
            {
                new AttachedFile { FileName = "report.pdf", Kind = AttachmentKind.Pdf,
                                   MediaType = "application/pdf", Data = "JVBERi0=" }
            };
            var body = OpenRouterClient.BuildRequestBody("vendor/docs", new List<ChatMessage> { msg },
                null, new ClientProperties());

            var content = ((JArray)JObject.Parse(body)["messages"])[1]["content"];
            Assert.Equal(JTokenType.Array, content.Type);

            var textPart = content[0];
            Assert.Equal("text", (string)textPart["type"]);
            Assert.Equal("summarize this", (string)textPart["text"]);

            var filePart = content[1];
            Assert.Equal("file", (string)filePart["type"]);
            Assert.Equal("report.pdf", (string)filePart["file"]["filename"]);
            Assert.Equal("data:application/pdf;base64,JVBERi0=", (string)filePart["file"]["file_data"]);
        }

        [Fact]
        public void Pdf_and_image_attachments_emit_both_part_types()
        {
            var msg = new ChatMessage("user", "look at both");
            msg.Attachments = new List<AttachedFile>
            {
                new AttachedFile { FileName = "a.png", Kind = AttachmentKind.Image,
                                   MediaType = "image/png", Data = "QQ==" },
                new AttachedFile { FileName = "b.pdf", Kind = AttachmentKind.Pdf,
                                   MediaType = "application/pdf", Data = "Qg==" }
            };
            var body = OpenRouterClient.BuildRequestBody("v/m", new List<ChatMessage> { msg },
                null, new ClientProperties());

            var content = ((JArray)JObject.Parse(body)["messages"])[1]["content"];
            Assert.Equal(3, content.Count()); // text + image + file
            Assert.Equal("image_url", (string)content[1]["type"]);
            Assert.Equal("file", (string)content[2]["type"]);
        }

        [Fact]
        public void Pdf_attachment_with_cache_flag_puts_cache_control_on_file_part()
        {
            var msg = new ChatMessage("user", "doc");
            msg.CacheControl = true;
            msg.Attachments = new List<AttachedFile>
            {
                new AttachedFile { FileName = "b.pdf", Kind = AttachmentKind.Pdf,
                                   MediaType = "application/pdf", Data = "Qg==" }
            };
            var body = OpenRouterClient.BuildRequestBody("v/m", new List<ChatMessage> { msg },
                null, new ClientProperties());

            var content = ((JArray)JObject.Parse(body)["messages"])[1]["content"];
            var lastPart = content[content.Count() - 1];
            Assert.Equal("file", (string)lastPart["type"]);
            Assert.Equal("ephemeral", (string)lastPart["cache_control"]["type"]);
        }
    }
}
