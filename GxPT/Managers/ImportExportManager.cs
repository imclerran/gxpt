using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace GxPT
{
    internal static class ImportExportManager
    {
        public static bool ExportAll(IWin32Window owner)
        {
            string sourceDir = ImportExportService.GetConversationsFolderPath();
            using (var sfd = new SaveFileDialog
            {
                Title = "Export Conversations",
                Filter = "GxPT Conversation Archive (*.gxcv)|*.gxcv|Zip Archive (*.zip)|*.zip",
                DefaultExt = "gxcv",
                FileName = "GxPT-Conversations-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".gxcv",
                OverwritePrompt = true
            })
            {
                if (sfd.ShowDialog(owner) != DialogResult.OK) return false;
                try
                {
                    ImportExportService.ExportAll(sourceDir, sfd.FileName);
                    try { MessageBox.Show(owner, "Export completed.", "Export Conversations", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                    catch { }
                    return true;
                }
                catch (Exception ex)
                {
                    try { MessageBox.Show(owner, "Export failed: " + ex.Message, "Export Conversations", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    catch { }
                    return false;
                }
            }
        }

        public static bool ImportAll(IWin32Window owner)
        {
            using (var ofd = new OpenFileDialog
            {
                Title = "Import Conversations",
                Filter = "GxPT Conversation Archive (*.gxcv)|*.gxcv|Zip Archive (*.zip)|*.zip",
                CheckFileExists = true,
                Multiselect = false
            })
            {
                if (ofd.ShowDialog(owner) != DialogResult.OK) return false;

                try
                {
                    if (ConversationStore.ListAll().Count > 0)
                    {
                        var dr = MessageBox.Show(owner,
                            "Importing will overwrite existing conversations with the same ID. Continue?",
                            "Import Conversations", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                        if (dr != DialogResult.Yes) return false;
                    }
                }
                catch { }

                string targetDir = ImportExportService.GetConversationsFolderPath();
                try
                {
                    Directory.CreateDirectory(targetDir);
                    ImportExportService.ImportAll(ofd.FileName, targetDir, true);
                    try { MessageBox.Show(owner, "Import completed.", "Import Conversations", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                    catch { }
                    return true;
                }
                catch (Exception ex)
                {
                    try { MessageBox.Show(owner, "Import failed: " + ex.Message, "Import Conversations", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    catch { }
                    return false;
                }
            }
        }

        public static bool ExportSingle(IWin32Window owner, ConversationStore.ConversationListItem info)
        {
            if (info == null)
            {
                try { MessageBox.Show(owner, "No conversation selected.", "Export Conversation", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                catch { }
                return false;
            }
            if (string.IsNullOrEmpty(info.Path) || !File.Exists(info.Path))
            {
                try { MessageBox.Show(owner, "The selected conversation file could not be found on disk.", "Export Conversation", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                catch { }
                return false;
            }

            string displayName = string.IsNullOrEmpty(info.Name) ? (info.Id ?? "Conversation") : info.Name;
            string safeName = ImportExportService.SanitizeFileName(displayName);
            if (string.IsNullOrEmpty(safeName)) safeName = info.Id ?? "Conversation";
            string defaultFile = string.Format("GxPT_{0}_{1}.gxcv", safeName, DateTime.Now.ToString("yyyy-MM-dd-HHmm"));

            using (var sfd = new SaveFileDialog
            {
                Title = "Export Conversation",
                Filter = "GxPT Conversation Archive (*.gxcv)|*.gxcv|Zip Archive (*.zip)|*.zip",
                DefaultExt = "gxcv",
                FileName = defaultFile,
                OverwritePrompt = true
            })
            {
                if (sfd.ShowDialog(owner) != DialogResult.OK) return false;
                try
                {
                    ImportExportService.ExportSingle(info.Path, sfd.FileName, info.Id);
                    try { MessageBox.Show(owner, "Export completed.", "Export Conversation", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                    catch { }
                    return true;
                }
                catch (Exception ex)
                {
                    try { MessageBox.Show(owner, "Export failed: " + ex.Message, "Export Conversation", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    catch { }
                    return false;
                }
            }
        }
    }
}
