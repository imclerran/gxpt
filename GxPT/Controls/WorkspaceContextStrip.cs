using System;
using System.Drawing;
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

        // An open-folder variant of the set icon, swapped in while hovering the (clickable) workspace
        // path (falls back to the closed icon if the resource is missing).
        private static readonly Image SetIconHover = ResourceManager.TryGetAssemblyImage("WorkspaceSetOpen.png");

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

        // Whether the pointer is currently over the rendered path text (vs. the blank space after it).
        // Tracked so cursor/tooltip/hover only toggle on boundary crossings.
        private bool _overText;

        // Whether the hover affordance is active; when set, the path text is drawn with a painted
        // underline (trial). Drawn manually rather than via an underlined font so changing it doesn't
        // relayout the (ellipsized) label and flicker.
        private bool _hoverActive;

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

            _text = new DoubleBufferedLabel();
            _text.AutoSize = false;
            _text.Dock = DockStyle.Fill;
            _text.AutoEllipsis = true;
            _text.TextAlign = ContentAlignment.MiddleLeft;
            // ForeColor is set per state in SetWorkingDir (dark green when set, brown when unset).
            // The small gap between the icon and the text is the label's left *padding* (not a margin)
            // so it lives inside the label and stays part of the clickable open-workspace region.
            _text.Margin = new Padding(0);
            _text.Padding = new Padding(6, 0, 0, 0);

            // When a workspace is set, the icon and path text act as a single click target that opens
            // the folder in Explorer. On hover they darken / swap to the open-folder glyph, show a
            // pointer cursor + tooltip, and (trial) underline the text. The text label fills its column,
            // so its handlers are position-aware: only the actual rendered text (not the blank space
            // after it) reacts. All handlers no-op while no workspace is set (guarded by _dir).
            _openTip = new ToolTip();
            _icon.Click += OnOpenWorkspaceClicked;
            _icon.MouseEnter += delegate { SetHover(true); };
            _icon.MouseLeave += delegate { SetHover(false); };
            _text.MouseClick += OnTextMouseClick;
            _text.MouseMove += OnTextMouseMove;
            _text.MouseLeave += OnTextMouseLeave;
            _text.Paint += OnTextPaint; // draws the hover underline (trial)

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

            // Reset the per-pointer text state; the next MouseMove re-evaluates it for the new text.
            _overText = false;
            _text.Cursor = Cursors.Default;
            _openTip.SetToolTip(_text, null);

            if (has)
            {
                this.BackColor = SetBack;
                _text.ForeColor = SetText;
                _text.Text = "Workspace:  " + dir;
                _change.Text = "Change...";
                _clear.Visible = true;
                _dismiss.Visible = false; // can't dismiss while a folder is set (use Clear)

                // The whole icon is a click target (pointer cursor + tooltip). The text label fills its
                // column, so its cursor/tooltip are applied per-pointer in OnTextMouseMove instead —
                // only over the rendered text, not the blank space after it.
                _icon.Cursor = Cursors.Hand;
                _openTip.SetToolTip(_icon, "Open in Explorer");
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
                _openTip.SetToolTip(_icon, null);
            }
        }

        // Apply (true) or clear (false) the hover affordance while the pointer is over the path: the
        // folder icon swaps to its open variant and the text darkens slightly. Only active when a
        // workspace is set, so the unset warning state never reacts to hover.
        private void SetHover(bool on)
        {
            if (string.IsNullOrEmpty(_dir)) return;
            _hoverActive = on;
            _icon.Image = on ? (SetIconHover ?? SetIcon) : SetIcon;
            _text.ForeColor = on ? SetTextHover : SetText;
            _text.Invalidate(); // repaint to add/remove the underline
        }

        // Draw the link-style underline under the path text while hovering (trial). Done here, with the
        // font unchanged, so toggling it never relayouts the ellipsized label (which is what flickered).
        private void OnTextPaint(object sender, PaintEventArgs e)
        {
            if (!_hoverActive || string.IsNullOrEmpty(_dir)) return;
            int left = _text.Padding.Left;          // text starts after the icon gap padding
            int right = TextRightEdge();             // end of the rendered text (capped to width)
            if (right <= left) return;
            Size sz = TextRenderer.MeasureText(_text.Text, _text.Font);
            int top = (_text.ClientSize.Height - sz.Height) / 2;
            int y = top + sz.Height - 1;             // sit a couple px below the glyphs
            using (var pen = new Pen(_text.ForeColor))
            {
                e.Graphics.DrawLine(pen, left, y, right - 1, y);
            }
        }

        // The path label fills its column; this is the right edge (within the label) of the clickable
        // open-workspace region: the left-padding gap after the icon plus the rendered text, capped to
        // the label width. A click/hover left of this opens the workspace (icon, gap, and text); the
        // trailing blank space after the text does not.
        private int TextRightEdge()
        {
            Size sz = TextRenderer.MeasureText(_text.Text, _text.Font);
            return Math.Min(_text.Padding.Left + sz.Width, _text.Width);
        }

        // Track + apply the hover affordance only while the pointer is over the actual text (the blank
        // space after a short path stays inert). Toggles on boundary crossings to avoid tooltip flicker.
        private void OnTextMouseMove(object sender, MouseEventArgs e)
        {
            if (string.IsNullOrEmpty(_dir)) return;
            bool over = e.X <= TextRightEdge();
            if (over == _overText) return;
            _overText = over;
            _text.Cursor = over ? Cursors.Hand : Cursors.Default;
            _openTip.SetToolTip(_text, over ? "Open in Explorer" : null);
            SetHover(over);
        }

        private void OnTextMouseLeave(object sender, EventArgs e)
        {
            if (!_overText) return;
            _overText = false;
            _text.Cursor = Cursors.Default;
            _openTip.SetToolTip(_text, null);
            SetHover(false);
        }

        // Open only when the click landed on the text, not the trailing blank space.
        private void OnTextMouseClick(object sender, MouseEventArgs e)
        {
            if (string.IsNullOrEmpty(_dir)) return;
            if (e.X > TextRightEdge()) return;
            OnOpenWorkspaceClicked(sender, e);
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

        // A Label that paints itself double-buffered, so toggling the font/color on hover doesn't flash
        // the background (the stock Label erases then redraws, which flickers).
        private sealed class DoubleBufferedLabel : Label
        {
            public DoubleBufferedLabel()
            {
                this.SetStyle(
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint,
                    true);
            }
        }
    }
}
