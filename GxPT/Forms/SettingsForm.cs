using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.IO;
using System.Windows.Forms;
using System.Web.Script.Serialization; // .NET 3.5 JSON serializer
using Newtonsoft.Json.Linq;            // mcp.json validation
using Krypton.Toolkit;
using Krypton.Navigator;

namespace GxPT
{
    public partial class SettingsForm : KryptonForm
    {
        private readonly string _settingsDir;
        private readonly string _settingsFile;
        private readonly string _mcpFile;

        // In-memory working copy (unsaved until Save/CTRL+S)
        private SettingsData _working = new SettingsData();

        // Guard to prevent event loops during programmatic sync
        private bool _isSyncing = false;

        // True once the user edits any control (and not yet saved). Drives the Apply button's enabled
        // state; cleared on a successful Apply/OK save and reset to false after the initial load.
        private bool _isDirty = false;

        // Snapshot of every tracked control's value, taken once the form has loaded/settled. Dirtiness
        // is decided by comparing the current values to this baseline rather than by "an event fired",
        // so spurious Changed events that don't actually alter a value (e.g. Krypton combos re-raising
        // SelectedIndexChanged when first realized on screen) never light up Apply.
        private string _dirtyBaseline;

        // Debounce timer for JSON syntax highlighting
        private Timer _jsonHighlightTimer;

        // Prevent re-entrant highlighting and repeated scheduling during formatting
        private bool _isHighlighting = false;

        // Track pending edited region to highlight (union of ranges until debounce fires)
        private int _pendingHighlightStart = -1;
        private int _pendingHighlightEnd = -1;

        // Debounce + guard for the mcp.json editor's syntax highlighting (mirrors the JSON tab).
        private Timer _mcpHighlightTimer;
        private bool _isMcpHighlighting = false;

        // Tooltip explaining why the Git toggle is disabled when git isn't on PATH.
        private readonly ToolTip _mcpTip = new ToolTip();

        // Agent effort-tier model pickers (low/medium/high), built in code into the Models groupbox. Narrow
        // owner-drawn combos that show just the model name while storing the full "author/model" id.
        private ComboBox cmbEffortLow;
        private ComboBox cmbEffortMedium;
        private ComboBox cmbEffortHigh;

        public SettingsForm()
        {
            InitializeComponent();
            this.Disposed += delegate { try { _mcpTip.Dispose(); } catch { } };

            // The visual tabs nest stock WinForms layout panels (TableLayoutPanels,
            // Panels) inside themed Krypton surfaces. Left opaque, those panels paint
            // a light rectangle over the dark KryptonPage/GroupBox in dark mode and the
            // KryptonLabels on them become light-on-light. Making the layout-only
            // containers transparent lets the themed surface show through so the labels
            // read correctly in both light and dark mode.
            try { KryptonThemeBridge.MakeLayoutContainersTransparent(this); } catch { }

            // The group boxes sit on the (light) navigator page but have dark panels.
            // The default caption straddles the top border, so half the caption text
            // lands on the light page behind it and is unreadable. Seat each caption
            // fully inside its own group area so it reads against the dark panel.
            try { KryptonThemeBridge.SeatGroupBoxCaptions(this); } catch { }

            // KryptonNumericUpDown hosts a stock NumericUpDown, which does NOT support a transparent
            // BackColor (its edit control renders and takes input incorrectly - misplaced caret,
            // no selection, typing prepends and overflows to Maximum). Our NUDs sit on the layout
            // panels that MakeLayoutContainersTransparent turns transparent, so they inherit
            // BackColor=Transparent via ambient inheritance and break. Give each an explicit opaque,
            // theme-appropriate BackColor so the internal control has a solid background again
            // (Krypton still themes the visible chrome). This is why the example app - whose NUDs sit
            // on an opaque group box - works while ours did not.
            try
            {
                bool nudDark = KryptonThemeBridge.IsDarkMode();
                Color nudBack = nudDark ? Color.FromArgb(0x3C, 0x46, 0x51) : SystemColors.Window;
                foreach (KryptonNumericUpDown nud in new KryptonNumericUpDown[]
                    { this.nudTranscriptMaxWidth, this.nudMessageMaxWidth, this.nudFontSize, this.nudMemoryMaxLines })
                {
                    if (nud == null) continue;
                    nud.BackColor = nudBack;
                    nud.TextAlign = HorizontalAlignment.Left;
                }
            }
            catch { }

            // The navigator fills the client area except the bottom OK/Cancel/Apply strip, which
            // would otherwise show the unthemed form background. Paint it the themed chrome-bar color
            // (the same one the main window's status strip uses) so it matches the window chrome.
            try { if (this.flowLayoutPanel1 != null) this.flowLayoutPanel1.BackColor = KryptonThemeBridge.StatusStripBackColor(); }
            catch { }

            // Compute settings paths under %AppData%\GxPT
            _settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GxPT");
            _settingsFile = Path.Combine(_settingsDir, "settings.json");
            _mcpFile = Path.Combine(_settingsDir, "mcp.json");

            // Configure new font size controls
            try
            {
                if (this.lblFontSize != null) this.lblFontSize.Text = "Chat Font Size";
                if (this.nudFontSize != null)
                {
                    this.nudFontSize.DecimalPlaces = 1;
                    this.nudFontSize.Increment = 0.5M;
                    this.nudFontSize.Minimum = 6M;
                    this.nudFontSize.Maximum = 48M;
                }
            }
            catch { }

            // Wire events (in case not hooked up in designer)
            this.Load += SettingsForm_Load;

            // Some Krypton inputs (KryptonComboBox / KryptonNumericUpDown) raise their
            // Changed events when first realized on screen - which happens after Load has
            // already cleared the dirty flag - spuriously enabling Apply on open. Clear the
            // dirty state once more after the form has finished showing (and the deferred
            // events have flushed) so Apply starts disabled until the user actually edits.
            this.Shown += SettingsForm_Shown;

            // Grey out the memory size limit when memory is disabled (it only applies when on).
            try
            {
                if (this.chkMemoryEnabled != null)
                    this.chkMemoryEnabled.CheckedChanged += delegate
                    {
                        if (this.nudMemoryMaxLines != null)
                            this.nudMemoryMaxLines.Enabled = this.chkMemoryEnabled.Checked;
                    };
            }
            catch { }

            // Configure theme controls (created in Designer)
            try
            {
                if (this.lblTheme != null) this.lblTheme.Text = "Theme";
                if (this.cmbTheme != null)
                {
                    this.cmbTheme.DropDownStyle = ComboBoxStyle.DropDownList;
                    this.cmbTheme.Items.Clear();
                    // Display in Pascal case for readability
                    this.cmbTheme.Items.Add("Light");
                    this.cmbTheme.Items.Add("Dark");
                }
            }
            catch { }

            // Enable Ctrl+S to save settings without closing the form
            this.KeyPreview = true;
            this.KeyDown += SettingsForm_KeyDown;

            // Keep tabs in sync
            this.tabControl1.SelectedPageChanged += TabControl1_SelectedIndexChanged;

            // Keep default model list updated as models are typed
            this.txtModels.TextChanged += TxtModels_TextChanged;

            // A multiline TextBox doesn't wire up Ctrl+A select-all natively (the single-line API key
            // box does), so handle it here.
            this.txtModels.KeyDown += TxtModels_KeyDown;

            // "Recommended models" group: bring the user's list in line with the shipped catalog.
            // Both buttons edit the textbox only; nothing persists until Save (so the user can review
            // or back out). Tooltips carry the "why" (append vs. replace, and the deprecated-model cleanup).
            if (this.btnAddRecommended != null)
                this.btnAddRecommended.Click += BtnAddRecommended_Click;
            if (this.btnReplaceRecommended != null)
                this.btnReplaceRecommended.Click += BtnReplaceRecommended_Click;
            // Manual re-fetch of OpenRouter's model metadata (context window sizes) - the same
            // fetch that runs automatically once a day at app open.
            if (this.btnUpdateModelInfo != null)
                this.btnUpdateModelInfo.Click += BtnUpdateModelInfo_Click;
            try
            {
                _mcpTip.SetToolTip(this.btnAddRecommended,
                    "Add GxPT's recommended models that aren't already in your list. Keeps everything you have.");
                _mcpTip.SetToolTip(this.btnReplaceRecommended,
                    "Replace your list with GxPT's latest recommended models, removing any that are no longer "
                    + "available. Nothing is saved until you click Save.");
                _mcpTip.SetToolTip(this.btnUpdateModelInfo,
                    "Re-download each model's context window size from OpenRouter now (also refreshed "
                    + "automatically once a day). The status bar's context meter uses these.");
            }
            catch { }

            // JSON editor changed -> debounce highlight
            _jsonHighlightTimer = new Timer();
            _jsonHighlightTimer.Interval = 200; // ms
            _jsonHighlightTimer.Tick += JsonHighlightTimer_Tick;
            this.rtbJson.TextChanged += RtbJson_TextChanged;

            // mcp.json editor: same JSON highlighting, debounced.
            _mcpHighlightTimer = new Timer();
            _mcpHighlightTimer.Interval = 200; // ms
            _mcpHighlightTimer.Tick += McpHighlightTimer_Tick;
            this.rtbMcpJson.TextChanged += RtbMcpJson_TextChanged;

            // Configure message max width as percentage UI (50-100)
            try
            {
                if (this.lblMessageMaxWidth != null) this.lblMessageMaxWidth.Text = "Message Max Width (%)";
                if (this.nudMessageMaxWidth != null)
                {
                    this.nudMessageMaxWidth.Minimum = 50;
                    this.nudMessageMaxWidth.Maximum = 100;
                    if (this.nudMessageMaxWidth.Value < 50 || this.nudMessageMaxWidth.Value > 100)
                        this.nudMessageMaxWidth.Value = 90;
                }
            }
            catch { }

            // MCP tab: gate the web-search / GitHub toggles on a plausibly-valid key/PAT.
            try
            {
                this.txtWebSearchKey.TextChanged += McpCredential_TextChanged;
                this.txtGithubPat.TextChanged += McpCredential_TextChanged;
                // Toggling the command server enables/disables its dependent scratch-dir option.
                this.chkMcpCommand.CheckedChanged += McpCredential_TextChanged;
            }
            catch { }

            // Build the agent effort-tier pickers into the Sub-agents groupbox (before dirty-tracking is
            // wired, so the new combos are covered too).
            try { BuildEffortRow(); }
            catch { }

            // In dark mode, KryptonCheckBox captions render dimmer than the labels next to them;
            // brighten them to match. Done after BuildEffortRow so the re-hosted Sub-agents
            // checkbox is included too.
            try { KryptonThemeBridge.FixDarkCheckBoxText(this); } catch { }

            // Track edits across every input so the Apply button can light up only when there are
            // unsaved changes. Wired generically (by control type) so new controls are covered too.
            try { WireDirtyTracking(this); }
            catch { }
            UpdateDialogButtons();
        }

