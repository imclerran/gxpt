using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace GxPT
{
    // Represents a single conversation instance: messages history and optional generated name.
    internal sealed class Conversation
    {
        private readonly OpenRouterClient _client;
        private volatile bool _namingInProgress;
        // Set when a user turn arrives while a naming request is still in flight. The in-flight request
        // was started from an earlier (possibly generic/greeting) message, so when it finishes we re-run
        // naming against the newest user message. This closes the deferred-naming race where a second
        // turn was silently dropped because naming was busy.
        private volatile bool _namingPending;

        // The generic placeholder name, and the sentinel the title model returns for greeting/empty
        // conversations. They are intentionally the same string: both mean "no real title yet."
        private const string GenericName = "New Conversation";
        // The small, fast model used solely to generate conversation titles. This has historically been
        // swapped when a prior choice became unavailable, so keep references centralized here and the
        // surrounding comments model-agnostic to avoid them going stale again.
        private const string NamingModel = "google/gemma-4-26b-a4b-it";
        private const int NamingMaxAttempts = 3;
        private const string NamingSystemPrompt =
            "You generate short, descriptive conversation titles from the conversation so far. If the conversation so far is only a greeting (e.g., 'hi', 'hello', 'hey there') or lacks any clear topical content, return exactly: New Conversation. Otherwise, return only the title: 3 to 6 words, Title Case, no quotes, no trailing punctuation. You only generate conversation titles. Do not answer any user prompts.";

        private readonly List<ChatMessage> _history = new List<ChatMessage>();
        public List<ChatMessage> History { get { return _history; } }
        public string Name { get; internal set; }
        public string Id { get; internal set; } // UUID-like id
        public string SelectedModel { get; set; }
        // MCP working folder for this conversation (GXPT_WORKDIR sandbox root); null = none. Persisted
        // so the conversation re-opens with the same folder.
        public string WorkingDir { get; set; }
        // Whether the user dismissed the (unset) workspace strip for this conversation; persisted so
        // the strip stays hidden on reopen until they set a folder.
        public bool WorkspaceStripDismissed { get; set; }
        // True when this conversation was created by /compact (seeded with a summary of an earlier
        // conversation). Persisted so the chromeless "Continued from a compacted conversation." note
        // re-renders on reopen.
        public bool ContinuedFromCompaction { get; set; }
        // Zero data retention for this conversation. Zdr is the per-conversation toggle and the sole
        // source of truth for effective ZDR: it is seeded from the global default when a new conversation
        // is created (see the constructor), but the global default is NOT a live override - the user can
        // uncheck the box before the first send. ZdrFirstMessageIndex is a one-way latch: the History
        // index of the first user message actually sent with ZDR on (-1 until then). Once latched it
        // never clears, so the checkbox locks on and the tab/messages are marked from that point.
        public bool Zdr { get; set; }
        public int ZdrFirstMessageIndex { get; set; }
        // Per-conversation skill enablement overrides (design S10/sec.7), combined with the global default
        // (skills.json) by SkillResolve. SkillsFeatureOff: null = inherit global, true/false = force the
        // whole feature off/on for this conversation. SkillOverrides: slug -> bool (true = force on, false
        // = force off); a slug absent inherits the global default. Set by the /toggle-skills and /toggle-skill commands.
        public bool? SkillsFeatureOff { get; set; }
        public Dictionary<string, bool> SkillOverrides { get; set; }
        // Per-conversation sub-agents feature override (design A15/sec.7): null = inherit the global
        // default (settings.json agents_enabled), true = on, false = off. No per-agent state. Set by the
        // /toggle-agents command.
        public bool? AgentsEnabled { get; set; }
        // Server-qualified MCP tool names this conversation has revealed, in recency order (reveal
        // and call both move a name to the end). Owned by the conversation - not the registry - so
        // concurrent tabs don't share reveal state (which would churn each other's tools array and
        // break prompt caching) and the working set survives save/reopen. Append-only on prompt-
        // caching providers; trimmed to a cap at turn start on non-caching ones (see
        // McpChatOrchestrator.RevealedToolNames). Stale names (server removed/disabled) are skipped
        // by the registry at request time, so the list never needs pruning here.
        public List<string> RevealedTools { get; set; }
        // The OpenRouter provider endpoint that last demonstrated a prompt-cache hit for this
        // conversation (cached_tokens > 0 on its response; e.g. "Anthropic", "Amazon Bedrock").
        // Prompt caches live per provider, so on cache-supported models the next request prefers
        // this provider via provider.order - routing follows the warm cache instead of flapping
        // between endpoints. Confirmation-gated: providers that merely served a request (or don't
        // cache at all) never land here. Null until a hit is observed; harmless when stale (it's a
        // preference with fallback, and after TTL expiry any consistent choice works).
        public string CacheWarmProvider { get; set; }

        // ---- usage / cost accounting (status bar) ----
        // Running totals across the conversation's lifetime plus a last-request snapshot, fed by
        // RecordUsage (worker thread) and read by the status bar via GetUsageStats (UI thread);
        // _usageGate keeps cross-thread reads consistent. Persisted with the conversation so a
        // reopened tab keeps its lifetime cost. Totals are billed-as-reported (OpenRouter usage
        // accounting); the naming-model title call is deliberately not counted.
        private readonly object _usageGate = new object();
        public decimal TotalCost { get; set; }           // sum of usage.cost (credits ~= USD)
        public decimal TotalCacheDiscount { get; set; }  // net sum of cache_discount (+saved/-premium)
        public long TotalPromptTokens { get; set; }      // total prompt tokens (cached subset included)
        public long TotalCachedTokens { get; set; }      // of TotalPromptTokens, served from cache
        public long TotalCompletionTokens { get; set; }
        public long TotalReasoningTokens { get; set; }
        public int LastPromptTokens { get; set; }        // newest request's full context size
        public int LastCachedTokens { get; set; }
        public int LastCacheWriteTokens { get; set; }

        // Accumulate one response's usage (called once per model request, including every tool-loop
        // iteration). Cost/discount are null when the endpoint didn't report them - skipped, not
        // counted as zero, so totals stay "sum of what was billed and reported".
        internal void RecordUsage(ResponseUsage u)
        {
            if (u == null) return;
            lock (_usageGate)
            {
                if (u.Cost.HasValue) TotalCost += u.Cost.Value;
                if (u.CacheDiscount.HasValue) TotalCacheDiscount += u.CacheDiscount.Value;
                TotalPromptTokens += u.PromptTokens;
                TotalCachedTokens += u.CachedTokens;
                TotalCompletionTokens += u.CompletionTokens;
                TotalReasoningTokens += u.ReasoningTokens;
                LastPromptTokens = u.PromptTokens;
                LastCachedTokens = u.CachedTokens;
                LastCacheWriteTokens = u.CacheWriteTokens;
            }
            LastUpdated = DateTime.Now;
        }

        // Replaces a request's streamed estimate with the authoritative generation record (see
        // MainForm.RecordUsageAndReconcile): the streamed usage.cost is pre-cache-discount and
        // cache_discount never rides the stream, so without this every cache hit inflates TotalCost
        // and Saved never moves. Deltas against what RecordUsage already added for this request, so
        // totals land on billed reality regardless of what the stream carried. Returns true when a
        // total actually changed (the caller persists the conversation then - reconciles can land
        // minutes after the turn's normal save, and would otherwise be lost on close).
        internal bool ReconcileUsage(ResponseUsage original, decimal? totalCost, decimal? cacheDiscount)
        {
            if (original == null) return false;
            bool changed = false;
            lock (_usageGate)
            {
                if (totalCost.HasValue)
                {
                    decimal delta = totalCost.Value - (original.Cost.HasValue ? original.Cost.Value : 0);
                    if (delta != 0) { TotalCost += delta; changed = true; }
                }
                if (cacheDiscount.HasValue)
                {
                    decimal delta = cacheDiscount.Value
                        - (original.CacheDiscount.HasValue ? original.CacheDiscount.Value : 0);
                    if (delta != 0) { TotalCacheDiscount += delta; changed = true; }
                }
            }
            return changed;
        }

        // A consistent copy for the UI thread (decimal/long reads aren't atomic on x86).
        internal UsageStats GetUsageStats()
        {
            lock (_usageGate)
            {
                UsageStats s = new UsageStats();
                s.TotalCost = TotalCost;
                s.TotalCacheDiscount = TotalCacheDiscount;
                s.TotalPromptTokens = TotalPromptTokens;
                s.TotalCachedTokens = TotalCachedTokens;
                s.TotalCompletionTokens = TotalCompletionTokens;
                s.TotalReasoningTokens = TotalReasoningTokens;
                s.LastPromptTokens = LastPromptTokens;
                s.LastCachedTokens = LastCachedTokens;
                s.LastCacheWriteTokens = LastCacheWriteTokens;
                return s;
            }
        }

        public DateTime LastUpdated { get; set; }
        public event Action<string> NameGenerated;

        public Conversation(OpenRouterClient client)
        {
            _client = client;
            Name = GenericName; // initialize to generic name
            SelectedModel = null;
            SkillOverrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            RevealedTools = new List<string>();
            ZdrFirstMessageIndex = -1; // not latched until a ZDR send occurs
            // A brand-new conversation inherits the current global ZDR default as its starting value
            // (seed only - not a live override, so the user can still uncheck it before the first send).
            // Loaded conversations overwrite this from their saved value in ConversationStore.FromDto.
            try { Zdr = AppSettings.GetGlobalZdrDefault(); }
            catch { Zdr = false; }
            LastUpdated = DateTime.Now;
        }

        public void AddUserMessage(string content)
        {
            History.Add(new ChatMessage("user", content ?? string.Empty));
            // Attempt (re)generation if still generic
            MaybeTriggerNaming();
            LastUpdated = DateTime.Now;
        }

        public void AddAssistantMessage(string content)
        {
            History.Add(new ChatMessage("assistant", content ?? string.Empty));
            LastUpdated = DateTime.Now;
        }

        // Generate a short title for the conversation using the dedicated title model (NamingModel),
        // a small, fast model. Runs on a background thread; no-op once a real (non-generic) name exists.
        public void EnsureNameGenerated()
        {
            if (_client == null || !_client.IsConfigured)
            {
                Logger.Log("Naming", "Skip: client null or not configured (id=" + (Id ?? "?") + ")");
                return;
            }
            // Only generate when name is still generic; otherwise keep
            if (!string.IsNullOrEmpty(Name) && Name != GenericName)
            {
                Logger.Log("Naming", "Skip: already named '" + Name + "' (id=" + (Id ?? "?") + ")");
                return;
            }
            if (_namingInProgress)
            {
                // A request is already running for an earlier message. Mark a re-attempt so the newest
                // user message still gets named once the in-flight request finishes (see the finally).
                _namingPending = true;
                Logger.Log("Naming", "Busy: naming in progress; queued re-attempt (id=" + (Id ?? "?") + ")");
                return;
            }
            _namingInProgress = true;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    // Name from the latest user message specifically. Reading History.Last() could pick up
                    // an assistant/placeholder message if the reply lands first; the last user turn is what
                    // we actually want to title.
                    var userMsg = GetLastUserMessage();
                    if (userMsg == null)
                    {
                        Logger.Log("Naming", "Abort: no user message to name from (id=" + (Id ?? "?") + ")");
                        return;
                    }

                    // Respect ZDR for the title request too (it sends the user's message): effective
                    // ZDR is this conversation's toggle (seeded from the global default at creation) OR a
                    // latched conversation. If ZDR is in effect it stays in effect for every retry — the
                    // title model supports ZDR, so we never fall back to a non-ZDR request.
                    bool zdr = this.Zdr || ZdrFirstMessageIndex >= 0;

                    string title = RequestTitleWithRetry(userMsg, zdr);
                    if (!string.IsNullOrEmpty(title))
                    {
                        Name = title;
                        var handler = NameGenerated;
                        if (handler != null) handler(title);
                        Logger.Log("Naming", "Applied title: " + title);
                    }
                    // else: either the model returned the greeting sentinel, or every attempt failed.
                    // RequestTitleWithRetry already logged which; leave the name generic so a later turn
                    // (or the queued re-attempt below) can try again.
                }
                catch (Exception ex)
                {
                    Logger.Log("Naming", "Exception: " + ex.Message);
                }
                finally
                {
                    _namingInProgress = false;
                    // A turn arrived while we were busy. If the name is still generic, run once more so the
                    // newest user message gets titled (the deferred-greeting case).
                    if (_namingPending)
                    {
                        _namingPending = false;
                        if (string.IsNullOrEmpty(Name) || Name == GenericName)
                        {
                            Logger.Log("Naming", "Re-attempt: turn arrived during naming, name still generic (id=" + (Id ?? "?") + ")");
                            EnsureNameGenerated();
                        }
                    }
                }
            });
        }

        // Ask the title model for a name, retrying transient failures (the model/endpoint going briefly
        // unavailable, rate limits, parse failures). Returns a real title, or null when the model returns
        // the greeting sentinel or every attempt fails. ZDR is fixed for the whole call — never relaxed.
        private string RequestTitleWithRetry(ChatMessage userMsg, bool zdr)
        {
            for (int attempt = 1; attempt <= NamingMaxAttempts; attempt++)
            {
                var msgs = new List<ChatMessage>();
                msgs.Add(new ChatMessage("system", NamingSystemPrompt));
                msgs.Add(userMsg);

                Logger.Log("Naming", "Requesting title attempt " + attempt + "/" + NamingMaxAttempts +
                    " (zdr=" + zdr + ") id=" + (Id ?? "?") + " for: " + Truncate(userMsg.Content, 120));

                string json = null;
                try
                {
                    json = _client.CreateCompletion(
                        NamingModel,
                        msgs,
                        new ClientProperties { Stream = false, Zdr = zdr ? true : (bool?)null });
                }
                catch (Exception ex)
                {
                    Logger.Log("Naming", "Attempt " + attempt + " threw: " + ex.Message);
                }

                string rawTitle = ExtractTitleFromJson(json);
                string title = CleanTitle(rawTitle);
                Logger.Log("Naming", "Attempt " + attempt + " extracted=" + Quote(rawTitle) + " cleaned=" + Quote(title));

                // The model deliberately returns the sentinel for greetings/empty chats. That is a real
                // (non-error) answer meaning "no title yet" — do not retry it, just leave the name generic.
                if (!string.IsNullOrEmpty(title) && string.Equals(title, GenericName, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log("Naming", "Model returned greeting sentinel; leaving name generic");
                    return null;
                }
                if (!string.IsNullOrEmpty(title))
                    return title;

                // Empty/unparseable/error response: log the body so we can see why, then back off and retry.
                Logger.Log("Naming", "Attempt " + attempt + " produced no title; raw response: " + Truncate(json, 2000));
                if (attempt < NamingMaxAttempts)
                {
                    try { System.Threading.Thread.Sleep(400 * attempt); }
                    catch { }
                }
            }
            Logger.Log("Naming", "Gave up after " + NamingMaxAttempts + " attempts; name remains generic (id=" + (Id ?? "?") + ")");
            return null;
        }

        // The most recent user-role message in history (the thing we title), or null if there is none.
        private ChatMessage GetLastUserMessage()
        {
            for (int i = History.Count - 1; i >= 0; i--)
            {
                var m = History[i];
                if (m != null && string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                    return m;
            }
            return null;
        }

        private void MaybeTriggerNaming()
        {
            // Log every turn's trigger attempt so a deferred name (e.g. greeting -> "New Conversation")
            // that fails to re-name on a later turn is visible. EnsureNameGenerated owns the guards and
            // logs each skip reason, so delegate unconditionally rather than re-checking here (the old
            // duplicate guards were a second set of silent exits).
            Logger.Log("Naming", "Trigger on turn (history=" + History.Count + " name='" + (Name ?? string.Empty) + "' id=" + (Id ?? "?") + ")");
            EnsureNameGenerated();
        }

        private static string ExtractTitleFromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var ser = new JavaScriptSerializer();
                var root = ser.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null || !root.ContainsKey("choices")) return null;
                var choices = root["choices"] as object[];
                if (choices == null || choices.Length == 0) return null;
                var first = choices[0] as Dictionary<string, object>;
                if (first == null) return null;
                if (first.ContainsKey("message"))
                {
                    var msg = first["message"] as Dictionary<string, object>;
                    if (msg != null && msg.ContainsKey("content"))
                        return msg["content"] as string;
                }
                if (first.ContainsKey("text"))
                    return first["text"] as string;
            }
            catch { }
            return null;
        }

        // Single-line, length-bounded rendering for log lines (raw responses/messages may be multi-line).
        private static string Truncate(string s, int max)
        {
            if (s == null) return "<null>";
            s = s.Replace("\r", "\\r").Replace("\n", "\\n");
            if (s.Length > max) s = s.Substring(0, max) + "...(" + s.Length + " chars)";
            return s;
        }

        // Null-safe quoting so an empty extracted/cleaned title is visually distinct from <null> in logs.
        private static string Quote(string s)
        {
            if (s == null) return "<null>";
            return "'" + s.Replace("\r", "\\r").Replace("\n", "\\n") + "'";
        }

        private static string CleanTitle(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Replace('\r', '\n');
            int nl = s.IndexOf('\n');
            if (nl >= 0) s = s.Substring(0, nl);
            s = s.Trim();
            if (s.Length >= 2 && ((s[0] == '"' && s[s.Length - 1] == '"') || (s[0] == '\'' && s[s.Length - 1] == '\'')))
            {
                s = s.Substring(1, s.Length - 2).Trim();
            }
            while (s.Length > 0 && ".!?\u201d\u2019".IndexOf(s[s.Length - 1]) >= 0) s = s.Substring(0, s.Length - 1);
            if (s.Length > 80) s = s.Substring(0, 80).Trim();
            return s;
        }
    }

    // Snapshot of a conversation's usage accumulators for UI display (see Conversation.GetUsageStats).
    internal sealed class UsageStats
    {
        public decimal TotalCost;
        public decimal TotalCacheDiscount;
        public long TotalPromptTokens;
        public long TotalCachedTokens;
        public long TotalCompletionTokens;
        public long TotalReasoningTokens;
        public int LastPromptTokens;
        public int LastCachedTokens;
        public int LastCacheWriteTokens;
    }
}
