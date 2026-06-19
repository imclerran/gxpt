namespace GxPT
{
    partial class SettingsForm
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(SettingsForm));
            this.flowLayoutPanel1 = new System.Windows.Forms.FlowLayoutPanel();
            this.btnSave = new System.Windows.Forms.Button();
            this.rtbJson = new System.Windows.Forms.RichTextBox();
            this.tblSettings = new System.Windows.Forms.TableLayoutPanel();
            this.lblApiKey = new System.Windows.Forms.Label();
            this.lblModels = new System.Windows.Forms.Label();
            this.lblDefaultModel = new System.Windows.Forms.Label();
            this.lblTheme = new System.Windows.Forms.Label();
            this.lblTranscriptMaxWidth = new System.Windows.Forms.Label();
            this.lblMessageMaxWidth = new System.Windows.Forms.Label();
            this.lblFontSize = new System.Windows.Forms.Label();
            this.lblEnableLogging = new System.Windows.Forms.Label();
            this.txtApiKey = new System.Windows.Forms.TextBox();
            this.pnlModelsRow = new System.Windows.Forms.Panel();
            this.txtModels = new System.Windows.Forms.TextBox();
            this.pnlModelsRight = new System.Windows.Forms.Panel();
            this.grpRecommended = new System.Windows.Forms.GroupBox();
            this.btnAddRecommended = new System.Windows.Forms.Button();
            this.btnReplaceRecommended = new System.Windows.Forms.Button();
            this.btnUpdateModelInfo = new System.Windows.Forms.Button();
            this.cmbDefaultModel = new System.Windows.Forms.ComboBox();
            this.cmbTheme = new System.Windows.Forms.ComboBox();
            this.nudTranscriptMaxWidth = new System.Windows.Forms.NumericUpDown();
            this.nudMessageMaxWidth = new System.Windows.Forms.NumericUpDown();
            this.nudFontSize = new System.Windows.Forms.NumericUpDown();
            this.chkEnableLogging = new System.Windows.Forms.CheckBox();
            this.lblProviderDataCollection = new System.Windows.Forms.Label();
            this.chkZdr = new System.Windows.Forms.CheckBox();
            this.lblColor = new System.Windows.Forms.Label();
            this.cmbColor = new System.Windows.Forms.ComboBox();
            this.lblMemoryEnabled = new System.Windows.Forms.Label();
            this.chkMemoryEnabled = new System.Windows.Forms.CheckBox();
            this.lblMemoryMaxLines = new System.Windows.Forms.Label();
            this.nudMemoryMaxLines = new System.Windows.Forms.NumericUpDown();
            this.tabControl1 = new System.Windows.Forms.TabControl();
            this.tabVisual = new System.Windows.Forms.TabPage();
            this.tabJson = new System.Windows.Forms.TabPage();
            this.tabMcp = new System.Windows.Forms.TabPage();
            this.tblMcp = new System.Windows.Forms.TableLayoutPanel();
            this.grpMcpWeb = new System.Windows.Forms.GroupBox();
            this.tblMcpWeb = new System.Windows.Forms.TableLayoutPanel();
            this.chkMcpWeb = new System.Windows.Forms.CheckBox();
            this.txtWebSearchKey = new System.Windows.Forms.TextBox();
            this.chkMcpGithub = new System.Windows.Forms.CheckBox();
            this.txtGithubPat = new System.Windows.Forms.TextBox();
            this.grpMcpWorkspace = new System.Windows.Forms.GroupBox();
            this.tblMcpWorkspace = new System.Windows.Forms.TableLayoutPanel();
            this.lblMcpWorkspaceHint = new System.Windows.Forms.Label();
            this.tblMcpKeyless = new System.Windows.Forms.TableLayoutPanel();
            this.chkMcpFiles = new System.Windows.Forms.CheckBox();
            this.chkMcpCommand = new System.Windows.Forms.CheckBox();
            this.chkMcpGit = new System.Windows.Forms.CheckBox();
            this.chkMcpMsBuild = new System.Windows.Forms.CheckBox();
            this.chkMcpCommandScratch = new System.Windows.Forms.CheckBox();
            this.grpMcpCustom = new System.Windows.Forms.GroupBox();
            this.tblMcpCustom = new System.Windows.Forms.TableLayoutPanel();
            this.lblMcpCustom = new System.Windows.Forms.Label();
            this.rtbMcpJson = new System.Windows.Forms.RichTextBox();
            this.flowLayoutPanel1.SuspendLayout();
            this.tblSettings.SuspendLayout();
            this.pnlModelsRow.SuspendLayout();
            this.pnlModelsRight.SuspendLayout();
            this.grpRecommended.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.nudTranscriptMaxWidth)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.nudMessageMaxWidth)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.nudFontSize)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.nudMemoryMaxLines)).BeginInit();
            this.tabControl1.SuspendLayout();
            this.tabVisual.SuspendLayout();
            this.tabJson.SuspendLayout();
            this.tabMcp.SuspendLayout();
            this.tblMcp.SuspendLayout();
            this.grpMcpWeb.SuspendLayout();
            this.tblMcpWeb.SuspendLayout();
            this.grpMcpWorkspace.SuspendLayout();
            this.tblMcpWorkspace.SuspendLayout();
            this.tblMcpKeyless.SuspendLayout();
            this.grpMcpCustom.SuspendLayout();
            this.tblMcpCustom.SuspendLayout();
            this.SuspendLayout();
            // 
            // flowLayoutPanel1
            // 
            this.flowLayoutPanel1.AutoSize = true;
            this.flowLayoutPanel1.Controls.Add(this.btnSave);
            this.flowLayoutPanel1.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.flowLayoutPanel1.FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft;
            this.flowLayoutPanel1.Location = new System.Drawing.Point(0, 437);
            this.flowLayoutPanel1.Name = "flowLayoutPanel1";
            this.flowLayoutPanel1.Size = new System.Drawing.Size(604, 23);
            this.flowLayoutPanel1.TabIndex = 0;
            // 
            // btnSave
            // 
            this.btnSave.Location = new System.Drawing.Point(529, 0);
            this.btnSave.Margin = new System.Windows.Forms.Padding(0);
            this.btnSave.Name = "btnSave";
            this.btnSave.Size = new System.Drawing.Size(75, 23);
            this.btnSave.TabIndex = 0;
            this.btnSave.Text = "Save";
            this.btnSave.UseVisualStyleBackColor = true;
            this.btnSave.Click += new System.EventHandler(this.btnSave_Click);
            // 
            // rtbJson
            // 
            this.rtbJson.AcceptsTab = true;
            this.rtbJson.BorderStyle = System.Windows.Forms.BorderStyle.None;
            this.rtbJson.DetectUrls = false;
            this.rtbJson.Dock = System.Windows.Forms.DockStyle.Fill;
            this.rtbJson.Font = new System.Drawing.Font("Consolas", 9F);
            this.rtbJson.HideSelection = false;
            this.rtbJson.Location = new System.Drawing.Point(3, 3);
            this.rtbJson.Name = "rtbJson";
            this.rtbJson.ScrollBars = System.Windows.Forms.RichTextBoxScrollBars.ForcedBoth;
            this.rtbJson.Size = new System.Drawing.Size(590, 357);
            this.rtbJson.TabIndex = 1;
            this.rtbJson.Text = "";
            this.rtbJson.WordWrap = false;
            // 
            // tblSettings
            // 
            this.tblSettings.ColumnCount = 2;
            this.tblSettings.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
            this.tblSettings.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.tblSettings.Controls.Add(this.lblApiKey, 0, 0);
            this.tblSettings.Controls.Add(this.lblModels, 0, 1);
            this.tblSettings.Controls.Add(this.lblDefaultModel, 0, 2);
            this.tblSettings.Controls.Add(this.lblTheme, 0, 4);
            this.tblSettings.Controls.Add(this.lblTranscriptMaxWidth, 0, 6);
            this.tblSettings.Controls.Add(this.lblMessageMaxWidth, 0, 7);
            this.tblSettings.Controls.Add(this.lblFontSize, 0, 8);
            this.tblSettings.Controls.Add(this.lblEnableLogging, 0, 11);
            this.tblSettings.Controls.Add(this.txtApiKey, 1, 0);
            this.tblSettings.Controls.Add(this.pnlModelsRow, 1, 1);
            this.tblSettings.Controls.Add(this.cmbDefaultModel, 1, 2);
            this.tblSettings.Controls.Add(this.cmbTheme, 1, 4);
            this.tblSettings.Controls.Add(this.nudTranscriptMaxWidth, 1, 6);
            this.tblSettings.Controls.Add(this.nudMessageMaxWidth, 1, 7);
            this.tblSettings.Controls.Add(this.nudFontSize, 1, 8);
            this.tblSettings.Controls.Add(this.chkEnableLogging, 1, 11);
            this.tblSettings.Controls.Add(this.lblProviderDataCollection, 0, 3);
            this.tblSettings.Controls.Add(this.chkZdr, 1, 3);
            this.tblSettings.Controls.Add(this.lblColor, 0, 5);
            this.tblSettings.Controls.Add(this.cmbColor, 1, 5);
            this.tblSettings.Controls.Add(this.lblMemoryEnabled, 0, 9);
            this.tblSettings.Controls.Add(this.chkMemoryEnabled, 1, 9);
            this.tblSettings.Controls.Add(this.lblMemoryMaxLines, 0, 10);
            this.tblSettings.Controls.Add(this.nudMemoryMaxLines, 1, 10);
            this.tblSettings.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tblSettings.Location = new System.Drawing.Point(3, 3);
            this.tblSettings.Name = "tblSettings";
            this.tblSettings.RowCount = 12;
            this.tblSettings.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblSettings.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.tblSettings.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblSettings.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblSettings.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblSettings.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblSettings.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblSettings.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblSettings.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblSettings.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblSettings.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblSettings.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblSettings.Size = new System.Drawing.Size(590, 405);
            this.tblSettings.TabIndex = 2;
            // 
            // lblApiKey
            // 
            this.lblApiKey.AutoSize = true;
            this.lblApiKey.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblApiKey.Location = new System.Drawing.Point(3, 0);
            this.lblApiKey.Name = "lblApiKey";
            this.lblApiKey.Size = new System.Drawing.Size(121, 26);
            this.lblApiKey.TabIndex = 0;
            this.lblApiKey.Text = "OpenRouter API Key";
            this.lblApiKey.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // lblModels
            // 
            this.lblModels.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.lblModels.AutoSize = true;
            this.lblModels.Location = new System.Drawing.Point(83, 32);
            this.lblModels.Margin = new System.Windows.Forms.Padding(3, 6, 3, 0);
            this.lblModels.Name = "lblModels";
            this.lblModels.Size = new System.Drawing.Size(41, 13);
            this.lblModels.TabIndex = 1;
            this.lblModels.Text = "Models";
            // 
            // lblDefaultModel
            // 
            this.lblDefaultModel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblDefaultModel.Location = new System.Drawing.Point(3, 148);
            this.lblDefaultModel.Name = "lblDefaultModel";
            this.lblDefaultModel.Size = new System.Drawing.Size(121, 27);
            this.lblDefaultModel.TabIndex = 2;
            this.lblDefaultModel.Text = "Default Model";
            this.lblDefaultModel.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // lblTheme
            // 
            this.lblTheme.AutoSize = true;
            this.lblTheme.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblTheme.Location = new System.Drawing.Point(3, 198);
            this.lblTheme.Name = "lblTheme";
            this.lblTheme.Size = new System.Drawing.Size(121, 27);
            this.lblTheme.TabIndex = 10;
            this.lblTheme.Text = "Theme";
            this.lblTheme.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // lblTranscriptMaxWidth
            // 
            this.lblTranscriptMaxWidth.AutoSize = true;
            this.lblTranscriptMaxWidth.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblTranscriptMaxWidth.Location = new System.Drawing.Point(3, 252);
            this.lblTranscriptMaxWidth.Name = "lblTranscriptMaxWidth";
            this.lblTranscriptMaxWidth.Size = new System.Drawing.Size(121, 26);
            this.lblTranscriptMaxWidth.TabIndex = 12;
            this.lblTranscriptMaxWidth.Text = "Transcript Max Width";
            this.lblTranscriptMaxWidth.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // lblMessageMaxWidth
            // 
            this.lblMessageMaxWidth.AutoSize = true;
            this.lblMessageMaxWidth.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblMessageMaxWidth.Location = new System.Drawing.Point(3, 278);
            this.lblMessageMaxWidth.Name = "lblMessageMaxWidth";
            this.lblMessageMaxWidth.Size = new System.Drawing.Size(121, 26);
            this.lblMessageMaxWidth.TabIndex = 14;
            this.lblMessageMaxWidth.Text = "Message Max Width (%)";
            this.lblMessageMaxWidth.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // lblFontSize
            // 
            this.lblFontSize.AutoSize = true;
            this.lblFontSize.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblFontSize.Location = new System.Drawing.Point(3, 304);
            this.lblFontSize.Name = "lblFontSize";
            this.lblFontSize.Size = new System.Drawing.Size(121, 26);
            this.lblFontSize.TabIndex = 8;
            this.lblFontSize.Text = "Font Size";
            this.lblFontSize.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // lblEnableLogging
            // 
            this.lblEnableLogging.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblEnableLogging.Location = new System.Drawing.Point(3, 382);
            this.lblEnableLogging.Name = "lblEnableLogging";
            this.lblEnableLogging.Size = new System.Drawing.Size(121, 23);
            this.lblEnableLogging.TabIndex = 3;
            this.lblEnableLogging.Text = "Enable Logging";
            this.lblEnableLogging.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // txtApiKey
            // 
            this.txtApiKey.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
                        | System.Windows.Forms.AnchorStyles.Right)));
            this.txtApiKey.Location = new System.Drawing.Point(127, 3);
            this.txtApiKey.Margin = new System.Windows.Forms.Padding(0, 3, 0, 3);
            this.txtApiKey.Name = "txtApiKey";
            this.txtApiKey.Size = new System.Drawing.Size(463, 20);
            this.txtApiKey.TabIndex = 7;
            // 
            // pnlModelsRow
            // 
            this.pnlModelsRow.Controls.Add(this.txtModels);
            this.pnlModelsRow.Controls.Add(this.pnlModelsRight);
            this.pnlModelsRow.Dock = System.Windows.Forms.DockStyle.Fill;
            this.pnlModelsRow.Location = new System.Drawing.Point(127, 26);
            this.pnlModelsRow.Margin = new System.Windows.Forms.Padding(0);
            this.pnlModelsRow.Name = "pnlModelsRow";
            this.pnlModelsRow.Size = new System.Drawing.Size(463, 122);
            this.pnlModelsRow.TabIndex = 20;
            // 
            // txtModels
            // 
            this.txtModels.AcceptsReturn = true;
            this.txtModels.Dock = System.Windows.Forms.DockStyle.Fill;
            this.txtModels.Location = new System.Drawing.Point(0, 0);
            this.txtModels.Multiline = true;
            this.txtModels.Name = "txtModels";
            this.txtModels.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtModels.Size = new System.Drawing.Size(274, 122);
            this.txtModels.TabIndex = 6;
            // 
            // pnlModelsRight
            // 
            this.pnlModelsRight.Controls.Add(this.grpRecommended);
            this.pnlModelsRight.Controls.Add(this.btnUpdateModelInfo);
            this.pnlModelsRight.Dock = System.Windows.Forms.DockStyle.Right;
            this.pnlModelsRight.Location = new System.Drawing.Point(274, 0);
            this.pnlModelsRight.Name = "pnlModelsRight";
            this.pnlModelsRight.Padding = new System.Windows.Forms.Padding(4, 0, 0, 0);
            this.pnlModelsRight.Size = new System.Drawing.Size(189, 122);
            this.pnlModelsRight.TabIndex = 20;
            // 
            // grpRecommended
            // 
            this.grpRecommended.Controls.Add(this.btnAddRecommended);
            this.grpRecommended.Controls.Add(this.btnReplaceRecommended);
            this.grpRecommended.Dock = System.Windows.Forms.DockStyle.Top;
            this.grpRecommended.Location = new System.Drawing.Point(4, 0);
            this.grpRecommended.Name = "grpRecommended";
            this.grpRecommended.Size = new System.Drawing.Size(185, 96);
            this.grpRecommended.TabIndex = 21;
            this.grpRecommended.TabStop = false;
            this.grpRecommended.Text = "Recommended models";
            // 
            // btnAddRecommended
            // 
            this.btnAddRecommended.Location = new System.Drawing.Point(13, 26);
            this.btnAddRecommended.Name = "btnAddRecommended";
            this.btnAddRecommended.Size = new System.Drawing.Size(150, 26);
            this.btnAddRecommended.TabIndex = 0;
            this.btnAddRecommended.Text = "Add to list";
            this.btnAddRecommended.UseVisualStyleBackColor = true;
            // 
            // btnReplaceRecommended
            // 
            this.btnReplaceRecommended.Location = new System.Drawing.Point(13, 60);
            this.btnReplaceRecommended.Name = "btnReplaceRecommended";
            this.btnReplaceRecommended.Size = new System.Drawing.Size(150, 26);
            this.btnReplaceRecommended.TabIndex = 1;
            this.btnReplaceRecommended.Text = "Replace list...";
            this.btnReplaceRecommended.UseVisualStyleBackColor = true;
            //
            // btnUpdateModelInfo
            //
            this.btnUpdateModelInfo.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.btnUpdateModelInfo.Location = new System.Drawing.Point(4, 99);
            this.btnUpdateModelInfo.Name = "btnUpdateModelInfo";
            this.btnUpdateModelInfo.Size = new System.Drawing.Size(185, 23);
            this.btnUpdateModelInfo.TabIndex = 22;
            this.btnUpdateModelInfo.Text = "Update Model Info";
            this.btnUpdateModelInfo.UseVisualStyleBackColor = true;
            //
            // cmbDefaultModel
            //
            this.cmbDefaultModel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.cmbDefaultModel.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbDefaultModel.DropDownWidth = 175;
            this.cmbDefaultModel.FormattingEnabled = true;
            this.cmbDefaultModel.Location = new System.Drawing.Point(130, 151);
            this.cmbDefaultModel.Name = "cmbDefaultModel";
            this.cmbDefaultModel.Size = new System.Drawing.Size(457, 21);
            this.cmbDefaultModel.TabIndex = 5;
            // 
            // cmbTheme
            // 
            this.cmbTheme.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbTheme.Location = new System.Drawing.Point(130, 201);
            this.cmbTheme.Name = "cmbTheme";
            this.cmbTheme.Size = new System.Drawing.Size(120, 21);
            this.cmbTheme.TabIndex = 11;
            // 
            // nudTranscriptMaxWidth
            // 
            this.nudTranscriptMaxWidth.Increment = new decimal(new int[] {
            50,
            0,
            0,
            0});
            this.nudTranscriptMaxWidth.Location = new System.Drawing.Point(130, 255);
            this.nudTranscriptMaxWidth.Maximum = new decimal(new int[] {
            1900,
            0,
            0,
            0});
            this.nudTranscriptMaxWidth.Minimum = new decimal(new int[] {
            300,
            0,
            0,
            0});
            this.nudTranscriptMaxWidth.Name = "nudTranscriptMaxWidth";
            this.nudTranscriptMaxWidth.Size = new System.Drawing.Size(120, 20);
            this.nudTranscriptMaxWidth.TabIndex = 13;
            this.nudTranscriptMaxWidth.Value = new decimal(new int[] {
            1000,
            0,
            0,
            0});
            // 
            // nudMessageMaxWidth
            // 
            this.nudMessageMaxWidth.Increment = new decimal(new int[] {
            5,
            0,
            0,
            0});
            this.nudMessageMaxWidth.Location = new System.Drawing.Point(130, 281);
            this.nudMessageMaxWidth.Minimum = new decimal(new int[] {
            50,
            0,
            0,
            0});
            this.nudMessageMaxWidth.Name = "nudMessageMaxWidth";
            this.nudMessageMaxWidth.Size = new System.Drawing.Size(120, 20);
            this.nudMessageMaxWidth.TabIndex = 15;
            this.nudMessageMaxWidth.Value = new decimal(new int[] {
            100,
            0,
            0,
            0});
            // 
            // nudFontSize
            // 
            this.nudFontSize.DecimalPlaces = 2;
            this.nudFontSize.Location = new System.Drawing.Point(130, 307);
            this.nudFontSize.Name = "nudFontSize";
            this.nudFontSize.Size = new System.Drawing.Size(120, 20);
            this.nudFontSize.TabIndex = 9;
            // 
            // chkEnableLogging
            // 
            this.chkEnableLogging.AutoSize = true;
            this.chkEnableLogging.Dock = System.Windows.Forms.DockStyle.Fill;
            this.chkEnableLogging.Location = new System.Drawing.Point(130, 388);
            this.chkEnableLogging.Margin = new System.Windows.Forms.Padding(3, 6, 3, 3);
            this.chkEnableLogging.Name = "chkEnableLogging";
            this.chkEnableLogging.Size = new System.Drawing.Size(457, 14);
            this.chkEnableLogging.TabIndex = 4;
            this.chkEnableLogging.UseVisualStyleBackColor = true;
            // 
            // lblProviderDataCollection
            // 
            this.lblProviderDataCollection.AutoSize = true;
            this.lblProviderDataCollection.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblProviderDataCollection.Location = new System.Drawing.Point(3, 175);
            this.lblProviderDataCollection.Name = "lblProviderDataCollection";
            this.lblProviderDataCollection.Size = new System.Drawing.Size(121, 23);
            this.lblProviderDataCollection.TabIndex = 16;
            this.lblProviderDataCollection.Text = "Zero Data Retention";
            this.lblProviderDataCollection.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // chkZdr
            // 
            this.chkZdr.AutoSize = true;
            this.chkZdr.Dock = System.Windows.Forms.DockStyle.Left;
            this.chkZdr.Location = new System.Drawing.Point(130, 178);
            this.chkZdr.Name = "chkZdr";
            this.chkZdr.Size = new System.Drawing.Size(299, 17);
            this.chkZdr.TabIndex = 17;
            this.chkZdr.Text = "Default new conversations to zero-retention providers only";
            this.chkZdr.UseVisualStyleBackColor = true;
            // 
            // lblColor
            // 
            this.lblColor.AutoSize = true;
            this.lblColor.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblColor.Location = new System.Drawing.Point(3, 225);
            this.lblColor.Name = "lblColor";
            this.lblColor.Size = new System.Drawing.Size(121, 27);
            this.lblColor.TabIndex = 18;
            this.lblColor.Text = "Chat Color";
            this.lblColor.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // cmbColor
            // 
            this.cmbColor.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbColor.FormattingEnabled = true;
            this.cmbColor.Location = new System.Drawing.Point(130, 228);
            this.cmbColor.Name = "cmbColor";
            this.cmbColor.Size = new System.Drawing.Size(121, 21);
            this.cmbColor.TabIndex = 19;
            // 
            // lblMemoryEnabled
            // 
            this.lblMemoryEnabled.AutoSize = true;
            this.lblMemoryEnabled.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblMemoryEnabled.Location = new System.Drawing.Point(3, 330);
            this.lblMemoryEnabled.Name = "lblMemoryEnabled";
            this.lblMemoryEnabled.Size = new System.Drawing.Size(121, 26);
            this.lblMemoryEnabled.TabIndex = 20;
            this.lblMemoryEnabled.Text = "Memory";
            this.lblMemoryEnabled.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // chkMemoryEnabled
            // 
            this.chkMemoryEnabled.AutoSize = true;
            this.chkMemoryEnabled.Dock = System.Windows.Forms.DockStyle.Fill;
            this.chkMemoryEnabled.Location = new System.Drawing.Point(130, 336);
            this.chkMemoryEnabled.Margin = new System.Windows.Forms.Padding(3, 6, 3, 3);
            this.chkMemoryEnabled.Name = "chkMemoryEnabled";
            this.chkMemoryEnabled.Size = new System.Drawing.Size(457, 17);
            this.chkMemoryEnabled.TabIndex = 21;
            this.chkMemoryEnabled.Text = "Remember facts about my workspaces (persistent project memory)";
            this.chkMemoryEnabled.UseVisualStyleBackColor = true;
            // 
            // lblMemoryMaxLines
            // 
            this.lblMemoryMaxLines.AutoSize = true;
            this.lblMemoryMaxLines.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblMemoryMaxLines.Location = new System.Drawing.Point(3, 356);
            this.lblMemoryMaxLines.Name = "lblMemoryMaxLines";
            this.lblMemoryMaxLines.Size = new System.Drawing.Size(121, 26);
            this.lblMemoryMaxLines.TabIndex = 22;
            this.lblMemoryMaxLines.Text = "Memory size limit";
            this.lblMemoryMaxLines.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // nudMemoryMaxLines
            // 
            this.nudMemoryMaxLines.Increment = new decimal(new int[] {
            5,
            0,
            0,
            0});
            this.nudMemoryMaxLines.Location = new System.Drawing.Point(130, 359);
            this.nudMemoryMaxLines.Maximum = new decimal(new int[] {
            500,
            0,
            0,
            0});
            this.nudMemoryMaxLines.Minimum = new decimal(new int[] {
            5,
            0,
            0,
            0});
            this.nudMemoryMaxLines.Name = "nudMemoryMaxLines";
            this.nudMemoryMaxLines.Size = new System.Drawing.Size(120, 20);
            this.nudMemoryMaxLines.TabIndex = 23;
            this.nudMemoryMaxLines.Value = new decimal(new int[] {
            40,
            0,
            0,
            0});
            // 
            // tabControl1
            // 
            this.tabControl1.Controls.Add(this.tabVisual);
            this.tabControl1.Controls.Add(this.tabJson);
            this.tabControl1.Controls.Add(this.tabMcp);
            this.tabControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tabControl1.Location = new System.Drawing.Point(0, 0);
            this.tabControl1.Name = "tabControl1";
            this.tabControl1.SelectedIndex = 0;
            this.tabControl1.Size = new System.Drawing.Size(604, 437);
            this.tabControl1.TabIndex = 3;
            // 
            // tabVisual
            // 
            this.tabVisual.Controls.Add(this.tblSettings);
            this.tabVisual.Location = new System.Drawing.Point(4, 22);
            this.tabVisual.Name = "tabVisual";
            this.tabVisual.Padding = new System.Windows.Forms.Padding(3);
            this.tabVisual.Size = new System.Drawing.Size(596, 411);
            this.tabVisual.TabIndex = 0;
            this.tabVisual.Text = "Visual";
            this.tabVisual.UseVisualStyleBackColor = true;
            // 
            // tabJson
            // 
            this.tabJson.Controls.Add(this.rtbJson);
            this.tabJson.Location = new System.Drawing.Point(4, 22);
            this.tabJson.Name = "tabJson";
            this.tabJson.Padding = new System.Windows.Forms.Padding(3);
            this.tabJson.Size = new System.Drawing.Size(596, 363);
            this.tabJson.TabIndex = 1;
            this.tabJson.Text = "JSON";
            this.tabJson.UseVisualStyleBackColor = true;
            // 
            // tabMcp
            // 
            this.tabMcp.Controls.Add(this.tblMcp);
            this.tabMcp.Location = new System.Drawing.Point(4, 22);
            this.tabMcp.Name = "tabMcp";
            this.tabMcp.Padding = new System.Windows.Forms.Padding(3);
            this.tabMcp.Size = new System.Drawing.Size(596, 363);
            this.tabMcp.TabIndex = 2;
            this.tabMcp.Text = "Tools";
            this.tabMcp.UseVisualStyleBackColor = true;
            // 
            // tblMcp
            // 
            this.tblMcp.ColumnCount = 1;
            this.tblMcp.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.tblMcp.Controls.Add(this.grpMcpWeb, 0, 0);
            this.tblMcp.Controls.Add(this.grpMcpWorkspace, 0, 1);
            this.tblMcp.Controls.Add(this.grpMcpCustom, 0, 2);
            this.tblMcp.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tblMcp.Location = new System.Drawing.Point(3, 3);
            this.tblMcp.Name = "tblMcp";
            this.tblMcp.RowCount = 3;
            this.tblMcp.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 86F));
            this.tblMcp.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 100F));
            this.tblMcp.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.tblMcp.Size = new System.Drawing.Size(590, 357);
            this.tblMcp.TabIndex = 0;
            //
            // grpMcpWeb
            //
            this.grpMcpWeb.Controls.Add(this.tblMcpWeb);
            this.grpMcpWeb.Dock = System.Windows.Forms.DockStyle.Fill;
            this.grpMcpWeb.Location = new System.Drawing.Point(3, 3);
            this.grpMcpWeb.Margin = new System.Windows.Forms.Padding(3, 3, 3, 3);
            this.grpMcpWeb.Name = "grpMcpWeb";
            this.grpMcpWeb.Padding = new System.Windows.Forms.Padding(6, 0, 6, 6);
            this.grpMcpWeb.Size = new System.Drawing.Size(584, 80);
            this.grpMcpWeb.TabIndex = 0;
            this.grpMcpWeb.TabStop = false;
            this.grpMcpWeb.Text = "Web && integrations";
            //
            // tblMcpWeb
            //
            this.tblMcpWeb.ColumnCount = 2;
            this.tblMcpWeb.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
            this.tblMcpWeb.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.tblMcpWeb.Controls.Add(this.chkMcpWeb, 0, 0);
            this.tblMcpWeb.Controls.Add(this.txtWebSearchKey, 1, 0);
            this.tblMcpWeb.Controls.Add(this.chkMcpGithub, 0, 1);
            this.tblMcpWeb.Controls.Add(this.txtGithubPat, 1, 1);
            this.tblMcpWeb.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tblMcpWeb.Location = new System.Drawing.Point(6, 13);
            this.tblMcpWeb.Name = "tblMcpWeb";
            this.tblMcpWeb.RowCount = 2;
            this.tblMcpWeb.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblMcpWeb.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblMcpWeb.Size = new System.Drawing.Size(572, 49);
            this.tblMcpWeb.TabIndex = 0;
            //
            // grpMcpWorkspace
            //
            this.grpMcpWorkspace.Controls.Add(this.tblMcpWorkspace);
            this.grpMcpWorkspace.Dock = System.Windows.Forms.DockStyle.Fill;
            this.grpMcpWorkspace.Location = new System.Drawing.Point(3, 77);
            this.grpMcpWorkspace.Margin = new System.Windows.Forms.Padding(3, 3, 3, 3);
            this.grpMcpWorkspace.Name = "grpMcpWorkspace";
            this.grpMcpWorkspace.Padding = new System.Windows.Forms.Padding(6, 0, 6, 6);
            this.grpMcpWorkspace.Size = new System.Drawing.Size(584, 94);
            this.grpMcpWorkspace.TabIndex = 1;
            this.grpMcpWorkspace.TabStop = false;
            this.grpMcpWorkspace.Text = "Workspace tools";
            //
            // tblMcpWorkspace
            //
            this.tblMcpWorkspace.ColumnCount = 1;
            this.tblMcpWorkspace.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.tblMcpWorkspace.Controls.Add(this.lblMcpWorkspaceHint, 0, 0);
            this.tblMcpWorkspace.Controls.Add(this.tblMcpKeyless, 0, 1);
            this.tblMcpWorkspace.Controls.Add(this.chkMcpCommandScratch, 0, 2);
            this.tblMcpWorkspace.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tblMcpWorkspace.Location = new System.Drawing.Point(6, 13);
            this.tblMcpWorkspace.Name = "tblMcpWorkspace";
            this.tblMcpWorkspace.RowCount = 3;
            this.tblMcpWorkspace.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblMcpWorkspace.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblMcpWorkspace.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblMcpWorkspace.Size = new System.Drawing.Size(572, 65);
            this.tblMcpWorkspace.TabIndex = 0;
            //
            // lblMcpWorkspaceHint
            //
            this.lblMcpWorkspaceHint.AutoSize = true;
            this.lblMcpWorkspaceHint.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblMcpWorkspaceHint.Location = new System.Drawing.Point(3, 0);
            this.lblMcpWorkspaceHint.Margin = new System.Windows.Forms.Padding(3, 0, 3, 4);
            this.lblMcpWorkspaceHint.Name = "lblMcpWorkspaceHint";
            this.lblMcpWorkspaceHint.Size = new System.Drawing.Size(317, 13);
            this.lblMcpWorkspaceHint.TabIndex = 0;
            this.lblMcpWorkspaceHint.Text = "These tools act on the conversation\'s workspace folder when one is set.";
            //
            // chkMcpWeb
            // 
            this.chkMcpWeb.Anchor = System.Windows.Forms.AnchorStyles.Left;
            this.chkMcpWeb.AutoSize = true;
            this.chkMcpWeb.Location = new System.Drawing.Point(3, 4);
            this.chkMcpWeb.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.chkMcpWeb.Name = "chkMcpWeb";
            this.chkMcpWeb.Size = new System.Drawing.Size(84, 17);
            this.chkMcpWeb.TabIndex = 0;
            this.chkMcpWeb.Text = "Web search";
            this.chkMcpWeb.UseVisualStyleBackColor = true;
            // 
            // txtWebSearchKey
            // 
            this.txtWebSearchKey.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right)));
            this.txtWebSearchKey.Location = new System.Drawing.Point(93, 3);
            this.txtWebSearchKey.Name = "txtWebSearchKey";
            this.txtWebSearchKey.Size = new System.Drawing.Size(494, 20);
            this.txtWebSearchKey.TabIndex = 1;
            // 
            // chkMcpGithub
            // 
            this.chkMcpGithub.Anchor = System.Windows.Forms.AnchorStyles.Left;
            this.chkMcpGithub.AutoSize = true;
            this.chkMcpGithub.Location = new System.Drawing.Point(3, 30);
            this.chkMcpGithub.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.chkMcpGithub.Name = "chkMcpGithub";
            this.chkMcpGithub.Size = new System.Drawing.Size(59, 17);
            this.chkMcpGithub.TabIndex = 2;
            this.chkMcpGithub.Text = "GitHub";
            this.chkMcpGithub.UseVisualStyleBackColor = true;
            // 
            // txtGithubPat
            // 
            this.txtGithubPat.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right)));
            this.txtGithubPat.Location = new System.Drawing.Point(93, 29);
            this.txtGithubPat.Name = "txtGithubPat";
            this.txtGithubPat.Size = new System.Drawing.Size(494, 20);
            this.txtGithubPat.TabIndex = 3;
            // 
            // tblMcpKeyless
            // 
            this.tblMcpKeyless.AutoSize = true;
            this.tblMcpKeyless.ColumnCount = 4;
            this.tblMcpKeyless.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
            this.tblMcpKeyless.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
            this.tblMcpKeyless.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
            this.tblMcpKeyless.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
            this.tblMcpKeyless.Controls.Add(this.chkMcpFiles, 0, 0);
            this.tblMcpKeyless.Controls.Add(this.chkMcpCommand, 1, 0);
            this.tblMcpKeyless.Controls.Add(this.chkMcpGit, 2, 0);
            this.tblMcpKeyless.Controls.Add(this.chkMcpMsBuild, 3, 0);
            this.tblMcpKeyless.Location = new System.Drawing.Point(0, 16);
            this.tblMcpKeyless.Margin = new System.Windows.Forms.Padding(0);
            this.tblMcpKeyless.Name = "tblMcpKeyless";
            this.tblMcpKeyless.RowCount = 1;
            this.tblMcpKeyless.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblMcpKeyless.Size = new System.Drawing.Size(258, 25);
            this.tblMcpKeyless.TabIndex = 1;
            // 
            // chkMcpFiles
            // 
            this.chkMcpFiles.Anchor = System.Windows.Forms.AnchorStyles.Left;
            this.chkMcpFiles.AutoSize = true;
            this.chkMcpFiles.Location = new System.Drawing.Point(3, 4);
            this.chkMcpFiles.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.chkMcpFiles.Name = "chkMcpFiles";
            this.chkMcpFiles.Size = new System.Drawing.Size(47, 17);
            this.chkMcpFiles.TabIndex = 4;
            this.chkMcpFiles.Text = "Files";
            this.chkMcpFiles.UseVisualStyleBackColor = true;
            // 
            // chkMcpCommand
            // 
            this.chkMcpCommand.Anchor = System.Windows.Forms.AnchorStyles.Left;
            this.chkMcpCommand.AutoSize = true;
            this.chkMcpCommand.Location = new System.Drawing.Point(56, 4);
            this.chkMcpCommand.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.chkMcpCommand.Name = "chkMcpCommand";
            this.chkMcpCommand.Size = new System.Drawing.Size(73, 17);
            this.chkMcpCommand.TabIndex = 5;
            this.chkMcpCommand.Text = "Command";
            this.chkMcpCommand.UseVisualStyleBackColor = true;
            // 
            // chkMcpGit
            // 
            this.chkMcpGit.Anchor = System.Windows.Forms.AnchorStyles.Left;
            this.chkMcpGit.AutoSize = true;
            this.chkMcpGit.Location = new System.Drawing.Point(135, 4);
            this.chkMcpGit.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.chkMcpGit.Name = "chkMcpGit";
            this.chkMcpGit.Size = new System.Drawing.Size(39, 17);
            this.chkMcpGit.TabIndex = 6;
            this.chkMcpGit.Text = "Git";
            this.chkMcpGit.UseVisualStyleBackColor = true;
            // 
            // chkMcpMsBuild
            // 
            this.chkMcpMsBuild.Anchor = System.Windows.Forms.AnchorStyles.Left;
            this.chkMcpMsBuild.AutoSize = true;
            this.chkMcpMsBuild.Location = new System.Drawing.Point(180, 4);
            this.chkMcpMsBuild.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.chkMcpMsBuild.Name = "chkMcpMsBuild";
            this.chkMcpMsBuild.Size = new System.Drawing.Size(65, 17);
            this.chkMcpMsBuild.TabIndex = 7;
            this.chkMcpMsBuild.Text = "MSBuild";
            this.chkMcpMsBuild.UseVisualStyleBackColor = true;
            //
            // chkMcpCommandScratch
            //
            this.chkMcpCommandScratch.Anchor = System.Windows.Forms.AnchorStyles.Left;
            this.chkMcpCommandScratch.AutoSize = true;
            this.chkMcpCommandScratch.Location = new System.Drawing.Point(3, 45);
            this.chkMcpCommandScratch.Margin = new System.Windows.Forms.Padding(3, 4, 3, 2);
            this.chkMcpCommandScratch.Name = "chkMcpCommandScratch";
            this.chkMcpCommandScratch.Size = new System.Drawing.Size(355, 17);
            this.chkMcpCommandScratch.TabIndex = 2;
            this.chkMcpCommandScratch.Text = "Run Command in a temporary scratch folder when no workspace is set";
            this.chkMcpCommandScratch.UseVisualStyleBackColor = true;
            //
            // grpMcpCustom
            //
            this.grpMcpCustom.Controls.Add(this.tblMcpCustom);
            this.grpMcpCustom.Dock = System.Windows.Forms.DockStyle.Fill;
            this.grpMcpCustom.Location = new System.Drawing.Point(3, 167);
            this.grpMcpCustom.Margin = new System.Windows.Forms.Padding(3, 3, 3, 3);
            this.grpMcpCustom.Name = "grpMcpCustom";
            this.grpMcpCustom.Padding = new System.Windows.Forms.Padding(6, 0, 6, 6);
            this.grpMcpCustom.Size = new System.Drawing.Size(584, 187);
            this.grpMcpCustom.TabIndex = 2;
            this.grpMcpCustom.TabStop = false;
            this.grpMcpCustom.Text = "Custom servers (mcp.json)";
            //
            // tblMcpCustom
            //
            this.tblMcpCustom.ColumnCount = 1;
            this.tblMcpCustom.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.tblMcpCustom.Controls.Add(this.lblMcpCustom, 0, 0);
            this.tblMcpCustom.Controls.Add(this.rtbMcpJson, 0, 1);
            this.tblMcpCustom.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tblMcpCustom.Location = new System.Drawing.Point(6, 13);
            this.tblMcpCustom.Name = "tblMcpCustom";
            this.tblMcpCustom.RowCount = 2;
            this.tblMcpCustom.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblMcpCustom.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.tblMcpCustom.Size = new System.Drawing.Size(572, 168);
            this.tblMcpCustom.TabIndex = 0;
            //
            // lblMcpCustom
            //
            this.lblMcpCustom.AutoSize = true;
            this.lblMcpCustom.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lblMcpCustom.Location = new System.Drawing.Point(3, 0);
            this.lblMcpCustom.Margin = new System.Windows.Forms.Padding(3, 0, 3, 4);
            this.lblMcpCustom.Name = "lblMcpCustom";
            this.lblMcpCustom.Size = new System.Drawing.Size(323, 13);
            this.lblMcpCustom.TabIndex = 0;
            this.lblMcpCustom.Text = "Add MCP servers by editing this JSON. GitHub is configured above.";
            //
            // rtbMcpJson
            //
            this.rtbMcpJson.AcceptsTab = true;
            this.rtbMcpJson.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.rtbMcpJson.DetectUrls = false;
            this.rtbMcpJson.Dock = System.Windows.Forms.DockStyle.Fill;
            this.rtbMcpJson.Font = new System.Drawing.Font("Consolas", 9F);
            this.rtbMcpJson.HideSelection = false;
            this.rtbMcpJson.Location = new System.Drawing.Point(3, 20);
            this.rtbMcpJson.Name = "rtbMcpJson";
            this.rtbMcpJson.Size = new System.Drawing.Size(566, 145);
            this.rtbMcpJson.TabIndex = 1;
            this.rtbMcpJson.Text = "";
            this.rtbMcpJson.WordWrap = false;
            //
            // SettingsForm
            // 
            this.AcceptButton = this.btnSave;
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(604, 460);
            this.Controls.Add(this.tabControl1);
            this.Controls.Add(this.flowLayoutPanel1);
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MinimumSize = new System.Drawing.Size(620, 498);
            this.Name = "SettingsForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Settings";
            this.flowLayoutPanel1.ResumeLayout(false);
            this.tblSettings.ResumeLayout(false);
            this.tblSettings.PerformLayout();
            this.pnlModelsRow.ResumeLayout(false);
            this.pnlModelsRow.PerformLayout();
            this.pnlModelsRight.ResumeLayout(false);
            this.grpRecommended.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.nudTranscriptMaxWidth)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.nudMessageMaxWidth)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.nudFontSize)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.nudMemoryMaxLines)).EndInit();
            this.tabControl1.ResumeLayout(false);
            this.tabVisual.ResumeLayout(false);
            this.tabJson.ResumeLayout(false);
            this.tabMcp.ResumeLayout(false);
            this.tblMcp.ResumeLayout(false);
            this.grpMcpWeb.ResumeLayout(false);
            this.tblMcpWeb.ResumeLayout(false);
            this.tblMcpWeb.PerformLayout();
            this.grpMcpWorkspace.ResumeLayout(false);
            this.tblMcpWorkspace.ResumeLayout(false);
            this.tblMcpWorkspace.PerformLayout();
            this.tblMcpKeyless.ResumeLayout(false);
            this.tblMcpKeyless.PerformLayout();
            this.grpMcpCustom.ResumeLayout(false);
            this.tblMcpCustom.ResumeLayout(false);
            this.tblMcpCustom.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.FlowLayoutPanel flowLayoutPanel1;
        private System.Windows.Forms.Button btnSave;
        private System.Windows.Forms.RichTextBox rtbJson;
        private System.Windows.Forms.TableLayoutPanel tblSettings;
        private System.Windows.Forms.Label lblApiKey;
        private System.Windows.Forms.Label lblModels;
        private System.Windows.Forms.Label lblDefaultModel;
        private System.Windows.Forms.Label lblEnableLogging;
        private System.Windows.Forms.CheckBox chkEnableLogging;
        private System.Windows.Forms.ComboBox cmbDefaultModel;
        private System.Windows.Forms.TextBox txtModels;
        private System.Windows.Forms.Panel pnlModelsRow;
        private System.Windows.Forms.Panel pnlModelsRight;
        private System.Windows.Forms.GroupBox grpRecommended;
        private System.Windows.Forms.Button btnAddRecommended;
        private System.Windows.Forms.Button btnReplaceRecommended;
        private System.Windows.Forms.Button btnUpdateModelInfo;
        private System.Windows.Forms.TextBox txtApiKey;
        private System.Windows.Forms.TabControl tabControl1;
        private System.Windows.Forms.TabPage tabVisual;
        private System.Windows.Forms.TabPage tabJson;
        private System.Windows.Forms.Label lblFontSize;
        private System.Windows.Forms.NumericUpDown nudFontSize;
        private System.Windows.Forms.Label lblTheme;
        private System.Windows.Forms.ComboBox cmbTheme;
        private System.Windows.Forms.Label lblTranscriptMaxWidth;
        private System.Windows.Forms.NumericUpDown nudTranscriptMaxWidth;
        private System.Windows.Forms.Label lblMessageMaxWidth;
        private System.Windows.Forms.NumericUpDown nudMessageMaxWidth;
        private System.Windows.Forms.Label lblProviderDataCollection;
        private System.Windows.Forms.CheckBox chkZdr;
        private System.Windows.Forms.Label lblMemoryEnabled;
        private System.Windows.Forms.CheckBox chkMemoryEnabled;
        private System.Windows.Forms.Label lblMemoryMaxLines;
        private System.Windows.Forms.NumericUpDown nudMemoryMaxLines;
        private System.Windows.Forms.Label lblColor;
        private System.Windows.Forms.ComboBox cmbColor;
        private System.Windows.Forms.TabPage tabMcp;
        private System.Windows.Forms.TableLayoutPanel tblMcp;
        private System.Windows.Forms.GroupBox grpMcpWeb;
        private System.Windows.Forms.TableLayoutPanel tblMcpWeb;
        private System.Windows.Forms.GroupBox grpMcpWorkspace;
        private System.Windows.Forms.TableLayoutPanel tblMcpWorkspace;
        private System.Windows.Forms.Label lblMcpWorkspaceHint;
        private System.Windows.Forms.TableLayoutPanel tblMcpKeyless;
        private System.Windows.Forms.GroupBox grpMcpCustom;
        private System.Windows.Forms.TableLayoutPanel tblMcpCustom;
        private System.Windows.Forms.CheckBox chkMcpWeb;
        private System.Windows.Forms.TextBox txtWebSearchKey;
        private System.Windows.Forms.CheckBox chkMcpGithub;
        private System.Windows.Forms.TextBox txtGithubPat;
        private System.Windows.Forms.CheckBox chkMcpFiles;
        private System.Windows.Forms.CheckBox chkMcpGit;
        private System.Windows.Forms.CheckBox chkMcpCommand;
        private System.Windows.Forms.CheckBox chkMcpCommandScratch;
        private System.Windows.Forms.CheckBox chkMcpMsBuild;
        private System.Windows.Forms.Label lblMcpCustom;
        private System.Windows.Forms.RichTextBox rtbMcpJson;
    }
}