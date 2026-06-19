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

        // A sane bound on one batch, so a single tool call can't fan out unboundedly.
        private const int MaxAgentsPerCall = 8;

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

        // The OpenAI-style function definition: dispatch_agent({ agents: [{ name, task }] }).
        public JObject DispatchAgentDef()
        {
            JObject nameP = new JObject(); nameP["type"] = "string";
            JObject taskP = new JObject(); taskP["type"] = "string";
            JObject entryProps = new JObject();
            entryProps["name"] = nameP;
            entryProps["task"] = taskP;
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
            List<string[]> entries = ParseEntries(argumentsJson);   // each = { name, task }
            if (entries.Count == 0) return "No agents specified to dispatch.";

            int n = entries.Count;
            bool truncated = n > MaxAgentsPerCall;
            if (truncated) n = MaxAgentsPerCall;

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                string name = entries[i][0];
                string task = entries[i][1];

                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append("## Agent: ").Append(name != null ? name : "(null)").Append('\n');

                Agent agent;
                if (name == null || !_bySlug.TryGetValue(name, out agent))
                {
                    sb.Append("Unknown agent: ").Append(name != null ? name : "(null)");
                    continue;
                }
                if (string.IsNullOrEmpty(task))
                {
                    sb.Append("No task was provided for this agent.");
                    continue;
                }
                sb.Append(RunChild(agent, task));
            }
            if (truncated)
                sb.Append("\n\n[Note: only the first ").Append(MaxAgentsPerCall)
                  .Append(" agents in this call were dispatched.]");
            return sb.ToString();
        }

        // Builds and runs one child orchestrator to completion, returning its final answer.
        private string RunChild(Agent agent, string task)
        {
            string model = !string.IsNullOrEmpty(agent.Model) ? agent.Model : _parentModel;
            int maxIter = agent.MaxTurns > 0 ? agent.MaxTurns : _defaultMaxIterations;

            McpChatOrchestrator child = new McpChatOrchestrator(_streamer, _registry, _approval, model,
                                                                _log, maxIter, _callTimeoutMs);
            child.WorkingDir = _workingDir;
            child.Cancellation = Cancellation;
            child.UsageReported = UsageReported;

            // Restrict the child to the agent's effective tool set by hiding everything else the parent can
            // call (no escalation, A11). Not setting an AgentDispatcher on the child means it has no
            // dispatch_agent and cannot nest (A12).
            if (_registry != null)
            {
                IList<string> parentNames = _registry.NamesForWorkdir(_workingDir);
                List<string> hidden = AgentToolResolver.Hidden(agent.Tools, agent.MaxTier, parentNames, _tierOf);
                if (hidden.Count > 0) child.HiddenToolNames = hidden;
            }

            List<ChatMessage> history = new List<ChatMessage>();
            string body = ReadBody(agent);                       // fresh read, so SKILL/AGENT edits apply
            if (!string.IsNullOrEmpty(body))
                history.Add(new ChatMessage("system", body));    // the agent's persona, after the standing head
            history.Add(new ChatMessage("user", task));

            try
            {
                child.RunTurn(history, NullToolLoopUi.Instance);
            }
            catch (Exception ex)
            {
                _log.Log("agents", "child '" + agent.Slug + "' threw: " + ex.Message);
                return "[agent error: " + ex.Message + "]";
            }
            return ExtractFinalAnswer(history);
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

        // Parses { agents: [ { name, task }, ... ] } into a list of {name, task} pairs. Malformed input
        // yields an empty list (the tool then reports "no agents"), never an exception.
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
                        list.Add(new string[] { AsString(e["name"]), AsString(e["task"]) });
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

        // A no-op transcript UI: a child's live activity is not surfaced to the parent model (A7); rich
        // per-child UI is the phase-8 observability work. The answer is read from history, not the UI.
        private sealed class NullToolLoopUi : IToolLoopUi
        {
            public static readonly NullToolLoopUi Instance = new NullToolLoopUi();
            public void AppendTextDelta(string text) { }
            public void OnToolCall(string functionName, string argumentsJson) { }
            public void OnToolResult(string functionName, string resultText, bool isError) { }
            public void OnError(string message) { }
            public void Complete() { }
        }
    }
}