        // Recursively subscribe to the relevant "changed" event of each input control. The handler is
        // guarded by _isSyncing, so programmatic population (load, tab sync, post-save refresh) doesn't
        // count as a user edit.
        private void WireDirtyTracking(Control root)
        {
            if (root == null) return;
            foreach (Control c in root.Controls)
            {
                // TextChanged is a Control-level event, so it covers the stock and
                // Krypton text / rich-text controls alike without a cast.
                if (c is TextBox || c is RichTextBox || c is KryptonTextBox || c is KryptonRichTextBox)
                    c.TextChanged += AnyInput_Changed;
                else if (c is CheckBox)
                    ((CheckBox)c).CheckedChanged += AnyInput_Changed;
                else if (c is KryptonCheckBox)
                    ((KryptonCheckBox)c).CheckedChanged += AnyInput_Changed;
                else if (c is ComboBox)
                    ((ComboBox)c).SelectedIndexChanged += AnyInput_Changed;
                else if (c is KryptonComboBox)
                    ((KryptonComboBox)c).SelectedIndexChanged += AnyInput_Changed;
                else if (c is NumericUpDown)
                    ((NumericUpDown)c).ValueChanged += AnyInput_Changed;
                else if (c is KryptonNumericUpDown)
                    ((KryptonNumericUpDown)c).ValueChanged += AnyInput_Changed;

                if (c.HasChildren) WireDirtyTracking(c);
            }
        }

        private void AnyInput_Changed(object sender, EventArgs e)
        {
            if (_isSyncing) return;
            MarkDirty();
        }

        private void MarkDirty()
        {
            if (_isSyncing) return;
            // Compare actual control values to the loaded baseline instead of trusting that an
            // event means a real edit. A spurious Changed event that leaves every value unchanged
            // (e.g. a Krypton combo re-raising SelectedIndexChanged on first display) compares
            // equal and keeps Apply disabled; a genuine edit differs and enables it.
            _isDirty = _dirtyBaseline != null && CollectTrackedSnapshot() != _dirtyBaseline;
            UpdateDialogButtons();
        }

        // Capture the current baseline of all tracked control values and clear the dirty state.
        // Called after load/show settle and after each successful save, so Apply greys out until
        // the user makes a change that actually differs from the saved state.
        private void ResetDirtyBaseline()
        {
            _dirtyBaseline = CollectTrackedSnapshot();
            _isDirty = false;
            UpdateDialogButtons();
        }

        // A stable string of every tracked input's value (same control set WireDirtyTracking hooks),
        // used purely for equality comparison to detect real edits.
        private string CollectTrackedSnapshot()
        {
            StringBuilder sb = new StringBuilder();
            CollectTrackedSnapshot(this, sb);
            return sb.ToString();
        }

        private void CollectTrackedSnapshot(Control root, StringBuilder sb)
        {
            foreach (Control c in root.Controls)
            {
                if (c is TextBox || c is RichTextBox || c is KryptonTextBox || c is KryptonRichTextBox)
                    sb.Append(c.Name).Append('=').Append(NormalizeText(c.Text)).Append('\n');
                else if (c is CheckBox)
                    sb.Append(c.Name).Append('=').Append(((CheckBox)c).Checked).Append('\n');
                else if (c is KryptonCheckBox)
                    sb.Append(c.Name).Append('=').Append(((KryptonCheckBox)c).Checked).Append('\n');
                else if (c is ComboBox)
                    sb.Append(c.Name).Append('=').Append(((ComboBox)c).Text).Append('\n');
                else if (c is KryptonComboBox)
                    sb.Append(c.Name).Append('=').Append(((KryptonComboBox)c).Text).Append('\n');
                // Use .Text, not .Value: reading NumericUpDown.Value forces ValidateEditText,
                // which snaps the half-typed number into [Minimum,Maximum] and rewrites the display
                // mid-edit (so typing a digit below the minimum jumps straight to the minimum). The
                // text is all we need to detect an edit.
                else if (c is NumericUpDown)
                    sb.Append(c.Name).Append('=').Append(((NumericUpDown)c).Text).Append('\n');
                else if (c is KryptonNumericUpDown)
                    sb.Append(c.Name).Append('=').Append(((KryptonNumericUpDown)c).Text).Append('\n');

                if (c.Controls.Count > 0) CollectTrackedSnapshot(c, sb);
            }
        }

        // A RichTextBox normalizes its line endings (and may add/drop a trailing newline) when its
        // handle is first created - which happens the first time its tab is shown. That makes the JSON
        // editors read differently after a tab switch than when the baseline was captured (those tabs
        // unrealized), spuriously enabling Apply. Compare on normalized line endings with trailing
        // whitespace removed so the realize artifact is ignored while real content edits still differ.
        private static string NormalizeText(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd();
        }

        private void SettingsForm_Shown(object sender, EventArgs e)
        {
            // Re-establish the baseline after the form has finished showing, so any Changed events
            // Krypton inputs raise as they're first realized on screen are folded into the baseline
            // rather than treated as edits. Deferred via BeginInvoke so it runs once the initial
            // display has settled; the form always opens clean.
            try
            {
                BeginInvoke((MethodInvoker)delegate { ResetDirtyBaseline(); });
            }
            catch { }
        }

        // OK and Cancel are always enabled (standard Windows practice); Apply only when there are
        // unsaved changes.
        private void UpdateDialogButtons()
        {
            try { if (this.btnApply != null) this.btnApply.Enabled = _isDirty; }
            catch { }
        }

        private void McpCredential_TextChanged(object sender, EventArgs e)
        {
            // Suppressed during programmatic population (ApplyMcpToControls runs it once at the end).
            if (_isSyncing) return;
            UpdateMcpEnableStates();
        }

