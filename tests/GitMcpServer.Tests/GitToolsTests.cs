using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GitMcpServer.Tests
{
    /// <summary>
    /// Integration tests over a fake "git" that echoes the argv it received and any stdin, so we
    /// can assert how the Git server built each command line (servers-spec §8 criterion 4):
    /// discrete tokens, commit message via stdin (not argv), diff path after "--".
    /// </summary>
    public class GitToolsTests : IDisposable
    {
        private static bool IsWindows
        {
            get { return Environment.OSVersion.Platform != PlatformID.Unix && Environment.OSVersion.Platform != PlatformID.MacOSX; }
        }

        private readonly string _dir;
        private readonly string _work;

        public GitToolsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "gitmcp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _work = Path.Combine(_dir, "work");
            Directory.CreateDirectory(_work);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        /// <summary>
        /// A fake git that records what it received: the full argv goes to stdout (prefixed
        /// "ARGV:"), and stdin is redirected to a sibling file "stdin.txt" the test can read. This
        /// avoids any shell quirk around echoing an empty pipe (cmd's 'more'/'cat' on no input).
        /// Exits with the given code.
        /// </summary>
        private string FakeGit(int exitCode)
        {
            _stdinCapture = Path.Combine(_dir, "stdin.txt");
            if (IsWindows)
            {
                string path = Path.Combine(_dir, "fakegit.cmd");
                // %* is the full argument tail. Redirect this script's own stdin to a file via
                // a nested invocation so an empty pipe can't stall: findstr "^" reads stdin and
                // writes it out; redirect that to the capture file. /v "" matches all lines.
                string script =
                    "@echo off\r\n" +
                    "echo ARGV:%*\r\n" +
                    "findstr \"^\" > \"" + _stdinCapture + "\"\r\n" +
                    "exit /b " + exitCode + "\r\n";
                File.WriteAllText(path, script);
                return path;
            }
            else
            {
                string path = Path.Combine(_dir, "fakegit.sh");
                string script =
                    "#!/bin/sh\n" +
                    "echo \"ARGV:$*\"\n" +
                    "cat > \"" + _stdinCapture + "\"\n" +
                    "exit " + exitCode + "\n";
                File.WriteAllText(path, script);
                try { System.Diagnostics.Process.Start("/bin/chmod", "+x \"" + path + "\"").WaitForExit(); } catch { }
                return path;
            }
        }

        private string _stdinCapture;

        private string CapturedStdin()
        {
            try { return _stdinCapture != null && File.Exists(_stdinCapture) ? File.ReadAllText(_stdinCapture) : ""; }
            catch { return ""; }
        }

        private string StdoutOf(JObject msg)
        {
            return (string)Harness.Structured(msg)["stdout"];
        }

        // ---- listing ----

        [Fact]
        public void Lists_all_tools()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsList(1));

            JArray tools = (JArray)msgs[0]["result"]["tools"];
            var names = new System.Collections.Generic.List<string>();
            foreach (JToken t in tools) names.Add((string)t["name"]);
            // original five
            Assert.Contains("status", names);
            Assert.Contains("diff", names);
            Assert.Contains("log", names);
            Assert.Contains("commit", names);
            Assert.Contains("push", names);
            // expansion
            foreach (string n in new[] { "fetch", "pull", "checkout", "restore", "branch", "merge",
                                         "rebase", "cherry_pick", "add", "reset", "rm", "stash", "worktree" })
                Assert.Contains(n, names);
        }

        [Fact]
        public void Every_tool_advertises_cwd()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsList(1));

            JArray tools = (JArray)msgs[0]["result"]["tools"];
            foreach (JToken t in tools)
            {
                JObject props = (JObject)t["inputSchema"]["properties"];
                Assert.True(props["cwd"] != null, "tool '" + (string)t["name"] + "' is missing the cwd argument");
            }
        }

        // ---- argv construction ----

        [Fact]
        public void Status_builds_porcelain_args()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "status", new JObject()));
            Assert.False(Harness.IsError(msgs[0]));
            string argv = StdoutOf(msgs[0]);
            Assert.Contains("status", argv);
            Assert.Contains("--porcelain=v1", argv);
            Assert.Contains("-b", argv);
        }

        [Fact]
        public void Diff_places_path_after_double_dash()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "diff", Harness.Args("path", "src/app.cs")));
            string argv = StdoutOf(msgs[0]);
            // the "--" separator must precede the path
            int sep = argv.IndexOf("--", StringComparison.Ordinal);
            int path = argv.IndexOf("src/app.cs", StringComparison.Ordinal);
            Assert.True(sep >= 0 && path > sep, "path must come after -- ; argv=" + argv);
        }

        [Fact]
        public void Diff_staged_adds_flag()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "diff", Harness.Args("staged", true)));
            Assert.Contains("--staged", StdoutOf(msgs[0]));
        }

        [Fact]
        public void Commit_passes_message_via_stdin_not_argv()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            const string message = "fix: a tricky \"quoted\" message; rm -rf";
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "commit", Harness.Args("message", message)));

            Assert.False(Harness.IsError(msgs[0]));
            string argv = StdoutOf(msgs[0]);            // the ARGV: line(s) from stdout
            string stdin = CapturedStdin();             // what was piped to git's stdin

            // The command line must be `commit -F -` and must NOT contain the message text.
            Assert.Contains("commit", argv);
            Assert.Contains("-F", argv);
            Assert.DoesNotContain("tricky", argv);      // message is not an argv token
            // The message must arrive via stdin (git commit -F -).
            Assert.Contains("tricky", stdin);
        }

        [Fact]
        public void Push_with_remote_and_branch_builds_args()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "push", Harness.Args("remote", "origin", "branch", "main")));
            string argv = StdoutOf(msgs[0]);
            Assert.Contains("push", argv);
            Assert.Contains("origin", argv);
            Assert.Contains("main", argv);
        }

        [Fact]
        public void Push_branch_without_remote_is_error()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "push", Harness.Args("branch", "main")));
            Assert.True(Harness.IsError(msgs[0]));
        }

        // ---- exit handling ----

        [Fact]
        public void Nonzero_exit_is_error_with_stderr()
        {
            var server = Harness.NewGitServer(FakeGit(1), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "status", new JObject()));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("git exited 1", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Commit_requires_message()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "commit", new JObject()));
            Assert.True(Harness.IsError(msgs[0]));
        }

        [Fact]
        public void Missing_git_executable_returns_clear_error()
        {
            // Point GXPT_GIT_PATH at a path that doesn't exist -> Process.Start throws Win32Exception,
            // which the tool turns into a clear "git was not found" error for the model.
            string noGit = Path.Combine(_dir, "definitely-not-git" + (IsWindows ? ".exe" : ""));
            var server = Harness.NewGitServer(noGit, _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "status", new JObject()));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("git was not found", Harness.Text(msgs[0]));
        }

        // ---- expansion: remote sync ----

        [Fact]
        public void Fetch_with_prune_and_remote()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "fetch", Harness.Args("prune", true, "remote", "origin")));
            string argv = StdoutOf(msgs[0]);
            Assert.Contains("fetch", argv);
            Assert.Contains("--prune", argv);
            Assert.Contains("origin", argv);
        }

        [Fact]
        public void Pull_rebase_builds_args()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "pull", Harness.Args("rebase", true, "remote", "origin", "branch", "main")));
            string argv = StdoutOf(msgs[0]);
            Assert.Contains("pull", argv);
            Assert.Contains("--rebase", argv);
            Assert.Contains("origin", argv);
            Assert.Contains("main", argv);
        }

        [Fact]
        public void Pull_branch_without_remote_is_error()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "pull", Harness.Args("branch", "main")));
            Assert.True(Harness.IsError(msgs[0]));
        }

        // ---- expansion: branches / working tree ----

        [Fact]
        public void Checkout_create_adds_dash_b()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "checkout", Harness.Args("ref", "feature", "create", true)));
            string argv = StdoutOf(msgs[0]);
            Assert.Contains("checkout", argv);
            Assert.Contains("-b", argv);
            Assert.Contains("feature", argv);
        }

        [Fact]
        public void Checkout_requires_ref()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "checkout", new JObject()));
            Assert.True(Harness.IsError(msgs[0]));
        }

        [Fact]
        public void Restore_places_paths_after_double_dash()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "restore",
                Harness.Args("paths", new[] { "src/a.cs" }, "staged", true)));
            string argv = StdoutOf(msgs[0]);
            Assert.Contains("restore", argv);
            Assert.Contains("--staged", argv);
            int sep = argv.IndexOf("--", argv.IndexOf("--staged", StringComparison.Ordinal) + 1, StringComparison.Ordinal);
            int path = argv.IndexOf("src/a.cs", StringComparison.Ordinal);
            Assert.True(sep >= 0 && path > sep, "path must come after -- ; argv=" + argv);
        }

        [Fact]
        public void Restore_requires_paths()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "restore", new JObject()));
            Assert.True(Harness.IsError(msgs[0]));
        }

        [Fact]
        public void Branch_delete_uses_force_flag()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "branch",
                Harness.Args("action", "delete", "name", "old", "force", true)));
            string argv = StdoutOf(msgs[0]);
            Assert.Contains("branch", argv);
            Assert.Contains("-D", argv);
            Assert.Contains("old", argv);
        }

        [Fact]
        public void Branch_rename_builds_args()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "branch",
                Harness.Args("action", "rename", "name", "a", "new_name", "b")));
            string argv = StdoutOf(msgs[0]);
            Assert.Contains("-m", argv);
            Assert.Contains("a", argv);
            Assert.Contains("b", argv);
        }

        [Fact]
        public void Branch_create_requires_name()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "branch", Harness.Args("action", "create")));
            Assert.True(Harness.IsError(msgs[0]));
        }

        [Fact]
        public void Branch_unknown_action_is_error()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "branch", Harness.Args("action", "nuke")));
            Assert.True(Harness.IsError(msgs[0]));
        }

        // ---- expansion: integrate ----

        [Fact]
        public void Merge_passes_no_edit_and_branch()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "merge", Harness.Args("branch", "feature", "no_ff", true)));
            string argv = StdoutOf(msgs[0]);
            Assert.Contains("merge", argv);
            Assert.Contains("--no-edit", argv);
            Assert.Contains("--no-ff", argv);
            Assert.Contains("feature", argv);
        }

        [Fact]
        public void Rebase_start_requires_onto()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "rebase", new JObject()));
            Assert.True(Harness.IsError(msgs[0]));
        }

        [Fact]
        public void Rebase_continue_builds_flag()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "rebase", Harness.Args("action", "continue")));
            Assert.Contains("--continue", StdoutOf(msgs[0]));
        }

        [Fact]
        public void Cherry_pick_passes_no_edit_and_commit()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "cherry_pick", Harness.Args("commit", "abc123")));
            string argv = StdoutOf(msgs[0]);
            Assert.Contains("cherry-pick", argv);
            Assert.Contains("--no-edit", argv);
            Assert.Contains("abc123", argv);
        }

        // ---- expansion: staging / stash ----

        [Fact]
        public void Add_all_uses_dash_A()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "add", Harness.Args("all", true)));
            string argv = StdoutOf(msgs[0]);
            Assert.Contains("add", argv);
            Assert.Contains("-A", argv);
        }

        [Fact]
        public void Add_paths_after_double_dash()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "add", Harness.Args("paths", new[] { "f.cs" })));
            string argv = StdoutOf(msgs[0]);
            int sep = argv.IndexOf("--", StringComparison.Ordinal);
            int path = argv.IndexOf("f.cs", StringComparison.Ordinal);
            Assert.True(sep >= 0 && path > sep, "path must come after -- ; argv=" + argv);
        }

        [Fact]
        public void Add_requires_paths_or_all()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "add", new JObject()));
            Assert.True(Harness.IsError(msgs[0]));
        }

        [Fact]
        public void Reset_hard_builds_mode_and_target()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "reset", Harness.Args("mode", "hard", "target", "HEAD~2")));
            string argv = StdoutOf(msgs[0]);
            Assert.Contains("reset", argv);
            Assert.Contains("--hard", argv);
            Assert.Contains("HEAD~2", argv);
        }

        [Fact]
        public void Reset_paths_unstage_after_double_dash()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "reset", Harness.Args("paths", new[] { "f.cs" })));
            string argv = StdoutOf(msgs[0]);
            Assert.DoesNotContain("--hard", argv); // mode ignored when paths given
            int sep = argv.IndexOf("--", StringComparison.Ordinal);
            int path = argv.IndexOf("f.cs", StringComparison.Ordinal);
            Assert.True(sep >= 0 && path > sep, "path must come after -- ; argv=" + argv);
        }

        [Fact]
        public void Rm_cached_places_paths_after_double_dash()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "rm", Harness.Args("paths", new[] { "f.cs" }, "cached", true)));
            string argv = StdoutOf(msgs[0]);
            Assert.Contains("rm", argv);
            Assert.Contains("--cached", argv);
            int sep = argv.IndexOf("--", argv.IndexOf("--cached", StringComparison.Ordinal) + 1, StringComparison.Ordinal);
            int path = argv.IndexOf("f.cs", StringComparison.Ordinal);
            Assert.True(sep >= 0 && path > sep, "path must come after -- ; argv=" + argv);
        }

        [Fact]
        public void Stash_push_with_message()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "stash", Harness.Args("action", "push", "message", "wip")));
            string argv = StdoutOf(msgs[0]);
            Assert.Contains("stash", argv);
            Assert.Contains("push", argv);
            Assert.Contains("-m", argv);
            Assert.Contains("wip", argv);
        }

        [Fact]
        public void Stash_pop_with_index_builds_entry()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "stash", Harness.Args("action", "pop", "index", 2)));
            string argv = StdoutOf(msgs[0]);
            Assert.Contains("pop", argv);
            Assert.Contains("stash@{2}", argv);
        }

        // ---- expansion: worktree ----

        [Fact]
        public void Worktree_list_uses_porcelain()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "worktree", Harness.Args("action", "list")));
            string argv = StdoutOf(msgs[0]);
            Assert.Contains("worktree", argv);
            Assert.Contains("list", argv);
            Assert.Contains("--porcelain", argv);
        }

        [Fact]
        public void Worktree_defaults_to_list()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "worktree", new JObject()));
            string argv = StdoutOf(msgs[0]);
            Assert.Contains("worktree", argv);
            Assert.Contains("list", argv);
        }

        [Fact]
        public void Worktree_add_builds_branch_and_resolved_path()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "worktree",
                Harness.Args("action", "add", "path", ".worktrees/feat", "branch", "feat", "ref", "main")));
            Assert.False(Harness.IsError(msgs[0]));
            string argv = StdoutOf(msgs[0]);
            Assert.Contains("worktree", argv);
            Assert.Contains("add", argv);
            Assert.Contains("-b", argv);
            Assert.Contains("feat", argv);
            Assert.Contains("main", argv);
            // the worktree path is resolved against the workspace root (absolute), inside the sandbox
            Assert.Contains("feat", argv);
            int b = argv.IndexOf("-b", StringComparison.Ordinal);
            int branch = argv.IndexOf("feat", b, StringComparison.Ordinal);
            int wtDir = argv.IndexOf("worktrees", StringComparison.Ordinal);
            Assert.True(branch > b && wtDir > branch, "expected -b <branch> before the worktree dir; argv=" + argv);
        }

        [Fact]
        public void Worktree_add_requires_path()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "worktree", Harness.Args("action", "add")));
            Assert.True(Harness.IsError(msgs[0]));
        }

        [Fact]
        public void Worktree_remove_force_builds_args()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "worktree",
                Harness.Args("action", "remove", "path", ".worktrees/feat", "force", true)));
            string argv = StdoutOf(msgs[0]);
            Assert.Contains("remove", argv);
            Assert.Contains("--force", argv);
            Assert.Contains("worktrees", argv);
        }

        [Fact]
        public void Worktree_prune_builds_args()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "worktree", Harness.Args("action", "prune")));
            Assert.Contains("prune", StdoutOf(msgs[0]));
        }

        [Fact]
        public void Worktree_path_escaping_sandbox_is_error()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "worktree",
                Harness.Args("action", "add", "path", "../escape")));
            Assert.True(Harness.IsError(msgs[0]));
        }

        [Fact]
        public void Worktree_unknown_action_is_error()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "worktree", Harness.Args("action", "nuke")));
            Assert.True(Harness.IsError(msgs[0]));
        }

        // ---- expansion: cwd (run git inside a subdirectory / worktree) ----

        [Fact]
        public void Cwd_runs_git_in_the_subdirectory()
        {
            // A fake git that reports its own working directory, so we can assert cwd took effect.
            string sub = Path.Combine(_work, "sub");
            Directory.CreateDirectory(sub);

            var server = Harness.NewGitServer(FakeGitPwd(), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "status", Harness.Args("cwd", "sub")));
            Assert.False(Harness.IsError(msgs[0]));
            string outp = StdoutOf(msgs[0]);
            // the fake git prints PWD: <dir>; it must be the sub directory, not the workspace root
            Assert.Contains("sub", outp);
        }

        [Fact]
        public void Cwd_escaping_sandbox_is_error()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "status", Harness.Args("cwd", "../escape")));
            Assert.True(Harness.IsError(msgs[0]));
        }

        [Fact]
        public void Cwd_not_found_is_error()
        {
            var server = Harness.NewGitServer(FakeGit(0), _work);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "status", Harness.Args("cwd", "nope")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("cwd not found", Harness.Text(msgs[0]));
        }

        // A fake git that echoes its current working directory (prefixed "PWD:") to stdout, so a
        // test can confirm the cwd argument changed where git ran. Exits 0.
        private string FakeGitPwd()
        {
            _stdinCapture = Path.Combine(_dir, "stdin.txt");
            if (IsWindows)
            {
                string path = Path.Combine(_dir, "fakegitpwd.cmd");
                string script =
                    "@echo off\r\n" +
                    "echo PWD:%CD%\r\n" +
                    "findstr \"^\" > \"" + _stdinCapture + "\"\r\n" +
                    "exit /b 0\r\n";
                File.WriteAllText(path, script);
                return path;
            }
            else
            {
                string path = Path.Combine(_dir, "fakegitpwd.sh");
                string script =
                    "#!/bin/sh\n" +
                    "echo \"PWD:$(pwd -P)\"\n" +
                    "cat > \"" + _stdinCapture + "\"\n" +
                    "exit 0\n";
                File.WriteAllText(path, script);
                try { System.Diagnostics.Process.Start("/bin/chmod", "+x \"" + path + "\"").WaitForExit(); } catch { }
                return path;
            }
        }
    }
}
