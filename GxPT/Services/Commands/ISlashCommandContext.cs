namespace GxPT
{
    // The host surface a slash command may consult or drive. Kept free of WinForms types so the command
    // core compiles into the unit-test assembly; the app provides a MainForm-backed implementation and
    // tests provide a fake. Prompt commands use only WorkingDir/HasServer; client commands use the rest.
    //
    // The cohesive clusters that had grown this into a god-interface are split into dedicated facets
    // (issue #119): model selection (Models), MCP-server toggles (Servers), and skills enablement
    // (Skills). What remains here is genuinely cross-cutting host state plus one-off app actions.
    internal interface ISlashCommandContext
    {
        // The conversation's working folder (may be null/empty when none is set). Path arguments are
        // always resolved relative to this root.
        string WorkingDir { get; }

        // True when an MCP server's toolset is actually available right now (enabled, connected, and
        // workdir-bound). Backed by McpToolRegistry.HasServer.
        bool HasServer(string serverName);

        // Show a short status line in the active transcript (UI only; not saved to history).
        void WriteInfo(string text);

        // Model selection for the active tab (used by /model).
        IModelControl Models { get; }

        // Built-in toggleable MCP servers (used by /toggle-tool).
        IServerControl Servers { get; }

        // Per-conversation skills enablement (used by /toggle-skills and /toggle-skill).
        ISkillEnablementStore Skills { get; }

        // Per-conversation sub-agents enablement (used by /toggle-agents).
        IAgentEnablementStore Agents { get; }

        // ---- conversation / app actions ----
        void NewConversation();
        void ExportConversations();

        // Export one skill as a one-item .gxpl plugin (the host shows a save-file dialog).
        void ExportSkill(Skill skill);

        // Import an archive - conversations (.gxcv), a skill (.gxsk), or a plugin (.gxpl) - chosen via an
        // open-file dialog.
        void ImportArchive();

        // ---- plugin actions (used by /plugin) ----

        // Author a new .gxpl from the user's/project's skills and agents (host shows a checklist dialog).
        void ExportPlugin();

        // Install or upgrade a .gxpl chosen via an open-file dialog.
        void InstallPlugin();

        // Enable/disable/uninstall an installed plugin by its stored name. Enable/disable move the plugin's
        // skills and agents into or out of the active roots; uninstall removes them and the registry entry.
        void EnablePlugin(string name);
        void DisablePlugin(string name);
        void UninstallPlugin(string name);

        // Summarize the current conversation and open the summary as context in a new conversation tab.
        // Runs asynchronously; the original conversation is left untouched.
        void Compact();
    }
}
