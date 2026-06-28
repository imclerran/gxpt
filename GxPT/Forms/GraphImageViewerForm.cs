// GraphImageViewerForm.cs
// A lightweight pan/zoom viewer for a rendered Graphviz graph. Opened from the chat transcript when
// the user clicks an inline graph (which is downscaled to fit the bubble and often unreadable for a
// complex graph). Shows the image at full resolution with scroll-to-zoom and drag-to-pan, and - since
// we still have the DOT source - re-renders a crisp high-DPI copy in the background for deep zooming.
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
        private readonly string _dot;   // DOT source (for Copy + hi-res re-render)
        private readonly string _engine;

        private float _zoom = 1f;       // on-screen pixels per image pixel
        private float _offsetX, _offsetY; // image top-left in client coordinates
        private bool _fit = true;       // true while tracking fit-to-window on resize

        private bool _dragging;
        private Point _dragStart;
        private float _dragOffX, _dragOffY;

        private bool _hiResStarted;

        private const float MinZoom = 0.02f;
        private const float MaxZoom = 16f;
        private const int HiResDpi = 192; // ~2x Graphviz's default 96dpi, for crisp zooming

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

            // Default to a comfortable size, clamped to the working area.
            try
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                int w = Math.Min(960, wa.Width - 80);
                int h = Math.Min(720, wa.Height - 80);
                ClientSize = new Size(Math.Max(400, w), Math.Max(300, h));
            }
            catch { ClientSize = new Size(800, 600); }

            BuildContextMenu();
        }

        private void BuildContextMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Fit to window", null, delegate { DoFit(); });
            menu.Items.Add("Actual size (100%)", null, delegate { DoActual(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Copy DOT source", null, delegate { CopySource(); });
            menu.Items.Add("Save image as…", null, delegate { SaveImage(); });
            ContextMenuStrip = menu;
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

        // ---------- View math ----------
        private void DoFit()
        {
            if (_image == null) return;
            int cw = ClientSize.Width, ch = ClientSize.Height;
            if (cw <= 0 || ch <= 0) return;
            float zx = (float)cw / _image.Width;
            float zy = (float)ch / _image.Height;
            float z = Math.Min(zx, zy);
            if (z > 1f) z = 1f;            // never upscale in fit; small graphs show at 100%
            if (z < MinZoom) z = MinZoom;
            _zoom = z;
            _offsetX = (cw - _image.Width * z) / 2f;
            _offsetY = (ch - _image.Height * z) / 2f;
            _fit = true;
            Invalidate();
        }

        private void DoActual()
        {
            if (_image == null) return;
            _zoom = 1f;
            _offsetX = (ClientSize.Width - _image.Width) / 2f;
            _offsetY = (ClientSize.Height - _image.Height) / 2f;
            _fit = false;
            Invalidate();
        }

        private void ZoomBy(float factor, float cx, float cy)
        {
            if (_image == null) return;
            float old = _zoom;
            float nz = old * factor;
            if (nz < MinZoom) nz = MinZoom;
            if (nz > MaxZoom) nz = MaxZoom;
            if (nz == old) return;
            // Keep the image point under (cx,cy) fixed.
            float ix = (cx - _offsetX) / old;
            float iy = (cy - _offsetY) / old;
            _offsetX = cx - ix * nz;
            _offsetY = cy - iy * nz;
            _zoom = nz;
            _fit = false;
            Invalidate();
        }

        // ---------- Input ----------
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float factor = e.Delta > 0 ? 1.15f : (1f / 1.15f);
            ZoomBy(factor, e.X, e.Y);
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
            float cx = ClientSize.Width / 2f, cy = ClientSize.Height / 2f;
            switch (e.KeyCode)
            {
                case Keys.Add:
                case Keys.Oemplus:
                    ZoomBy(1.15f, cx, cy); e.Handled = true; break;
                case Keys.Subtract:
                case Keys.OemMinus:
                    ZoomBy(1f / 1.15f, cx, cy); e.Handled = true; break;
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
                var rect = new RectangleF(_offsetX, _offsetY, _image.Width * _zoom, _image.Height * _zoom);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                try { g.DrawImage(_image, rect); }
                catch { }
            }

            // Small hint, bottom-left.
            try
            {
                string hint = "Scroll to zoom · drag to pan · double-click to fit · right-click for more";
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

        // Replace the displayed image with a higher-resolution copy, keeping the current view stable:
        // offsets are in client coords (unchanged), and zoom is scaled by oldW/newW so the on-screen
        // size stays identical - the image just gains resolution for zooming in.
        private void SwapImage(Bitmap bmp)
        {
            if (bmp == null) return;
            if (IsDisposed) { try { bmp.Dispose(); } catch { } return; }

            Bitmap old = _image;
            float k = (old != null && bmp.Width > 0) ? (float)old.Width / bmp.Width : 1f;
            _image = bmp;
            if (_fit) DoFit();
            else { _zoom *= k; Invalidate(); }
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
