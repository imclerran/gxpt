using System;
using System.Collections.Generic;
using System.Threading;
using Mcp35.Client;
using Mcp35.Core.Diagnostics;

namespace GxPT
{
    // Owns the host's MCP server connections (D11) and the shared McpToolRegistry. Assembles
    // connections from config specs, wires each connection's lifecycle into the registry, and
    // manages the lifecycle of workdir-scoped servers:
    //   * workdir-independent servers (web + every mcp.json entry) are opened once via Start();
    //   * workdir-scoped built-ins (files/git/command) run as ONE process set PER working directory.
    //     EnsureWorkingDir(dir) lazily launches a folder's set (GXPT_WORKDIR=dir) and keeps it alive;
    //     several conversation tabs sharing a folder share its set, while tabs on different folders get
    //     independent sets. Switching tabs never tears anything down — only ReleaseWorkingDir/RetainOnly
    //     (driven by which folders still have an open tab) and Dispose close scoped servers.
    // Each scoped connection is registered with its workdir so tool calls resolve to the folder that
    // requested them (McpToolRegistry.TryResolve(name, workdir)).
    // Transport construction is delegated to an IServerConnector so this logic is testable without
    // spawning processes. Thread-safe via a single lock; event handlers touch only the (separately
    // locked) registry, so they never re-enter this lock. The blocking part of connecting (process
    // spawn + handshake in conn.Open) runs WITHOUT the lock held: a connection is opened on a
    // throwaway object, then published into the collections/registry under the lock with a _disposed
    // re-check. This keeps Dispose() from ever waiting behind an in-progress connect (the cause of
    // the slow app close when shutting down while servers were still connecting).
    internal sealed class McpHost : IDisposable
    {
        public const int DefaultOpenTimeoutMs = 15000;

        private readonly IServerConnector _connector;
        private readonly McpToolRegistry _registry;
        private readonly ILogSink _log;
        private readonly int _openTimeoutMs;
        private readonly object _lock = new object();

        private readonly List<McpServerConnection> _eager = new List<McpServerConnection>();
        // One scoped connection set per working directory (key = the directory as supplied).
        private readonly Dictionary<string, List<McpServerConnection>> _scopedByWorkdir =
            new Dictionary<string, List<McpServerConnection>>(StringComparer.OrdinalIgnoreCase);
        // Working dirs requested before Start() knew the scoped specs; launched when Start arrives.
        private readonly List<string> _pendingWorkdirs = new List<string>();
        // Workdirs whose scoped set is being connected right now. Reserved (under _lock) before the
        // blocking Open() runs unlocked, so a second EnsureWorkingDir for the same folder waits on the
        // event instead of launching a duplicate set.
        private readonly Dictionary<string, ManualResetEvent> _connecting =
            new Dictionary<string, ManualResetEvent>(StringComparer.OrdinalIgnoreCase);
        // Working dirs that are SCRATCH sandboxes (no user workspace): only RunsInScratch scoped specs
        // (the command server) launch for these; files/git/msbuild are skipped. Recorded before the
        // (unlocked) connect so ConnectScoped filters the spec set, and pruned alongside the scoped set
        // on teardown. A scratch path and a real-workspace path never collide (scratch lives under a
        // dedicated %AppData% subfolder), so a given key is only ever one kind.
        private readonly Dictionary<string, bool> _scratchWorkdirs =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private List<McpServerSpec> _scopedSpecs = new List<McpServerSpec>();
        private bool _started;
        // Volatile: the connect loops (which run OUTSIDE _lock) poll this to bail out promptly once
        // Dispose flips it, and the publish steps re-check it under _lock.
        private volatile bool _disposed;

        public McpHost(IServerConnector connector, McpToolRegistry registry, ILogSink log)
            : this(connector, registry, log, DefaultOpenTimeoutMs)
        {
        }

        public McpHost(IServerConnector connector, McpToolRegistry registry, ILogSink log, int openTimeoutMs)
        {
            if (connector == null) throw new ArgumentNullException("connector");
            if (registry == null) throw new ArgumentNullException("registry");
            _connector = connector;
            _registry = registry;
            _log = log != null ? log : NullLogSink.Instance;
            _openTimeoutMs = openTimeoutMs > 0 ? openTimeoutMs : DefaultOpenTimeoutMs;
        }

