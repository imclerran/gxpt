using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mcp35.Core.Diagnostics;
using Newtonsoft.Json.Linq;

namespace GxPT
{
    // The host-synthesized dispatch_agent meta-tool (phase 4): runs a sub-agent in an isolated child
    // McpChatOrchestrator and returns only its final answer (design A1-A3). Like open_skill, it is handled
    // in the orchestrator's ExecuteCall without an MCP round-trip. Phase 4 runs a batch SERIALLY (parallel
    // fan-out is phase 7). Each child gets a fresh context (the standing agent guidance + workspace block
    // come from the child orchestrator's own stable head; the agent's body is added as a system message,
    // then the task), a tool set restricted to allowlist-intersect-parent-intersect-max_tier
    // (AgentToolResolver, via HiddenToolNames), the parent's shared approval policy, and NO dispatcher - so
    // a sub-agent cannot itself dispatch (no nesting, A12). XP / .NET 3.5 friendly.
    internal sealed class AgentDispatcher
    {
        public const string DispatchAgentName = "dispatch_agent";

        // A sane bound on one batch, so a single tool call can't fan out unboundedly. internal so the
        // activity panel can size its safety ceiling to the same maximum.
        internal const int MaxAgentsPerCall = 8;

        // Max children running at once in a read-only fan-out (design: bounded concurrency). Tunable.
        private const int MaxParallelAgents = 3;

        private readonly Dictionary<string, Agent> _bySlug;
        private readonly IChatStreamer _streamer;
        private readonly McpToolRegistry _registry;
        private readonly IToolApprovalPolicy _approval;
        private readonly string _parentModel;
        private readonly string _workingDir;
        private readonly ILogSink _log;
        private readonly Func<string, ToolTier> _tierOf;
        private readonly int _defaultMaxIterations;
        private readonly int _callTimeoutMs;

        // Optional passthrough to the parent turn (set by the host): child usage aggregates onto the parent
        // conversation, and the parent's cancellation handle lets a Stop cancel an in-flight child.
        public Action<ResponseUsage> UsageReported { get; set; }
        public RequestCancellation Cancellation { get; set; }

        // Maps a model effort (low/medium/high) to the model id the user configured for it in settings, or
        // null/empty when that tier is unset. Set by the host from AppSettings; null here (e.g. in tests)
        // means effort never resolves and the model/parent fallbacks apply. See ResolveModel.
        public Func<AgentEffort, string> EffortModel { get; set; }

        // The parent turn's privacy settings, propagated to every child request (set by the host). A
        // sub-agent runs in a fresh orchestrator that would otherwise default to no ZDR / provider
        // default - so without this, a child would send the conversation's data to a non-ZDR endpoint
        // even when the user enabled Zero Data Retention. Inheriting the parent's settings keeps the
        // privacy guarantee across the firewall. Null leaves the provider default (the headless default).
        public bool? Zdr { get; set; }
        public bool? ProviderDataCollectionAllowed { get; set; }

        // Optional observability hooks (design sec.14): the dispatcher reports fan-out / per-child lifecycle
        // so the host can show the activity panel and relabel the Stop button. Null => headless.
        public IAgentActivityUi ActivityUi { get; set; }

        // The per-slot child transcripts from the most recent Dispatch (tier 3). Set at the end of each
        // fan-out; the host snapshots it under the dispatch record's key for the read-only viewer. Indexed
        // by entry slot (aligned with the record body); a slot is null if that agent did not run a child.
        public AgentTranscript[] LastTranscripts { get; private set; }

        // Per-row live broadcasters for the in-flight fan-out (tier 3 "watch live"). Created per child,
        // dropped when the fan-out ends. Guarded by _liveLock: children register on worker threads while the
        // UI thread looks one up to open the streaming viewer.
        private readonly object _liveLock = new object();
        private Dictionary<int, AgentLiveStream> _liveStreams;

        // The live stream for a panel row, or null if the fan-out ended or that row isn't running.
        public AgentLiveStream GetLiveStream(int row)
        {
            lock (_liveLock)
            {
                AgentLiveStream s;
                if (_liveStreams != null && _liveStreams.TryGetValue(row, out s)) return s;
                return null;
            }
        }

