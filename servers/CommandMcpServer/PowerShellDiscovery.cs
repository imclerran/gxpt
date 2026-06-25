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
        public string ToolName;   // "powershell", "powershell_v1", or "pwsh"
        public string Label;      // "Windows PowerShell" / "PowerShell"
        public string Exe;        // full path to powershell.exe / pwsh.exe
        public string Version;    // "5.1.19041.4046", "1.0", "7.4.1", or null when unknown

        // PowerShell 1.0 predates -EncodedCommand/-ExecutionPolicy/-NonInteractive, so its tool feeds
        // the script over stdin (`-Command -`) instead. False for every 2.0+ / Core host.
        public bool LegacyStdin;
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

            string version;
            bool legacy;
            DetectVersion(exe, log, out version, out legacy);

            // 1.0 gets its own tool (stdin invocation); 2.0-5.1 keep the standard -EncodedCommand tool.
            return new PowerShellInstall
            {
                ToolName = legacy ? "powershell_v1" : "powershell",
                Label = "Windows PowerShell",
                Exe = exe,
                Version = version,
                LegacyStdin = legacy
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

                string version;
                bool legacy;
                DetectVersion(exe, log, out version, out legacy);
                return new PowerShellInstall
                {
                    ToolName = "pwsh",
                    Label = "PowerShell",
                    Exe = exe,
                    Version = version,
                    LegacyStdin = legacy   // always false for Core (6+), which supports -EncodedCommand
                };
            }
            return null;
        }

        // Detect the interpreter's version and which invocation style its tool must use:
        //   * 2.0+ : $PSVersionTable.PSVersion gives the exact version; the tool uses -EncodedCommand.
        //   * 1.0  : $PSVersionTable doesn't exist and -EncodedCommand/-ExecutionPolicy/-NonInteractive
        //            aren't supported, so we confirm the version via (Get-Host).Version fed over stdin
        //            and flag the host for the stdin (`-Command -`) invocation style.
        // Both probes are short, time-boxed, and never throw — a probe failure just leaves version null
        // (modern style), so a wedged or unexpected interpreter can't stall or crash startup discovery.
        private static void DetectVersion(string exe, ILogSink log, out string version, out bool legacyStdin)
        {
            version = null;
            legacyStdin = false;

            string modern = RunProbe(exe,
                "-NoProfile -NonInteractive -Command \"$PSVersionTable.PSVersion.ToString()\"", null, log);
            if (!string.IsNullOrEmpty(modern)) { version = modern; return; }

            // No $PSVersionTable → likely 1.0. (Get-Host).Version works there; feed it over stdin via
            // `-Command -` so no switch or quoting a 1.0 host might reject is involved. Only a 1.x answer
            // flips to the legacy style; anything else is treated as a normal (modern) host.
            string legacy = RunProbe(exe, "-NoProfile -Command -",
                "(Get-Host).Version.ToString()" + Environment.NewLine, log);
            if (!string.IsNullOrEmpty(legacy))
            {
                version = legacy;
                legacyStdin = legacy.StartsWith("1.", StringComparison.Ordinal);
            }
        }

        private static string RunProbe(string exe, string args, string stdin, ILogSink log)
        {
            try
            {
                ProcessRequest req = new ProcessRequest();
                req.FileName = exe;
                req.Arguments = args;
                req.StdinText = stdin;
                req.TimeoutMs = 15000;

                ProcessResult res = new ProcessRunner(null).Run(req);
                if (res.TimedOut || res.ExitCode != 0) return null;
                string v = (res.StdOut ?? string.Empty).Trim();
                return string.IsNullOrEmpty(v) ? null : v;
            }
            catch (Exception ex)
            {
                Note(log, "version probe failed for " + exe + ": " + ex.Message);
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
