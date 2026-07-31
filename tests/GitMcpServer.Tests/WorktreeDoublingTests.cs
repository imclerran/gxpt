using System;
using System.IO;
using Gxpt.Mcp.Conventions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GitMcpServer.Tests
{
    // The worktree-doubling guard: a worktree path whose leading segments re-state the current
    // directory's own tail (".worktrees/feat" issued from ...\.worktrees\feat) is a workspace-root-
    // relative path used while cd'd — resolved, it would carve a worktree INSIDE the existing one
    // (...\.worktrees\feat\.worktrees\feat, the observed doom-loop disaster that doubled trees on
    // disk). Reject with the fix instead of creating it.
    public class WorktreeDoublingTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _work;
        private readonly string _wt;

        public WorktreeDoublingTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "gitwtdbl_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _work = Path.Combine(_dir, "work");
            Directory.CreateDirectory(_work);
            _wt = Path.Combine(Path.Combine(_work, ".worktrees"), "feat");
            Directory.CreateDirectory(_wt);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string ToolsCallCwd(int id, string name, JObject args, string cwd)
        {
            JObject p = new JObject();
            p["name"] = name;
            if (args != null) p["arguments"] = args;
            if (cwd != null)
            {
                JObject meta = new JObject();
                meta[GxptMeta.CwdKey] = cwd;
                p["_meta"] = meta;
            }
            return Harness.Request(id, "tools/call", p);
        }

        [Fact]
        public void Worktree_add_that_restates_the_cwd_tail_is_rejected()
        {
            // git is never invoked: the guard fires before RunGit, so a bogus git path is fine.
            var server = Harness.NewGitServer(Path.Combine(_dir, "no-such-git"), _work);
            // cd'd into .worktrees/feat, adding ".worktrees/feat" again - the doubling signature.
            var msgs = Harness.Exchange(server,
                ToolsCallCwd(1, "worktree", Harness.Args("action", "add", "path", ".worktrees/feat"), _wt));
            Assert.True(Harness.IsError(msgs[0]));
            string text = Harness.Text(msgs[0]);
            Assert.Contains("re-stating the current directory", text);
            Assert.Contains("cd with no argument", text);
        }

        [Fact]
        public void Worktree_add_from_the_root_is_not_flagged()
        {
            // From the workspace root the same path is exactly right; the guard must not fire (the
            // call then proceeds to git, which fails here because the git exe is bogus - but with a
            // git error, not the frame rejection).
            var server = Harness.NewGitServer(Path.Combine(_dir, "no-such-git"), _work);
            var msgs = Harness.Exchange(server,
                ToolsCallCwd(1, "worktree", Harness.Args("action", "add", "path", ".worktrees/other"), null));
            Assert.DoesNotContain("re-stating the current directory", Harness.Text(msgs[0]));
        }
    }
}
