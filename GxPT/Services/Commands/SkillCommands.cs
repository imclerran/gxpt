using System;
using System.Collections.Generic;
using System.Text;

namespace GxPT
{
    // Slash commands for the skills feature (design sec.6), built on the existing ISlashCommand framework:
    //   /list-skills                                    -- list skills and their effective on/off state
    //   /toggle-skills [on|off|reset] [here|global]     -- toggle/reset the whole feature (bare = flip here)
    //   /toggle-skill <slug> [on|off|reset] [here|global] -- toggle/reset one skill (bare slug toggles)
    //   /use-skill <slug> [text]                        -- use a skill (body attached as hidden context)
    // Management commands are Client (local, no LLM send); /use-skill sends a short "Use the X skill" ask
    // and attaches the skill body as a hidden system message (it never enters the transcript). Scope
    // defaults to "here" (this conversation); "global" edits skills.json. Conversation overrides are
    // read/written through ISlashCommandContext; the global default through SkillEnablement directly.
    internal static class SkillCommandShared
    {
        public static IList<ISlashCommand> BuiltIns()
        {
            List<ISlashCommand> list = new List<ISlashCommand>();
            list.Add(new ListSkillsCommand());
            list.Add(new ToggleSkillsCommand());
            list.Add(new ToggleSkillCommand());
            list.Add(new UseSkillCommand());
            return list;
        }

        // ---- shared helpers ----

        internal static SkillCatalog BuildCatalog(ISlashCommandContext ctx)
        {
            string workdir = ctx != null ? ctx.WorkingDir : null;
            return SkillRoots.BuildCatalog(AppDomain.CurrentDomain.BaseDirectory, workdir);
        }

        // Splits raw args on whitespace into non-empty tokens.
        internal static string[] Tokens(string args)
        {
            if (string.IsNullOrEmpty(args)) return new string[0];
            return args.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        }

        // true = on, false = off, null = not an on/off word.
        internal static bool? ParseOnOff(string token)
        {
            return SlashArgs.ParseOnOff(token);
        }

        // Recognizes the trailing scope word. Returns false for anything that isn't here/global.
        internal static bool TryScope(string token, out bool isGlobal)
        {
            isGlobal = false;
            if (string.IsNullOrEmpty(token)) return false;
            string t = token.Trim().ToLowerInvariant();
            if (t == "global") { isGlobal = true; return true; }
            if (t == "here") { isGlobal = false; return true; }
            return false;
        }

        // Resolve a user-typed slug (exact, else kebab-normalized) against the catalog.
        internal static bool ResolveSkill(SkillCatalog cat, string typed, out Skill skill)
        {
            skill = null;
            if (cat == null || string.IsNullOrEmpty(typed)) return false;
            if (cat.TryGet(typed, out skill)) return true;
            string norm = SkillSlug.Make(typed);
            return !string.IsNullOrEmpty(norm) && cat.TryGet(norm, out skill);
        }

        internal static bool? ConvOverrideFor(ISlashCommandContext ctx, string slug)
        {
            IDictionary<string, bool> ov = ctx.Skills.GetConversationSkillOverrides();
            bool v;
            if (ov != null && slug != null && ov.TryGetValue(slug, out v)) return v;
            return null;
        }

        // Adds choices that match the partial token. InsertArg = prefix + choice, plus a trailing space
        // when there is a further level (cont) so accepting it advances the popup to that next level
        // immediately (matching name-mode / the /toggle-tool completer) instead of waiting for a typed
        // space.
        internal static void AddMatching(List<ArgCompletion> into, string[] choices, string partial,
            string prefix, bool cont)
        {
            for (int i = 0; i < choices.Length; i++)
            {
                if (partial.Length > 0 && !choices[i].StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                    continue;
                into.Add(new ArgCompletion(choices[i], prefix + choices[i] + (cont ? " " : ""), cont));
            }
        }
    }

    // /list-skills -- show every skill with its effective on/off state and the rule that decided it.
    internal sealed class ListSkillsCommand : ClientCommandBase
    {
        public override string Name { get { return "list-skills"; } }
        public override string Description { get { return "List skills and their on/off state"; } }

        public override SlashCommandResult Invoke(string args, ISlashCommandContext ctx)
        {
            SkillCatalog cat = SkillCommandShared.BuildCatalog(ctx);
            SkillEnablement global = SkillEnablement.LoadGlobal();
            ctx.WriteInfo(BuildList(cat, global, ctx));
            return SlashCommandResult.Handled();
        }

