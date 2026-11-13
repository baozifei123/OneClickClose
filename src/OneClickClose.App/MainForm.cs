using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OneClickClose
{
    public sealed class MainForm : Form
    {
        private const int ColCheck = 0;
        private const int ColIcon = 1;
        private const int ColName = 2;
        private const int ColProcess = 3;
        private const int ColWindowCount = 4;
        private const int ColKind = 5;
        private const int ColTitle = 6;
        private const int ColPermission = 7;
        private const int ColResult = 8;

        private static readonly string[] DangerProcessNames =
        {
            "windowsterminal", "cmd", "powershell", "pwsh", "conhost", "openconsole"
        };

        private readonly ConfigStore _configStore;
        private readonly ProcessScanner _scanner = new ProcessScanner();
        private readonly CloseService _closeService = new CloseService();
        private readonly Dictionary<string, Image> _iconCache =
            new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<IntPtr>> _windowSelections =
            new Dictionary<string, HashSet<IntPtr>>(StringComparer.OrdinalIgnoreCase);
        private readonly object _iconLock = new object();
        private readonly bool _currentElevated = ProcessPrivilege.IsCurrentProcessElevated();

        private DataGridView _grid;
        private Label _statusLabel;
        private TextBox _searchBox;
        private RadioButton _radioWindows;
        private RadioButton _radioAll;
        private CheckBox _showAllWindowsCheckBox;
        private CheckBox _includeChildrenCheckBox;
        private Button _refreshButton;
        private Button _settingsButton;
        private Button _closeButton;
        private Button _previewButton;
        private Button _protectSelectedButton;
        private Button _selectAllButton;
        private Button _selectNoneButton;
        private ComboBox _profileCombo;
        private Button _profileApplyButton;
        private Button _profileSaveButton;
        private Button _profileDeleteButton;
        private Button _profileRestoreButton;
        private ContextMenuStrip _gridMenu;
        private ToolStripMenuItem _protectItem;
        private ToolStripMenuItem _unprotectItem;
        private ToolStripMenuItem _windowSelectionItem;
        private ToolStripMenuItem _pathProtectItem;
        private ToolStripMenuItem _pathUnprotectItem;
        private ToolStripMenuItem _settingsItem;
        private ToolStripMenuItem _adminRestartItem;
        private NotifyIcon _trayIcon;
        private HotkeyWindow _hotkeyWindow;
        private Font _baseFont;
        private Font _boldFont;
        private Image _genericIcon;

        private string _currentMode;
        private bool _uiReady;
        private bool _busy;
        private bool _refreshing;
        private bool _reallyExit;
        private bool _suppressGridEvents;
        private bool _suppressModeChange;
        private bool _suppressViewSave;
        private readonly bool _startedHidden;
        private int _contextRowIndex = -1;
        private CancellationTokenSource _refreshCts;
        private string _scanMode;
        private string _pendingMode;
        private IntPtr _pendingForeground;

        private AppConfig Config
        {
            get { return _configStore.Current; }
        }

        internal int RowCountForTest
        {
            get { return _grid == null ? 0 : _grid.Rows.Count; }
        }

        internal bool IsRefreshingForTest
        {
            get { return _refreshing; }
        }

        internal Task RefreshForTestAsync(string mode)
        {
            return RefreshAsync(mode, NativeMethods.GetForegroundWindow(), true);
        }

        public MainForm(ConfigStore configStore, bool startHidden)
        {
            if (configStore == null)
            {
                throw new ArgumentNullException("configStore");
            }

            _configStore = configStore;
            _startedHidden = startHidden;
            _currentMode = string.Equals(Config.LastMode, "all", StringComparison.OrdinalIgnoreCase)
                ? "all"
                : "windows";

            BuildUi();
            InitializeTray();
            InitializeHotkey();

            _uiReady = true;
            RefreshProfileList();
            UpdateSelectionStatus();
            UpdateControlAvailability();

            Shown += MainForm_Shown;
            if (!string.IsNullOrEmpty(_configStore.LastLoadError))
            {
                SetStatus("配置读取失败；可点【恢复备份】：" + _configStore.LastLoadError, Color.Firebrick);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_reallyExit)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_refreshCts != null)
                {
                    try { _refreshCts.Cancel(); }
                    catch { }
                    _refreshCts = null;
                }
                if (_hotkeyWindow != null)
                {
                    try { _hotkeyWindow.Dispose(); }
                    catch { }
                    _hotkeyWindow = null;
                }
                if (_trayIcon != null)
                {
                    try
                    {
                        _trayIcon.Visible = false;
                        _trayIcon.Dispose();
                    }
                    catch { }
                    _trayIcon = null;
                }
            }
            base.Dispose(disposing);
        }

        private void BuildUi()
        {
            Text = "清场助手 — 选择要关闭的程序";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1040, 640);
            MinimumSize = new Size(860, 440);
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.Font;

            _baseFont = new Font("Microsoft YaHei UI", 9f);
            _boldFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            _genericIcon = SystemIcons.Application.ToBitmap();
            Font = _baseFont;
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                Icon = SystemIcons.Application;
            }

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.Padding = new Padding(10);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));
            Controls.Add(root);

            FlowLayoutPanel top = new FlowLayoutPanel();
            top.Dock = DockStyle.Fill;
            top.FlowDirection = FlowDirection.LeftToRight;
            top.WrapContents = true;
            top.Padding = new Padding(0, 4, 0, 0);
            root.Controls.Add(top, 0, 0);

            _radioWindows = new RadioButton();
            _radioWindows.Text = "只关窗口（保留托盘）";
            _radioWindows.AutoSize = true;
            _radioWindows.Margin = new Padding(0, 4, 14, 0);
            _radioWindows.Checked = _currentMode == "windows";
            top.Controls.Add(_radioWindows);

            _radioAll = new RadioButton();
            _radioAll.Text = "彻底退出（含后台）";
            _radioAll.AutoSize = true;
            _radioAll.Margin = new Padding(0, 4, 20, 0);
            _radioAll.Checked = _currentMode == "all";
            top.Controls.Add(_radioAll);

            _showAllWindowsCheckBox = new CheckBox();
            _showAllWindowsCheckBox.Text = "显示所有窗口";
            _showAllWindowsCheckBox.AutoSize = true;
            _showAllWindowsCheckBox.Margin = new Padding(0, 4, 14, 0);
            _showAllWindowsCheckBox.Checked = Config.ShowAllWindows;
            top.Controls.Add(_showAllWindowsCheckBox);

            _includeChildrenCheckBox = new CheckBox();
            _includeChildrenCheckBox.Text = "包含子进程";
            _includeChildrenCheckBox.AutoSize = true;
            _includeChildrenCheckBox.Margin = new Padding(0, 4, 14, 0);
            _includeChildrenCheckBox.Checked = Config.IncludeChildProcesses;
            _includeChildrenCheckBox.Enabled = _currentMode == "all";
            _includeChildrenCheckBox.ForeColor = Color.DimGray;
            top.Controls.Add(_includeChildrenCheckBox);

            Label filterLabel = new Label();
            filterLabel.Text = "筛选：";
            filterLabel.AutoSize = true;
            filterLabel.Margin = new Padding(0, 7, 4, 0);
            top.Controls.Add(filterLabel);

            _searchBox = new TextBox();
            _searchBox.Width = 190;
            _searchBox.Margin = new Padding(0, 4, 14, 0);
            top.Controls.Add(_searchBox);

            _refreshButton = new Button();
            _refreshButton.Text = "刷新 (F5)";
            _refreshButton.AutoSize = true;
            _refreshButton.Margin = new Padding(0, 3, 6, 0);
            top.Controls.Add(_refreshButton);

            _settingsButton = new Button();
            _settingsButton.Text = "设置";
            _settingsButton.AutoSize = true;
            _settingsButton.Margin = new Padding(0, 3, 0, 0);
            top.Controls.Add(_settingsButton);

            FlowLayoutPanel profilePanel = new FlowLayoutPanel();
            profilePanel.Dock = DockStyle.Fill;
            profilePanel.FlowDirection = FlowDirection.LeftToRight;
            profilePanel.WrapContents = false;
            profilePanel.Padding = new Padding(0, 5, 0, 0);
            root.Controls.Add(profilePanel, 0, 1);

            Label profileLabel = new Label();
            profileLabel.Text = "保护预设：";
            profileLabel.AutoSize = true;
            profileLabel.Margin = new Padding(0, 4, 4, 0);
            profilePanel.Controls.Add(profileLabel);

            _profileCombo = new ComboBox();
            _profileCombo.Width = 150;
            _profileCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _profileCombo.Margin = new Padding(0, 1, 6, 0);
            profilePanel.Controls.Add(_profileCombo);

            _profileApplyButton = new Button();
            _profileApplyButton.Text = "应用";
            _profileApplyButton.AutoSize = true;
            _profileApplyButton.Margin = new Padding(0, 0, 4, 0);
            profilePanel.Controls.Add(_profileApplyButton);

            _profileSaveButton = new Button();
            _profileSaveButton.Text = "保存当前名单";
            _profileSaveButton.AutoSize = true;
            _profileSaveButton.Margin = new Padding(0, 0, 4, 0);
            profilePanel.Controls.Add(_profileSaveButton);

            _profileDeleteButton = new Button();
            _profileDeleteButton.Text = "删除";
            _profileDeleteButton.AutoSize = true;
            _profileDeleteButton.Margin = new Padding(0, 0, 4, 0);
            profilePanel.Controls.Add(_profileDeleteButton);

            _profileRestoreButton = new Button();
            _profileRestoreButton.Text = "恢复备份";
            _profileRestoreButton.AutoSize = true;
            _profileRestoreButton.Margin = new Padding(0, 0, 8, 0);
            profilePanel.Controls.Add(_profileRestoreButton);

            Label profileHint = new Label();
            profileHint.Text = "应用会替换当前名单，不会关闭程序";
            profileHint.AutoSize = true;
            profileHint.ForeColor = Color.DimGray;
            profileHint.Margin = new Padding(0, 5, 0, 0);
            profilePanel.Controls.Add(profileHint);

            _grid = new DataGridView();
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = false;
            _grid.BackgroundColor = Color.White;
            _grid.BorderStyle = BorderStyle.None;
            _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            _grid.ColumnHeadersHeight = 30;
            _grid.RowTemplate.Height = 30;
            _grid.EditMode = DataGridViewEditMode.EditOnEnter;
            _grid.Font = _baseFont;
            _grid.GridColor = Color.Gainsboro;
            root.Controls.Add(_grid, 0, 2);

            DataGridViewCheckBoxColumn checkColumn = new DataGridViewCheckBoxColumn();
            checkColumn.HeaderText = string.Empty;
            checkColumn.Width = 40;
            checkColumn.Name = "colCheck";
            _grid.Columns.Add(checkColumn);

            DataGridViewImageColumn iconColumn = new DataGridViewImageColumn();
            iconColumn.HeaderText = string.Empty;
            iconColumn.Width = 36;
            iconColumn.Name = "colIcon";
            iconColumn.ImageLayout = DataGridViewImageCellLayout.Zoom;
            iconColumn.DefaultCellStyle.NullValue = new Bitmap(1, 1);
            _grid.Columns.Add(iconColumn);

            DataGridViewTextBoxColumn nameColumn = new DataGridViewTextBoxColumn();
            nameColumn.HeaderText = "程序";
            nameColumn.Width = 200;
            nameColumn.Name = "colName";
            nameColumn.ReadOnly = true;
            _grid.Columns.Add(nameColumn);

            DataGridViewTextBoxColumn processColumn = new DataGridViewTextBoxColumn();
            processColumn.HeaderText = "进程";
            processColumn.Width = 190;
            processColumn.Name = "colProcess";
            processColumn.ReadOnly = true;
            _grid.Columns.Add(processColumn);

            DataGridViewTextBoxColumn countColumn = new DataGridViewTextBoxColumn();
            countColumn.HeaderText = "窗口";
            countColumn.Width = 64;
            countColumn.Name = "colCount";
            countColumn.ReadOnly = true;
            _grid.Columns.Add(countColumn);

            DataGridViewTextBoxColumn kindColumn = new DataGridViewTextBoxColumn();
            kindColumn.HeaderText = "类型";
            kindColumn.Width = 72;
            kindColumn.Name = "colKind";
            kindColumn.ReadOnly = true;
            _grid.Columns.Add(kindColumn);

            DataGridViewTextBoxColumn titleColumn = new DataGridViewTextBoxColumn();
            titleColumn.HeaderText = "窗口标题";
            titleColumn.Name = "colTitle";
            titleColumn.ReadOnly = true;
            titleColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _grid.Columns.Add(titleColumn);

            DataGridViewTextBoxColumn permissionColumn = new DataGridViewTextBoxColumn();
            permissionColumn.HeaderText = "权限";
            permissionColumn.Width = 64;
            permissionColumn.Name = "colPermission";
            permissionColumn.ReadOnly = true;
            _grid.Columns.Add(permissionColumn);

            DataGridViewTextBoxColumn resultColumn = new DataGridViewTextBoxColumn();
            resultColumn.HeaderText = "结果";
            resultColumn.Width = 170;
            resultColumn.Name = "colResult";
            resultColumn.ReadOnly = true;
            _grid.Columns.Add(resultColumn);

            foreach (DataGridViewColumn column in _grid.Columns)
            {
                column.SortMode = DataGridViewColumnSortMode.NotSortable;
            }

            TableLayoutPanel bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Fill;
            bottom.ColumnCount = 2;
            bottom.RowCount = 1;
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.Controls.Add(bottom, 0, 3);

            _statusLabel = new Label();
            _statusLabel.Dock = DockStyle.Fill;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _statusLabel.ForeColor = Color.DimGray;
            _statusLabel.Text = "就绪";
            bottom.Controls.Add(_statusLabel, 0, 0);

            FlowLayoutPanel buttonPanel = new FlowLayoutPanel();
            buttonPanel.Dock = DockStyle.Fill;
            buttonPanel.AutoSize = true;
            buttonPanel.FlowDirection = FlowDirection.RightToLeft;
            buttonPanel.WrapContents = false;
            buttonPanel.Padding = new Padding(0, 7, 0, 0);
            bottom.Controls.Add(buttonPanel, 1, 0);

            _closeButton = new Button();
            _closeButton.Text = "关闭选中";
            _closeButton.AutoSize = true;
            _closeButton.BackColor = Color.FromArgb(0, 120, 215);
            _closeButton.ForeColor = Color.White;
            _closeButton.FlatStyle = FlatStyle.Flat;
            _closeButton.FlatAppearance.BorderSize = 0;
            _closeButton.Padding = new Padding(10, 2, 10, 2);
            _closeButton.Margin = new Padding(0, 0, 8, 0);
            buttonPanel.Controls.Add(_closeButton);

            _previewButton = new Button();
            _previewButton.Text = "演练";
            _previewButton.AutoSize = true;
            _previewButton.Margin = new Padding(0, 0, 6, 0);
            buttonPanel.Controls.Add(_previewButton);

            _protectSelectedButton = new Button();
            _protectSelectedButton.Text = "保护选中";
            _protectSelectedButton.AutoSize = true;
            _protectSelectedButton.Margin = new Padding(0, 0, 6, 0);
            buttonPanel.Controls.Add(_protectSelectedButton);

            _selectNoneButton = new Button();
            _selectNoneButton.Text = "全不选";
            _selectNoneButton.AutoSize = true;
            buttonPanel.Controls.Add(_selectNoneButton);

            _selectAllButton = new Button();
            _selectAllButton.Text = "全选";
            _selectAllButton.AutoSize = true;
            buttonPanel.Controls.Add(_selectAllButton);

            _gridMenu = new ContextMenuStrip();
            _protectItem = new ToolStripMenuItem("加入保护名单（永不关闭）");
            _unprotectItem = new ToolStripMenuItem("移出保护名单");
            _windowSelectionItem = new ToolStripMenuItem("选择要关闭的窗口...");
            _pathProtectItem = new ToolStripMenuItem("按路径加入保护");
            _pathUnprotectItem = new ToolStripMenuItem("按路径移出保护");
            _gridMenu.Items.Add(_protectItem);
            _gridMenu.Items.Add(_unprotectItem);
            _gridMenu.Items.Add(new ToolStripSeparator());
            _gridMenu.Items.Add(_windowSelectionItem);
            _gridMenu.Items.Add(new ToolStripSeparator());
            _gridMenu.Items.Add(_pathProtectItem);
            _gridMenu.Items.Add(_pathUnprotectItem);
            _grid.ContextMenuStrip = _gridMenu;

            _grid.CurrentCellDirtyStateChanged += Grid_CurrentCellDirtyStateChanged;
            _grid.CellValueChanged += Grid_CellValueChanged;
            _grid.CellDoubleClick += Grid_CellDoubleClick;
            _grid.CellMouseDown += Grid_CellMouseDown;
            _gridMenu.Opening += GridMenu_Opening;
            _protectItem.Click += delegate { ToggleContextProtection(true); };
            _unprotectItem.Click += delegate { ToggleContextProtection(false); };
            _windowSelectionItem.Click += delegate { SelectContextWindows(); };
            _pathProtectItem.Click += delegate { ToggleContextPathProtection(true); };
            _pathUnprotectItem.Click += delegate { ToggleContextPathProtection(false); };

            _refreshButton.Click += async delegate { await RefreshAsync(_currentMode, NativeMethods.GetForegroundWindow(), true); };
            _closeButton.Click += async delegate { await CloseSelectedAsync(); };
            _previewButton.Click += async delegate { await PreviewSelectedAsync(); };
            _settingsButton.Click += delegate { ShowSettings(); };
            _protectSelectedButton.Click += delegate { ProtectSelected(); };
            _profileApplyButton.Click += delegate { ApplyProfile(); };
            _profileSaveButton.Click += delegate { SaveProfile(); };
            _profileDeleteButton.Click += delegate { DeleteProfile(); };
            _profileRestoreButton.Click += delegate { RestoreBackup(); };
            _selectAllButton.Click += delegate { SelectAllVisible(); };
            _selectNoneButton.Click += delegate { SelectNone(); };
            _searchBox.TextChanged += delegate { ApplyFilter(); };
            _showAllWindowsCheckBox.CheckedChanged += delegate
            {
                if (_uiReady && !_suppressViewSave)
                {
                    Config.ShowAllWindows = _showAllWindowsCheckBox.Checked;
                    if (!SaveViewPreference())
                    {
                        SetStatus("视图设置未保存：" + _configStore.LastSaveError, Color.Firebrick);
                    }
                }
                ApplyFilter();
                UpdateSelectionStatus();
            };
            _includeChildrenCheckBox.CheckedChanged += delegate
            {
                if (_uiReady && !_suppressViewSave)
                {
                    Config.IncludeChildProcesses = _includeChildrenCheckBox.Checked;
                    if (!SaveViewPreference())
                    {
                        SetStatus("关闭范围设置未保存：" + _configStore.LastSaveError, Color.Firebrick);
                    }
                }
            };

            _radioWindows.CheckedChanged += async delegate
            {
                if (_radioWindows.Checked && _uiReady && !_suppressModeChange)
                {
                    await SetModeAsync("windows");
                }
            };
            _radioAll.CheckedChanged += async delegate
            {
                if (_radioAll.Checked && _uiReady && !_suppressModeChange)
                {
                    await SetModeAsync("all");
                }
            };

            KeyDown += MainForm_KeyDown;
        }

        private void MainForm_Shown(object sender, EventArgs e)
        {
            if (_startedHidden)
            {
                Hide();
                return;
            }
            _ = RefreshAsync(_currentMode, NativeMethods.GetForegroundWindow(), true);
        }

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                Hide();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.F5)
            {
                _ = RefreshAsync(_currentMode, NativeMethods.GetForegroundWindow(), true);
                e.Handled = true;
            }
        }

        private async Task SetModeAsync(string mode)
        {
            if (string.Equals(_currentMode, mode, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _currentMode = mode;
            Config.LastMode = mode;
            SaveViewPreference();
            if (_includeChildrenCheckBox != null)
            {
                _includeChildrenCheckBox.Enabled = mode == "all" && !_busy && !_refreshing;
            }
            UpdateSelectionStatus();
            await RefreshAsync(mode, NativeMethods.GetForegroundWindow(), true);
        }

        private async Task RefreshAsync(string mode, IntPtr foregroundHandle, bool force)
        {
            if (!_uiReady || _busy)
            {
                return;
            }
            if (_refreshing)
            {
                if (force || !string.Equals(mode, _scanMode, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingMode = mode;
                    _pendingForeground = foregroundHandle;
                }
                return;
            }

            _refreshing = true;
            _scanMode = mode;
            UpdateControlAvailability();

            CancellationTokenSource cts = new CancellationTokenSource();
            _refreshCts = cts;
            try
            {
                await RefreshCoreAsync(mode, foregroundHandle, cts.Token);
            }
            finally
            {
                if (ReferenceEquals(_refreshCts, cts))
                {
                    _refreshCts = null;
                }
                cts.Dispose();
                _refreshing = false;
                RestoreRefreshUi();
                if (!string.IsNullOrEmpty(_pendingMode))
                {
                    string pendingMode = _pendingMode;
                    IntPtr pendingForeground = _pendingForeground;
                    _pendingMode = null;
                    _pendingForeground = IntPtr.Zero;
                    _ = RefreshAsync(pendingMode, pendingForeground, true);
                }
            }
        }

        private async Task RefreshCoreAsync(string mode, IntPtr foregroundHandle, CancellationToken token)
        {
            LockRefreshUi();
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                List<AppCandidate> apps = await _scanner.ScanAsync(Config, mode, foregroundHandle, token);
                token.ThrowIfCancellationRequested();
                UpdateRows(apps);
                int visibleCount = 0;
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    if (row.Visible)
                    {
                        visibleCount++;
                    }
                }
                watch.Stop();
                SetStatus(
                    "扫描完成：显示 " + visibleCount + " 个程序（候选 " + apps.Count +
                    " 个），用时 " + watch.ElapsedMilliseconds + "ms",
                    Color.DimGray);
            }
            catch (OperationCanceledException)
            {
                SetStatus("扫描已取消", Color.DimGray);
            }
            catch (Exception ex)
            {
                SetStatus("扫描失败，保留旧清单：" + ex.Message, Color.Firebrick);
            }
        }

        private void LockRefreshUi()
        {
            if (_grid == null || _grid.IsDisposed)
            {
                return;
            }
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag != null)
                {
                    row.Cells[ColCheck].ReadOnly = true;
                }
            }
            UpdateControlAvailability();
        }

        private void RestoreRefreshUi()
        {
            if (_grid != null && !_grid.IsDisposed)
            {
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    if (row.Tag != null)
                    {
                        AppCandidate app = (AppCandidate)row.Tag;
                        row.Cells[ColCheck].ReadOnly = app.Protected;
                    }
                }
            }
            UpdateControlAvailability();
        }

        private void UpdateControlAvailability()
        {
            if (!_uiReady)
            {
                return;
            }
            bool canAct = !_busy && !_refreshing;
            if (_closeButton != null) _closeButton.Enabled = canAct;
            if (_previewButton != null) _previewButton.Enabled = canAct;
            if (_settingsButton != null) _settingsButton.Enabled = canAct;
            if (_protectSelectedButton != null) _protectSelectedButton.Enabled = canAct;
            if (_selectAllButton != null) _selectAllButton.Enabled = canAct;
            if (_selectNoneButton != null) _selectNoneButton.Enabled = canAct;
            if (_refreshButton != null) _refreshButton.Enabled = canAct;
            if (_searchBox != null) _searchBox.Enabled = canAct;
            if (_showAllWindowsCheckBox != null) _showAllWindowsCheckBox.Enabled = canAct;
            if (_includeChildrenCheckBox != null)
            {
                _includeChildrenCheckBox.Enabled = canAct && _currentMode == "all";
            }
            if (_radioWindows != null) _radioWindows.Enabled = canAct;
            if (_radioAll != null) _radioAll.Enabled = canAct;
            if (_profileCombo != null) _profileCombo.Enabled = canAct;
            if (_profileApplyButton != null)
            {
                _profileApplyButton.Enabled = canAct && _profileCombo != null && _profileCombo.Items.Count > 0;
            }
            if (_profileSaveButton != null) _profileSaveButton.Enabled = canAct;
            if (_profileDeleteButton != null)
            {
                _profileDeleteButton.Enabled = canAct && _profileCombo != null && _profileCombo.Items.Count > 0;
            }
            if (_profileRestoreButton != null)
            {
                _profileRestoreButton.Enabled = canAct && File.Exists(_configStore.ConfigPath + ".bak");
            }
            if (_gridMenu != null) _gridMenu.Enabled = canAct;
        }

        private void UpdateRows(List<AppCandidate> apps)
        {
            HashSet<string> checkedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataGridViewRow existing in _grid.Rows)
            {
                if (existing.Tag == null)
                {
                    continue;
                }
                if (existing.Cells[ColCheck].Value is bool && (bool)existing.Cells[ColCheck].Value)
                {
                    checkedKeys.Add(GetSelectionKey((AppCandidate)existing.Tag));
                }
            }

            AttachWindowSelections(apps);

            bool previousSuppress = _suppressGridEvents;
            _suppressGridEvents = true;
            _grid.SuspendLayout();
            try
            {
                List<DataGridViewRow> prepared = new List<DataGridViewRow>();
                foreach (AppCandidate app in apps)
                {
                    DataGridViewRow row = new DataGridViewRow();
                    row.CreateCells(_grid);
                    row.Tag = app;
                    bool isChecked = !app.Protected &&
                        checkedKeys.Contains(GetSelectionKey(app));
                    row.Cells[ColCheck].Value = isChecked;
                    row.Cells[ColIcon].Value = GetAppIcon(app);
                    row.Cells[ColName].Value = app.DisplayName;
                    row.Cells[ColProcess].Value = app.ProcessCount > 1
                        ? app.Name + " (" + app.ProcessCount + " 个进程)"
                        : app.Name + " (#" + app.ProcessId + ")";
                    row.Cells[ColWindowCount].Value = BuildWindowCountText(app);
                    row.Cells[ColKind].Value = app.Kind;
                    row.Cells[ColTitle].Value = app.Titles;
                    row.Cells[ColPermission].Value = app.RequiresElevation ? "管理员" : string.Empty;
                    row.Cells[ColPermission].ToolTipText = app.RequiresElevation
                        ? "需要以管理员身份运行才能关闭此进程"
                        : string.Empty;
                    if (app.RequiresElevation)
                    {
                        row.Cells[ColPermission].Style.ForeColor = Color.Firebrick;
                    }
                    row.Cells[ColResult].Value = string.Empty;

                    row.Cells[ColName].ToolTipText = app.Path;
                    int[] processIds = app.ProcessIds ?? new int[0];
                    row.Cells[ColProcess].ToolTipText =
                        "PID: " + string.Join(", ", processIds) + Environment.NewLine + app.Path;
                    row.Cells[ColTitle].ToolTipText = app.Titles;
                    if (app.SelectedWindowHandles != null && app.SelectedWindowHandles.Count > 0)
                    {
                        row.Cells[ColWindowCount].ToolTipText =
                            "只关闭选中的 " + app.SelectedWindowHandles.Count + " 个窗口";
                    }
                    ApplyRowProtectionStyle(row, app);
                    prepared.Add(row);
                }

                _grid.Rows.Clear();
                if (prepared.Count > 0)
                {
                    _grid.Rows.AddRange(prepared.ToArray());
                }
                ApplyFilter();
                UpdateSelectionStatus();
                _grid.ClearSelection();
            }
            finally
            {
                _grid.ResumeLayout(true);
                _grid.Invalidate();
                _suppressGridEvents = previousSuppress;
            }
        }

        private void AttachWindowSelections(List<AppCandidate> apps)
        {
            foreach (AppCandidate app in apps)
            {
                string key = GetSelectionKey(app);
                HashSet<IntPtr> selection;
                if (!_windowSelections.TryGetValue(key, out selection))
                {
                    continue;
                }

                HashSet<IntPtr> available = new HashSet<IntPtr>(
                    (app.Windows ?? new List<WindowInfo>()).Select(window => window.Handle));
                selection.RemoveWhere(handle => !available.Contains(handle));
                if (selection.Count == 0 || app.WindowCount == 0 || selection.Count >= app.WindowCount)
                {
                    _windowSelections.Remove(key);
                    app.SelectedWindowHandles = null;
                }
                else
                {
                    app.SelectedWindowHandles = new HashSet<IntPtr>(selection);
                }
            }
        }

        private static string GetSelectionKey(AppCandidate app)
        {
            if (app == null)
            {
                return string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(app.Path))
            {
                string path = ProcessNameUtility.NormalizePath(app.Path);
                if (path.Length > 0)
                {
                    return "path:" + path.ToLowerInvariant();
                }
            }
            return "pid:" + app.ProcessId;
        }

        private static string BuildWindowCountText(AppCandidate app)
        {
            if (app.WindowCount <= 0)
            {
                return string.Empty;
            }
            if (app.SelectedWindowHandles != null &&
                app.SelectedWindowHandles.Count > 0 &&
                app.SelectedWindowHandles.Count < app.WindowCount)
            {
                return app.SelectedWindowHandles.Count + "/" + app.WindowCount;
            }
            return app.WindowCount.ToString();
        }

        private Image GetAppIcon(AppCandidate app)
        {
            if (app.WindowCount == 0 || string.IsNullOrWhiteSpace(app.Path))
            {
                return _genericIcon;
            }
            lock (_iconLock)
            {
                Image cached;
                if (_iconCache.TryGetValue(app.Path, out cached))
                {
                    return cached;
                }
            }

            Image image = null;
            try
            {
                using (Icon icon = Icon.ExtractAssociatedIcon(app.Path))
                {
                    if (icon != null)
                    {
                        image = icon.ToBitmap();
                    }
                }
            }
            catch
            {
                image = null;
            }
            if (image == null)
            {
                image = _genericIcon;
            }
            lock (_iconLock)
            {
                _iconCache[app.Path] = image;
            }
            return image;
        }

        private void ApplyRowProtectionStyle(DataGridViewRow row, AppCandidate app)
        {
            if (app.Protected)
            {
                row.Cells[ColCheck].Value = false;
                row.Cells[ColCheck].ReadOnly = true;
                row.Cells[ColKind].Value = "已保护";
                row.DefaultCellStyle.ForeColor = Color.Gray;
                row.DefaultCellStyle.Font = _baseFont;
            }
            else
            {
                row.Cells[ColCheck].ReadOnly = false;
                row.Cells[ColKind].Value = app.Kind;
                row.DefaultCellStyle.ForeColor = Color.Empty;
                row.DefaultCellStyle.Font = app.IsForeground ? _boldFont : _baseFont;
            }
        }

        private void ApplyFilter()
        {
            if (_grid == null)
            {
                return;
            }
            string text = _searchBox == null ? string.Empty : _searchBox.Text;
            bool showAllWindows = _showAllWindowsCheckBox != null && _showAllWindowsCheckBox.Checked;
            bool includeSuspendedUwp = Config.IncludeSuspendedUwp;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag == null)
                {
                    continue;
                }
                row.Visible = CandidateFilter.Matches(
                    (AppCandidate)row.Tag,
                    text,
                    showAllWindows,
                    includeSuspendedUwp);
            }
            UpdateSelectionStatus();
        }

        private void UpdateSelectionStatus()
        {
            int count = 0;
            bool danger = false;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag == null || !row.Visible)
                {
                    continue;
                }
                bool isChecked = row.Cells[ColCheck].Value is bool && (bool)row.Cells[ColCheck].Value;
                if (!isChecked)
                {
                    continue;
                }
                count++;
                AppCandidate app = (AppCandidate)row.Tag;
                if (!app.Protected &&
                    DangerProcessNames.Contains(ProcessNameUtility.Normalize(app.Name), StringComparer.OrdinalIgnoreCase))
                {
                    danger = true;
                }
            }

            if (danger)
            {
                SetStatus(
                    "已选 " + count + " 个程序   |   注意：包含终端窗口，关闭会终止其中运行的任务",
                    Color.Firebrick);
                return;
            }

            string modeText = _currentMode == "all"
                ? "彻底退出（强制结束后台，未保存数据可能丢失" +
                    (Config.IncludeChildProcesses ? "；含子进程" : string.Empty) + "）"
                : "只关窗口（保留托盘）";
            SetStatus(
                "已选 " + count + " 个程序   |   模式：" + modeText,
                _currentMode == "all" ? Color.FromArgb(180, 80, 0) : Color.DimGray);
        }

        private List<AppCandidate> GetSelectedCandidates()
        {
            List<AppCandidate> selected = new List<AppCandidate>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag == null || !row.Visible)
                {
                    continue;
                }
                AppCandidate app = (AppCandidate)row.Tag;
                if (app.Protected)
                {
                    continue;
                }
                if (row.Cells[ColCheck].Value is bool && (bool)row.Cells[ColCheck].Value)
                {
                    selected.Add(app);
                }
            }
            return selected;
        }

        private async Task CloseSelectedAsync()
        {
            if (!_uiReady || _busy || _refreshing)
            {
                return;
            }

            List<AppCandidate> selected = GetSelectedCandidates();
            if (selected.Count == 0)
            {
                SetStatus("请先勾选要关闭的程序", Color.DimGray);
                return;
            }

            string mode = _currentMode;
            int timeout = Config.GracefulTimeoutSeconds;
            if (timeout < 1)
            {
                timeout = 5;
            }
            bool includeChildren = mode == "all" && Config.IncludeChildProcesses;
            bool applyWindowSelection = mode == "windows";

            List<CloseTargetGroup> groups = _closeService.CreateTargets(
                selected,
                includeChildren,
                applyWindowSelection);
            int liveCount = groups.Sum(group => group.Targets.Count(target => !target.Skipped));
            if (liveCount == 0)
            {
                UpdateResultCells(groups);
                SetStatus("选中的进程已退出或身份已变化；未发送关闭请求", Color.DimGray);
                return;
            }

            Func<List<CloseTarget>, ClosePreviewResult> confirm = null;
            if (string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase))
            {
                confirm = delegate(List<CloseTarget> targets)
                {
                    using (ClosePreviewForm dialog = new ClosePreviewForm(targets, mode, timeout, false))
                    {
                        if (dialog.ShowDialog(this) != DialogResult.OK)
                        {
                            return new ClosePreviewResult { Confirmed = false };
                        }
                        return dialog.Result;
                    }
                };
            }

            _busy = true;
            UpdateControlAvailability();
            SetStatus("正在关闭...", Color.DimGray);
            try
            {
                IProgress<string> progress = new Progress<string>(
                    text => SetStatus(text, Color.DimGray));
                bool executed = await _closeService.ExecuteSelectedAsync(
                    groups,
                    mode,
                    timeout,
                    confirm,
                    progress);
                if (!executed)
                {
                    return;
                }

                UpdateResultCells(groups);
                int failed = groups.Count(group => !IsGroupSuccess(group));
                int success = groups.Count - failed;
                if (failed == 0)
                {
                    SetStatus("完成：成功处理 " + success + " 个应用组", Color.DimGray);
                }
                else
                {
                    SetStatus(
                        "完成：成功 " + success + " 个，未完成 " + failed + " 个应用组（详见结果列）",
                        Color.Firebrick);
                }
            }
            catch (Exception ex)
            {
                SetStatus("执行出错：" + ex.Message, Color.Firebrick);
            }
            finally
            {
                _busy = false;
                UpdateControlAvailability();
            }
        }

        private Task PreviewSelectedAsync()
        {
            if (!_uiReady || _busy || _refreshing)
            {
                return Task.FromResult(0);
            }

            List<AppCandidate> selected = GetSelectedCandidates();
            if (selected.Count == 0)
            {
                SetStatus("请先勾选要演练的程序", Color.DimGray);
                return Task.FromResult(0);
            }

            string mode = _currentMode;
            int timeout = Config.GracefulTimeoutSeconds;
            if (timeout < 1)
            {
                timeout = 5;
            }
            bool includeChildren = mode == "all" && Config.IncludeChildProcesses;
            bool applyWindowSelection = mode == "windows";

            List<CloseTargetGroup> groups = _closeService.CreateTargets(
                selected,
                includeChildren,
                applyWindowSelection);
            List<CloseTarget> targets = groups
                .SelectMany(group => group.Targets)
                .ToList();
            if (!targets.Any(target => !target.Skipped))
            {
                SetStatus("选中的进程已退出或身份已变化；没有可演练的目标", Color.DimGray);
                return Task.FromResult(0);
            }

            _busy = true;
            UpdateControlAvailability();
            try
            {
                using (ClosePreviewForm dialog = new ClosePreviewForm(targets, mode, timeout, true))
                {
                    dialog.ShowDialog(this);
                }
                SetStatus("演练完成，没有发送关闭请求", Color.DimGray);
            }
            finally
            {
                _busy = false;
                UpdateControlAvailability();
            }
            return Task.FromResult(0);
        }

        private bool SaveViewPreference()
        {
            return !string.IsNullOrEmpty(_configStore.LastLoadError)
                ? _configStore.Save()
                : _configStore.Save(false);
        }

        private void ShowSettings()
        {
            if (_busy || _refreshing)
            {
                return;
            }

            using (SettingsForm dialog = new SettingsForm(Config, StartupManager.IsEnabled()))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result == null)
                {
                    return;
                }

                SettingsResult result = dialog.Result;
                AppConfig snapshot = Config.Clone();
                bool hotkeyChanged = !string.Equals(
                    snapshot.Hotkey,
                    result.Hotkey,
                    StringComparison.OrdinalIgnoreCase);

                Config.Hotkey = result.Hotkey;
                Config.GracefulTimeoutSeconds = result.GracefulTimeoutSeconds;
                Config.ShowAllWindows = result.ShowAllWindows;
                Config.IncludeChildProcesses = result.IncludeChildProcesses;
                Config.IncludeSuspendedUwp = result.IncludeSuspendedUwp;

                if (!_configStore.Save())
                {
                    Config.Hotkey = snapshot.Hotkey;
                    Config.GracefulTimeoutSeconds = snapshot.GracefulTimeoutSeconds;
                    Config.ShowAllWindows = snapshot.ShowAllWindows;
                    Config.IncludeChildProcesses = snapshot.IncludeChildProcesses;
                    Config.IncludeSuspendedUwp = snapshot.IncludeSuspendedUwp;
                    SetStatus("设置未保存，已撤销更改：" + _configStore.LastSaveError, Color.Firebrick);
                    return;
                }

                _suppressViewSave = true;
                try
                {
                    _showAllWindowsCheckBox.Checked = Config.ShowAllWindows;
                    _includeChildrenCheckBox.Checked = Config.IncludeChildProcesses;
                }
                finally
                {
                    _suppressViewSave = false;
                }
                ApplyFilter();

                bool hotkeyOk = true;
                if (hotkeyChanged)
                {
                    hotkeyOk = ReRegisterHotkey();
                    if (!hotkeyOk)
                    {
                        Config.Hotkey = snapshot.Hotkey;
                        _configStore.Save(false);
                        ReRegisterHotkey();
                    }
                }

                string startupError;
                bool startupOk = StartupManager.SetEnabled(
                    result.StartWithWindows,
                    out startupError);

                if (!startupOk)
                {
                    SetStatus(
                        "设置已保存，但开机启动设置失败：" + startupError,
                        Color.Firebrick);
                }
                else if (!hotkeyOk)
                {
                    SetStatus(
                        "设置已保存，但新热键注册失败，已保留旧热键 " + Config.Hotkey,
                        Color.Firebrick);
                }
                else
                {
                    SetStatus("设置已保存", Color.DarkGreen);
                }
            }
        }

        private void RestartAsAdministrator()
        {
            if (_currentElevated)
            {
                SetStatus("当前已以管理员身份运行", Color.DimGray);
                return;
            }
            if (!Confirm(
                "将通过 UAC 以管理员身份重启清场助手；当前实例会先退出。继续吗？",
                "以管理员身份重启",
                MessageBoxIcon.Question))
            {
                return;
            }

            try
            {
                string executable = Application.ExecutablePath;
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments =
                    "/c ping 127.0.0.1 -n 3 >nul & start \"\" \"" + executable + "\"";
                startInfo.UseShellExecute = true;
                startInfo.Verb = "runas";
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(startInfo);
                ExitApp();
            }
            catch (Exception ex)
            {
                SetStatus("提权启动失败（可能取消了 UAC）：" + ex.Message, Color.Firebrick);
            }
        }

        private DataGridViewRow GetContextRow()
        {
            if (_contextRowIndex < 0 || _contextRowIndex >= _grid.Rows.Count)
            {
                return null;
            }
            return _grid.Rows[_contextRowIndex];
        }

        private void SelectContextWindows()
        {
            if (_busy || _refreshing || _currentMode != "windows")
            {
                return;
            }
            DataGridViewRow row = GetContextRow();
            if (row == null || row.Tag == null)
            {
                return;
            }
            AppCandidate app = (AppCandidate)row.Tag;
            if (app.Protected || app.WindowCount == 0)
            {
                return;
            }

            using (WindowSelectionForm dialog = new WindowSelectionForm(app))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                HashSet<IntPtr> handles = dialog.SelectedHandles ?? new HashSet<IntPtr>();
                string key = GetSelectionKey(app);
                if (handles.Count == 0)
                {
                    SetStatus("没有选择窗口；保持原来的窗口范围", Color.DimGray);
                }
                else if (handles.Count >= app.WindowCount)
                {
                    _windowSelections.Remove(key);
                    app.SelectedWindowHandles = null;
                    SetStatus("将关闭 " + app.DisplayName + " 的全部 " + app.WindowCount + " 个窗口", Color.DimGray);
                }
                else
                {
                    _windowSelections[key] = new HashSet<IntPtr>(handles);
                    app.SelectedWindowHandles = new HashSet<IntPtr>(handles);
                    SetStatus(
                        "只关闭 " + app.DisplayName + " 的 " + handles.Count + " 个窗口（共 " +
                        app.WindowCount + " 个）",
                        Color.DimGray);
                }
                UpdateWindowCell(row, app);
            }
        }

        private void UpdateWindowCell(DataGridViewRow row, AppCandidate app)
        {
            if (row == null || row.Tag == null)
            {
                return;
            }
            row.Cells[ColWindowCount].Value = BuildWindowCountText(app);
            row.Cells[ColWindowCount].ToolTipText =
                app.SelectedWindowHandles != null && app.SelectedWindowHandles.Count > 0
                    ? "只关闭选中的 " + app.SelectedWindowHandles.Count + " 个窗口"
                    : string.Empty;
        }

        private bool IsPathProtected(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Config.ProtectedPaths == null)
            {
                return false;
            }
            string normalized = ProcessNameUtility.NormalizePath(path);
            return Config.ProtectedPaths.Any(item =>
                string.Equals(
                    ProcessNameUtility.NormalizePath(item),
                    normalized,
                    StringComparison.OrdinalIgnoreCase));
        }

        private bool IsNameProtected(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || Config.ProtectedProcesses == null)
            {
                return false;
            }
            return Config.ProtectedProcesses.Any(item => ProcessNameUtility.SameName(item, name));
        }

        private void ToggleContextPathProtection(bool protect)
        {
            if (_busy || _refreshing)
            {
                return;
            }
            DataGridViewRow row = GetContextRow();
            if (row == null || row.Tag == null)
            {
                return;
            }
            AppCandidate app = (AppCandidate)row.Tag;
            if (string.IsNullOrWhiteSpace(app.Path))
            {
                SetStatus("该程序路径未知，无法按路径保护", Color.DimGray);
                return;
            }

            int count = _configStore.SetPathProtection(new[] { app.Path }, protect);
            if (!_configStore.LastSaveSucceeded)
            {
                SetStatus("路径保护规则未保存，已撤销更改：" + _configStore.LastSaveError, Color.Firebrick);
                return;
            }
            SyncVisibleProtectionState();
            RefreshProfileList();
            SetStatus(
                (protect ? "已按路径加入保护：" : "已按路径移出保护：") + app.Path,
                Color.DimGray);
        }

        private static bool IsGroupSuccess(CloseTargetGroup group)
        {
            int processed;
            int total;
            bool allSucceeded;
            CloseService.GetGroupSummary(group, out processed, out total, out allSucceeded);
            return allSucceeded;
        }

        private void UpdateResultCells(List<CloseTargetGroup> groups)
        {
            Dictionary<AppCandidate, DataGridViewRow> rows =
                new Dictionary<AppCandidate, DataGridViewRow>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag is AppCandidate)
                {
                    rows[(AppCandidate)row.Tag] = row;
                }
            }

            foreach (CloseTargetGroup group in groups)
            {
                DataGridViewRow row;
                if (!rows.TryGetValue(group.App, out row))
                {
                    continue;
                }
                int processed;
                int total;
                bool allSucceeded;
                CloseService.GetGroupSummary(group, out processed, out total, out allSucceeded);
                if (total == 0)
                {
                    row.Cells[ColResult].Value = "没有可执行目标";
                }
                else if (allSucceeded)
                {
                    if (total == 1)
                    {
                        row.Cells[ColResult].Value = group.Targets
                            .First(target => !target.IsUserExcluded &&
                                !string.IsNullOrEmpty(target.Status))
                            .Status;
                    }
                    else
                    {
                        row.Cells[ColResult].Value = processed + " 个进程已处理";
                    }
                }
                else
                {
                    row.Cells[ColResult].Value = processed + "/" + total + " 个进程已处理";
                }
            }
        }

        private void ProtectSelected()
        {
            if (!_uiReady || _busy || _refreshing)
            {
                return;
            }
            List<string> names = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag == null || !row.Visible)
                {
                    continue;
                }
                AppCandidate app = (AppCandidate)row.Tag;
                if (app.Protected)
                {
                    continue;
                }
                if (row.Cells[ColCheck].Value is bool && (bool)row.Cells[ColCheck].Value)
                {
                    string name = ProcessNameUtility.Normalize(app.Name);
                    if (name.Length > 0 && seen.Add(name))
                    {
                        names.Add(name);
                    }
                }
            }
            if (names.Count == 0)
            {
                SetStatus("请先勾选要加入保护名单的程序", Color.DimGray);
                return;
            }

            int count = SetProtectionState(names, true);
            if (!_configStore.LastSaveSucceeded)
            {
                return;
            }
            SetStatus("已将 " + count + " 个程序名称加入保护名单（同名实例一起保护）", Color.DimGray);
        }

        private int SetProtectionState(IEnumerable<string> names, bool protect)
        {
            List<string> normalized = ProcessNameUtility.NormalizeDistinct(names);
            if (normalized.Count == 0)
            {
                return 0;
            }

            int count = _configStore.SetProtection(normalized, protect);
            if (!_configStore.LastSaveSucceeded)
            {
                SetStatus("保护名单未保存，已撤销更改：" + _configStore.LastSaveError, Color.Firebrick);
                return 0;
            }

            SyncVisibleProtectionState();
            return count;
        }

        private void ToggleContextProtection(bool protect)
        {
            if (_busy || _refreshing)
            {
                return;
            }
            if (_contextRowIndex < 0 || _contextRowIndex >= _grid.Rows.Count)
            {
                return;
            }
            DataGridViewRow row = _grid.Rows[_contextRowIndex];
            if (row.Tag == null)
            {
                return;
            }
            AppCandidate app = (AppCandidate)row.Tag;
            string name = ProcessNameUtility.Normalize(app.Name);
            if (name.Length == 0)
            {
                return;
            }
            int count = SetProtectionState(new[] { name }, protect);
            if (_configStore.LastSaveSucceeded)
            {
                bool pathStillProtected = !protect && IsPathProtected(app.Path);
                SetStatus(
                    (protect
                        ? "已加入保护名单："
                        : (pathStillProtected
                            ? "已移出名称保护，仍受路径规则保护："
                            : "已移出保护名单：")) + app.Name,
                    Color.DimGray);
            }
        }

        private void SyncVisibleProtectionState()
        {
            if (_grid == null || _busy || _refreshing)
            {
                return;
            }
            HashSet<string> protectedNames = new HashSet<string>(
                Config.ProtectedProcesses.Select(ProcessNameUtility.Normalize),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> protectedPaths = new HashSet<string>(
                (Config.ProtectedPaths ?? new List<string>())
                    .Select(ProcessNameUtility.NormalizePath)
                    .Where(path => path.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            _suppressGridEvents = true;
            try
            {
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    if (row.Tag == null)
                    {
                        continue;
                    }
                    AppCandidate app = (AppCandidate)row.Tag;
                    bool nameProtected = protectedNames.Contains(ProcessNameUtility.Normalize(app.Name));
                    bool pathProtected = !string.IsNullOrWhiteSpace(app.Path) &&
                        protectedPaths.Contains(ProcessNameUtility.NormalizePath(app.Path));
                    app.Protected = nameProtected || pathProtected;
                    ApplyRowProtectionStyle(row, app);
                }
            }
            finally
            {
                _suppressGridEvents = false;
            }
            UpdateSelectionStatus();
        }

        private void SelectAllVisible()
        {
            if (!_uiReady || _busy || _refreshing)
            {
                return;
            }
            _suppressGridEvents = true;
            try
            {
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    if (row.Tag == null)
                    {
                        continue;
                    }
                    AppCandidate app = (AppCandidate)row.Tag;
                    if (row.Visible && !app.Protected)
                    {
                        row.Cells[ColCheck].Value = true;
                    }
                }
            }
            finally
            {
                _suppressGridEvents = false;
            }
            UpdateSelectionStatus();
        }

        private void SelectNone()
        {
            if (!_uiReady || _busy || _refreshing)
            {
                return;
            }
            _suppressGridEvents = true;
            try
            {
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    if (row.Tag == null)
                    {
                        continue;
                    }
                    AppCandidate app = (AppCandidate)row.Tag;
                    if (!app.Protected)
                    {
                        row.Cells[ColCheck].Value = false;
                    }
                }
            }
            finally
            {
                _suppressGridEvents = false;
            }
            UpdateSelectionStatus();
        }

        private void RefreshProfileList()
        {
            if (_profileCombo == null)
            {
                return;
            }
            string selected = Config.ActiveProfile;
            _profileCombo.BeginUpdate();
            try
            {
                _profileCombo.Items.Clear();
                foreach (string name in ProcessNameUtility.GetProfileNames(Config))
                {
                    _profileCombo.Items.Add(name);
                }
                if (!string.IsNullOrEmpty(selected))
                {
                    int index = _profileCombo.Items.IndexOf(selected);
                    if (index >= 0)
                    {
                        _profileCombo.SelectedIndex = index;
                    }
                }
                if (_profileCombo.SelectedIndex < 0 && _profileCombo.Items.Count > 0)
                {
                    _profileCombo.SelectedIndex = 0;
                }
            }
            finally
            {
                _profileCombo.EndUpdate();
            }
            UpdateControlAvailability();
        }

        private void SaveProfile()
        {
            if (_busy || _refreshing)
            {
                return;
            }
            string name;
            using (ProfileNameDialog dialog = new ProfileNameDialog())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
                name = dialog.ProfileName;
            }
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            string existing = _configStore.FindProfileKey(name);
            if (existing != null)
            {
                if (!Confirm("保护预设【" + existing + "】已存在，是否覆盖？", "清场助手", MessageBoxIcon.Question))
                {
                    return;
                }
                name = existing;
            }

            if (!_configStore.SaveProfile(name, Config.ProtectedProcesses))
            {
                SetStatus("保护预设未保存：" + _configStore.LastSaveError, Color.Firebrick);
                return;
            }
            RefreshProfileList();
            SetStatus(
                "已保存预设【" + name + "】（" + Config.ProtectedProcesses.Count + " 个保护程序）",
                Color.DimGray);
        }

        private void ApplyProfile()
        {
            if (_busy || _refreshing)
            {
                return;
            }
            string name = _profileCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(name))
            {
                return;
            }
            if (!Confirm(
                "应用预设【" + name + "】会替换当前用户保护名单；不会关闭任何程序。继续吗？",
                "应用保护预设",
                MessageBoxIcon.Question))
            {
                return;
            }
            if (!_configStore.ApplyProfile(name))
            {
                SetStatus("预设未应用：" + _configStore.LastSaveError, Color.Firebrick);
                return;
            }
            SyncVisibleProtectionState();
            RefreshProfileList();
            SetStatus("已应用预设【" + name + "】；当前保护名单已替换", Color.DimGray);
        }

        private void DeleteProfile()
        {
            if (_busy || _refreshing)
            {
                return;
            }
            string name = _profileCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(name))
            {
                return;
            }
            if (!Confirm(
                "确定删除保护预设【" + name + "】？当前保护名单不会改变。",
                "删除保护预设",
                MessageBoxIcon.Warning))
            {
                return;
            }
            if (!_configStore.DeleteProfile(name))
            {
                SetStatus("预设未删除：" + _configStore.LastSaveError, Color.Firebrick);
                return;
            }
            RefreshProfileList();
            SetStatus("已删除预设【" + name + "】；当前保护名单未改变", Color.DimGray);
        }

        private void RestoreBackup()
        {
            if (_busy || _refreshing)
            {
                return;
            }
            if (!File.Exists(_configStore.ConfigPath + ".bak"))
            {
                SetStatus("还没有配置备份；修改保护名单或预设后会自动生成 config.json.bak", Color.DimGray);
                return;
            }
            if (!Confirm(
                "将使用 config.json.bak 恢复热键、关闭模式和保护名单。当前 config.json 会先保存为带时间戳的恢复点；恢复后会重新注册热键。继续吗？",
                "恢复上次配置",
                MessageBoxIcon.Warning))
            {
                return;
            }
            if (!_configStore.RestoreBackup())
            {
                SetStatus("恢复配置失败：" + _configStore.LastRestoreError, Color.Firebrick);
                return;
            }

            _suppressModeChange = true;
            try
            {
                _currentMode = string.Equals(Config.LastMode, "all", StringComparison.OrdinalIgnoreCase)
                    ? "all"
                    : "windows";
                _radioAll.Checked = _currentMode == "all";
                _radioWindows.Checked = _currentMode != "all";
            }
            finally
            {
                _suppressModeChange = false;
            }

            _suppressViewSave = true;
            try
            {
                _showAllWindowsCheckBox.Checked = Config.ShowAllWindows;
                _includeChildrenCheckBox.Checked = Config.IncludeChildProcesses;
            }
            finally
            {
                _suppressViewSave = false;
            }
            ApplyFilter();

            RefreshProfileList();
            SyncVisibleProtectionState();
            bool hotkeyOk = ReRegisterHotkey();
            _ = RefreshAsync(_currentMode, NativeMethods.GetForegroundWindow(), true);
            if (hotkeyOk)
            {
                SetStatus(
                    "已从 config.json.bak 恢复；恢复点：" + _configStore.LastRestorePointPath,
                    Color.DarkGreen);
            }
            else
            {
                SetStatus("已恢复配置，但热键注册失败，请从托盘图标打开", Color.Firebrick);
            }
        }

        private bool Confirm(string message, string title, MessageBoxIcon icon)
        {
            return MessageBox.Show(
                this,
                message,
                title,
                MessageBoxButtons.YesNo,
                icon,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private void InitializeTray()
        {
            Icon trayIcon;
            try
            {
                trayIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            }
            catch
            {
                trayIcon = SystemIcons.Application;
            }

            _trayIcon = new NotifyIcon();
            _trayIcon.Icon = trayIcon;
            _trayIcon.Text = "清场助手";
            _trayIcon.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem openItem = new ToolStripMenuItem("打开面板");
            _settingsItem = new ToolStripMenuItem("设置");
            _adminRestartItem = new ToolStripMenuItem(
                _currentElevated ? "已以管理员身份运行" : "以管理员身份重启");
            _adminRestartItem.Enabled = !_currentElevated;
            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            menu.Items.Add(openItem);
            menu.Items.Add(_settingsItem);
            menu.Items.Add(_adminRestartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);
            _trayIcon.ContextMenuStrip = menu;

            openItem.Click += delegate { ShowPicker(); };
            _settingsItem.Click += delegate { ShowSettings(); };
            _adminRestartItem.Click += delegate { RestartAsAdministrator(); };
            exitItem.Click += delegate { ExitApp(); };
            _trayIcon.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    ShowPicker();
                }
            };
        }

        private bool InitializeHotkey()
        {
            HotkeyDefinition definition = HotkeyParser.Parse(Config.Hotkey);
            try
            {
                _hotkeyWindow = new HotkeyWindow(definition);
                _hotkeyWindow.HotkeyPressed += delegate { TogglePicker(); };
                if (!_hotkeyWindow.Registered)
                {
                    SetStatus(
                        "热键 " + Config.Hotkey +
                        " 注册失败（可能被旧版或别的程序占用）；可从托盘打开，或在设置中换一个热键",
                        Color.Firebrick);
                    ShowBalloon(
                        "热键 " + Config.Hotkey +
                        " 注册失败（可能被旧版或别的程序占用）。可从托盘打开，或在设置中换一个热键。");
                    return false;
                }
                if (!definition.IsValid)
                {
                    SetStatus("热键格式无效，已使用 Ctrl+Alt+X", Color.Firebrick);
                }
                return true;
            }
            catch (Exception ex)
            {
                SetStatus("热键注册失败：" + ex.Message, Color.Firebrick);
                ShowBalloon("热键注册失败，请从托盘图标打开。");
                return false;
            }
        }

        private bool ReRegisterHotkey()
        {
            if (_hotkeyWindow != null)
            {
                _hotkeyWindow.Dispose();
                _hotkeyWindow = null;
            }
            return InitializeHotkey();
        }

        private void ShowBalloon(string text)
        {
            if (_trayIcon == null)
            {
                return;
            }
            try
            {
                _trayIcon.ShowBalloonTip(5000, "清场助手", text, ToolTipIcon.Warning);
            }
            catch
            {
            }
        }

        private void TogglePicker()
        {
            if (_busy || IsDisposed)
            {
                return;
            }
            if (Visible)
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    WindowState = FormWindowState.Normal;
                    Activate();
                    return;
                }
                Hide();
                return;
            }
            ShowPicker();
        }

        internal void ShowFromSecondInstance()
        {
            if (!IsDisposed)
            {
                ShowPicker();
            }
        }

        private void ShowPicker()
        {
            if (_busy || IsDisposed)
            {
                return;
            }
            if (Visible)
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    WindowState = FormWindowState.Normal;
                }
                Activate();
                return;
            }

            IntPtr foreground = NativeMethods.GetForegroundWindow();
            Show();
            NativeMethods.SetForegroundWindow(Handle);
            Activate();
            _ = RefreshAsync(_currentMode, foreground, true);
        }

        private void ExitApp()
        {
            _reallyExit = true;
            if (_refreshCts != null)
            {
                try { _refreshCts.Cancel(); }
                catch { }
            }
            if (_hotkeyWindow != null)
            {
                try { _hotkeyWindow.Dispose(); }
                catch { }
                _hotkeyWindow = null;
            }
            if (_trayIcon != null)
            {
                try
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }
                catch { }
                _trayIcon = null;
            }
            Close();
            Application.Exit();
        }

        private void Grid_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        }

        private void Grid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_suppressGridEvents)
            {
                return;
            }
            if (e.ColumnIndex == ColCheck)
            {
                UpdateSelectionStatus();
            }
        }

        private void Grid_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (_refreshing || _busy || e.RowIndex < 0)
            {
                return;
            }
            DataGridViewRow row = _grid.Rows[e.RowIndex];
            if (row.Tag == null)
            {
                return;
            }
            AppCandidate app = (AppCandidate)row.Tag;
            if (app.Protected)
            {
                return;
            }
            row.Cells[ColCheck].Value = !(row.Cells[ColCheck].Value is bool && (bool)row.Cells[ColCheck].Value);
        }

        private void Grid_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
            {
                _contextRowIndex = e.RowIndex;
                _grid.ClearSelection();
                _grid.Rows[e.RowIndex].Selected = true;
            }
        }

        private void GridMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            DataGridViewRow row = GetContextRow();
            AppCandidate app = row == null ? null : row.Tag as AppCandidate;
            if (app == null)
            {
                _protectItem.Enabled = false;
                _unprotectItem.Enabled = false;
                _windowSelectionItem.Enabled = false;
                _pathProtectItem.Enabled = false;
                _pathUnprotectItem.Enabled = false;
                return;
            }

            bool pathProtected = IsPathProtected(app.Path);
            _protectItem.Enabled = !app.Protected;
            _unprotectItem.Enabled = IsNameProtected(app.Name);
            _windowSelectionItem.Enabled =
                _currentMode == "windows" && !app.Protected && app.WindowCount > 0;
            _pathProtectItem.Enabled =
                !string.IsNullOrWhiteSpace(app.Path) && !pathProtected;
            _pathUnprotectItem.Enabled = pathProtected;
        }

        private void SetStatus(string text, Color color)
        {
            if (_statusLabel == null || _statusLabel.IsDisposed)
            {
                return;
            }
            _statusLabel.ForeColor = color;
            _statusLabel.Text = text;
        }
    }
}
