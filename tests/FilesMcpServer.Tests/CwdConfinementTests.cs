using System;
using System.IO;
using Gxpt.Mcp.Conventions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FilesMcpServer.Tests
{
    // D2 enforcement: when the host injects a current directory (params._meta["gxpt.cwd"]) the files
    // server re-roots its path sandbox there, so reads/writes are hard-confined to the subdir and a
    // path argument can't climb back out to the anchor.
    public class CwdConfinementTests : IDisposable
    {
        private readonly string _root;
        private readonly string _sub;

        public CwdConfinementTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "filescwd_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _sub = Path.Combine(_root, "sub");
            Directory.CreateDirectory(_sub);
            File.WriteAllText(Path.Combine(_root, "root.txt"), "ROOT");
            File.WriteAllText(Path.Combine(_sub, "inner.txt"), "INNER");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private static string ToolsCallCwd(int id, string name, JObject args, string cwd)
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
        public void Read_with_cwd_resolves_relative_to_the_subdir()
        {
            var server = Harness.NewFilesServer(_root);
            var msgs = Harness.Exchange(server, ToolsCallCwd(1, "read", Harness.Args("path", "inner.txt"), _sub));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Contains("INNER", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Read_above_the_cwd_is_rejected()
        {
            var server = Harness.NewFilesServer(_root);
            // From the subdir, "../root.txt" escapes the re-rooted sandbox even though it is inside the
            // anchor — the model has scoped into sub and cannot reach back up without a deliberate cd.
            var msgs = Harness.Exchange(server, ToolsCallCwd(1, "read", Harness.Args("path", "../root.txt"), _sub));
            Assert.True(Harness.IsError(msgs[0]));
        }

        [Fact]
        public void Read_without_cwd_resolves_relative_to_the_anchor()
        {
            var server = Harness.NewFilesServer(_root);
            var msgs = Harness.Exchange(server, ToolsCallCwd(1, "read", Harness.Args("path", "root.txt"), null));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Contains("ROOT", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Cwd_outside_the_anchor_is_rejected()
        {
            var server = Harness.NewFilesServer(_root);
            string outside = Path.GetFullPath(Path.Combine(_root, ".."));
            var msgs = Harness.Exchange(server, ToolsCallCwd(1, "read", Harness.Args("path", "inner.txt"), outside));
            Assert.True(Harness.IsError(msgs[0]));
        }
    }
}