        private void SettingsForm_Load(object sender, EventArgs e)
        {
            try
            {
                EnsureSettingsFileExists();
                // Load file into working copy, then populate both views
                var raw = File.ReadAllText(_settingsFile, Encoding.UTF8);
                if (!TryDeserialize(raw, out _working))
                {
                    _working = BuildDefaultSettings();
                }

                _isSyncing = true;
                try
                {
                    ApplySettingsToVisualControls(_working);
                    UpdateJsonEditorFromSettings(_working);
                    ApplyMcpToControls(_working);
                }
                finally { _isSyncing = false; }

                // mcp.json lives in its own file beside settings.json.
                LoadMcpJsonEditor();

                // Nothing the user did yet: snapshot the populated controls as the baseline so
                // Apply starts disabled (the Shown handler refreshes it once the display settles).
                ResetDirtyBaseline();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to load settings: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void EnsureSettingsFileExists()
        {
            if (!Directory.Exists(_settingsDir))
            {
                Directory.CreateDirectory(_settingsDir);
            }

            // One seed path (issue #164): AppSettings fills every absent key from the schema and creates
            // the file if needed, so the file we read below is always complete - no per-form default JSON.
            AppSettings.EnsureSeeded();
        }

        internal static float GetChatDefaultFontSize()
        {
            try
            {
                using (var ctl = new ChatTranscriptControl())
                {
                    var f = ctl.Font;
                    return (f != null ? f.Size : 9f);
                }
            }
            catch { return 9f; }
        }

        // Strongly-typed default object, derived from the one schema (SettingsSchema) so it can't drift
        // from the seeded file. Used only as the in-memory fallback when settings.json fails to parse.
        private static SettingsData BuildDefaultSettings()
        {
            SettingsData s = null;
            try
            {
                var ser = new JavaScriptSerializer();
                s = ser.Deserialize<SettingsData>(ser.Serialize(SettingsSchema.BuildDefaults()));
            }
            catch { }
            if (s == null) s = new SettingsData();
            PostProcess(s);
            return s;
        }

        private bool SaveSettingsOnly()
        {
            try
            {
                if (!Directory.Exists(_settingsDir))
                {
                    Directory.CreateDirectory(_settingsDir);
                }

                // Ensure working copy reflects active tab before saving
                if (!SyncWorkingSettingsFromActiveTab(true))
                {
                    // If JSON invalid, we already notified the user. Abort save.
                    return false;
                }

                // Validate the mcp.json editor before writing anything; fold MCP toggles + web key
                // into the working settings so they persist to settings.json.
                string mcpJsonText;
                if (!TryValidateMcpJson(out mcpJsonText)) return false;
                CaptureMcpControlsToWorking(_working);

                var json = Serialize(_working);
                File.WriteAllText(_settingsFile, json, Encoding.UTF8);
                WriteMcpJson(mcpJsonText);

                // Refresh JSON editor with normalized JSON
                _isSyncing = true;
                try { UpdateJsonEditorFromSettings(_working); }
                finally { _isSyncing = false; }

                // If currently on the JSON tab, re-apply highlighting once post-save
                if (this.tabControl1.SelectedPage == this.tabJson)
                {
                    try { BeginInvoke(new Action(HighlightJsonNow)); }
                    catch { /* ignore */ }
                }

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to save settings: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        // OK: save and close.
        private void btnOk_Click(object sender, EventArgs e)
        {
            if (SaveSettingsOnly())
            {
                _isDirty = false;
                this.DialogResult = DialogResult.OK;
                this.Close();
            }
        }

        // Apply: save without closing. Disabled (so unreachable) unless there are unsaved changes.
        private void btnApply_Click(object sender, EventArgs e)
        {
            ApplyChanges();
        }

        // Persist the working settings, then clear the dirty state on success so Apply greys out again.
        // Shared by the Apply button and Ctrl+S. Cancel needs no handler: it neither saves nor leaves a
        // dirty file (the form only writes on save), so closing simply discards the unsaved edits.
        private bool ApplyChanges()
        {
            bool ok = SaveSettingsOnly();
            if (ok)
            {
                // Saved state is the new baseline, so Apply greys out until the next real edit.
                ResetDirtyBaseline();
            }
            return ok;
        }

        private void SettingsForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.S)
            {
                e.SuppressKeyPress = true; // prevent ding
                ApplyChanges(); // Save without closing the form
            }
        }

        // Tab synchronization logic
        private void TabControl1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_isSyncing) return;

            // A tab switch runs a sync (regenerate the JSON view, realize the page's controls) that
            // isn't a user edit. If nothing was edited before the switch, fold the resulting derived /
            // realize-time values into the baseline once the new page settles so Apply stays disabled;
            // if edits were already pending, leave the dirty state untouched.
            bool wasDirty = _isDirty;

            // The MCP tab edits mcp.json + its own settings; it is independent of the settings.json
            // visual/JSON sync. Just (re)highlight its editor on entry.
            if (this.tabControl1.SelectedPage == this.tabMcp)
            {
                try { BeginInvoke(new Action(HighlightMcpJsonNow)); }
                catch { /* ignore */ }
                RebaselineIfClean(wasDirty);
                return;
            }

            bool toJson = this.tabControl1.SelectedPage == this.tabJson;

            _isSyncing = true;
            try
            {
                if (toJson)
                {
                    // Visual -> working -> JSON
                    CaptureVisualControlsToSettings(_working);
                    UpdateJsonEditorFromSettings(_working);
                }
                else
                {
                    // JSON -> working -> Visual
                    SettingsData parsed;
                    string error;
                    if (!TryParseJsonEditorToSettings(out parsed, out error))
                    {
                        _isSyncing = false; // allow tab change
                        var choice = ShowJsonInvalidPrompt(this,
                            "JSON Parse Error",
                            "The JSON is invalid. Reload the last saved settings, or continue editing?",
                            error);
                        _isSyncing = true;

                        if (choice == JsonPromptChoice.Edit)
                        {
                            // Stay on JSON tab
                            this.tabControl1.SelectedPage = this.tabJson;
                            return;
                        }
                        else
                        {
                            // Reload from disk and proceed to Visual tab
                            try
                            {
                                var raw = File.ReadAllText(_settingsFile, Encoding.UTF8);
                                SettingsData loaded;
                                if (!TryDeserialize(raw, out loaded))
                                {
                                    _isSyncing = false;
                                    MessageBox.Show(this, "Could not reload the last saved settings because the file is invalid.",
                                        "Reload Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                    _isSyncing = true;
                                    // Stay on JSON tab to edit
                                    this.tabControl1.SelectedPage = this.tabJson;
                                    return;
                                }
                                _working = loaded;
                                ApplySettingsToVisualControls(_working);
                                UpdateJsonEditorFromSettings(_working);
                                // Stay on JSON tab so the user can continue editing there
                                this.tabControl1.SelectedPage = this.tabJson;
                                return;
                            }
                            catch (Exception ex)
                            {
                                _isSyncing = false;
                                MessageBox.Show(this, "Failed to reload settings: " + ex.Message, "Reload Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                _isSyncing = true;
                                this.tabControl1.SelectedPage = this.tabJson;
                                return;
                            }
                        }
                    }

                    _working = parsed;
                    ApplySettingsToVisualControls(_working);
                }
            }
            finally { _isSyncing = false; }

            // When switching to JSON tab, run a one-time highlight
            if (toJson)
            {
                try { BeginInvoke(new Action(HighlightJsonNow)); }
                catch { /* ignore */ }
            }

            RebaselineIfClean(wasDirty);
        }

        // After a tab switch that wasn't preceded by edits, the values the sync produced (regenerated
        // JSON text, RichTextBox handle-creation reformatting, Krypton controls realized for the first
        // time) are not user edits - so recapture the baseline once the new page has settled, leaving
        // Apply disabled. If edits were already pending, keep the dirty state as-is. Deferred via
        // BeginInvoke so it runs after the realize/highlight events for the new page have flushed.
        private void RebaselineIfClean(bool wasDirty)
        {
            if (wasDirty) return;
            try { BeginInvoke((MethodInvoker)delegate { ResetDirtyBaseline(); }); }
            catch { }
        }

        // --- Serialization helpers (JavaScriptSerializer for .NET 3.5) ---
        private static bool TryDeserialize(string json, out SettingsData settings)
        {
            try
            {
                var ser = new JavaScriptSerializer();
                settings = ser.Deserialize<SettingsData>(json) ?? new SettingsData();
                PostProcess(settings);
                return true;
            }
            catch
            {
                settings = new SettingsData();
                return false;
            }
        }

        private static string Serialize(SettingsData settings)
        {
            var ser = new JavaScriptSerializer();
            return ser.Serialize(settings);
        }

        private void UpdateJsonEditorFromSettings(SettingsData settings)
        {
            // SettingsData now models every global key, so the typed projection IS the file - no
            // free-form merge needed (issue #164).
            var json = Serialize(settings);
            this.rtbJson.Text = PrettyPrintJson(json);
            // Do not trigger highlight here; only on TextChanged
        }

        private bool TryParseJsonEditorToSettings(out SettingsData settings, out string error)
        {
            try
            {
                var ser = new JavaScriptSerializer();
                settings = ser.Deserialize<SettingsData>(this.rtbJson.Text ?? string.Empty) ?? new SettingsData();
                PostProcess(settings);
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                settings = new SettingsData();
                error = ex.Message;
                return false;
            }
        }

        // --- JSON RichTextBox syntax highlighting (debounced) ---
        private void RtbJson_TextChanged(object sender, EventArgs e)
        {
            if (_isSyncing || _isHighlighting) return;

            // Compute an edited range covering the current line +/- one adjacent line
            int caret = this.rtbJson.SelectionStart;
            int selLen = this.rtbJson.SelectionLength;
            int totalLines = this.rtbJson.Lines != null ? this.rtbJson.Lines.Length : 0;

            int startLine = this.rtbJson.GetLineFromCharIndex(Math.Max(0, caret));
            int endLine = this.rtbJson.GetLineFromCharIndex(Math.Max(0, caret + Math.Max(0, selLen - 1)));
            if (totalLines > 0)
            {
                startLine = Math.Max(0, startLine - 1);
                endLine = Math.Min(totalLines - 1, endLine + 1);
            }

            int startPos = this.rtbJson.GetFirstCharIndexFromLine(startLine);
            if (startPos < 0) startPos = 0;
            int nextLinePos = (endLine + 1 < totalLines) ? this.rtbJson.GetFirstCharIndexFromLine(endLine + 1) : this.rtbJson.TextLength;
            int endPos = Math.Max(startPos, nextLinePos);

            // Merge into pending range
            if (_pendingHighlightStart < 0)
            {
                _pendingHighlightStart = startPos;
                _pendingHighlightEnd = endPos;
            }
            else
            {
                _pendingHighlightStart = Math.Min(_pendingHighlightStart, startPos);
                _pendingHighlightEnd = Math.Max(_pendingHighlightEnd, endPos);
            }

            HighlightJsonSoon();
        }

        private void JsonHighlightTimer_Tick(object sender, EventArgs e)
        {
            _jsonHighlightTimer.Stop();
            int start = _pendingHighlightStart;
            int end = _pendingHighlightEnd;
            _pendingHighlightStart = -1;
            _pendingHighlightEnd = -1;
            if (start >= 0 && end >= start)
            {
                HighlightJsonRange(start, end - start);
            }
            else
            {
                HighlightJsonNow();
            }
        }

        private void HighlightJsonSoon()
        {
            if (_jsonHighlightTimer != null)
            {
                _jsonHighlightTimer.Stop();
                _jsonHighlightTimer.Start();
            }
        }

        private void HighlightJsonNow()
        {
            try { _isHighlighting = true; ApplyJsonHighlightFull(this.rtbJson); }
            catch { /* ignore */ }
            finally { _isHighlighting = false; }
        }

        // Full-document JSON highlight for any RichTextBox (shared by the settings JSON tab and the
        // mcp.json editor) so both render identically.
        private static void ApplyJsonHighlightFull(RichTextBox rtb)
        {
            if (rtb == null || rtb.IsDisposed) return;

            string text = rtb.Text ?? string.Empty;
            int savedStart = rtb.SelectionStart;
            int savedLength = rtb.SelectionLength;

            rtb.SuspendLayout();
            try
            {
                // Reset to default color
                rtb.SelectionStart = 0;
                rtb.SelectionLength = rtb.TextLength;
                rtb.SelectionColor = SystemColors.WindowText;

                if (text.Length > 0)
                {
                    var tokens = SyntaxHighlighter.Highlight("json", text);
                    int maxLen = rtb.TextLength;
                    for (int i = 0; i < tokens.Count; i++)
                    {
                        var t = tokens[i];
                        if (t.Type == TokenType.Normal || t.Length <= 0 || t.StartIndex < 0 || t.StartIndex >= maxLen) continue;

                        int length = t.Length;
                        int end = t.StartIndex + length;
                        if (end > maxLen)
                        {
                            length = Math.Max(0, maxLen - t.StartIndex);
                            if (length == 0) continue;
                        }

                        rtb.SelectionStart = t.StartIndex;
                        rtb.SelectionLength = length;
                        rtb.SelectionColor = SyntaxHighlighter.GetTokenColor(t.Type);
                    }
                }

                // Restore caret
                rtb.SelectionStart = Math.Max(0, Math.Min(savedStart, rtb.TextLength));
                rtb.SelectionLength = Math.Max(0, Math.Min(savedLength, rtb.TextLength - rtb.SelectionStart));
            }
            finally
            {
                rtb.ResumeLayout();
                rtb.Invalidate();
            }
        }

        // --- mcp.json editor highlighting (debounced; reuses the JSON tab's tokenizer/colors) ---
        private void RtbMcpJson_TextChanged(object sender, EventArgs e)
        {
            if (_isSyncing || _isMcpHighlighting) return;
            if (_mcpHighlightTimer != null) { _mcpHighlightTimer.Stop(); _mcpHighlightTimer.Start(); }
        }

        private void McpHighlightTimer_Tick(object sender, EventArgs e)
        {
            _mcpHighlightTimer.Stop();
            HighlightMcpJsonNow();
        }

        private void HighlightMcpJsonNow()
        {
            try { _isMcpHighlighting = true; ApplyJsonHighlightFull(this.rtbMcpJson); }
            catch { /* ignore */ }
            finally { _isMcpHighlighting = false; }
        }

        private void HighlightJsonRange(int start, int length)
        {
            if (length <= 0) return;
            try
            {
                _isHighlighting = true;
                var rtb = this.rtbJson;
                if (rtb == null || rtb.IsDisposed) return;

                int maxLen = rtb.TextLength;
                if (start >= maxLen) return;
                if (start + length > maxLen) length = Math.Max(0, maxLen - start);
                if (length == 0) return;

                string segment = (rtb.Text ?? string.Empty).Substring(start, length);

                // Save caret
                int savedStart = rtb.SelectionStart;
                int savedLength = rtb.SelectionLength;

                rtb.SuspendLayout();

                // Reset segment to default color
                rtb.SelectionStart = start;
                rtb.SelectionLength = length;
                rtb.SelectionColor = SystemColors.WindowText;

                var tokens = SyntaxHighlighter.Highlight("json", segment);
                for (int i = 0; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t.Type == TokenType.Normal || t.Length <= 0) continue;
                    int tStart = start + t.StartIndex;
                    int tLen = t.Length;
                    if (tStart < 0 || tStart >= maxLen) continue;
                    if (tStart + tLen > maxLen)
                    {
                        tLen = Math.Max(0, maxLen - tStart);
                        if (tLen == 0) continue;
                    }

                    rtb.SelectionStart = tStart;
                    rtb.SelectionLength = tLen;
                    rtb.SelectionColor = SyntaxHighlighter.GetTokenColor(t.Type);
                }

                // Restore caret
                rtb.SelectionStart = Math.Max(0, Math.Min(savedStart, rtb.TextLength));
                rtb.SelectionLength = Math.Max(0, Math.Min(savedLength, rtb.TextLength - rtb.SelectionStart));
            }
            catch
            {
                // ignore
            }
            finally
            {
                this.rtbJson.ResumeLayout();
                this.rtbJson.Invalidate();
                _isHighlighting = false;
            }
        }

        // Pretty-print JSON for display in the JSON tab (works on .NET 3.5)
        private static string PrettyPrintJson(string json)
        {
            if (json == null) return string.Empty;
            int indent = 0;
            bool inQuotes = false;
            bool escape = false;
            var sb = new StringBuilder(json.Length * 2);
            for (int i = 0; i < json.Length; i++)
            {
                char ch = json[i];

                if (escape)
                {
                    sb.Append(ch);
                    escape = false;
                    continue;
                }

                if (ch == '\\')
                {
                    sb.Append(ch);
                    if (inQuotes) escape = true;
                    continue;
                }

                if (ch == '"')
                {
                    sb.Append(ch);
                    inQuotes = !inQuotes;
                    continue;
                }

                if (inQuotes)
                {
                    sb.Append(ch);
                    continue;
                }

                switch (ch)
                {
                    case '{':
                    case '[':
                        sb.Append(ch);
                        sb.Append(Environment.NewLine);
                        indent++;
                        sb.Append(new string(' ', indent * 2));
                        break;
                    case '}':
                    case ']':
                        sb.Append(Environment.NewLine);
                        indent = Math.Max(0, indent - 1);
                        sb.Append(new string(' ', indent * 2));
                        sb.Append(ch);
                        break;
                    case ',':
                        sb.Append(ch);
                        sb.Append(Environment.NewLine);
                        sb.Append(new string(' ', indent * 2));
                        break;
                    case ':':
                        sb.Append(ch);
                        sb.Append(' ');
                        break;
                    default:
                        if (!char.IsWhiteSpace(ch)) sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        private static void PostProcess(SettingsData s)
        {
            if (s.models == null) s.models = new List<string>();
            // Trim and de-duplicate models
            var cleaned = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in s.models)
            {
                if (m == null) continue;
                var t = m.Trim();
                if (t.Length == 0) continue;
                if (seen.Add(t)) cleaned.Add(t);
            }
            s.models = cleaned;

            if (string.IsNullOrEmpty(s.default_model))
            {
                s.default_model = s.models.Count > 0 ? s.models[0] : "openai/gpt-4o";
            }

            // Clamp or set default font size
            try
            {
                double fs = s.font_size;
                if (fs <= 0) fs = GetChatDefaultFontSize();
                if (fs < 6) fs = 6; if (fs > 48) fs = 48;
                s.font_size = fs;
            }
            catch { s.font_size = GetChatDefaultFontSize(); }

            // Theme normalization
            try
            {
                string t = s.theme ?? "";
                t = t.Trim().ToLowerInvariant();
                if (t != "dark" && t != "light") t = "light";
                s.theme = t;
            }
            catch { s.theme = "light"; }

            // Color theme normalization (id string)
            try
            {
                string ct = s.color_theme ?? "blue";
                ct = ct.Trim();
                if (ct.Length == 0) ct = "blue";
                // Keep as-is; ThemeService will fallback if unknown
                s.color_theme = ct;
            }
            catch { s.color_theme = "blue"; }

            // Transcript/message width normalization
            try
            {
                int tw = s.transcript_max_width;
                if (tw <= 0) tw = 1000;
                if (tw < 300) tw = 300; if (tw > 1900) tw = 1900;
                s.transcript_max_width = tw;
            }
            catch { s.transcript_max_width = 1000; }
            try
            {
                int tw = s.transcript_max_width > 0 ? s.transcript_max_width : 1000;
                int raw = s.message_max_width;
                int p;
                // If outside 50..100, interpret as legacy pixels and convert to percent
                if (raw < 50 || raw > 100)
                {
                    if (raw <= 0) p = 90;
                    else p = (int)Math.Round(100.0 * raw / tw);
                }
                else p = raw;
                if (p < 50) p = 50; if (p > 100) p = 100;
                s.message_max_width = p;
            }
            catch { s.message_max_width = 90; }

            // ZDR replaced the old data-collection setting. If a settings file predates ZDR (no
            // provider_zdr), migrate once from the old preference: data_collection "deny" (false)
            // means the user already wanted no retention, so default ZDR on.
            if (!s.provider_zdr.HasValue)
                s.provider_zdr = !s.provider_data_collection;
        }

        // --- Visual controls <-> working settings ---
        private void ApplySettingsToVisualControls(SettingsData s)
        {
            if (s == null) s = new SettingsData();
            if (s.models == null) s.models = new List<string>();
            // API Key
            this.txtApiKey.Text = s.openrouter_api_key ?? string.Empty;

            // Enable logging
            this.chkEnableLogging.Checked = s.enable_logging;

            // Models list (one per line)
            var lines = (s.models != null && s.models.Count > 0) ? s.models.ToArray() : new string[0];
            this.txtModels.Lines = lines;

            // Default model combobox
            this.cmbDefaultModel.BeginUpdate();
            try
            {
                this.cmbDefaultModel.Items.Clear();
                foreach (var m in s.models) this.cmbDefaultModel.Items.Add(m);

                // Ensure selection
                var def = s.default_model ?? string.Empty;
                if (!string.IsNullOrEmpty(def) && !s.models.Any(x => string.Equals(x, def, StringComparison.OrdinalIgnoreCase)))
                {
                    this.cmbDefaultModel.Items.Add(def);
                }
                this.cmbDefaultModel.SelectedItem = def;
            }
            finally { this.cmbDefaultModel.EndUpdate(); }

            // Effort-tier pickers: same model list, each seeded with its configured (or default) model.
            SyncEffortCombo(this.cmbEffortLow, s.models, s.model_effort_low);
            SyncEffortCombo(this.cmbEffortMedium, s.models, s.model_effort_medium);
            SyncEffortCombo(this.cmbEffortHigh, s.models, s.model_effort_high);

            // Font size
            try
            {
                decimal val = (decimal)(s.font_size > 0 ? s.font_size : GetChatDefaultFontSize());
                if (val < this.nudFontSize.Minimum) val = this.nudFontSize.Minimum;
                if (val > this.nudFontSize.Maximum) val = this.nudFontSize.Maximum;
                this.nudFontSize.Value = val;
            }
            catch { }

            // Theme
            try
            {
                string t = s.theme ?? "light";
                if (this.cmbTheme != null)
                {
                    // Ensure items are Pascal case
                    this.cmbTheme.Items.Clear();
                    this.cmbTheme.Items.Add("Light");
                    this.cmbTheme.Items.Add("Dark");
                    // Map stored lowercase to displayed Pascal case
                    string disp = string.Equals(t, "dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
                    this.cmbTheme.SelectedItem = disp;
                }
            }
            catch { }

            // Color Theme (populate from ThemeService)
            try
            {
                if (this.cmbColor != null)
                {
                    var themes = ThemeService.GetAvailableThemes();
                    // Provide a stable list even if service returns none
                    if (themes == null) themes = new List<ThemeInfo>();
                    this.cmbColor.BeginUpdate();
                    try
                    {
                        this.cmbColor.DataSource = null; // reset binding
                        this.cmbColor.Items.Clear();
                        // Bind to theme list showing Name with Id as value
                        this.cmbColor.DisplayMember = "Name";
                        this.cmbColor.ValueMember = "Id";
                        this.cmbColor.DataSource = themes;
                        // Select current color theme id (default blue)
                        string ct = s.color_theme;
                        if (string.IsNullOrEmpty(ct)) ct = "blue";
                        this.cmbColor.SelectedValue = ct;
                    }
                    finally { this.cmbColor.EndUpdate(); }
                }
            }
            catch { }

            // Global ZDR default (migrated from the old data-collection pref when absent).
            try
            {
                bool zdr = s.provider_zdr.HasValue ? s.provider_zdr.Value : !s.provider_data_collection;
                if (this.chkZdr != null) this.chkZdr.Checked = zdr;
            }
            catch { }

            // Persistent project memory: enable toggle + soft index line cap.
            try
            {
                if (this.chkMemoryEnabled != null) this.chkMemoryEnabled.Checked = s.mcp_memory_enabled;
                if (this.nudMemoryMaxLines != null)
                {
                    decimal ml = (decimal)(s.mcp_memory_max_lines > 0 ? s.mcp_memory_max_lines : 40);
                    if (ml < this.nudMemoryMaxLines.Minimum) ml = this.nudMemoryMaxLines.Minimum;
                    if (ml > this.nudMemoryMaxLines.Maximum) ml = this.nudMemoryMaxLines.Maximum;
                    this.nudMemoryMaxLines.Value = ml;
                    this.nudMemoryMaxLines.Enabled = (this.chkMemoryEnabled == null) || this.chkMemoryEnabled.Checked;
                }
            }
            catch { }

            // Transcript Max Width and Message Max Width (%)
            try
            {
                if (this.nudTranscriptMaxWidth != null)
                {
                    decimal tw = (decimal)(s.transcript_max_width > 0 ? s.transcript_max_width : 1000);
                    if (tw < this.nudTranscriptMaxWidth.Minimum) tw = this.nudTranscriptMaxWidth.Minimum;
                    if (tw > this.nudTranscriptMaxWidth.Maximum) tw = this.nudTranscriptMaxWidth.Maximum;
                    this.nudTranscriptMaxWidth.Value = tw;
                }
                if (this.nudMessageMaxWidth != null)
                {
                    // Configure as percentage 50..100
                    try { this.nudMessageMaxWidth.Minimum = 50; this.nudMessageMaxWidth.Maximum = 100; }
                    catch { }
                    decimal p = (decimal)(s.message_max_width > 0 ? s.message_max_width : 90);
                    if (p < this.nudMessageMaxWidth.Minimum) p = this.nudMessageMaxWidth.Minimum;
                    if (p > this.nudMessageMaxWidth.Maximum) p = this.nudMessageMaxWidth.Maximum;
                    this.nudMessageMaxWidth.Value = p;
                }
            }
            catch { }

            // Load runs with _isSyncing set, which suppresses TxtModels_TextChanged, so set the
            // recommended-models button states explicitly here.
            UpdateRecommendedButtonStates();
        }

        private void CaptureVisualControlsToSettings(SettingsData target)
        {
            target.openrouter_api_key = this.txtApiKey.Text ?? string.Empty;
            target.enable_logging = this.chkEnableLogging.Checked;

            // Models from multiline textbox
            var models = new List<string>();
            if (this.txtModels.Lines != null)
            {
                foreach (var line in this.txtModels.Lines)
                {
                    if (line == null) continue;
                    var t = line.Trim();
                    if (t.Length > 0 && !models.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) models.Add(t);
                }
            }
            target.models = models;

            // Default model from combo (SelectedItem preferred, fallback to Text)
            var sel = this.cmbDefaultModel.SelectedItem as string;
            if (string.IsNullOrEmpty(sel)) sel = this.cmbDefaultModel.Text;
            if (string.IsNullOrEmpty(sel)) sel = models.FirstOrDefault() ?? string.Empty;
            target.default_model = sel ?? string.Empty;

            // Effort tiers from their combos (fall back to the existing working value if a combo is empty,
            // so a tier is never silently wiped).
            target.model_effort_low = EffortComboValue(this.cmbEffortLow, target.model_effort_low);
            target.model_effort_medium = EffortComboValue(this.cmbEffortMedium, target.model_effort_medium);
            target.model_effort_high = EffortComboValue(this.cmbEffortHigh, target.model_effort_high);

            // Font size
            try { target.font_size = (double)this.nudFontSize.Value; }
            catch { target.font_size = GetChatDefaultFontSize(); }

            // Theme
            try
            {
                var themeSel = this.cmbTheme != null ? (this.cmbTheme.SelectedItem as string) : null;
                if (string.IsNullOrEmpty(themeSel) && this.cmbTheme != null) themeSel = this.cmbTheme.Text;
                if (string.IsNullOrEmpty(themeSel)) themeSel = "light";
                // Store as lowercase regardless of displayed casing
                target.theme = (themeSel ?? "light").Trim().ToLowerInvariant();
            }
            catch { target.theme = "light"; }

            // Color Theme id
            try
            {
                string ct = null;
                if (this.cmbColor != null)
                {
                    // Prefer SelectedValue from data-bound list
                    object val = this.cmbColor.SelectedValue;
                    if (val != null) ct = Convert.ToString(val);
                    if (string.IsNullOrEmpty(ct)) ct = this.cmbColor.Text;
                }
                if (string.IsNullOrEmpty(ct)) ct = "blue";
                target.color_theme = ct;
            }
            catch { if (string.IsNullOrEmpty(target.color_theme)) target.color_theme = "blue"; }

            // Global ZDR default from the checkbox.
            try
            {
                if (this.chkZdr != null) target.provider_zdr = this.chkZdr.Checked;
            }
            catch { }

            // Persistent project memory toggle + soft index cap.
            try
            {
                if (this.chkMemoryEnabled != null) target.mcp_memory_enabled = this.chkMemoryEnabled.Checked;
                if (this.nudMemoryMaxLines != null)
                {
                    int ml = (int)this.nudMemoryMaxLines.Value;
                    target.mcp_memory_max_lines = ml > 0 ? ml : 40;
                }
            }
            catch { if (target.mcp_memory_max_lines <= 0) target.mcp_memory_max_lines = 40; }

            // Transcript width and Message width percent
            try { target.transcript_max_width = (int)this.nudTranscriptMaxWidth.Value; }
            catch { if (target.transcript_max_width <= 0) target.transcript_max_width = 1000; }
            try
            {
                int p = (int)this.nudMessageMaxWidth.Value;
                if (p < 50) p = 50; if (p > 100) p = 100;
                target.message_max_width = p; // store percent
            }
            catch { if (target.message_max_width <= 0) target.message_max_width = 90; }
        }

        // Append any recommended models the user doesn't already have, preserving their existing list
        // and order. Case-insensitive de-dupe mirrors PostProcess so the textbox stays clean.
        private void BtnAddRecommended_Click(object sender, EventArgs e)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            if (this.txtModels.Lines != null)
            {
                foreach (var line in this.txtModels.Lines)
                {
                    if (line == null) continue;
                    var t = line.Trim();
                    if (t.Length == 0) continue;
                    if (seen.Add(t)) result.Add(t);
                }
            }

            bool added = false;
            foreach (var m in ModelDefaults.Models)
            {
                if (seen.Add(m)) { result.Add(m); added = true; }
            }

            // The button is disabled when there's nothing to add, so this is just a guard.
            if (!added) return;

            // Setting Lines fires TxtModels_TextChanged, which refreshes the combo and button states.
            this.txtModels.Lines = result.ToArray();
        }

        // Force-refresh the model context-size catalog (ModelCatalogService) in the background.
        // The button doubles as the progress indicator; quiet on success (the status bar's
        // context meter updates by itself via CatalogUpdated), a message box only on failure -
        // the one case where the user, who explicitly asked, would otherwise see nothing happen.
        private void BtnUpdateModelInfo_Click(object sender, EventArgs e)
        {
            var btn = this.btnUpdateModelInfo;
            if (btn == null || !btn.Enabled) return;
            string idleText = btn.Text;
            btn.Enabled = false;
            btn.Text = "Updating...";
            ModelCatalogService.ForceRefresh(delegate(bool ok)
            {
                // Worker thread; the form may have been closed while the fetch ran.
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)delegate
                    {
                        try
                        {
                            btn.Text = idleText;
                            btn.Enabled = true;
                            if (!ok)
                                MessageBox.Show(this,
                                    "Could not update model info from OpenRouter. Check your network connection and try again.",
                                    "Update Model Info", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        catch { }
                    });
                }
                catch { }
            });
        }

        // Replace the entire list with the shipped catalog. Confirmed because it discards the user's
        // customizations; the actual write still waits for Save.
        private void BtnReplaceRecommended_Click(object sender, EventArgs e)
        {
            var answer = MessageBox.Show(this,
                "Replace your entire model list with GxPT's latest recommended models?\r\n\r\n"
                + "This removes any models you've added, including ones that may no longer be available. "
                + "Nothing is saved until you click Save.",
                "Replace model list", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (answer != DialogResult.OK) return;

            this.txtModels.Lines = (string[])ModelDefaults.Models.Clone();
        }

        // Enable the recommended-models buttons based on how the current list compares to the shipped
        // catalog (independent of the acknowledged-hash that drives the banner). "Add to list" is enabled
        // whenever a recommended model is missing; "Replace list..." whenever the list isn't already
        // exactly the recommended set/order - so it doubles as a "reset to default" even after the user
        // applied the recommendations and then tweaked them.
        private void UpdateRecommendedButtonStates()
        {
            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var current = new List<string>();
                if (this.txtModels.Lines != null)
                {
                    foreach (var line in this.txtModels.Lines)
                    {
                        if (line == null) continue;
                        var t = line.Trim();
                        if (t.Length == 0) continue;
                        if (seen.Add(t)) current.Add(t);
                    }
                }

                bool anyMissing = false;
                foreach (var m in ModelDefaults.Models)
                {
                    if (!seen.Contains(m)) { anyMissing = true; break; }
                }

                bool matchesRecommended = (current.Count == ModelDefaults.Models.Length);
                if (matchesRecommended)
                {
                    for (int i = 0; i < current.Count; i++)
                    {
                        if (!string.Equals(current[i], ModelDefaults.Models[i], StringComparison.OrdinalIgnoreCase))
                        {
                            matchesRecommended = false;
                            break;
                        }
                    }
                }

                if (this.btnAddRecommended != null) this.btnAddRecommended.Enabled = anyMissing;
                if (this.btnReplaceRecommended != null) this.btnReplaceRecommended.Enabled = !matchesRecommended;
            }
            catch { }
        }

        // When the models textbox changes, add any new non-empty lines to the default model combo box
        private void TxtModels_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.A)
            {
                this.txtModels.SelectAll();
                e.SuppressKeyPress = true; // prevent the ding and the default (no-op) handling
                e.Handled = true;
            }
        }