        private static string BuildList(SkillCatalog cat, SkillEnablement global, ISlashCommandContext ctx)
        {
            bool? convFeatureOff = ctx.Skills.GetConversationSkillsFeatureOff();
            IDictionary<string, bool> convOv = ctx.Skills.GetConversationSkillOverrides();

            StringBuilder sb = new StringBuilder();
            sb.Append("Skills \u2014 most specific setting wins.");
            // The feature toggle (rungs 3-4) = the default for any skill with no per-skill setting. The
            // "here" half only shows when this conversation has set it (otherwise it inherits global).
            sb.Append("\nDefault: ").Append(global.FeatureOff ? "OFF" : "ON").Append(" globally");
            if (convFeatureOff.HasValue)
                sb.Append(" \u00b7 ").Append(convFeatureOff.Value ? "OFF" : "ON").Append(" here");

            IList<Skill> skills = cat.Skills;
            if (skills == null || skills.Count == 0)
            {
                sb.Append("\nNo skills found.");
                return sb.ToString();
            }

            int width = 0;
            for (int i = 0; i < skills.Count; i++)
                if (skills[i].Slug != null && skills[i].Slug.Length > width) width = skills[i].Slug.Length;

            sb.Append("\n"); // blank line before the list
            for (int i = 0; i < skills.Count; i++)
            {
                Skill s = skills[i];
                bool v;
                bool? ov = (convOv != null && convOv.TryGetValue(s.Slug, out v)) ? (bool?)v : null;
                SkillRule rule;
                bool enabled = SkillResolve.Resolve(global, s.Slug, ov, convFeatureOff, out rule);
                sb.Append("\n- ").Append((s.Slug != null ? s.Slug : "").PadRight(width)).Append("  ")
                  .Append(enabled ? "ON " : "OFF").Append("  (").Append(ReasonText(rule, enabled)).Append(")");
            }
            return sb.ToString();
        }

        // Human-readable form of the rule that decided a skill's state (for the /list-skills list).
        private static string ReasonText(SkillRule rule, bool enabled)
        {
            string st = enabled ? "on" : "off";
            switch (rule)
            {
                case SkillRule.SkillHere: return st + " here";
                case SkillRule.SkillGlobal: return st + " globally";
                case SkillRule.FeatureHere: return "all skills " + st + " here";
                case SkillRule.FeatureGlobal: return "all skills " + st;
                default: return "default";
            }
        }
    }

    // /toggle-skills [on|off|reset] [here|global]   (bare "/toggle-skills" flips the feature for this
    // conversation)
    internal sealed class ToggleSkillsCommand : ClientCommandBase, IArgumentCompleter
    {
        public override string Name { get { return "toggle-skills"; } }
        public override string Description { get { return "Turn the skills feature on/off"; } }
        public override string ArgumentHint { get { return "[on|off|reset] [here|global]"; } }

