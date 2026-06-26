using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // A read-only dialog that lists a plugin's member skills and agents in a list view (Type / Name /
    // Description), styled like the Manage Plugins dialog. The member list is supplied by the caller (read
    // from frontmatter via the service). When a Name or Description is too wide for its column, hovering the
    // cell shows a tooltip with the full text. Built in code, like the app's other small dialogs.
    // XP / .NET 3.5 friendly.
    internal sealed class PluginDetailsForm : Form
    {
        private readonly ListView _list;
        private readonly ToolTip _tip;
        // The cell the tooltip currently tracks, so MouseMove only re-evaluates when the cell changes.
        private int _tipItem = -1;
        private int _tipCol = -1;

        public PluginDetailsForm(string title, IList<PluginMemberInfo> members)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 360);
            MinimumSize = new Size(420, 260);

            _list = new ListView();
            _list.SetBounds(12, 12, 536, 300);
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _list.Columns.Add("Type", 70);
            _list.Columns.Add("Name", 160);
            _list.Columns.Add("Description", 290);
            _list.MouseMove += new MouseEventHandler(OnListMouseMove);
            _list.MouseLeave += new EventHandler(OnListMouseLeave);

            if (members != null)
            {
                for (int i = 0; i < members.Count; i++)
                {
                    PluginMemberInfo m = members[i];
                    ListViewItem lvi = new ListViewItem(Capitalize(m.Kind));
                    lvi.SubItems.Add(m.Name);
                    lvi.SubItems.Add(m.Description);
                    _list.Items.Add(lvi);
                }
            }

            _tip = new ToolTip();
            _tip.AutoPopDelay = 10000;
            _tip.InitialDelay = 400;
            _tip.ReshowDelay = 100;

            Button close = new Button();
            close.Text = "&Close";
            close.SetBounds(472, 322, 76, 26);
            close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            close.DialogResult = DialogResult.OK;

            Controls.Add(_list);
            Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;
        }

        // Shows the full cell text in a tooltip when (and only when) it's wider than its column. Guarded by
        // the tracked cell so the tooltip isn't re-shown (which would flicker) while the cursor stays put.
        private void OnListMouseMove(object sender, MouseEventArgs e)
        {
            ListViewHitTestInfo hit = _list.HitTest(e.Location);
            if (hit == null || hit.Item == null || hit.SubItem == null)
            {
                HideTip();
                return;
            }

            int itemIdx = hit.Item.Index;
            int colIdx = hit.Item.SubItems.IndexOf(hit.SubItem);
            if (itemIdx == _tipItem && colIdx == _tipCol) return; // same cell - leave the tooltip as-is
            _tipItem = itemIdx;
            _tipCol = colIdx;

            string text = hit.SubItem.Text;
            if (string.IsNullOrEmpty(text) || colIdx < 0 || colIdx >= _list.Columns.Count)
            {
                _tip.Hide(_list);
                return;
            }

            // Truncated when the text needs more room than the column allows (a few px for cell padding).
            int colWidth = _list.Columns[colIdx].Width;
            int textWidth = TextRenderer.MeasureText(text, _list.Font).Width;
            if (textWidth + 4 > colWidth)
                _tip.Show(text, _list, e.X + 16, e.Y + 16, _tip.AutoPopDelay);
            else
                _tip.Hide(_list);
        }

        private void OnListMouseLeave(object sender, EventArgs e)
        {
            HideTip();
        }

        private void HideTip()
        {
            if (_tipItem == -1 && _tipCol == -1) return;
            _tipItem = -1;
            _tipCol = -1;
            _tip.Hide(_list);
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

        protected override void Dispose(bool disposing)
        {
            if (disposing && _tip != null) _tip.Dispose();
            base.Dispose(disposing);
        }
    }
}
