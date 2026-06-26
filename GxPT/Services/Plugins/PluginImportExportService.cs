using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Ionic.Zip;

namespace GxPT
{
    // Result of installing a .gxpl: what landed, and (on an upgrade) what was dropped relative to the prior
    // version. Surfaced so the UI can report "installed N skills / M agents" or "upgraded, removed X".
    internal sealed class PluginInstallResult
    {
        public string Name;
        public string Version;
        public IList<string> Skills;
        public IList<string> Agents;
        public bool WasUpgrade;
        public IList<string> RemovedSkills;
        public IList<string> RemovedAgents;
    }

    // Core plugin import/export (no UI), parallel to SkillImportExportService: throw on failure so callers
    // own the UX. A .gxpl archive is a zip holding a plugin.json manifest at the root plus the member files
    // under skills/<slug>/ and agents/<slug>.md. Install fans those files into the ordinary user skills and
    // agents roots (so the catalogs discover them with no changes), and records membership in a registry
    // manifest under the plugins root. The registry is what makes a plugin upgradable, disablable, and
    // uninstallable as one unit. All operations take explicit roots so the logic stays pure and
    // net48-testable. XP / .NET 3.5 friendly.
    internal static class PluginImportExportService
    {
        // Builds a .gxpl from already-resolved skills/agents (the caller filters out bundled items - they
        // ship with every install and aren't packaged, mirroring /export). At least one member is required.
        public static void ExportPlugin(string name, string version, string description,
            IEnumerable<Skill> skills, IEnumerable<Agent> agents, string archivePath)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Plugin name is required.", "name");
            if (string.IsNullOrEmpty(archivePath))
                throw new ArgumentException("Archive path is required.", "archivePath");

            List<Skill> skillList = new List<Skill>();
            if (skills != null) foreach (Skill s in skills) if (s != null) skillList.Add(s);
            List<Agent> agentList = new List<Agent>();
            if (agents != null) foreach (Agent a in agents) if (a != null) agentList.Add(a);

            if (skillList.Count == 0 && agentList.Count == 0)
                throw new InvalidOperationException("A plugin must contain at least one skill or agent.");

            PluginManifest manifest = new PluginManifest();
            manifest.Name = name;
            manifest.Version = version;
            manifest.Description = description;
            manifest.Enabled = true;

            using (var zip = new ZipFile())
            {
                zip.AlternateEncoding = Encoding.UTF8;
                zip.AlternateEncodingUsage = ZipOption.AsNecessary;
                zip.CompressionLevel = Ionic.Zlib.CompressionLevel.BestCompression;

                for (int i = 0; i < skillList.Count; i++)
                {
                    Skill s = skillList[i];
                    if (string.IsNullOrEmpty(s.Directory) || !Directory.Exists(s.Directory))
                        throw new InvalidOperationException("Skill folder not found: " + s.Slug);
                    zip.AddDirectory(s.Directory, "skills/" + s.Slug);
                    if (!manifest.Skills.Contains(s.Slug)) manifest.Skills.Add(s.Slug);
                }
                for (int i = 0; i < agentList.Count; i++)
                {
                    Agent a = agentList[i];
                    if (string.IsNullOrEmpty(a.FilePath) || !File.Exists(a.FilePath))
                        throw new InvalidOperationException("Agent file not found: " + a.Slug);
                    // Store under agents/<slug>.md (byte-for-byte) so the on-disk file name (which may be
                    // "Code Explorer.md") is normalized to the slug the catalog will rediscover on import.
                    zip.AddEntry("agents/" + a.Slug + ".md", File.ReadAllBytes(a.FilePath));
                    if (!manifest.Agents.Contains(a.Slug)) manifest.Agents.Add(a.Slug);
                }

                zip.AddEntry(PluginRegistry.ManifestFileName, manifest.ToJson(), new UTF8Encoding(false));
                zip.Save(archivePath);
            }
        }

