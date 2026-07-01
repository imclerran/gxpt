using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Krypton.Navigator;

namespace GxPT
{
    internal sealed class TabManager
    {
        private readonly MainForm _mainForm;
        private readonly KryptonNavigator _tabControl;
        private readonly Dictionary<KryptonPage, ChatTabContext> _tabContexts = new Dictionary<KryptonPage, ChatTabContext>();

        // Tab context menu
        private ContextMenuStrip _tabCtxMenu;
        private ToolStripMenuItem _miTabNew;
        private ToolStripMenuItem _miTabClose;
        private ToolStripMenuItem _miTabCloseOthers;
        private ToolStripMenuItem _miTabWorkdir;
        private ToolStripMenuItem _miTabRename;
        private ToolStripMenuItem _miTabExport;
        private ToolStripMenuItem _miTabDelete;
        private KryptonPage _tabCtxTarget;

        // Custom toolbar buttons
        private GlyphToolStripButton _btnNewTab;
        private GlyphToolStripButton _btnCloseTab;

        public event Action<KryptonPage> TabSelected;
        public event Action TabsChanged;

        // Per-tab chat context
        public sealed class ChatTabContext
        {
            public KryptonPage Page;
            public ChatTranscriptControl Transcript;
            // Backing field for Conversation. Assignment goes through the property so that putting a
            // conversation on a tab is the SAME action as registering it in the sidebar's open-by-id
            // dedup map - no open-path can do one without the other. See ConversationAssigned.
            private Conversation _conversation;
            public Conversation Conversation
            {
                get { return _conversation; }
                set
                {
                    _conversation = value;
                    Action<ChatTabContext> h = ConversationAssigned;
                    if (h != null) h(this);
                }
            }
            // Fired whenever this tab's Conversation reference is (re)assigned. TabManager wires this
            // (WireConversationTracking) to the host's open-by-id tracking, so every current and future
            // way of opening a tab keeps the dedup map in sync just by assigning the property. Set after
            // construction, so the object-initializer assignment in the creators does not fire it -
            // those assign the conversation explicitly once the callback is wired.
            internal Action<ChatTabContext> ConversationAssigned;
            public bool IsSending;
            public string SelectedModel;
            public bool NoSaveUntilUserSend;
            // Per-conversation working directory for MCP files/git/command servers (GXPT_WORKDIR);
            // null = no workspace (those tools won't connect for this tab).
            public string WorkingDir;
            // The conversation's CURRENT directory (host `cd`): a subdirectory at or below WorkingDir the
            // model has scoped into, or null to mean the anchor itself. Transient (in memory only) and
            // reset to the anchor on conversation load, by design — you reopen at the consented boundary,
            // never at some subdir the model wandered into. The MCP server set is still pooled per
            // WorkingDir (the anchor); this rides each call as out-of-band metadata.
            public string CurrentDir;
            // The per-tab workspace strip docked above this tab's transcript (set by MainForm).
            public WorkspaceContextStrip WorkspaceStrip;
            // The per-tab tool-approval panel docked at the bottom of this tab's transcript (set by
            // MainForm). A pending approval shows only on the conversation that requested it.
            public ToolApprovalPanel ApprovalPanel;
            // The per-tab ask_user question panel docked at the bottom (set by MainForm). A pending
            // question shows only on the conversation that asked it; released on teardown like the
            // approval panel so a blocked turn isn't stranded.
            public QuestionPanel QuestionPanel;
            // The per-tab sub-agents activity panel docked at the bottom (set by MainForm). Shown only
            // while a dispatch_agent fan-out runs on this tab's conversation.
            public AgentActivityPanel AgentActivityPanel;
            // True while a dispatch_agent fan-out is running on this tab - drives the status bar's passive
            // "Sub-agents running..." indicator (set by AgentActivityUiBridge).
            public bool AgentsFanOutActive;
            // The in-flight request's cancellation handle (null when idle). The status bar's Stop
            // button calls Cancel() on it to kill the model request.
            public RequestCancellation Cancellation;
            // True when this tab was recycled (last tab closed) out from under an in-flight send:
            // the turn keeps running detached (IsSending still gates new sends until it finalizes),
            // but the status bar's generation indicator must not show for it — the conversation it
            // belongs to is closed. Cleared when the next send starts.
            public bool SendDetached;
            // True when this tab's conversation has been opened but its transcript has NOT yet been
            // built (deferred at startup so only the visible tab pays the cost; built on first view).
            public bool NeedsTranscriptRebuild;
            public List<AttachedFile> PendingAttachments = new List<AttachedFile>();
            // Pending edit of a prior user message (by transcript/history index)
            public bool PendingEditActive;
            public int PendingEditIndex = -1;
            // Model that was active when entering edit mode; used to detect resends due to model change
            public string PendingEditOriginalModel;
        }

