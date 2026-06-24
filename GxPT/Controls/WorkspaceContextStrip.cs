using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace GxPT
{
    // A thin strip docked at the top of each chat tab, above its transcript, showing that
    // conversation's working folder (the MCP files/git/command sandbox root, GXPT_WORKDIR) with
    // links to set / change / clear it (and dismiss the strip when unset).
    //
    // Styling note: this intentionally uses FIXED colors and does NOT follow the app's light/dark
    // theme — it is meant to match the tab strip above it (which is also un-themed system chrome).
    internal sealed class WorkspaceContextStrip : Panel
    {
        // Fixed palette (does not follow the app theme).
        private static readonly Color SetBack = Color.FromArgb(237, 244, 237);   // subtle green-grey
        private static readonly Color UnsetBack = Color.FromArgb(252, 246, 220); // cream / warning
        private static readonly Color SetText = Color.FromArgb(27, 94, 47);      // dark green (matches set bg)
        private static readonly Color UnsetText = Color.FromArgb(120, 80, 20);   // brown (matches unset bg)
        private static readonly Color LinkColor = Color.FromArgb(0, 90, 158);

        // Slightly darker shade of the set text/icon, shown while hovering the (clickable) workspace
        // path; this is a subtle press-affordance, NOT a link — no underline, no link color.
        private static readonly Color SetTextHover = Darken(SetText, 0.80f);

        // Folder glyphs shown at the left of the strip; green when a folder is set, yellow when not.
        // Loaded once and shared across all strip instances (null if the resource is missing).
        private static readonly Image SetIcon = ResourceManager.TryGetAssemblyImage("WorkspaceSet.png");
        private static readonly Image UnsetIcon = ResourceManager.TryGetAssemblyImage("WorkspaceUnset.png");

        // A slightly darkened copy of the set icon, swapped in while hovering (matches the text hover).
        private static readonly Image SetIconHover = DarkenImage(SetIcon, 0.85f);

        private readonly TableLayoutPanel _root;
        private readonly PictureBox _icon;
        private readonly Label _text;
        private readonly FlowLayoutPanel _links;
        private readonly LinkLabel _change;
        private readonly LinkLabel _clear;
        private readonly LinkLabel _dismiss;
        private readonly ToolTip _openTip;

        // The workspace folder currently shown (null/empty when unset); captured so the icon/text
        // click can open it in Explorer.
        private string _dir;

        public event EventHandler ChangeRequested;
        public event EventHandler ClearRequested;
        public event EventHandler DismissRequested;

        public WorkspaceContextStrip()
        {
            this.Dock = DockStyle.Top;
            this.Height = 26;
            this.Padding = new Padding(8, 0, 8, 0);

            _change = MakeLink("Set workspace...", delegate { Raise(ChangeRequested); });
            _clear = MakeLink("Clear", delegate { Raise(ClearRequested); });
            _dismiss = MakeLink("Dismiss", delegate { Raise(DismissRequested); });

            // Links flow left-to-right in add order. The flow panel sizes to its content (both
            // dimensions) and is anchored to the right edge of its cell; with neither Top nor
            // Bottom anchored, the table centers it vertically.
            _links = new FlowLayoutPanel();
            _links.FlowDirection = FlowDirection.LeftToRight;
            _links.WrapContents = false;
            _links.AutoSize = true;
            _links.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _links.Margin = new Padding(0);
            _links.Anchor = AnchorStyles.Right;
            _links.Controls.Add(_change);
            _links.Controls.Add(_clear);
            _links.Controls.Add(_dismiss);

            // Small folder icon at the far left; the image is swapped per state in SetWorkingDir.
            // Anchor=None centers it (both axes) within its auto-sized cell.
            _icon = new PictureBox();
            _icon.Size = new Size(20, 20);
            _icon.SizeMode = PictureBoxSizeMode.Zoom;
            _icon.Margin = new Padding(0);
            _icon.Anchor = AnchorStyles.None;

            _text = new Label();
            _text.AutoSize = false;
            _text.Dock = DockStyle.Fill;
            _text.AutoEllipsis = true;
            _text.TextAlign = ContentAlignment.MiddleLeft;
            // ForeColor is set per state in SetWorkingDir (dark green when set, brown when unset).
            // Small gap between the icon and the text.
            _text.Margin = new Padding(6, 0, 0, 0);

            // When a workspace is set, the icon and path text act as a single click target that opens
            // the folder in Explorer. They darken on hover and show a pointer cursor + tooltip, but are
            // deliberately NOT styled as a link (no underline, no link color). The handlers are always
            // attached and simply no-op while no workspace is set (guarded by _dir).
            _openTip = new ToolTip();
            _icon.Click += OnOpenWorkspaceClicked;
            _text.Click += OnOpenWorkspaceClicked;
            _icon.MouseEnter += delegate { SetHover(true); };
            _icon.MouseLeave += delegate { SetHover(false); };
            _text.MouseEnter += delegate { SetHover(true); };
            _text.MouseLeave += delegate { SetHover(false); };

            // A single-row table deterministically centers each cell's content vertically, which
            // dock/anchor/autosize alone did not do reliably (the links and text hugged the top).
            // Columns: icon (auto) | text (fills) | links (auto).
            _root = new TableLayoutPanel();
            _root.Dock = DockStyle.Fill;
            _root.Margin = new Padding(0);
            _root.Padding = new Padding(0);
            _root.ColumnCount = 3;
            _root.RowCount = 1;
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            _root.Controls.Add(_icon, 0, 0);
            _root.Controls.Add(_text, 1, 0);
            _root.Controls.Add(_links, 2, 0);

            this.Controls.Add(_root);

            SetWorkingDir(null);
        }

        private static LinkLabel MakeLink(string text, EventHandler onClick)
        {
            var lnk = new LinkLabel();
            lnk.AutoSize = true;
            lnk.Text = text;
            lnk.TextAlign = ContentAlignment.MiddleLeft;
            lnk.LinkColor = LinkColor;
            lnk.ActiveLinkColor = LinkColor;
            lnk.VisitedLinkColor = LinkColor;
            lnk.LinkBehavior = LinkBehavior.HoverUnderline;
            lnk.Margin = new Padding(10, 0, 0, 0);
            lnk.Anchor = AnchorStyles.None; // vertically centered in the flow panel
            lnk.LinkClicked += delegate { onClick(null, EventArgs.Empty); };
            return lnk;
        }

        private void Raise(EventHandler h) { if (h != null) h(this, EventArgs.Empty); }

        // Reflect the given working folder (null/empty => "no folder" warning state).
        public void SetWorkingDir(string dir)
        {
            bool has = !string.IsNullOrEmpty(dir);
            _dir = has ? dir : null;
            _icon.Image = has ? SetIcon : UnsetIcon;
            if (has)
            {
                this.BackColor = SetBack;
                _text.ForeColor = SetText;
                _text.Text = "Workspace:  " + dir;
                _change.Text = "Change...";
                _clear.Visible = true;
                _dismiss.Visible = false; // can't dismiss while a folder is set (use Clear)

                // Make the icon + path read as clickable: pointer cursor and an "Open in Explorer"
                // tooltip on both.
                _icon.Cursor = Cursors.Hand;
                _text.Cursor = Cursors.Hand;
                _openTip.SetToolTip(_icon, "Open in Explorer");
                _openTip.SetToolTip(_text, "Open in Explorer");
            }
            else
            {
                this.BackColor = UnsetBack;
                _text.ForeColor = UnsetText;
                _text.Text = "No workspace: some tools are disabled for this conversation.";
                _change.Text = "Set workspace...";
                _clear.Visible = false;
                _dismiss.Visible = true;

                _icon.Cursor = Cursors.Default;
                _text.Cursor = Cursors.Default;
                _openTip.SetToolTip(_icon, null);
                _openTip.SetToolTip(_text, null);
            }
        }

        // Darken (true) or restore (false) the icon and path text while the pointer is over them.
        // Only active when a workspace is set, so the unset warning state never reacts to hover.
        private void SetHover(bool on)
        {
            if (string.IsNullOrEmpty(_dir)) return;
            _icon.Image = on ? (SetIconHover ?? SetIcon) : SetIcon;
            _text.ForeColor = on ? SetTextHover : SetText;
        }

        // Open the current workspace folder in Windows Explorer (no-op when none is set).
        private void OnOpenWorkspaceClicked(object sender, EventArgs e)
        {
            string dir = _dir;
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                System.Diagnostics.Process.Start(dir);
            }
            catch
            {
                try { System.Diagnostics.Process.Start("explorer.exe", dir); }
                catch { }
            }
        }

        // Multiply a color's RGB channels by factor (alpha preserved) to get a slightly darker shade.
        private static Color Darken(Color c, double factor)
        {
            return Color.FromArgb(
                c.A,
                (int)(c.R * factor),
                (int)(c.G * factor),
                (int)(c.B * factor));
        }

        // Produce a copy of an image with its RGB channels scaled by factor (alpha preserved, so
        // transparent areas stay transparent). Returns null if the source is missing.
        private static Image DarkenImage(Image src, float factor)
        {
            if (src == null) return null;
            var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var cm = new ColorMatrix(new float[][]
                {
                    new float[] { factor, 0, 0, 0, 0 },
                    new float[] { 0, factor, 0, 0, 0 },
                    new float[] { 0, 0, factor, 0, 0 },
                    new float[] { 0, 0, 0, 1, 0 },
                    new float[] { 0, 0, 0, 0, 1 },
                });
                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(cm);
                    g.DrawImage(src,
                        new Rectangle(0, 0, src.Width, src.Height),
                        0, 0, src.Width, src.Height,
                        GraphicsUnit.Pixel, ia);
                }
            }
            return bmp;
        }
    }
}
