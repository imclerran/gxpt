using System;
using System.Collections.Generic;

namespace GxPT
{
    // A point-in-time view of the available MCP tools: the server-qualified function names, plus the tier
    // classifier (ToolApprovalPolicy.TierOf - the same classification dispatch uses). Built by the host
    // (MainForm) from the live registry, consumed by RequiredToolsDetect (export) and RequiredToolsCheck
    // (install/Details). WinForms-free.
    internal sealed class ToolSnapshot
    {
        public IList<string> Names;
        public Func<string, ToolTier> TierOf;

        public ToolSnapshot()
        {
            Names = new List<string>();
            TierOf = delegate(string n) { return ToolTier.Write; }; // cautious default if unset
        }
    }
}
