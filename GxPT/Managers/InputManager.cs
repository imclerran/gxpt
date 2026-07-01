using System;
using System.Drawing;
using System.Windows.Forms;
using Krypton.Toolkit;

namespace GxPT
{
    internal sealed class InputManager
    {
        private const int MinInputHeightPx = 75;
        private const string InputHintText = "Ask anything...";
        private readonly Color InputHintColor = System.Drawing.Color.Gray;

        private readonly MainForm _mainForm;
        private readonly TextBox _txtMessage;
        private readonly Panel _pnlInput;
        private readonly KryptonButton _btnSend;
        private readonly ComboBox _cmbModel;
        private readonly SplitContainer _splitContainer;
        private readonly Panel _pnlApiKeyBanner;
        private readonly Panel _pnlAttachmentsBanner;
        // Optional extra banner hosted in pnlBottom (the "updated recommended models" notice), registered
        // after construction because it is built programmatically once the managers already exist.
        private Panel _pnlModelUpdateBanner;

        // Slash-command autocomplete popup (filters the registry / completes path arguments as the user
        // types after a leading '/').
        private SlashAutocompleteController _autocomplete;

        // Designer height of the model row (pnlModelRow). The row may grow so the combo isn't
        // clipped at larger fonts/DPI, but never shrinks below this — the ZDR checkbox and the
        // 75px input floor are sized against it.
        private int _modelRowBaseHeight = -1;

        // Automatic property with both getter and setter
        public bool TextIsHint { get; private set; }

        public InputManager(MainForm mainForm, TextBox txtMessage, Panel pnlInput,
            KryptonButton btnSend, ComboBox cmbModel, SplitContainer splitContainer,
            Panel pnlApiKeyBanner, Panel pnlAttachmentsBanner)
        {
            _mainForm = mainForm;
            _txtMessage = txtMessage;
            _pnlInput = pnlInput;
            _btnSend = btnSend;
            _cmbModel = cmbModel;
            _splitContainer = splitContainer;
            _pnlApiKeyBanner = pnlApiKeyBanner;
            _pnlAttachmentsBanner = pnlAttachmentsBanner;

            InitializeInput();
            WireEvents();

            try { _autocomplete = new SlashAutocompleteController(_mainForm, this, _txtMessage); }
            catch { _autocomplete = null; }
        }

        // Register the (programmatically built) model-update banner so its footprint is included in the
        // available-height math and the input box reflows when it shows/hides.
        public void SetModelUpdateBanner(Panel banner)
        {
            _pnlModelUpdateBanner = banner;
            try
            {
                if (banner != null)
                {
                    banner.VisibleChanged += (s, e) => AdjustInputBoxHeight();
                    banner.Resize += (s, e) => AdjustInputBoxHeight();
                }
            }
            catch { }
            try { AdjustInputBoxHeight(); }
            catch { }
        }

        // The themed foreground color for real (non-hint) input text. Used whenever text is set
        // programmatically so it doesn't revert to black under the dark theme; falls back to the system
        // window text color if the theme manager isn't available yet (early init).
        private Color GetThemedForeColor()
        {
            try
            {
                var theme = _mainForm != null ? _mainForm.GetThemeManager() : null;
                if (theme != null) return theme.GetUiForeColor();
            }
            catch { }
            return SystemColors.WindowText;
        }

        // Programmatically set the input text and optionally focus the input box.
        public void SetInputText(string text, bool focus)
        {
            try
            {
                if (_txtMessage == null) return;
                TextIsHint = false;
                _txtMessage.ForeColor = GetThemedForeColor();
                _txtMessage.Text = text ?? string.Empty;
                try { _txtMessage.SelectionStart = _txtMessage.TextLength; _txtMessage.SelectionLength = 0; }
                catch { }
                if (focus) FocusInput();
                AdjustInputBoxHeight();
            }
            catch { }
        }

        private void InitializeInput()
        {
            if (_txtMessage != null)
            {
                _txtMessage.Multiline = true;
                _txtMessage.AcceptsReturn = true;
                _txtMessage.WordWrap = true;
                _txtMessage.ScrollBars = ScrollBars.None;
            }

            try
            {
                if (_pnlInput != null)
                    _pnlInput.AutoSize = false;
            }
            catch { }

            try { AdjustInputBoxHeight(); }
            catch { }
        }

