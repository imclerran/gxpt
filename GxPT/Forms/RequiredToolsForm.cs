using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // The required-tools picker shown during plugin export. One group per MCP server (detected from the
    // bundled agents): an Any-of/All choice, a checklist of that server's candidate tools (pre-checked from
    // detection, "(not connected)" flagged), and - when an agent globbed the server - a "require the whole
    // server" option. The author's edits are written back into the supplied seed; Result exposes it for
    // RequiredToolsDetect.ToGroups. Built in code, like the other plugin dialogs. XP / .NET 3.5 friendly.
    internal sealed class RequiredToolsForm : Form
    {
        private sealed class GroupUi
        {
            public ToolGroupSeed Seed;
            public RadioButton Any;
            public RadioButton All;
            public CheckBox WholeServer; // null when the server wasn't globbed
            public CheckedListBox List;  // null when the group has no concrete items
        }

        private readonly IList<ToolGroupSeed> _seed;
        private readonly List<GroupUi> _groups = new List<GroupUi>();

        public IList<ToolGroupSeed> Result { get { return _seed; } }

        public RequiredToolsForm(IList<ToolGroupSeed> seed)
        {
            _seed = seed ?? new List<ToolGroupSeed>();

            Text = "Required Tools";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(520, 470);
            MinimumSize = new Size(440, 320);

            Label intro = new Label();
            intro.Text = "Select the tools this plugin's agents require. The installer is warned when a "
                + "required tool isn't available.";
            intro.SetBounds(12, 10, 496, 32);
            intro.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            Panel scroll = new Panel();
            scroll.SetBounds(12, 46, 496, 376);
            scroll.AutoScroll = true;
            scroll.BorderStyle = BorderStyle.FixedSingle;
            scroll.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            BuildGroups(scroll);

            Button ok = new Button();
            ok.Text = "&OK";
            ok.SetBounds(348, 432, 76, 26);
            ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            ok.Click += new EventHandler(OnOk);

            Button cancel = new Button();
            cancel.Text = "&Cancel";
            cancel.SetBounds(432, 432, 76, 26);
            cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancel.DialogResult = DialogResult.Cancel;

            Controls.Add(intro);
            Controls.Add(scroll);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void BuildGroups(Panel host)
        {
            int width = 460;
            int y = 6;
            for (int i = 0; i < _seed.Count; i++)
            {
                ToolGroupSeed s = _seed[i];
                if (s == null || string.IsNullOrEmpty(s.Server)) continue;

                int rows = s.Items != null ? s.Items.Count : 0;
                int listH = rows > 0 ? Math.Min(rows, 6) * 17 + 6 : 0;
                bool whole = s.OfferWholeServer;
                int panelH = 26 + 24 + (whole ? 24 : 0) + (listH > 0 ? listH + 4 : 0) + 8;

                Panel g = new Panel();
                g.SetBounds(6, y, width, panelH);
                g.BorderStyle = BorderStyle.None;

                Label header = new Label();
                header.Font = new Font(Font, FontStyle.Bold);
                header.Text = s.Server + (s.ServerConnected ? "" : "  (not connected)");
                header.SetBounds(2, 2, width - 8, 18);
                g.Controls.Add(header);

                RadioButton any = new RadioButton();
                any.Text = "Any of";
                any.SetBounds(8, 24, 70, 20);
                any.Checked = s.Mode != RequiredToolMode.All;
                RadioButton all = new RadioButton();
                all.Text = "All";
                all.SetBounds(86, 24, 60, 20);
                all.Checked = s.Mode == RequiredToolMode.All;
                g.Controls.Add(any);
                g.Controls.Add(all);

                GroupUi ui = new GroupUi();
                ui.Seed = s;
                ui.Any = any;
                ui.All = all;

                int cy = 48;
                if (whole)
                {
                    CheckBox ws = new CheckBox();
                    ws.Text = "Require the whole server (any tool)";
                    ws.SetBounds(8, cy, width - 16, 20);
                    ws.Checked = s.WholeServerSelected;
                    g.Controls.Add(ws);
                    ui.WholeServer = ws;
                    cy += 24;
                }

                if (listH > 0)
                {
                    CheckedListBox list = new CheckedListBox();
                    list.SetBounds(8, cy, width - 16, listH);
                    list.CheckOnClick = true;
                    list.IntegralHeight = false;
                    for (int k = 0; k < s.Items.Count; k++)
                    {
                        ToolSeedItem it = s.Items[k];
                        string label = it.Id + (it.Connected ? "" : "  (not connected)");
                        list.Items.Add(label, it.Checked);
                    }
                    g.Controls.Add(list);
                    ui.List = list;

                    // The whole-server option supersedes the per-tool list; grey it out when selected.
                    if (ui.WholeServer != null)
                    {
                        list.Enabled = !ui.WholeServer.Checked;
                        CheckedListBox capturedList = list;
                        CheckBox capturedWs = ui.WholeServer;
                        capturedWs.CheckedChanged += delegate(object sender, EventArgs e)
                        {
                            capturedList.Enabled = !capturedWs.Checked;
                        };
                    }
                }

                _groups.Add(ui);
                host.Controls.Add(g);
                y += panelH + 6;
            }

            if (_groups.Count == 0)
            {
                Label none = new Label();
                none.Text = "The selected agents declare no tools.";
                none.SetBounds(8, 8, width, 20);
                host.Controls.Add(none);
            }
        }

        private void OnOk(object sender, EventArgs e)
        {
            for (int i = 0; i < _groups.Count; i++)
            {
                GroupUi ui = _groups[i];
                ui.Seed.Mode = ui.All.Checked ? RequiredToolMode.All : RequiredToolMode.AnyOf;
                ui.Seed.WholeServerSelected = ui.WholeServer != null && ui.WholeServer.Checked;
                if (ui.List != null && ui.Seed.Items != null)
                {
                    int n = Math.Min(ui.List.Items.Count, ui.Seed.Items.Count);
                    for (int k = 0; k < n; k++)
                        ui.Seed.Items[k].Checked = ui.List.GetItemChecked(k);
                }
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            PluginImportExportManager.ApplyOwnerIcon(this);
        }
    }
}