        public TabManager(MainForm mainForm, KryptonNavigator tabControl, MenuStrip menuStrip)
        {
            _mainForm = mainForm;
            _tabControl = tabControl;

            InitializeTabControl();
            CreateTabContextMenu();
            AddCustomButtons(menuStrip);
        }

        public Dictionary<KryptonPage, ChatTabContext> TabContexts
        {
            get { return _tabContexts; }
        }

        private void InitializeTabControl()
        {
            if (_tabControl != null)
            {
                _tabControl.SelectedPageChanged += (s, e) =>
                {
                    if (TabSelected != null) TabSelected(_tabControl.SelectedPage);
                };

                try
                {
                    _tabControl.MouseDown -= tabControl1_MouseDown;
                    _tabControl.MouseDown += tabControl1_MouseDown;
                    _tabControl.MouseUp -= tabControl1_MouseUp;
                    _tabControl.MouseUp += tabControl1_MouseUp;
                    // Route the built-in per-tab close button through our own close logic.
                    _tabControl.CloseAction -= tabControl1_CloseAction;
                    _tabControl.CloseAction += tabControl1_CloseAction;
                }
                catch { }
            }
        }

        private void CreateTabContextMenu()
        {
            try
            {
                _tabCtxMenu = new ContextMenuStrip();
                // Use a renderer that survives GDI+ failures while graying a disabled item's
                // image; the Delete item below carries an icon and is disabled for unsaved tabs.
                try { _tabCtxMenu.Renderer = new SafeToolStripRenderer(); }
                catch { }
                _miTabNew = new ToolStripMenuItem("New Tab");
                _miTabClose = new ToolStripMenuItem("Close");
                _miTabCloseOthers = new ToolStripMenuItem("Close Others");
                _miTabWorkdir = new ToolStripMenuItem("Set Working Folder...");
                _miTabRename = new ToolStripMenuItem("Rename");
                _miTabExport = new ToolStripMenuItem("Export");
                _miTabDelete = new ToolStripMenuItem("Delete");
                _miTabDelete.Image = ResourceManager.TryGetAssemblyImage("ExplorerDelete.png");

                _miTabNew.Click += delegate { CreateConversationTab(); };
                _miTabClose.Click += delegate { if (_tabCtxTarget != null) CloseConversationTab(_tabCtxTarget); };
                _miTabCloseOthers.Click += delegate { if (_tabCtxTarget != null) CloseOtherTabs(_tabCtxTarget); };
                _miTabWorkdir.Click += delegate { if (_tabCtxTarget != null) _mainForm.SetWorkingFolderForTab(_tabCtxTarget); };
                _miTabRename.Click += delegate { if (_tabCtxTarget != null) RenameConversationTab(_tabCtxTarget); };
                _miTabExport.Click += delegate { if (_tabCtxTarget != null) ExportConversationTab(_tabCtxTarget); };
                _miTabDelete.Click += delegate { if (_tabCtxTarget != null) DeleteConversationTab(_tabCtxTarget); };

                _tabCtxMenu.Items.AddRange(new ToolStripItem[] { _miTabNew, new ToolStripSeparator(), _miTabWorkdir, _miTabRename, _miTabExport, new ToolStripSeparator(), _miTabClose, _miTabCloseOthers, new ToolStripSeparator(), _miTabDelete });
            }
            catch { }
        }

