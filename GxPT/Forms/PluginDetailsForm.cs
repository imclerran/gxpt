using System;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // A small read-only dialog that shows a plugin's member skills and agents (names + descriptions). The
    // body text is built by the caller; this form just presents it in a scrollable, word-wrapped read-only
    // box. Built in code, like the app's other small dialogs. XP / .NET 3.5 friendly.
    internal sealed class PluginDetailsForm : Form
    {
        public PluginDetailsForm(string title, string body)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(460, 360);
            MinimumSize = new Size(360, 240);

            TextBox text = new TextBox();
            text.SetBounds(12, 12, 436, 300);
            text.Multiline = true;
            text.ReadOnly = true;
            text.WordWrap = true;
            text.ScrollBars = ScrollBars.Vertical;
            text.BackColor = SystemColors.Window;
            text.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            text.Text = body ?? string.Empty;
            text.Select(0, 0); // caret at the top, nothing highlighted

            Button close = new Button();
            close.Text = "&Close";
            close.SetBounds(372, 322, 76, 26);
            close.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            close.DialogResult = DialogResult.OK;

            Controls.Add(text);
            Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;
        }

        // Adopt the owner window's title-bar icon (the main form's) once shown with its owner set.
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            PluginImportExportManager.ApplyOwnerIcon(this);
        }
    }
}
