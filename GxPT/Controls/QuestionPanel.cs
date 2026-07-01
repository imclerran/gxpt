using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Krypton.Toolkit;

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
        private readonly Label _counter;           // "Question X of Y", pinned bottom-left (multi-question turns)
        private readonly FlowLayoutPanel _options; // option rows (radios/checks + descriptions + Other)
        private readonly FlowLayoutPanel _buttons;  // Submit / Skip
        private readonly TextBox _otherText;
        private readonly KryptonButton _submit;
        private readonly KryptonButton _skip;
        private Font _headerFont;          // bold header font, rebuilt per show from the live UI font
        private Font _uiFont;              // panel-created UI font assigned to this.Font (NOT the inherited one)

        // Tracks the Other text box's empty state so multi-select can auto-(un)check on the
        // empty<->non-empty transition only (leaving manual toggles untouched), and a build guard so
        // populating the panel doesn't fire the auto-select/toggle side effects.
        private bool _otherWasEmpty = true;
        private bool _building;

        // Left indent (px) for content that should align under an option's TITLE text rather than its
        // radio/checkbox glyph: the description subtitles and the Other text box. Approximates the glyph
        // width plus the selector's left margin.
        private const int LabelIndent = 18;

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
            // _header.Font (bold) is built per show in ShowQuestion from the live UI font, so it tracks
            // the user-chosen font size; all other controls inherit this.Font ambiently.

            // "Question X of Y" indicator, overlaid on the panel's bottom-left corner (not docked, on the
            // button row), shown only when the model asked several questions this turn. Kept off the
            // header row so it never steals width from the (wrapping) question text.
            _counter = new Label();
            _counter.AutoSize = true;
            _counter.Visible = false;

            _options = new FlowLayoutPanel();
            _options.Dock = DockStyle.Fill;
            _options.FlowDirection = FlowDirection.TopDown;
            _options.WrapContents = false;
            _options.AutoScroll = true;

            // The Other text box is always enabled so the user can click/type in it directly: focusing it
            // selects the Other option (single-select), and typing auto-checks it (multi-select). Aligned
            // under the option title text (LabelIndent); width is set to the full panel in LayoutToContent.
            _otherText = new TextBox();
            _otherText.Margin = new Padding(LabelIndent, 2, 8, 4);
            _otherText.TextChanged += OnOtherTextChanged;
            _otherText.Enter += OnOtherTextEnter;

            _buttons = new FlowLayoutPanel();
            _buttons.Dock = DockStyle.Bottom;
            _buttons.FlowDirection = FlowDirection.RightToLeft; // Submit rightmost, Skip to its left
            _buttons.WrapContents = true;
            _buttons.AutoScroll = false;
            _buttons.AutoSize = true;
            _buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _buttons.MinimumSize = new Size(0, 34);

            _submit = new KryptonButton();
            _submit.Text = "Submit";
            _submit.AutoSize = true;
            _submit.Margin = new Padding(4);
            _submit.Click += OnSubmitClicked;

            _skip = new KryptonButton();
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
            this.Controls.Add(_counter); // overlay; brought to front when shown

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
                if (_counter != null) { _counter.BackColor = tc.AssistantBubbleBack; _counter.ForeColor = MutedFore(tc, dark); }
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
                // _submit / _skip are KryptonButtons - they theme themselves from the active palette.

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

        // Populate + show for one question. answerCallback is invoked (on the UI thread) with the user's
        // answer when they Submit, or a dismissed answer on Skip / teardown.
        public void ShowQuestion(QuestionRequest req, Action<QuestionAnswer> answerCallback)
        {
            _onAnswer = answerCallback;
            _multi = req != null && req.MultiSelect;

            // Adopt the user-chosen font size before building, so every control (which inherits this.Font
            // ambiently) and the bold header below render at that size.
            ApplyUserFont();
            Font oldHeader = _headerFont;
            _headerFont = new Font(this.Font, FontStyle.Bold);
            _header.Font = _headerFont;
            if (oldHeader != null) { try { oldHeader.Dispose(); } catch { } }

            // Suppress the Other-row auto-select/toggle side effects while we (re)populate the panel.
            _building = true;

            // Dispose the prior question's selector/description controls before clearing - Controls.Clear
            // only detaches, it does not Dispose, so without this each shown question would leak its
            // controls' window handles. The reused _otherText is a field (never in these lists), so it
            // survives the clear and is re-added below.
            for (int i = 0; i < _selectors.Count; i++)
                if (_selectors[i] != null) { try { _selectors[i].Dispose(); } catch { } }
            for (int i = 0; i < _descriptions.Count; i++)
                if (_descriptions[i] != null) { try { _descriptions[i].Dispose(); } catch { } }
            _options.Controls.Clear();
            _selectors.Clear();
            _descriptions.Clear();
            _otherSelector = null;

            _header.Text = req != null ? req.Question : string.Empty;
            UpdateCounter(req != null ? req.Position : 1, req != null ? req.Total : 1);

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
            _otherText.Text = string.Empty;
            _otherWasEmpty = true;
            _options.Controls.Add(_otherText);

            _building = false;
            UpdateSubmitEnabled();
            ApplyTheme();
            LayoutToContent();

            this.Visible = true;
            // Keep this Bottom-docked panel BEHIND the Fill transcript (same z-order rule as the approval
            // panel) so the transcript shrinks above it rather than the panel overlaying it.
            this.SendToBack();
            SetPromptVisible(true);
            FocusFirstSelector();

            // Reposition the counter once the docked layout has settled (the button strip's bounds are
            // only final after layout). The early PositionCounter in UpdateCounter may have run against
            // stale/zero button bounds; this deferred pass lands it on the button row. Guarded by
            // visibility so a single-question panel does no work.
            if (_counter != null && _counter.Visible)
            {
                try { BeginInvoke((MethodInvoker)delegate { try { PositionCounter(); } catch { } }); }
                catch { }
            }
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
            d.Margin = new Padding(LabelIndent, 0, 2, 4); // aligned under the option title text
            // No explicit Font: inherits this.Font (the user-chosen size); distinguished by a muted color.
            return d;
        }

        private bool IsChecked(ButtonBase b)
        {
            RadioButton rb = b as RadioButton;
            if (rb != null) return rb.Checked;
            CheckBox cb = b as CheckBox;
            return cb != null && cb.Checked;
        }

        private void SetChecked(ButtonBase b, bool value)
        {
            RadioButton rb = b as RadioButton;
            if (rb != null) { rb.Checked = value; return; }
            CheckBox cb = b as CheckBox;
            if (cb != null) cb.Checked = value;
        }

        private void OnSelectionChanged(object sender, EventArgs e)
        {
            if (_building) return;
            // When the Other row becomes selected, focus its text box so the user can type immediately.
            // The text box stays enabled regardless, so typing/clicking it can also drive the selection
            // (see OnOtherTextEnter / OnOtherTextChanged); we never clear the text on deselect, so a
            // manual toggle off and back on keeps what they typed.
            if (_otherSelector != null && ReferenceEquals(sender, _otherSelector) && IsChecked(_otherSelector))
            {
                try { _otherText.Focus(); }
                catch { }
            }
            UpdateSubmitEnabled();
        }

        // Single-select: focusing (clicking or tabbing into) the Other text box selects the Other radio,
        // so the user doesn't have to click the radio separately. Multi-select intentionally does NOT
        // check on focus alone - only typing does (OnOtherTextChanged) - so the user can leave it unchecked.
        private void OnOtherTextEnter(object sender, EventArgs e)
        {
            if (_building) return;
            if (!_multi && _otherSelector != null && !IsChecked(_otherSelector))
                SetChecked(_otherSelector, true);
        }

        // Multi-select: typing the first character auto-checks the Other box and deleting the last
        // character auto-unchecks it (the empty<->non-empty transition only), while a manual toggle with
        // text present is preserved. Single-select tracks emptiness for Submit validation only.
        private void OnOtherTextChanged(object sender, EventArgs e)
        {
            if (_building) { UpdateSubmitEnabled(); return; }
            bool nowEmpty = string.IsNullOrEmpty(_otherText.Text);
            if (_multi && _otherSelector != null)
            {
                if (_otherWasEmpty && !nowEmpty && !IsChecked(_otherSelector))
                    SetChecked(_otherSelector, true);
                else if (!_otherWasEmpty && nowEmpty && IsChecked(_otherSelector))
                    SetChecked(_otherSelector, false);
            }
            _otherWasEmpty = nowEmpty;
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

        // Show/hide the bottom-left "Question X of Y" indicator (only when the model asked more than one
        // question this turn). Deliberately not on the header row, so it never reserves width from the
        // wrapping question text.
        private void UpdateCounter(int position, int total)
        {
            if (_counter == null) return;
            bool show = total > 1 && position >= 1;
            _counter.Visible = show;
            if (show)
            {
                _counter.Text = "Question " + position + " of " + total;
                PositionCounter();
                _counter.BringToFront(); // paint over the docked button strip's empty left area
            }
        }

        // Pin the counter to the panel's lower-left, vertically centered on the button strip so it lines
        // up with Submit/Skip. Falls back to bottom-aligned if the strip isn't laid out yet. Re-run on
        // resize; positioning a hidden label is harmless.
        private void PositionCounter()
        {
            if (_counter == null) return;
            int h = _counter.PreferredSize.Height;
            int x = this.Padding.Left;
            int y;
            if (_buttons != null && _buttons.Height > 0)
                y = _buttons.Top + Math.Max(0, (_buttons.Height - h) / 2);
            else
                y = this.ClientSize.Height - this.Padding.Bottom - h;
            if (y < this.Padding.Top) y = this.Padding.Top;
            _counter.Location = new Point(x, y);
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
                    d.MaximumSize = new Size(Math.Max(80, avail - LabelIndent - 8), 0);

                // The Other text box spans the full content width (minus its indent + a right gap), using
                // the options area's client width when available so a vertical scrollbar is accounted for.
                if (_otherText != null)
                {
                    int baseW = _options.ClientSize.Width > 10 ? _options.ClientSize.Width : avail;
                    int otherW = baseW - LabelIndent - 8;
                    if (otherW < 80) otherW = 80;
                    if (_otherText.Width != otherW) _otherText.Width = otherW;
                }

                // If any option is wider than the content area, the options panel shows a horizontal
                // scrollbar. Reserve its height so the scrollbar doesn't eat into the content area and
                // force a (redundant) vertical scrollbar too - grow the panel instead. Compared against
                // the no-scrollbar content width (avail); descriptions wrap (MaximumSize) and the Other
                // box is sized to fit, so only a long single-line option can overflow.
                // Measure regardless of Control.Visible: this runs from ShowQuestion while the panel is
                // still hidden (so children report Visible==false), and PreferredSize is valid anyway.
                int widest = 0;
                foreach (Control c in _options.Controls)
                {
                    if (c == null) continue;
                    int w = c.PreferredSize.Width + c.Margin.Horizontal;
                    if (w > widest) widest = w;
                }
                int hScroll = (widest > avail) ? SystemInformation.HorizontalScrollBarHeight : 0;

                // Raise the cap by the scrollbar's height when one is reserved, so content that was just
                // under the cap doesn't get clamped below (content + scrollbar) and force a vertical
                // scrollbar anyway - the whole point of reserving hScroll.
                int optionsContent = _options.GetPreferredSize(new Size(avail, 0)).Height + 6 + hScroll;
                int optionsH = Math.Max(MinOptionsHeight, Math.Min(MaxOptionsHeight + hScroll, optionsContent));

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
            PositionCounter(); // keep the counter anchored to the (now moved) right edge
        }

        // Set this.Font to the user-chosen UI font size (settings.json font_size), the same clamp the
        // rest of the UI uses (ThemeManager). Child controls without their own Font inherit it ambiently,
        // so the whole panel renders at that size. No-op when the setting is unset (keeps the inherited
        // page font).
        private void ApplyUserFont()
        {
            try
            {
                double fs = AppSettings.GetDouble("font_size", 0);
                if (fs <= 0) return;
                float size = (float)Math.Max(6, Math.Min(48, fs));
                // Control.Font is never null (ambient default), so no null-guard needed here.
                if (Math.Abs(this.Font.Size - size) <= 0.01f) return; // already the right size
                Font created = new Font(this.Font.FontFamily, size, this.Font.Style);
                this.Font = created;
                // Dispose only a font WE created previously - never the first inherited/ambient font
                // (_uiFont starts null, so the inherited one is left alone).
                if (_uiFont != null) { try { _uiFont.Dispose(); } catch { } }
                _uiFont = created;
            }
            catch { }
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
                if (_headerFont != null) { try { _headerFont.Dispose(); } catch { } _headerFont = null; }
                if (_uiFont != null) { try { _uiFont.Dispose(); } catch { } _uiFont = null; }
            }
            base.Dispose(disposing);
        }
    }
}
