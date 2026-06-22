using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Mcp35.Client;
using Mcp35.Core.Diagnostics;
using Mcp35.Core.Errors;
using Mcp35.Core.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxPT
{
    // The host-side tool-call loop (phase 4): call the model, reassemble streamed tool_calls, run
    // them through the registry/approval/connection, feed results back as tool-role messages, and
    // re-call until the model produces a final answer (bounded by MaxIterations). Runs on a worker
    // thread; the UI callbacks marshal to the form. Approval is pluggable (phase-4 stub → phase-6
    // tiered policy) and called at the one right point.
    internal sealed class McpChatOrchestrator
    {
        public const int DefaultMaxIterations = 25;
        public const int DefaultCallTimeoutMs = 60000;
        // Reveal-set cap, enforced only on models whose provider has no prompt caching (see the
        // eviction note on RevealedToolNames). Matches the old registry-global LRU cap.
        public const int RevealEvictionCap = 24;

        // Tool-result content returned when the user denies a call. Lives here (this file is linked
        // into the test project) so the transcript renderer can recognize a denied call; McpMarkers
        // references it.
        internal const string DeniedResultText = "[Call denied by user.]";

        // Agentic behavior guidance, prepended as a system message on tool-enabled turns only
        // (this orchestrator runs solely when at least one tool is available). Kept short to
        // limit token cost. Reinforces five things: act through the tools, don't return a null/
        // evasive answer when a tool could resolve the question, keep working through multi-step
        // tasks instead of stopping to narrate while work remains, treat a denial as scoped to that
        // one call rather than a permanent ban, and don't volunteer a rundown of tools unasked.
        internal const string AgentSystemPrompt =
            "You are an AI assistant operating as an agent with access to tools. Use them "
            + "proactively to accomplish the user's request instead of asking the user to do what "
            + "you can do yourself, and instead of guessing when a tool could give you the answer.\n\n"
            + "Before saying you don't know or can't help, consider whether one of your tools could "
            + "answer the question - if so, use it. Do not return an empty or evasive response when "
            + "investigation is possible; make a genuine attempt with the tools available before "
            + "reporting that something cannot be done.\n\n"
            + "Work through multi-step tasks to completion before ending your turn. A turn with no "
            + "tool call hands control back to the user, so do not just announce what you are about "
            + "to do and then stop - when more work remains, make the next tool call in the same "
            + "turn as any narration, and never send an empty message. Reserve a reply that has no "
            + "tool call for when the task is genuinely finished or you need the user to decide "
            + "something. This is not a push to over-call: once the request is satisfied, give your "
            + "final answer and stop rather than making needless calls.\n\n"
            + "When a tool call is denied or cancelled, treat it as a refusal of that specific "
            + "call in that specific moment, not a permanent ban on the tool. You may try the same "
            + "tool again later with adjusted arguments or once the situation changes.\n\n"
            + "Do not list or describe your tools or capabilities unless the user asks. Reply to "
            + "greetings and casual messages naturally and briefly, and bring up what you can do "
            + "only when it is relevant to the user's request.\n\n"
            + "Some requests end with an extra user-role message marked [Ephemeral context ...]. "
            + "It is appended by the host application (workspace memory, skills, tool availability), "
            + "not written by the user; treat it as background information from the system.";

        // Per-turn workspace block, built from WorkingDir and injected as its own ephemeral system
        // message right after the agent prompt (only when a workspace is set). Kept separate from
        // AgentSystemPrompt because the path is dynamic; absent entirely when there is no workspace,
        // so a workspace-less turn leaves no trace of one in context. Tells the model where it is
        // running so questions about "the project/code/files" go to disk before the web.
        internal static string WorkspaceSystemMessage(string workingDir)
        {
            if (string.IsNullOrEmpty(workingDir)) return null;
            return "You are running in this workspace directory: `" + workingDir + "`. When the user "
                + "asks about files, code, or the project without naming an external source, they "
                + "mean this workspace - look here first.";
        }

        // Per-turn scratch-sandbox block, injected only when there is no workspace but the command
        // server is running in a per-conversation scratch directory (the opt-in scratch-command
        // feature). Tells the model it has a throwaway working directory for running commands (the
        // command server's CWD) and that it is NOT a project: there are no workspace files to read, and
        // it is deleted when the conversation is deleted or the app closes. Kept separate from the
        // workspace block so a scratch turn is never presented as a real workspace. Null/empty scratch
        // dir => no block, leaving a folderless turn with no trace of one.
        internal static string ScratchSystemMessage(string scratchDir)
        {
            if (string.IsNullOrEmpty(scratchDir)) return null;
            return "No workspace folder is set for this conversation. A temporary scratch directory has "
                + "been created at `" + scratchDir + "` and is the working directory for any commands "
                + "you run. It starts empty and is not a project - there are no workspace files to read - "
                + "and it is deleted when this conversation is deleted or the app closes. Use it only for "
                + "transient work.";
        }

        private readonly IChatStreamer _streamer;
        private readonly McpToolRegistry _registry;
        private readonly IToolApprovalPolicy _approval;
        private readonly string _model;
        private readonly ILogSink _log;
        private readonly int _maxIterations;
        private readonly int _callTimeoutMs;

        // The tools array offered on the most recent loop iteration. The cap wrap-up re-sends it
        // (with tool_choice "none") so the wrap-up request's prompt prefix stays byte-identical to
        // the loop's requests and re-reads the turn's prompt cache - omitting the array would change
        // position 0 of the prompt and re-bill the whole transcript on the turn's largest request.
        private IList<JObject> _lastOfferedTools;

        // Optional hook to transform history into the messages actually sent (e.g. inline file
        // attachments) without mutating the persisted history. Identity transform when null. Must
        // preserve assistant ToolCalls and tool-role ToolCallId, and must be byte-deterministic per
        // message: any nondeterminism (timestamps, fresh temp paths) silently zeroes the prompt-cache
        // hit rate for everything after the first differing byte.
        public Func<IList<ChatMessage>, IList<ChatMessage>> RequestMessageTransform { get; set; }

        // The conversation's revealed-tool list (server-qualified names), owned by the Conversation
        // and persisted with it. Per-conversation scoping keeps one tab's reveals out of another
        // tab's tools array; the registry validates each name against the live catalog at request
        // time, so stale names are skipped harmlessly. The list is kept in recency order (reveal and
        // call both bump a name to the end). Eviction is provider-gated at turn start: on prompt-
        // caching providers the set is append-only - evicting a def changes the tools array at
        // position 0 of the prompt and re-bills the whole transcript, which always costs more than
        // the few hundred cache-discounted tokens the stale def occupies - while on non-caching
        // providers the list is trimmed to RevealEvictionCap, oldest first, since stale defs are
        // pure per-turn cost there. Defaults to a turn-local list when the host doesn't supply one.
        public IList<string> RevealedToolNames { get; set; }

        // Optional project-instructions block (the workspace root's AGENTS.md, framed by
        // AgentsFileInjection), injected as the last stable-head system message (Zone A). A plain
        // string set per send rather than a provider: Zone A must stay byte-identical across the
        // turn's loop iterations, so the host reads the file once before the turn and an edit
        // (including one the model makes itself through file tools) takes effect on the NEXT turn,
        // re-billing the prefix once. Null/empty => no block, so a project without AGENTS.md
        // leaves no trace in context.
        public string ProjectInstructions { get; set; }

        // Optional provider of the persistent-memory system block (the current .gxpt/memory.md index
        // plus its framing), rebuilt from disk each request and injected as an ephemeral, non-persisted
        // system message - the same treatment as the names manifest. Returns null/empty when memory is
        // disabled or there is no workspace. All memory framing lives here (never in AgentSystemPrompt)
        // so a disabled memory system leaves no trace in context (design M5/M6).
        public Func<string> MemorySystemMessageProvider { get; set; }

        // Optional provider of the skills manifest system block (the always-on slug/description list plus
        // its framing), rebuilt each request and injected as an ephemeral system message ordered after
        // the memory block and before the MCP names manifest (design sec.5). Null/empty => no skills
        // block, so a skill-less conversation leaves no trace in context.
        public Func<string> SkillsManifestSystemMessageProvider { get; set; }

        // Optional provider of the agents manifest system block (the always-on slug/description list of
        // dispatchable sub-agents plus its framing), rebuilt each request and injected as an ephemeral
        // system message ordered after the skills block and before the MCP names manifest (design sec.5).
        // Gated by the single agents feature toggle (the host only sets it when agents are enabled), so a
        // conversation with agents off leaves no trace in context. Null/empty => no agents block.
        public Func<string> AgentsManifestSystemMessageProvider { get; set; }

        // Optional skills meta-tool surface (open_skill). When set and it has skills, open_skill is
        // exposed in the tools array and handled locally without an MCP round-trip, like reveal_tools.
        public SkillTools SkillTools { get; set; }

        // Optional sub-agent dispatch surface (dispatch_agent). When set and it has agents, dispatch_agent
        // is exposed in the tools array and handled locally (a child McpChatOrchestrator runs the agent and
        // its final answer is the tool result). The host only sets it when the agents feature is enabled.
        // A child never gets a dispatcher, so a sub-agent cannot dispatch (no nesting, A12).
        public AgentDispatcher AgentDispatcher { get; set; }

        // Server-qualified MCP tool names to omit from this turn's context (names manifest + exposed
        // defs) and refuse to call. Used to gate the skill-authoring tools on the meta-skill (SkillToolGate).
        // Set per send (the orchestrator is built fresh each turn), so it's not shared/racy.
        public ICollection<string> HiddenToolNames { get; set; }

        // Sticky provider routing (prompt caching): the provider endpoint that last DEMONSTRATED a
        // cache hit for this conversation (cached_tokens > 0 on its response), seeded by the host
        // from Conversation.CacheWarmProvider. Prompt caches live per provider, so on
        // cache-supported models each request emits provider.order = [PreferredProvider] - a
        // preference with fallback, not a pin. Confirmation-gated on purpose: merely serving a
        // request proves nothing (the endpoint may not cache at all - e.g. a third-party host of an
        // open-weights model - and pinning there would constrain load balancing for no benefit),
        // while cache activity proves it: a read (cached_tokens > 0) shows the warm cache lives
        // here, and a write (cache_write_tokens > 0) shows it was just created here - either
        // latches the preference, so explicit-caching providers stick from the conversation's very
        // first request. A later no-activity response from the confirmed provider does NOT clear
        // the preference - that is usually TTL expiry between turns, where keeping the rebuild on
        // one provider is exactly the point; the preference moves only when a different provider
        // demonstrates cache activity. Providers that report neither (implicit cachers whose writes
        // are silent) latch on their first observed hit instead. Ignored entirely on models without
        // prompt caching.
        public string PreferredProvider { get; set; }

        // Notifies the host when a provider demonstrates a cache hit, so it can persist the value
        // to the conversation for the next turn. Invoked from the worker thread.
        public Action<string> ProviderServed { get; set; }

        // Per-response usage/cost accounting (every model, every loop iteration), for the host to
        // accumulate on the conversation and surface in the UI. Invoked from the worker thread.
        public Action<ResponseUsage> UsageReported { get; set; }

        // Provider data-collection preference applied to every request in the turn. Null leaves
        // it unset (provider default).
        public bool? ProviderDataCollectionAllowed { get; set; }

        // Zero data retention for every request in this turn. When true, emits provider.zdr=true so
        // OpenRouter routes only to zero-retention endpoints. Null/false leaves routing unconstrained.
        public bool? Zdr { get; set; }

        // Called when a turn exhausts its iteration budget with tool calls still pending. The argument
        // is the number of model iterations completed so far. Return true to grant another full budget
        // (the user chose to keep going), false to wrap up. Null => wrap up. The host wires this to an
        // in-transcript confirmation similar to the tool-approval prompt; it blocks the turn until the
        // user answers, which is correct (the user is present).
        public Func<int, bool> ContinuationDecider { get; set; }

        // Per-turn cancellation handle for the in-flight model request (may be null). When the user
        // stops the turn, Cancel() kills the current model stream via the streamer; this loop also
        // reads IsCancelled to bail out cleanly between steps - the partial assistant text is kept and
        // the turn is Completed (not surfaced as an error). Graceful by design: a stop that lands while
        // a tool is executing lets that one call finish, then stops before the next model call.
        public RequestCancellation Cancellation { get; set; }

        // The working directory of the conversation running this turn. Resolution of workdir-scoped
        // tools (files/git/command) is routed to the server bound to THIS folder, so concurrent turns
        // in different tabs hit their own folders' servers. Null = no workspace (scoped tools won't
        // resolve); workdir-independent tools (web/github/custom) resolve regardless.
        public string WorkingDir { get; set; }

        // When there is no user WorkingDir, the per-conversation SCRATCH directory the command server
        // runs in (folderless conversations with the opt-in scratch-command feature). Used ONLY to
        // resolve and expose workdir-scoped tools (the command server) - never for the workspace prompt
        // block, which stays absent so a scratch turn is not presented as a real workspace. Instead a
        // distinct scratch-sandbox note (ScratchSystemMessage) tells the model about the temp dir. Null
        // when not applicable (a real workspace is set, or the feature is off).
        public string ScratchWorkingDir { get; set; }

        // The working directory used to resolve/expose workdir-scoped MCP tools for this turn: the user
        // workspace when set, else the scratch dir (command server only). Distinct from WorkingDir, which
        // drives the prompt's workspace block and is left null for a scratch turn.
        private string ResolutionWorkdir
        {
            get { return !string.IsNullOrEmpty(WorkingDir) ? WorkingDir : ScratchWorkingDir; }
        }

        public McpChatOrchestrator(IChatStreamer streamer, McpToolRegistry registry,
                                   IToolApprovalPolicy approval, string model, ILogSink log)
            : this(streamer, registry, approval, model, log, DefaultMaxIterations, DefaultCallTimeoutMs)
        {
        }

        public McpChatOrchestrator(IChatStreamer streamer, McpToolRegistry registry,
                                   IToolApprovalPolicy approval, string model, ILogSink log,
                                   int maxIterations, int callTimeoutMs)
        {
            if (streamer == null) throw new ArgumentNullException("streamer");
            _streamer = streamer;
            _registry = registry;
            _approval = approval != null ? approval : new AllowAllApprovalPolicy();
            _model = model;
            _log = log != null ? log : NullLogSink.Instance;
            _maxIterations = maxIterations > 0 ? maxIterations : DefaultMaxIterations;
            _callTimeoutMs = callTimeoutMs > 0 ? callTimeoutMs : DefaultCallTimeoutMs;
        }

        // Run one user turn to completion: appends the user message, then loops model<->tools,
        // mutating history in place (so the conversation keeps assistant tool_calls + tool results).
        public void RunTurn(IList<ChatMessage> history, string userText, IToolLoopUi ui)
        {
            if (history == null) throw new ArgumentNullException("history");
            history.Add(new ChatMessage("user", userText));
            RunTurn(history, ui);
        }

        // Same loop, but over a history whose last message is already the user's turn — the host's
        // chat path adds the user message to the conversation itself. history is mutated in place.
        public void RunTurn(IList<ChatMessage> history, IToolLoopUi ui)
        {
            if (history == null) throw new ArgumentNullException("history");

            // Short id so a turn's lines can be followed in the log even when tabs run concurrently
            // (the ThreadPool thread id is reused across turns and can't be relied on for this).
            string turnId = Guid.NewGuid().ToString("N").Substring(0, 6);
            _log.Log("mcp", "[turn " + turnId + "] start: model=" + _model + ", history=" + history.Count
                + " msg(s), maxIterations=" + _maxIterations);

            if (RevealedToolNames == null) RevealedToolNames = new List<string>();
            // Provider-gated eviction, at turn boundaries only (mid-loop eviction would churn the
            // tools array between iterations for no benefit). See RevealedToolNames for the rationale.
            if (RevealedToolNames.Count > RevealEvictionCap
                && !OpenRouterClient.ModelSupportsPromptCaching(_model))
            {
                int evicted = 0;
                while (RevealedToolNames.Count > RevealEvictionCap)
                {
                    RevealedToolNames.RemoveAt(0); // recency order: index 0 is least recently used
                    evicted++;
                }
                _log.Log("mcp", "[turn " + turnId + "] evicted " + evicted
                    + " least-recently-used revealed tool def(s) (non-caching provider, cap "
                    + RevealEvictionCap + ")");
            }

            // The cap is a budget rather than a fixed loop bound so the user can grant another batch
            // when it's reached (ContinuationDecider) instead of dead-ending the turn.
            int budget = _maxIterations;
            // Set after a dispatch_agent the user stopped: the next model call is forced to tool_choice
            // "none" so the model must produce a text answer (a summary + "how should I proceed?") instead
            // of charging ahead with more tool calls. Reset each iteration once consumed.
            bool forceTextThisCall = false;
            for (int iter = 0; ; iter++)
            {
                // Stop requested between iterations (e.g. while the previous iteration's tools ran):
                // don't call the model again - finalize the turn cleanly.
                if (CancelRequested())
                {
                    FinishCancelled(history, null, ui, turnId);
                    return;
                }

                if (iter >= budget)
                {
                    bool cont = (ContinuationDecider != null) && ContinuationDecider(iter);
                    if (cont)
                    {
                        budget += _maxIterations;
                        _log.Log("mcp", "[turn " + turnId + "] iteration cap reached at " + iter
                            + "; user chose to continue (budget now " + budget + ")");
                    }
                    else
                    {
                        _log.Log("mcp", "[turn " + turnId + "] iteration cap reached at " + iter
                            + "; wrapping up");
                        RunCapWrapUp(history, _lastOfferedTools, ui, turnId);
                        return;
                    }
                }

                // MCP tools (reveal_tools + revealed defs) and their names manifest only when a server
                // actually contributes tools; a skills-only turn skips both and offers just open_skill.
                // Filter by THIS turn's workdir so a folderless turn never advertises another folder's
                // scoped tools (files/git/run_skill_script, ...) that it couldn't actually call.
                string resolveDir = ResolutionWorkdir;
                bool hasMcpTools = _registry != null && _registry.HasToolsForWorkdir(resolveDir);
                IList<JObject> tools = hasMcpTools
                    ? _registry.ExposedFunctionDefs(resolveDir, RevealedToolNames) : null;
                string manifest = hasMcpTools ? _registry.NamesManifestSystemMessage(resolveDir) : null;
                if (SkillTools != null && SkillTools.HasSkills)
                {
                    if (tools == null) tools = new List<JObject>();
                    tools.Add(SkillTools.OpenSkillDef());
                    tools.Add(SkillTools.ReadSkillFileDef());
                }
                if (AgentDispatcher != null && AgentDispatcher.HasAgents)
                {
                    if (tools == null) tools = new List<JObject>();
                    tools.Add(AgentDispatcher.DispatchAgentDef());
                }
                // Hide owned-but-locked tools (e.g. skill-authoring tools when the meta-skill is off):
                // drop them from the exposed defs and the names manifest so the model can't see or call them.
                if (HiddenToolNames != null && HiddenToolNames.Count > 0)
                {
                    tools = FilterHiddenDefs(tools, HiddenToolNames);
                    manifest = FilterHiddenManifest(manifest, HiddenToolNames);
                }
                // If filtering removed every tool line, drop the manifest entirely - otherwise the model
                // is left with the framing ("The following MCP tools are available... Available tools:")
                // over an empty list (e.g. a folderless turn whose only resolvable tools are all hidden).
                if (manifest != null && manifest.IndexOf("\n- ", StringComparison.Ordinal) < 0)
                    manifest = null;
                _lastOfferedTools = tools;
                _log.Log("mcp", "[turn " + turnId + "] iteration " + (iter + 1) + "/" + budget
                    + ": requesting model with " + (tools != null ? tools.Count : 0) + " exposed tool(s)");

                // Request layout, designed for prompt-cache reuse (three zones by volatility):
                //   Zone A - stable head: constant agent prompt + workspace block + AGENTS.md
                //            project instructions (system messages; byte-identical for the
                //            conversation's lifetime). Cache breakpoint #1 on
                //            its last message caches tools + system head together (tools render at
                //            position 0 of the prompt).
                //   Zone B - the persisted history (append-only). Cache breakpoint #2 rides the
                //            newest message, so each loop iteration / turn reads the previous
                //            request's prefix from cache and extends it incrementally.
                //   Zone C - one ephemeral user-role tail message holding everything that may change
                //            between requests (memory, skills manifest, MCP names manifest). Placed
                //            AFTER the breakpoints so its churn never invalidates the cached
                //            transcript. Never persisted; rebuilt every request.
                List<ChatMessage> requestMessages = BuildStableHead();
                int headCount = requestMessages.Count;

                // Build the sent messages from history, optionally transformed (e.g. attachments
                // inlined). The transform must not drop tool_calls / tool_call_id.
                IList<ChatMessage> contextMessages = RequestMessageTransform != null
                    ? RequestMessageTransform(history) : history;
                requestMessages.AddRange(contextMessages);
                ApplyCacheBreakpoints(requestMessages, headCount);

                string memoryBlock = MemorySystemMessageProvider != null
                    ? MemorySystemMessageProvider() : null;
                string skillsBlock = SkillsManifestSystemMessageProvider != null
                    ? SkillsManifestSystemMessageProvider() : null;
                string agentsBlock = AgentsManifestSystemMessageProvider != null
                    ? AgentsManifestSystemMessageProvider() : null;
                string ephemeralTail = BuildEphemeralContextText(memoryBlock, skillsBlock, agentsBlock, manifest);
                if (!string.IsNullOrEmpty(ephemeralTail))
                    requestMessages.Add(new ChatMessage("user", ephemeralTail));

                bool errored;
                string errMessage;
                // After a user-stopped fan-out, force a text-only answer (the dispatch result carries the
                // "summarize and ask" directive); otherwise normal auto tool choice.
                string toolChoice = forceTextThisCall ? "none" : null;
                forceTextThisCall = false;
                ToolCallAssembler asm = StreamOnce(requestMessages, tools, toolChoice, ui, out errored, out errMessage);
                if (errored)
                {
                    _log.Log("mcp", "[turn " + turnId + "] aborted on iteration " + (iter + 1)
                        + ": stream error: " + (errMessage ?? "(none)"));
                    if (ui != null) ui.OnError(errMessage);
                    return;
                }
                LogResponse(turnId, iter, asm);

                // Degenerate response (no tool calls AND no text, but no error): some providers emit an
                // empty completion on a transient hiccup. Retry the same request once before giving up.
                if (!asm.ProducedToolCalls && IsEmptyText(asm.Text))
                {
                    _log.Log("mcp", "[turn " + turnId + "] empty response (no tool calls, no text); retrying once");
                    asm = StreamOnce(requestMessages, tools, toolChoice, ui, out errored, out errMessage);
                    if (errored)
                    {
                        _log.Log("mcp", "[turn " + turnId + "] aborted on iteration " + (iter + 1)
                            + " (retry): stream error: " + (errMessage ?? "(none)"));
                        if (ui != null) ui.OnError(errMessage);
                        return;
                    }
                    LogResponse(turnId, iter, asm);
                }

                // Stop requested during (or just after) the model stream: the stream was killed, so its
                // tool calls are partial/unexecutable. Keep any text it produced and finalize - never
                // act on a half-streamed tool call.
                if (CancelRequested())
                {
                    FinishCancelled(history, asm, ui, turnId);
                    return;
                }

                if (!asm.ProducedToolCalls)
                {
                    if (IsEmptyText(asm.Text))
                    {
                        // Still empty after a retry: surface a clear, resumable notice rather than
                        // completing with a silent empty bubble.
                        string emptyNotice = "The model returned an empty response. Please try again.";
                        history.Add(new ChatMessage("assistant", emptyNotice));
                        _log.Log("mcp", "[turn " + turnId + "] still empty after retry; surfaced notice");
                        if (ui != null) { ui.AppendTextDelta(emptyNotice); ui.Complete(); }
                        return;
                    }

                    history.Add(new ChatMessage("assistant", asm.Text));
                    _log.Log("mcp", "[turn " + turnId + "] complete: final answer after " + (iter + 1)
                        + " iteration(s), " + asm.Text.Length + " chars");
                    if (ui != null) ui.Complete();
                    return;
                }

                // The assistant turn that requested the calls must be recorded with its tool_calls,
                // or the follow-up tool messages have nothing to answer.
                ChatMessage assistantMsg = new ChatMessage("assistant", asm.Text);
                assistantMsg.ToolCalls = asm.Calls;
                history.Add(assistantMsg);

                // Serial execution (phase 4): one call fully handled before the next.
                for (int c = 0; c < asm.Calls.Count; c++)
                {
                    ToolCall call = asm.Calls[c];
                    if (ui != null) ui.OnToolCall(call.Name, call.ArgumentsJson, call.Id);

                    bool isError;
                    string result = ExecuteCall(call, turnId, out isError);

                    if (ui != null) ui.OnToolResult(call.Name, result, isError, call.Id);
                    ChatMessage toolMsg = new ChatMessage("tool", result);
                    toolMsg.ToolCallId = call.Id;
                    history.Add(toolMsg);

                    // If the user stopped this dispatch_agent fan-out, force the next model call to text
                    // only so it wraps up (summary + ask) per the directive in the tool result, rather than
                    // launching into more tool calls.
                    if (AgentDispatcher != null && AgentDispatcher.IsDispatchAgent(call.Name)
                        && AgentDispatcher.GroupCancellation != null
                        && AgentDispatcher.GroupCancellation.IsCancelled)
                        forceTextThisCall = true;
                }
                // Loop: re-call the model with the tool results in context.
            }
        }

        // One streamed model request into a fresh assembler. Shared by the main loop, the
        // empty-response retry, and (with toolChoice = "none") the cap wrap-up.
        private ToolCallAssembler StreamOnce(IList<ChatMessage> requestMessages, IList<JObject> tools,
                                             string toolChoice, IToolLoopUi ui, out bool errored,
                                             out string errMessage)
        {
            ClientProperties props = new ClientProperties();
            props.Stream = true;
            props.ProviderDataCollectionAllowed = ProviderDataCollectionAllowed;
            props.Zdr = Zdr;
            props.ToolChoice = toolChoice;

            // Sticky provider routing on cache-supported models (see PreferredProvider); usage
            // accounting on every model. Stickiness is confirmation-gated: only a response
            // demonstrating cache activity - a read or a write - establishes or moves the
            // preference.
            bool cachingModel = OpenRouterClient.ModelSupportsPromptCaching(_model);
            if (cachingModel && !string.IsNullOrEmpty(PreferredProvider))
                props.ProviderOrder = new List<string> { PreferredProvider };
            props.ResponseUsageCallback = delegate(ResponseUsage u)
            {
                if (u == null) return;
                if (cachingModel && !string.IsNullOrEmpty(u.Provider)
                    && (u.CachedTokens > 0 || u.CacheWriteTokens > 0))
                {
                    PreferredProvider = u.Provider;
                    Action<string> cb = ProviderServed;
                    if (cb != null) cb(u.Provider);
                }
                Action<ResponseUsage> ucb = UsageReported;
                if (ucb != null) ucb(u);
            };

            Action<string> textSink = (ui != null) ? new Action<string>(ui.AppendTextDelta) : null;
            ToolCallAssembler asm = new ToolCallAssembler(textSink);
            bool err = false;
            string emsg = null;
            _streamer.StreamChat(_model, requestMessages, tools, props,
                asm.OnChunk,
                delegate(string e) { err = true; emsg = e; },
                Cancellation);
            asm.Finish();
            errored = err;
            errMessage = emsg;
            return asm;
        }

        private void LogResponse(string turnId, int iter, ToolCallAssembler asm)
        {
            _log.Log("mcp", "[turn " + turnId + "] iteration " + (iter + 1) + " response: finish_reason="
                + (asm.FinishReason ?? "(none)")
                + ", toolCalls=" + (asm.ProducedToolCalls ? asm.Calls.Count : 0)
                + ", textLen=" + (asm.Text != null ? asm.Text.Length : 0)
                + (asm.Truncated ? " [TRUNCATED: model output cut off by length]" : ""));
        }

        // Cap reached and not continued: one final model call (tool_choice "none") asking it to
        // summarize and ask how to proceed, so the turn ends with a readable assistant message
        // rather than a cryptic dead-end. The user can simply reply to keep going (a fresh budget
        // next turn). The request reuses the loop's prompt shape - same stable head, same history,
        // same tools array - rather than dropping tools: on Anthropic a tool_choice change
        // invalidates only the message-tier cache, while removing the tools array would change
        // position 0 of the prompt and invalidate the tools+system tiers as well. The next user
        // turn (back on tool_choice auto) still extends the loop's cached prefix unharmed.
        private void RunCapWrapUp(IList<ChatMessage> history, IList<JObject> tools, IToolLoopUi ui,
                                  string turnId)
        {
            List<ChatMessage> requestMessages = BuildStableHead();
            int headCount = requestMessages.Count;
            IList<ChatMessage> contextMessages = RequestMessageTransform != null
                ? RequestMessageTransform(history) : history;
            requestMessages.AddRange(contextMessages);
            ApplyCacheBreakpoints(requestMessages, headCount);
            // Sent as a user message, not system: Anthropic (via OpenRouter) hoists in-array system
            // messages to the top-level system parameter, which would leave the conversation ending
            // on a tool result and the model with nothing in-position to answer (it replies with a
            // near-empty acknowledgment). A trailing user turn keeps the instruction in place so the
            // model actually summarizes.
            requestMessages.Add(new ChatMessage("user",
                "You have reached the maximum number of tool calls allowed for this turn. Do not "
                + "request any more tools now. Briefly summarize what you have done so far and what "
                + "still remains, then ask the user how they would like to proceed."));

            // tool_choice "none": the model must answer with text, while the unchanged tools array
            // keeps the cached prefix intact.
            bool errored;
            string errMessage;
            ToolCallAssembler asm = StreamOnce(requestMessages, tools, "none", ui, out errored, out errMessage);

            string text;
            if (errored || IsEmptyText(asm.Text))
            {
                if (errored)
                    _log.Log("mcp", "[turn " + turnId + "] wrap-up stream error: " + (errMessage ?? "(none)"));
                text = "I've reached the tool-call limit for this turn. Let me know how you'd like to proceed.";
                // StreamOnce streamed nothing usable, so emit the fallback to the UI ourselves.
                if (ui != null) ui.AppendTextDelta(text);
            }
            else
            {
                text = asm.Text;
                _log.Log("mcp", "[turn " + turnId + "] wrap-up complete (" + text.Length + " chars)");
            }

            history.Add(new ChatMessage("assistant", text));
            if (ui != null) ui.Complete();
        }

        // Flags the request's cache breakpoints (Anthropic allows at most 4 per request):
        //   #1  last message of the stable head (request-local object; flagged directly),
        //   #2  the newest history message (a WithCacheControl clone - the flag must never land
        //       on, or accumulate in, persisted history),
        //   plus up to TWO intermediate flags spaced ~12 estimated content blocks apart, walking
        //   back from the end. The intermediates bridge Anthropic's ~20-block matcher lookback:
        //   without them, a single iteration that appends more blocks than the lookback covers
        //   (an assistant message with K tool calls renders as ~K+1 blocks and its results as K
        //   more, so K >= ~10 outruns it) leaves the next request's breakpoint unable to find the
        //   previous cache entry - a silent full miss. With both spares, fan-outs up to ~18 calls
        //   per iteration stay bridged; a single message wider than the lookback (~20+ calls in
        //   one assistant turn) is unbridgeable by placement and accepted. When the appended span
        //   is short, the spares land in older, already-cached content, which is harmless.
        internal static void ApplyCacheBreakpoints(List<ChatMessage> requestMessages, int headCount)
        {
            if (headCount > 0 && requestMessages.Count >= headCount)
                requestMessages[headCount - 1].CacheControl = true;
            if (requestMessages.Count <= headCount) return;

            int last = requestMessages.Count - 1;
            requestMessages[last] = requestMessages[last].WithCacheControl();

            int blocksSinceFlag = 0;
            int extraFlags = 0;
            for (int i = last - 1; i >= headCount && extraFlags < 2; i--)
            {
                blocksSinceFlag += EstimateContentBlocks(requestMessages[i]);
                if (blocksSinceFlag >= 12)
                {
                    requestMessages[i] = requestMessages[i].WithCacheControl();
                    blocksSinceFlag = 0;
                    extraFlags++;
                }
            }
        }

        // How many Anthropic content blocks one OpenAI-format message renders as: an assistant
        // message contributes one tool_use block per call plus a text block when it carried text;
        // everything else (user text, tool result, system) is a single block.
        internal static int EstimateContentBlocks(ChatMessage m)
        {
            if (m == null) return 0;
            int blocks = (m.ToolCalls != null) ? m.ToolCalls.Count : 0;
            if (blocks == 0 || !string.IsNullOrEmpty(m.Content)) blocks++;
            return blocks;
        }

        // Zone A: the stable system head, byte-identical for every request of a conversation (the
        // agent prompt is constant; the workspace block is constant while the workspace is; the
        // project-instructions block is fixed per turn and constant while AGENTS.md is). Fresh
        // message objects each call, so callers may set CacheControl on them directly.
        private List<ChatMessage> BuildStableHead()
        {
            List<ChatMessage> head = new List<ChatMessage>();
            head.Add(new ChatMessage("system", AgentSystemPrompt));
            string workspaceBlock = WorkspaceSystemMessage(WorkingDir);
            if (!string.IsNullOrEmpty(workspaceBlock))
                head.Add(new ChatMessage("system", workspaceBlock));
            else
            {
                // No workspace, but the command server may be running in a per-conversation scratch
                // sandbox: tell the model about that temp dir (kept distinct from the workspace block).
                string scratchBlock = ScratchSystemMessage(ScratchWorkingDir);
                if (!string.IsNullOrEmpty(scratchBlock))
                    head.Add(new ChatMessage("system", scratchBlock));
            }
            if (!string.IsNullOrEmpty(ProjectInstructions))
                head.Add(new ChatMessage("system", ProjectInstructions));
            return head;
        }

        // Zone C: the ephemeral context tail - one user-role message holding everything that may
        // change between requests (memory index, skills manifest, MCP names manifest). User role
        // because Anthropic (via OpenRouter) hoists in-array system messages to the top-level system
        // parameter, which would put this back in front of the cached history; as a trailing user
        // message it merges into the same user turn as any preceding tool results ([tool_result...,
        // text] is the order Anthropic requires). Returns null when every block is empty, so a turn
        // without memory/skills/agents/tools leaves no trace. Never persisted; the UI never renders it.
        internal static string BuildEphemeralContextText(string memory, string skills, string agents, string toolManifest)
        {
            bool hasMemory = !string.IsNullOrEmpty(memory);
            bool hasSkills = !string.IsNullOrEmpty(skills);
            bool hasAgents = !string.IsNullOrEmpty(agents);
            bool hasManifest = !string.IsNullOrEmpty(toolManifest);
            if (!hasMemory && !hasSkills && !hasAgents && !hasManifest) return null;

            StringBuilder sb = new StringBuilder();
            sb.Append("[Ephemeral context appended by the host application for this request. ");
            sb.Append("It is not part of the user's message.]");
            if (hasMemory)
                sb.Append("\n\n<memory>\n").Append(memory).Append("\n</memory>");
            if (hasSkills)
                sb.Append("\n\n<skills>\n").Append(skills).Append("\n</skills>");
            if (hasAgents)
                sb.Append("\n\n<agents>\n").Append(agents).Append("\n</agents>");
            if (hasManifest)
                sb.Append("\n\n<available_tools>\n").Append(toolManifest).Append("\n</available_tools>");
            return sb.ToString();
        }

        private static bool IsEmptyText(string s)
        {
            return s == null || s.Trim().Length == 0;
        }

        private bool CancelRequested()
        {
            return Cancellation != null && Cancellation.IsCancelled;
        }

        // Cancellation landed: finalize the turn cleanly. Persist any partial assistant text (so the
        // streamed bubble isn't lost), then signal completion - never an error, since the user asked
        // to stop. asm may be null (cancel between iterations, before this iteration streamed). Only
        // plain text is recorded: a partial tool call is dropped so history can't end on a tool_call
        // with no matching result, which the next request would reject.
        private void FinishCancelled(IList<ChatMessage> history, ToolCallAssembler asm, IToolLoopUi ui, string turnId)
        {
            string partial = (asm != null) ? asm.Text : null;
            if (!IsEmptyText(partial))
                history.Add(new ChatMessage("assistant", partial));
            _log.Log("mcp", "[turn " + turnId + "] cancelled by user"
                + (IsEmptyText(partial) ? string.Empty : " (kept " + partial.Length + " chars of partial text)"));
            if (ui != null) ui.Complete();
        }

        // Executes one tool call, returning the text to feed back as the tool message content.
        // Failures are returned as content (not thrown) so the model can recover; isError flags the
        // UI marker. reveal_tools is handled locally without an MCP round-trip.
        private string ExecuteCall(ToolCall call, string turnId, out bool isError)
        {
            isError = false;

            if (_registry != null && _registry.IsRevealTools(call.Name))
            {
                string[] names = ParseRevealNames(call.ArgumentsJson);
                _log.Log("mcp", "[turn " + turnId + "] reveal_tools: " + names.Length + " name(s)");
                return _registry.Reveal(names, ResolutionWorkdir, RevealedToolNames);
            }

            // open_skill is a host meta-tool (no MCP round-trip): load skill bodies by slug. Same
            // {names:[...]} argument shape as reveal_tools, so the parser is reused.
            if (SkillTools != null && SkillTools.IsOpenSkill(call.Name))
            {
                string[] slugs = ParseRevealNames(call.ArgumentsJson);
                _log.Log("mcp", "[turn " + turnId + "] open_skill: " + slugs.Length + " name(s)");
                return SkillTools.Open(slugs);
            }

            // read_skill_file is a host meta-tool too (ReadOnly): read a bundled asset by (slug, relpath).
            if (SkillTools != null && SkillTools.IsReadSkillFile(call.Name))
            {
                string skillSlug, relpath;
                ParseSkillFileArgs(call.ArgumentsJson, out skillSlug, out relpath);
                _log.Log("mcp", "[turn " + turnId + "] read_skill_file: "
                    + (skillSlug != null ? skillSlug : "?") + " / " + (relpath != null ? relpath : "?"));
                return SkillTools.ReadFile(skillSlug, relpath);
            }

            // dispatch_agent is a host meta-tool too: run the sub-agent(s) in isolated child orchestrators
            // and return their final answer(s). No MCP round-trip; the child gets no dispatcher, so it
            // cannot nest (A12). Failures inside a child come back as content, not an exception.
            if (AgentDispatcher != null && AgentDispatcher.IsDispatchAgent(call.Name))
            {
                _log.Log("mcp", "[turn " + turnId + "] dispatch_agent");
                return AgentDispatcher.Dispatch(call.ArgumentsJson);
            }

            // A hidden (gated-off) tool must not be callable even if the model names it directly.
            if (HiddenToolNames != null && HiddenToolNames.Contains(call.Name))
            {
                isError = true;
                _log.Log("mcp", "[turn " + turnId + "] blocked hidden tool '" + call.Name + "'");
                return "[Unknown tool: " + call.Name + "]";
            }

            McpServerConnection conn;
            string toolName;
            string resolveDir = ResolutionWorkdir;
            if (_registry == null || !_registry.TryResolve(call.Name, resolveDir, out conn, out toolName))
            {
                isError = true;
                _log.Log("mcp", "[turn " + turnId + "] unresolved tool '" + call.Name + "' (workdir="
                    + (string.IsNullOrEmpty(resolveDir) ? "(none)" : resolveDir) + ")");
                return "[Unknown tool: " + call.Name + "]";
            }

            // An actively-called tool moves to the end of the recency-ordered reveal list, so the
            // provider-gated eviction (non-caching models only) trims idle defs first. Reordering the
            // list is cache-safe: the emitted tools array is sorted by name, not list order.
            if (RevealedToolNames != null && RevealedToolNames.Contains(call.Name))
            {
                RevealedToolNames.Remove(call.Name);
                RevealedToolNames.Add(call.Name);
            }

            JObject args;
            if (!TryParseArgs(call.ArgumentsJson, out args))
            {
                isError = true;
                _log.Log("mcp", "[turn " + turnId + "] invalid arguments for '" + call.Name
                    + "' (not valid JSON)");
                return "[Invalid tool arguments: not valid JSON.]";
            }

            // Logged before the approval check: if the next line for this call is far behind in
            // wall-clock time but reports a small tool 'ms', the gap was the user's approval prompt.
            _log.Log("mcp", "[turn " + turnId + "] dispatch '" + call.Name + "' (args "
                + (call.ArgumentsJson != null ? call.ArgumentsJson.Length : 0) + " bytes)");

            ApprovalDecision decision = _approval.Check(call.Name, args);
            if (decision == ApprovalDecision.Deny)
            {
                isError = true;
                _log.Log("mcp", "[turn " + turnId + "] '" + call.Name + "' denied by approval policy");
                return DeniedResultText;
            }

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                CallToolResult res = conn.CallTool(toolName, args, _callTimeoutMs);
                sw.Stop();
                isError = (res != null && res.IsError);
                string formatted = FormatResult(res);
                _log.Log("mcp", "[turn " + turnId + "] '" + call.Name + "' -> "
                    + (isError ? "isError" : "ok") + " (" + (formatted != null ? formatted.Length : 0)
                    + " chars, " + sw.ElapsedMilliseconds + "ms)");
                return formatted;
            }
            catch (McpTransportException ex)
            {
                sw.Stop();
                _log.Log("mcp", "[turn " + turnId + "] transport fault calling '" + call.Name + "' after "
                    + sw.ElapsedMilliseconds + "ms: " + ex.Message);
                isError = true;
                return "[Server unavailable.]";
            }
            catch (McpTimeoutException ex)
            {
                sw.Stop();
                _log.Log("mcp", "[turn " + turnId + "] timeout calling '" + call.Name + "' after "
                    + sw.ElapsedMilliseconds + "ms: " + ex.Message);
                isError = true;
                return "[Tool timed out.]";
            }
            catch (McpException ex)
            {
                sw.Stop();
                _log.Log("mcp", "[turn " + turnId + "] tool error calling '" + call.Name + "' after "
                    + sw.ElapsedMilliseconds + "ms: " + ex.Message);
                isError = true;
                return "[Tool error: " + ex.Message + "]";
            }
        }

        // ---- helpers ----

        private static bool TryParseArgs(string argumentsJson, out JObject args)
        {
            args = null;
            try
            {
                if (string.IsNullOrEmpty(argumentsJson))
                {
                    args = new JObject();
                    return true;
                }
                JToken t = JToken.Parse(argumentsJson);
                if (t.Type == JTokenType.Object) { args = (JObject)t; return true; }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static string[] ParseRevealNames(string argumentsJson)
        {
            List<string> names = new List<string>();
            try
            {
                JObject o = JObject.Parse(argumentsJson);
                JToken arr = o["names"];
                if (arr != null && arr.Type == JTokenType.Array)
                {
                    foreach (JToken n in (JArray)arr)
                    {
                        if (n != null && n.Type == JTokenType.String) names.Add((string)n);
                    }
                }
            }
            catch
            {
                // malformed reveal args → no names; Reveal returns an empty def list.
            }
            return names.ToArray();
        }

        // Extracts { slug, relpath } string args for read_skill_file; missing/invalid -> null (the tool
        // then returns a short notice the model can read).
        private static void ParseSkillFileArgs(string argumentsJson, out string slug, out string relpath)
        {
            slug = null;
            relpath = null;
            try
            {
                JObject o = JObject.Parse(argumentsJson);
                JToken s = o["slug"];
                if (s != null && s.Type == JTokenType.String) slug = (string)s;
                JToken r = o["relpath"];
                if (r != null && r.Type == JTokenType.String) relpath = (string)r;
            }
            catch
            {
                // malformed args -> nulls; ReadFile reports the problem.
            }
        }

        // Drops any def whose function name is hidden (reveal_tools/open_skill are never in the hidden set).
        internal static IList<JObject> FilterHiddenDefs(IList<JObject> defs, ICollection<string> hidden)
        {
            if (defs == null || hidden == null || hidden.Count == 0) return defs;
            List<JObject> kept = new List<JObject>(defs.Count);
            for (int i = 0; i < defs.Count; i++)
            {
                string name = DefFunctionName(defs[i]);
                if (name != null && hidden.Contains(name)) continue;
                kept.Add(defs[i]);
            }
            return kept;
        }

        private static string DefFunctionName(JObject def)
        {
            if (def == null) return null;
            JToken fn = def["function"];
            if (fn == null) return null;
            JToken n = fn["name"];
            return (n != null && n.Type == JTokenType.String) ? (string)n : null;
        }

        // Removes the "- <name>" manifest lines for hidden tools, leaving the framing intact.
        internal static string FilterHiddenManifest(string manifest, ICollection<string> hidden)
        {
            if (string.IsNullOrEmpty(manifest) || hidden == null || hidden.Count == 0) return manifest;
            string[] lines = manifest.Split('\n');
            List<string> kept = new List<string>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length > 2 && trimmed[0] == '-' && trimmed[1] == ' '
                    && hidden.Contains(trimmed.Substring(2).Trim()))
                    continue;
                kept.Add(lines[i]);
            }
            return string.Join("\n", kept.ToArray());
        }

        // CallToolResult.content[] → a single string for the tool message. Text blocks are
        // concatenated; non-text blocks become a short placeholder; structuredContent (if any) is
        // appended as compact JSON. isError content is still returned verbatim (the model sees it).
        private static string FormatResult(CallToolResult res)
        {
            if (res == null) return string.Empty;

            StringBuilder sb = new StringBuilder();
            if (res.Content != null)
            {
                for (int i = 0; i < res.Content.Count; i++)
                {
                    ContentBlock b = res.Content[i];
                    if (b == null) continue;
                    string text;
                    if (b.TryGetText(out text))
                        sb.Append(text);
                    else
                        sb.Append(Placeholder(b));
                }
            }

            if (res.StructuredContent != null)
            {
                if (sb.Length > 0) sb.Append("\n");
                sb.Append(res.StructuredContent.ToString(Formatting.None));
            }
            return sb.ToString();
        }

        private static string Placeholder(ContentBlock b)
        {
            string type = b.Type != null ? b.Type : "unknown";
            if (type == "resource" || type == "resource_link")
            {
                string uri = null;
                if (b.Raw != null)
                {
                    JToken direct = b.Raw["uri"];
                    if (direct != null && direct.Type == JTokenType.String) uri = (string)direct;
                    if (uri == null)
                    {
                        JToken resource = b.Raw["resource"];
                        if (resource != null && resource.Type == JTokenType.Object)
                        {
                            JToken nested = resource["uri"];
                            if (nested != null && nested.Type == JTokenType.String) uri = (string)nested;
                        }
                    }
                }
                return "[resource: " + (uri != null ? uri : "?") + "]";
            }
            return "[" + type + "]";
        }
    }
}
