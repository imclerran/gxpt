using System;
using System.Collections.Generic;
using System.IO;
using Mcp35.Core.Diagnostics;
using Mcp35.Core.Json;
using Mcp35.Core.Protocol;
using Mcp35.Core.Rpc;
using Mcp35.Core.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mcp35.Server
{
    /// <summary>
    /// The stdio MCP server runtime: register tools, then <see cref="Run()"/> the
    /// newline-delimited JSON dispatch loop until stdin EOF. Single-threaded and serial
    /// (one request fully handled before the next). See mcp35-server-spec.md §2,§4.
    /// </summary>
    public sealed class McpServer
    {
        private readonly Implementation _serverInfo;
        private readonly ILogSink _log;
        private readonly Dictionary<string, RegisteredTool> _tools =
            new Dictionary<string, RegisteredTool>(StringComparer.Ordinal);
        private readonly List<string> _toolOrder = new List<string>();
        private readonly List<Action> _shutdownHooks = new List<Action>();

        private bool _started;   // latches true on first Run(); registration is frozen thereafter
        private bool _initialized;

        public McpServer(Implementation serverInfo, ILogSink log)
        {
            if (serverInfo == null) throw new ArgumentNullException("serverInfo");
            _serverInfo = serverInfo;
            _log = log ?? NullLogSink.Instance;
        }

        // ---- registration ----

        public void AddTool(string name, string description, JObject inputSchema, ToolHandler handler)
        {
            AddTool(name, description, inputSchema, null, handler);
        }

        // Overload that also carries the tool's capability annotations (readOnlyHint/destructiveHint,
        // see ToolAnnotations). The host classifies tools by these hints, so built-in servers should
        // declare them; an absent annotations object passes through as no hints (host fails closed).
        public void AddTool(string name, string description, JObject inputSchema, JObject annotations, ToolHandler handler)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Tool name is required.", "name");
            if (handler == null) throw new ArgumentNullException("handler");
            if (_started) throw new InvalidOperationException("Cannot add tools after Run() has started.");
            if (_tools.ContainsKey(name)) throw new ArgumentException("Duplicate tool name: " + name, "name");

            Tool descriptor = new Tool();
            descriptor.Name = name;
            descriptor.Description = description;
            descriptor.InputSchema = inputSchema ?? EmptyObjectSchema();
            descriptor.Annotations = annotations; // null => omitted from tools/list (NullValueHandling.Ignore)

            _tools[name] = new RegisteredTool(descriptor, handler);
            _toolOrder.Add(name);
        }

        public void OnShutdown(Action cleanup)
        {
            if (cleanup != null) _shutdownHooks.Add(cleanup);
        }

        // ---- run loop ----

        public void Run()
        {
            Stream stdin = Console.OpenStandardInput();
            Stream stdout = Console.OpenStandardOutput();
            Run(stdin, stdout);
        }

        public void Run(Stream stdin, Stream stdout)
        {
            if (stdin == null) throw new ArgumentNullException("stdin");
            if (stdout == null) throw new ArgumentNullException("stdout");
            _started = true;

            StreamReader reader = StdioFraming.CreateReader(stdin);
            try
            {
                string line;
                while ((line = StdioFraming.ReadMessage(reader)) != null)
                {
                    DispatchLine(line, stdout);
                }
            }
            catch (Exception ex)
            {
                // Unrecoverable transport fault: log, then fall through to the same clean shutdown.
                _log.Log("mcp", "Dispatch loop fault: " + ex.Message);
            }
            finally
            {
                RunShutdown(stdout);
            }
        }

        private void DispatchLine(string line, Stream stdout)
        {
            JObject msg;
            try
            {
                JToken tok = McpJson.Parse(line);
                msg = tok as JObject;
            }
            catch (Exception ex)
            {
                _log.Log("mcp", "Parse error: " + ex.Message);
                WriteError(stdout, RequestId.Null, JsonRpcErrorCodes.ParseError, "Parse error");
                return;
            }

            if (msg == null)
            {
                WriteError(stdout, RequestId.Null, JsonRpcErrorCodes.InvalidRequest, "Expected a JSON-RPC object");
                return;
            }

            string method = (string)msg["method"];

            // Notification (no id).
            if (msg.Property("id") == null)
            {
                HandleNotification(method);
                return;
            }

            RequestId id = ParseId(msg["id"]);
            JObject prms = msg["params"] as JObject;

            switch (method)
            {
                case McpMethods.Initialize:
                    WriteResult(stdout, id, BuildInitializeResult(prms));
                    break;
                case McpMethods.ToolsList:
                    WriteResult(stdout, id, BuildToolsList());
                    break;
                case McpMethods.ToolsCall:
                    HandleToolsCall(stdout, id, prms);
                    break;
                case McpMethods.Ping:
                    WriteResult(stdout, id, new JObject());
                    break;
                default:
                    WriteError(stdout, id, JsonRpcErrorCodes.MethodNotFound, "method not found: " + method);
                    break;
            }
        }

        private void HandleNotification(string method)
        {
            switch (method)
            {
                case McpMethods.Initialized:
                    _initialized = true;
                    _log.Log("mcp", "Client initialized.");
                    break;
                // notifications/cancelled and anything else: ignored (serial dispatch, §7).
            }
        }

        // ---- method handlers ----

        private JObject BuildInitializeResult(JObject prms)
        {
            string clientVersion = prms == null ? null : (string)prms["protocolVersion"];
            string negotiated = NegotiateVersion(clientVersion);

            JObject toolsCap = new JObject();
            toolsCap["listChanged"] = false; // static tool sets (§7)
            JObject capabilities = new JObject();
            capabilities["tools"] = toolsCap;

            InitializeResult result = new InitializeResult();
            result.ProtocolVersion = negotiated;
            result.Capabilities = capabilities;
            result.ServerInfo = _serverInfo;

            return JObject.FromObject(result, Serializer());
        }

        private static string NegotiateVersion(string clientVersion)
        {
            // Echo the client's version if it's one we recognize at or above the HTTP floor;
            // otherwise advertise our default. (Stdio framing is identical across revisions.)
            if (!string.IsNullOrEmpty(clientVersion))
            {
                if (string.CompareOrdinal(clientVersion, ProtocolVersions.HttpFloor) >= 0)
                    return clientVersion;
            }
            return ProtocolVersions.Default;
        }

        private JObject BuildToolsList()
        {
            ListToolsResult list = new ListToolsResult();
            list.Tools = new List<Tool>();
            for (int i = 0; i < _toolOrder.Count; i++)
                list.Tools.Add(_tools[_toolOrder[i]].Descriptor);
            list.NextCursor = null; // no paging — small static sets
            return JObject.FromObject(list, Serializer());
        }

        private void HandleToolsCall(Stream stdout, RequestId id, JObject prms)
        {
            string name = prms == null ? null : (string)prms["name"];
            if (string.IsNullOrEmpty(name))
            {
                WriteError(stdout, id, JsonRpcErrorCodes.InvalidParams, "missing tool name");
                return;
            }

            RegisteredTool tool;
            if (!_tools.TryGetValue(name, out tool))
            {
                WriteError(stdout, id, JsonRpcErrorCodes.InvalidParams, "unknown tool: " + name);
                return;
            }

            JObject args = prms["arguments"] as JObject;
            // params._meta is out-of-band (a sibling of arguments, not part of any tool's schema): a
            // host carries call metadata here. The framework stays agnostic to its keys (consumers read
            // them via ctx.Meta / ctx.MetaString).
            JObject meta = prms["_meta"] as JObject;
            ToolCallContext ctx = new ToolCallContext(name, args, _log, meta);

            CallToolResult result;
            try
            {
                result = tool.Handler(ctx);
                if (result == null) result = ToolResults.Error("tool returned no result");
            }
            catch (Exception ex)
            {
                // A tool bug must never crash the server: contain it as an isError result.
                _log.Log("mcp", "Tool '" + name + "' threw: " + ex.Message);
                result = ToolResults.Error("tool execution failed: " + ex.Message);
            }

            WriteResult(stdout, id, JObject.FromObject(result, Serializer()));
        }

        // ---- shutdown ----

        private void RunShutdown(Stream stdout)
        {
            for (int i = 0; i < _shutdownHooks.Count; i++)
            {
                try { _shutdownHooks[i](); }
                catch (Exception ex) { _log.Log("mcp", "Shutdown hook threw: " + ex.Message); }
            }
            try { stdout.Flush(); }
            catch { }
            try { Console.Error.Flush(); }
            catch { }
            _log.Log("mcp", "Server shut down cleanly.");
        }

        // ---- wire helpers ----

        private void WriteResult(Stream stdout, RequestId id, JToken result)
        {
            JObject o = new JObject();
            o["jsonrpc"] = "2.0";
            o["id"] = IdToToken(id);
            o["result"] = result ?? JValue.CreateNull();
            StdioFraming.WriteMessage(stdout, o.ToString(Formatting.None));
        }

        private void WriteError(Stream stdout, RequestId id, int code, string message)
        {
            JObject err = new JObject();
            err["code"] = code;
            err["message"] = message;
            JObject o = new JObject();
            o["jsonrpc"] = "2.0";
            o["id"] = IdToToken(id);
            o["error"] = err;
            StdioFraming.WriteMessage(stdout, o.ToString(Formatting.None));
        }

        private static JsonSerializer Serializer()
        {
            return JsonSerializer.Create(McpJson.Settings);
        }

        private static JObject EmptyObjectSchema()
        {
            JObject s = new JObject();
            s["type"] = "object";
            return s;
        }

        private static JToken IdToToken(RequestId id)
        {
            if (id.IsNull) return JValue.CreateNull();
            if (id.IsString) return new JValue(id.AsString);
            return new JValue(id.AsLong);
        }

        private static RequestId ParseId(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return RequestId.Null;
            if (t.Type == JTokenType.Integer) return RequestId.FromLong(t.Value<long>());
            if (t.Type == JTokenType.String) return RequestId.FromString(t.Value<string>());
            return RequestId.FromString(t.ToString());
        }
    }
}
