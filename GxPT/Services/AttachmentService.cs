using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GxPT
{
    // Contract for pluggable attachment extractors
    internal interface IAttachmentExtractor
    {
        // True if this extractor can handle the specified path
        bool CanHandle(string filePath);
        // Extracts and returns an AttachedFile (FileName + textual Content)
        AttachedFile Extract(string filePath);
        // File dialog patterns, e.g., ["*.txt", "*.md"]
        IList<string> GetFileDialogPatterns();
        // Optional: a short category label (e.g., "Text Files", "PDF Files")
        string GetCategoryLabel();
    }

    // Coordinator that routes files to the appropriate extractor and builds dialog filters
    internal sealed class AttachmentService
    {
        private readonly List<IAttachmentExtractor> _extractors;
        private readonly TextFileAttachmentExtractor _textExtractor;
        private readonly ImageAttachmentExtractor _imageExtractor;

        // Hard capability gate: when false, the image extractor is skipped everywhere (dialog
        // filter, IsSupported, extraction) so images cannot be attached to a non-vision model.
        // The caller (MainForm) sets this from the selected model before each attach operation.
        public bool ImageAttachmentsEnabled { get; set; }

        public AttachmentService()
        {
            ImageAttachmentsEnabled = true;
            _extractors = new List<IAttachmentExtractor>();
            // Register built-in extractors (order matters only for dialog filter composition).
            // Images before text: the text extractor's byte-sniffer rejects binary files anyway,
            // but explicit ordering makes the intent clear.
            _imageExtractor = new ImageAttachmentExtractor();
            _extractors.Add(_imageExtractor);
            _textExtractor = new TextFileAttachmentExtractor(null);
            _extractors.Add(_textExtractor);
            _extractors.Add(new PdfAttachmentExtractor());
            _extractors.Add(new DocxAttachmentExtractor());
        }

        // An extractor is active unless it's the image extractor while images are gated off.
        private bool IsActive(IAttachmentExtractor ex)
        {
            if (!ImageAttachmentsEnabled && ReferenceEquals(ex, _imageExtractor)) return false;
            return true;
        }

        public AttachmentService(IEnumerable<string> additionalTextFilePatterns)
            : this()
        {
            try { if (_textExtractor != null) _textExtractor.SetAdditionalPatterns(additionalTextFilePatterns); }
            catch { }
        }

        // Allow late binding/injection of syntax-highlighter patterns without reflection
        public void SetAdditionalTextPatterns(IEnumerable<string> patterns)
        {
            try { if (_textExtractor != null) _textExtractor.SetAdditionalPatterns(patterns); }
            catch { }
        }

        public void RegisterExtractor(IAttachmentExtractor extractor)
        {
            if (extractor == null) return;
            _extractors.Add(extractor);
        }

        public bool IsSupported(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            if (Directory.Exists(filePath)) return false; // folders not supported
            for (int i = 0; i < _extractors.Count; i++)
            {
                if (!IsActive(_extractors[i])) continue;
                try { if (_extractors[i].CanHandle(filePath)) return true; }
                catch { }
            }
            return false;
        }

        public string BuildOpenFileDialogFilter()
        {
            // Compose: "Supported Files|<all>;...|<Per-category>|All Files|*.*"
            var all = new List<string>();
            var cats = new List<string>();
            for (int i = 0; i < _extractors.Count; i++)
            {
                var ex = _extractors[i];
                if (!IsActive(ex)) continue; // images gated off -> omit the Image Files category
                IList<string> pats = null; string label = null;
                try { pats = ex.GetFileDialogPatterns(); }
                catch { }
                try { label = ex.GetCategoryLabel(); }
                catch { }
                if (pats == null || pats.Count == 0) continue;

                // Ensure highlighter patterns are included under the Text Files category as well
                if (ex is TextFileAttachmentExtractor)
                {
                    try
                    {
                        string[] hi = SyntaxHighlighter.GetAllHighlighterFilePatterns();
                        if (hi != null && hi.Length > 0)
                        {
                            var merged = new List<string>(pats);
                            for (int j = 0; j < hi.Length; j++)
                            {
                                var p = hi[j];
                                if (!string.IsNullOrEmpty(p)) merged.Add(p);
                            }
                            pats = Dedup(merged);
                        }
                    }
                    catch { }
                }
                all.AddRange(pats);
                if (!string.IsNullOrEmpty(label))
                {
                    cats.Add(label + "|" + JoinPatterns(pats));
                }
            }
            string allJoined = JoinPatterns(Dedup(all));
            var sb = new StringBuilder();
            sb.Append("Supported Files|").Append(allJoined);
            for (int i = 0; i < cats.Count; i++)
            {
                sb.Append('|').Append(cats[i]);
            }
            sb.Append("|All Files|*.*");
            return sb.ToString();
        }

        private static List<string> Dedup(IEnumerable<string> patterns)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            foreach (var p in patterns)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (seen.Contains(p)) continue;
                seen.Add(p); list.Add(p);
            }
            return list;
        }

        private static string JoinPatterns(IList<string> pats)
        {
            if (pats == null || pats.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            for (int i = 0; i < pats.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(pats[i]);
            }
            return sb.ToString();
        }

        public List<AttachedFile> ExtractMany(IEnumerable<string> paths, out List<string> skippedDisplayNames)
        {
            skippedDisplayNames = new List<string>();
            var results = new List<AttachedFile>();
            if (paths == null) return results;
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                try
                {
                    if (Directory.Exists(path)) { skippedDisplayNames.Add(Path.GetFileName(path) + " (folder)"); continue; }
                    IAttachmentExtractor ex = FindExtractor(path);
                    if (ex == null) { skippedDisplayNames.Add(Path.GetFileName(path)); continue; }
                    var af = ex.Extract(path);
                    if (af != null) results.Add(af);
                    else skippedDisplayNames.Add(Path.GetFileName(path));
                }
                catch (Exception)
                {
                    skippedDisplayNames.Add(Path.GetFileName(path));
                }
            }
            return results;
        }

        public AttachedFile ExtractOne(string path)
        {
            var ex = FindExtractor(path);
            if (ex == null) return null;
            return ex.Extract(path);
        }

        private IAttachmentExtractor FindExtractor(string path)
        {
            for (int i = 0; i < _extractors.Count; i++)
            {
                if (!IsActive(_extractors[i])) continue;
                try { if (_extractors[i].CanHandle(path)) return _extractors[i]; }
                catch { }
            }
            return null;
        }
    }
}
