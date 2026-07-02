using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Windows.Forms;
using Krypton.Navigator;
using Krypton.Toolkit;

namespace GxPT
{
    internal sealed class SidebarManager
    {
        // Sidebar animation settings
        private const int SidebarMinWidth = 8;
        private const int SidebarMaxWidth = 240;
        private const int SidebarAnimIntervalMs = 10;
        private const int SidebarAnimDurationMs = 100;

        private readonly MainForm _mainForm;
        private readonly SplitContainer _splitContainer;
        private readonly ToolStripMenuItem _miConversationHistory;

        // Animation state
        private bool _sidebarExpanded;
        private bool _sidebarAnimating;
        private int _sidebarTargetWidth;
        private Timer _sidebarTimer;
        private Stopwatch _sidebarAnimWatch = new Stopwatch();
        private int _sidebarStartWidth;

        // UI components
        private KryptonDataGridView _lvConversations;
        private Panel _sidebarArrowPanel;
        private ContextMenuStrip _conversationContextMenu;
        private TextBox _renameTextBox;
        private Panel _renameHostPanel;
        private DataGridViewRow _renamingItem;
        private int _sidebarRowHeight = 22;
        // Themed vertical scrollbar overlaid on the conversation grid (whose native scrollbars are off).
        private KryptonScrollBar _sidebarScrollBar;
        private const int SidebarScrollBarWidth = 17;
        private bool _syncingSidebarScroll;

        // False until the deferred initial population runs (post-Shown); RefreshSidebarList is a
        // no-op before then so startup paths can't trigger the cold conversation-metadata disk scan.
        private bool _sidebarListReady;

        // Tooltip for the sidebar arrow clickable region
        private ToolTip _sidebarToolTip;

        // Open conversations tracking
        private readonly Dictionary<string, KryptonPage> _openConversationsById = new Dictionary<string, KryptonPage>();

        public event Action SidebarToggled;

        public SidebarManager(MainForm mainForm, SplitContainer splitContainer, ToolStripMenuItem miConversationHistory)
        {
            _mainForm = mainForm;
            _splitContainer = splitContainer;
            _miConversationHistory = miConversationHistory;

            InitializeSidebar();
            InitializeTimer();
            WireEvents();
        }

        public bool IsExpanded
        {
            get { return _sidebarExpanded; }
        }

        // Expanded width grows 5% per font point above the default size so longer
        // row text at larger fonts still fits. Default mirrors GetChatDefaultFontSize
        // in SettingsForm: an unparented control's font, i.e. Control.DefaultFont.
        private static int GetExpandedWidth()
        {
            int width = SidebarMaxWidth;
            try
            {
                double fs = AppSettings.GetDouble("font_size", 0);
                double def = Control.DefaultFont.Size;
                if (fs > def)
                    width = (int)Math.Round(SidebarMaxWidth * (1.0 + 0.05 * (fs - def)));
            }
            catch { }
            return width;
        }

        private void InitializeSidebar()
        {
            if (_splitContainer != null)
            {
                _splitContainer.FixedPanel = FixedPanel.Panel1;
                _splitContainer.IsSplitterFixed = true;
                _splitContainer.Panel1MinSize = SidebarMinWidth;
                _splitContainer.Panel2MinSize = 0;
                _splitContainer.SplitterWidth = 1;
                _splitContainer.SplitterDistance = SidebarMinWidth;
                _sidebarExpanded = false;

                // Create tooltip instance
                try
                {
                    if (_sidebarToolTip == null)
                    {
                        _sidebarToolTip = new ToolTip();
                        _sidebarToolTip.ShowAlways = true;
                        _sidebarToolTip.AutoPopDelay = 5000;
                        _sidebarToolTip.InitialDelay = 400;
                        _sidebarToolTip.ReshowDelay = 100;
                    }
                }
                catch { }

                EnsureSidebarList();
                EnsureSidebarArrowStrip();

                _splitContainer.Panel1.TabStop = true;
                _splitContainer.Panel1.PreviewKeyDown += Panel1_PreviewKeyDown;
            }
        }

        private void InitializeTimer()
        {
            _sidebarTimer = new Timer();
            _sidebarTimer.Interval = SidebarAnimIntervalMs;
            _sidebarTimer.Tick += SidebarTimer_Tick;
        }

        private void WireEvents()
        {
            if (_splitContainer != null && _splitContainer.Panel1 != null)
            {
                _splitContainer.Panel1.Resize += (s, e) =>
                {
                    if (_sidebarArrowPanel != null)
                        _sidebarArrowPanel.Invalidate();
                    LayoutSidebarChildren();
                };
            }

            if (_miConversationHistory != null)
            {
                _miConversationHistory.CheckOnClick = false;
                _miConversationHistory.Click += (s, e) => ToggleSidebar();
                UpdateConversationHistoryCheckedState();
            }
        }

        public void ToggleSidebar()
        {
            if (_splitContainer == null || _sidebarAnimating) return;

            _sidebarStartWidth = _splitContainer.SplitterDistance;
            _sidebarTargetWidth = _sidebarExpanded ? SidebarMinWidth : GetExpandedWidth();
            _sidebarAnimating = true;

            try
            {
                _sidebarAnimWatch.Reset();
                _sidebarAnimWatch.Start();
            }
            catch { }

            _sidebarTimer.Start();
        }

        private void SidebarTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (_splitContainer == null)
                {
                    _sidebarTimer.Stop();
                    _sidebarAnimating = false;
                    return;
                }