        // Optional group cancellation: when set, children use THIS handle instead of the parent turn's
        // (Cancellation), so a "Stop N agents" click can cancel the fan-out without ending the turn. Null
        // => children fall back to Cancellation (parent Stop cancels them, the phase-4 behavior).
        public RequestCancellation GroupCancellation { get; set; }

        public AgentDispatcher(IList<Agent> agents, IChatStreamer streamer, McpToolRegistry registry,
                               IToolApprovalPolicy approval, string parentModel, string workingDir,
                               ILogSink log, Func<string, ToolTier> tierOf,
                               int defaultMaxIterations, int callTimeoutMs)
        {
            if (streamer == null) throw new ArgumentNullException("streamer");
            _bySlug = new Dictionary<string, Agent>(StringComparer.OrdinalIgnoreCase);
            if (agents != null)
            {
                for (int i = 0; i < agents.Count; i++)
                {
                    Agent a = agents[i];
                    if (a != null && !string.IsNullOrEmpty(a.Slug)) _bySlug[a.Slug] = a;
                }
            }
            _streamer = streamer;
            _registry = registry;
            _approval = approval;
            _parentModel = parentModel;
            _workingDir = workingDir;
            _log = log != null ? log : NullLogSink.Instance;
            _tierOf = tierOf;
            _defaultMaxIterations = defaultMaxIterations > 0
                ? defaultMaxIterations : McpChatOrchestrator.DefaultMaxIterations;
            _callTimeoutMs = callTimeoutMs > 0 ? callTimeoutMs : McpChatOrchestrator.DefaultCallTimeoutMs;
        }

        public bool HasAgents { get { return _bySlug.Count > 0; } }

        public bool IsDispatchAgent(string functionName) { return functionName == DispatchAgentName; }

        // The OpenAI-style function definition: dispatch_agent({ agents: [{ name, task, effort?, model? }] }).
        public JObject DispatchAgentDef()
        {
            JObject nameP = new JObject(); nameP["type"] = "string";
            JObject taskP = new JObject(); taskP["type"] = "string";
            JObject effortP = new JObject();
            effortP["type"] = "string";
            effortP["enum"] = new JArray("low", "medium", "high");
            effortP["description"] = "Optional override for the effort level (low | medium | high) this "
                + "agent runs at. Each agent already has a tuned default, so normally omit this and let it "
                + "run at its configured effort. Set it only when the user or a skill explicitly asks for a "
                + "different level.";
            JObject modelP = new JObject();
            modelP["type"] = "string";
            modelP["description"] = "Optional explicit model id, in OpenRouter format "
                + "\"model-author/model-name\" (e.g. \"z-ai/glm-5.2\"). Prefer \"effort\" unless you need a "
                + "specific model. Takes precedence over \"effort\" and the agent's frontmatter for this "
                + "dispatch only. Omit to use effort or the agent's own model.";
            JObject entryProps = new JObject();
            entryProps["name"] = nameP;
            entryProps["task"] = taskP;
            entryProps["effort"] = effortP;
            entryProps["model"] = modelP;
            JObject entrySchema = new JObject();
            entrySchema["type"] = "object";
            entrySchema["properties"] = entryProps;
            entrySchema["required"] = new JArray("name", "task");

            JObject agentsArr = new JObject();
            agentsArr["type"] = "array";
            agentsArr["items"] = entrySchema;

            JObject props = new JObject();
            props["agents"] = agentsArr;
            JObject schema = new JObject();
            schema["type"] = "object";
            schema["properties"] = props;
            schema["required"] = new JArray("agents");

            JObject fn = new JObject();
            fn["name"] = DispatchAgentName;
            fn["description"] = "Delegate one or more self-contained sub-tasks to specialist agents that "
                + "work in isolation and report back. Pass each agent's slug (from the agents list) and a "
                + "complete task description - the agent does not see this conversation, only the task you "
                + "give it. It returns a written result.";
            fn["parameters"] = schema;

            JObject def = new JObject();
            def["type"] = "function";
            def["function"] = fn;
            return def;
        }

