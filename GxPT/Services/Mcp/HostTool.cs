using System;
using Newtonsoft.Json.Linq;

namespace GxPT
{
    // Signature for a host tool's local dispatch: answer the call, set isError, and return the text fed
    // back as the tool message content. Mirrors ExecuteCall's own out-param contract so a handler can be
    // lifted straight out of the dispatch switch.
    internal delegate string HostToolHandler(ToolCall call, out bool isError);

    // A host ("meta") tool: one the orchestrator answers locally WITHOUT an MCP round-trip and that is
    // exempt from reveal-before-call (reveal_tools, open_skill, read_skill_file, dispatch_agent, ask_user).
    //
    // Each instance is the SINGLE source of truth pairing the two halves that previously had to be kept in
    // sync by hand: the tool's exposed schema (Def, added to the request's tools array) and its local
    // dispatch (Handle, run in ExecuteCall). Because both halves come from one list built per turn from
    // current state (McpChatOrchestrator.BuildHostTools), a host tool is exposed IFF it is
    // dispatch-exempt - the "exposed but unknown on call" and "exempt but invisible" drift modes are gone.
    // (reveal_tools is the one nuance: it's dispatched via the table like the rest, but its Def is emitted
    // by the registry in lead position 0 of the tools array, so the exposure builder skips its table Def.)
    // XP / .NET 3.5 friendly.
    internal sealed class HostTool
    {
        public readonly string Name;            // server-unqualified function name the model calls
        public readonly Func<JObject> Def;      // the tool's schema, appended to the exposed tools array
        public readonly HostToolHandler Handle; // local dispatch, invoked from ExecuteCall

        public HostTool(string name, Func<JObject> def, HostToolHandler handle)
        {
            Name = name;
            Def = def;
            Handle = handle;
        }
    }
}