                if (!_sidebarAnimWatch.IsRunning)
                    _sidebarAnimWatch.Start();

                long elapsed = _sidebarAnimWatch.ElapsedMilliseconds;
                double t = Math.Max(0.0, Math.Min(1.0, (double)elapsed / SidebarAnimDurationMs));
                double eased = EaseInOutCubic(t);

                int start = _sidebarStartWidth;
                int end = _sidebarTargetWidth;
                int next = (int)Math.Round(start + (end - start) * eased);

                int cur = _splitContainer.SplitterDistance;
                if (next != cur)
                {
                    _splitContainer.SuspendLayout();
                    try { _splitContainer.SplitterDistance = next; }
                    finally { _splitContainer.ResumeLayout(); }

                    if (_sidebarArrowPanel != null)
                    {
                        int h = _sidebarArrowPanel.ClientSize.Height;
                        var rect = new Rectangle(0, Math.Max(0, h / 2 - 20), _sidebarArrowPanel.Width, 40);
                        _sidebarArrowPanel.Invalidate(rect);
                    }
                    LayoutSidebarChildren();
                }

                if (t >= 1.0)
                {
                    try
                    {
                        _splitContainer.SuspendLayout();
                        if (_splitContainer.SplitterDistance != _sidebarTargetWidth)
                            _splitContainer.SplitterDistance = _sidebarTargetWidth;
                    }
                    finally { _splitContainer.ResumeLayout(); }

                    _sidebarTimer.Stop();
                    _sidebarAnimWatch.Stop();
                    _sidebarAnimating = false;
                    _sidebarExpanded = (_sidebarTargetWidth > SidebarMinWidth);

                    if (_sidebarArrowPanel != null)
                        _sidebarArrowPanel.Invalidate();
                    LayoutSidebarChildren();
                    UpdateConversationHistoryCheckedState();

                    if (SidebarToggled != null) SidebarToggled();

                    // Update tooltip text to reflect new state
                    UpdateArrowToolTip();
                }
            }
            catch
            {
                try { _sidebarTimer.Stop(); }
                catch { }
                try { _sidebarAnimWatch.Stop(); }
                catch { }
                _sidebarAnimating = false;
            }
        }

        private static double EaseInOutCubic(double t)
        {
            return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        }

        private void Panel1_PreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                ToggleSidebar();
                e.IsInputKey = true;
            }
        }

        private void Panel1_ClickToggle(object sender, EventArgs e)
        {
            try
            {
                if (_sidebarArrowPanel != null && _sidebarExpanded)
                {
                    var me = e as MouseEventArgs;
                    if (me != null)
                    {
                        int half = _sidebarArrowPanel.Width / 2;
                        if (me.X < half) return;
                    }
                }
            }
            catch { }
            ToggleSidebar();
        }

        private void EnsureSidebarArrowStrip()
        {
            try
            {
                if (_sidebarArrowPanel != null) return;

                _sidebarArrowPanel = new Panel();
                _sidebarArrowPanel.Width = 14;
                _sidebarArrowPanel.Dock = DockStyle.Right;
                _sidebarArrowPanel.Margin = new Padding(0);
                _sidebarArrowPanel.Padding = new Padding(0);
                _sidebarArrowPanel.Cursor = Cursors.Hand;
                _sidebarArrowPanel.BackColor = _splitContainer.Panel1.BackColor;
                _sidebarArrowPanel.Paint += Panel1_PaintArrow;
                _sidebarArrowPanel.Click += Panel1_ClickToggle;
                _sidebarArrowPanel.PreviewKeyDown += Panel1_PreviewKeyDown;
                _sidebarArrowPanel.TabStop = true;
                _splitContainer.Panel1.Controls.Add(_sidebarArrowPanel);
                _sidebarArrowPanel.BringToFront();
                LayoutSidebarChildren();

                // Assign initial tooltip
                UpdateArrowToolTip();
            }
            catch { }
        }

        private void Panel1_PaintArrow(object sender, PaintEventArgs e)
        {
            try
            {
                var p = _sidebarArrowPanel ?? (_splitContainer != null ? _splitContainer.Panel1 : null);
                if (p == null) return;

                int w = p.ClientSize.Width;
                int h = p.ClientSize.Height;
                if (w <= 0 || h <= 0) return;

                bool pointRight;
                if (_sidebarAnimating)
                {
                    pointRight = _sidebarTargetWidth > _splitContainer.SplitterDistance;
                }
                else
                {
                    pointRight = !_sidebarExpanded;
                }

                int arrowH = Math.Max(8, Math.Min(12, h / 20));
                int arrowW = Math.Max(5, arrowH / 2 + 2);
                int cy = h / 2;
                int paddingRight = 1;
                int cxRight = Math.Max(arrowW + 1, Math.Min(w - paddingRight, w));
                int cxLeft = Math.Max(arrowW + 1, Math.Min(w - paddingRight, w));

                Color glyphColor;
                try { glyphColor = KryptonThemeBridge.MenuTextColor(); }
                catch { glyphColor = Color.DimGray; }
                using (var sb = new SolidBrush(glyphColor))
                {
                    var oldMode = e.Graphics.SmoothingMode;
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    Point[] tri;
                    if (pointRight)
                    {
                        tri = new[]
                        {
                            new Point(cxRight - arrowW, cy - arrowH/2),
                            new Point(cxRight - arrowW, cy + arrowH/2),
                            new Point(cxRight,            cy)
                        };
                    }
                    else
                    {
                        int cx = cxLeft - 1;
                        tri = new[]
                        {
                            new Point(cx,            cy - arrowH/2),
                            new Point(cx,            cy + arrowH/2),
                            new Point(cx - arrowW,   cy)
                        };
                    }
                    e.Graphics.FillPolygon(sb, tri);
                    e.Graphics.SmoothingMode = oldMode;
                }
            }
            catch { }
        }

        private void EnsureSidebarList()
        {
            try
            {
                if (_lvConversations != null) return;

                _lvConversations = new KryptonDataGridView();
                _lvConversations.ColumnHeadersVisible = false;   // no top header row
                _lvConversations.RowHeadersVisible = false;      // no left row-selector gutter
                _lvConversations.AllowUserToAddRows = false;
                _lvConversations.AllowUserToDeleteRows = false;
                _lvConversations.AllowUserToResizeRows = false;
                _lvConversations.AllowUserToResizeColumns = false;
                _lvConversations.AllowUserToOrderColumns = false;
                _lvConversations.ReadOnly = true;
                _lvConversations.EditMode = DataGridViewEditMode.EditProgrammatically;
                _lvConversations.MultiSelect = false;
                _lvConversations.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
                _lvConversations.BorderStyle = BorderStyle.None;
                _lvConversations.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
                _lvConversations.RowTemplate.Height = _sidebarRowHeight;
                _lvConversations.ShowCellToolTips = true;
                _lvConversations.ScrollBars = ScrollBars.None; // scrolling is driven by the KryptonScrollBar
                _lvConversations.Dock = DockStyle.Left;

                // Single text column that fills the client width (no horizontal scroll).
                var col = new DataGridViewTextBoxColumn();
                col.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                col.SortMode = DataGridViewColumnSortMode.NotSortable;
                col.Resizable = DataGridViewTriState.False;
                col.DefaultCellStyle.Padding = new Padding(4, 0, 0, 0);
                _lvConversations.Columns.Add(col);

                _lvConversations.CellDoubleClick += LvConversations_CellDoubleClick;
                _lvConversations.CellMouseUp += LvConversations_CellMouseUp;
                _lvConversations.MouseDown += LvConversations_MouseDown;
                _lvConversations.MouseWheel += LvConversations_MouseWheel;
                _lvConversations.SelectionChanged += delegate { UpdateSidebarScrollBar(); };
                _lvConversations.RowsAdded += delegate { UpdateSidebarScrollBar(); };
                _lvConversations.RowsRemoved += delegate { UpdateSidebarScrollBar(); };

                // Themed scrollbar overlaid in the reserved gutter to the left of the collapse arrow.
                _sidebarScrollBar = new KryptonScrollBar();
                _sidebarScrollBar.Orientation = ScrollBarOrientation.VERTICAL;
                _sidebarScrollBar.Width = SidebarScrollBarWidth;
                _sidebarScrollBar.Scroll += SidebarScrollBar_Scroll;
                _splitContainer.Panel1.Controls.Add(_sidebarScrollBar);

                // Create context menu but don't assign it directly
                _conversationContextMenu = new ContextMenuStrip();
                // Use a renderer that survives GDI+ failures while graying a disabled item's
                // image (the Delete item below carries an icon).
                try { _conversationContextMenu.Renderer = new SafeToolStripRenderer(); }
                catch { }
                var miOpen = new ToolStripMenuItem("Open");
                var miExport = new ToolStripMenuItem("Export");
                var miRename = new ToolStripMenuItem("Rename");
                var miDelete = new ToolStripMenuItem("Delete");
                var deleteImage = ResourceManager.TryGetAssemblyImage("ExplorerDelete.png");
                miOpen.Click += (s, e) => TryOpenSelectedConversation();
                miExport.Click += (s, e) => ExportSelectedConversation();
                miRename.Click += (s, e) => StartRenameSelectedConversation();
                miDelete.Click += (s, e) => DeleteSelectedConversation();
                miDelete.Image = deleteImage;
                _conversationContextMenu.Items.Add(miOpen);
                _conversationContextMenu.Items.Add(miExport);
                _conversationContextMenu.Items.Add(miRename);
                _conversationContextMenu.Items.Add(new ToolStripSeparator());
                _conversationContextMenu.Items.Add(miDelete);

                _sidebarRowHeight = Math.Max(_lvConversations.Font.Height + 8, 22);
                _lvConversations.RowTemplate.Height = _sidebarRowHeight;

                _lvConversations.Resize += (s, e) => ResizeSidebarColumn();
                _splitContainer.Panel1.Controls.Add(_lvConversations);

                // The initial population is deferred: it reads every conversation file's metadata off
                // disk (cold cache at launch), and the sidebar starts collapsed - nobody can see the
                // rows until it is expanded, well after the window is up. MainForm calls
                // PopulateInitialList() at the end of its post-Shown session restore (window first,
                // then tabs, then this). Until then RefreshSidebarList is a no-op (gated by
                // _sidebarListReady), so no startup code path can trigger the scan early.
                LayoutSidebarChildren();
                ApplyTheme(); // theme the freshly-created list immediately
            }
            catch { }
        }

        // Paint the sidebar to match the themed KryptonForm chrome: the Krypton client background color
        // (same as the composer/background panels) with the menu/label text color (same source as the
        // open/close glyph and the tab +/x glyphs). Called on every theme apply and at startup.
        public void ApplyTheme()
        {
            try
            {
                Color bg = KryptonThemeBridge.FormBackColor();
                Color fg = KryptonThemeBridge.MenuTextColor();

                if (_splitContainer != null && _splitContainer.Panel1 != null)
                    _splitContainer.Panel1.BackColor = bg;
                if (_lvConversations != null)
                {
                    // A subtle themed row-selection highlight: lighten the background in dark mode,
                    // darken it in light mode.
                    bool dark = fg.GetBrightness() > bg.GetBrightness();
                    Color sel = dark ? ControlPaint.Light(bg, 0.5f) : ControlPaint.Dark(bg, 0.05f);

                    _lvConversations.BackColor = bg;
                    _lvConversations.ForeColor = fg;
                    _lvConversations.BackgroundColor = bg;          // area below the rows
                    _lvConversations.GridColor = bg;                // hide gridlines by matching bg
                    _lvConversations.CellBorderStyle = DataGridViewCellBorderStyle.None;
                    // KryptonDataGridView draws its own palette cell borders on top of the above; turn them
                    // off so the conversation rows have no outlines.
                    try { _lvConversations.StateCommon.DataCell.Border.DrawBorders = PaletteDrawBorders.None; }
                    catch { }
                    var cs = _lvConversations.DefaultCellStyle;
                    cs.BackColor = bg;
                    cs.ForeColor = fg;
                    cs.SelectionBackColor = sel;
                    cs.SelectionForeColor = fg;
                }
                if (_sidebarArrowPanel != null)
                {
                    _sidebarArrowPanel.BackColor = bg;
                    _sidebarArrowPanel.Invalidate();
                }
                if (_renameHostPanel != null) _renameHostPanel.BackColor = bg;
                if (_renameTextBox != null)
                {
                    _renameTextBox.BackColor = bg;
                    _renameTextBox.ForeColor = fg;
                }
            }
            catch { }
        }

        // One-time initial population, called by MainForm at the end of its post-Shown session
        // restore. Ungates RefreshSidebarList (a no-op until now, see EnsureSidebarList) and builds
        // the rows for the first time.
        public void PopulateInitialList()
        {
            _sidebarListReady = true;
            try
            {
                RefreshSidebarList();
                UpdateSidebarScrollBar();
            }
            catch { }
        }

        public void RefreshSidebarList()
        {
            try
            {
                if (_lvConversations == null) return;
                // No-op until PopulateInitialList has run (post-Shown): the list can't be seen before
                // the window is up, and ListAll() on a cold cache is the launch's single most
                // expensive disk scan. See EnsureSidebarList.
                if (!_sidebarListReady) return;
                var items = ConversationStore.ListAll();
                _lvConversations.SuspendLayout();
                try
                {
                    _lvConversations.Rows.Clear();
                    foreach (var it in items)
                    {
                        string text = string.IsNullOrEmpty(it.Name) ? "New Conversation" : it.Name;
                        if (it.Zdr) text = MainForm.ZdrTitlePrefix + text;
                        int idx = _lvConversations.Rows.Add(text);
                        var row = _lvConversations.Rows[idx];
                        row.Tag = it;
                        row.Height = _sidebarRowHeight;
                    }
                    // No auto-selection until the user clicks (mirrors the old ListView).
                    _lvConversations.ClearSelection();
                    try { _lvConversations.CurrentCell = null; }
                    catch { }
                    ResizeSidebarColumn();
                }
                finally
                {
                    _lvConversations.ResumeLayout();
                }
            }
            catch { }
        }

        private void LayoutSidebarChildren()
        {
            try
            {
                if (_splitContainer == null || _lvConversations == null) return;
                int arrowW = (_sidebarArrowPanel != null ? _sidebarArrowPanel.Width : 0);
                int sbW = (_sidebarScrollBar != null ? SidebarScrollBarWidth : 0);
                int panelW = _splitContainer.Panel1.ClientSize.Width;
                int panelH = _splitContainer.Panel1.ClientSize.Height;
                // Reserve a fixed gutter for the scrollbar between the list and the collapse arrow.
                int targetW = Math.Max(0, panelW - arrowW - sbW);
                if (_lvConversations.Dock != DockStyle.Left) _lvConversations.Dock = DockStyle.Left;
                if (_lvConversations.Width != targetW) _lvConversations.Width = targetW;

                if (_sidebarScrollBar != null)
                {
                    _sidebarScrollBar.Bounds = new Rectangle(targetW, 0, sbW, panelH);
                    _sidebarScrollBar.BringToFront();
                }
                UpdateSidebarScrollBar();
            }
            catch { }
        }

        // Reflect the grid's vertical scroll state onto the KryptonScrollBar (row-based): show/enable it
        // only when the rows overflow the viewport, and size the thumb by the number of visible rows.
        private void UpdateSidebarScrollBar()
        {
            if (_syncingSidebarScroll) return;
            try
            {
                if (_lvConversations == null || _sidebarScrollBar == null) return;

                // Never show the scrollbar while the sidebar is collapsed or animating - it would sit over
                // the collapse-arrow strip and make it un-clickable to reopen.
                if (!_sidebarExpanded || _sidebarAnimating)
                {
                    try { _sidebarScrollBar.Visible = false; }
                    catch { }
                    return;
                }

                int rowCount = _lvConversations.Rows.Count;
                int viewport = Math.Max(0, _lvConversations.ClientSize.Height);
                int rowH = Math.Max(1, _sidebarRowHeight);
                int visibleRows = Math.Max(1, viewport / rowH);
                bool needed = rowCount > visibleRows;

                _sidebarScrollBar.Visible = needed;
                _sidebarScrollBar.Enabled = needed;
                if (!needed) return;

                int maxFirst = Math.Max(0, rowCount - visibleRows);
                int first = 0;
                try { first = Math.Max(0, _lvConversations.FirstDisplayedScrollingRowIndex); }
                catch { }
                first = Math.Min(first, maxFirst);

                _syncingSidebarScroll = true;
                try
                {
                    _sidebarScrollBar.Minimum = 0;
                    // KryptonScrollBar's Value ranges over [Minimum, Maximum] (thumb bottom at Value ==
                    // Maximum), so Maximum IS the last first-displayed-row index - not maxFirst+LargeChange-1.
                    _sidebarScrollBar.Maximum = maxFirst;
                    // Thumb size is largeChange/Maximum of the track; the proportional page size
                    // (maxFirst * visibleRows / rowCount) makes the thumb reflect the visible fraction and
                    // keeps it off the arrow buttons.
                    int page = (rowCount > 0)
                        ? (int)Math.Round((double)maxFirst * visibleRows / rowCount)
                        : visibleRows;
                    page = Math.Max(2, Math.Min(Math.Max(2, maxFirst), page));
                    _sidebarScrollBar.SmallChange = 1;
                    _sidebarScrollBar.LargeChange = page;

                    // KryptonScrollBar doesn't reposition its thumb on a programmatic Value change; the
                    // bridge writes the backing value and forces the reposition so wheel syncs the thumb.
                    KryptonThemeBridge.SetScrollBarValue(_sidebarScrollBar, Math.Max(0, Math.Min(maxFirst, first)));
                }
                catch { }
                finally { _syncingSidebarScroll = false; }
            }
            catch { }
        }

        // Drive the grid from the scrollbar: set the first displayed row to the (clamped) scrollbar value.
        private void SidebarScrollBar_Scroll(object sender, ScrollEventArgs e)
        {
            if (_syncingSidebarScroll) return;
            try
            {
                if (_lvConversations == null) return;
                int rowCount = _lvConversations.Rows.Count;
                if (rowCount == 0) return;
                int viewport = Math.Max(0, _lvConversations.ClientSize.Height);
                int rowH = Math.Max(1, _sidebarRowHeight);
                int visibleRows = Math.Max(1, viewport / rowH);
                int maxFirst = Math.Max(0, rowCount - visibleRows);
                int target = Math.Max(0, Math.Min(maxFirst, _sidebarScrollBar.Value));
                try { _lvConversations.FirstDisplayedScrollingRowIndex = target; }
                catch { }
            }
            catch { }
        }

        // The grid's native wheel scrolling is off (ScrollBars=None), so scroll it here and resync the bar.
        private void LvConversations_MouseWheel(object sender, MouseEventArgs e)
        {
            try
            {
                if (_lvConversations == null) return;
                int rowCount = _lvConversations.Rows.Count;
                if (rowCount == 0) return;
                int viewport = Math.Max(0, _lvConversations.ClientSize.Height);
                int rowH = Math.Max(1, _sidebarRowHeight);
                int visibleRows = Math.Max(1, viewport / rowH);
                int maxFirst = Math.Max(0, rowCount - visibleRows);
                if (maxFirst <= 0) return;

                int lines = SystemInformation.MouseWheelScrollLines;
                if (lines <= 0) lines = 3;
                int first = Math.Max(0, _lvConversations.FirstDisplayedScrollingRowIndex);
                int target = Math.Max(0, Math.Min(maxFirst, first - (e.Delta / 120) * lines));
                try { _lvConversations.FirstDisplayedScrollingRowIndex = target; }
                catch { }
                UpdateSidebarScrollBar();
            }
            catch { }
        }

        // The currently-selected conversation row (or null). DataGridView keeps a CurrentRow even when
        // the selection is cleared, so prefer an explicit selection and fall back to CurrentRow.
        private DataGridViewRow GetSelectedRow()
        {
            try
            {
                if (_lvConversations == null) return null;
                if (_lvConversations.SelectedRows.Count > 0) return _lvConversations.SelectedRows[0];
                if (_lvConversations.CurrentRow != null && _lvConversations.CurrentRow.Selected)
                    return _lvConversations.CurrentRow;
            }
            catch { }
            return null;
        }

        private void LvConversations_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            try
            {
                if (e.RowIndex < 0) return;
                _lvConversations.Rows[e.RowIndex].Selected = true;
                TryOpenSelectedConversation();
            }
            catch { }
        }

        private void LvConversations_MouseDown(object sender, MouseEventArgs e)
        {
            // If we're renaming and the user clicks a different row (or empty space), finish the rename.
            if (_renamingItem == null) return;
            try
            {
                var hit = _lvConversations.HitTest(e.X, e.Y);
                if (hit.RowIndex != _renamingItem.Index)
                    FinishRename(true);
            }
            catch { }
        }

        private void LvConversations_CellMouseUp(object sender, DataGridViewCellMouseEventArgs e)
        {
            // Only show the context menu for right-clicks that hit an actual row.
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            try
            {
                _lvConversations.Rows[e.RowIndex].Selected = true;
                if (_conversationContextMenu != null)
                {
                    // e.Location is cell-relative; offset to the cell's position for the menu anchor.
                    Rectangle cr = _lvConversations.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
                    _conversationContextMenu.Show(_lvConversations, new Point(cr.X + e.X, cr.Y + e.Y));
                }
            }
            catch { }
        }

        private void TryOpenSelectedConversation()
        {
            try
            {
                var lvi = GetSelectedRow();
                if (lvi == null) return;
                var info = lvi.Tag as ConversationStore.ConversationListItem;
                if (info == null) return;

                KryptonPage page;
                if (!string.IsNullOrEmpty(info.Id) && _openConversationsById.TryGetValue(info.Id, out page))
                {
                    _mainForm.SelectTab(page);
                    return;
                }

                var convo = ConversationStore.Load(_mainForm.GetClient(), info.Path);
                if (convo == null) return;

                _mainForm.OpenConversation(convo);
            }
            catch { }
        }

        private void DeleteSelectedConversation()
        {
            try
            {
                var lvi = GetSelectedRow();
                if (lvi == null) return;
                var info = lvi.Tag as ConversationStore.ConversationListItem;
                if (info == null) return;

                KryptonPage openPage;
                if (!string.IsNullOrEmpty(info.Id) && _openConversationsById.TryGetValue(info.Id, out openPage))
                {
                    _mainForm.CloseTab(openPage);
                }

                ConversationStore.DeletePath(info.Path);
                // Remove the conversation's scratch working directory (if any), stopping its command
                // server first so the directory isn't held open.
                try { if (!string.IsNullOrEmpty(info.Id)) _mainForm.DeleteScratchForConversation(info.Id); }
                catch { }
                RefreshSidebarList();
            }
            catch { }
        }

        private void ExportSelectedConversation()
        {
            try
            {
                var lvi = GetSelectedRow();
                if (lvi == null) return;
                var info = lvi.Tag as ConversationStore.ConversationListItem;
                if (info == null) return;

                ImportExportManager.ExportSingle(_mainForm, info);
            }
            catch { }
        }

        private void StartRenameSelectedConversation()
        {
            try
            {
                var lvi = GetSelectedRow();
                if (lvi == null) return;
                var info = lvi.Tag as ConversationStore.ConversationListItem;
                if (info == null) return;

                // Don't allow multiple renames at once
                if (_renamingItem != null) return;

                _renamingItem = lvi;

                // Create textbox for inline editing (a subclass that keeps every key to itself -
                // see RenameTextBoxControl for why a plain TextBox loses arrow keys here).
                _renameTextBox = new RenameTextBoxControl();
                // Seed with the raw conversation name, not the displayed row text: the latter
                // carries the "[zdr] " marker prefix, which must not become part of the name.
                string editText = Convert.ToString(lvi.Cells[0].Value);
                if (info.Zdr && editText != null && editText.StartsWith(MainForm.ZdrTitlePrefix))
                {
                    editText = editText.Substring(MainForm.ZdrTitlePrefix.Length);
                }
                _renameTextBox.Text = editText;
                _renameTextBox.BorderStyle = BorderStyle.None;
                // Allow explicit height by using multiline (single-line behavior preserved by AcceptsReturn=false)
                _renameTextBox.Multiline = true;
                _renameTextBox.AcceptsReturn = false;
                _renameTextBox.AutoSize = false;
                _renameTextBox.Margin = new Padding(0);
                _renameTextBox.BackColor = _lvConversations.BackColor;
                _renameTextBox.ForeColor = _lvConversations.ForeColor;

                // Apply the same font size from settings that the ListView uses
                try
                {
                    double fs = AppSettings.GetDouble("font_size", 0);
                    if (fs > 0)
                    {
                        float size = (float)Math.Max(6, Math.Min(48, fs));
                        _renameTextBox.Font = new Font(_lvConversations.Font.FontFamily, size, _lvConversations.Font.Style);
                    }
                    else
                    {
                        _renameTextBox.Font = _lvConversations.Font;
                    }
                }
                catch
                {
                    _renameTextBox.Font = _lvConversations.Font;
                }

                // Position a host panel to cover the entire row, then place the textbox inside aligned to the label area
                Rectangle rowRect;
                Rectangle labelRect;
                try
                {
                    rowRect = _lvConversations.GetRowDisplayRectangle(lvi.Index, false);
                }
                catch { rowRect = Rectangle.Empty; }
                try
                {
                    labelRect = _lvConversations.GetCellDisplayRectangle(0, lvi.Index, false);
                }
                catch { labelRect = rowRect; }

                int left = Math.Max(0, labelRect.X + 4); // matches the cell's left content padding
                int panelTop = Math.Max(0, rowRect.Y);
                int panelHeight = Math.Max(1, rowRect.Height);

                // Create/position host panel to cover the row
                _renameHostPanel = new Panel();
                _renameHostPanel.Margin = new Padding(0);
                _renameHostPanel.Padding = new Padding(0);
                _renameHostPanel.BackColor = _lvConversations.BackColor;
                _renameHostPanel.Bounds = new Rectangle(0, panelTop, _lvConversations.ClientSize.Width, panelHeight);

                // Measure single line text height for vertical centering
                int textHeight;
                try { textHeight = TextRenderer.MeasureText("Ag", _renameTextBox.Font).Height; }
                catch { textHeight = _renameTextBox.Font.Height + 2; }
                int tbHeight = Math.Min(panelHeight, Math.Max(_renameTextBox.Font.Height + 2, textHeight));
                int tbTop = Math.Max(0, (panelHeight - tbHeight) / 2);
                int tbWidth = Math.Max(20, _lvConversations.ClientSize.Width - left);
                _renameTextBox.Bounds = new Rectangle(left, tbTop, tbWidth, tbHeight);

                // Wire up events. The textbox is parented inside the DataGridView, which treats
                // Enter/Escape as dialog keys (row navigation / cancel) and processes them BEFORE
                // the textbox's KeyDown can see them - claiming them as input keys in PreviewKeyDown
                // routes them to RenameTextBox_KeyDown instead. (The old ListView host never
                // intercepted these, so plain KeyDown sufficed there.)
                _renameTextBox.PreviewKeyDown += RenameTextBox_PreviewKeyDown;
                _renameTextBox.KeyDown += RenameTextBox_KeyDown;
                _renameTextBox.LostFocus += RenameTextBox_LostFocus;

                // Add to host panel, then to the grid, and focus. Focus/selection are DEFERRED
                // (BeginInvoke) so they run after the context menu that launched the rename has
                // fully closed: focusing synchronously races the menu's modal message filter
                // teardown, which can leave the first rename of the session with a focused-looking
                // textbox that doesn't receive caret keys.
                _renameHostPanel.Controls.Add(_renameTextBox);
                _lvConversations.Controls.Add(_renameHostPanel);
                _renameHostPanel.BringToFront();
                _renameTextBox.BringToFront();
                try
                {
                    TextBox tb = _renameTextBox;
                    _lvConversations.BeginInvoke((MethodInvoker)delegate
                    {
                        try
                        {
                            // Still the active rename? (A fast Escape/click could have closed it.)
                            if (tb == null || !object.ReferenceEquals(tb, _renameTextBox)) return;
                            tb.SelectAll();
                            tb.Focus();
                        }
                        catch { }
                    });
                }
                catch
                {
                    _renameTextBox.SelectAll();
                    _renameTextBox.Focus();
                }
            }
            catch { }
        }

        // The inline rename editor. A plain TextBox parented inside the DataGridView loses its
        // navigation keys: after a key is delivered to a focused child, WinForms offers it up the
        // parent chain via ProcessKeyPreview, and DataGridView's override consumes arrows/Home/End
        // as grid navigation (Enter/Escape only while a real cell editor is active - which is why
        // those reached KeyDown but arrows never did; the perf trace showed PreviewKeyDown firing
        // with no KeyDown following). Overriding ProcessKeyMessage to skip the parent preview keeps
        // every key local to the editor. (The pre-Krypton ListView host had no key preview, which
        // is why this never happened before the migration.)
        private sealed class RenameTextBoxControl : TextBox
        {
            protected override bool ProcessKeyMessage(ref Message m)
            {
                return ProcessKeyEventArgs(ref m);
            }
        }

        private void RenameTextBox_PreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            // Keep editing keys in the textbox instead of an ancestor's command/dialog-key
            // processing: the hosting DataGridView treats Enter (row navigation), Escape, and the
            // caret keys as navigation, and key preprocessing could swallow them before the
            // textbox's KeyDown fires (observed: the first rename after launch lost arrow keys
            // entirely). Claiming them as input keys delivers them to the textbox.
            switch (e.KeyCode)
            {
                case Keys.Enter:
                case Keys.Escape:
                case Keys.Left:
                case Keys.Right:
                case Keys.Up:
                case Keys.Down:
                case Keys.Home:
                case Keys.End:
                    e.IsInputKey = true;
                    break;
            }
        }

        private void RenameTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                FinishRename(true);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                FinishRename(false);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void RenameTextBox_LostFocus(object sender, EventArgs e)
        {
            FinishRename(true);
        }

        private void FinishRename(bool saveChanges)
        {
            try
            {
                if (_renameTextBox == null || _renamingItem == null) return;

                string newName = saveChanges ? _renameTextBox.Text.Trim() : null;
                string originalName = Convert.ToString(_renamingItem.Cells[0].Value);

                // Remove the textbox and its host panel
                try
                {
                    if (_renameTextBox != null)
                    {
                        if (_renameTextBox.Parent != null) _renameTextBox.Parent.Controls.Remove(_renameTextBox);
                        _renameTextBox.Dispose();
                    }
                }
                catch { }
                _renameTextBox = null;
                try
                {
                    if (_renameHostPanel != null)
                    {
                        _lvConversations.Controls.Remove(_renameHostPanel);
                        _renameHostPanel.Dispose();
                    }
                }
                catch { }
                _renameHostPanel = null;

                // Only update if we're saving changes, the new name is valid, and it's actually different
                if (saveChanges && !string.IsNullOrEmpty(newName) && newName != originalName)
                {
                    var info = _renamingItem.Tag as ConversationStore.ConversationListItem;
                    if (info != null)
                    {
                        // When the conversation is open in a tab, rename the LIVE object - it is the
                        // authoritative copy for that tab. Renaming a disk-loaded clone here used to
                        // be silently undone by the tab's next send, which saved the old in-memory
                        // name back over this one. Closed conversations still load from disk.
                        Conversation conversation = null;
                        KryptonPage openPage = null;
                        if (!string.IsNullOrEmpty(info.Id) && _openConversationsById.TryGetValue(info.Id, out openPage))
                        {
                            var tm = _mainForm.GetTabManager();
                            TabManager.ChatTabContext ctx;
                            if (tm != null && openPage != null &&
                                tm.TabContexts.TryGetValue(openPage, out ctx) && ctx != null)
                                conversation = ctx.Conversation;
                        }
                        if (conversation == null)
                            conversation = ConversationStore.Load(_mainForm.GetClient(), info.Path);
                        if (conversation != null)
                        {
                            conversation.Name = newName;
                            // A save that races an open tab's running turn can throw; the in-memory
                            // rename has already stuck, and that tab's next save persists it.
                            try { ConversationStore.Save(conversation); }
                            catch { }

                            // Update any open tab with this conversation
                            if (openPage != null)
                            {
                                openPage.Text = MainForm.ZdrTitle(conversation, newName);
                                _mainForm.UpdateWindowTitle();
                            }

                            // When saving changes (Enter pressed), allow the list to resort by refreshing
                            _renamingItem = null;
                            RefreshSidebarList();
                            return;
                        }
                    }
                }

                // If we didn't save changes (Escape), no changes were made, or failed to save,
                // just clear the rename state without refreshing the list (preserves position and timestamp)
                _renamingItem = null;
            }
            catch { }
        }

        private void ResizeSidebarColumn()
        {
            try
            {
                if (_lvConversations == null || _lvConversations.Columns.Count == 0) return;
                // The single column is AutoSizeMode=Fill, so it already tracks the client width; just
                // refresh the truncation tooltips for the new width.
                UpdateSidebarTooltips();
            }
            catch { }
        }

        // Show a hover tooltip with the full conversation name, but only for items
        // whose name is too long to fit and is therefore truncated with an ellipsis.
        private void UpdateSidebarTooltips()
        {
            try
            {
                if (_lvConversations == null || _lvConversations.Columns.Count == 0) return;
                // Available text width inside the column, leaving room for the cell's left padding.
                int available = _lvConversations.Columns[0].Width - 8;
                foreach (DataGridViewRow row in _lvConversations.Rows)
                {
                    if (row == null || row.IsNewRow) continue;
                    var cell = row.Cells[0];
                    string text = Convert.ToString(cell.Value);
                    int textWidth = TextRenderer.MeasureText(text, _lvConversations.Font).Width;
                    string tip = (textWidth > available) ? text : string.Empty;
                    if (cell.ToolTipText != tip) cell.ToolTipText = tip;
                }
            }
            catch { }
        }

        private void UpdateConversationHistoryCheckedState()
        {
            try
            {
                if (_miConversationHistory != null)
                {
                    _miConversationHistory.Checked = _sidebarExpanded;
                }
            }
            catch { }
        }

        // Update the tooltip on the arrow strip to indicate the action on click
        private void UpdateArrowToolTip()
        {
            try
            {
                if (_sidebarToolTip == null || _sidebarArrowPanel == null) return;
                bool willExpand;
                if (_sidebarAnimating)
                {
                    willExpand = _sidebarTargetWidth > _sidebarStartWidth;
                }
                else
                {
                    willExpand = !_sidebarExpanded;
                }
                string text = willExpand ? "Expand conversations" : "Collapse conversations";
                _sidebarToolTip.SetToolTip(_sidebarArrowPanel, text);
            }
            catch { }
        }

        public void TrackOpenConversation(string conversationId, KryptonPage page)
        {
            try
            {
                if (!string.IsNullOrEmpty(conversationId))
                    _openConversationsById[conversationId] = page;
            }
            catch { }
        }

        public void UntrackOpenConversation(KryptonPage page)
        {
            try
            {
                var toRemove = _openConversationsById.Where(kv => object.ReferenceEquals(kv.Value, page))
                    .Select(kv => kv.Key).ToList();
                foreach (var k in toRemove)
                    _openConversationsById.Remove(k);
            }
            catch { }
        }

        public void ApplyFontSetting()
        {
            try
            {
                if (_lvConversations == null) return;
                double fs = AppSettings.GetDouble("font_size", 0);
                if (fs <= 0) return;
                float size = (float)Math.Max(6, Math.Min(48, fs));

                try { _lvConversations.Font = new Font(_lvConversations.Font.FontFamily, size, _lvConversations.Font.Style); }
                catch { }

                // KryptonDataGridView paints cell text with the PALETTE's data-cell font, not the
                // control Font set above (which only drives row-height math here) - so without these
                // the rows stayed at the default size while e.g. the rename textbox scaled. Set the
                // font on both the Krypton cell state and the grid's DefaultCellStyle so the drawn
                // rows follow the setting whichever path resolves the style.
                try { _lvConversations.StateCommon.DataCell.Content.Font = _lvConversations.Font; }
                catch { }
                try { _lvConversations.DefaultCellStyle.Font = _lvConversations.Font; }
                catch { }

                _sidebarRowHeight = Math.Max(_lvConversations.Font.Height + 8, 22);
                try
                {
                    _lvConversations.RowTemplate.Height = _sidebarRowHeight;
                    foreach (DataGridViewRow row in _lvConversations.Rows)
                    {
                        if (row == null || row.IsNewRow) continue;
                        row.Height = _sidebarRowHeight;
                    }
                }
                catch { }

                // Re-apply the font-scaled expanded width if currently expanded
                try
                {
                    if (_sidebarExpanded && !_sidebarAnimating && _splitContainer != null)
                    {
                        int expandedW = GetExpandedWidth();
                        if (_splitContainer.SplitterDistance != expandedW)
                        {
                            _splitContainer.SplitterDistance = expandedW;
                            LayoutSidebarChildren();
                        }
                    }
                }
                catch { }

                try { ResizeSidebarColumn(); }
                catch { }
                try { UpdateSidebarScrollBar(); } // row height changed -> recompute visible rows / thumb
                catch { }
                try { _lvConversations.Invalidate(); _lvConversations.Update(); }
                catch { }
            }
            catch { }
        }
    }
}
