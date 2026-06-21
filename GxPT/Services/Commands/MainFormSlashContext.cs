using System.Collections.Generic;

namespace GxPT
{
    // ISlashCommandContext implementation backed by MainForm. It forwards to MainForm's internal Slash*
    // helpers (which read live state -- active tab, model combo, MCP registry/host), so commands never
    // hold stale references and stay decoupled from MainForm itself. The clustered facets (Models,
    // Servers, Skills) are thin adapters over the same helpers.
    internal sealed class MainFormSlashContext : ISlashCommandContext
    {
        private readonly MainForm _form;
        private readonly IModelControl _models;
        private readonly IServerControl _servers;
        private readonly ISkillEnablementStore _skills;
        private readonly IAgentEnablementStore _agents;

        public MainFormSlashContext(MainForm form)
        {
            _form = form;
            _models = new MainFormModelControl(form);
            _servers = new MainFormServerControl(form);
            _skills = new MainFormSkillStore(form);
            _agents = new MainFormAgentStore(form);
        }

        public string WorkingDir { get { return _form.SlashWorkingDir(); } }
        public bool HasServer(string serverName) { return _form.SlashHasServer(serverName); }
        public void WriteInfo(string text) { _form.SlashWriteInfo(text); }

        public IModelControl Models { get { return _models; } }
        public IServerControl Servers { get { return _servers; } }
        public ISkillEnablementStore Skills { get { return _skills; } }
        public IAgentEnablementStore Agents { get { return _agents; } }

        public void NewConversation() { _form.SlashNewConversation(); }
        public void ExportConversations() { _form.SlashExportConversations(); }
        public void ExportSkill(Skill skill) { _form.SlashExportSkill(skill); }
        public void ImportArchive() { _form.SlashImportArchive(); }
        public void Compact() { _form.SlashCompact(); }
    }

    internal sealed class MainFormModelControl : IModelControl
    {
        private readonly MainForm _form;
        public MainFormModelControl(MainForm form) { _form = form; }

        public IList<string> GetModels() { return _form.SlashGetModels(); }
        public string GetActiveModel() { return _form.SlashGetActiveModel(); }
        public void SetModel(string slug) { _form.SlashSetModel(slug); }
    }

    internal sealed class MainFormServerControl : IServerControl
    {
        private readonly MainForm _form;
        public MainFormServerControl(MainForm form) { _form = form; }

        public IList<string> GetServerNames() { return _form.SlashGetServerNames(); }
        public bool GetServerEnabled(string serverName) { return _form.SlashGetServerEnabled(serverName); }
        public string SetServerEnabled(string serverName, bool enabled) { return _form.SlashSetServerEnabled(serverName, enabled); }
    }

    internal sealed class MainFormSkillStore : ISkillEnablementStore
    {
        private readonly MainForm _form;
        public MainFormSkillStore(MainForm form) { _form = form; }

        public bool? GetConversationSkillsFeatureOff() { return _form.SlashGetConversationSkillsFeatureOff(); }
        public void SetConversationSkillsFeatureOff(bool? value) { _form.SlashSetConversationSkillsFeatureOff(value); }
        public IDictionary<string, bool> GetConversationSkillOverrides() { return _form.SlashGetConversationSkillOverrides(); }
        public void SetConversationSkillOverride(string slug, bool? value) { _form.SlashSetConversationSkillOverride(slug, value); }
        public void ResetConversationSkills() { _form.SlashResetConversationSkills(); }
        public void RefreshSkillsServer() { _form.SlashRefreshSkillsServer(); }
    }

    internal sealed class MainFormAgentStore : IAgentEnablementStore
    {
        private readonly MainForm _form;
        public MainFormAgentStore(MainForm form) { _form = form; }

        public bool? GetConversationAgentsEnabled() { return _form.SlashGetConversationAgentsEnabled(); }
        public void SetConversationAgentsEnabled(bool? value) { _form.SlashSetConversationAgentsEnabled(value); }
    }
}
