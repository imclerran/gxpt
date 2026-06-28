// GraphImageViewerForm.cs
// A lightweight pan/zoom viewer for a rendered Graphviz graph. Opened from the chat transcript when
// the user clicks an inline graph (which is downscaled to fit the bubble and often unreadable for a
// complex graph). Shows the image at full resolution with discrete, staged zoom (Fit / 0.5 / 0.75 /
// 1x / 1.5x / 2x / 3x / 4x) via toolbar buttons, scroll wheel, or keys, plus drag-to-pan. Since we
// still have the DOT source, it re-renders a crisp high-DPI copy in the background for deep zooming.
//
// Zoom is tracked in LOGICAL units where 1.0 == the graph's natural (96dpi) size, independent of the
// bitmap actually displayed. That way swapping in the 2x high-DPI bitmap doesn't change what "100%"
// means - it only adds resolution.
// Target: .NET 3.5, Windows XP compatible. Code-only (no designer/.resx).

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading;
using System.Windows.Forms;

namespace GxPT
{
    public sealed class GraphImageViewerForm : Form
    {
        private Bitmap _image;          // owned by this form; swapped to a hi-res copy when ready
        private float _imageScale = 1f; // bitmap pixels per logical unit (1 = 96dpi, 2 = 192dpi)
        private readonly string _dot;   // DOT source (for Copy + hi-res re-render)
        private readonly string _engine;

        private float _zoom = 1f;       // LOGICAL zoom; 1.0 == natural graph size
        private float _offsetX, _offsetY; // image top-left in client coordinates
        private bool _fit = true;       // true while tracking fit-to-window on resize

        private bool _dragging;
        private Point _dragStart;
        private float _dragOffX, _dragOffY;

        private bool _hiResStarted;

        private ToolStrip _toolStrip;
        private ToolStripLabel _zoomLabel;

        // Discrete logical zoom stages stepped through by the buttons / wheel / +- keys.
        private static readonly float[] Stages = { 0.5f, 0.75f, 1f, 1.5f, 2f, 3f, 4f };
        private const float MinZoom = 0.05f;
        private const float MaxZoom = 8f;
        private const float Eps = 0.001f;

        private const int HiResDpi = 192; // ~2x Graphviz's default, for crisp zooming past 1x

        public GraphImageViewerForm(Bitmap image, string engine, string dot)
        {
            _image = image;
            _engine = engine;
            _dot = dot;

            string eng = string.IsNullOrEmpty(engine) ? "graph" : engine;
            Text = "Graph Viewer — " + eng;
            DoubleBuffered = true;
            KeyPreview = true;
            BackColor = Color.FromArgb(0x55, 0x57, 0x66); // neutral slate so a white graph stands out
            StartPosition = FormStartPosition.CenterScreen;

            try
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                int w = Math.Min(960, wa.Width - 80);
                int h = Math.Min(720, wa.Height - 80);
                ClientSize = new Size(Math.Max(400, w), Math.Max(300, h));
            }
            catch { ClientSize = new Size(800, 600); }

            BuildToolbar();
            BuildContextMenu();
        }

        private int TopInset { get { return _toolStrip != null ? _toolStrip.Height : 0; } }

        private void BuildToolbar()
        {
            _toolStrip = new ToolStrip();
            _toolStrip.GripStyle = ToolStripGripStyle.Hidden;
            _toolStrip.RenderMode = ToolStripRenderMode.System;

            var outBtn = new ToolStripButton("Zoom Out");
            outBtn.ToolTipText = "Zoom out (-)";
            outBtn.Click += delegate { ZoomOut(ViewportCenterX(), ViewportCenterY()); };

            var inBtn = new ToolStripButton("Zoom In");
            inBtn.ToolTipText = "Zoom in (+)";
            inBtn.Click += delegate { ZoomIn(ViewportCenterX(), ViewportCenterY()); };

            var fitBtn = new ToolStripButton("Fit");
            fitBtn.ToolTipText = "Fit to window (0)";
            fitBtn.Click += delegate { DoFit(); };

            var actualBtn = new ToolStripButton("100%");
            actualBtn.ToolTipText = "Actual size (1)";
            actualBtn.Click += delegate { DoActual(); };

            _zoomLabel = new ToolStripLabel("Fit");
            _zoomLabel.AutoSize = false;
            _zoomLabel.Width = 56;
            _zoomLabel.TextAlign = ContentAlignment.MiddleCenter;
            // Blend into the toolbar chrome instead of painting the form's (dark) inherited BackColor.
            _zoomLabel.BackColor = Color.Transparent;

            var copyBtn = new ToolStripButton("Copy DOT");
            copyBtn.Alignment = ToolStripItemAlignment.Right;
            copyBtn.ToolTipText = "Copy the DOT source (Ctrl+C)";
            copyBtn.Click += delegate { CopySource(); };

            var saveBtn = new ToolStripButton("Save…");
            saveBtn.Alignment = ToolStripItemAlignment.Right;
            saveBtn.ToolTipText = "Save image as PNG (Ctrl+S)";
            saveBtn.Click += delegate { SaveImage(); };

            _toolStrip.Items.Add(outBtn);
            _toolStrip.Items.Add(inBtn);
            _toolStrip.Items.Add(new ToolStripSeparator());
            _toolStrip.Items.Add(fitBtn);
            _toolStrip.Items.Add(actualBtn);
            _toolStrip.Items.Add(new ToolStripSeparator());
            _toolStrip.Items.Add(_zoomLabel);
            _toolStrip.Items.Add(saveBtn);
            _toolStrip.Items.Add(copyBtn);

            Controls.Add(_toolStrip);
        }

