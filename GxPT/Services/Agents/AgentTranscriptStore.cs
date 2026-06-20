using System;
using System.Collections.Generic;

namespace GxPT
{
    // A small, session-scoped cache of child-agent transcripts (design sec.14, tier 3). A dispatch_agent
    // record in the chat is keyed by a per-call id; after the fan-out the host stores that call's per-slot
    // transcripts here under the same key, so the record's "View transcript" links can later look them up
    // and open the read-only viewer. In-memory only (not persisted): transcripts survive for the session,
    // so the link works until the app closes, then the record degrades to "(transcript unavailable)" - the
    // design's "retained for the session" contract. Bounded by MaxKeys with FIFO eviction so a long session
    // of fan-outs can't grow unbounded. Thread-safe (the dispatcher runs on worker threads; the UI reads on
    // the UI thread).
    internal static class AgentTranscriptStore
    {
        // How many dispatch records' transcript sets to retain at once. Oldest evicted first.
        private const int MaxKeys = 64;

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, AgentTranscript[]> _map =
            new Dictionary<string, AgentTranscript[]>(StringComparer.Ordinal);
        private static readonly Queue<string> _order = new Queue<string>();

        // Stores (or replaces) the per-slot transcripts for one dispatch record key.
        public static void Put(string key, AgentTranscript[] transcripts)
        {
            if (string.IsNullOrEmpty(key) || transcripts == null) return;
            lock (_lock)
            {
                if (!_map.ContainsKey(key)) _order.Enqueue(key);
                _map[key] = transcripts;
                // Evict oldest keys past the cap (skip the key just written, if it bubbles up).
                while (_order.Count > MaxKeys)
                {
                    string old = _order.Dequeue();
                    if (!string.Equals(old, key, StringComparison.Ordinal)) _map.Remove(old);
                }
            }
        }

        // The transcript for slot `index` under `key`, or null if the key is unknown (e.g. evicted, or a
        // record reloaded from history after restart), the index is out of range, or that slot ran no child.
        public static AgentTranscript Get(string key, int index)
        {
            if (string.IsNullOrEmpty(key) || index < 0) return null;
            lock (_lock)
            {
                AgentTranscript[] arr;
                if (!_map.TryGetValue(key, out arr) || arr == null || index >= arr.Length) return null;
                return arr[index];
            }
        }
    }
}
