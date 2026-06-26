// GraphvizRenderer.cs
// Background renderer that turns Graphviz "dot" source into a PNG bitmap by shelling out to a
// bundled, portable Graphviz 2.38 (Lib\graphviz\dot.exe with a minimal config6 + GDI+ plugin).
// Mirrors SyntaxHighlightingRenderer's async cache/worker model: the UI thread enqueues a render
// and draws a placeholder until the worker finishes, then GraphReady fires and the transcript
// re-measures so the image takes its natural size.
// Target: .NET 3.5, Windows XP compatible.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;

namespace GxPT
{
    public static class GraphvizRenderer
    {
        // The outcome of a render. Image is non-null only on success; Failed is true when the source
        // could not be rendered (bad dot, dot.exe missing, timeout). A failed result causes the
        // transcript to fall back to drawing the fenced block as ordinary highlighted code.
        public struct GraphResult
        {
            public Bitmap Image;
            public bool Failed;
        }

        // How long a single dot.exe invocation may run before it is killed (guards against a
        // pathological graph or a hung process). Generous; real graphs render in well under a second.
        private const int RenderTimeoutMs = 20000;

        private static readonly Dictionary<string, GraphResult> _cache = new Dictionary<string, GraphResult>();
        private static readonly Dictionary<string, string> _pending = new Dictionary<string, string>(); // key -> dot source
        private static readonly List<string> _lifo = new List<string>(); // act as a stack (pop from end)
        private static readonly object _lock = new object();
        private static Thread _worker;

        // Resolved once: false means dot.exe isn't present, so every render fails fast (no spawn churn).
        private static int _dotPathState; // 0 = unknown, 1 = present, 2 = absent
        private static string _dotPath;

        // Raised on a worker thread when a key finishes; the UI marshals to the UI thread, then
        // re-measures and repaints (unlike highlight, the rendered size is not known until completion).
        public static event Action<string> GraphReady;

        /// <summary>FNV-1a 64-bit hash for a stable cache key (matches SyntaxHighlightingRenderer).</summary>
        private static string Hash64(string s)
        {
            unchecked
            {
                const ulong offset = 14695981039346656037UL;
                const ulong prime = 1099511628211UL;
                ulong h = offset;
                if (s != null)
                {
                    for (int i = 0; i < s.Length; i++) { h ^= (byte)s[i]; h *= prime; }
                }
                return h.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        private static string MakeKey(string dot)
        {
            return Hash64(dot ?? string.Empty);
        }

        /// <summary>
        /// Returns the cached render for the given dot source, if one exists yet. A false return means
        /// the work is still pending (or not yet enqueued) - callers should draw a placeholder.
        /// </summary>
        public static bool TryGetResult(string dot, out GraphResult result)
        {
            string key = MakeKey(dot);
            lock (_lock)
            {
                return _cache.TryGetValue(key, out result);
            }
        }

        /// <summary>
        /// Queue a dot source for background rendering. Cheap and idempotent: a source already cached
        /// or in-flight is ignored. LIFO ordering means the most recently requested graphs render first.
        /// </summary>
        public static void EnqueueRender(string dot)
        {
            if (string.IsNullOrEmpty(dot)) return;
            string key = MakeKey(dot);
            lock (_lock)
            {
                if (_cache.ContainsKey(key) || _pending.ContainsKey(key)) return;
                _pending[key] = dot;
                _lifo.Add(key);
                EnsureWorker();
            }
        }

        private static void EnsureWorker()
        {
            if (_worker != null && _worker.IsAlive) return;
            _worker = new Thread(WorkerLoop);
            try { _worker.IsBackground = true; }
            catch { }
            try { _worker.Priority = ThreadPriority.BelowNormal; }
            catch { }
            _worker.Start();
        }

        private static void WorkerLoop()
        {
            while (true)
            {
                string key, dot;
                lock (_lock)
                {
                    if (_lifo.Count == 0)
                    {
                        _worker = null;
                        return;
                    }
                    int last = _lifo.Count - 1;
                    key = _lifo[last];
                    _lifo.RemoveAt(last);
                    dot = _pending[key];
                    _pending.Remove(key);
                }

                GraphResult result;
                try { result = RenderDot(dot); }
                catch { result = new GraphResult(); result.Failed = true; }

                lock (_lock)
                {
                    _cache[key] = result;
                }

                Action<string> ev = GraphReady;
                if (ev != null)
                {
                    try { ev(key); }
                    catch { }
                }

                try { Thread.Sleep(1); }
                catch { }
            }
        }

        // Resolve the bundled dot.exe path once. Returns null when Graphviz isn't deployed.
        private static string ResolveDotPath()
        {
            if (_dotPathState == 1) return _dotPath;
            if (_dotPathState == 2) return null;

            string path = null;
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                path = Path.Combine(baseDir, "Lib\\graphviz\\dot.exe");
            }
            catch { path = null; }

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                _dotPath = path;
                _dotPathState = 1;
                return path;
            }
            _dotPathState = 2;
            return null;
        }