        // Re-exports an already-installed plugin to a .gxpl, packaging its current member files from wherever
        // they live - the active roots when enabled, or the disabled holding area when disabled - so an
        // installed plugin round-trips regardless of state. Throws if the plugin isn't installed.
        public static void ExportInstalledPlugin(string name, string skillsRoot, string agentsRoot,
            string pluginsRoot, string archivePath)
        {
            PluginRegistry registry = new PluginRegistry(pluginsRoot);
            PluginManifest m = registry.Load(name);
            if (m == null) throw new InvalidOperationException("Plugin '" + name + "' is not installed.");

            string disabled = registry.DisabledDir(name);
            List<Skill> skills = new List<Skill>();
            for (int i = 0; i < m.Skills.Count; i++)
            {
                string slug = m.Skills[i];
                string dir = m.Enabled
                    ? Path.Combine(skillsRoot, slug)
                    : Path.Combine(Path.Combine(disabled, "skills"), slug);
                if (Directory.Exists(dir))
                    skills.Add(new Skill(slug, slug, string.Empty, dir, Path.Combine(dir, "SKILL.md"), SkillSource.User));
            }
            List<Agent> agents = new List<Agent>();
            for (int i = 0; i < m.Agents.Count; i++)
            {
                string slug = m.Agents[i];
                string file = m.Enabled
                    ? Path.Combine(agentsRoot, slug + ".md")
                    : Path.Combine(Path.Combine(disabled, "agents"), slug + ".md");
                if (File.Exists(file))
                    agents.Add(new Agent(slug, slug, string.Empty, null, AgentMaxTier.Write, null, 0, file, AgentSource.User));
            }

            ExportPlugin(m.Name, m.Version, m.Description, skills, agents, archivePath);
        }

