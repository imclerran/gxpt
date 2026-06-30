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
        // KryptonDataGridView is Krypton's columned data control (KryptonListView
        // can't do a Details/columns view). It themes fully - cells AND column
        // headers - from the active palette. Used read-only, one row per plugin.
        private readonly KryptonDataGridView _list;
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

            _list = new KryptonDataGridView();
            // KryptonDataGridView defaults AutoSize on, which shrinks the control to
            // its content (header + rows). Turn it off so SetBounds + anchors make it
            // fill the dialog like the old ListView did.
            _list.AutoSize = false;
            _list.SetBounds(12, 12, 636, 302);
            _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            // Read-only, full-row single selection, no row-header gutter, and no
            // user editing/adding - it's a display list, not an editable grid.
            _list.ReadOnly = true;
            // Let Krypton paint the column headers from the palette (Sparkle
            // blue-grey) instead of the OS visual style, which otherwise leaves a
            // light-grey header strip that ignores the theme.
            _list.EnableHeadersVisualStyles = false;
            _list.EditMode = DataGridViewEditMode.EditProgrammatically;
            _list.AllowUserToAddRows = false;
            _list.AllowUserToDeleteRows = false;
            _list.AllowUserToResizeRows = false;
            _list.AllowUserToOrderColumns = false;
            _list.RowHeadersVisible = false;
            _list.MultiSelect = false;
            _list.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _list.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            _list.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            AddColumn("Plugin", 200);
            AddColumn("Version", 80);
            AddColumn("State", 90);
            AddColumn("Skills", 70);
            AddColumn("Agents", 70);
            // Let the name column absorb any leftover width so the columns fill the
            // grid horizontally without the user having to drag them.
            _list.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _list.SelectionChanged += new EventHandler(OnSelectionChanged);
            _list.CellMouseDown += new DataGridViewCellMouseEventHandler(OnGridCellMouseDown);
            _list.DoubleClick += new EventHandler(OnDetails); // double-click a row opens its details
            // KryptonDataGridView themes its chrome but leaves cell interiors white;
            // fill cell/header/background/selection colors from the active palette.
            KryptonThemeBridge.StyleDataGrid(_list);

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

            // A KryptonForm themes only its border/caption; its client area is a
            // plain Form surface (system grey). Host the controls on a KryptonPanel
            // docked to fill, so the area around the list takes the palette's panel
            // color (Sparkle blue-grey in dark mode) and blends with the chrome.
            KryptonPanel root = new KryptonPanel();
            // Size the panel to the client area BEFORE adding the anchored children
            // so their Anchor math is computed against the final size. Otherwise the
            // bottom-anchored buttons land off-screen and the grid is mis-sized,
            // because a freshly-created panel starts at its small default size.
            root.Size = this.ClientSize;
            root.Dock = DockStyle.Fill;
            root.Controls.Add(_list);
            root.Controls.Add(install);
            root.Controls.Add(newPlugin);
            root.Controls.Add(_toggle);
            root.Controls.Add(_export);
            root.Controls.Add(_uninstall);
            root.Controls.Add(_details);
            root.Controls.Add(close);
            Controls.Add(root);

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

        // A fixed-width, read-only, non-sortable text column.
        private void AddColumn(string header, int width)
        {
            KryptonDataGridViewTextBoxColumn col = new KryptonDataGridViewTextBoxColumn();
            col.HeaderText = header;
            col.Width = width;
            col.ReadOnly = true;
            col.SortMode = DataGridViewColumnSortMode.NotSortable;
            col.Resizable = DataGridViewTriState.True;
            _list.Columns.Add(col);
        }

        // Reads the registry fresh and repopulates the list, preserving no transient UI state but the
        // current selection's plugin name where possible.
        private void Reload()
        {
            string keep = null;
            PluginManifest sel = Selected();
            if (sel != null) keep = sel.Name;

            _list.SuspendLayout();
            try
            {
                _list.Rows.Clear();
                System.Collections.Generic.IList<PluginManifest> plugins =
                    new PluginRegistry(PluginRoots.UserRoot()).ListInstalled();
                for (int i = 0; i < plugins.Count; i++)
                {
                    PluginManifest m = plugins[i];
                    // Show the plugin's original name (e.g. "TPRM"); the slug is its on-disk identity, not
                    // its label.
                    int idx = _list.Rows.Add(
                        m.Name,
                        m.Version ?? string.Empty,
                        m.Enabled ? "Enabled" : "Disabled",
                        m.Skills.Count.ToString(),
                        m.Agents.Count.ToString());
                    _list.Rows[idx].Tag = m;
                }
            }
            finally { _list.ResumeLayout(); }

            // DataGridView auto-selects the first row; clear that, then restore the
            // prior selection by plugin name if it still exists (matching the old
            // ListView's "no selection unless restored" behavior).
            _list.ClearSelection();
            if (keep != null)
            {
                for (int i = 0; i < _list.Rows.Count; i++)
                {
                    PluginManifest m = _list.Rows[i].Tag as PluginManifest;
                    if (m != null && string.Equals(m.Name, keep, StringComparison.OrdinalIgnoreCase))
                    {
                        _list.Rows[i].Selected = true;
                        break;
                    }
                }
            }
            UpdateButtons();
        }

        private PluginManifest Selected()
        {
            if (_list == null || _list.SelectedRows.Count == 0) return null;
            return _list.SelectedRows[0].Tag as PluginManifest;
        }

        private void UpdateButtons()
        {
            // DataGridView can raise SelectionChanged during construction, before
            // the action buttons exist; ignore until they're wired up.
            if (_toggle == null) return;
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

        // Right-clicking a row selects it so the context menu acts on it. (The grid
        // selects on left-click already, but not on right-click.)
        private void OnGridCellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            if (e.RowIndex < 0 || e.RowIndex >= _list.Rows.Count) return;
            _list.ClearSelection();
            _list.Rows[e.RowIndex].Selected = true;
            try { _list.CurrentCell = _list.Rows[e.RowIndex].Cells[0]; }
            catch { }
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