        // Runs the requested agents (serially in phase 4) and returns the aggregated result - one labeled
        // section per agent. Unknown slugs become a short note rather than an error, so a partly-wrong
        // batch still returns the rest (the open_skill tolerance). Never throws to the caller.
        public string Dispatch(string argumentsJson)
        {
            List<string[]> entries = ParseEntries(argumentsJson);   // each = { name, task, model, effort }
            if (entries.Count == 0) return "No agents specified to dispatch.";

            int n = entries.Count;
            bool truncated = n > MaxAgentsPerCall;
            if (truncated) n = MaxAgentsPerCall;

            // Resolve each entry up front into a per-slot result (unknown slug / missing task are filled
            // immediately) and a list of slots that will actually run a child, in order.
            string[] names = new string[n];
            Agent[] agents = new Agent[n];
            string[] tasks = new string[n];
            // Optional per-call overrides (the dispatch_agent `model` / `effort` args). Null => fall back to
            // the agent's own frontmatter (model, then effort), then the parent model. See ResolveModel.
            string[] modelOverrides = new string[n];
            string[] effortOverrides = new string[n];
            string[] results = new string[n];
            // Per-slot child transcripts (tier 3): slot i is null until/unless that agent runs a child.
            // Exposed via LastTranscripts so the host can cache them under the dispatch record's key and
            // open a read-only viewer. Indexed by slot (entry order), aligned with the record body's rows.
            AgentTranscript[] transcripts = new AgentTranscript[n];
            List<int> runnable = new List<int>();
            for (int i = 0; i < n; i++)
            {
                names[i] = entries[i][0];
                tasks[i] = entries[i][1];
                modelOverrides[i] = entries[i][2];
                effortOverrides[i] = entries[i][3];
                Agent agent;
                if (names[i] == null || !_bySlug.TryGetValue(names[i], out agent))
                    results[i] = "Unknown agent: " + (names[i] != null ? names[i] : "(null)");
                else if (string.IsNullOrEmpty(tasks[i]))
                    results[i] = "No task was provided for this agent.";
                else { agents[i] = agent; runnable.Add(i); }
            }

            // Observability: announce the fan-out so the host can show the panel + relabel Stop. Paired
            // with OnFanOutEnd in finally so the button always reverts, even on an unexpected throw.
            IAgentActivityUi ui = ActivityUi;
            if (ui != null && runnable.Count > 0)
            {
                List<string> slugs = new List<string>(runnable.Count);
                List<string> taskList = new List<string>(runnable.Count);
                List<string> modelList = new List<string>(runnable.Count);
                for (int k = 0; k < runnable.Count; k++)
                {
                    int slot = runnable[k];
                    Agent a = agents[slot];
                    slugs.Add(a.Slug);
                    taskList.Add(tasks[slot]);
                    modelList.Add(ResolveModel(modelOverrides[slot], effortOverrides[slot], a));
                }
                ui.OnFanOutStart(slugs, taskList, modelList);
            }
            // Fresh live-stream table for this fan-out (tier 3 "watch live"); the panel looks streams up by
            // row while the children run, and it is dropped when the fan-out ends.
            lock (_liveLock) { _liveStreams = new Dictionary<int, AgentLiveStream>(); }
            try
            {
                // Read-only batches run concurrently (the win is overlapping LLM streams); a batch with any
                // write-capable agent runs serially (design A9 - the chosen "reads parallel, writes serial"
                // rule). Concurrent children safely share the MCP connections (the transport is multiplexed:
                // atomic request ids + serialized writes) and the streamer (per-call), so no extra locking.
                if (RunsInParallel(agents, runnable))
                    RunParallel(agents, tasks, modelOverrides, effortOverrides, runnable, results, transcripts);
                else
                    for (int k = 0; k < runnable.Count; k++)
                    {
                        int i = runnable[k];
                        results[i] = RunChildReported(k, i, agents[i], tasks[i], modelOverrides[i],
                                                      effortOverrides[i], transcripts);
                    }
            }
            finally
            {
                LastTranscripts = transcripts;
                lock (_liveLock) { _liveStreams = null; }   // streams are only live during the fan-out
                if (ui != null && runnable.Count > 0) ui.OnFanOutEnd();
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append("## Agent: ").Append(names[i] != null ? names[i] : "(null)").Append('\n')
                  .Append(results[i]);
            }
            if (truncated)
                sb.Append("\n\n[Note: only the first ").Append(MaxAgentsPerCall)
                  .Append(" agents in this call were dispatched.]");

            // If the user stopped the fan-out (panel "Stop agents" trips GroupCancellation), the sections
            // above are partial. Steer the model to wrap up rather than silently retry: summarize what was
            // gathered and ask how to proceed (the design's tailored stop directive, sec.14).
            if (GroupCancellation != null && GroupCancellation.IsCancelled)
                sb.Append("\n\n[The user deliberately stopped the sub-agents, so the results above may be ")
                  .Append("partial or empty. Do NOT continue the task yourself, do NOT call any more tools, ")
                  .Append("and do NOT re-dispatch the agents. In your next message, briefly report what (if ")
                  .Append("anything) the agents gathered above, then ask the user how they would like to ")
                  .Append("proceed (for example: retry, narrow the scope, or take a different approach), and ")
                  .Append("wait for their reply.]");

            return sb.ToString();
        }

