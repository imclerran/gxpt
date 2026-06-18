using System.Collections.Generic;

namespace GxPT
{
    internal enum McpTransportKind
    {
        Stdio,
        Http
    }

    // A host-neutral description of one configurable MCP server (built-in Tier-1 or mcp.json Tier-2).
    // McpHost translates a spec into the actual transport (StdioTransport / HttpTransport) at launch;
    // keeping config parsing free of Mcp35.Client makes it pure and unit-testable.
    internal sealed class McpServerSpec
    {
        public string Name;
        public McpTransportKind Kind;
        public bool Enabled;

        // True for built-in servers sandboxed to a working directory (files/git/command): the host
        // injects GXPT_WORKDIR and launches them lazily, per conversation. web/HTTP servers are
        // workdir-independent and may be launched once.
        public bool WorkdirScoped;

        // For a WorkdirScoped server, also run ONE workdir-independent instance (no GXPT_WORKDIR), so its
        // workdir-less features work in a folderless conversation. The skills server uses this: the
        // workdir-less instance handles user-global authoring (and advertises a reduced tool set), while
        // the per-workdir instances handle project authoring + run_skill_script. Ignored when not scoped.
        public bool RunsWithoutWorkdir;

        // For a WorkdirScoped server, eligible to run in a per-conversation SCRATCH working directory
        // when the user has no workspace set (the command server uses this). A scratch dir launches
        // ONLY the scoped specs flagged here — files/git/msbuild require a real workspace and stay off.
        // Ignored when not WorkdirScoped. The host decides whether scratch dirs are used at all (opt-in
        // setting); this flag only says "this server is allowed there".
        public bool RunsInScratch;

        // True for the four bundled first-party servers (hardcoded launch, never in mcp.json, D15).
        public bool BuiltIn;

        // stdio
        public string Command;
        public string[] Args;
        public Dictionary<string, string> Env;

        // http
        public string Url;
        public Dictionary<string, string> Headers;

        public McpServerSpec()
        {
            Args = new string[0];
            Env = new Dictionary<string, string>();
            Headers = new Dictionary<string, string>();
        }
    }
}
