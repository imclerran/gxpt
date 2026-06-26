using System;
using System.IO;
using System.Text;
using Mcp35.Core.Security;
using Mcp35.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FilesMcpServer.Tests
{
    public class FilesToolsTests : IDisposable
    {
        private readonly string _root;

        public FilesToolsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "filesmcp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private string Abs(string rel) { return Path.Combine(_root, rel); }

        // ---- Criterion 1: listing ----

        [Fact]
        public void Lists_the_documented_tools_with_schema()
        {
            var server = Harness.NewFilesServer(_root);
            var msgs = Harness.Exchange(server, Harness.ToolsList(1));

            JArray tools = (JArray)msgs[0]["result"]["tools"];
            var names = new System.Collections.Generic.List<string>();
            foreach (JToken t in tools) names.Add((string)t["name"]);

            Assert.Contains("read", names);
            Assert.Contains("list", names);
            Assert.Contains("write", names);
            Assert.Contains("delete", names);
            Assert.Contains("edit", names);
            Assert.Contains("search", names);
            // schema intact
            foreach (JToken t in tools)
                Assert.Equal("object", (string)t["inputSchema"]["type"]);
        }

        // ---- Criterion 2: sandbox ----

        [Theory]
        [InlineData("../escape.txt")]
        [InlineData("../../etc/passwd")]
        [InlineData("subdir/../../outside.txt")]
        public void Rejects_parent_traversal(string path)
        {
            var server = Harness.NewFilesServer(_root);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "read", Harness.Args("path", path)));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("escape", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Rejects_absolute_paths()
        {
            var server = Harness.NewFilesServer(_root);
            string abs = Path.Combine(_root, "x.txt"); // a rooted path
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "read", Harness.Args("path", abs)));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("absolute", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Sibling_root_prefix_is_not_within()
        {
            // The classic "/root" vs "/root-evil" boundary trick: a sibling dir whose path shares
            // the root's string prefix must NOT be considered inside the sandbox.
            string sibling = _root + "-evil";
            Directory.CreateDirectory(sibling);
            try
            {
                File.WriteAllText(Path.Combine(sibling, "secret.txt"), "nope");
                var sandbox = new PathSandbox(_root);
                Assert.False(sandbox.IsWithin(Path.Combine(sibling, "secret.txt")));
            }
            finally
            {
                try { Directory.Delete(sibling, true); } catch { }
            }
        }

        [Fact]
        public void In_root_read_write_list_delete_round_trip()
        {
            var server = Harness.NewFilesServer(_root);

            // write
            var w = Harness.Exchange(server, Harness.ToolsCall(1, "write",
                Harness.Args("path", "notes/todo.txt", "content", "hello world", "create_dirs", true)));
            Assert.False(Harness.IsError(w[0]));
            Assert.True(File.Exists(Abs(Path.Combine("notes", "todo.txt"))));

            // read (fresh server instances are fine; same root)
            var r = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "notes/todo.txt")));
            Assert.Equal("hello world", Harness.Text(r[0]));

            // list
            var l = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "list", Harness.Args("path", "notes")));
            Assert.False(Harness.IsError(l[0]));
            Assert.Equal(1, (int)l[0]["result"]["structuredContent"]["count"]);

            // delete
            var d = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "delete", Harness.Args("path", "notes/todo.txt")));
            Assert.False(Harness.IsError(d[0]));
            Assert.False(File.Exists(Abs(Path.Combine("notes", "todo.txt"))));
        }

        [Fact]
        public void Oversize_single_line_read_truncates_with_next_offset()
        {
            // A 2 MiB file with no newlines is one giant line: it can't be subdivided by line, so the
            // read truncates mid-line and hands back a byte offset to continue from (not an error).
            File.WriteAllText(Abs("big.txt"), new string('a', 2 * 1024 * 1024)); // 2 MiB > 1 MiB cap
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "big.txt")));
            Assert.False(Harness.IsError(msgs[0]));
            JToken sc = msgs[0]["result"]["structuredContent"];
            Assert.True((bool)sc["truncated"]);
            Assert.Equal(1024 * 1024, (long)sc["next_offset"]);       // cut at the 1 MiB cap
            Assert.Equal(1024 * 1024, ((string)sc["content"]).Length);
            Assert.Null(sc["next_start_line"]);                       // byte cut, not a line boundary
        }

        [Fact]
        public void Binary_read_is_error()
        {
            File.WriteAllBytes(Abs("blob.bin"), new byte[] { 1, 2, 0, 3, 4 }); // NUL byte → binary
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "blob.bin")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("not a text file", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Write_without_create_dirs_into_missing_parent_is_error()
        {
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "write", Harness.Args("path", "missing/x.txt", "content", "y")));
            Assert.True(Harness.IsError(msgs[0]));
        }

        [Fact]
        public void Write_is_atomic_overwrite()
        {
            File.WriteAllText(Abs("f.txt"), "old");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "write", Harness.Args("path", "f.txt", "content", "new")));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal("new", File.ReadAllText(Abs("f.txt")));
        }

        [Fact]
        public void Delete_refuses_non_empty_directory()
        {
            Directory.CreateDirectory(Abs("full"));
            File.WriteAllText(Abs(Path.Combine("full", "a.txt")), "x");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "delete", Harness.Args("path", "full")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("not empty", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Delete_removes_empty_directory()
        {
            Directory.CreateDirectory(Abs("empty"));
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "delete", Harness.Args("path", "empty")));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.False(Directory.Exists(Abs("empty")));
        }

        [Fact]
        public void Read_bom_file_strips_bom()
        {
            byte[] withBom = new byte[] { 0xEF, 0xBB, 0xBF };
            byte[] text = Encoding.UTF8.GetBytes("café");
            byte[] all = new byte[withBom.Length + text.Length];
            Buffer.BlockCopy(withBom, 0, all, 0, withBom.Length);
            Buffer.BlockCopy(text, 0, all, withBom.Length, text.Length);
            File.WriteAllBytes(Abs("bom.txt"), all);

            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "bom.txt")));
            Assert.Equal("café", Harness.Text(msgs[0])); // no leading BOM char
        }

        // ---- read: line range + numbering ----

        [Fact]
        public void Read_returns_requested_line_range()
        {
            File.WriteAllText(Abs("r.txt"), "one\ntwo\nthree\nfour\nfive");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "r.txt", "start_line", 2, "end_line", 4)));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal("two\nthree\nfour", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Read_with_line_numbers_prefixes_each_line()
        {
            File.WriteAllText(Abs("r.txt"), "alpha\nbeta\ngamma");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "r.txt", "line_numbers", true)));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal("1\talpha\n2\tbeta\n3\tgamma", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Read_start_line_past_eof_is_error()
        {
            File.WriteAllText(Abs("r.txt"), "only\ntwo");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "r.txt", "start_line", 9)));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("exceeds file length", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Read_whole_file_is_verbatim_when_no_range_or_numbers()
        {
            File.WriteAllText(Abs("r.txt"), "trailing\nnewline\n");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "r.txt")));
            Assert.Equal("trailing\nnewline\n", Harness.Text(msgs[0]));
        }

        // ---- edit ----

        [Fact]
        public void Edit_replaces_unique_span()
        {
            File.WriteAllText(Abs("e.txt"), "hello world");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "edit", Harness.Args("path", "e.txt",
                    "old_string", "world", "new_string", "there")));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal(1, (int)msgs[0]["result"]["structuredContent"]["replacements"]);
            Assert.Equal("hello there", File.ReadAllText(Abs("e.txt")));
        }

        [Fact]
        public void Edit_non_unique_without_replace_all_is_error()
        {
            File.WriteAllText(Abs("e.txt"), "a a a");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "edit", Harness.Args("path", "e.txt",
                    "old_string", "a", "new_string", "b")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("not unique", Harness.Text(msgs[0]));
            Assert.Equal("a a a", File.ReadAllText(Abs("e.txt"))); // unchanged
        }

        [Fact]
        public void Edit_replace_all_replaces_every_occurrence()
        {
            File.WriteAllText(Abs("e.txt"), "x x x");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "edit", Harness.Args("path", "e.txt",
                    "old_string", "x", "new_string", "y", "replace_all", true)));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal(3, (int)msgs[0]["result"]["structuredContent"]["replacements"]);
            Assert.Equal("y y y", File.ReadAllText(Abs("e.txt")));
        }

        [Fact]
        public void Edit_missing_old_string_is_error()
        {
            File.WriteAllText(Abs("e.txt"), "content");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "edit", Harness.Args("path", "e.txt",
                    "old_string", "absent", "new_string", "z")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("not found", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Edit_nonexistent_file_is_error()
        {
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "edit", Harness.Args("path", "nope.txt",
                    "old_string", "a", "new_string", "b")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("file not found", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Edit_rejects_parent_traversal()
        {
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "edit", Harness.Args("path", "../escape.txt",
                    "old_string", "a", "new_string", "b")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("escape", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Edit_works_on_oversize_file()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 60000; i++) sb.Append("padding padding padding\n"); // ~1.4 MiB
            sb.Append("UNIQUE_MARKER stays here\n");
            for (int i = 0; i < 60000; i++) sb.Append("more more more more more\n");
            File.WriteAllText(Abs("big.txt"), sb.ToString());
            Assert.True(new FileInfo(Abs("big.txt")).Length > 1024 * 1024);

            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "edit", Harness.Args("path", "big.txt",
                    "old_string", "UNIQUE_MARKER", "new_string", "REPLACED")));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal(1, (int)msgs[0]["result"]["structuredContent"]["replacements"]);

            string after = File.ReadAllText(Abs("big.txt"));
            Assert.Contains("REPLACED stays here", after);
            Assert.DoesNotContain("UNIQUE_MARKER", after);
            // surrounding content is preserved verbatim
            Assert.True(after.StartsWith("padding padding padding\n"));
            Assert.True(after.EndsWith("more more more more more\n"));
        }

        [Fact]
        public void Edit_replaces_match_spanning_a_read_chunk_boundary()
        {
            // The streaming reader chunks at 64K chars; place a match straddling that boundary so
            // only the carry-buffer logic can catch it. 65535 dots + "NEEDLE" => 'N' is char #65536.
            string prefix = new string('.', 65535);
            File.WriteAllText(Abs("span.txt"), prefix + "NEEDLE" + "tail");

            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "edit", Harness.Args("path", "span.txt",
                    "old_string", "NEEDLE", "new_string", "FOUND")));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal(1, (int)msgs[0]["result"]["structuredContent"]["replacements"]);
            Assert.Equal(prefix + "FOUND" + "tail", File.ReadAllText(Abs("span.txt")));
        }

        [Fact]
        public void Edit_preserves_crlf_line_endings_outside_the_span()
        {
            File.WriteAllText(Abs("crlf.txt"), "one\r\ntwo\r\nthree");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "edit", Harness.Args("path", "crlf.txt",
                    "old_string", "two", "new_string", "TWO")));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal("one\r\nTWO\r\nthree", File.ReadAllText(Abs("crlf.txt")));
        }

        [Fact]
        public void Edit_matches_lf_old_string_against_crlf_file()
        {
            // The reported bug: the read tools hand back LF-normalized text, so a multi-line
            // old_string copied from a read is LF-only — but the file on disk is CRLF. The match
            // must still succeed (and surrounding CRLFs are preserved).
            File.WriteAllText(Abs("crlf.txt"), "alpha\r\nbeta\r\ngamma\r\ndelta");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "edit", Harness.Args("path", "crlf.txt",
                    "old_string", "beta\ngamma", "new_string", "BETA\nGAMMA")));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal(1, (int)msgs[0]["result"]["structuredContent"]["replacements"]);
            Assert.Equal("alpha\r\nBETA\r\nGAMMA\r\ndelta", File.ReadAllText(Abs("crlf.txt")));
        }

        [Fact]
        public void Edit_normalizes_new_string_newlines_to_a_crlf_file()
        {
            // new_string's LFs are rewritten to the file's CRLF so the edit doesn't leave the file
            // with mixed line endings (which is what plain single-line edits used to do).
            File.WriteAllText(Abs("crlf.txt"), "head\r\ntail");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "edit", Harness.Args("path", "crlf.txt",
                    "old_string", "tail", "new_string", "x\ny\nz")));
            Assert.False(Harness.IsError(msgs[0]));
            string after = File.ReadAllText(Abs("crlf.txt"));
            Assert.Equal("head\r\nx\r\ny\r\nz", after);
            Assert.DoesNotContain("\n", after.Replace("\r\n", "")); // every LF is part of a CRLF — no bare LFs
        }

        [Fact]
        public void Edit_keeps_lf_endings_in_an_lf_file()
        {
            // No regression for the common LF case: a CRLF detection must not inject CRLFs into an
            // LF file, and an LF old_string keeps matching.
            File.WriteAllText(Abs("lf.txt"), "a\nb\nc\nd");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "edit", Harness.Args("path", "lf.txt",
                    "old_string", "b\nc", "new_string", "B\nC")));
            Assert.False(Harness.IsError(msgs[0]));
            string after = File.ReadAllText(Abs("lf.txt"));
            Assert.Equal("a\nB\nC\nd", after);
            Assert.DoesNotContain("\r", after);
        }

        [Fact]
        public void Edit_matches_lf_old_string_spanning_a_chunk_boundary_in_crlf_file()
        {
            // Combine the two hard cases: a CRLF file, an LF-authored multi-line old_string, AND a
            // match whose internal CRLF straddles the 64K read boundary. This exercises the carry
            // buffer using the post-normalization (CRLF-expanded) old_string length.
            const int chunk = 64 * 1024;          // EditChunkChars
            string filler = string.Concat(System.Linq.Enumerable.Repeat("ab\r\n", 16383)); // 65532 chars
            string prefix = filler + "c";         // 65533 chars: the marker's '\r' lands at index 65535
            File.WriteAllText(Abs("span.txt"), prefix + "AA\r\nBB" + "tail");
            Assert.True(prefix.Length + 2 == chunk - 1); // '\r' is the last char of chunk 1

            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "edit", Harness.Args("path", "span.txt",
                    "old_string", "AA\nBB", "new_string", "CC")));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal(1, (int)msgs[0]["result"]["structuredContent"]["replacements"]);
            Assert.Equal(prefix + "CC" + "tail", File.ReadAllText(Abs("span.txt")));
        }

        // ---- search ----

        [Fact]
        public void Search_finds_substring_matches_with_line_numbers()
        {
            File.WriteAllText(Abs("a.txt"), "alpha\nneedle here\nbeta");
            File.WriteAllText(Abs("b.txt"), "no match\nanother needle\n");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "needle")));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal(2, (int)msgs[0]["result"]["structuredContent"]["count"]);
            JArray m = (JArray)msgs[0]["result"]["structuredContent"]["matches"];
            // a.txt match is on line 2
            bool found = false;
            foreach (JToken t in m)
                if ((string)t["path"] == "a.txt" && (int)t["line"] == 2) found = true;
            Assert.True(found);
        }

        [Fact]
        public void Search_ignore_case()
        {
            File.WriteAllText(Abs("a.txt"), "Hello THERE");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "there", "ignore_case", true)));
            Assert.Equal(1, (int)msgs[0]["result"]["structuredContent"]["count"]);
        }

        [Fact]
        public void Search_regex_mode()
        {
            File.WriteAllText(Abs("a.txt"), "id=42\nid=abc\nid=7");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "id=[0-9]+", "regex", true)));
            Assert.Equal(2, (int)msgs[0]["result"]["structuredContent"]["count"]);
        }

        [Fact]
        public void Search_multiline_regex_spans_lines()
        {
            File.WriteAllText(Abs("a.txt"), "alpha\nbeta\ngamma");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "beta\ngamma",
                    "regex", true, "multiline", true)));
            Assert.Equal(1, (int)msgs[0]["result"]["structuredContent"]["count"]);
            JArray m = (JArray)msgs[0]["result"]["structuredContent"]["matches"];
            Assert.Equal(2, (int)m[0]["line"]); // match starts on line 2 (beta)
        }

        [Fact]
        public void Search_without_multiline_cannot_span_lines()
        {
            // Default line-oriented behavior: a pattern containing a newline never matches because
            // ReadLine strips the terminator before matching. This guards the default.
            File.WriteAllText(Abs("a.txt"), "alpha\nbeta\ngamma");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "beta\ngamma", "regex", true)));
            Assert.Equal(0, (int)msgs[0]["result"]["structuredContent"]["count"]);
        }

        [Fact]
        public void Search_multiline_literal_matches_across_crlf()
        {
            // Literal (non-regex) multiline: the query's own newline matches the file's CRLF region.
            File.WriteAllText(Abs("a.txt"), "a\r\nb\r\nc");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "a\r\nb", "multiline", true)));
            Assert.Equal(1, (int)msgs[0]["result"]["structuredContent"]["count"]);
            JArray m = (JArray)msgs[0]["result"]["structuredContent"]["matches"];
            Assert.Equal(1, (int)m[0]["line"]);
        }

        [Fact]
        public void Search_multiline_can_match_crlf_line_terminators()
        {
            // The CRLF diagnostic that line-oriented search can never do: \r$ in multiline mode finds
            // the carriage return before each LF (^/$ are line anchors under RegexOptions.Multiline).
            File.WriteAllText(Abs("crlf.txt"), "a\r\nb\r\nc");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "\r$",
                    "regex", true, "multiline", true)));
            Assert.Equal(2, (int)msgs[0]["result"]["structuredContent"]["count"]);
        }

        [Fact]
        public void Search_multiline_reports_match_start_line()
        {
            // '.' does not cross newlines by default, so [\s\S] is used to span; the reported line is
            // where the match starts, not where it ends.
            File.WriteAllText(Abs("a.txt"), "x\nALPHA\nmid\nBETA\ny");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "ALPHA[\\s\\S]*BETA",
                    "regex", true, "multiline", true)));
            Assert.Equal(1, (int)msgs[0]["result"]["structuredContent"]["count"]);
            JArray m = (JArray)msgs[0]["result"]["structuredContent"]["matches"];
            Assert.Equal(2, (int)m[0]["line"]);
        }

        [Fact]
        public void Search_invalid_regex_is_error()
        {
            File.WriteAllText(Abs("a.txt"), "x");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "(unclosed", "regex", true)));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("invalid regex", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Search_glob_filters_by_filename()
        {
            File.WriteAllText(Abs("keep.cs"), "target");
            File.WriteAllText(Abs("skip.txt"), "target");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "target", "glob", "*.cs")));
            Assert.Equal(1, (int)msgs[0]["result"]["structuredContent"]["count"]);
            JArray m = (JArray)msgs[0]["result"]["structuredContent"]["matches"];
            Assert.Equal("keep.cs", (string)m[0]["path"]);
        }

        [Fact]
        public void Search_skips_binary_files()
        {
            File.WriteAllBytes(Abs("blob.bin"), new byte[] { (byte)'h', (byte)'i', 0, (byte)'t' });
            File.WriteAllText(Abs("ok.txt"), "hit");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "hi")));
            // Only ok.txt's "hit" line counts; the binary blob is skipped.
            Assert.Equal(1, (int)msgs[0]["result"]["structuredContent"]["count"]);
        }

        [Fact]
        public void Search_recurses_subdirectories()
        {
            Directory.CreateDirectory(Abs("sub"));
            File.WriteAllText(Abs(Path.Combine("sub", "deep.txt")), "buried treasure");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "treasure")));
            Assert.Equal(1, (int)msgs[0]["result"]["structuredContent"]["count"]);
        }

        [Fact]
        public void Search_max_results_truncates()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 10; i++) sb.Append("match\n");
            File.WriteAllText(Abs("many.txt"), sb.ToString());
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "match", "max_results", 3)));
            Assert.Equal(3, (int)msgs[0]["result"]["structuredContent"]["count"]);
            Assert.True((bool)msgs[0]["result"]["structuredContent"]["truncated"]);
        }

        // ---- large files: search streams (no size cap); ranged read streams a slice ----

        [Fact]
        public void Search_finds_matches_in_oversize_file()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 100000; i++) sb.Append("filler line\n"); // ~1.2 MiB > 1 MiB cap
            sb.Append("the needle is here\n");
            File.WriteAllText(Abs("big.txt"), sb.ToString());
            Assert.True(new FileInfo(Abs("big.txt")).Length > 1024 * 1024);

            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "needle")));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal(1, (int)msgs[0]["result"]["structuredContent"]["count"]);
            JArray m = (JArray)msgs[0]["result"]["structuredContent"]["matches"];
            Assert.Equal(100001, (int)m[0]["line"]);
        }

        [Fact]
        public void Read_range_works_on_oversize_file_and_whole_file_truncates()
        {
            var sb = new StringBuilder();
            sb.Append("first line\n");
            for (int i = 0; i < 120000; i++) sb.Append("padding padding padding\n"); // ~2.8 MiB
            File.WriteAllText(Abs("big.txt"), sb.ToString());
            Assert.True(new FileInfo(Abs("big.txt")).Length > 1024 * 1024);

            // Ranged read of the first line succeeds despite the file exceeding the cap.
            var r = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "big.txt", "start_line", 1, "end_line", 1)));
            Assert.False(Harness.IsError(r[0]));
            Assert.Equal("first line", Harness.Text(r[0]));

            // Whole-file read now truncates at a line boundary instead of failing.
            var w = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "big.txt")));
            Assert.False(Harness.IsError(w[0]));
            JToken sc = w[0]["result"]["structuredContent"];
            Assert.True((bool)sc["truncated"]);
            Assert.True((int)sc["next_start_line"] > 1);   // resume by line (multi-line file)
            Assert.Null(sc["next_offset"]);
        }

        [Fact]
        public void Read_range_oversize_selection_truncates_at_line_boundary()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 120000; i++) sb.Append("padding padding padding\n"); // ~2.8 MiB
            File.WriteAllText(Abs("big.txt"), sb.ToString());

            // An open-ended range over the cap truncates and points at the next line.
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "big.txt", "start_line", 1)));
            Assert.False(Harness.IsError(msgs[0]));
            JToken sc = msgs[0]["result"]["structuredContent"];
            Assert.True((bool)sc["truncated"]);
            Assert.True((int)sc["next_start_line"] > 1);
        }

        [Fact]
        public void Read_range_cap_counts_utf8_bytes_not_chars()
        {
            // 2000 lines × 250 '€' (3 bytes each) ≈ 1.5 MiB of UTF-8, but only ~0.5M chars — under a
            // char-based cap, over the byte cap. The read truncates on real byte size: a char-based
            // cap would never trip (500K chars < 1 MiB), so truncation here proves byte counting.
            var sb = new StringBuilder();
            for (int i = 0; i < 2000; i++) sb.Append(new string('€', 250)).Append('\n');
            File.WriteAllText(Abs("uni.txt"), sb.ToString(), new UTF8Encoding(false));
            Assert.True(sb.Length < 1024 * 1024);                                  // char count under cap
            Assert.True(new FileInfo(Abs("uni.txt")).Length > 1024 * 1024);        // byte count over cap

            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "uni.txt", "start_line", 1)));
            Assert.False(Harness.IsError(msgs[0]));
            JToken sc = msgs[0]["result"]["structuredContent"];
            Assert.True((bool)sc["truncated"]);
            int next = (int)sc["next_start_line"];
            Assert.True(next > 1 && next < 2000);    // cut partway through on byte size, not at line 2000
        }

        [Fact]
        public void Read_range_at_cap_boundary_returns_just_under_and_truncates_just_over()
        {
            // 16-byte ASCII lines; joined by '\n' that is 17 bytes/line. Output for N lines = 17N-1.
            // 17×61681-1 = 1,048,576 = the 1 MiB cap exactly (fits, since the check is strictly >);
            // line 61682 tips it over and is left for the next read.
            string row = new string('x', 16);
            var sb = new StringBuilder();
            for (int i = 0; i < 70000; i++) sb.Append(row).Append('\n');
            File.WriteAllText(Abs("rows.txt"), sb.ToString(), new UTF8Encoding(false));

            var ok = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "rows.txt", "start_line", 1, "end_line", 61681)));
            Assert.False(Harness.IsError(ok[0]));
            Assert.Equal(61681 * 17 - 1, Harness.Text(ok[0]).Length); // 61681 rows joined by 61680 '\n'

            var over = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "rows.txt", "start_line", 1, "end_line", 61682)));
            Assert.False(Harness.IsError(over[0]));
            JToken sc = over[0]["result"]["structuredContent"];
            Assert.True((bool)sc["truncated"]);
            Assert.Equal(61682, (int)sc["next_start_line"]);                  // resume at the line that didn't fit
            Assert.Equal(61681 * 17 - 1, ((string)sc["content"]).Length);     // same content as the just-under read
        }

        // ---- read: truncation continuation (chunked / minified files) ----

        [Fact]
        public void Read_minified_file_pages_via_next_offset_until_complete()
        {
            // ~2.5 MiB single line of varied bytes: page through it with offset and reassemble.
            var sb = new StringBuilder();
            for (int i = 0; i < 2500000; i++) sb.Append((char)('0' + (i % 10)));
            string original = sb.ToString();
            File.WriteAllText(Abs("min.json"), original, new UTF8Encoding(false));

            var assembled = new StringBuilder();
            long offset = 0;
            bool first = true;
            int guard = 0;
            while (guard++ < 100)
            {
                JObject call = first
                    ? Harness.Args("path", "min.json")
                    : Harness.Args("path", "min.json", "offset", offset);
                first = false;
                var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                    Harness.ToolsCall(1, "read", call));
                Assert.False(Harness.IsError(msgs[0]));
                JToken sc = msgs[0]["result"]["structuredContent"];
                if (sc != null && sc["truncated"] != null && (bool)sc["truncated"])
                {
                    assembled.Append((string)sc["content"]);
                    offset = (long)sc["next_offset"];
                }
                else
                {
                    assembled.Append(Harness.Text(msgs[0])); // final chunk: plain text
                    break;
                }
            }
            Assert.Equal(original, assembled.ToString());
        }

        [Fact]
        public void Read_byte_cut_keeps_utf8_codepoints_intact()
        {
            // One line of many 3-byte '€', over the cap. The mid-line cut must land on a char
            // boundary (no replacement chars), and the two pieces must reassemble to the original.
            string original = new string('€', 450000); // 450000×3 = ~1.35 MiB > cap
            File.WriteAllText(Abs("euro.txt"), original, new UTF8Encoding(false));

            var r1 = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "euro.txt")));
            JToken sc = r1[0]["result"]["structuredContent"];
            Assert.True((bool)sc["truncated"]);
            string part1 = (string)sc["content"];
            Assert.True(part1.IndexOf('�') < 0); // no replacement char => clean codepoint cut
            long offset = (long)sc["next_offset"];
            Assert.Equal(0, offset % 3);                  // boundary aligned to the 3-byte char

            var r2 = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "euro.txt", "offset", offset)));
            string part2 = Harness.Text(r2[0]);           // remainder fits under the cap => plain text
            Assert.Equal(original, part1 + part2);
        }

        [Fact]
        public void Read_short_line_then_giant_line_switches_from_line_to_offset()
        {
            // Line 1 is short, line 2 is a single over-cap line. The first read stops at the boundary
            // (next_start_line=2); resuming at line 2 then falls to a byte offset.
            string giant = new string('z', 2 * 1024 * 1024);
            File.WriteAllText(Abs("mix.txt"), "short\n" + giant, new UTF8Encoding(false));

            var r1 = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "mix.txt")));
            JToken sc1 = r1[0]["result"]["structuredContent"];
            Assert.True((bool)sc1["truncated"]);
            Assert.Equal(2, (int)sc1["next_start_line"]);
            Assert.Equal("short", (string)sc1["content"]);

            var r2 = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "mix.txt", "start_line", 2)));
            JToken sc2 = r2[0]["result"]["structuredContent"];
            Assert.True((bool)sc2["truncated"]);
            Assert.NotNull(sc2["next_offset"]);
            Assert.Equal(1024 * 1024, ((string)sc2["content"]).Length);
        }

        [Fact]
        public void Read_offset_past_eof_is_error()
        {
            File.WriteAllText(Abs("s.txt"), "hello");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "s.txt", "offset", 99)));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("exceeds file length", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Read_offset_at_eof_returns_empty_not_truncated()
        {
            File.WriteAllText(Abs("s.txt"), "hello");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "s.txt", "offset", 5)));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal("", Harness.Text(msgs[0]));
        }

        // ---- Criterion 6: lifecycle ----

        [Fact]
        public void Missing_file_read_is_error_and_server_survives()
        {
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "nope.txt")),
                Harness.Request(2, "ping", null));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.NotNull(msgs[1]["result"]); // ping still answered
        }
    }
}