        // A batch runs in parallel iff there is more than one runnable agent and every one of them is
        // read-only (max_tier ReadOnly) - so none can mutate the workspace (design A9). Any write-capable
        // agent forces the whole batch serial.
        internal static bool RunsInParallel(Agent[] agents, List<int> runnable)
        {
            if (agents == null || runnable == null || runnable.Count < 2) return false;
            for (int k = 0; k < runnable.Count; k++)
            {
                Agent a = agents[runnable[k]];
                if (a == null || a.MaxTier != AgentMaxTier.ReadOnly) return false;
            }
            return true;
        }

        // Runs the runnable slots concurrently with a sliding concurrency window of at most
        // MaxParallelAgents: a small pool of worker threads pulls the next row from a shared cursor, so the
        // moment one child finishes that worker starts the next-in-order child (no wave barrier - a slow
        // child no longer holds back the rest of its group). Interlocked.Increment hands out row indices in
        // strict order, so the lowest-numbered queued row is always the next to start (FIFO start order, so
        // the panel never shows a later row running while an earlier one is still queued). Each child writes
        // its own result slot. WaitHandle.WaitAll runs on the parent turn's ThreadPool (MTA) worker, so it
        // is valid here; no lock is held across the join, so the fan-out cannot deadlock. At most
        // MaxParallelAgents worker handles are joined, well under WaitAll's 64-handle limit.
        private void RunParallel(Agent[] agents, string[] tasks, string[] modelOverrides,
                                 string[] effortOverrides, List<int> runnable,
                                 string[] results, AgentTranscript[] transcripts)
        {
            int count = runnable.Count;
            int workerCount = Math.Min(MaxParallelAgents, count);
            int next = -1;   // shared cursor; Interlocked.Increment yields 0,1,2,... in order across workers
            System.Threading.ManualResetEvent[] dones = new System.Threading.ManualResetEvent[workerCount];
            try
            {
                for (int w = 0; w < workerCount; w++)
                {
                    System.Threading.ManualResetEvent done = new System.Threading.ManualResetEvent(false);
                    dones[w] = done;
                    System.Threading.ThreadPool.QueueUserWorkItem(delegate
                    {
                        // done.Set() is the outermost finally so WaitAll can never hang, even on an
                        // unexpected throw. Each worker loops, claiming the next row until the batch is drained.
                        try
                        {
                            int row;
                            while ((row = System.Threading.Interlocked.Increment(ref next)) < count)
                            {
                                int slot = runnable[row];   // entry slot (position in the agents/tasks arrays)
                                Agent agent = agents[slot];
                                string task = tasks[slot];
                                string modelOverride = modelOverrides[slot];
                                string effortOverride = effortOverrides[slot];
                                try { results[slot] = RunChildReported(row, slot, agent, task, modelOverride, effortOverride, transcripts); }
                                catch (Exception ex) { results[slot] = "[agent error: " + ex.Message + "]"; }
                            }
                        }
                        finally { done.Set(); }
                    });
                }
                System.Threading.WaitHandle.WaitAll(dones);
            }
            finally
            {
                for (int w = 0; w < workerCount; w++) if (dones[w] != null) dones[w].Close();
            }
        }

