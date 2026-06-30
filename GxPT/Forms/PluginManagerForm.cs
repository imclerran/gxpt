using System;
using System.Drawing;
using System.Windows.Forms;
using Krypton.Toolkit;

namespace GxPT
{
    // The Manage Plugins dialog (File > Plugins Manager): a list of installed plugins with per-row
    // Enable/Disable, Export, Uninstall, and Details, plus global Install (a .gxpl) and New (author a .gxpl
    // from a checklist) buttons - so this one dialog is the whole plugin UI. State is read live from the
    // plugin registry; the actions delegate to PluginImportExportManager (which reports via MessageBox) and
    // the list reloads after each. Built in code, like the app's other small dialogs. XP / .NET 3.5 friendly.
    internal sealed class PluginManagerForm : KryptonForm
    {
        private readonly string _workingDir;
        // A plain ListView: KryptonListView throws NotSupportedException for the
        // Details (columned) view - Krypton only styles icon/list views and
        // points columned lists at KryptonDataGridView. Rather than rewrite this
        // to a grid, keep the ListView and theme its colors to match the palette.
        private readonly ListView _list;
        private readonly KryptonButton _toggle;
        private readonly KryptonButton _export;
        private readonly KryptonButton _uninstall;
        private readonly KryptonButton _details;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _miToggle;
        private readonly ToolStripMenuItem _miExport;
        private readonly ToolStripMenuItem _miUninstall;
        private readonly ToolStripMenuItem _miDetails;

        // workingDir scopes which project skills/agents the New... (authoring) flow offers; may be null.
        public PluginManagerForm(string workingDir)
        {
            _workingDir = workingDir;

            Text = "Manage Plugins";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(660, 360);
            MinimumSize = new Size(620, 300);

            _list = new ListView();
            _list.SetBounds(12, 12, 636, 302);
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.BorderStyle = BorderStyle.FixedSingle;
            _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            // KryptonListView can't do Details view, so colour the plain ListView
            // to match the active palette (dark backdrop in dark mode).
            try
            {
                string theme = AppSettings.GetString("theme");
                bool dark = !string.IsNullOrEmpty(theme) &&
                            theme.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
                ThemeColors tc = ThemeService.GetColors(dark);
                _list.BackColor = tc.UiBackground;
                _list.ForeColor = tc.UiForeground;
            }
            catch { }
            _list.Columns.Add("Plugin", 200);
            _list.Columns.Add("Version", 80);
            _list.Columns.Add("State", 90);
            _list.Columns.Add("Skills", 70);
            _list.Columns.Add("Agents", 70);
            _list.SelectedIndexChanged += new EventHandler(OnSelectionChanged);
            _list.MouseDown += new MouseEventHandler(OnListMouseDown);
            _list.DoubleClick += new EventHandler(OnDetails); // double-click a row opens its details

            // Global actions (no selection needed): install a .gxpl, or author a new one from a checklist.
            // Install is the dialog's primary affordance, so it carries the accent (Custom1); the rest
            // stay neutral.
            KryptonButton install = MakeButton("&Install...", 12, OnInstall);
            install.ButtonStyle = ButtonStyle.Custom1;
            KryptonButton newPlugin = MakeButton("Ne&w...", 90, OnNew);

            // Per-row actions, set off from the global group by a wider gap. The toggle reads "Enable" or
            // "Disable" for the selection (see UpdateButtons).
            _toggle = MakeButton("Disa&ble", 186, OnToggle);
            _export = MakeButton("&Export...", 264, OnExport);
            _uninstall = MakeButton("&Uninstall", 342, OnUninstall);
            _details = MakeButton("De&tails...", 420, OnDetails);

            KryptonButton close = new KryptonButton();
            close.Text = "&Close";
            close.SetBounds(572, 322, 76, 26);
            close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            close.DialogResult = DialogResult.OK;

            _menu = new ContextMenuStrip();
            _miToggle = AddMenuItem("Disable", OnToggle);
            _miDetails = AddMenuItem("Details...", OnDetails);
            _miExport = AddMenuItem("Export...", OnExport);
            _menu.Items.Add(new ToolStripSeparator());
            _miUninstall = AddMenuItem("Uninstall", OnUninstall);
            _menu.Opening += new System.ComponentModel.CancelEventHandler(OnMenuOpening);
            _list.ContextMenuStrip = _menu;

            Controls.Add(_list);
            Controls.Add(install);
            Controls.Add(newPlugin);
            Controls.Add(_toggle);
            Controls.Add(_export);
            Controls.Add(_uninstall);
            Controls.Add(_details);
            Controls.Add(close);

            AcceptButton = close;
            CancelButton = close;

            Reload();
        }

        // Adopt the owner window's title-bar icon (the main form's) once the dialog is shown with its owner set.
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            PluginImportExportManager.ApplyOwnerIcon(this);
        }

        private KryptonButton MakeButton(string text, int x, EventHandler onClick)
        {
            KryptonButton b = new KryptonButton();
            b.Text = text;
            b.SetBounds(x, 322, 76, 26);
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
                    // Show the plugin's original name (e.g. "TPRM"); the slug is its on-disk identity, not
                    // its label.
                    ListViewItem lvi = new ListViewItem(m.Name);
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
            // The toggle reads the action it will perform on the selection: "Disable" an enabled plugin,
            // "Enable" a disabled one. With no selection it's disabled (label left at its prior state).
            if (has) _toggle.Text = m.Enabled ? "Disa&ble" : "E&nable";
            _toggle.Enabled = has;
            _export.Enabled = has;
            _uninstall.Enabled = has;
            _details.Enabled = has;
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
            _miToggle.Text = m.Enabled ? "Disable" : "Enable";
        }

        // ---- actions ----

        private void OnInstall(object sender, EventArgs e)
        {
            if (PluginImportExportManager.InstallInteractive(this)) Reload();
        }

        // Author a new .gxpl from a checklist of skills/agents. Exporting writes a file but doesn't install,
        // so the installed list is unchanged - no reload.
        private void OnNew(object sender, EventArgs e)
        {
            PluginImportExportManager.ExportInteractive(this, _workingDir);
        }

        // Flips the selected plugin's state. Quiet: no success popup - the reloaded State column is the
        // feedback.
        private void OnToggle(object sender, EventArgs e)
        {
            PluginManifest m = Selected();
            if (m != null && PluginImportExportManager.SetEnabled(this, m.Name, !m.Enabled, false)) Reload();
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

        private void OnDetails(object sender, EventArgs e)
        {
            PluginManifest m = Selected();
            if (m != null) PluginImportExportManager.ShowDetails(this, m.Name);
        }
    }
}
