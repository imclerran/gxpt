using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Mcp35.Core.Protocol;
using Mcp35.Core.Security;
using Mcp35.Server;
using Newtonsoft.Json.Linq;

namespace FilesMcpServer
{
    /// <summary>
    /// The Files server's tools, all confined to the sandbox root. read/list/search are ReadOnly,
    /// write/edit are Write(path-scoped), delete is Destructive(path-scoped) — host-gated; the
    /// server's job is safe construction (servers-spec §2). edit/search and read's line-range/
    /// numbering options are the agentic enhancements (range reads were flagged as a later add).
    /// </summary>
    internal static class FilesTools
    {
        // Caps (servers-spec §2).
        private const long MaxReadBytes = 1024 * 1024;       // 1 MiB
        private const int MaxListEntries = 1000;
        private const int MaxRecursiveDepth = 16;
        private const int BinarySniffBytes = 8000;

        // Search caps (servers-spec §2 — bounded blast radius for the model context).
        private const int DefaultSearchMax = 100;
        private const int MaxSearchMax = 1000;
        private const int MaxMatchLineLength = 1000;
        private const int MaxSearchScanFiles = 5000;

        public static void Register(McpServer server, FilesConfig config)
        {
            PathSandbox sandbox = new PathSandbox(config.WorkDir, "workspace root");

            server.AddTool("read", "Read the UTF-8 text contents of a file under the workspace root. "
                + "Optionally read a line range (1-based, inclusive) and/or prefix each line with its number.",
                SchemaBuilder.Object()
                    .Str("path", true, "Path relative to the workspace root")
                    .Int("start_line", false, "First line to return (1-based, inclusive)")
                    .Int("end_line", false, "Last line to return (1-based, inclusive)")
                    .Bool("line_numbers", false, "Prefix each returned line with its 1-based line number")
                    .Build(),
                ToolAnnotations.ReadOnly(),
                delegate(ToolCallContext ctx) { return Read(sandbox, ctx); });

            server.AddTool("list", "List entries of a directory under the workspace root.",
                SchemaBuilder.Object()
                    .Str("path", true, "Directory path relative to the workspace root")
                    .Bool("recursive", false, "Recurse into subdirectories (bounded depth)")
                    .Build(),
                ToolAnnotations.ReadOnly(),
                delegate(ToolCallContext ctx) { return List(sandbox, ctx); });

            server.AddTool("write", "Create or overwrite a text file under the workspace root.",
                SchemaBuilder.Object()
                    .Str("path", true, "Path relative to the workspace root")
                    .Str("content", true, "UTF-8 text content to write")
                    .Bool("create_dirs", false, "Create missing parent directories")
                    .Build(),
                ToolAnnotations.Write(),
                delegate(ToolCallContext ctx) { return Write(sandbox, ctx); });

            server.AddTool("delete", "Delete a file or an empty directory under the workspace root.",
                SchemaBuilder.Object().Str("path", true, "Path relative to the workspace root").Build(),
                ToolAnnotations.Destructive(),
                delegate(ToolCallContext ctx) { return Delete(sandbox, ctx); });

            server.AddTool("edit", "Replace an exact text span in a file under the workspace root. "
                + "Prefer this over write for large files: it is targeted and never rewrites the whole file. "
                + "old_string must match exactly and be unique unless replace_all is set.",
                SchemaBuilder.Object()
                    .Str("path", true, "Path relative to the workspace root")
                    .Str("old_string", true, "Exact text to find (must be unique unless replace_all is set)")
                    .Str("new_string", true, "Replacement text")
                    .Bool("replace_all", false, "Replace every occurrence instead of requiring a unique match")
                    .Build(),
                ToolAnnotations.Write(),
                delegate(ToolCallContext ctx) { return Edit(sandbox, ctx); });

            server.AddTool("search", "Search file contents for a string or regex under the workspace root, "
                + "returning matching {path, line, text}. Recursive, skips binary files. Line-oriented by "
                + "default (each line is matched with its terminator stripped, so a pattern cannot match a "
                + "newline or span lines); set multiline to match across line boundaries (grep -U style).",
                SchemaBuilder.Object()
                    .Str("query", true, "Text or regex to search for")
                    .Str("path", false, "Directory (or file) to search under; defaults to the workspace root")
                    .Bool("regex", false, "Treat query as a .NET regular expression (default: literal substring)")
                    .Bool("ignore_case", false, "Case-insensitive match")
                    .Str("glob", false, "Only search files whose name matches this wildcard (e.g. *.cs)")
                    .Int("max_results", false, "Maximum matches to return (default 100, max 1000)")
                    .Bool("multiline", false, "Match against whole-file text with line endings intact, so the "
                        + "query/pattern may contain or span newlines (grep -U). In regex mode ^/$ become line "
                        + "anchors. 'line' is where each match starts. Bounded by the read cap; line-mode streams "
                        + "files of any size, multiline skips files over the cap.")
                    .Build(),
                ToolAnnotations.ReadOnly(),
                delegate(ToolCallContext ctx) { return Search(sandbox, ctx); });
        }

