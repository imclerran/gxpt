using System;
using System.Collections.Generic;
using GxPT;

namespace GxPT.Tests.Commands
{
    // ISlashCommandContext for tests. The HasServer/WorkingDir bits drive prompt-command gating tests;
    // the clustered facets (ModelStore/ServerStore/SkillStore) carry the recorded state and actions the
    // client-command tests assert against.
    internal sealed class FakeSlashCommandContext : ISlashCommandContext
    {
        private readonly HashSet<string> _servers =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public FakeSlashCommandContext(string workingDir, params string[] servers)
        {
            WorkingDir = workingDir;
            if (servers != null)
            {
                for (int i = 0; i < servers.Length; i++)
                    if (!string.IsNullOrEmpty(servers[i])) _servers.Add(servers[i]);
            }
        }

        public string WorkingDir { get; private set; }

        public bool HasServer(string serverName)
        {
            return !string.IsNullOrEmpty(serverName) && _servers.Contains(serverName);
        }

        // ---- clustered facets (state + recorded actions live on the stores) ----
        public readonly FakeModelControl ModelStore = new FakeModelControl();
        public readonly FakeServerControl ServerStore = new FakeServerControl();
        public readonly FakeSkillEnablementStore SkillStore = new FakeSkillEnablementStore();
        public readonly FakeAgentEnablementStore AgentStore = new FakeAgentEnablementStore();

        public IModelControl Models { get { return ModelStore; } }
        public IServerControl Servers { get { return ServerStore; } }
        public ISkillEnablementStore Skills { get { return SkillStore; } }
        public IAgentEnablementStore Agents { get { return AgentStore; } }

        // ---- top-level state / recorded actions ----
        public List<string> Infos = new List<string>();
        public int NewConversationCount;
        public int ExportCount;
        public int CompactCount;
        public List<Skill> ExportedSkills = new List<Skill>();
        public int ImportCount;

        public void WriteInfo(string text) { Infos.Add(text); }
        public void NewConversation() { NewConversationCount++; }
        public void ExportConversations() { ExportCount++; }
        public void ExportSkill(Skill skill) { ExportedSkills.Add(skill); }
        public void ImportArchive() { ImportCount++; }
        public void Compact() { CompactCount++; }
    }

    // Records the model the /model command selects and serves the configurable model list to completion.
    internal sealed class FakeModelControl : IModelControl
    {
        public List<string> Models = new List<string>();
        public string LastModelSet;

        public IList<string> GetModels() { return Models; }
        public string GetActiveModel() { return LastModelSet; }
        public void SetModel(string slug) { LastModelSet = slug; }
    }

    // Backs the /toggle-tool command: configurable server states, mutated in place by SetServerEnabled.
    internal sealed class FakeServerControl : IServerControl
    {
        public Dictionary<string, bool> ServerStates =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public IList<string> GetServerNames() { return new List<string>(ServerStates.Keys); }
        public bool GetServerEnabled(string serverName)
        {
            bool v;
            return ServerStates.TryGetValue(serverName, out v) && v;
        }
        public string SetServerEnabled(string serverName, bool enabled)
        {
            ServerStates[serverName] = enabled;
            return null;
        }
    }

    // Backs /toggle-skills and /toggle-skill: per-conversation override state plus the RefreshSkillsServer call count.
    internal sealed class FakeSkillEnablementStore : ISkillEnablementStore
    {
        public bool? ConvFeatureOff;
        public Dictionary<string, bool> ConvOverrides =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public int RefreshSkillsServerCount;

        public bool? GetConversationSkillsFeatureOff() { return ConvFeatureOff; }
        public void SetConversationSkillsFeatureOff(bool? value) { ConvFeatureOff = value; }
        public IDictionary<string, bool> GetConversationSkillOverrides()
        { return new Dictionary<string, bool>(ConvOverrides, StringComparer.OrdinalIgnoreCase); }
        public void SetConversationSkillOverride(string slug, bool? value)
        { if (value.HasValue) ConvOverrides[slug] = value.Value; else ConvOverrides.Remove(slug); }
        public void ResetConversationSkills() { ConvFeatureOff = null; ConvOverrides.Clear(); }
        public void RefreshSkillsServer() { RefreshSkillsServerCount++; }
    }

    // Backs /toggle-agents: the per-conversation agents-enabled override (null = inherit global).
    internal sealed class FakeAgentEnablementStore : IAgentEnablementStore
    {
        public bool? ConvAgentsEnabled;
        public bool? GetConversationAgentsEnabled() { return ConvAgentsEnabled; }
        public void SetConversationAgentsEnabled(bool? value) { ConvAgentsEnabled = value; }
    }
}
