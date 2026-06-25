using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Mcp35.Client;
using Mcp35.Core.Diagnostics;
using Mcp35.Core.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxPT
{
    // The host's tool-discovery component (phase 5). Aggregates every connected server's tools into
    // (a) a cheap names manifest, (b) full definitions for a conversation's "revealed" set, and
    // (c) reveal_tools, the meta-tool that moves a tool from "known by name" to "callable". Owns the
    // server-qualified function-name bijection so tool_calls resolve back to (connection, toolName).
    //
    // Reveal state lives on the CONVERSATION, not here (prompt-caching design): each conversation
    // owns an append-only, never-evicted list of revealed names that it passes into Reveal /
    // ExposedFunctionDefs. Per-conversation scoping keeps one tab's reveals out of another tab's
    // tools array, and append-only keeps the serialized tools array stable - the array renders at
    // position 0 of the prompt, so any membership or order change invalidates the provider's prompt
    // cache for the conversation's entire transcript. (Eviction never pays: a revealed def costs a
    // few hundred cache-discounted tokens per turn; evicting it re-bills the whole transcript once.)
    // The registry validates every name against the live catalog at emission time, so a stale
    // revealed name (server removed or disabled) is skipped harmlessly rather than pruned.
    //
    // Workdir-scoped servers (files/git/command) run as one process PER working directory, so several
    // connections can expose the SAME function name (e.g. files__read) for different folders. The
    // model-facing surface (manifest / defs / reveal) is therefore deduped by function name — it's
    // identical across folders — while resolution is workdir-aware: TryResolve(name, workdir) returns
    // the connection bound to the calling turn's folder. Workdir-independent servers (web/github/
    // custom) register with a null workdir and resolve for any turn.
    //
    // Thread-safe: lifecycle methods are driven from connection reader threads (Ready/ToolsChanged/
    // fault), while the orchestrator calls the per-request/resolution methods from its worker thread.
    internal sealed class McpToolRegistry : IToolAnnotationSource
    {
        public const string RevealToolsName = "reveal_tools";

        private sealed class CatalogEntry
        {
            public McpServerConnection Conn;
            public string OriginalName;
            public string FunctionName;
            public string Description;
            public JObject Schema;
            public JObject Annotations; // tool capability hints (readOnlyHint/destructiveHint); may be null
            public string Workdir; // null = workdir-independent (web/github/custom)
        }

        private readonly object _lock = new object();
        // One function name maps to one-or-more candidates: workdir-scoped tools have a candidate per
        // open working directory (same name, different connection); workdir-independent tools have one.
        private readonly Dictionary<string, List<CatalogEntry>> _byFunctionName =
            new Dictionary<string, List<CatalogEntry>>(StringComparer.Ordinal);
        private readonly Dictionary<McpServerConnection, List<string>> _byConnection =
            new Dictionary<McpServerConnection, List<string>>();
        // Remembers each connection's workdir so RefreshConnection (driven by a ToolsChanged event,
        // which carries only the connection) can rebuild its entries with the right workdir tag.
        private readonly Dictionary<McpServerConnection, string> _connWorkdir =
            new Dictionary<McpServerConnection, string>();

        private readonly ILogSink _log;
        private string _manifestCache;
        private bool _manifestDirty = true;

        // Raised after any catalog mutation (a server's tools added, refreshed, or removed) so UI
        // surfaces (the status bar's tool count) can repaint. Fired on connection reader threads —
        // listeners must marshal to the UI thread themselves.
        public event Action Changed;

        private void RaiseChanged()
        {
            Action h = Changed;
            if (h != null)
            {
                try { h(); }
                catch { }
            }
        }

        public McpToolRegistry(ILogSink log)
        {
            _log = log != null ? log : NullLogSink.Instance;
        }

        // ---- lifecycle (driven by McpHost from connection events) ----

        // Workdir-independent registration (web/github/custom servers).
        public void AddConnection(McpServerConnection conn)
        {
            AddConnection(conn, null);
        }

        // workdir tags this connection's tools so resolution can pick the folder-specific instance.
        // null => workdir-independent.
        public void AddConnection(McpServerConnection conn, string workdir)
        {
            if (conn == null) return;
            // ListTools may hit the transport — call it outside the lock, then merge under it.
            IList<Tool> tools = SafeListTools(conn, false);

            lock (_lock)
            {
                _connWorkdir[conn] = workdir;
                List<string> fns;
                if (!_byConnection.TryGetValue(conn, out fns))
                {
                    fns = new List<string>();
                    _byConnection[conn] = fns;
                }
                for (int i = 0; i < tools.Count; i++)
                {
                    Tool t = tools[i];
                    if (t == null || string.IsNullOrEmpty(t.Name)) continue;
                    string fn = Munge(conn.Name, t.Name);

                    CatalogEntry e = new CatalogEntry();
                    e.Conn = conn;
                    e.OriginalName = t.Name;
                    e.FunctionName = fn;
                    e.Description = t.Description;
                    e.Schema = t.InputSchema;
                    e.Annotations = t.Annotations;
                    e.Workdir = workdir;
                    AddEntryLocked(fn, e, fns);
                }
                _manifestDirty = true;
            }
            RaiseChanged();
        }

        public void RefreshConnection(McpServerConnection conn)
        {
            if (conn == null) return;
            IList<Tool> tools = SafeListTools(conn, true);

            lock (_lock)
            {
                string workdir;
                if (!_connWorkdir.TryGetValue(conn, out workdir)) workdir = null;
                RemoveConnectionLocked(conn);
                _connWorkdir[conn] = workdir;

                List<string> fns = new List<string>();
                _byConnection[conn] = fns;
                for (int i = 0; i < tools.Count; i++)
                {
                    Tool t = tools[i];
                    if (t == null || string.IsNullOrEmpty(t.Name)) continue;
                    string fn = Munge(conn.Name, t.Name);

                    CatalogEntry e = new CatalogEntry();
                    e.Conn = conn;
                    e.OriginalName = t.Name;
                    e.FunctionName = fn;
                    e.Description = t.Description;
                    e.Schema = t.InputSchema;
                    e.Annotations = t.Annotations;
                    e.Workdir = workdir;
                    AddEntryLocked(fn, e, fns);
                }
                _manifestDirty = true;
            }
            RaiseChanged();
        }

        public void RemoveConnection(McpServerConnection conn)
        {
            if (conn == null) return;
            lock (_lock)
            {
                RemoveConnectionLocked(conn);
                _manifestDirty = true;
            }
            RaiseChanged();
        }

        // Append (or replace, on refresh) this connection's entry for a function name. Several
        // connections may legitimately share one name (the same scoped server for different folders).
        private void AddEntryLocked(string fn, CatalogEntry e, List<string> fns)
        {
            List<CatalogEntry> list;
            if (!_byFunctionName.TryGetValue(fn, out list))
            {
                list = new List<CatalogEntry>();
                _byFunctionName[fn] = list;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i].Conn, e.Conn)) { list[i] = e; if (!fns.Contains(fn)) fns.Add(fn); return; }
            }
            list.Add(e);
            if (!fns.Contains(fn)) fns.Add(fn);
        }

        // ListTools may hit the transport; call it outside the lock and never let a fault propagate
        // into the registry (a faulted server just contributes no tools).
        private IList<Tool> SafeListTools(McpServerConnection conn, bool refresh)
        {
            try { return conn.ListTools(refresh); }
            catch (Exception ex)
            {
                _log.Log("mcp", "ListTools failed for '" + conn.Name + "': " + ex.Message);
                return new List<Tool>();
            }
        }

        private void RemoveConnectionLocked(McpServerConnection conn)
        {
            _connWorkdir.Remove(conn);
            List<string> fns;
            if (_byConnection.TryGetValue(conn, out fns))
            {
                for (int i = 0; i < fns.Count; i++)
                {
                    string fn = fns[i];
                    List<CatalogEntry> list;
                    if (_byFunctionName.TryGetValue(fn, out list))
                    {
                        for (int j = list.Count - 1; j >= 0; j--)
                            if (ReferenceEquals(list[j].Conn, conn)) list.RemoveAt(j);
                        // The name disappears from the surface only when no connection provides it.
                        // Conversations' revealed lists keep the name; ExposedFunctionDefs skips
                        // names absent from the catalog, so the def simply stops being emitted (and
                        // reappears if the server comes back).
                        if (list.Count == 0)
                        {
                            _byFunctionName.Remove(fn);
                        }
                    }
                }
                _byConnection.Remove(conn);
            }
        }

        // First candidate for a function name (any will do for surface metadata — name, description and
        // schema are identical across a scoped tool's per-folder instances).
        private bool TryFirstEntryLocked(string fn, out CatalogEntry e)
        {
            List<CatalogEntry> list;
            if (fn != null && _byFunctionName.TryGetValue(fn, out list) && list.Count > 0)
            {
                e = list[0];
                return true;
            }
            e = null;
            return false;
        }

        // The candidate whose metadata the model should see for a turn with `workdir`: the SAME one
        // TryResolve would route the call to (exact-workdir match first, then a workdir-independent
        // candidate). This keeps the advertised/revealed schema in lockstep with the connection that
        // will actually execute the call. It matters when one server is offered as BOTH a workdir-
        // scoped and a workdir-less instance (skills): those two carry DIFFERENT schemas (e.g. the
        // default `scope` they apply), so picking list[0] could show the model the schema of an
        // instance the call never reaches. Falls back to list[0] only when nothing matches (the caller
        // has already filtered to resolvable names, so a match is guaranteed in practice).
        private bool TryBestEntryLocked(string fn, string workdir, out CatalogEntry e)
        {
            List<CatalogEntry> list;
            if (fn != null && _byFunctionName.TryGetValue(fn, out list) && list.Count > 0)
            {
                for (int i = 0; i < list.Count; i++)
                    if (WorkdirEquals(list[i].Workdir, workdir)) { e = list[i]; return true; }
                for (int i = 0; i < list.Count; i++)
                    if (list[i].Workdir == null) { e = list[i]; return true; }
                e = list[0];
                return true;
            }
            e = null;
            return false;
        }

        // ---- per-request (from the orchestrator) ----

        // True when at least one connected server has contributed a tool — the host uses this to
        // decide whether to route a chat turn through the tool-call loop at all.
        public bool HasTools
        {
            get { lock (_lock) { return _byFunctionName.Count > 0; } }
        }

        // True when any tool from the named server is currently exposed (the server is enabled,
        // connected, and -- for workdir-scoped servers -- bound to a working directory). Tool names are
        // server-qualified ("git__status"), so a present prefix is an accurate "this toolset is usable
        // right now" signal. Used to gate slash commands via their Requires / RequiresAny lists.
        public bool HasServer(string serverName)
        {
            if (string.IsNullOrEmpty(serverName)) return false;
            string prefix = serverName + "__";
            lock (_lock)
            {
                foreach (string fn in _byFunctionName.Keys)
                {
                    if (fn != null && fn.StartsWith(prefix, StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }

        public string NamesManifestSystemMessage()
        {
            lock (_lock)
            {
                if (_manifestDirty || _manifestCache == null)
                {
                    List<string> names = new List<string>(_byFunctionName.Keys);
                    names.Sort(StringComparer.Ordinal); // server__ prefix groups visually
                    _manifestCache = BuildManifestText(names);
                    _manifestDirty = false;
                }
                return _manifestCache;
            }
        }

        // Workdir-aware manifest: only the tools usable on a turn with this working directory (an exact
        // workdir match or a workdir-independent tool). Keeps a folderless turn from advertising another
        // folder's scoped tools (files/git/run_skill_script, ...), which would only fail at call time.
        public string NamesManifestSystemMessage(string workdir)
        {
            lock (_lock)
            {
                List<string> names = new List<string>();
                foreach (KeyValuePair<string, List<CatalogEntry>> kv in _byFunctionName)
                    if (ResolvableForWorkdirLocked(kv.Value, workdir)) names.Add(kv.Key);
                names.Sort(StringComparer.Ordinal);
                return BuildManifestText(names);
            }
        }

        // Builds the manifest text from an already-sorted, server-qualified name list.
        private static string BuildManifestText(List<string> names)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("The following MCP tools are available, listed by name only. ");
            sb.Append("You CANNOT call any of these tools directly from this list. ");
            sb.Append("Before calling a tool, you MUST first call reveal_tools({\"names\":[...]}) ");
            sb.Append("with the exact names you intend to use; that loads their full definitions ");
            sb.Append("and makes them callable on the next step. You may reveal several at once. ");
            sb.Append("Only reveal_tools and tools you have already revealed can be called.");
            sb.Append("\n\nAvailable tools:");
            bool hasGit = false, hasCommand = false;
            for (int i = 0; i < names.Count; i++)
            {
                sb.Append("\n- ").Append(names[i]);
                if (!hasGit && names[i].StartsWith("git__", StringComparison.Ordinal)) hasGit = true;
                if (!hasCommand && names[i].StartsWith("command__", StringComparison.Ordinal)) hasCommand = true;
            }

            // When both the git and command toolsets are available, steer the model to the
            // dedicated git tools: each git operation is a separately approvable tool (finer-
            // grained, auditable permissions), whereas the command tool needs a broad "git"
            // command approval. Command stays a fallback for git functionality git__ lacks.
            if (hasGit && hasCommand)
            {
                sb.Append("\n\nFor Git operations, prefer the dedicated git__ tools over the ");
                sb.Append("command tool. Only fall back to the command tool for Git functionality ");
                sb.Append("the git__ tools do not provide.");
            }
            return sb.ToString();
        }

        // Workdir-agnostic exposed defs (kept for callers/tests that don't run a folder-scoped turn).
        public IList<JObject> ExposedFunctionDefs(IEnumerable<string> revealed)
        {
            return ExposedFunctionDefs(null, revealed);
        }

        // The exposed defs for a turn: reveal_tools plus the conversation's revealed set, usable on a
        // turn with this working directory. A revealed tool that only exists for another folder is
        // dropped, so it can't be sent then fail; a name no longer in the catalog is skipped.
        //
        // Order is BY NAME, never by reveal order or recency: the tools array renders at position 0
        // of the prompt, so the serialization must be byte-identical between requests or every
        // request misses the provider's prompt cache. (The old recency order re-sorted the array
        // whenever a tool was called, which would have invalidated the cache on every loop iteration.)
        public IList<JObject> ExposedFunctionDefs(string workdir, IEnumerable<string> revealed)
        {
            List<JObject> result = new List<JObject>();
            result.Add(RevealToolsDef());
            if (revealed == null) return result;

            List<string> fns = new List<string>(revealed);
            fns.Sort(StringComparer.Ordinal);
            lock (_lock)
            {
                string prev = null;
                for (int i = 0; i < fns.Count; i++)
                {
                    string fn = fns[i];
                    if (fn == null || fn == prev) continue; // defensive dedup (sorted, so adjacent)
                    prev = fn;
                    List<CatalogEntry> list;
                    if (!_byFunctionName.TryGetValue(fn, out list)) continue;
                    if (!ResolvableForWorkdirLocked(list, workdir)) continue;
                    CatalogEntry e;
                    if (TryBestEntryLocked(fn, workdir, out e))
                        result.Add(FunctionDef(e.FunctionName, e.Description, CloneSchema(e.Schema)));
                }
            }
            return result;
        }

        // The tool names usable on a turn with this working directory — the same set the names
        // manifest advertises (NamesManifestSystemMessage(workdir)), returned as a list so callers
        // can count or filter (the status bar's tool count) without parsing manifest text.
        public IList<string> NamesForWorkdir(string workdir)
        {
            List<string> names = new List<string>();
            lock (_lock)
            {
                foreach (KeyValuePair<string, List<CatalogEntry>> kv in _byFunctionName)
                    if (ResolvableForWorkdirLocked(kv.Value, workdir)) names.Add(kv.Key);
            }
            return names;
        }

        // Any tool usable on a turn with this working directory (an exact workdir match or a workdir-
        // independent tool). Workdir-aware counterpart of HasTools, for gating the tool-loop per turn.
        public bool HasToolsForWorkdir(string workdir)
        {
            lock (_lock)
            {
                foreach (KeyValuePair<string, List<CatalogEntry>> kv in _byFunctionName)
                    if (ResolvableForWorkdirLocked(kv.Value, workdir)) return true;
            }
            return false;
        }

        // A function name is usable on a turn with `workdir` when it has an exact-workdir candidate or a
        // workdir-independent (null) one - exactly TryResolve's selection.
        private bool ResolvableForWorkdirLocked(List<CatalogEntry> list, string workdir)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
                if (list[i].Workdir == null || WorkdirEquals(list[i].Workdir, workdir)) return true;
            return false;
        }

        // ---- tool_call handling ----

        public bool IsRevealTools(string functionName)
        {
            return functionName == RevealToolsName;
        }

        // Workdir-agnostic reveal (independent tools, or tests that don't run a folder turn).
        public string Reveal(string[] names, IList<string> revealed)
        {
            return Reveal(names, null, revealed);
        }

        // Reveal the named tools' full defs for a turn with `workdir`, choosing each tool's schema from
        // the candidate that turn will actually call (TryBestEntryLocked) so the revealed schema matches
        // the executing connection — see TryBestEntryLocked. Successful names are recorded in
        // `revealed`, the conversation's reveal list; the list is kept in recency order (a re-reveal
        // moves the name to the end) so the orchestrator's provider-gated eviction can trim oldest
        // first. Emission order is independent of list order (ExposedFunctionDefs sorts by name).
        public string Reveal(string[] names, string workdir, IList<string> revealed)
        {
            JArray defs = new JArray();
            List<string> notes = new List<string>();

            lock (_lock)
            {
                if (names != null)
                {
                    for (int i = 0; i < names.Length; i++)
                    {
                        string n = names[i];
                        CatalogEntry e;
                        if (n != null && TryBestEntryLocked(n, workdir, out e))
                        {
                            if (revealed != null)
                            {
                                revealed.Remove(n); // no-op when absent; bump-to-end when present
                                revealed.Add(n);
                            }
                            JObject d = new JObject();
                            d["name"] = e.FunctionName;
                            d["description"] = e.Description != null ? e.Description : string.Empty;
                            d["parameters"] = CloneSchema(e.Schema);
                            defs.Add(d);
                        }
                        else
                        {
                            notes.Add("unknown tool: " + (n != null ? n : "(null)"));
                        }
                    }
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(defs.ToString(Formatting.None));
            for (int i = 0; i < notes.Count; i++)
                sb.Append("\n").Append(notes[i]);
            return sb.ToString();
        }

        // Workdir-agnostic resolve (workdir-independent tools only). Kept for callers/tests that don't
        // run a folder-scoped turn.
        public bool TryResolve(string functionName, out McpServerConnection conn, out string toolName)
        {
            return TryResolve(functionName, null, out conn, out toolName);
        }

        // Resolve a function name to the connection for this turn's working directory. Scoped tools
        // match on workdir; workdir-independent tools (Workdir == null) match any turn.
        public bool TryResolve(string functionName, string workdir, out McpServerConnection conn, out string toolName)
        {
            lock (_lock)
            {
                List<CatalogEntry> list;
                if (functionName != null && _byFunctionName.TryGetValue(functionName, out list) && list.Count > 0)
                {
                    CatalogEntry best = null;
                    // 1) exact workdir match (covers scoped tools, and null==null for independents).
                    for (int i = 0; i < list.Count; i++)
                        if (WorkdirEquals(list[i].Workdir, workdir)) { best = list[i]; break; }
                    // 2) fall back to a workdir-independent candidate for an independent tool called
                    //    from within a folder turn (e.g. web__search during a files turn).
                    if (best == null)
                        for (int i = 0; i < list.Count; i++)
                            if (list[i].Workdir == null) { best = list[i]; break; }
                    if (best != null)
                    {
                        conn = best.Conn;
                        toolName = best.OriginalName;
                        return true;
                    }
                }
            }
            conn = null;
            toolName = null;
            return false;
        }

        // The discovered capability annotations (readOnlyHint/destructiveHint) for a function name, so
        // the approval gate can classify a tool by what it declares. Returns a clone (never the stored
        // token) so the caller can't reparent the catalog's copy; null when the tool set no annotations
        // — the classifier then fails closed (treats it as not read-only -> Write/Tool tier).
        public JObject AnnotationsFor(string functionName)
        {
            lock (_lock)
            {
                CatalogEntry e;
                if (TryFirstEntryLocked(functionName, out e) && e.Annotations != null)
                    return (JObject)e.Annotations.DeepClone();
            }
            return null;
        }

        // ---- internals ----

        private static bool WorkdirEquals(string a, string b)
        {
            if (a == null) return b == null;
            if (b == null) return false;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static JObject RevealToolsDef()
        {
            JObject props = new JObject();
            JObject namesProp = new JObject();
            namesProp["type"] = "array";
            JObject items = new JObject();
            items["type"] = "string";
            namesProp["items"] = items;
            props["names"] = namesProp;

            JObject schema = new JObject();
            schema["type"] = "object";
            schema["properties"] = props;
            schema["required"] = new JArray("names");

            return FunctionDef(RevealToolsName,
                "Load full definitions for tools by exact name so they become callable.", schema);
        }

        // Clone so the catalog's stored schema is never reparented into a request-body JObject
        // (a JToken can have only one parent; exposed defs are rebuilt every request).
        private static JObject CloneSchema(JObject schema)
        {
            return schema != null ? (JObject)schema.DeepClone() : new JObject();
        }

        private static JObject FunctionDef(string name, string description, JObject parameters)
        {
            JObject fn = new JObject();
            fn["name"] = name;
            fn["description"] = description != null ? description : string.Empty;
            fn["parameters"] = parameters != null ? (JToken)parameters : new JObject();

            JObject def = new JObject();
            def["type"] = "function";
            def["function"] = fn;
            return def;
        }

        // Deterministic, OpenAI-legal, server-qualified name. Collisions/over-length resolved by a
        // stable hash suffix. Bound (not yet) here — caller binds the returned name to its entry.
        private string Munge(string serverName, string toolName)
        {
            string raw = (serverName != null ? serverName : string.Empty) + "__" +
                         (toolName != null ? toolName : string.Empty);
            string baseName = Sanitize(raw);

            if (baseName.Length <= 64 && !CollidesWithDifferentLocked(baseName, serverName, toolName))
                return baseName;

            string h = ShortHash((serverName != null ? serverName : string.Empty) + "\0" +
                                 (toolName != null ? toolName : string.Empty));
            return Truncate(baseName, 64 - 7) + "_" + h;
        }

        private bool CollidesWithDifferentLocked(string baseName, string serverName, string toolName)
        {
            CatalogEntry e;
            if (!TryFirstEntryLocked(baseName, out e)) return false;
            // Same identity (a stale entry being re-added, or the same scoped server for another
            // folder) is not a collision — those legitimately share the function name.
            bool sameServer = (e.Conn != null && e.Conn.Name == serverName);
            return !(sameServer && e.OriginalName == toolName);
        }

        private static string Sanitize(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                          (c >= '0' && c <= '9') || c == '_' || c == '-';
                sb.Append(ok ? c : '_');
            }
            return sb.ToString();
        }

        private static string Truncate(string s, int n)
        {
            if (s.Length <= n) return s;
            return s.Substring(0, n);
        }

        // FNV-1a over UTF-8 bytes → 6 lowercase hex. Deterministic across runs (no crypto dep).
        private static string ShortHash(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            uint h = 2166136261u;
            for (int i = 0; i < bytes.Length; i++)
            {
                h ^= bytes[i];
                h *= 16777619u;
            }
            return h.ToString("x8", CultureInfo.InvariantCulture).Substring(0, 6);
        }
    }
}
