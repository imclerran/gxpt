using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace GxPT
{
    // UI layer for plugin import/export, parallel to SkillImportExportManager: save/open dialogs and
    // MessageBox reporting around PluginImportExportService. A plugin's skill/agent files install into the
    // user-global roots (%AppData%/GxPT/skills and .../agents) so they are discovered like any other user
    // item; the membership registry that makes a plugin upgradable/disablable/uninstallable lives under
    // %AppData%/GxPT/plugins. XP / .NET 3.5 friendly.
    internal static class PluginImportExportManager
    {
        // ---- export ----

        // The full authoring flow: a checklist dialog over the user's and project's skills/agents, then a
        // save dialog, then ExportPlugin. workingDir scopes which project items are offered.
        public static bool ExportInteractive(IWin32Window owner, string workingDir)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            SkillCatalog skillCat = SkillRoots.BuildCatalog(exeDir, workingDir);
            AgentCatalog agentCat = AgentRoots.BuildCatalog(exeDir, workingDir);

            List<Skill> skills = new List<Skill>();
            foreach (Skill s in skillCat.Skills)
                if (s.Source != SkillSource.Bundled) skills.Add(s);
            List<Agent> agents = new List<Agent>();
            foreach (Agent a in agentCat.Agents)
                if (a.Source != AgentSource.Bundled) agents.Add(a);

            if (skills.Count == 0 && agents.Count == 0)
            {
                Info(owner, "There are no user or project skills or agents to export.");
                return false;
            }

            using (PluginExportForm dlg = new PluginExportForm(skills, agents))
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK) return false;
                return SaveAndExport(owner, dlg.PluginName, dlg.PluginVersion, dlg.PluginDescription,
                    dlg.SelectedSkills, dlg.SelectedAgents);
            }
        }

        // Exports a single skill as a one-item .gxpl (the plugin is named after the skill). This is the
        // universal export path: a lone skill is just the smallest possible plugin.
        public static bool ExportSingleSkill(IWin32Window owner, Skill skill)
        {
            if (skill == null)
            {
                Info(owner, "No skill selected.");
                return false;
            }
            return SaveAndExport(owner, skill.Slug, "1.0.0", skill.Description,
                new List<Skill>(new Skill[] { skill }), null);
        }

        private static bool SaveAndExport(IWin32Window owner, string name, string version,
            string description, IList<Skill> skills, IList<Agent> agents)
        {
            using (SaveFileDialog sfd = new SaveFileDialog
            {
                Title = "Export Plugin",
                Filter = "GxPT Plugin (*.gxpl)|*.gxpl",
                DefaultExt = "gxpl",
                FileName = (SkillSlug.Make(name) ?? "plugin") + ".gxpl",
                OverwritePrompt = true
            })
            {
                if (sfd.ShowDialog(owner) != DialogResult.OK) return false;
                try
                {
                    PluginImportExportService.ExportPlugin(name, version, description, skills, agents, sfd.FileName);
                    Info(owner, "Exported plugin '" + name + "'.");
                    return true;
                }
                catch (Exception ex)
                {
                    Error(owner, "Export failed: " + ex.Message);
                    return false;
                }
            }
        }

        // Re-exports an already-installed plugin (used by the Manage Plugins dialog's per-row Export).
        public static bool ExportInstalled(IWin32Window owner, string name)
        {
            string skillsRoot, agentsRoot, pluginsRoot;
            if (!ResolveRoots(owner, out skillsRoot, out agentsRoot, out pluginsRoot)) return false;

            using (SaveFileDialog sfd = new SaveFileDialog
            {
                Title = "Export Plugin",
                Filter = "GxPT Plugin (*.gxpl)|*.gxpl",
                DefaultExt = "gxpl",
                FileName = (SkillSlug.Make(name) ?? "plugin") + ".gxpl",
                OverwritePrompt = true
            })
            {
                if (sfd.ShowDialog(owner) != DialogResult.OK) return false;
                try
                {
                    PluginImportExportService.ExportInstalledPlugin(name, skillsRoot, agentsRoot, pluginsRoot, sfd.FileName);
                    Info(owner, "Exported plugin '" + name + "'.");
                    return true;
                }
                catch (Exception ex)
                {
                    Error(owner, "Export failed: " + ex.Message);
                    return false;
                }
            }
        }

        // ---- install / upgrade ----

        // Open-file dialog + install/upgrade. Shared by File > Plugins > Install and the Manage dialog so the
        // dialog filter lives in one place; the caller refreshes the skills server on a true result.
        public static bool InstallInteractive(IWin32Window owner)
        {
            using (OpenFileDialog ofd = new OpenFileDialog
            {
                Title = "Install Plugin",
                Filter = "GxPT Plugin (*.gxpl)|*.gxpl|Zip Archive (*.zip)|*.zip",
                CheckFileExists = true,
                Multiselect = false
            })
            {
                if (ofd.ShowDialog(owner) != DialogResult.OK) return false;
                return InstallFromFile(owner, ofd.FileName);
            }
        }

        public static bool InstallFromFile(IWin32Window owner, string archivePath)
        {
            string skillsRoot, agentsRoot, pluginsRoot;
            if (!ResolveRoots(owner, out skillsRoot, out agentsRoot, out pluginsRoot)) return false;

            try
            {
                PluginInstallResult r = PluginImportExportService.ImportPlugin(
                    archivePath, skillsRoot, agentsRoot, pluginsRoot,
                    ConflictPrompt(owner, "Installing this plugin requires the following changes:"));
                if (r == null) return false; // declined at the conflict prompt

                string verb = r.WasUpgrade ? "Upgraded" : "Installed";
                StringBuilder msg = new StringBuilder();
                msg.Append(verb).Append(" plugin '").Append(r.Name).Append("' (")
                   .Append(r.Skills.Count).Append(" skill(s), ")
                   .Append(r.Agents.Count).Append(" agent(s)).");
                if (r.WasUpgrade && (r.RemovedSkills.Count > 0 || r.RemovedAgents.Count > 0))
                    msg.Append("\nRemoved ").Append(r.RemovedSkills.Count + r.RemovedAgents.Count)
                       .Append(" item(s) no longer in this version.");
                Info(owner, msg.ToString());
                return true;
            }
            catch (Exception ex)
            {
                Error(owner, "Install failed: " + ex.Message);
                return false;
            }
        }

        // ---- enable / disable / uninstall (by plugin name) ----

        public static bool SetEnabled(IWin32Window owner, string name, bool enabled)
        {
            return SetEnabled(owner, name, enabled, true);
        }

        // announce=false suppresses the success message box (the Manage dialog reloads and the State column
        // already shows the change, so a popup there is just noise). Failures are always reported.
        public static bool SetEnabled(IWin32Window owner, string name, bool enabled, bool announce)
        {
            string skillsRoot, agentsRoot, pluginsRoot;
            if (!ResolveRoots(owner, out skillsRoot, out agentsRoot, out pluginsRoot)) return false;
            try
            {
                if (enabled)
                {
                    bool done = PluginImportExportService.EnablePlugin(name, skillsRoot, agentsRoot, pluginsRoot,
                        ConflictPrompt(owner, "Enabling this plugin requires the following changes:"));
                    if (!done) return false; // declined at the conflict prompt
                }
                else
                {
                    PluginImportExportService.DisablePlugin(name, skillsRoot, agentsRoot, pluginsRoot);
                }
                if (announce)
                    Info(owner, (enabled ? "Enabled" : "Disabled") + " plugin '" + name + "'.");
                return true;
            }
            catch (Exception ex)
            {
                Error(owner, (enabled ? "Enable" : "Disable") + " failed: " + ex.Message);
                return false;
            }
        }

        // A Yes/No prompt that lists the conflict resolution lines (plugins to disable, items to overwrite)
        // built by the service. Used by both install and enable so the wording stays in one place.
        private static Predicate<IList<string>> ConflictPrompt(IWin32Window owner, string header)
        {
            return delegate(IList<string> lines)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append(header).Append("\n\n");
                for (int i = 0; i < lines.Count; i++)
                    sb.Append("    ").Append(lines[i]).Append('\n');
                sb.Append("\nContinue?");
                return MessageBox.Show(owner, sb.ToString(), "Plugins",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
            };
        }

        public static bool Uninstall(IWin32Window owner, string name)
        {
            string skillsRoot, agentsRoot, pluginsRoot;
            if (!ResolveRoots(owner, out skillsRoot, out agentsRoot, out pluginsRoot)) return false;

            var dr = MessageBox.Show(owner,
                "Uninstall plugin '" + name + "'? This removes its skills and agents.",
                "Uninstall Plugin", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (dr != DialogResult.Yes) return false;

            try
            {
                PluginImportExportService.UninstallPlugin(name, skillsRoot, agentsRoot, pluginsRoot);
                Info(owner, "Uninstalled plugin '" + name + "'.");
                return true;
            }
            catch (Exception ex)
            {
                Error(owner, "Uninstall failed: " + ex.Message);
                return false;
            }
        }

        // ---- details ----

        // Shows a dialog listing the plugin's member skills and agents (type, name, description) in a
        // list view, read from frontmatter via the service. Reports an error if details can't be read.
        public static void ShowDetails(IWin32Window owner, string name)
        {
            string skillsRoot, agentsRoot, pluginsRoot;
            if (!ResolveRoots(owner, out skillsRoot, out agentsRoot, out pluginsRoot)) return;

            System.Collections.Generic.IList<PluginMemberInfo> members;
            try { members = PluginImportExportService.GetPluginDetails(name, skillsRoot, agentsRoot, pluginsRoot); }
            catch (Exception ex) { Error(owner, "Could not read plugin details: " + ex.Message); return; }

            using (PluginDetailsForm dlg = new PluginDetailsForm("Plugin: " + name, members))
                dlg.ShowDialog(owner);
        }

        // ---- helpers ----

        // Gives a dialog the app's title-bar icon, matching the main window. The owner form's icon (the
        // MainForm's) is cloned so the dialog owns its own copy to dispose; falls back to the running exe's
        // associated icon if there's no owner. Best-effort - a missing icon just leaves the default.
        public static void ApplyOwnerIcon(Form form)
        {
            if (form == null) return;
            try
            {
                Form o = form.Owner;
                if (o != null && o.Icon != null)
                    form.Icon = (System.Drawing.Icon)o.Icon.Clone();
                else
                    form.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }
        }

        private static bool ResolveRoots(IWin32Window owner, out string skillsRoot,
            out string agentsRoot, out string pluginsRoot)
        {
            skillsRoot = SkillRoots.UserRoot();
            agentsRoot = AgentRoots.UserRoot();
            pluginsRoot = PluginRoots.UserRoot();
            if (string.IsNullOrEmpty(skillsRoot) || string.IsNullOrEmpty(agentsRoot) || string.IsNullOrEmpty(pluginsRoot))
            {
                Error(owner, "Could not resolve the user data folders.");
                return false;
            }
            return true;
        }

        private static void Info(IWin32Window owner, string text)
        {
            try { MessageBox.Show(owner, text, "Plugins", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            catch { }
        }

        private static void Error(IWin32Window owner, string text)
        {
            try { MessageBox.Show(owner, text, "Plugins", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            catch { }
        }
    }
}