        private void BuildContextMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Zoom in", null, delegate { ZoomIn(ViewportCenterX(), ViewportCenterY()); });
            menu.Items.Add("Zoom out", null, delegate { ZoomOut(ViewportCenterX(), ViewportCenterY()); });
            menu.Items.Add("Fit to window", null, delegate { DoFit(); });
            menu.Items.Add("Actual size (100%)", null, delegate { DoActual(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Copy DOT source", null, delegate { CopySource(); });
            menu.Items.Add("Save image as…", null, delegate { SaveImage(); });
            ContextMenuStrip = menu;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Match the app's title-bar icon (clones the owner/MainForm icon, falls back to the exe's).
            try { PluginImportExportManager.ApplyOwnerIcon(this); }
            catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            DoFit();
            StartHiRes();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_fit) DoFit(); else Invalidate();
        }

        // ---------- View math (logical zoom + bitmap resolution scale) ----------
        // On-screen pixels per bitmap pixel for the current logical zoom.
        private float EffScale() { return _zoom / _imageScale; }

        private float LogicalWidth() { return _image != null ? _image.Width / _imageScale : 0f; }
        private float LogicalHeight() { return _image != null ? _image.Height / _imageScale : 0f; }

        private int ViewportCenterX() { return ClientSize.Width / 2; }
        private int ViewportCenterY() { return TopInset + (ClientSize.Height - TopInset) / 2; }

        private void DoFit()
        {
            if (_image == null) return;
            int cw = ClientSize.Width, ch = ClientSize.Height - TopInset;
            if (cw <= 0 || ch <= 0) return;
            float lw = LogicalWidth(), lh = LogicalHeight();
            if (lw <= 0 || lh <= 0) return;
            float z = Math.Min(cw / lw, ch / lh);
            if (z > 1f) z = 1f;          // never upscale in fit; small graphs show at 100%
            if (z < MinZoom) z = MinZoom;
            _zoom = z;
            _offsetX = (cw - lw * z) / 2f;
            _offsetY = TopInset + (ch - lh * z) / 2f;
            _fit = true;
            UpdateZoomLabel();
            Invalidate();
        }

        private void DoActual()
        {
            if (_image == null) return;
            _zoom = 1f;
            int ch = ClientSize.Height - TopInset;
            float drawW = LogicalWidth();   // == _image.Width * EffScale() at zoom 1
            float drawH = LogicalHeight();
            _offsetX = (ClientSize.Width - drawW) / 2f;
            _offsetY = TopInset + (ch - drawH) / 2f;
            _fit = false;
            UpdateZoomLabel();
            Invalidate();
        }

        // Set an absolute logical zoom, keeping the image point under (cx,cy) fixed on screen.
        private void SetZoom(float target, float cx, float cy)
        {
            if (_image == null) return;
            if (target < MinZoom) target = MinZoom;
            if (target > MaxZoom) target = MaxZoom;
            float oldEff = EffScale();
            float ix = (cx - _offsetX) / oldEff;
            float iy = (cy - _offsetY) / oldEff;
            _zoom = target;
            float newEff = EffScale();
            _offsetX = cx - ix * newEff;
            _offsetY = cy - iy * newEff;
            _fit = false;
            UpdateZoomLabel();
            Invalidate();
        }

        private void ZoomIn(float cx, float cy)
        {
            float cur = _zoom;
            for (int i = 0; i < Stages.Length; i++)
            {
                if (Stages[i] > cur + Eps) { SetZoom(Stages[i], cx, cy); return; }
            }
            // Already at/above the top stage: nudge toward the max if there's room.
            if (cur < MaxZoom - Eps) SetZoom(Math.Min(MaxZoom, cur * 1.5f), cx, cy);
        }

        private void ZoomOut(float cx, float cy)
        {
            float cur = _zoom;
            for (int i = Stages.Length - 1; i >= 0; i--)
            {
                if (Stages[i] < cur - Eps) { SetZoom(Stages[i], cx, cy); return; }
            }
            // Below the smallest stage: fall back to whole-graph Fit.
            DoFit();
        }

