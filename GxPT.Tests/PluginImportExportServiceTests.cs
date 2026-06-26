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
                AgentMaxTier.Write, null, 0, file, AgentSource.User);
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
                delegate(IList<string> conflicts) { asked = true; Assert.Contains("skill: shared", conflicts); return false; });
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

            PluginImportExportService.EnablePlugin("pack", _skillsRoot, _agentsRoot, _pluginsRoot);
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
    }
}