        private void AddCustomButtons(MenuStrip menuStrip)
        {
            try
            {
                if (menuStrip != null)
                {
                    // Ensure the menu strip displays item tooltips
                    try { menuStrip.ShowItemToolTips = true; }
                    catch { }

                    _btnNewTab = new GlyphToolStripButton(GlyphToolStripButton.GlyphType.Plus);
                    _btnNewTab.Margin = new Padding(2, 2, 2, 2);
                    _btnNewTab.ToolTipText = "New Tab";
                    _btnNewTab.Click += delegate { CreateConversationTab(); };
                    _btnNewTab.Alignment = ToolStripItemAlignment.Right;

                    _btnCloseTab = new GlyphToolStripButton(GlyphToolStripButton.GlyphType.Close);
                    _btnCloseTab.Margin = new Padding(2, 2, 3, 2);
                    _btnCloseTab.ToolTipText = "Close Tab";
                    _btnCloseTab.Click += delegate { CloseActiveConversationTab(); };
                    _btnCloseTab.Alignment = ToolStripItemAlignment.Right;

                    menuStrip.Items.Add(_btnCloseTab);
                    menuStrip.Items.Add(_btnNewTab);
                }
            }
            catch { }
        }

        // Wire a context so any (re)assignment of its Conversation keeps the host's sidebar open-by-id
        // dedup map in sync. This is the single chokepoint: every path that puts a conversation on a tab
        // does so through the Conversation property, so none can open a tab without registering it - the
        // class of bug where a new open-path silently forks a duplicate becomes structurally impossible.
        private void WireConversationTracking(ChatTabContext ctx)
        {
            if (ctx == null) return;
            ctx.ConversationAssigned = delegate(ChatTabContext c) { _mainForm.OnTabConversationAssigned(c); };
        }

        public ChatTabContext SetupInitialConversationTab(KryptonPage initialTab, ChatTranscriptControl initialTranscript)
        {
            try
            {
                if (_tabControl == null || initialTab == null || initialTranscript == null)
                    return null;

                if (_tabContexts.ContainsKey(initialTab))
                    return _tabContexts[initialTab];

                var ctx = new ChatTabContext
                {
                    Page = initialTab,
                    Transcript = initialTranscript,
                    IsSending = false,
                    SelectedModel = _mainForm.GetConfiguredDefaultModel()
                };
                WireConversationTracking(ctx);
                // Ensure the id before assigning, so the property setter registers a real id in the
                // dedup map (a brand-new Conversation has no id until EnsureConversationId runs).
                var initialConvo = new Conversation(_mainForm.GetClient());
                initialConvo.SelectedModel = ctx.SelectedModel;
                _mainForm.EnsureConversationId(initialConvo);
                ctx.Conversation = initialConvo;
                // Wire edit-request, retry and name-generated handlers for this tab
                HookEditRequest(ctx);
                HookRetryRequest(ctx);
                HookNameGenerated(ctx);

                try { ctx.Page.Text = "New Conversation"; }
                catch { }

                // Apply transcript/message width from settings to the initial transcript
                try { TranscriptWidthSettings.Resolve().ApplyTo(ctx.Transcript); }
                catch { }

                _tabContexts[initialTab] = ctx;
                _mainForm.AttachWorkspaceStrip(ctx);
                if (TabsChanged != null) TabsChanged();
                return ctx;
            }
            catch { return null; }
        }

        public ChatTabContext CreateConversationTab()
        {
            if (_tabControl == null) return null;

            var page = new KryptonPage("New Conversation");
            page.Padding = new Padding(0); // transcript sits flush to the page edges

            var transcript = new ChatTranscriptControl();
            transcript.Dock = DockStyle.Fill;
            _mainForm.ApplyFontSetting(transcript);
            page.Controls.Add(transcript);

            var ctx = new ChatTabContext
            {
                Page = page,
                Transcript = transcript,
                IsSending = false,
                SelectedModel = _mainForm.GetConfiguredDefaultModel()
            };
            WireConversationTracking(ctx);
            // Ensure the id before assigning, so the property setter registers a real id in the dedup
            // map (a brand-new Conversation has no id until EnsureConversationId runs).
            var convo = new Conversation(_mainForm.GetClient());
            convo.SelectedModel = ctx.SelectedModel;
            _mainForm.EnsureConversationId(convo);
            ctx.Conversation = convo;

            // Wire edit-request, retry and name-generated handlers for this tab
            HookEditRequest(ctx);
            HookRetryRequest(ctx);
            HookNameGenerated(ctx);

            _tabContexts[page] = ctx;
            _mainForm.AttachWorkspaceStrip(ctx);

            _tabControl.Pages.Add(page);
            try { _tabControl.SelectedPage = page; }
            catch { }

            // Apply transcript/message width from settings for newly created transcript
            try { TranscriptWidthSettings.Resolve().ApplyTo(transcript); }
            catch { }

            if (TabsChanged != null) TabsChanged();
            return ctx;
        }