        public McpToolRegistry Registry { get { return _registry; } }

        // The working directories that currently have a live (or pending) scoped server set. Snapshot;
        // safe to enumerate.
        public string[] ActiveWorkingDirs
        {
            get
            {
                lock (_lock)
                {
                    var keys = new List<string>(_scopedByWorkdir.Keys);
                    for (int i = 0; i < _pendingWorkdirs.Count; i++)
                        if (!keys.Contains(_pendingWorkdirs[i])) keys.Add(_pendingWorkdirs[i]);
                    return keys.ToArray();
                }
            }
        }

        // Open all enabled, workdir-independent servers; remember the workdir-scoped specs for
        // EnsureWorkingDir. Call once after building specs from config.
        public void Start(IEnumerable<McpServerSpec> specs)
        {
            // Phase 1 (locked, fast): record the scoped specs, list the eager specs to open, flip
            // _started, and snapshot any workdirs requested before Start knew the specs. No blocking
            // work happens under the lock.
            List<McpServerSpec> eagerToOpen = new List<McpServerSpec>();
            List<string> pending;
            lock (_lock)
            {
                if (_disposed) return;
                List<McpServerSpec> scoped = new List<McpServerSpec>();
                if (specs != null)
                {
                    foreach (McpServerSpec spec in specs)
                    {
                        if (spec == null) continue;
                        if (spec.WorkdirScoped)
                        {
                            scoped.Add(spec);
                            // A scoped server flagged RunsWithoutWorkdir also gets ONE eager, workdir-less
                            // instance (no GXPT_WORKDIR) so its folderless features work without a workspace.
                            if (spec.Enabled && spec.RunsWithoutWorkdir) eagerToOpen.Add(spec);
                            continue;
                        }
                        if (!spec.Enabled) continue;
                        eagerToOpen.Add(spec);
                    }
                }
                _scopedSpecs = scoped;
                _started = true;
                pending = new List<string>(_pendingWorkdirs);
                _pendingWorkdirs.Clear();
            }

            // Phase 2 (unlocked): connect the eager servers, publishing each as it becomes ready.
            for (int i = 0; i < eagerToOpen.Count; i++)
            {
                if (_disposed) break;
                McpServerConnection conn = CreateAndOpen(eagerToOpen[i], null);
                if (conn != null) PublishEager(conn);
            }

            // Phase 3 (unlocked): connect the scoped sets for any pre-Start workdir requests.
            for (int i = 0; i < pending.Count; i++)
            {
                if (_disposed) break;
                ConnectScoped(pending[i]);
            }
        }

