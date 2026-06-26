using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // A read-only dialog that lists a plugin's member skills and agents in a list view (Type / Name /
    // Description), styled like the Manage Plugins dialog. The member list is supplied by the caller (read
    // from frontmatter via the service). Built in code, like the app's other small dialogs.
    // XP / .NET 3.5 friendly.
    internal sealed class PluginDetailsForm : Form
    {
        public PluginDetailsForm(string title, IList<PluginMemberInfo> members)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 360);
            MinimumSize = new Size(420, 260);

            ListView list = new ListView();
            list.SetBounds(12, 12, 536, 300);
            list.View = View.Details;
            list.FullRowSelect = true;
            list.MultiSelect = false;
            list.HideSelection = false;
            list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            list.Columns.Add("Type", 70);
            list.Columns.Add("Name", 160);
            list.Columns.Add("Description", 290);

            if (members != null)
            {
                for (int i = 0; i < members.Count; i++)
                {
                    PluginMemberInfo m = members[i];
                    ListViewItem lvi = new ListViewItem(Capitalize(m.Kind));
                    lvi.SubItems.Add(m.Name);
                    lvi.SubItems.Add(m.Description);
                    list.Items.Add(lvi);
                }
            }

            Button close = new Button();
            close.Text = "&Close";
            close.SetBounds(472, 322, 76, 26);
            close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            close.DialogResult = DialogResult.OK;

            Controls.Add(list);
            Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;
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
