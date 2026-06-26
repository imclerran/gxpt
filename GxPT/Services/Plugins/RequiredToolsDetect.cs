using System;
using System.Collections.Generic;

namespace GxPT
{
    // One candidate tool row in the picker: its server-qualified id, whether it's pre-checked (an agent can
    // call it / declared it), and whether it's currently present in the live catalog.
    internal sealed class ToolSeedItem
    {
        public string Id;
        public bool Checked;
        public bool Connected;
    }

    // One server group in the picker: its candidate tool rows, and - when an agent globbed the whole server -
    // an option to require the whole server (at WholeServerTier). Mode and WholeServerSelected are the
    // author-editable state the picker mutates; ToGroups reads them.
    internal sealed class ToolGroupSeed
    {
        public string Server;
        public List<ToolSeedItem> Items;
        public bool OfferWholeServer;
        public AgentMaxTier WholeServerTier;
        public bool ServerConnected;

        public RequiredToolMode Mode;          // author choice; default AnyOf
        public bool WholeServerSelected;       // is the "(whole server)" option chosen

        public ToolGroupSeed()
        {
            Items = new List<ToolSeedItem>();
            WholeServerTier = AgentMaxTier.ReadOnly;
            Mode = RequiredToolMode.AnyOf;
        }
    }

    // Seeds the required-tools picker from the bundled agents only (skills/scripts never contribute). Per
    // server: the tools the agents can actually call (AgentToolResolver.Resolve - glob a tier a available,
    // pre-checked + connected), plus any not-connected declared ids, plus a whole-server option when an agent
    // globbed the server. The live catalog (names + tierOf) comes from the export probe snapshot. Pure.
    internal static class RequiredToolsDetect
    {
        public static IList<ToolGroupSeed> Seed(IEnumerable<Agent> agents,
            IEnumerable<string> available, Func<string, ToolTier> tierOf)
        {
            HashSet<string> avail = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (available != null)
                foreach (string n in available) if (!string.IsNullOrEmpty(n)) avail.Add(n);

            Dictionary<string, ToolGroupSeed> byServer =
                new Dictionary<string, ToolGroupSeed>(StringComparer.OrdinalIgnoreCase);

            if (agents != null)
            {
                foreach (Agent a in agents)
                {
                    if (a == null) continue;

                    // Resolved: connected, tier-capped, glob-matched - exactly what the agent can call.
                    List<string> resolved = AgentToolResolver.Resolve(a.Tools, a.MaxTier, avail, tierOf);
                    for (int i = 0; i < resolved.Count; i++)
                        AddItem(byServer, resolved[i], true, true);

                    if (a.Tools == null) continue;
                    for (int i = 0; i < a.Tools.Length; i++)
                    {
                        string decl = a.Tools[i];
                        if (string.IsNullOrEmpty(decl)) continue;

                        if (RequiredTool.IsWholeServerGlob(decl))
                        {
                            ToolGroupSeed g = Get(byServer, RequiredTool.ServerOf(decl));
                            if (!g.OfferWholeServer) { g.OfferWholeServer = true; g.WholeServerSelected = true; }
                            if ((int)a.MaxTier > (int)g.WholeServerTier) g.WholeServerTier = a.MaxTier;
                        }
                        else if (!RequiredTool.HasWildcard(decl) && !avail.Contains(decl))
                        {
                            // A concrete id the agent named that isn't currently connected: surface it
                            // pre-checked but flagged, so the author keeps it as a requirement.
                            AddItem(byServer, decl, true, false);
                        }
                    }
                }
            }

            // A server is "connected" if any catalog tool carries its prefix.
            foreach (KeyValuePair<string, ToolGroupSeed> kv in byServer)
            {
                foreach (string n in avail)
                {
                    if (string.Equals(RequiredTool.ServerOf(n), kv.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        kv.Value.ServerConnected = true;
                        break;
                    }
                }
            }

            List<ToolGroupSeed> list = new List<ToolGroupSeed>(byServer.Values);
            list.Sort(delegate(ToolGroupSeed x, ToolGroupSeed y) { return string.CompareOrdinal(x.Server, y.Server); });
            for (int i = 0; i < list.Count; i++)
                list[i].Items.Sort(delegate(ToolSeedItem x, ToolSeedItem y) { return string.CompareOrdinal(x.Id, y.Id); });
            return list;
        }

        // The PowerShell host tools the command server may expose, in preference order: Windows PowerShell
        // (Desktop 2.0-5.1), then PowerShell Core (6/7), then legacy 1.0. A skill shipping a .ps1 needs one
        // of these.
        public static readonly string[] PowerShellHostTools = new string[]
        {
            "command__powershell", "command__pwsh", "command__powershell_v1"
        };

        // Ensures the seed requires a PowerShell host (used when a bundled skill ships a .ps1). Only hosts
        // actually present in the catalog are offered, and exactly one is pre-checked: the highest-preference
        // available host (powershell > pwsh > powershell_v1). The author can check additional hosts for an
        // "any of these" requirement. If no host is present at all, all three are listed (unchecked, not
        // connected) so a requirement can still be declared for the installer's sake.
        public static void AddPowerShellRequirement(IList<ToolGroupSeed> seed, IEnumerable<string> available)
        {
            if (seed == null) return;
            HashSet<string> avail = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (available != null) foreach (string n in available) if (!string.IsNullOrEmpty(n)) avail.Add(n);

            List<string> show = new List<string>();
            for (int i = 0; i < PowerShellHostTools.Length; i++)
                if (avail.Contains(PowerShellHostTools[i])) show.Add(PowerShellHostTools[i]);

            string preferred;
            if (show.Count > 0)
            {
                preferred = show[0]; // highest-preference available host
            }
            else
            {
                // None installed: list all so the author can still require one; default to PowerShell Core.
                for (int i = 0; i < PowerShellHostTools.Length; i++) show.Add(PowerShellHostTools[i]);
                preferred = "command__pwsh";
            }

            string server = RequiredTool.ServerOf(PowerShellHostTools[0]); // "command"
            ToolGroupSeed g = null;
            for (int i = 0; i < seed.Count; i++)
                if (string.Equals(seed[i].Server, server, StringComparison.OrdinalIgnoreCase)) { g = seed[i]; break; }
            if (g == null) { g = new ToolGroupSeed(); g.Server = server; seed.Add(g); }

            for (int i = 0; i < show.Count; i++)
            {
                string id = show[i];
                bool chk = string.Equals(id, preferred, StringComparison.OrdinalIgnoreCase);
                bool present = false;
                for (int k = 0; k < g.Items.Count; k++)
                    if (string.Equals(g.Items[k].Id, id, StringComparison.OrdinalIgnoreCase))
                    { g.Items[k].Checked = chk; present = true; break; }
                if (!present)
                {
                    ToolSeedItem it = new ToolSeedItem();
                    it.Id = id; it.Checked = chk; it.Connected = avail.Contains(id);
                    g.Items.Add(it);
                }
                if (avail.Contains(id)) g.ServerConnected = true;
            }
            g.Items.Sort(delegate(ToolSeedItem x, ToolSeedItem y) { return string.CompareOrdinal(x.Id, y.Id); });
        }

        // Converts a (possibly author-edited) seed into manifest requiredTools groups. Per server: when the
        // whole-server option is selected, a single "<server>__*" glob (at the server's tier); otherwise the
        // checked concrete tool ids. Servers with nothing selected are dropped.
        public static List<RequiredToolGroup> ToGroups(IEnumerable<ToolGroupSeed> seed)
        {
            List<RequiredToolGroup> groups = new List<RequiredToolGroup>();
            if (seed == null) return groups;
            foreach (ToolGroupSeed s in seed)
            {
                if (s == null || string.IsNullOrEmpty(s.Server)) continue;

                RequiredToolGroup g = new RequiredToolGroup();
                g.Server = s.Server;
                g.Mode = s.Mode;

                if (s.OfferWholeServer && s.WholeServerSelected)
                {
                    g.Globs.Add(new RequiredToolGlob(s.Server + "__*", s.WholeServerTier));
                }
                else if (s.Items != null)
                {
                    for (int i = 0; i < s.Items.Count; i++)
                        if (s.Items[i].Checked && !string.IsNullOrEmpty(s.Items[i].Id))
                            g.Tools.Add(s.Items[i].Id);
                }

                if (g.Tools.Count > 0 || g.Globs.Count > 0) groups.Add(g);
            }
            return groups;
        }

        private static ToolGroupSeed Get(Dictionary<string, ToolGroupSeed> byServer, string server)
        {
            ToolGroupSeed g;
            if (!byServer.TryGetValue(server, out g))
            {
                g = new ToolGroupSeed();
                g.Server = server;
                byServer[server] = g;
            }
            return g;
        }

        private static void AddItem(Dictionary<string, ToolGroupSeed> byServer, string id, bool chk, bool connected)
        {
            ToolGroupSeed g = Get(byServer, RequiredTool.ServerOf(id));
            for (int i = 0; i < g.Items.Count; i++)
            {
                if (string.Equals(g.Items[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    if (chk) g.Items[i].Checked = true;          // strengthen on a later, stronger signal
                    if (connected) g.Items[i].Connected = true;
                    return;
                }
            }
            ToolSeedItem it = new ToolSeedItem();
            it.Id = id;
            it.Checked = chk;
            it.Connected = connected;
            g.Items.Add(it);
        }
    }
}