        // ---- read ----

        private static CallToolResult Read(PathSandbox sandbox, ToolCallContext ctx)
        {
            string full;
            CallToolResult err = ResolvePath(sandbox, ctx, out full);
            if (err != null) return err;

            if (!File.Exists(full)) return ToolResults.Error("file not found");

            bool hasStart = HasArg(ctx, "start_line");
            bool hasEnd = HasArg(ctx, "end_line");
            bool lineNumbers = BoolArg(ctx, "line_numbers", false);

            // A line range streams, so a slice of a large file is readable even past the whole-file
            // cap (the output is bounded by the range, not the file size).
            if (hasStart || hasEnd)
                return ReadRange(full, ctx, hasStart, hasEnd, lineNumbers);

            // Whole-file read: bounded by the size cap (it all lands in the model context).
            FileInfo fi = new FileInfo(full);
            if (fi.Length > MaxReadBytes)
                return ToolResults.Error("file too large (" + fi.Length + " bytes; max " + MaxReadBytes
                    + ") — read a line range instead");

            byte[] bytes = File.ReadAllBytes(full);
            if (LooksBinary(bytes)) return ToolResults.Error("not a text file");
            string text = DecodeUtf8(bytes);

            // Fast path: whole file, no numbering -> return content verbatim (preserves exact bytes).
            if (!lineNumbers)
                return ToolResults.Text(text);

            string[] lines = SplitLines(text);
            int width = lines.Length.ToString().Length;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                sb.Append((i + 1).ToString().PadLeft(width));
                sb.Append('\t');
                sb.Append(lines[i]);
                if (i < lines.Length - 1) sb.Append('\n');
            }
            return ToolResults.Text(sb.ToString());
        }