        // Wraps RunChild with the activity-UI start/finished hooks (called from both the serial and the
        // parallel paths). Safe to call concurrently: a host implementation marshals to the UI thread.
        // `row` is the panel-row index (position among runnable, used for all UI callbacks); `slot` is the
        // entry index (used for the result/transcript arrays). A per-child forwarding UI reports the
        // child's tool calls as the row's live activity line (tier 2); the run's full message list is
        // captured into transcripts[slot] for the tier-3 viewer (even on error - the partial history).
        private string RunChildReported(int row, int slot, Agent agent, string task, string modelOverride,
                                        string effortOverride, AgentTranscript[] transcripts)
        {
            IAgentActivityUi ui = ActivityUi;
            if (ui != null) ui.OnAgentStart(row, agent.Slug, task);
            IToolLoopUi childUi;
            if (ui != null)
            {
                // The live broadcaster doubles as the child's tool-loop UI: it feeds the panel's count line
                // and records events for a viewer that attaches mid-run. Registered by row so the panel's
                // "View transcript" can find it. The persona (system prompt) is passed so the live viewer can
                // show it up front, the same way the static viewer does (RunChild reads it again for the
                // child's actual system message - both are the agent body, read at the same moment).
                AgentLiveStream stream = new AgentLiveStream(ui, row, agent.Slug, task, ReadBody(agent));
                lock (_liveLock) { if (_liveStreams != null) _liveStreams[row] = stream; }
                childUi = stream;
            }
            else childUi = NullToolLoopUi.Instance;
            try
            {
                IList<ChatMessage> history;
                string answer = RunChild(agent, task, modelOverride, effortOverride, childUi, out history);
                if (transcripts != null) transcripts[slot] = new AgentTranscript(agent.Slug, task, history);
                return answer;
            }
            finally
            {
                bool cancelled = GroupCancellation != null && GroupCancellation.IsCancelled;
                if (ui != null) ui.OnAgentFinished(row, agent.Slug, cancelled);
            }
        }

