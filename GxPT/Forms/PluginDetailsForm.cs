using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Krypton.Toolkit;

namespace GxPT
{
    // A read-only dialog that lists a plugin's member skills and agents in a grid (Type / Name /
    // Description), styled like the Manage Plugins dialog: KryptonForm chrome, a KryptonPanel client
    // surface, and a KryptonDataGridView themed from the active palette. The member list is supplied by
    // the caller (read from frontmatter via the service). Truncated Name/Description cells show the full
    // text in a hover tooltip - the grid's built-in cell tooltips handle that, replacing the manual
    // ToolTip tracking the old ListView needed. Built in code, like the app's other small dialogs.
    // XP / .NET 3.5 friendly.
    internal sealed class PluginDetailsForm : KryptonForm
    {
        private readonly KryptonDataGridView _list;

        public PluginDetailsForm(string title, IList<PluginMemberInfo> members)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 360);
            MinimumSize = new Size(420, 260);

            _list = new KryptonDataGridView();
            _list.AutoSize = false;
            _list.SetBounds(12, 12, 536, 300);
            _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            // Read-only display list: full-row single selection, no row-header gutter, no editing.
            _list.ReadOnly = true;
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
            _list.ShowCellToolTips = true; // full text on hover when a cell is truncated
            AddColumn("Type", 70);
            AddColumn("Name", 160);
            AddColumn("Description", 290);
            // Description absorbs any leftover width so the columns fill the grid.
            _list.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            // KryptonDataGridView themes its chrome but leaves cell interiors white;
            // fill cell/header/background/selection colors from the active palette.
            KryptonThemeBridge.StyleDataGrid(_list);

            if (members != null)
            {
                for (int i = 0; i < members.Count; i++)
                {
                    PluginMemberInfo m = members[i];
                    _list.Rows.Add(Capitalize(m.Kind), m.Name, m.Description);
                }
            }
            _list.ClearSelection();

            KryptonButton close = new KryptonButton();
            close.Text = "&Close";
            close.SetBounds(472, 322, 76, 26);
            close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            close.DialogResult = DialogResult.OK;

            // A KryptonForm themes only its border/caption; host the controls on a KryptonPanel docked
            // to fill so the client surface takes the palette's panel color. Size the panel to the
            // client area BEFORE adding the anchored children so their Anchor math is computed against
            // the final size (see PluginManagerForm).
            KryptonPanel root = new KryptonPanel();
            root.Size = this.ClientSize;
            root.Dock = DockStyle.Fill;
            root.Controls.Add(_list);
            root.Controls.Add(close);
            Controls.Add(root);

            AcceptButton = close;
            CancelButton = close;
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

        // "skill" -> "Skill", "agent" -> "Agent"; anything else passed through unchanged.
        private static string Capitalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        // Adopt the owner window's title-bar icon (the main form's) once shown with its owner set.
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            PluginImportExportManager.ApplyOwnerIcon(this);
        }
    }
}
