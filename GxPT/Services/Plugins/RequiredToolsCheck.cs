using System;
using System.Collections.Generic;

namespace GxPT
{
    // The availability verdict for one required-tool group: whether it's satisfied by the installer's catalog,
    // and (when not) the entry labels that are missing - for the install warning and the Details dialog.
    internal sealed class RequiredToolStatus
    {
        public RequiredToolGroup Group;
        public bool Satisfied;
        public List<string> Missing;

        public RequiredToolStatus() { Missing = new List<string>(); }
    }

    // Evaluates a plugin's requiredTools against a live tool catalog. A concrete id is met when present; a glob
    // is met when AgentToolResolver.Resolve (glob a tier-ceiling a available) finds at least one match - i.e.
    // the same intersection used at authoring time, re-run on the installer's catalog so it survives tool-set
    // drift. Best-effort: callers warn, never block. Pure; net48-testable.
    internal static class RequiredToolsCheck
    {
        public static IList<RequiredToolStatus> Evaluate(IList<RequiredToolGroup> groups,
            IEnumerable<string> available, Func<string, ToolTier> tierOf)
        {
            List<RequiredToolStatus> result = new List<RequiredToolStatus>();
            if (groups == null) return result;

            HashSet<string> avail = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (available != null)
                foreach (string n in available) if (!string.IsNullOrEmpty(n)) avail.Add(n);

            for (int i = 0; i < groups.Count; i++)
            {
                RequiredToolGroup g = groups[i];
                if (g == null) continue;

                List<bool> entryOk = new List<bool>();
                List<string> missing = new List<string>();

                if (g.Tools != null)
                    for (int j = 0; j < g.Tools.Count; j++)
                    {
                        bool ok = avail.Contains(g.Tools[j]);
                        entryOk.Add(ok);
                        if (!ok) missing.Add(g.Tools[j]);
                    }
                if (g.Globs != null)
                    for (int j = 0; j < g.Globs.Count; j++)
                    {
                        RequiredToolGlob gl = g.Globs[j];
                        if (gl == null || string.IsNullOrEmpty(gl.Pattern)) continue;
                        List<string> hits = AgentToolResolver.Resolve(
                            new string[] { gl.Pattern }, gl.MaxTier, avail, tierOf);
                        bool ok = hits.Count > 0;
                        entryOk.Add(ok);
                        if (!ok) missing.Add(gl.Pattern);
                    }

                RequiredToolStatus st = new RequiredToolStatus();
                st.Group = g;
                if (entryOk.Count == 0) st.Satisfied = true;                       // nothing required
                else if (g.Mode == RequiredToolMode.All) st.Satisfied = !entryOk.Contains(false);
                else st.Satisfied = entryOk.Contains(true);                        // anyOf
                if (!st.Satisfied) st.Missing = missing;
                result.Add(st);
            }
            return result;
        }

        // Just the unsatisfied groups (the install warning's content).
        public static IList<RequiredToolStatus> Unmet(IList<RequiredToolGroup> groups,
            IEnumerable<string> available, Func<string, ToolTier> tierOf)
        {
            List<RequiredToolStatus> unmet = new List<RequiredToolStatus>();
            IList<RequiredToolStatus> all = Evaluate(groups, available, tierOf);
            for (int i = 0; i < all.Count; i++) if (!all[i].Satisfied) unmet.Add(all[i]);
            return unmet;
        }
    }
}
