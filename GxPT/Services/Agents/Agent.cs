using System;

namespace GxPT
{
    // Where a discovered agent came from, in increasing specificity. A more specific source shadows a
    // less specific one of the same slug: project (this workspace) shadows user (this machine) shadows
    // bundled (shipped) - design A11/A4, mirroring SkillSource.
    internal enum AgentSource
    {
        Bundled,
        User,
        Project
    }

    // The tier ceiling an agent's tools may reach (design A5/sec.3). Caps the allowlist regardless of what
    // `tools` names. Parsed here; mapped onto the host ToolClassifier tiers in a later phase
    // (AgentToolResolver). Default Write - an agent can edit, but the approval gate still confirms;
    // `destructive` must be opted into explicitly.
    internal enum AgentMaxTier
    {
        ReadOnly,
        Write,
        Destructive
    }

    // How much an agent may do unattended within the approval gate (design sec.8 layer 3). Default Gated -
    // behaves like the main agent (every tier prompts per the normal rules). AutoReadOnly lets the
    // agent's ReadOnly-tier calls auto-allow for the duration of a dispatch, once granted.
    internal enum AgentAutonomy
    {
        Gated,
        AutoReadOnly
    }

    // One discovered sub-agent: a flat <slug>.md file whose frontmatter declares the agent's contract
    // and whose body is the agent's system prompt (design A4 - one file per agent, no folder). The
    // catalog holds a slug -> Agent map. The body is read from FilePath on dispatch (a single small
    // file, read fresh so edits take effect), not stored here - mirroring how skills read SKILL.md on
    // open. XP / .NET 3.5 friendly.
    internal sealed class Agent
    {
        // Kebab-case handle: appears in the manifest, taken by dispatch_agent, typed in the slash command.
        public string Slug { get; private set; }

        // Frontmatter "name" (human label); falls back to the slug when the author omits it.
        public string Name { get; private set; }

        // Frontmatter "description": the single manifest line the model reads to decide whether/which to
        // delegate ("use this agent when ...").
        public string Description { get; private set; }

        // The frontmatter tool allowlist - server-qualified names or glob patterns (files__*, mcp__*, *).
        // Null when the author omitted `tools:` entirely (resolved to a conservative ReadOnly default
        // later, design A5). An explicit `tools: []` is a zero-length array, distinct from null.
        public string[] Tools { get; private set; }

        public AgentMaxTier MaxTier { get; private set; }
        public AgentAutonomy Autonomy { get; private set; }

        // Optional model id override; null/empty => the parent turn's model.
        public string Model { get; private set; }

        // Absolute path to the agent's <slug>.md. The body (= system prompt) is read from here at dispatch.
        public string FilePath { get; private set; }

        public AgentSource Source { get; private set; }

        public Agent(string slug, string name, string description, string[] tools,
                     AgentMaxTier maxTier, AgentAutonomy autonomy, string model,
                     string filePath, AgentSource source)
        {
            Slug = slug;
            Name = name;
            Description = description;
            Tools = tools;
            MaxTier = maxTier;
            Autonomy = autonomy;
            Model = model;
            FilePath = filePath;
            Source = source;
        }
    }
}