        public ChatTabContext GetActiveContext()
        {
            try
            {
                if (_tabControl == null) return null;
                var page = _tabControl.SelectedPage;
                if (page == null) return null;
                ChatTabContext ctx;
                return _tabContexts.TryGetValue(page, out ctx) ? ctx : null;
            }
            catch { return null; }
        }

        public void CloseActiveConversationTab()
        {
            try
            {
                if (_tabControl == null) return;
                var page = _tabControl.SelectedPage;
                if (page == null) return;
                CloseConversationTab(page);
            }
            catch { }
        }

        // True when the tab's conversation has a saved file on disk (and thus appears in the
        // sidebar). Used to gate the tab context menu's Delete entry.
        private bool ConversationHasSavedFile(KryptonPage page)
        {
            try
            {
                ChatTabContext ctx;
                if (page == null || !_tabContexts.TryGetValue(page, out ctx) || ctx == null || ctx.Conversation == null)
                    return false;
                string path = ConversationStore.GetPathForId(ctx.Conversation.Id);
                return !string.IsNullOrEmpty(path) && System.IO.File.Exists(path);
            }
            catch { return false; }
        }

        // Export the conversation backing a tab: the single-conversation export, identical to the
        // sidebar's Export (same .gxcv SaveFileDialog flow). It packages the SAVED file, so the
        // menu item is gated on ConversationHasSavedFile like Delete.
        public void ExportConversationTab(KryptonPage page)
        {
            try
            {
                ChatTabContext ctx;
                if (page == null || !_tabContexts.TryGetValue(page, out ctx) || ctx == null || ctx.Conversation == null)
                    return;
                string path = ConversationStore.GetPathForId(ctx.Conversation.Id);
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;

                var info = new ConversationStore.ConversationListItem();
                info.Id = ctx.Conversation.Id;
                info.Name = ctx.Conversation.Name;
                info.Path = path;
                ImportExportManager.ExportSingle(_mainForm, info);
            }
            catch { }
        }

        // Rename the conversation backing a tab via a small prompt dialog (tabs have no room for
        // the sidebar's inline edit). Renames the LIVE conversation object - for an open tab it is
        // the authoritative copy - then persists update-only: an unsaved tab keeps the new name in
        // memory and it lands on disk with the conversation's first regular save.
        public void RenameConversationTab(KryptonPage page)
        {
            try
            {
                ChatTabContext ctx;
                if (page == null || !_tabContexts.TryGetValue(page, out ctx) || ctx == null || ctx.Conversation == null)
                    return;

                string newName = PromptForConversationName(ctx.Conversation.Name);
                if (newName == null || newName.Length == 0) return;
                if (string.Equals(newName, ctx.Conversation.Name ?? string.Empty, StringComparison.Ordinal)) return;

                ctx.Conversation.Name = newName;
                try { page.Text = MainForm.ZdrTitle(ctx.Conversation, newName); }
                catch { }
                _mainForm.UpdateWindowTitle();

                // Update-only save (never create a file for an unsaved tab); a save that races a
                // running turn's history mutation throws and is skipped - the next save catches up.
                try
                {
                    string path = ConversationStore.GetPathForId(ctx.Conversation.Id);
                    if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                    {
                        ConversationStore.Save(ctx.Conversation);
                        var sidebar = _mainForm.GetSidebarManager();
                        if (sidebar != null) sidebar.RefreshSidebarList();
                    }
                }
                catch { }
            }
            catch { }
        }

