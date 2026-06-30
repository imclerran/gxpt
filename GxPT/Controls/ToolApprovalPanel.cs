using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace GxPT
{
    // A native panel docked at the bottom of the chat area that asks the user to approve a pending
    // MCP tool call (approval spec §4, rendered in-transcript rather than as a modal). Shown only
    // while a call awaits a decision; the buttons offered depend on the tool's remember scope.
    //
    // Threading: the tool-loop worker calls Ask (blocking) via TranscriptApprovalPrompt, which
    // marshals ShowFor onto the UI thread; the user's button click signals back the chosen result.
    internal sealed class ToolApprovalPanel : Panel
    {
        private readonly Label _header;
        private readonly Label _tierBadge;
        private readonly Label _previewLabel;
        private readonly TextBox _preview;
        private readonly DiffPreviewPanel _diffPanel;   // shown instead of _preview for files__edit
        private readonly Font _monoFont;
        private readonly FlowLayoutPanel _buttons;
        private readonly ToolTip _toolTip;

        private Action<ApprovalChoice> _onChoose;
        private Action<bool> _onContinue;   // set instead of _onChoose for the iteration-cap prompt

        // Raised (on the UI thread) when a prompt starts or stops awaiting the user's decision, so the
        // host can reflect it in the status bar: pause the generation marquee and swap the Stop button
        // for an "awaiting user..." label while a prompt is up. IsPromptVisible is the current state.
        public event EventHandler PromptVisibleChanged;
        private bool _promptVisible;
        public bool IsPromptVisible { get { return _promptVisible; } }

        // Supplies the active workspace root so an edit approval can show a few lines of real file
        // context around the change. Set by the host (MainForm); null disables context (bare diff).
        public Func<string> WorkingDirProvider;

        // The details area (diff/preview) is sized to its content between these bounds: short prompts
        // (e.g. a one-line command) collapse instead of leaving a tall empty box, while long content
        // is capped here and scrolls. See LayoutToContent.
        private const int MinDetailsHeight = 24;
        private const int MaxDetailsHeight = 200;

        // Clamped content height for the current prompt; the panel height is rebuilt from this plus
        // the (possibly multi-row) button strip whenever the panel resizes. _handledDetails records
        // whether the active prompt uses the diff panel (vs the raw preview) so the height can be
        // re-measured at the new width on resize. Re-entrancy guard stops the height we set from
        // recursing through OnSizeChanged.
        private int _detailsHeight = MinDetailsHeight;
        private bool _handledDetails;
        private bool _inLayoutToContent;

        // The current prompt's tier, remembered so ApplyTheme can re-tint the tier badge (its color is
        // semantic) when the theme switches live. The continuation prompt reuses the Write color.
        private ToolTier _currentTier = ToolTier.Write;

        // The button to focus once the panel is shown (the tier's default action). Focus is deferred to
        // after Visible = true: focusing while the panel is still hidden does nothing, so GotFocus never
        // fires and the initial blue focus border was missing until the user tabbed.
        private Button _defaultButton;

        // A small "Explain" affordance pinned to the panel's top-right corner, shown only while a
        // command-style tool (command__run or a discovered PowerShell tool) awaits approval. Clicking it
        // asks the host to open a fresh chat tab that explains the pending command - it does NOT resolve
        // the approval, which stays up for the user to Allow/Deny. The captured command + whether it's
        // PowerShell are remembered so the deferred click knows what to explain.
        private readonly Button _explainButton;
        private string _explainCommand;
        private bool _explainIsPowerShell;

        // Raised (on the UI thread) when the user clicks Explain for the current command tool. The host
        // (MainForm) opens a new tab seeded with an "explain this command" prompt, carrying over the
        // source conversation's model + ZDR setting. Arguments: the command line, and whether it is a
        // PowerShell (vs cmd) command so the prompt and code fence match. Null = no handler wired.
        public Action<string, bool> ExplainRequested;

        public ToolApprovalPanel()
        {
            this.Dock = DockStyle.Bottom;
            this.Visible = false;
            this.AutoSize = false;
            this.Height = 150;
            this.Padding = new Padding(8);
            this.BorderStyle = BorderStyle.FixedSingle;

            _header = new Label();
            _header.Dock = DockStyle.Top;
            _header.Height = 20;
            _header.Font = new Font(this.Font, FontStyle.Bold);
            _header.AutoEllipsis = true;

            _tierBadge = new Label();
            _tierBadge.Dock = DockStyle.Top;
            _tierBadge.Height = 18;

            _previewLabel = new Label();
            _previewLabel.Dock = DockStyle.Top;
            _previewLabel.Height = 16;
            _previewLabel.Text = "Details:";

            _monoFont = new Font("Consolas", 9F);

            _preview = new TextBox();
            _preview.Multiline = true;
            _preview.ReadOnly = true;
            _preview.ScrollBars = ScrollBars.Vertical;
            _preview.Dock = DockStyle.Fill;
            _preview.Font = _monoFont;

            _diffPanel = new DiffPreviewPanel();
            _diffPanel.Dock = DockStyle.Fill;
            _diffPanel.Visible = false;

            _buttons = new RightAlignedFlowLayoutPanel();
            _buttons.Dock = DockStyle.Bottom;
            // LeftToRight flow (the buttons are inserted in reverse so the visual order is still
            // Allow…Deny left-to-right; see AddButton). A FlowLayoutPanel always wraps the control at
            // the END of its flow first, so LeftToRight makes the RIGHT-most buttons (Deny side) drop
            // to the new row when the window is too narrow, rather than the left-most ones. The
            // RightAlignedFlowLayoutPanel then shifts every row flush to the right edge, so the strip
            // stays right-aligned whether it occupies one row or several.
            _buttons.FlowDirection = FlowDirection.LeftToRight;
            // Wrap onto extra rows when the window is too narrow to fit every button on one line, and
            // AutoSize so the strip grows upward (shrinking the preview above) to keep them all
            // visible. The old fixed-height, no-wrap, AutoScroll setup let a horizontal scrollbar
            // appear on resize and slice the buttons' bottoms off — and the scroll offset persisted,
            // so they stayed cut off after the resize.
            _buttons.WrapContents = true;
            _buttons.AutoScroll = false;
            _buttons.AutoSize = true;
            _buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _buttons.MinimumSize = new Size(0, 34);

            _toolTip = new ToolTip();
            _toolTip.AutoPopDelay = 15000; // keep the multi-line rule explanation visible long enough to read

            // The Explain affordance: a flat chrome button overlaid on the top-right corner (not docked),
            // shown only for command tools. AutoSize so it fits "Explain" at any font/DPI; positioned and
            // brought to the front in PositionExplainButton once a command prompt is shown.
            _explainButton = new Button();
            _explainButton.Text = "Explain";
            _explainButton.AutoSize = true;
            _explainButton.Visible = false;
            _explainButton.Margin = new Padding(0);
            // Keep Explain at the end of the tab cycle so it never intercepts focus ahead of the
            // Allow/Deny strip (which owns the keyboard default). It lives on the panel, not in the
            // _buttons strip that SetButtonTabOrder manages, so it needs its own high TabIndex.
            _explainButton.TabIndex = 1000;
            _explainButton.Click += OnExplainClicked;
            _explainButton.GotFocus += OnButtonFocusChanged;
            _explainButton.LostFocus += OnButtonFocusChanged;

            // Order added (Fill must be added before docked siblings to lay out correctly):
            this.Controls.Add(_diffPanel);
            this.Controls.Add(_preview);
            this.Controls.Add(_previewLabel);
            this.Controls.Add(_tierBadge);
            this.Controls.Add(_header);
            this.Controls.Add(_buttons);
            this.Controls.Add(_explainButton);

            // Theme the chrome up front so the panel isn't a stark white block before the first prompt.
            ApplyTheme();
        }

        // True when the active theme is dark. Mirrors AgentActivityPanel.IsDark.
        private static bool IsDark()
        {
            try
            {
                string th = AppSettings.GetString("theme");
                return !string.IsNullOrEmpty(th) && th.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // The tier badge color, brightened in dark mode so it stays legible against the dark panel
        // (matching how the agents panel lightens its status colors).
        private static Color TierColor(ToolTier tier, bool dark)
        {
            switch (tier)
            {
                case ToolTier.Destructive: return dark ? Color.FromArgb(240, 120, 120) : Color.Firebrick;
                case ToolTier.Write: return dark ? Color.FromArgb(226, 184, 80) : Color.DarkGoldenrod;
                default: return dark ? Color.FromArgb(126, 204, 126) : Color.ForestGreen;
            }
        }

        // Re-color the panel and all its child controls for the active theme, using the same palette as
        // the agents activity panel (ThemeService), so the approval prompt isn't stark white in dark
        // mode. Safe to call repeatedly: the host calls it again on a live light<->dark switch (see
        // MainForm.ApplyThemeToAllApprovalPanels), and it re-tints the already-rendered diff/preview.
        public void ApplyTheme()
        {
            try
            {
                bool dark = IsDark();
                ThemeColors tc = ThemeService.GetColors(dark);

                this.BackColor = tc.AssistantBubbleBack;
                this.ForeColor = tc.UiForeground;

                if (_header != null) { _header.BackColor = tc.AssistantBubbleBack; _header.ForeColor = tc.UiForeground; }
                if (_tierBadge != null) { _tierBadge.BackColor = tc.AssistantBubbleBack; _tierBadge.ForeColor = TierColor(_currentTier, dark); }
                if (_previewLabel != null) { _previewLabel.BackColor = tc.AssistantBubbleBack; _previewLabel.ForeColor = tc.UiForeground; }
                // The raw-JSON fallback preview reads like a code block, so use the code palette.
                if (_preview != null) { _preview.BackColor = tc.CodeBack; _preview.ForeColor = tc.UiForeground; }

                if (_buttons != null)
                {
                    _buttons.BackColor = tc.AssistantBubbleBack;
                    foreach (Control c in _buttons.Controls)
                    {
                        Button b = c as Button;
                        if (b != null) ApplyButtonTheme(b, dark, tc);
                    }
                }

                // The overlaid Explain button shares the flat, theme-tinted button styling.
                if (_explainButton != null) ApplyButtonTheme(_explainButton, dark, tc);

                // Re-tint the syntax-highlighted diff/preview when it's the visible details control, so a
                // live theme switch updates it too (it was themed once at SetContent time).
                if (_diffPanel != null && _diffPanel.Visible)
                    _diffPanel.ReapplyTheme(dark, tc.CodeBack, tc.UiForeground);

                Invalidate(true);
            }
            catch { }
        }

        // Flat, theme-tinted buttons in both light and dark mode so the two themes match (a native
        // visual-styles button ignores BackColor, so flat is the only way to tint it consistently).
        // The Deny button is called out with a red (firebrick) border and text.
        private static void ApplyButtonTheme(Button b, bool dark, ThemeColors tc)
        {
            bool isDeny = (b.Tag is ApprovalChoice) && ((ApprovalChoice)b.Tag == ApprovalChoice.Deny);
            Color red = TierColor(ToolTier.Destructive, dark);

            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.BackColor = tc.CodeBack;
            b.ForeColor = isDeny ? red : tc.UiForeground;
            try
            {
                b.FlatAppearance.BorderColor = ButtonBorderColor(b, dark, tc);
                b.FlatAppearance.MouseOverBackColor = tc.CopyHover;
                b.FlatAppearance.MouseDownBackColor = tc.CopyPressed;
            }
            catch { }
        }

        // The border color for a button in its current focus state: a focused non-Deny button gets a blue
        // border so the keyboard default is obvious - the flat button's subtle panel border barely
        // changed when focused. A fixed blue (lightened in dark mode) rather than the theme accent, since
        // some themes' accent is red/orange. Deny stays red whether focused or not.
        private static Color ButtonBorderColor(Button b, bool dark, ThemeColors tc)
        {
            bool isDeny = (b.Tag is ApprovalChoice) && ((ApprovalChoice)b.Tag == ApprovalChoice.Deny);
            if (isDeny) return TierColor(ToolTier.Destructive, dark);
            if (!b.Focused) return tc.AssistantBubbleBorder;
            return dark ? Color.FromArgb(120, 170, 255) : Color.FromArgb(0, 102, 204);
        }

        // Re-tint a button's border as it gains/loses focus (wired on every button), so the blue focus
        // cue follows the keyboard default as the user tabs across the strip.
        private void OnButtonFocusChanged(object sender, EventArgs e)
        {
            Button b = sender as Button;
            if (b == null) return;
            try
            {
                bool dark = IsDark();
                b.FlatAppearance.BorderColor = ButtonBorderColor(b, dark, ThemeService.GetColors(dark));
                b.Invalidate();
            }
            catch { }
        }

        // Order the button strip so Tab moves left->right and Shift+Tab right->left (e.g. Allow->Deny).
        // Buttons are inserted front-first (see AddButton), so with the LeftToRight flow a button's
        // index in the Controls collection already matches its visual left-to-right position; assign
        // TabIndex to follow that order.
        private void SetButtonTabOrder()
        {
            if (_buttons == null) return;
            int n = _buttons.Controls.Count;
            for (int i = 0; i < n; i++)
                _buttons.Controls[i].TabIndex = i;
        }

        // Populate + show for one request. choiceCallback is invoked (on the UI thread) with the
        // user's decision. Builds the scope-appropriate buttons per approval spec §4.
        public void ShowFor(ApprovalRequest req, Action<ApprovalChoice> choiceCallback)
        {
            _onChoose = choiceCallback;
            // Cleared up front; the command-tool branch below re-arms it when this is a command prompt.
            _explainCommand = null;
            _explainIsPowerShell = false;

            _header.Text = (req.ServerName != null ? req.ServerName : "?") + "  ·  " +
                           (req.ToolName != null ? req.ToolName : req.FunctionName);

            ToolTier tier = req.Policy != null ? req.Policy.Tier : ToolTier.Write;
            _currentTier = tier;
            _tierBadge.Text = "Tier: " + tier;

            // Theme palette for this prompt (the tier badge and diff/preview colors derive from it);
            // ApplyTheme below colors the rest of the chrome.
            bool dark = IsDark();
            ThemeColors tc = ThemeService.GetColors(dark);

            // files__edit -> a colored diff (with a little live file context); command__run -> the
            // command line, syntax-highlighted. Either replaces the raw JSON preview.
            bool handled = false;
            if (req.Arguments != null)
            {
                if (string.Equals(req.FunctionName, "files__edit", StringComparison.Ordinal))
                {
                    string path = req.Arguments.Value<string>("path") ?? string.Empty;
                    string oldS = req.Arguments.Value<string>("old_string") ?? string.Empty;
                    string newS = req.Arguments.Value<string>("new_string") ?? string.Empty;
                    string workdir = WorkingDirProvider != null ? WorkingDirProvider() : null;
                    string fileText = ReadWorkspaceFile(workdir, path);
                    LineDiffResult diff = DiffUtil.BuildLineDiffWithContext(fileText, oldS, newS, 3);
                    _diffPanel.SetContent(path, diff.Body, "diff", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                    _previewLabel.Text = "Diff:";
                    handled = true;
                }
                else if (string.Equals(req.FunctionName, "extensions__edit_skill_file", StringComparison.Ordinal))
                {
                    // Same treatment as files__edit: a colored diff of the change. The skill file lives
                    // outside the workspace, so there's no live file context to fold in - diff the
                    // old/new spans directly. Header carries the slug + relpath.
                    string slug = req.Arguments.Value<string>("slug") ?? string.Empty;
                    string rel = req.Arguments.Value<string>("relpath") ?? string.Empty;
                    string oldS = req.Arguments.Value<string>("old_string") ?? string.Empty;
                    string newS = req.Arguments.Value<string>("new_string") ?? string.Empty;
                    string target = (slug.Length > 0 && rel.Length > 0) ? (slug + "/" + rel)
                                  : (rel.Length > 0 ? rel : slug);
                    LineDiffResult diff = DiffUtil.BuildLineDiff(oldS, newS);
                    _diffPanel.SetContent(target, diff.Body, "diff", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                    _previewLabel.Text = "Diff:";
                    handled = true;
                }
                else if (string.Equals(req.FunctionName, "extensions__create_skill", StringComparison.Ordinal)
                      || string.Equals(req.FunctionName, "extensions__update_skill", StringComparison.Ordinal))
                {
                    // Show the skill's authored fields (name/description/instructions) as readable
                    // markdown rather than raw JSON. update_skill carries only the fields being changed.
                    string slug = req.Arguments.Value<string>("slug") ?? string.Empty;
                    string text = BuildSkillFields(req.Arguments);
                    _diffPanel.SetContent(slug, text, "markdown", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                    _previewLabel.Text = string.Equals(req.FunctionName, "extensions__create_skill", StringComparison.Ordinal)
                        ? "Create skill:" : "Update skill:";
                    handled = true;
                }
                else if (string.Equals(req.FunctionName, "extensions__write_skill_file", StringComparison.Ordinal))
                {
                    // Mirror files__write: the file content, highlighted by its extension.
                    string slug = req.Arguments.Value<string>("slug") ?? string.Empty;
                    string rel = req.Arguments.Value<string>("relpath") ?? string.Empty;
                    string content = req.Arguments.Value<string>("content") ?? string.Empty;
                    string lang = (rel.Length > 0 ? SyntaxHighlighter.GetLanguageForFileName(rel) : null) ?? "text";
                    _diffPanel.SetContent(SkillFileTarget(slug, rel), content, lang, dark, _monoFont, tc.CodeBack, tc.UiForeground);
                    _previewLabel.Text = "Write skill file:";
                    handled = true;
                }
                else if (string.Equals(req.FunctionName, "extensions__run_skill_script", StringComparison.Ordinal))
                {
                    // Mirror command__run: the script and its literal arguments, with the owning skill in
                    // the header so the per-skill remember scope is clear before approving.
                    string slug = req.Arguments.Value<string>("slug") ?? string.Empty;
                    string rel = req.Arguments.Value<string>("relpath") ?? string.Empty;
                    string scriptArgs = PathsOf(req.Arguments, "args");
                    string text = (rel + (scriptArgs.Length > 0 ? " " + scriptArgs : "")).Trim();
                    if (text.Length > 0)
                    {
                        _diffPanel.SetContent(slug, text, "batch", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                        _previewLabel.Text = "Run skill script:";
                        handled = true;
                    }
                }
                else if (string.Equals(req.FunctionName, "extensions__delete_skill_file", StringComparison.Ordinal))
                {
                    string slug = req.Arguments.Value<string>("slug") ?? string.Empty;
                    string rel = req.Arguments.Value<string>("relpath") ?? string.Empty;
                    _diffPanel.SetContent(string.Empty, SkillFileTarget(slug, rel), "text", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                    _previewLabel.Text = "Delete skill file:";
                    handled = true;
                }
                else if (string.Equals(req.FunctionName, "extensions__delete_skill", StringComparison.Ordinal))
                {
                    string slug = req.Arguments.Value<string>("slug") ?? string.Empty;
                    if (slug.Length > 0)
                    {
                        _diffPanel.SetContent(string.Empty, slug, "text", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                        _previewLabel.Text = "Delete skill:";
                        handled = true;
                    }
                }
                else if (string.Equals(req.FunctionName, "extensions__create_agent", StringComparison.Ordinal)
                      || string.Equals(req.FunctionName, "extensions__update_agent", StringComparison.Ordinal))
                {
                    // Show the agent's authored fields (name/description/system prompt) as readable markdown
                    // rather than raw JSON. update_agent carries only the fields being changed.
                    string slug = req.Arguments.Value<string>("slug") ?? string.Empty;
                    string text = BuildSkillFields(req.Arguments);
                    _diffPanel.SetContent(slug, text, "markdown", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                    _previewLabel.Text = string.Equals(req.FunctionName, "extensions__create_agent", StringComparison.Ordinal)
                        ? "Create agent:" : "Update agent:";
                    handled = true;
                }
                else if (string.Equals(req.FunctionName, "extensions__edit_agent", StringComparison.Ordinal))
                {
                    // Same treatment as edit_skill_file: a colored diff of the change to the agent's body.
                    string slug = req.Arguments.Value<string>("slug") ?? string.Empty;
                    string oldS = req.Arguments.Value<string>("old_string") ?? string.Empty;
                    string newS = req.Arguments.Value<string>("new_string") ?? string.Empty;
                    LineDiffResult diff = DiffUtil.BuildLineDiff(oldS, newS);
                    _diffPanel.SetContent(slug, diff.Body, "diff", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                    _previewLabel.Text = "Diff:";
                    handled = true;
                }
                else if (string.Equals(req.FunctionName, "extensions__delete_agent", StringComparison.Ordinal))
                {
                    string slug = req.Arguments.Value<string>("slug") ?? string.Empty;
                    if (slug.Length > 0)
                    {
                        _diffPanel.SetContent(string.Empty, slug, "text", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                        _previewLabel.Text = "Delete agent:";
                        handled = true;
                    }
                }
                else if (string.Equals(req.FunctionName, "extensions__rename_skill", StringComparison.Ordinal)
                      || string.Equals(req.FunctionName, "extensions__rename_agent", StringComparison.Ordinal))
                {
                    // Show the handle change (old slug -> new slug), plus the new name when one was given.
                    string slug = req.Arguments.Value<string>("slug") ?? string.Empty;
                    string ns = req.Arguments.Value<string>("new_slug") ?? string.Empty;
                    string nn = req.Arguments.Value<string>("new_name") ?? string.Empty;
                    if (slug.Length > 0 && ns.Length > 0)
                    {
                        string text = slug + " -> " + ns + (nn.Length > 0 ? "  (name: " + nn + ")" : "");
                        _diffPanel.SetContent(string.Empty, text, "text", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                        _previewLabel.Text = string.Equals(req.FunctionName, "extensions__rename_skill", StringComparison.Ordinal)
                            ? "Rename skill:" : "Rename agent:";
                        handled = true;
                    }
                }
                else if (string.Equals(req.FunctionName, "command__run", StringComparison.Ordinal)
                      || McpConfig.IsPowerShellTool(req.FunctionName))
                {
                    // command__run and the discovered PowerShell tools render identically: the command/
                    // script itself (highlighted), with the "command pattern" signature surfaced next to
                    // it when it differs from the full line (i.e. flags/args were dropped) so the broader
                    // allow-scope is visible without hovering the button's tooltip. Only the highlight
                    // language and label prefix differ between cmd and PowerShell.
                    bool isPs = McpConfig.IsPowerShellTool(req.FunctionName);
                    string cmd = req.Arguments.Value<string>("command") ?? string.Empty;
                    if (cmd.Trim().Length > 0)
                    {
                        _diffPanel.SetContent(string.Empty, cmd, isPs ? "powershell" : "batch", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                        string label = isPs ? "PowerShell" : "Command";
                        string sig = ToolApprovalPolicy.CommandSignature(cmd);
                        _previewLabel.Text =
                            (!string.IsNullOrEmpty(sig) && !string.Equals(sig, cmd.Trim(), StringComparison.Ordinal))
                            ? label + "   (pattern: " + sig + ")"
                            : label + ":";
                        handled = true;
                        // Remember the command so the Explain button (shown below) can open a tab that
                        // describes it.
                        _explainCommand = cmd;
                        _explainIsPowerShell = isPs;
                    }
                }
                else if (string.Equals(req.FunctionName, "files__write", StringComparison.Ordinal))
                {
                    string path = req.Arguments.Value<string>("path") ?? string.Empty;
                    string content = req.Arguments.Value<string>("content") ?? string.Empty;
                    string lang = SyntaxHighlighter.GetLanguageForFileName(path) ?? "text";
                    _diffPanel.SetContent(path, content, lang, dark, _monoFont, tc.CodeBack, tc.UiForeground);
                    _previewLabel.Text = "Write:";
                    handled = true;
                }
                else if (string.Equals(req.FunctionName, "git__commit", StringComparison.Ordinal))
                {
                    string msg = req.Arguments.Value<string>("message") ?? string.Empty;
                    if (msg.Trim().Length > 0)
                    {
                        _diffPanel.SetContent(string.Empty, msg, "text", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                        // Note when commit will stage everything first (git add -A), so it isn't a surprise.
                        _previewLabel.Text = Bv(req.Arguments, "all")
                            ? "Commit message (staging all changes):" : "Commit message:";
                        handled = true;
                    }
                }
                else if (string.Equals(req.FunctionName, "web__extract", StringComparison.Ordinal))
                {
                    string urls = JoinUrlArgs(req.Arguments);
                    if (urls.Length > 0)
                    {
                        _diffPanel.SetContent(string.Empty, urls, "text", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                        _previewLabel.Text = "Fetch URLs:";
                        handled = true;
                    }
                }
                else if (string.Equals(req.FunctionName, "web__http", StringComparison.Ordinal))
                {
                    // web__http is the state-changing tool (GET lives in the auto-allowed web__get).
                    string method = (req.Arguments.Value<string>("method") ?? "POST").Trim().ToUpperInvariant();
                    if (method.Length == 0) method = "POST";
                    string url = req.Arguments.Value<string>("url") ?? string.Empty;
                    string body = req.Arguments.Value<string>("body") ?? string.Empty;
                    string text = method + " " + url;
                    if (body.Trim().Length > 0) text += "\r\n\r\n" + body;
                    _diffPanel.SetContent(string.Empty, text, "text", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                    _previewLabel.Text = "HTTP request:";
                    handled = true;
                }
                else if (string.Equals(req.FunctionName, "files__delete", StringComparison.Ordinal))
                {
                    string path = req.Arguments.Value<string>("path") ?? string.Empty;
                    if (path.Length > 0)
                    {
                        _diffPanel.SetContent(string.Empty, path, "text", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                        _previewLabel.Text = "Delete:";
                        handled = true;
                    }
                }
                else if (string.Equals(req.FunctionName, "git__push", StringComparison.Ordinal))
                {
                    string remote = req.Arguments.Value<string>("remote") ?? string.Empty;
                    string branch = req.Arguments.Value<string>("branch") ?? string.Empty;
                    string tgt = remote.Length > 0 ? (branch.Length > 0 ? remote + "/" + branch : remote) : (branch.Length > 0 ? branch : "(default remote/branch)");
                    _diffPanel.SetContent(string.Empty, tgt, "text", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                    _previewLabel.Text = "Push to:";
                    handled = true;
                }
                else if (req.FunctionName != null && req.FunctionName.StartsWith("git__", StringComparison.Ordinal))
                {
                    // Other git tools: show the equivalent command line so a destructive op (reset
                    // --hard, rm, rebase, ...) is legible before approving.
                    string summary = GitOpSummary(req);
                    if (summary.Length > 0)
                    {
                        _diffPanel.SetContent(string.Empty, summary, "batch", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                        _previewLabel.Text = "Git command:";
                        handled = true;
                    }
                }
                else if (req.FunctionName != null && req.FunctionName.StartsWith("msbuild__build_solution_", StringComparison.Ordinal))
                {
                    // devenv (whole-solution) build: show the equivalent command line. The IDE year is the
                    // tool-name suffix (build_solution_2022 -> 2022). Checked before the MSBuild prefix
                    // below since "build_solution_*" also starts with "build_".
                    string year = req.FunctionName.Substring("msbuild__build_solution_".Length);
                    _diffPanel.SetContent("Visual Studio " + year, DevenvCommandPreview(req.Arguments), "batch", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                    _previewLabel.Text = "Build (Visual Studio):";
                    handled = true;
                }
                else if (req.FunctionName != null && req.FunctionName.StartsWith("msbuild__build_", StringComparison.Ordinal))
                {
                    // MSBuild build: the equivalent command line. The engine version is the tool-name
                    // suffix (build_17_0 -> 17.0); MSBuild can run arbitrary build logic, so this is gated.
                    string ver = req.FunctionName.Substring("msbuild__build_".Length).Replace('_', '.');
                    string bitness = req.Arguments.Value<string>("bitness") ?? string.Empty;
                    string head = "MSBuild " + ver + (bitness.Length > 0 ? " (" + bitness + ")" : string.Empty);
                    _diffPanel.SetContent(head, MsBuildCommandPreview(req.Arguments), "batch", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                    _previewLabel.Text = "Build (MSBuild):";
                    handled = true;
                }
                else if (string.Equals(req.FunctionName, "memory__remember", StringComparison.Ordinal)
                      || string.Equals(req.FunctionName, "memory__update_memory", StringComparison.Ordinal))
                {
                    // Show the memory's summary (+ optional detail) as readable markdown rather than JSON.
                    // update_memory carries only the fields being changed.
                    string name = req.Arguments.Value<string>("name") ?? string.Empty;
                    _diffPanel.SetContent(name, MemoryBody(req.Arguments), "markdown", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                    _previewLabel.Text = string.Equals(req.FunctionName, "memory__remember", StringComparison.Ordinal)
                        ? "Create memory:" : "Update memory:";
                    handled = true;
                }
                else if (string.Equals(req.FunctionName, "memory__forget", StringComparison.Ordinal))
                {
                    string name = req.Arguments.Value<string>("name") ?? string.Empty;
                    if (name.Length > 0)
                    {
                        _diffPanel.SetContent(string.Empty, name, "text", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                        _previewLabel.Text = "Forget memory:";
                        handled = true;
                    }
                }
                else if (string.Equals(req.FunctionName, "memory__consolidate", StringComparison.Ordinal))
                {
                    // Which memories are merged away (and into what), then the new entry's summary/detail.
                    string newName = req.Arguments.Value<string>("new_name") ?? string.Empty;
                    var sb2 = new System.Text.StringBuilder();
                    string sources = JoinArr(req.Arguments, "names", ", ");
                    if (sources.Length > 0) sb2.Append("Merging: ").Append(sources);
                    string rest = MemoryBody(req.Arguments);
                    if (rest.Length > 0) { if (sb2.Length > 0) sb2.Append("\r\n\r\n"); sb2.Append(rest); }
                    _diffPanel.SetContent(newName, sb2.ToString(), "markdown", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                    _previewLabel.Text = "Consolidate memories:";
                    handled = true;
                }
            }

            if (handled)
            {
                _preview.Visible = false;
                _diffPanel.Visible = true;
            }
            else
            {
                _preview.Text = BuildPreviewText(req);
                _previewLabel.Text = "Details:";
                _diffPanel.Visible = false;
                _preview.Visible = true;
            }

            _buttons.Controls.Clear();
            _defaultButton = null;
            // Deny is always present (added first => rightmost; see AddButton). Auto-focused on the
            // Destructive tier so the keyboard default is the safe choice (it shows the focused flat
            // button's heavier border).
            AddButton("Deny", ApprovalChoice.Deny, tier == ToolTier.Destructive);
            AddRememberButtons(req);
            // Write tier defaults to "Allow once" (the cautious default for the riskier Destructive tier
            // is Deny, handled above).
            AddButton("Allow once", ApprovalChoice.AllowOnce, tier == ToolTier.Write);
            SetButtonTabOrder();

            // Offer Explain only for a command prompt (the branch above captured the command). The
            // button overlays the header's top-right corner; reserve header space so its text never
            // runs under the button.
            UpdateExplainButton(!string.IsNullOrEmpty(_explainCommand));

            // Color the panel + the freshly built buttons for the active theme before measuring, so the
            // button metrics used by LayoutToContent reflect their themed style.
            ApplyTheme();

            // Size to fit the content (buttons built above so their height is counted, including any
            // wrapping at the current width; LayoutToContent measures the details at the live width).
            _handledDetails = handled;
            LayoutToContent();

            this.Visible = true;
            // Keep this Bottom-docked panel BEHIND the Fill transcript in z-order. WinForms fills the
            // remaining space with the front-most Fill control, so the panel must stay back for the
            // transcript to shrink *above* it (rather than the panel overlaying the transcript's
            // bottom edge and its right-docked scrollbar). BringToFront would cause exactly that.
            this.SendToBack();
            SetPromptVisible(true);
            FocusDefaultButton();
        }

        // Show or hide the top-right Explain button for the current prompt, reserving room in the header
        // so its (possibly long) "server · tool" text never draws under the button.
        private void UpdateExplainButton(bool show)
        {
            if (_explainButton == null) return;
            _explainButton.Visible = show;
            if (show)
            {
                int reserve = _explainButton.PreferredSize.Width + 8;
                _header.Padding = new Padding(0, 0, reserve, 0);
                PositionExplainButton();
                _explainButton.BringToFront(); // paint over the docked header it overlaps
            }
            else
            {
                _header.Padding = Padding.Empty;
            }
        }

        // Pin the Explain button to the panel's inner top-right corner (inside the panel padding),
        // aligned with the header row. Re-run on resize because the panel width changes with the window.
        // Deliberately NOT gated on _explainButton.Visible: ShowFor positions the button while the panel
        // itself is still hidden, and the Control.Visible getter reports false whenever a parent is
        // hidden - so a Visible check here would skip the very placement ShowFor needs, leaving the
        // button at its default top-left location. Positioning a hidden button is harmless.
        private void PositionExplainButton()
        {
            if (_explainButton == null) return;
            int bw = _explainButton.PreferredSize.Width;
            int x = this.ClientSize.Width - this.Padding.Right - bw;
            int y = this.Padding.Top;
            if (x < this.Padding.Left) x = this.Padding.Left;
            _explainButton.Location = new Point(x, y);
        }

        // Explain click: hand the captured command to the host so it can open a fresh chat tab that
        // explains it. Deliberately does NOT hide the panel - the approval stays up for the user to
        // Allow/Deny after reading the explanation.
        private void OnExplainClicked(object sender, EventArgs e)
        {
            string cmd = _explainCommand;
            bool isPs = _explainIsPowerShell;
            Action<string, bool> h = ExplainRequested;
            if (h != null && !string.IsNullOrEmpty(cmd))
            {
                try { h(cmd, isPs); }
                catch { }
            }
        }

        // Focus the tier's default button now that the panel is visible (focusing earlier is a no-op).
        // Real focus fires GotFocus, which paints the blue focus border; deferring to BeginInvoke lets
        // the just-shown panel settle so the focus reliably takes.
        private void FocusDefaultButton()
        {
            Button b = _defaultButton;
            if (b == null) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    try { if (b.IsHandleCreated && b.Visible) b.Focus(); }
                    catch { }
                });
            }
            catch
            {
                try { b.Focus(); }
                catch { }
            }
        }

        // Iteration-cap confirmation, reusing this docked panel so it reads like the tool-approval
        // prompt. callback(true) => grant another batch, callback(false) => stop (wrap up). Marshalled
        // onto the UI thread by TranscriptContinuationPrompt; the click signals the blocked worker.
        public void ShowContinuation(int iterationsSoFar, Action<bool> callback)
        {
            _onChoose = null;
            _onContinue = callback;
            // The iteration-cap prompt is not a command tool: never offer Explain here.
            _explainCommand = null;
            _explainIsPowerShell = false;
            UpdateExplainButton(false);

            _header.Text = "Tool-call limit reached";
            _currentTier = ToolTier.Write; // informational; reuse the Write (goldenrod) badge color
            _tierBadge.Text = "Paused after " + iterationsSoFar + " tool iteration(s) this turn";

            _previewLabel.Text = "Details:";
            _preview.Text = "The agent has been working for a long time. Do you want to continue?\r\n\r\n"
                + "Choose Continue to let it keep working, or Stop to have it summarize progress and "
                + "ask how you'd like to proceed.";
            _diffPanel.Visible = false;
            _preview.Visible = true;

            _buttons.Controls.Clear();
            _defaultButton = null;
            // Added first => rightmost (see AddContinuationButton).
            AddContinuationButton("Stop", false, false);
            AddContinuationButton("Continue", true, true);
            SetButtonTabOrder();

            ApplyTheme();

            _handledDetails = false;
            LayoutToContent();

            this.Visible = true;
            this.SendToBack();
            SetPromptVisible(true);
            FocusDefaultButton();
        }

        public void HidePanel()
        {
            this.Visible = false;
            _onChoose = null;
            _onContinue = null;
            SetPromptVisible(false);
        }

        // Track and broadcast whether a prompt is currently awaiting the user.
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

        // Resolve any pending prompt as if the user had declined (Deny / Stop), then hide. Used when
        // the prompting turn detaches from this panel's tab (the tab closed or was recycled
        // mid-prompt): the worker blocked in TranscriptApprovalPrompt/TranscriptContinuationPrompt
        // must be released - HidePanel alone clears the callbacks WITHOUT invoking them, which would
        // leave that turn waiting forever (and its conversation never saved).
        public void DenyPending()
        {
            Action<ApprovalChoice> choose = _onChoose;
            Action<bool> cont = _onContinue;
            HidePanel();
            if (choose != null) choose(ApprovalChoice.Deny);
            if (cont != null) cont(false);
        }

        // The clamped natural height of the current details control's content, in [Min,Max].
        private int ClampedDetailsHeight(bool handled)
        {
            int detailsContent = handled
                ? _diffPanel.GetPreferredContentHeight(DiffAvailableWidth())
                : MeasurePreviewContentHeight();
            return Math.Max(MinDetailsHeight, Math.Min(MaxDetailsHeight, detailsContent));
        }

        // Width the diff panel has for its content, used to predict whether a horizontal scrollbar
        // will appear (so its height can be reserved). The diff panel fills this panel minus padding;
        // prefer its actual width, with fallbacks for when it hasn't been laid out yet.
        private int DiffAvailableWidth()
        {
            // The approval panel's own client width is authoritative and current (even mid-resize,
            // when the Fill child's width may lag); the diff panel fills it minus padding.
            int w = this.ClientSize.Width - this.Padding.Horizontal;
            if (w <= 0 && _diffPanel != null) w = _diffPanel.ClientSize.Width;
            if (w <= 0 && this.Parent != null) w = this.Parent.ClientSize.Width - this.Padding.Horizontal;
            return w > 0 ? w : 400;
        }

        // Rebuild the panel height so the Fill details control keeps its clamped content height even
        // as the button strip wraps to more rows on a narrow window: the panel grows to fit the taller
        // strip instead of squeezing (and clipping) the details. Capped so it can't swallow the
        // transcript above it; past the cap the details give way and scroll. Recomputed on every
        // resize because button wrapping depends on the current width.
        private void LayoutToContent()
        {
            if (_inLayoutToContent) return;
            _inLayoutToContent = true;
            try
            {
                // Re-measure the details at the current width: a horizontal scrollbar (long command)
                // appears/disappears as the panel widens or narrows, and that changes the height needed.
                _detailsHeight = ClampedDetailsHeight(_handledDetails);

                int avail = this.ClientSize.Width - this.Padding.Horizontal;
                if (avail < 1) avail = (this.Parent != null ? this.Parent.ClientSize.Width : 400) - this.Padding.Horizontal;
                if (avail < 1) avail = 400;

                // GetPreferredSize at the real width includes any row wrapping of the buttons. The strip
                // itself (a RightAlignedFlowLayoutPanel) keeps each row flush to the right edge.
                int buttonsH = _buttons.GetPreferredSize(new Size(avail, 0)).Height;
                if (buttonsH < 28) buttonsH = 34; // guard against a not-yet-measured strip

                int chrome = _header.Height + _tierBadge.Height + _previewLabel.Height;
                const int border = 2; // FixedSingle: 1px top + 1px bottom
                int target = this.Padding.Top + this.Padding.Bottom + chrome + _detailsHeight + buttonsH + border;

                // Don't let the docked panel grow tall enough to bury the transcript above it.
                if (this.Parent != null)
                {
                    int cap = this.Parent.ClientSize.Height - 64;
                    if (cap > 120 && target > cap) target = cap;
                }

                if (this.Height != target) this.Height = target;
            }
            finally { _inLayoutToContent = false; }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            // Width changes (parent resize) can re-wrap the buttons; keep the panel tall enough.
            if (this.Visible) LayoutToContent();
            // Keep the overlaid Explain button anchored to the (now moved) right edge.
            PositionExplainButton();
        }

        // Approximate wrapped height of the raw-JSON preview text at the current width. Best-effort:
        // the TextBox scrolls if we under-shoot, and the caller clamps the result anyway.
        private int MeasurePreviewContentHeight()
        {
            string text = _preview.Text != null ? _preview.Text : string.Empty;
            if (text.Length == 0) return MinDetailsHeight;
            int width = this.ClientSize.Width - this.Padding.Horizontal;
            if (width < 50 && this.Parent != null) width = this.Parent.ClientSize.Width - this.Padding.Horizontal;
            if (width < 50) width = 400;
            Size sz = TextRenderer.MeasureText(text, _preview.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak);
            return sz.Height + 8;
        }

        // If the panel is torn down (e.g. its tab is closed) while a call still awaits a decision,
        // resolve the pending request as Deny so the blocked tool-loop worker is released rather than
        // left waiting on the signal forever.
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Action<ApprovalChoice> cb = _onChoose;
                _onChoose = null;
                if (cb != null)
                {
                    try { cb(ApprovalChoice.Deny); }
                    catch { }
                }

                // A pending continuation prompt resolves to "stop" so the blocked worker is released
                // (wraps up) rather than left waiting forever.
                Action<bool> cc = _onContinue;
                _onContinue = null;
                if (cc != null)
                {
                    try { cc(false); }
                    catch { }
                }

                if (_toolTip != null)
                {
                    try { _toolTip.Dispose(); }
                    catch { }
                }
            }
            base.Dispose(disposing);
        }

        private void AddRememberButtons(ApprovalRequest req)
        {
            RememberScope scope = req.Policy != null ? req.Policy.Scope : RememberScope.Tool;
            string argPath = req.Policy != null ? req.Policy.ScopeArgPath : null;

            if (scope == RememberScope.Tool)
            {
                AddButton("Always allow this tool", ApprovalChoice.RememberTool, false);
            }
            else if (scope == RememberScope.Argument && argPath == "command")
            {
                AddButton("Always allow this command pattern", ApprovalChoice.RememberPrefixArg, false,
                    CommandPatternTooltip(req));
                AddButton("Always allow this exact command", ApprovalChoice.RememberExactArg, false,
                    "Allows only this exact command line. Any change to the command — including its "
                    + "flags or arguments — prompts again.");
            }
            else if (scope == RememberScope.Argument && argPath == "path")
            {
                // "this file" (exact) and a workspace-wide blanket for all write-tier files tools. The
                // per-folder prefix option is intentionally omitted to keep the strip to four buttons;
                // the workspace blanket covers the "trust this working area" case more broadly.
                AddButton("Allow all edits in this workspace", ApprovalChoice.RememberWorkdirWrites, false);
                AddButton("Always allow this file", ApprovalChoice.RememberExactArg, false);
            }
            else if (scope == RememberScope.SkillScript)
            {
                // run_skill_script: a per-skill blanket and a per-script exact rule. Add the broader
                // option first so (front-insertion, see AddButton) the more specific "this script" lands
                // nearer "Allow once" and the broader "scripts for this skill" nearer Deny - mirroring
                // the exact-vs-pattern ordering of the command buttons above.
                AddButton("Always allow scripts for this skill", ApprovalChoice.RememberSkillScripts, false,
                    "Allows running any script bundled with this skill, with any arguments. A script "
                    + "belonging to a different skill still prompts.");
                AddButton("Always allow this script", ApprovalChoice.RememberSkillScript, false,
                    "Allows only this exact script (this skill + this script path), with any arguments. "
                    + "Any other script - including a different one in the same skill - still prompts.");
            }
            // Scope == None: no remember buttons (Allow once / Deny only).
        }

        // Summarizes what "Always allow this command pattern" will permit, showing the concrete
        // signature this command reduces to (e.g. "powershell hello.ps1") so the user sees exactly
        // what future commands will match.
        private static string CommandPatternTooltip(ApprovalRequest req)
        {
            string cmd = (req != null && req.Arguments != null) ? req.Arguments.Value<string>("command") : null;
            string sig = ToolApprovalPolicy.CommandSignature(cmd);
            string body =
                "Allows future commands matching the program and its main target (a subcommand or "
                + "file), while ignoring other flags and arguments. A different program, file, or "
                + "subcommand prompts again.";
            if (!string.IsNullOrEmpty(sig))
                body = "Allows:  " + sig + "  (any flags or arguments)\r\n\r\n" + body;
            return body;
        }

        private void AddButton(string text, ApprovalChoice choice, bool defaultFocus)
        {
            AddButton(text, choice, defaultFocus, null);
        }

        private void AddButton(string text, ApprovalChoice choice, bool defaultFocus, string tooltip)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.Margin = new Padding(4, 4, 4, 4);
            b.Tag = choice;
            b.Click += delegate
            {
                Action<ApprovalChoice> cb = _onChoose;
                HidePanel();
                if (cb != null) cb(choice);
            };
            b.GotFocus += OnButtonFocusChanged;
            b.LostFocus += OnButtonFocusChanged;
            // Insert at the front: the LeftToRight strip lays controls out in collection order, so
            // adding Deny first then prepending each later button keeps Deny right-most (and makes it
            // the first to wrap onto a new row when space runs out).
            _buttons.Controls.Add(b);
            _buttons.Controls.SetChildIndex(b, 0);
            if (!string.IsNullOrEmpty(tooltip) && _toolTip != null)
            {
                try { _toolTip.SetToolTip(b, tooltip); }
                catch { }
            }
            if (defaultFocus) _defaultButton = b; // focused after the panel is shown (see FocusDefaultButton)
        }

        private void AddContinuationButton(string text, bool cont, bool defaultFocus)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.Margin = new Padding(4, 4, 4, 4);
            b.Click += delegate
            {
                Action<bool> cb = _onContinue;
                HidePanel();
                if (cb != null) cb(cont);
            };
            b.GotFocus += OnButtonFocusChanged;
            b.LostFocus += OnButtonFocusChanged;
            // Front-insert (matching AddButton) so Stop stays right-most under the LeftToRight flow.
            _buttons.Controls.Add(b);
            _buttons.Controls.SetChildIndex(b, 0);
            if (defaultFocus) _defaultButton = b; // focused after the panel is shown (see FocusDefaultButton)
        }

        // Reads a workspace-relative file for diff context. Mirrors the files sandbox: relative paths
        // only, must resolve inside the workspace root. Returns null on any failure (→ bare diff).
        private static string ReadWorkspaceFile(string workdir, string relPath)
        {
            if (string.IsNullOrEmpty(workdir) || string.IsNullOrEmpty(relPath)) return null;
            try
            {
                if (Path.IsPathRooted(relPath)) return null;
                string root = Path.GetFullPath(workdir);
                string full = Path.GetFullPath(Path.Combine(root, relPath));
                string rootSep = root.EndsWith(Path.DirectorySeparatorChar.ToString()) ? root : root + Path.DirectorySeparatorChar;
                if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase) &&
                    !full.StartsWith(rootSep, StringComparison.OrdinalIgnoreCase)) return null;
                if (!File.Exists(full)) return null;
                return File.ReadAllText(full);
            }
            catch { return null; }
        }

        // One URL per line from the web__extract "urls" array argument.
        private static string JoinUrlArgs(Newtonsoft.Json.Linq.JObject args)
        {
            try
            {
                var arr = args["urls"] as Newtonsoft.Json.Linq.JArray;
                if (arr == null) return string.Empty;
                var sb = new System.Text.StringBuilder();
                foreach (var u in arr)
                {
                    string s = (string)u;
                    if (string.IsNullOrEmpty(s)) continue;
                    if (sb.Length > 0) sb.Append("\r\n");
                    sb.Append(s);
                }
                return sb.ToString();
            }
            catch { return string.Empty; }
        }

        // "slug/relpath" for a skill-file approval header, tolerant of a missing part.
        private static string SkillFileTarget(string slug, string rel)
        {
            if (slug.Length > 0 && rel.Length > 0) return slug + "/" + rel;
            if (rel.Length > 0) return rel;
            return slug.Length > 0 ? slug : "(skill file)";
        }

        // Readable "Name:/Description:" preamble + instructions body for create/update_skill approvals,
        // shown as markdown instead of raw JSON. Only non-empty fields appear (update_skill sends just
        // the fields being changed).
        private static string BuildSkillFields(Newtonsoft.Json.Linq.JObject a)
        {
            string name = a.Value<string>("name") ?? string.Empty;
            string desc = a.Value<string>("description") ?? string.Empty;
            string body = a.Value<string>("body") ?? string.Empty;
            var sb = new System.Text.StringBuilder();
            if (name.Length > 0) sb.Append("Name: ").Append(name);
            if (desc.Length > 0) { if (sb.Length > 0) sb.Append("\r\n"); sb.Append("Description: ").Append(desc); }
            if (body.Length > 0) { if (sb.Length > 0) sb.Append("\r\n\r\n"); sb.Append(body); }
            return sb.ToString();
        }

        // The memory's one-line summary, then its optional detail note, for remember/update/consolidate
        // approvals — shown as markdown instead of raw JSON. Only non-empty fields appear (update sends
        // just the fields being changed).
        private static string MemoryBody(Newtonsoft.Json.Linq.JObject a)
        {
            string summary = Sv(a, "summary");
            string detail = Sv(a, "detail");
            var sb = new System.Text.StringBuilder();
            if (summary.Length > 0) sb.Append(summary);
            if (detail.Length > 0) { if (sb.Length > 0) sb.Append("\r\n\r\n"); sb.Append(detail); }
            return sb.ToString();
        }

        // "msbuild <project> /t:... /p:Configuration=... ..." for an msbuild__build_<ver> approval,
        // mirroring the switches the server actually passes (MsBuildTools.BuildArgs). Routine defaults
        // (/v:minimal, /nologo) are omitted so the risk-bearing parts (project, targets, properties)
        // stand out.
        private static string MsBuildCommandPreview(Newtonsoft.Json.Linq.JObject a)
        {
            var sb = new System.Text.StringBuilder("msbuild");
            string project = Sv(a, "project");
            sb.Append(' ').Append(project.Length > 0 ? project : "<lone solution/project in workdir>");

            string targets = JoinArr(a, "targets", ";");
            if (targets.Length > 0) sb.Append(" /t:").Append(targets);

            string config = Sv(a, "configuration");
            if (config.Length > 0) sb.Append(" /p:Configuration=").Append(config);
            string platform = Sv(a, "platform");
            if (platform.Length > 0) sb.Append(" /p:Platform=").Append(platform);

            var props = a["properties"] as Newtonsoft.Json.Linq.JObject;
            if (props != null)
                foreach (var kv in props)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    sb.Append(" /p:").Append(kv.Key).Append('=').Append(kv.Value != null ? kv.Value.ToString() : string.Empty);
                }

            string verbosity = Sv(a, "verbosity");
            if (verbosity.Length > 0) sb.Append(" /v:").Append(verbosity);
            return sb.ToString();
        }

        // "devenv <solution> /Build \"Config|Platform\" ..." for an msbuild__build_solution_<year>
        // approval (mirrors MsBuildTools.BuildDevenvArgs).
        private static string DevenvCommandPreview(Newtonsoft.Json.Linq.JObject a)
        {
            var sb = new System.Text.StringBuilder("devenv");
            string solution = Sv(a, "solution");
            sb.Append(' ').Append(solution.Length > 0 ? solution : "<lone .sln in workdir>");
            sb.Append(' ').Append(DevenvActionSwitch(Sv(a, "action")));

            string config = Sv(a, "configuration"); if (config.Length == 0) config = "Release";
            string platform = Sv(a, "platform");
            sb.Append(" \"").Append(platform.Length > 0 ? config + "|" + platform : config).Append('"');

            string project = Sv(a, "project");
            if (project.Length > 0) sb.Append(" /Project ").Append(project);
            string projectConfig = Sv(a, "project_config");
            if (projectConfig.Length > 0) sb.Append(" /ProjectConfig ").Append(projectConfig);
            return sb.ToString();
        }

        private static string DevenvActionSwitch(string action)
        {
            if (string.IsNullOrEmpty(action)) return "/Build";
            switch (action.ToLowerInvariant())
            {
                case "rebuild": return "/Rebuild";
                case "clean": return "/Clean";
                case "deploy": return "/Deploy";
                default: return "/Build";
            }
        }

        // Joins a string[] arg with the given separator (also accepts a lone string). Empty when absent.
        private static string JoinArr(Newtonsoft.Json.Linq.JObject a, string name, string sep)
        {
            var arr = a[name] as Newtonsoft.Json.Linq.JArray;
            if (arr == null) { string s = a.Value<string>(name); return s ?? string.Empty; }
            var sb = new System.Text.StringBuilder();
            foreach (var t in arr)
            {
                string s = (string)t; if (string.IsNullOrEmpty(s)) continue;
                if (sb.Length > 0) sb.Append(sep);
                sb.Append(s);
            }
            return sb.ToString();
        }

        // A readable "git <subcommand> ..." line for the approval preview of the extended git tools.
        // Returns "" for tools handled elsewhere (commit/push) or unknown ones.
        private static string GitOpSummary(ApprovalRequest req)
        {
            Newtonsoft.Json.Linq.JObject a = req.Arguments != null ? req.Arguments : new Newtonsoft.Json.Linq.JObject();
            var sb = new System.Text.StringBuilder("git ");
            switch (req.FunctionName)
            {
                case "git__fetch":
                    sb.Append("fetch"); if (Bv(a, "prune")) sb.Append(" --prune"); Append(sb, Sv(a, "remote")); break;
                case "git__pull":
                    sb.Append("pull"); if (Bv(a, "rebase")) sb.Append(" --rebase"); Append(sb, Sv(a, "remote")); Append(sb, Sv(a, "branch")); break;
                case "git__checkout":
                    sb.Append("checkout"); if (Bv(a, "create")) sb.Append(" -b"); Append(sb, Sv(a, "ref")); Append(sb, Sv(a, "start_point")); break;
                case "git__restore":
                    sb.Append("restore"); if (Bv(a, "staged")) sb.Append(" --staged");
                    if (Sv(a, "source").Length > 0) sb.Append(" --source ").Append(Sv(a, "source"));
                    Append(sb, PathsOf(a, "paths")); break;
                case "git__branch":
                {
                    string act = Sv(a, "action"); if (act.Length == 0) act = "list";
                    string nm = Sv(a, "name");
                    switch (act.ToLowerInvariant())
                    {
                        // Real git syntax so a force-delete (-D) of unmerged work is visible before approving.
                        case "create": sb.Append("branch"); if (Bv(a, "force")) sb.Append(" -f"); Append(sb, nm); break;
                        case "delete": sb.Append(Bv(a, "force") ? "branch -D" : "branch -d"); Append(sb, nm); break;
                        case "rename": sb.Append("branch -m"); Append(sb, nm); Append(sb, Sv(a, "new_name")); break;
                        default: sb.Append("branch"); if (Bv(a, "all")) sb.Append(" -a"); break;
                    }
                    break;
                }
                case "git__merge":
                    sb.Append("merge"); if (Bv(a, "no_ff")) sb.Append(" --no-ff"); Append(sb, Sv(a, "branch")); break;
                case "git__rebase":
                {
                    string act = Sv(a, "action"); if (act.Length == 0) act = "start";
                    if (act == "start") { sb.Append("rebase"); Append(sb, Sv(a, "onto")); }
                    else sb.Append("rebase --").Append(act);
                    break;
                }
                case "git__cherry_pick":
                    sb.Append("cherry-pick"); if (Bv(a, "no_commit")) sb.Append(" -n"); Append(sb, Sv(a, "commit")); break;
                case "git__add":
                    sb.Append("add"); if (Bv(a, "all")) sb.Append(" -A"); else Append(sb, PathsOf(a, "paths")); break;
                case "git__reset":
                {
                    string paths = PathsOf(a, "paths");
                    if (paths.Length > 0) { sb.Append("reset"); Append(sb, Sv(a, "target")); sb.Append(" -- ").Append(paths); }
                    else { string m = Sv(a, "mode"); if (m.Length == 0) m = "mixed"; sb.Append("reset --").Append(m.ToLowerInvariant()); Append(sb, Sv(a, "target")); }
                    break;
                }
                case "git__rm":
                    sb.Append("rm"); if (Bv(a, "cached")) sb.Append(" --cached"); if (Bv(a, "recursive")) sb.Append(" -r"); Append(sb, PathsOf(a, "paths")); break;
                case "git__worktree":
                {
                    string act = Sv(a, "action"); if (act.Length == 0) act = "list";
                    sb.Append("worktree ").Append(act.ToLowerInvariant());
                    switch (act.ToLowerInvariant())
                    {
                        case "add":
                            if (Bv(a, "force")) sb.Append(" --force");
                            if (Sv(a, "branch").Length > 0) sb.Append(" -b ").Append(Sv(a, "branch"));
                            Append(sb, Sv(a, "path")); Append(sb, Sv(a, "ref")); break;
                        case "remove":
                            if (Bv(a, "force")) sb.Append(" --force");
                            Append(sb, Sv(a, "path")); break;
                    }
                    break;
                }
                case "git__stash":
                {
                    string act = Sv(a, "action"); if (act.Length == 0) act = "push";
                    sb.Append("stash ").Append(act);
                    if (act == "push")
                    {
                        if (Sv(a, "message").Length > 0) sb.Append(" -m ").Append(Sv(a, "message"));
                    }
                    else if (act == "pop" || act == "apply" || act == "drop")
                    {
                        // Show the targeted entry (stash@{N}) so it's clear which stash is affected.
                        string idx = Sv(a, "index");
                        if (idx.Length > 0) sb.Append(" stash@{").Append(idx).Append('}');
                    }
                    break;
                }
                default: return string.Empty;
            }
            return sb.ToString();
        }

        private static void Append(System.Text.StringBuilder sb, string v) { if (!string.IsNullOrEmpty(v)) sb.Append(' ').Append(v); }
        private static string Sv(Newtonsoft.Json.Linq.JObject a, string n) { var v = a.Value<string>(n); return v ?? string.Empty; }
        private static bool Bv(Newtonsoft.Json.Linq.JObject a, string n) { var t = a[n]; try { return t != null && (bool)t; } catch { return false; } }
        private static string PathsOf(Newtonsoft.Json.Linq.JObject a, string n)
        {
            var arr = a[n] as Newtonsoft.Json.Linq.JArray;
            if (arr == null) { string s = a.Value<string>(n); return s ?? string.Empty; }
            var sb = new System.Text.StringBuilder();
            foreach (var p in arr) { string s = (string)p; if (string.IsNullOrEmpty(s)) continue; if (sb.Length > 0) sb.Append(' '); sb.Append(s); }
            return sb.ToString();
        }

        private static string BuildPreviewText(ApprovalRequest req)
        {
            string preview = req.Preview != null ? req.Preview : string.Empty;
            string args = req.Arguments != null ? req.Arguments.ToString(Formatting.Indented) : "{}";
            if (!string.IsNullOrEmpty(preview) && preview != args)
                return preview + "\r\n\r\n" + args;
            return args;
        }
    }

    // A FlowLayoutPanel that keeps each row flush against its right edge. The stock control left-aligns
    // rows, so a horizontally-flowing strip drifts left once it wraps (a single left-padding offset
    // can't right-align two rows of different widths). The button strip uses LeftToRight flow so the
    // right-most buttons wrap first; this subclass then nudges each wrapped row back to the right after
    // the base layout, so the strip looks right-aligned whether it fills one row or several.
    internal sealed class RightAlignedFlowLayoutPanel : FlowLayoutPanel
    {
        private bool _aligning; // guard against the re-entrant layout our own moves would trigger

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);

            // Only horizontal flows have a meaningful "right edge" to align to.
            if (_aligning || this.Controls.Count == 0) return;
            if (this.FlowDirection != FlowDirection.LeftToRight && this.FlowDirection != FlowDirection.RightToLeft) return;

            _aligning = true;
            this.SuspendLayout(); // move the controls without provoking another layout pass
            try { RightAlignRows(); }
            finally { this.ResumeLayout(false); _aligning = false; }
        }

        // Group the laid-out controls into rows by their shared Top, then shift each row right by its
        // leftover space so its right-most control sits against the panel's inner right edge.
        private void RightAlignRows()
        {
            int innerRight = this.ClientSize.Width - this.Padding.Right;
            var rows = new Dictionary<int, List<Control>>();
            foreach (Control c in this.Controls)
            {
                if (!c.Visible) continue;
                List<Control> row;
                if (!rows.TryGetValue(c.Top, out row)) { row = new List<Control>(); rows[c.Top] = row; }
                row.Add(c);
            }

            foreach (List<Control> row in rows.Values)
            {
                int rowRight = int.MinValue;
                foreach (Control c in row)
                {
                    int r = c.Right + c.Margin.Right; // include the trailing margin so the gap matches the leading one
                    if (r > rowRight) rowRight = r;
                }
                int slack = innerRight - rowRight;
                if (slack <= 0) continue;
                foreach (Control c in row) c.Left += slack;
            }
        }
    }
}