        // Builds the effort-tier model pickers into the "Sub-agents" groupbox (Tools tab) in code, so the
        // designer's layout is left untouched. The existing enable checkbox and the new pickers are re-hosted
        // in a small docked table; the group's row in tblMcp is grown to fit (its neighbour is Percent(100),
        // which absorbs the change, so no other section moves).
        private void BuildEffortRow()
        {
            if (this.grpAgents == null || this.chkAgents == null) return;
            this.grpAgents.SuspendLayout();
            try
            {
                // Give the Sub-agents group room for the stacked caption + combo row (its tblMcp row is
                // Absolute height).
                if (this.tblMcp != null && this.tblMcp.RowStyles.Count > 2
                    && this.tblMcp.RowStyles[2] is RowStyle)
                {
                    ((RowStyle)this.tblMcp.RowStyles[2]).SizeType = SizeType.Absolute;
                    ((RowStyle)this.tblMcp.RowStyles[2]).Height = 112F;
                }

                // Re-host the existing enable checkbox + the effort grid in a 2-row table docked into the group
                // (the checkbox keeps its name/state/wiring - only its parent changes).
                // A KryptonGroupBox hosts its content on .Panel (not .Controls); adding to
                // .Controls would put the table behind the panel and hide everything.
                this.grpAgents.Panel.Controls.Remove(this.chkAgents);
                this.chkAgents.Anchor = AnchorStyles.Left;
                this.chkAgents.Margin = new Padding(3, 3, 3, 2);

                TableLayoutPanel layout = new TableLayoutPanel();
                layout.Dock = DockStyle.Fill;
                layout.ColumnCount = 1;
                layout.RowCount = 2;
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F));
                layout.Controls.Add(this.chkAgents, 0, 0);
                layout.Controls.Add(BuildEffortGrid(), 0, 1);
                this.grpAgents.Panel.Controls.Add(layout);

