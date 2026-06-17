using System.Collections.Generic;
using GxPT;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxPT.Tests.Mcp
{
    public class ToolApprovalTests
    {
        // A scripted prompt that records requests and returns a fixed choice.
        private sealed class ScriptedPrompt : IToolApprovalPrompt
        {
            public ApprovalChoice Next = ApprovalChoice.Deny;
            public int Calls;
            public ApprovalRequest Last;
            public ApprovalChoice Ask(ApprovalRequest req) { Calls++; Last = req; return Next; }
        }

        // A fixed annotation source: maps a function name to the readOnlyHint/destructiveHint JObject
        // the discovered tool would carry (mirrors what McpToolRegistry plumbs from tools/list).
        private sealed class FakeAnnotations : IToolAnnotationSource
        {
            private readonly Dictionary<string, JObject> _map = new Dictionary<string, JObject>();
            public FakeAnnotations Set(string fn, JObject ann) { _map[fn] = ann; return this; }
            public JObject AnnotationsFor(string functionName)
            {
                JObject a;
                return _map.TryGetValue(functionName, out a) ? a : null;
            }
        }

        private static JObject Args(string json) { return JObject.Parse(json); }

        private static ToolApprovalPolicy Policy(ScriptedPrompt prompt, IApprovalStore store)
        {
            return new ToolApprovalPolicy(new ToolClassifier(), prompt, store);
        }

        private static ToolApprovalPolicy Policy(ScriptedPrompt prompt, IApprovalStore store, IToolAnnotationSource annotations)
        {
            return new ToolApprovalPolicy(new ToolClassifier(), prompt, store, annotations);
        }

        // ---- classification (spec §2) ----

        [Fact]
        public void First_party_table_is_authoritative()
        {
            var c = new ToolClassifier();
            var read = c.Classify("files__read", null, true);
            Assert.Equal(ToolTier.ReadOnly, read.Tier);
            Assert.Equal(RememberScope.Tool, read.Scope);

            var write = c.Classify("files__write", null, true);
            Assert.Equal(ToolTier.Write, write.Tier);
            Assert.Equal(RememberScope.Argument, write.Scope);
            Assert.Equal("path", write.ScopeArgPath);

            var run = c.Classify("command__run", null, true);
            Assert.Equal(ToolTier.Destructive, run.Tier);
            Assert.Equal(RememberScope.Argument, run.Scope);
            Assert.Equal("command", run.ScopeArgPath);

            var push = c.Classify("git__push", null, true);
            Assert.Equal(ToolTier.Destructive, push.Tier);
            Assert.Equal(RememberScope.None, push.Scope);

            var extract = c.Classify("web__extract", null, true);
            Assert.Equal(ToolTier.ReadOnly, extract.Tier);
            Assert.Equal(RememberScope.Tool, extract.Scope);

            // web__get is an HTTP GET (no remote mutation) -> ReadOnly/auto-allow, like extract.
            var get = c.Classify("web__get", null, true);
            Assert.Equal(ToolTier.ReadOnly, get.Tier);
            Assert.Equal(RememberScope.Tool, get.Scope);

            // web__http makes state-changing HTTP requests (egress/SSRF/remote-mutation surface):
            // Destructive, Scope=None -> confirmed every time, never remembered (like git__push).
            var http = c.Classify("web__http", null, true);
            Assert.Equal(ToolTier.Destructive, http.Tier);
            Assert.Equal(RememberScope.None, http.Scope);

            // MSBuild tools are named per discovered engine, so they're matched by the msbuild__ prefix
            // (not a static table entry) and gated as Destructive, argument-scoped on the project built.
            var build = c.Classify("msbuild__build_17_0", null, true);
            Assert.Equal(ToolTier.Destructive, build.Tier);
            Assert.Equal(RememberScope.Argument, build.Scope);
            Assert.Equal("project", build.ScopeArgPath);

            // devenv solution builds are likewise Destructive, but scoped on the 'solution' argument.
            var sln = c.Classify("msbuild__build_solution_2022", null, true);
            Assert.Equal(ToolTier.Destructive, sln.Tier);
            Assert.Equal(RememberScope.Argument, sln.Scope);
            Assert.Equal("solution", sln.ScopeArgPath);
        }

        [Fact]
        public void Third_party_uses_annotations_else_write_tool()
        {
            var c = new ToolClassifier();
            Assert.Equal(ToolTier.ReadOnly, c.Classify("acme__peek", JObject.Parse("{\"readOnlyHint\":true}"), false).Tier);

            var destr = c.Classify("acme__nuke", JObject.Parse("{\"destructiveHint\":true}"), false);
            Assert.Equal(ToolTier.Destructive, destr.Tier);
            Assert.Equal(RememberScope.None, destr.Scope); // third-party never gets Argument scope

            var unknown = c.Classify("acme__do", null, false);
            Assert.Equal(ToolTier.Write, unknown.Tier);
            Assert.Equal(RememberScope.Tool, unknown.Scope);
        }

        // ---- annotations plumbed end-to-end through the policy (#94) ----

        [Fact]
        public void Third_party_read_only_annotation_auto_allows_without_prompt()
        {
            var prompt = new ScriptedPrompt { Next = ApprovalChoice.Deny };
            var ann = new FakeAnnotations().Set("acme__peek", JObject.Parse("{\"readOnlyHint\":true}"));
            var pol = Policy(prompt, new InMemoryApprovalStore(), ann);

            // The declared readOnlyHint reaches the classifier -> ReadOnly tier -> allowed, no prompt.
            Assert.Equal(ApprovalDecision.Allow, pol.Check("acme__peek", Args("{}")));
            Assert.Equal(0, prompt.Calls);
        }

        [Fact]
        public void Third_party_destructive_annotation_prompts_every_time()
        {
            var prompt = new ScriptedPrompt { Next = ApprovalChoice.RememberTool }; // even if user tries to remember
            var ann = new FakeAnnotations().Set("acme__nuke", JObject.Parse("{\"destructiveHint\":true}"));
            var pol = Policy(prompt, new InMemoryApprovalStore(), ann);

            pol.Check("acme__nuke", Args("{}"));
            pol.Check("acme__nuke", Args("{}"));
            Assert.Equal(2, prompt.Calls); // Destructive -> None scope -> never remembered
        }

        [Fact]
        public void Third_party_without_annotation_falls_back_to_write_tool()
        {
            // Fail-safe default (#94): an absent hint is treated as NOT read-only, so the tool is gated
            // as Write/Tool (prompt once, then remembered) rather than silently auto-allowed.
            var prompt = new ScriptedPrompt { Next = ApprovalChoice.RememberTool };
            var pol = Policy(prompt, new InMemoryApprovalStore(), new FakeAnnotations());

            Assert.Equal(ApprovalDecision.Allow, pol.Check("acme__do", Args("{}")));
            Assert.Equal(1, prompt.Calls);
            Assert.Equal(ApprovalDecision.Allow, pol.Check("acme__do", Args("{}")));
            Assert.Equal(1, prompt.Calls); // remembered (Tool scope)
        }

        [Fact]
        public void First_party_tools_ignore_the_annotation_source()
        {
            // First-party tools are classified by the authoritative table, never by annotations — so a
            // (bogus) readOnlyHint for git__push must not downgrade it out of the Destructive tier.
            var prompt = new ScriptedPrompt { Next = ApprovalChoice.Deny };
            var ann = new FakeAnnotations().Set("git__push", JObject.Parse("{\"readOnlyHint\":true}"));
            var pol = Policy(prompt, new InMemoryApprovalStore(), ann);

            pol.Check("git__push", Args("{}"));
            Assert.Equal(1, prompt.Calls); // still gated (Destructive), not auto-allowed
        }

        [Fact]
        public void Web_http_prompts_every_time_and_is_never_remembered()
        {
            var prompt = new ScriptedPrompt { Next = ApprovalChoice.RememberTool }; // even if user tries to remember
            var pol = Policy(prompt, new InMemoryApprovalStore(), new FakeAnnotations());

            pol.Check("web__http", Args("{\"url\":\"https://api.test/\"}"));
            pol.Check("web__http", Args("{\"url\":\"https://api.test/\"}"));
            Assert.Equal(2, prompt.Calls); // Destructive/None -> always prompts
        }

        // ---- decision model (spec §3) ----

        [Fact]
        public void Tool_scope_prompts_first_then_remembered()
        {
            var prompt = new ScriptedPrompt { Next = ApprovalChoice.RememberTool };
            var store = new InMemoryApprovalStore();
            var pol = Policy(prompt, store);

            // git__commit is Tool-scope and gated (not auto-allowed), so it prompts the first time.
            Assert.Equal(ApprovalDecision.Allow, pol.Check("git__commit", Args("{\"message\":\"x\"}")));
            Assert.Equal(1, prompt.Calls);
            // second call: remembered, no prompt
            Assert.Equal(ApprovalDecision.Allow, pol.Check("git__commit", Args("{\"message\":\"y\"}")));
            Assert.Equal(1, prompt.Calls);
        }

        [Fact]
        public void Allow_once_does_not_persist()
        {
            var prompt = new ScriptedPrompt { Next = ApprovalChoice.AllowOnce };
            var pol = Policy(prompt, new InMemoryApprovalStore());
            pol.Check("git__commit", Args("{\"message\":\"x\"}"));
            pol.Check("git__commit", Args("{\"message\":\"y\"}"));
            Assert.Equal(2, prompt.Calls); // prompted both times
        }

        [Fact]
        public void Auto_allowed_read_only_tools_never_prompt()
        {
            // The read-only built-ins (and web__search / web__extract / web__get) are always allowed without a prompt.
            var prompt = new ScriptedPrompt { Next = ApprovalChoice.Deny };
            var pol = Policy(prompt, new InMemoryApprovalStore());
            Assert.Equal(ApprovalDecision.Allow, pol.Check("web__search", Args("{\"query\":\"x\"}")));
            Assert.Equal(ApprovalDecision.Allow, pol.Check("web__extract", Args("{\"urls\":[\"x\"]}")));
            Assert.Equal(ApprovalDecision.Allow, pol.Check("web__get", Args("{\"url\":\"https://api.test/\"}")));
            Assert.Equal(ApprovalDecision.Allow, pol.Check("files__read", Args("{\"path\":\"a\"}")));
            Assert.Equal(ApprovalDecision.Allow, pol.Check("git__status", Args("{}")));
            Assert.Equal(0, prompt.Calls);
        }

        [Fact]
        public void Destructive_scope_none_prompts_every_time()
        {
            var prompt = new ScriptedPrompt { Next = ApprovalChoice.RememberTool }; // even if user tries to remember
            var pol = Policy(prompt, new InMemoryApprovalStore());
            pol.Check("git__push", Args("{}"));
            pol.Check("git__push", Args("{}"));
            Assert.Equal(2, prompt.Calls); // None scope: never remembered
        }

        [Fact]
        public void Deny_returns_deny()
        {
            var prompt = new ScriptedPrompt { Next = ApprovalChoice.Deny };
            var pol = Policy(prompt, new InMemoryApprovalStore());
            Assert.Equal(ApprovalDecision.Deny, pol.Check("files__write", Args("{\"path\":\"/a\"}")));
        }

        // ---- argument rules (spec §3) ----

        [Fact]
        public void Exact_command_rule_matches_only_exact()
        {
            var prompt = new ScriptedPrompt { Next = ApprovalChoice.RememberExactArg };
            var pol = Policy(prompt, new InMemoryApprovalStore());

            pol.Check("command__run", Args("{\"command\":\"git status\"}")); // remembered exact
            Assert.Equal(1, prompt.Calls);
            Assert.Equal(ApprovalDecision.Allow, pol.Check("command__run", Args("{\"command\":\"git status\"}")));
            Assert.Equal(1, prompt.Calls); // matched, no prompt
            // different command -> prompts again
            pol.Check("command__run", Args("{\"command\":\"rm -rf /\"}"));
            Assert.Equal(2, prompt.Calls);
        }

        [Fact]
        public void Prefix_command_rule_is_token_aware()
        {
            var prompt = new ScriptedPrompt { Next = ApprovalChoice.RememberPrefixArg };
            var pol = Policy(prompt, new InMemoryApprovalStore());

            pol.Check("command__run", Args("{\"command\":\"git status\"}")); // -> prefix "git status"
            Assert.Equal(1, prompt.Calls);

            // token boundary: "git status -s" matches
            Assert.Equal(ApprovalDecision.Allow, pol.Check("command__run", Args("{\"command\":\"git status -s\"}")));
            Assert.Equal(1, prompt.Calls);
            // "git status-hack" must NOT match
            pol.Check("command__run", Args("{\"command\":\"git status-hack\"}"));
            Assert.Equal(2, prompt.Calls);
        }

        [Fact]
        public void Prefix_path_rule_is_directory_boundary_aware()
        {
            Assert.True(ToolApprovalPolicy.PrefixMatches("/a/b/c", "/a/b", true));
            Assert.False(ToolApprovalPolicy.PrefixMatches("/a/bc", "/a/b", true));
            Assert.True(ToolApprovalPolicy.PrefixMatches("/a/b", "/a/b", true));
        }

        [Fact]
        public void Directory_path_rule_covers_sibling_files_in_the_same_folder()
        {
            var prompt = new ScriptedPrompt { Next = ApprovalChoice.RememberPrefixArg };
            var pol = Policy(prompt, new InMemoryApprovalStore());

            // Approve "directory and below" while writing one file...
            pol.Check("files__write", Args("{\"path\":\"sub/a.txt\"}"));
            Assert.Equal(1, prompt.Calls);

            // ...a different file in the SAME directory must match (the bug: it kept prompting).
            Assert.Equal(ApprovalDecision.Allow, pol.Check("files__write", Args("{\"path\":\"sub/b.txt\"}")));
            Assert.Equal(1, prompt.Calls);

            // a file in a different directory still prompts
            pol.Check("files__write", Args("{\"path\":\"other/c.txt\"}"));
            Assert.Equal(2, prompt.Calls);
        }

        [Fact]
        public void Directory_rule_for_a_root_level_file_covers_siblings_in_the_root()
        {
            // Files server uses paths relative to the workspace root, so a file in the root has no
            // directory component — the directory rule must still cover its root-level siblings.
            var prompt = new ScriptedPrompt { Next = ApprovalChoice.RememberPrefixArg };
            var pol = Policy(prompt, new InMemoryApprovalStore());

            pol.Check("files__write", Args("{\"path\":\"report.txt\"}"));
            Assert.Equal(1, prompt.Calls);

            Assert.Equal(ApprovalDecision.Allow, pol.Check("files__write", Args("{\"path\":\"notes.txt\"}")));
            Assert.Equal(1, prompt.Calls);
            // a subdirectory file is also under the root
            Assert.Equal(ApprovalDecision.Allow, pol.Check("files__write", Args("{\"path\":\"sub/x.txt\"}")));
            Assert.Equal(1, prompt.Calls);
        }

        [Fact]
        public void Empty_path_pattern_matches_any_relative_path()
        {
            Assert.True(ToolApprovalPolicy.PrefixMatches("anything.txt", "", true));
            Assert.True(ToolApprovalPolicy.PrefixMatches("sub/deep/file.txt", "", true));
        }

        [Fact]
        public void Directory_rule_covers_siblings_in_a_nested_folder()
        {
            // Multi-level path: the parent dir is "a/b", and a sibling "a/b/y.txt" must match.
            var prompt = new ScriptedPrompt { Next = ApprovalChoice.RememberPrefixArg };
            var pol = Policy(prompt, new InMemoryApprovalStore());

            pol.Check("files__write", Args("{\"path\":\"a/b/x.txt\"}"));
            Assert.Equal(1, prompt.Calls);

            Assert.Equal(ApprovalDecision.Allow, pol.Check("files__write", Args("{\"path\":\"a/b/y.txt\"}")));
            Assert.Equal(1, prompt.Calls);
            // deeper still under a/b is covered
            Assert.Equal(ApprovalDecision.Allow, pol.Check("files__write", Args("{\"path\":\"a/b/c/z.txt\"}")));
            Assert.Equal(1, prompt.Calls);
            // sibling directory "a/bc" must NOT match the "a/b" boundary
            pol.Check("files__write", Args("{\"path\":\"a/bc/w.txt\"}"));
            Assert.Equal(2, prompt.Calls);
        }

        [Fact]
        public void Path_matching_normalizes_separators()
        {
            // A rule stored with a backslash separator still matches a forward-slash value.
            Assert.True(ToolApprovalPolicy.PrefixMatches("a/b/y.txt", "a\\b", true));
            Assert.True(ToolApprovalPolicy.PrefixMatches("a\\b\\y.txt", "a/b", true));
            Assert.False(ToolApprovalPolicy.PrefixMatches("a/bc/y.txt", "a\\b", true));
        }

        // ---- "all edits in this workspace" blanket approval ----

        [Fact]
        public void Workspace_approval_covers_all_write_tier_files_tools_in_the_same_workdir()
        {
            var prompt = new ScriptedPrompt { Next = ApprovalChoice.RememberWorkdirWrites };
            var pol = Policy(prompt, new InMemoryApprovalStore());
            pol.WorkingDir = "/work/project";

            // Approving the blanket on a write call...
            pol.Check("files__write", Args("{\"path\":\"a.txt\"}"));
            Assert.Equal(1, prompt.Calls);

            // ...a later write in a different folder is covered (whole workspace, not just one dir)...
            Assert.Equal(ApprovalDecision.Allow, pol.Check("files__write", Args("{\"path\":\"deep/nested/b.txt\"}")));
            Assert.Equal(1, prompt.Calls);

            // ...and so is files__edit, even though it's a different function name (spans write tools).
            Assert.Equal(ApprovalDecision.Allow, pol.Check("files__edit", Args("{\"path\":\"c.txt\"}")));
            Assert.Equal(1, prompt.Calls);
        }

        [Fact]
        public void Workspace_approval_does_not_leak_to_a_different_workdir()
        {
            // The store is shared app-wide; workspace-relative paths would collide across tabs, so the
            // approval must be scoped to the canonical working directory.
            var store = new InMemoryApprovalStore();

            var p1 = new ScriptedPrompt { Next = ApprovalChoice.RememberWorkdirWrites };
            var pol1 = Policy(p1, store);
            pol1.WorkingDir = "/work/project-a";
            pol1.Check("files__edit", Args("{\"path\":\"a.txt\"}"));
            Assert.Equal(1, p1.Calls);

            // A turn in a different workspace (same shared store) is NOT covered -> prompts.
            var p2 = new ScriptedPrompt { Next = ApprovalChoice.Deny };
            var pol2 = Policy(p2, store);
            pol2.WorkingDir = "/work/project-b";
            pol2.Check("files__edit", Args("{\"path\":\"a.txt\"}"));
            Assert.Equal(1, p2.Calls);
        }

        [Fact]
        public void Workspace_approval_is_separator_and_case_insensitive_for_the_workdir()
        {
            var store = new InMemoryApprovalStore();

            var p1 = new ScriptedPrompt { Next = ApprovalChoice.RememberWorkdirWrites };
            var pol1 = Policy(p1, store);
            pol1.WorkingDir = "/Work/Project/";
            pol1.Check("files__write", Args("{\"path\":\"a.txt\"}"));
            Assert.Equal(1, p1.Calls);

            // Same folder, different separators/casing/trailing-slash -> still matches the canonical key.
            var p2 = new ScriptedPrompt { Next = ApprovalChoice.Deny };
            var pol2 = Policy(p2, store);
            pol2.WorkingDir = "\\work\\project";
            Assert.Equal(ApprovalDecision.Allow, pol2.Check("files__write", Args("{\"path\":\"a.txt\"}")));
            Assert.Equal(0, p2.Calls);
        }

        [Fact]
        public void Workspace_approval_does_not_sweep_in_destructive_files_delete()
        {
            var prompt = new ScriptedPrompt { Next = ApprovalChoice.RememberWorkdirWrites };
            var pol = Policy(prompt, new InMemoryApprovalStore());
            pol.WorkingDir = "/work/project";

            pol.Check("files__write", Args("{\"path\":\"a.txt\"}")); // grants the blanket
            Assert.Equal(1, prompt.Calls);

            // files__delete is Destructive (Scope=None) -> never covered by the write blanket.
            pol.Check("files__delete", Args("{\"path\":\"a.txt\"}"));
            Assert.Equal(2, prompt.Calls);
        }

        [Fact]
        public void Workspace_approval_persists_nothing_without_a_workdir()
        {
            // No active workspace -> the blanket has nothing to scope to; the next call still prompts.
            var prompt = new ScriptedPrompt { Next = ApprovalChoice.RememberWorkdirWrites };
            var pol = Policy(prompt, new InMemoryApprovalStore());
            // WorkingDir intentionally left unset (null).

            pol.Check("files__write", Args("{\"path\":\"a.txt\"}"));
            Assert.Equal(1, prompt.Calls);
            pol.Check("files__write", Args("{\"path\":\"a.txt\"}"));
            Assert.Equal(2, prompt.Calls);
        }

        [Fact]
        public void Prefix_command_boundary_helper()
        {
            Assert.True(ToolApprovalPolicy.PrefixMatches("git status -s", "git status", false));
            Assert.False(ToolApprovalPolicy.PrefixMatches("git status-hack", "git status", false));
        }

        // ---- injection backstop (spec §6) ----

        [Fact]
        public void Injected_new_command_still_prompts_despite_existing_rule()
        {
            var prompt = new ScriptedPrompt { Next = ApprovalChoice.RememberExactArg };
            var pol = Policy(prompt, new InMemoryApprovalStore());
            pol.Check("command__run", Args("{\"command\":\"ls\"}")); // approve "ls" exactly
            Assert.Equal(1, prompt.Calls);

            // a malicious/injected different command does not match the narrow rule
            pol.Check("command__run", Args("{\"command\":\"rm -rf /\"}"));
            Assert.Equal(2, prompt.Calls);
        }

        // ---- persistence round-trip via the store ----

        [Fact]
        public void Store_round_trips_tools_and_rules()
        {
            var store = new InMemoryApprovalStore();
            store.AddApprovedTool("web__search");
            store.AddRule(new ApprovalRule("command__run", RuleKind.Prefix, "command", "git status"));

            Assert.True(store.IsToolApproved("web__search"));
            Assert.False(store.IsToolApproved("files__write"));
            Assert.Single(store.RulesFor("command__run"));
            Assert.Empty(store.RulesFor("files__write"));

            // duplicate rule is ignored
            store.AddRule(new ApprovalRule("command__run", RuleKind.Prefix, "command", "git status"));
            Assert.Single(store.RulesFor("command__run"));
        }

        [Fact]
        public void Reveal_tools_is_never_seen_by_the_policy()
        {
            // The orchestrator handles reveal_tools before the gate; if it ever reached here it would
            // classify as Write/Tool (third-party-style) and prompt — i.e. nothing auto-allows it.
            var prompt = new ScriptedPrompt { Next = ApprovalChoice.Deny };
            var pol = Policy(prompt, new InMemoryApprovalStore());
            Assert.Equal(ApprovalDecision.Deny, pol.Check("reveal_tools", Args("{\"names\":[]}")));
        }
    }
}
