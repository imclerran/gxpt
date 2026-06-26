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
    // an option to require the whole server (at WholeServerTier).
    internal sealed class ToolGroupSeed
    {
        public string Server;
        public List<ToolSeedItem> Items;
        public bool OfferWholeServer;
        public AgentMaxTier WholeServerTier;
        public bool ServerConnected;

        public ToolGroupSeed()
        {
            Items = new List<ToolSeedItem>();
            WholeServerTier = AgentMaxTier.ReadOnly;
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
                            g.OfferWholeServer = true;
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