        public override SlashCommandResult Invoke(string args, ISlashCommandContext ctx)
        {
            string[] tok = SkillCommandShared.Tokens(args);
            SkillEnablement global = SkillEnablement.LoadGlobal();

            // Bare "/toggle-skills": flip the effective feature state for this conversation.
            if (tok.Length == 0)
            {
                bool? convOff = ctx.Skills.GetConversationSkillsFeatureOff();
                bool currentlyOff = convOff.HasValue ? convOff.Value : global.FeatureOff;
                bool newOn = currentlyOff; // off -> on, on -> off
                ctx.Skills.SetConversationSkillsFeatureOff(newOn ? (bool?)false : (bool?)true);
                ctx.Skills.RefreshSkillsServer();
                ctx.WriteInfo("Skills turned " + (newOn ? "on" : "off") + " for this conversation.");
                return SlashCommandResult.Handled();
            }
            if (tok.Length > 2) // verb + optional scope; reject trailing junk rather than silently ignore
                return SlashCommandResult.Fail("Usage: /toggle-skills [on|off|reset] [here|global]");

            bool isGlobal = false;
            if (tok.Length >= 2 && !SkillCommandShared.TryScope(tok[1], out isGlobal))
                return SlashCommandResult.Fail("Unknown scope '" + tok[1] + "'. Use 'here' or 'global'.");

            string verb = tok[0].ToLowerInvariant();
            string info;
            if (verb == "reset")
            {
                if (isGlobal)
                {
                    global.FeatureOff = false;
                    global.ClearSkillOverrides();
                    global.SaveGlobal();
                    info = "Skills: global defaults reset (feature on, no per-skill settings).";
                }
                else
                {
                    ctx.Skills.ResetConversationSkills();
                    info = "Skills: cleared this conversation's overrides.";
                }
            }
            else
            {
                bool? onoff = SkillCommandShared.ParseOnOff(verb);
                if (!onoff.HasValue)
                    return SlashCommandResult.Fail("Usage: /toggle-skills [on|off|reset] [here|global]");

                bool on = onoff.Value;
                if (isGlobal) { global.FeatureOff = !on; global.SaveGlobal(); }
                else { ctx.Skills.SetConversationSkillsFeatureOff(on ? (bool?)false : (bool?)true); }
                info = "Skills turned " + (on ? "on" : "off") + " " + (isGlobal ? "globally" : "for this conversation") + ".";
            }

            ctx.Skills.RefreshSkillsServer(); // once, after a successful mutation - the server follows enablement
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
                // Offer "run with no arguments" (flip here) as the default entry, so the bare command is
                // selectable from the popup; then the verb choices.
                if (a.Length == 0)
                    result.Add(new ArgCompletion("(toggle skills for this conversation)", "", false));
                SkillCommandShared.AddMatching(result, new string[] { "on", "off", "reset" }, a, "", true);
            }
            else
            {
                string first = a.Substring(0, sp);
                string rest = a.Substring(sp + 1).TrimStart();
                SkillCommandShared.AddMatching(result, new string[] { "here", "global" }, rest, first + " ", false);
            }
            return result;
        }
    }

    // /toggle-skill <slug> [on|off|reset] [here|global]   (bare "/toggle-skill <slug>" toggles for this
    // conversation)
    internal sealed class ToggleSkillCommand : ClientCommandBase, IArgumentCompleter
    {
        public override string Name { get { return "toggle-skill"; } }
        public override string Description { get { return "Enable or disable one skill"; } }
        public override string ArgumentHint { get { return "<slug> [on|off|reset] [here|global]"; } }

