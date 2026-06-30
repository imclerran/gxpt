using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using GxPT;
using Krypton.Toolkit;
using System.IO;
using Ionic.Zip;
using System.Reflection;
using iTextSharp.text.pdf;
using Parser = iTextSharp.text.pdf.parser;

namespace GxPT
{
    public partial class MainForm : KryptonForm
    {
        private OpenRouterClient _client;
        private McpHost _mcpHost;
        private McpToolRegistry _mcpRegistry;
        // Last enablement applied to the Skills MCP server spec (it follows skill enablement); used to
        // rebuild only when the on/off boundary is crossed.
        private bool _skillsServerEnabled;
        // Slash-command subsystem (built lazily on first send/keystroke; see EnsureSlashCommands).
        private SlashCommandRegistry _slashRegistry;
        private SlashCommandProcessor _slashProcessor;
        private ISlashCommandContext _slashContext;
        // Remembered tool approvals are shared across all tabs for the app session (in-memory). The
        // approval *prompt* is per-tab (each conversation has its own ToolApprovalPanel), but the
        // remembered choices live in this one store so "remember" applies everywhere.
        private IApprovalStore _approvalStore;
        // True only while restoring the previous session's tabs at startup. During restore, opening a
        // conversation marks its transcript for lazy build instead of building it immediately, so only
        // the visible tab is rendered up front (the rest hydrate when first selected).
        private bool _restoringTabs;
        private const string HelpApiKeysId = "help:api_keys";
        private const string HelpPrivacyId = "help:privacy";

        // Manager classes for UI concerns
        private SidebarManager _sidebarManager;
        private TabManager _tabManager;
        private ThemeManager _themeManager;
        private InputManager _inputManager;
        private AttachmentService _attachmentService;

        private bool _syncingModelCombo; // avoid event feedback loops when syncing combo text

        // Slim cream notice at the top of the chat area, shown when the shipped recommended model list
        // has changed since the user last acknowledged it. Built programmatically (see
        // InitModelUpdateBanner); persists until the user reviews settings or dismisses it.
        private System.Windows.Forms.Panel _modelUpdateBanner;

        // Helper DTO for background-parsed messages
        private sealed class ParsedMessage
        {
            public MessageRole Role;
            public string Text;
            public List<Block> Blocks;
            public List<AttachedFile> Attachments; // optional
            public bool Zdr; // tiny zero-retention corner tag (user/assistant only)
        }

        public MainForm()
        {
            InitializeComponent();
            InitializeManagers();
            HookEvents();
            InitializeDragAndDrop();
            InitializeClient();
            BuildPluginsMenu();

            // Setup initial tab context for the designer-created tab
            SetupInitialConversationTab();
            _inputManager.SetHintText();
            InitUsageStatusBar();

            // Daily, non-blocking refresh of the model context-size catalog (the status bar's
            // context meter divides by it). The updated event lands on the fetch worker thread;
            // repaint the strip so meters appear as soon as sizes are known - including the
            // first-launch case where the strip starts meterless.
            ModelCatalogService.CatalogUpdated += ModelCatalog_Updated;
            try { ModelCatalogService.RefreshIfDue(); }
            catch { }

            // Wire banner link and ensure it lays out nicely
            if (this.lnkOpenSettings != null)
                this.lnkOpenSettings.LinkClicked += lnkOpenSettings_LinkClicked;
            if (this.pnlApiKeyBanner != null)
                this.pnlApiKeyBanner.Resize += (s, e) => LayoutApiKeyBanner();

            // Build the "updated recommended models" banner (hidden until Load decides whether to show it).
            InitModelUpdateBanner();

            this.Load += (s, e) =>
            {
                UpdateApiKeyBanner();
                MaybeShowModelUpdateBanner();
                try { RestoreOpenTabsOnStartup(); }
                catch { }
                // The status bar synced in the constructor, before tabs were restored - and no
                // OnTabSelected fires for the tab that is already selected, so without this the
                // first visible tab shows empty usage stats until the user switches away and back.
                try { SyncUsageStatusFromActiveTab(); }
                catch { }
                // Same for the tool/skill counts: the constructor's seed ran before the MCP servers
                // connected (and registry events that fired before the handle existed were dropped),
                // so re-derive them now that the form is live.
                try { SyncGenerationIndicatorFromActiveTab(); }
                catch { }
                // After restoring tabs, if no API key is configured, open the API Keys Help tab and focus it
                try
                {
                    string k = AppSettings.GetString("openrouter_api_key");
                    if (k == null || k.Trim().Length == 0)
                    {
                        miApiKeysHelp_Click(this, EventArgs.Empty);
                    }
                }
                catch { }
            };
            this.FormClosing += MainForm_FormClosing_SaveOpenTabs;

            // Configure attachments banner container
            if (this.pnlAttachmentsBanner != null)
            {
                this.pnlAttachmentsBanner.AutoSize = false; // we'll manage height so it can grow with wrapping
                this.pnlAttachmentsBanner.Dock = DockStyle.Bottom;
                this.pnlAttachmentsBanner.Padding = new Padding(6, 3, 6, 3);
                this.pnlAttachmentsBanner.WrapContents = true;
                this.pnlAttachmentsBanner.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                // Fixed darker color for contrast with input panel
                this.pnlAttachmentsBanner.BackColor = Color.DarkGray;
                // Recompute on container resize to grow for wrapped chips
                if (this.pnlBottom != null)
                    this.pnlBottom.SizeChanged += (s, e) => RebuildAttachmentsBanner();
            }

            // Kick off background prewarm of syntax highlighters to avoid UI stalls the first time
            // a code block is measured/drawn. Once warmed, mark ready and request a trivial reflow.
            try
            {
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        // Touch the SyntaxHighlighter static state
                        var langs = SyntaxHighlighter.GetSupportedLanguages();
                        if (langs != null && langs.Length > 0)
                        {
                            // Tokenize a tiny sample for a handful of common languages
                            string sample = "int x = 42; // warm\n";
                            for (int i = 0; i < langs.Length; i++)
                            {
                                string lang = langs[i];
                                if (string.IsNullOrEmpty(lang)) continue;
                                try { SyntaxHighlighter.Highlight(lang, sample); }
                                catch { }
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        try { SyntaxHighlightingRenderer.MarkHighlighterReady(); }
                        catch { }
                        // Ask active transcript to reflow once, now that highlighters are ready
                        try
                        {
                            if (IsHandleCreated)
                            {
                                BeginInvoke((MethodInvoker)delegate
                                {
                                    var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
                                    if (ctx != null && ctx.Transcript != null)
                                    {
                                        ctx.Transcript.RefreshTheme(); // triggers a reflow
                                    }
                                });
                            }
                        }
                        catch { }
                    }
                });
            }
            catch { }
        }

        // ===== Drag & Drop Attachments =====
        private void InitializeDragAndDrop()
        {
            try
            {
                // Enable on the form
                this.AllowDrop = true;
                this.DragEnter -= MainForm_DragEnter;
                this.DragEnter += MainForm_DragEnter;
                this.DragDrop -= MainForm_DragDrop;
                this.DragDrop += MainForm_DragDrop;

                // Also enable on key child controls so drops over them are handled too
                EnableDropOnControl(this.tabControl1);
                EnableDropOnControl(this.chatTranscript);
                EnableDropOnControl(this.splitContainer1);
                EnableDropOnControl(this.pnlBottom);
            }
            catch { }
        }

        private void EnableDropOnControl(Control c)
        {
            if (c == null) return;
            try
            {
                c.AllowDrop = true;
                c.DragEnter -= MainForm_DragEnter;
                c.DragEnter += MainForm_DragEnter;
                c.DragDrop -= MainForm_DragDrop;
                c.DragDrop += MainForm_DragDrop;
            }
            catch { }
        }

