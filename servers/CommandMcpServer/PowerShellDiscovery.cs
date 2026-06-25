using System;
using System.Collections.Generic;
using System.IO;
using Mcp35.Core.Diagnostics;
using Mcp35.Server.Process;

namespace CommandMcpServer
{
    /// <summary>
    /// One discovered PowerShell host. <see cref="Exe"/> is the interpreter; <see cref="Version"/> is
    /// the detected $PSVersionTable.PSVersion (or null when it could not be queried). Windows PowerShell
    /// (the .NET Framework "Desktop" edition, powershell.exe) and PowerShell 6+ Core (pwsh.exe) are
    /// distinct hosts with different cmdlet sets and language levels, so each is surfaced as its own tool.
    /// </summary>
    internal sealed class PowerShellInstall
    {
        public string ToolName;   // "powershell" or "pwsh"
        public string Label;      // "Windows PowerShell" / "PowerShell"
        public string Exe;        // full path to powershell.exe / pwsh.exe
        public string Version;    // "5.1.19041.4046", "7.4.1", or null when unknown
    }

    /// <summary>
    /// Probes the system for installed PowerShell hosts. Mirrors MsBuildDiscovery's posture: every probe
    /// is defensive (a failure for one source yields no entry for it, never an exception — discovery must
    /// not crash startup), it runs once at startup, and the resulting tool set is static for the life of
    /// the process. When nothing is found the Command server simply registers no PowerShell tool, so it
    /// is never advertised on a system without PowerShell.
    /// </summary>
    internal static class PowerShellDiscovery
    {
        public static IList<PowerShellInstall> Discover(ILogSink log)
        {
            List<PowerShellInstall> result = new List<PowerShellInstall>();

            try { PowerShellInstall ps = DiscoverWindowsPowerShell(log); if (ps != null) result.Add(ps); }
            catch (Exception ex) { Note(log, "windows powershell probe failed: " + ex.Message); }

            try { PowerShellInstall core = DiscoverPowerShellCore(log); if (core != null) result.Add(core); }
            catch (Exception ex) { Note(log, "powershell core probe failed: " + ex.Message); }

            return result;
        }

        // Windows PowerShell (Desktop): %WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe. Present
        // on every Windows from 7 onward (and on XP/Vista once the WMF add-on is installed). The "v1.0"
        // folder name is historical — the engine version it ships is whatever the OS/WMF provides, which
        // is exactly why we query it rather than assume.
        private static PowerShellInstall DiscoverWindowsPowerShell(ILogSink log)
        {
            string windir = Environment.GetEnvironmentVariable("WINDIR");
            if (string.IsNullOrEmpty(windir)) windir = Environment.GetEnvironmentVariable("SystemRoot");
            if (string.IsNullOrEmpty(windir)) return null;

            string exe = Path.Combine(windir, Path.Combine("System32", Path.Combine("WindowsPowerShell", Path.Combine("v1.0", "powershell.exe"))));
            if (!Exists(exe)) return null;

            return new PowerShellInstall
            {
                ToolName = "powershell",
                Label = "Windows PowerShell",
                Exe = exe,
                Version = QueryVersion(exe, log)
            };
        }

        // PowerShell 6+ (Core, cross-platform): pwsh.exe, installed out-of-band under
        // %ProgramFiles%\PowerShell\<major>\pwsh.exe (prefer 7, then 6). Optional — absent on most
        // XP/7 boxes, present alongside Windows PowerShell on newer ones.
        private static PowerShellInstall DiscoverPowerShellCore(ILogSink log)
        {
            string pf = Environment.GetEnvironmentVariable("ProgramFiles");
            if (string.IsNullOrEmpty(pf)) return null;

            string[] majors = new string[] { "7", "6" };
            foreach (string major in majors)
            {
                string exe = Path.Combine(pf, Path.Combine("PowerShell", Path.Combine(major, "pwsh.exe")));
                if (!Exists(exe)) continue;
                return new PowerShellInstall
                {
                    ToolName = "pwsh",
                    Label = "PowerShell",
                    Exe = exe,
                    Version = QueryVersion(exe, log)
                };
            }
            return null;
        }

        // Ask the interpreter its own version. $PSVersionTable exists from PowerShell 2.0 onward; on the
        // (vanishingly rare) 1.0 the command yields nothing and Version stays null. Kept short with a
        // tight timeout so a wedged interpreter can't stall startup discovery.
        private static string QueryVersion(string exe, ILogSink log)
        {
            try
            {
                ProcessRequest req = new ProcessRequest();
                req.FileName = exe;
                req.Arguments = "-NoProfile -NonInteractive -Command \"$PSVersionTable.PSVersion.ToString()\"";
                req.TimeoutMs = 15000;

                ProcessResult res = new ProcessRunner(null).Run(req);
                if (res.TimedOut || res.ExitCode != 0) return null;
                string v = (res.StdOut ?? string.Empty).Trim();
                return string.IsNullOrEmpty(v) ? null : v;
            }
            catch (Exception ex)
            {
                Note(log, "version query failed for " + exe + ": " + ex.Message);
                return null;
            }
        }

        private static bool Exists(string path)
        {
            try { return !string.IsNullOrEmpty(path) && File.Exists(path); }
            catch { return false; }
        }

        private static void Note(ILogSink log, string msg)
        {
            if (log != null) log.Log("command", msg);
        }
    }
}
