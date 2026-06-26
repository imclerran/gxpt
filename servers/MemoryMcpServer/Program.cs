using Mcp35.Core.Protocol;
using Mcp35.Server;

namespace MemoryMcpServer
{
    /// <summary>
    /// First-party Memory MCP server: persistent, project-scoped memory under GXPT_WORKDIR/.gxpt/memory.
    /// The same five lines as every first-party server (servers-spec sec.1), around the Memory tool set.
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            StdErrLogSink log = new StdErrLogSink();

            Implementation info = new Implementation();
            info.Name = "memory";
            info.Version = "1.0";

            McpServer server = new McpServer(info, log);
            MemoryTools.Register(server, MemoryConfig.FromEnvironment(log));
            server.Run(); // blocks until stdin EOF
            return 0;
        }
    }
}
