using Newtonsoft.Json.Linq;

namespace GxPT
{
    // Approval tiers and remember-scope (approval spec §2).
    internal enum ToolTier { ReadOnly, Write, Destructive }
    //   None      - never remembered (always prompts), e.g. git__push.
    //   Tool      - remember the whole function name.
    //   Argument  - remember a rule over one argument (command/path), see ScopeArgPath.
    //   SkillScript - extensions__run_skill_script only: two remember dimensions (the whole skill,
    //                 or one exact script). Scoped on (slug) and (slug, relpath) rather than a single
    //                 argument, so it gets its own scope rather than overloading Argument.
    internal enum RememberScope { None, Tool, Argument, SkillScript }

    // The classification of a tool: how much friction (tier) and how an approval may be remembered.
    internal sealed class ToolPolicy
    {
        public ToolTier Tier;
        public RememberScope Scope;
        public string ScopeArgPath;   // argument name when Scope==Argument (e.g. "command", "path")

        public ToolPolicy(ToolTier tier, RememberScope scope, string scopeArgPath)
        {
            Tier = tier; Scope = scope; ScopeArgPath = scopeArgPath;
        }
    }

    // A remembered argument-scoped rule (approval spec §3). Tool-scope approvals are stored as a
    // plain function-name set; these cover command__run / files__* granular allowlisting.
    internal enum RuleKind { ExactArgs, Prefix }

    internal sealed class ApprovalRule
    {
        public string FunctionName;
        public RuleKind Kind;
        public string ArgPath;   // which argument (e.g. "command", "path")
        public string Pattern;   // exact value | prefix

        public ApprovalRule() { }
        public ApprovalRule(string functionName, RuleKind kind, string argPath, string pattern)
        {
            FunctionName = functionName; Kind = kind; ArgPath = argPath; Pattern = pattern;
        }
    }

    // Everything the gate (and the confirmation UI) needs about one pending call.
    internal sealed class ApprovalRequest
    {
        public string ServerName;
        public string FunctionName;   // qualified (e.g. "command__run")
        public string ToolName;       // original server tool name (e.g. "run")
        public ToolPolicy Policy;
        public JObject Arguments;
        public string Preview;        // resolved command line / target path, for the dangerous surface
    }

    // What the user chose at the confirmation prompt. The policy maps this onto an outcome plus what
    // (if anything) to persist.
    internal enum ApprovalChoice
    {
        Deny,
        AllowOnce,
        RememberTool,        // Tool scope: remember the whole function name
        RememberExactArg,    // Argument scope: ExactArgs rule on ScopeArgPath
        RememberPrefixArg,   // Argument scope: Prefix rule (base+sub / directory-and-below)
        RememberWorkdirWrites, // Workdir scope: every Write-tier files__ tool in the active workspace
        RememberSkillScripts, // SkillScript scope: every script bundled with this skill (by slug)
        RememberSkillScript   // SkillScript scope: this exact script only (by slug + relpath)
    }

    // The confirmation surface the policy invokes when a call isn't already allowed. Implemented by
    // the host (in-transcript confirmation, 3b); tests inject a scripted prompt. Returns synchronously
    // (the tool-loop worker blocks here while the user decides).
    internal interface IToolApprovalPrompt
    {
        ApprovalChoice Ask(ApprovalRequest req);
    }
}
