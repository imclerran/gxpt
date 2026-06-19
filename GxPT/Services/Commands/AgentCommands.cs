using System;
using System.Collections.Generic;

namespace GxPT
{
    // Slash commands for the sub-agents feature (design sec.6), in the scope-first hyphen-prefixed style
    // PRs #156/#157 set for skills. The whole feature is a single on/off toggle (no per-agent management,
    // A15), so the surface is just two commands:
    //   /toggle-agents <here|global> <on|off|inherit>   -- enable/disable the feature at that scope
    //   /dispatch-agent <slug> <task>                    -- explicitly delegate a task to an agent
    // Reuses the shared scope/verb/completion helpers (SkillCommandShared); the conversation override goes
    // through ISlashCommandContext.Agents, and global-scope changes go to settings.json via AppSettings.
    internal static class AgentCommands
    {
        public static IList<ISlashCommand> BuiltIns()
        {
            List<ISlashCommand> list = new List<ISlashCommand>();
            list.Add(new ToggleAgentsCommand());
            list.Add(new DispatchAgentCommand());
            return list;
        }
    }

    // /toggle-agents <here|global> <on|off|inherit>   (scope first; global takes only on|off)
    internal sealed class ToggleAgentsCommand : ClientCommandBase, IArgumentCompleter
    {
        public override string Name { get { return "toggle-agents"; } }
        public override string Description { get { return "Turn the sub-agents feature on/off"; } }
        public override string ArgumentHint { get { return "<here|global> <on|off|inherit>"; } }

        public override SlashCommandResult Invoke(string args, ISlashCommandContext ctx)
        {
            string[] tok = SkillCommandShared.Tokens(args);
            if (tok.Length != 2) // scope + verb, both required
                return SlashCommandResult.Fail("Usage: /toggle-agents <here|global> <on|off|inherit>");

            bool isGlobal;
            if (!SkillCommandShared.TryScope(tok[0], out isGlobal))
                return SlashCommandResult.Fail("Unknown scope '" + tok[0] + "'. Use 'here' or 'global'.");

            bool? onoff;
            bool isInherit;
            if (!SkillCommandShared.TryParseVerb(tok[1], out onoff, out isInherit))
                return SlashCommandResult.Fail("Usage: /toggle-agents <here|global> <on|off|inherit>");

            string info;
            if (isInherit)
            {
                // The global default is the most upstream layer (a plain bool in settings.json), so it has
                // nothing to inherit from -- only on/off.
                if (isGlobal)
                    return SlashCommandResult.Fail("The global agents default is always on or off; use 'on' or 'off'.");
                ctx.Agents.SetConversationAgentsEnabled(null);
                info = "Agents: this conversation now follows the global default.";
            }
            else
            {
                bool on = onoff.Value;
                if (isGlobal) AppSettings.SetBool(AgentEnablement.GlobalSettingKey, on);
                else ctx.Agents.SetConversationAgentsEnabled(on ? (bool?)true : (bool?)false);
                info = "Agents turned " + (on ? "on" : "off") + " "
                    + (isGlobal ? "globally" : "for this conversation") + ".";
            }

            ctx.WriteInfo(info);
            return SlashCommandResult.Handled();
        }

        public IList<ArgCompletion> CompleteArgument(string argText, ISlashCommandContext ctx)
        {
            List<ArgCompletion> result = new List<ArgCompletion>();
            string a = argText ?? string.Empty;
            int sp = a.IndexOf(' ');
            if (sp < 0)
            {
                // First token: the scope, which then narrows the verb choices.
                SkillCommandShared.AddMatching(result, new string[] { "here", "global" }, a, "", true);
            }
            else
            {
                string scopeTok = a.Substring(0, sp);
                string rest = a.Substring(sp + 1).TrimStart();
                bool isGlobal;
                if (!SkillCommandShared.TryScope(scopeTok, out isGlobal)) return result; // unknown scope
                SkillCommandShared.AddMatching(result, SkillCommandShared.VerbsForScope(isGlobal, true),
                    rest, scopeTok + " ", false);
            }
            return result;
        }
    }

    // /dispatch-agent <slug> <task>   (explicit delegation; sends an instruction so the model issues a real
    // dispatch_agent tool call - which requires the feature to be enabled so the tool is exposed).
    internal sealed class DispatchAgentCommand : ClientCommandBase, IArgumentCompleter
    {
        public override string Name { get { return "dispatch-agent"; } }
        public override string Description { get { return "Delegate a task to a sub-agent"; } }
        public override string ArgumentHint { get { return "<slug> <task>"; } }

        public override SlashCommandResult Invoke(string args, ISlashCommandContext ctx)
        {
            string a = (args ?? string.Empty).Trim();
            if (a.Length == 0) return SlashCommandResult.Fail("Usage: /dispatch-agent <slug> <task>");

            string slugArg, task;
            int sp = a.IndexOf(' ');
            if (sp < 0) { slugArg = a; task = string.Empty; }
            else { slugArg = a.Substring(0, sp); task = a.Substring(sp + 1).Trim(); }
            if (task.Length == 0) return SlashCommandResult.Fail("Usage: /dispatch-agent <slug> <task>");

            AgentCatalog cat = AgentRoots.BuildCatalog(AppDomain.CurrentDomain.BaseDirectory, ctx.WorkingDir);
            Agent agent;
            if (!cat.TryGet(slugArg, out agent))
                return SlashCommandResult.Fail("Unknown agent: " + slugArg);

            // A normal user instruction so the model issues a real dispatch_agent call. Works only while the
            // feature is enabled (the host exposes dispatch_agent then); otherwise the model has no tool.
            string msg = "Dispatch the " + agent.Slug + " agent with this task: " + task;
            return SlashCommandResult.Send(msg);
        }

        public IList<ArgCompletion> CompleteArgument(string argText, ISlashCommandContext ctx)
        {
            List<ArgCompletion> result = new List<ArgCompletion>();
            string a = argText ?? string.Empty;
            if (a.IndexOf(' ') >= 0) return result; // only complete the first token (the slug)

            AgentCatalog cat = AgentRoots.BuildCatalog(AppDomain.CurrentDomain.BaseDirectory, ctx.WorkingDir);
            IList<Agent> agents = cat.Agents;
            for (int i = 0; i < agents.Count; i++)
            {
                string slug = agents[i].Slug;
                if (a.Length > 0 && !SlashMatch.HyphenPrefix(slug, a)) continue;
                result.Add(new ArgCompletion(slug + " - " + agents[i].Description, slug + " ", false));
            }
            return result;
        }
    }
}
