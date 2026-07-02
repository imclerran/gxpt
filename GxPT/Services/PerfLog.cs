using System;
using System.Diagnostics;
using System.Text;

namespace GxPT
{
    // TEMPORARY diagnostic. Appends per-stage timings for the light/dark theme toggle to the
    // main log (%AppData%\GxPT\gxpt.log, category [Perf]) so a non-locally-reproducible
    // slowdown can be localized to the exact stage that grows across repeated toggles. Writes
    // even when enable_logging is off. Safe to silence (Enabled=false) or delete entirely once
    // the theme-toggle performance work is closed out - nothing depends on it.
    internal static class PerfLog
    {
        // Flip to false to silence without removing the call sites.
        public static bool Enabled = true;

        private static readonly object _lock = new object();
        private static int _toggleSeq;

        // Marks the start of one logical toggle; returns a session that stage timings attach to.
        public static Session Begin(string label)
        {
            int seq;
            lock (_lock) { seq = ++_toggleSeq; }
            return new Session(label, seq);
        }

        internal sealed class Session
        {
            private readonly string _label;
            private readonly int _seq;
            private readonly Stopwatch _total;
            private readonly StringBuilder _line;

            internal Session(string label, int seq)
            {
                _label = label;
                _seq = seq;
                _total = Stopwatch.StartNew();
                _line = new StringBuilder();
            }

            // Times a single stage and records its elapsed milliseconds.
            public void Stage(string name, Action work)
            {
                if (!Enabled) { if (work != null) work(); return; }
                Stopwatch sw = Stopwatch.StartNew();
                try { if (work != null) work(); }
                finally
                {
                    sw.Stop();
                    _line.Append(' ').Append(name).Append('=').Append(sw.ElapsedMilliseconds).Append("ms");
                }
            }

            public void End()
            {
                if (!Enabled) return;
                _total.Stop();
                Write("[" + _seq.ToString("D3") + "] " + _label + " TOTAL=" + _total.ElapsedMilliseconds + "ms" + _line.ToString());
            }
        }

        private static void Write(string line)
        {
            try { Logger.LogAlways("Perf", line); }
            catch { }
        }
    }
}
