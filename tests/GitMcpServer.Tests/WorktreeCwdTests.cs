using System;
using System.IO;
using Mcp35.Core.Protocol;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GitMcpServer.Tests
{
    // A7: `worktree remove` of the conversation's current directory (or an ancestor of it) is refused —
    // the next call's current dir would vanish. The model must deliberately cd to the workspace root
    // first, rather than the git server reaching into host current-dir state to move it.
    public class WorktreeCwdTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _work;
        private readonly string _wt;

        public WorktreeCwdTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "gitwtcwd_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _work = Path.Combine(_dir, "work");
            Directory.CreateDirectory(_work);
            _wt = Path.Combine(_work, "wt");
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
            JObject meta = new JObject();
            meta[McpMeta.CwdKey] = cwd;
            p["_meta"] = meta;
            return Harness.Request(id, "tools/call", p);
        }

        [Fact]
        public void Worktree_remove_of_current_dir_is_refused()
        {
            // git is never invoked: the guard fires before RunGit, so a bogus git path is fine.
            var server = Harness.NewGitServer(Path.Combine(_dir, "no-such-git"), _work);
            // current dir == the worktree; removing "." targets the current dir itself.
            var msgs = Harness.Exchange(server,
                ToolsCallCwd(1, "worktree", Harness.Args("action", "remove", "path", "."), _wt));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("cd to the workspace root", Harness.Text(msgs[0]));
        }
    }
}
