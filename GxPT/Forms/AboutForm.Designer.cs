namespace GxPT
{
    partial class AboutForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(AboutForm));
            this.kpnlRoot = new Krypton.Toolkit.KryptonPanel();
            this.tableLayoutPanel = new System.Windows.Forms.TableLayoutPanel();
            this.logoPictureBox = new System.Windows.Forms.PictureBox();
            this.labelProductName = new Krypton.Toolkit.KryptonLabel();
            this.labelVersion = new Krypton.Toolkit.KryptonLabel();
            this.labelCopyright = new Krypton.Toolkit.KryptonLabel();
            this.labelCompanyName = new Krypton.Toolkit.KryptonLabel();
            this.textBoxDescription = new Krypton.Toolkit.KryptonTextBox();
            this.okButton = new Krypton.Toolkit.KryptonButton();
            this.link3rdPartyLicenses = new Krypton.Toolkit.KryptonLinkLabel();
            this.kpnlRoot.SuspendLayout();
            this.tableLayoutPanel.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.logoPictureBox)).BeginInit();
            this.SuspendLayout();
            //
            // kpnlRoot
            //
            // A KryptonForm themes only its border/caption; this panel gives the client area the
            // palette's panel color. The stock TableLayoutPanel above it is made transparent at
            // runtime (MakeLayoutContainersTransparent in the ctor) so the panel shows through.
            // The 9px inset lives HERE (not as form Padding): a docked panel sits inside form
            // padding, which left a ring of the raw grey form client showing around it.
            this.kpnlRoot.Controls.Add(this.tableLayoutPanel);
            this.kpnlRoot.Dock = System.Windows.Forms.DockStyle.Fill;
            this.kpnlRoot.Padding = new System.Windows.Forms.Padding(9);
            this.kpnlRoot.Name = "kpnlRoot";
            this.kpnlRoot.TabIndex = 0;
            //
            // tableLayoutPanel
            //
            this.tableLayoutPanel.ColumnCount = 2;
            this.tableLayoutPanel.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 33F));
            this.tableLayoutPanel.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 67F));
            this.tableLayoutPanel.Controls.Add(this.logoPictureBox, 0, 0);
            this.tableLayoutPanel.Controls.Add(this.labelProductName, 1, 0);
            this.tableLayoutPanel.Controls.Add(this.labelVersion, 1, 1);
            this.tableLayoutPanel.Controls.Add(this.labelCopyright, 1, 2);
            this.tableLayoutPanel.Controls.Add(this.labelCompanyName, 1, 3);
            this.tableLayoutPanel.Controls.Add(this.textBoxDescription, 1, 4);
            this.tableLayoutPanel.Controls.Add(this.okButton, 1, 6);
            this.tableLayoutPanel.Controls.Add(this.link3rdPartyLicenses, 1, 5);
            this.tableLayoutPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tableLayoutPanel.Location = new System.Drawing.Point(9, 9);
            this.tableLayoutPanel.Name = "tableLayoutPanel";
            this.tableLayoutPanel.RowCount = 7;
            this.tableLayoutPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 10F));
            this.tableLayoutPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 10F));
            this.tableLayoutPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 10F));
            this.tableLayoutPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 10F));
            this.tableLayoutPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 40F));
            this.tableLayoutPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 10F));
            this.tableLayoutPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 10F));
            this.tableLayoutPanel.Size = new System.Drawing.Size(417, 300);
            this.tableLayoutPanel.TabIndex = 0;
            //
            // logoPictureBox
            //
            this.logoPictureBox.BackColor = System.Drawing.Color.Transparent;
            this.logoPictureBox.Dock = System.Windows.Forms.DockStyle.Fill;
            this.logoPictureBox.Image = ((System.Drawing.Image)(resources.GetObject("logoPictureBox.Image")));
            this.logoPictureBox.Location = new System.Drawing.Point(3, 3);
            this.logoPictureBox.Name = "logoPictureBox";
            this.tableLayoutPanel.SetRowSpan(this.logoPictureBox, 7);
            this.logoPictureBox.Size = new System.Drawing.Size(131, 294);
            this.logoPictureBox.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;
            this.logoPictureBox.TabIndex = 12;
            this.logoPictureBox.TabStop = false;
            //
            // labelProductName
            //
            this.labelProductName.AutoSize = false;
            this.labelProductName.Dock = System.Windows.Forms.DockStyle.Fill;
            this.labelProductName.Location = new System.Drawing.Point(143, 0);
            this.labelProductName.Margin = new System.Windows.Forms.Padding(6, 0, 3, 0);
            this.labelProductName.Name = "labelProductName";
            this.labelProductName.Size = new System.Drawing.Size(271, 30);
            this.labelProductName.TabIndex = 19;
            this.labelProductName.Values.Text = "Product Name";
            //
            // labelVersion
            //
            this.labelVersion.AutoSize = false;
            this.labelVersion.Dock = System.Windows.Forms.DockStyle.Fill;
            this.labelVersion.Location = new System.Drawing.Point(143, 30);
            this.labelVersion.Margin = new System.Windows.Forms.Padding(6, 0, 3, 0);
            this.labelVersion.Name = "labelVersion";
            this.labelVersion.Size = new System.Drawing.Size(271, 30);
            this.labelVersion.TabIndex = 0;
            this.labelVersion.Values.Text = "Version";
            //
            // labelCopyright
            //
            this.labelCopyright.AutoSize = false;
            this.labelCopyright.Dock = System.Windows.Forms.DockStyle.Fill;
            this.labelCopyright.Location = new System.Drawing.Point(143, 60);
            this.labelCopyright.Margin = new System.Windows.Forms.Padding(6, 0, 3, 0);
            this.labelCopyright.Name = "labelCopyright";
            this.labelCopyright.Size = new System.Drawing.Size(271, 30);
            this.labelCopyright.TabIndex = 21;
            this.labelCopyright.Values.Text = "Copyright";
            //
            // labelCompanyName
            //
            this.labelCompanyName.AutoSize = false;
            this.labelCompanyName.Dock = System.Windows.Forms.DockStyle.Fill;
            this.labelCompanyName.Location = new System.Drawing.Point(143, 90);
            this.labelCompanyName.Margin = new System.Windows.Forms.Padding(6, 0, 3, 0);
            this.labelCompanyName.Name = "labelCompanyName";
            this.labelCompanyName.Size = new System.Drawing.Size(271, 30);
            this.labelCompanyName.TabIndex = 22;
            this.labelCompanyName.Values.Text = "Company Name";
            //
            // textBoxDescription
            //
            this.textBoxDescription.Dock = System.Windows.Forms.DockStyle.Fill;
            this.textBoxDescription.Location = new System.Drawing.Point(143, 123);
            this.textBoxDescription.Margin = new System.Windows.Forms.Padding(6, 3, 3, 3);
            this.textBoxDescription.Multiline = true;
            this.textBoxDescription.Name = "textBoxDescription";
            this.textBoxDescription.ReadOnly = true;
            this.textBoxDescription.ScrollBars = System.Windows.Forms.ScrollBars.Both;
            this.textBoxDescription.Size = new System.Drawing.Size(271, 114);
            this.textBoxDescription.TabIndex = 23;
            this.textBoxDescription.TabStop = false;
            this.textBoxDescription.Text = resources.GetString("textBoxDescription.Text");
            //
            // okButton
            //
            this.okButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.okButton.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.okButton.Location = new System.Drawing.Point(339, 274);
            this.okButton.Name = "okButton";
            this.okButton.Size = new System.Drawing.Size(75, 23);
            this.okButton.TabIndex = 24;
            this.okButton.Values.Text = "&OK";
            //
            // link3rdPartyLicenses
            //
            this.link3rdPartyLicenses.Dock = System.Windows.Forms.DockStyle.Fill;
            this.link3rdPartyLicenses.Location = new System.Drawing.Point(140, 240);
            this.link3rdPartyLicenses.Name = "link3rdPartyLicenses";
            this.link3rdPartyLicenses.Size = new System.Drawing.Size(274, 30);
            this.link3rdPartyLicenses.TabIndex = 25;
            this.link3rdPartyLicenses.TabStop = true;
            this.link3rdPartyLicenses.Values.Text = "3rd Party Licenses";
            this.link3rdPartyLicenses.MouseClick += new System.Windows.Forms.MouseEventHandler(this.link3rdPartyLicenses_MouseClick);
            //
            // AboutForm
            //
            this.AcceptButton = this.okButton;
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(435, 318);
            this.Controls.Add(this.kpnlRoot);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "AboutForm";
            this.ShowIcon = false;
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "AboutForm";
            this.kpnlRoot.ResumeLayout(false);
            this.tableLayoutPanel.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.logoPictureBox)).EndInit();
            this.ResumeLayout(false);

        }

        #endregion

        private Krypton.Toolkit.KryptonPanel kpnlRoot;
        private System.Windows.Forms.TableLayoutPanel tableLayoutPanel;
        private System.Windows.Forms.PictureBox logoPictureBox;
        private Krypton.Toolkit.KryptonLabel labelProductName;
        private Krypton.Toolkit.KryptonLabel labelVersion;
        private Krypton.Toolkit.KryptonLabel labelCopyright;
        private Krypton.Toolkit.KryptonLabel labelCompanyName;
        private Krypton.Toolkit.KryptonTextBox textBoxDescription;
        private Krypton.Toolkit.KryptonButton okButton;
        private Krypton.Toolkit.KryptonLinkLabel link3rdPartyLicenses;
    }
}
