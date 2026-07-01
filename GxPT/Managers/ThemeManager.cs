using System;
using System.Drawing;
using System.Windows.Forms;
using Krypton.Toolkit;

namespace GxPT
{
    internal sealed class ThemeManager
    {
        private readonly MainForm _mainForm;
        private readonly ChatTranscriptControl _primaryTranscript;
        private readonly TextBox _txtMessage;
        private readonly KryptonButton _btnSend;
        private readonly KryptonButton _btnAttach;
        private readonly ModelComboBox _cmbModel;
        private readonly LinkLabel _lnkOpenSettings;
        private readonly Label _lblNoApiKey;

        // The scaled paperclip currently shown on the attach button. We own it (Krypton just references
        // it), so it is disposed when replaced on a theme switch.
        private Image _attachIcon;

        public ThemeManager(MainForm mainForm, ChatTranscriptControl primaryTranscript,
            TextBox txtMessage, KryptonButton btnSend, KryptonButton btnAttach, ModelComboBox cmbModel,
            LinkLabel lnkOpenSettings, Label lblNoApiKey)
        {
            _mainForm = mainForm;
            _primaryTranscript = primaryTranscript;
            _txtMessage = txtMessage;
            _btnSend = btnSend;
            _btnAttach = btnAttach;
            _cmbModel = cmbModel;
            _lnkOpenSettings = lnkOpenSettings;
            _lblNoApiKey = lblNoApiKey;
        }

        public void ApplyFontSizeSettingToAllUi()
        {
            try
            {
                double fs = AppSettings.GetDouble("font_size", 0);
                if (fs <= 0)
                {
                    ApplyFontSizeSettingToAllTranscripts();
                    return;
                }
                float size = (float)Math.Max(6, Math.Min(48, fs));

                // Core chat transcript(s)
                ApplyFontSizeSettingToAllTranscripts();

                // Input textbox
                try
                {
                    if (_txtMessage != null)
                        _txtMessage.Font = new Font(_txtMessage.Font.FontFamily, size, _txtMessage.Font.Style);
                }
                catch { }

                // Send button
                try
                {
                    if (_btnSend != null)
                        _btnSend.Font = new Font(_btnSend.Font.FontFamily, size, _btnSend.Font.Style);
                }
                catch { }

                // Model combo box
                try
                {
                    if (_cmbModel != null)
                        _cmbModel.Font = new Font(_cmbModel.Font.FontFamily, size, _cmbModel.Font.Style);
                }
                catch { }

                // API key banner link/label
                try
                {
                    if (_lnkOpenSettings != null)
                        _lnkOpenSettings.Font = new Font(_lnkOpenSettings.Font.FontFamily, size, _lnkOpenSettings.Font.Style);
                }
                catch { }

                try
                {
                    if (_lblNoApiKey != null)
                        _lblNoApiKey.Font = new Font(_lblNoApiKey.Font.FontFamily, size, _lblNoApiKey.Font.Style);
                }
                catch { }

                // Tab manager and sidebar will handle their own fonts
                var tabManager = _mainForm.GetTabManager();
                if (tabManager != null) tabManager.ApplyFontSetting();
                var sidebarManager = _mainForm.GetSidebarManager();
                if (sidebarManager != null) sidebarManager.ApplyFontSetting();

                // Recalculate input height because font changes alter measured height
                var inputManager = _mainForm.GetInputManager();
                if (inputManager != null) inputManager.AdjustInputBoxHeight();
            }
            catch { }
        }

        public void ApplyFontSizeSettingToAllTranscripts()
        {
            try
            {
                double fs = AppSettings.GetDouble("font_size", 0);
                if (fs <= 0) return;
                float size = (float)Math.Max(6, Math.Min(48, fs));

                // Designer-created transcript
                try
                {
                    if (_primaryTranscript != null)
                        _primaryTranscript.Font = new Font(_primaryTranscript.Font.FontFamily, size, _primaryTranscript.Font.Style);
                }
                catch { }

                // Tab manager handles transcripts in tabs
                var tabManager = _mainForm.GetTabManager();
                if (tabManager != null) tabManager.ApplyFontSetting();
            }
            catch { }
        }

