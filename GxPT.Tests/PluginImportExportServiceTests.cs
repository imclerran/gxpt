using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GxPT;
using Ionic.Zip;
using Xunit;

namespace GxPT.Tests
{
    public sealed class PluginImportExportServiceTests : IDisposable
    {
        private readonly string _root;
        private readonly string _skillsRoot;
        private readonly string _agentsRoot;
        private readonly string _pluginsRoot;

        public PluginImportExportServiceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_pluginio_" + Guid.NewGuid().ToString("N"));
            _skillsRoot = Path.Combine(_root, "skills");
            _agentsRoot = Path.Combine(_root, "agents");
            _pluginsRoot = Path.Combine(_root, "plugins");
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        // ---- helpers ----

        private Skill WriteSkill(string slug, string description)
        {
            string dir = Path.Combine(Path.Combine(_root, "source", "skills"), slug);
            Directory.CreateDirectory(Path.Combine(dir, "scripts"));
            File.WriteAllText(Path.Combine(dir, "SKILL.md"),
                "---\nname: " + slug + "\ndescription: " + description + "\n---\n\nBody.\n",
                new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "scripts", "run.bat"), "@echo off\n");
            return new Skill(slug, slug, description, dir, Path.Combine(dir, "SKILL.md"), SkillSource.User);
        }