        private void UpdateZoomLabel()
        {
            if (_zoomLabel == null) return;
            _zoomLabel.Text = _fit ? "Fit" : (Math.Round(_zoom * 100f).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%");
        }

        // ---------- Input ----------
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (e.Delta > 0) ZoomIn(e.X, e.Y);
            else if (e.Delta < 0) ZoomOut(e.X, e.Y);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _dragStart = e.Location;
                _dragOffX = _offsetX;
                _dragOffY = _offsetY;
                Cursor = Cursors.SizeAll;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                _offsetX = _dragOffX + (e.X - _dragStart.X);
                _offsetY = _dragOffY + (e.Y - _dragStart.Y);
                _fit = false;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && _dragging)
            {
                _dragging = false;
                Cursor = Cursors.Default;
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left)
            {
                if (_fit) DoActual(); else DoFit();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            float cx = ViewportCenterX(), cy = ViewportCenterY();
            switch (e.KeyCode)
            {
                case Keys.Add:
                case Keys.Oemplus:
                    ZoomIn(cx, cy); e.Handled = true; break;
                case Keys.Subtract:
                case Keys.OemMinus:
                    ZoomOut(cx, cy); e.Handled = true; break;
                case Keys.D0:
                case Keys.NumPad0:
                    DoFit(); e.Handled = true; break;
                case Keys.D1:
                case Keys.NumPad1:
                    DoActual(); e.Handled = true; break;
                case Keys.Escape:
                    Close(); e.Handled = true; break;
                case Keys.S:
                    if (e.Control) { SaveImage(); e.Handled = true; } break;
                case Keys.C:
                    if (e.Control) { CopySource(); e.Handled = true; } break;
            }
        }

        // ---------- Paint ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            if (_image != null)
            {
                float eff = EffScale();
                var rect = new RectangleF(_offsetX, _offsetY, _image.Width * eff, _image.Height * eff);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                try { g.DrawImage(_image, rect); }
                catch { }
            }

            try
            {
                string hint = "Scroll or +/- to zoom · drag to pan · double-click to fit";
                using (var f = new Font(FontFamily.GenericSansSerif, 8f))
                using (var sh = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
                using (var fg = new SolidBrush(Color.FromArgb(230, 255, 255, 255)))
                {
                    SizeF sz = g.MeasureString(hint, f);
                    float x = 6f, y = ClientSize.Height - sz.Height - 5f;
                    g.FillRectangle(sh, x - 3f, y - 2f, sz.Width + 6f, sz.Height + 4f);
                    g.DrawString(hint, f, fg, x, y);
                }
            }
            catch { }
        }

        // ---------- Hi-res background render ----------
        private void StartHiRes()
        {
            if (_hiResStarted) return;
            _hiResStarted = true;
            if (string.IsNullOrEmpty(_dot)) return;

            Thread t = new Thread(delegate()
            {
                GraphvizRenderer.GraphResult r;
                try { r = GraphvizRenderer.RenderNow(_engine, _dot, HiResDpi); }
                catch { return; }
                if (r.Failed || r.Image == null) return;

                Bitmap bmp = r.Image;
                try
                {
                    if (IsHandleCreated && !IsDisposed)
                        BeginInvoke((MethodInvoker)delegate { SwapImage(bmp); });
                    else
                        bmp.Dispose();
                }
                catch { try { bmp.Dispose(); } catch { } }
            });
            t.IsBackground = true;
            try { t.Priority = ThreadPriority.BelowNormal; }
            catch { }
            t.Start();
        }

        // Replace the displayed image with a higher-resolution copy. The new bitmap renders the same
        // graph at more pixels, so its resolution scale relative to the current one is just the width
        // ratio. Tracking that in _imageScale keeps logical zoom (and "100%") meaning the same natural
        // size, so the on-screen view is unchanged - the image just gains pixels for crisper zooming.
        private void SwapImage(Bitmap bmp)
        {
            if (bmp == null) return;
            if (IsDisposed) { try { bmp.Dispose(); } catch { } return; }

            Bitmap old = _image;
            if (old != null && old.Width > 0)
                _imageScale = _imageScale * ((float)bmp.Width / old.Width);
            _image = bmp;
            if (_fit) DoFit(); else Invalidate();
            if (old != null && !ReferenceEquals(old, bmp))
            {
                try { old.Dispose(); }
                catch { }
            }
        }

        // ---------- Actions ----------
        private void CopySource()
        {
            try
            {
                string s = _dot ?? string.Empty;
                s = s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
                Clipboard.SetText(s);
            }
            catch { }
        }

        private void SaveImage()
        {
            if (_image == null) return;
            try
            {
                using (var sfd = new SaveFileDialog())
                {
                    sfd.Filter = "PNG image (*.png)|*.png";
                    sfd.FileName = "graph.png";
                    if (sfd.ShowDialog(this) == DialogResult.OK)
                    {
                        try { _image.Save(sfd.FileName, ImageFormat.Png); }
                        catch { }
                    }
                }
            }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (_image != null)
            {
                try { _image.Dispose(); }
                catch { }
                _image = null;
            }
        }
    }
}
