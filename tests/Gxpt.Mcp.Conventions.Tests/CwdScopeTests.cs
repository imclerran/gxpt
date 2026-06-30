using System;
using System.IO;
using Mcp35.Core.Security;
using Mcp35.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Gxpt.Mcp.Conventions.Tests
{
    // CwdScope interprets GxPT's host-injected current directory (params._meta["gxpt.cwd"]) and
    // re-validates it against the server's launch anchor (defense in depth), independent of the host.
    public class CwdScopeTests : IDisposable
    {
        private readonly string _anchor;
        private readonly string _sub;

        public CwdScopeTests()
        {
            _anchor = Path.Combine(Path.GetTempPath(), "cwdscope_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_anchor);
            _sub = Path.Combine(_anchor, "sub");
            Directory.CreateDirectory(_sub);
        }

        public void Dispose()
        {
            try { Directory.Delete(_anchor, true); } catch { }
        }

        private static ToolCallContext CtxWithCwd(string cwd)
        {
            JObject meta = new JObject();
            if (cwd != null) meta[GxptMeta.CwdKey] = cwd;
            return new ToolCallContext("t", new JObject(), null, meta);
        }

        [Fact]
        public void Absent_cwd_resolves_to_the_anchor()
        {
            string workDir; PathSandbox sandbox; string err;
            bool ok = CwdScope.TryResolve(CtxWithCwd(null), _anchor, "workspace root",
                out workDir, out sandbox, out err);
            Assert.True(ok);
            Assert.Null(err);
            Assert.Equal(new PathSandbox(_anchor).Root, workDir);
        }

        [Fact]
        public void Cwd_within_anchor_resolves_and_reroots_the_sandbox()
        {
            string workDir; PathSandbox sandbox; string err;
            bool ok = CwdScope.TryResolve(CtxWithCwd(_sub), _anchor, "workspace root",
                out workDir, out sandbox, out err);
            Assert.True(ok);
            Assert.Equal(Path.GetFullPath(_sub), workDir);
            Assert.Equal(new PathSandbox(_sub).Root, sandbox.Root);
        }

        [Fact]
        public void Cwd_above_the_anchor_is_rejected()
        {
            string outside = Path.GetFullPath(Path.Combine(_anchor, ".."));
            string workDir; PathSandbox sandbox; string err;
            bool ok = CwdScope.TryResolve(CtxWithCwd(outside), _anchor, "workspace root",
                out workDir, out sandbox, out err);
            Assert.False(ok);
            Assert.NotNull(err);
        }

        [Fact]
        public void Cwd_that_no_longer_exists_is_rejected_not_silently_widened()
        {
            string gone = Path.Combine(_anchor, "removed-worktree");
            string workDir; PathSandbox sandbox; string err;
            bool ok = CwdScope.TryResolve(CtxWithCwd(gone), _anchor, "workspace root",
                out workDir, out sandbox, out err);
            Assert.False(ok);
            Assert.NotNull(err);
        }

        [Fact]
        public void TryResolveWorkingDir_validates_but_discards_the_sandbox()
        {
            string workDir; string err;
            Assert.True(CwdScope.TryResolveWorkingDir(CtxWithCwd(_sub), _anchor, "workspace root",
                out workDir, out err));
            Assert.Equal(Path.GetFullPath(_sub), workDir);

            string outside = Path.GetFullPath(Path.Combine(_anchor, ".."));
            Assert.False(CwdScope.TryResolveWorkingDir(CtxWithCwd(outside), _anchor, "workspace root",
                out workDir, out err));
            Assert.NotNull(err);
        }
    }
}
