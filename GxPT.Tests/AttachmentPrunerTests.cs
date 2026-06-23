using System.Collections.Generic;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    // §10 prune-with-placeholder budgeting: keep newest-first within the global-conservative caps,
    // never prune the current turn's own attachments, tighten under context pressure.
    public class AttachmentPrunerTests
    {
        private const int OneMb = 1024 * 1024;

        private static AttachedFile Img(string name, int dataLen)
        {
            return new AttachedFile
            {
                FileName = name,
                Kind = AttachmentKind.Image,
                MediaType = "image/png",
                Data = new string('x', dataLen)
            };
        }

        private static AttachedFile Pdf(string name, int dataLen, bool native)
        {
            return new AttachedFile
            {
                FileName = name,
                Kind = AttachmentKind.Pdf,
                MediaType = "application/pdf",
                Content = "extracted text",
                Data = new string('x', dataLen),
                SendNativePdf = native
            };
        }

        private static ChatMessage Msg(params AttachedFile[] atts)
        {
            return new ChatMessage("user", "hi") { Attachments = new List<AttachedFile>(atts) };
        }

        // Each attachment in its own message, oldest first.
        private static List<ChatMessage> EachInOwnMessage(IEnumerable<AttachedFile> atts)
        {
            var h = new List<ChatMessage>();
            foreach (var a in atts) h.Add(Msg(a));
            return h;
        }

        [Fact]
        public void Null_or_empty_history_prunes_nothing()
        {
            Assert.Empty(AttachmentPruner.ComputePruned(null, true, true, false, 0, 0));
            Assert.Empty(AttachmentPruner.ComputePruned(new List<ChatMessage>(), true, true, false, 0, 0));
        }

        [Fact]
        public void Under_caps_prunes_nothing()
        {
            var imgs = new List<AttachedFile>();
            for (int i = 0; i < 5; i++) imgs.Add(Img("p" + i + ".png", 1000));
            var pruned = AttachmentPruner.ComputePruned(EachInOwnMessage(imgs), true, true, false, 0, 0);
            Assert.Empty(pruned);
        }

        [Fact]
        public void Image_count_over_cap_prunes_oldest_first()
        {
            // 12 single-image messages, tiny bytes: keep newest 10, prune the 2 oldest.
            var imgs = new List<AttachedFile>();
            for (int i = 0; i < 12; i++) imgs.Add(Img("p" + i + ".png", 10));
            var pruned = AttachmentPruner.ComputePruned(EachInOwnMessage(imgs), true, true, false, 0, 0);

            Assert.Equal(2, pruned.Count);
            Assert.Contains(imgs[0], pruned);
            Assert.Contains(imgs[1], pruned);
            Assert.DoesNotContain(imgs[11], pruned); // newest always kept
        }

        [Fact]
        public void Byte_cap_prunes_oldest_first()
        {
            // 6 × 1 MB images, 4 MB budget: keep the newest 4 (= 4 MB), prune the 2 oldest.
            var imgs = new List<AttachedFile>();
            for (int i = 0; i < 6; i++) imgs.Add(Img("p" + i + ".png", OneMb));
            var pruned = AttachmentPruner.ComputePruned(EachInOwnMessage(imgs), true, true, false, 0, 0);

            Assert.Equal(2, pruned.Count);
            Assert.Contains(imgs[0], pruned);
            Assert.Contains(imgs[1], pruned);
        }

        [Fact]
        public void Context_pressure_tightens_image_cap_to_three()
        {
            // 6 small single-image messages; prior tokens at 90% of context => pressured (cap 3).
            var imgs = new List<AttachedFile>();
            for (int i = 0; i < 6; i++) imgs.Add(Img("p" + i + ".png", 10));
            var pruned = AttachmentPruner.ComputePruned(EachInOwnMessage(imgs), true, true, false, 90, 100);

            Assert.Equal(3, pruned.Count); // keep newest 3, prune 3 oldest
            Assert.Contains(imgs[0], pruned);
            Assert.Contains(imgs[2], pruned);
            Assert.DoesNotContain(imgs[3], pruned);
        }

        [Fact]
        public void Below_pressure_ratio_uses_full_caps()
        {
            var imgs = new List<AttachedFile>();
            for (int i = 0; i < 6; i++) imgs.Add(Img("p" + i + ".png", 10));
            // 50% of context is well under the 0.85 ratio: full caps, nothing pruned.
            var pruned = AttachmentPruner.ComputePruned(EachInOwnMessage(imgs), true, true, false, 50, 100);
            Assert.Empty(pruned);
        }

        [Fact]
        public void Current_turn_attachments_are_never_pruned()
        {
            // A single message carrying 15 images: all belong to the newest turn, so none are pruned.
            var atts = new List<AttachedFile>();
            for (int i = 0; i < 15; i++) atts.Add(Img("p" + i + ".png", 10));
            var history = new List<ChatMessage> { Msg(atts.ToArray()) };
            var pruned = AttachmentPruner.ComputePruned(history, true, true, false, 0, 0);
            Assert.Empty(pruned);
        }

        [Fact]
        public void Images_not_eligible_without_vision_are_not_pruned()
        {
            var imgs = new List<AttachedFile>();
            for (int i = 0; i < 12; i++) imgs.Add(Img("p" + i + ".png", 10));
            // supportsImage = false: images render as placeholders anyway, never native, so never pruned.
            var pruned = AttachmentPruner.ComputePruned(EachInOwnMessage(imgs), false, true, false, 0, 0);
            Assert.Empty(pruned);
        }

        [Fact]
        public void Native_pdfs_under_zdr_are_not_eligible()
        {
            var pdfs = new List<AttachedFile>();
            for (int i = 0; i < 12; i++) pdfs.Add(Pdf("d" + i + ".pdf", OneMb, true));
            // ZDR forces PDFs to the local text path: not native, so not counted or pruned here.
            var pruned = AttachmentPruner.ComputePruned(EachInOwnMessage(pdfs), true, true, true, 0, 0);
            Assert.Empty(pruned);
        }

        [Fact]
        public void Native_pdfs_count_toward_byte_budget()
        {
            // 6 × 1 MB native PDFs, 4 MB budget: keep newest 4, prune 2 oldest (no image-count limit hit).
            var pdfs = new List<AttachedFile>();
            for (int i = 0; i < 6; i++) pdfs.Add(Pdf("d" + i + ".pdf", OneMb, true));
            var pruned = AttachmentPruner.ComputePruned(EachInOwnMessage(pdfs), true, true, false, 0, 0);

            Assert.Equal(2, pruned.Count);
            Assert.Contains(pdfs[0], pruned);
            Assert.Contains(pdfs[1], pruned);
        }

        [Fact]
        public void Non_native_pdfs_are_not_eligible()
        {
            // SendNativePdf not set: PDF goes as extracted text, never native, so never pruned.
            var pdfs = new List<AttachedFile>();
            for (int i = 0; i < 12; i++) pdfs.Add(Pdf("d" + i + ".pdf", OneMb, false));
            var pruned = AttachmentPruner.ComputePruned(EachInOwnMessage(pdfs), true, true, false, 0, 0);
            Assert.Empty(pruned);
        }
    }
}