        // Invoke dot.exe with the dot source on stdin and capture the PNG it writes to stdout.
        // No temp files. stdin is written on a helper thread so a large PNG filling the stdout pipe
        // can't deadlock against us still writing stdin.
        private static GraphResult RenderDot(string dot)
        {
            var result = new GraphResult();

            string dotPath = ResolveDotPath();
            if (string.IsNullOrEmpty(dotPath)) { result.Failed = true; return result; }

            var psi = new ProcessStartInfo
            {
                FileName = dotPath,
                Arguments = "-Tpng",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            try { psi.WorkingDirectory = Path.GetDirectoryName(dotPath); }
            catch { }

            Process p = null;
            try
            {
                p = Process.Start(psi);
                if (p == null) { result.Failed = true; return result; }

                Process proc = p; // captured for the helper threads

                // Feed the dot source to stdin off-thread, then close it so dot can finish.
                byte[] inBytes = new UTF8Encoding(false).GetBytes(dot ?? string.Empty);
                Thread stdinThread = new Thread(delegate()
                {
                    try
                    {
                        Stream s = proc.StandardInput.BaseStream;
                        s.Write(inBytes, 0, inBytes.Length);
                        s.Flush();
                        proc.StandardInput.Close();
                    }
                    catch { }
                });
                stdinThread.IsBackground = true;
                stdinThread.Start();

                // Drain stderr off-thread so a verbose run can't fill the error pipe and deadlock the
                // stdout read below (which would block waiting for output the stalled child won't emit).
                Thread stderrThread = new Thread(delegate()
                {
                    try { proc.StandardError.ReadToEnd(); }
                    catch { }
                });
                stderrThread.IsBackground = true;
                stderrThread.Start();

                // Watchdog: kill a runaway process so the stdout read below can't block forever.
                Thread watchdog = new Thread(delegate()
                {
                    try
                    {
                        if (!proc.WaitForExit(RenderTimeoutMs))
                        {
                            try { proc.Kill(); }
                            catch { }
                        }
                    }
                    catch { }
                });
                watchdog.IsBackground = true;
                watchdog.Start();

                // Read the binary PNG from stdout until EOF (dot closes the pipe when it exits / is killed).
                byte[] png;
                using (var ms = new MemoryStream())
                {
                    Stream outStream = p.StandardOutput.BaseStream;
                    byte[] buf = new byte[16384];
                    int n;
                    while ((n = outStream.Read(buf, 0, buf.Length)) > 0)
                        ms.Write(buf, 0, n);
                    png = ms.ToArray();
                }

                try { p.WaitForExit(); }
                catch { }

                int exit = -1;
                try { exit = p.ExitCode; }
                catch { }

                if (exit != 0 || png == null || png.Length == 0)
                {
                    result.Failed = true;
                    return result;
                }

                // Materialize into a standalone Bitmap so it doesn't depend on the source stream's
                // lifetime (GDI+ would otherwise lazy-read the stream). Same pattern as FileViewerForm.
                using (var ms2 = new MemoryStream(png))
                using (var tmp = Image.FromStream(ms2))
                {
                    result.Image = new Bitmap(tmp);
                }
                return result;
            }
            catch
            {
                result.Failed = true;
                return result;
            }
            finally
            {
                if (p != null)
                {
                    try { p.Dispose(); }
                    catch { }
                }
            }
        }
    }
}
