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

    // One member of an installed plugin, with the human-facing name/description read from its frontmatter
    // (the manifest stores only slugs). Kind is "skill" or "agent".
    internal sealed class PluginMemberInfo
    {
        public string Kind;
        public string Slug;
        public string Name;
        public string Description;
    }

    // Core plugin import/export (no UI), parallel to SkillImportExportService: throw on failure so callers
    // own the UX. A .gxpl archive is a zip holding a plugin.json manifest at the root plus the member files
    // under skills/<slug>/ and agents/<slug>.md. Install fans those files into the ordinary user skills and
    // agents roots (so the catalogs discover them with no changes), and records membership in a registry
    // manifest under the plugins root. The registry is what makes a plugin upgradable, disablable, and
    // uninstallable as one unit. All operations take explicit roots so the logic stays pure and
    // net48-testable. XP / .NET 3.5 friendly.
    //
    // Ownership model: a plugin owns the skill/agent slugs its manifest lists, and a given slug is owned by
    // at most one plugin. Install treats an existing same-slug item (a user's own, or another plugin's) as a
    // foreign CONFLICT - the caller is prompted, and on confirm ownership transfers to the incoming plugin
    // (the slug is removed from any other plugin's manifest). Because ownership is exclusive, uninstall and
    // upgrade-drop only ever delete a plugin's OWN copy (the active copy when enabled, the parked copy when
    // disabled), never a foreign item left in the active roots.
    internal static class PluginImportExportService
    {
        private enum MemberKind { Skill, Agent }

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

        // Lists an installed plugin's members with their frontmatter name/description, read from wherever the
        // files live (active roots when enabled, the disabled holding area when disabled). Skills first, then
        // agents, in manifest order. A member whose file is missing/unreadable still appears, with its slug as
        // the name and an empty description. Throws if the plugin isn't installed.
        public static IList<PluginMemberInfo> GetPluginDetails(string name, string skillsRoot,
            string agentsRoot, string pluginsRoot)
        {
            PluginRegistry registry = new PluginRegistry(pluginsRoot);
            PluginManifest m = registry.Load(name);
            if (m == null) throw new InvalidOperationException("Plugin '" + name + "' is not installed.");

            string disabled = registry.DisabledDir(name);
            List<PluginMemberInfo> list = new List<PluginMemberInfo>();

            for (int i = 0; i < m.Skills.Count; i++)
            {
                string slug = m.Skills[i];
                string dir = CurrentPath(MemberKind.Skill, m.Enabled, skillsRoot, agentsRoot, disabled, slug);
                PluginMemberInfo info = NewMember("skill", slug);
                try
                {
                    string file = Path.Combine(dir, "SKILL.md");
                    if (File.Exists(file))
                    {
                        SkillFrontmatter fm = SkillFrontmatter.Parse(File.ReadAllText(file, Encoding.UTF8));
                        if (fm != null)
                        {
                            if (!string.IsNullOrEmpty(fm.Name)) info.Name = fm.Name;
                            info.Description = fm.Description ?? string.Empty;
                        }
                    }
                }
                catch { }
                list.Add(info);
            }

            for (int i = 0; i < m.Agents.Count; i++)
            {
                string slug = m.Agents[i];
                string file = CurrentPath(MemberKind.Agent, m.Enabled, skillsRoot, agentsRoot, disabled, slug);
                PluginMemberInfo info = NewMember("agent", slug);
                try
                {
                    if (File.Exists(file))
                    {
                        AgentFrontmatter fm = AgentFrontmatter.Parse(File.ReadAllText(file, Encoding.UTF8));
                        if (fm != null)
                        {
                            if (!string.IsNullOrEmpty(fm.Name)) info.Name = fm.Name;
                            info.Description = fm.Description ?? string.Empty;
                        }
                    }
                }
                catch { }
                list.Add(info);
            }

            return list;
        }

        private static PluginMemberInfo NewMember(string kind, string slug)
        {
            PluginMemberInfo info = new PluginMemberInfo();
            info.Kind = kind;
            info.Slug = slug;
            info.Name = slug;
            info.Description = string.Empty;
            return info;
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
                string dir = CurrentPath(MemberKind.Skill, m.Enabled, skillsRoot, agentsRoot, disabled, slug);
                if (Directory.Exists(dir))
                    skills.Add(new Skill(slug, slug, string.Empty, dir, Path.Combine(dir, "SKILL.md"), SkillSource.User));
            }
            List<Agent> agents = new List<Agent>();
            for (int i = 0; i < m.Agents.Count; i++)
            {
                string slug = m.Agents[i];
                string file = CurrentPath(MemberKind.Agent, m.Enabled, skillsRoot, agentsRoot, disabled, slug);
                if (File.Exists(file))
                    agents.Add(new Agent(slug, slug, string.Empty, null, AgentMaxTier.Write, null, 0, file, AgentSource.User));
            }

            ExportPlugin(m.Name, m.Version, m.Description, skills, agents, archivePath);
        }

        // Installs (or upgrades) the archive's plugin. Member files are staged and validated before anything
        // touches the real roots. confirmOverwrite is consulted ONCE with the list of foreign items (a user's
        // own same-slug item, or one owned by another plugin) that would be replaced; returning false cancels
        // the whole install. On confirm, ownership of any conflicting slug transfers to this plugin (it is
        // removed from the other plugin's manifest). On an upgrade, members present in the prior version but
        // absent from this one are removed. Returns the install result, or null when cancelled at the prompt.
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
                bool ownsActively = existing != null && existing.Enabled; // our installed copies are in the active roots
                string selfSlug = SkillSlug.Make(archived.Name);
                IList<PluginManifest> allPlugins = registry.ListInstalled();

                // A foreign conflict is a same-slug item we do NOT actively own: a stray item in the active
                // root (a user's own, or - when this plugin is disabled - anything sitting where our parked
                // copy isn't), or a slug some OTHER installed plugin lists. Conflicts collect transfers so the
                // other plugin gives up the slug on confirm.
                List<string> conflicts = new List<string>();
                List<Transfer> transfers = new List<Transfer>();
                CollectConflicts(MemberKind.Skill, skillDirs.Keys, skillsRoot, agentsRoot,
                    existing, ownsActively, allPlugins, selfSlug, conflicts, transfers);
                CollectConflicts(MemberKind.Agent, agentFiles.Keys, skillsRoot, agentsRoot,
                    existing, ownsActively, allPlugins, selfSlug, conflicts, transfers);

                if (conflicts.Count > 0)
                {
                    if (confirmOverwrite == null || !confirmOverwrite(conflicts.AsReadOnly()))
                        return null;
                }

                // Confirmed: transfer ownership of every conflicting slug away from any other plugin.
                List<PluginManifest> ownersToSave = new List<PluginManifest>();
                for (int i = 0; i < transfers.Count; i++)
                {
                    Transfer t = transfers[i];
                    RemoveIgnoreCase(KindList(t.Owner, t.Kind), t.Slug);
                    if (!ownersToSave.Contains(t.Owner)) ownersToSave.Add(t.Owner);
                }
                for (int i = 0; i < ownersToSave.Count; i++) registry.Save(ownersToSave[i]);

                string disabled = registry.DisabledDir(archived.Name);

                // Upgrade: drop members the new version no longer ships, deleting only OUR copy (active when we
                // were enabled, parked when disabled) so a foreign same-slug item in the active root is safe.
                List<string> removedSkills = new List<string>();
                List<string> removedAgents = new List<string>();
                if (isUpgrade)
                {
                    for (int i = 0; i < existing.Skills.Count; i++)
                    {
                        string slug = existing.Skills[i];
                        if (skillDirs.ContainsKey(slug)) continue;
                        DeleteMemberQuiet(MemberKind.Skill,
                            CurrentPath(MemberKind.Skill, existing.Enabled, skillsRoot, agentsRoot, disabled, slug));
                        removedSkills.Add(slug);
                    }
                    for (int i = 0; i < existing.Agents.Count; i++)
                    {
                        string slug = existing.Agents[i];
                        if (agentFiles.ContainsKey(slug)) continue;
                        DeleteMemberQuiet(MemberKind.Agent,
                            CurrentPath(MemberKind.Agent, existing.Enabled, skillsRoot, agentsRoot, disabled, slug));
                        removedAgents.Add(slug);
                    }
                }

                // Fan the new files into the active roots, replacing in place. Track what actually landed so
                // that even on a mid-fan failure the registry is saved matching disk (no permanent drift).
                Directory.CreateDirectory(skillsRoot);
                Directory.CreateDirectory(agentsRoot);
                List<string> installedSkills = new List<string>();
                List<string> installedAgents = new List<string>();
                PluginManifest record = new PluginManifest();
                record.Name = archived.Name;
                record.Version = archived.Version;
                record.Description = archived.Description;
                record.Enabled = true;
                try
                {
                    foreach (KeyValuePair<string, string> kv in skillDirs)
                    {
                        string dest = Path.Combine(skillsRoot, kv.Key);
                        DeleteDirQuiet(dest);
                        FileSafe.CopyDirectory(kv.Value, dest);
                        installedSkills.Add(kv.Key);
                    }
                    foreach (KeyValuePair<string, string> kv in agentFiles)
                    {
                        string dest = Path.Combine(agentsRoot, kv.Key + ".md");
                        File.Copy(kv.Value, dest, true);
                        installedAgents.Add(kv.Key);
                    }
                }
                catch
                {
                    // Persist the partially-installed set so the manifest still matches what's on disk, then
                    // surface the failure. A retry (re-import) completes the install.
                    SaveRecord(registry, record, installedSkills, installedAgents);
                    throw;
                }

                installedSkills.Sort(StringComparer.Ordinal);
                installedAgents.Sort(StringComparer.Ordinal);
                SaveRecord(registry, record, installedSkills, installedAgents);
                // A fresh install lands enabled with its files in the active roots, so clear any stale disabled
                // holding from a prior disabled-then-reimported state.
                if (!string.IsNullOrEmpty(disabled)) DeleteDirQuiet(disabled);

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
        // disabled/ holding area, so the catalogs no longer discover them. Throws if the plugin isn't
        // installed or if a member file can't be moved (e.g. locked); the enabled flag is only flipped after
        // the moves succeed, so a failure leaves a consistent "still enabled" state to retry.
        public static void DisablePlugin(string name, string skillsRoot, string agentsRoot, string pluginsRoot)
        {
            PluginRegistry registry = new PluginRegistry(pluginsRoot);
            PluginManifest m = registry.Load(name);
            if (m == null) throw new InvalidOperationException("Plugin '" + name + "' is not installed.");
            if (!m.Enabled) return;

            Relocate(m, registry.DisabledDir(name), skillsRoot, agentsRoot, true);
            m.Enabled = false;
            registry.Save(m);
        }

        // Re-enables a disabled plugin by moving its files back into the active roots. Refuses to overwrite a
        // foreign same-slug item a user created while the plugin was disabled (throws with the collisions);
        // the enabled flag is only flipped after the moves succeed.
        public static void EnablePlugin(string name, string skillsRoot, string agentsRoot, string pluginsRoot)
        {
            PluginRegistry registry = new PluginRegistry(pluginsRoot);
            PluginManifest m = registry.Load(name);
            if (m == null) throw new InvalidOperationException("Plugin '" + name + "' is not installed.");
            if (m.Enabled) return;

            Directory.CreateDirectory(skillsRoot);
            Directory.CreateDirectory(agentsRoot);
            string disabled = registry.DisabledDir(name);
            Relocate(m, disabled, skillsRoot, agentsRoot, false);
            m.Enabled = true;
            registry.Save(m);
            if (!string.IsNullOrEmpty(disabled)) DeleteDirQuiet(disabled);
        }

        // Uninstalls a plugin: deletes its member files (the active copies when enabled; the parked copies in
        // the disabled holding are removed with the registry folder), then removes the registry folder. Only
        // the plugin's OWN copies are touched, never a foreign same-slug item. A no-op if not installed.
        public static void UninstallPlugin(string name, string skillsRoot, string agentsRoot, string pluginsRoot)
        {
            PluginRegistry registry = new PluginRegistry(pluginsRoot);
            PluginManifest m = registry.Load(name);
            if (m != null && m.Enabled)
            {
                for (int i = 0; i < m.Skills.Count; i++)
                    DeleteDirQuiet(ActivePath(MemberKind.Skill, skillsRoot, agentsRoot, m.Skills[i]));
                for (int i = 0; i < m.Agents.Count; i++)
                    DeleteFileQuiet(ActivePath(MemberKind.Agent, skillsRoot, agentsRoot, m.Agents[i]));
            }
            registry.RemoveDir(name); // also clears the disabled holding area (it lives inside the plugin dir)
        }

        // True when the zip carries a plugin.json at its root. Routes a generic .zip between the plugin,
        // skill, and conversation importers; .gxpl/.gxsk/.gxcv route by extension and never need this.
        public static bool ArchiveContainsPlugin(string archivePath)
        {
            return ZipSafe.ContainsEntry(archivePath, delegate(string name)
            {
                return string.Equals(name, PluginRegistry.ManifestFileName, StringComparison.OrdinalIgnoreCase);
            });
        }

        // ---- ownership / conflict ----

        // A conflicting slug owned by another plugin, to be released from that plugin on confirm.
        private sealed class Transfer
        {
            public PluginManifest Owner;
            public MemberKind Kind;
            public string Slug;
        }

        private static void CollectConflicts(MemberKind kind, IEnumerable<string> stagedSlugs,
            string skillsRoot, string agentsRoot, PluginManifest existing, bool ownsActively,
            IList<PluginManifest> allPlugins, string selfSlug,
            List<string> conflicts, List<Transfer> transfers)
        {
            string label = (kind == MemberKind.Agent ? "agent: " : "skill: ");
            foreach (string slug in stagedSlugs)
            {
                // We "actively own" the slug only when this plugin is enabled (its copy is in the active root)
                // and its prior manifest lists it; that copy is replaced silently as part of the upgrade.
                bool ourActive = ownsActively && existing != null && ListContainsIgnoreCase(KindList(existing, kind), slug);
                bool activeForeign = MemberExists(kind, ActivePath(kind, skillsRoot, agentsRoot, slug)) && !ourActive;

                bool ownedByOther = false;
                for (int i = 0; i < allPlugins.Count; i++)
                {
                    PluginManifest p = allPlugins[i];
                    if (string.Equals(SkillSlug.Make(p.Name), selfSlug, StringComparison.OrdinalIgnoreCase)) continue;
                    if (ListContainsIgnoreCase(KindList(p, kind), slug))
                    {
                        ownedByOther = true;
                        Transfer t = new Transfer();
                        t.Owner = p; t.Kind = kind; t.Slug = slug;
                        transfers.Add(t);
                    }
                }

                if (activeForeign || ownedByOther) conflicts.Add(label + slug);
            }
        }

        private static void SaveRecord(PluginRegistry registry, PluginManifest record,
            List<string> skills, List<string> agents)
        {
            record.Skills.Clear();
            record.Agents.Clear();
            record.Skills.AddRange(skills);
            record.Agents.AddRange(agents);
            registry.Save(record);
        }

        private static List<string> KindList(PluginManifest m, MemberKind kind)
        {
            return kind == MemberKind.Agent ? m.Agents : m.Skills;
        }

        private static bool ListContainsIgnoreCase(List<string> list, string value)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void RemoveIgnoreCase(List<string> list, string value)
        {
            if (list == null) return;
            for (int i = list.Count - 1; i >= 0; i--)
                if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) list.RemoveAt(i);
        }

        // ---- relocate (enable/disable) ----

        // Moves every member between the active roots and the disabled holding. toDisabled=true parks the
        // active copies (overwriting the holding); toDisabled=false restores them, first checking that no
        // foreign same-slug item occupies the active root (it would otherwise be clobbered).
        private static void Relocate(PluginManifest m, string disabledDir, string skillsRoot, string agentsRoot, bool toDisabled)
        {
            if (!toDisabled)
            {
                List<string> collisions = new List<string>();
                AddActiveCollisions(MemberKind.Skill, m.Skills, skillsRoot, agentsRoot, collisions);
                AddActiveCollisions(MemberKind.Agent, m.Agents, skillsRoot, agentsRoot, collisions);
                if (collisions.Count > 0)
                    throw new IOException("Cannot enable plugin '" + m.Name +
                        "': these already exist and would be overwritten: " + string.Join(", ", collisions.ToArray()));
            }

            MoveMembers(MemberKind.Skill, m.Skills, disabledDir, skillsRoot, agentsRoot, toDisabled);
            MoveMembers(MemberKind.Agent, m.Agents, disabledDir, skillsRoot, agentsRoot, toDisabled);
        }

        private static void AddActiveCollisions(MemberKind kind, List<string> slugs,
            string skillsRoot, string agentsRoot, List<string> into)
        {
            string label = (kind == MemberKind.Agent ? "agent: " : "skill: ");
            for (int i = 0; i < slugs.Count; i++)
                if (MemberExists(kind, ActivePath(kind, skillsRoot, agentsRoot, slugs[i])))
                    into.Add(label + slugs[i]);
        }

        private static void MoveMembers(MemberKind kind, List<string> slugs,
            string disabledDir, string skillsRoot, string agentsRoot, bool toDisabled)
        {
            for (int i = 0; i < slugs.Count; i++)
            {
                string slug = slugs[i];
                string active = ActivePath(kind, skillsRoot, agentsRoot, slug);
                string parked = DisabledPath(kind, disabledDir, slug);
                string from = toDisabled ? active : parked;
                string to = toDisabled ? parked : active;
                MoveMember(kind, from, to, toDisabled); // disable overwrites the holding; enable was pre-checked
            }
        }

        // ---- path / member helpers ----

        private static string ActivePath(MemberKind kind, string skillsRoot, string agentsRoot, string slug)
        {
            return kind == MemberKind.Agent ? Path.Combine(agentsRoot, slug + ".md") : Path.Combine(skillsRoot, slug);
        }

        private static string DisabledPath(MemberKind kind, string disabledDir, string slug)
        {
            string sub = Path.Combine(disabledDir, kind == MemberKind.Agent ? "agents" : "skills");
            return kind == MemberKind.Agent ? Path.Combine(sub, slug + ".md") : Path.Combine(sub, slug);
        }

        // Where the member's files live right now, given the plugin's enabled state.
        private static string CurrentPath(MemberKind kind, bool enabled,
            string skillsRoot, string agentsRoot, string disabledDir, string slug)
        {
            return enabled
                ? ActivePath(kind, skillsRoot, agentsRoot, slug)
                : DisabledPath(kind, disabledDir, slug);
        }

        private static bool MemberExists(MemberKind kind, string path)
        {
            return kind == MemberKind.Agent ? File.Exists(path) : Directory.Exists(path);
        }

        private static void DeleteMemberQuiet(MemberKind kind, string path)
        {
            if (kind == MemberKind.Agent) DeleteFileQuiet(path); else DeleteDirQuiet(path);
        }

        // Moves a skill folder or agent file. A missing source is a no-op (idempotent). When overwriteTarget
        // is false and the target exists, the underlying copy/move throws rather than clobbering. The source
        // is always removed; if it can't be (e.g. locked), the exception propagates so the caller can leave a
        // consistent state and report the failure.
        private static void MoveMember(MemberKind kind, string src, string dst, bool overwriteTarget)
        {
            if (!MemberExists(kind, src)) return;
            string parent = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            if (kind == MemberKind.Agent)
            {
                File.Copy(src, dst, overwriteTarget); // overwrite=false => throws if dst exists
                File.Delete(src);
                return;
            }

            if (overwriteTarget && Directory.Exists(dst)) Directory.Delete(dst, true);
            try { Directory.Move(src, dst); }
            catch
            {
                FileSafe.CopyDirectory(src, dst);
                Directory.Delete(src, true);
            }
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
                // Guard the Win32 "*.md" wildcard quirk (it can also match longer extensions like .mdx).
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
