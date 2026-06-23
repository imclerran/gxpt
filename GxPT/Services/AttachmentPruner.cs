using System.Collections.Generic;

namespace GxPT
{
    // Transient prune-with-placeholder budgeting (multimodal spec §10). Decides which native-eligible
    // attachments to demote to text placeholders on a given request so the replayed binary set stays
    // within bounds that are safe across all providers without per-provider catalog data.
    //
    // Pure logic — no I/O, no WinForms — so it is unit-tested directly. Prune state is never persisted;
    // it is recomputed every request from the live token numbers (oldest-first, newest exempt).
    internal static class AttachmentPruner
    {
        public const int MaxNativeImages = 10;                       // safety-net image count cap
        public const long MaxNativeBytes = 4L * 1024 * 1024;         // ~4 MB of base64 payload
        public const int MaxNativeImagesPressured = 3;               // tighter caps under context pressure
        public const long MaxNativeBytesPressured = 1L * 1024 * 1024;
        public const double ContextPressureRatio = 0.85;             // prior prompt tokens vs context length

        // Decide which native-eligible attachments to demote to placeholders, chosen OLDEST-FIRST.
        // The newest message's own attachments are never pruned (the current turn's images/PDFs always
        // ride native); only older turns shed their heavy binary once the caps are exceeded. When the
        // prior turn's prompt tokens approach the model's context window the caps tighten so the next
        // send has headroom. Pass priorPromptTokens/contextLength = 0 to disable the token trigger (the
        // count/byte safety net still applies). Returns a reference-identity set of attachments to demote.
        public static HashSet<AttachedFile> ComputePruned(
            List<ChatMessage> history, bool supportsImage, bool supportsFile, bool zdr,
            int priorPromptTokens, int contextLength)
        {
            var pruned = new HashSet<AttachedFile>();
            if (history == null) return pruned;

            // Collect native-eligible attachments in order (oldest first), tracking each one's message
            // index so the newest message carrying binary can be exempted from pruning.
            var eligible = new List<AttachedFile>();
            var eligibleMsg = new List<int>();
            for (int i = 0; i < history.Count; i++)
            {
                var m = history[i];
                if (m == null || m.Attachments == null) continue;
                for (int j = 0; j < m.Attachments.Count; j++)
                {
                    var af = m.Attachments[j];
                    if (af == null || string.IsNullOrEmpty(af.Data)) continue;
                    bool nativeEligible =
                        (af.EffectiveKind == AttachmentKind.Image && supportsImage)
                        || (af.EffectiveKind == AttachmentKind.Pdf && af.SendNativePdf == true && supportsFile && !zdr);
                    if (!nativeEligible) continue;
                    eligible.Add(af);
                    eligibleMsg.Add(i);
                }
            }
            if (eligible.Count == 0) return pruned;

            int newestMsgWithEligible = eligibleMsg[eligibleMsg.Count - 1];
            bool pressured = contextLength > 0 && priorPromptTokens > 0
                             && priorPromptTokens >= (int)(contextLength * ContextPressureRatio);
            int imageBudget = pressured ? MaxNativeImagesPressured : MaxNativeImages;
            long byteBudget = pressured ? MaxNativeBytesPressured : MaxNativeBytes;

            // Walk newest-first; keep within budgets, demote the rest (the older items fall off).
            int images = 0;
            long bytes = 0;
            for (int k = eligible.Count - 1; k >= 0; k--)
            {
                var af = eligible[k];
                bool isImage = (af.EffectiveKind == AttachmentKind.Image);
                long len = (af.Data != null) ? af.Data.Length : 0;

                // The current turn's attachments are never pruned (count their weight, but always keep).
                if (eligibleMsg[k] == newestMsgWithEligible)
                {
                    bytes += len;
                    if (isImage) images++;
                    continue;
                }

                bool fits = (bytes + len) <= byteBudget && (!isImage || images < imageBudget);
                if (fits)
                {
                    bytes += len;
                    if (isImage) images++;
                }
                else
                {
                    pruned.Add(af);
                }
            }
            return pruned;
        }
    }
}
