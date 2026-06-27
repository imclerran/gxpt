using Mcp35.Core.Protocol;
using Mcp35.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Mcp35.Server.Tests
{
    // The framework's generic, key-agnostic _meta surface: ToolCallContext exposes the raw bag and a
    // typed string accessor, and the dispatch loop threads params._meta onto the context. The framework
    // does NOT know what any particular key means — that is the consumer's concern.
    public class ToolCallContextMetaTests
    {
        [Fact]
        public void MetaString_reads_a_string_field_by_key()
        {
            JObject meta = new JObject();
            meta["x.key"] = "value";
            var ctx = new ToolCallContext("t", new JObject(), null, meta);
            Assert.Equal("value", ctx.MetaString("x.key"));
        }

        [Fact]
        public void MetaString_is_null_when_absent_empty_or_non_string()
        {
            JObject meta = new JObject();
            meta["empty"] = "";
            meta["num"] = 42;
            var ctx = new ToolCallContext("t", new JObject(), null, meta);
            Assert.Null(ctx.MetaString("missing"));
            Assert.Null(ctx.MetaString("empty"));
            Assert.Null(ctx.MetaString("num"));
            Assert.Null(new ToolCallContext("t", new JObject(), null).MetaString("anything"));
        }

        [Fact]
        public void Dispatch_threads_params_meta_onto_the_context()
        {
            Implementation info = new Implementation();
            info.Name = "t"; info.Version = "1.0";
            McpServer server = new McpServer(info, null);
            server.AddTool("echo_meta", "echo a meta field", SchemaBuilder.Object().Build(),
                delegate(ToolCallContext ctx) { return ToolResults.Text(ctx.MetaString("x.key") ?? "<null>"); });

            JObject meta = new JObject();
            meta["x.key"] = "from-meta";
            JObject prms = new JObject();
            prms["name"] = "echo_meta";
            prms["_meta"] = meta;

            var msgs = ServerHarness.Exchange(server, ServerHarness.Request(1, "tools/call", prms));
            Assert.Single(msgs);
            Assert.Equal("from-meta", (string)msgs[0]["result"]["content"][0]["text"]);
        }
    }
}
