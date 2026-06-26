using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mcp35.Core.Diagnostics;
using Mcp35.Core.Security;

namespace MemoryMcpServer
{
    /// <summary>A user-facing validation failure (relayed to the model as an isError result).</summary>
    internal sealed class MemoryException : Exception
    {
        public MemoryException(string message) : base(message) { }
    }

    /// <summary>One line of the index: a slug handle, a single-line summary, and whether a detail file exists.</summary>
    internal sealed class MemoryEntry
    {
        public string Slug;
        public string Summary;
        public bool HasDetail;
    }

    /// <summary>
    /// Reads and writes the .gxpt/memory store. The server is the <b>only</b> writer, so
    /// <c>memory.md</c> is rewritten canonically on every change - a flat list of
    /// "<c>- slug: summary</c>" lines (one entry per line, with a "<c> -> slug.md</c>" marker when a
    /// detail file exists). Detail lives in <c>&lt;slug&gt;.md</c>, read on demand. All writes are
    /// atomic (temp file + replace) so concurrent tabs sharing a workdir can't tear a file.
    /// </summary>
    internal sealed class MemoryStore
    {
        private const string IndexFileName = "memory.md";
        private const string GitignoreName = ".gitignore";
        // U+2192 right-arrow shown in the index, written via escape so the source stays ASCII (csc
        // reads these files without a BOM and would otherwise mangle a literal multibyte char).
        private const string DetailMarker = " \u2192 ";
        private const char DetailMarkerChar = '\u2192';
        private static readonly char[] LineSplit = new char[] { '\n' };
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly string _root;
        private readonly PathSandbox _sandbox;
        private readonly int _maxLines;
        private readonly ILogSink _log;

        public MemoryStore(string root, int maxLines, ILogSink log)
        {
            if (string.IsNullOrEmpty(root)) throw new ArgumentException("root is required", "root");
            _root = root;
            _sandbox = new PathSandbox(root, "workspace root");
            _maxLines = maxLines > 0 ? maxLines : MemoryConfig.DefaultMaxLines;
            _log = log != null ? log : NullLogSink.Instance;
        }

        // ---- operations ----

        public string Remember(string name, string summary, string detail)
        {
            string slug = MakeSlug(name);
            summary = NormalizeSummary(summary);

            EnsureStore();
            List<MemoryEntry> entries = Load();
            if (FindIndex(entries, slug) >= 0)
                throw new MemoryException("a memory named '" + slug
                    + "' already exists; use update_memory to change it");

            MemoryEntry e = new MemoryEntry();
            e.Slug = slug;
            e.Summary = summary;
            e.HasDetail = !string.IsNullOrEmpty(detail);
            if (e.HasDetail) WriteDetail(slug, detail);
            entries.Add(e);
            Save(entries);
            return "Remembered '" + slug + "'." + CapNote(entries.Count);
        }

        public string Read(string name)
        {
            string slug = MakeSlug(name);
            string path = DetailPath(slug);
            if (!File.Exists(path))
                throw new MemoryException("no detail file for '" + slug
                    + "' (this memory has only an index summary, or does not exist)");
            return File.ReadAllText(path, Encoding.UTF8);
        }

        public string Update(string name, string summary, string detail, bool hasSummary, bool hasDetail)
        {
            string slug = MakeSlug(name);
            EnsureStore();
            List<MemoryEntry> entries = Load();
            int idx = FindIndex(entries, slug);
            if (idx < 0)
                throw new MemoryException("no memory named '" + slug + "'; use remember to add it");

            MemoryEntry e = entries[idx];
            if (hasSummary)
                e.Summary = NormalizeSummary(summary);
            if (hasDetail)
            {
                if (string.IsNullOrEmpty(detail))
                {
                    DeleteDetail(slug);
                    e.HasDetail = false;
                }
                else
                {
                    WriteDetail(slug, detail);
                    e.HasDetail = true;
                }
            }
            Save(entries);
            return "Updated '" + slug + "'.";
        }

        public string Forget(string name)
        {
            string slug = MakeSlug(name);
            EnsureStore();
            List<MemoryEntry> entries = Load();
            int idx = FindIndex(entries, slug);
            if (idx < 0)
                throw new MemoryException("no memory named '" + slug + "'");

            entries.RemoveAt(idx);
            DeleteDetail(slug);
            Save(entries);
            return "Forgot '" + slug + "'.";
        }

        public string Consolidate(IList<string> names, string newName, string summary, string detail)
        {
            if (names == null || names.Count == 0)
                throw new MemoryException("consolidate requires at least one name in 'names'");
            string newSlug = MakeSlug(newName);
            summary = NormalizeSummary(summary);
            if (string.IsNullOrEmpty(detail))
                throw new MemoryException("consolidate requires 'detail' (the merged note)");

            // Resolve and de-duplicate the source slugs.
            List<string> sources = new List<string>();
            for (int i = 0; i < names.Count; i++)
            {
                string s = MakeSlug(names[i]);
                if (!ContainsSlug(sources, s)) sources.Add(s);
            }

            EnsureStore();
            List<MemoryEntry> entries = Load();
            for (int i = 0; i < sources.Count; i++)
            {
                if (FindIndex(entries, sources[i]) < 0)
                    throw new MemoryException("no memory named '" + sources[i] + "'");
            }
            // The new entry may reuse a source name (replacing it); otherwise it must be free.
            if (!ContainsSlug(sources, newSlug) && FindIndex(entries, newSlug) >= 0)
                throw new MemoryException("a memory named '" + newSlug
                    + "' already exists and is not among the consolidated entries");

            // Write the merged detail first, then drop every source's index entry and detail file
            // (except the new slug's, which we just wrote), then add the consolidated entry.
            WriteDetail(newSlug, detail);
            for (int i = 0; i < sources.Count; i++)
            {
                RemoveEntry(entries, sources[i]);
                if (!SlugEquals(sources[i], newSlug)) DeleteDetail(sources[i]);
            }
            RemoveEntry(entries, newSlug);

            MemoryEntry merged = new MemoryEntry();
            merged.Slug = newSlug;
            merged.Summary = summary;
            merged.HasDetail = true;
            entries.Add(merged);
            Save(entries);
            return "Consolidated " + sources.Count + " entr" + (sources.Count == 1 ? "y" : "ies")
                + " into '" + newSlug + "'." + CapNote(entries.Count);
        }

