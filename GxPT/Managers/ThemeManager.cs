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
                Color color = GetUiForeColor();

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

        // Render a vertical paperclip inside box: two nested, opposite-facing rounded "U" hairpins (an
        // outer U opening upward, a narrower inner U opening downward) that overlap in the middle like a
        // bent wire. Straight tines + semicircular bends, stroked with a round-capped pen.
        private static void DrawPaperclip(Graphics g, RectangleF box, Color color)
        {
            float thickness = Math.Max(1.5f, box.Width * 0.13f);
            float pad = thickness * 0.6f + 1f;              // keep strokes (and round caps) inside the box
            float left = box.Left + pad;
            float right = box.Right - pad;
            float top = box.Top + pad;
            float bottom = box.Bottom - pad;
            float width = right - left;
            float height = bottom - top;
            if (width <= 0 || height <= 0) return;

            // Outer tines sit slightly in from the edges; inner tines are pulled well toward the center.
            float xOL = left + width * 0.16f;
            float xOR = right - width * 0.16f;
            float xIL = left + width * 0.34f;
            float xIR = right - width * 0.34f;
            float outerTop = top + height * 0.22f;          // outer U's open ends, in the upper quarter
            float innerBottom = bottom - height * 0.22f;    // inner U's open ends, in the lower quarter
            float rO = (xOR - xOL) / 2f;
            float rI = (xIR - xIL) / 2f;

            using (Pen pen = new Pen(color, thickness))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;

                // Outer U (opens up): left tine down -> bottom semicircle (left->bottom->right) -> right tine up.
                using (System.Drawing.Drawing2D.GraphicsPath outer = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    outer.AddLine(xOL, outerTop, xOL, bottom - rO);
                    outer.AddArc(xOL, bottom - 2f * rO, 2f * rO, 2f * rO, 180f, -180f);
                    outer.AddLine(xOR, bottom - rO, xOR, outerTop);
                    g.DrawPath(pen, outer);
                }

                // Inner U (opens down): left tine up -> top semicircle (left->top->right) -> right tine down.
                using (System.Drawing.Drawing2D.GraphicsPath inner = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    inner.AddLine(xIL, innerBottom, xIL, top + rI);
                    inner.AddArc(xIL, top, 2f * rI, 2f * rI, 180f, 180f);
                    inner.AddLine(xIR, top + rI, xIR, innerBottom);
                    g.DrawPath(pen, inner);
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
