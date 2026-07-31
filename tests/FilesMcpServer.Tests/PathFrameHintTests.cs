using System;
using System.IO;
using Gxpt.Mcp.Conventions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FilesMcpServer.Tests
{
    // Path-frame observability (doom-loop fixes): errors carry the RESOLVED absolute path, failures
    // while cd'd get a frame-orientation note (with a targeted "did you mean" when the path exists
    // at the workspace root), and a write whose path re-states the cwd's own tail — the doubling
    // signature — warns in its result.
    public class PathFrameHintTests : IDisposable
    {
        private readonly string _root;
        private readonly string _sub;

        public PathFrameHintTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "filesframe_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _sub = Path.Combine(_root, "sub");
            Directory.CreateDirectory(_sub);
            File.WriteAllText(Path.Combine(_root, "root.txt"), "ROOT");
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
        public void Read_error_includes_the_resolved_absolute_path()
        {
            var server = Harness.NewFilesServer(_root);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "read", Harness.Args("path", "missing.txt")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains(Path.Combine(_root, "missing.txt"), Harness.Text(msgs[0]));
        }

        [Fact]
        public void Failed_read_while_cdd_gets_the_frame_note_and_did_you_mean()
        {
            // The doom-loop shape: cd'd into sub, the model asks for a root file by its root-relative
            // name. The error must say where the path actually resolved, that paths are cwd-relative,
            // and that the file DOES exist at the workspace root.
            var server = Harness.NewFilesServer(_root);
            var msgs = Harness.Exchange(server, ToolsCallCwd(1, "read", Harness.Args("path", "root.txt"), _sub));
            Assert.True(Harness.IsError(msgs[0]));
            string text = Harness.Text(msgs[0]);
            Assert.Contains(Path.Combine(_sub, "root.txt"), text);           // where it resolved
            Assert.Contains("current directory 'sub'", text);                // which frame applies
            Assert.Contains("does exist relative to the workspace root", text); // targeted correction
        }

        [Fact]
        public void Failed_read_at_the_root_gets_no_frame_note()
        {
            var server = Harness.NewFilesServer(_root);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "read", Harness.Args("path", "missing.txt")));
            Assert.DoesNotContain("[note:", Harness.Text(msgs[0])); // one frame only - nothing to explain
        }

        [Fact]
        public void Write_that_restates_the_cwd_tail_warns_and_reports_the_absolute_path()
        {
            // cd'd into sub, writing "sub/x.txt": resolves to sub\sub\x.txt - the doubled tree. The
            // write still succeeds (a genuinely intended nested write stays possible) but the result
            // carries the warning and the absolute path, so the mistake is visible immediately.
            var server = Harness.NewFilesServer(_root);
            var msgs = Harness.Exchange(server, ToolsCallCwd(1, "write",
                Harness.Args("path", "sub/x.txt", "content", "hi", "create_dirs", true), _sub));
            Assert.False(Harness.IsError(msgs[0]));
            string text = Harness.Text(msgs[0]);
            Assert.Contains("warning", text);
            Assert.Contains("re-states the current directory", text);
            string doubled = Path.Combine(Path.Combine(_sub, "sub"), "x.txt");
            Assert.Contains(doubled.Replace("\\", "\\\\"), text); // absolute_path in the JSON mirror
            Assert.True(File.Exists(doubled));
        }

        [Fact]
        public void Ordinary_write_carries_no_warning()
        {
            var server = Harness.NewFilesServer(_root);
            var msgs = Harness.Exchange(server, ToolsCallCwd(1, "write",
                Harness.Args("path", "y.txt", "content", "hi"), _sub));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.DoesNotContain("warning", Harness.Text(msgs[0]));
            Assert.True(File.Exists(Path.Combine(_sub, "y.txt")));
        }
    }
}
