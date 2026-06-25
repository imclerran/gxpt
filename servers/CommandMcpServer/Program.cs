using Mcp35.Core.Protocol;
using Mcp35.Server;

namespace CommandMcpServer
{
    /// <summary>
    /// First-party Command MCP server: run an already-approved shell command line, plus — when a
    /// PowerShell host is discovered on the system — a PowerShell tool per host (servers-spec §1, §5).
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            StdErrLogSink log = new StdErrLogSink();

            Implementation info = new Implementation();
            info.Name = "command";
            info.Version = "1.0";

            CommandConfig config = CommandConfig.FromEnvironment(log);
            McpServer server = new McpServer(info, log);
            CommandTools.Register(server, config);
            // PowerShell tool(s) are added only when a host is discovered on this system; on a box
            // without PowerShell none are registered, so the server advertises just `run`.
            PowerShellTools.Register(server, config);
            server.Run(); // blocks until stdin EOF
            return 0;
        }
    }
}