                // These containers are built after the constructor's theming pass, so
                // make them transparent now too - otherwise the stock TableLayoutPanels
                // paint a light rectangle over the dark group panel in dark mode.
                KryptonThemeBridge.MakeLayoutContainersTransparent(this.grpAgents);
            }
            finally { this.grpAgents.ResumeLayout(); }
        }

        // The effort-tier pickers: a 4-column x 2-row grid. Column 0 is the "Models" row-label (spanning both
        // rows so it centers vertically against the caption+combo block); columns 1-3 stack each tier's caption
        // ("Low/Medium/High effort") directly above its combo. Equal Percent tier columns fit the width with no
        // clipping; each caption centers over its combo.
        private TableLayoutPanel BuildEffortGrid()
        {
            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.ColumnCount = 4;
            grid.RowCount = 2;
            grid.Margin = new Padding(0, 1, 0, 1);
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76F));   // row label ("Models")
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F)); // captions (fixed so the
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // Dock=Fill labels don't collapse)

            // Row label spanning both rows -> vertically centered across the caption+combo block.
            // A KryptonLabel themes itself from the palette exactly like the designer labels,
            // so it stays consistent with the surrounding text in both light and dark mode.
            KryptonLabel lbl = new KryptonLabel();
            lbl.Text = "Models";
            lbl.AutoSize = false;
            lbl.Dock = DockStyle.Fill;
            lbl.Margin = new Padding(3, 0, 6, 0);
            lbl.StateCommon.ShortText.TextH = PaletteRelativeAlign.Near;
            lbl.StateCommon.ShortText.TextV = PaletteRelativeAlign.Center;
            grid.Controls.Add(lbl, 0, 0);
            grid.SetRowSpan(lbl, 2);
            _mcpTip.SetToolTip(lbl, "Pick the model used for each agent effort tier. An agent (or "
                + "dispatch_agent) can ask for low/medium/high without naming a model.");

            this.cmbEffortLow = MakeEffortCombo();
            this.cmbEffortMedium = MakeEffortCombo();
            this.cmbEffortHigh = MakeEffortCombo();

            grid.Controls.Add(MakeEffortCaption("Low effort"), 1, 0);
            grid.Controls.Add(MakeEffortCaption("Medium effort"), 2, 0);
            grid.Controls.Add(MakeEffortCaption("High effort"), 3, 0);
            grid.Controls.Add(this.cmbEffortLow, 1, 1);
            grid.Controls.Add(this.cmbEffortMedium, 2, 1);
            grid.Controls.Add(this.cmbEffortHigh, 3, 1);
            return grid;
        }

        // A tier caption that centers over its combo: Dock=Fill + centered text, with the same right margin
        // the combo uses so the two line up.
        private static KryptonLabel MakeEffortCaption(string text)
        {
            KryptonLabel c = new KryptonLabel();
            c.Text = text;
            c.AutoSize = false;
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(0, 0, 6, 0);
            c.StateCommon.ShortText.TextH = PaletteRelativeAlign.Center;
            c.StateCommon.ShortText.TextV = PaletteRelativeAlign.Center;
            return c;
        }

        private ComboBox MakeEffortCombo()
        {
            ComboBox c = new ComboBox();
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(0, 2, 6, 2);
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.DrawMode = DrawMode.OwnerDrawFixed;   // show just the model name; the item value stays the full id
            // Left at stock (white) colors deliberately, even in dark mode.
            c.DrawItem += EffortCombo_DrawItem;
            c.DropDown += EffortCombo_DropDown;
            return c;
        }

        // Owner-draw via the shared helper so the effort pickers render the short model name exactly like
        // the main window's model selector.
        private void EffortCombo_DrawItem(object sender, DrawItemEventArgs e)
        {
            MainForm.DrawModelComboItem(e, sender as ComboBox);
        }

        // The box is narrow, so widen the dropdown to fit the longest (short) model name when it opens.
        private void EffortCombo_DropDown(object sender, EventArgs e)
        {
            ComboBox combo = sender as ComboBox;
            if (combo == null) return;
            int w = combo.Width;
            try
            {
                using (Graphics g = combo.CreateGraphics())
                {
                    foreach (object item in combo.Items)
                    {
                        string s = MainForm.ShortModelName(Convert.ToString(item));
                        int tw = TextRenderer.MeasureText(g, s, combo.Font).Width + 24;
                        if (tw > w) w = tw;
                    }
                }
                combo.DropDownWidth = Math.Min(w, 600);
            }
            catch { }
        }

        // Repopulate one effort combo from the model list, preserving (or seeding) its selection. A stored
        // value not in the list is still added so it stays visible/selectable (mirrors cmbDefaultModel).
        private void SyncEffortCombo(ComboBox combo, IList<string> models, string selected)
        {
            if (combo == null) return;
            combo.BeginUpdate();
            try
            {
                combo.Items.Clear();
                if (models != null)
                    foreach (var m in models) combo.Items.Add(m);
                string sel = selected ?? string.Empty;
                if (sel.Length > 0 && !ContainsOrdinalIgnoreCase(models, sel))
                    combo.Items.Add(sel);
                combo.SelectedItem = sel;
            }
            finally { combo.EndUpdate(); }
        }

        private static bool ContainsOrdinalIgnoreCase(IList<string> list, string value)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // The selected model id for an effort combo, or the fallback (the existing working value) when the
        // combo is empty - so capturing settings never silently clears a tier.
        private static string EffortComboValue(ComboBox combo, string fallback)
        {
            if (combo == null) return fallback ?? string.Empty;
            string sel = combo.SelectedItem as string;
            if (string.IsNullOrEmpty(sel)) sel = combo.Text;
            return string.IsNullOrEmpty(sel) ? (fallback ?? string.Empty) : sel;
        }

        private void TxtModels_TextChanged(object sender, EventArgs e)
        {
            if (_isSyncing) return;

            var lines = this.txtModels.Lines;
            // Build unique, trimmed list of models from textbox
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var models = new List<string>();
            if (lines != null)
            {
                foreach (var line in lines)
                {
                    if (line == null) continue;
                    var t = line.Trim();
                    if (t.Length == 0) continue; // ignore empty lines
                    if (unique.Add(t)) models.Add(t);
                }
            }

            // Preserve existing selection if possible
            var prevSelected = this.cmbDefaultModel.SelectedItem as string;

            this.cmbDefaultModel.BeginUpdate();
            try
            {
                this.cmbDefaultModel.Items.Clear();
                foreach (var m in models) this.cmbDefaultModel.Items.Add(m);

                if (!string.IsNullOrEmpty(prevSelected))
                {
                    // Restore selection if it still exists
                    foreach (object item in this.cmbDefaultModel.Items)
                    {
                        var s = item as string;
                        if (s != null && string.Equals(s, prevSelected, StringComparison.OrdinalIgnoreCase))
                        {
                            this.cmbDefaultModel.SelectedItem = s;
                            break;
                        }
                    }
                }
            }
            finally
            {
                this.cmbDefaultModel.EndUpdate();
            }

            // Keep the effort-tier pickers in step with the model list too, preserving each selection.
            SyncEffortCombo(this.cmbEffortLow, models, this.cmbEffortLow != null ? this.cmbEffortLow.SelectedItem as string : null);
            SyncEffortCombo(this.cmbEffortMedium, models, this.cmbEffortMedium != null ? this.cmbEffortMedium.SelectedItem as string : null);
            SyncEffortCombo(this.cmbEffortHigh, models, this.cmbEffortHigh != null ? this.cmbEffortHigh.SelectedItem as string : null);

            UpdateRecommendedButtonStates();
        }

        // Ensure working copy is current before saving
        private bool SyncWorkingSettingsFromActiveTab(bool showErrors)
        {
            bool jsonActive = this.tabControl1.SelectedPage == this.tabJson;
            if (jsonActive)
            {
                SettingsData parsed;
                string error;
                if (!TryParseJsonEditorToSettings(out parsed, out error))
                {
                    if (showErrors)
                    {
                        var choice = ShowJsonInvalidPrompt(this,
                            "Cannot Save",
                            "The JSON is invalid and cannot be saved. Reload the last saved settings, or continue editing?",
                            error);
                        if (choice == JsonPromptChoice.Reload)
                        {
                            try
                            {
                                var raw = File.ReadAllText(_settingsFile, Encoding.UTF8);
                                SettingsData loaded;
                                if (TryDeserialize(raw, out loaded))
                                {
                                    _working = loaded;
                                    _isSyncing = true;
                                    try
                                    {
                                        // Refresh both views to the last saved state
                                        ApplySettingsToVisualControls(_working);
                                        UpdateJsonEditorFromSettings(_working);
                                    }
                                    finally { _isSyncing = false; }
                                    // Keep user on JSON tab to continue editing
                                    this.tabControl1.SelectedPage = this.tabJson;
                                }
                                else
                                {
                                    MessageBox.Show(this, "Could not reload the last saved settings because the file is invalid.",
                                        "Reload Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                }
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show(this, "Failed to reload settings: " + ex.Message, "Reload Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        }
                    }
                    // Whether Edit or Reload, do not proceed with save now
                    return false;
                }
                _working = parsed;
            }
            else
            {
                CaptureVisualControlsToSettings(_working);
            }
            return true;
        }

        // --- JSON invalid prompt (Reload/Edit) ---
        private enum JsonPromptChoice { Reload, Edit }

        private static JsonPromptChoice ShowJsonInvalidPrompt(IWin32Window owner, string title, string message, string details)
        {
            using (var dlg = new Form())
            using (var lbl = new Label())
            using (var tb = new TextBox())
            using (var btnReload = new Button())
            using (var btnEdit = new Button())
            {
                dlg.Text = title;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ClientSize = new Size(600, 320);

                lbl.AutoSize = false;
                lbl.Text = message;
                lbl.SetBounds(12, 12, 576, 40);

                tb.Multiline = true;
                tb.ReadOnly = true;
                tb.ScrollBars = ScrollBars.Vertical;
                tb.SetBounds(12, 60, 576, 200);
                tb.Text = details ?? string.Empty;

                btnReload.Text = "Reload";
                btnReload.SetBounds(412, 270, 80, 28);
                btnReload.DialogResult = DialogResult.Yes;

                btnEdit.Text = "Edit";
                btnEdit.SetBounds(508, 270, 80, 28);
                btnEdit.DialogResult = DialogResult.No;

                // Default is Edit
                dlg.AcceptButton = btnEdit;

                dlg.Controls.AddRange(new Control[] { lbl, tb, btnReload, btnEdit });
                dlg.CancelButton = btnEdit; // ESC = Edit

                var dr = dlg.ShowDialog(owner);
                return dr == DialogResult.Yes ? JsonPromptChoice.Reload : JsonPromptChoice.Edit;
            }
        }

        // --- MCP settings tab (controls live in the Designer) ---

        // Empty custom-servers template shown when mcp.json doesn't exist yet. GitHub is configured
        // via its own toggle + PAT field (settings.json), not here.
        private const string McpJsonTemplate = "{\r\n  \"mcp_servers\": {\r\n  }\r\n}\r\n";

        private void ApplyMcpToControls(SettingsData s)
        {
            if (s == null) return;
            this.chkMcpWeb.Checked = s.mcp_web_enabled;
            this.chkMcpFiles.Checked = s.mcp_files_enabled;
            // Git/MSBuild read straight from the typed model now that the file is always seeded complete
            // (so an absent key can't read as false); still force-OFF below when the tool isn't installed.
            this.chkMcpGit.Checked = GitProbe.IsInstalled() && s.mcp_git_enabled;
            this.chkMcpCommand.Checked = s.mcp_command_enabled;
            this.chkMcpCommandScratch.Checked = s.mcp_command_scratch_enabled;
            this.chkMcpMsBuild.Checked = MsBuildProbe.IsInstalled() && s.mcp_msbuild_enabled;
            this.chkMcpGithub.Checked = s.mcp_github_enabled;
            this.txtWebSearchKey.Text = s.mcp_websearch_key != null ? s.mcp_websearch_key : string.Empty;
            this.txtGithubPat.Text = s.mcp_github_pat != null ? s.mcp_github_pat : string.Empty;
            // Sub-agents now lives in the typed model (agents_enabled) like every other toggle.
            this.chkAgents.Checked = s.agents_enabled;
            UpdateMcpEnableStates();
        }

        private void CaptureMcpControlsToWorking(SettingsData target)
        {
            if (target == null) return;
            target.mcp_web_enabled = this.chkMcpWeb.Checked;
            target.mcp_files_enabled = this.chkMcpFiles.Checked;
            target.mcp_git_enabled = this.chkMcpGit.Checked;
            target.mcp_command_enabled = this.chkMcpCommand.Checked;
            // Persist the scratch choice as-is, independent of the command server toggle, so disabling
            // (then re-enabling) the command server doesn't wipe it. The runtime gates scratch behavior
            // on the command server being on (ScratchWorkspace.IsEnabled), so this can't take effect alone.
            target.mcp_command_scratch_enabled = this.chkMcpCommandScratch.Checked;
            target.mcp_msbuild_enabled = this.chkMcpMsBuild.Checked;
            target.mcp_github_enabled = this.chkMcpGithub.Checked;
            target.mcp_websearch_key = this.txtWebSearchKey.Text != null ? this.txtWebSearchKey.Text.Trim() : string.Empty;
            target.mcp_github_pat = this.txtGithubPat.Text != null ? this.txtGithubPat.Text.Trim() : string.Empty;
            // Sub-agents is a typed key now; Save writes the checkbox state through SettingsData.
            target.agents_enabled = this.chkAgents.Checked;
        }

        // A web-search / GitHub toggle is only enableable when its key/PAT looks plausibly valid;
        // an empty or malformed field disables (and clears) the toggle so it can't be saved on.
        private void UpdateMcpEnableStates()
        {
            bool webOk = LooksLikeTavilyKey(this.txtWebSearchKey.Text);
            this.chkMcpWeb.Enabled = webOk;
            if (!webOk) this.chkMcpWeb.Checked = false;

            bool patOk = McpConfig.IsValidGitHubPat(this.txtGithubPat.Text != null ? this.txtGithubPat.Text.Trim() : null);
            this.chkMcpGithub.Enabled = patOk;
            if (!patOk) this.chkMcpGithub.Checked = false;

            // Git requires git on the system; if it isn't found, the toggle can't be enabled.
            bool gitInstalled = GitProbe.IsInstalled();
            this.chkMcpGit.Enabled = gitInstalled;
            if (!gitInstalled) this.chkMcpGit.Checked = false;
            try
            {
                this._mcpTip.SetToolTip(this.chkMcpGit, gitInstalled
                    ? string.Empty
                    : "Git was not found on your PATH. Install Git to enable these tools.");
            }
            catch { }

            // MSBuild requires an MSBuild engine on the system; if none is found, the toggle is disabled.
            bool msbuildInstalled = MsBuildProbe.IsInstalled();
            this.chkMcpMsBuild.Enabled = msbuildInstalled;
            if (!msbuildInstalled) this.chkMcpMsBuild.Checked = false;
            try
            {
                this._mcpTip.SetToolTip(this.chkMcpMsBuild, msbuildInstalled
                    ? string.Empty
                    : "No MSBuild was found on this system. Install the .NET Framework or Visual Studio/Build Tools to enable these tools.");
            }
            catch { }

            // The scratch-dir option only applies to the command server, so the control is enableable
            // only while the command server itself is on. Its checked state is left untouched when the
            // command server is off (the value is preserved, just greyed out), so re-enabling the command
            // server brings back the user's prior choice. The runtime (ScratchWorkspace.IsEnabled) gates
            // on the command server too, so a preserved-on scratch setting can't take effect on its own.
            this.chkMcpCommandScratch.Enabled = this.chkMcpCommand.Checked;
        }

        // Tavily keys look like "tvly-dev-XXXXXXXX" (or "tvly-XXXXXXXX"). Lenient prefix + length check.
        private static bool LooksLikeTavilyKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            key = key.Trim();
            return key.StartsWith("tvly-", StringComparison.OrdinalIgnoreCase) && key.Length >= 12;
        }

        private void LoadMcpJsonEditor()
        {
            string text = null;
            try { if (File.Exists(_mcpFile)) text = File.ReadAllText(_mcpFile, Encoding.UTF8); }
            catch { }
            if (string.IsNullOrEmpty(text)) text = McpJsonTemplate;
            this.rtbMcpJson.Text = text;
        }

        // Validate the mcp.json editor. Empty -> seed the default (GitHub placeholder). Invalid JSON
        // blocks the save and switches to the MCP tab.
        private bool TryValidateMcpJson(out string text)
        {
            text = this.rtbMcpJson.Text;
            string trimmed = text != null ? text.Trim() : string.Empty;
            if (trimmed.Length == 0) { text = McpJsonTemplate; return true; }
            try
            {
                JObject.Parse(trimmed);
                text = trimmed;
                return true;
            }
            catch (Exception ex)
            {
                try { if (tabMcp != null) this.tabControl1.SelectedPage = tabMcp; }
                catch { }
                MessageBox.Show(this, "mcp.json is not valid JSON:\r\n\r\n" + ex.Message,
                    "Invalid mcp.json", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private void WriteMcpJson(string text)
        {
            try { FileSafe.WriteAllTextAtomic(_mcpFile, text != null ? text : string.Empty, new UTF8Encoding(false)); }
            catch (Exception ex)
            {
                try { Logger.Log("mcp", "Failed to write mcp.json: " + ex.Message); }
                catch { }
            }
        }

        // Settings schema
        private sealed class SettingsData
        {
            public string openrouter_api_key { get; set; }
            public List<string> models { get; set; }
            public string default_model { get; set; }
            // Agent effort tiers: each maps a capability level the model/agent can request to a model id.
            // Round-tripped through the form (and the raw JSON editor) like any other setting.
            public string model_effort_low { get; set; }
            public string model_effort_medium { get; set; }
            public string model_effort_high { get; set; }
            // Fingerprint (ModelDefaults.RecommendedHash) of the recommended catalog the user has
            // acknowledged. Carried through here so saving settings doesn't drop the key the
            // "updated recommended models" banner relies on. Not surfaced in the visual editor.
            public string recommended_hash_seen { get; set; }
            public bool enable_logging { get; set; }
            // Show/hide the bottom status bar (toggled from the main window; modeled here so saving the
            // settings form preserves it instead of dropping it).
            public bool statusbar_visible { get; set; }
            // Global sub-agents feature default (also written by /toggle-agents). Modeled here so it
            // round-trips through the form like any other toggle.
            public bool agents_enabled { get; set; }
            public double font_size { get; set; }
            public string theme { get; set; }
            public string color_theme { get; set; }
            public int transcript_max_width { get; set; }
            // Store percent (50-100) using legacy key name
            public int message_max_width { get; set; }
            // Legacy data-collection pref, retained only so existing files can migrate to provider_zdr.
            public bool provider_data_collection { get; set; }
            // Global zero-data-retention default for new conversations. Nullable so an absent value in
            // an older settings file can be detected and migrated from provider_data_collection.
            public bool? provider_zdr { get; set; }

            // MCP built-in server toggles + credentials (read by the host via AppSettings).
            public bool mcp_web_enabled { get; set; }
            public bool mcp_files_enabled { get; set; }
            public bool mcp_git_enabled { get; set; }
            public bool mcp_command_enabled { get; set; }
            // Opt-in: run the command server in a per-conversation scratch dir when no workspace is set.
            public bool mcp_command_scratch_enabled { get; set; }
            public bool mcp_msbuild_enabled { get; set; }
            public bool mcp_github_enabled { get; set; }
            public bool mcp_memory_enabled { get; set; }
            public int mcp_memory_max_lines { get; set; }
            public string mcp_websearch_key { get; set; }
            public string mcp_github_pat { get; set; }
        }
    }
}
