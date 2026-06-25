using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxPT
{
    // Supplies the capability annotations (readOnlyHint/destructiveHint) a discovered tool declared,
    // keyed by qualified function name. Implemented by McpToolRegistry; injected into ToolApprovalPolicy
    // so third-party tools are classified by their declared hints rather than always falling through to
    // the conservative Write/Tool tier. Returns null when the tool set no annotations.
    internal interface IToolAnnotationSource
    {
        JObject AnnotationsFor(string functionName);
    }

    // The real approval gate (approval spec §3) behind the orchestrator's IToolApprovalPolicy.Check.
    // Classifies the tool, consults remembered approvals (tool-name set + argument rules), and only
    // prompts (via IToolApprovalPrompt) when not already allowed; persists the user's remembered
    // choice. Pure logic + an injected prompt + an injected store — no UI here, so it's unit-testable.
    internal sealed class ToolApprovalPolicy : IToolApprovalPolicy
    {
        // Identifies first-party servers (whose tools use the hardcoded classification table).
        private static readonly Dictionary<string, bool> _firstPartyServers = BuildFirstPartySet();

        private readonly IToolClassifier _classifier;
        private readonly IToolApprovalPrompt _prompt;
        private readonly IApprovalStore _store;
        private readonly IToolAnnotationSource _annotations;
        private string _workdirKey; // canonical working dir for this turn (null = no workspace)

        // The working directory of the turn this policy serves, set per-turn by the host (mirrors
        // McpChatOrchestrator.WorkingDir). Stored canonicalized so a "all edits in this workspace"
        // approval matches the same folder regardless of separator/case/trailing-slash differences,
        // and never matches a different workspace. Null/empty clears it (no workdir-scoped approvals).
        public string WorkingDir
        {
            set { _workdirKey = CanonicalWorkdir(value); }
        }

        public ToolApprovalPolicy(IToolClassifier classifier, IToolApprovalPrompt prompt, IApprovalStore store)
            : this(classifier, prompt, store, null)
        {
        }

        // annotations: source of each tool's declared readOnlyHint/destructiveHint (the registry). When
        // null, no annotations are available and every non-first-party tool falls back to Write/Tool.
        public ToolApprovalPolicy(IToolClassifier classifier, IToolApprovalPrompt prompt, IApprovalStore store,
            IToolAnnotationSource annotations)
        {
            _classifier = classifier != null ? classifier : new ToolClassifier();
            _prompt = prompt;
            _store = store != null ? store : new InMemoryApprovalStore();
            _annotations = annotations;
        }

        // Classifies a qualified tool name into its ToolPolicy (tier + remember scope). First-party tools
        // are classified by the authoritative hardcoded table (annotations ignored), so only look them up
        // for third-party tools. Absent annotations -> null -> classifier fails closed (Write/Tool), so a
        // forgotten readOnlyHint never silently widens access. Shared by Check and the public TierOf.
        private ToolPolicy ClassifyTool(string functionName)
        {
            string server = ServerOf(functionName);
            bool firstParty = server != null && _firstPartyServers.ContainsKey(server);
            JObject annotations = (!firstParty && _annotations != null)
                ? _annotations.AnnotationsFor(functionName)
                : null;
            return _classifier.Classify(functionName, annotations, firstParty);
        }

        // The classified tier of a tool, by qualified name - used by AgentToolResolver to enforce a
        // sub-agent's max_tier ceiling with the same classification the approval gate uses.
        public ToolTier TierOf(string functionName)
        {
            return ClassifyTool(functionName).Tier;
        }

        // The orchestrator calls this. functionName is the qualified name; args already parsed.
        public ApprovalDecision Check(string functionName, JObject args)
        {
            string server = ServerOf(functionName);
            ToolPolicy pol = ClassifyTool(functionName);

            // Read-only tools never modify anything -> always allowed, no prompt. Driven by the
            // classified tier (not a name list), so every ReadOnly tool auto-allows: the read-only
            // built-ins (files read/list/search, git status/diff/log/fetch, web search/extract,
            // memory read_memory) and any future ReadOnly tool. A third-party tool now reaches ReadOnly
            // too when it declares readOnlyHint:true (plumbed above); if you don't want to trust a
            // server's self-declared readOnlyHint, add "&& firstParty" here.
            if (pol.Tier == ToolTier.ReadOnly)
                return ApprovalDecision.Allow;

            // Already-remembered fast paths.
            if (pol.Scope == RememberScope.Tool && _store.IsToolApproved(functionName))
                return ApprovalDecision.Allow;
            // Blanket "all edits in this workspace" approval: covers every Write-tier path-scoped files
            // tool (write/edit) for the active workspace. Gated on Tier==Write so Destructive path tools
            // (files__delete) are never swept in, and on a non-null workdir so it can't match globally.
            if (IsWorkdirWriteEligible(pol) && _workdirKey != null
                && _store.IsWorkdirApproved(server, _workdirKey))
                return ApprovalDecision.Allow;
            if (pol.Scope == RememberScope.Argument && MatchesAnyRule(functionName, pol, args))
                return ApprovalDecision.Allow;
            // Skill-script approvals: either a per-skill blanket (any script in the skill) or this one
            // exact script (skill + relpath). An injected call for a different skill/script won't match.
            if (pol.Scope == RememberScope.SkillScript && MatchesSkillScriptRule(functionName, args))
                return ApprovalDecision.Allow;

            // Not remembered (or Scope==None) -> prompt.
            if (_prompt == null) return ApprovalDecision.Deny; // no UI available -> safe default

            ApprovalRequest req = new ApprovalRequest();
            req.ServerName = server;
            req.FunctionName = functionName;
            req.ToolName = ToolOf(functionName);
            req.Policy = pol;
            req.Arguments = args;
            req.Preview = BuildPreview(functionName, pol, args);

            ApprovalChoice choice = _prompt.Ask(req);
            Persist(req, choice);
            return choice == ApprovalChoice.Deny ? ApprovalDecision.Deny : ApprovalDecision.Allow;
        }

        // ---- remembered-rule matching (approval spec §3) ----

        private bool MatchesAnyRule(string functionName, ToolPolicy pol, JObject args)
        {
            string val = ArgValue(args, pol.ScopeArgPath);
            if (val == null) return false;

            IList<ApprovalRule> rules = _store.RulesFor(functionName);
            for (int i = 0; i < rules.Count; i++)
            {
                ApprovalRule r = rules[i];
                if (r == null || r.ArgPath != pol.ScopeArgPath) continue;
                if (r.Kind == RuleKind.ExactArgs)
                {
                    if (string.Equals(val, r.Pattern, StringComparison.Ordinal)) return true;
                }
                else // Prefix
                {
                    if (pol.ScopeArgPath == "path")
                    {
                        if (PrefixMatches(val, r.Pattern, true)) return true;
                    }
                    else
                    {
                        // Command "pattern" rule: match by normalized signature (command + its
                        // subcommand/file/first-flag, flags otherwise ignored), computed identically
                        // for the stored rule and the candidate. NOT a prefix — see CommandSignature.
                        if (string.Equals(CommandSignature(val), r.Pattern, StringComparison.Ordinal)) return true;
                    }
                }
            }
            return false;
        }

        // ---- skill-script rule matching (run_skill_script's two remember dimensions) ----

        // True when a remembered skill-script rule covers this (slug, relpath) call. Two rule shapes,
        // both stored as ExactArgs (see Persist):
        //   ArgPath "slug"    -> blanket for the whole skill: matches any relpath in that slug.
        //   ArgPath "relpath" -> this exact script: Pattern is "<slugKey>\0<relKey>" so a script with
        //                        the same relative path in a DIFFERENT skill never collides.
        // Both keys are normalized the same way here and in Persist, so storage and lookup agree.
        private bool MatchesSkillScriptRule(string functionName, JObject args)
        {
            string slug = SkillSlugKey(ArgValue(args, "slug"));
            if (slug.Length == 0) return false;
            string exactKey = slug + "\0" + NormalizeRelpath(ArgValue(args, "relpath"));

            IList<ApprovalRule> rules = _store.RulesFor(functionName);
            for (int i = 0; i < rules.Count; i++)
            {
                ApprovalRule r = rules[i];
                if (r == null || r.Kind != RuleKind.ExactArgs) continue;
                if (r.ArgPath == "slug")
                {
                    if (string.Equals(slug, r.Pattern, StringComparison.Ordinal)) return true;
                }
                else if (r.ArgPath == "relpath")
                {
                    if (string.Equals(exactKey, r.Pattern, StringComparison.Ordinal)) return true;
                }
            }
            return false;
        }

        // Stable comparison key for a skill slug: trimmed + lowercased. The host doesn't run the
        // server's SkillSlug.Make, but a slug is already kebab-case by contract, so this only reconciles
        // incidental case/whitespace so the stored rule and a later call agree.
        private static string SkillSlugKey(string slug)
        {
            return string.IsNullOrEmpty(slug) ? string.Empty : slug.Trim().ToLowerInvariant();
        }

        // Stable comparison key for a script's relpath: '/'-normalized (NormalizePath) and lowercased,
        // since the relpath confines to a folder and Windows paths compare case-insensitively.
        private static string NormalizeRelpath(string rel)
        {
            return NormalizePath(rel).ToLowerInvariant();
        }

        // Boundary-aware prefix match (security, spec §3):
        //  - path: directory-boundary aware ("/a/b" matches "/a/b/c", not "/a/bc")
        //  - command: token-aware ("git status" matches "git status -s", not "git status-hack")
        internal static bool PrefixMatches(string value, string pattern, bool isPath)
        {
            if (value == null || pattern == null) return false;

            if (isPath)
            {
                // Normalize separators so a rule stored with one separator (e.g. '\' from
                // Path.GetDirectoryName on Windows) still matches a value using the other ('/' from
                // the model). Compare case-insensitively (Windows paths).
                value = NormalizePath(value);
                pattern = NormalizePath(pattern);

                // An empty pattern is the workspace root: matches any relative path under it.
                if (pattern.Length == 0) return true;
                if (value.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return true;
                if (!value.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)) return false;
                // Directory boundary: the char after the pattern must be a separator
                // ("sub" matches "sub/a", not "subdir/a").
                return value[pattern.Length] == '/';
            }

            // command: token-aware prefix.
            if (value.Equals(pattern, StringComparison.Ordinal)) return true;
            if (!value.StartsWith(pattern, StringComparison.Ordinal)) return false;
            char next = value[pattern.Length];
            return next == ' ' || next == '\t';
        }

        // Canonical relative-path form for comparison: '\' -> '/', collapse a leading './', drop any
        // trailing separator.
        private static string NormalizePath(string p)
        {
            if (string.IsNullOrEmpty(p)) return string.Empty;
            p = p.Replace('\\', '/');
            while (p.StartsWith("./", StringComparison.Ordinal)) p = p.Substring(2);
            while (p.Length > 1 && p[p.Length - 1] == '/') p = p.Substring(0, p.Length - 1);
            return p;
        }

        // ---- persistence of the user's choice ----

        private void Persist(ApprovalRequest req, ApprovalChoice choice)
        {
            switch (choice)
            {
                case ApprovalChoice.RememberTool:
                    _store.AddApprovedTool(req.FunctionName);
                    break;
                case ApprovalChoice.RememberExactArg:
                    _store.AddRule(new ApprovalRule(req.FunctionName, RuleKind.ExactArgs,
                        req.Policy.ScopeArgPath, ArgValue(req.Arguments, req.Policy.ScopeArgPath)));
                    break;
                case ApprovalChoice.RememberPrefixArg:
                    _store.AddRule(new ApprovalRule(req.FunctionName, RuleKind.Prefix,
                        req.Policy.ScopeArgPath, PrefixPattern(req)));
                    break;
                case ApprovalChoice.RememberWorkdirWrites:
                    // Approve every Write-tier files tool in the active workspace. No-op without a
                    // workdir (nothing to scope the blanket approval to).
                    if (_workdirKey != null) _store.AddApprovedWorkdir(req.ServerName, _workdirKey);
                    break;
                case ApprovalChoice.RememberSkillScripts:
                {
                    // Blanket for the whole skill: an ExactArgs rule on the slug (any relpath matches).
                    string slug = SkillSlugKey(ArgValue(req.Arguments, "slug"));
                    if (slug.Length > 0)
                        _store.AddRule(new ApprovalRule(req.FunctionName, RuleKind.ExactArgs, "slug", slug));
                    break;
                }
                case ApprovalChoice.RememberSkillScript:
                {
                    // This exact script: an ExactArgs rule on relpath, keyed by "<slug>\0<relpath>" so a
                    // same-named script in another skill never matches. No-op without a slug.
                    string slug = SkillSlugKey(ArgValue(req.Arguments, "slug"));
                    if (slug.Length > 0)
                        _store.AddRule(new ApprovalRule(req.FunctionName, RuleKind.ExactArgs, "relpath",
                            slug + "\0" + NormalizeRelpath(ArgValue(req.Arguments, "relpath"))));
                    break;
                }
                // AllowOnce / Deny: nothing persisted.
            }
        }

        // The structured pattern remembered for a RememberPrefixArg choice, derived from the actual
        // argument (no free-form entry — spec §3/§4): a "directory and below" prefix for a path rule,
        // or the normalized command signature (see CommandSignature) for a command rule.
        private static string PrefixPattern(ApprovalRequest req)
        {
            string val = ArgValue(req.Arguments, req.Policy.ScopeArgPath);
            if (val == null) return string.Empty;
            if (req.Policy.ScopeArgPath == "path")
            {
                // "directory and below": the rule is the file's PARENT directory, so other files in
                // the same folder match. Compute it with normalized '/' separators (NOT
                // Path.GetDirectoryName, which yields '\' on Windows and wouldn't match the model's
                // forward-slash paths). A root-level file yields "" — the workspace root.
                string norm = NormalizePath(val);
                int slash = norm.LastIndexOf('/');
                return slash < 0 ? string.Empty : norm.Substring(0, slash);
            }
            // command: the normalized signature (see CommandSignature).
            return CommandSignature(val);
        }

        // The "command pattern" signature: the invariant identity of a command, ignoring incidental
        // flags/arguments so re-runs that differ only in options still match. Computed identically when
        // a rule is stored and when a candidate is checked, then compared for equality (NOT a prefix).
        //
        //   command + 2nd token            when the 2nd token is NOT a flag   (git status, del foo.txt)
        //   command + first file/path tok  when the 2nd token IS a flag       (powershell hello.ps1)
        //   command + first flag           fallback: flag, no file/path found (dir /s, powershell -Command)
        //
        // Rationale and limits (flag arity is unknowable without per-tool grammar) are discussed in the
        // approval design notes; the file/path heuristic only shifts where the signature ends, so a
        // misdetection is benign (re-prompt), never a silent widening.
        internal static string CommandSignature(string command)
        {
            if (command == null) return string.Empty;
            string trimmed = command.Trim();
            if (trimmed.Length == 0) return string.Empty;
            // A multi-line command (typically a PowerShell script) has no meaningful "program + first
            // operand" identity - reducing it to its first line's leading tokens would let a DIFFERENT
            // multi-line script that merely opens the same way match a remembered "command pattern" rule.
            // Match such commands EXACTLY instead (the safe direction: re-prompt rather than silently
            // auto-allow a script the user never approved).
            if (trimmed.IndexOf('\n') >= 0 || trimmed.IndexOf('\r') >= 0) return trimmed;
            string[] t = TokenizeCommand(trimmed);
            if (t.Length == 0) return string.Empty;
            if (t.Length == 1) return t[0];

            if (!IsFlagToken(t[1])) return t[0] + " " + t[1];

            // 2nd token is a flag: prefer the first file/path-shaped operand (skipping flags and their
            // space-separated values, which aren't path-shaped).
            for (int i = 1; i < t.Length; i++)
            {
                if (!IsFlagToken(t[i]) && LooksLikePath(t[i])) return t[0] + " " + t[i];
            }
            // No concrete operand -> keep today's behavior (command + first flag).
            return t[0] + " " + t[1];
        }

        // Split a single-line command into whitespace-separated tokens, keeping a quoted span ("..." or
        // '...') as ONE token (quotes preserved) so a path with interior spaces — e.g.
        // powershell "C:\Program Files\app.ps1" — isn't broken at the first space. Only space/tab
        // separate tokens; multi-line commands never reach here (CommandSignature matches them exactly),
        // so a newline is never a separator. This is a signature/display heuristic, not a shell parser:
        // it doesn't process backslash escapes or nested quotes, which the signature's downstream use
        // (compare-for-equality, ignore flags) doesn't need. Quotes are kept in the token text so the
        // displayed pattern stays unambiguous; the path/flag heuristics strip them where needed
        // (LooksLikePath, IsFlagToken).
        private static string[] TokenizeCommand(string command)
        {
            List<string> tokens = new List<string>();
            if (string.IsNullOrEmpty(command)) return tokens.ToArray();

            StringBuilder cur = new StringBuilder();
            bool inToken = false;
            char quote = '\0';
            for (int i = 0; i < command.Length; i++)
            {
                char c = command[i];
                if (quote != '\0')
                {
                    cur.Append(c);
                    if (c == quote) quote = '\0';
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                    cur.Append(c);
                    inToken = true;
                }
                else if (c == ' ' || c == '\t')
                {
                    if (inToken) { tokens.Add(cur.ToString()); cur.Length = 0; inToken = false; }
                }
                else
                {
                    cur.Append(c);
                    inToken = true;
                }
            }
            if (inToken) tokens.Add(cur.ToString());
            return tokens.ToArray();
        }

        private static bool IsFlagToken(string tok)
        {
            if (string.IsNullOrEmpty(tok)) return false;
            char c = tok[0];
            return c == '-' || c == '/';
        }

        // Recognized script/executable extensions (case-insensitive). Kept deliberately small; extend
        // as needed. A token is also treated as a path if it contains a separator.
        private static readonly string[] _scriptExtensions =
            { ".ps1", ".bat", ".cmd", ".sh", ".py", ".js", ".ts", ".rb", ".pl", ".php", ".lua",
              ".exe", ".com", ".sln", ".csproj", ".vbs", ".psm1" };

        private static bool LooksLikePath(string tok)
        {
            if (string.IsNullOrEmpty(tok)) return false;
            // Strip a wrapping/ trailing quote so e.g. hello.ps1" still reads as a script.
            string s = tok.Trim('"', '\'');
            if (s.Length == 0) return false;
            if (s.IndexOf('/') >= 0 || s.IndexOf('\\') >= 0) return true;
            for (int i = 0; i < _scriptExtensions.Length; i++)
                if (s.EndsWith(_scriptExtensions[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ---- helpers ----

        // The shape a "all edits in this workspace" approval covers: a remember-eligible Write-tier
        // path-scoped tool (files__write / files__edit). Tier==Write deliberately excludes the
        // Destructive files__delete, which shares the same Argument/path scope.
        private static bool IsWorkdirWriteEligible(ToolPolicy pol)
        {
            return pol != null && pol.Tier == ToolTier.Write
                && pol.Scope == RememberScope.Argument && pol.ScopeArgPath == "path";
        }

        // Canonical key for a working directory: '/'-separated, no trailing slash, lowercased (Windows
        // paths compare case-insensitively, matching the OrdinalIgnoreCase path rules above). Used as
        // the stable lookup key for workdir-scoped approvals so the same folder always matches and
        // different folders never collide. The host supplies an already-absolute path (ctx.WorkingDir),
        // so this only reconciles separator/case/trailing-slash spelling — no filesystem resolution
        // (which would be platform-dependent for drive rooting).
        private static string CanonicalWorkdir(string workdir)
        {
            if (string.IsNullOrEmpty(workdir)) return null;
            string p = workdir.Replace('\\', '/');
            while (p.Length > 1 && p[p.Length - 1] == '/') p = p.Substring(0, p.Length - 1);
            return p.ToLowerInvariant();
        }

        private static string BuildPreview(string functionName, ToolPolicy pol, JObject args)
        {
            if (pol.Scope == RememberScope.Argument && pol.ScopeArgPath != null)
            {
                string v = ArgValue(args, pol.ScopeArgPath);
                if (v != null) return v;
            }
            return args != null ? args.ToString(Formatting.None) : string.Empty;
        }

        private static string ArgValue(JObject args, string path)
        {
            if (args == null || string.IsNullOrEmpty(path)) return null;
            JToken t = args[path];
            if (t == null || t.Type == JTokenType.Null) return null;
            return t.Type == JTokenType.String ? (string)t : t.ToString(Formatting.None);
        }

        private static string ServerOf(string functionName)
        {
            if (string.IsNullOrEmpty(functionName)) return null;
            int i = functionName.IndexOf("__", StringComparison.Ordinal);
            return i > 0 ? functionName.Substring(0, i) : null;
        }

        private static string ToolOf(string functionName)
        {
            if (string.IsNullOrEmpty(functionName)) return functionName;
            int i = functionName.IndexOf("__", StringComparison.Ordinal);
            return i >= 0 ? functionName.Substring(i + 2) : functionName;
        }

        private static Dictionary<string, bool> BuildFirstPartySet()
        {
            var s = new Dictionary<string, bool>(StringComparer.Ordinal);
            s[McpConfig.WebName] = true;
            s[McpConfig.FilesName] = true;
            s[McpConfig.GitName] = true;
            s[McpConfig.CommandName] = true;
            s[McpConfig.MsBuildName] = true;
            s[McpConfig.MemoryName] = true;
            s[McpConfig.ExtensionsName] = true;
            // GitHub is a first-party-managed HTTP server but a third-party tool surface; classify
            // its tools via annotations (not the hardcoded table).
            return s;
        }
    }
}
