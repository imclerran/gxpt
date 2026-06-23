using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace GxPT
{
    // Attaches image files as normalized binary payloads (Kind = Image).
    // Accepts png/jpeg/gif/bmp/tiff; rejects webp (GDI+ on XP cannot decode it).
    // Normalizes at attach time — encode-once means the stored base64 bytes are
    // byte-identical on every replayed turn, keeping prompt-cache prefixes stable.
    internal sealed class ImageAttachmentExtractor : IAttachmentExtractor
    {
        // Hard cap on the stored (post-normalize) bytes. Configurable later; 500 KB keeps
        // transcripts bounded and satisfies the XP memory-discipline requirement (§5).
        private const int MaxStoredBytes = 500 * 1024;
        // Anthropic's no-auto-downscale threshold: images within this bound are processed
        // as-is; larger ones are downscaled so the provider receives max-fidelity pixels.
        private const int MaxLongEdge = 1568;

        private static readonly string[] Extensions =
            { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tiff", ".tif" };

        public bool CanHandle(string filePath)
        {
            return IsImageFile(filePath);
        }

        // True if the path's extension is a decodable image type this extractor accepts.
        // Static so attach-UI gating (MainForm) can identify image drops without an instance.
        // WebP is intentionally excluded (GDI+ on XP cannot decode it).
        public static bool IsImageFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            string ext = Path.GetExtension(filePath);
            if (string.IsNullOrEmpty(ext)) return false;
            ext = ext.ToLowerInvariant();
            for (int i = 0; i < Extensions.Length; i++)
                if (Extensions[i] == ext) return true;
            return false;
        }

        public AttachedFile Extract(string filePath)
        {
            byte[] raw = File.ReadAllBytes(filePath);
            if (raw == null || raw.Length == 0)
                throw new InvalidOperationException("Image file is empty.");

            int width, height;
            string mediaType;
            byte[] normalized = Normalize(raw, out width, out height, out mediaType);

            if (normalized.Length > MaxStoredBytes)
                throw new InvalidOperationException(string.Format(
                    "Image is too large after normalization ({0:N0} KB). Maximum is {1:N0} KB.",
                    normalized.Length / 1024, MaxStoredBytes / 1024));

            return new AttachedFile
            {
                FileName = Path.GetFileName(filePath),
                Kind = AttachmentKind.Image,
                MediaType = mediaType,
                Data = Convert.ToBase64String(normalized),
                Width = width,
                Height = height
            };
        }

        public IList<string> GetFileDialogPatterns()
        {
            return new List<string> { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.tiff", "*.tif" };
        }

        public string GetCategoryLabel()
        {
            return "Image Files";
        }

        // Decode with GDI+, downscale if needed, re-encode with alpha-based format selection.
        // Pass-through path (no re-encode) when source is already a valid wire type (png/jpeg/gif),
        // within dimension bounds, and within the byte cap — preserving exact source bytes keeps
        // the base64 in Data byte-identical to the original, which is a cache-neutrality guarantee.
        private static byte[] Normalize(byte[] source, out int width, out int height, out string mediaType)
        {
            using (var inMs = new MemoryStream(source))
            using (var img = Image.FromStream(inMs))
            {
                width = img.Width;
                height = img.Height;

                string srcMedia = RawFormatToMediaType(img.RawFormat);
                bool isWireType = srcMedia == "image/png"
                               || srcMedia == "image/jpeg"
                               || srcMedia == "image/gif";
                bool needsDownscale = width > MaxLongEdge || height > MaxLongEdge;

                // Pass-through: already wire-safe, within bounds, and within the byte cap.
                if (isWireType && !needsDownscale && source.Length <= MaxStoredBytes)
                {
                    mediaType = srcMedia;
                    return source;
                }

                // GIF that needs downscaling: convert to PNG (GIF has no JPEG path; animation is
                // already flattened to the first frame by GDI+ on load, so nothing extra is lost).
                bool sourceIsGif = srcMedia == "image/gif";

                // Alpha-based format: check the SOURCE image (before downscaling). Creating the
                // downscaled bitmap as Format32bppArgb always sets the alpha bit, so checking
                // the downscaled copy would always pick PNG — check img.PixelFormat instead.
                bool hasAlpha = Image.IsAlphaPixelFormat(img.PixelFormat);

                Image target = img;
                Bitmap downscaled = null;
                try
                {
                    if (needsDownscale)
                    {
                        float scale = (float)MaxLongEdge / (float)Math.Max(width, height);
                        int newW = Math.Max(1, (int)(width * scale));
                        int newH = Math.Max(1, (int)(height * scale));
                        downscaled = new Bitmap(newW, newH, PixelFormat.Format32bppArgb);
                        using (Graphics g = Graphics.FromImage(downscaled))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(img, 0, 0, newW, newH);
                        }
                        target = downscaled;
                        width = newW;
                        height = newH;
                    }

                    ImageFormat encFmt;
                    if (sourceIsGif || hasAlpha)
                    {
                        encFmt = ImageFormat.Png;
                        mediaType = "image/png";
                    }
                    else
                    {
                        encFmt = ImageFormat.Jpeg;
                        mediaType = "image/jpeg";
                    }

                    using (var outMs = new MemoryStream())
                    {
                        target.Save(outMs, encFmt);
                        return outMs.ToArray();
                    }
                }
                finally
                {
                    if (downscaled != null) downscaled.Dispose();
                }
            }
        }

        private static string RawFormatToMediaType(ImageFormat fmt)
        {
            if (fmt.Equals(ImageFormat.Png)) return "image/png";
            if (fmt.Equals(ImageFormat.Jpeg)) return "image/jpeg";
            if (fmt.Equals(ImageFormat.Gif)) return "image/gif";
            if (fmt.Equals(ImageFormat.Bmp)) return "image/bmp";
            if (fmt.Equals(ImageFormat.Tiff)) return "image/tiff";
            return "image/png"; // fallback: treat as PNG (will be re-encoded)
        }
    }
}
