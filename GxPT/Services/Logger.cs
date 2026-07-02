using System;
using System.IO;
using System.Text;
using System.Threading;

namespace GxPT
{
    internal static class Logger
    {
        private static readonly object _gate = new object();
        private static string _logPath;
        private static bool _initialized;

        private static void EnsureInit()
        {
            if (_initialized) return;
            try
            {
                string dir = AppSettings.SettingsDirectory;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                _logPath = Path.Combine(dir, "gxpt.log");
                _initialized = true;
            }
            catch { }
        }

        private static bool IsEnabled()
        {
            try
            {
                string env = Environment.GetEnvironmentVariable("GXPT_LOG");
                if (!string.IsNullOrEmpty(env))
                {
                    env = env.Trim().ToLowerInvariant();
                    if (env == "1" || env == "true" || env == "yes" || env == "on") return true;
                }
                string s = AppSettings.GetString("enable_logging");
                if (string.IsNullOrEmpty(s)) return false;
                s = s.Trim().ToLowerInvariant();
                return s == "1" || s == "true" || s == "yes" || s == "on";
            }
            catch { return false; }
        }

        // For callers that build expensive log messages: lets them skip the work entirely
        // when logging is off.
        public static bool Enabled
        {
            get { return IsEnabled(); }
        }

        public static void Log(string category, string message)
        {
            if (!IsEnabled()) return;
            EnsureInit();
            try
            {
                lock (_gate)
                {
                    try { RotateIfTooLarge(); }
                    catch { }
                    using (var sw = new StreamWriter(_logPath, true, Encoding.UTF8))
                    {
                        string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                        int tid = Thread.CurrentThread.ManagedThreadId;
                        sw.WriteLine("[{0}] [T{1}] [{2}] {3}", ts, tid, category ?? "?", message ?? string.Empty);
                    }
                }
            }
            catch { }
        }

        private static void RotateIfTooLarge()
        {
            if (string.IsNullOrEmpty(_logPath)) return;
            try
            {
                var fi = new FileInfo(_logPath);
                if (fi.Exists && fi.Length > (2 * 1024 * 1024)) // 2 MB
                {
                    string bak = _logPath + ".old";
                    try { if (File.Exists(bak)) File.Delete(bak); }
                    catch { }
                    try { File.Move(_logPath, bak); }
                    catch { }
                }
            }
            catch { }
        }
    }
}
