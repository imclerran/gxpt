using Mcp35.Core.Protocol;
using Mcp35.Server;

namespace ExtensionsMcpServer
{
    /// <summary>
    /// First-party Extensions MCP server: authors the user's local extensions - skill files and agent
    /// files - and runs a skill's bundled .bat. The same five-line shape as every first-party server
    /// (servers-spec sec.1), around the skill + agent authoring tool set plus run_skill_script.
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            StdErrLogSink log = new StdErrLogSink();

            Implementation info = new Implementation();
            info.Name = "extensions";
            info.Version = "1.0";

            McpServer server = new McpServer(info, log);
            ExtensionsServerTools.Register(server, ExtensionsConfig.FromEnvironment(log));
            server.Run(); // blocks until stdin EOF
            return 0;
        }
    }
}