        // Builds and runs one child orchestrator to completion, returning its final answer. `history` is set
        // (before the run) to the child's message list so the caller gets the full transcript even if the
        // turn throws. `childUi` receives the child's tool activity (forwarded to the row's activity line).
        private string RunChild(Agent agent, string task, string modelOverride, string effortOverride,
                                IToolLoopUi childUi, out IList<ChatMessage> history)
        {
            string model = ResolveModel(modelOverride, effortOverride, agent);
            int maxIter = agent.MaxTurns > 0 ? agent.MaxTurns : _defaultMaxIterations;

            McpChatOrchestrator child = new McpChatOrchestrator(_streamer, _registry, _approval, model,
                                                                _log, maxIter, _callTimeoutMs);
            child.WorkingDir = _workingDir;
            // CurrentDir deliberately not inherited: a child runs at the anchor, and its own ephemeral
            // current-directory line says so - the parent's `cd` scoping is per-conversation state.
            // A "Stop N agents" click trips GroupCancellation (cancels the fan-out, not the turn); when the
            // host hasn't set one, fall back to the parent turn's handle so a plain Stop still cancels.
            child.Cancellation = GroupCancellation != null ? GroupCancellation : Cancellation;
            child.UsageReported = UsageReported;
            // Inherit the parent turn's privacy posture: a ZDR conversation must stay ZDR inside its
            // sub-agents (A3's firewall isolates the transcript, not the data-handling guarantee). If
            // the child's model has no ZDR-capable endpoint the request fails rather than silently
            // downgrading - the correct, safe failure for a retention guarantee.
            child.Zdr = Zdr;
            child.ProviderDataCollectionAllowed = ProviderDataCollectionAllowed;

            // Restrict the child to the agent's effective tool set by hiding everything else the parent can
            // call (no escalation, A11). Not setting an AgentDispatcher on the child means it has no
            // dispatch_agent and cannot nest (A12).
            if (_registry != null)
            {
                IList<string> parentNames = _registry.NamesForWorkdir(_workingDir);
                List<string> hidden = AgentToolResolver.Hidden(agent.Tools, agent.MaxTier, parentNames, _tierOf);
                if (hidden.Count > 0) child.HiddenToolNames = hidden;

                // Pre-reveal the child's allowed tools (the parent catalog minus the hidden set). A sub-
                // agent's tool set is fixed by its frontmatter - there is nothing to progressively discover
                // - so pre-seeding RevealedToolNames spares it the reveal_tools round-trip the enforcement
                // gate (McpChatOrchestrator.ExecuteCall) would otherwise force on its first iteration, which
                // for a tight MaxTurns can cost the agent a whole turn before it reaches real work.
                List<string> allowed = new List<string>();
                for (int i = 0; i < parentNames.Count; i++)
                    if (!hidden.Contains(parentNames[i])) allowed.Add(parentNames[i]);

                // A child whose frontmatter promises SPECIFIC tools but whose effective set resolved to
                // nothing would burn its whole MaxTurns budget improvising with host tools alone (the
                // observed cd-only doom loop: 40 iterations of directory-walking with no way to work).
                // Fail the dispatch loudly instead — the parent can retry once the workspace servers
                // are back, or surface the misconfiguration to the user. A bare "*" allowlist (or none)
                // inherits "whatever the parent has", so an empty result there is consistent rather
                // than a broken promise, and such children still run (e.g. text-only agents).
                if (allowed.Count == 0 && DeclaresConcreteTools(agent.Tools))
                {
                    history = new List<ChatMessage>();
                    _log.Log("agents", "child '" + agent.Slug + "' not dispatched: its declared tool "
                        + "allowlist resolved to 0 available tools (workdir=" + (_workingDir ?? "") + ")");
                    return "[agent error: '" + agent.Slug + "' declares specific tools in its frontmatter, "
                        + "but none of them are currently available in this workspace (the workspace tool "
                        + "servers may be down or restarting). The agent was NOT dispatched — retry once "
                        + "tools are available.]";
                }
                child.RevealedToolNames = allowed;

                // Freeze the child's MCP defs NOW (A11: an allowlisted child is handed its tool defs
                // whole, no reveal_tools dance). Snapshotting the concrete defs — not just the names —
                // means the child's exposure no longer re-reads the registry each iteration, so registry
                // churn during a long parent turn can't strip a running child's tools out from under it.
                // Never freeze EMPTINESS, though: 0 defs at dispatch means the registry is empty or the
                // servers are mid-(re)connect, and pinning that would leave the child toolless for its
                // whole budget. Falling back to live derivation lets it recover as the registry refills
                // (the pre-seeded RevealedToolNames make its tools appear without a reveal round-trip).
                IList<JObject> frozen = _registry.FunctionDefsForNames(_workingDir, allowed);
                if (frozen.Count > 0) child.FrozenToolDefs = frozen;
                else if (allowed.Count > 0)
                    _log.Log("agents", "child '" + agent.Slug + "' resolved 0 tool defs from " + allowed.Count
                        + " allowed name(s) (workdir=" + (_workingDir ?? "") + ") - registry empty or servers"
                        + " down? Falling back to live derivation.");
            }

            List<ChatMessage> msgs = new List<ChatMessage>();
            history = msgs;                                      // assigned up front so a throw still hands back the partial transcript
            string body = ReadBody(agent);                       // fresh read, so SKILL/AGENT edits apply
            if (!string.IsNullOrEmpty(body))
                msgs.Add(new ChatMessage("system", body));       // the agent's persona, after the standing head
            msgs.Add(new ChatMessage("user", task));

            try
            {
                child.RunTurn(msgs, childUi != null ? childUi : NullToolLoopUi.Instance);
            }
            catch (Exception ex)
            {
                _log.Log("agents", "child '" + agent.Slug + "' threw: " + ex.Message);
                return "[agent error: " + ex.Message + "]";
            }
            return ExtractFinalAnswer(msgs);
        }

        // True when the frontmatter allowlist names at least one CONCRETE tool pattern ("files__*",
        // "git__status") — i.e. the agent definition promises specific tools. A bare "*" only says
        // "whatever the parent has", so an empty effective set is consistent with it, not a broken
        // promise.
        private static bool DeclaresConcreteTools(string[] tools)
        {
            if (tools == null) return false;
            for (int i = 0; i < tools.Length; i++)
            {
                string t = tools[i];
                if (!string.IsNullOrEmpty(t) && t.Trim() != "*") return true;
            }
            return false;
        }