        // Installs (or upgrades) the archive's plugin. Member files are staged and validated before anything
        // touches the real roots. confirmOverwrite is consulted ONCE with the list of foreign items (skills
        // or agents not already owned by this plugin) that would be replaced; returning false cancels the
        // whole install. On an upgrade, members present in the prior version but absent from this one are
        // removed. Returns the install result, or null when cancelled at the overwrite prompt.
        public static PluginInstallResult ImportPlugin(string archivePath, string skillsRoot,
            string agentsRoot, string pluginsRoot, Predicate<IList<string>> confirmOverwrite)
        {
            if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath))
                throw new FileNotFoundException("Archive not found.", archivePath);
            if (string.IsNullOrEmpty(skillsRoot)) throw new ArgumentException("Skills root is required.", "skillsRoot");
            if (string.IsNullOrEmpty(agentsRoot)) throw new ArgumentException("Agents root is required.", "agentsRoot");
            if (string.IsNullOrEmpty(pluginsRoot)) throw new ArgumentException("Plugins root is required.", "pluginsRoot");

            string staging = Path.Combine(Path.GetTempPath(),
                "GxPT-plugin-import-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(staging);
                ZipSafe.SafeExtract(archivePath, staging, true);

                string manifestPath = Path.Combine(staging, PluginRegistry.ManifestFileName);
                if (!File.Exists(manifestPath))
                    throw new InvalidDataException("The archive is not a plugin (no plugin.json found).");

                PluginManifest archived = PluginManifest.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
                if (archived == null || string.IsNullOrEmpty(archived.Name))
                    throw new InvalidDataException("The plugin manifest declares no name.");

                // Disk contents are authoritative for membership (the manifest arrays are informational).
                Dictionary<string, string> skillDirs = LocateSkills(Path.Combine(staging, "skills"));
                Dictionary<string, string> agentFiles = LocateAgents(Path.Combine(staging, "agents"));
                if (skillDirs.Count == 0 && agentFiles.Count == 0)
                    throw new InvalidDataException("The plugin contains no skills or agents.");

                PluginRegistry registry = new PluginRegistry(pluginsRoot);
                PluginManifest existing = registry.Load(archived.Name);
                bool isUpgrade = existing != null;

                List<string> ownedSkills = existing != null ? existing.Skills : new List<string>();
                List<string> ownedAgents = existing != null ? existing.Agents : new List<string>();

                // Foreign conflicts: a target already on disk that this plugin does NOT already own (a user
                // item, or another plugin's). Items we own are silently replaced - that's the upgrade path.
                List<string> conflicts = new List<string>();
                foreach (string slug in skillDirs.Keys)
                    if (Directory.Exists(Path.Combine(skillsRoot, slug)) && !ownedSkills.Contains(slug))
                        conflicts.Add("skill: " + slug);
                foreach (string slug in agentFiles.Keys)
                    if (File.Exists(Path.Combine(agentsRoot, slug + ".md")) && !ownedAgents.Contains(slug))
                        conflicts.Add("agent: " + slug);

                if (conflicts.Count > 0)
                {
                    if (confirmOverwrite == null || !confirmOverwrite(conflicts.AsReadOnly()))
                        return null;
                }

                // Upgrade: drop members the new version no longer ships (from active roots and any disabled
                // holding the prior version parked them in).
                List<string> removedSkills = new List<string>();
                List<string> removedAgents = new List<string>();
                if (isUpgrade)
                {
                    string disabled = registry.DisabledDir(archived.Name);
                    for (int i = 0; i < ownedSkills.Count; i++)
                    {
                        string slug = ownedSkills[i];
                        if (skillDirs.ContainsKey(slug)) continue;
                        DeleteDirQuiet(Path.Combine(skillsRoot, slug));
                        if (!string.IsNullOrEmpty(disabled))
                            DeleteDirQuiet(Path.Combine(Path.Combine(disabled, "skills"), slug));
                        removedSkills.Add(slug);
                    }
                    for (int i = 0; i < ownedAgents.Count; i++)
                    {
                        string slug = ownedAgents[i];
                        if (agentFiles.ContainsKey(slug)) continue;
                        DeleteFileQuiet(Path.Combine(agentsRoot, slug + ".md"));
                        if (!string.IsNullOrEmpty(disabled))
                            DeleteFileQuiet(Path.Combine(Path.Combine(disabled, "agents"), slug + ".md"));
                        removedAgents.Add(slug);
                    }
                }

                // Fan the new files into the active roots, replacing in place.
                Directory.CreateDirectory(skillsRoot);
                Directory.CreateDirectory(agentsRoot);
                List<string> installedSkills = new List<string>();
                foreach (KeyValuePair<string, string> kv in skillDirs)
                {
                    string dest = Path.Combine(skillsRoot, kv.Key);
                    DeleteDirQuiet(dest);
                    CopyDirectory(kv.Value, dest);
                    installedSkills.Add(kv.Key);
                }
                List<string> installedAgents = new List<string>();
                foreach (KeyValuePair<string, string> kv in agentFiles)
                {
                    string dest = Path.Combine(agentsRoot, kv.Key + ".md");
                    File.Copy(kv.Value, dest, true);
                    installedAgents.Add(kv.Key);
                }
                installedSkills.Sort(StringComparer.Ordinal);
                installedAgents.Sort(StringComparer.Ordinal);

                // Record membership. A fresh install lands enabled with its files in the active roots, so
                // clear any stale disabled holding from a prior disabled-then-reimported state.
                PluginManifest record = new PluginManifest();
                record.Name = archived.Name;
                record.Version = archived.Version;
                record.Description = archived.Description;
                record.Enabled = true;
                record.Skills.AddRange(installedSkills);
                record.Agents.AddRange(installedAgents);
                registry.Save(record);
                string disabledDir = registry.DisabledDir(archived.Name);
                if (!string.IsNullOrEmpty(disabledDir)) DeleteDirQuiet(disabledDir);

                PluginInstallResult result = new PluginInstallResult();
                result.Name = record.Name;
                result.Version = record.Version;
                result.Skills = installedSkills.AsReadOnly();
                result.Agents = installedAgents.AsReadOnly();
                result.WasUpgrade = isUpgrade;
                result.RemovedSkills = removedSkills.AsReadOnly();
                result.RemovedAgents = removedAgents.AsReadOnly();
                return result;
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                catch { }
            }
        }

        // Disables an installed plugin by moving its member files OUT of the active roots into the plugin's
        // disabled/ holding area, so the catalogs no longer discover them. Idempotent: a missing source file
        // (already moved, or removed by the user) is skipped. Throws if the plugin isn't installed.
        public static void DisablePlugin(string name, string skillsRoot, string agentsRoot, string pluginsRoot)
        {
            PluginRegistry registry = new PluginRegistry(pluginsRoot);
            PluginManifest m = registry.Load(name);
            if (m == null) throw new InvalidOperationException("Plugin '" + name + "' is not installed.");
            if (!m.Enabled) return;

            string disabled = registry.DisabledDir(name);
            string dSkills = Path.Combine(disabled, "skills");
            string dAgents = Path.Combine(disabled, "agents");

            for (int i = 0; i < m.Skills.Count; i++)
                MoveDir(Path.Combine(skillsRoot, m.Skills[i]), Path.Combine(dSkills, m.Skills[i]));
            for (int i = 0; i < m.Agents.Count; i++)
                MoveFile(Path.Combine(agentsRoot, m.Agents[i] + ".md"),
                         Path.Combine(dAgents, m.Agents[i] + ".md"));

            m.Enabled = false;
            registry.Save(m);
        }

        // Re-enables a disabled plugin by moving its files back into the active roots. Idempotent.
        public static void EnablePlugin(string name, string skillsRoot, string agentsRoot, string pluginsRoot)
        {
            PluginRegistry registry = new PluginRegistry(pluginsRoot);
            PluginManifest m = registry.Load(name);
            if (m == null) throw new InvalidOperationException("Plugin '" + name + "' is not installed.");
            if (m.Enabled) return;

            string disabled = registry.DisabledDir(name);
            string dSkills = Path.Combine(disabled, "skills");
            string dAgents = Path.Combine(disabled, "agents");

            Directory.CreateDirectory(skillsRoot);
            Directory.CreateDirectory(agentsRoot);
            for (int i = 0; i < m.Skills.Count; i++)
                MoveDir(Path.Combine(dSkills, m.Skills[i]), Path.Combine(skillsRoot, m.Skills[i]));
            for (int i = 0; i < m.Agents.Count; i++)
                MoveFile(Path.Combine(dAgents, m.Agents[i] + ".md"),
                         Path.Combine(agentsRoot, m.Agents[i] + ".md"));

            m.Enabled = true;
            registry.Save(m);
            if (!string.IsNullOrEmpty(disabled)) DeleteDirQuiet(disabled);
        }

        // Uninstalls a plugin: deletes its member skill folders and agent files (whether currently active or
        // parked in the disabled holding area), then removes the registry folder. A no-op if not installed.
        public static void UninstallPlugin(string name, string skillsRoot, string agentsRoot, string pluginsRoot)
        {
            PluginRegistry registry = new PluginRegistry(pluginsRoot);
            PluginManifest m = registry.Load(name);
            if (m != null)
            {
                for (int i = 0; i < m.Skills.Count; i++)
                    DeleteDirQuiet(Path.Combine(skillsRoot, m.Skills[i]));
                for (int i = 0; i < m.Agents.Count; i++)
                    DeleteFileQuiet(Path.Combine(agentsRoot, m.Agents[i] + ".md"));
            }
            registry.RemoveDir(name); // also clears the disabled holding area (it lives inside the plugin dir)
        }

        // True when the zip carries a plugin.json at its root. Routes a generic .zip between the plugin,
        // skill, and conversation importers; .gxpl/.gxsk/.gxcv route by extension and never need this.
        public static bool ArchiveContainsPlugin(string archivePath)
        {
            try
            {
                using (var zip = ZipFile.Read(archivePath, new ReadOptions { Encoding = Encoding.UTF8 }))
                {
                    foreach (ZipEntry entry in zip)
                    {
                        if (entry == null || entry.IsDirectory) continue;
                        string name = (entry.FileName ?? string.Empty).Replace('\\', '/').TrimStart('/');
                        if (string.Equals(name, PluginRegistry.ManifestFileName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        // ---- staging discovery ----

        // slug -> folder, for each skills/<slug> that holds a SKILL.md with a non-empty description (the
        // same validity bar as catalog discovery). A malformed member fails the whole install loudly.
        private static Dictionary<string, string> LocateSkills(string skillsStaging)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(skillsStaging)) return map;
            string[] dirs = Directory.GetDirectories(skillsStaging);
            Array.Sort(dirs, StringComparer.Ordinal);
            for (int i = 0; i < dirs.Length; i++)
            {
                string slug = SkillSlug.Make(Path.GetFileName(dirs[i]));
                if (string.IsNullOrEmpty(slug)) continue;
                string skillMd = Path.Combine(dirs[i], "SKILL.md");
                if (!File.Exists(skillMd))
                    throw new InvalidDataException("Plugin skill '" + slug + "' has no SKILL.md.");
                SkillFrontmatter fm = SkillFrontmatter.Parse(File.ReadAllText(skillMd, Encoding.UTF8));
                if (fm == null || string.IsNullOrEmpty(fm.Description))
                    throw new InvalidDataException("Plugin skill '" + slug + "' declares no description.");
                map[slug] = dirs[i];
            }
            return map;
        }

        // slug -> file, for each agents/<slug>.md with a non-empty description.
        private static Dictionary<string, string> LocateAgents(string agentsStaging)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(agentsStaging)) return map;
            string[] files = Directory.GetFiles(agentsStaging, "*.md");
            Array.Sort(files, StringComparer.Ordinal);
            for (int i = 0; i < files.Length; i++)
            {
                string ext = Path.GetExtension(files[i]);
                if (ext == null || !ext.Equals(".md", StringComparison.OrdinalIgnoreCase)) continue;
                string slug = SkillSlug.Make(Path.GetFileNameWithoutExtension(files[i]));
                if (string.IsNullOrEmpty(slug)) continue;
                AgentFrontmatter fm = AgentFrontmatter.Parse(File.ReadAllText(files[i], Encoding.UTF8));
                if (fm == null || string.IsNullOrEmpty(fm.Description))
                    throw new InvalidDataException("Plugin agent '" + slug + "' declares no description.");
                map[slug] = files[i];
            }
            return map;
        }

        // ---- file helpers ----

        private static void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            string[] files = Directory.GetFiles(sourceDir);
            for (int i = 0; i < files.Length; i++)
                File.Copy(files[i], Path.Combine(targetDir, Path.GetFileName(files[i])), true);
            string[] dirs = Directory.GetDirectories(sourceDir);
            for (int i = 0; i < dirs.Length; i++)
                CopyDirectory(dirs[i], Path.Combine(targetDir, Path.GetFileName(dirs[i])));
        }

        // Moves a directory, replacing any existing target. Directory.Move can't cross volumes or overwrite,
        // so fall back to copy-then-delete. A missing source is a no-op (idempotent enable/disable).
        private static void MoveDir(string sourceDir, string targetDir)
        {
            if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir)) return;
            DeleteDirQuiet(targetDir);
            string parent = Path.GetDirectoryName(targetDir);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            try { Directory.Move(sourceDir, targetDir); }
            catch
            {
                CopyDirectory(sourceDir, targetDir);
                DeleteDirQuiet(sourceDir);
            }
        }

        private static void MoveFile(string sourceFile, string targetFile)
        {
            if (string.IsNullOrEmpty(sourceFile) || !File.Exists(sourceFile)) return;
            string parent = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.Copy(sourceFile, targetFile, true);
            DeleteFileQuiet(sourceFile);
        }

        private static void DeleteDirQuiet(string dir)
        {
            try { if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { }
        }

        private static void DeleteFileQuiet(string file)
        {
            try { if (!string.IsNullOrEmpty(file) && File.Exists(file)) File.Delete(file); }
            catch { }
        }
    }
}
