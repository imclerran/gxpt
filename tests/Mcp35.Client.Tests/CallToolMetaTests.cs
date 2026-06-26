using Mcp35.Client;
using Mcp35.Core.Protocol;
using Mcp35.Core.Rpc;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Mcp35.Client.Tests
{
    // CallTool's _meta overload carries out-of-band request metadata (the host current directory) as a
    // sibling of `arguments` in params._meta, where the model can neither see nor spoof it.
    public class CallToolMetaTests
    {
        private static FakeTransport Ready(out JToken captured)
        {
            var t = new FakeTransport();
            t.Handlers[McpMethods.Initialize] = delegate(JToken p)
            {
                return FakeTransport.Result(FakeTransport.InitializeResult("2025-06-18", true));
            };
            t.Handlers[McpMethods.ToolsList] = delegate(JToken p)
            {
                return FakeTransport.Result(FakeTransport.ToolsListResult(null, "echo"));
            };
            return t;
        }

        private static McpServerConnection NewConnection(FakeTransport t)
        {
            Implementation client = new Implementation();
            client.Name = "test-client";
            client.Version = "1.0";
            return new McpServerConnection("fake", t, client, null);
        }

        [Fact]
        public void CallTool_carries_cwd_in_params_meta()
        {
            JToken dummy;
            var t = Ready(out dummy);
            JToken captured = null;
            t.Handlers[McpMethods.ToolsCall] = delegate(JToken p)
            {
                captured = p;
                return FakeTransport.Result(new JObject());
            };

            using (var conn = NewConnection(t))
            {
                conn.Open(5000);
                JObject meta = new JObject();
                meta[McpMeta.CwdKey] = "C:/proj/sub";
                conn.CallTool("echo", new JObject(), meta, 5000);
            }

            Assert.NotNull(captured);
            JObject po = (JObject)captured;
            Assert.NotNull(po["_meta"]);
            Assert.Equal("C:/proj/sub", (string)po["_meta"][McpMeta.CwdKey]);
        }

        [Fact]
        public void CallTool_omits_meta_when_absent()
        {
            JToken dummy;
            var t = Ready(out dummy);
            JToken captured = null;
            t.Handlers[McpMethods.ToolsCall] = delegate(JToken p)
            {
                captured = p;
                return FakeTransport.Result(new JObject());
            };

            using (var conn = NewConnection(t))
            {
                conn.Open(5000);
                conn.CallTool("echo", new JObject(), null, 5000);   // no meta
                conn.CallTool("echo", new JObject(), new JObject(), 5000); // empty meta
            }

            Assert.NotNull(captured);
            Assert.Null(((JObject)captured)["_meta"]); // empty/absent meta is not serialized
        }
    }
}
