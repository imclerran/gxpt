using System.Collections.Generic;

namespace GxPT
{
    // Built-in toggleable MCP servers, split out of ISlashCommandContext (issue #119) so /tool depends
    // only on this surface rather than the whole host facade. (Availability gating - HasServer - stays on
    // ISlashCommandContext, since every command is gated on it.)
    internal interface IServerControl
    {
        IList<string> GetServerNames();           // names that can be toggled
        bool GetServerEnabled(string serverName); // effective current state
        // Apply a new enabled state. Returns null on success, or a message explaining why it didn't
        // change (e.g. the tool isn't installed).
        string SetServerEnabled(string serverName, bool enabled);
    }
}