        public void ApplyFontSetting(ChatTranscriptControl transcript)
        {
            if (transcript == null) return;
            try
            {
                double fs = AppSettings.GetDouble("font_size", 0);
                if (fs <= 0) return;
                float size = (float)Math.Max(6, Math.Min(48, fs));
                transcript.Font = new Font(transcript.Font.FontFamily, size, transcript.Font.Style);
            }
            catch { }
        }

        public void ApplyThemeToAllTranscripts()
        {
            // Swap the Krypton global palette so all window chrome (forms,
            // buttons, inputs, headers, menu/status strips) re-themes in lockstep
            // with the transcript. Safe and self-contained: failures here never
            // affect the transcript theming below.
            try { KryptonThemeBridge.Apply(); }
            catch { }

            try
            {
                if (_primaryTranscript != null)
                    _primaryTranscript.RefreshTheme();
            }
            catch { }

            // Tab manager handles transcripts in tabs
            var tabManager = _mainForm.GetTabManager();
            if (tabManager != null) tabManager.ApplyThemeToAllTranscripts();

            // Also apply matching background/foreground to the input textbox
            ApplyThemeToTextBox();

            // Swap the attach-button paperclip to the light/dark variant for the active theme.
            ApplyAttachIcon();
        }

        // Draw the attach button's paperclip glyph for the active theme, stroked in the theme's foreground
        // (text) color so it always matches the mode. The glyph is rendered into a bitmap sized to fit
        // inside this narrow button - Krypton drops a content image that overflows its content rectangle.
        public void ApplyAttachIcon()
        {
            if (_btnAttach == null) return;
            try
            {
                // Leave a small margin so the bitmap fits within the button's (border-inset) content box.
                int w = Math.Max(1, _btnAttach.Width - 8);
                int h = Math.Max(1, _btnAttach.Height - 10);
                // Match the menu/tab-glyph text color (dark blue under Office 2010, near-white under
                // Sparkle dark) - the same source the tab +/x glyphs use, read from the live toolstrip
                // color table so it's correct in both modes.
                Color color = KryptonThemeBridge.MenuTextColor();

                Bitmap bmp = new Bitmap(w, h); // 32bpp ARGB - starts fully transparent
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    DrawPaperclip(g, new RectangleF(0, 0, w, h), color);
                }

                _btnAttach.Values.Image = bmp;
                Image prev = _attachIcon;
                _attachIcon = bmp;
                if (prev != null && !object.ReferenceEquals(prev, bmp)) prev.Dispose();
            }
            catch { }
        }

