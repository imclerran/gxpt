using System;
using Mcp35.Core.Protocol;
using Mcp35.Server;
using Mcp35.Server.Process;
using Newtonsoft.Json.Linq;

namespace CommandMcpServer
{
    /// <summary>
    /// The Command server's single tool, run (→ command__run, Destructive, argument-scoped). It
    /// executes an already-approved command line via the shell and reports the outcome verbatim.
    /// The server has no allowlist of its own — containment is the host gate + working-dir +
    /// timeout, NOT string filtering. It treats the command as opaque and never sanitizes it
    /// (sanitizing would give a false sense of safety). See servers-spec §5.
    /// </summary>
    internal static class CommandTools
    {
        private const int DefaultTimeoutMs = 60000;
        private const int MaxTimeoutMs = 600000;

        public static void Register(McpServer server, CommandConfig config)
        {
            ProcessRunner runner = new ProcessRunner(null);

            server.AddTool("run",
                "Run a command line on Windows via cmd.exe (/c) and capture its stdout, stderr, and exit code. " +
                "The command already runs in the conversation's working directory (the project folder), so it " +
                "operates on that folder directly - do NOT cd into it; relative paths resolve against it.",
                SchemaBuilder.Object()
                    .Str("command", true, "The exact command line to run, in Windows cmd.exe syntax.")
                    .Int("timeout_ms", false, "Kill the command after this many milliseconds (default 60000)")
                    .Build(),
                ToolAnnotations.Destructive(),
                delegate(ToolCallContext ctx) { return Run(config, runner, ctx); });
        }

        private static CallToolResult Run(CommandConfig config, ProcessRunner runner, ToolCallContext ctx)
        {
            string command = ctx.Arguments.Value<string>("command");
            if (string.IsNullOrEmpty(command)) return ToolResults.Error("command is required");

            int timeout = CommandToolHelpers.IntArg(ctx.Arguments, "timeout_ms", DefaultTimeoutMs, 1, MaxTimeoutMs);

            ProcessRequest req = new ProcessRequest();
            req.FileName = config.Shell;
            // Hand the command to the shell as written; the shell (not this server) parses it.
            // The host already showed the user this exact string at the approval gate.
            //
            // Wrap as: cmd.exe /s /c "<command>". The /S flag plus a single outer quote pair makes
            // cmd strip exactly those outer quotes and pass everything between them through verbatim.
            // Without it, bare `cmd /c <command>` hits cmd's legacy quoting rule: when the line
            // starts with a quote and carries more than one quoted token (e.g. a quoted program path
            // AND a quoted argument), cmd strips the FIRST and LAST quote of the whole line,
            // corrupting it (the "quote stripping" failure on commands like
            // `"C:\Program Files\...\ssh-keygen.exe" -f "C:\path" -N ""`).
            req.Arguments = "/s /c \"" + command + "\"";
            req.WorkingDirectory = config.WorkDir;
            req.TimeoutMs = timeout;

            ProcessResult result;
            try
            {
                result = runner.Run(req);
            }
            catch (Exception ex)
            {
                return ToolResults.Error("failed to run command: " + ex.Message);
            }

            return ToolResults.Json(CommandToolHelpers.BuildResult(result, null));
        }
    }
}