        // Stream a 1-based inclusive line range; output bytes are capped (not the file size), so a
        // slice of an arbitrarily large file is readable. start/end default to file bounds.
        private static CallToolResult ReadRange(string full, ToolCallContext ctx, bool hasStart, bool hasEnd, bool lineNumbers)
        {
            int start = hasStart ? IntArg(ctx, "start_line", 1, 1, int.MaxValue) : 1;
            int end = hasEnd ? IntArg(ctx, "end_line", int.MaxValue, 1, int.MaxValue) : int.MaxValue;
            if (end < start)
                return ToolResults.Error("end_line (" + end + ") is before start_line (" + start + ")");

            StreamReader sr;
            try { sr = OpenTextReaderOrNull(full); }
            catch { return ToolResults.Error("file not found"); }
            if (sr == null) return ToolResults.Error("not a text file");

            UTF8Encoding utf8NoBom = new UTF8Encoding(false);
            List<string> picked = new List<string>();
            int lineNo = 0;
            long contentBytes = 0;
            using (sr)
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    lineNo++;
                    if (lineNo < start) continue;
                    if (lineNo > end) break;
                    picked.Add(line);
                    contentBytes += utf8NoBom.GetByteCount(line);

                    // Cap the exact rendered UTF-8 size, not the char count. The rendered output is:
                    // the line bytes, '\n' separators (count - 1), and — when numbering — a prefix on
                    // every line of `width` digits + a '\t', where width is the digit count of the
                    // LAST line number (right-aligned PadLeft). `lineNo` is that last number so far, so
                    // applying its width to all picked lines makes this total exact at the final line.
                    long total = contentBytes + (picked.Count - 1);
                    if (lineNumbers) total += (long)picked.Count * (DigitCount(lineNo) + 1);
                    if (total > MaxReadBytes)
                        return ToolResults.Error("requested range is too large (over " + MaxReadBytes
                            + " bytes of rendered output) — narrow start_line/end_line");
                }
            }

            if (picked.Count == 0)
                return ToolResults.Error("start_line " + start + " exceeds file length (" + lineNo + " lines)");

            int lastLineNo = start + picked.Count - 1;
            int width = lastLineNo.ToString().Length;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < picked.Count; i++)
            {
                if (lineNumbers)
                {
                    sb.Append((start + i).ToString().PadLeft(width));
                    sb.Append('\t');
                }
                sb.Append(picked[i]);
                if (i < picked.Count - 1) sb.Append('\n');
            }
            return ToolResults.Text(sb.ToString());
        }

        // ---- list ----

        private static CallToolResult List(PathSandbox sandbox, ToolCallContext ctx)
        {
            string full;
            CallToolResult err = ResolvePath(sandbox, ctx, out full);
            if (err != null) return err;

            if (!Directory.Exists(full)) return ToolResults.Error("directory not found");

            bool recursive = BoolArg(ctx, "recursive", false);

            List<JObject> entries = new List<JObject>();
            bool truncated = Collect(sandbox, full, recursive, recursive ? MaxRecursiveDepth : 0, entries);

            JObject result = new JObject();
            JArray arr = new JArray();
            foreach (JObject e in entries) arr.Add(e);
            result["entries"] = arr;
            result["count"] = entries.Count;
            result["truncated"] = truncated;
            return ToolResults.Json(result);
        }

        private static bool Collect(PathSandbox sandbox, string dir, bool recursive, int depthLeft, List<JObject> entries)
        {
            string[] dirs = Directory.GetDirectories(dir);
            string[] files = Directory.GetFiles(dir);

            foreach (string d in dirs)
            {
                if (entries.Count >= MaxListEntries) return true;
                entries.Add(Entry(sandbox, d, "dir", 0));
                if (recursive && depthLeft > 0)
                {
                    if (Collect(sandbox, d, true, depthLeft - 1, entries)) return true;
                }
            }
            foreach (string f in files)
            {
                if (entries.Count >= MaxListEntries) return true;
                long size = 0;
                try { size = new FileInfo(f).Length; }
                catch { }
                entries.Add(Entry(sandbox, f, "file", size));
            }
            return false;
        }

        private static JObject Entry(PathSandbox sandbox, string full, string type, long size)
        {
            JObject o = new JObject();
            o["name"] = sandbox.ToRelative(full);
            o["type"] = type;
            o["size"] = size;
            return o;
        }

        // ---- write ----

        private static CallToolResult Write(PathSandbox sandbox, ToolCallContext ctx)
        {
            string full;
            CallToolResult err = ResolvePath(sandbox, ctx, out full);
            if (err != null) return err;

            if (Directory.Exists(full)) return ToolResults.Error("path is a directory");

            string content = ctx.Arguments.Value<string>("content");
            if (content == null) return ToolResults.Error("content is required");

            bool createDirs = BoolArg(ctx, "create_dirs", false);
            string parent = Path.GetDirectoryName(full);
            if (!Directory.Exists(parent))
            {
                if (!createDirs) return ToolResults.Error("parent directory does not exist (set create_dirs to create it)");
                Directory.CreateDirectory(parent);
            }

            int bytesWritten = WriteAtomic(full, content);

            JObject result = new JObject();
            result["path"] = sandbox.ToRelative(full);
            result["bytesWritten"] = bytesWritten;
            return ToolResults.Json(result);
        }

        /// <summary>
        /// Atomic-ish write: temp file in the same dir, then replace (mirrors AppSettings'
        /// settings.json write — crash-safe). Returns the UTF-8 (no BOM) byte count written.
        /// </summary>
        private static int WriteAtomic(string full, string content)
        {
            string parent = Path.GetDirectoryName(full);
            string tmp = Path.Combine(parent, "." + Guid.NewGuid().ToString("N") + ".tmp");
            UTF8Encoding utf8NoBom = new UTF8Encoding(false);
            try
            {
                File.WriteAllText(tmp, content, utf8NoBom);
                if (File.Exists(full)) File.Delete(full);
                File.Move(tmp, full);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch { }
            }
            return utf8NoBom.GetByteCount(content);
        }

        // ---- edit ----

        private const int EditChunkChars = 64 * 1024;

        private static CallToolResult Edit(PathSandbox sandbox, ToolCallContext ctx)
        {
            string full;
            CallToolResult err = ResolvePath(sandbox, ctx, out full);
            if (err != null) return err;

            if (Directory.Exists(full)) return ToolResults.Error("path is a directory");
            if (!File.Exists(full)) return ToolResults.Error("file not found");

            string oldString = ctx.Arguments.Value<string>("old_string");
            if (string.IsNullOrEmpty(oldString)) return ToolResults.Error("old_string is required");
            string newString = ctx.Arguments.Value<string>("new_string");
            if (newString == null) return ToolResults.Error("new_string is required");
            bool replaceAll = BoolArg(ctx, "replace_all", false);

            // Stream the file through a transform into a temp file, then atomically move it over the
            // original (mirrors WriteAtomic). No size cap: edit writes to disk, not the model context,
            // so memory is the only concern and it stays bounded by one chunk + the carried tail.
            // A `carry` of (oldString.Length - 1) chars bridges matches that straddle a read boundary.
            StreamReader sr;
            try { sr = OpenTextReaderOrNull(full); }
            catch { return ToolResults.Error("file not found"); }
            if (sr == null) return ToolResults.Error("not a text file");

            string parent = Path.GetDirectoryName(full);
            string tmp = Path.Combine(parent, "." + Guid.NewGuid().ToString("N") + ".tmp");
            int keep = oldString.Length - 1;
            int replacements = 0;
            bool tooMany = false;
            bool committed = false;
            try
            {
                using (sr)
                using (StreamWriter sw = new StreamWriter(
                    new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None),
                    new UTF8Encoding(false)))
                {
                    char[] buf = new char[EditChunkChars];
                    string carry = string.Empty;
                    bool eolResolved = false;
                    int read;
                    while (!tooMany && (read = sr.Read(buf, 0, buf.Length)) > 0)
                    {
                        string chunk = new string(buf, 0, read);

                        // Match against the file's OWN line endings. The read tools (numbered/ranged
                        // reads, search) normalize CRLF/CR to LF before the caller sees them, so an
                        // old_string copied from a read is LF-only even when the file on disk is CRLF.
                        // A byte-exact Ordinal match would then never find a multi-line span. Sniff the
                        // file's dominant newline from the first chunk and translate old/new to it (which
                        // also keeps new_string consistent with the file instead of injecting bare LFs).
                        if (!eolResolved)
                        {
                            string fileNewline = DetectDominantNewline(chunk);
                            oldString = NormalizeNewlines(oldString, fileNewline);
                            newString = NormalizeNewlines(newString, fileNewline);
                            keep = oldString.Length - 1;
                            eolResolved = true;
                        }

                        string window = carry + chunk;
                        carry = ProcessEditWindow(window, oldString, newString, replaceAll, keep,
                            sw, ref replacements, ref tooMany);
                    }
                    // EOF: carry is shorter than oldString, so it cannot contain a full match — emit it.
                    if (!tooMany) sw.Write(carry);
                }

                if (tooMany)
                    return ToolResults.Error("old_string is not unique (matches more than once); "
                        + "add surrounding context or set replace_all");
                if (replacements == 0)
                    return ToolResults.Error("old_string not found in file");

                if (File.Exists(full)) File.Delete(full);
                File.Move(tmp, full);
                committed = true;
            }
            finally
            {
                if (!committed)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); }
                    catch { }
                }
            }

            JObject result = new JObject();
            result["path"] = sandbox.ToRelative(full);
            result["replacements"] = replacements;
            result["bytesWritten"] = new FileInfo(full).Length;
            return ToolResults.Json(result);
        }

        // Process one window of text: write replaced/verbatim content for everything that can be
        // resolved now, and return the trailing (up to `keep`) chars to carry — they might be the
        // prefix of a match completed by the next read. Sets tooMany when a unique edit sees a 2nd hit.
        private static string ProcessEditWindow(string window, string oldString, string newString,
            bool replaceAll, int keep, StreamWriter sw, ref int replacements, ref bool tooMany)
        {
            int pos = 0;
            while (true)
            {
                int idx = window.IndexOf(oldString, pos, StringComparison.Ordinal);
                if (idx < 0) break;

                if (!replaceAll && replacements >= 1)
                {
                    // A second occurrence in unique mode: abort (caller discards the temp file).
                    // (Substring, not Write(window, pos, len): that binds to the composite-format
                    // overload Write(string, object, object) and would emit the whole string.)
                    sw.Write(window.Substring(pos, idx - pos));
                    tooMany = true;
                    return string.Empty;
                }

                sw.Write(window.Substring(pos, idx - pos));   // verbatim gap before the match
                sw.Write(newString);                          // the replacement
                replacements++;
                pos = idx + oldString.Length;
            }

            // No more full matches at/after pos. Emit up to the last `keep` chars; carry the rest so a
            // match spanning into the next read isn't split. (keep == 0 when oldString is a single char.)
            int emitUpto = window.Length - keep;
            if (emitUpto < pos) emitUpto = pos;
            sw.Write(window.Substring(pos, emitUpto - pos));
            return window.Substring(emitUpto);
        }

        // ---- search ----

        private static CallToolResult Search(PathSandbox sandbox, ToolCallContext ctx)
        {
            string query = ctx.Arguments.Value<string>("query");
            if (string.IsNullOrEmpty(query)) return ToolResults.Error("query is required");

            string rel = ctx.Arguments.Value<string>("path");
            string root;
            if (string.IsNullOrEmpty(rel))
            {
                root = sandbox.Root;
            }
            else
            {
                try { root = sandbox.Resolve(rel); }
                catch (SandboxException ex) { return ToolResults.Error(ex.Message); }
            }
            if (!Directory.Exists(root) && !File.Exists(root))
                return ToolResults.Error("path not found");

            bool useRegex = BoolArg(ctx, "regex", false);
            bool ignoreCase = BoolArg(ctx, "ignore_case", false);
            bool multiline = BoolArg(ctx, "multiline", false);
            int maxResults = IntArg(ctx, "max_results", DefaultSearchMax, 1, MaxSearchMax);
            string glob = ctx.Arguments.Value<string>("glob");

            Regex rx = null;
            if (useRegex)
            {
                try
                {
                    RegexOptions opts = RegexOptions.CultureInvariant;
                    if (ignoreCase) opts |= RegexOptions.IgnoreCase;
                    // Multiline mode matches the whole file text, so make ^/$ line anchors (the grep/
                    // ripgrep -U convention). '.' still does not cross newlines unless the pattern opts
                    // in with (?s). NOTE (.NET): $ matches before a '\n', so on CRLF text a trailing '\r'
                    // sits between content and $ — use \r?$ to match line ends regardless of CRLF/LF.
                    if (multiline) opts |= RegexOptions.Multiline;
                    rx = new Regex(query, opts);
                }
                catch (ArgumentException ex)
                {
                    return ToolResults.Error("invalid regex: " + ex.Message);
                }
            }
            Regex globRx = string.IsNullOrEmpty(glob) ? null : GlobToRegex(glob);
            StringComparison cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            List<JObject> matches = new List<JObject>();
            int[] scanned = new int[1];
            bool truncated = SearchWalk(sandbox, root, MaxRecursiveDepth, rx, query, cmp, globRx,
                maxResults, multiline, matches, scanned);

            JObject result = new JObject();
            JArray arr = new JArray();
            foreach (JObject m in matches) arr.Add(m);
            result["matches"] = arr;
            result["count"] = matches.Count;
            result["truncated"] = truncated;
            return ToolResults.Json(result);
        }

        // Returns true if results were truncated (hit a cap before scanning everything).
        private static bool SearchWalk(PathSandbox sandbox, string path, int depthLeft, Regex rx,
            string query, StringComparison cmp, Regex globRx, int maxResults, bool multiline,
            List<JObject> matches, int[] scanned)
        {
            if (File.Exists(path))
                return SearchFile(sandbox, path, rx, query, cmp, globRx, maxResults, multiline, matches, scanned);

            string[] files = Directory.GetFiles(path);
            foreach (string f in files)
            {
                if (SearchFile(sandbox, f, rx, query, cmp, globRx, maxResults, multiline, matches, scanned))
                    return true;
            }
            if (depthLeft > 0)
            {
                string[] dirs = Directory.GetDirectories(path);
                foreach (string d in dirs)
                {
                    if (SearchWalk(sandbox, d, depthLeft - 1, rx, query, cmp, globRx, maxResults, multiline, matches, scanned))
                        return true;
                }
            }
            return false;
        }

        private static bool SearchFile(PathSandbox sandbox, string file, Regex rx, string query,
            StringComparison cmp, Regex globRx, int maxResults, bool multiline, List<JObject> matches, int[] scanned)
        {
            if (globRx != null && !globRx.IsMatch(Path.GetFileName(file))) return false;

            if (scanned[0] >= MaxSearchScanFiles) return true; // scanned-file cap -> truncated
            scanned[0]++;

            if (multiline)
                return SearchFileMultiline(sandbox, file, rx, query, cmp, maxResults, matches);

            // Stream line-by-line: file size is not a limit (output is bounded by maxResults), so
            // large files — exactly where grep matters most — are searchable. Binary/unreadable skip.
            StreamReader sr;
            try { sr = OpenTextReaderOrNull(file); }
            catch { return false; } // unreadable -> skip silently
            if (sr == null) return false; // binary -> skip silently

            string relPath = sandbox.ToRelative(file);
            using (sr)
            {
                int lineNo = 0;
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    lineNo++;
                    bool hit = rx != null ? rx.IsMatch(line) : line.IndexOf(query, cmp) >= 0;
                    if (!hit) continue;

                    JObject m = new JObject();
                    m["path"] = relPath;
                    m["line"] = lineNo;
                    m["text"] = CapText(line);
                    matches.Add(m);
                    if (matches.Count >= maxResults) return true; // result cap -> truncated
                }
            }
            return false;
        }

        // Multiline search: match the whole file text with its line endings intact, so a pattern can
        // span lines and can match \r/\n directly (the grep/ripgrep -U analog). Bounded by the read
        // cap — files over it are skipped (line-mode search still streams them). Reports the 1-based
        // line where each match starts; 'text' is the (capped) matched span, which may contain newlines.
        private static bool SearchFileMultiline(PathSandbox sandbox, string file, Regex rx, string query,
            StringComparison cmp, int maxResults, List<JObject> matches)
        {
            try { if (new FileInfo(file).Length > MaxReadBytes) return false; }
            catch { return false; }

            StreamReader sr;
            try { sr = OpenTextReaderOrNull(file); }
            catch { return false; } // unreadable -> skip silently
            if (sr == null) return false; // binary -> skip silently
            string text;
            using (sr) { text = sr.ReadToEnd(); }

            string relPath = sandbox.ToRelative(file);

            // Matches arrive in non-decreasing index order, so carry a running line counter (count the
            // newlines only between the previous and current match start) to stay O(file length).
            int line = 1;
            int counted = 0;
            int pos = 0;
            while (pos <= text.Length)
            {
                int idx, matchLen;
                string value;
                if (rx != null)
                {
                    Match mm = rx.Match(text, pos);
                    if (!mm.Success) break;
                    idx = mm.Index; matchLen = mm.Length; value = mm.Value;
                }
                else
                {
                    idx = text.IndexOf(query, pos, cmp);
                    if (idx < 0) break;
                    matchLen = query.Length; value = query;
                }

                while (counted < idx) { if (text[counted] == '\n') line++; counted++; }

                JObject m = new JObject();
                m["path"] = relPath;
                m["line"] = line;
                m["text"] = CapText(value);
                matches.Add(m);
                if (matches.Count >= maxResults) return true; // result cap -> truncated

                pos = idx + (matchLen > 0 ? matchLen : 1); // ensure progress on zero-length matches
            }
            return false;
        }

        // ---- delete ----

        private static CallToolResult Delete(PathSandbox sandbox, ToolCallContext ctx)
        {
            string full;
            CallToolResult err = ResolvePath(sandbox, ctx, out full);
            if (err != null) return err;

            if (Directory.Exists(full))
            {
                // Empty directories only — never recursive (bounded blast radius, §2).
                if (Directory.GetFileSystemEntries(full).Length > 0)
                    return ToolResults.Error("directory is not empty");
                Directory.Delete(full, false);
            }
            else if (File.Exists(full))
            {
                File.Delete(full);
            }
            else
            {
                return ToolResults.Error("path not found");
            }

            JObject result = new JObject();
            result["deleted"] = sandbox.ToRelative(full);
            return ToolResults.Json(result);
        }

        // ---- helpers ----

        private static CallToolResult ResolvePath(PathSandbox sandbox, ToolCallContext ctx, out string full)
        {
            full = null;
            string rel = ctx.Arguments.Value<string>("path");
            try
            {
                full = sandbox.Resolve(rel);
                return null;
            }
            catch (SandboxException ex)
            {
                return ToolResults.Error(ex.Message);
            }
        }

        private static bool BoolArg(ToolCallContext ctx, string name, bool fallback)
        {
            JToken t = ctx.Arguments[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            try { return t.Value<bool>(); }
            catch { return fallback; }
        }

        private static bool HasArg(ToolCallContext ctx, string name)
        {
            JToken t = ctx.Arguments[name];
            return t != null && t.Type != JTokenType.Null;
        }

        private static int IntArg(ToolCallContext ctx, string name, int fallback, int min, int max)
        {
            JToken t = ctx.Arguments[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            int n;
            try { n = t.Value<int>(); }
            catch { return fallback; }
            if (n < min) return min;
            if (n > max) return max;
            return n;
        }

        // Split into lines on \n, \r\n, or \r, dropping the separators. A trailing newline does
        // NOT produce a spurious empty final line (so a 3-line file reads as 3 lines).
        private static string[] SplitLines(string text)
        {
            if (text.Length == 0) return new string[] { string.Empty };
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (normalized.Length > 0 && normalized[normalized.Length - 1] == '\n')
                normalized = normalized.Substring(0, normalized.Length - 1);
            return normalized.Split('\n');
        }

        // The file's dominant line-ending style, used by Edit to match against the file's own
        // endings rather than the LF-normalized form the read tools expose. CRLF wins ties (a file
        // with any CRLF is treated as CRLF) since mixed files are almost always CRLF-with-stray-LFs.
        // Returns "\n" when there are no newlines (translation is then a no-op).
        private static string DetectDominantNewline(string sample)
        {
            int crlf = 0;
            int loneLf = 0;
            for (int i = 0; i < sample.Length; i++)
            {
                if (sample[i] != '\n') continue;
                if (i > 0 && sample[i - 1] == '\r') crlf++;
                else loneLf++;
            }
            return (crlf > 0 && crlf >= loneLf) ? "\r\n" : "\n";
        }

        // Rewrite all line endings in s to `target` (first collapse CRLF/CR to LF, then expand).
        private static string NormalizeNewlines(string s, string target)
        {
            if (string.IsNullOrEmpty(s)) return s;
            string lf = s.Replace("\r\n", "\n").Replace('\r', '\n');
            return target == "\r\n" ? lf.Replace("\n", "\r\n") : lf;
        }

        // Number of decimal digits in a non-negative line number (matches its rendered width).
        private static int DigitCount(int n)
        {
            int digits = 1;
            while (n >= 10) { n /= 10; digits++; }
            return digits;
        }

        private static string CapText(string s)
        {
            if (s.Length <= MaxMatchLineLength) return s;
            return s.Substring(0, MaxMatchLineLength);
        }

        // Translate a simple filename wildcard (* and ?) into an anchored, case-insensitive regex.
        private static Regex GlobToRegex(string glob)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('^');
            foreach (char c in glob)
            {
                if (c == '*') sb.Append(".*");
                else if (c == '?') sb.Append('.');
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool LooksBinary(byte[] bytes)
        {
            return LooksBinary(bytes, bytes.Length);
        }

        private static bool LooksBinary(byte[] bytes, int count)
        {
            int n = Math.Min(count, BinarySniffBytes);
            for (int i = 0; i < n; i++)
                if (bytes[i] == 0) return true; // NUL byte → treat as binary
            return false;
        }

        /// <summary>
        /// Open a file as BOM-aware UTF-8 text lines after sniffing its head for a NUL byte. Returns
        /// <c>null</c> (stream disposed) if the file looks binary. Streaming lets search and ranged
        /// read work on files larger than the whole-file cap — output is bounded by matches / range,
        /// not file size. Caller owns the returned reader (wrap in <c>using</c>).
        /// </summary>
        private static StreamReader OpenTextReaderOrNull(string file)
        {
            FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            try
            {
                byte[] head = new byte[BinarySniffBytes];
                int n = fs.Read(head, 0, head.Length);
                if (LooksBinary(head, n)) { fs.Dispose(); return null; }
                fs.Position = 0;
                return new StreamReader(fs, new UTF8Encoding(false, false), true);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        private static string DecodeUtf8(byte[] bytes)
        {
            // BOM-aware: strip a leading UTF-8 BOM if present.
            int start = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                start = 3;
            UTF8Encoding utf8 = new UTF8Encoding(false, false);
            return utf8.GetString(bytes, start, bytes.Length - start);
        }
    }
}