        // Modal name prompt for RenameConversationTab. Returns the trimmed name, or null on cancel.
        private string PromptForConversationName(string current)
        {
            using (Form dlg = new Form())
            using (TextBox tb = new TextBox())
            using (Button ok = new Button())
            using (Button cancel = new Button())
            {
                dlg.Text = "Rename Conversation";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.ClientSize = new Size(360, 77);

                tb.Bounds = new Rectangle(12, 12, dlg.ClientSize.Width - 24, 20);
                tb.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                tb.Text = current ?? string.Empty;

                ok.Text = "OK";
                ok.DialogResult = DialogResult.OK;
                ok.Bounds = new Rectangle(dlg.ClientSize.Width - 168, 42, 75, 23);
                cancel.Text = "Cancel";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Bounds = new Rectangle(dlg.ClientSize.Width - 87, 42, 75, 23);

                dlg.Controls.Add(tb);
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                tb.SelectAll();
                if (dlg.ShowDialog(_mainForm) != DialogResult.OK) return null;
                return (tb.Text ?? string.Empty).Trim();
            }
        }

        // Delete the conversation backing a tab: remove its saved file, close the tab, and
        // refresh the sidebar. Mirrors the sidebar's Delete action from the tab side.
        public void DeleteConversationTab(KryptonPage page)
        {
            if (page == null) return;
            try
            {
                ChatTabContext ctx;
                if (_tabContexts.TryGetValue(page, out ctx) && ctx != null && ctx.Conversation != null)
                {
                    string path = ConversationStore.GetPathForId(ctx.Conversation.Id);
                    ConversationStore.DeletePath(path);
                }

                CloseConversationTab(page);

                var sidebar = _mainForm.GetSidebarManager();
                if (sidebar != null) sidebar.RefreshSidebarList();
            }
            catch { }
        }

        public void CloseConversationTab(KryptonPage page)
        {
            if (page == null) return;

            ChatTabContext ctx;
            _tabContexts.TryGetValue(page, out ctx);

            if (_tabControl != null && _tabControl.Pages.Count <= 1)
            {
                // Reset single remaining tab
                try
                {
                    if (ctx != null)
                    {
                        // Ensure the sidebar no longer thinks the previous conversation is still open on this page
                        try { _mainForm.UntrackOpenConversation(page); }
                        catch { }
                        if (ctx.Transcript != null) ctx.Transcript.ClearMessages();
                        // Fresh blank conversation for the recycled tab; ensure its id before assigning
                        // so the property setter re-registers this page under the new id.
                        var recycled = new Conversation(_mainForm.GetClient());
                        _mainForm.EnsureConversationId(recycled);
                        ctx.Conversation = recycled;
                        // Reset model to default on a fresh blank tab
                        try
                        {
                            ctx.SelectedModel = _mainForm.GetConfiguredDefaultModel();
                            ctx.Conversation.SelectedModel = ctx.SelectedModel;
                            _mainForm.SyncComboModelFromActiveTab();
                        }
                        catch { }
                        // re-hook name event for the fresh conversation on this reused tab
                        HookNameGenerated(ctx);
                        // The fresh conversation cleared its ZDR + working-dir state, but the per-tab
                        // ZDR checkbox and workspace strip are views that must be re-synced so the
                        // recycled tab starts as a true blank slate.
                        try { _mainForm.ResetRecycledTabWorkspaceState(ctx); }
                        catch { }
                        page.Text = "New Conversation";
                        _mainForm.UpdateWindowTitle();
                    }
                }
                catch { }
                if (TabsChanged != null) TabsChanged();
                return;
            }

            try
            {
                // A pending approval/continuation prompt would be disposed with the page WITHOUT its
                // callback firing, stranding the closed conversation's turn on its blocked worker
                // forever (it would never finalize or save). Resolve it as Deny/Stop first; the
                // now-detached turn then wraps up on its own.
                try { if (ctx != null && ctx.ApprovalPanel != null) ctx.ApprovalPanel.DenyPending(); }
                catch { }
                try { if (ctx != null && ctx.QuestionPanel != null) ctx.QuestionPanel.DenyPending(); }
                catch { }

                int desiredIndex = -1;
                if (_tabControl != null)
                {
                    try
                    {
                        int idx = _tabControl.Pages.IndexOf(page);
                        if (idx >= 0) desiredIndex = Math.Max(0, idx - 1);
                    }
                    catch { }
                }

                if (_tabContexts.ContainsKey(page))
                    _tabContexts.Remove(page);

                _mainForm.UntrackOpenConversation(page);

                if (_tabControl != null)
                {
                    _tabControl.Pages.Remove(page);
                    try
                    {
                        if (_tabControl.Pages.Count > 0)
                        {
                            if (desiredIndex < 0) desiredIndex = 0;
                            if (desiredIndex >= _tabControl.Pages.Count)
                                desiredIndex = _tabControl.Pages.Count - 1;
                            _tabControl.SelectedIndex = desiredIndex;
                        }
                    }
                    catch { }
                }

                try { page.Dispose(); }
                catch { }

                _mainForm.UpdateWindowTitle();
                if (TabsChanged != null) TabsChanged();
            }
            catch { }
        }

