using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Krypton.Toolkit;

namespace GxPT
{
    // Modal dialog for authoring a .gxpl: a name/version/description plus a checklist of the user's and
    // project's skills and agents (bundled items are excluded by the caller, mirroring what /export already
    // enforces). A single-item bundle is just one checkbox. Krypton throughout: KryptonForm chrome, a
    // KryptonPanel client surface, and Krypton inputs/labels/checklists themed from the active palette.
    // Built in code rather than with a Designer file, like the app's other small dialogs.
    // XP / .NET 3.5 friendly.
    internal sealed class PluginExportForm : KryptonForm
    {
        private readonly IList<Skill> _skillItems;
        private readonly IList<Agent> _agentItems;
        private readonly KryptonTextBox _name;
        private readonly KryptonTextBox _version;
        private readonly KryptonTextBox _description;
        private readonly KryptonCheckedListBox _skills;
        private readonly KryptonCheckedListBox _agents;

        public string PluginName { get; private set; }
        public string PluginVersion { get; private set; }
        public string PluginDescription { get; private set; }
        public IList<Skill> SelectedSkills { get; private set; }
        public IList<Agent> SelectedAgents { get; private set; }

        public PluginExportForm(IList<Skill> skills, IList<Agent> agents)
        {
            _skillItems = skills ?? new List<Skill>();
            _agentItems = agents ?? new List<Agent>();

            Text = "Export Plugin";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(460, 470);

            KryptonLabel nameLbl = MakeLabel("Name:", 12, 15);
            _name = MakeTextBox(90, 12, 350);

            KryptonLabel verLbl = MakeLabel("Version:", 12, 43);
            _version = MakeTextBox(90, 40, 120);
            _version.Text = "1.0.0";

            KryptonLabel descLbl = MakeLabel("Description:", 12, 71);
            _description = MakeTextBox(90, 68, 350);

            KryptonLabel skillsLbl = MakeLabel("Skills:", 12, 98);
            _skills = MakeCheckList(12, 116, 428, 140);
            for (int i = 0; i < _skillItems.Count; i++)
                _skills.Items.Add(Describe(_skillItems[i].Slug,
                    _skillItems[i].Source == SkillSource.Project, _skillItems[i].Description));

            KryptonLabel agentsLbl = MakeLabel("Agents:", 12, 262);
            _agents = MakeCheckList(12, 280, 428, 140);
            for (int i = 0; i < _agentItems.Count; i++)
                _agents.Items.Add(Describe(_agentItems[i].Slug,
                    _agentItems[i].Source == AgentSource.Project, _agentItems[i].Description));

            KryptonButton ok = new KryptonButton();
            ok.Text = "Export...";
            ok.SetBounds(274, 432, 80, 26);
            ok.Click += new EventHandler(OnOk);

            KryptonButton cancel = new KryptonButton();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(360, 432, 80, 26);

            // A KryptonForm themes only its border/caption; host everything on a KryptonPanel docked to
            // fill so the client surface takes the palette's panel color. Sized to the client area
            // BEFORE the children are added (fixed dialog, no anchors - but keeps the pattern used by
            // the app's other Krypton dialogs).
            KryptonPanel root = new KryptonPanel();
            root.Size = this.ClientSize;
            root.Dock = DockStyle.Fill;
            root.Controls.Add(nameLbl); root.Controls.Add(_name);
            root.Controls.Add(verLbl); root.Controls.Add(_version);
            root.Controls.Add(descLbl); root.Controls.Add(_description);
            root.Controls.Add(skillsLbl); root.Controls.Add(_skills);
            root.Controls.Add(agentsLbl); root.Controls.Add(_agents);
            root.Controls.Add(ok); root.Controls.Add(cancel);
            Controls.Add(root);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        // Adopt the owner window's title-bar icon (the main form's) once shown with its owner set.
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            PluginImportExportManager.ApplyOwnerIcon(this);
        }

        private static KryptonLabel MakeLabel(string text, int x, int y)
        {
            KryptonLabel l = new KryptonLabel();
            l.Text = text;
            l.Location = new Point(x, y);
            return l;
        }

        private static KryptonTextBox MakeTextBox(int x, int y, int w)
        {
            KryptonTextBox t = new KryptonTextBox();
            // Height is driven by the palette font; only the width is meaningful here.
            t.SetBounds(x, y, w, 23);
            return t;
        }

        private static KryptonCheckedListBox MakeCheckList(int x, int y, int w, int h)
        {
            KryptonCheckedListBox c = new KryptonCheckedListBox();
            c.SetBounds(x, y, w, h);
            c.CheckOnClick = true;
            return c;
        }

        private static string Describe(string slug, bool isProject, string description)
        {
            string scope = isProject ? "project" : "user";
            string d = string.IsNullOrEmpty(description) ? string.Empty : " - " + description;
            return slug + " [" + scope + "]" + d;
        }

        private void OnOk(object sender, EventArgs e)
        {
            string name = (_name.Text ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                MessageBox.Show(this, "Enter a plugin name.", "Export Plugin",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            List<Skill> selSkills = new List<Skill>();
            for (int i = 0; i < _skillItems.Count; i++)
                if (_skills.GetItemChecked(i)) selSkills.Add(_skillItems[i]);
            List<Agent> selAgents = new List<Agent>();
            for (int i = 0; i < _agentItems.Count; i++)
                if (_agents.GetItemChecked(i)) selAgents.Add(_agentItems[i]);

            if (selSkills.Count == 0 && selAgents.Count == 0)
            {
                MessageBox.Show(this, "Select at least one skill or agent to include.", "Export Plugin",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            PluginName = name;
            PluginVersion = (_version.Text ?? string.Empty).Trim();
            PluginDescription = (_description.Text ?? string.Empty).Trim();
            SelectedSkills = selSkills;
            SelectedAgents = selAgents;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
