using System;
using System.Collections.Generic;
using System.Text;
using Mcp35.Core.Protocol;
using Mcp35.Server;
using Mcp35.Server.Process;
using Newtonsoft.Json.Linq;

namespace CommandMcpServer
{
    /// <summary>
    /// Registers one PowerShell tool per discovered host (Windows PowerShell → command__powershell,
    /// PowerShell Core → command__pwsh), mirroring how the MSBuild server surfaces one tool per
    /// discovered engine: the tool exists only when PowerShell is found and is simply absent — never
    /// advertised — on a system without it. The detected $PSVersionTable.PSVersion is woven into the
    /// tool description so the model can choose cmdlets and language features the installed version
    /// actually supports.
    ///
    /// Like the Command server's run tool, the script is opaque and runs already-approved (the host gate
    /// showed the user the exact script). It is handed to PowerShell via -EncodedCommand (base64 of the
    /// UTF-16LE script), so no quoting, newline, or special character in the model's script can be
    /// misparsed by the shell — the whole script crosses as one opaque token. See servers-spec §5.
    /// </summary>
    internal static class PowerShellTools
    {
        private const int DefaultTimeoutMs = 60000;
        private const int MaxTimeoutMs = 600000;
        private const int OutputCap = 100000; // chars per stream

        public static void Register(McpServer server, CommandConfig config)
        {
            ProcessRunner runner = new ProcessRunner(null);
            // Discovery diagnostics go to stderr (never stdout — that carries the JSON-RPC stream).
            IList<PowerShellInstall> installs = PowerShellDiscovery.Discover(new StdErrLogSink());

            foreach (PowerShellInstall install in installs)
            {
                PowerShellInstall ps = install; // capture a per-iteration copy for the closure
                server.AddTool(ps.ToolName, Description(ps), BuildSchema(ps),
                    ToolAnnotations.Destructive(),
                    delegate(ToolCallContext ctx) { return Run(config, runner, ps, ctx); });
            }
        }

        private static JObject BuildSchema(PowerShellInstall ps)
        {
            return SchemaBuilder.Object()
                .Str("command", true, "The " + ps.Label + " script to run. Provide it exactly as you would type it at a "
                    + ps.Label + " prompt; multiple lines and any quoting are fine.")
                .Int("timeout_ms", false, "Kill the script after this many milliseconds (default 60000, max 600000).")
                .Build();
        }

        private static string Description(PowerShellInstall ps)
        {
            string d = "Run a " + ps.Label + " script and capture its stdout, stderr, and exit code. ";
            if (!string.IsNullOrEmpty(ps.Version))
                d += ps.Label + " version " + ps.Version + " is installed - use cmdlets and language "
                   + "features supported by that version. ";
            else
                d += "(The installed version could not be determined.) ";
            d += "The script runs in the conversation's working directory (the project folder), so it "
               + "operates on that folder directly - do NOT cd into it; relative paths resolve against it.";
            return d;
        }

        private static CallToolResult Run(CommandConfig config, ProcessRunner runner, PowerShellInstall ps, ToolCallContext ctx)
        {
            string command = ctx.Arguments.Value<string>("command");
            if (string.IsNullOrEmpty(command)) return ToolResults.Error("command is required");

            int timeout = IntArg(ctx, "timeout_ms", DefaultTimeoutMs, 1, MaxTimeoutMs);

            ProcessRequest req = new ProcessRequest();
            req.FileName = ps.Exe;
            // -EncodedCommand takes base64 of the UTF-16LE script, sidestepping shell quoting entirely
            // (the host already showed the user the exact script at the approval gate). -NoProfile keeps
            // a user's $PROFILE from changing behavior; -NonInteractive fails fast rather than hanging on
            // a prompt; -ExecutionPolicy Bypass lets the inline script run regardless of machine policy.
            req.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Encode(command);
            req.WorkingDirectory = config.WorkDir;
            req.TimeoutMs = timeout;

            ProcessResult result;
            try
            {
                result = runner.Run(req);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return ToolResults.Error(ps.Label + " could not be launched (" + ps.Exe + ").");
            }
            catch (Exception ex)
            {
                return ToolResults.Error("failed to run " + ps.Label + ": " + ex.Message);
            }

            bool outTrunc, errTrunc;
            JObject outp = new JObject();
            if (!string.IsNullOrEmpty(ps.Version)) outp["version"] = ps.Version;
            outp["exitCode"] = result.ExitCode;
            outp["stdout"] = Cap(result.StdOut, out outTrunc);
            outp["stderr"] = Cap(result.StdErr, out errTrunc);
            outp["timedOut"] = result.TimedOut;
            if (outTrunc || errTrunc) outp["truncated"] = true;
            return ToolResults.Json(outp);
        }

        // PowerShell's -EncodedCommand expects base64 of the UTF-16LE (little-endian) bytes of the
        // script. Pure (no I/O) so it is unit-testable without the MCP runtime or an installed shell.
        internal static string Encode(string command)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(command ?? string.Empty);
            return Convert.ToBase64String(bytes);
        }

        private static int IntArg(ToolCallContext ctx, string name, int fallback, int min, int max)
        {
            JToken t = ctx.Arguments[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            int n;
            try { n = t.Value<int>(); }
            catch { return fallback; }
            if (n < min) return min;
            if (n > max) return max;
            return n;
        }

        private static string Cap(string s, out bool truncated)
        {
            truncated = false;
            if (s == null) return string.Empty;
            if (s.Length <= OutputCap) return s;
            truncated = true;
            return s.Substring(0, OutputCap);
        }
    }
}
