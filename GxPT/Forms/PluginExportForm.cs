using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // Modal dialog for authoring a .gxpl: a name/version/description plus a checklist of the user's and
    // project's skills and agents (bundled items are excluded by the caller, mirroring what /export already
    // enforces). A single-item bundle is just one checkbox. Built in code rather than with a Designer file,
    // like the app's other small dialogs. XP / .NET 3.5 friendly.
    internal sealed class PluginExportForm : Form
    {
        private readonly IList<Skill> _skillItems;
        private readonly IList<Agent> _agentItems;
        private readonly TextBox _name;
        private readonly TextBox _version;
        private readonly TextBox _description;
        private readonly CheckedListBox _skills;
        private readonly CheckedListBox _agents;

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

            Label nameLbl = MakeLabel("Name:", 12, 15, 70);
            _name = new TextBox();
            _name.SetBounds(90, 12, 350, 20);

            Label verLbl = MakeLabel("Version:", 12, 43, 70);
            _version = new TextBox();
            _version.SetBounds(90, 40, 120, 20);
            _version.Text = "1.0.0";

            Label descLbl = MakeLabel("Description:", 12, 71, 75);
            _description = new TextBox();
            _description.SetBounds(90, 68, 350, 20);

            Label skillsLbl = MakeLabel("Skills:", 12, 98, 200);
            _skills = MakeCheckList(12, 116, 428, 140);
            for (int i = 0; i < _skillItems.Count; i++)
                _skills.Items.Add(Describe(_skillItems[i].Slug,
                    _skillItems[i].Source == SkillSource.Project, _skillItems[i].Description));

            Label agentsLbl = MakeLabel("Agents:", 12, 262, 200);
            _agents = MakeCheckList(12, 280, 428, 140);
            for (int i = 0; i < _agentItems.Count; i++)
                _agents.Items.Add(Describe(_agentItems[i].Slug,
                    _agentItems[i].Source == AgentSource.Project, _agentItems[i].Description));

            Button ok = new Button();
            ok.Text = "Export...";
            ok.SetBounds(274, 432, 80, 26);
            ok.Click += new EventHandler(OnOk);

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(360, 432, 80, 26);

            Controls.Add(nameLbl); Controls.Add(_name);
            Controls.Add(verLbl); Controls.Add(_version);
            Controls.Add(descLbl); Controls.Add(_description);
            Controls.Add(skillsLbl); Controls.Add(_skills);
            Controls.Add(agentsLbl); Controls.Add(_agents);
            Controls.Add(ok); Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        // Adopt the owner window's title-bar icon (the main form's) once shown with its owner set.
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            PluginImportExportManager.ApplyOwnerIcon(this);
        }

        private static Label MakeLabel(string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(x, y, w, 20);
            return l;
        }

        private static CheckedListBox MakeCheckList(int x, int y, int w, int h)
        {
            CheckedListBox c = new CheckedListBox();
            c.SetBounds(x, y, w, h);
            c.CheckOnClick = true;
            c.IntegralHeight = false;
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