        private void WireEvents()
        {
            if (_txtMessage != null)
            {
                _txtMessage.KeyDown += txtMessage_KeyDown;
                _txtMessage.TextChanged += (s, e) => AdjustInputBoxHeight();
                _txtMessage.Resize += (s, e) => AdjustInputBoxHeight();
                //try { _txtMessage.Resize += (s, e) => AdjustInputBoxHeight(); }
                //catch { }
            }

            // Keep input responsive to container changes; avoid extra listeners that caused visual artifacts

            // Recalculate input height when containers resize
            try
            {
                if (_splitContainer != null && _splitContainer.Panel2 != null)
                    _splitContainer.Panel2.Resize += (s, e) => AdjustInputBoxHeight();
            }
            catch { }

            // And when the API key banner changes visibility/size
            try
            {
                if (_pnlApiKeyBanner != null)
                {
                    _pnlApiKeyBanner.VisibleChanged += (s, e) => AdjustInputBoxHeight();
                    _pnlApiKeyBanner.Resize += (s, e) => AdjustInputBoxHeight();
                }
            }
            catch { }

            // When attachments banner size/visibility changes
            try
            {
                if (_pnlAttachmentsBanner != null)
                {
                    _pnlAttachmentsBanner.VisibleChanged += (s, e) => AdjustInputBoxHeight();
                    _pnlAttachmentsBanner.Resize += (s, e) => AdjustInputBoxHeight();
                }
            }
            catch { }
        }

        private void txtMessage_KeyDown(object sender, KeyEventArgs e)
        {
            // KeyDown is wired both here and via MainForm; bail if the other handler already acted.
            if (e.Handled || e.SuppressKeyPress) return;
            // Let the autocomplete popup consume navigation/accept keys first.
            if (_autocomplete != null && _autocomplete.HandleKeyDown(e)) return;

            // ESC cancels editing (if active), clears input and attachments, and restores state
            if (e.KeyCode == Keys.Escape)
            {
                try
                {
                    var tm = _mainForm != null ? _mainForm.GetTabManager() : null;
                    var ctx = tm != null ? tm.GetActiveContext() : null;
                    if (ctx != null && ctx.PendingEditActive)
                    {
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        _mainForm.CancelEditingAndRestoreConversation();
                        return;
                    }
                }
                catch { }
            }
            // Ctrl+A selects all text in the input box
            if (e.Control && e.KeyCode == Keys.A)
            {
                try { if (_txtMessage != null) _txtMessage.SelectAll(); }
                catch { }
                e.SuppressKeyPress = true;
                e.Handled = true;
                return;
            }

            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                // Prevent newline and trigger send
                e.SuppressKeyPress = true;
                e.Handled = true;
                // Do not send if we're showing the hint text
                if (TextIsHint) return;
                if (_btnSend != null)
                    _btnSend.PerformClick();
            }
        }

        public void ResetInputBoxHeight()
        {
            // Use the same logic as automatic adjustment to avoid undersizing at various DPI/theme
            try { AdjustInputBoxHeight(); }
            catch { }
        }

        // The owner-drawn model combo snaps to its font-driven height regardless of how the row
        // docks it, so the row must be at least that tall or the combo's bottom border is clipped
        // by the row's edge.
        private void SyncModelRowHeight()
        {
            try
            {
                var row = (_cmbModel != null) ? _cmbModel.Parent : null;
                if (row == null) return;
                if (_modelRowBaseHeight < 0) _modelRowBaseHeight = row.Height;
                int wanted = Math.Max(_modelRowBaseHeight, _cmbModel.Height);
                if (row.Height != wanted) row.Height = wanted;
            }
            catch { }
        }

        // Send stays flush with the top of the input box only while the panel's floor matches the
        // docked right-column stack (button row + model row) exactly, so derive the floor from the
        // live heights instead of assuming the designer's 52 + 23.
        private int GetMinInputHeight()
        {
            try
            {
                var buttons = (_btnSend != null) ? _btnSend.Parent : null;
                var row = (_cmbModel != null) ? _cmbModel.Parent : null;
                if (buttons != null && row != null)
                    return Math.Max(MinInputHeightPx, buttons.Height + row.Height);
            }
            catch { }
            return MinInputHeightPx;
        }

        public void AdjustInputBoxHeight()
        {
            if (_txtMessage == null || _pnlInput == null) return;

            // Make sure the model row fits the combo before using its height in the floor math.
            SyncModelRowHeight();

            // Available area: the right panel that hosts the transcript + input, minus any banner
            int available = GetAvailableChatAreaHeight();
            int maxHeight = Math.Max(MinInputHeightPx, (int)Math.Floor(available * 0.5));

            // Measure required height for current text within the textbox width (wrap-aware)
            int width = Math.Max(10, _txtMessage.ClientSize.Width);
            var proposed = new Size(width, int.MaxValue);
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding;
            string text = _txtMessage.Text;
            if (string.IsNullOrEmpty(text)) text = " ";
            Size measured = TextRenderer.MeasureText(text, _txtMessage.Font, proposed, flags);

            // Add a small padding to avoid clipping last line
            int desired = measured.Height + 8;

            // The right column (button row above the model row) is laid out by docking inside the
            // panel and clamps to fill it, so the panel only needs the docked stack's height as a
            // floor. This previously added btnSend/cmbModel PreferredSize, which over-reported and
            // made the panel taller than the docked button stack — leaving the gap above Send.
            int minHeight = GetMinInputHeight();

            // Clamp to [minHeight, maxHeight]
            int newHeight = Math.Max(minHeight, Math.Min(desired, maxHeight));

            // Apply height to the container panel (textbox is Dock:Fill)
            if (_pnlInput.Height != newHeight)
                _pnlInput.Height = newHeight;

            // Manage scrollbars: only show when content exceeds cap
            bool exceedsCap = desired > maxHeight;
            var newScroll = exceedsCap ? ScrollBars.Vertical : ScrollBars.None;
            if (_txtMessage.ScrollBars != newScroll)
                _txtMessage.ScrollBars = newScroll;
        }

