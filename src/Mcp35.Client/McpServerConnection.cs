using System;
using System.Collections.Generic;
using Mcp35.Core.Diagnostics;
using Mcp35.Core.Errors;
using Mcp35.Core.Json;
using Mcp35.Core.Protocol;
using Mcp35.Core.Rpc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mcp35.Client
{
    /// <summary>
    /// One connected MCP server: drives the handshake, caches <c>tools/list</c>, and routes
    /// <c>tools/call</c>. Transport-agnostic (any <see cref="IRpcTransport"/>), single-connection
    /// by design — the host owns the collection. See mcp35-client-spec.md §4.
    /// </summary>
    public sealed class McpServerConnection : IDisposable
    {
        private readonly string _name;
        private readonly IRpcTransport _transport;
        private readonly Implementation _clientInfo;
        private readonly ILogSink _log;
        private readonly object _gate = new object();

        private ConnectionState _state = ConnectionState.Created;
        private InitializeResult _serverInfo;
        private bool _supportsTools;

        private List<Tool> _toolsCache;
        private bool _toolsDirty = true;

        /// <summary>
        /// Optional minimum acceptable negotiated protocol version (inclusive). null = accept any
        /// (stdio default; framing is identical across revisions). The host sets this to
        /// <c>ProtocolVersions.HttpFloor</c> for HTTP connections, where older dual-endpoint
        /// transports are unsupported (mcp35-http-spec.md §3).
        /// </summary>
        public string MinProtocolVersion;

        public event EventHandler<ConnectionStateEventArgs> StateChanged;
        public event EventHandler ToolsChanged;

        public McpServerConnection(string name, IRpcTransport transport, Implementation clientInfo, ILogSink log)
        {
            if (transport == null) throw new ArgumentNullException("transport");
            _name = name ?? "mcp";
            _transport = transport;
            _clientInfo = clientInfo ?? DefaultClientInfo();
            _log = log ?? NullLogSink.Instance;
            _transport.Inbound += OnInbound;
        }

        public string Name { get { return _name; } }
        public ConnectionState State { get { lock (_gate) { return _state; } } }
        public InitializeResult ServerInfo { get { return _serverInfo; } }
        public bool SupportsTools { get { return _supportsTools; } }

        // ---- handshake ----

        public void Open(int initializeTimeoutMs)
        {
            try
            {
                SetState(ConnectionState.Starting, null);
                _transport.Start();

                SetState(ConnectionState.Initializing, null);

                InitializeParams ip = new InitializeParams();
                ip.ProtocolVersion = ProtocolVersions.Default;
                ip.Capabilities = new JObject();   // empty client capabilities (phase 3)
                ip.ClientInfo = _clientInfo;

                JsonRpcResponse resp = _transport.SendRequest(
                    McpMethods.Initialize, JObject.FromObject(ip, Serializer()), initializeTimeoutMs);

                if (resp.IsError)
                    throw new McpException(resp.Error);

                InitializeResult result = ((JObject)resp.Result).ToObject<InitializeResult>(Serializer());
                if (!IsAcceptableVersion(result.ProtocolVersion))
                    throw new McpException("unacceptable protocol version: " + result.ProtocolVersion);

                _serverInfo = result;
                _supportsTools = result.Capabilities != null && result.Capabilities["tools"] != null;

                _transport.SendNotification(McpMethods.Initialized, null);

                // Prefetch tools so the host can list immediately.
                RefreshTools();

                SetState(ConnectionState.Ready, null);
            }
            catch (Exception ex)
            {
                // Any failure during the handshake faults the connection. Re-throw the original
                // exception so callers can distinguish timeout / protocol error / transport fault
                // (McpTimeoutException and McpTransportException both extend McpException, so a
                // type-based catch here would otherwise mask them).
                Fault("open failed: " + ex.Message);
                throw;
            }
        }

        // ---- tools ----

        public IList<Tool> ListTools(bool refresh)
        {
            if (!_supportsTools) return new List<Tool>();

            lock (_gate)
            {
                if (!refresh && !_toolsDirty && _toolsCache != null)
                    return new List<Tool>(_toolsCache);
            }

            return RefreshTools();
        }

        private IList<Tool> RefreshTools()
        {
            if (!_supportsTools)
            {
                lock (_gate) { _toolsCache = new List<Tool>(); _toolsDirty = false; }
                return new List<Tool>();
            }

            List<Tool> all = new List<Tool>();
            string cursor = null;
            do
            {
                ListToolsParams p = new ListToolsParams();
                p.Cursor = cursor;
                JToken paramsTok = cursor == null ? null : (JToken)JObject.FromObject(p, Serializer());

                JsonRpcResponse resp = _transport.SendRequest(McpMethods.ToolsList, paramsTok, DefaultCallTimeoutMs);
                if (resp.IsError) throw new McpException(resp.Error);

                ListToolsResult page = ((JObject)resp.Result).ToObject<ListToolsResult>(Serializer());
                if (page.Tools != null) all.AddRange(page.Tools);
                cursor = string.IsNullOrEmpty(page.NextCursor) ? null : page.NextCursor;
            }
            while (cursor != null);

            lock (_gate)
            {
                _toolsCache = all;
                _toolsDirty = false;
            }
            return new List<Tool>(all);
        }

        // ---- calling ----

        public CallToolResult CallTool(string name, JObject args, int timeoutMs)
        {
            return CallTool(name, args, null, timeoutMs);
        }

        // Overload carrying out-of-band request metadata (params._meta) — host-authoritative call data.
        // meta rides alongside `arguments` and is never part of a tool's input schema, so the model can
        // neither see nor spoof it. The client is agnostic to which keys it holds (the caller defines them).
        public CallToolResult CallTool(string name, JObject args, JObject meta, int timeoutMs)
        {
            if (State != ConnectionState.Ready)
                throw new InvalidOperationException("Connection '" + _name + "' is not Ready (state=" + State + ").");
            if (!_supportsTools)
                throw new InvalidOperationException("Server '" + _name + "' does not support tools.");

            CallToolParams cp = new CallToolParams();
            cp.Name = name;
            cp.Arguments = args;
            cp.Meta = (meta != null && meta.Count > 0) ? meta : null;

            JsonRpcResponse resp = _transport.SendRequest(
                McpMethods.ToolsCall, JObject.FromObject(cp, Serializer()), timeoutMs);

            // A JSON-RPC error (e.g. unknown tool -32602) is a protocol failure → throw.
            if (resp.IsError) throw new McpException(resp.Error);

            // Otherwise the CallToolResult (including isError tool failures) passes through verbatim.
            return ((JObject)resp.Result).ToObject<CallToolResult>(Serializer());
        }

        // ---- inbound notifications (reader thread) ----

        private void OnInbound(object sender, JsonRpcInboundEventArgs e)
        {
            if (e.IsRequest) return; // server->client requests handled/declined by the peer

            if (e.Method == McpMethods.ToolsListChanged)
            {
                lock (_gate) { _toolsDirty = true; }
                EventHandler h = ToolsChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
            else
            {
                _log.Log("mcp", "Notification ignored: " + e.Method);
            }
        }

        // ---- lifecycle ----

        public void Close()
        {
            Dispose();
        }

        public void Dispose()
        {
            Shutdown(false);
        }

        /// <summary>
        /// Close the connection. When <paramref name="forceful"/> is true, the transport tears down
        /// for speed (kill child processes immediately, skip best-effort niceties) — for
        /// application/host shutdown. When false, behaves like <see cref="Dispose"/>.
        /// </summary>
        public void Shutdown(bool forceful)
        {
            try { _transport.Inbound -= OnInbound; }
            catch { }
            try { _transport.Shutdown(forceful); }
            catch (Exception ex) { _log.Log("mcp", "Transport dispose threw: " + ex.Message); }
            SetState(ConnectionState.Closed, null);
        }

        // ---- helpers ----

        private void Fault(string message)
        {
            _log.Log("mcp", "Connection '" + _name + "' faulted: " + message);
            SetState(ConnectionState.Faulted, message);
        }

        private void SetState(ConnectionState next, string faultMessage)
        {
            ConnectionState prev;
            lock (_gate)
            {
                if (_state == next) return;
                // Closed is terminal.
                if (_state == ConnectionState.Closed) return;
                prev = _state;
                _state = next;
            }
            EventHandler<ConnectionStateEventArgs> h = StateChanged;
            if (h != null) h(this, new ConnectionStateEventArgs(prev, next, faultMessage));
        }

        private bool IsAcceptableVersion(string negotiated)
        {
            if (string.IsNullOrEmpty(negotiated)) return false;
            // Stdio accepts any returned version (framing is identical across revisions). HTTP sets
            // MinProtocolVersion = HttpFloor; the date-shaped version strings sort lexicographically,
            // so an ordinal compare is a correct >= test.
            if (!string.IsNullOrEmpty(MinProtocolVersion))
                return string.CompareOrdinal(negotiated, MinProtocolVersion) >= 0;
            return true;
        }

        private static JsonSerializer Serializer()
        {
            return JsonSerializer.Create(McpJson.Settings);
        }

        private static Implementation DefaultClientInfo()
        {
            Implementation impl = new Implementation();
            impl.Name = "Mcp35.Client";
            impl.Version = "0.1.0";
            return impl;
        }

        /// <summary>Default per-call timeout (ms); the host can pass its own to <see cref="CallTool"/>.</summary>
        public const int DefaultCallTimeoutMs = 60000;

        /// <summary>Default handshake timeout (ms): generous for stdio cold start.</summary>
        public const int DefaultInitializeTimeoutMs = 10000;
    }
}
