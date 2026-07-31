using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxPT
{
    // Periodic doom-loop / cycle detection (design A18). An unattended agent can drain its whole
    // max_turns budget while stuck in a repeating cycle - edit -> test-fails -> revert -> edit ... -
    // that a naive "N identical calls in a row" check never catches, because the calls oscillate
    // rather than repeat verbatim. This detector keeps a short rolling window of recent
    // `name:normalized-args` tool-call signatures and reports a loop when the tail is `reps`
    // consecutive repetitions of a period-`p` block (p in 1..MaxPeriod; reps = 3 for p==1, else 2).
    // Ported from the OpenMonoAgent DoomLoopDetector shape (MaxPeriod=4, MaxHistory=12, arguments
    // JSON-normalized with sorted object keys). Pure logic - no UI, no model: the orchestrator feeds
    // each executed tool call in and, on a positive, wraps the turn up as content (never a throw), so
    // the main agent and every sub-agent are covered by the same valve.
    internal sealed class DoomLoopDetector
    {
        // Longest cycle period considered. A period-p cycle repeated `reps` times needs p*reps
        // signatures; the widest lookback is p=1 (3 reps) and p=4 (2 reps) => max 8, so a 12-entry
        // window carries ample headroom while staying cheap.
        internal const int MaxPeriod = 4;
        internal const int MaxHistory = 12;

        private readonly List<string> _sigs = new List<string>();

        // Record one executed tool call (in call order) and report whether its arrival closes a
        // repeating cycle. Call exactly once per call the model actually ran.
        public bool Record(string name, string argumentsJson)
        {
            return Record(name, argumentsJson, null);
        }

        // `scope` is the location the call ran in — the host current directory in effect after the
        // call (null at the workspace root). It is part of the signature because a legitimate
        // directory WALK repeats name+args verbatim ("cd ..", then "list .") while moving through
        // DIFFERENT directories; without the salt, walking up a tree reads as a period-1/2 cycle
        // (an observed false positive). A genuinely stuck loop repeats in place — or cycles back
        // through the same places, which repeats the scopes too — so true positives still match.
        public bool Record(string name, string argumentsJson, string scope)
        {
            _sigs.Add(Signature(name, argumentsJson) + "@" + (scope ?? string.Empty));
            // Bound the window so an old, unrelated prefix can never combine with the recent tail to
            // fake a cycle, and so memory stays O(1) across a long turn. Record adds exactly one
            // entry, so a single removal restores the bound.
            if (_sigs.Count > MaxHistory) _sigs.RemoveAt(0);
            return HasCycle();
        }

        // Drop the window. The orchestrator owns one detector per turn, but this keeps reuse safe.
        public void Clear() { _sigs.Clear(); }

        // The smallest qualifying period wins, so p=1 (the same call over and over) is reported as a
        // period-1 cycle rather than being mistaken for a wider one.
        private bool HasCycle()
        {
            int n = _sigs.Count;
            for (int p = 1; p <= MaxPeriod; p++)
            {
                int reps = (p == 1) ? 3 : 2;
                int need = p * reps;
                if (n < need) continue;
                if (IsPeriodic(p, need)) return true;
            }
            return false;
        }

        // Are the last `need` signatures periodic with period `p`? Equivalent to: every entry in the
        // window equals the one `p` positions before it (which, checked across `need` = p*reps
        // entries, proves `reps` back-to-back copies of the trailing p-block).
        private bool IsPeriodic(int p, int need)
        {
            int n = _sigs.Count;
            for (int k = n - need + p; k < n; k++)
                if (!string.Equals(_sigs[k], _sigs[k - p], StringComparison.Ordinal))
                    return false;
            return true;
        }

        // `name:normalized-args`. Arguments are parsed and re-serialized with object keys sorted, so
        // two semantically identical calls that differ only in key order or whitespace collapse to one
        // signature. Unparseable arguments fall back to the raw (trimmed) text.
        internal static string Signature(string name, string argumentsJson)
        {
            return (name ?? string.Empty) + ":" + NormalizeArgs(argumentsJson);
        }

        internal static string NormalizeArgs(string argumentsJson)
        {
            if (string.IsNullOrEmpty(argumentsJson)) return string.Empty;
            try
            {
                JToken parsed = JToken.Parse(argumentsJson);
                return Canonicalize(parsed).ToString(Formatting.None);
            }
            catch (JsonException)
            {
                return argumentsJson.Trim();
            }
        }

        // Recursively sort object property names so key order can't split a signature. Array order is
        // left intact - order is semantically meaningful in a JSON array.
        private static JToken Canonicalize(JToken token)
        {
            JObject obj = token as JObject;
            if (obj != null)
            {
                List<string> keys = new List<string>();
                foreach (JProperty prop in obj.Properties()) keys.Add(prop.Name);
                keys.Sort(StringComparer.Ordinal);
                JObject sorted = new JObject();
                foreach (string key in keys) sorted[key] = Canonicalize(obj[key]);
                return sorted;
            }
            JArray arr = token as JArray;
            if (arr != null)
            {
                JArray copy = new JArray();
                foreach (JToken item in arr) copy.Add(Canonicalize(item));
                return copy;
            }
            // A leaf value still belongs to the parsed tree; detach a copy so re-parenting it into the
            // freshly built container above can't fault on an already-owned token.
            return token.DeepClone();
        }
    }
}
