using System;
using System.Windows.Forms;

namespace GxPT
{
    // UI layer for skill import/export, parallel to ImportExportManager: save/open dialogs around
    // SkillImportExportService with MessageBox success/error reporting. Imported skills land in the
    // user-global skills root (%AppData%/GxPT/skills), so they are available to every workspace and
    // can be toggled or shadowed like any other user skill.
    internal static class SkillImportExportManager
    {
        private const string DialogFilter = "GxPT Skill (*.gxsk)|*.gxsk|Zip Archive (*.zip)|*.zip";

        public static bool ExportSkill(IWin32Window owner, Skill skill)
        {
            if (skill == null)
            {
                try { MessageBox.Show(owner, "No skill selected.", "Export Skill", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                catch { }
                return false;
            }

            using (var sfd = new SaveFileDialog
            {
                Title = "Export Skill",
                Filter = DialogFilter,
                DefaultExt = "gxsk",
                FileName = skill.Slug + ".gxsk",
                OverwritePrompt = true
            })
            {
                if (sfd.ShowDialog(owner) != DialogResult.OK) return false;
                try
                {
                    SkillImportExportService.ExportSkill(skill, sfd.FileName);
                    try { MessageBox.Show(owner, "Export completed.", "Export Skill", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                    catch { }
                    return true;
                }
                catch (Exception ex)
                {
                    try { MessageBox.Show(owner, "Export failed: " + ex.Message, "Export Skill", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    catch { }
                    return false;
                }
            }
        }

        public static bool ImportSkillFromFile(IWin32Window owner, string archivePath)
        {
            string targetRoot = SkillRoots.UserRoot();
            if (string.IsNullOrEmpty(targetRoot))
            {
                try { MessageBox.Show(owner, "Could not resolve the user skills folder.", "Import Skill", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                catch { }
                return false;
            }

            try
            {
                string slug = SkillImportExportService.ImportSkill(archivePath, targetRoot,
                    delegate(string s)
                    {
                        var dr = MessageBox.Show(owner,
                            "A skill named '" + s + "' already exists in your user skills. Replace it?",
                            "Import Skill", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                        return dr == DialogResult.Yes;
                    });
                if (slug == null) return false; // user declined the overwrite

                try { MessageBox.Show(owner, "Imported skill '" + slug + "'.", "Import Skill", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                catch { }
                return true;
            }
            catch (Exception ex)
            {
                try { MessageBox.Show(owner, "Import failed: " + ex.Message, "Import Skill", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                catch { }
                return false;
            }
        }
    }
}
