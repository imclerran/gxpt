using System;
using System.Collections.Generic;

namespace GxPT
{
    // Which rule decided a skill's effective state (most specific first). Surfaced by the /list-skills list so
    // the user can see *why* a skill is on or off.
    internal enum SkillRule
    {
        SkillHere,      // rung 1: this skill, this conversation
        SkillGlobal,    // rung 2: this skill, globally
        FeatureHere,    // rung 3: all skills, this conversation
        FeatureGlobal,  // rung 4: all skills, globally
        Default         // rung 5: built-in default (on)
    }

    // Resolves a skill's effective enabled state by "most specific setting wins" (design sec.7):
    //   1 this skill here  >  2 this skill global  >  3 all skills here  >  4 all skills global  >  5 ON
    // A per-skill rule (rung 1/2) beats a feature-wide rule (rung 3/4); within a level, the conversation
    // beats global. There is NO hard feature gate - the feature toggle is just the default for skills with
    // no per-skill rule. Pure; net48-testable.
    internal static class SkillResolve
    {
        // Effective state for one skill + the rule that decided it.
        public static bool Resolve(SkillEnablement global, string slug, bool? convSkill,
            bool? convFeatureOff, out SkillRule rule)
        {
            if (convSkill.HasValue) { rule = SkillRule.SkillHere; return convSkill.Value; }

            bool? g = (global != null) ? global.GetSkillOverride(slug) : null;
            if (g.HasValue) { rule = SkillRule.SkillGlobal; return g.Value; }

            if (convFeatureOff.HasValue) { rule = SkillRule.FeatureHere; return !convFeatureOff.Value; }

            if (global != null && global.FeatureOff) { rule = SkillRule.FeatureGlobal; return false; }

            rule = SkillRule.Default;
            return true;
        }

        public static bool IsEnabled(SkillEnablement global, string slug, bool? convSkill, bool? convFeatureOff)
        {
            SkillRule rule;
            return Resolve(global, slug, convSkill, convFeatureOff, out rule);
        }

        // The subset of discovered skills enabled for this conversation, by the ladder above.
        public static List<Skill> EnabledSkills(IList<Skill> all, SkillEnablement global,
            bool? convFeatureOff, IDictionary<string, bool> convOverrides)
        {
            List<Skill> result = new List<Skill>();
            if (all == null) return result;

            for (int i = 0; i < all.Count; i++)
            {
                Skill s = all[i];
                if (s == null) continue;
                bool? ov = null;
                bool v;
                if (convOverrides != null && s.Slug != null && convOverrides.TryGetValue(s.Slug, out v))
                    ov = v;
                if (IsEnabled(global, s.Slug, ov, convFeatureOff)) result.Add(s);
            }
            return result;
        }
    }
}
