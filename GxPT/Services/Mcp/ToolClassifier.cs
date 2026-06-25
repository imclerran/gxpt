using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxPT
{
    // Classifies a tool into a ToolPolicy (tier + remember scope), per approval spec §2:
    //   1. reveal_tools is handled upstream (never reaches here / never gated).
    //   2. First-party tools use a hardcoded, authoritative table (annotations ignored).
    //   3. Third-party tools use advisory annotations (readOnlyHint / destructiveHint); since we
    //      can't infer their argument semantics, they never get Argument scope.
    //   4. Unknown / unannotated -> Write/Tool.
    internal interface IToolClassifier
    {
        ToolPolicy Classify(string functionName, JObject annotations, bool isFirstParty);
    }

    internal sealed class ToolClassifier : IToolClassifier
    {
        // First-party table keyed by qualified function name (approval spec §2).
        private static readonly Dictionary<string, ToolPolicy> _firstParty = BuildFirstPartyTable();

        private static Dictionary<string, ToolPolicy> BuildFirstPartyTable()
        {
            var t = new Dictionary<string, ToolPolicy>(StringComparer.Ordinal);
            t["files__read"] = new ToolPolicy(ToolTier.ReadOnly, RememberScope.Tool, null);
            t["files__list"] = new ToolPolicy(ToolTier.ReadOnly, RememberScope.Tool, null);
            t["files__search"] = new ToolPolicy(ToolTier.ReadOnly, RememberScope.Tool, null);
            t["files__write"] = new ToolPolicy(ToolTier.Write, RememberScope.Argument, "path");
            t["files__edit"] = new ToolPolicy(ToolTier.Write, RememberScope.Argument, "path");
            t["files__delete"] = new ToolPolicy(ToolTier.Destructive, RememberScope.Argument, "path");
            t["git__status"] = new ToolPolicy(ToolTier.ReadOnly, RememberScope.Tool, null);
            t["git__diff"] = new ToolPolicy(ToolTier.ReadOnly, RememberScope.Tool, null);
            t["git__log"] = new ToolPolicy(ToolTier.ReadOnly, RememberScope.Tool, null);
            t["git__commit"] = new ToolPolicy(ToolTier.Write, RememberScope.Tool, null);
            t["git__push"] = new ToolPolicy(ToolTier.Destructive, RememberScope.None, null);
            // fetch only updates remote-tracking refs (no working-tree change) -> ReadOnly/auto-allow.
            t["git__fetch"] = new ToolPolicy(ToolTier.ReadOnly, RememberScope.Tool, null);
            // Stage/branch/stash: mutate the index or refs but don't discard work -> Write (prompt once).
            t["git__add"] = new ToolPolicy(ToolTier.Write, RememberScope.Tool, null);
            t["git__branch"] = new ToolPolicy(ToolTier.Write, RememberScope.Tool, null);
            t["git__stash"] = new ToolPolicy(ToolTier.Write, RememberScope.Tool, null);
            // Can lose uncommitted work, move HEAD, or rewrite history -> Destructive (prompt every time).
            t["git__pull"] = new ToolPolicy(ToolTier.Destructive, RememberScope.None, null);
            t["git__checkout"] = new ToolPolicy(ToolTier.Destructive, RememberScope.None, null);
            t["git__restore"] = new ToolPolicy(ToolTier.Destructive, RememberScope.None, null);
            t["git__merge"] = new ToolPolicy(ToolTier.Destructive, RememberScope.None, null);
            t["git__rebase"] = new ToolPolicy(ToolTier.Destructive, RememberScope.None, null);
            t["git__reset"] = new ToolPolicy(ToolTier.Destructive, RememberScope.None, null);
            t["git__rm"] = new ToolPolicy(ToolTier.Destructive, RememberScope.None, null);
            t["git__cherry_pick"] = new ToolPolicy(ToolTier.Destructive, RememberScope.None, null);
            t["command__run"] = new ToolPolicy(ToolTier.Destructive, RememberScope.Argument, "command");
            t["web__search"] = new ToolPolicy(ToolTier.ReadOnly, RememberScope.Tool, null);
            // extract only fetches and returns page content (no state change) -> ReadOnly/auto-allow.
            t["web__extract"] = new ToolPolicy(ToolTier.ReadOnly, RememberScope.Tool, null);
            // get is an HTTP GET (no body sent, can't mutate remote state) -> ReadOnly/auto-allow, the
            // same egress posture as extract.
            t["web__get"] = new ToolPolicy(ToolTier.ReadOnly, RememberScope.Tool, null);
            // http issues a state-changing request (POST/PUT/PATCH/DELETE to any http(s) URL): a
            // remote-mutation + data-egress + SSRF surface, so Destructive and confirmed EVERY time
            // (Scope=None, like git__push) - the user sees the exact method+URL before anything leaves.
            t["web__http"] = new ToolPolicy(ToolTier.Destructive, RememberScope.None, null);
            // Memory: reads auto-allow; writes are low-risk local .gxpt edits -> Write (prompt once,
            // remember-eligible per tool). forget stays Write rather than Destructive (design M7/sec.8).
            t["memory__read_memory"] = new ToolPolicy(ToolTier.ReadOnly, RememberScope.Tool, null);
            t["memory__remember"] = new ToolPolicy(ToolTier.Write, RememberScope.Tool, null);
            t["memory__update_memory"] = new ToolPolicy(ToolTier.Write, RememberScope.Tool, null);
            t["memory__forget"] = new ToolPolicy(ToolTier.Write, RememberScope.Tool, null);
            t["memory__consolidate"] = new ToolPolicy(ToolTier.Write, RememberScope.Tool, null);
            // Skills authoring (design S16) on the extensions server: .gxpt/skills edits. create_skill is
            // per-tool (each call is a new slug); the Write edits are argument-scoped on the skill being
            // edited ("always allow editing skill X"). list/validate are ReadOnly (auto-allowed); delete_*
            // are Destructive and confirmed every time.
            t["extensions__create_skill"] = new ToolPolicy(ToolTier.Write, RememberScope.Tool, null);
            t["extensions__write_skill_file"] = new ToolPolicy(ToolTier.Write, RememberScope.Argument, "slug");
            t["extensions__update_skill"] = new ToolPolicy(ToolTier.Write, RememberScope.Argument, "slug");
            t["extensions__edit_skill_file"] = new ToolPolicy(ToolTier.Write, RememberScope.Argument, "slug");
            // rename moves the folder/file (a slug change); content is preserved and a collision is refused,
            // so it's Write, but it's a structural change to the handle -> confirmed every time (Scope.None,
            // never blanket-remembered), like the agent rename below.
            t["extensions__rename_skill"] = new ToolPolicy(ToolTier.Write, RememberScope.None, null);
            t["extensions__list_skill_files"] = new ToolPolicy(ToolTier.ReadOnly, RememberScope.Tool, null);
            t["extensions__validate_skill"] = new ToolPolicy(ToolTier.ReadOnly, RememberScope.Tool, null);
            t["extensions__delete_skill_file"] = new ToolPolicy(ToolTier.Destructive, RememberScope.None, null);
            t["extensions__delete_skill"] = new ToolPolicy(ToolTier.Destructive, RememberScope.None, null);
            // Agent authoring (design A4/phase 10) on the same server: .gxpt/agents edits, mirroring the
            // skill tiers. create_agent is per-tool; update/edit are argument-scoped on the agent's slug;
            // read/list/validate are ReadOnly; delete_agent is Destructive and confirmed every time.
            t["extensions__create_agent"] = new ToolPolicy(ToolTier.Write, RememberScope.Tool, null);
            t["extensions__update_agent"] = new ToolPolicy(ToolTier.Write, RememberScope.Argument, "slug");
            t["extensions__edit_agent"] = new ToolPolicy(ToolTier.Write, RememberScope.Argument, "slug");
            // rename moves <slug>.md (a slug change); content preserved, collision refused -> Write, but a
            // structural handle change, so confirmed every time (Scope.None, never blanket-remembered).
            t["extensions__rename_agent"] = new ToolPolicy(ToolTier.Write, RememberScope.None, null);
            t["extensions__read_agent"] = new ToolPolicy(ToolTier.ReadOnly, RememberScope.Tool, null);
            t["extensions__list_agents"] = new ToolPolicy(ToolTier.ReadOnly, RememberScope.Tool, null);
            t["extensions__validate_agent"] = new ToolPolicy(ToolTier.ReadOnly, RememberScope.Tool, null);
            t["extensions__delete_agent"] = new ToolPolicy(ToolTier.Destructive, RememberScope.None, null);
            // Skills execution (design S9/S11): running a skill's batch is Destructive and confirmed every
            // time (like git push / command__run's heavier siblings) - the model can run only a named
            // skill's declared .bat with literal args, never an arbitrary command.
            t["extensions__run_skill_script"] = new ToolPolicy(ToolTier.Destructive, RememberScope.None, null);
            return t;
        }

        public ToolPolicy Classify(string functionName, JObject annotations, bool isFirstParty)
        {
            if (isFirstParty && functionName != null)
            {
                ToolPolicy fp;
                if (_firstParty.TryGetValue(functionName, out fp))
                    return new ToolPolicy(fp.Tier, fp.Scope, fp.ScopeArgPath);
                // The MSBuild server surfaces build tools per installed engine/IDE, so its tool names are
                // discovered at runtime (msbuild__build_4_0, msbuild__build_solution_2022, ...) and can't
                // be listed in the static table. A build runs arbitrary build logic (targets, Exec tasks,
                // pre/post build events), so it is Destructive and argument-scoped on the project/solution
                // being built - "always allow building this one", never a blanket "always allow MSBuild".
                // devenv solution builds scope on the 'solution' arg; MSBuild engine builds on 'project'.
                if (functionName.StartsWith(McpConfig.MsBuildName + "__", StringComparison.Ordinal))
                {
                    string scopeArg = functionName.IndexOf("build_solution", StringComparison.Ordinal) >= 0
                        ? "solution" : "project";
                    return new ToolPolicy(ToolTier.Destructive, RememberScope.Argument, scopeArg);
                }
                // First-party but not in the table (shouldn't happen) -> conservative Write/Tool.
                return new ToolPolicy(ToolTier.Write, RememberScope.Tool, null);
            }

            // Third-party: advisory annotation hints only; never Argument scope (we can't infer
            // which argument is a command/path).
            if (annotations != null)
            {
                if (IsTrue(annotations["destructiveHint"]))
                    return new ToolPolicy(ToolTier.Destructive, RememberScope.None, null);
                if (IsTrue(annotations["readOnlyHint"]))
                    return new ToolPolicy(ToolTier.ReadOnly, RememberScope.Tool, null);
            }
            // No annotation / unknown -> Write/Tool (remember-eligible only after a first prompt).
            return new ToolPolicy(ToolTier.Write, RememberScope.Tool, null);
        }

        private static bool IsTrue(JToken t)
        {
            return t != null && t.Type == JTokenType.Boolean && (bool)t;
        }
    }
}