        private void CloseOtherTabs(KryptonPage keep)
        {
            try
            {
                if (_tabControl == null || keep == null) return;
                var toClose = new List<KryptonPage>();
                foreach (KryptonPage p in _tabControl.Pages)
                {
                    if (!object.ReferenceEquals(p, keep)) toClose.Add(p);
                }
                foreach (var p in toClose)
                {
                    CloseConversationTab(p);
                }
            }
            catch { }
        }

        private void tabControl1_MouseDown(object sender, MouseEventArgs e)
        {
            try
            {
                if (_tabControl == null) return;

                if (e.Button == MouseButtons.Middle)
                {
                    // PageFromPoint returns the page whose tab header is under the point (null otherwise).
                    KryptonPage page = _tabControl.PageFromPoint(e.Location);
                    if (page != null) CloseConversationTab(page);
                    return;
                }
            }
            catch { }
        }

        private void tabControl1_MouseUp(object sender, MouseEventArgs e)
        {
            try
            {
                if (_tabControl == null || _tabCtxMenu == null) return;
                if (e.Button != MouseButtons.Right) return;

                _tabCtxTarget = _tabControl.PageFromPoint(e.Location);
                if (_tabCtxTarget != null)
                {
                    try { _tabControl.SelectedPage = _tabCtxTarget; }
                    catch { }
                }

                bool hasTarget = (_tabCtxTarget != null);
                _miTabClose.Enabled = hasTarget;
                _miTabCloseOthers.Enabled = hasTarget && _tabControl.Pages.Count > 1;
                _miTabRename.Enabled = hasTarget;
                // Export packages the conversation's saved file (like the sidebar's Export), and
                // Delete removes it - both need the file to exist. A brand-new, message-less tab
                // has nothing to export or delete.
                _miTabExport.Enabled = hasTarget && ConversationHasSavedFile(_tabCtxTarget);
                _miTabDelete.Enabled = hasTarget && ConversationHasSavedFile(_tabCtxTarget);

                _tabCtxMenu.Show(_tabControl, e.Location);
            }
            catch { }
        }

        // The navigator's built-in close button fires this. Suppress Krypton's own remove/dispose
        // (CloseButtonAction.None) and route through CloseConversationTab so the last tab recycles in
        // place and the sidebar/approval/question cleanup runs - exactly like the menu-strip x button.
        private void tabControl1_CloseAction(object sender, CloseActionEventArgs e)
        {
            try
            {
                if (e == null) return;
                e.Action = CloseButtonAction.None;
                if (e.Item != null) CloseConversationTab(e.Item);
            }
            catch { }
        }

        public void SelectTab(KryptonPage page)
        {
            try
            {
                if (_tabControl != null && _tabControl.Pages.Contains(page))
                    _tabControl.SelectedPage = page;
            }
            catch { }
        }

        // Keyboard navigation helpers: cycle through tabs
        public void SelectNextTab()
        {
            try
            {
                if (_tabControl == null) return;
                int count = _tabControl.Pages.Count;
                if (count <= 0) return;
                int idx = Math.Max(0, _tabControl.SelectedIndex);
                int next = (idx + 1) % count;
                _tabControl.SelectedIndex = next;
            }
            catch { }
        }

        public void SelectPreviousTab()
        {
            try
            {
                if (_tabControl == null) return;
                int count = _tabControl.Pages.Count;
                if (count <= 0) return;
                int idx = Math.Max(0, _tabControl.SelectedIndex);
                int prev = (idx - 1 + count) % count;
                _tabControl.SelectedIndex = prev;
            }
            catch { }
        }