        public override SlashCommandResult Invoke(string args, ISlashCommandContext ctx)
        {
            string[] tok = SkillCommandShared.Tokens(args);
            if (tok.Length == 0 || tok.Length > 3) // slug + optional verb + optional scope; no trailing junk
                return SlashCommandResult.Fail("Usage: /toggle-skill <slug> [on|off|reset] [here|global]");

            SkillCatalog cat = SkillCommandShared.BuildCatalog(ctx);
            Skill skill;
            if (!SkillCommandShared.ResolveSkill(cat, tok[0], out skill))
                return SlashCommandResult.Fail("Unknown skill: " + tok[0]);
            string slug = skill.Slug;
            SkillEnablement global = SkillEnablement.LoadGlobal();

            string info;

            // Bare "/toggle-skill <slug>": toggle the effective state for this conversation.
            if (tok.Length == 1)
            {
                bool current = SkillResolve.IsEnabled(global, slug,
                    SkillCommandShared.ConvOverrideFor(ctx, slug), ctx.Skills.GetConversationSkillsFeatureOff());
                ctx.Skills.SetConversationSkillOverride(slug, !current);
                info = "Skill '" + slug + "' " + (!current ? "enabled" : "disabled") + " for this conversation.";
            }
            else
            {
                bool isGlobal = false;
                if (tok.Length >= 3 && !SkillCommandShared.TryScope(tok[2], out isGlobal))
                    return SlashCommandResult.Fail("Unknown scope '" + tok[2] + "'. Use 'here' or 'global'.");

                string verb = tok[1].ToLowerInvariant();
                if (verb == "reset")
                {
                    if (isGlobal) { global.SetSkillOverride(slug, null); global.SaveGlobal(); info = "Skill '" + slug + "': global setting cleared."; }
                    else { ctx.Skills.SetConversationSkillOverride(slug, null); info = "Skill '" + slug + "': conversation override cleared."; }
                }
                else
                {
                    bool? onoff = SkillCommandShared.ParseOnOff(verb);
                    if (!onoff.HasValue)
                        return SlashCommandResult.Fail("Usage: /toggle-skill <slug> [on|off|reset] [here|global]");

                    bool on = onoff.Value;
                    if (isGlobal) { global.SetSkillOverride(slug, on); global.SaveGlobal(); }
                    else { ctx.Skills.SetConversationSkillOverride(slug, on); }
                    info = "Skill '" + slug + "' " + (on ? "enabled" : "disabled") + " " + (isGlobal ? "globally" : "for this conversation") + ".";
                }
            }

            ctx.Skills.RefreshSkillsServer(); // once, after a successful mutation - the server follows enablement
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
                // First token: skill slugs, annotated with their effective state.
                SkillCatalog cat = SkillCommandShared.BuildCatalog(ctx);
                SkillEnablement global = SkillEnablement.LoadGlobal();
                bool? convFeatureOff = ctx.Skills.GetConversationSkillsFeatureOff();
                IList<Skill> skills = cat.Skills;
                for (int i = 0; i < skills.Count; i++)
                {
                    string slug = skills[i].Slug;
                    if (a.Length > 0 && !SlashMatch.HyphenPrefix(slug, a)) continue;
                    bool enabled = SkillResolve.IsEnabled(global, slug, SkillCommandShared.ConvOverrideFor(ctx, slug), convFeatureOff);
                    result.Add(new ArgCompletion(slug + "  (" + (enabled ? "on" : "off") + ")", slug + " ", true));
                }
            }
            else
            {
                int sp2 = a.IndexOf(' ', sp + 1);
                if (sp2 < 0)
                {
                    string slug = a.Substring(0, sp);
                    string rest = a.Substring(sp + 1).TrimStart();
                    SkillCommandShared.AddMatching(result, new string[] { "on", "off", "reset" }, rest, slug + " ", true);
                }
                else
                {
                    string head = a.Substring(0, sp2);
                    string rest = a.Substring(sp2 + 1).TrimStart();
                    SkillCommandShared.AddMatching(result, new string[] { "here", "global" }, rest, head + " ", false);
                }
            }
            return result;
        }
    }

    // /use-skill <slug> [text] -- invoke a skill explicitly, regardless of its enabled state. Sends a
    // short user message ("Use the <slug> skill. [text]") and attaches the skill's full instructions as a
    // HIDDEN system message (context the model sees but the transcript never shows) - so the body never
    // clutters the user transcript. Custom behavior, not a generic prompt expansion.
    internal sealed class UseSkillCommand : ClientCommandBase, IArgumentCompleter
    {
        public override string Name { get { return "use-skill"; } }
        public override string Description { get { return "Use a skill (loads it as context)"; } }
        public override string ArgumentHint { get { return "<slug> [message]"; } }

        public override SlashCommandResult Invoke(string args, ISlashCommandContext ctx)
        {
            string a = (args ?? string.Empty).Trim();
            if (a.Length == 0) return SlashCommandResult.Fail("Usage: /use-skill <slug> [message]");

            string slugArg, rest;
            int sp = a.IndexOf(' ');
            if (sp < 0) { slugArg = a; rest = string.Empty; }
            else { slugArg = a.Substring(0, sp); rest = a.Substring(sp + 1).Trim(); }

            SkillCatalog cat = SkillCommandShared.BuildCatalog(ctx);
            Skill skill;
            if (!SkillCommandShared.ResolveSkill(cat, slugArg, out skill))
                return SlashCommandResult.Fail("Unknown skill: " + slugArg);

            // The skill body rides as a hidden system message (committed at send, not now), so the
            // transcript shows only the short ask and an early return can't orphan it.
            string systemContext =
                "The user invoked this skill with /use-skill. Follow its instructions for their request.\n\n"
                + SkillTools.RenderSkill(skill);

            string msg = "Use the " + skill.Slug + " skill.";
            if (rest.Length > 0) msg += " " + rest;
            return SlashCommandResult.Send(msg, systemContext);
        }

        public IList<ArgCompletion> CompleteArgument(string argText, ISlashCommandContext ctx)
        {
            List<ArgCompletion> result = new List<ArgCompletion>();
            string a = argText ?? string.Empty;
            if (a.IndexOf(' ') >= 0) return result; // only complete the first token (the slug)

            SkillCatalog cat = SkillCommandShared.BuildCatalog(ctx);
            IList<Skill> skills = cat.Skills;
            for (int i = 0; i < skills.Count; i++)
            {
                string slug = skills[i].Slug;
                if (a.Length > 0 && !SlashMatch.HyphenPrefix(slug, a)) continue;
                result.Add(new ArgCompletion(slug + " - " + skills[i].Description, slug + " ", false));
            }
            return result;
        }
    }
}