        // Render a vertical gem paperclip inside box as a SINGLE continuous wire (one open GraphicsPath),
        // stroked with a round-capped pen. The wire makes three nested U-turns with shared legs:
        //
        //   * U1 - widest, opens downward (bend at the top). Its LEFT leg (xa) is a free end.
        //   * U2 - narrower, opens upward (bend at the bottom). Shares U1's RIGHT leg (xd).
        //   * U3 - narrowest, opens downward (bend at the top). Shares U2's LEFT leg (xb); its RIGHT
        //          leg (xc) is the other free end.
        //
        // Trace: up leg a -> U1 (top, a->d) -> down leg d -> U2 (bottom, d->b) -> up leg b ->
        //        U3 (top, b->c) -> down leg c. All bends are semicircles (radius = half the span).
        private static void DrawPaperclip(Graphics g, RectangleF box, Color color)
        {
            float thickness = Math.Max(1.4f, box.Width * 0.10f);
            float pad = thickness * 0.6f + 1f;              // keep strokes (and round caps) inside the box
            float left = box.Left + pad;
            float top = box.Top + pad;
            float W = box.Width - 2f * pad;
            float H = box.Height - 2f * pad;
            if (W <= 0 || H <= 0) return;

            // Four tine x-positions (left -> right). Widths nest: U1 (a..d) > U2 (b..d) > U3 (b..c).
            float xa = left + W * 0.10f;   // U1 left  (free end)
            float xb = left + W * 0.30f;   // U2 left / U3 left (shared)
            float xc = left + W * 0.62f;   // U3 right (free end)
            float xd = left + W * 0.90f;   // U1 right / U2 right (shared)

            float rU1 = (xd - xa) / 2f;    // widest top bend
            float rU2 = (xd - xb) / 2f;    // bottom bend
            float rU3 = (xc - xb) / 2f;    // narrowest top bend

            float y1 = top + rU1;                     // U1 top-bend centre (apex at the very top)
            float y2 = top + H - rU2 - H * 0.07f;     // U2 bottom-bend centre (lifted for even top/bottom margins)
            float y3 = top + rU3 + H * 0.16f;         // U3 top-bend centre (dropped well below U1's top bend)
            float yaEnd = top + H * 0.84f;            // free end of the long outer-left tine
            float ycEnd = top + H * 0.58f;            // free end of the shorter inner tine

            using (Pen pen = new Pen(color, thickness))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;

                using (System.Drawing.Drawing2D.GraphicsPath p = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    p.AddLine(xa, yaEnd, xa, y1);                                   // leg a: up to U1
                    p.AddArc(xa, y1 - rU1, 2f * rU1, 2f * rU1, 180f, 180f);         // U1 (top): a -> d
                    p.AddLine(xd, y1, xd, y2);                                      // leg d: down to U2
                    p.AddArc(xb, y2 - rU2, 2f * rU2, 2f * rU2, 0f, 180f);           // U2 (bottom): d -> b
                    p.AddLine(xb, y2, xb, y3);                                      // leg b: up to U3
                    p.AddArc(xb, y3 - rU3, 2f * rU3, 2f * rU3, 180f, 180f);         // U3 (top): b -> c
                    p.AddLine(xc, y3, xc, ycEnd);                                   // leg c: down to free end
                    g.DrawPath(pen, p);
                }
            }
        }

        // Apply transcript max width setting to all existing transcripts
        public void ApplyTranscriptWidthToAllTranscripts()
        {
            try
            {
                int w = (int)Math.Round(AppSettings.GetDouble("transcript_max_width", 1000));
                if (w <= 0) w = 1000;
                if (w < 300) w = 300; if (w > 1900) w = 1900;

                if (_primaryTranscript != null)
                {
                    try { _primaryTranscript.MaxContentWidth = w; }
                    catch { }
                }

                var tabManager = _mainForm.GetTabManager();
                if (tabManager != null) tabManager.ApplyTranscriptWidthToAllTranscripts(w);
            }
            catch { }
        }

        public void ApplyThemeToTextBox()
        {
            try
            {
                bool dark = IsDarkTheme();

                if (_txtMessage != null)
                {
                    var colors = ThemeService.GetColors(dark);
                    _txtMessage.BackColor = colors.UiBackground;
                    // Do not apply theme foreground color for hint text
                    if (_txtMessage.ForeColor != System.Drawing.Color.Gray || _txtMessage.Text == "")
                        _txtMessage.ForeColor = colors.UiForeground;
                }
            }
            catch { }
        }

        // True when the active theme is dark.
        public bool IsDarkTheme()
        {
            try
            {
                string theme = AppSettings.GetString("theme");
                return !string.IsNullOrEmpty(theme) &&
                    theme.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // The input box's foreground color for real (non-hint) text under the active theme. Callers that
        // programmatically replace the input text - e.g. accepting a slash-command completion - use this
        // instead of SystemColors.WindowText so the text keeps the dark-mode color rather than turning black.
        public Color GetUiForeColor()
        {
            try { return ThemeService.GetColors(IsDarkTheme()).UiForeground; }
            catch { return SystemColors.WindowText; }
        }
    }
}