        public void ApplyFontSetting()
        {
            try
            {
                double fs = AppSettings.GetDouble("font_size", 0);
                if (fs <= 0) return;
                float size = (float)Math.Max(6, Math.Min(48, fs));

                if (_tabControl != null)
                {
                    _tabControl.Font = new Font(_tabControl.Font.FontFamily, size, _tabControl.Font.Style);
                    foreach (KryptonPage p in _tabControl.Pages)
                    {
                        try { if (p != null) p.Font = new Font(p.Font.FontFamily, size, p.Font.Style); }
                        catch { }
                    }
                }

                foreach (var kv in _tabContexts)
                {
                    try
                    {
                        var t = kv.Value.Transcript;
                        if (t != null) t.Font = new Font(t.Font.FontFamily, size, t.Font.Style);
                    }
                    catch { }
                }
            }
            catch { }
        }

        public void ApplyThemeToAllTranscripts()
        {
            try
            {
                foreach (var kv in _tabContexts)
                {
                    try { if (kv.Value.Transcript != null) kv.Value.Transcript.RefreshTheme(); }
                    catch { }
                    // The agent activity panel is owner-drawn and reads the theme in OnPaint, so a theme
                    // toggle while it is visible needs an explicit repaint (it ignores BackColor/ForeColor).
                    try { if (kv.Value.AgentActivityPanel != null) kv.Value.AgentActivityPanel.Invalidate(); }
                    catch { }
                }
            }
            catch { }
        }

        // Compatibility helpers: apply transcript/message width settings across all transcripts.
        // These overloads allow older callers to compile regardless of signature.
        public void ApplyTranscriptWidthToAllTranscripts()
        {
            try
            {
                var widths = TranscriptWidthSettings.Resolve();
                foreach (var kv in _tabContexts)
                    widths.ApplyTo(kv.Value != null ? kv.Value.Transcript : null);
            }
            catch { }
        }

        public void ApplyTranscriptWidthToAllTranscripts(int maxContentWidth, int maxBubbleWidth)
        {
            try
            {
                foreach (var kv in _tabContexts)
                {
                    var t = kv.Value != null ? kv.Value.Transcript : null;
                    if (t == null) continue;
                    try { t.MaxContentWidth = maxContentWidth; }
                    catch { }
                    try { t.MaxBubbleWidth = maxBubbleWidth; }
                    catch { }
                }
            }
            catch { }
        }

        // Back-compat: apply only content width; leave bubble width unchanged
        public void ApplyTranscriptWidthToAllTranscripts(int maxContentWidth)
        {
            try
            {
                foreach (var kv in _tabContexts)
                {
                    var t = kv.Value != null ? kv.Value.Transcript : null;
                    if (t == null) continue;
                    try { t.MaxContentWidth = maxContentWidth; }
                    catch { }
                }
            }
            catch { }
        }

        // Wire a tab's transcript Retry button (shown on a trailing error notice) to re-run the
        // failed turn.
        private void HookRetryRequest(ChatTabContext ctx)
        {
            if (ctx == null || ctx.Transcript == null) return;
            ctx.Transcript.RetryRequested += delegate
            {
                try { _mainForm.RetryLastTurn(ctx); }
                catch { }
            };
            // A dispatch_agent record's "View transcript" link opens the read-only child viewer (tier 3).
            ctx.Transcript.AgentTranscriptLinkClicked += delegate(string url)
            {
                try { _mainForm.OpenAgentTranscript(url); }
                catch { }
            };
        }