        // Resolves the model a child runs under, in decreasing precedence (design A21). Dispatch-level args
        // beat the agent's own frontmatter, and an explicit model beats an effort tier within each level:
        //   1. dispatch `model` arg            (caller named an exact id for this call)
        //   2. dispatch `effort` arg           (caller asked for a tier; mapped via settings)
        //   3. frontmatter `model`             (the agent's own pinned model)
        //   4. frontmatter `effort`            (the agent's own tier; mapped via settings)
        //   5. the parent turn's model         (the default)
        // An effort rung is skipped when it maps to nothing (no EffortModel host hook, or that tier is blank
        // in settings), so the chain always lands on a concrete model.
        private string ResolveModel(string modelOverride, string effortOverride, Agent agent)
        {
            if (!string.IsNullOrEmpty(modelOverride)) return modelOverride;

            AgentEffort overrideEffort;
            if (AgentFrontmatter.TryParseEffort(effortOverride, out overrideEffort))
            {
                string m = ModelForEffort(overrideEffort);
                if (!string.IsNullOrEmpty(m)) return m;
            }

            if (agent != null && !string.IsNullOrEmpty(agent.Model)) return agent.Model;

            if (agent != null)
            {
                string m = ModelForEffort(agent.Effort);
                if (!string.IsNullOrEmpty(m)) return m;
            }

            return _parentModel;
        }

        // The model the user configured for an effort tier, or null when there is no hint (Unset), no host
        // hook, or that tier is blank in settings. Never throws - a bad hook just falls through to the next
        // resolution rung.
        private string ModelForEffort(AgentEffort effort)
        {
            if (effort == AgentEffort.Unset) return null;
            Func<AgentEffort, string> f = EffortModel;
            if (f == null) return null;
            try
            {
                string s = f(effort);
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch { return null; }
        }

        // The child's final answer is the last assistant message its turn appended to history. Only the
        // final answer crosses back to the parent (context firewall, A3/A7); intermediate tool chatter
        // stays in the child's own message list.
        private static string ExtractFinalAnswer(IList<ChatMessage> history)
        {
            if (history != null)
            {
                for (int i = history.Count - 1; i >= 0; i--)
                {
                    ChatMessage m = history[i];
                    if (m != null && m.Role == "assistant" && !string.IsNullOrEmpty(m.Content))
                        return m.Content;
                }
            }
            return "(the agent produced no answer)";
        }

        private static string ReadBody(Agent agent)
        {
            if (agent == null || string.IsNullOrEmpty(agent.FilePath)) return string.Empty;
            try
            {
                string text = File.ReadAllText(agent.FilePath, Encoding.UTF8);
                return AgentFrontmatter.Parse(text).Body;
            }
            catch
            {
                return string.Empty;
            }
        }

        // Parses { agents: [ { name, task, model?, effort? }, ... ] } into a list of {name, task, model,
        // effort} tuples (model/effort null when omitted). Malformed input yields an empty list (the tool
        // then reports "no agents"), never an exception.
        private static List<string[]> ParseEntries(string argumentsJson)
        {
            List<string[]> list = new List<string[]>();
            try
            {
                if (string.IsNullOrEmpty(argumentsJson)) return list;
                JObject o = JObject.Parse(argumentsJson);
                JToken arr = o["agents"];
                if (arr != null && arr.Type == JTokenType.Array)
                {
                    foreach (JToken t in (JArray)arr)
                    {
                        if (t == null || t.Type != JTokenType.Object) continue;
                        JObject e = (JObject)t;
                        list.Add(new string[] { AsString(e["name"]), AsString(e["task"]),
                                                AsString(e["model"]), AsString(e["effort"]) });
                    }
                }
            }
            catch
            {
                // malformed args -> empty list
            }
            return list;
        }

        private static string AsString(JToken t)
        {
            return (t != null && t.Type == JTokenType.String) ? (string)t : null;
        }

        // A no-op transcript UI: used when there is no observability host (headless / tests). The answer is
        // read from history, not the UI.
        private sealed class NullToolLoopUi : IToolLoopUi
        {
            public static readonly NullToolLoopUi Instance = new NullToolLoopUi();
            public void AppendTextDelta(string text) { }
            public void OnToolCall(string functionName, string argumentsJson, string callId) { }
            public void OnToolResult(string functionName, string resultText, bool isError, string callId) { }
            public void OnError(string message) { }
            public void Complete() { }
        }
    }
}
