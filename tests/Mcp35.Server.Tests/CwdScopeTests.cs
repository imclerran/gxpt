using System;
using System.IO;
using Mcp35.Core.Protocol;
using Mcp35.Core.Security;
using Mcp35.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Mcp35.Server.Tests
{
    // The host-cd plumbing on the server side: ToolCallContext surfaces params._meta["gxpt.cwd"], the
    // dispatch loop threads it onto the context, and CwdScope re-validates it against the launch anchor.
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
            if (cwd != null) meta[McpMeta.CwdKey] = cwd;
            return new ToolCallContext("t", new JObject(), null, meta);
        }

        // ---- ToolCallContext ----

        [Fact]
        public void Cwd_reads_meta_key()
        {
            Assert.Equal("C:/proj/sub", CtxWithCwd("C:/proj/sub").Cwd);
        }

        [Fact]
        public void Cwd_is_null_when_absent_or_empty()
        {
            Assert.Null(new ToolCallContext("t", new JObject(), null).Cwd);
            Assert.Null(CtxWithCwd(null).Cwd);
            Assert.Null(CtxWithCwd("").Cwd);
        }

        // ---- CwdScope ----

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

        // ---- dispatch threads _meta onto the context ----

        [Fact]
        public void Dispatch_threads_meta_cwd_to_the_handler()
        {
            Implementation info = new Implementation();
            info.Name = "t"; info.Version = "1.0";
            McpServer server = new McpServer(info, null);
            server.AddTool("echo_cwd", "echo cwd", SchemaBuilder.Object().Build(),
                delegate(ToolCallContext ctx) { return ToolResults.Text(ctx.Cwd ?? "<null>"); });

            JObject meta = new JObject();
            meta[McpMeta.CwdKey] = "C:/proj/sub";
            JObject prms = new JObject();
            prms["name"] = "echo_cwd";
            prms["_meta"] = meta;

            var msgs = ServerHarness.Exchange(server, ServerHarness.Request(1, "tools/call", prms));
            Assert.Single(msgs);
            string text = (string)msgs[0]["result"]["content"][0]["text"];
            Assert.Equal("C:/proj/sub", text);
        }
    }
}
