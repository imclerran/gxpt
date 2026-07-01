namespace GxPT
{
    partial class MainForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
            this.components = new System.ComponentModel.Container();
            this.msMain = new System.Windows.Forms.MenuStrip();
            this.miFile = new System.Windows.Forms.ToolStripMenuItem();
            this.miSettings = new System.Windows.Forms.ToolStripMenuItem();
            this.toolStripSeparator2 = new System.Windows.Forms.ToolStripSeparator();
            this.miNewConversation = new System.Windows.Forms.ToolStripMenuItem();
            this.miOpenRecentWorkDir = new System.Windows.Forms.ToolStripMenuItem();
            this.miCloseConversation = new System.Windows.Forms.ToolStripMenuItem();
            this.toolStripSeparator4 = new System.Windows.Forms.ToolStripSeparator();
            this.miImport = new System.Windows.Forms.ToolStripMenuItem();
            this.miExport = new System.Windows.Forms.ToolStripMenuItem();
            this.miPluginManage = new System.Windows.Forms.ToolStripMenuItem();
            this.miDeleteConversations = new System.Windows.Forms.ToolStripMenuItem();
            this.toolStripSeparator1 = new System.Windows.Forms.ToolStripSeparator();
            this.miExit = new System.Windows.Forms.ToolStripMenuItem();
            this.miView = new System.Windows.Forms.ToolStripMenuItem();
            this.miConversationHistory = new System.Windows.Forms.ToolStripMenuItem();
            this.toolStripSeparator5 = new System.Windows.Forms.ToolStripSeparator();
            this.miNextTab = new System.Windows.Forms.ToolStripMenuItem();
            this.miPreviousTab = new System.Windows.Forms.ToolStripMenuItem();
            this.toolStripSeparator6 = new System.Windows.Forms.ToolStripSeparator();
            this.miDarkMode = new System.Windows.Forms.ToolStripMenuItem();
            this.miStatusBar = new System.Windows.Forms.ToolStripMenuItem();
            this.miHelp = new System.Windows.Forms.ToolStripMenuItem();
            this.miApiKeysHelp = new System.Windows.Forms.ToolStripMenuItem();
            this.miPrivacyHelp = new System.Windows.Forms.ToolStripMenuItem();
            this.toolStripSeparator3 = new System.Windows.Forms.ToolStripSeparator();
            this.miAbout = new System.Windows.Forms.ToolStripMenuItem();
            this.pnlInput = new System.Windows.Forms.Panel();
            this.txtMessage = new System.Windows.Forms.TextBox();
            this.pnlInputRight = new System.Windows.Forms.Panel();
            this.pnlButtonsFill = new System.Windows.Forms.Panel();
            this.pnlButtons = new System.Windows.Forms.Panel();
            this.btnSend = new Krypton.Toolkit.KryptonButton();
            this.btnAttach = new Krypton.Toolkit.KryptonButton();
            this.pnlModelRow = new System.Windows.Forms.Panel();
            this.cmbModel = new System.Windows.Forms.ComboBox();
            this.chkZdrTab = new Krypton.Toolkit.KryptonCheckBox();
            this.toolTipZdr = new System.Windows.Forms.ToolTip(this.components);
            this.pnlBottom = new System.Windows.Forms.Panel();
            this.pnlApiKeyBanner = new System.Windows.Forms.Panel();
            this.flowLayoutPanel1 = new System.Windows.Forms.FlowLayoutPanel();
            this.lblNoApiKey = new System.Windows.Forms.Label();
            this.lnkOpenSettings = new System.Windows.Forms.LinkLabel();
            this.pnlAttachmentsBanner = new System.Windows.Forms.FlowLayoutPanel();
            this.tabControl1 = new System.Windows.Forms.TabControl();
            this.tabPage1 = new System.Windows.Forms.TabPage();
            this.chatTranscript = new GxPT.ChatTranscriptControl();
            this.splitContainer1 = new System.Windows.Forms.SplitContainer();
            this.ssMain = new System.Windows.Forms.StatusStrip();
            this.tspGenProgress = new System.Windows.Forms.ToolStripProgressBar();
            this.tsiStopGen = new GxPT.StopGenerationItem();
            this.tslTools = new System.Windows.Forms.ToolStripStatusLabel();
            this.tslToolsValue = new System.Windows.Forms.ToolStripStatusLabel();
            this.tslSkills = new System.Windows.Forms.ToolStripStatusLabel();
            this.tslSkillsValue = new System.Windows.Forms.ToolStripStatusLabel();
            this.tslAgents = new System.Windows.Forms.ToolStripStatusLabel();
            this.tslAgentsValue = new System.Windows.Forms.ToolStripStatusLabel();
            this.tslSpring = new System.Windows.Forms.ToolStripStatusLabel();
            this.tslContext = new System.Windows.Forms.ToolStripStatusLabel();
            this.tspContextMeter = new GxPT.ContextMeterItem();
            this.tslContextValue = new System.Windows.Forms.ToolStripStatusLabel();
            this.tslCost = new System.Windows.Forms.ToolStripStatusLabel();
            this.tslCostValue = new System.Windows.Forms.ToolStripStatusLabel();
            this.tslSaved = new System.Windows.Forms.ToolStripStatusLabel();
            this.tslSavedValue = new System.Windows.Forms.ToolStripStatusLabel();
            this.msMain.SuspendLayout();
            this.ssMain.SuspendLayout();
            this.pnlInput.SuspendLayout();
            this.pnlInputRight.SuspendLayout();
            this.pnlButtonsFill.SuspendLayout();
            this.pnlButtons.SuspendLayout();
            this.pnlModelRow.SuspendLayout();
            this.pnlBottom.SuspendLayout();
            this.pnlApiKeyBanner.SuspendLayout();
            this.flowLayoutPanel1.SuspendLayout();
            this.tabControl1.SuspendLayout();
            this.tabPage1.SuspendLayout();
            this.splitContainer1.Panel2.SuspendLayout();
            this.splitContainer1.SuspendLayout();
            this.SuspendLayout();
            // 
            // msMain
            // 
            this.msMain.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.miFile,
            this.miView,
            this.miHelp});
            this.msMain.Location = new System.Drawing.Point(0, 0);
            this.msMain.Name = "msMain";
            this.msMain.Size = new System.Drawing.Size(892, 24);
            this.msMain.TabIndex = 1;
            this.msMain.Text = "menuStrip1";
            // 
            // miFile
            // 
            this.miFile.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.miSettings,
            this.toolStripSeparator2,
            this.miNewConversation,
            this.miOpenRecentWorkDir,
            this.miCloseConversation,
            this.toolStripSeparator4,
            this.miImport,
            this.miExport,
            this.miPluginManage,
            this.miDeleteConversations,
            this.toolStripSeparator1,
            this.miExit});
            this.miFile.Name = "miFile";
            this.miFile.Size = new System.Drawing.Size(37, 20);
            this.miFile.Text = "&File";
            // 
            // miSettings
            // 
            this.miSettings.Name = "miSettings";
            this.miSettings.ShortcutKeyDisplayString = "Ctrl+,";
            this.miSettings.ShortcutKeys = ((System.Windows.Forms.Keys)((System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Oemcomma)));
            this.miSettings.Size = new System.Drawing.Size(221, 22);
            this.miSettings.Text = "&Settings";
            this.miSettings.Click += new System.EventHandler(this.miSettings_Click);
            // 
            // toolStripSeparator2
            // 
            this.toolStripSeparator2.Name = "toolStripSeparator2";
            this.toolStripSeparator2.Size = new System.Drawing.Size(218, 6);
            // 
            // miNewConversation
            // 
            this.miNewConversation.Name = "miNewConversation";
            this.miNewConversation.ShortcutKeys = ((System.Windows.Forms.Keys)((System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.N)));
            this.miNewConversation.Size = new System.Drawing.Size(221, 22);
            this.miNewConversation.Text = "&New Conversation";
            this.miNewConversation.Click += new System.EventHandler(this.miNewConversation_Click);
            // 
            // miOpenRecentWorkDir
            // 
            this.miOpenRecentWorkDir.Name = "miOpenRecentWorkDir";
            this.miOpenRecentWorkDir.Size = new System.Drawing.Size(221, 22);
            this.miOpenRecentWorkDir.Text = "Open Recent &Workspace";
            // 
            // miCloseConversation
            // 
            this.miCloseConversation.Name = "miCloseConversation";
            this.miCloseConversation.ShortcutKeys = ((System.Windows.Forms.Keys)((System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.W)));
            this.miCloseConversation.Size = new System.Drawing.Size(221, 22);
            this.miCloseConversation.Text = "&Close Conversation";
            // 
            // toolStripSeparator4
            // 
            this.toolStripSeparator4.Name = "toolStripSeparator4";
            this.toolStripSeparator4.Size = new System.Drawing.Size(218, 6);
            // 
            // miImport
            // 
            this.miImport.Name = "miImport";
            this.miImport.Size = new System.Drawing.Size(221, 22);
            this.miImport.Text = "&Import";
            this.miImport.Click += new System.EventHandler(this.miImport_Click);
            // 
            // miExport
            // 
            this.miExport.Name = "miExport";
            this.miExport.Size = new System.Drawing.Size(221, 22);
            this.miExport.Text = "&Export";
            this.miExport.Click += new System.EventHandler(this.miExport_Click);
            //
            // miPluginManage
            //
            this.miPluginManage.Name = "miPluginManage";
            this.miPluginManage.Size = new System.Drawing.Size(221, 22);
            this.miPluginManage.Text = "&Plugins Manager...";
            this.miPluginManage.Click += new System.EventHandler(this.miPluginManage_Click);
            //
            // miDeleteConversations
            // 
            this.miDeleteConversations.Name = "miDeleteConversations";
            this.miDeleteConversations.Size = new System.Drawing.Size(221, 22);
            this.miDeleteConversations.Text = "&Delete All Conversations";
            this.miDeleteConversations.Click += new System.EventHandler(this.miDeleteConversations_Click);
            // 
            // toolStripSeparator1
            // 
            this.toolStripSeparator1.Name = "toolStripSeparator1";
            this.toolStripSeparator1.Size = new System.Drawing.Size(218, 6);
            // 
            // miExit
            // 
            this.miExit.Name = "miExit";
            this.miExit.Size = new System.Drawing.Size(221, 22);
            this.miExit.Text = "E&xit";
            this.miExit.Click += new System.EventHandler(this.miExit_Click);
            // 
            // miView
            // 
            this.miView.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.miConversationHistory,
            this.toolStripSeparator5,
            this.miNextTab,
            this.miPreviousTab,
            this.toolStripSeparator6,
            this.miDarkMode,
            this.miStatusBar});
            this.miView.Name = "miView";
            this.miView.Size = new System.Drawing.Size(44, 20);
            this.miView.Text = "&View";
            // 
            // miConversationHistory
            // 
            this.miConversationHistory.Name = "miConversationHistory";
            this.miConversationHistory.ShortcutKeys = ((System.Windows.Forms.Keys)((System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.H)));
            this.miConversationHistory.Size = new System.Drawing.Size(228, 22);
            this.miConversationHistory.Text = "Conversation &History";
            this.miConversationHistory.Click += new System.EventHandler(this.miConversationHistory_Click);
            // 
            // toolStripSeparator5
            // 
            this.toolStripSeparator5.Name = "toolStripSeparator5";
            this.toolStripSeparator5.Size = new System.Drawing.Size(225, 6);
            // 
            // miNextTab
            // 
            this.miNextTab.Name = "miNextTab";
            this.miNextTab.ShortcutKeys = ((System.Windows.Forms.Keys)((System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Tab)));
            this.miNextTab.Size = new System.Drawing.Size(228, 22);
            this.miNextTab.Text = "&Next Tab";
            this.miNextTab.Click += new System.EventHandler(this.miNextTab_Click);
            // 
            // miPreviousTab
            // 
            this.miPreviousTab.Name = "miPreviousTab";
            this.miPreviousTab.ShortcutKeys = ((System.Windows.Forms.Keys)(((System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Shift)
                        | System.Windows.Forms.Keys.Tab)));
            this.miPreviousTab.Size = new System.Drawing.Size(228, 22);
            this.miPreviousTab.Text = "&Previous Tab";
            this.miPreviousTab.Click += new System.EventHandler(this.miPreviousTab_Click);
            // 
            // toolStripSeparator6
            // 
            this.toolStripSeparator6.Name = "toolStripSeparator6";
            this.toolStripSeparator6.Size = new System.Drawing.Size(225, 6);
            // 
            // miDarkMode
            // 
            this.miDarkMode.Name = "miDarkMode";
            this.miDarkMode.ShortcutKeys = ((System.Windows.Forms.Keys)(((System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Shift)
                        | System.Windows.Forms.Keys.D)));
            this.miDarkMode.Size = new System.Drawing.Size(228, 22);
            this.miDarkMode.Text = "&Dark Mode";
            this.miDarkMode.Click += new System.EventHandler(this.miDarkMode_Click);
            //
            // miStatusBar
            //
            this.miStatusBar.Checked = true;
            this.miStatusBar.CheckState = System.Windows.Forms.CheckState.Checked;
            this.miStatusBar.Name = "miStatusBar";
            this.miStatusBar.Size = new System.Drawing.Size(228, 22);
            this.miStatusBar.Text = "&Status Bar";
            this.miStatusBar.Click += new System.EventHandler(this.miStatusBar_Click);
            //
            // miHelp
            // 
            this.miHelp.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.miApiKeysHelp,
            this.miPrivacyHelp,
            this.toolStripSeparator3,
            this.miAbout});
            this.miHelp.Name = "miHelp";
            this.miHelp.Size = new System.Drawing.Size(44, 20);
            this.miHelp.Text = "Help";
            // 
            // miApiKeysHelp
            // 
            this.miApiKeysHelp.Name = "miApiKeysHelp";
            this.miApiKeysHelp.ShortcutKeys = System.Windows.Forms.Keys.F1;
            this.miApiKeysHelp.Size = new System.Drawing.Size(138, 22);
            this.miApiKeysHelp.Text = "API &Keys";
            this.miApiKeysHelp.Click += new System.EventHandler(this.miApiKeysHelp_Click);
            // 
            // miPrivacyHelp
            // 
            this.miPrivacyHelp.Name = "miPrivacyHelp";
            this.miPrivacyHelp.Size = new System.Drawing.Size(138, 22);
            this.miPrivacyHelp.Text = "&Privacy";
            this.miPrivacyHelp.Click += new System.EventHandler(this.miPrivacyHelp_Click);
            // 
            // toolStripSeparator3
            // 
            this.toolStripSeparator3.Name = "toolStripSeparator3";
            this.toolStripSeparator3.Size = new System.Drawing.Size(135, 6);
            // 
            // miAbout
            // 
            this.miAbout.Name = "miAbout";
            this.miAbout.Size = new System.Drawing.Size(138, 22);
            this.miAbout.Text = "&About";
            this.miAbout.Click += new System.EventHandler(this.miAbout_Click);
            // 
            // pnlInput
            // 
            this.pnlInput.AutoSize = true;
            this.pnlInput.Controls.Add(this.txtMessage);
            this.pnlInput.Controls.Add(this.pnlInputRight);
            this.pnlInput.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.pnlInput.Location = new System.Drawing.Point(0, 21);
            this.pnlInput.MinimumSize = new System.Drawing.Size(0, 75);
            this.pnlInput.Name = "pnlInput";
            this.pnlInput.Size = new System.Drawing.Size(885, 75);
            this.pnlInput.TabIndex = 2;
            // 
            // txtMessage
            // 
            this.txtMessage.AcceptsReturn = true;
            this.txtMessage.Dock = System.Windows.Forms.DockStyle.Fill;
            this.txtMessage.Location = new System.Drawing.Point(0, 0);
            this.txtMessage.Margin = new System.Windows.Forms.Padding(0);
            this.txtMessage.MaxLength = 0;
            this.txtMessage.Multiline = true;
            this.txtMessage.Name = "txtMessage";
            this.txtMessage.Size = new System.Drawing.Size(685, 75);
            this.txtMessage.TabIndex = 1;
            this.txtMessage.KeyDown += new System.Windows.Forms.KeyEventHandler(this.txtMessage_KeyDown);
            this.txtMessage.Leave += new System.EventHandler(this.txtMessage_Leave);
            this.txtMessage.Enter += new System.EventHandler(this.txtMessage_Enter);
            // 
            // pnlInputRight
            // 
            this.pnlInputRight.Controls.Add(this.pnlButtonsFill);
            this.pnlInputRight.Controls.Add(this.pnlModelRow);
            this.pnlInputRight.Dock = System.Windows.Forms.DockStyle.Right;
            this.pnlInputRight.Location = new System.Drawing.Point(685, 0);
            this.pnlInputRight.Name = "pnlInputRight";
            this.pnlInputRight.Size = new System.Drawing.Size(200, 75);
            this.pnlInputRight.TabIndex = 3;
            // 
            // pnlButtonsFill
            // 
            this.pnlButtonsFill.Controls.Add(this.pnlButtons);
            this.pnlButtonsFill.Dock = System.Windows.Forms.DockStyle.Fill;
            this.pnlButtonsFill.Location = new System.Drawing.Point(0, 0);
            this.pnlButtonsFill.Margin = new System.Windows.Forms.Padding(0);
            this.pnlButtonsFill.Name = "pnlButtonsFill";
            this.pnlButtonsFill.Size = new System.Drawing.Size(200, 52);
            this.pnlButtonsFill.TabIndex = 6;
            // 
            // pnlButtons
            // 
            this.pnlButtons.Controls.Add(this.btnSend);
            this.pnlButtons.Controls.Add(this.btnAttach);
            this.pnlButtons.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.pnlButtons.Location = new System.Drawing.Point(0, 0);
            this.pnlButtons.Margin = new System.Windows.Forms.Padding(0);
            this.pnlButtons.Name = "pnlButtons";
            this.pnlButtons.Size = new System.Drawing.Size(200, 52);
            this.pnlButtons.TabIndex = 4;
            // 
            // btnSend
            // 
            this.btnSend.Dock = System.Windows.Forms.DockStyle.Fill;
            this.btnSend.Location = new System.Drawing.Point(26, 0);
            this.btnSend.Name = "btnSend";
            this.btnSend.Size = new System.Drawing.Size(174, 52);
            this.btnSend.TabIndex = 0;
            this.btnSend.Text = "Send";
            this.btnSend.Click += new System.EventHandler(this.btnSend_Click);
            // 
            // btnAttach
            // 
            this.btnAttach.Dock = System.Windows.Forms.DockStyle.Left;
            this.btnAttach.Location = new System.Drawing.Point(0, 0);
            this.btnAttach.Name = "btnAttach";
            this.btnAttach.Size = new System.Drawing.Size(26, 52);
            this.btnAttach.TabIndex = 3;
            this.btnAttach.Values.Image = global::GxPT.Properties.Resources.AttatchGrey;
            // 
            // pnlModelRow
            // 
            this.pnlModelRow.Controls.Add(this.cmbModel);
            this.pnlModelRow.Controls.Add(this.chkZdrTab);
            this.pnlModelRow.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.pnlModelRow.Location = new System.Drawing.Point(0, 52);
            this.pnlModelRow.Margin = new System.Windows.Forms.Padding(0);
            this.pnlModelRow.Name = "pnlModelRow";
            this.pnlModelRow.Size = new System.Drawing.Size(200, 23);
            this.pnlModelRow.TabIndex = 5;
            // 
            // cmbModel
            // 
            this.cmbModel.Dock = System.Windows.Forms.DockStyle.Left;
            this.cmbModel.DrawMode = System.Windows.Forms.DrawMode.OwnerDrawFixed;
            this.cmbModel.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbModel.FormattingEnabled = true;
            // Items are populated at runtime by MainForm.PopulateModelsFromSettings (from the user's
            // settings, or ModelDefaults on a fresh install) - not hardcoded here, so the default
            // catalog lives in one place.
            this.cmbModel.Location = new System.Drawing.Point(0, 0);
            this.cmbModel.Margin = new System.Windows.Forms.Padding(0);
            this.cmbModel.Name = "cmbModel";
            this.cmbModel.Size = new System.Drawing.Size(146, 21);
            this.cmbModel.Sorted = true;
            this.cmbModel.TabIndex = 2;
            // 
            // chkZdrTab
            // 
            this.chkZdrTab.Dock = System.Windows.Forms.DockStyle.Right;
            this.chkZdrTab.Location = new System.Drawing.Point(152, 0);
            this.chkZdrTab.Margin = new System.Windows.Forms.Padding(0);
            this.chkZdrTab.Name = "chkZdrTab";
            this.chkZdrTab.Size = new System.Drawing.Size(48, 23);
            this.chkZdrTab.TabIndex = 3;
            this.chkZdrTab.Text = "ZDR";
            this.toolTipZdr.SetToolTip(this.chkZdrTab, "Enable Zero Data Retention for this conversation");
            // 
            // pnlBottom
            // 
            this.pnlBottom.AutoSize = true;
            this.pnlBottom.Controls.Add(this.pnlApiKeyBanner);
            this.pnlBottom.Controls.Add(this.pnlAttachmentsBanner);
            this.pnlBottom.Controls.Add(this.pnlInput);
            this.pnlBottom.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.pnlBottom.Location = new System.Drawing.Point(0, 646);
            this.pnlBottom.Margin = new System.Windows.Forms.Padding(0, 3, 3, 3);
            this.pnlBottom.Name = "pnlBottom";
            this.pnlBottom.Size = new System.Drawing.Size(885, 96);
            this.pnlBottom.TabIndex = 3;
            // 
            // pnlApiKeyBanner
            // 
            this.pnlApiKeyBanner.AutoSize = true;
            this.pnlApiKeyBanner.BackColor = System.Drawing.Color.Gold;
            this.pnlApiKeyBanner.Controls.Add(this.flowLayoutPanel1);
            this.pnlApiKeyBanner.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.pnlApiKeyBanner.Location = new System.Drawing.Point(0, 0);
            this.pnlApiKeyBanner.Margin = new System.Windows.Forms.Padding(0);
            this.pnlApiKeyBanner.Name = "pnlApiKeyBanner";
            this.pnlApiKeyBanner.Padding = new System.Windows.Forms.Padding(6, 4, 6, 4);
            this.pnlApiKeyBanner.Size = new System.Drawing.Size(885, 21);
            this.pnlApiKeyBanner.TabIndex = 1;
            // 
            // flowLayoutPanel1
            // 
            this.flowLayoutPanel1.AutoSize = true;
            this.flowLayoutPanel1.Controls.Add(this.lblNoApiKey);
            this.flowLayoutPanel1.Controls.Add(this.lnkOpenSettings);
            this.flowLayoutPanel1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.flowLayoutPanel1.Location = new System.Drawing.Point(6, 4);
            this.flowLayoutPanel1.Margin = new System.Windows.Forms.Padding(0);
            this.flowLayoutPanel1.Name = "flowLayoutPanel1";
            this.flowLayoutPanel1.Size = new System.Drawing.Size(873, 13);
            this.flowLayoutPanel1.TabIndex = 2;
            // 
            // lblNoApiKey
            // 
            this.lblNoApiKey.AutoSize = true;
            this.lblNoApiKey.Location = new System.Drawing.Point(3, 0);
            this.lblNoApiKey.Name = "lblNoApiKey";
            this.lblNoApiKey.Size = new System.Drawing.Size(114, 13);
            this.lblNoApiKey.TabIndex = 0;
            this.lblNoApiKey.Text = "No API key configured";
            this.lblNoApiKey.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            // 
            // lnkOpenSettings
            // 
            this.lnkOpenSettings.AutoSize = true;
            this.lnkOpenSettings.Location = new System.Drawing.Point(123, 0);
            this.lnkOpenSettings.Name = "lnkOpenSettings";
            this.lnkOpenSettings.Size = new System.Drawing.Size(74, 13);
            this.lnkOpenSettings.TabIndex = 1;
            this.lnkOpenSettings.TabStop = true;
            this.lnkOpenSettings.Text = "Open Settings";
            // 
            // pnlAttachmentsBanner
            // 
            this.pnlAttachmentsBanner.Location = new System.Drawing.Point(0, 0);
            this.pnlAttachmentsBanner.Name = "pnlAttachmentsBanner";
            this.pnlAttachmentsBanner.Size = new System.Drawing.Size(0, 0);
            this.pnlAttachmentsBanner.TabIndex = 2;
            // 
            // tabControl1
            // 
            this.tabControl1.Controls.Add(this.tabPage1);
            this.tabControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tabControl1.Location = new System.Drawing.Point(0, 0);
            this.tabControl1.Name = "tabControl1";
            this.tabControl1.SelectedIndex = 0;
            this.tabControl1.Size = new System.Drawing.Size(885, 646);
            this.tabControl1.TabIndex = 4;
            // 
            // tabPage1
            // 
            this.tabPage1.Controls.Add(this.chatTranscript);
            this.tabPage1.Location = new System.Drawing.Point(4, 22);
            this.tabPage1.Name = "tabPage1";
            this.tabPage1.Size = new System.Drawing.Size(877, 620);
            this.tabPage1.TabIndex = 0;
            this.tabPage1.Text = "New Conversation";
            this.tabPage1.UseVisualStyleBackColor = true;
            // 
            // chatTranscript
            // 
            this.chatTranscript.AccessibleName = "Chat transcript";
            this.chatTranscript.BackColor = System.Drawing.SystemColors.ControlLightLight;
            this.chatTranscript.Dock = System.Windows.Forms.DockStyle.Fill;
            this.chatTranscript.ForeColor = System.Drawing.SystemColors.WindowText;
            this.chatTranscript.Location = new System.Drawing.Point(0, 0);
            this.chatTranscript.Margin = new System.Windows.Forms.Padding(0);
            this.chatTranscript.Name = "chatTranscript";
            this.chatTranscript.Size = new System.Drawing.Size(877, 620);
            this.chatTranscript.TabIndex = 0;
            // 
            // splitContainer1
            // 
            this.splitContainer1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer1.IsSplitterFixed = true;
            this.splitContainer1.Location = new System.Drawing.Point(0, 24);
            this.splitContainer1.Margin = new System.Windows.Forms.Padding(0);
            this.splitContainer1.Name = "splitContainer1";
            this.splitContainer1.Panel1MinSize = 5;
            // 
            // splitContainer1.Panel2
            // 
            this.splitContainer1.Panel2.Controls.Add(this.tabControl1);
            this.splitContainer1.Panel2.Controls.Add(this.pnlBottom);
            this.splitContainer1.Size = new System.Drawing.Size(892, 742);
            this.splitContainer1.SplitterDistance = 6;
            this.splitContainer1.SplitterWidth = 1;
            this.splitContainer1.TabIndex = 1;
            //
            // ssMain
            //
            this.ssMain.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.tspGenProgress,
            this.tsiStopGen,
            this.tslTools,
            this.tslToolsValue,
            this.tslSkills,
            this.tslSkillsValue,
            this.tslAgents,
            this.tslAgentsValue,
            this.tslSpring,
            this.tslContext,
            this.tspContextMeter,
            this.tslContextValue,
            this.tslCost,
            this.tslCostValue,
            this.tslSaved,
            this.tslSavedValue});
            this.ssMain.Location = new System.Drawing.Point(0, 744);
            this.ssMain.Name = "ssMain";
            // Item tooltips are driven manually by StatusStripTooltipFix (the native ones flicker
            // when positioned over the taskbar at the bottom of the screen). Keep this false.
            this.ssMain.ShowItemToolTips = false;
            this.ssMain.Size = new System.Drawing.Size(892, 22);
            this.ssMain.SizingGrip = false;
            this.ssMain.TabIndex = 3;
            //
            // tspGenProgress
            //
            // Shown (with tsiStopGen) only while the active tab has a request in flight; MainForm
            // toggles them and (re)starts the marquee. Visual styles (enabled in Program.cs) are
            // required for the marquee animation; without them it renders static.
            this.tspGenProgress.AutoSize = false;
            this.tspGenProgress.MarqueeAnimationSpeed = 0;
            this.tspGenProgress.Margin = new System.Windows.Forms.Padding(5, 4, 0, 3);
            this.tspGenProgress.Name = "tspGenProgress";
            this.tspGenProgress.Size = new System.Drawing.Size(120, 15);
            this.tspGenProgress.Style = System.Windows.Forms.ProgressBarStyle.Marquee;
            this.tspGenProgress.Visible = false;
            //
            // tsiStopGen
            //
            // Top/bottom margins match tspGenProgress's so the button's 15px height lines up with
            // the bar exactly.
            this.tsiStopGen.Margin = new System.Windows.Forms.Padding(2, 4, 0, 3);
            this.tsiStopGen.Name = "tsiStopGen";
            this.tsiStopGen.ToolTipText = "Stop generating";
            this.tsiStopGen.Visible = false;
            //
            // tslTools
            //
            // The idle face of the left slot (with tslSkills): the active conversation's tool/skill
            // counts, swapped out for the progress bar + stop button while a request is in flight.
            // Caption/value pairs mirror the usage panes on the right; texts are set by
            // MainForm.UpdateToolSkillCounts.
            this.tslTools.Margin = new System.Windows.Forms.Padding(5, 3, 0, 2);
            this.tslTools.Name = "tslTools";
            this.tslTools.Size = new System.Drawing.Size(0, 17);
            this.tslTools.ToolTipText = "MCP tools available to this conversation";
            //
            // tslToolsValue
            //
            this.tslToolsValue.Margin = new System.Windows.Forms.Padding(-2, 3, 0, 2);
            this.tslToolsValue.Name = "tslToolsValue";
            this.tslToolsValue.Size = new System.Drawing.Size(0, 17);
            this.tslToolsValue.ToolTipText = "MCP tools available to this conversation";
            //
            // tslSkills
            //
            this.tslSkills.BorderSides = System.Windows.Forms.ToolStripStatusLabelBorderSides.Left;
            this.tslSkills.Margin = new System.Windows.Forms.Padding(5, 3, 0, 2);
            this.tslSkills.Name = "tslSkills";
            this.tslSkills.Padding = new System.Windows.Forms.Padding(5, 0, 0, 0);
            this.tslSkills.Size = new System.Drawing.Size(0, 17);
            this.tslSkills.ToolTipText = "Skills enabled for this conversation";
            //
            // tslSkillsValue
            //
            this.tslSkillsValue.Margin = new System.Windows.Forms.Padding(-2, 3, 0, 2);
            this.tslSkillsValue.Name = "tslSkillsValue";
            this.tslSkillsValue.Size = new System.Drawing.Size(0, 17);
            this.tslSkillsValue.ToolTipText = "Skills enabled for this conversation";
            //
            // tslAgents
            //
            this.tslAgents.BorderSides = System.Windows.Forms.ToolStripStatusLabelBorderSides.Left;
            this.tslAgents.Margin = new System.Windows.Forms.Padding(5, 3, 0, 2);
            this.tslAgents.Name = "tslAgents";
            this.tslAgents.Padding = new System.Windows.Forms.Padding(5, 0, 0, 0);
            this.tslAgents.Size = new System.Drawing.Size(0, 17);
            this.tslAgents.ToolTipText = "Sub-agents available to this conversation";
            //
            // tslAgentsValue
            //
            this.tslAgentsValue.Margin = new System.Windows.Forms.Padding(-2, 3, 0, 2);
            this.tslAgentsValue.Name = "tslAgentsValue";
            this.tslAgentsValue.Size = new System.Drawing.Size(0, 17);
            this.tslAgentsValue.ToolTipText = "Sub-agents available to this conversation";
            //
            // tslSpring
            //
            this.tslSpring.Name = "tslSpring";
            this.tslSpring.Size = new System.Drawing.Size(700, 17);
            this.tslSpring.Spring = true;
            //
            // tslContext
            //
            this.tslContext.Name = "tslContext";
            this.tslContext.Size = new System.Drawing.Size(0, 17);
            //
            // tspContextMeter
            //
            // Right margin offsets tslContextValue's -2 (which tucks the value against the
            // caption when the meter is hidden) so a 3px gap survives when the meter shows.
            this.tspContextMeter.Margin = new System.Windows.Forms.Padding(5, 4, 5, 3);
            this.tspContextMeter.Name = "tspContextMeter";
            // Width is dictated by the item's block geometry (ContextMeterItem.MeterWidth).
            this.tspContextMeter.Size = new System.Drawing.Size(81, 15);
            this.tspContextMeter.Visible = false;
            //
            // tslContextValue
            //
            this.tslContextValue.Margin = new System.Windows.Forms.Padding(-2, 3, 0, 2);
            this.tslContextValue.Name = "tslContextValue";
            this.tslContextValue.Size = new System.Drawing.Size(0, 17);
            //
            // tslCost
            //
            this.tslCost.BorderSides = System.Windows.Forms.ToolStripStatusLabelBorderSides.Left;
            this.tslCost.Margin = new System.Windows.Forms.Padding(5, 3, 0, 2);
            this.tslCost.Name = "tslCost";
            this.tslCost.Padding = new System.Windows.Forms.Padding(5, 0, 0, 0);
            this.tslCost.Size = new System.Drawing.Size(0, 17);
            //
            // tslCostValue
            //
            this.tslCostValue.Margin = new System.Windows.Forms.Padding(-2, 3, 0, 2);
            this.tslCostValue.Name = "tslCostValue";
            this.tslCostValue.Size = new System.Drawing.Size(0, 17);
            //
            // tslSaved
            //
            this.tslSaved.BorderSides = System.Windows.Forms.ToolStripStatusLabelBorderSides.Left;
            this.tslSaved.Margin = new System.Windows.Forms.Padding(5, 3, 0, 2);
            this.tslSaved.Name = "tslSaved";
            this.tslSaved.Padding = new System.Windows.Forms.Padding(5, 0, 0, 0);
            this.tslSaved.Size = new System.Drawing.Size(0, 17);
            //
            // tslSavedValue
            //
            this.tslSavedValue.Margin = new System.Windows.Forms.Padding(-2, 3, 0, 2);
            this.tslSavedValue.Name = "tslSavedValue";
            this.tslSavedValue.Size = new System.Drawing.Size(0, 17);
            //
            // MainForm
            //
            this.AcceptButton = this.btnSend;
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(892, 766);
            this.Controls.Add(this.splitContainer1);
            this.Controls.Add(this.msMain);
            this.Controls.Add(this.ssMain);
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MainMenuStrip = this.msMain;
            this.Name = "MainForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "GxPT - New Conversation";
            this.msMain.ResumeLayout(false);
            this.msMain.PerformLayout();
            this.ssMain.ResumeLayout(false);
            this.ssMain.PerformLayout();
            this.pnlInput.ResumeLayout(false);
            this.pnlInput.PerformLayout();
            this.pnlInputRight.ResumeLayout(false);
            this.pnlButtonsFill.ResumeLayout(false);
            this.pnlButtons.ResumeLayout(false);
            this.pnlModelRow.ResumeLayout(false);
            this.pnlBottom.ResumeLayout(false);
            this.pnlBottom.PerformLayout();
            this.pnlApiKeyBanner.ResumeLayout(false);
            this.pnlApiKeyBanner.PerformLayout();
            this.flowLayoutPanel1.ResumeLayout(false);
            this.flowLayoutPanel1.PerformLayout();
            this.tabControl1.ResumeLayout(false);
            this.tabPage1.ResumeLayout(false);
            this.splitContainer1.Panel2.ResumeLayout(false);
            this.splitContainer1.Panel2.PerformLayout();
            this.splitContainer1.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private ChatTranscriptControl chatTranscript;
        private System.Windows.Forms.MenuStrip msMain;
        private System.Windows.Forms.ToolStripMenuItem miFile;
        private System.Windows.Forms.ToolStripMenuItem miSettings;
        private System.Windows.Forms.ToolStripSeparator toolStripSeparator1;
        private System.Windows.Forms.ToolStripMenuItem miExit;
        private System.Windows.Forms.Panel pnlInput;
        private Krypton.Toolkit.KryptonButton btnSend;
        private System.Windows.Forms.TextBox txtMessage;
        private System.Windows.Forms.Panel pnlInputRight;
        private System.Windows.Forms.Panel pnlButtonsFill;
        private System.Windows.Forms.Panel pnlModelRow;
        private System.Windows.Forms.ComboBox cmbModel;
        private Krypton.Toolkit.KryptonCheckBox chkZdrTab;
        private System.Windows.Forms.ToolTip toolTipZdr;
        private System.Windows.Forms.Panel pnlBottom;
        private System.Windows.Forms.Panel pnlApiKeyBanner;
        private System.Windows.Forms.Label lblNoApiKey;
        private System.Windows.Forms.LinkLabel lnkOpenSettings;
        private System.Windows.Forms.ToolStripMenuItem miNewConversation;
        private System.Windows.Forms.ToolStripMenuItem miOpenRecentWorkDir;
        private System.Windows.Forms.TabControl tabControl1;
        private System.Windows.Forms.TabPage tabPage1;
        private System.Windows.Forms.ToolStripSeparator toolStripSeparator2;
        private System.Windows.Forms.ToolStripMenuItem miCloseConversation;
        private System.Windows.Forms.SplitContainer splitContainer1;
        private System.Windows.Forms.ToolStripMenuItem miView;
        private System.Windows.Forms.ToolStripMenuItem miConversationHistory;
        private System.Windows.Forms.ToolStripMenuItem miHelp;
        private System.Windows.Forms.ToolStripMenuItem miApiKeysHelp;
        private System.Windows.Forms.ToolStripMenuItem miPrivacyHelp;
        private System.Windows.Forms.FlowLayoutPanel flowLayoutPanel1;
        private System.Windows.Forms.ToolStripSeparator toolStripSeparator3;
        private System.Windows.Forms.ToolStripMenuItem miAbout;
        private System.Windows.Forms.ToolStripSeparator toolStripSeparator4;
        private System.Windows.Forms.ToolStripMenuItem miImport;
        private System.Windows.Forms.ToolStripMenuItem miExport;
        private System.Windows.Forms.ToolStripMenuItem miPluginManage;
        private Krypton.Toolkit.KryptonButton btnAttach;
        private System.Windows.Forms.Panel pnlButtons;
        private System.Windows.Forms.FlowLayoutPanel pnlAttachmentsBanner;
        private System.Windows.Forms.ToolStripMenuItem miDarkMode;
        private System.Windows.Forms.ToolStripMenuItem miStatusBar;
        private System.Windows.Forms.StatusStrip ssMain;
        private System.Windows.Forms.ToolStripProgressBar tspGenProgress;
        private StopGenerationItem tsiStopGen;
        private System.Windows.Forms.ToolStripStatusLabel tslTools;
        private System.Windows.Forms.ToolStripStatusLabel tslToolsValue;
        private System.Windows.Forms.ToolStripStatusLabel tslSkills;
        private System.Windows.Forms.ToolStripStatusLabel tslSkillsValue;
        private System.Windows.Forms.ToolStripStatusLabel tslAgents;
        private System.Windows.Forms.ToolStripStatusLabel tslAgentsValue;
        private System.Windows.Forms.ToolStripStatusLabel tslSpring;
        private System.Windows.Forms.ToolStripStatusLabel tslContext;
        private ContextMeterItem tspContextMeter;
        private System.Windows.Forms.ToolStripStatusLabel tslCost;
        private System.Windows.Forms.ToolStripStatusLabel tslContextValue;
        private System.Windows.Forms.ToolStripStatusLabel tslCostValue;
        private System.Windows.Forms.ToolStripStatusLabel tslSaved;
        private System.Windows.Forms.ToolStripStatusLabel tslSavedValue;
        private System.Windows.Forms.ToolStripMenuItem miDeleteConversations;
        private System.Windows.Forms.ToolStripMenuItem miNextTab;
        private System.Windows.Forms.ToolStripMenuItem miPreviousTab;
        private System.Windows.Forms.ToolStripSeparator toolStripSeparator5;
        private System.Windows.Forms.ToolStripSeparator toolStripSeparator6;
    }
}

