using Mcp35.Core.Diagnostics;
using Mcp35.Core.Protocol;
using Newtonsoft.Json.Linq;

namespace Mcp35.Server
{
    /// <summary>A tool implementation: receives the call context, returns a result (failures are values).</summary>
    public delegate CallToolResult ToolHandler(ToolCallContext ctx);

    /// <summary>Everything a handler needs for one <c>tools/call</c> invocation.</summary>
    public sealed class ToolCallContext
    {
        private readonly string _toolName;
        private readonly JObject _arguments;
        private readonly ILogSink _log;
        private readonly JObject _meta;

        public ToolCallContext(string toolName, JObject arguments, ILogSink log)
            : this(toolName, arguments, log, null)
        {
        }

        // meta carries the request's out-of-band params._meta (host-authoritative metadata such as the
        // current working directory, McpMeta.CwdKey); normalized to an empty object so handlers never
        // have to null-check ctx.Meta. It is NOT part of any tool's input schema and the model cannot
        // set it — only the host injects it.
        public ToolCallContext(string toolName, JObject arguments, ILogSink log, JObject meta)
        {
            _toolName = toolName;
            // MCP allows arguments to be omitted; normalize null to an empty object so handlers
            // never have to null-check ctx.Arguments.
            _arguments = arguments ?? new JObject();
            _log = log ?? NullLogSink.Instance;
            _meta = meta ?? new JObject();
        }

        public string ToolName { get { return _toolName; } }
        public JObject Arguments { get { return _arguments; } }
        public ILogSink Log { get { return _log; } }

        /// <summary>The request's out-of-band <c>_meta</c> map (never null).</summary>
        public JObject Meta { get { return _meta; } }

        /// <summary>
        /// The host-injected current working directory for this call (absolute), or null when absent —
        /// in which case the server falls back to its launch-time workspace root (GXPT_WORKDIR). Read
        /// from <c>_meta[McpMeta.CwdKey]</c>; the model cannot supply this.
        /// </summary>
        public string Cwd
        {
            get
            {
                JToken t = _meta[McpMeta.CwdKey];
                if (t == null || t.Type == JTokenType.Null || t.Type != JTokenType.String) return null;
                string s = (string)t;
                return string.IsNullOrEmpty(s) ? null : s;
            }
        }
    }

    /// <summary>A registered tool: its public MCP descriptor plus the handler to invoke.</summary>
    internal sealed class RegisteredTool
    {
        public readonly Tool Descriptor;
        public readonly ToolHandler Handler;

        public RegisteredTool(Tool descriptor, ToolHandler handler)
        {
            Descriptor = descriptor;
            Handler = handler;
        }
    }
}
