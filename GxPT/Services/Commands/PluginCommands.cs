using System;
using System.Collections.Generic;
using System.Text;

namespace GxPT
{
    // /plugin <verb> [name] -- manage .gxpl plugins (bundles of skills + agents tracked as one unit):
    //   /plugin list                  -- show installed plugins: version, member counts, enabled state
    //   /plugin export                -- author a new .gxpl from your skills/agents (checklist dialog)
    //   /plugin install               -- install or upgrade a .gxpl via an open-file dialog
    //   /plugin enable <name>         -- restore a disabled plugin's skills/agents
    //   /plugin disable <name>        -- move a plugin's skills/agents aside (out of the active roots)
    //   /plugin uninstall <name>      -- remove a plugin's skills/agents and its registry entry
    // The read-only verb (list) and name completion read the plugin registry directly; the mutating verbs go
    // through the host (ISlashCommandContext) so dialogs/MessageBoxes parent correctly and the skills server
    // refreshes afterward. Membership is tracked in the registry, never in skill/agent frontmatter.
    internal sealed class PluginCommand : ClientCommandBase, IArgumentCompleter
    {
        private static readonly string[] Verbs =
            new string[] { "list", "export", "install", "enable", "disable", "uninstall" };

        public override string Name { get { return "plugin"; } }
        public override string Description { get { return "Manage skill/agent plugins"; } }
        public override string ArgumentHint { get { return "<verb> [name]"; } }

        public override SlashCommandResult Invoke(string args, ISlashCommandContext ctx)
        {
            string[] tok = SkillCommandShared.Tokens(args);
            string verb = tok.Length > 0 ? tok[0].ToLowerInvariant() : "list";

            switch (verb)
            {
                case "list":
                    return ListPlugins(ctx);
                case "export":
                    ctx.ExportPlugin();
                    return SlashCommandResult.Handled();
                case "install":
                case "import":
                    ctx.InstallPlugin();
                    return SlashCommandResult.Handled();
                case "enable":
                case "disable":
                case "uninstall":
                case "remove":
                    return MutateByName(verb, tok, ctx);
                default:
                    return SlashCommandResult.Fail(
                        "Usage: /plugin <list|export|install|enable|disable|uninstall> [name]");
            }
        }

        private static SlashCommandResult MutateByName(string verb, string[] tok, ISlashCommandContext ctx)
        {
            if (tok.Length < 2)
                return SlashCommandResult.Fail("Usage: /plugin " + verb + " <name>");

            string resolved;
            if (!ResolveName(tok[1], out resolved))
                return SlashCommandResult.Fail("Unknown plugin: " + tok[1]);

            if (verb == "enable") ctx.EnablePlugin(resolved);
            else if (verb == "disable") ctx.DisablePlugin(resolved);
            else ctx.UninstallPlugin(resolved); // uninstall / remove
            return SlashCommandResult.Handled();
        }

        private static SlashCommandResult ListPlugins(ISlashCommandContext ctx)
        {
            IList<PluginManifest> plugins = Installed();
            if (plugins.Count == 0)
            {
                ctx.WriteInfo("No plugins installed.");
                return SlashCommandResult.Handled();
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("Installed plugins:");
            for (int i = 0; i < plugins.Count; i++)
            {
                PluginManifest m = plugins[i];
                sb.Append('\n').Append("  ").Append(SlugOf(m));
                if (!string.IsNullOrEmpty(m.Version)) sb.Append(" v").Append(m.Version);
                if (!m.Enabled) sb.Append(" (disabled)");
                sb.Append(" - ").Append(m.Skills.Count).Append(" skill(s), ")
                  .Append(m.Agents.Count).Append(" agent(s)");
            }
            ctx.WriteInfo(sb.ToString());
            return SlashCommandResult.Handled();
        }

        // ---- completion ----

        public IList<ArgCompletion> CompleteArgument(string argText, ISlashCommandContext ctx)
        {
            List<ArgCompletion> result = new List<ArgCompletion>();
            string a = argText ?? string.Empty;
            int sp = a.IndexOf(' ');

            if (sp < 0)
            {
                // Completing the verb.
                for (int i = 0; i < Verbs.Length; i++)
                {
                    if (a.Length > 0 && !Verbs[i].StartsWith(a, StringComparison.OrdinalIgnoreCase)) continue;
                    bool takesName = TakesName(Verbs[i]);
                    result.Add(new ArgCompletion(Verbs[i], Verbs[i] + (takesName ? " " : ""), takesName));
                }
                return result;
            }

            // Completing a plugin name for enable/disable/uninstall.
            string verb = a.Substring(0, sp).Trim().ToLowerInvariant();
            if (!TakesName(verb)) return result;
            string partial = a.Substring(sp + 1).Trim();

            IList<PluginManifest> plugins = Installed();
            for (int i = 0; i < plugins.Count; i++)
            {
                // enable offers only disabled plugins; disable offers only enabled ones.
                if (verb == "enable" && plugins[i].Enabled) continue;
                if (verb == "disable" && !plugins[i].Enabled) continue;

                string slug = SlugOf(plugins[i]);
                if (partial.Length > 0 && !SlashMatch.HyphenPrefix(slug, partial)) continue;
                result.Add(new ArgCompletion(slug + (plugins[i].Enabled ? "" : " (disabled)"),
                    verb + " " + slug, false));
            }
            return result;
        }

        // ---- shared ----

        private static bool TakesName(string verb)
        {
            return verb == "enable" || verb == "disable" || verb == "uninstall" || verb == "remove";
        }

        private static string SlugOf(PluginManifest m)
        {
            return SkillSlug.Make(m.Name) ?? m.Name;
        }

        private static IList<PluginManifest> Installed()
        {
            return new PluginRegistry(PluginRoots.UserRoot()).ListInstalled();
        }

        // Resolve a typed token (slug or display name) to an installed plugin's stored Name.
        private static bool ResolveName(string typed, out string name)
        {
            name = null;
            if (string.IsNullOrEmpty(typed)) return false;
            string wanted = SkillSlug.Make(typed) ?? typed;
            IList<PluginManifest> plugins = Installed();
            for (int i = 0; i < plugins.Count; i++)
            {
                if (string.Equals(SlugOf(plugins[i]), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    name = plugins[i].Name;
                    return true;
                }
            }
            return false;
        }
    }
}