        // Publish a freshly-opened eager connection into the host + registry. If the host was disposed
        // while we were connecting (unlocked), discard it instead so we never leak a started server.
        private void PublishEager(McpServerConnection conn)
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    _registry.AddConnection(conn, null);
                    _eager.Add(conn);
                    return;
                }
            }
            Teardown(conn, true);
        }

        // Ensure the workdir-scoped servers (files/git/command) for `workdir` are running. Idempotent:
        // a folder already served returns immediately; other folders' sets are left untouched. A
        // null/empty workdir is a no-op (no scoped tools for a folderless conversation). Safe to call
        // from a worker thread right before a tool turn; (re)connecting can block.
        public void EnsureWorkingDir(string workdir)
        {
            if (string.IsNullOrEmpty(workdir)) return;
            ConnectScoped(workdir);
        }

        // Ensure the SCRATCH server set for `workdir` is running: like EnsureWorkingDir, but only the
        // scratch-eligible scoped specs (the command server) launch here - files/git/msbuild require a
        // real workspace and are skipped. Used for folderless conversations whose command server runs in
        // a per-conversation scratch dir. The directory must already exist (it becomes the child's CWD).
        // Idempotent; a null/empty workdir is a no-op.
        public void EnsureScratchDir(string workdir)
        {
            if (string.IsNullOrEmpty(workdir)) return;
            lock (_lock)
            {
                if (_disposed) return;
                _scratchWorkdirs[workdir] = true; // mark BEFORE connecting so ConnectScoped filters
            }
            ConnectScoped(workdir);
        }

        // Ensure the scoped set for `workdir` is running, connecting it if needed. The blocking Open()
        // handshakes run WITHOUT _lock held (so Dispose never waits on them); the lock is taken only
        // briefly to reserve the workdir and again to publish the result. A second caller for a
        // workdir already being connected waits for that connect rather than launching a duplicate.
        private void ConnectScoped(string workdir)
        {
            if (string.IsNullOrEmpty(workdir)) return;

            List<McpServerSpec> specs;
            bool scratch = false;
            ManualResetEvent reservation;
            ManualResetEvent waitFor;
            lock (_lock)
            {
                if (_disposed) return;
                if (_scopedByWorkdir.ContainsKey(workdir)) return;      // already connected
                scratch = _scratchWorkdirs.ContainsKey(workdir);
                if (!_started)
                {
                    if (!_pendingWorkdirs.Contains(workdir)) _pendingWorkdirs.Add(workdir);
                    return;                                             // Start will connect it
                }
                if (_connecting.TryGetValue(workdir, out waitFor))
                {
                    specs = null;                                       // someone else owns the connect
                    reservation = null;
                }
                else
                {
                    waitFor = null;
                    reservation = new ManualResetEvent(false);
                    _connecting[workdir] = reservation;
                    specs = new List<McpServerSpec>(_scopedSpecs);      // snapshot to use unlocked
                }
            }

            if (waitFor != null)
            {
                waitFor.WaitOne();   // an in-progress connect owns this workdir; wait for it to publish
                return;
            }

            // We hold the reservation: connect the scoped specs OUTSIDE the lock, then publish (or
            // discard, if the host was disposed meanwhile) under it. try/finally guarantees the
            // reservation is always cleared and signaled, so waiters never hang.
            List<McpServerConnection> conns = new List<McpServerConnection>();
            try
            {
                for (int i = 0; i < specs.Count; i++)
                {
                    if (_disposed) break;
                    McpServerSpec spec = specs[i];
                    if (spec == null || !spec.Enabled) continue;
                    // A scratch sandbox runs only the scratch-eligible scoped specs (command); the
                    // workspace-only servers (files/git/msbuild/memory/skills) are skipped there.
                    if (scratch && !spec.RunsInScratch) continue;
                    McpServerConnection conn = CreateAndOpen(spec, workdir);
                    if (conn != null) conns.Add(conn);
                }
            }
            finally
            {
                List<McpServerConnection> drop = null;
                lock (_lock)
                {
                    _connecting.Remove(workdir);
                    if (_disposed || _scopedByWorkdir.ContainsKey(workdir))
                    {
                        drop = conns;
                    }
                    else
                    {
                        List<McpServerConnection> keep = new List<McpServerConnection>();
                        for (int i = 0; i < conns.Count; i++)
                        {
                            // A spec disabled while this connect was in flight (SetBuiltInServerEnabled
                            // ran between our spec snapshot and this publish) must not be published —
                            // that would orphan a live instance the disable pass couldn't see, exposing
                            // its tools to a conversation the flip meant to hide them from.
                            if (SpecDisabledLocked(conns[i].Name))
                            {
                                if (drop == null) drop = new List<McpServerConnection>();
                                drop.Add(conns[i]);
                                continue;
                            }
                            _registry.AddConnection(conns[i], workdir);
                            keep.Add(conns[i]);
                        }
                        _scopedByWorkdir[workdir] = keep;
                    }
                }
                if (drop != null)
                    for (int i = 0; i < drop.Count; i++) Teardown(drop[i], true);
                reservation.Set();
            }
        }

        // Enable or disable ONE named workdir-scoped built-in server at runtime, without touching any
        // other server or the registry identity. Used by the skills-enablement refresh: the extensions
        // server follows "any enabled skill" per conversation, and flipping it used to run a full host
        // rebuild — which swaps the registry object out from under an in-flight turn (the running
        // orchestrator/dispatcher/children keep the old, emptied registry and lose every tool: the
        // "cd-only sub-agent" doom loop). Enabling connects the server's eager workdir-less instance
        // (if the spec runs one) plus one instance per live workdir — including workdirs whose scoped
        // set is CONNECTING right now (their ConnectScoped snapshot may have seen the spec disabled;
        // we wait for the publish, then top the set up). Disabling tears down just that server's
        // instances; a mid-connect instance of a disabled spec is discarded by ConnectScoped's own
        // publish step (SpecDisabledLocked). Returns false when nothing could be committed (disposed,
        // not started, unknown spec) so the caller can retract optimistic state; true when the spec
        // was flipped (or already matched). Blocking (spawns/handshakes) — call off the UI thread.
        public bool SetBuiltInServerEnabled(string name, bool enabled)
        {
            if (string.IsNullOrEmpty(name)) return false;

            McpServerSpec spec = null;
            List<McpServerConnection> toClose = null;
            List<string> ensureDirs = null;
            List<string> pendingDirs = null;
            List<ManualResetEvent> pendingEvents = null;
            bool ensureEager = false;
            lock (_lock)
            {
                if (_disposed || !_started) return false;
                for (int i = 0; i < _scopedSpecs.Count; i++)
                {
                    McpServerSpec s = _scopedSpecs[i];
                    if (s != null && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                    { spec = s; break; }
                }
                if (spec == null) return false;
                if (spec.Enabled == enabled) return true;
                spec.Enabled = enabled; // future ConnectScoped calls follow the new state

                if (!enabled)
                {
                    // Collect this server's live instances (eager + every workdir) for teardown below.
                    toClose = new List<McpServerConnection>();
                    for (int i = _eager.Count - 1; i >= 0; i--)
                        if (string.Equals(_eager[i].Name, name, StringComparison.OrdinalIgnoreCase))
                        { toClose.Add(_eager[i]); _eager.RemoveAt(i); }
                    foreach (List<McpServerConnection> conns in _scopedByWorkdir.Values)
                        for (int i = conns.Count - 1; i >= 0; i--)
                            if (string.Equals(conns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                            { toClose.Add(conns[i]); conns.RemoveAt(i); }
                }
                else
                {
                    ensureEager = spec.RunsWithoutWorkdir;
                    ensureDirs = new List<string>(_scopedByWorkdir.Keys);
                    foreach (KeyValuePair<string, ManualResetEvent> kv in _connecting)
                    {
                        if (pendingDirs == null)
                        {
                            pendingDirs = new List<string>();
                            pendingEvents = new List<ManualResetEvent>();
                        }
                        pendingDirs.Add(kv.Key);
                        pendingEvents.Add(kv.Value);
                    }
                }
            }

            if (toClose != null)
                for (int i = 0; i < toClose.Count; i++) Teardown(toClose[i]);

            if (enabled)
            {
                if (ensureEager) EnsureInstance(spec, null);
                for (int i = 0; i < ensureDirs.Count; i++) EnsureInstance(spec, ensureDirs[i]);
                if (pendingDirs != null)
                {
                    // Workdirs that were mid-connect when the flip landed: wait (unlocked) for their
                    // owner to publish, then top the published set up with this server.
                    for (int i = 0; i < pendingDirs.Count; i++)
                    {
                        pendingEvents[i].WaitOne();
                        EnsureInstance(spec, pendingDirs[i]);
                    }
                }
            }
            return true;
        }

        // Connect and publish one instance of `spec` for `workdir` (null = the eager, workdir-less
        // instance). Idempotent and race-safe: skips the spawn when an instance with the same name is
        // already present, the spec is disabled, the workdir's set is gone, or the workdir is a
        // scratch dir the spec isn't eligible for — and re-checks all of that under the lock before
        // publishing, discarding the freshly opened connection if anything changed meanwhile.
        private void EnsureInstance(McpServerSpec spec, string workdir)
        {
            lock (_lock)
            {
                if (_disposed || !spec.Enabled) return;
                List<McpServerConnection> have;
                if (workdir == null) have = _eager;
                else if (!_scopedByWorkdir.TryGetValue(workdir, out have)) return; // set gone
                if (HasNamed(have, spec.Name)) return;
                if (workdir != null && _scratchWorkdirs.ContainsKey(workdir) && !spec.RunsInScratch) return;
            }

            McpServerConnection conn = CreateAndOpen(spec, workdir);
            if (conn == null) return;
            bool discard;
            lock (_lock)
            {
                List<McpServerConnection> list;
                if (workdir == null) list = _eager;
                else if (!_scopedByWorkdir.TryGetValue(workdir, out list)) list = null;
                if (_disposed || !spec.Enabled || list == null || HasNamed(list, spec.Name))
                    discard = true;
                else
                {
                    _registry.AddConnection(conn, workdir);
                    list.Add(conn);
                    discard = false;
                }
            }
            if (discard) Teardown(conn, true);
        }

        private static bool HasNamed(List<McpServerConnection> conns, string name)
        {
            if (conns == null) return false;
            for (int i = 0; i < conns.Count; i++)
                if (string.Equals(conns[i].Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // True when a scoped spec with this name exists and is currently disabled. Caller holds _lock.
        private bool SpecDisabledLocked(string name)
        {
            for (int i = 0; i < _scopedSpecs.Count; i++)
            {
                McpServerSpec s = _scopedSpecs[i];
                if (s != null && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                    return !s.Enabled;
            }
            return false;
        }

        // Tear down the scoped servers for a single working directory (e.g. its last tab closed).
        public void ReleaseWorkingDir(string workdir)
        {
            if (string.IsNullOrEmpty(workdir)) return;
            lock (_lock)
            {
                _pendingWorkdirs.Remove(workdir);
                _scratchWorkdirs.Remove(workdir);
                List<McpServerConnection> conns;
                if (_scopedByWorkdir.TryGetValue(workdir, out conns))
                {
                    for (int i = 0; i < conns.Count; i++) Teardown(conns[i]);
                    _scopedByWorkdir.Remove(workdir);
                }
            }
        }

        // Keep only the scoped server sets whose working directory is still in `keep`; tear down the
        // rest. Called when the set of open tabs (and thus referenced folders) changes, so processes
        // for closed conversations don't linger.
        public void RetainOnly(IEnumerable<string> keep)
        {
            var keepSet = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (keep != null)
                foreach (string k in keep)
                    if (!string.IsNullOrEmpty(k)) keepSet[k] = true;

            lock (_lock)
            {
                if (_disposed) return;
                for (int i = _pendingWorkdirs.Count - 1; i >= 0; i--)
                    if (!keepSet.ContainsKey(_pendingWorkdirs[i])) _pendingWorkdirs.RemoveAt(i);

                // Forget scratch markers for folders no longer referenced by an open tab (their scoped
                // set is torn down below); a re-ensure later re-marks them.
                List<string> dropScratch = null;
                foreach (string wd in _scratchWorkdirs.Keys)
                    if (!keepSet.ContainsKey(wd)) (dropScratch ?? (dropScratch = new List<string>())).Add(wd);
                if (dropScratch != null)
                    for (int i = 0; i < dropScratch.Count; i++) _scratchWorkdirs.Remove(dropScratch[i]);

                List<string> drop = null;
                foreach (string wd in _scopedByWorkdir.Keys)
                    if (!keepSet.ContainsKey(wd)) { (drop ?? (drop = new List<string>())).Add(wd); }
                if (drop != null)
                {
                    for (int i = 0; i < drop.Count; i++)
                    {
                        List<McpServerConnection> conns = _scopedByWorkdir[drop[i]];
                        for (int j = 0; j < conns.Count; j++) Teardown(conns[j]);
                        _scopedByWorkdir.Remove(drop[i]);
                    }
                }
            }
        }

        // Create + Open a single connection WITHOUT holding _lock — this is the blocking part (process
        // spawn + initialize/tools-list handshake). Returns a Ready connection (not yet registered or
        // published) or null on failure, cleaning up on the way out. The caller publishes the result
        // into the collections + registry under _lock.
        private McpServerConnection CreateAndOpen(McpServerSpec spec, string workdir)
        {
            McpServerConnection conn;
            try { conn = _connector.Create(spec, workdir); }
            catch (Exception ex)
            {
                _log.Log("mcp", "connector failed for '" + spec.Name + "': " + ex.Message);
                return null;
            }
            if (conn == null) return null;

            // Wire lifecycle → registry before Open so a fault during/after open is handled.
            conn.ToolsChanged += OnToolsChanged;
            conn.StateChanged += OnStateChanged;

            try
            {
                if (conn.State == ConnectionState.Created) conn.Open(_openTimeoutMs);
            }
            catch (Exception ex)
            {
                _log.Log("mcp", "open failed for '" + spec.Name + "': " + ex.Message);
                Teardown(conn);
                return null;
            }

            if (conn.State == ConnectionState.Ready)
            {
                _log.Log("mcp", "server '" + spec.Name + "' ready"
                    + (string.IsNullOrEmpty(workdir) ? " (eager)" : " (workdir=" + workdir + ")"));
                return conn;
            }

            _log.Log("mcp", "server '" + spec.Name + "' not Ready after open (state=" + conn.State + ").");
            Teardown(conn);
            return null;
        }

        private void OnToolsChanged(object sender, EventArgs e)
        {
            McpServerConnection conn = sender as McpServerConnection;
            if (conn != null) _registry.RefreshConnection(conn);
        }

        private void OnStateChanged(object sender, ConnectionStateEventArgs e)
        {
            if (e == null) return;
            if (e.NewState == ConnectionState.Faulted || e.NewState == ConnectionState.Closed)
            {
                McpServerConnection conn = sender as McpServerConnection;
                if (conn == null) return;
                _registry.RemoveConnection(conn);
                // Also forget the dead connection in the host's OWN collections. Leaving it in
                // _scopedByWorkdir made EnsureWorkingDir a permanent no-op for that folder ("already
                // connected" by key) even though the registry had dropped its tools — reconnection
                // was impossible until an app restart. When a workdir's LAST connection dies, its key
                // goes too, so the next EnsureWorkingDir relaunches the full set. Taking _lock here
                // is safe: every deliberate Teardown unsubscribes this handler before shutting the
                // connection down, so no caller already holding _lock can re-enter through it.
                lock (_lock)
                {
                    if (_disposed) return;
                    _eager.Remove(conn);
                    List<string> emptied = null;
                    foreach (KeyValuePair<string, List<McpServerConnection>> kv in _scopedByWorkdir)
                    {
                        if (kv.Value.Remove(conn) && kv.Value.Count == 0)
                            (emptied ?? (emptied = new List<string>())).Add(kv.Key);
                    }
                    if (emptied != null)
                        for (int i = 0; i < emptied.Count; i++) _scopedByWorkdir.Remove(emptied[i]);
                }
            }
        }

        // Unsubscribe (so Dispose's Closed event doesn't re-enter), drop from the registry, dispose.
        // Graceful path: gives each child the stdin-EOF grace window. Runtime tab-close teardowns
        // (ReleaseWorkingDir/RetainOnly) use this.
        private void Teardown(McpServerConnection conn)
        {
            Teardown(conn, false);
        }

        // forceful=true tears the connection down for speed (kill child immediately, skip the HTTP
        // session DELETE) — for application/host shutdown.
        private void Teardown(McpServerConnection conn, bool forceful)
        {
            if (conn == null) return;
            try { conn.ToolsChanged -= OnToolsChanged; }
            catch { }
            try { conn.StateChanged -= OnStateChanged; }
            catch { }
            _registry.RemoveConnection(conn);
            try { conn.Shutdown(forceful); }
            catch { }
        }

        public void Dispose()
        {
            // Snapshot every connection under the lock, then tear them down OUTSIDE it so a slow
            // teardown can't block other host callers on the lock.
            List<McpServerConnection> all = new List<McpServerConnection>();
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (List<McpServerConnection> conns in _scopedByWorkdir.Values)
                    all.AddRange(conns);
                _scopedByWorkdir.Clear();
                _pendingWorkdirs.Clear();
                _scratchWorkdirs.Clear();
                all.AddRange(_eager);
                _eager.Clear();
            }

            // Forceful (kill-now) teardown: each child exits in ~1ms whether it's killed or sees its
            // stdin close, so a simple sequential loop is plenty fast and avoids thread-pool overhead.
            for (int i = 0; i < all.Count; i++) Teardown(all[i], true);
        }
    }
}