        private Agent WriteAgent(string slug, string description)
        {
            string dir = Path.Combine(_root, "source", "agents");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, slug + ".md");
            File.WriteAllText(file,
                "---\nname: " + slug + "\ndescription: " + description + "\ntools: [a]\n---\n\nPrompt.\n",
                new UTF8Encoding(false));
            return new Agent(slug, slug, description, new string[] { "a" },
                AgentMaxTier.Write, null, AgentEffort.Unset, 0, file, AgentSource.User);
        }

        private string ExportSample(string name, string version, IEnumerable<Skill> skills, IEnumerable<Agent> agents)
        {
            string archive = Path.Combine(_root, name + ".gxpl");
            PluginImportExportService.ExportPlugin(name, version, "A bundle.", skills, agents, archive);
            return archive;
        }

        // ---- export / import round trip ----

        [Fact]
        public void Export_then_import_installs_skills_and_agents_and_records_membership()
        {
            string archive = ExportSample("demo-pack", "1.0.0",
                new[] { WriteSkill("skill-a", "A."), WriteSkill("skill-b", "B.") },
                new[] { WriteAgent("helper", "H.") });
            Assert.True(File.Exists(archive));

            PluginInstallResult r = PluginImportExportService.ImportPlugin(
                archive, _skillsRoot, _agentsRoot, _pluginsRoot, null);

            Assert.NotNull(r);
            Assert.False(r.WasUpgrade);
            Assert.True(File.Exists(Path.Combine(_skillsRoot, "skill-a", "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(_skillsRoot, "skill-a", "scripts", "run.bat")));
            Assert.True(File.Exists(Path.Combine(_skillsRoot, "skill-b", "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(_agentsRoot, "helper.md")));

            PluginManifest m = new PluginRegistry(_pluginsRoot).Load("demo-pack");
            Assert.NotNull(m);
            Assert.True(m.Enabled);
            Assert.Equal("1.0.0", m.Version);
            Assert.Contains("skill-a", m.Skills);
            Assert.Contains("skill-b", m.Skills);
            Assert.Contains("helper", m.Agents);
        }

        [Fact]
        public void Export_requires_at_least_one_member()
        {
            Assert.Throws<InvalidOperationException>(delegate
            {
                PluginImportExportService.ExportPlugin("empty", "1", "d",
                    new Skill[0], new Agent[0], Path.Combine(_root, "empty.gxpl"));
            });
        }

        [Fact]
        public void Import_non_plugin_archive_throws()
        {
            string archive = Path.Combine(_root, "notaplugin.zip");
            using (var zip = new ZipFile())
            {
                zip.AddEntry("skills/x/SKILL.md", "---\ndescription: d.\n---\n");
                zip.Save(archive); // no plugin.json at the root
            }
            Assert.Throws<InvalidDataException>(delegate
            {
                PluginImportExportService.ImportPlugin(archive, _skillsRoot, _agentsRoot, _pluginsRoot, null);
            });
        }

        [Fact]
        public void ArchiveContainsPlugin_detects_plugin_archives_only()
        {
            string plugin = ExportSample("probe", "1", new[] { WriteSkill("p-skill", "P.") }, null);
            Assert.True(PluginImportExportService.ArchiveContainsPlugin(plugin));

            string skillOnly = Path.Combine(_root, "skill.zip");
            using (var zip = new ZipFile())
            {
                zip.AddEntry("some-skill/SKILL.md", "---\ndescription: d.\n---\n");
                zip.Save(skillOnly);
            }
            Assert.False(PluginImportExportService.ArchiveContainsPlugin(skillOnly));
        }

        // ---- upgrade ----

        [Fact]
        public void Upgrade_removes_members_dropped_by_the_new_version()
        {
            string v1 = ExportSample("pack", "1.0",
                new[] { WriteSkill("keep", "K."), WriteSkill("drop", "D.") },
                new[] { WriteAgent("old-agent", "O.") });
            PluginImportExportService.ImportPlugin(v1, _skillsRoot, _agentsRoot, _pluginsRoot, null);
            Assert.True(Directory.Exists(Path.Combine(_skillsRoot, "drop")));
            Assert.True(File.Exists(Path.Combine(_agentsRoot, "old-agent.md")));

            // v2 keeps "keep", drops "drop" and "old-agent" (its own members - no overwrite prompt needed).
            string v2 = ExportSample("pack", "2.0", new[] { WriteSkill("keep", "K2.") }, null);
            PluginInstallResult r = PluginImportExportService.ImportPlugin(
                v2, _skillsRoot, _agentsRoot, _pluginsRoot,
                delegate(IList<string> conflicts) { Assert.True(false, "no conflict expected"); return true; });

            Assert.True(r.WasUpgrade);
            Assert.Contains("drop", r.RemovedSkills);
            Assert.Contains("old-agent", r.RemovedAgents);
            Assert.False(Directory.Exists(Path.Combine(_skillsRoot, "drop")));
            Assert.False(File.Exists(Path.Combine(_agentsRoot, "old-agent.md")));
            Assert.True(Directory.Exists(Path.Combine(_skillsRoot, "keep")));

            PluginManifest m = new PluginRegistry(_pluginsRoot).Load("pack");
            Assert.Equal("2.0", m.Version);
            Assert.DoesNotContain("drop", m.Skills);
        }

        // ---- conflict with a foreign item ----

        [Fact]
        public void Import_prompts_and_aborts_on_foreign_conflict()
        {
            // A pre-existing user skill not owned by any plugin.
            Directory.CreateDirectory(Path.Combine(_skillsRoot, "shared"));
            File.WriteAllText(Path.Combine(_skillsRoot, "shared", "SKILL.md"), "user copy");
            File.WriteAllText(Path.Combine(_skillsRoot, "shared", "marker.txt"), "keep");

            string archive = ExportSample("pack", "1", new[] { WriteSkill("shared", "From plugin.") }, null);

            // Declined -> returns null, the user's file untouched.
            bool asked = false;
            PluginInstallResult declined = PluginImportExportService.ImportPlugin(
                archive, _skillsRoot, _agentsRoot, _pluginsRoot,
                delegate(IList<string> conflicts) { asked = true; Assert.Contains(conflicts, Mentions("shared")); return false; });
            Assert.True(asked);
            Assert.Null(declined);
            Assert.True(File.Exists(Path.Combine(_skillsRoot, "shared", "marker.txt")));
            Assert.False(new PluginRegistry(_pluginsRoot).Exists("pack"));

            // Confirmed -> the folder is replaced wholesale (stray marker gone).
            PluginInstallResult confirmed = PluginImportExportService.ImportPlugin(
                archive, _skillsRoot, _agentsRoot, _pluginsRoot,
                delegate(IList<string> conflicts) { return true; });
            Assert.NotNull(confirmed);
            Assert.False(File.Exists(Path.Combine(_skillsRoot, "shared", "marker.txt")));
            Assert.True(File.Exists(Path.Combine(_skillsRoot, "shared", "SKILL.md")));
        }

        // ---- disable / enable (move-aside) ----

        [Fact]
        public void Disable_moves_members_aside_and_enable_restores_them()
        {
            string archive = ExportSample("pack", "1",
                new[] { WriteSkill("s1", "S.") }, new[] { WriteAgent("a1", "A.") });
            PluginImportExportService.ImportPlugin(archive, _skillsRoot, _agentsRoot, _pluginsRoot, null);

            PluginImportExportService.DisablePlugin("pack", _skillsRoot, _agentsRoot, _pluginsRoot);
            Assert.False(Directory.Exists(Path.Combine(_skillsRoot, "s1")));
            Assert.False(File.Exists(Path.Combine(_agentsRoot, "a1.md")));
            Assert.False(new PluginRegistry(_pluginsRoot).Load("pack").Enabled);

            PluginImportExportService.EnablePlugin("pack", _skillsRoot, _agentsRoot, _pluginsRoot, null);
            Assert.True(Directory.Exists(Path.Combine(_skillsRoot, "s1")));
            Assert.True(File.Exists(Path.Combine(_agentsRoot, "a1.md")));
            Assert.True(new PluginRegistry(_pluginsRoot).Load("pack").Enabled);
        }

        // ---- uninstall ----

        [Fact]
        public void Uninstall_removes_member_files_and_registry_entry()
        {
            string archive = ExportSample("pack", "1",
                new[] { WriteSkill("s1", "S.") }, new[] { WriteAgent("a1", "A.") });
            PluginImportExportService.ImportPlugin(archive, _skillsRoot, _agentsRoot, _pluginsRoot, null);

            PluginImportExportService.UninstallPlugin("pack", _skillsRoot, _agentsRoot, _pluginsRoot);
            Assert.False(Directory.Exists(Path.Combine(_skillsRoot, "s1")));
            Assert.False(File.Exists(Path.Combine(_agentsRoot, "a1.md")));
            Assert.False(new PluginRegistry(_pluginsRoot).Exists("pack"));
        }

        // ---- details ----

        [Fact]
        public void GetPluginDetails_lists_member_names_and_descriptions()
        {
            string archive = ExportSample("pack", "1",
                new[] { WriteSkill("skill-a", "Does A things.") },
                new[] { WriteAgent("helper", "Helps out.") });
            PluginImportExportService.ImportPlugin(archive, _skillsRoot, _agentsRoot, _pluginsRoot, null);

            System.Collections.Generic.IList<PluginMemberInfo> details =
                PluginImportExportService.GetPluginDetails("pack", _skillsRoot, _agentsRoot, _pluginsRoot);

            Assert.Equal(2, details.Count);
            Assert.Equal("skill", details[0].Kind);
            Assert.Equal("skill-a", details[0].Slug);
            Assert.Equal("Does A things.", details[0].Description);
            Assert.Equal("agent", details[1].Kind);
            Assert.Equal("Helps out.", details[1].Description);
        }

        [Fact]
        public void GetPluginDetails_reads_a_disabled_plugin_from_its_holding_area()
        {
            string archive = ExportSample("pack", "1", new[] { WriteSkill("skill-a", "A desc.") }, null);
            PluginImportExportService.ImportPlugin(archive, _skillsRoot, _agentsRoot, _pluginsRoot, null);
            PluginImportExportService.DisablePlugin("pack", _skillsRoot, _agentsRoot, _pluginsRoot);

            System.Collections.Generic.IList<PluginMemberInfo> details =
                PluginImportExportService.GetPluginDetails("pack", _skillsRoot, _agentsRoot, _pluginsRoot);

            Assert.Single(details);
            Assert.Equal("A desc.", details[0].Description);
        }

        // ---- re-export an installed plugin ----

        [Fact]
        public void ExportInstalled_repackages_an_installed_plugin_for_reinstall()
        {
            string archive = ExportSample("pack", "1.5",
                new[] { WriteSkill("s1", "S.") }, new[] { WriteAgent("a1", "A.") });
            PluginImportExportService.ImportPlugin(archive, _skillsRoot, _agentsRoot, _pluginsRoot, null);

            string reexport = Path.Combine(_root, "reexport.gxpl");
            PluginImportExportService.ExportInstalledPlugin("pack", _skillsRoot, _agentsRoot, _pluginsRoot, reexport);
            Assert.True(File.Exists(reexport));

            // Wipe and reinstall from the re-export: members and version come back.
            PluginImportExportService.UninstallPlugin("pack", _skillsRoot, _agentsRoot, _pluginsRoot);
            PluginInstallResult r = PluginImportExportService.ImportPlugin(
                reexport, _skillsRoot, _agentsRoot, _pluginsRoot, null);

            Assert.Equal("1.5", r.Version);
            Assert.Contains("s1", r.Skills);
            Assert.Contains("a1", r.Agents);
            Assert.True(Directory.Exists(Path.Combine(_skillsRoot, "s1")));
            Assert.True(File.Exists(Path.Combine(_agentsRoot, "a1.md")));
        }

        [Fact]
        public void ExportInstalled_works_for_a_disabled_plugin()
        {
            string archive = ExportSample("pack", "1", new[] { WriteSkill("s1", "S.") }, null);
            PluginImportExportService.ImportPlugin(archive, _skillsRoot, _agentsRoot, _pluginsRoot, null);
            PluginImportExportService.DisablePlugin("pack", _skillsRoot, _agentsRoot, _pluginsRoot);

            // The active root no longer holds s1, but the export still packages it from the disabled holding.
            string reexport = Path.Combine(_root, "disabled-export.gxpl");
            PluginImportExportService.ExportInstalledPlugin("pack", _skillsRoot, _agentsRoot, _pluginsRoot, reexport);
            Assert.True(PluginImportExportService.ArchiveContainsPlugin(reexport));
        }

        [Fact]
        public void Uninstall_also_removes_a_disabled_plugins_parked_files()
        {
            string archive = ExportSample("pack", "1", new[] { WriteSkill("s1", "S.") }, null);
            PluginImportExportService.ImportPlugin(archive, _skillsRoot, _agentsRoot, _pluginsRoot, null);
            PluginImportExportService.DisablePlugin("pack", _skillsRoot, _agentsRoot, _pluginsRoot);

            PluginImportExportService.UninstallPlugin("pack", _skillsRoot, _agentsRoot, _pluginsRoot);
            Assert.False(new PluginRegistry(_pluginsRoot).Exists("pack"));
            Assert.False(Directory.Exists(new PluginRegistry(_pluginsRoot).PluginDir("pack")));
        }

        // ---- ownership / provenance (review fixes) ----

        // Writes a non-plugin, hand-authored skill straight into the active skills root.
        private void WriteForeignSkill(string slug, string body)
        {
            string dir = Path.Combine(_skillsRoot, slug);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), body, new UTF8Encoding(false));
        }

        [Fact]
        public void Installing_over_another_plugins_slug_disables_that_plugin_losslessly()
        {
            string a = ExportSample("plugin-a", "1", new[] { WriteSkill("shared", "A's.") }, null);
            PluginImportExportService.ImportPlugin(a, _skillsRoot, _agentsRoot, _pluginsRoot, null);

            string b = ExportSample("plugin-b", "1", new[] { WriteSkill("shared", "B's.") }, null);
            bool asked = false;
            PluginInstallResult r = PluginImportExportService.ImportPlugin(b, _skillsRoot, _agentsRoot, _pluginsRoot,
                delegate(IList<string> c) { asked = true; Assert.Contains(c, Mentions("plugin-a")); return true; });
            Assert.True(asked);
            Assert.NotNull(r);

            // Both stay installed and still own 'shared'; A is disabled, B active.
            PluginRegistry reg = new PluginRegistry(_pluginsRoot);
            Assert.Contains("shared", reg.Load("plugin-a").Skills);
            Assert.Contains("shared", reg.Load("plugin-b").Skills);
            Assert.False(reg.Load("plugin-a").Enabled);
            Assert.True(reg.Load("plugin-b").Enabled);

            // Toggle back to A: B is disabled, A restored - lossless.
            PluginImportExportService.EnablePlugin("plugin-a", _skillsRoot, _agentsRoot, _pluginsRoot,
                delegate(IList<string> c) { return true; });
            Assert.True(reg.Load("plugin-a").Enabled);
            Assert.False(reg.Load("plugin-b").Enabled);
        }

        [Fact]
        public void Installing_lists_every_conflicting_plugin_and_disables_them_all()
        {
            // A owns s1, B owns s2 (both enabled); C ships s1 + s2.
            PluginImportExportService.ImportPlugin(
                ExportSample("plugin-a", "1", new[] { WriteSkill("s1", "A.") }, null),
                _skillsRoot, _agentsRoot, _pluginsRoot, null);
            PluginImportExportService.ImportPlugin(
                ExportSample("plugin-b", "1", new[] { WriteSkill("s2", "B.") }, null),
                _skillsRoot, _agentsRoot, _pluginsRoot, null);

            string c = ExportSample("plugin-c", "1",
                new[] { WriteSkill("s1", "C1."), WriteSkill("s2", "C2.") }, null);
            IList<string> shown = null;
            PluginImportExportService.ImportPlugin(c, _skillsRoot, _agentsRoot, _pluginsRoot,
                delegate(IList<string> lines) { shown = lines; return true; });

            // The prompt named both conflicting plugins, and both got disabled.
            Assert.Contains(shown, Mentions("plugin-a"));
            Assert.Contains(shown, Mentions("plugin-b"));
            PluginRegistry reg = new PluginRegistry(_pluginsRoot);
            Assert.False(reg.Load("plugin-a").Enabled);
            Assert.False(reg.Load("plugin-b").Enabled);
            Assert.True(reg.Load("plugin-c").Enabled);
        }

        [Fact]
        public void Upgrading_a_disabled_plugin_prompts_before_overwriting_a_foreign_active_item()
        {
            string v1 = ExportSample("pack", "1", new[] { WriteSkill("s1", "v1.") }, null);
            PluginImportExportService.ImportPlugin(v1, _skillsRoot, _agentsRoot, _pluginsRoot, null);
            PluginImportExportService.DisablePlugin("pack", _skillsRoot, _agentsRoot, _pluginsRoot);

            // The user creates their own 's1' in the now-empty active root.
            WriteForeignSkill("s1", "user copy");

            string v2 = ExportSample("pack", "2", new[] { WriteSkill("s1", "v2.") }, null);
            bool asked = false;
            PluginInstallResult declined = PluginImportExportService.ImportPlugin(v2, _skillsRoot, _agentsRoot, _pluginsRoot,
                delegate(IList<string> c) { asked = true; Assert.Contains(c, Mentions("s1")); return false; });

            Assert.True(asked);
            Assert.Null(declined);
            Assert.Equal("user copy", File.ReadAllText(Path.Combine(_skillsRoot, "s1", "SKILL.md")));
        }

        [Fact]
        public void Enabling_prompts_before_overwriting_a_foreign_user_item_and_can_decline()
        {
            string v1 = ExportSample("pack", "1", new[] { WriteSkill("s1", "plugin.") }, null);
            PluginImportExportService.ImportPlugin(v1, _skillsRoot, _agentsRoot, _pluginsRoot, null);
            PluginImportExportService.DisablePlugin("pack", _skillsRoot, _agentsRoot, _pluginsRoot);

            WriteForeignSkill("s1", "user copy");

            // Decline: the plugin stays disabled and the user's item is untouched.
            bool enabled = PluginImportExportService.EnablePlugin("pack", _skillsRoot, _agentsRoot, _pluginsRoot,
                delegate(IList<string> c) { Assert.Contains(c, Mentions("s1")); return false; });
            Assert.False(enabled);
            Assert.Equal("user copy", File.ReadAllText(Path.Combine(_skillsRoot, "s1", "SKILL.md")));
            Assert.False(new PluginRegistry(_pluginsRoot).Load("pack").Enabled);

            // Confirm: the plugin's copy replaces the user's item.
            bool enabled2 = PluginImportExportService.EnablePlugin("pack", _skillsRoot, _agentsRoot, _pluginsRoot,
                delegate(IList<string> c) { return true; });
            Assert.True(enabled2);
            Assert.True(new PluginRegistry(_pluginsRoot).Load("pack").Enabled);
        }

        // xUnit predicate: a conflict line that mentions the given token.
        private static Predicate<string> Mentions(string token)
        {
            return delegate(string line) { return line != null && line.Contains(token); };
        }

        [Fact]
        public void Upgrade_drop_of_a_disabled_plugin_keeps_a_foreign_active_item()
        {
            string v1 = ExportSample("pack", "1",
                new[] { WriteSkill("keep", "k."), WriteSkill("drop", "d.") }, null);
            PluginImportExportService.ImportPlugin(v1, _skillsRoot, _agentsRoot, _pluginsRoot, null);
            PluginImportExportService.DisablePlugin("pack", _skillsRoot, _agentsRoot, _pluginsRoot);

            // The user creates their own 'drop' in the active root while the plugin is disabled.
            WriteForeignSkill("drop", "user copy");

            string v2 = ExportSample("pack", "2", new[] { WriteSkill("keep", "k2.") }, null);
            PluginImportExportService.ImportPlugin(v2, _skillsRoot, _agentsRoot, _pluginsRoot,
                delegate(IList<string> c) { return true; });

            // The drop happened against the plugin's parked copy, not the user's active 'drop'.
            Assert.Equal("user copy", File.ReadAllText(Path.Combine(_skillsRoot, "drop", "SKILL.md")));
        }
    }
}