        // Wire a tab's transcript edit-request to populate the input box and enter edit mode.
        // Editing is only permitted for prior user messages.
        private void HookEditRequest(ChatTabContext ctx)
        {
            if (ctx == null || ctx.Transcript == null) return;
            ctx.Transcript.UserMessageEditRequested += delegate(int index, string text)
            {
                try
                {
                    // Map transcript index (user+assistant only) to conversation history index (skipping system)
                    int histIndex = MapTranscriptToHistoryIndex(ctx.Conversation, index);
                    if (histIndex < 0 || histIndex >= ctx.Conversation.History.Count)
                        return;
                    var msg = ctx.Conversation.History[histIndex];
                    if (msg == null || !string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
                        return; // only allow editing user messages

                    var im = _mainForm.GetInputManager();
                    if (im != null) im.SetInputText(msg.Content ?? string.Empty, true);
                    ctx.PendingEditActive = true;
                    ctx.PendingEditIndex = histIndex;
                    // Capture the model at time of entering edit mode
                    ctx.PendingEditOriginalModel = ctx.SelectedModel;
                    // Seed pending attachments from the original message being edited
                    try
                    {
                        var list = new List<AttachedFile>();
                        if (msg.Attachments != null)
                        {
                            for (int i = 0; i < msg.Attachments.Count; i++)
                            {
                                var af = msg.Attachments[i];
                                if (af == null) continue;
                                // Clone so editing a message keeps image/PDF bytes and Kind
                                // (an image's Content is empty, so a name/content copy drops it).
                                list.Add(af.Clone());
                            }
                        }
                        ctx.PendingAttachments = list;
                        // Refresh attachments banner UI
                        try { _mainForm.RefreshAttachmentsBannerUi(); }
                        catch { }
                    }
                    catch { }
                    _mainForm.SelectTab(ctx.Page);
                }
                catch { }
            };
        }

        // Update the tab title (and window title) on the UI thread when a conversation's
        // name is generated in the background.
        private void HookNameGenerated(ChatTabContext ctx)
        {
            if (ctx == null || ctx.Conversation == null) return;
            ctx.Conversation.NameGenerated += delegate(string name)
            {
                try
                {
                    if (_mainForm.IsHandleCreated)
                    {
                        _mainForm.BeginInvoke((MethodInvoker)delegate
                        {
                            // Keep the ZDR marker when the generated name arrives.
                            ctx.Page.Text = MainForm.ZdrTitle(ctx.Conversation, name);
                            _mainForm.UpdateWindowTitle();
                        });
                    }
                }
                catch { }
            };
        }

        // Map a transcript message index (which excludes system messages) to the corresponding
        // history index in Conversation.History (which may include system messages).
        private static int MapTranscriptToHistoryIndex(Conversation convo, int transcriptIndex)
        {
            try
            {
                if (convo == null || convo.History == null) return -1;
                if (transcriptIndex < 0) return -1;
                int count = 0;
                for (int i = 0; i < convo.History.Count; i++)
                {
                    var m = convo.History[i];
                    if (m == null) continue;
                    if (string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                        continue; // not shown in transcript
                    if (count == transcriptIndex) return i;
                    count++;
                }
            }
            catch { }
            return -1;
        }

        // Custom ToolStripButton with copy-button-like hover/press visuals and +/x glyphs
        // A right-aligned +/x button on the menu strip. It is a stock ToolStripButton
        // so Krypton's toolstrip renderer draws its hover/pressed background to match
        // the themed strip (we deliberately do NOT paint our own background, which is
        // what used to clash). We only paint the glyph, in the same color Krypton
        // uses for the menu-bar text so it blends with File/View/Help.
        private sealed class GlyphToolStripButton : ToolStripButton
        {
            public enum GlyphType { Plus, Close }
            private readonly GlyphType _glyph;

            public GlyphToolStripButton(GlyphType glyph)
            {
                _glyph = glyph;
                DisplayStyle = ToolStripItemDisplayStyle.None;
                AutoSize = false;
                Size = new Size(24, 20);
                Margin = new Padding(2);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                // Let the (Krypton) renderer paint the themed button background and
                // hover/pressed states first; ToolStripButton tracks those itself.
                base.OnPaint(e);

                var g = e.Graphics;
                Rectangle r = new Rectangle(0, 0, (int)this.Width - 1, (int)this.Height - 1);
                Color glyphColor;
                try { glyphColor = KryptonThemeBridge.MenuTextColor(); }
                catch { glyphColor = Color.FromArgb(0x50, 0x50, 0x50); }

                using (var pen = new Pen(glyphColor, 2f))
                {
                    int cx = r.Left + r.Width / 2;
                    int cy = r.Top + r.Height / 2;
                    int len = Math.Min(r.Width, r.Height) / 2 - 3;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    if (_glyph == GlyphType.Plus)
                    {
                        g.DrawLine(pen, cx - len, cy, cx + len, cy);
                        g.DrawLine(pen, cx, cy - len, cx, cy + len);
                    }
                    else
                    {
                        g.DrawLine(pen, cx - len, cy - len, cx + len, cy + len);
                        g.DrawLine(pen, cx - len, cy + len, cx + len, cy - len);
                    }
                }
            }
        }
    }
}
