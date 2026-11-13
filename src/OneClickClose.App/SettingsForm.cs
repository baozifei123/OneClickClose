using System;
using System.Drawing;
using System.Windows.Forms;

namespace OneClickClose
{
    public sealed class SettingsResult
    {
        public string Hotkey { get; set; }
        public int GracefulTimeoutSeconds { get; set; }
        public bool ShowAllWindows { get; set; }
        public bool IncludeChildProcesses { get; set; }
        public bool IncludeSuspendedUwp { get; set; }
        public bool StartWithWindows { get; set; }
    }

    public sealed class SettingsForm : Form
    {
        private readonly TextBox _hotkeyBox;
        private readonly NumericUpDown _timeoutBox;
        private readonly CheckBox _showAllBox;
        private readonly CheckBox _includeChildrenBox;
        private readonly CheckBox _includeSuspendedUwpBox;
        private readonly CheckBox _startupBox;

        public SettingsResult Result { get; private set; }

        public SettingsForm(AppConfig config, bool startupEnabled)
        {
            AppConfig source = config ?? AppConfig.CreateDefault();
            Text = "设置";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(680, 400);
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 2;
            layout.RowCount = 7;
            layout.Padding = new Padding(14);
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150f));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 5; i++)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));
            }
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));
            Controls.Add(layout);

            Label hotkeyLabel = CreateLabel("全局热键：");
            layout.Controls.Add(hotkeyLabel, 0, 0);
            _hotkeyBox = new TextBox();
            _hotkeyBox.Dock = DockStyle.Fill;
            _hotkeyBox.Text = source.Hotkey;
            _hotkeyBox.Margin = new Padding(0, 5, 0, 5);
            layout.Controls.Add(_hotkeyBox, 1, 0);

            Label timeoutLabel = CreateLabel("优雅关闭等待（秒）：");
            layout.Controls.Add(timeoutLabel, 0, 1);
            _timeoutBox = new NumericUpDown();
            _timeoutBox.Minimum = 1;
            _timeoutBox.Maximum = 60;
            _timeoutBox.Value = Math.Max(1, Math.Min(60, source.GracefulTimeoutSeconds));
            _timeoutBox.Width = 80;
            _timeoutBox.Margin = new Padding(0, 5, 0, 5);
            layout.Controls.Add(_timeoutBox, 1, 1);

            Label viewLabel = CreateLabel("视图与关闭范围：");
            layout.Controls.Add(viewLabel, 0, 2);
            FlowLayoutPanel viewPanel = new FlowLayoutPanel();
            viewPanel.Dock = DockStyle.Fill;
            viewPanel.WrapContents = false;
            viewPanel.AutoSize = true;
            _showAllBox = new CheckBox();
            _showAllBox.Text = "默认显示所有窗口";
            _showAllBox.AutoSize = true;
            _showAllBox.Checked = source.ShowAllWindows;
            _showAllBox.Margin = new Padding(0, 8, 14, 0);
            viewPanel.Controls.Add(_showAllBox);
            _includeChildrenBox = new CheckBox();
            _includeChildrenBox.Text = "彻底退出含子进程";
            _includeChildrenBox.AutoSize = true;
            _includeChildrenBox.Checked = source.IncludeChildProcesses;
            _includeChildrenBox.Margin = new Padding(0, 8, 0, 0);
            viewPanel.Controls.Add(_includeChildrenBox);
            layout.Controls.Add(viewPanel, 1, 2);

            Label uwpLabel = CreateLabel("任务栏范围：");
            layout.Controls.Add(uwpLabel, 0, 3);
            _includeSuspendedUwpBox = new CheckBox();
            _includeSuspendedUwpBox.Text = "显示挂起的 UWP 应用（如“设置”）";
            _includeSuspendedUwpBox.AutoSize = true;
            _includeSuspendedUwpBox.Checked = source.IncludeSuspendedUwp;
            _includeSuspendedUwpBox.Margin = new Padding(0, 8, 0, 0);
            layout.Controls.Add(_includeSuspendedUwpBox, 1, 3);

            Label startupLabel = CreateLabel("开机启动：");
            layout.Controls.Add(startupLabel, 0, 4);
            _startupBox = new CheckBox();
            _startupBox.Text = "登录时自动启动并隐藏到托盘";
            _startupBox.AutoSize = true;
            _startupBox.Checked = startupEnabled;
            _startupBox.Margin = new Padding(0, 8, 0, 0);
            layout.Controls.Add(_startupBox, 1, 4);

            Label hint = new Label();
            hint.Dock = DockStyle.Fill;
            hint.ForeColor = Color.DimGray;
            hint.Text = "热键示例：Ctrl+Alt+X、Ctrl+Shift+Q。保存后立即生效；热键被占用时会保留旧热键并提示。";
            hint.Margin = new Padding(0, 8, 0, 0);
            layout.SetColumnSpan(hint, 2);
            layout.Controls.Add(hint, 0, 5);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;
            buttons.Padding = new Padding(0, 8, 0, 0);
            layout.SetColumnSpan(buttons, 2);
            layout.Controls.Add(buttons, 0, 6);

            Button cancelButton = new Button();
            cancelButton.Text = "取消";
            cancelButton.AutoSize = true;
            cancelButton.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(cancelButton);

            Button saveButton = new Button();
            saveButton.Text = "保存";
            saveButton.AutoSize = true;
            saveButton.BackColor = Color.FromArgb(0, 120, 215);
            saveButton.ForeColor = Color.White;
            saveButton.FlatStyle = FlatStyle.Flat;
            saveButton.FlatAppearance.BorderSize = 0;
            saveButton.Click += SaveButton_Click;
            buttons.Controls.Add(saveButton);

            CancelButton = cancelButton;
            AcceptButton = saveButton;
        }

        private static Label CreateLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Margin = new Padding(0, 8, 0, 0);
            return label;
        }

        private void SaveButton_Click(object sender, EventArgs e)
        {
            string hotkey = (_hotkeyBox.Text ?? string.Empty).Trim();
            HotkeyDefinition definition = HotkeyParser.Parse(hotkey);
            if (!definition.IsValid)
            {
                MessageBox.Show(
                    this,
                    "热键格式无效。示例：Ctrl+Alt+X、Ctrl+Shift+Q；主键支持 A-Z、0-9、F1-F24。",
                    "设置",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                _hotkeyBox.Focus();
                _hotkeyBox.SelectAll();
                return;
            }

            Result = new SettingsResult
            {
                Hotkey = hotkey,
                GracefulTimeoutSeconds = (int)_timeoutBox.Value,
                ShowAllWindows = _showAllBox.Checked,
                IncludeChildProcesses = _includeChildrenBox.Checked,
                IncludeSuspendedUwp = _includeSuspendedUwpBox.Checked,
                StartWithWindows = _startupBox.Checked
            };
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