        private void MainForm_DragEnter(object sender, DragEventArgs e)
        {
            try
            {
                if (e != null && e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > 0)
                    {
                        EnsureAttachmentService();
                        // Hard gate: a non-vision model rejects image files (IsSupported skips them).
                        if (_attachmentService != null)
                            _attachmentService.ImageAttachmentsEnabled = CurrentModelSupportsImages();
                        for (int i = 0; i < files.Length; i++)
                        {
                            var f = files[i];
                            if (_attachmentService != null && _attachmentService.IsSupported(f))
                            { e.Effect = DragDropEffects.Copy; return; }
                        }
                    }
                }
            }
            catch { }
            e.Effect = DragDropEffects.None;
        }

        private void MainForm_DragDrop(object sender, DragEventArgs e)
        {
            try
            {
                if (e == null || e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0) return;
                HandleDroppedFiles(files);
            }
            catch { }
        }

        private void HandleDroppedFiles(IEnumerable<string> paths)
        {
            var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (ctx == null) return;
            EnsureAttachmentService();
            // Hard gate for this drop, based on the currently selected model.
            bool imagesAllowed = CurrentModelSupportsImages();
            if (_attachmentService != null)
                _attachmentService.ImageAttachmentsEnabled = imagesAllowed;

            // When images are gated off, detect dropped image files so we can explain the block
            // with a clear reason rather than the generic "unsupported item" message.
            bool droppedGatedImage = false;
            if (!imagesAllowed && paths != null)
            {
                foreach (var p in paths)
                    if (ImageAttachmentExtractor.IsImageFile(p)) { droppedGatedImage = true; break; }
            }

            List<SkippedAttachment> skipped = new List<SkippedAttachment>();
            var extracted = (_attachmentService != null) ? _attachmentService.ExtractMany(paths, out skipped) : new List<AttachedFile>();
            if (extracted != null && extracted.Count > 0)
            {
                HandleScannedPdfs(extracted);
                if (ctx.PendingAttachments == null) ctx.PendingAttachments = new List<AttachedFile>();
                ctx.PendingAttachments.AddRange(extracted);
            }

            RebuildAttachmentsBanner();

            if (droppedGatedImage)
            {
                try
                {
                    MessageBox.Show(this,
                        "Images can only be attached to a vision-capable model. The selected model "
                        + "cannot view images - switch to a model that supports image input, then attach again.",
                        "Image Attachments Unavailable",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                catch { }
                // Drop image entries from the generic skipped list so they aren't reported twice.
                if (skipped != null && skipped.Count > 0)
                {
                    var filtered = new List<SkippedAttachment>();
                    foreach (var s in skipped)
                        if (s != null && !ImageAttachmentExtractor.IsImageFile(s.DisplayName)) filtered.Add(s);
                    skipped = filtered;
                }
            }

            ShowSkippedAttachmentsMessage(skipped);
        }

        // Report files that couldn't be attached, including each extractor's reason when it has
        // one (e.g. the image size-cap message) instead of a bare "unsupported item".
        private void ShowSkippedAttachmentsMessage(List<SkippedAttachment> skipped)
        {
            if (skipped == null || skipped.Count == 0) return;
            try
            {
                var sb = new StringBuilder();
                sb.Append("Some files could not be attached:");
                for (int i = 0; i < skipped.Count; i++)
                {
                    var s = skipped[i];
                    if (s == null) continue;
                    sb.Append("\n - ").Append(s.DisplayName ?? string.Empty);
                    if (!string.IsNullOrEmpty(s.Reason)) sb.Append(": ").Append(s.Reason);
                }
                MessageBox.Show(this, sb.ToString(), "Attach Files",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch { }
        }

        // Persist and restore open tabs (conversation IDs and active tab)
        private void MainForm_FormClosing_SaveOpenTabs(object sender, FormClosingEventArgs e)
        {
            try
            {
                var openIds = GetOpenConversationIdsInOrder();
                string activeId = GetActiveConversationId();
                SessionState.SaveOpenTabs(openIds, activeId);
            }
            catch { }

            // Shut down MCP servers (close stdio children / HTTP sessions).
            try { if (_mcpHost != null) _mcpHost.Dispose(); }
            catch { }

            // Remove every per-conversation scratch directory this process created. Done AFTER the host
            // is disposed so the command children (which had a scratch dir as their CWD) have exited and
            // no longer hold it open.
            try { ScratchWorkspace.DeleteAllCreated(); }
            catch { }
        }

        private List<string> GetOpenConversationIdsInOrder()
        {
            var list = new List<string>();
            try
            {
                if (this.tabControl1 == null) return list;
                foreach (TabPage page in this.tabControl1.TabPages)
                {
                    try
                    {
                        var ctx = _tabManager != null && page != null && _tabManager.TabContexts.ContainsKey(page)
                            ? _tabManager.TabContexts[page]
                            : null;
                        var id = ctx != null && ctx.Conversation != null ? ctx.Conversation.Id : null;
                        if (string.IsNullOrEmpty(id)) continue;
                        // Include help tabs (by known help ids) even if marked NoSaveUntilUserSend
                        if (ctx != null && ctx.NoSaveUntilUserSend && !IsHelpConversationId(id)) continue;
                        list.Add(id);
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        private string GetActiveConversationId()
        {
            try
            {
                var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
                if (ctx != null && ctx.Conversation != null) return ctx.Conversation.Id;
            }
            catch { }
            return null;
        }

        private void RestoreOpenTabsOnStartup()
        {
            try
            {
                List<string> ids; string activeFromFile;
                SessionState.LoadOpenTabs(out ids, out activeFromFile);
                if (ids == null || ids.Count == 0) return;

                // Defer transcript builds while every tab is opened, so reopening a session with many
                // tabs doesn't render them all up front. Only the tab left visible is built below; the
                // rest hydrate the first time they're selected (OnTabSelected -> HydrateTabIfNeeded).
                _restoringTabs = true;
                try
                {
                // We'll reuse the initial blank tab for the first item if suitable
                bool firstUsed = false;
                // A conversation must not open into two tabs: duplicates fork the in-memory copy and
                // their saves clobber each other (a prior bug could already have written the same id
                // twice into session.json), so skip ids we've already opened this restore.
                var seenIds = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < ids.Count; i++)
                {
                    string id = ids[i];
                    if (string.IsNullOrEmpty(id)) continue;
                    if (!seenIds.Add(id)) continue;
                    try
                    {
                        if (IsHelpConversationId(id))
                        {
                            // Reopen help tab from embedded resource by id
                            OpenHelpConversationById(id, ref firstUsed);
                        }
                        else
                        {
                            string path = ConversationStore.GetPathForId(id);
                            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) continue;
                            var convo = ConversationStore.Load(_client, path);
                            if (convo == null) continue;

                            if (!firstUsed)
                            {
                                var blank = FindBlankTabPreferActive();
                                if (blank != null)
                                {
                                    OpenConversationInTab(blank, convo);
                                    firstUsed = true;
                                    continue;
                                }
                            }
                            OpenConversationInNewTab(convo);
                        }
                    }
                    catch { }
                }

                // Reselect previously active tab if still present
                try
                {
                    string active = activeFromFile;
                    if (!string.IsNullOrEmpty(active) && this.tabControl1 != null && _tabManager != null)
                    {
                        foreach (TabPage p in this.tabControl1.TabPages)
                        {
                            try
                            {
                                var ctx = _tabManager.TabContexts.ContainsKey(p) ? _tabManager.TabContexts[p] : null;
                                if (ctx != null && ctx.Conversation != null && string.Equals(ctx.Conversation.Id, active, StringComparison.Ordinal))
                                {
                                    this.tabControl1.SelectedTab = p;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
                }
                finally { _restoringTabs = false; }

                // Build only the now-visible tab; the rest stay deferred until first selected.
                try { HydrateTabIfNeeded(_tabManager != null ? _tabManager.GetActiveContext() : null); }
                catch { }

                // Pre-warm MCP servers for the visible tab only (one reconcile, off the UI thread).
                // Other tabs' servers launch lazily when they're sent to or first selected.
                try { SyncMcpWorkingDirFromActiveTab(); }
                catch { }
            }
            catch { }
        }

        // Build a tab's transcript if it was deferred at session restore. Idempotent and cheap once
        // hydrated (the flag is cleared before the rebuild). Called for the visible tab after restore
        // and for each tab the first time it becomes active.
        private void HydrateTabIfNeeded(TabManager.ChatTabContext ctx)
        {
            if (ctx == null || !ctx.NeedsTranscriptRebuild) return;
            ctx.NeedsTranscriptRebuild = false;
            if (ctx.Conversation != null) RebuildTranscriptAsync(ctx, ctx.Conversation);
        }

        private bool IsHelpConversationId(string id)
        {
            try
            {
                if (string.IsNullOrEmpty(id)) return false;
                if (string.Equals(id, HelpApiKeysId, StringComparison.Ordinal)) return true;
                if (string.Equals(id, HelpPrivacyId, StringComparison.Ordinal)) return true;
                return id.StartsWith("help_", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private void OpenHelpConversationById(string id, ref bool firstUsed)
        {
            try
            {
                Conversation convo = LoadHelpConversationById(id);
                if (convo == null) return;
                // Keep specialized help id to restore by id later
                // Mark as help/no-save until user sends
                var ctx = !firstUsed ? FindBlankTabPreferActive() : null;
                if (!firstUsed && ctx != null)
                {
                    ctx.NoSaveUntilUserSend = true;
                    OpenConversationInTab(ctx, convo);
                    try { if (ctx.Transcript != null) ctx.Transcript.ScrollToTop(); }
                    catch { }
                    firstUsed = true;
                }
                else
                {
                    // New tab
                    var ctx2 = _tabManager != null ? _tabManager.CreateConversationTab() : null;
                    if (ctx2 == null) return;
                    ctx2.NoSaveUntilUserSend = true;
                    OpenConversationInTab(ctx2, convo);
                    try { if (ctx2.Transcript != null) ctx2.Transcript.ScrollToTop(); }
                    catch { }
                }
            }
            catch { }
        }

        private Conversation LoadHelpConversationById(string id)
        {
            try
            {
                string resourceName = null;
                if (string.Equals(id, HelpApiKeysId, StringComparison.Ordinal)) resourceName = "GxPT.Resources.Help.help_api_keys.json";
                else if (string.Equals(id, HelpPrivacyId, StringComparison.Ordinal)) resourceName = "GxPT.Resources.Help.help_privacy.json";
                else return null;

                var asm = typeof(MainForm).Assembly;
                using (var s = asm.GetManifestResourceStream(resourceName))
                {
                    if (s == null) return null;
                    using (var sr = new StreamReader(s, Encoding.UTF8, true))
                    {
                        string json = sr.ReadToEnd();
                        var convo = ConversationStore.LoadFromJson(_client, json);
                        // Help conversations start with the workspace strip hidden until the user
                        // explicitly sets a working folder (right-click tab), which re-shows it.
                        if (convo != null) convo.WorkspaceStripDismissed = true;
                        return convo;
                    }
                }
            }
            catch { return null; }
        }

        private void InitializeManagers()
        {
            // Remembered approvals are kept only for the lifetime of the app (in-memory), not
            // persisted to settings.json — a remembered choice lasts the session, then resets. The
            // approval UI itself is per-tab: each conversation gets its own ToolApprovalPanel (see
            // AttachApprovalPanel), so a pending approval appears on the tab that requested it rather
            // than whatever tab happens to be active. The policy is built per turn in BeginToolSend
            // and bound to that tab's panel, all sharing this one store.
            try { _approvalStore = new InMemoryApprovalStore(); }
            catch { }

            // Initialize managers for UI concerns
            _sidebarManager = new SidebarManager(this, this.splitContainer1, this.miConversationHistory);
            _tabManager = new TabManager(this, this.tabControl1, this.msMain);
            _themeManager = new ThemeManager(this, this.chatTranscript, this.txtMessage,
                this.btnSend, this.cmbModel, this.lnkOpenSettings, this.lblNoApiKey);
            _inputManager = new InputManager(this, this.txtMessage, this.pnlInput,
                this.btnSend, this.cmbModel, this.splitContainer1, this.pnlApiKeyBanner, this.pnlAttachmentsBanner);

            // Wire manager events
            if (_tabManager != null)
            {
                _tabManager.TabSelected += OnTabSelected;
                _tabManager.TabsChanged += OnTabsChanged;
            }

            if (_sidebarManager != null)
            {
                _sidebarManager.SidebarToggled += OnSidebarToggled;
            }
        }

        private void HookEvents()
        {
            this.Resize += MainForm_Resize;

            // Wire menu items
            try
            {
                if (this.miNewConversation != null)
                {
                    this.miNewConversation.Click -= miNewConversation_Click;
                    this.miNewConversation.Click += miNewConversation_Click;
                }
            }
            catch { }
            try
            {
                if (this.miCloseConversation != null)
                {
                    this.miCloseConversation.Click -= closeConversationToolStripMenuItem_Click;
                    this.miCloseConversation.Click += closeConversationToolStripMenuItem_Click;
                }
            }
            catch { }
            try
            {
                if (this.miFile != null)
                {
                    this.miFile.DropDownOpening -= miFile_DropDownOpening;
                    this.miFile.DropDownOpening += miFile_DropDownOpening;
                }
            }
            catch { }

            try
            {
                if (this.cmbModel != null)
                {
                    this.cmbModel.SelectedIndexChanged -= cmbModel_SelectedIndexChanged;
                    this.cmbModel.SelectedIndexChanged += cmbModel_SelectedIndexChanged;
                    this.cmbModel.TextUpdate -= cmbModel_TextUpdate;
                    this.cmbModel.TextUpdate += cmbModel_TextUpdate;
                    // Adjust dropdown width dynamically to fit the widest item
                    this.cmbModel.DropDown -= cmbModel_DropDownAdjustWidth;
                    this.cmbModel.DropDown += cmbModel_DropDownAdjustWidth;
                    // Owner-draw shows only the model name (after "author/"); the item value stays the
                    // full "author/model" id, so GetSelectedModel and the request are unchanged.
                    this.cmbModel.DrawItem -= cmbModel_DrawItem;
                    this.cmbModel.DrawItem += cmbModel_DrawItem;
                }
            }
            catch { }

            try
            {
                if (this.chkZdrTab != null)
                {
                    this.chkZdrTab.CheckedChanged -= chkZdrTab_CheckedChanged;
                    this.chkZdrTab.CheckedChanged += chkZdrTab_CheckedChanged;
                }
            }
            catch { }

            try
            {
                if (this.tsiStopGen != null)
                {
                    this.tsiStopGen.Click -= tsiStopGen_Click;
                    this.tsiStopGen.Click += tsiStopGen_Click;
                }
            }
            catch { }

            try
            {
                if (this.btnAttach != null)
                {
                    this.btnAttach.Click -= btnAttach_Click;
                    this.btnAttach.Click += btnAttach_Click;
                }
            }
            catch { }
        }

        // Event handlers for manager events
        private void OnTabSelected(TabPage selectedTab)
        {
            // Build this tab's transcript if it was deferred at session restore (first time it's shown).
            // Skipped while the restore loop is still opening tabs — the visible one is built afterward.
            if (!_restoringTabs && _tabManager != null && selectedTab != null)
            {
                TabManager.ChatTabContext sel;
                if (_tabManager.TabContexts.TryGetValue(selectedTab, out sel)) HydrateTabIfNeeded(sel);
            }
            UpdateWindowTitleFromActiveTab();
            SyncComboModelFromActiveTab();
            SyncZdrCheckboxFromActiveTab();
            // Refresh the attachments banner to reflect the active tab's pending attachments
            RebuildAttachmentsBanner();
            // Point the MCP host's workdir-scoped servers (files/git/command) at the active tab's folder.
            SyncMcpWorkingDirFromActiveTab();
            // Reflect the active conversation's usage/cost totals in the status bar.
            SyncUsageStatusFromActiveTab();
            // And whether the active tab has a request in flight (the generation indicator is
            // form-wide but tracks the active tab only).
            SyncGenerationIndicatorFromActiveTab();
            if (_inputManager != null) _inputManager.FocusInputSoon();
        }

        // The per-conversation scratch directory used to run the command server when this conversation
        // has no workspace and the scratch-command feature is enabled (opt-in); null otherwise (a real
        // workspace takes precedence, or the feature is off). When create is true the directory - and
        // the conversation id it is keyed by - is materialized; when false a missing id/dir yields a
        // path without side effects (for read-only callers like the tab reconcile / status bar).
        private string ScratchDirForContext(TabManager.ChatTabContext ctx, bool create)
        {
            if (ctx == null || ctx.Conversation == null) return null;
            if (!string.IsNullOrEmpty(ctx.WorkingDir)) return null;     // a real workspace wins
            if (!ScratchWorkspace.IsEnabled()) return null;
            string id = ctx.Conversation.Id;
            if (string.IsNullOrEmpty(id))
            {
                if (!create) return null;
                id = ConversationStore.EnsureConversationId(ctx.Conversation);
            }
            return create ? ScratchWorkspace.EnsureDir(id) : ScratchWorkspace.PathFor(id);
        }

        // The working directory used to RESOLVE workdir-scoped MCP tools for a tab (status bar count,
        // turn routing): the user workspace if set, else this conversation's already-created scratch dir
        // (command server only). Null when neither applies. Read-only - never creates a dir or an id, so
        // the scratch path counts only once a turn has actually materialized it.
        private string ResolutionWorkdirForContext(TabManager.ChatTabContext ctx)
        {
            if (ctx == null) return null;
            if (!string.IsNullOrEmpty(ctx.WorkingDir)) return ctx.WorkingDir;
            string s = ScratchDirForContext(ctx, false);
            return (!string.IsNullOrEmpty(s) && System.IO.Directory.Exists(s)) ? s : null;
        }

        // Release and delete a conversation's scratch working directory (if any). Stops the scratch
        // command server bound to it first so the directory isn't held open, then removes it. Safe for
        // conversations that never used a scratch dir. Used when a conversation is deleted.
        internal void DeleteScratchForConversation(string conversationId)
        {
            if (string.IsNullOrEmpty(conversationId)) return;
            try
            {
                string path = ScratchWorkspace.PathFor(conversationId);
                if (string.IsNullOrEmpty(path)) return;
                if (_mcpHost != null) _mcpHost.ReleaseWorkingDir(path); // tear down the command server holding it
                ScratchWorkspace.DeletePath(path);
            }
            catch { }
        }

        // Reconcile the MCP host's workdir-scoped servers (files/git/command) with the open tabs:
        // pre-warm the active conversation's folder and tear down any folder no longer referenced by an
        // open tab. Each distinct working directory runs its own server set, kept alive across tab
        // switches, so a tab is never disconnected by activity in another tab. Snapshots the tab state
        // on the UI thread, then does the (potentially blocking) connect/teardown off-thread. Called on
        // tab switch, tab open/close, and working-folder changes.
        private void SyncMcpWorkingDirFromActiveTab()
        {
            if (_mcpHost == null) return;
            // Don't launch MCP servers while restoring a session: opening each tab would otherwise
            // spin up a files/git/command set per distinct folder, and those process spawns/handshakes
            // saturate the thread pool and delay the visible tab's transcript from rendering. Servers
            // are launched lazily on first send (BeginToolSend) and pre-warmed once after restore.
            if (_restoringTabs) return;
            // Every caller is a workdir-affecting path (folder set/clear, tab open/close/switch) and
            // the status bar's tool count is workdir-scoped — refresh it alongside the servers.
            UpdateToolSkillCounts();
            string active = null;
            bool activeScratch = false;
            var inUse = new List<string>();
            try
            {
                var act = _tabManager != null ? _tabManager.GetActiveContext() : null;
                if (act != null)
                {
                    if (!string.IsNullOrEmpty(act.WorkingDir)) active = act.WorkingDir;
                    else
                    {
                        // Folderless active tab: pre-warm its scratch command server, but only once a
                        // turn has created the dir (the first send creates + connects it). Connecting a
                        // not-yet-created dir would launch the child with a missing CWD.
                        string s = ResolutionWorkdirForContext(act);
                        if (!string.IsNullOrEmpty(s)) { active = s; activeScratch = true; }
                    }
                }
                if (_tabManager != null)
                {
                    foreach (var c in _tabManager.TabContexts.Values)
                    {
                        if (c == null) continue;
                        if (!string.IsNullOrEmpty(c.WorkingDir)) { inUse.Add(c.WorkingDir); continue; }
                        // Keep a folderless tab's scratch server alive across tab switches by listing its
                        // scratch dir among the retained workdirs.
                        string s = ResolutionWorkdirForContext(c);
                        if (!string.IsNullOrEmpty(s)) inUse.Add(s);
                    }
                }
            }
            catch { }

            McpHost hostRef = _mcpHost;
            string activeRef = active;
            bool activeScratchRef = activeScratch;
            string[] inUseArr = inUse.ToArray();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (!string.IsNullOrEmpty(activeRef))
                    {
                        if (activeScratchRef) hostRef.EnsureScratchDir(activeRef);
                        else hostRef.EnsureWorkingDir(activeRef);
                    }
                    hostRef.RetainOnly(inUseArr); // release folders whose last tab closed
                }
                catch (Exception ex) { try { Logger.Log("mcp", "MCP workdir reconcile failed: " + ex.Message); } catch { } }
            });
        }

        // Creates the per-tab workspace strip docked above this tab's transcript, wiring its
        // Set/Change/Clear actions to that conversation's working folder. Called by TabManager when a
        // tab context is created.
        internal void AttachWorkspaceStrip(TabManager.ChatTabContext ctx)
        {
            if (ctx == null || ctx.Page == null) return;
            try
            {
                // Seed from the (possibly loaded) conversation so a re-opened chat keeps its folder.
                if (string.IsNullOrEmpty(ctx.WorkingDir) && ctx.Conversation != null &&
                    !string.IsNullOrEmpty(ctx.Conversation.WorkingDir))
                    ctx.WorkingDir = ctx.Conversation.WorkingDir;

                var strip = new WorkspaceContextStrip();
                ctx.WorkspaceStrip = strip;
                var ctxRef = ctx;
                strip.ChangeRequested += delegate { SetWorkingFolderForContext(ctxRef); };
                strip.ClearRequested += delegate { ClearWorkingFolderForContext(ctxRef); };
                strip.DismissRequested += delegate { DismissWorkspaceStripForContext(ctxRef); };
                // Dock order: the strip is Top, the approval panel is Bottom, and the transcript is
                // Fill. WinForms lays out docked controls by REVERSE z-order, so the Fill transcript
                // must be the frontmost child for it to fill the area *between* the Top strip and the
                // Bottom approval panel. Add the docked siblings, then send the transcript to front.
                ctx.Page.Controls.Add(strip);
                AttachApprovalPanel(ctx);
                AttachQuestionPanel(ctx);
                AttachAgentActivityPanel(ctx);
                if (ctx.Transcript != null) ctx.Transcript.BringToFront();
                strip.SetWorkingDir(ctx.WorkingDir);
                // Honor a persisted dismissal (only meaningful when no folder is set; setting one
                // re-shows the strip).
                if (string.IsNullOrEmpty(ctx.WorkingDir) && ctx.Conversation != null &&
                    ctx.Conversation.WorkspaceStripDismissed)
                    strip.Visible = false;
            }
            catch { }
        }

        // Create this tab's tool-approval panel (docked at the bottom of its transcript area, hidden
        // until a tool call awaits a decision). Each conversation gets its own panel so an approval is
        // shown on the tab that requested it; it reads file context from this tab's working dir.
        internal void AttachApprovalPanel(TabManager.ChatTabContext ctx)
        {
            if (ctx == null || ctx.Page == null) return;
            try
            {
                var panel = new ToolApprovalPanel();
                ctx.ApprovalPanel = panel;
                var ctxRef = ctx;
                panel.WorkingDirProvider = delegate { return ctxRef.WorkingDir; };
                // While this tab's prompt awaits the user, pause the status-bar marquee and swap the
                // Stop button for an "awaiting user..." label (only when this is the active tab).
                panel.PromptVisibleChanged += delegate { SyncGenerationIndicatorFromActiveTab(); };
                // The approval panel's Explain button opens a fresh tab that describes the pending
                // command, carrying over this conversation's model + ZDR. The approval itself stays up.
                panel.ExplainRequested = delegate(string command, bool isPowerShell)
                {
                    OpenCommandExplainTab(ctxRef, command, isPowerShell);
                };
                ctx.Page.Controls.Add(panel); // self-docks Bottom, starts hidden
            }
            catch { }
        }

        // Create this tab's ask_user question panel (docked at the bottom of its transcript area, hidden
        // until the model asks a question). Per-tab like the approval panel, so a question appears on the
        // conversation that asked it; while it's up, the status bar shows "awaiting user...".
        internal void AttachQuestionPanel(TabManager.ChatTabContext ctx)
        {
            if (ctx == null || ctx.Page == null) return;
            try
            {
                var panel = new QuestionPanel();
                ctx.QuestionPanel = panel;
                panel.PromptVisibleChanged += delegate { SyncGenerationIndicatorFromActiveTab(); };
                ctx.Page.Controls.Add(panel); // self-docks Bottom, starts hidden
            }
            catch { }
        }

        // Open a new chat tab that asks the model to explain a pending command (from the approval
        // panel's Explain button), then send it. The new conversation inherits the source tab's model
        // and ZDR setting so the explanation routes through the same provider/privacy choice. The new
        // tab gets NO working directory, so it normally runs as a plain chat turn; note that if a
        // scratch workspace is globally enabled, StartModelTurn can still route folderless tabs through
        // the tool loop, in which case the explanation could itself call a tool. The source tab's
        // approval prompt is untouched - it stays up for the user to Allow/Deny.
        internal void OpenCommandExplainTab(TabManager.ChatTabContext sourceCtx, string command, bool isPowerShell)
        {
            try
            {
                if (_tabManager == null || string.IsNullOrEmpty(command)) return;

                // The message input is a single shared box; capture the source tab's unsent draft so the
                // overwrite-and-send below doesn't silently discard it (restored at the end).
                string priorDraft = _inputManager != null ? _inputManager.GetInputText() : null;

                // Snapshot the source conversation's model + effective ZDR before we switch tabs.
                string model = sourceCtx != null ? sourceCtx.SelectedModel : null;
                bool zdr = sourceCtx != null && sourceCtx.Conversation != null
                           && (sourceCtx.Conversation.Zdr || sourceCtx.Conversation.ZdrFirstMessageIndex >= 0);

                var ctx = _tabManager.CreateConversationTab(); // creates and selects the new tab
                if (ctx == null) return;

                if (!string.IsNullOrEmpty(model))
                {
                    ctx.SelectedModel = model;
                    if (ctx.Conversation != null) ctx.Conversation.SelectedModel = model;
                }
                if (ctx.Conversation != null) ctx.Conversation.Zdr = zdr;

                // Reflect the carried-over model + ZDR on the toolbar now that this tab is active, so the
                // send below (which reads the combo) uses them.
                SyncComboModelFromActiveTab();
                SyncZdrCheckboxFromActiveTab();

                string prompt = BuildCommandExplainPrompt(command, isPowerShell);
                if (_inputManager != null) _inputManager.SetInputText(prompt, true);
                btnSend_Click(this, EventArgs.Empty);

                // btnSend_Click cleared the shared input on a successful send; put the user's prior draft
                // back so switching to the explain tab didn't cost them their typed-but-unsent message.
                if (!string.IsNullOrEmpty(priorDraft) && _inputManager != null)
                    _inputManager.SetInputText(priorDraft, false);
            }
            catch { }
        }

        // The user prompt seeded into an Explain tab: ask for a plain-language walkthrough of the
        // command, an evaluation of the risks it carries (both to the user's system and from the data
        // its results would expose / send off the machine), and a short summary at the end. Fenced in
        // the matching language so the command renders as a code block.
        private static string BuildCommandExplainPrompt(string command, bool isPowerShell)
        {
            string kind = isPowerShell ? "PowerShell" : "Windows command-line";
            string fence = isPowerShell ? "powershell" : "batch";
            return "Explain, in plain language, what the following " + kind + " command does. "
                + "Do not run it. Cover these in order:\r\n\r\n"
                + "1. **What it does** - walk through the command step by step, including each program, "
                + "flag, argument, pipe, and redirection, and the overall effect.\r\n"
                + "2. **Risks to the system** - evaluate what could go wrong on the machine that runs it: "
                + "data loss or overwrites, irreversible or destructive actions, changes to files or "
                + "system state outside the working directory, elevated-privilege or security-sensitive "
                + "operations, and anything unexpected or obfuscated. Note if it looks safe.\r\n"
                + "3. **Data-sharing / exfiltration risk** - this is about what running the command would "
                + "*expose or send*, not about the command text itself. Two things to evaluate: (a) does "
                + "the command move data off the machine - uploading, posting, or otherwise transmitting "
                + "data to a network or external service? and (b) would its output or results surface "
                + "sensitive data (secrets, credentials, keys, tokens, personal data, private file "
                + "contents)? Such results are returned to the model and could be retained, so flag any "
                + "sensitive data the command would read or emit, and any external destination it would "
                + "send data to.\r\n"
                + "4. **Summary** - finish with a brief one- or two-sentence summary of the command and "
                + "its overall risk level.\r\n\r\n"
                + "```" + fence + "\r\n" + (command ?? string.Empty) + "\r\n```";
        }

        // Re-theme every tab's tool-approval panel after a light<->dark switch: a currently-visible
        // prompt updates immediately, and hidden ones are already correct when next shown (they also
        // re-theme themselves in ShowFor). Mirrors how transcripts are refreshed on a theme change.
        private void ApplyThemeToAllApprovalPanels()
        {
            try
            {
                if (_tabManager == null) return;
                foreach (var kv in _tabManager.TabContexts)
                {
                    var ctx = kv.Value;
                    if (ctx != null && ctx.ApprovalPanel != null)
                        ctx.ApprovalPanel.ApplyTheme();
                    if (ctx != null && ctx.QuestionPanel != null)
                        ctx.QuestionPanel.ApplyTheme();
                }
            }
            catch { }
        }

        // The per-tab sub-agents activity panel (design sec.14), docked at the bottom like the approval
        // panel. Shown only while a dispatch_agent fan-out runs; updated via AgentActivityUiBridge.
        internal void AttachAgentActivityPanel(TabManager.ChatTabContext ctx)
        {
            if (ctx == null || ctx.Page == null) return;
            try
            {
                AgentActivityPanel panel = new AgentActivityPanel();
                ctx.AgentActivityPanel = panel;
                ctx.Page.Controls.Add(panel); // self-docks Bottom, starts hidden
            }
            catch { }
        }

        // Status bar Stop button: cancel a tab's in-flight model request. Killing the curl process
        // drops the connection so the API stops generating; the streaming/orchestrator paths then
        // finalize the turn cleanly (keeping any partial text) and hide the indicator.
        private void CancelActiveRequest(TabManager.ChatTabContext ctx)
        {
            if (ctx == null) return;
            try
            {
                RequestCancellation c = ctx.Cancellation;
                if (c != null)
                {
                    Logger.Log("Send", "User requested stop.");
                    c.Cancel();
                }
            }
            catch { }
        }

        // Show/hide the status bar's generation indicator (marquee progress bar + stop button) for
        // a tab's send. The indicator lives in the form-wide status strip while sends are per-tab,
        // so it reflects the ACTIVE tab only: a background tab's turn must not flip the indicator
        // under the foreground tab. Tab switches restore the right state from ctx.IsSending via
        // SyncGenerationIndicatorFromActiveTab.
        private void ShowGenerationBar(TabManager.ChatTabContext ctx)
        {
            try
            {
                var act = _tabManager != null ? _tabManager.GetActiveContext() : null;
                if (act != null && ReferenceEquals(act, ctx))
                    SyncGenerationIndicatorFromActiveTab();
            }
            catch { }
        }

        private void HideGenerationBar(TabManager.ChatTabContext ctx)
        {
            try
            {
                var act = _tabManager != null ? _tabManager.GetActiveContext() : null;
                if (act != null && ReferenceEquals(act, ctx))
                    SyncGenerationIndicatorFromActiveTab();
            }
            catch { }
        }

        // Called by AgentActivityUiBridge (on the UI thread) when a fan-out starts/ends on a tab, so the
        // status bar's passive "Sub-agents running..." indicator tracks it.
        internal void NotifyAgentFanOutChanged(TabManager.ChatTabContext ctx, bool active)
        {
            if (ctx != null) ctx.AgentsFanOutActive = active;
            SyncGenerationIndicatorFromActiveTab();
        }

        private void SyncGenerationIndicatorFromActiveTab()
        {
            try
            {
                var act = _tabManager != null ? _tabManager.GetActiveContext() : null;
                bool busy = act != null && act.IsSending && !act.SendDetached;
                // The turn is paused at an approval/continuation/question gate when the active tab's
                // prompt is up: pause the marquee and show "awaiting user..." in place of the Stop button.
                bool awaiting = busy
                    && ((act.ApprovalPanel != null && act.ApprovalPanel.IsPromptVisible)
                        || (act.QuestionPanel != null && act.QuestionPanel.IsPromptVisible));
                // A dispatch_agent fan-out is running: keep the marquee going but show a passive
                // "Sub-agents running..." label (cancel them from the panel). Approval takes priority.
                bool agentsRunning = busy && act.AgentsFanOutActive && !awaiting;
                SetGenerationIndicatorVisible(busy, awaiting, agentsRunning);
            }
            catch { }
        }

        private void SetGenerationIndicatorVisible(bool busy, bool awaiting, bool agentsRunning)
        {
            try
            {
                if (this.tspGenProgress != null)
                {
                    // Run the marquee only while shown and not paused at a prompt (no point animating
                    // an invisible or stalled bar). MarqueeAnimationSpeed is the native PBM_SETMARQUEE
                    // interval (ms between updates), which XP/Luna honors literally while Vista+/Aero
                    // drives the animation from the theme engine and effectively ignores it. A short
                    // interval therefore scrolls far too fast on XP; 120ms reads like the Win7 pace
                    // there and leaves the (ignored) Aero animation unchanged. 0 freezes the marquee.
                    this.tspGenProgress.MarqueeAnimationSpeed = (busy && !awaiting) ? 120 : 0;
                    this.tspGenProgress.Visible = busy;
                }
                if (this.tsiStopGen != null)
                {
                    this.tsiStopGen.Awaiting = awaiting;
                    this.tsiStopGen.AgentsRunning = agentsRunning;
                    this.tsiStopGen.Visible = busy;
                }
                // The slot's idle face: the active conversation's tool/skill counts. Refresh before
                // showing so a turn's side effects (a server faulting, a skill the model authored)
                // are reflected the moment the indicator yields the slot back.
                if (!busy) UpdateToolSkillCounts();
                if (this.tslTools != null) this.tslTools.Visible = !busy;
                if (this.tslToolsValue != null) this.tslToolsValue.Visible = !busy;
                if (this.tslSkills != null) this.tslSkills.Visible = !busy;
                if (this.tslSkillsValue != null) this.tslSkillsValue.Visible = !busy;
                if (this.tslAgents != null) this.tslAgents.Visible = !busy;
                if (this.tslAgentsValue != null) this.tslAgentsValue.Visible = !busy;
            }
            catch { }
        }

        // Renders the idle half of the status bar's left slot: how many MCP tools and skills the
        // active conversation would get on its next turn. Mirrors the send path's math — tools are
        // the registry names usable for the tab's working directory minus the skill-gated hidden
        // set; skills resolve the on-disk catalog through the global-default + per-conversation
        // override ladder (SkillResolve), exactly like the turn setup does.
        private void UpdateToolSkillCounts()
        {
            try
            {
                var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
                Conversation convo = (ctx != null) ? ctx.Conversation : null;
                string workdir = (ctx != null) ? ctx.WorkingDir : null;

                SkillCatalog catalog =
                    SkillRoots.BuildCatalog(AppDomain.CurrentDomain.BaseDirectory, workdir);
                List<Skill> enabledSkills = SkillResolve.EnabledSkills(
                    catalog.Skills, SkillEnablement.LoadGlobal(),
                    (convo != null) ? convo.SkillsFeatureOff : null,
                    (convo != null) ? convo.SkillOverrides : null);

                int toolCount = 0;
                if (_mcpRegistry != null)
                {
                    // Tools may include a folderless conversation's scratch command server; skills/project
                    // discovery above stays on the real workspace (scratch has no project skills).
                    IList<string> names = _mcpRegistry.NamesForWorkdir(ResolutionWorkdirForContext(ctx));
                    ICollection<string> hidden = ExtensionsToolGate.HiddenTools(enabledSkills);
                    for (int i = 0; i < names.Count; i++)
                        if (!hidden.Contains(names[i])) toolCount++;
                }

                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (this.tslTools != null) this.tslTools.Text = "Tools enabled:";
                if (this.tslToolsValue != null) this.tslToolsValue.Text = toolCount.ToString(inv);
                if (this.tslSkills != null) this.tslSkills.Text = "Skills:";
                if (this.tslSkillsValue != null)
                    this.tslSkillsValue.Text = enabledSkills.Count.ToString(inv);

                // Agents (after Skills): how many sub-agents are usable on the next turn. "Usable" means the
                // agent's required tools are actually available here (runtime per-agent enablement), matching
                // the send path's filter. The tooltip lists the enabled ones and, for any disabled, what to
                // turn on (e.g. web-research needs the web tools).
                int agentCount = 0;
                string agentTip = "Sub-agents available to this conversation";
                bool agentsOn = AgentEnablement.FeatureEnabled(convo != null ? convo.AgentsEnabled : null);
                if (!agentsOn)
                {
                    agentTip = "Sub-agents are off. Turn them on in Settings (Tools) or with /toggle-agents.";
                }
                else
                {
                    AgentCatalog acat = AgentRoots.BuildCatalog(AppDomain.CurrentDomain.BaseDirectory, workdir);
                    if (acat.Agents.Count == 0)
                    {
                        agentTip = "No agents found. Add <slug>.md files under the app's agents folder, "
                            + "%AppData%/GxPT/agents, or <workspace>/.gxpt/agents.";
                    }
                    else
                    {
                        IList<string> aNames = (_mcpRegistry != null)
                            ? _mcpRegistry.NamesForWorkdir(workdir) : (IList<string>)new List<string>();
                        Func<string, ToolTier> agentTierOf = AgentTierOf();
                        // Three buckets: full use (all tools available), degraded (usable but missing some),
                        // disabled (no tools available). The status-bar count is full + degraded (all usable).
                        List<string> fullLines = new List<string>();
                        List<string> degradedLines = new List<string>();
                        List<string> disabledLines = new List<string>();
                        for (int i = 0; i < acat.Agents.Count; i++)
                        {
                            Agent a = acat.Agents[i];
                            List<string> need = AgentAvailability.MissingTools(a, aNames);
                            string missing = need.Count > 0 ? " (missing " + string.Join(", ", need.ToArray()) + ")" : "";
                            if (AgentAvailability.IsAvailable(a, aNames, agentTierOf))
                            {
                                agentCount++;
                                if (need.Count > 0) degradedLines.Add("  " + a.Slug + missing);
                                else fullLines.Add("  " + a.Slug);
                            }
                            else
                            {
                                disabledLines.Add("  " + a.Slug + (need.Count > 0 ? missing : " (no available tools)"));
                            }
                        }
                        System.Text.StringBuilder tip = new System.Text.StringBuilder();
                        AppendAgentSection(tip, "Full use", fullLines);
                        AppendAgentSection(tip, "Degraded", degradedLines);
                        AppendAgentSection(tip, "Disabled", disabledLines);
                        agentTip = tip.ToString();
                    }
                }
                if (this.tslAgents != null) { this.tslAgents.Text = "Agents:"; this.tslAgents.ToolTipText = agentTip; }
                if (this.tslAgentsValue != null)
                {
                    this.tslAgentsValue.Text = agentCount.ToString(inv);
                    this.tslAgentsValue.ToolTipText = agentTip;
                }
            }
            catch { }
        }

        // A tool->tier classifier for status-bar agent availability (workdir-independent classification,
        // built fresh and cheap). Mirrors the send path's tierOf (the approval policy's classification);
        // first-party agent tools are classified by the hardcoded table, so annotations are best-effort.
        private Func<string, ToolTier> AgentTierOf()
        {
            try
            {
                ToolApprovalPolicy p = new ToolApprovalPolicy(new ToolClassifier(), null, null,
                    _mcpRegistry as IToolAnnotationSource);
                return p.TierOf;
            }
            catch { return null; }
        }

        // Appends a "<header> (N):" section + its indented lines to the agents tooltip; skips empty sections,
        // and separates non-first sections with a blank line.
        private static void AppendAgentSection(System.Text.StringBuilder sb, string header, List<string> lines)
        {
            if (sb == null || lines == null || lines.Count == 0) return;
            if (sb.Length > 0) sb.Append("\r\n\r\n");
            sb.Append(header).Append(" (").Append(lines.Count).Append("):");
            for (int i = 0; i < lines.Count; i++) sb.Append("\r\n").Append(lines[i]);
        }

        // The tool registry changed (a server connected, refreshed its tools, or went away) on a
        // connection reader thread: marshal to the UI thread and repaint the status bar's tool
        // count so it tracks servers coming online after launch / workdir changes.
        private void McpRegistry_Changed()
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    try { UpdateToolSkillCounts(); }
                    catch { }
                });
            }
            catch { }
        }

        // Stop button in the status bar: the indicator always shows the active tab's send, so
        // cancel the active tab's request.
        private void tsiStopGen_Click(object sender, EventArgs e)
        {
            // While the item shows a passive label ("awaiting user..." at an approval gate, or
            // "sub-agents running..." during a fan-out - cancel those from the panel), there's nothing to
            // stop from here; ignore clicks.
            if (this.tsiStopGen != null && (this.tsiStopGen.Awaiting || this.tsiStopGen.AgentsRunning)) return;
            try { CancelActiveRequest(_tabManager != null ? _tabManager.GetActiveContext() : null); }
            catch { }
        }

        private void DismissWorkspaceStripForContext(TabManager.ChatTabContext ctx)
        {
            if (ctx == null) return;
            if (ctx.WorkspaceStrip != null) ctx.WorkspaceStrip.Visible = false;
            if (ctx.Conversation != null)
            {
                ctx.Conversation.WorkspaceStripDismissed = true;
                try { if (!ctx.NoSaveUntilUserSend) ConversationStore.Save(ctx.Conversation); }
                catch { }
            }
        }

        // Entry point for the tab context menu: set the working folder for a specific tab. Useful
        // when the strip has been dismissed (setting a folder re-shows it).
        internal void SetWorkingFolderForTab(TabPage page)
        {
            if (_tabManager == null || page == null) return;
            TabManager.ChatTabContext ctx;
            if (!_tabManager.TabContexts.TryGetValue(page, out ctx) || ctx == null) return;
            SetWorkingFolderForContext(ctx);
        }

        private void SetWorkingFolderForContext(TabManager.ChatTabContext ctx)
        {
            if (ctx == null) return;
            string selectedDir;
            if (!FolderPicker.TrySelectFolder(this, ctx.WorkingDir,
                    "Select a working folder for file, git, and command tools in this conversation.",
                    out selectedDir))
                return;
            ctx.WorkingDir = selectedDir;
            RecentWorkDirs.Add(ctx.WorkingDir);
            // Setting a folder re-shows the strip, so a prior dismissal no longer applies.
            if (ctx.Conversation != null) ctx.Conversation.WorkspaceStripDismissed = false;
            PersistWorkingDir(ctx);
            if (ctx.WorkspaceStrip != null)
            {
                ctx.WorkspaceStrip.SetWorkingDir(ctx.WorkingDir);
                ctx.WorkspaceStrip.Visible = true; // re-show if it had been dismissed
            }
            SyncMcpWorkingDirFromActiveTab();
        }

        private void ClearWorkingFolderForContext(TabManager.ChatTabContext ctx)
        {
            if (ctx == null) return;
            ctx.WorkingDir = null;
            PersistWorkingDir(ctx);
            if (ctx.WorkspaceStrip != null) ctx.WorkspaceStrip.SetWorkingDir(null);
            SyncMcpWorkingDirFromActiveTab();
        }

        // Reset the per-tab workspace + ZDR *views* to a blank-slate state when the last tab is closed
        // and reused as a fresh conversation. The recycle path already swaps in a new Conversation (with
        // its ZDR and working-dir state cleared), but the tab-level WorkingDir field, the workspace strip,
        // and the ZDR checkbox are derived views that must be re-synced or they keep the closed tab's
        // state. No PersistWorkingDir here: the fresh conversation's WorkingDir is already null and the
        // blank tab shouldn't be saved.
        internal void ResetRecycledTabWorkspaceState(TabManager.ChatTabContext ctx)
        {
            if (ctx == null) return;
            // Working folder: drop the tab's folder and return the strip to its "no folder" state, shown
            // like a brand-new tab (the fresh conversation isn't dismissed).
            ctx.WorkingDir = null;
            if (ctx.WorkspaceStrip != null)
            {
                ctx.WorkspaceStrip.SetWorkingDir(null);
                ctx.WorkspaceStrip.Visible = true;
            }
            SyncMcpWorkingDirFromActiveTab(); // release the closed folder's scoped MCP servers
            // ZDR: the new conversation is unlatched and not per-tab ZDR, so re-sync the checkbox view
            // (it reverts to the global default's checked/disabled state).
            SyncZdrCheckboxFromActiveTab();
            // Generation indicator: the closed conversation's turn may still be in flight on this
            // recycled context. It keeps running detached (IsSending still gates new sends until it
            // finalizes), but the indicator must stop advertising it — the conversation it belongs
            // to is gone. The flag also keeps later tab-switch syncs from re-showing it.
            ctx.SendDetached = ctx.IsSending;
            SyncGenerationIndicatorFromActiveTab();
            // Usage status bar (context / cost / savings): recycling reuses the same selected tab, so
            // no tab-switch fires to refresh it - without this the bar keeps showing the closed
            // conversation's totals. The fresh conversation's totals are zero, so the bar clears.
            SyncUsageStatusFromActiveTab();
            // A tool-approval / continuation prompt may already be showing for the closed
            // conversation's now-detached turn: resolve it as Deny/Stop so the blocked worker is
            // released and the turn wraps up and saves. (Prompts the turn raises AFTER detaching are
            // auto-denied by the send path's panel gate.)
            try { if (ctx.ApprovalPanel != null) ctx.ApprovalPanel.DenyPending(); }
            catch { }
            try { if (ctx.QuestionPanel != null) ctx.QuestionPanel.DenyPending(); }
            catch { }
        }

        // Opens a conversation tab whose working folder is preset to 'dir'. Mirrors the
        // load flow (set working dir -> persist -> adopt onto strip + MCP host), and (via
        // ApplyLoadedWorkingDir) bumps 'dir' to the top of the recent list. Like opening a
        // conversation from history, this reuses a blank tab (no messages) when one exists
        // instead of stacking a new tab.
        private void OpenNewTabWithWorkingDir(string dir)
        {
            if (_tabManager == null || string.IsNullOrEmpty(dir)) return;
            TabManager.ChatTabContext ctx = FindBlankTabPreferActive();
            if (ctx == null) ctx = _tabManager.CreateConversationTab();
            if (ctx == null) return;
            ctx.WorkingDir = dir;
            if (ctx.Conversation != null)
            {
                ctx.Conversation.WorkingDir = dir;
                ctx.Conversation.WorkspaceStripDismissed = false;
            }
            PersistWorkingDir(ctx); // holds the folder in memory; a blank tab isn't written until first send
            // (The tab is already registered in the open-by-id dedup map from when its conversation was
            // assigned - see TabManager.WireConversationTracking - under the id it will save with.)
            ApplyLoadedWorkingDir(ctx); // shows the workspace strip + binds MCP; also re-adds to recents
            try { SelectTab(ctx.Page); } catch { }
        }

        // After a conversation is loaded into a tab, adopt its persisted working folder onto the tab
        // context + strip and (re)bind the MCP host to it.
        private void ApplyLoadedWorkingDir(TabManager.ChatTabContext ctx)
        {
            if (ctx == null) return;
            ctx.WorkingDir = (ctx.Conversation != null) ? ctx.Conversation.WorkingDir : null;
            if (!string.IsNullOrEmpty(ctx.WorkingDir)) RecentWorkDirs.Add(ctx.WorkingDir);
            if (ctx.WorkspaceStrip != null)
            {
                ctx.WorkspaceStrip.SetWorkingDir(ctx.WorkingDir);
                // Hidden only if there's no folder AND the user previously dismissed it.
                bool dismissed = string.IsNullOrEmpty(ctx.WorkingDir) && ctx.Conversation != null &&
                                 ctx.Conversation.WorkspaceStripDismissed;
                ctx.WorkspaceStrip.Visible = !dismissed;
            }
            SyncMcpWorkingDirFromActiveTab();
        }

        // Mirror the tab's working folder onto its persisted conversation and save, so it re-opens
        // with the same folder. Skipped for brand-new, never-sent conversations (NoSaveUntilUserSend).
        private void PersistWorkingDir(TabManager.ChatTabContext ctx)
        {
            if (ctx == null || ctx.Conversation == null) return;
            // Always hold the folder in memory; the first user send persists the conversation (with this
            // folder) like any other blank tab.
            ctx.Conversation.WorkingDir = ctx.WorkingDir;
            // Don't write a blank, never-sent conversation to disk just because a workspace folder was
            // set - that littered the history sidebar with empty "New Conversation" entries (and created
            // invisible empty files on the strip path, which doesn't refresh the sidebar). A conversation
            // that already has messages still persists the folder change immediately.
            try
            {
                if (!ctx.NoSaveUntilUserSend && ctx.Conversation.History.Count > 0)
                    ConversationStore.Save(ctx.Conversation);
            }
            catch { }
        }

        // Single point that registers a tab's conversation in the sidebar's open-by-id dedup map, so
        // re-opening that conversation focuses THIS tab instead of loading a duplicate copy. Driven by
        // ChatTabContext.ConversationAssigned (wired in TabManager.WireConversationTracking), so it runs
        // for EVERY way a conversation is put on a tab - new tab, /new-conversation, recycle, opening from the
        // sidebar/history, help, or opening a recent workspace - and any future path gets it for free.
        // Tracking an id before its file exists is harmless: the dedup only ever looks up ids that have a
        // saved sidebar entry, and a brand-new conversation keeps the same id through to its first save.
        // Idempotent (overwrites the id->page entry). The id is ensured at creation/load before the
        // assignment fires, so it is non-empty here in practice; guarded anyway.
        internal void OnTabConversationAssigned(TabManager.ChatTabContext ctx)
        {
            if (ctx == null) return;
            // Atomic re-point: drop whatever id this page mapped to before (e.g. the blank conversation
            // a reused tab started with) and register the new one, so the page maps to exactly its
            // current conversation and no stale entries accumulate.
            UntrackOpenConversation(ctx.Page);
            TrackOpenConversationForTab(ctx);
        }

        private void TrackOpenConversationForTab(TabManager.ChatTabContext ctx)
        {
            if (_sidebarManager == null || ctx == null || ctx.Conversation == null) return;
            if (string.IsNullOrEmpty(ctx.Conversation.Id)) return;
            _sidebarManager.TrackOpenConversation(ctx.Conversation.Id, ctx.Page);
        }

        private void OnTabsChanged()
        {
            if (_sidebarManager != null) _sidebarManager.RefreshSidebarList();
            // A tab opening/closing changes which working folders are still in use; release the
            // scoped servers of any folder whose last tab just closed.
            SyncMcpWorkingDirFromActiveTab();
            // The active conversation may differ in whether it has any enabled skills; bring the Skills
            // MCP server into line (rebuilds only on an actual on/off change).
            SlashRefreshSkillsServer();
        }

        private void OnSidebarToggled()
        {
            if (_themeManager != null) _themeManager.ApplyFontSizeSettingToAllUi();
        }

        // Public methods for managers to call back
        internal OpenRouterClient GetClient()
        {
            return _client;
        }

        internal MenuStrip GetMenuStrip()
        {
            return this.msMain;
        }

        internal SidebarManager GetSidebarManager()
        {
            return _sidebarManager;
        }

        internal TabManager GetTabManager()
        {
            return _tabManager;
        }

        internal ThemeManager GetThemeManager()
        {
            return _themeManager;
        }

        internal InputManager GetInputManager()
        {
            return _inputManager;
        }

        // Allow TabManager to force-refresh the attachments banner after seeding attachments
        internal void RefreshAttachmentsBannerUi()
        {
            try { RebuildAttachmentsBanner(); }
            catch { }
        }

        internal void SelectTab(TabPage page)
        {
            if (_tabManager != null) _tabManager.SelectTab(page);
        }

        internal void CloseTab(TabPage page)
        {
            if (_tabManager != null) _tabManager.CloseConversationTab(page);
        }

        internal void UntrackOpenConversation(TabPage page)
        {
            if (_sidebarManager != null) _sidebarManager.UntrackOpenConversation(page);
        }

        internal void UpdateWindowTitle()
        {
            UpdateWindowTitleFromActiveTab();
        }

        internal void ApplyFontSetting(ChatTranscriptControl transcript)
        {
            if (_themeManager != null) _themeManager.ApplyFontSetting(transcript);
        }

        internal void EnsureConversationId(Conversation conversation)
        {
            ConversationStore.EnsureConversationId(conversation);
        }

        // Exit edit mode and restore compose UI (used for ESC and unchanged sends)
        internal void CancelEditingAndRestoreConversation()
        {
            try
            {
                var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
                if (ctx == null) return;

                ctx.PendingEditActive = false;
                ctx.PendingEditIndex = -1;
                ctx.PendingEditOriginalModel = null;

                // Clear attachments and refresh banner
                ClearAttachmentsBanner();

                // Clear input and restore hint
                if (_inputManager != null)
                {
                    _inputManager.ClearInput();
                    _inputManager.SetHintText();
                    _inputManager.FocusInputSoon();
                }
            }
            catch { }
        }

        internal void OpenConversation(Conversation convo)
        {
            // Prefer reusing a blank tab (no messages). If none, open a new tab.
            var blank = FindBlankTabPreferActive();
            if (blank != null)
            {
                OpenConversationInTab(blank, convo);
            }
            else
            {
                OpenConversationInNewTab(convo);
            }
        }

        public string GetSelectedModel()
        {
            try
            {
                string m = (cmbModel != null ? cmbModel.Text : null) ?? string.Empty;
                m = m.Trim();
                return string.IsNullOrEmpty(m) ? ModelDefaults.DefaultModel : m;
            }
            catch
            {
                return ModelDefaults.DefaultModel;
            }
        }

        private void InitializeClient()
        {
            // Read API key from settings.json in %AppData%\GxPT
            string apiKey = AppSettings.GetString("openrouter_api_key");
            // curl.exe expected next to executable (bin/Debug)
            string curlPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Lib\\curl.exe");
            _client = new OpenRouterClient(apiKey, curlPath);
            // Populate models from settings and reflect configuration
            PopulateModelsFromSettings();
            UpdateApiKeyBanner();
            if (_themeManager != null) _themeManager.ApplyFontSizeSettingToAllUi();
            // Apply theme across existing transcripts and primary UI
            try { if (_themeManager != null) _themeManager.ApplyThemeToAllTranscripts(); }
            catch { }

            // Sync Dark Mode menu checked state from settings
            try
            {
                if (this.miDarkMode != null)
                {
                    string theme = AppSettings.GetString("theme") ?? "light";
                    bool isDark = theme.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
                    this.miDarkMode.Checked = isDark;
                }
            }
            catch { }

            // Apply transcript/message width settings to existing transcripts
            try
            {
                var widths = TranscriptWidthSettings.Resolve();
                // Primary designer transcript
                widths.ApplyTo(this.chatTranscript);
                // All tab transcripts
                if (_tabManager != null)
                {
                    foreach (var kv in _tabManager.TabContexts)
                        widths.ApplyTo(kv.Value != null ? kv.Value.Transcript : null);
                }
            }
            catch { }

            // (Re)build the MCP host from the current settings (toggles, web-search key, GitHub PAT,
            // and mcp.json custom servers). Connecting happens on a background thread.
            RebuildMcpHost();
        }

        // Assembles MCP server specs from settings and (re)starts the host. Safe to call repeatedly
        // (e.g. after Settings is saved). Workdir-independent servers (web + GitHub + custom) connect
        // here; files/git/command are deferred until a working-directory UX exists.
        private void RebuildMcpHost()
        {
            try
            {
                // Tear down any previous host (servers from the old settings).
                if (_mcpHost != null)
                {
                    try { _mcpHost.Dispose(); }
                    catch { }
                    _mcpHost = null;
                }

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string curlPath = System.IO.Path.Combine(baseDir, "Lib\\curl.exe");
                string caBundle = System.IO.Path.Combine(baseDir, "Lib\\curl-ca-bundle.crt");

                var opts = new McpConfig.BuiltInOptions();
                // Defaults come from the schema (SettingsSchema) via the parameterless GetBool, so the
                // launch decision can't disagree with the seeded settings.json.
                opts.WebEnabled = AppSettings.GetBool("mcp_web_enabled");
                opts.FilesEnabled = AppSettings.GetBool("mcp_files_enabled");
                // Git defaults ON when git is installed and OFF when it isn't, and is force-disabled
                // when git is missing regardless of the stored setting — so we never launch a git
                // server whose tools could only return "git not found".
                opts.GitEnabled = GitProbe.IsInstalled() && AppSettings.GetBool("mcp_git_enabled");
                opts.CommandEnabled = AppSettings.GetBool("mcp_command_enabled");
                // MSBuild defaults ON when at least one MSBuild engine is found, OFF when none is, and is
                // force-disabled when none is present regardless of the stored setting — so we never launch
                // an MSBuild server whose tools would be an empty set.
                opts.MsBuildEnabled = MsBuildProbe.IsInstalled() && AppSettings.GetBool("mcp_msbuild_enabled");
                // Memory is a feature toggle (off by default); its soft index cap is user-configurable
                // (design sec.3/sec.6). Both the server launch and the system-prompt injection read this one
                // setting, so they can never desync.
                opts.MemoryEnabled = AppSettings.GetBool("mcp_memory_enabled");
                int memMaxLines = (int)AppSettings.GetDouble("mcp_memory_max_lines", 40);
                opts.MemoryMaxLines = memMaxLines > 0 ? memMaxLines : 40;
                // The extensions MCP server (skill + agent authoring + run_skill_script) follows skill
                // enablement: it runs whenever the active conversation has at least one enabled skill, and
                // is off when none are (so a skill-less chat pays nothing). Because agent-writer is itself a
                // skill, enabling it brings the server up too; the per-turn ExtensionsToolGate then reveals the
                // agent tools. SlashRefreshSkillsServer re-applies this when enablement changes. There is no
                // separate server toggle - /toggle-skills is the control.
                opts.SkillsEnabled = ActiveConversationHasEnabledSkills();
                _skillsServerEnabled = opts.SkillsEnabled;
                // Skill roots the server resolves scripts against: bundled (<exe>/skills, shipped with the
                // app) and user-global (%AppData%/GxPT/skills). The project root is derived from
                // GXPT_WORKDIR inside the server. These mirror the read-side roots (SkillInjection).
                opts.SkillsBundledRoot = SkillRoots.BundledRoot(baseDir);
                opts.SkillsUserRoot = SkillRoots.UserRoot();
                // Agent authoring shares the same server; its user-global root (%AppData%/GxPT/agents) is
                // injected so scope=user agent authoring has a target (the project root is derived from
                // GXPT_WORKDIR inside the server). The bundled root (<exe>/agents) is injected too so
                // read_agent/list_agents can see the shipped agents (e.g. explore) - reads span all roots,
                // mirroring read_skill_file; writes stay project/user.
                opts.AgentsUserRoot = AgentRoots.UserRoot();
                opts.AgentsBundledRoot = AgentRoots.BundledRoot(baseDir);
                opts.WebSearchKey = AppSettings.GetString("mcp_websearch_key");
                opts.CurlPath = curlPath;
                // Server exes: dev builds deploy them to a 'mcp-servers' subfolder (AfterBuild copy);
                // the installer lays them flat beside GxPT.exe (so VS dedupes the shared Mcp35/
                // Newtonsoft DLLs). Prefer the subfolder when present, else use the app dir.
                string serverSubdir = System.IO.Path.Combine(baseDir, "mcp-servers");
                opts.ServerDir = System.IO.Directory.Exists(serverSubdir) ? serverSubdir : baseDir;

                var specs = new List<McpServerSpec>(McpConfig.BuiltInSpecs(opts));
                specs.Add(McpConfig.GitHubSpec(
                    AppSettings.GetBool("mcp_github_enabled"),
                    AppSettings.GetString("mcp_github_pat")));

                // Custom servers from mcp.json (GitHub is configured above, not here).
                try
                {
                    string mcpFile = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "GxPT\\mcp.json");
                    if (System.IO.File.Exists(mcpFile))
                    {
                        string mcpText = System.IO.File.ReadAllText(mcpFile);
                        specs.AddRange(McpConfig.ParseUserServers(mcpText, LoggerSink.Instance));
                    }
                }
                catch { }

                var clientInfo = new Mcp35.Core.Protocol.Implementation();
                clientInfo.Name = "GxPT";
                clientInfo.Version = "1.0";

                _mcpRegistry = new McpToolRegistry(LoggerSink.Instance);
                // Keep the status bar's tool count tracking this (new) registry; the old registry's
                // teardown events may still fire but the handler always reads the current field.
                _mcpRegistry.Changed += McpRegistry_Changed;
                var connector = new DefaultServerConnector(clientInfo, curlPath, caBundle, LoggerSink.Instance);
                _mcpHost = new McpHost(connector, _mcpRegistry, LoggerSink.Instance);

                // Snapshot the active tab's working folder now (on the UI thread) so the rebuilt host
                // re-binds its workdir-scoped servers (files/git/command) to it. Without this, a host
                // rebuilt after a Settings save starts with no active workdir and those servers never
                // launch even though the current conversation has a folder set.
                string activeWorkdir = null;
                bool activeScratch = false;
                try
                {
                    var actCtx = _tabManager != null ? _tabManager.GetActiveContext() : null;
                    if (actCtx != null)
                    {
                        if (!string.IsNullOrEmpty(actCtx.WorkingDir)) activeWorkdir = actCtx.WorkingDir;
                        else
                        {
                            // Folderless active tab with an already-created scratch dir: re-bind its
                            // command-only set after the rebuild.
                            string s = ResolutionWorkdirForContext(actCtx);
                            if (!string.IsNullOrEmpty(s)) { activeWorkdir = s; activeScratch = true; }
                        }
                    }
                }
                catch { }

                // Connecting (handshakes / process spawns) can block — do it off the UI thread.
                McpHost hostRef = _mcpHost;
                string wd = activeWorkdir;
                bool wdScratch = activeScratch;
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        hostRef.Start(specs);
                        // Launch the workdir-scoped servers for the active conversation's folder (or the
                        // command-only set for its scratch dir when folderless).
                        if (!string.IsNullOrEmpty(wd))
                        {
                            if (wdScratch) hostRef.EnsureScratchDir(wd);
                            else hostRef.EnsureWorkingDir(wd);
                        }
                    }
                    catch (Exception ex) { try { Logger.Log("mcp", "host start failed: " + ex.Message); } catch { } }
                });
            }
            catch (Exception ex)
            {
                try { Logger.Log("mcp", "RebuildMcpHost failed: " + ex.Message); }
                catch { }
            }
        }

        // Build a context for the existing designer tab (tabPage1 + chatTranscript)
        private void SetupInitialConversationTab()
        {
            try
            {
                if (this.tabControl1 == null || this.tabPage1 == null || this.chatTranscript == null)
                    return;

                var ctx = _tabManager != null ? _tabManager.SetupInitialConversationTab(this.tabPage1, this.chatTranscript) : null;
                if (ctx != null)
                {
                    if (_sidebarManager != null && ctx.Conversation != null)
                        _sidebarManager.TrackOpenConversation(ctx.Conversation.Id, ctx.Page);
                    SyncComboModelFromActiveTab();
                    if (_sidebarManager != null) _sidebarManager.RefreshSidebarList();

                    // Ensure initial transcript theme is applied on launch
                    try { if (ctx.Transcript != null) ctx.Transcript.RefreshTheme(); }
                    catch { }
                }
            }
            catch { }
        }

        private void UpdateWindowTitleFromActiveTab()
        {
            try
            {
                var page = (this.tabControl1 != null ? this.tabControl1.SelectedTab : null);
                string name = (page != null ? page.Text : null);
                this.Text = string.IsNullOrEmpty(name) ? "GxPT" : ("GxPT - " + name);
            }
            catch { }
        }

        private void miSettings_Click(object sender, EventArgs e)
        {
            using (var dlg = new SettingsForm())
            {
                dlg.ShowDialog(this);
                // The dialog writes settings.json directly; drop the cached copy so reads are fresh.
                AppSettings.Reload();
                // Re-sync the per-tab checkbox. Its checked/enabled state now depends only on the
                // conversation's own toggle and latch — the global default seeds new conversations but
                // does not retroactively change one that's already open.
                SyncZdrCheckboxFromActiveTab();
                // Re-init client in case API key changed
                InitializeClient();
                UpdateApiKeyBanner();
                // Theme may have changed; re-apply to all open transcripts
                try { if (_themeManager != null) _themeManager.ApplyThemeToAllTranscripts(); }
                catch { }
                // Re-apply transcript/message widths in case they changed
                try
                {
                    var widths = TranscriptWidthSettings.Resolve();
                    widths.ApplyTo(this.chatTranscript);
                    if (_tabManager != null)
                    {
                        foreach (var kv in _tabManager.TabContexts)
                            widths.ApplyTo(kv.Value != null ? kv.Value.Transcript : null);
                    }
                }
                catch { }
            }
        }

        // ===== Export/Import conversations (DotNetZip) =====
        // Legacy path resolver no longer needed; logic consolidated in ImportExportService

        // Designer wires this
        private void miExport_Click(object sender, EventArgs e)
        {
            ImportExportManager.ExportAll(this);
        }

        // Designer wires this. Routes through the same open-file dialog as /import, so File > Import handles
        // every archive kind - conversations (.gxcv), skills (.gxsk), and plugins (.gxpl) - not just
        // conversations. The conversation path inside ImportArchiveFromShell refreshes the sidebar itself.
        private void miImport_Click(object sender, EventArgs e)
        {
            SlashImportArchive();
        }

        // Called by Program.cs after the form is shown when the app is launched by double-clicking a
        // .gxpt/.gxcv/.gxsk/.gxpl file; also the dispatch point for /import. A .gxpl (or a .zip with a
        // plugin.json) routes to the plugin importer, a .gxsk (or a .zip with a SKILL.md) to the skill
        // importer, and everything else to the conversation importer.
        public void ImportArchiveFromShell(string archivePath)
        {
            if (string.IsNullOrEmpty(archivePath)) return;
            try
            {
                if (!System.IO.File.Exists(archivePath)) return;

                string ext = null;
                try { ext = System.IO.Path.GetExtension(archivePath); }
                catch { }
                if (ext != null) ext = ext.ToLowerInvariant();
                // A plugin (.gxpl, or a .zip carrying a plugin.json) is checked first: a plugin archive also
                // contains SKILL.md files, so it would otherwise be mistaken for a bare skill.
                if (ext == ".gxpl" || (ext == ".zip" && PluginImportExportService.ArchiveContainsPlugin(archivePath)))
                {
                    if (PluginImportExportManager.InstallFromFile(this, archivePath))
                        SlashRefreshSkillsServer(); // installed skills may flip "any skill enabled"
                    return;
                }
                if (ext == ".gxsk" || (ext == ".zip" && SkillImportExportService.ArchiveContainsSkill(archivePath)))
                {
                    if (SkillImportExportManager.ImportSkillFromFile(this, archivePath))
                        SlashRefreshSkillsServer(); // the imported skill may flip "any skill enabled"
                    return;
                }

                // Warn about overwrite if there are existing conversations
                try
                {
                    if (ConversationStore.ListAll().Count > 0)
                    {
                        var dr = MessageBox.Show(this,
                            "Importing will overwrite existing files with the same names. Continue?",
                            "Import Conversations", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                        if (dr != DialogResult.Yes) return;
                    }
                }
                catch { }

                string targetDir = ImportExportService.GetConversationsFolderPath();
                try { System.IO.Directory.CreateDirectory(targetDir); }
                catch { }

                try
                {
                    ImportExportService.ImportAll(archivePath, targetDir, true);
                    try { MessageBox.Show(this, "Import completed.", "Import Conversations", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                    catch { }
                    try { if (_sidebarManager != null) _sidebarManager.RefreshSidebarList(); }
                    catch { }
                }
                catch (Exception ex)
                {
                    try { MessageBox.Show(this, "Import failed: " + ex.Message, "Import Conversations", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    catch { }
                }
            }
            catch { }
        }

        private void miExit_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        // Build the slash-command registry/processor on first use. Defaults are merged with the user's
        // %AppData%/GxPT/commands.json (if present). The context reads live state through delegates so
        // it always sees the current working folder and the current MCP registry (which is recreated
        // when the working folder changes).
        private void EnsureSlashCommands()
        {
            if (_slashProcessor != null) return;

            string userJson = null;
            try
            {
                string path = Path.Combine(AppSettings.SettingsDirectory, "commands.json");
                if (File.Exists(path)) userJson = File.ReadAllText(path);
            }
            catch { }

            // Built-in client commands first, then skills commands, then prompt commands (built-in +
            // user commands.json).
            var all = new List<ISlashCommand>();
            all.AddRange(ClientCommands.BuiltIns());
            all.AddRange(SkillCommandShared.BuiltIns());
            all.AddRange(AgentCommands.BuiltIns());
            all.AddRange(SlashCommandConfig.LoadMerged(userJson, LoggerSink.Instance));

            _slashRegistry = new SlashCommandRegistry(all);
            _slashProcessor = new SlashCommandProcessor(_slashRegistry);
            _slashContext = new MainFormSlashContext(this);
        }

        // ===== ISlashCommandContext backing (called via MainFormSlashContext) =====

        internal string SlashWorkingDir()
        {
            var c = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (c == null) return null;
            // The tab's WorkingDir is the canonical value (what the MCP servers receive); fall back to the
            // conversation's copy only if the tab field is empty.
            if (!string.IsNullOrEmpty(c.WorkingDir)) return c.WorkingDir;
            return c.Conversation != null ? c.Conversation.WorkingDir : null;
        }

        internal bool SlashHasServer(string serverName)
        {
            return _mcpRegistry != null && _mcpRegistry.HasServer(serverName);
        }

        internal void SlashWriteInfo(string text)
        {
            try
            {
                var c = _tabManager != null ? _tabManager.GetActiveContext() : null;
                if (c != null && c.Transcript != null)
                    c.Transcript.AddMessage(MessageRole.Tool, text ?? string.Empty); // chromeless, like tool activity
            }
            catch { }
        }

        internal IList<string> SlashGetModels()
        {
            var list = AppSettings.GetList("models");
            if (list == null || list.Count == 0) list = ModelDefaults.ModelList();
            return list ?? new List<string>();
        }

        internal string SlashGetActiveModel()
        {
            return GetSelectedModel();
        }

        internal void SlashSetModel(string slug)
        {
            if (string.IsNullOrEmpty(slug)) return;
            try
            {
                if (this.cmbModel != null)
                {
                    _syncingModelCombo = true;
                    try { this.cmbModel.Text = slug; }
                    finally { _syncingModelCombo = false; }
                }
                var c = _tabManager != null ? _tabManager.GetActiveContext() : null;
                if (c != null)
                {
                    c.SelectedModel = slug;
                    if (c.Conversation != null) c.Conversation.SelectedModel = slug;
                }
            }
            catch { }
        }

        // Built-in MCP tool servers that can be toggled by name with /toggle-tool (custom mcp.json servers are
        // presence-based). Memory and skills are intentionally excluded -- they are feature toggles, not
        // tool servers: memory via Settings, skills via /toggle-skills (which also drives the skills MCP server).
        internal IList<string> SlashGetServerNames()
        {
            return new List<string>(new string[]
            {
                McpConfig.WebName, McpConfig.FilesName, McpConfig.GitName,
                McpConfig.CommandName, McpConfig.MsBuildName, McpConfig.GitHubName
            });
        }

        // (settings key, default-on) for a server name, or null/false for an unknown name.
        private static string ServerSettingKey(string name, out bool defaultOn)
        {
            defaultOn = false;
            if (string.Equals(name, McpConfig.WebName, StringComparison.OrdinalIgnoreCase)) return "mcp_web_enabled";
            if (string.Equals(name, McpConfig.FilesName, StringComparison.OrdinalIgnoreCase)) return "mcp_files_enabled";
            if (string.Equals(name, McpConfig.GitName, StringComparison.OrdinalIgnoreCase)) { defaultOn = true; return "mcp_git_enabled"; }
            if (string.Equals(name, McpConfig.CommandName, StringComparison.OrdinalIgnoreCase)) return "mcp_command_enabled";
            if (string.Equals(name, McpConfig.MsBuildName, StringComparison.OrdinalIgnoreCase)) { defaultOn = true; return "mcp_msbuild_enabled"; }
            if (string.Equals(name, McpConfig.MemoryName, StringComparison.OrdinalIgnoreCase)) return "mcp_memory_enabled";
            // skills has no independent server key - it follows the skills feature (/toggle-skills global).
            if (string.Equals(name, McpConfig.GitHubName, StringComparison.OrdinalIgnoreCase)) return "mcp_github_enabled";
            return null;
        }

        internal bool SlashGetServerEnabled(string name)
        {
            bool defaultOn;
            string key = ServerSettingKey(name, out defaultOn);
            if (key == null) return false;
            bool stored = AppSettings.GetBool(key, defaultOn);
            // Git/MSBuild are force-off when the underlying tool isn't installed, mirroring RebuildMcpHost.
            if (string.Equals(name, McpConfig.GitName, StringComparison.OrdinalIgnoreCase)) return stored && GitProbe.IsInstalled();
            if (string.Equals(name, McpConfig.MsBuildName, StringComparison.OrdinalIgnoreCase)) return stored && MsBuildProbe.IsInstalled();
            return stored;
        }

        internal string SlashSetServerEnabled(string name, bool enabled)
        {
            bool defaultOn;
            string key = ServerSettingKey(name, out defaultOn);
            if (key == null) return "Unknown server: " + name;

            if (enabled && string.Equals(name, McpConfig.GitName, StringComparison.OrdinalIgnoreCase) && !GitProbe.IsInstalled())
                return "git is not installed, so the git server can't be enabled.";
            if (enabled && string.Equals(name, McpConfig.MsBuildName, StringComparison.OrdinalIgnoreCase) && !MsBuildProbe.IsInstalled())
                return "MSBuild was not found, so the msbuild server can't be enabled.";
            if (enabled && string.Equals(name, McpConfig.GitHubName, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(AppSettings.GetString("mcp_github_pat")))
                return "Set a GitHub PAT in Settings before enabling the github server.";

            AppSettings.SetBool(key, enabled);
            RebuildMcpHost(); // reconnects with the new server set (connects on a background thread)
            return null;
        }

        // ---- skills: per-conversation override accessors backed by the active tab's Conversation ----

        internal bool? SlashGetConversationSkillsFeatureOff()
        {
            var c = _tabManager != null ? _tabManager.GetActiveContext() : null;
            return (c != null && c.Conversation != null) ? c.Conversation.SkillsFeatureOff : null;
        }

        internal void SlashSetConversationSkillsFeatureOff(bool? value)
        {
            var c = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (c == null || c.Conversation == null) return;
            c.Conversation.SkillsFeatureOff = value;
            SaveConversationContext(c);
        }

        internal bool? SlashGetConversationAgentsEnabled()
        {
            var c = _tabManager != null ? _tabManager.GetActiveContext() : null;
            return (c != null && c.Conversation != null) ? c.Conversation.AgentsEnabled : null;
        }

        internal void SlashSetConversationAgentsEnabled(bool? value)
        {
            var c = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (c == null || c.Conversation == null) return;
            c.Conversation.AgentsEnabled = value;
            SaveConversationContext(c);
        }

        // True when the active conversation has at least one enabled skill - the signal for whether the
        // Skills MCP server needs to run. Builds the catalog for the active workdir and resolves it
        // through the same ladder the send path uses, so the answer matches what the model will see.
        private bool ActiveConversationHasEnabledSkills()
        {
            var c = _tabManager != null ? _tabManager.GetActiveContext() : null;
            Conversation convo = (c != null) ? c.Conversation : null;
            // When there's no active conversation yet (early startup: RebuildMcpHost runs in the ctor
            // before the first tab is set up), fall back to the global defaults a fresh conversation
            // would inherit - null feature-off / no per-skill overrides. Otherwise the server stays OFF
            // at launch even with default-on skills, until something later (a /toggle-skill toggle, a tab
            // switch) happens to trigger a rebuild. A default conversation resolves identically to this
            // fallback, so it's the right "we don't know the conversation yet" answer.
            bool? convFeatureOff = (convo != null) ? convo.SkillsFeatureOff : null;
            IDictionary<string, bool> convOverrides = (convo != null) ? convo.SkillOverrides : null;
            string workdir = (c != null) ? c.WorkingDir : null;
            return SkillInjection.HasAnyEnabledSkills(
                AppDomain.CurrentDomain.BaseDirectory, workdir, convFeatureOff, convOverrides);
        }

        // The Skills MCP server tracks skill enablement; rebuild the host only when that crosses the
        // on/off boundary (so per-skill toggles that don't change whether ANY skill is enabled cost
        // nothing). Called after skills slash-command changes and on tab switches.
        internal void SlashRefreshSkillsServer()
        {
            if (ActiveConversationHasEnabledSkills() != _skillsServerEnabled)
                RebuildMcpHost();
            // Per-skill toggles change the counts even when they don't cross the server boundary.
            UpdateToolSkillCounts();
        }

        internal IDictionary<string, bool> SlashGetConversationSkillOverrides()
        {
            var c = _tabManager != null ? _tabManager.GetActiveContext() : null;
            var src = (c != null && c.Conversation != null) ? c.Conversation.SkillOverrides : null;
            return src != null
                ? new Dictionary<string, bool>(src, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        internal void SlashSetConversationSkillOverride(string slug, bool? value)
        {
            if (string.IsNullOrEmpty(slug)) return;
            var c = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (c == null || c.Conversation == null) return;
            if (c.Conversation.SkillOverrides == null)
                c.Conversation.SkillOverrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (value.HasValue) c.Conversation.SkillOverrides[slug] = value.Value;
            else c.Conversation.SkillOverrides.Remove(slug);
            SaveConversationContext(c);
        }

        internal void SlashResetConversationSkills()
        {
            var c = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (c == null || c.Conversation == null) return;
            c.Conversation.SkillsFeatureOff = null;
            if (c.Conversation.SkillOverrides != null) c.Conversation.SkillOverrides.Clear();
            SaveConversationContext(c);
        }

        // Persist a conversation after a slash-command edit, honoring the help-template no-save guard.
        private static void SaveConversationContext(TabManager.ChatTabContext c)
        {
            try { if (c != null && !c.NoSaveUntilUserSend) ConversationStore.Save(c.Conversation); }
            catch { }
        }

        internal void SlashNewConversation()
        {
            if (_tabManager != null) _tabManager.CreateConversationTab();
        }

        internal void SlashExportConversations()
        {
            ImportExportManager.ExportAll(this);
        }

        internal void SlashExportSkill(Skill skill)
        {
            // Universal export: a single skill is the smallest possible plugin (.gxpl), named after the skill.
            PluginImportExportManager.ExportSingleSkill(this, skill);
        }

        // /plugin export: author a new .gxpl from the user's/project's skills and agents.
        internal void SlashExportPlugin()
        {
            PluginImportExportManager.ExportInteractive(this, SlashWorkingDir());
        }

        // /plugin install: choose a .gxpl (or generic .zip) and install/upgrade it.
        internal void SlashInstallPlugin()
        {
            if (PluginImportExportManager.InstallInteractive(this))
                SlashRefreshSkillsServer(); // installed skills may flip "any skill enabled"
        }

        // File > Plugins > Manage: the installed-plugins dialog. Enable/disable/uninstall/install inside it
        // change which skills are active, so refresh the skills server once it closes.
        internal void SlashManagePlugins()
        {
            using (var dlg = new PluginManagerForm(SlashWorkingDir()))
                dlg.ShowDialog(this);
            SlashRefreshSkillsServer();
        }

        // Adds a single "Plugins Manager..." item to the File menu (after Export) in code rather than the
        // Designer, to avoid hand-editing generated menu plumbing. Install/uninstall/export/author all live
        // in the dialog, so one entry suffices; /plugin still offers the same actions from the command bar.
        private void BuildPluginsMenu()
        {
            try
            {
                if (miFile == null) return;

                var manage = new ToolStripMenuItem("&Plugins Manager...");
                manage.Click += new EventHandler(miPluginManage_Click);

                int idx = miExport != null ? miFile.DropDownItems.IndexOf(miExport) : -1;
                if (idx >= 0) miFile.DropDownItems.Insert(idx + 1, manage);
                else miFile.DropDownItems.Add(manage);
            }
            catch { }
        }

        private void miPluginManage_Click(object sender, EventArgs e) { SlashManagePlugins(); }

        // /plugin enable|disable <name>: move the plugin's skills/agents into or out of the active roots.
        internal void SlashSetPluginEnabled(string name, bool enabled)
        {
            if (PluginImportExportManager.SetEnabled(this, name, enabled))
                SlashRefreshSkillsServer();
        }

        // /plugin uninstall <name>: remove the plugin's skills/agents and its registry entry.
        internal void SlashUninstallPlugin(string name)
        {
            if (PluginImportExportManager.Uninstall(this, name))
                SlashRefreshSkillsServer();
        }

        // /import: one open-file dialog covering every archive kind; ImportArchiveFromShell routes the
        // chosen file to the plugin, skill, or conversation importer.
        internal void SlashImportArchive()
        {
            using (var ofd = new OpenFileDialog
            {
                Title = "Import",
                Filter = "GxPT Archives (*.gxcv;*.gxsk;*.gxpl)|*.gxcv;*.gxsk;*.gxpl"
                    + "|GxPT Conversation Archive (*.gxcv)|*.gxcv"
                    + "|GxPT Skill (*.gxsk)|*.gxsk"
                    + "|GxPT Plugin (*.gxpl)|*.gxpl"
                    + "|Zip Archive (*.zip)|*.zip",
                CheckFileExists = true,
                Multiselect = false
            })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                ImportArchiveFromShell(ofd.FileName);
            }
        }

        // ===== /compact: summarize the current conversation into a new tab =====

        private const string CompactionSystemPrompt =
            "You are a summarization assistant. Produce a concise but complete summary of the "
            + "conversation that captures the user's goals, key decisions, important facts, code, file "
            + "paths, and any open tasks or next steps. Write it so another assistant can resume the work "
            + "with full context. Output only the summary, with no preamble or commentary.";

        private const string CompactionContextPrefix =
            "The following is a summary of an earlier conversation, provided as context so you can "
            + "continue it seamlessly:\n\n";

        // Compaction always summarizes with deepseek-v4-flash (an order of magnitude cheaper than
        // comparable models, which matters when summarizing very long conversations), regardless of
        // the chat model.
        private const string CompactionModel = "deepseek/deepseek-v4-flash";

        // Chromeless marker shown atop a conversation that /compact created. Persisted via
        // Conversation.ContinuedFromCompaction, so it re-renders on reopen (see RebuildTranscriptAsync).
        internal const string CompactionNoteText = "Continued from a compacted conversation.";

        internal void SlashCompact()
        {
            var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (ctx == null || ctx.Conversation == null || ctx.Transcript == null) return;

            string transcriptText = RenderHistoryForSummary(ctx.Conversation.History);
            if (string.IsNullOrEmpty(transcriptText))
            {
                ctx.Transcript.AddMessage(MessageRole.Tool, "Nothing to compact.");
                return;
            }
            if (_client == null || !_client.IsConfigured)
            {
                ctx.Transcript.AddMessage(MessageRole.Tool, "Cannot compact: client is not configured.");
                return;
            }

            // Chromeless progress line; remember its index so we can flip it to "Compacted" when done.
            int msgIndex = ctx.Transcript.AddMessageGetIndex(MessageRole.Tool, "Compacting conversation...");

            string model = CompactionModel;
            bool zdr = ResolveZdrForSend(ctx);

            List<ChatMessage> messages = new List<ChatMessage>();
            messages.Add(new ChatMessage("system", CompactionSystemPrompt));
            messages.Add(new ChatMessage("user",
                "Summarize the following conversation so a new session can continue it seamlessly.\n\n"
                + transcriptText));

            // Snapshot what the completion callback needs (the active tab may change before it returns).
            var sourceCtx = ctx;
            var transcriptRef = ctx.Transcript;
            int idx = msgIndex;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string summary = null, error = null;
                try
                {
                    string raw = _client.CreateCompletion(model, messages,
                        new ClientProperties { Zdr = zdr ? true : (bool?)null });
                    summary = ExtractCompletionContent(raw); // CreateCompletion returns the raw JSON body
                }
                catch (Exception ex) { error = ex.Message; }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(summary))
                            {
                                transcriptRef.UpdateMessageAt(idx,
                                    "Compaction failed" + (string.IsNullOrEmpty(error) ? "." : ": " + error));
                                return;
                            }
                            transcriptRef.UpdateMessageAt(idx, "Compacted conversation.");
                            OpenCompactedConversation(sourceCtx, summary);
                        }
                        catch (Exception ex2)
                        {
                            try { transcriptRef.UpdateMessageAt(idx, "Compaction failed: " + ex2.Message); }
                            catch { }
                        }
                    });
                }
                catch { }
            });
        }

        // Open a new conversation tab seeded with the summary as a hidden system message (context). The
        // original conversation is left untouched. Inherits the source tab's working folder and model.
        private void OpenCompactedConversation(TabManager.ChatTabContext sourceCtx, string summary)
        {
            string wd = sourceCtx != null ? sourceCtx.WorkingDir : null;
            TabManager.ChatTabContext nctx;
            if (!string.IsNullOrEmpty(wd))
            {
                OpenNewTabWithWorkingDir(wd);
                nctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
            }
            else
            {
                nctx = _tabManager != null ? _tabManager.CreateConversationTab() : null;
            }
            if (nctx == null || nctx.Conversation == null) return;

            // System-role history is sent to the model but never rendered (RebuildTranscript skips it),
            // so the summary rides along as invisible context.
            nctx.Conversation.History.Add(new ChatMessage("system", CompactionContextPrefix + summary));
            // Persist the "continued from compaction" marker so the note survives reopen.
            nctx.Conversation.ContinuedFromCompaction = true;

            // Continue with the source conversation's model.
            if (sourceCtx != null && !string.IsNullOrEmpty(sourceCtx.SelectedModel))
            {
                nctx.SelectedModel = sourceCtx.SelectedModel;
                nctx.Conversation.SelectedModel = sourceCtx.SelectedModel;
                try
                {
                    if (this.cmbModel != null)
                    {
                        _syncingModelCombo = true;
                        try { this.cmbModel.Text = sourceCtx.SelectedModel; }
                        finally { _syncingModelCombo = false; }
                    }
                }
                catch { }
            }

            if (nctx.Transcript != null)
                nctx.Transcript.AddMessage(MessageRole.Tool, CompactionNoteText);

            // Persist immediately. A compacted conversation already has meaningful content (the summary
            // context + marker), so it must survive closing/reopening and app restart even before the
            // first user send -- otherwise it stays NoSaveUntilUserSend and is never written to disk.
            string srcName = (sourceCtx != null && sourceCtx.Conversation != null) ? sourceCtx.Conversation.Name : null;
            if (!string.IsNullOrEmpty(srcName) && !string.Equals(srcName, "New Conversation", StringComparison.OrdinalIgnoreCase))
                nctx.Conversation.Name = srcName + " (compacted)";
            nctx.Conversation.LastUpdated = DateTime.Now; // sort to the top of the history list
            nctx.NoSaveUntilUserSend = false;
            try { ConversationStore.Save(nctx.Conversation); }
            catch { }

            // Reflect the name on the tab, and register this tab as the open instance of the persisted
            // conversation -- so double-clicking its history entry focuses this tab instead of opening a
            // duplicate (the dedup is keyed on Conversation.Id via TrackOpenConversation).
            try
            {
                string title = string.IsNullOrEmpty(nctx.Conversation.Name) ? "New Conversation" : nctx.Conversation.Name;
                if (nctx.Page != null) nctx.Page.Text = ZdrTitle(nctx.Conversation, title);
            }
            catch { }
            try { if (_sidebarManager != null) _sidebarManager.TrackOpenConversation(nctx.Conversation.Id, nctx.Page); }
            catch { }

            // Refresh the open history sidebar on a fresh UI message: a synchronous refresh here runs
            // inside the tab-creation/layout call stack and doesn't take, so the new entry would only
            // show after the sidebar is toggled. Deferring lets it settle first.
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    try { if (_sidebarManager != null) _sidebarManager.RefreshSidebarList(); }
                    catch { }
                });
            }
            catch { }
        }

        // Pull choices[0].message.content from a chat-completion JSON body (CreateCompletion returns the
        // raw body). Mirrors Conversation.ExtractTitleFromJson; uses JavaScriptSerializer like that path.
        private static string ExtractCompletionContent(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                var root = ser.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null || !root.ContainsKey("choices")) return null;
                var choices = root["choices"] as object[];
                if (choices == null || choices.Length == 0) return null;
                var first = choices[0] as Dictionary<string, object>;
                if (first == null) return null;
                if (first.ContainsKey("message"))
                {
                    var msg = first["message"] as Dictionary<string, object>;
                    if (msg != null && msg.ContainsKey("content"))
                        return msg["content"] as string;
                }
                if (first.ContainsKey("text"))
                    return first["text"] as string;
            }
            catch { }
            return null;
        }

        private static string RenderHistoryForSummary(IList<ChatMessage> history)
        {
            if (history == null) return string.Empty;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < history.Count; i++)
            {
                var m = history[i];
                if (m == null) continue;
                string role = (m.Role ?? string.Empty).ToLowerInvariant();
                if (role != "user" && role != "assistant") continue;
                string content = m.Content ?? string.Empty;
                if (content.Trim().Length == 0) continue;
                sb.Append(role == "user" ? "User: " : "Assistant: ").Append(content).Append("\n\n");
            }
            return sb.ToString().Trim();
        }

        // Accessors used by the autocomplete popup. Both ensure the subsystem is built first, so the
        // popup works on the very first keystroke (before any send has occurred).
        internal SlashCommandRegistry GetSlashRegistry()
        {
            EnsureSlashCommands();
            return _slashRegistry;
        }

        internal ISlashCommandContext GetSlashContext()
        {
            EnsureSlashCommands();
            return _slashContext;
        }

        private void btnSend_Click(object sender, EventArgs e)
        {
            var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (ctx == null) return;

            // Validate input
            string baseText = _inputManager != null ? (_inputManager.GetInputText() ?? string.Empty) : string.Empty;
            string text = baseText;
            if (ctx.IsSending) return; // ensure only one in-flight request per tab

            // Slash-command interception. Only a leading slash (position 0) is a command; prompt
            // commands expand in place (the expansion becomes the actual user message), while gated or
            // invalid commands are surfaced without sending. Unknown "/foo" returns null and is sent
            // literally, so ordinary messages that happen to start with "/" are not hijacked.
            string pendingSystemContext = null; // a slash command's hidden system message, committed at send
            if (baseText.Length > 0 && baseText[0] == '/')
            {
                EnsureSlashCommands();
                var slash = _slashProcessor.Process(baseText, _slashContext);
                if (slash != null)
                {
                    if (slash.Error != null)
                    {
                        // Surface the reason as a chromeless (tool-style) line; keep the typed command so
                        // the user can correct it (e.g. fix the path or enable the server).
                        ctx.Transcript.AddMessage(MessageRole.Tool, slash.Error);
                        return;
                    }
                    if (!slash.SendToModel)
                    {
                        // Client command handled locally (none in v1, but the seam is live).
                        if (_inputManager != null) _inputManager.ClearInput();
                        return;
                    }
                    // Prompt expansion: send the template instead of the typed "/command".
                    baseText = slash.TextToSend;
                    text = slash.TextToSend;
                    pendingSystemContext = slash.SystemContext; // flushed to history at the send commit
                }
            }

            // Guard: a PDF with no extractable text that won't be sent as a full document would only
            // produce an empty placeholder. Block the send and explain, rather than sending nothing.
            if (ctx.PendingAttachments != null && ctx.PendingAttachments.Count > 0)
            {
                bool supportsFile = CurrentModelSupportsFile();
                bool zdr = ActiveConversationIsZdr();
                for (int i = 0; i < ctx.PendingAttachments.Count; i++)
                {
                    var af = ctx.PendingAttachments[i];
                    if (af == null || af.EffectiveKind != AttachmentKind.Pdf) continue;
                    bool hasText = !string.IsNullOrEmpty(af.Content) && af.Content.Trim().Length > 0;
                    if (hasText) continue;
                    bool nativeOk = (af.SendNativePdf == true) && supportsFile && !zdr
                                    && !string.IsNullOrEmpty(af.Data);
                    if (nativeOk) continue;

                    // Tailor both the cause and the remedy to the actual reason so the text never
                    // mentions ZDR when the cause is model support, or vice versa.
                    string why, remedy;
                    if (zdr)
                    {
                        why = "full-document reading is disabled in Zero-Data-Retention conversations";
                        remedy = "Remove the attachment, or start a new conversation without "
                               + "Zero-Data-Retention.";
                    }
                    else if (!supportsFile)
                    {
                        why = "the selected model can't read full PDF documents";
                        remedy = "Remove the attachment, or switch to a model that supports full PDF "
                               + "documents.";
                    }
                    else
                    {
                        why = "full-document sending isn't enabled for this attachment";
                        remedy = "Remove the attachment, or enable full-document sending for it.";
                    }
                    MessageBox.Show(this,
                        "\"" + af.FileName + "\" has no extractable text and can't be sent as a full "
                        + "document - " + why + ".\n\n" + remedy,
                        "Cannot Send PDF", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            // If editing a prior user message, compare text+attachments+model; confirm only if changed
            bool isEditResend = false;
            if (ctx.PendingEditActive && ctx.PendingEditIndex >= 0)
            {
                int cutIndex = ctx.PendingEditIndex;
                if (cutIndex >= 0 && cutIndex < ctx.Conversation.History.Count)
                {
                    var orig = ctx.Conversation.History[cutIndex];
                    if (orig != null && string.Equals(orig.Role, "user", StringComparison.OrdinalIgnoreCase))
                    {
                        // Compare text
                        bool sameText = string.Equals(orig.Content ?? string.Empty, text ?? string.Empty, StringComparison.Ordinal);
                        // Compare attachments
                        var pending = ctx.PendingAttachments ?? new List<AttachedFile>();
                        var origAtt = orig.Attachments ?? new List<AttachedFile>();
                        bool sameAtt = AreAttachmentsEqual(origAtt, pending);
                        // Compare model (changed model should allow resend/reset)
                        string modelAtEditStart = ctx.PendingEditOriginalModel ?? ctx.SelectedModel;
                        string currentModel = ctx.SelectedModel;
                        bool sameModel = string.Equals(modelAtEditStart ?? string.Empty, currentModel ?? string.Empty, StringComparison.Ordinal);
                        if (sameText && sameAtt && sameModel)
                        {
                            // Nothing changed: exit edit mode and restore to un-edited state
                            try { CancelEditingAndRestoreConversation(); }
                            catch { }
                            return;
                        }

                        // Confirm reset
                        var dr2 = MessageBox.Show(this,
                            "You are editing a previous user message. The conversation will be reset back to that point. Continue?",
                            "Reset Conversation?", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                        if (dr2 != DialogResult.OK) return;

                        try
                        {
                            // Replace content and attachments, then truncate after
                            orig.Content = text;
                            if (pending != null && pending.Count > 0)
                            {
                                orig.Attachments = new List<AttachedFile>();
                                for (int i = 0; i < pending.Count; i++)
                                {
                                    var af = pending[i]; if (af == null) continue;
                                    // Clone (not new AttachedFile(name, content)) so binary
                                    // payloads (images/PDFs) and Kind survive an edit/resend.
                                    orig.Attachments.Add(af.Clone());
                                }
                            }
                            else
                            {
                                orig.Attachments = null;
                            }

                            while (ctx.Conversation.History.Count > cutIndex + 1)
                                ctx.Conversation.History.RemoveAt(ctx.Conversation.History.Count - 1);

                            // Rebuild transcript UI from truncated conversation (batched)
                            ctx.Transcript.ClearMessages();
                            ctx.Transcript.BeginBatchUpdates();
                            try
                            {
                                foreach (var m in ctx.Conversation.History)
                                {
                                    if (string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                                        ctx.Transcript.AddMessage(MessageRole.Assistant, m.Content);
                                    else if (string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (m.Attachments != null && m.Attachments.Count > 0)
                                            ctx.Transcript.AddMessage(MessageRole.User, m.Content, m.Attachments);
                                        else
                                            ctx.Transcript.AddMessage(MessageRole.User, m.Content);
                                    }
                                }
                            }
                            finally
                            {
                                ctx.Transcript.EndBatchUpdates(true);
                            }

                            // Reset edit state and mark as edit resend path
                            ctx.PendingEditActive = false; ctx.PendingEditIndex = -1; ctx.PendingEditOriginalModel = null; isEditResend = true;
                            // Clear pending attachments UI for a clean send path
                            ClearAttachmentsBanner();
                        }
                        catch { }
                    }
                }
            }

            // For normal sends, block empty input; allow empty when re-sending an edit (conversation already has the message)
            if (!isEditResend && text.Length == 0) return;

            // Ensure client configured before we lock sending
            if (_client == null || !_client.IsConfigured)
            {
                string reason = _client == null
                    ? "Client not initialized."
                    : (!System.IO.File.Exists(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Lib\\curl.exe"))
                        ? "curl.exe could not be found."
                        : "Missing API key in settings.");
                // Show error as assistant bubble (no placeholder first)
                ctx.Transcript.AddMessage(MessageRole.Assistant, "Error: " + reason);
                Logger.Log("Send", "Blocked: " + reason);
                return;
            }

            // Lock sending immediately to avoid duplicate sends from rapid clicks/Enter
            ctx.IsSending = true;
            ctx.SendDetached = false; // this send belongs to the live conversation on this tab
            // Fresh cancellation handle for this turn, and show the in-flight status strip (covers both
            // the plain stream below and the tool-loop path in BeginToolSend, including the wait before
            // the first token while the model thinks / assembles a long tool call).
            ctx.Cancellation = new RequestCancellation();
            ShowGenerationBar(ctx);

            try
            {
                Logger.Log("Send", "Begin send. Model=" + GetSelectedModel());

                // Snapshot pending attachments, but do not append into stored history content
                List<AttachedFile> attachmentsSnapshot = null;
                string textForTranscript = baseText; // UI shows only user-entered text
                if (ctx.PendingAttachments != null && ctx.PendingAttachments.Count > 0)
                {
                    attachmentsSnapshot = new List<AttachedFile>(ctx.PendingAttachments.Count);
                    for (int i = 0; i < ctx.PendingAttachments.Count; i++)
                    {
                        var af = ctx.PendingAttachments[i];
                        if (af == null) continue;
                        // Clone so the snapshot keeps binary payloads (images/PDFs) and Kind.
                        attachmentsSnapshot.Add(af.Clone());
                    }
                }
                if (!isEditResend)
                {
                    // Normal path: append a new user message to transcript and history
                    if (attachmentsSnapshot != null && attachmentsSnapshot.Count > 0)
                        ctx.Transcript.AddMessage(MessageRole.User, textForTranscript, attachmentsSnapshot);
                    else
                        ctx.Transcript.AddMessage(MessageRole.User, textForTranscript);
                    // A slash command's hidden system context (e.g. /use-skill's skill body) is committed to
                    // history right before the user message - so it's never orphaned if an earlier path
                    // returned. It is sent to the model but not rendered (RebuildTranscript skips system).
                    if (!string.IsNullOrEmpty(pendingSystemContext))
                        ctx.Conversation.History.Add(new ChatMessage("system", pendingSystemContext));
                    // Store in conversation history
                    ctx.Conversation.AddUserMessage(baseText);
                    try
                    {
                        if (attachmentsSnapshot != null && attachmentsSnapshot.Count > 0)
                        {
                            var last = ctx.Conversation.History.Count > 0 ? ctx.Conversation.History[ctx.Conversation.History.Count - 1] : null;
                            if (last != null && string.Equals(last.Role, "user", StringComparison.OrdinalIgnoreCase))
                                last.Attachments = attachmentsSnapshot;
                        }
                    }
                    catch { }
                }
                ctx.Conversation.SelectedModel = ctx.SelectedModel;
                // If this tab was a help template, convert to a standard conversation id now
                if (ctx.NoSaveUntilUserSend)
                {
                    try
                    {
                        if (IsHelpConversationId(ctx.Conversation.Id))
                        {
                            // Clear id so a new standard id is generated on save
                            ctx.Conversation.Id = null;
                            ConversationStore.EnsureConversationId(ctx.Conversation);
                            // Update sidebar tracking to reflect new id
                            if (_sidebarManager != null) _sidebarManager.TrackOpenConversation(ctx.Conversation.Id, ctx.Page);
                        }
                    }
                    catch { }
                    ctx.NoSaveUntilUserSend = false;
                }
                ConversationStore.Save(ctx.Conversation); // save when a new user message starts/continues a convo
                // (No explicit open-by-id tracking here: the tab was registered when its conversation was
                // assigned - see TabManager.WireConversationTracking - under the same id it saves with.)
                Logger.Log("Send", "User message added. HistoryCount=" + ctx.Conversation.History.Count);
                if (_inputManager != null)
                {
                    _inputManager.ClearInput();
                    // Ensure the input panel is sized correctly immediately after send
                    try { _inputManager.AdjustInputBoxHeight(); }
                    catch { }
                }
                ClearAttachmentsBanner();

                StartModelTurn(ctx);
            }
            catch
            {
                Logger.Log("Send", "Send failed unexpectedly; unlocking.");
                ctx.IsSending = false;
                ctx.Cancellation = null;
                HideGenerationBar(ctx);
                throw;
            }
        }

        // Kick off the model turn for the conversation's current history: routes to the tool loop
        // when MCP tools or skills apply, otherwise streams a plain completion into a new assistant
        // bubble. Callers own the send lock — ctx.IsSending is already true, ctx.Cancellation is
        // fresh and the generation bar is showing. Used by the normal send path and by
        // RetryLastTurn, which re-runs the turn over unchanged history after a failure.
        private void StartModelTurn(TabManager.ChatTabContext ctx)
        {
            var modelToUse = GetSelectedModel();

            // Effective ZDR for this send (global default OR the per-conversation toggle); engages
            // the one-way latch on the first ZDR send and tags the message bubbles just added.
            bool zdrForSend = ResolveZdrForSend(ctx);
            if (zdrForSend) MarkActiveTurnZdrBubbles(ctx);

            // Tool-enabled turn: tool activity renders as a separate chrome-less message above the
            // answer bubble. BeginToolSend owns the whole turn; the plain path below is unchanged.
            // Skills also route through the tool loop (they inject a manifest and expose open_skill),
            // so a conversation with skills but no MCP tools still takes this path.
            bool hasMcpTools = _mcpRegistry != null && _mcpRegistry.HasToolsForWorkdir(ctx.WorkingDir);
            // Folderless conversations can still route through the tool loop for the command server in a
            // scratch dir (opt-in). The scratch server may not be connected yet at this point - it is
            // launched in BeginToolSend - so gate on the feature/settings, not the live registry.
            bool scratchCommand = string.IsNullOrEmpty(ctx.WorkingDir)
                && ctx.Conversation != null && ScratchWorkspace.IsEnabled();
            if (hasMcpTools || ConversationHasSkills(ctx) || scratchCommand)
            {
                BeginToolSend(ctx, modelToUse, zdrForSend);
                return;
            }

            // The turn's conversation, snapshotted NOW (UI thread, send start). Closing the last tab
            // recycles the page and REPLACES ctx.Conversation with a fresh one mid-flight; this turn
            // must keep appending to - and finally save - the conversation it was started for, and
            // must stop painting into the recycled tab's transcript (#141).
            Conversation convo = ctx.Conversation;

            // Add placeholder assistant message to stream into and capture its index
            int assistantIndex = ctx.Transcript.AddMessageGetIndex(MessageRole.Assistant, string.Empty);
            if (zdrForSend) ctx.Transcript.SetMessageZdrTag(assistantIndex, true);
            Logger.Log("Send", "Assistant placeholder index=" + assistantIndex);
            var assistantBuilder = new StringBuilder();
            // Capture this turn's cancellation handle for the streaming callbacks (the field is
            // cleared to null when the turn finalizes).
            RequestCancellation cancel = ctx.Cancellation;

            // Throttle UI updates with a WinForms timer (coalesces rapid deltas)
            var sbLock = new object();
            int lastRenderedLen = 0;
            var renderTimer = new System.Windows.Forms.Timer();
            renderTimer.Interval = 75; // ~13 fps; adjust between 50-150ms if needed
            renderTimer.Tick += (s2, e2) =>
            {
                // The recycled tab's transcript is not this turn's to draw on (the placeholder
                // index points into a cleared list).
                if (!ReferenceEquals(ctx.Conversation, convo)) return;
                string snapshot;
                int len;
                lock (sbLock)
                {
                    len = assistantBuilder.Length;
                    if (len == lastRenderedLen) return; // nothing new
                    snapshot = assistantBuilder.ToString();
                    lastRenderedLen = len;
                }
                // Update on UI thread (timer runs on UI thread)
                ctx.Transcript.UpdateMessageAt(assistantIndex, snapshot);
            };
            // Keep view pinned to bottom while streaming
            try { ctx.Transcript.StickToBottomDuringStreaming = true; }
            catch { }
            renderTimer.Start();

            // Kick off streaming in background
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    // Snapshot the history and log it
                    // Build a model snapshot where attachments are appended to content on-the-fly.
                    // Pass the prior turn's prompt tokens + model context length so §10 prune-with-
                    // placeholder can shed oldest binary when the replayed set grows too large.
                    int ctxLenSnap = 0;
                    if (!string.IsNullOrEmpty(modelToUse))
                        ModelCatalogService.TryGetContextLength(modelToUse, out ctxLenSnap);
                    int priorTokensSnap = (convo != null) ? convo.GetUsageStats().LastPromptTokens : 0;
                    var snapshot = BuildMessagesForModel(convo.History, modelToUse, zdrForSend, priorTokensSnap, ctxLenSnap);
                    // Prompt caching: the newest message carries the cache breakpoint, so each
                    // turn re-reads the prior turn's prefix and extends it. Snapshot messages are
                    // request-local clones - the flag never lands in persisted history.
                    if (snapshot.Count > 0)
                        snapshot[snapshot.Count - 1].CacheControl = true;
                    try
                    {
                        var sbSnap = new StringBuilder();
                        sbSnap.Append("Sending ").Append(snapshot.Count).Append(" messages:\n");
                        for (int i = 0; i < snapshot.Count; i++)
                        {
                            var m = snapshot[i];
                            string content = m.Content ?? string.Empty;
                            content = content.Replace("\r", " ").Replace("\n", " ");
                            if (content.Length > 200) content = content.Substring(0, 200) + "...";
                            sbSnap.Append("  ").Append(i).Append(": ").Append(m.Role).Append(" | ").Append(content).Append('\n');
                        }
                        Logger.Log("Send", sbSnap.ToString());
                    }
                    catch { }

                    var sendProps = new ClientProperties { Stream = true, Zdr = zdrForSend ? true : (bool?)null };
                    // Sticky provider routing on cache-supported models: prefer (provider.order,
                    // preference with fallback) the endpoint holding this conversation's warm
                    // prompt cache. Stickiness is confirmation-gated - only a response reporting
                    // cache activity (a read or a write) sets or moves the preference; merely
                    // serving a request proves nothing. Usage/cost accounting (status bar)
                    // records on every model.
                    Conversation usageConvo = convo;
                    bool cachingModel = OpenRouterClient.ModelSupportsPromptCaching(modelToUse);
                    if (usageConvo != null)
                    {
                        if (cachingModel && !string.IsNullOrEmpty(usageConvo.CacheWarmProvider))
                            sendProps.ProviderOrder = new List<string> { usageConvo.CacheWarmProvider };
                        sendProps.ResponseUsageCallback = delegate(ResponseUsage u)
                        {
                            if (u == null) return;
                            if (cachingModel && !string.IsNullOrEmpty(u.Provider)
                                && (u.CachedTokens > 0 || u.CacheWriteTokens > 0))
                                usageConvo.CacheWarmProvider = u.Provider;
                            RecordUsageAndReconcile(usageConvo, u, cachingModel);
                        };
                    }
                    _client.CreateCompletionStream(
                        modelToUse,
                        snapshot,
                        sendProps,
                        delegate(string d)
                        {
                            if (string.IsNullOrEmpty(d)) return;
                            lock (sbLock) { assistantBuilder.Append(d); }
                            // no per-delta BeginInvoke; timer will render
                        },
                        delegate
                        {
                            // finalize on UI thread (update history and unlock send)
                            string finalText;
                            lock (sbLock) { finalText = assistantBuilder.ToString(); }
                            BeginInvoke((MethodInvoker)delegate
                            {
                                try { renderTimer.Stop(); renderTimer.Dispose(); }
                                catch { }
                                // Detached = the tab was recycled mid-flight: the transcript is not
                                // this turn's to touch, but the response still belongs in the turn's
                                // conversation (and its file).
                                bool attached = ReferenceEquals(ctx.Conversation, convo);
                                if (attached)
                                {
                                    try { ctx.Transcript.StickToBottomDuringStreaming = false; }
                                    catch { }
                                }
                                // A user stop ends the stream via onDone (no error). If nothing
                                // streamed yet, drop the empty placeholder rather than committing an
                                // empty assistant message; otherwise keep the partial text as normal.
                                bool cancelled = (cancel != null && cancel.IsCancelled);
                                if (cancelled && finalText.Trim().Length == 0)
                                {
                                    if (attached && assistantIndex == ctx.Transcript.MessageCount - 1)
                                        ctx.Transcript.RemoveLastMessage();
                                    Logger.Log("Send", "Stream stopped by user before any output at index=" + assistantIndex);
                                }
                                else
                                {
                                    if (attached) ctx.Transcript.UpdateMessageAt(assistantIndex, finalText);
                                    convo.AddAssistantMessage(finalText);
                                    if (attached)
                                    {
                                        convo.SelectedModel = ctx.SelectedModel;
                                        // Save assistant completion if allowed
                                        if (!ctx.NoSaveUntilUserSend)
                                            ConversationStore.Save(convo); // save only after streaming completes
                                    }
                                    else
                                    {
                                        SaveDetachedTurnConversation(convo);
                                    }
                                    try { Logger.Log("Transcript", "Final assistant message at index=" + assistantIndex + ":\n" + (finalText ?? string.Empty)); }
                                    catch { }
                                    Logger.Log("Send", "Assistant finalized at index=" + assistantIndex + ", chars=" + (finalText != null ? finalText.Length : 0) + (cancelled ? " (stopped by user)" : string.Empty));
                                }
                                ctx.IsSending = false;
                                ctx.Cancellation = null;
                                HideGenerationBar(ctx);
                                if (_sidebarManager != null) _sidebarManager.RefreshSidebarList();
                            });
                        },
                        delegate(string err)
                        {
                            if (string.IsNullOrEmpty(err)) err = "Unknown error.";
                            string partial;
                            lock (sbLock) { partial = assistantBuilder.ToString(); }
                            BeginInvoke((MethodInvoker)delegate
                            {
                                try { renderTimer.Stop(); renderTimer.Dispose(); }
                                catch { }
                                // Transcript writes only while the tab still hosts this turn's
                                // conversation (see the success path).
                                if (ReferenceEquals(ctx.Conversation, convo))
                                {
                                    try { ctx.Transcript.StickToBottomDuringStreaming = false; }
                                    catch { }
                                    // Keep any partial text in the assistant bubble; otherwise drop the
                                    // empty placeholder so only the red error notice shows.
                                    if (partial.Trim().Length > 0)
                                        ctx.Transcript.UpdateMessageAt(assistantIndex, partial);
                                    else if (assistantIndex == ctx.Transcript.MessageCount - 1)
                                        ctx.Transcript.RemoveLastMessage();
                                    ShowTranscriptError(ctx.Transcript, err);
                                }
                                Logger.Log("Send", "Stream error at index=" + assistantIndex + ": " + err);
                                ctx.IsSending = false;
                                ctx.Cancellation = null;
                                HideGenerationBar(ctx);
                                // don't save on failure; no new assistant content
                            });
                        },
                        cancel
                    );
                }
                catch (Exception ex)
                {
                    string partial;
                    lock (sbLock) { partial = assistantBuilder.ToString(); }
                    BeginInvoke((MethodInvoker)delegate
                    {
                        try { renderTimer.Stop(); renderTimer.Dispose(); }
                        catch { }
                        // Transcript writes only while the tab still hosts this turn's conversation.
                        if (ReferenceEquals(ctx.Conversation, convo))
                        {
                            try { ctx.Transcript.StickToBottomDuringStreaming = false; }
                            catch { }
                            if (partial.Trim().Length > 0)
                                ctx.Transcript.UpdateMessageAt(assistantIndex, partial);
                            else if (assistantIndex == ctx.Transcript.MessageCount - 1)
                                ctx.Transcript.RemoveLastMessage();
                            ShowTranscriptError(ctx.Transcript, ex.Message);
                        }
                        Logger.Log("Send", "Exception in streaming worker: " + ex.Message);
                        ctx.IsSending = false;
                        ctx.Cancellation = null;
                        HideGenerationBar(ctx);
                    });
                }
            });
        }

        // Re-run the turn that just failed (the Retry button on a trailing error notice). The failed
        // turn's input is already in history, so nothing is appended: the error notice is removed and
        // the turn restarts over the unchanged history — this holds for both the plain stream (the
        // user message is committed before the request) and the tool loop (the orchestrator's partial
        // progress stays in history, same as when the user keeps typing after a failure).
        private static string ConvId(TabManager.ChatTabContext ctx)
        {
            return (ctx != null && ctx.Conversation != null) ? ctx.Conversation.Id : null;
        }

        // Open the live, streaming viewer for a running child agent (the panel's per-row "View transcript",
        // tier 3 "watch live"). Modeless, so the user can keep watching while the fan-out continues (and
        // still hit "Stop agents"). A null stream means the child already finished and its stream was
        // dropped - fall back to a short notice.
        internal void OpenAgentLiveTranscript(AgentLiveStream stream)
        {
            if (stream == null)
            {
                MessageBox.Show(this, "That agent has finished. Open its transcript from the dispatch record below.",
                    "Agent transcript", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                AgentTranscriptViewerForm viewer = new AgentTranscriptViewerForm(stream);
                ShowAgentTranscriptWindow(viewer);
            }
            catch { }
        }

        // Open the read-only child-transcript viewer for a dispatch_agent "View transcript" link (tier 3).
        // The link encodes the dispatch record's key + agent slot; resolve it against the session cache. A
        // miss (cache evicted, or a record reloaded from history after restart) shows a short notice rather
        // than nothing, so the click is never silently dead.
        internal void OpenAgentTranscript(string url)
        {
            string key; int slot;
            if (!AgentTranscriptLinks.TryParse(url, out key, out slot)) return;
            AgentTranscript t = AgentTranscriptStore.Get(key, slot);
            if (t == null)
            {
                MessageBox.Show(this,
                    "This agent's transcript is no longer available (it is kept only for the current session).",
                    "Agent transcript", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                AgentTranscriptViewerForm viewer = new AgentTranscriptViewerForm(t);
                ShowAgentTranscriptWindow(viewer);
            }
            catch { }
        }

        // Show a sub-agent transcript window the same way for both the live and finished viewers: modeless
        // (so the main form stays interactive - no modal "ding" when the user clicks it) and unowned (so the
        // main form can be brought back to the front instead of being pinned behind the transcript). Centered
        // on the main form by hand, since an unowned window ignores CenterParent. The form disposes itself
        // when closed, so there's no using/Dispose to manage here.
        private void ShowAgentTranscriptWindow(AgentTranscriptViewerForm viewer)
        {
            if (viewer == null) return;
            try
            {
                Rectangle b = this.Bounds;
                Size s = viewer.Size;
                viewer.Location = new Point(
                    b.Left + Math.Max(0, (b.Width - s.Width) / 2),
                    b.Top + Math.Max(0, (b.Height - s.Height) / 2));
            }
            catch { }
            viewer.Show();
        }

        internal void RetryLastTurn(TabManager.ChatTabContext ctx)
        {
            if (ctx == null || ctx.Conversation == null || ctx.Transcript == null) return;
            if (ctx.IsSending) return;
            if (ctx.Conversation.History == null || LastUserHistoryIndex(ctx.Conversation) < 0)
                return; // no turn to re-run

            if (_client == null || !_client.IsConfigured)
            {
                string reason = _client == null
                    ? "Client not initialized."
                    : (!System.IO.File.Exists(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Lib\\curl.exe"))
                        ? "curl.exe could not be found."
                        : "Missing API key in settings.");
                ctx.Transcript.AddMessage(MessageRole.Assistant, "Error: " + reason);
                Logger.Log("Send", "Retry blocked: " + reason);
                return;
            }

            // Drop the trailing error notice so the retried turn streams in its place; a repeat
            // failure surfaces a fresh notice (with a fresh Retry button).
            ctx.Transcript.RemoveTrailingErrorNotices();

            ctx.IsSending = true;
            ctx.SendDetached = false; // this send belongs to the live conversation on this tab
            ctx.Cancellation = new RequestCancellation();
            ShowGenerationBar(ctx);
            try
            {
                Logger.Log("Send", "Retry turn. Model=" + GetSelectedModel());
                StartModelTurn(ctx);
            }
            catch
            {
                Logger.Log("Send", "Retry failed unexpectedly; unlocking.");
                ctx.IsSending = false;
                ctx.Cancellation = null;
                HideGenerationBar(ctx);
                throw;
            }
        }

        // ---------------- ZDR (zero data retention) per-conversation plumbing ----------------

        // Effective ZDR for the active conversation's next send: the global default OR the per-tab
        // toggle. On the first send where ZDR is effective, engages the one-way latch (recording the
        // history index of the user message being sent) so the checkbox locks on and the tab + every
        // message from here on are marked ZDR. Returns whether ZDR applies to this send.
        private bool ResolveZdrForSend(TabManager.ChatTabContext ctx)
        {
            if (ctx == null || ctx.Conversation == null) return false;
            // The per-conversation toggle (seeded from the global default at creation) is the source of
            // truth; a latched conversation stays ZDR for good. The global default is not re-applied here,
            // so unchecking the box before the first send genuinely disables ZDR for this conversation.
            bool zdr = ctx.Conversation.Zdr || ConvIsZdrLatched(ctx);
            if (!zdr) return false;

            if (ctx.Conversation.ZdrFirstMessageIndex < 0)
            {
                int idx = LastUserHistoryIndex(ctx.Conversation);
                ctx.Conversation.ZdrFirstMessageIndex =
                    idx >= 0 ? idx : Math.Max(0, ctx.Conversation.History.Count - 1);
                try { if (!ctx.NoSaveUntilUserSend) ConversationStore.Save(ctx.Conversation); }
                catch { }
                // Reflect the now-latched state: lock the checkbox and mark the tab + sidebar.
                try { SyncZdrCheckboxFromActiveTab(); }
                catch { }
                try { UpdateTabTitleForZdr(ctx); }
                catch { }
                try { if (_sidebarManager != null) _sidebarManager.RefreshSidebarList(); }
                catch { }
            }
            return true;
        }

        private static int LastUserHistoryIndex(Conversation convo)
        {
            var h = convo.History;
            for (int i = h.Count - 1; i >= 0; i--)
                if (h[i] != null && string.Equals(h[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        // True once a conversation has latched ZDR: from that point every user/assistant message is a
        // zero-retention message (the checkbox is locked on and ZDR never turns back off).
        private static bool ConvIsZdrLatched(TabManager.ChatTabContext ctx)
        {
            return ctx != null && ctx.Conversation != null && ctx.Conversation.ZdrFirstMessageIndex >= 0;
        }

        // Tags the user bubble just added for this turn (assistant bubbles are tagged as they
        // materialize). No-op when the conversation isn't ZDR-latched.
        private void MarkActiveTurnZdrBubbles(TabManager.ChatTabContext ctx)
        {
            if (ctx == null || ctx.Transcript == null) return;
            try { ctx.Transcript.MarkLastUserMessageZdr(); }
            catch { }
        }

        // Marker prefixed to a tab title / sidebar row once a conversation has latched ZDR.
        internal const string ZdrTitlePrefix = "[zdr] ";

        // The display title for a conversation: its name, prefixed with the ZDR marker once latched.
        internal static string ZdrTitle(Conversation convo, string name)
        {
            string n = string.IsNullOrEmpty(name) ? "Conversation" : name;
            return (convo != null && convo.ZdrFirstMessageIndex >= 0) ? ZdrTitlePrefix + n : n;
        }

        // Recompute the active tab's title to reflect a newly-latched ZDR state.
        private void UpdateTabTitleForZdr(TabManager.ChatTabContext ctx)
        {
            if (ctx == null || ctx.Page == null || ctx.Conversation == null) return;
            try { ctx.Page.Text = ZdrTitle(ctx.Conversation, ctx.Conversation.Name); }
            catch { }
        }

        // Owner-draw the model combo so only the model name shows (after the last "/"), while the item
        // value stays the full "author/model" id.
        private void cmbModel_DrawItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index >= 0 && this.cmbModel != null && e.Index < this.cmbModel.Items.Count)
            {
                string full = Convert.ToString(this.cmbModel.Items[e.Index]);
                // Clip the name at the edge like a native combo (no ellipsis).
                TextRenderer.DrawText(e.Graphics, ShortModelName(full), e.Font, e.Bounds, e.ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
            e.DrawFocusRectangle();
        }

        // "author/model-name" -> "model-name"; passes through anything without a slash.
        internal static string ShortModelName(string full)
        {
            if (string.IsNullOrEmpty(full)) return full ?? string.Empty;
            int slash = full.LastIndexOf('/');
            return (slash >= 0 && slash < full.Length - 1) ? full.Substring(slash + 1) : full;
        }

        // Per-tab ZDR checkbox <-> active conversation. Checked = the conversation's own ZDR toggle
        // (seeded from the global default when the conversation was created), or locked on once latched.
        // Disabled only when the conversation has latched ZDR — the global default no longer locks the
        // box, so the user can uncheck it before the first send.
        private bool _syncingZdr;
        private void SyncZdrCheckboxFromActiveTab()
        {
            if (this.chkZdrTab == null) return;
            var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
            bool latched = ConvIsZdrLatched(ctx);
            bool convZdr = ctx != null && ctx.Conversation != null && ctx.Conversation.Zdr;
            _syncingZdr = true;
            try
            {
                this.chkZdrTab.Checked = convZdr || latched;
                this.chkZdrTab.Enabled = ctx != null && ctx.Conversation != null && !latched;
            }
            finally { _syncingZdr = false; }
        }

        private void chkZdrTab_CheckedChanged(object sender, EventArgs e)
        {
            if (_syncingZdr) return;
            var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (ctx == null || ctx.Conversation == null) return;
            ctx.Conversation.Zdr = this.chkZdrTab.Checked;
            // Persist only for an already-saved conversation; a brand-new empty tab persists the choice
            // on its first send (the in-memory flag is still honored until then).
            try
            {
                if (!ctx.NoSaveUntilUserSend && ctx.Conversation.History != null && ctx.Conversation.History.Count > 0)
                    ConversationStore.Save(ctx.Conversation);
            }
            catch { }
        }

        // True when this conversation has at least one ENABLED skill (bundled or project). Used to route
        // the turn through the tool loop even with no MCP tools, since skills inject a manifest and expose
        // open_skill there. Cheap (a couple of directory scans + skills.json); BeginToolSend re-resolves
        // the enabled set it actually uses, so this is just the gate.
        private static bool ConversationHasSkills(TabManager.ChatTabContext ctx)
        {
            try
            {
                Conversation convo = (ctx != null) ? ctx.Conversation : null;
                return SkillInjection.HasAnyEnabledSkills(
                    AppDomain.CurrentDomain.BaseDirectory, (ctx != null) ? ctx.WorkingDir : null,
                    convo != null ? convo.SkillsFeatureOff : null,
                    convo != null ? convo.SkillOverrides : null);
            }
            catch { return false; }
        }

        // Owns a tool-enabled chat turn. Tool activity streams into a chrome-less "Tool" message and
        // the model's answer into a separate Assistant bubble below it. Both bubbles are created
        // lazily (in the render timer) so they appear in the order content arrives — tool calls
        // first, then the answer. Runs the orchestrator on a worker thread; it appends the structured
        // assistant/tool messages to history itself. Called on the UI thread.
        // One ordered segment of a streaming tool turn: either an assistant-text bubble or a
        // chrome-less tool-activity block. Text is appended on the orchestrator worker thread (under
        // the turn's lock); Index/LastLen are assigned on the UI thread when first rendered.
        private sealed class LiveSeg
        {
            public readonly MessageRole Role;
            public readonly StringBuilder Text = new StringBuilder();
            public int Index;   // transcript message index, -1 until materialized
            public int LastLen; // last rendered text length (UI thread only)
            public LiveSeg(MessageRole role) { Role = role; Index = -1; LastLen = -1; }
        }

        private void BeginToolSend(TabManager.ChatTabContext ctx, string model, bool zdr)
        {
            // The turn's conversation, snapshotted NOW (UI thread, send start). Closing the last tab
            // recycles the page and REPLACES ctx.Conversation with a fresh one mid-flight; this turn
            // must keep appending to - and finally save - the conversation it was started for, and
            // must stop painting into the recycled tab's transcript (#141).
            Conversation convo = ctx.Conversation;

            // Ordered transcript segments for this turn: assistant-text bubbles and chrome-less
            // tool-activity blocks, interleaved in arrival order so the view reads chronologically
            // (text, then the tools it triggered, then the next text, ...). A segment only becomes a
            // real transcript message once it has content, so a turn that errors before producing any
            // output never leaves an empty placeholder bubble behind.
            var sbLock = new object();
            var segs = new List<LiveSeg>();

            var renderTimer = new System.Windows.Forms.Timer();
            renderTimer.Interval = 75;
            // Stop materializing once the tab no longer hosts this turn's conversation (recycled):
            // unmaterialized segments would otherwise ADD bubbles to the new blank transcript.
            renderTimer.Tick += delegate
            {
                if (ReferenceEquals(ctx.Conversation, convo)) MaterializeLiveSegs(ctx, sbLock, segs);
            };
            try { ctx.Transcript.StickToBottomDuringStreaming = true; }
            catch { }
            renderTimer.Start();

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    // Make sure THIS conversation's folder has its own scoped server set before the
                    // turn runs (idempotent; no-op if already connected), and route the turn's tool
                    // calls to that folder so a concurrent turn in another tab can't interfere. A
                    // folderless conversation with the opt-in scratch-command feature instead gets a
                    // per-conversation scratch dir (command server only); compute it once here (creates
                    // the dir + ensures the id), launch its command-only set, and route the turn at it.
                    string scratchDir = ScratchDirForContext(ctx, true);
                    if (_mcpHost != null)
                    {
                        if (!string.IsNullOrEmpty(ctx.WorkingDir))
                            _mcpHost.EnsureWorkingDir(ctx.WorkingDir);
                        else if (!string.IsNullOrEmpty(scratchDir))
                            _mcpHost.EnsureScratchDir(scratchDir);
                    }

                    // Per-turn approval policy bound to THIS tab's panel, so the prompt appears on the
                    // conversation that requested the tool. The remembered-choice store is shared
                    // across tabs. Falls back to allow-all only if approvals couldn't be set up.
                    // The panel resolver yields null once the tab no longer hosts this turn's
                    // conversation (recycled mid-flight): a detached turn's prompts AUTO-DENY instead
                    // of appearing on the new blank tab, and the turn wraps up and saves.
                    IToolApprovalPolicy approval = (_approvalStore != null && ctx.ApprovalPanel != null)
                        ? new ToolApprovalPolicy(
                            new ToolClassifier(),
                            new TranscriptApprovalPrompt(this, delegate
                            {
                                // Null (=> deny) when the tab was recycled away from this turn's
                                // conversation, or closed outright (panel disposed with the page).
                                ToolApprovalPanel p = ctx.ApprovalPanel;
                                bool usable = ReferenceEquals(ctx.Conversation, convo)
                                    && p != null && !p.IsDisposed;
                                return usable ? p : null;
                            }),
                            _approvalStore,
                            _mcpRegistry) // classify third-party tools by their declared readOnly/destructive hints
                        : (IToolApprovalPolicy)new AllowAllApprovalPolicy();
                    // Scope this turn's "all edits in this workspace" approvals to the active folder
                    // (same workdir the orchestrator routes tool calls to, below).
                    ToolApprovalPolicy realPolicy = approval as ToolApprovalPolicy;
                    if (realPolicy != null) realPolicy.WorkingDir = ctx.WorkingDir;
                    var orch = new McpChatOrchestrator(_client, _mcpRegistry, approval,
                                                       model, LoggerSink.Instance);
                    orch.WorkingDir = ctx.WorkingDir;
                    // Route scoped-tool resolution at the scratch dir when folderless (null otherwise);
                    // the prompt's workspace block stays absent and a scratch-sandbox note is used instead.
                    orch.ScratchWorkingDir = string.IsNullOrEmpty(ctx.WorkingDir) ? scratchDir : null;
                    orch.Zdr = zdr ? true : (bool?)null;
                    // Reveal state is per-conversation (recency-ordered; persisted with the
                    // conversation) so concurrent tabs don't churn each other's tools array - the
                    // array must stay byte-stable across requests for prompt caching to hit.
                    Conversation revealConvo = convo;
                    if (revealConvo != null)
                    {
                        if (revealConvo.RevealedTools == null)
                            revealConvo.RevealedTools = new List<string>();
                        orch.RevealedToolNames = revealConvo.RevealedTools;
                        // Sticky provider routing: seed the orchestrator with the provider that
                        // last demonstrated a cache hit for this conversation, and persist any
                        // newly confirmed one, so requests keep landing on the endpoint holding
                        // the warm prompt cache (the orchestrator gates updates on cached > 0).
                        orch.PreferredProvider = revealConvo.CacheWarmProvider;
                        orch.ProviderServed = delegate(string served)
                        { revealConvo.CacheWarmProvider = served; };
                        // Per-response usage/cost accounting for the status bar (every iteration),
                        // with post-stream reconciliation against the billed generation record.
                        bool cachingForUsage = OpenRouterClient.ModelSupportsPromptCaching(model);
                        orch.UsageReported = delegate(ResponseUsage u)
                        { RecordUsageAndReconcile(revealConvo, u, cachingForUsage); };
                    }
                    // Stop button cancellation: kills the in-flight model stream and lets the loop bail
                    // out cleanly between steps. Set once before the turn runs (read-only thereafter).
                    orch.Cancellation = ctx.Cancellation;
                    // At the tool-call cap, confirm in-transcript (like a tool-approval prompt) whether
                    // to continue: Continue grants another batch, Stop has the model wrap up and ask how
                    // to proceed. Needs this tab's panel to host the prompt; without one the orchestrator
                    // wraps up by default.
                    if (ctx.ApprovalPanel != null)
                    {
                        var contPrompt = new TranscriptContinuationPrompt(this, delegate
                        {
                            // Same gate as the approval prompt: null (=> stop/wrap up) once detached.
                            ToolApprovalPanel p = ctx.ApprovalPanel;
                            bool usable = ReferenceEquals(ctx.Conversation, convo)
                                && p != null && !p.IsDisposed;
                            return usable ? p : null;
                        });
                        orch.ContinuationDecider = delegate(int n) { return contPrompt.Ask(n); };
                    }
                    // ask_user: expose the question tool, routed to this tab's QuestionPanel. The panel
                    // resolver uses the same recycled-tab guard as the approval prompt - it yields null
                    // once the tab no longer hosts this turn's conversation (or the panel is gone), so a
                    // detached turn's question resolves as dismissed instead of appearing on a blank tab.
                    if (ctx.QuestionPanel != null)
                    {
                        orch.AskUser = new AskUserTool(new TranscriptQuestionPrompt(this, delegate
                        {
                            QuestionPanel qp = ctx.QuestionPanel;
                            bool usable = ReferenceEquals(ctx.Conversation, convo)
                                && qp != null && !qp.IsDisposed;
                            return usable ? qp : null;
                        }));
                    }
                    string modelForTransform = model; // capture for transform closure
                    bool zdrForTransform = zdr;       // capture ZDR for the transform closure
                    // §10 prune inputs: captured once for the turn (LastPromptTokens only updates after
                    // the turn completes, so it's stable across the tool loop's iterations).
                    int ctxLenForTransform = 0;
                    if (!string.IsNullOrEmpty(modelForTransform))
                        ModelCatalogService.TryGetContextLength(modelForTransform, out ctxLenForTransform);
                    int priorTokensForTransform = (convo != null) ? convo.GetUsageStats().LastPromptTokens : 0;
                    orch.RequestMessageTransform = delegate(IList<ChatMessage> h)
                    {
                        List<ChatMessage> asList = h as List<ChatMessage>;
                        if (asList == null) asList = new List<ChatMessage>(h);
                        return BuildMessagesForModel(asList, modelForTransform, zdrForTransform, priorTokensForTransform, ctxLenForTransform);
                    };
                    // AGENTS.md: inject the workspace root's project instructions into the stable
                    // head (Zone A - cached, so a large file bills once per conversation). Read
                    // once per send, here on the worker thread, so the block stays byte-identical
                    // across the turn's loop iterations; an edit takes effect on the next turn.
                    orch.ProjectInstructions = AgentsFileInjection.Build(ctx.WorkingDir);

                    // Inject the workspace's persistent memory index (rebuilt from .gxpt/memory/memory.md each
                    // request) only when memory is enabled and this conversation has a workspace.
                    if (AppSettings.GetBool("mcp_memory_enabled") && !string.IsNullOrEmpty(ctx.WorkingDir))
                    {
                        string memWorkdir = ctx.WorkingDir;
                        orch.MemorySystemMessageProvider = delegate { return MemoryInjection.Build(memWorkdir); };
                    }

                    // Skills: discover bundled (<exe>/skills) + project (<workdir>/.gxpt/skills) skills for
                    // this turn, resolve which are enabled (global skills.json default + the per-conversation
                    // override layer), then inject the manifest and expose open_skill over
                    // the enabled set. Rebuilt per send, so on-disk edits take effect on the next turn.
                    SkillCatalog skillCatalog =
                        SkillRoots.BuildCatalog(AppDomain.CurrentDomain.BaseDirectory, ctx.WorkingDir);
                    Conversation skillConvo = convo;
                    List<Skill> enabledSkills = SkillResolve.EnabledSkills(
                        skillCatalog.Skills, SkillEnablement.LoadGlobal(),
                        skillConvo != null ? skillConvo.SkillsFeatureOff : null,
                        skillConvo != null ? skillConvo.SkillOverrides : null);
                    if (enabledSkills.Count > 0)
                    {
                        List<Skill> enabledForTurn = enabledSkills;
                        // Inventory list only; the static framing lives in the cached stable head.
                        orch.SkillsManifestSystemMessageProvider =
                            delegate { return SkillInjection.BuildList(enabledForTurn); };
                        // open_skill is enabled-scoped; read_skill_file spans the whole catalog.
                        orch.SkillTools = new SkillTools(enabledForTurn, skillCatalog);
                    }
                    // The skill-authoring tools are owned by the skill-writer meta-skill: omit them from
                    // context unless that skill is enabled for this conversation.
                    ICollection<string> hiddenTools = ExtensionsToolGate.HiddenTools(enabledSkills);
                    if (hiddenTools.Count > 0) orch.HiddenToolNames = hiddenTools;

                    // Sub-agents: when the agents feature is enabled (settings.json `agents_enabled`),
                    // discover all agents under <exe>/agents + <workdir>/.gxpt/agents + %AppData%/GxPT/agents
                    // and expose the manifest + dispatch_agent. Rebuilt per send, so on-disk edits take
                    // effect on the next turn. No per-agent enablement (design A15); the conversation
                    // override (/toggle-agents here) wins over the global settings.json default.
                    // Hoisted so onToolResult (below) can snapshot the most recent fan-out's child
                    // transcripts under the dispatch record's key for the tier-3 viewer.
                    AgentDispatcher dispatcherForTurn = null;
                    if (AgentEnablement.FeatureEnabled(convo != null ? convo.AgentsEnabled : null))
                    {
                        AgentCatalog agentCatalog =
                            AgentRoots.BuildCatalog(AppDomain.CurrentDomain.BaseDirectory, ctx.WorkingDir);
                        if (agentCatalog.Agents.Count > 0)
                        {
                            // tierOf reuses the approval policy's classification so an agent's max_tier
                            // ceiling matches the gate; the AllowAll fallback (no policy) leaves it null
                            // and the resolver treats every tool as Write.
                            Func<string, ToolTier> tierOf = null;
                            ToolApprovalPolicy tap = approval as ToolApprovalPolicy;
                            if (tap != null) tierOf = tap.TierOf;
                            // Runtime per-agent enablement: only offer agents whose required tools are
                            // actually available in this workspace (e.g. web-research needs the web tools),
                            // so the model never dispatches an agent that would resolve to nothing.
                            IList<string> parentNames = _mcpRegistry != null
                                ? _mcpRegistry.NamesForWorkdir(ctx.WorkingDir) : (IList<string>)new List<string>();
                            List<Agent> agentsForTurn = new List<Agent>();
                            for (int ai = 0; ai < agentCatalog.Agents.Count; ai++)
                                if (AgentAvailability.IsAvailable(agentCatalog.Agents[ai], parentNames, tierOf))
                                    agentsForTurn.Add(agentCatalog.Agents[ai]);
                            if (agentsForTurn.Count > 0)
                            {
                                // Inventory list only; the static framing lives in the cached stable head.
                                orch.AgentsManifestSystemMessageProvider =
                                    delegate { return AgentInjection.BuildList(agentsForTurn); };
                                AgentDispatcher dispatcher = new AgentDispatcher(
                                    agentsForTurn, _client, _mcpRegistry, approval, model, ctx.WorkingDir,
                                    LoggerSink.Instance, tierOf,
                                    McpChatOrchestrator.DefaultMaxIterations, McpChatOrchestrator.DefaultCallTimeoutMs);
                                dispatcher.Cancellation = ctx.Cancellation;
                                // Propagate the parent turn's privacy posture to every child request: a ZDR
                                // conversation must stay ZDR inside its sub-agents (the context firewall
                                // isolates the transcript, not the data-retention guarantee). Mirrors what
                                // the parent orchestrator was configured with above.
                                dispatcher.Zdr = orch.Zdr;
                                dispatcher.ProviderDataCollectionAllowed = orch.ProviderDataCollectionAllowed;
                                // Child usage adds to cost/token totals but must NOT move the parent's context
                                // gauge (the child has its own isolated context) - so it routes through the
                                // updateContextGauge=false overload, not orch.UsageReported.
                                dispatcher.UsageReported = delegate(ResponseUsage u)
                                {
                                    RecordUsageAndReconcile(revealConvo, u,
                                        OpenRouterClient.ModelSupportsPromptCaching(model), false);
                                };
                                // Observability + group cancel (design sec.14): a dedicated handle the panel's
                                // Stop button trips, so it cancels the agents (children watch GroupCancellation)
                                // without ending the turn - the parent loop resumes with the partial results.
                                RequestCancellation agentGroup = new RequestCancellation();
                                dispatcher.GroupCancellation = agentGroup;
                                dispatcher.ActivityUi = new AgentActivityUiBridge(this, ctx, agentGroup, dispatcher);
                                orch.AgentDispatcher = dispatcher;
                                dispatcherForTurn = dispatcher;
                            }
                        }
                    }

                    // Assistant text appends to the current assistant bubble; a tool call closes it so
                    // the next run of text starts a fresh bubble below the tool record. Inter-turn
                    // whitespace (a stray newline some models emit before the next tool call) must NOT
                    // open a bubble — otherwise it splits two adjacent tool blocks with an empty-looking
                    // gap instead of letting them merge into one tight block.
                    Action<string> onAppend = delegate(string t)
                    {
                        if (string.IsNullOrEmpty(t)) return;
                        lock (sbLock)
                        {
                            LiveSeg cur = (segs.Count > 0) ? segs[segs.Count - 1] : null;
                            if (cur != null && cur.Role == MessageRole.Assistant)
                            {
                                cur.Text.Append(t); // continuation — keep internal whitespace
                                return;
                            }
                            if (t.Trim().Length == 0) return; // don't open a bubble on whitespace alone
                            cur = new LiveSeg(MessageRole.Assistant);
                            segs.Add(cur);
                            cur.Text.Append(t);
                        }
                    };
                    // Tool activity renders in two beats: an immediate "Using <tool>" placeholder on the
                    // call (live feedback while it runs / the approval gate waits), replaced by the
                    // outcome marker on the result. So a denied call ends as "denied: <tool>" and an
                    // approved edit ends as its collapsible record — never an unapproved diff. The
                    // placeholder sits at the tail of the tool block (calls are serial and no model text
                    // streams between a call and its result), so we truncate back to it and append.
                    LiveSeg pendingToolSeg = null;
                    int pendingToolStart = 0;
                    Action<string> onToolCall = delegate(string name)
                    {
                        lock (sbLock)
                        {
                            LiveSeg cur = (segs.Count > 0) ? segs[segs.Count - 1] : null;
                            if (cur == null || cur.Role != MessageRole.Tool)
                            {
                                cur = new LiveSeg(MessageRole.Tool);
                                segs.Add(cur);
                            }
                            if (cur.Text.Length > 0) cur.Text.Append("\r\n");
                            pendingToolSeg = cur;
                            pendingToolStart = cur.Text.Length;
                            cur.Text.Append(McpMarkers.Call(name));
                        }
                    };
                    // argsJson is threaded through for files__edit etc., which render a collapsible
                    // record instead of the generic "using" marker; register the record (it has its own
                    // lock) before taking sbLock. The record key is the model's call id, so the live and
                    // reloaded views share one identity (and persisted agent transcripts resolve on reload).
                    ToolResultCallback onToolResult = delegate(string name, string argsJson, string resultText, bool isError, string callId)
                    {
                        string recKey = !string.IsNullOrEmpty(callId) ? callId : Guid.NewGuid().ToString("N");
                        // A dispatch_agent record gets per-agent "View transcript" links keyed by recKey;
                        // cache + persist this fan-out's child transcripts under the same key so the links
                        // resolve now and after reload. Tool calls are serial, so LastTranscripts is this
                        // call's batch.
                        if (dispatcherForTurn != null && AgentDispatcher.DispatchAgentName == name
                            && dispatcherForTurn.LastTranscripts != null)
                        {
                            AgentTranscriptStore.Put(recKey, dispatcherForTurn.LastTranscripts);
                            try { AgentTranscriptPersistence.Save(ConvId(ctx), recKey, dispatcherForTurn.LastTranscripts); }
                            catch { }
                        }
                        string marker = McpMarkers.IsDenied(resultText)
                            ? McpMarkers.Denied(name)
                            : EditDiffMarkerOrCall(ctx.Transcript, name, argsJson, recKey);
                        lock (sbLock)
                        {
                            if (pendingToolSeg != null)
                            {
                                // Replace the placeholder (at the tail) with the outcome marker.
                                if (pendingToolStart <= pendingToolSeg.Text.Length)
                                    pendingToolSeg.Text.Length = pendingToolStart;
                                pendingToolSeg.Text.Append(marker);
                                pendingToolSeg = null;
                            }
                            else
                            {
                                LiveSeg cur = (segs.Count > 0) ? segs[segs.Count - 1] : null;
                                if (cur == null || cur.Role != MessageRole.Tool)
                                {
                                    cur = new LiveSeg(MessageRole.Tool);
                                    segs.Add(cur);
                                }
                                if (cur.Text.Length > 0) cur.Text.Append("\r\n");
                                cur.Text.Append(marker);
                            }
                        }
                    };
                    Action onComplete = delegate
                    {
                        BeginInvoke((MethodInvoker)delegate
                        { FinalizeToolSend(ctx, convo, renderTimer, sbLock, segs, null); });
                    };
                    Action<string> onErr = delegate(string err)
                    {
                        string e2 = string.IsNullOrEmpty(err) ? "Unknown error." : err;
                        BeginInvoke((MethodInvoker)delegate
                        { FinalizeToolSend(ctx, convo, renderTimer, sbLock, segs, e2); });
                    };

                    orch.RunTurn(convo.History, new DelegateToolLoopUi(onAppend, onToolCall, onToolResult, onComplete, onErr));
                }
                catch (Exception ex)
                {
                    // The turn worker crashed (e.g. in EnsureWorkingDir / BuildMessagesForModel /
                    // RunTurn). The user sees ex.Message in the transcript, but log the full exception
                    // (type + stack) too — otherwise a worker crash leaves no trace in the log.
                    try { LoggerSink.Instance.Log("mcp", "turn worker failed: " + ex); }
                    catch { }
                    string msg = ex.Message;
                    BeginInvoke((MethodInvoker)delegate
                    { FinalizeToolSend(ctx, convo, renderTimer, sbLock, segs, msg); });
                }
            });
        }

        // Finalize a tool turn on the UI thread. error == null means success. The orchestrator has
        // already appended the structured assistant/tool messages to history, so we only flush the
        // transcript bubbles and save. convo is the conversation the turn was STARTED for - when the
        // tab was recycled mid-flight (last tab closed) it differs from ctx.Conversation, and the
        // turn finishes detached: no transcript writes, but the response still reaches its file.
        private void FinalizeToolSend(TabManager.ChatTabContext ctx, Conversation convo,
                                      System.Windows.Forms.Timer renderTimer,
                                      object sbLock, List<LiveSeg> segs, string error)
        {
            try { renderTimer.Stop(); renderTimer.Dispose(); }
            catch { }

            bool attached = ReferenceEquals(ctx.Conversation, convo);
            if (attached)
            {
                try { ctx.Transcript.StickToBottomDuringStreaming = false; }
                catch { }

                // Flush any pending segment content (final delta the timer may not have rendered yet).
                MaterializeLiveSegs(ctx, sbLock, segs);

                // A failed turn surfaces as a chrome-less red notice below whatever (if anything) the
                // model managed to produce — never baked into a bubble.
                if (error != null)
                    ShowTranscriptError(ctx.Transcript, error);
            }

            if (error == null && convo != null)
            {
                if (attached)
                {
                    convo.SelectedModel = ctx.SelectedModel;
                    if (!ctx.NoSaveUntilUserSend) ConversationStore.Save(convo);
                }
                else
                {
                    SaveDetachedTurnConversation(convo);
                }
            }
            ctx.IsSending = false;
            ctx.Cancellation = null;
            HideGenerationBar(ctx);
            if (_sidebarManager != null) _sidebarManager.RefreshSidebarList();
        }

        // Persist a detached turn's conversation (its tab was recycled out from under it). Update-only:
        // never re-create a file the user deleted while the turn was running - the tab Delete action
        // also routes through the recycle path, and its delete must stick.
        private static void SaveDetachedTurnConversation(Conversation convo)
        {
            if (convo == null) return;
            try
            {
                string path = ConversationStore.GetPathForId(convo.Id);
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                    ConversationStore.Save(convo);
            }
            catch { }
        }

        // Create/update the transcript messages backing the ordered live segments. Each segment becomes
        // a real message only once it has content, so empty placeholder bubbles never appear. Runs on
        // the UI thread (render timer + finalize). Segment text is snapshotted under sbLock; the
        // transcript index/last-rendered length live on the segment and are only touched here.
        private void MaterializeLiveSegs(TabManager.ChatTabContext ctx, object sbLock, List<LiveSeg> segs)
        {
            LiveSeg[] snap; string[] texts;
            lock (sbLock)
            {
                snap = segs.ToArray();
                texts = new string[snap.Length];
                for (int i = 0; i < snap.Length; i++) texts[i] = snap[i].Text.ToString();
            }
            for (int i = 0; i < snap.Length; i++)
            {
                LiveSeg seg = snap[i];
                string text = texts[i];
                if (text.Length == 0) continue; // not materialized until it actually has content
                if (seg.Index < 0)
                {
                    seg.Index = ctx.Transcript.AddMessageGetIndex(seg.Role, text);
                    seg.LastLen = text.Length;
                    // Once the conversation has latched ZDR, every assistant bubble it produces is a
                    // zero-retention message — tag it as it materializes (tool blocks are not tagged).
                    if (seg.Role == MessageRole.Assistant && ConvIsZdrLatched(ctx))
                        ctx.Transcript.SetMessageZdrTag(seg.Index, true);
                }
                else if (text.Length != seg.LastLen)
                {
                    ctx.Transcript.UpdateMessageAt(seg.Index, text);
                    seg.LastLen = text.Length;
                }
            }
        }

        // Build request-scoped messages from persisted history, resolving per-attachment
        // representation based on the selected model's capabilities and ZDR state (§6/§8/§9):
        //   - Text → inline extracted text into Content (unchanged path)
        //   - Image, model supports vision → leave binary on nm.Attachments for ContentValue
        //   - Image, model unknown / no vision → inline placeholder text into Content
        //   - PDF, opted into native + model supports file input + not ZDR → leave binary on
        //     nm.Attachments (ContentValue emits a `file` part)
        //   - PDF otherwise → inline extracted text (or a placeholder if it has none, e.g. scanned)
        // The model parameter may be null (first-run / catalog miss) → conservative text-only.
        // zdr forces every PDF to the local text path (native PDF is disabled under ZDR, §9).
        // priorPromptTokens/contextLength drive oldest-first prune-with-placeholder (§10); pass 0 to
        // disable (e.g. when usage/context are unknown — the count/byte safety net still applies).
        private static List<ChatMessage> BuildMessagesForModel(List<ChatMessage> history, string model, bool zdr,
            int priorPromptTokens, int contextLength)
        {
            ModelInfo modelInfo = null;
            bool supportsImage = false;
            bool supportsFile = false;
            if (!string.IsNullOrEmpty(model))
            {
                try { ModelCatalogService.TryGetModelInfo(model, out modelInfo); }
                catch { }
                if (modelInfo != null)
                {
                    supportsImage = modelInfo.SupportsImageInput;
                    supportsFile = modelInfo.SupportsFileInput;
                }
            }

            // Transient prune set (§10): native-eligible attachments to demote to placeholders this
            // request, oldest-first, keeping the replayed binary within the global-conservative caps.
            HashSet<AttachedFile> pruned = AttachmentPruner.ComputePruned(
                history, supportsImage, supportsFile, zdr, priorPromptTokens, contextLength);

            var list = new List<ChatMessage>();
            if (history == null) return list;
            for (int i = 0; i < history.Count; i++)
            {
                var m = history[i];
                if (m == null) continue;
                string content = m.Content ?? string.Empty;
                List<AttachedFile> nativeParts = null; // images / PDFs to carry as binary parts

                if (m.Attachments != null && m.Attachments.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.Append(content);
                    for (int j = 0; j < m.Attachments.Count; j++)
                    {
                        var af = m.Attachments[j];
                        if (af == null) continue;

                        if (af.EffectiveKind == AttachmentKind.Image)
                        {
                            if (supportsImage && !string.IsNullOrEmpty(af.Data) && pruned.Contains(af))
                            {
                                // Pruned (§10): shed the heavy bytes, leave a placeholder. The image
                                // stays in the transcript and re-activates once it's back within budget.
                                sb.AppendLine();
                                sb.AppendLine("[image earlier in conversation: " + af.FileName + "]");
                            }
                            else if (supportsImage && !string.IsNullOrEmpty(af.Data))
                            {
                                // Native image path: leave binary on the request-scoped message.
                                if (nativeParts == null) nativeParts = new List<AttachedFile>();
                                nativeParts.Add(af);
                            }
                            else
                            {
                                // Fallback: placeholder so the model knows the image was attached.
                                sb.AppendLine();
                                sb.AppendLine("[image attached: " + af.FileName
                                    + (supportsImage ? "" : " — current model has no vision") + "]");
                            }
                        }
                        else if (af.EffectiveKind == AttachmentKind.Pdf)
                        {
                            bool wantsNative = (af.SendNativePdf == true);
                            bool hasText = !string.IsNullOrEmpty(af.Content) && af.Content.Trim().Length > 0;
                            if (wantsNative && supportsFile && !zdr && !string.IsNullOrEmpty(af.Data) && pruned.Contains(af))
                            {
                                // Pruned native PDF (§10): drop the bytes. Prefer the extracted text layer
                                // (lighter than the bytes, still faithful); placeholder only if scanned.
                                sb.AppendLine();
                                if (hasText)
                                {
                                    sb.AppendLine("--- Attached File: " + af.FileName + " ---");
                                    sb.AppendLine(af.Content);
                                    sb.AppendLine("--- End Attached File: " + af.FileName + " ---");
                                }
                                else
                                {
                                    sb.AppendLine("[PDF earlier in conversation: " + af.FileName + "]");
                                }
                            }
                            else if (wantsNative && supportsFile && !zdr && !string.IsNullOrEmpty(af.Data))
                            {
                                // Native PDF path: leave binary on the message (file part in §8).
                                if (nativeParts == null) nativeParts = new List<AttachedFile>();
                                nativeParts.Add(af);
                            }
                            else if (hasText)
                            {
                                // Text path: inline the extracted text (default, universal, cheap).
                                sb.AppendLine();
                                sb.AppendLine("--- Attached File: " + af.FileName + " ---");
                                sb.AppendLine(af.Content);
                                sb.AppendLine("--- End Attached File: " + af.FileName + " ---");
                            }
                            else
                            {
                                // No text layer and native not available here: placeholder note.
                                sb.AppendLine();
                                sb.AppendLine("[PDF attached: " + af.FileName
                                    + " — no extractable text" + (zdr ? "; full-document reading is disabled in Zero-Data-Retention conversations" : "") + "]");
                            }
                        }
                        else
                        {
                            // Text attachment: inline extracted text content (unchanged path).
                            sb.AppendLine();
                            sb.AppendLine("--- Attached File: " + af.FileName + " ---");
                            sb.AppendLine(af.Content ?? string.Empty);
                            sb.AppendLine("--- End Attached File: " + af.FileName + " ---");
                        }
                    }
                    content = sb.ToString();
                }

                var nm = new ChatMessage(m.Role, content);
                // Preserve tool-call linkage so this is safe as the orchestrator's request transform.
                nm.ToolCalls = m.ToolCalls;
                nm.ToolCallId = m.ToolCallId;
                // Native images / PDFs travel on Attachments; ContentValue emits the wire parts.
                if (nativeParts != null) nm.Attachments = nativeParts;
                list.Add(nm);
            }
            return list;
        }

        private void PopulateModelsFromSettings()
        {
            try
            {
                var list = AppSettings.GetList("models");
                string def = AppSettings.GetString("default_model");

                // Fresh install (no models configured yet): fall back to the shared default catalog so
                // the combo is never empty and matches what the Settings seed will write.
                if (list == null || list.Count == 0) list = ModelDefaults.ModelList();
                if (string.IsNullOrEmpty(def)) def = ModelDefaults.DefaultModel;

                if (this.cmbModel != null)
                {
                    // Preserve the active tab's chosen model if possible
                    string currentTabModel = null;
                    try
                    {
                        var act = _tabManager != null ? _tabManager.GetActiveContext() : null;
                        if (act != null) currentTabModel = act.SelectedModel;
                    }
                    catch { }

                    this.cmbModel.BeginUpdate();
                    try
                    {
                        this.cmbModel.Items.Clear();
                        foreach (var m in list) this.cmbModel.Items.Add(m);

                        string target = !string.IsNullOrEmpty(currentTabModel) ? currentTabModel : (!string.IsNullOrEmpty(def) ? def : (list.Count > 0 ? list[0] : null));
                        if (!string.IsNullOrEmpty(target))
                        {
                            _syncingModelCombo = true;
                            try { this.cmbModel.Text = target; }
                            finally { _syncingModelCombo = false; }
                        }
                    }
                    finally
                    {
                        this.cmbModel.EndUpdate();
                    }
                    // Ensure dropdown width fits longest item (or control width if shorter)
                    try { AdjustComboDropDownWidth(this.cmbModel); }
                    catch { }
                    // Ensure the active context stores whatever is shown
                    var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
                    if (ctx != null) ctx.SelectedModel = GetSelectedModel();
                }
            }
            catch { }
        }

        // Ensure the combo's dropdown width accommodates the widest item text
        private void AdjustComboDropDownWidth(ComboBox combo)
        {
            if (combo == null) return;
            try
            {
                int maxWidth = combo.Width; // at least the control width
                // Measure each item text
                for (int i = 0; i < combo.Items.Count; i++)
                {
                    string s = combo.GetItemText(combo.Items[i]) ?? string.Empty;
                    if (s.Length == 0) continue;
                    // TextRenderer accounts for ComboBox text rendering in WinForms
                    int w = TextRenderer.MeasureText(s, combo.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine).Width;
                    if (w > maxWidth) maxWidth = w;
                }
                // Add space for vertical scrollbar if it will appear, plus a small margin
                int extra = 10;
                try { if (combo.Items.Count > combo.MaxDropDownItems) extra += SystemInformation.VerticalScrollBarWidth; }
                catch { }
                int target = Math.Max(combo.Width, Math.Min(maxWidth + extra, 2000)); // reasonable upper bound
                combo.DropDownWidth = target;
            }
            catch { }
        }

        // Recompute dropdown width right before it opens
        private void cmbModel_DropDownAdjustWidth(object sender, EventArgs e)
        {
            try { AdjustComboDropDownWidth(this.cmbModel); }
            catch { }
        }

        private void MainForm_Resize(object sender, EventArgs e)
        {
            if (_inputManager != null) _inputManager.AdjustInputBoxHeight();
        }

        // File > New Conversation
        private void miNewConversation_Click(object sender, EventArgs e)
        {
            if (_tabManager != null) _tabManager.CreateConversationTab();
        }

        // Rebuilds the "Open Recent Workspace" submenu each time the File menu opens. Lists each
        // remembered dir (full path) that still exists; silently drops missing ones. Disables the
        // parent when nothing valid remains.
        private void PopulateRecentWorkDirsMenu()
        {
            if (miOpenRecentWorkDir == null) return;

            for (int i = miOpenRecentWorkDir.DropDownItems.Count - 1; i >= 0; i--)
            {
                System.Windows.Forms.ToolStripItem old = miOpenRecentWorkDir.DropDownItems[i];
                miOpenRecentWorkDir.DropDownItems.RemoveAt(i);
                old.Dispose();
            }

            int added = 0;
            List<string> dirs = RecentWorkDirs.Get();
            foreach (string dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;

                bool exists;
                try { exists = Directory.Exists(dir); }
                catch { exists = false; }

                string captured = dir; // avoid the closure capturing the loop variable
                System.Windows.Forms.ToolStripMenuItem item =
                    new System.Windows.Forms.ToolStripMenuItem(captured);
                if (exists)
                {
                    item.Click += delegate(object s, EventArgs e) { OpenNewTabWithWorkingDir(captured); };
                }
                else
                {
                    // Keep missing dirs visible but unselectable so the user can see them.
                    item.Enabled = false;
                    item.ToolTipText = "Folder not found";
                }
                miOpenRecentWorkDir.DropDownItems.Add(item);
                added++;
            }

            miOpenRecentWorkDir.Enabled = added > 0;
        }

        private void miFile_DropDownOpening(object sender, EventArgs e)
        {
            PopulateRecentWorkDirsMenu();
        }

        // File > Close Conversation
        private void closeConversationToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (_tabManager != null) _tabManager.CloseActiveConversationTab();
        }

        // Build the cream notice strip and dock it at the bottom of the chat area (in pnlBottom, just
        // above the input - the same slot as the API-key banner). Styling mirrors WorkspaceContextStrip;
        // the font size is matched to the rest of the UI (ThemeManager scales the API-key banner the same way).
        private void InitModelUpdateBanner()
        {
            try
            {
                if (this.pnlBottom == null) return;

                _modelUpdateBanner = new System.Windows.Forms.Panel();
                _modelUpdateBanner.AutoSize = true;
                _modelUpdateBanner.Dock = DockStyle.Top;
                _modelUpdateBanner.Padding = new Padding(6, 4, 6, 4);
                _modelUpdateBanner.BackColor = Color.FromArgb(252, 246, 220); // cream / warning
                _modelUpdateBanner.Visible = false;

                // The default control font is noticeably small; match the configured chat font size so this
                // reads like the rest of the UI (and the API-key banner, which ThemeManager scales likewise).
                try
                {
                    double fs = AppSettings.GetDouble("font_size", 0);
                    float size = (fs > 0) ? (float)Math.Max(6, Math.Min(48, fs)) : 9f;
                    _modelUpdateBanner.Font = new Font(_modelUpdateBanner.Font.FontFamily, size, _modelUpdateBanner.Font.Style);
                }
                catch { }

                var flow = new FlowLayoutPanel();
                flow.AutoSize = true;
                flow.Dock = DockStyle.Fill;
                flow.FlowDirection = FlowDirection.LeftToRight;
                flow.WrapContents = false;
                flow.Margin = new Padding(0);

                var text = new Label();
                text.AutoSize = true;
                text.TextAlign = ContentAlignment.MiddleLeft;
                text.ForeColor = Color.FromArgb(55, 55, 55);
                text.Margin = new Padding(3, 0, 3, 0);
                text.Anchor = AnchorStyles.None; // vertically centered in the flow row
                text.Text = "Updated recommended models are available.";

                var review = MakeBannerLink("Review in Settings");
                review.LinkClicked += delegate { OnModelBannerReview(); };
                var dismiss = MakeBannerLink("Dismiss");
                dismiss.LinkClicked += delegate { OnModelBannerDismiss(); };

                flow.Controls.Add(text);
                flow.Controls.Add(review);
                flow.Controls.Add(dismiss);
                _modelUpdateBanner.Controls.Add(flow);

                // Host in pnlBottom. The input box (and other banners) are docked Bottom, so docking this
                // Top pins it to the top of pnlBottom - i.e. the bottom of the transcript, directly above
                // the input area - regardless of child add order. BringToFront so the Top dock wins.
                this.pnlBottom.Controls.Add(_modelUpdateBanner);
                _modelUpdateBanner.BringToFront();

                // Let the input-height math account for this banner's footprint, like the other banners.
                if (_inputManager != null) _inputManager.SetModelUpdateBanner(_modelUpdateBanner);
            }
            catch { }
        }

        private static LinkLabel MakeBannerLink(string text)
        {
            var lnk = new LinkLabel();
            lnk.AutoSize = true;
            lnk.Text = text;
            lnk.TextAlign = ContentAlignment.MiddleLeft;
            lnk.LinkColor = Color.FromArgb(0, 90, 158);
            lnk.ActiveLinkColor = Color.FromArgb(0, 90, 158);
            lnk.VisitedLinkColor = Color.FromArgb(0, 90, 158);
            lnk.LinkBehavior = LinkBehavior.HoverUnderline;
            lnk.Margin = new Padding(12, 0, 3, 0);
            lnk.Anchor = AnchorStyles.None; // vertically centered in the flow row
            return lnk;
        }

        // Show the banner when the shipped recommended list differs from what the user last acknowledged
        // (or has never acknowledged one). The acknowledged fingerprint is only written when the user acts
        // (reviews or dismisses), so the banner persists across launches until then.
        private void MaybeShowModelUpdateBanner()
        {
            try
            {
                if (_modelUpdateBanner == null) return;
                string seen = AppSettings.GetString("recommended_hash_seen");
                string current = ModelDefaults.RecommendedHash();
                bool show = string.IsNullOrEmpty(seen) || !string.Equals(seen, current, StringComparison.Ordinal);
                _modelUpdateBanner.Visible = show;
            }
            catch { }
        }

        private void MarkRecommendedSeen()
        {
            try { AppSettings.SetString("recommended_hash_seen", ModelDefaults.RecommendedHash()); }
            catch { }
            if (_modelUpdateBanner != null) _modelUpdateBanner.Visible = false;
        }

        private void OnModelBannerDismiss()
        {
            MarkRecommendedSeen();
        }

        private void OnModelBannerReview()
        {
            MarkRecommendedSeen();
            miSettings_Click(this, EventArgs.Empty);
        }

        // Center the label and link vertically within the banner panel
        private void LayoutApiKeyBanner()
        {
            try
            {
                if (this.pnlApiKeyBanner == null) return;
                int h = this.pnlApiKeyBanner.ClientSize.Height;
                if (this.lblNoApiKey != null)
                {
                    int y = Math.Max(0, (h - this.lblNoApiKey.Height) / 2);
                    this.lblNoApiKey.Top = y;
                }
                if (this.lnkOpenSettings != null)
                {
                    int y = Math.Max(0, (h - this.lnkOpenSettings.Height) / 2);
                    this.lnkOpenSettings.Top = y;
                }
            }
            catch { }
        }

        private void UpdateApiKeyBanner()
        {
            try
            {
                string key = AppSettings.GetString("openrouter_api_key");
                bool hasKey = (key != null && key.Trim().Length > 0);
                if (this.pnlApiKeyBanner != null)
                    this.pnlApiKeyBanner.Visible = !hasKey;

                // Enable/disable input controls based on configuration
                if (this.txtMessage != null) this.txtMessage.Enabled = hasKey;
                if (this.btnSend != null) this.btnSend.Enabled = hasKey;
                if (this.cmbModel != null) this.cmbModel.Enabled = hasKey;
                if (this.btnAttach != null) this.btnAttach.Enabled = hasKey;

                // Keep banner layout tidy when toggled
                if (!hasKey) LayoutApiKeyBanner();

                // Recompute input height because available space changes when the banner toggles
                if (_inputManager != null) _inputManager.AdjustInputBoxHeight();
            }
            catch
            {
                if (this.pnlApiKeyBanner != null) this.pnlApiKeyBanner.Visible = true;
                if (this.txtMessage != null) this.txtMessage.Enabled = false;
                if (this.btnSend != null) this.btnSend.Enabled = false;
                if (this.cmbModel != null) this.cmbModel.Enabled = false;
                if (this.btnAttach != null) this.btnAttach.Enabled = false;

                // Still try to recalc layout
                try { if (_inputManager != null) _inputManager.AdjustInputBoxHeight(); }
                catch { }
            }
        }

        private void lnkOpenSettings_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            using (var dlg = new SettingsForm())
            {
                DialogResult dr = dlg.ShowDialog(this);
                // The dialog writes settings.json directly; drop the cached copy so reads are fresh.
                AppSettings.Reload();
                // Re-sync the per-tab checkbox after settings may have changed (the global default
                // seeds new conversations; it does not retroactively re-lock this one).
                SyncZdrCheckboxFromActiveTab();
                if (dr == DialogResult.OK)
                {
                    InitializeClient();
                }
                else
                {
                    // They might have clicked Apply
                    InitializeClient();
                }
                UpdateApiKeyBanner();
                try { if (_themeManager != null) _themeManager.ApplyThemeToAllTranscripts(); }
                catch { }
                // Theme may have changed in Settings; keep open approval prompts in sync.
                ApplyThemeToAllApprovalPanels();
            }
        }

        // ===== Attachments =====
        private void btnAttach_Click(object sender, EventArgs e)
        {
            var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (ctx == null) return;
            EnsureAttachmentService();
            // Hard gate: only offer the Image Files category when the selected model has vision.
            if (_attachmentService != null)
                _attachmentService.ImageAttachmentsEnabled = CurrentModelSupportsImages();
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "Attach File(s)";
                ofd.Multiselect = true;
                ofd.Filter = (_attachmentService != null) ? _attachmentService.BuildOpenFileDialogFilter() : "All Files|*.*";
                ofd.CheckFileExists = true;
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                List<SkippedAttachment> skipped = new List<SkippedAttachment>();
                var extracted = (_attachmentService != null) ? _attachmentService.ExtractMany(ofd.FileNames, out skipped) : new List<AttachedFile>();
                if (extracted != null && extracted.Count > 0)
                {
                    HandleScannedPdfs(extracted);
                    if (ctx.PendingAttachments == null) ctx.PendingAttachments = new List<AttachedFile>();
                    ctx.PendingAttachments.AddRange(extracted);
                }
                ShowSkippedAttachmentsMessage(skipped);
                RebuildAttachmentsBanner();
            }
        }

        // Replace NULs and non-printable control chars (except CR/LF/TAB) and normalize newlines.
        private static string SanitizeDisplayText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

            bool needs = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\')
                {
                    if (i + 5 < text.Length && text[i + 1] == 'u' && text[i + 2] == '0' && text[i + 3] == '0' && text[i + 4] == '0' && text[i + 5] == '0')
                    { needs = true; break; }
                }
                if (c == '\0' || (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t'))
                { needs = true; break; }
            }
            if (!needs)
            {
                return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine);
            }

            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\' && i + 5 < text.Length && text[i + 1] == 'u' && text[i + 2] == '0' && text[i + 3] == '0' && text[i + 4] == '0' && text[i + 5] == '0')
                {
                    sb.Append(' ');
                    i += 5;
                    continue;
                }
                if (c == '\0') { sb.Append(' '); continue; }
                if (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t') { sb.Append(' '); continue; }
                if (char.IsSurrogate(c))
                {
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        sb.Append(c);
                        sb.Append(text[i + 1]);
                        i++;
                        continue;
                    }
                    sb.Append(' ');
                    continue;
                }
                sb.Append(c);
            }

            string cleaned = sb.ToString();
            cleaned = cleaned.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine);
            return cleaned;
        }

        private void EnsureAttachmentService()
        {
            if (_attachmentService == null)
            {
                // Inject syntax-highlighter file extensions into the attachment service
                string[] patterns = null;
                try { patterns = SyntaxHighlighter.GetAllHighlighterFilePatterns(); }
                catch { patterns = null; }
                if (patterns != null && patterns.Length > 0)
                    _attachmentService = new AttachmentService(patterns);
                else
                    _attachmentService = new AttachmentService();
            }
        }

        // True only when the catalog confirms the selected model accepts image input. Conservative
        // on a miss (unknown model / first-run before the catalog fetch lands) -> false, so images
        // are gated off until capability is positively known (spec §6.4).
        private bool CurrentModelSupportsImages()
        {
            try
            {
                string model = GetSelectedModel();
                if (string.IsNullOrEmpty(model)) return false;
                ModelInfo info;
                if (ModelCatalogService.TryGetModelInfo(model, out info) && info != null)
                    return info.SupportsImageInput;
            }
            catch { }
            return false;
        }

        // True only when the catalog confirms the selected model accepts native file/PDF input.
        // Conservative on a miss (unknown model / first-run) -> false (spec §6.4).
        private bool CurrentModelSupportsFile()
        {
            try
            {
                string model = GetSelectedModel();
                if (string.IsNullOrEmpty(model)) return false;
                ModelInfo info;
                if (ModelCatalogService.TryGetModelInfo(model, out info) && info != null)
                    return info.SupportsFileInput;
            }
            catch { }
            return false;
        }

        // Read-only effective ZDR for the active conversation (does NOT engage the latch the way
        // ResolveZdrForSend does). Used for attach-time gating of native PDF.
        private bool ActiveConversationIsZdr()
        {
            var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (ctx == null || ctx.Conversation == null) return false;
            return ctx.Conversation.Zdr || ConvIsZdrLatched(ctx);
        }

        // Scanned PDFs (no extractable text layer) always request full-document sending. When the
        // current model/ZDR state doesn't support it, surface an early warning so the user knows to
        // change settings before sending (the send-time guard will also block if they don't).
        private void HandleScannedPdfs(List<AttachedFile> extracted)
        {
            if (extracted == null || extracted.Count == 0) return;
            bool supportsFile = CurrentModelSupportsFile();
            bool zdr = ActiveConversationIsZdr();
            var blockedZdr = new List<string>();
            var blockedNoFile = new List<string>();

            for (int i = 0; i < extracted.Count; i++)
            {
                var af = extracted[i];
                if (af == null || af.EffectiveKind != AttachmentKind.Pdf || !af.IsLikelyScanned) continue;
                af.SendNativePdf = true; // always request full-document; guard blocks if state is invalid
                if (zdr)
                    blockedZdr.Add(af.FileName);
                else if (!supportsFile)
                    blockedNoFile.Add(af.FileName);
            }

            if (blockedZdr.Count > 0)
            {
                try
                {
                    MessageBox.Show(this,
                        "This PDF appears to be scanned (no extractable text):\n - "
                        + string.Join("\n - ", blockedZdr.ToArray())
                        + "\n\nReading it requires sending the full document, which routes through a "
                        + "third-party processor whose data-retention policy can't be confirmed - so it "
                        + "is disabled in Zero-Data-Retention conversations. To read this document, start "
                        + "a new conversation without ZDR.",
                        "Scanned PDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
            }
            if (blockedNoFile.Count > 0)
            {
                try
                {
                    MessageBox.Show(this,
                        "This PDF appears to be scanned (no extractable text):\n - "
                        + string.Join("\n - ", blockedNoFile.ToArray())
                        + "\n\nThe selected model can't read full PDF documents. Switch to a model with "
                        + "document support to read it.",
                        "Scanned PDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
            }
        }

        private void RebuildAttachmentsBanner()
        {
            try
            {
                if (this.pnlAttachmentsBanner == null) return;
                var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
                this.pnlAttachmentsBanner.SuspendLayout();
                try
                {
                    // Dispose old chips first - Controls.Clear() detaches but doesn't dispose, and
                    // PDF chips own a ContextMenuStrip (disposed via the chip's Disposed handler).
                    for (int ci = this.pnlAttachmentsBanner.Controls.Count - 1; ci >= 0; ci--)
                    {
                        try { this.pnlAttachmentsBanner.Controls[ci].Dispose(); }
                        catch { }
                    }
                    this.pnlAttachmentsBanner.Controls.Clear();
                    if (ctx == null || ctx.PendingAttachments == null || ctx.PendingAttachments.Count == 0)
                    {
                        this.pnlAttachmentsBanner.Visible = false;
                        // collapse height when hidden
                        this.pnlAttachmentsBanner.Height = 0;
                    }
                    else
                    {
                        this.pnlAttachmentsBanner.Visible = true;
                        for (int i = 0; i < ctx.PendingAttachments.Count; i++)
                        {
                            var af = ctx.PendingAttachments[i];
                            if (af == null) continue;
                            var chip = CreateAttachmentChip(af, ctx.PendingAttachments);
                            this.pnlAttachmentsBanner.Controls.Add(chip);
                        }
                        // Ensure the banner grows to fit wrapped rows
                        int availableWidth = this.pnlAttachmentsBanner.Parent != null ? this.pnlAttachmentsBanner.Parent.ClientSize.Width : this.pnlAttachmentsBanner.Width;
                        if (availableWidth <= 0) availableWidth = this.ClientSize.Width;
                        var pref = this.pnlAttachmentsBanner.GetPreferredSize(new Size(availableWidth, 0));
                        // include padding
                        int targetHeight = Math.Max(0, pref.Height);
                        this.pnlAttachmentsBanner.Height = targetHeight;
                    }
                }
                finally
                {
                    this.pnlAttachmentsBanner.ResumeLayout();
                }
                if (_inputManager != null) _inputManager.AdjustInputBoxHeight();
            }
            catch { }
        }

        private Control CreateAttachmentChip(AttachedFile afRef, IList<AttachedFile> list)
        {
            var panel = new Panel();
            panel.Margin = new Padding(4, 2, 4, 2);
            panel.Padding = new Padding(10, 4, 10, 4); // slightly smaller chips
            panel.AutoSize = false; // we'll size explicitly based on text
            panel.BackColor = Color.Transparent; // we'll paint background ourselves
            panel.Cursor = Cursors.Hand;

            string text = afRef != null ? (afRef.FileName ?? string.Empty) : string.Empty;
            // PDF escalated to native full-document: surface the mode on the chip label.
            if (afRef != null && afRef.EffectiveKind == AttachmentKind.Pdf && afRef.SendNativePdf == true)
                text += "  (full PDF)";
            panel.Font = (this.txtMessage != null ? this.txtMessage.Font : this.Font);

            // Measure and set size so FlowLayout can lay out correctly
            Size textSize = TextRenderer.MeasureText(text, panel.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine);
            int width = Math.Max(18, textSize.Width + panel.Padding.Horizontal);
            int height = Math.Max(18, textSize.Height + panel.Padding.Vertical);
            panel.Size = new Size(width, height);

            int cornerRadius = 6; // reduce radius for a sharper pill
            Color baseFill = Color.FromArgb(230, 230, 230);
            Color hoverFill = Color.FromArgb(240, 240, 240);
            Color pressFill = Color.FromArgb(210, 210, 210);
            Color fillColor = baseFill;
            bool hover = false, pressed = false; // track hover for text color

            // Rounded look via Paint, clip region, and centered text
            panel.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, Math.Max(1, panel.Width - 1), Math.Max(1, panel.Height - 1));
                using (var path = ChatTranscriptControl_RoundedRect(r, cornerRadius))
                using (var b = new SolidBrush(fillColor))
                using (var pen = new Pen(Color.FromArgb(200, 200, 200)))
                {
                    g.FillPath(b, path);
                    g.DrawPath(pen, path);
                }
                var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                var textColor = hover ? Color.Red : panel.ForeColor;
                TextRenderer.DrawText(e.Graphics, text, panel.Font, r, textColor, flags);
            };

            EventHandler applyRegion = (s, e) =>
            {
                try
                {
                    Rectangle r = new Rectangle(0, 0, Math.Max(1, panel.Width - 1), Math.Max(1, panel.Height - 1));
                    using (var path = ChatTranscriptControl_RoundedRect(r, cornerRadius))
                    { panel.Region = new Region(path); }
                }
                catch { }
            };
            panel.SizeChanged += applyRegion;
            panel.HandleCreated += (s, e) => applyRegion(s, e);

            EventHandler repaint = (s, e) => panel.Invalidate();
            MouseEventHandler onMouseDown = (s, e) => { if (e.Button == MouseButtons.Left) { pressed = true; fillColor = pressFill; repaint(s, e); } };
            EventHandler onMouseEnter = (s, e) => { hover = true; if (!pressed) fillColor = hoverFill; repaint(s, e); };
            EventHandler onMouseLeave = (s, e) => { hover = false; pressed = false; fillColor = baseFill; repaint(s, e); };
            MouseEventHandler onMouseUp = (s, e) =>
            {
                if (pressed && e.Button == MouseButtons.Left)
                {
                    pressed = false; fillColor = hover ? hoverFill : baseFill; repaint(s, e);
                    // Remove this attachment by reference
                    if (afRef != null && list != null)
                    {
                        try { list.Remove(afRef); }
                        catch { }
                        RebuildAttachmentsBanner();
                    }
                }
            };
            panel.MouseEnter += onMouseEnter;
            panel.MouseLeave += onMouseLeave;
            panel.MouseDown += onMouseDown;
            panel.MouseUp += onMouseUp;

            // Double-click opens a preview
            EventHandler onDoubleClick = (s, e) => { if (afRef != null) OpenAttachmentInViewer(afRef); };
            panel.DoubleClick += onDoubleClick;

            // PDF attachments: right-click to choose extracted-text vs native full-PDF carriage.
            // (Left-click still removes; double-click still previews - the menu is right-click only.)
            if (afRef != null && afRef.EffectiveKind == AttachmentKind.Pdf)
            {
                var cms = new ContextMenuStrip();
                var miText = new ToolStripMenuItem("Send as extracted text");
                var miFull = new ToolStripMenuItem("Send as full PDF");

                // Recompute checked/enabled/tooltip each time the menu opens. The model or ZDR state
                // can change after the chip is built (e.g. user switches to a file-capable model), so
                // computing once at creation time would leave "Send as full PDF" stale (still disabled).
                cms.Opening += (s, e) =>
                {
                    if (afRef == null) return;
                    bool hasText = !string.IsNullOrEmpty(afRef.Content) && afRef.Content.Trim().Length > 0;
                    bool isNative = (afRef.SendNativePdf == true);
                    bool nativeEligible = CurrentModelSupportsFile() && !ActiveConversationIsZdr()
                                          && !string.IsNullOrEmpty(afRef.Data);
                    if (!hasText)
                    {
                        // No text layer: extracted-text path is unavailable; full PDF is always selected.
                        miText.Enabled = false;
                        miText.Checked = false;
                        miText.ToolTipText = "No extractable text available";
                        miFull.Checked = true;
                        miFull.Enabled = nativeEligible;
                    }
                    else
                    {
                        miText.Enabled = true;
                        miText.Checked = !isNative;
                        miText.ToolTipText = string.Empty;
                        miFull.Checked = isNative;
                        // Allow switching to Full only when eligible; always allow switching back to text.
                        miFull.Enabled = nativeEligible || isNative;
                    }
                    miFull.ToolTipText = miFull.Enabled
                        ? string.Empty
                        : (ActiveConversationIsZdr()
                            ? "Disabled in Zero-Data-Retention conversations"
                            : "The selected model can't read full PDF documents");
                };

                miText.Click += (s, e) => { if (afRef != null) { afRef.SendNativePdf = false; RebuildAttachmentsBanner(); } };
                miFull.Click += (s, e) => { if (afRef != null) { afRef.SendNativePdf = true; RebuildAttachmentsBanner(); } };
                cms.Items.Add(miText);
                cms.Items.Add(miFull);
                panel.ContextMenuStrip = cms;
                // ContextMenuStrip isn't owned by the panel; dispose it when the chip goes away.
                panel.Disposed += (s, e) => { try { cms.Dispose(); } catch { } };
            }

            return panel;
        }

        private void ClearAttachmentsBanner()
        {
            var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
            try { if (ctx != null && ctx.PendingAttachments != null) ctx.PendingAttachments.Clear(); }
            catch { }
            RebuildAttachmentsBanner();
        }

        private void OpenAttachmentInViewer(AttachedFile af)
        {
            if (af == null) return;
            try
            {
                bool darkForViewer = false;
                try
                {
                    string theme = AppSettings.GetString("theme");
                    darkForViewer = !string.IsNullOrEmpty(theme) && theme.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
                }
                catch { darkForViewer = false; }

                using (var dlg = new FileViewerForm())
                {
                    dlg.Text = af.FileName ?? "Attachment";
                    dlg.StartPosition = FormStartPosition.CenterParent;
                    // LoadAttachment branches on Kind: PictureBox for images, RichTextBox for text.
                    dlg.LoadAttachment(af, darkForViewer);
                    dlg.ShowDialog(this);
                }
            }
            catch { }
        }

        private static System.Drawing.Drawing2D.GraphicsPath ChatTranscriptControl_RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var gp = new System.Drawing.Drawing2D.GraphicsPath();
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        // Return default model from settings or fallback
        internal string GetConfiguredDefaultModel()
        {
            try
            {
                var def = AppSettings.GetString("default_model");
                if (!string.IsNullOrEmpty(def)) return def;
                var list = AppSettings.GetList("models");
                if (list != null && list.Count > 0) return list[0];
            }
            catch { }
            return ModelDefaults.DefaultModel;
        }

        // Sync the model combo box to the active tab's SelectedModel
        internal void SyncComboModelFromActiveTab()
        {
            try
            {
                if (this.cmbModel == null) return;
                var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
                if (ctx == null) return;
                var target = ctx.SelectedModel;
                if (string.IsNullOrEmpty(target)) target = GetConfiguredDefaultModel();
                if (!string.Equals(this.cmbModel.Text, target, StringComparison.Ordinal))
                {
                    _syncingModelCombo = true;
                    try { this.cmbModel.Text = target; }
                    finally { _syncingModelCombo = false; }
                }
            }
            catch { }
        }

        // Update the active tab's SelectedModel when user changes selection in the combo
        private void cmbModel_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_syncingModelCombo) return;
            try
            {
                var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
                if (ctx != null) ctx.SelectedModel = GetSelectedModel();
                if (ctx != null && ctx.Conversation != null)
                {
                    ctx.Conversation.SelectedModel = ctx.SelectedModel;
                    // Optional: persist model change only when messages exist and saving is allowed
                    if (ctx.Conversation.History.Count > 0 && !ctx.NoSaveUntilUserSend)
                    {
                        ConversationStore.Save(ctx.Conversation);
                        if (_sidebarManager != null) _sidebarManager.RefreshSidebarList();
                    }
                }
                // The context meter's denominator follows the selected model.
                SyncUsageStatusFromActiveTab();
            }
            catch { }
        }

        // Track typed text into the combo box as model selection per tab
        private void cmbModel_TextUpdate(object sender, EventArgs e)
        {
            if (_syncingModelCombo) return;
            try
            {
                var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
                if (ctx != null) ctx.SelectedModel = GetSelectedModel();
                if (ctx != null && ctx.Conversation != null)
                {
                    ctx.Conversation.SelectedModel = ctx.SelectedModel;
                    if (ctx.Conversation.History.Count > 0 && !ctx.NoSaveUntilUserSend)
                    {
                        ConversationStore.Save(ctx.Conversation);
                        if (_sidebarManager != null) _sidebarManager.RefreshSidebarList();
                    }
                }
                // The context meter's denominator follows the typed model id.
                SyncUsageStatusFromActiveTab();
            }
            catch { }
        }

        // Rebuild a tab's transcript from a conversation's history without blocking the UI:
        // Markdown is parsed on a background thread and the resulting blocks are appended to the
        // transcript in small batches on a UI timer. System messages are skipped; help templates
        // (NoSaveUntilUserSend) are scrolled to the top, otherwise the view sticks to the bottom.
        // Role marker for the chrome-less tool-activity blocks used on reload.
        private const string ToolActivityRole = "toolactivity";

        // Activity marker for a *completed* tool call. If the tool maps to a collapsible/labelled record,
        // register it with the transcript under a stable key and return its sentinel; otherwise (or on any
        // failure) return the generic past-tense "Used <tool>" marker. internal so the agent transcript
        // viewer can render a child's tool calls identically to the main chat.
        internal static string EditDiffMarkerOrCall(ChatTranscriptControl transcript, string name, string argsJson, string key)
        {
            if (transcript != null && !string.IsNullOrEmpty(key))
            {
                try
                {
                    var args = Newtonsoft.Json.Linq.JObject.Parse(string.IsNullOrEmpty(argsJson) ? "{}" : argsJson);
                    string header, body, language; int added, removed;
                    if (TryBuildToolRecord(name, args, key, out header, out body, out language, out added, out removed))
                    {
                        transcript.RegisterToolRecord(key, header, body, language, added, removed);
                        return McpMarkers.EditDiff(key);
                    }
                }
                catch { /* fall through to the generic marker */ }
            }
            return McpMarkers.Used(name);
        }

        // Surface a streaming/transport failure as a chrome-less red notice in the transcript, so a
        // failed turn (e.g. an unavailable model, or a Claude request that returns nothing) is visible
        // instead of leaving the user staring at an empty/missing bubble.
        private static void ShowTranscriptError(ChatTranscriptControl transcript, string rawError)
        {
            if (transcript == null) return;
            transcript.AddMessage(MessageRole.Tool, MarkdownParser.ErrorSentinel(FriendlyStreamError(rawError)));
        }

        // Light humanization of the raw streamer error for display. Provider messages from OpenRouter
        // are usually already readable, so we mostly pass them through under a clear prefix.
        private static string FriendlyStreamError(string raw)
        {
            string e = raw == null ? string.Empty : raw.Trim();
            if (e.Length == 0)
                return "The request failed before the model responded. Please try again or pick a different model.";
            return "The request failed: " + e;
        }

        // Maps a tool call to a transcript record. An empty body yields a one-line label (no expansion);
        // a non-empty body is a collapsible record highlighted in 'language'. Returns false for tools
        // that should keep the generic marker.
        private static bool TryBuildToolRecord(string name, Newtonsoft.Json.Linq.JObject args, string key, out string header, out string body, out string language, out int added, out int removed)
        {
            header = null; body = string.Empty; language = "text"; added = -1; removed = -1;

            // MSBuild tools are named per discovered engine/IDE (msbuild__build_4_0,
            // msbuild__build_solution_2022, ...), so they can't be cased in the switch below; render them
            // from the name + args here. Solution (devenv) builds are checked first - the more specific
            // prefix - then the per-engine MSBuild builds.
            if (name != null && name.StartsWith("msbuild__build_solution_", StringComparison.Ordinal))
                return BuildDevenvRecord(name, args, out header, out body, out language);
            if (name != null && name.StartsWith("msbuild__build_", StringComparison.Ordinal))
                return BuildMsBuildRecord(name, args, out header, out body, out language);

            // command__run and the discovered PowerShell tools render the same record: the command/
            // script from 'command', highlighted (batch for cmd.exe, powershell otherwise). The
            // PowerShell name set lives in McpConfig.IsPowerShellTool so this and the approval preview
            // stay in agreement when hosts are added/renamed.
            if (string.Equals(name, McpConfig.CommandName + "__run", StringComparison.Ordinal) || McpConfig.IsPowerShellTool(name))
            {
                string cmd = Str(args, "command"); if (cmd.Trim().Length == 0) return false;
                bool isPs = McpConfig.IsPowerShellTool(name);
                header = isPs ? "Ran a PowerShell command" : "Ran a command";
                body = cmd; language = isPs ? "powershell" : "batch"; return true;
            }

            switch (name)
            {
                case "ask_user":
                {
                    // "Asked" collapses to the question text (rendered as prose). The chosen answer isn't
                    // shown here - like command__run, the record reflects the call (the question), not its
                    // result. No question -> fall back to the generic marker.
                    string q = Str(args, "question");
                    if (q.Trim().Length == 0) return false;
                    header = "Asked a question"; body = q; language = "markdown"; return true;
                }
                case "files__edit":
                {
                    string path = Str(args, "path");
                    LineDiffResult diff = DiffUtil.BuildLineDiff(Str(args, "old_string"), Str(args, "new_string"));
                    // The +N/-N counts ride alongside (color-coded at draw time), so the header label
                    // is just the path.
                    header = "Edited " + (path.Length > 0 ? path : "(file)");
                    body = diff.Body; language = "diff"; added = diff.Added; removed = diff.Removed; return true;
                }
                case "files__write":
                {
                    string path = Str(args, "path");
                    header = "Wrote " + (path.Length > 0 ? path : "(file)");
                    body = Str(args, "content");
                    language = SyntaxHighlighter.GetLanguageForFileName(path) ?? "text";
                    return true;
                }
                case "files__delete":
                {
                    string path = Str(args, "path"); if (path.Length == 0) return false;
                    header = "Deleted " + path; return true;
                }
                case "files__read":
                {
                    string path = Str(args, "path"); if (path.Length == 0) return false;
                    header = "Read " + path + LineRangeSuffix(args); return true;
                }
                case "files__list":
                {
                    string path = Str(args, "path"); if (path.Length == 0) return false;
                    header = "Listed " + path + (Bool(args, "recursive") ? " (recursive)" : ""); return true;
                }
                case "files__search":
                {
                    string query = Str(args, "query"); if (query.Length == 0) return false;
                    header = "Searched files"; body = query;
                    language = Bool(args, "regex") ? "regex" : "text"; return true;
                }
                case "dispatch_agent":
                {
                    Newtonsoft.Json.Linq.JToken arr = args != null ? args["agents"] : null;
                    if (arr == null || arr.Type != Newtonsoft.Json.Linq.JTokenType.Array) return false;
                    int count = 0;
                    int slot = -1;   // entry slot, aligned with AgentDispatcher.LastTranscripts (counts every object element)
                    System.Text.StringBuilder md = new System.Text.StringBuilder();
                    foreach (Newtonsoft.Json.Linq.JToken t in (Newtonsoft.Json.Linq.JArray)arr)
                    {
                        if (t == null || t.Type != Newtonsoft.Json.Linq.JTokenType.Object) continue;
                        slot++;
                        Newtonsoft.Json.Linq.JObject ag = (Newtonsoft.Json.Linq.JObject)t;
                        string slug = Str(ag, "name").Trim();
                        string task = Str(ag, "task").Trim();
                        if (slug.Length == 0 && task.Length == 0) continue;
                        count++;
                        // Blank line between agents so each is its own paragraph. Bold slug, then the
                        // child's tool-call count (from its transcript) on its own line, then the
                        // "View transcript" link. The count/link only show when a transcript exists
                        // (live: just cached; reloaded: re-seeded from disk) - so an unknown agent that
                        // ran no child has neither.
                        if (md.Length > 0) md.Append("\n\n");
                        md.Append("**").Append(slug.Length > 0 ? slug : "(agent)").Append(":**");
                        AgentTranscript tr = !string.IsNullOrEmpty(key) ? AgentTranscriptStore.Get(key, slot) : null;
                        if (tr != null)
                        {
                            int tc = tr.ToolCallCount;
                            // Count on the same line as the slug; the link drops to the next line.
                            md.Append(' ').Append(tc).Append(tc == 1 ? " tool call" : " tool calls");
                            // Tier 3: a per-agent "View transcript" link the transcript control intercepts
                            // (AgentTranscriptLinks scheme) to open the read-only child transcript popup.
                            md.Append("\n[View transcript](").Append(AgentTranscriptLinks.Build(key, slot)).Append(')');
                        }
                    }
                    if (count == 0) return false;
                    header = "Dispatched " + count + (count == 1 ? " agent" : " agents");
                    body = md.ToString(); language = "markdown"; return true;
                }
                case "web__search":
                {
                    string query = Str(args, "query"); if (query.Trim().Length == 0) return false;
                    header = "Searched the web"; body = query; return true;
                }
                case "web__extract":
                {
                    string urls = JoinUrls(args); if (urls.Length == 0) return false;
                    int n = urls.Split('\n').Length;
                    header = n > 1 ? ("Read " + n + " web pages") : "Read a web page";
                    body = urls; return true;
                }
                case "web__get":
                {
                    string url = Str(args, "url"); if (url.Length == 0) return false;
                    header = "Fetched " + url;
                    // When custom request headers were sent, show them as a readable name: value list;
                    // otherwise this is a plain one-line record.
                    body = FormatHeaderMap(args, "headers"); language = "text"; return true;
                }
                case "web__http":
                {
                    string url = Str(args, "url"); if (url.Length == 0) return false;
                    string method = Str(args, "method"); if (method.Length == 0) method = "POST";
                    header = "Sent " + method.ToUpperInvariant() + " to " + url;
                    // Expandable body reads like a raw request: header lines, then a blank line, then the
                    // request body. With no headers, a JSON body is highlighted as JSON.
                    string hdrs = FormatHeaderMap(args, "headers");
                    string reqBody = Str(args, "body");
                    if (hdrs.Length == 0) { body = reqBody; language = LooksLikeJson(reqBody) ? "json" : "text"; }
                    else if (reqBody.Length == 0) { body = hdrs; language = "text"; }
                    else { body = hdrs + "\n\n" + reqBody; language = "text"; }
                    return true;
                }
                case "git__commit":
                {
                    string msg = Str(args, "message"); if (msg.Trim().Length == 0) return false;
                    header = "Committed"; body = msg; return true;
                }
                case "git__push":
                {
                    string remote = Str(args, "remote"); string branch = Str(args, "branch");
                    string tgt = remote.Length > 0 ? (branch.Length > 0 ? remote + "/" + branch : remote) : branch;
                    header = tgt.Length > 0 ? ("Pushed to " + tgt) : "Pushed commits"; return true;
                }
                case "git__status": header = "Checked git status"; return true;
                case "git__log": header = "Viewed git log"; return true;
                case "git__diff":
                {
                    string path = Str(args, "path");
                    header = "Viewed git diff" + (Bool(args, "staged") ? " (staged)" : "") + (path.Length > 0 ? " of " + path : "");
                    return true;
                }
                case "git__fetch":
                {
                    string remote = Str(args, "remote");
                    header = remote.Length > 0 ? ("Fetched from " + remote) : "Fetched from remote"; return true;
                }
                case "git__pull":
                {
                    string remote = Str(args, "remote"); string branch = Str(args, "branch");
                    string tgt = remote.Length > 0 ? (branch.Length > 0 ? remote + "/" + branch : remote) : branch;
                    header = (Bool(args, "rebase") ? "Pulled (rebase)" : "Pulled") + (tgt.Length > 0 ? " from " + tgt : ""); return true;
                }
                case "git__checkout":
                {
                    string r = Str(args, "ref"); if (r.Length == 0) return false;
                    header = Bool(args, "create") ? ("Created branch " + r) : ("Switched to " + r); return true;
                }
                case "git__restore":
                {
                    string paths = JoinPaths(args, "paths"); if (paths.Length == 0) return false;
                    header = (Bool(args, "staged") ? "Unstaged " : "Restored ") + paths; return true;
                }
                case "git__branch":
                {
                    string action = Str(args, "action"); if (action.Length == 0) action = "list";
                    string nm = Str(args, "name");
                    switch (action.ToLowerInvariant())
                    {
                        case "create": header = nm.Length > 0 ? ("Created branch " + nm) : "Created branch"; break;
                        case "delete":
                        {
                            // A force-delete (-D) can drop unmerged work, so call it out distinctly.
                            string verb = Bool(args, "force") ? "Force-deleted branch" : "Deleted branch";
                            header = nm.Length > 0 ? (verb + " " + nm) : verb; break;
                        }
                        case "rename":
                        {
                            string from = Str(args, "name"); string to = Str(args, "new_name");
                            header = "Renamed branch" + (from.Length > 0 ? " " + from : "") + (to.Length > 0 ? " to " + to : ""); break;
                        }
                        default: header = "Listed branches"; break;
                    }
                    return true;
                }
                case "git__merge":
                {
                    string b = Str(args, "branch"); if (b.Length == 0) return false;
                    header = "Merged " + b; return true;
                }
                case "git__rebase":
                {
                    string action = Str(args, "action"); if (action.Length == 0) action = "start";
                    string onto = Str(args, "onto");
                    switch (action.ToLowerInvariant())
                    {
                        case "continue": header = "Continued rebase"; break;
                        case "abort": header = "Aborted rebase"; break;
                        case "skip": header = "Skipped rebase commit"; break;
                        default: header = onto.Length > 0 ? ("Rebased onto " + onto) : "Rebased"; break;
                    }
                    return true;
                }
                case "git__cherry_pick":
                {
                    string c = Str(args, "commit"); if (c.Length == 0) return false;
                    header = "Cherry-picked " + c; return true;
                }
                case "git__add":
                {
                    if (Bool(args, "all")) { header = "Staged all changes"; return true; }
                    string paths = JoinPaths(args, "paths"); if (paths.Length == 0) return false;
                    header = "Staged " + paths; return true;
                }
                case "git__reset":
                {
                    string paths = JoinPaths(args, "paths");
                    if (paths.Length > 0) { header = "Unstaged " + paths; return true; }
                    string mode = Str(args, "mode"); if (mode.Length == 0) mode = "mixed";
                    string target = Str(args, "target");
                    header = "Reset (" + mode.ToLowerInvariant() + ")" + (target.Length > 0 ? " to " + target : ""); return true;
                }
                case "git__rm":
                {
                    string paths = JoinPaths(args, "paths"); if (paths.Length == 0) return false;
                    header = (Bool(args, "cached") ? "Unstaged (kept) " : "Removed ") + paths; return true;
                }
                case "git__stash":
                {
                    string action = Str(args, "action"); if (action.Length == 0) action = "push";
                    switch (action.ToLowerInvariant())
                    {
                        case "pop": header = "Popped stash"; break;
                        case "apply": header = "Applied stash"; break;
                        case "drop": header = "Dropped stash"; break;
                        case "list": header = "Listed stashes"; break;
                        default: header = "Stashed changes"; break;
                    }
                    return true;
                }
                case "reveal_tools": header = "Checked available tools"; return true;
                case "open_skill":
                {
                    // Post-flight (completed) record label, so past tense like the other records.
                    string slugs = JoinPaths(args, "names"); // generic string-array joiner
                    header = slugs.Length > 0 ? "Read skill " + slugs : "Read skill";
                    return true;
                }
                case "read_skill_file":
                {
                    string rel = Str(args, "relpath");
                    header = rel.Length > 0 ? "Read skill file " + rel : "Read skill file";
                    return true;
                }
                case "extensions__edit_skill_file":
                {
                    // Mirror files__edit: a colored, collapsible line-diff. The skill file lives under the
                    // skill folder, so the header carries the slug + relpath for context.
                    string slug = Str(args, "slug");
                    string rel = Str(args, "relpath");
                    LineDiffResult diff = DiffUtil.BuildLineDiff(Str(args, "old_string"), Str(args, "new_string"));
                    header = "Edited " + SkillTarget(slug, rel);
                    body = diff.Body; language = "diff"; added = diff.Added; removed = diff.Removed; return true;
                }
                case "extensions__run_skill_script":
                {
                    string rel = Str(args, "relpath");
                    header = rel.Length > 0 ? "Ran skill script " + rel : "Ran a skill script";
                    return true;
                }
                case "extensions__create_skill":
                {
                    string slug = Str(args, "slug"); string skillName = Str(args, "name");
                    header = "Created skill " + (slug.Length > 0 ? slug : (skillName.Length > 0 ? skillName : "(skill)"));
                    // Expandable view of the skill's fields (name/description/instructions), not raw JSON.
                    body = FormatSkillFields(skillName, Str(args, "description"), Str(args, "body"));
                    language = "markdown"; return true;
                }
                case "extensions__update_skill":
                {
                    string slug = Str(args, "slug");
                    header = "Updated skill " + (slug.Length > 0 ? slug : "(skill)");
                    // Only the fields the model actually changed are shown.
                    body = FormatSkillFields(Str(args, "name"), Str(args, "description"), Str(args, "body"));
                    language = "markdown"; return true;
                }
                case "extensions__write_skill_file":
                {
                    // Mirror files__write: show the file's content highlighted by its extension.
                    string rel = Str(args, "relpath");
                    header = "Wrote skill file " + SkillTarget(Str(args, "slug"), rel);
                    body = Str(args, "content");
                    language = (rel.Length > 0 ? SyntaxHighlighter.GetLanguageForFileName(rel) : null) ?? "text";
                    return true;
                }
                case "extensions__delete_skill_file":
                {
                    string rel = Str(args, "relpath"); if (rel.Length == 0) return false;
                    header = "Deleted skill file " + SkillTarget(Str(args, "slug"), rel); return true;
                }
                case "extensions__delete_skill":
                {
                    string slug = Str(args, "slug"); if (slug.Length == 0) return false;
                    header = "Deleted skill " + slug; return true;
                }
                case "extensions__list_skill_files":
                {
                    string slug = Str(args, "slug"); if (slug.Length == 0) return false;
                    header = "Listed skill files for " + slug; return true;
                }
                case "extensions__validate_skill":
                {
                    string slug = Str(args, "slug"); if (slug.Length == 0) return false;
                    header = "Validated skill " + slug; return true;
                }
                case "extensions__rename_skill":
                {
                    string slug = Str(args, "slug"); string ns = Str(args, "new_slug");
                    if (slug.Length == 0 || ns.Length == 0) return false;
                    header = "Renamed skill " + slug + " to " + ns; return true;
                }
                case "extensions__create_agent":
                {
                    string slug = Str(args, "slug"); string agentName = Str(args, "name");
                    header = "Created agent " + (slug.Length > 0 ? slug : (agentName.Length > 0 ? agentName : "(agent)"));
                    body = FormatSkillFields(agentName, Str(args, "description"), Str(args, "body"));
                    language = "markdown"; return true;
                }
                case "extensions__update_agent":
                {
                    string slug = Str(args, "slug");
                    header = "Updated agent " + (slug.Length > 0 ? slug : "(agent)");
                    body = FormatSkillFields(Str(args, "name"), Str(args, "description"), Str(args, "body"));
                    language = "markdown"; return true;
                }
                case "extensions__edit_agent":
                {
                    // Mirror edit_skill_file: a colored, collapsible line-diff of the agent's body.
                    string slug = Str(args, "slug");
                    LineDiffResult diff = DiffUtil.BuildLineDiff(Str(args, "old_string"), Str(args, "new_string"));
                    header = "Edited agent " + (slug.Length > 0 ? slug : "(agent)");
                    body = diff.Body; language = "diff"; added = diff.Added; removed = diff.Removed; return true;
                }
                case "extensions__read_agent":
                {
                    // Always render a record (mirrors read_skill_file): a slug-less call still reads as
                    // "Read agent" rather than falling back to the raw "using extensions: read_agent" marker.
                    string slug = Str(args, "slug");
                    header = slug.Length > 0 ? "Read agent " + slug : "Read agent";
                    return true;
                }
                case "extensions__list_agents":
                {
                    header = "Listed agents"; return true;
                }
                case "extensions__delete_agent":
                {
                    string slug = Str(args, "slug"); if (slug.Length == 0) return false;
                    header = "Deleted agent " + slug; return true;
                }
                case "extensions__rename_agent":
                {
                    string slug = Str(args, "slug"); string ns = Str(args, "new_slug");
                    if (slug.Length == 0 || ns.Length == 0) return false;
                    header = "Renamed agent " + slug + " to " + ns; return true;
                }
                case "extensions__validate_agent":
                {
                    string slug = Str(args, "slug"); if (slug.Length == 0) return false;
                    header = "Validated agent " + slug; return true;
                }
                case "memory__remember":
                {
                    string nm = Str(args, "name"); if (nm.Length == 0) return false;
                    header = "Remembered " + nm;
                    // Expandable view of the memory: its one-line summary, then the optional detail note.
                    body = FormatMemory(Str(args, "summary"), Str(args, "detail"));
                    language = "markdown"; return true;
                }
                case "memory__update_memory":
                {
                    string nm = Str(args, "name"); if (nm.Length == 0) return false;
                    header = "Updated memory " + nm;
                    // Only the fields being changed are shown (update replaces; omitted fields are kept).
                    body = FormatMemory(Str(args, "summary"), Str(args, "detail"));
                    language = "markdown"; return true;
                }
                case "memory__read_memory":
                {
                    string nm = Str(args, "name"); if (nm.Length == 0) return false;
                    header = "Read memory " + nm; return true;
                }
                case "memory__forget":
                {
                    string nm = Str(args, "name"); if (nm.Length == 0) return false;
                    header = "Forgot " + nm; return true;
                }
                case "memory__consolidate":
                {
                    string newName = Str(args, "new_name");
                    string sources = JoinPaths(args, "names");
                    header = "Consolidated memories into " + (newName.Length > 0 ? newName : "(memory)");
                    // Body: which memories were merged, then the new entry's summary and detail.
                    var mem = new System.Text.StringBuilder();
                    if (sources.Length > 0) mem.Append("Merged: ").Append(sources);
                    string rest = FormatMemory(Str(args, "summary"), Str(args, "detail"));
                    if (rest.Length > 0) { if (mem.Length > 0) mem.Append("\n\n"); mem.Append(rest); }
                    body = mem.ToString(); language = "markdown"; return true;
                }
                default: return false;
            }
        }

        private static string Str(Newtonsoft.Json.Linq.JObject args, string name)
        {
            try { var t = args[name]; return t == null ? string.Empty : ((string)t ?? string.Empty); }
            catch { return string.Empty; }
        }
        private static bool Bool(Newtonsoft.Json.Linq.JObject args, string name)
        {
            try { var t = args[name]; return t != null && (bool)t; }
            catch { return false; }
        }
        private static string LineRangeSuffix(Newtonsoft.Json.Linq.JObject args)
        {
            try
            {
                var s = args["start_line"]; var e = args["end_line"];
                if (s != null && e != null) return " (lines " + (int)s + "–" + (int)e + ")";
                if (s != null) return " (from line " + (int)s + ")";
                if (e != null) return " (through line " + (int)e + ")";
            }
            catch { }
            return string.Empty;
        }
        private static string JoinUrls(Newtonsoft.Json.Linq.JObject args)
        {
            try
            {
                var arr = args["urls"] as Newtonsoft.Json.Linq.JArray;
                if (arr == null) return string.Empty;
                var sb = new System.Text.StringBuilder();
                foreach (var u in arr)
                {
                    string s = (string)u; if (string.IsNullOrEmpty(s)) continue;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(s);
                }
                return sb.ToString();
            }
            catch { return string.Empty; }
        }

        // Flattens a free-form header map arg ({ "Accept": "application/json", ... }) into readable
        // "Name: value" lines for a tool-record body. Empty when the arg is absent or not an object.
        private static string FormatHeaderMap(Newtonsoft.Json.Linq.JObject args, string name)
        {
            try
            {
                var obj = args[name] as Newtonsoft.Json.Linq.JObject;
                if (obj == null) return string.Empty;
                var sb = new System.Text.StringBuilder();
                foreach (var p in obj)
                {
                    if (p.Value == null) continue;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(p.Key).Append(": ").Append((string)p.Value);
                }
                return sb.ToString();
            }
            catch { return string.Empty; }
        }

        // Heuristic: does this text look like a JSON object/array body (so it can be syntax-highlighted
        // as JSON)? A cheap leading-bracket check; the highlighter degrades gracefully if it's wrong.
        private static bool LooksLikeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.Trim();
            return t.Length > 1 && (t[0] == '{' || t[0] == '[');
        }

        // "slug/relpath" for a skill-file record header, tolerant of a missing slug or relpath.
        private static string SkillTarget(string slug, string relpath)
        {
            if (slug.Length > 0 && relpath.Length > 0) return slug + "/" + relpath;
            if (relpath.Length > 0) return relpath;
            return slug.Length > 0 ? slug : "(skill file)";
        }

        // Expandable view of a skill's authored fields for create/update records: a readable
        // "Name:"/"Description:" preamble followed by the instructions body. Only non-empty fields
        // appear (update_skill passes only the fields being changed), so an unchanged field is omitted.
        private static string FormatSkillFields(string name, string description, string body)
        {
            var sb = new System.Text.StringBuilder();
            if (name != null && name.Length > 0) sb.Append("Name: ").Append(name);
            if (description != null && description.Length > 0)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("Description: ").Append(description);
            }
            if (body != null && body.Length > 0)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(body);
            }
            return sb.ToString();
        }

        // Expandable view of a memory for remember/update records: the one-line summary, then the
        // optional detail note below it. Only non-empty fields appear (update sends just what changed).
        private static string FormatMemory(string summary, string detail)
        {
            var sb = new System.Text.StringBuilder();
            if (summary != null && summary.Length > 0) sb.Append(summary);
            if (detail != null && detail.Length > 0)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(detail);
            }
            return sb.ToString();
        }

        // Comma-separated summary of a string[] pathspec arg (also accepts a lone string), for a
        // one-line tool-record header. Empty when absent.
        private static string JoinPaths(Newtonsoft.Json.Linq.JObject args, string name)
        {
            try
            {
                var t = args[name];
                var sb = new System.Text.StringBuilder();
                var arr = t as Newtonsoft.Json.Linq.JArray;
                if (arr != null)
                {
                    foreach (var p in arr)
                    {
                        string s = (string)p; if (string.IsNullOrEmpty(s)) continue;
                        if (sb.Length > 0) sb.Append(", ");
                        sb.Append(s);
                    }
                }
                else if (t != null && t.Type == Newtonsoft.Json.Linq.JTokenType.String)
                {
                    sb.Append((string)t);
                }
                return sb.ToString();
            }
            catch { return string.Empty; }
        }

        // Renders an MSBuild build-tool call (msbuild__build_<ver>) into a transcript record: a one-line
        // header like "Built MyApp.sln (Release · MSBuild 17.0 x64)" with the build options as a
        // collapsible body. The engine version comes from the tool-name suffix (build_17_0 -> 17.0).
        private static bool BuildMsBuildRecord(string name, Newtonsoft.Json.Linq.JObject args, out string header, out string body, out string language)
        {
            header = null; body = string.Empty; language = "text";

            string ver = name.Substring("msbuild__build_".Length).Replace('_', '.');
            string project = Str(args, "project");
            string projectLabel = project.Length > 0
                ? System.IO.Path.GetFileName(project.Replace('\\', '/')) : "solution";

            // Verb from the requested targets (Clean/Rebuild/Restore), defaulting to "Built".
            string targets = JoinPaths(args, "targets");
            string verb = "Built";
            switch (targets.ToLowerInvariant())
            {
                case "clean": verb = "Cleaned"; break;
                case "rebuild": verb = "Rebuilt"; break;
                case "restore": verb = "Restored"; break;
            }

            // Parenthetical: configuration, then "MSBuild <ver>[ <bitness>]".
            var bits = new List<string>();
            string config = Str(args, "configuration");
            if (config.Length > 0) bits.Add(config);
            string engine = "MSBuild " + ver;
            string bitness = Str(args, "bitness");
            if (bitness.Length > 0) engine += " " + bitness;
            bits.Add(engine);
            header = verb + " " + projectLabel + " (" + string.Join(" · ", bits.ToArray()) + ")";

            // Body: the build options, for the collapsible record (omitted when there's nothing extra).
            var lines = new List<string>();
            if (project.Length > 0) lines.Add("project: " + project);
            if (targets.Length > 0) lines.Add("targets: " + targets);
            if (config.Length > 0) lines.Add("configuration: " + config);
            string platform = Str(args, "platform");
            if (platform.Length > 0) lines.Add("platform: " + platform);
            var props = args["properties"] as Newtonsoft.Json.Linq.JObject;
            if (props != null)
                foreach (var kv in props)
                    lines.Add("property: " + kv.Key + "=" + (kv.Value != null ? kv.Value.ToString() : string.Empty));
            body = string.Join("\n", lines.ToArray());
            return true;
        }

        // Renders a devenv solution-build call (msbuild__build_solution_<year>) into a transcript record:
        // a header like "Built MyApp.sln (Release · Visual Studio 2022)" with the options as a body. The
        // VS year comes from the tool-name suffix (build_solution_2022 -> 2022).
        private static bool BuildDevenvRecord(string name, Newtonsoft.Json.Linq.JObject args, out string header, out string body, out string language)
        {
            header = null; body = string.Empty; language = "text";

            string year = name.Substring("msbuild__build_solution_".Length);
            string solution = Str(args, "solution");
            string solutionLabel = solution.Length > 0
                ? System.IO.Path.GetFileName(solution.Replace('\\', '/')) : "solution";

            // Verb from the action (Rebuild/Clean/Deploy), defaulting to "Built".
            string action = Str(args, "action");
            string verb = "Built";
            switch (action.ToLowerInvariant())
            {
                case "rebuild": verb = "Rebuilt"; break;
                case "clean": verb = "Cleaned"; break;
                case "deploy": verb = "Deployed"; break;
            }

            // Parenthetical: configuration (default Release), then "Visual Studio <year>".
            var bits = new List<string>();
            string config = Str(args, "configuration");
            if (config.Length == 0) config = "Release";
            string platform = Str(args, "platform");
            bits.Add(platform.Length > 0 ? config + "|" + platform : config);
            bits.Add("Visual Studio " + year);
            header = verb + " " + solutionLabel + " (" + string.Join(" · ", bits.ToArray()) + ")";

            // Body: the build options, for the collapsible record.
            var lines = new List<string>();
            if (solution.Length > 0) lines.Add("solution: " + solution);
            if (action.Length > 0) lines.Add("action: " + action);
            lines.Add("configuration: " + (platform.Length > 0 ? config + "|" + platform : config));
            string project = Str(args, "project");
            if (project.Length > 0) lines.Add("project: " + project);
            body = string.Join("\n", lines.ToArray());
            return true;
        }

        private void RebuildTranscriptAsync(TabManager.ChatTabContext ctx, Conversation convo)
        {
            if (ctx == null || ctx.Transcript == null || convo == null) return;
            try
            {
                ctx.Transcript.ClearMessages();
                if (_themeManager != null) _themeManager.ApplyFontSetting(ctx.Transcript);
                try { ctx.Transcript.RefreshTheme(); }
                catch { }

                // Re-seed this conversation's persisted sub-agent transcripts into the in-memory store (keyed
                // by the dispatch record id = tool call id), so the rebuilt records show the tool-call count
                // and their "View transcript" links resolve after a restart.
                try
                {
                    System.Collections.Generic.Dictionary<string, AgentTranscript[]> persisted =
                        AgentTranscriptPersistence.LoadAll(convo.Id);
                    foreach (System.Collections.Generic.KeyValuePair<string, AgentTranscript[]> kv in persisted)
                        AgentTranscriptStore.Put(kv.Key, kv.Value);
                }
                catch { }

                // Snapshot messages to process. System and tool-result messages are not shown. A
                // tool-using assistant turn is persisted as several messages (assistant-with-tool_calls,
                // tool result(s), ..., final assistant). Emit each assistant message's text as its own
                // bubble and its tool calls as a chrome-less "using <tool>" block, interleaved in
                // history order — so multi-turn tool use reads chronologically (text, tools, text, ...),
                // matching the live streamed view. Tool results are never shown (the model summarizes).
                var items = new List<ChatMessage>();
                // Parallel to items: whether each emitted user/assistant bubble is a zero-retention
                // message (its source history index is at/after the ZDR latch). Tool blocks are false.
                var itemZdr = new List<bool>();
                int zdrLatch = convo.ZdrFirstMessageIndex;
                // A /compact conversation shows a persistent chromeless marker at the very top, rebuilt
                // from the saved flag (the live note added at creation isn't in history). ToolActivityRole
                // renders as MessageRole.Tool (chromeless); kept parallel with itemZdr.
                if (convo.ContinuedFromCompaction)
                {
                    items.Add(new ChatMessage(ToolActivityRole, CompactionNoteText));
                    itemZdr.Add(false);
                }
                // Consecutive tool-call runs that aren't separated by real assistant text merge into one
                // chrome-less block (e.g. reveal_tools followed by the call it enabled), so they read as
                // tightly as a single multi-call turn instead of two blocks with a gap between them. A
                // real assistant text bubble or a user message flushes the running block.
                System.Text.StringBuilder pendingTools = null;
                Action flushTools = delegate
                {
                    if (pendingTools != null && pendingTools.Length > 0)
                    {
                        items.Add(new ChatMessage(ToolActivityRole, pendingTools.ToString()));
                        itemZdr.Add(false);
                    }
                    pendingTools = null;
                };
                // Outcome of each tool call (by id) so a denied call renders as "denied" instead of as
                // an applied record. Tool messages aren't shown directly, but their content is the
                // result the orchestrator recorded (McpChatOrchestrator.DeniedResultText on denial).
                var toolResultById = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var rm in convo.History)
                {
                    if (rm == null) continue;
                    if (string.Equals(rm.Role, "tool", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(rm.ToolCallId))
                        toolResultById[rm.ToolCallId] = rm.Content;
                }
                int hi = -1;
                foreach (var m in convo.History)
                {
                    hi++;
                    if (m == null) continue;
                    string role = (m.Role ?? string.Empty).ToLowerInvariant();
                    if (role == "system" || role == "tool") continue; // not shown

                    bool z = zdrLatch >= 0 && hi >= zdrLatch;

                    if (role == "user")
                    {
                        flushTools();
                        items.Add(m);
                        itemZdr.Add(z);
                        continue;
                    }

                    // assistant text (intermediate or final) -> its own bubble, closing any tool run
                    if (!string.IsNullOrEmpty(m.Content) && m.Content.Trim().Length > 0)
                    {
                        flushTools();
                        items.Add(new ChatMessage("assistant", m.Content));
                        itemZdr.Add(z);
                    }

                    // this turn's tool calls -> append to the running chrome-less block
                    if (m.ToolCalls != null && m.ToolCalls.Count > 0)
                    {
                        if (pendingTools == null) pendingTools = new System.Text.StringBuilder();
                        for (int ti = 0; ti < m.ToolCalls.Count; ti++)
                        {
                            var tc = m.ToolCalls[ti];
                            if (tc == null) continue;
                            if (pendingTools.Length > 0) pendingTools.Append("\r\n");
                            string res;
                            bool denied = !string.IsNullOrEmpty(tc.Id)
                                && toolResultById.TryGetValue(tc.Id, out res) && McpMarkers.IsDenied(res);
                            if (denied)
                            {
                                pendingTools.Append(McpMarkers.Denied(tc.Name));
                            }
                            else
                            {
                                string key = !string.IsNullOrEmpty(tc.Id) ? tc.Id : ("edit" + ti);
                                pendingTools.Append(EditDiffMarkerOrCall(ctx.Transcript, tc.Name, tc.ArgumentsJson, key));
                            }
                        }
                    }
                }
                flushTools();

                // Producer-consumer: parse off-UI, consume on UI timer in small chunks
                var parsed = new List<ParsedMessage>(Math.Max(4, items.Count));
                int produced = 0; // parsed items ready in 'parsed'
                int consumed = 0; // items appended to transcript
                bool parsingDone = false;
                object gate = new object();

                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        const int parseChunk = 40; // parse up to N per pass
                        int i = 0;
                        while (i < items.Count)
                        {
                            int count = Math.Min(parseChunk, items.Count - i);
                            var local = new ParsedMessage[count];
                            for (int k = 0; k < count; k++)
                            {
                                var m = items[i + k];
                                MessageRole role;
                                if (string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)) role = MessageRole.Assistant;
                                else if (string.Equals(m.Role, ToolActivityRole, StringComparison.OrdinalIgnoreCase)) role = MessageRole.Tool;
                                else role = MessageRole.User;
                                var text = m.Content ?? string.Empty;
                                var blocks = MarkdownParser.ParseMarkdown(text);
                                var att = (m.Attachments != null && m.Attachments.Count > 0) ? new List<AttachedFile>(m.Attachments) : null;
                                local[k] = new ParsedMessage { Role = role, Text = text, Blocks = blocks, Attachments = att, Zdr = itemZdr[i + k] };
                            }
                            lock (gate)
                            {
                                parsed.AddRange(local);
                                produced += count;
                            }
                            i += count;
                        }
                    }
                    catch { }
                    finally { parsingDone = true; }
                });

                // UI timer consumes progressively
                var timer = new System.Windows.Forms.Timer();
                timer.Interval = 15; // ~60 fps budget for small bursts
                timer.Tick += (s2, e2) =>
                {
                    // The tab may have been closed (e.g. "Close Others") while this build was still
                    // running — its transcript is then disposed. Stop touching it, or BeginBatchUpdates/
                    // EndBatchUpdates would throw ObjectDisposedException.
                    if (ctx == null || ctx.Transcript == null || ctx.Transcript.IsDisposed)
                    {
                        try { timer.Stop(); timer.Dispose(); }
                        catch { }
                        return;
                    }

                    int avail;
                    lock (gate) { avail = produced - consumed; }
                    if (avail <= 0)
                    {
                        if (parsingDone) { timer.Stop(); timer.Dispose(); }
                        return;
                    }

                    // Consume a small batch to keep the UI fluid
                    int take = Math.Min(20, avail);
                    ctx.Transcript.BeginBatchUpdates();
                    try
                    {
                        for (int k = 0; k < take; k++)
                        {
                            ParsedMessage pm;
                            lock (gate) { pm = parsed[consumed]; consumed++; }
                            if (pm != null)
                            {
                                ctx.Transcript.AddParsedMessage(pm.Role, pm.Text, pm.Blocks, pm.Attachments);
                                if (pm.Zdr) ctx.Transcript.SetMessageZdrTag(ctx.Transcript.MessageCount - 1, true);
                            }
                        }
                    }
                    finally
                    {
                        bool last = parsingDone && consumed >= items.Count;
                        bool scrollToBottom = last && !ctx.NoSaveUntilUserSend;
                        ctx.Transcript.EndBatchUpdates(scrollToBottom);
                        if (last)
                        {
                            // Help templates (no-save until user sends) should land at the top
                            try { if (ctx.NoSaveUntilUserSend && ctx.Transcript != null) ctx.Transcript.ScrollToTop(); }
                            catch { }
                            timer.Stop();
                            timer.Dispose();
                        }
                    }
                };
                timer.Start();
            }
            catch { }
        }

        private void OpenConversationInNewTab(Conversation convo)
        {
            if (this.tabControl1 == null || convo == null) return;
            var ctx = _tabManager != null ? _tabManager.CreateConversationTab() : null;
            if (ctx == null) return;

            // Replace the conversation and refresh transcript UI
            ctx.Conversation = convo;
            ctx.SelectedModel = string.IsNullOrEmpty(convo.SelectedModel) ? GetSelectedModel() : convo.SelectedModel;
            try { this.cmbModel.Text = ctx.SelectedModel; }
            catch { }
            ConversationStore.EnsureConversationId(ctx.Conversation);
            if (_sidebarManager != null && ctx.Conversation != null)
                _sidebarManager.TrackOpenConversation(ctx.Conversation.Id, ctx.Page);

            // Rebuild transcript UI off the UI thread to avoid freezes on large histories. During
            // session restore this is deferred (see HydrateTabIfNeeded) so only the visible tab renders.
            if (_restoringTabs) ctx.NeedsTranscriptRebuild = true;
            else RebuildTranscriptAsync(ctx, convo);

            // Adopt the conversation's saved working folder.
            ApplyLoadedWorkingDir(ctx);

            // Update tab title and window
            try
            {
                ctx.Page.Text = ZdrTitle(convo, convo.Name);
                UpdateWindowTitleFromActiveTab();
            }
            catch { }

            // The tab was selected before the conversation was assigned, so re-sync the per-tab ZDR
            // checkbox and the usage status bar (context / cost / savings) now that we know this
            // conversation - the OnTabSelected that fired on creation saw the empty placeholder, so
            // without this the bar stays empty until the next tab switch.
            SyncZdrCheckboxFromActiveTab();
            SyncUsageStatusFromActiveTab();

            // Focus input when a new tab opens from history
            if (_inputManager != null) _inputManager.FocusInputSoon();
        }

        // Load a conversation into a specific tab context (typically blank) and select it
        private void OpenConversationInTab(TabManager.ChatTabContext ctx, Conversation convo)
        {
            if (this.tabControl1 == null || convo == null || ctx == null) return;

            try
            {
                // Update model selection for this tab
                ctx.Conversation = convo;
                ctx.SelectedModel = string.IsNullOrEmpty(convo.SelectedModel) ? GetSelectedModel() : convo.SelectedModel;
                try { this.cmbModel.Text = ctx.SelectedModel; }
                catch { }

                // Update sidebar tracking to point this page at the loaded conversation id
                try
                {
                    // Remove any prior mapping for this page, then track the new id
                    UntrackOpenConversation(ctx.Page);
                }
                catch { }
                ConversationStore.EnsureConversationId(ctx.Conversation);
                if (_sidebarManager != null && ctx.Conversation != null)
                    _sidebarManager.TrackOpenConversation(ctx.Conversation.Id, ctx.Page);

                // Rebuild transcript UI off the UI thread to avoid freezes on large histories. During
                // session restore this is deferred (see HydrateTabIfNeeded) so only the visible tab
                // renders up front; the rest build when first selected.
                if (_restoringTabs) ctx.NeedsTranscriptRebuild = true;
                else RebuildTranscriptAsync(ctx, convo);

                // Adopt the conversation's saved working folder.
                ApplyLoadedWorkingDir(ctx);

                // Hook name updates for this conversation
                try
                {
                    ctx.Conversation.NameGenerated += delegate(string name)
                    {
                        try
                        {
                            if (IsHandleCreated)
                            {
                                BeginInvoke((MethodInvoker)delegate
                                {
                                    ctx.Page.Text = ZdrTitle(ctx.Conversation, name);
                                    UpdateWindowTitleFromActiveTab();
                                });
                            }
                        }
                        catch { }
                    };
                }
                catch { }

                // Update tab title and window
                try
                {
                    ctx.Page.Text = ZdrTitle(convo, convo.Name);
                    // Select this tab and update window title
                    SelectTab(ctx.Page);
                    UpdateWindowTitleFromActiveTab();
                }
                catch { }

                // Reusing the active blank tab won't re-fire OnTabSelected, so sync the ZDR checkbox
                // and the usage status bar (context / cost / savings) to the loaded conversation's
                // state explicitly - otherwise the bar stays empty until the next tab switch.
                SyncZdrCheckboxFromActiveTab();
                SyncUsageStatusFromActiveTab();

                if (_inputManager != null) _inputManager.FocusInputSoon();
                if (_sidebarManager != null) _sidebarManager.RefreshSidebarList();
            }
            catch { }
        }

        // Find a blank tab (no messages). Prefer the active one, else any.
        private TabManager.ChatTabContext FindBlankTabPreferActive()
        {
            try
            {
                if (_tabManager == null) return null;
                var active = _tabManager.GetActiveContext();
                if (IsBlank(active)) return active;
                foreach (var kv in _tabManager.TabContexts)
                {
                    var ctx = kv.Value;
                    if (IsBlank(ctx)) return ctx;
                }
            }
            catch { }
            return null;
        }

        private static bool IsBlank(TabManager.ChatTabContext ctx)
        {
            try
            {
                return ctx != null && ctx.Conversation != null &&
                       (ctx.Conversation.History == null || ctx.Conversation.History.Count == 0);
            }
            catch { return false; }
        }

        private void miApiKeysHelp_Click(object sender, EventArgs e)
        {
            // If an API Keys help tab is already open, focus it
            try
            {
                if (this.tabControl1 != null && _tabManager != null)
                {
                    foreach (TabPage p in this.tabControl1.TabPages)
                    {
                        var ctxOpen = _tabManager.TabContexts.ContainsKey(p) ? _tabManager.TabContexts[p] : null;
                        if (ctxOpen != null && ctxOpen.Conversation != null && string.Equals(ctxOpen.Conversation.Id, HelpApiKeysId, StringComparison.Ordinal))
                        {
                            SelectTab(p);
                            UpdateWindowTitleFromActiveTab();
                            try { if (ctxOpen.Transcript != null) ctxOpen.Transcript.ScrollToTop(); }
                            catch { }
                            return;
                        }
                    }
                }
            }
            catch { }
            // Reuse any blank tab if available (prefer active); otherwise create a new one
            TabManager.ChatTabContext ctx = FindBlankTabPreferActive();
            if (ctx == null)
            {
                ctx = _tabManager != null ? _tabManager.CreateConversationTab() : null;
            }
            if (ctx == null) return;
            try { SelectTab(ctx.Page); }
            catch { }

            try
            {
                // Load base help conversation from embedded resource
                Conversation convo = null;
                try
                {
                    var asm = typeof(MainForm).Assembly;
                    // Resource name follows default: RootNamespace.folder.filename
                    using (var s = asm.GetManifestResourceStream("GxPT.Resources.Help.help_api_keys.json"))
                    {
                        if (s != null)
                        {
                            using (var sr = new StreamReader(s, Encoding.UTF8, true))
                            {
                                string json = sr.ReadToEnd();
                                convo = ConversationStore.LoadFromJson(_client, json);
                            }
                        }
                    }
                }
                catch { }
                if (convo != null)
                {
                    // Help starts with the workspace strip hidden until a working folder is set.
                    convo.WorkspaceStripDismissed = true;
                    // Always treat help templates as no-save until user sends a new message
                    ctx.NoSaveUntilUserSend = true;
                    // Keep the specialized help id from the template for restoration
                    // Give the tab its name from the conversation
                    try { ctx.Page.Text = string.IsNullOrEmpty(convo.Name) ? "API Keys Help" : convo.Name; }
                    catch { }
                    OpenConversationInTab(ctx, convo);
                    // Ensure help opens scrolled to the top
                    try { if (ctx.Transcript != null) ctx.Transcript.ScrollToTop(); }
                    catch { }
                }
                else
                {
                    // No template found: show a notice and do not inject any hardcoded messages
                    MessageBox.Show(this,
                        "Help template not found in embedded resources.",
                        "API Keys Help",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                UpdateWindowTitleFromActiveTab();
                if (_inputManager != null) _inputManager.FocusInputSoon();
            }
            catch { }
        }

        private void miPrivacyHelp_Click(object sender, EventArgs e)
        {
            // If a Privacy help tab is already open, focus it
            try
            {
                if (this.tabControl1 != null && _tabManager != null)
                {
                    foreach (TabPage p in this.tabControl1.TabPages)
                    {
                        var ctxOpen = _tabManager.TabContexts.ContainsKey(p) ? _tabManager.TabContexts[p] : null;
                        if (ctxOpen != null && ctxOpen.Conversation != null && string.Equals(ctxOpen.Conversation.Id, HelpPrivacyId, StringComparison.Ordinal))
                        {
                            SelectTab(p);
                            UpdateWindowTitleFromActiveTab();
                            try { if (ctxOpen.Transcript != null) ctxOpen.Transcript.ScrollToTop(); }
                            catch { }
                            return;
                        }
                    }
                }
            }
            catch { }
            // Reuse any blank tab if available (prefer active); otherwise create a new one
            TabManager.ChatTabContext ctx = FindBlankTabPreferActive();
            if (ctx == null)
            {
                ctx = _tabManager != null ? _tabManager.CreateConversationTab() : null;
            }
            if (ctx == null) return;
            try { SelectTab(ctx.Page); }
            catch { }

            try
            {
                Conversation convo = null;
                try
                {
                    var asm = typeof(MainForm).Assembly;
                    using (var s = asm.GetManifestResourceStream("GxPT.Resources.Help.help_privacy.json"))
                    {
                        if (s != null)
                        {
                            using (var sr = new StreamReader(s, Encoding.UTF8, true))
                            {
                                string json = sr.ReadToEnd();
                                convo = ConversationStore.LoadFromJson(_client, json);
                            }
                        }
                    }
                }
                catch { }
                if (convo != null)
                {
                    // Help starts with the workspace strip hidden until a working folder is set.
                    convo.WorkspaceStripDismissed = true;
                    ctx.NoSaveUntilUserSend = true;
                    try { ctx.Page.Text = string.IsNullOrEmpty(convo.Name) ? "Privacy" : convo.Name; }
                    catch { }
                    OpenConversationInTab(ctx, convo);
                    // Ensure help opens scrolled to the top
                    try { if (ctx.Transcript != null) ctx.Transcript.ScrollToTop(); }
                    catch { }
                }
                else
                {
                    MessageBox.Show(this,
                        "Help template not found in embedded resources.",
                        "Privacy",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                UpdateWindowTitleFromActiveTab();
                if (_inputManager != null) _inputManager.FocusInputSoon();
            }
            catch { }
        }

        private void miAbout_Click(object sender, EventArgs e)
        {
            using (var dlg = new AboutForm())
            {
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.ShowDialog(this);
            }
        }

        // Compare attachments by filename, text content, and binary payload/kind (same order and
        // count). Data and Kind matter because an image's Content is empty - without them, swapping
        // one image for another with the same filename would falsely read as "no change" on edit.
        private static bool AreAttachmentsEqual(List<AttachedFile> a, List<AttachedFile> b)
        {
            try
            {
                int ac = (a != null) ? a.Count : 0;
                int bc = (b != null) ? b.Count : 0;
                if (ac != bc) return false;
                for (int i = 0; i < ac; i++)
                {
                    var ai = a[i]; var bi = b[i];
                    string afn = ai != null ? (ai.FileName ?? string.Empty) : string.Empty;
                    string bfn = bi != null ? (bi.FileName ?? string.Empty) : string.Empty;
                    if (!string.Equals(afn, bfn, StringComparison.Ordinal)) return false;
                    string acnt = ai != null ? (ai.Content ?? string.Empty) : string.Empty;
                    string bcnt = bi != null ? (bi.Content ?? string.Empty) : string.Empty;
                    if (!string.Equals(acnt, bcnt, StringComparison.Ordinal)) return false;
                    string adata = ai != null ? (ai.Data ?? string.Empty) : string.Empty;
                    string bdata = bi != null ? (bi.Data ?? string.Empty) : string.Empty;
                    if (!string.Equals(adata, bdata, StringComparison.Ordinal)) return false;
                    AttachmentKind akind = ai != null ? ai.EffectiveKind : AttachmentKind.Text;
                    AttachmentKind bkind = bi != null ? bi.EffectiveKind : AttachmentKind.Text;
                    if (akind != bkind) return false;
                }
                return true;
            }
            catch { return false; }
        }

        // Missing event handlers expected by designer
        private void miConversationHistory_Click(object sender, EventArgs e)
        {
            if (_sidebarManager != null) _sidebarManager.ToggleSidebar();
        }

        private void txtMessage_KeyDown(object sender, KeyEventArgs e)
        {
            if (_inputManager != null) _inputManager.HandleKeyDown(e);
        }

        private void txtMessage_Enter(object sender, EventArgs e)
        {
            _inputManager.RemoveHintText();
            _themeManager.ApplyThemeToTextBox();
        }

        private void txtMessage_Leave(object sender, EventArgs e)
        {
            _inputManager.SetHintText();
        }

        private void miDarkMode_Click(object sender, EventArgs e)
        {
            try
            {
                // Toggle theme string between light and dark
                string theme = AppSettings.GetString("theme") ?? "light";
                bool isDark = theme.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
                bool toDark = !isDark;

                // Persist setting
                AppSettings.SetString("theme", toDark ? "dark" : "light");

                // Update menu checked state immediately
                if (this.miDarkMode != null) this.miDarkMode.Checked = toDark;

                // Apply theme to all transcripts and relevant UI controls now
                if (_themeManager != null)
                {
                    _themeManager.ApplyThemeToAllTranscripts();
                    _themeManager.ApplyFontSizeSettingToAllUi(); // keep fonts consistent; no-op for theme but safe
                }

                // Also refresh tab headers (font/color may rely on system colors)
                try { if (this.tabControl1 != null) this.tabControl1.Invalidate(); }
                catch { }

                // Status bar follows the UI theme
                ApplyThemeToStatusBar();

                // Re-theme any open tool-approval prompts so they switch with the rest of the UI
                ApplyThemeToAllApprovalPanels();
            }
            catch { }
        }

        // ---- usage status bar (prompt caching / cost telemetry) ----

        private void InitUsageStatusBar()
        {
            try
            {
                // The status strip is a single, fixed-height row by design (its item fonts are never
                // rescaled). Left to AutoSize, its Table layout recomputes height from whatever width it
                // is queried with during a layout pass; the re-entrant layout produced by collapsing the
                // history sidebar while the input box is expanded can measure it at a too-small width,
                // wrap the items onto a second row, and leave the strip permanently taller - overlapping
                // the Send button, model selector, and ZDR checkbox. Pin the height (keeping the current,
                // already DPI-scaled one-row value) so the strip can never grow.
                if (this.ssMain != null)
                {
                    int oneRowHeight = this.ssMain.Height;
                    this.ssMain.AutoSize = false;
                    this.ssMain.Height = oneRowHeight;

                    // The native ToolStrip tooltips flicker when shown over the taskbar at the
                    // bottom of the screen. Drive the item tooltips manually instead (shown above
                    // the bar, on hover rather than mouse-move).
                    StatusStripTooltipFix.Apply(this.ssMain);
                }

                bool visible = AppSettings.GetBool("statusbar_visible");
                if (this.ssMain != null) this.ssMain.Visible = visible;
                if (this.miStatusBar != null) this.miStatusBar.Checked = visible;
                ApplyThemeToStatusBar();
                SyncUsageStatusFromActiveTab();
                // Seed the left slot too (idle at startup): captions + counts, so the labels don't
                // sit empty until the first tab switch or send.
                SyncGenerationIndicatorFromActiveTab();
            }
            catch { }
        }

        private void miStatusBar_Click(object sender, EventArgs e)
        {
            try
            {
                bool visible = !(this.ssMain != null && this.ssMain.Visible);
                if (this.ssMain != null) this.ssMain.Visible = visible;
                if (this.miStatusBar != null) this.miStatusBar.Checked = visible;
                AppSettings.SetBool("statusbar_visible", visible);
            }
            catch { }
        }

        // The status strip matches the menu bar's system chrome rather than the transcript theme:
        // the MenuStrip stays system-rendered in both light and dark modes, and a transcript-white
        // strip looked out of place against it. Saved's health color is reapplied by the sync.
        private void ApplyThemeToStatusBar()
        {
            try
            {
                if (this.ssMain == null) return;
                // Krypton doesn't paint the StatusStrip background (its BackColor
                // shows through, confirmed even under the stock palette mode), so set
                // BackColor to the menu-bar color so the top and bottom bars match.
                // ForeColor is set from the palette so the labels (which inherit it)
                // read on the strip; the Saved label keeps its own red/green/default.
                this.ssMain.BackColor = KryptonThemeBridge.StatusStripBackColor();
                this.ssMain.ForeColor = KryptonThemeBridge.StatusStripTextColor();
                SyncUsageStatusFromActiveTab();
            }
            catch { }
        }

        private void SyncUsageStatusFromActiveTab()
        {
            try
            {
                var act = _tabManager != null ? _tabManager.GetActiveContext() : null;
                UpdateUsageStatusStrip(act != null ? act.Conversation : null);
            }
            catch { }
        }

        // The model catalog finished a background fetch (worker thread): marshal to the UI
        // thread and repaint the active tab's Context pane so the meter (dis)appears promptly.
        private void ModelCatalog_Updated()
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    try { SyncUsageStatusFromActiveTab(); }
                    catch { }
                });
            }
            catch { }
        }

        // Records a response's streamed usage (instant feedback), then reconciles it against
        // OpenRouter's authoritative generation record on a background thread. cache_discount
        // never arrives on the stream, so the generation record is the Saved figure's only data
        // source; it also corrects Cost wherever a provider's stream estimate differs from billing
        // (observed accurate on Bedrock; not guaranteed elsewhere - the delta reconcile is a no-op
        // when they match).
        //
        // Reconciliation is gated by the MODEL's caching capability, never by the streamed cache
        // counters: some provider streams (seen with Amazon Bedrock) omit prompt_tokens_details
        // entirely, so a counter-based gate silently skips exactly the requests that need
        // reconciling - the failure mode where Saved stays frozen at $0.00 while the OpenRouter
        // dashboard shows cache activity on every request.
        private void RecordUsageAndReconcile(Conversation convo, ResponseUsage u, bool cachingModel)
        {
            RecordUsageAndReconcile(convo, u, cachingModel, true);
        }

        // updateContextGauge=false (sub-agent usage): accumulate cost/token totals and reconcile cost, but
        // do not move the parent conversation's context gauge - the child runs in its own isolated context.
        private void RecordUsageAndReconcile(Conversation convo, ResponseUsage u, bool cachingModel, bool updateContextGauge)
        {
            if (convo == null || u == null) return;
            convo.RecordUsage(u, updateContextGauge);
            NotifyUsageUpdated(convo);

            if (string.IsNullOrEmpty(u.Id) || _client == null) return;
            // Caching capability follows the model that ACTUALLY produced this usage (u.Model), not
            // the caller's flag - which is the parent turn's model and is wrong for a sub-agent that
            // overrode its model (e.g. a caching child under a non-caching parent: the parent-model
            // flag would skip the discount fetch and "Saved" would never move for that child). Falls
            // back to the caller's flag when the streamer didn't stamp a model (e.g. scripted tests).
            bool caching = !string.IsNullOrEmpty(u.Model)
                ? OpenRouterClient.ModelSupportsPromptCaching(u.Model) : cachingModel;
            // Stream-first, fetch-as-fallback: the fetch exists for cache_discount (and as a cost
            // correction). When a stream ever carries both, there is nothing left to fetch - this
            // self-cancels the extra GETs if OpenRouter adds cache_discount to SSE chunks.
            if (u.CacheDiscount.HasValue && u.Cost.HasValue) return;
            if (!caching && u.Cost.HasValue) return;
            // A dedicated background thread, not the ThreadPool: the fetch can sleep for up to
            // ~2 minutes waiting for a slow generation record, and the app's sends/naming also run
            // on the pool - a tool loop's worth of sleeping fetches must not starve them.
            var worker = new System.Threading.Thread(delegate()
            {
                try
                {
                    GenerationStats stats = _client.FetchGenerationStats(u.Id);
                    if (stats == null) return;
                    bool changed = convo.ReconcileUsage(u, stats.TotalCost, stats.CacheDiscount);
                    // Streams that omit cache counters also starve the stream-side stickiness
                    // gate; a nonzero billed discount is the same proof of cache activity,
                    // observed late. Latch the conversation-level preference here - the next
                    // turn emits it (mid-turn iterations still rely on stream counters).
                    if (caching && stats.CacheDiscount.HasValue && stats.CacheDiscount.Value != 0)
                    {
                        string prov = !string.IsNullOrEmpty(u.Provider) ? u.Provider : stats.ProviderName;
                        if (!string.IsNullOrEmpty(prov)) convo.CacheWarmProvider = prov;
                    }
                    NotifyUsageUpdated(convo);
                    // Persist the corrected totals: a reconcile can land minutes after the turn's
                    // normal save, and would otherwise be lost on app close / conversation close.
                    // Update-only - never re-create a file the user deleted while the fetch was in
                    // flight. The try/catch save matches the house pattern: a save that races a
                    // running turn's history mutation throws and is skipped, and the next save
                    // (which includes these totals) catches up.
                    if (changed)
                    {
                        try
                        {
                            string path = ConversationStore.GetPathForId(convo.Id);
                            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                                ConversationStore.Save(convo);
                        }
                        catch { }
                    }
                }
                catch { }
            });
            worker.IsBackground = true; // never holds the app open; unreconciled discounts at exit are accepted
            worker.Start();
        }

        // Marshals a usage update from a send worker thread to the UI thread, refreshing the strip
        // only when the reported conversation is still the active tab's (a background tab's turn
        // must not paint over the foreground tab's stats; the totals are still recorded and appear
        // on tab switch via SyncUsageStatusFromActiveTab).
        private void NotifyUsageUpdated(Conversation convo)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        var act = _tabManager != null ? _tabManager.GetActiveContext() : null;
                        if (act != null && ReferenceEquals(act.Conversation, convo))
                            UpdateUsageStatusStrip(convo);
                    }
                    catch { }
                });
            }
            catch { }
        }

        // Renders a conversation's usage accumulators into the status strip. Panes are running
        // totals (Cost/Saved) plus the Context gauge (newest request's full prompt size); the cache
        // percentages live in the tooltip where their time window can be labeled explicitly.
        private void UpdateUsageStatusStrip(Conversation convo)
        {
            if (this.ssMain == null) return;
            // Zeros, not blanks, for fresh conversations: empty labels collapse the pane dividers
            // and the strip looks broken until the first response arrives.
            UsageStats s = (convo != null) ? convo.GetUsageStats() : new UsageStats();

            // The context meter needs the model's context window. Prefer the conversation's own
            // model (it tracks the combo and survives tab switches); fall back to whatever the
            // combo shows for a tab that hasn't picked one yet. Unknown models (catalog not yet
            // fetched, or a model OpenRouter no longer lists) get no meter, just the token count.
            string meterModel = (convo != null && !string.IsNullOrEmpty(convo.SelectedModel))
                ? convo.SelectedModel : GetSelectedModel();
            int maxContext;
            bool haveMax = ModelCatalogService.TryGetContextLength(meterModel, out maxContext)
                && maxContext > 0;

            // Every pane is a caption label + value label pair: the Saved pane needs the split so
            // only the amount carries the health color, and the other two match it so all three
            // panes share identical caption/value spacing. Captions (with the dividers) stay
            // system text; the Saved value goes green once caching has net-saved money, red while
            // net negative (write premiums not yet amortized by reads), neutral at zero.
            if (this.tslContext != null)
                this.tslContext.Text = "Context:";
            if (this.tspContextMeter != null)
            {
                this.tspContextMeter.Visible = haveMax;
                if (haveMax) this.tspContextMeter.SetLevel(s.LastPromptTokens, maxContext);
            }
            if (this.tslContextValue != null)
                this.tslContextValue.Text = haveMax
                    ? FormatTokenCount(s.LastPromptTokens) + " / " + FormatTokenCount(maxContext)
                    : FormatTokenCount(s.LastPromptTokens) + " tok";
            if (this.tslCost != null)
                this.tslCost.Text = "Cost:";
            if (this.tslCostValue != null)
                this.tslCostValue.Text = FormatMoney(s.TotalCost);
            if (this.tslSaved != null)
                this.tslSaved.Text = "Saved:";
            if (this.tslSavedValue != null)
            {
                this.tslSavedValue.Text = FormatMoney(s.TotalCacheDiscount);
                // Green = savings, Firebrick = surcharge, otherwise the themed status
                // text color (not ControlText, which is unreadable on the dark strip).
                this.tslSavedValue.ForeColor = s.TotalCacheDiscount > 0 ? Color.Green
                    : (s.TotalCacheDiscount < 0 ? Color.Firebrick : KryptonThemeBridge.StatusStripTextColor());
            }

            string breakdown = BuildUsageTooltip(s, haveMax ? maxContext : 0);
            ToolStripItem[] panes = new ToolStripItem[]
            {
                this.tslContext, this.tspContextMeter, this.tslContextValue,
                this.tslCost, this.tslCostValue, this.tslSaved, this.tslSavedValue
            };
            foreach (ToolStripItem pane in panes)
                if (pane != null) pane.ToolTipText = breakdown;
        }

        // The hover breakdown: cache percentages with their time windows labeled explicitly (the
        // glanceable panes deliberately omit them - "last request" vs "lifetime" is ambiguous at a
        // glance next to running totals). Short bulleted lines on purpose: the native tooltip
        // word-wraps long lines at an arbitrary width, which mangled the prose layout.
        // maxContext is the model's context window in tokens; 0 = unknown (line omitted).
        internal static string BuildUsageTooltip(UsageStats s, int maxContext)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            if (maxContext > 0)
            {
                sb.Append("Model context window: ").Append(maxContext.ToString("N0", inv)).Append(" tokens");
                if (s.LastPromptTokens > 0)
                    sb.Append(" (").Append((100.0 * s.LastPromptTokens / maxContext).ToString("0", inv)).Append("% used)");
                sb.Append("\r\n\r\n");
            }
            sb.Append("Last request:");
            sb.Append("\r\n- ").Append(s.LastPromptTokens.ToString("N0", inv)).Append(" prompt tokens");
            sb.Append("\r\n- ").Append(s.LastCachedTokens.ToString("N0", inv)).Append(" read from cache");
            if (s.LastPromptTokens > 0)
                sb.Append(" (").Append((100.0 * s.LastCachedTokens / s.LastPromptTokens).ToString("0", inv)).Append("%)");
            if (s.LastCacheWriteTokens > 0)
                sb.Append("\r\n- ").Append(s.LastCacheWriteTokens.ToString("N0", inv)).Append(" written to cache");
            sb.Append("\r\n\r\nLifetime:");
            sb.Append("\r\n- ").Append(s.TotalPromptTokens.ToString("N0", inv)).Append(" prompt tokens");
            sb.Append("\r\n- ").Append(s.TotalCachedTokens.ToString("N0", inv)).Append(" read from cache");
            if (s.TotalPromptTokens > 0)
                sb.Append(" (").Append((100.0 * s.TotalCachedTokens / s.TotalPromptTokens).ToString("0", inv)).Append("%)");
            sb.Append("\r\n- ").Append(s.TotalCompletionTokens.ToString("N0", inv)).Append(" output tokens");
            if (s.TotalReasoningTokens > 0)
                sb.Append(" (").Append(s.TotalReasoningTokens.ToString("N0", inv)).Append(" reasoning)");
            return sb.ToString();
        }

        // "812", "41.2K", "1.3M" - compact enough for a status pane.
        internal static string FormatTokenCount(long n)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (n >= 1000000) return (n / 1000000.0).ToString("0.0", inv) + "M";
            if (n >= 1000) return (n / 1000.0).ToString("0.0", inv) + "K";
            return n.ToString(inv);
        }

        // OpenRouter credits are dollar-denominated. Two decimals minimum, four maximum so
        // sub-cent conversations don't render as "$0.00" forever; negatives (a Saved total that is
        // net write-premium so far) keep the sign visible.
        internal static string FormatMoney(decimal v)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string sign = v < 0 ? "-" : string.Empty;
            decimal a = Math.Abs(v);
            return sign + "$" + a.ToString("0.00##", inv);
        }

        private void miDeleteConversations_Click(object sender, EventArgs e)
        {
            try
            {
                var dr = MessageBox.Show(
                    this,
                    "This will permanently delete all saved conversations. This action cannot be undone.\n\nDo you want to continue?",
                    "Delete All Conversations",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (dr != DialogResult.Yes) return; // default is No/Cancel

                int deleted = 0;
                try
                {
                    deleted = ConversationStore.DeleteAll();
                }
                catch { }

                // Refresh UI (sidebar list, etc.)
                try { if (_sidebarManager != null) _sidebarManager.RefreshSidebarList(); }
                catch { }

                MessageBox.Show(this,
                    deleted > 0 ? "All conversations have been deleted." : "No conversations were found to delete.",
                    "Delete All Conversations",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch { }
        }

        private void miNextTab_Click(object sender, EventArgs e)
        {
            _tabManager.SelectNextTab();
        }

        private void miPreviousTab_Click(object sender, EventArgs e)
        {
            _tabManager.SelectPreviousTab();
        }
    }
}
