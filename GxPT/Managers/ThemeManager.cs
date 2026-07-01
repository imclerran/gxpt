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
        private readonly ModelComboBox _cmbModel;
        private readonly LinkLabel _lnkOpenSettings;
        private readonly Label _lblNoApiKey;

        public ThemeManager(MainForm mainForm, ChatTranscriptControl primaryTranscript,
            TextBox txtMessage, KryptonButton btnSend, ModelComboBox cmbModel,
            LinkLabel lnkOpenSettings, Label lblNoApiKey)
        {
            _mainForm = mainForm;
            _primaryTranscript = primaryTranscript;
            _txtMessage = txtMessage;
            _btnSend = btnSend;
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
