using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace GxPT
{
    // The Manage Plugins dialog (File > Plugins > Manage): a list of installed plugins with per-row
    // Enable/Disable, Export, Uninstall, and Reveal, plus an Install button. State is read live from the
    // plugin registry; the actions delegate to PluginImportExportManager (which reports via MessageBox) and
    // the list reloads after each. Built in code, like the app's other small dialogs. XP / .NET 3.5 friendly.
    internal sealed class PluginManagerForm : Form
    {
        private readonly ListView _list;
        private readonly Button _enable;
        private readonly Button _disable;
        private readonly Button _export;
        private readonly Button _uninstall;
        private readonly Button _reveal;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _miEnable;
        private readonly ToolStripMenuItem _miDisable;
        private readonly ToolStripMenuItem _miExport;
        private readonly ToolStripMenuItem _miUninstall;
        private readonly ToolStripMenuItem _miReveal;

        public PluginManagerForm()
        {
            Text = "Manage Plugins";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(640, 360);
            MinimumSize = new Size(560, 320);

            _list = new ListView();
            _list.SetBounds(12, 12, 616, 280);
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _list.Columns.Add("Plugin", 200);
            _list.Columns.Add("Version", 80);
            _list.Columns.Add("State", 90);
            _list.Columns.Add("Skills", 70);
            _list.Columns.Add("Agents", 70);
            _list.SelectedIndexChanged += new EventHandler(OnSelectionChanged);
            _list.MouseDown += new MouseEventHandler(OnListMouseDown);
            _list.DoubleClick += new EventHandler(OnReveal);

            Button install = new Button();
            install.Text = "&Install...";
            install.SetBounds(12, 300, 90, 26);
            install.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            install.Click += new EventHandler(OnInstall);

            _enable = MakeButton("E&nable", 108, OnEnable);
            _disable = MakeButton("&Disable", 187, OnDisable);
            _export = MakeButton("&Export...", 266, OnExport);
            _uninstall = MakeButton("&Uninstall", 350, OnUninstall);
            _reveal = MakeButton("&Reveal", 434, OnReveal);

            Button close = new Button();
            close.Text = "&Close";
            close.SetBounds(552, 300, 76, 26);
            close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            close.DialogResult = DialogResult.OK;

            _menu = new ContextMenuStrip();
            _miEnable = AddMenuItem("Enable", OnEnable);
            _miDisable = AddMenuItem("Disable", OnDisable);
            _miExport = AddMenuItem("Export...", OnExport);
            _menu.Items.Add(new ToolStripSeparator());
            _miUninstall = AddMenuItem("Uninstall", OnUninstall);
            _miReveal = AddMenuItem("Reveal Files", OnReveal);
            _menu.Opening += new System.ComponentModel.CancelEventHandler(OnMenuOpening);
            _list.ContextMenuStrip = _menu;

            Controls.Add(_list);
            Controls.Add(install);
            Controls.Add(_enable);
            Controls.Add(_disable);
            Controls.Add(_export);
            Controls.Add(_uninstall);
            Controls.Add(_reveal);
            Controls.Add(close);

            AcceptButton = close;
            CancelButton = close;

            Reload();
        }

        private Button MakeButton(string text, int x, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, 300, 78, 26);
            b.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            b.Click += onClick;
            return b;
        }

        private ToolStripMenuItem AddMenuItem(string text, EventHandler onClick)
        {
            ToolStripMenuItem mi = new ToolStripMenuItem(text);
            mi.Click += onClick;
            _menu.Items.Add(mi);
            return mi;
        }

        // Reads the registry fresh and repopulates the list, preserving no transient UI state but the
        // current selection's plugin name where possible.
        private void Reload()
        {
            string keep = null;
            PluginManifest sel = Selected();
            if (sel != null) keep = sel.Name;

            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                System.Collections.Generic.IList<PluginManifest> plugins =
                    new PluginRegistry(PluginRoots.UserRoot()).ListInstalled();
                for (int i = 0; i < plugins.Count; i++)
                {
                    PluginManifest m = plugins[i];
                    ListViewItem lvi = new ListViewItem(SkillSlug.Make(m.Name) ?? m.Name);
                    lvi.SubItems.Add(m.Version ?? string.Empty);
                    lvi.SubItems.Add(m.Enabled ? "Enabled" : "Disabled");
                    lvi.SubItems.Add(m.Skills.Count.ToString());
                    lvi.SubItems.Add(m.Agents.Count.ToString());
                    lvi.Tag = m;
                    if (keep != null && string.Equals(m.Name, keep, StringComparison.OrdinalIgnoreCase))
                        lvi.Selected = true;
                    _list.Items.Add(lvi);
                }
            }
            finally { _list.EndUpdate(); }
            UpdateButtons();
        }

        private PluginManifest Selected()
        {
            if (_list == null || _list.SelectedItems.Count == 0) return null;
            return _list.SelectedItems[0].Tag as PluginManifest;
        }

        private void UpdateButtons()
        {
            PluginManifest m = Selected();
            bool has = m != null;
            _enable.Enabled = has && !m.Enabled;
            _disable.Enabled = has && m.Enabled;
            _export.Enabled = has;
            _uninstall.Enabled = has;
            _reveal.Enabled = has;
        }

        private void OnSelectionChanged(object sender, EventArgs e) { UpdateButtons(); }

        // Right-clicking selects the row under the cursor so the context menu acts on it.
        private void OnListMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            ListViewItem hit = _list.GetItemAt(e.X, e.Y);
            if (hit != null) hit.Selected = true;
        }

        private void OnMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            PluginManifest m = Selected();
            if (m == null) { e.Cancel = true; return; }
            _miEnable.Enabled = !m.Enabled;
            _miDisable.Enabled = m.Enabled;
        }

        // ---- actions ----

        private void OnInstall(object sender, EventArgs e)
        {
            if (PluginImportExportManager.InstallInteractive(this)) Reload();
        }

        private void OnEnable(object sender, EventArgs e)
        {
            PluginManifest m = Selected();
            if (m != null && PluginImportExportManager.SetEnabled(this, m.Name, true)) Reload();
        }

        private void OnDisable(object sender, EventArgs e)
        {
            PluginManifest m = Selected();
            if (m != null && PluginImportExportManager.SetEnabled(this, m.Name, false)) Reload();
        }

        private void OnExport(object sender, EventArgs e)
        {
            PluginManifest m = Selected();
            if (m != null) PluginImportExportManager.ExportInstalled(this, m.Name);
        }

        private void OnUninstall(object sender, EventArgs e)
        {
            PluginManifest m = Selected();
            if (m != null && PluginImportExportManager.Uninstall(this, m.Name)) Reload();
        }

        // Opens the GxPT user data folder (the parent of skills/, agents/, plugins/) in Explorer. A plugin's
        // member files live across those roots, so the data folder is the one always-valid reveal target.
        private void OnReveal(object sender, EventArgs e)
        {
            if (Selected() == null) return;
            try
            {
                string skillsRoot = SkillRoots.UserRoot();
                string dataDir = !string.IsNullOrEmpty(skillsRoot) ? Path.GetDirectoryName(skillsRoot) : null;
                if (!string.IsNullOrEmpty(dataDir) && Directory.Exists(dataDir))
                    System.Diagnostics.Process.Start("explorer.exe", dataDir);
            }
            catch { }
        }
    }
}