        // Determine the vertical space shared by the transcript and input within the main chat container.
        private int GetAvailableChatAreaHeight()
        {
            try
            {
                Control container = (_splitContainer != null) ? (Control)_splitContainer.Panel2 : _mainForm;
                int h = container.ClientSize.Height;

                // Subtract the API key banner height when visible (banner is hosted in pnlBottom)
                if (_pnlApiKeyBanner != null && _pnlApiKeyBanner.Visible)
                    h = Math.Max(0, h - _pnlApiKeyBanner.Height);
                // Subtract attachments banner if present
                if (_pnlAttachmentsBanner != null && _pnlAttachmentsBanner.Visible)
                    h = Math.Max(0, h - _pnlAttachmentsBanner.Height);
                // Subtract the model-update banner if present (also hosted in pnlBottom)
                if (_pnlModelUpdateBanner != null && _pnlModelUpdateBanner.Visible)
                    h = Math.Max(0, h - _pnlModelUpdateBanner.Height);
                return Math.Max(0, h);
            }
            catch
            {
                var menuStrip = _mainForm.GetMenuStrip();
                int menuH = (menuStrip != null ? menuStrip.Height : 0);
                return Math.Max(0, _mainForm.ClientSize.Height - menuH);
            }
        }

        public void ClearInput()
        {
            try
            {
                if (_txtMessage != null)
                {
                    _txtMessage.Clear();
                    ResetInputBoxHeight();
                }
            }
            catch { }
        }

        public string GetInputText()
        {
            try
            {
                if (TextIsHint) return string.Empty;
                return (_txtMessage != null ? _txtMessage.Text : string.Empty).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        public void FocusInput()
        {
            try
            {
                if (_txtMessage == null) return;
                if (!_txtMessage.CanFocus) return;
                _txtMessage.Focus();
                // place caret at end
                try
                {
                    _txtMessage.SelectionStart = _txtMessage.TextLength;
                    _txtMessage.SelectionLength = 0;
                }
                catch { }
            }
            catch { }
        }

        public void FocusInputSoon()
        {
            try
            {
                _mainForm.BeginInvoke((MethodInvoker)(() => FocusInput()));
            }
            catch { }
        }

        public void HandleKeyDown(KeyEventArgs e)
        {
            // KeyDown is wired both here and via the InputManager's own handler; bail if already acted.
            if (e.Handled || e.SuppressKeyPress) return;
            // Let the autocomplete popup consume navigation/accept keys first.
            if (_autocomplete != null && _autocomplete.HandleKeyDown(e)) return;

            // ESC cancels editing (if active), clears input and attachments, and restores state
            if (e.KeyCode == Keys.Escape)
            {
                try
                {
                    var tm = _mainForm != null ? _mainForm.GetTabManager() : null;
                    var ctx = tm != null ? tm.GetActiveContext() : null;
                    if (ctx != null && ctx.PendingEditActive)
                    {
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        _mainForm.CancelEditingAndRestoreConversation();
                        return;
                    }
                }
                catch { }
            }
            // Ctrl+A selects all text in the input box
            if (e.Control && e.KeyCode == Keys.A)
            {
                try { if (_txtMessage != null) _txtMessage.SelectAll(); }
                catch { }
                e.SuppressKeyPress = true;
                e.Handled = true;
                return;
            }

            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                // Prevent newline and trigger send
                e.SuppressKeyPress = true;
                e.Handled = true;
                // Do not send if hint is showing
                if (TextIsHint) return;
                if (_btnSend != null) _btnSend.PerformClick();
            }
        }

        public void SetHintText()
        {
            if (_txtMessage == null) return;
            if (!string.IsNullOrEmpty(_txtMessage.Text)) return;
            // Never show hint while focused
            if (_txtMessage.Focused)
            {
                TextIsHint = false;
                _txtMessage.ForeColor = GetThemedForeColor();
                return;
            }
            _txtMessage.Text = InputHintText;
            _txtMessage.ForeColor = InputHintColor;
            TextIsHint = true;
        }

        public void RemoveHintText()
        {
            if (TextIsHint)
            {
                _txtMessage.Text = "";
                TextIsHint = false;
            }
        }
    }
}
