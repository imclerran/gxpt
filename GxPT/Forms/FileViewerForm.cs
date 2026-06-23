using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace GxPT
{
    public partial class FileViewerForm : Form
    {
        // Created programmatically for image attachments; null for text-only mode.
        private PictureBox _pictureBox;

        public FileViewerForm()
        {
            InitializeComponent();
            ApplyFontSetting();
            ApplyThemeFromSettings();
        }

        // Load an attachment for display, branching on kind:
        //   Image → show PictureBox with the decoded bitmap; hide RichTextBox.
        //   Text/Pdf/unknown → show RichTextBox with syntax-highlighted text (existing path).
        // The `dark` flag drives theming for both modes.
        public void LoadAttachment(AttachedFile af, bool dark)
        {
            if (af == null) return;
            if (af.EffectiveKind == AttachmentKind.Image && !string.IsNullOrEmpty(af.Data))
            {
                ShowImage(af, dark);
            }
            else
            {
                ShowText(af, dark);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            // Dispose the image bitmap when the viewer closes to free GDI handles (XP sensitive).
            DisposeImage();
        }

        private void ShowImage(AttachedFile af, bool dark)
        {
            // Build PictureBox if not yet created.
            if (_pictureBox == null)
            {
                _pictureBox = new PictureBox();
                _pictureBox.Dock = DockStyle.Fill;
                _pictureBox.SizeMode = PictureBoxSizeMode.Zoom;
                _pictureBox.BackColor = dark
                    ? System.Drawing.Color.FromArgb(0x24, 0x27, 0x3A)
                    : System.Drawing.SystemColors.Window;
                this.Controls.Add(_pictureBox);
            }
            else
            {
                _pictureBox.BackColor = dark
                    ? System.Drawing.Color.FromArgb(0x24, 0x27, 0x3A)
                    : System.Drawing.SystemColors.Window;
            }

            // Decode base64 → bitmap. Copy into a standalone Bitmap so the MemoryStream can
            // be closed: Image.FromStream reads lazily and would fault if the stream were gone.
            DisposeImage();
            try
            {
                byte[] bytes = Convert.FromBase64String(af.Data);
                using (var ms = new MemoryStream(bytes))
                using (var tmp = Image.FromStream(ms))
                    _pictureBox.Image = new Bitmap(tmp);
            }
            catch { _pictureBox.Image = null; }

            // Switch visibility: show picture box, hide text box.
            if (this.rtbFileText != null) this.rtbFileText.Visible = false;
            _pictureBox.Visible = true;
            _pictureBox.BringToFront();
        }

        private void ShowText(AttachedFile af, bool dark)
        {
            if (_pictureBox != null) _pictureBox.Visible = false;
            if (this.rtbFileText == null) return;

            this.rtbFileText.Visible = true;
            this.rtbFileText.Text = af.Content ?? string.Empty;

            var colors = ThemeService.GetColors(dark);
            this.rtbFileText.BackColor = colors.UiBackground;
            this.rtbFileText.ForeColor = colors.UiForeground;

            string lang = GetFileExtension(af.FileName);
            try { RichTextBoxSyntaxHighlighter.Highlight(this.rtbFileText, lang, dark); }
            catch { }
        }

        private void DisposeImage()
        {
            if (_pictureBox != null && _pictureBox.Image != null)
            {
                var img = _pictureBox.Image;
                _pictureBox.Image = null;
                img.Dispose();
            }
        }

        private static string GetFileExtension(string fileName)
        {
            try
            {
                if (string.IsNullOrEmpty(fileName)) return null;
                string ext = System.IO.Path.GetExtension(fileName);
                if (string.IsNullOrEmpty(ext)) return null;
                return ext.TrimStart('.').ToLowerInvariant();
            }
            catch { return null; }
        }

        private void ApplyFontSetting()
        {
            try
            {
                double fs = AppSettings.GetDouble("font_size", 0);
                if (fs <= 0) return;
                float size = (float)Math.Max(6, Math.Min(48, fs));
                if (this.rtbFileText != null)
                {
                    this.rtbFileText.Font = new System.Drawing.Font(
                        this.rtbFileText.Font.FontFamily,
                        size,
                        this.rtbFileText.Font.Style);
                }
            }
            catch { }
        }

        private void cmsFileText_Opening(object sender, CancelEventArgs e)
        {
            if (this.mnuCopy != null)
            {
                bool hasSelection = (this.rtbFileText != null) && !string.IsNullOrEmpty(this.rtbFileText.SelectedText);
                this.mnuCopy.Enabled = hasSelection;
            }
        }

        private void mnuCopy_Click(object sender, EventArgs e)
        {
            try
            {
                if (this.rtbFileText != null && !string.IsNullOrEmpty(this.rtbFileText.SelectedText))
                {
                    string sel = this.rtbFileText.SelectedText;
                    sel = sel.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine);
                    Clipboard.SetText(sel, TextDataFormat.UnicodeText);
                }
            }
            catch { }
        }

        private void ApplyThemeFromSettings()
        {
            try
            {
                string theme = null;
                try { theme = AppSettings.GetString("theme"); }
                catch { theme = null; }

                bool dark = !string.IsNullOrEmpty(theme) && theme.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);

                if (this.rtbFileText != null)
                {
                    if (dark)
                    {
                        this.rtbFileText.BackColor = System.Drawing.Color.FromArgb(0x24, 0x27, 0x3A);
                        this.rtbFileText.ForeColor = System.Drawing.Color.FromArgb(230, 230, 230);
                    }
                    else
                    {
                        this.rtbFileText.BackColor = System.Drawing.SystemColors.Window;
                        this.rtbFileText.ForeColor = System.Drawing.SystemColors.WindowText;
                    }
                }
            }
            catch { }
        }
    }
}
