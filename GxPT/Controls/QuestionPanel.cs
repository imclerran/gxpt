using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // A native panel docked at the bottom of the chat area that puts a multiple-choice question from
    // the model to the user (the ask_user host tool). Single-select renders radio buttons, multi-select
    // renders checkboxes; a free-text "Other" row is always offered. The user confirms with Submit (so
    // selection and confirmation are always distinct steps) or declines with Skip.
    //
    // Threading: the tool-loop worker calls IQuestionPrompt.Ask (blocking) via TranscriptQuestionPrompt,
    // which marshals ShowQuestion onto the UI thread; Submit/Skip signals the chosen answer back. Mirrors
    // ToolApprovalPanel's worker<->UI handoff (including the disposed/recycled-tab release in DenyPending).
    internal sealed class QuestionPanel : Panel
    {
        private readonly Label _header;            // the question (wraps)
        private readonly FlowLayoutPanel _options; // option rows (radios/checks + descriptions + Other)
        private readonly FlowLayoutPanel _buttons;  // Submit / Skip
        private readonly TextBox _otherText;
        private readonly Button _submit;
        private readonly Button _skip;
        private readonly Font _descFont;   // shared by all description subtitles (created once)

        // The option selectors in display order (RadioButton when single-select, CheckBox when multi);
        // Tag carries the option label. _otherSelector is the always-present free-text row's selector.
        private readonly List<ButtonBase> _selectors = new List<ButtonBase>();
        private readonly List<Label> _descriptions = new List<Label>(); // width-managed on resize
        private ButtonBase _otherSelector;

        private Action<QuestionAnswer> _onAnswer;
        private bool _multi;
        private bool _inLayout;

        // Mirrors ToolApprovalPanel: pause the status marquee / swap the Stop button for "awaiting
        // user..." while a question is up. IsPromptVisible is the current state.
        public event EventHandler PromptVisibleChanged;
        private bool _promptVisible;
        public bool IsPromptVisible { get { return _promptVisible; } }

        private const int MinOptionsHeight = 48;
        private const int MaxOptionsHeight = 260;

        public QuestionPanel()
        {
            this.Dock = DockStyle.Bottom;
            this.Visible = false;
            this.AutoSize = false;
            this.Height = 160;
            this.Padding = new Padding(8);
            this.BorderStyle = BorderStyle.FixedSingle;

            _header = new Label();
            _header.Dock = DockStyle.Top;
            _header.AutoSize = false;
            _header.Height = 20;
            _header.Font = new Font(this.Font, FontStyle.Bold);
            _descFont = new Font(this.Font.FontFamily, Math.Max(7f, this.Font.Size - 1f));

            _options = new FlowLayoutPanel();
            _options.Dock = DockStyle.Fill;
            _options.FlowDirection = FlowDirection.TopDown;
            _options.WrapContents = false;
            _options.AutoScroll = true;

            _otherText = new TextBox();
            _otherText.Width = 240;
            _otherText.Enabled = false; // enabled only while the Other row is selected
            _otherText.TextChanged += delegate { UpdateSubmitEnabled(); };

            _buttons = new FlowLayoutPanel();
            _buttons.Dock = DockStyle.Bottom;
            _buttons.FlowDirection = FlowDirection.RightToLeft; // Submit rightmost, Skip to its left
            _buttons.WrapContents = true;
            _buttons.AutoScroll = false;
            _buttons.AutoSize = true;
            _buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _buttons.MinimumSize = new Size(0, 34);

            _submit = new Button();
            _submit.Text = "Submit";
            _submit.AutoSize = true;
            _submit.Margin = new Padding(4);
            _submit.Click += OnSubmitClicked;

            _skip = new Button();
            _skip.Text = "Skip";
            _skip.AutoSize = true;
            _skip.Margin = new Padding(4);
            _skip.Click += OnSkipClicked;

            _buttons.Controls.Add(_submit);
            _buttons.Controls.Add(_skip);

            // Fill added before docked siblings (WinForms lays out docked controls by reverse z-order),
            // matching ToolApprovalPanel's ordering so the options fill the area between header and buttons.
            this.Controls.Add(_options);
            this.Controls.Add(_header);
            this.Controls.Add(_buttons);

            ApplyTheme();
        }

        // True when the active theme is dark (same probe as ToolApprovalPanel).
        private static bool IsDark()
        {
            try
            {
                string th = AppSettings.GetString("theme");
                return !string.IsNullOrEmpty(th) && th.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // Re-color the panel and its children for the active theme, using the same palette as the
        // approval panel so the question prompt isn't stark white in dark mode. Safe to call repeatedly;
        // the host calls it again on a live light<->dark switch.
        public void ApplyTheme()
        {
            try
            {
                bool dark = IsDark();
                ThemeColors tc = ThemeService.GetColors(dark);

                this.BackColor = tc.AssistantBubbleBack;
                this.ForeColor = tc.UiForeground;
                if (_header != null) { _header.BackColor = tc.AssistantBubbleBack; _header.ForeColor = tc.UiForeground; }
                if (_options != null) _options.BackColor = tc.AssistantBubbleBack;
                if (_buttons != null) _buttons.BackColor = tc.AssistantBubbleBack;

                foreach (ButtonBase b in _selectors)
                {
                    b.BackColor = tc.AssistantBubbleBack;
                    b.ForeColor = tc.UiForeground;
                }
                // Description labels are dimmer than the option text (a subtitle), so derive a muted tone.
                foreach (Label d in _descriptions)
                {
                    d.BackColor = tc.AssistantBubbleBack;
                    d.ForeColor = MutedFore(tc, dark);
                }
                if (_otherText != null)
                {
                    _otherText.BackColor = tc.CodeBack;
                    _otherText.ForeColor = tc.UiForeground;
                }
                if (_submit != null) ApplyButtonTheme(_submit, dark, tc);
                if (_skip != null) ApplyButtonTheme(_skip, dark, tc);

                Invalidate(true);
            }
            catch { }
        }

        private static Color MutedFore(ThemeColors tc, bool dark)
        {
            Color f = tc.UiForeground;
            // Blend halfway toward the panel background for a subtitle look.
            Color b = tc.AssistantBubbleBack;
            return Color.FromArgb((f.R + b.R) / 2, (f.G + b.G) / 2, (f.B + b.B) / 2);
        }

        // Flat, theme-tinted buttons (a visual-styles button ignores BackColor), matching ToolApprovalPanel.
        private static void ApplyButtonTheme(Button b, bool dark, ThemeColors tc)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.BackColor = tc.CodeBack;
            b.ForeColor = tc.UiForeground;
            try
            {
                b.FlatAppearance.BorderColor = tc.AssistantBubbleBorder;
                b.FlatAppearance.MouseOverBackColor = tc.CopyHover;
                b.FlatAppearance.MouseDownBackColor = tc.CopyPressed;
            }
            catch { }
        }

        // Populate + show for one question. answerCallback is invoked (on the UI thread) with the user's
        // answer when they Submit, or a dismissed answer on Skip / teardown.
        public void ShowQuestion(QuestionRequest req, Action<QuestionAnswer> answerCallback)
        {
            _onAnswer = answerCallback;
            _multi = req != null && req.MultiSelect;

            _options.Controls.Clear();
            _selectors.Clear();
            _descriptions.Clear();
            _otherSelector = null;

            _header.Text = req != null ? req.Question : string.Empty;

            if (req != null && req.Options != null)
            {
                for (int i = 0; i < req.Options.Count; i++)
                {
                    QuestionOption opt = req.Options[i];
                    if (opt == null || string.IsNullOrEmpty(opt.Label)) continue;
                    ButtonBase sel = MakeSelector(opt.Label);
                    sel.Tag = opt.Label;
                    _selectors.Add(sel);
                    _options.Controls.Add(sel);
                    if (!string.IsNullOrEmpty(opt.Description))
                    {
                        Label d = MakeDescription(opt.Description);
                        _descriptions.Add(d);
                        _options.Controls.Add(d);
                    }
                }
            }

            // The always-present free-text "Other" row: selector + an inline text box.
            _otherSelector = MakeSelector("Other:");
            _otherSelector.Tag = null; // null Tag distinguishes the custom row from a preset option
            _selectors.Add(_otherSelector);
            _options.Controls.Add(_otherSelector);
            _otherText.Enabled = false;
            _otherText.Text = string.Empty;
            _options.Controls.Add(_otherText);

            UpdateSubmitEnabled();
            ApplyTheme();
            LayoutToContent();

            this.Visible = true;
            // Keep this Bottom-docked panel BEHIND the Fill transcript (same z-order rule as the approval
            // panel) so the transcript shrinks above it rather than the panel overlaying it.
            this.SendToBack();
            SetPromptVisible(true);
            FocusFirstSelector();
        }

        // Build a selector for the current mode: a RadioButton (single-select; auto-grouped because all
        // selectors share _options as their immediate parent) or a CheckBox (multi-select).
        private ButtonBase MakeSelector(string text)
        {
            ButtonBase b;
            if (_multi)
            {
                CheckBox cb = new CheckBox();
                cb.CheckedChanged += OnSelectionChanged;
                b = cb;
            }
            else
            {
                RadioButton rb = new RadioButton();
                rb.CheckedChanged += OnSelectionChanged;
                b = rb;
            }
            b.Text = text;
            b.AutoSize = true;
            b.Margin = new Padding(2, 2, 2, 0);
            return b;
        }

        private Label MakeDescription(string text)
        {
            Label d = new Label();
            d.Text = text;
            d.AutoSize = true;
            d.Margin = new Padding(22, 0, 2, 4); // indented under the selector
            d.Font = _descFont;
            return d;
        }

        private bool IsChecked(ButtonBase b)
        {
            RadioButton rb = b as RadioButton;
            if (rb != null) return rb.Checked;
            CheckBox cb = b as CheckBox;
            return cb != null && cb.Checked;
        }

        private void OnSelectionChanged(object sender, EventArgs e)
        {
            // Enable the Other text box only while its row is selected; clear it when deselected so a
            // stale custom answer can't ride along.
            if (_otherSelector != null)
            {
                bool otherOn = IsChecked(_otherSelector);
                _otherText.Enabled = otherOn;
                if (otherOn) { try { _otherText.Focus(); } catch { } }
            }
            UpdateSubmitEnabled();
        }

        // Submit is valid when the answer is well-formed: single-select needs exactly one selection (and
        // non-empty text if it's Other); multi-select needs at least one preset checked, or Other checked
        // with non-empty text. This is the guard that stops an empty "custom" answer reaching the model.
        private void UpdateSubmitEnabled()
        {
            bool otherOn = _otherSelector != null && IsChecked(_otherSelector);
            bool otherHasText = otherOn && _otherText.Text != null && _otherText.Text.Trim().Length > 0;
            int presetChecked = 0;
            for (int i = 0; i < _selectors.Count; i++)
                if (!ReferenceEquals(_selectors[i], _otherSelector) && IsChecked(_selectors[i]))
                    presetChecked++;

            bool valid;
            if (!_multi)
                valid = otherOn ? otherHasText : (presetChecked == 1);
            else
                valid = (presetChecked > 0) || otherHasText;

            _submit.Enabled = valid;
        }

        private void OnSubmitClicked(object sender, EventArgs e)
        {
            QuestionAnswer ans = new QuestionAnswer();
            ans.Selected = new List<string>();
            for (int i = 0; i < _selectors.Count; i++)
            {
                ButtonBase b = _selectors[i];
                if (ReferenceEquals(b, _otherSelector)) continue;
                if (IsChecked(b) && b.Tag is string) ans.Selected.Add((string)b.Tag);
            }
            if (_otherSelector != null && IsChecked(_otherSelector))
            {
                string t = _otherText.Text != null ? _otherText.Text.Trim() : string.Empty;
                if (t.Length > 0) ans.CustomText = t; // empty custom is dropped (UpdateSubmitEnabled guards it)
            }

            Action<QuestionAnswer> cb = _onAnswer;
            HidePanel();
            if (cb != null) cb(ans);
        }

        private void OnSkipClicked(object sender, EventArgs e)
        {
            Action<QuestionAnswer> cb = _onAnswer;
            HidePanel();
            if (cb != null) cb(QuestionAnswer.DismissedAnswer());
        }

        public void HidePanel()
        {
            this.Visible = false;
            _onAnswer = null;
            SetPromptVisible(false);
        }

        // Resolve any pending question as dismissed, then hide. Used when the prompting turn detaches
        // from this panel's tab (closed or recycled mid-prompt): the worker blocked in
        // TranscriptQuestionPrompt must be released - HidePanel alone clears the callback WITHOUT
        // invoking it, stranding that turn forever. Mirrors ToolApprovalPanel.DenyPending.
        public void DenyPending()
        {
            Action<QuestionAnswer> cb = _onAnswer;
            HidePanel();
            if (cb != null) cb(QuestionAnswer.DismissedAnswer());
        }

        private void FocusFirstSelector()
        {
            ButtonBase first = _selectors.Count > 0 ? _selectors[0] : null;
            if (first == null) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    try { if (first.IsHandleCreated && first.Visible) first.Focus(); }
                    catch { }
                });
            }
            catch
            {
                try { first.Focus(); }
                catch { }
            }
        }

        private void SetPromptVisible(bool v)
        {
            if (_promptVisible == v) return;
            _promptVisible = v;
            EventHandler h = PromptVisibleChanged;
            if (h != null)
            {
                try { h(this, EventArgs.Empty); }
                catch { }
            }
        }

        // Rebuild the panel height so the options area fits its content between bounds (short questions
        // collapse; long option lists cap and scroll), plus the wrapped header and the button strip.
        // Capped so it can't bury the transcript above it. Recomputed on resize because both the header
        // wrap and the description-label wrap depend on the current width.
        private void LayoutToContent()
        {
            if (_inLayout) return;
            _inLayout = true;
            try
            {
                int avail = this.ClientSize.Width - this.Padding.Horizontal;
                if (avail < 1) avail = (this.Parent != null ? this.Parent.ClientSize.Width : 400) - this.Padding.Horizontal;
                if (avail < 1) avail = 400;

                // Wrap the question text and the description subtitles at the live width.
                Size hsz = TextRenderer.MeasureText(_header.Text ?? string.Empty, _header.Font,
                    new Size(avail, int.MaxValue), TextFormatFlags.WordBreak);
                _header.Height = Math.Max(20, hsz.Height + 4);
                foreach (Label d in _descriptions)
                    d.MaximumSize = new Size(Math.Max(80, avail - 24), 0);

                int optionsContent = _options.GetPreferredSize(new Size(avail, 0)).Height + 6;
                int optionsH = Math.Max(MinOptionsHeight, Math.Min(MaxOptionsHeight, optionsContent));

                int buttonsH = _buttons.GetPreferredSize(new Size(avail, 0)).Height;
                if (buttonsH < 28) buttonsH = 34;

                const int border = 2; // FixedSingle: 1px top + 1px bottom
                int target = this.Padding.Top + this.Padding.Bottom + _header.Height + optionsH + buttonsH + border;

                if (this.Parent != null)
                {
                    int cap = this.Parent.ClientSize.Height - 64;
                    if (cap > 120 && target > cap) target = cap;
                }
                if (this.Height != target) this.Height = target;
            }
            finally { _inLayout = false; }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (this.Visible) LayoutToContent();
        }

        // If the panel is torn down (e.g. its tab is closed) while a question still awaits an answer,
        // resolve it as dismissed so the blocked tool-loop worker is released rather than left waiting
        // on the signal forever. Mirrors ToolApprovalPanel.Dispose.
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Action<QuestionAnswer> cb = _onAnswer;
                _onAnswer = null;
                if (cb != null)
                {
                    try { cb(QuestionAnswer.DismissedAnswer()); }
                    catch { }
                }
            }
            base.Dispose(disposing);
        }
    }
}