        // ---- index io ----

        public List<MemoryEntry> Load()
        {
            List<MemoryEntry> list = new List<MemoryEntry>();
            string path = IndexPath();
            if (!File.Exists(path)) return list;

            string text = File.ReadAllText(path, Encoding.UTF8);
            string[] lines = text.Split(LineSplit);
            for (int i = 0; i < lines.Length; i++)
            {
                MemoryEntry e = ParseLine(lines[i]);
                if (e != null && FindIndex(list, e.Slug) < 0) list.Add(e);
            }
            return list;
        }

        private void Save(List<MemoryEntry> entries)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < entries.Count; i++)
            {
                MemoryEntry e = entries[i];
                sb.Append("- ").Append(e.Slug).Append(": ").Append(e.Summary);
                if (e.HasDetail) sb.Append(DetailMarker).Append(e.Slug).Append(".md");
                sb.Append('\n');
            }
            AtomicWrite(IndexPath(), sb.ToString());
        }

        private MemoryEntry ParseLine(string raw)
        {
            if (raw == null) return null;
            string line = raw.Trim();
            if (line.Length == 0) return null;
            if (line.StartsWith("- ")) line = line.Substring(2).Trim();

            int colon = line.IndexOf(':');
            if (colon <= 0) return null;
            string slug = line.Substring(0, colon).Trim();
            if (slug.Length == 0) return null;

            string rest = line.Substring(colon + 1).Trim();
            int marker = rest.IndexOf(DetailMarkerChar);
            if (marker >= 0) rest = rest.Substring(0, marker).Trim();

            MemoryEntry e = new MemoryEntry();
            e.Slug = slug;
            e.Summary = rest;
            e.HasDetail = File.Exists(DetailPath(slug));
            return e;
        }

        // ---- detail files ----

        private void WriteDetail(string slug, string content)
        {
            EnsureStore();
            AtomicWrite(DetailPath(slug), content ?? string.Empty);
        }

        private void DeleteDetail(string slug)
        {
            try
            {
                string path = DetailPath(slug);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                _log.Log("memory", "could not delete detail for '" + slug + "': " + ex.Message);
            }
        }

        private string DetailPath(string slug)
        {
            return _sandbox.Resolve(slug + ".md");
        }

        private string IndexPath()
        {
            return Path.Combine(_root, IndexFileName);
        }

        // ---- store setup ----

        // Create .gxpt/memory and seed a gitignore so personal memory isn't committed unless opted in.
        private void EnsureStore()
        {
            Directory.CreateDirectory(_root);
            try
            {
                string gitignore = Path.Combine(_root, GitignoreName);
                if (!File.Exists(gitignore))
                    File.WriteAllText(gitignore, "*\n", Utf8NoBom);
            }
            catch (Exception ex)
            {
                _log.Log("memory", "could not seed .gitignore: " + ex.Message);
            }
        }

        // ---- helpers ----

        private static string MakeSlug(string name)
        {
            string err;
            string slug = Slug.Make(name, out err);
            if (slug == null) throw new MemoryException(err);
            return slug;
        }

        // Collapse to a single line: a summary is index-only and must never span lines.
        private static string NormalizeSummary(string summary)
        {
            if (summary == null) throw new MemoryException("summary is required");
            string s = summary.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (s.Length == 0) throw new MemoryException("summary is required");
            return s;
        }

        private string CapNote(int count)
        {
            if (count <= _maxLines) return string.Empty;
            return " Note: the memory index now has " + count + " entries (soft cap " + _maxLines
                + "). Consider consolidating related entries or forgetting stale ones to keep it light.";
        }

        private static int FindIndex(List<MemoryEntry> entries, string slug)
        {
            for (int i = 0; i < entries.Count; i++)
                if (SlugEquals(entries[i].Slug, slug)) return i;
            return -1;
        }

        private static void RemoveEntry(List<MemoryEntry> entries, string slug)
        {
            int idx = FindIndex(entries, slug);
            if (idx >= 0) entries.RemoveAt(idx);
        }

        private static bool ContainsSlug(List<string> slugs, string slug)
        {
            for (int i = 0; i < slugs.Count; i++)
                if (SlugEquals(slugs[i], slug)) return true;
            return false;
        }

        // Slugs are compared case-insensitively because the detail filename lives on a
        // case-insensitive (Windows) filesystem.
        private static bool SlugEquals(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static void AtomicWrite(string path, string content)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllText(tmp, content, Utf8NoBom);
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); return; }
                catch { File.Delete(path); }
            }
            File.Move(tmp, path);
        }
    }
}
