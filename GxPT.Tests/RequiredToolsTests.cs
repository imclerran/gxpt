using System;
using System.Collections.Generic;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public sealed class RequiredToolsTests
    {
        // A fixed tier map for the test catalog.
        private static ToolTier Tier(string name)
        {
            switch (name)
            {
                case "command__pwsh":
                case "command__run":
                case "files__write":
                    return ToolTier.Write;
                case "files__delete":
                case "danger__nuke":
                    return ToolTier.Destructive;
                default:
                    return ToolTier.ReadOnly; // files__read, etc.
            }
        }

        private static string[] Catalog()
        {
            return new string[]
            {
                "command__pwsh", "command__run",
                "files__read", "files__write", "files__delete",
                "danger__nuke"
            };
        }

        private static Agent MakeAgent(string slug, AgentMaxTier tier, params string[] tools)
        {
            return new Agent(slug, slug, "d.", tools, tier, null, 0, slug + ".md", AgentSource.User);
        }

        // ---- manifest round-trip ----

        [Fact]
        public void Manifest_round_trips_required_tools()
        {
            PluginManifest m = new PluginManifest();
            m.Name = "p";
            m.RequiredTools.Add(new RequiredToolGroup
            {
                Server = "command",
                Mode = RequiredToolMode.AnyOf,
                Tools = new List<string> { "command__pwsh" },
                Globs = new List<RequiredToolGlob> { new RequiredToolGlob("command__*", AgentMaxTier.Write) }
            });
            m.RequiredTools.Add(new RequiredToolGroup
            {
                Server = "github",
                Mode = RequiredToolMode.All,
                Tools = new List<string> { "github__create_pr" }
            });

            PluginManifest r = PluginManifest.Parse(m.ToJson());

            Assert.Equal(2, r.RequiredTools.Count);
            RequiredToolGroup g = r.RequiredTools[0];
            Assert.Equal("command", g.Server);
            Assert.Equal(RequiredToolMode.AnyOf, g.Mode);
            Assert.Contains("command__pwsh", g.Tools);
            Assert.Single(g.Globs);
            Assert.Equal("command__*", g.Globs[0].Pattern);
            Assert.Equal(AgentMaxTier.Write, g.Globs[0].MaxTier);
            Assert.Equal(RequiredToolMode.All, r.RequiredTools[1].Mode);
        }

        [Fact]
        public void Manifest_without_required_tools_omits_the_key_and_parses_empty()
        {
            PluginManifest m = new PluginManifest();
            m.Name = "p";
            Assert.DoesNotContain("requiredTools", m.ToJson());
            Assert.Empty(PluginManifest.Parse(m.ToJson()).RequiredTools);
        }

        // ---- install-time check ----

        [Fact]
        public void Check_glob_is_satisfied_only_within_the_tier_ceiling()
        {
            // command__* at Write: matches command__pwsh/run -> satisfied.
            RequiredToolGroup ok = Glob("command", "command__*", AgentMaxTier.Write);
            // danger__* at ReadOnly: danger__nuke is Destructive -> excluded -> unsatisfied.
            RequiredToolGroup tierBlocked = Glob("danger", "danger__*", AgentMaxTier.ReadOnly);
            // git__* : no git tools in the catalog -> unsatisfied.
            RequiredToolGroup absent = Glob("git", "git__*", AgentMaxTier.Write);

            IList<RequiredToolStatus> r = RequiredToolsCheck.Evaluate(
                new List<RequiredToolGroup> { ok, tierBlocked, absent }, Catalog(), Tier);

            Assert.True(r[0].Satisfied);
            Assert.False(r[1].Satisfied);
            Assert.False(r[2].Satisfied);
        }

        [Fact]
        public void Check_anyOf_vs_all_for_concrete_tools()
        {
            RequiredToolGroup anyOf = new RequiredToolGroup
            {
                Server = "command",
                Mode = RequiredToolMode.AnyOf,
                Tools = new List<string> { "command__pwsh", "command__missing" }
            };
            RequiredToolGroup all = new RequiredToolGroup
            {
                Server = "command",
                Mode = RequiredToolMode.All,
                Tools = new List<string> { "command__pwsh", "command__missing" }
            };

            IList<RequiredToolStatus> r = RequiredToolsCheck.Evaluate(
                new List<RequiredToolGroup> { anyOf, all }, Catalog(), Tier);

            Assert.True(r[0].Satisfied);                       // one present is enough
            Assert.False(r[1].Satisfied);                      // one missing fails "all"
            Assert.Contains("command__missing", r[1].Missing);

            IList<RequiredToolStatus> unmet = RequiredToolsCheck.Unmet(
                new List<RequiredToolGroup> { anyOf, all }, Catalog(), Tier);
            Assert.Single(unmet);
        }

        // ---- detection seed ----

        [Fact]
        public void Detect_seeds_resolved_tools_globs_and_not_connected_ids()
        {
            Agent x = MakeAgent("x", AgentMaxTier.Write, "command__*");
            Agent y = MakeAgent("y", AgentMaxTier.ReadOnly, "files__read", "custom__tool");

            IList<ToolGroupSeed> seed = RequiredToolsDetect.Seed(
                new List<Agent> { x, y }, Catalog(), Tier);

            ToolGroupSeed command = Find(seed, "command");
            Assert.NotNull(command);
            Assert.True(command.OfferWholeServer);
            Assert.Equal(AgentMaxTier.Write, command.WholeServerTier);
            Assert.True(command.ServerConnected);
            // command__pwsh and command__run resolved (Write tier), both checked + connected.
            Assert.Equal(2, command.Items.Count);
            Assert.True(command.Items.TrueForAll(delegate(ToolSeedItem t) { return t.Checked && t.Connected; }));

            ToolGroupSeed files = Find(seed, "files");
            Assert.NotNull(files);
            Assert.False(files.OfferWholeServer);
            Assert.Single(files.Items); // only files__read (ReadOnly agent can't reach write/delete)
            Assert.Equal("files__read", files.Items[0].Id);

            // A concrete id the agent named but the catalog lacks: surfaced, checked, flagged not-connected.
            ToolGroupSeed custom = Find(seed, "custom");
            Assert.NotNull(custom);
            Assert.False(custom.ServerConnected);
            Assert.Single(custom.Items);
            Assert.True(custom.Items[0].Checked);
            Assert.False(custom.Items[0].Connected);
        }

        [Fact]
        public void ToGroups_emits_globs_for_whole_server_and_concrete_tools_otherwise()
        {
            Agent x = MakeAgent("x", AgentMaxTier.Write, "command__*");
            Agent y = MakeAgent("y", AgentMaxTier.ReadOnly, "files__read", "custom__tool");

            IList<ToolGroupSeed> seed = RequiredToolsDetect.Seed(new List<Agent> { x, y }, Catalog(), Tier);
            List<RequiredToolGroup> groups = RequiredToolsDetect.ToGroups(seed);

            RequiredToolGroup command = FindGroup(groups, "command");
            Assert.Single(command.Globs);                       // whole-server glob, not enumerated
            Assert.Equal("command__*", command.Globs[0].Pattern);
            Assert.Equal(AgentMaxTier.Write, command.Globs[0].MaxTier);
            Assert.Empty(command.Tools);

            RequiredToolGroup files = FindGroup(groups, "files");
            Assert.Contains("files__read", files.Tools);        // concrete tool (no whole-server glob)
            Assert.Empty(files.Globs);

            RequiredToolGroup custom = FindGroup(groups, "custom");
            Assert.Contains("custom__tool", custom.Tools);      // not-connected concrete id still required
        }

        [Fact]
        public void AddPowerShellRequirement_adds_any_host_group_pre_checked()
        {
            List<ToolGroupSeed> seed = new List<ToolGroupSeed>();
            RequiredToolsDetect.AddPowerShellRequirement(seed, new string[] { "command__pwsh", "command__run" });

            ToolGroupSeed cmd = Find(seed, "command");
            Assert.NotNull(cmd);
            Assert.Equal(3, cmd.Items.Count);                                  // all three hosts
            Assert.True(cmd.Items.TrueForAll(delegate(ToolSeedItem t) { return t.Checked; }));
            Assert.True(cmd.ServerConnected);
            Assert.True(Item(cmd, "command__pwsh").Connected);                  // present in catalog
            Assert.False(Item(cmd, "command__powershell_v1").Connected);       // absent but still listed

            // The resulting requirement is "any of the hosts".
            RequiredToolGroup g = FindGroup(RequiredToolsDetect.ToGroups(seed), "command");
            Assert.Contains("command__pwsh", g.Tools);
            Assert.Equal(RequiredToolMode.AnyOf, g.Mode);
        }

        private static ToolSeedItem Item(ToolGroupSeed g, string id)
        {
            for (int i = 0; i < g.Items.Count; i++)
                if (string.Equals(g.Items[i].Id, id, StringComparison.OrdinalIgnoreCase)) return g.Items[i];
            return null;
        }

        private static RequiredToolGroup FindGroup(List<RequiredToolGroup> groups, string server)
        {
            for (int i = 0; i < groups.Count; i++)
                if (string.Equals(groups[i].Server, server, StringComparison.OrdinalIgnoreCase)) return groups[i];
            return null;
        }

        private static RequiredToolGroup Glob(string server, string pattern, AgentMaxTier tier)
        {
            return new RequiredToolGroup
            {
                Server = server,
                Mode = RequiredToolMode.AnyOf,
                Globs = new List<RequiredToolGlob> { new RequiredToolGlob(pattern, tier) }
            };
        }

        private static ToolGroupSeed Find(IList<ToolGroupSeed> seed, string server)
        {
            for (int i = 0; i < seed.Count; i++)
                if (string.Equals(seed[i].Server, server, StringComparison.OrdinalIgnoreCase)) return seed[i];
            return null;
        }
    }
}
