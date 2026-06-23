using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using iTextSharp.text.pdf;
using Parser = iTextSharp.text.pdf.parser;

namespace GxPT
{
    internal sealed class PdfAttachmentExtractor : IAttachmentExtractor
    {
        public bool CanHandle(string filePath)
        {
            try
            {
                string ext = Path.GetExtension(filePath);
                ext = string.IsNullOrEmpty(ext) ? string.Empty : ext.ToLowerInvariant();
                return ext == ".pdf";
            }
            catch { return false; }
        }

        public AttachedFile Extract(string filePath)
        {
            int pageCount;
            string text = ExtractText(filePath, out pageCount) ?? string.Empty;
            byte[] bytes = null;
            try { bytes = File.ReadAllBytes(filePath); }
            catch { bytes = null; }

            // Scanned/image-only PDF: has pages but no extractable text layer. The text path can't
            // represent it, so flag it for the attach-time native-PDF prompt.
            bool likelyScanned = pageCount > 0 && string.IsNullOrEmpty(text.Trim());

            var af = new AttachedFile
            {
                FileName = Path.GetFileName(filePath),
                Content = text,                 // dual-representation: cheap text path (default)
                Kind = AttachmentKind.Pdf,
                IsLikelyScanned = likelyScanned
            };
            // Retain original bytes so the PDF can be escalated to a native `file` block later.
            if (bytes != null && bytes.Length > 0)
            {
                af.MediaType = "application/pdf";
                af.Data = Convert.ToBase64String(bytes);
            }
            return af;
        }

        public IList<string> GetFileDialogPatterns()
        {
            return new List<string> { "*.pdf" };
        }

        public string GetCategoryLabel()
        {
            return "PDF Files";
        }

        private static string ExtractText(string filePath, out int pageCount)
        {
            pageCount = 0;
            try
            {
                var sb = new StringBuilder(4096);
                using (var reader = new PdfReader(filePath))
                {
                    int n = reader.NumberOfPages;
                    pageCount = n;
                    for (int page = 1; page <= n; page++)
                    {
                        Parser.ITextExtractionStrategy strategy = new Parser.LocationTextExtractionStrategy();
                        string pageText = Parser.PdfTextExtractor.GetTextFromPage(reader, page, strategy);
                        pageText = SanitizeDisplayText(pageText);
                        if (!string.IsNullOrEmpty(pageText))
                        {
                            sb.AppendLine(pageText);
                            sb.AppendLine();
                        }
                    }
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                try { Logger.Log("PDF", "Failed to extract PDF: " + ex.Message); }
                catch { }
                throw;
            }
        }

        // Copied from MainForm and kept internal to avoid dependency back into UI
        private static string SanitizeDisplayText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

            bool needs = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\')
                {
                    if (i + 5 < text.Length && text[i + 1] == 'u' && text[i + 2] == '0' && text[i + 3] == '0' && text[i + 4] == '0' && text[i + 5] == '0')
                    { needs = true; break; }
                }
                if (c == '\0' || (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t'))
                { needs = true; break; }
            }
            if (!needs)
            {
                return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine);
            }

            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\' && i + 5 < text.Length && text[i + 1] == 'u' && text[i + 2] == '0' && text[i + 3] == '0' && text[i + 4] == '0' && text[i + 5] == '0')
                {
                    sb.Append(' ');
                    i += 5;
                    continue;
                }
                if (c == '\0') { sb.Append(' '); continue; }
                if (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t') { sb.Append(' '); continue; }
                if (char.IsSurrogate(c))
                {
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        sb.Append(c);
                        sb.Append(text[i + 1]);
                        i++;
                        continue;
                    }
                    sb.Append(' ');
                    continue;
                }
                sb.Append(c);
            }

            string cleaned = sb.ToString();
            cleaned = cleaned.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine);
            return cleaned;
        }
    }
}
