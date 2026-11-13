using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace OneClickClose
{
    public sealed class ClosePreviewForm : Form
    {
        private readonly List<CloseTarget> _targets;
        private readonly string _mode;
        private readonly int _timeoutSeconds;
        private readonly bool _previewOnly;
        private readonly DataGridView _grid;
        private readonly CheckBox _dryRunBox;
        private readonly Label _summaryLabel;

        public ClosePreviewResult Result { get; private set; }

        public ClosePreviewForm(
            List<CloseTarget> targets,
            string mode,
            int timeoutSeconds,
            bool previewOnly)
        {
            _targets = targets ?? new List<CloseTarget>();
            _mode = string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase) ? "all" : "windows";
            _timeoutSeconds = Math.Max(1, timeoutSeconds);
            _previewOnly = previewOnly;
            Result = new ClosePreviewResult();

            Text = previewOnly ? "演练 — 不会关闭程序" : "确认关闭目标";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Size = new Size(940, 560);
            MinimumSize = new Size(760, 420);
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 5;
            layout.Padding = new Padding(10);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48f));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));
            Controls.Add(layout);

            Label warning = new Label();
            warning.Dock = DockStyle.Fill;
            warning.TextAlign = ContentAlignment.MiddleLeft;
            warning.ForeColor = previewOnly ? Color.DarkGreen : Color.Firebrick;
            warning.Text = previewOnly
                ? "演练模式：只展示将要处理的目标，不会发送任何关闭请求。"
                : (_mode == "all"
                    ? "确认后先请求关闭，超时再强制结束；未保存内容可能丢失。点【取消】不会发送任何关闭请求。"
                    : "确认后只向选中窗口发送 WM_CLOSE；程序可以选择保存或留在托盘。");
            layout.Controls.Add(warning, 0, 0);

            _grid = new DataGridView();
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = true;
            _grid.BackgroundColor = Color.White;
            _grid.BorderStyle = BorderStyle.None;
            _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            _grid.ColumnHeadersHeight = 28;
            _grid.RowTemplate.Height = 26;
            _grid.EditMode = DataGridViewEditMode.EditOnEnter;
            _grid.Font = Font;
            _grid.GridColor = Color.Gainsboro;
            layout.Controls.Add(_grid, 0, 1);

            DataGridViewCheckBoxColumn checkColumn = new DataGridViewCheckBoxColumn();
            checkColumn.HeaderText = string.Empty;
            checkColumn.Width = 38;
            checkColumn.Name = "colCheck";
            _grid.Columns.Add(checkColumn);

            DataGridViewTextBoxColumn appColumn = new DataGridViewTextBoxColumn();
            appColumn.HeaderText = "应用";
            appColumn.Width = 160;
            appColumn.ReadOnly = true;
            _grid.Columns.Add(appColumn);

            DataGridViewTextBoxColumn processColumn = new DataGridViewTextBoxColumn();
            processColumn.HeaderText = "进程";
            processColumn.Width = 140;
            processColumn.ReadOnly = true;
            _grid.Columns.Add(processColumn);

            DataGridViewTextBoxColumn pidColumn = new DataGridViewTextBoxColumn();
            pidColumn.HeaderText = "PID";
            pidColumn.Width = 70;
            pidColumn.ReadOnly = true;
            _grid.Columns.Add(pidColumn);

            DataGridViewTextBoxColumn permissionColumn = new DataGridViewTextBoxColumn();
            permissionColumn.HeaderText = "权限";
            permissionColumn.Width = 76;
            permissionColumn.ReadOnly = true;
            _grid.Columns.Add(permissionColumn);

            DataGridViewTextBoxColumn actionColumn = new DataGridViewTextBoxColumn();
            actionColumn.HeaderText = "动作";
            actionColumn.Width = 250;
            actionColumn.ReadOnly = true;
            _grid.Columns.Add(actionColumn);

            DataGridViewTextBoxColumn windowColumn = new DataGridViewTextBoxColumn();
            windowColumn.HeaderText = "窗口";
            windowColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            windowColumn.ReadOnly = true;
            _grid.Columns.Add(windowColumn);

            foreach (DataGridViewColumn column in _grid.Columns)
            {
                column.SortMode = DataGridViewColumnSortMode.NotSortable;
            }

            foreach (CloseTarget target in _targets)
            {
                int index = _grid.Rows.Add();
                DataGridViewRow row = _grid.Rows[index];
                row.Tag = target;
                row.Cells[0].Value = !target.Skipped;
                row.Cells[0].ReadOnly = target.Skipped;
                row.Cells[1].Value = target.DisplayName;
                row.Cells[2].Value = target.ProcessName;
                row.Cells[3].Value = target.ProcessId;
                row.Cells[4].Value = target.RequiresElevation ? "管理员" : string.Empty;
                row.Cells[5].Value = target.Skipped ? target.Status : BuildActionText(target);
                row.Cells[6].Value = target.WindowTitles;
                if (target.Skipped)
                {
                    row.DefaultCellStyle.ForeColor = Color.Gray;
                }
                else if (target.RequiresElevation)
                {
                    row.Cells[4].Style.ForeColor = Color.Firebrick;
                }
                if (!string.IsNullOrWhiteSpace(target.WindowTitles))
                {
                    row.Cells[6].ToolTipText = target.WindowTitles;
                }
            }

            _grid.CurrentCellDirtyStateChanged += delegate
            {
                if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
                {
                    _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
            };
            _grid.CellValueChanged += delegate { UpdateSummary(); };

            _summaryLabel = new Label();
            _summaryLabel.Dock = DockStyle.Fill;
            _summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
            _summaryLabel.ForeColor = Color.DimGray;
            layout.Controls.Add(_summaryLabel, 0, 2);

            _dryRunBox = new CheckBox();
            _dryRunBox.Text = "仅演练，不执行";
            _dryRunBox.AutoSize = true;
            _dryRunBox.Checked = previewOnly;
            _dryRunBox.Enabled = !previewOnly;
            _dryRunBox.Margin = new Padding(0, 4, 0, 0);
            layout.Controls.Add(_dryRunBox, 0, 3);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;
            buttons.Padding = new Padding(0, 6, 0, 0);
            layout.Controls.Add(buttons, 0, 4);

            Button cancelButton = new Button();
            cancelButton.Text = "取消";
            cancelButton.AutoSize = true;
            cancelButton.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(cancelButton);

            Button confirmButton = new Button();
            confirmButton.Text = previewOnly ? "关闭" : (_mode == "all" ? "确认彻底退出" : "确认关闭窗口");
            confirmButton.AutoSize = true;
            confirmButton.BackColor = previewOnly ? Color.FromArgb(0, 120, 215) : Color.Firebrick;
            confirmButton.ForeColor = Color.White;
            confirmButton.FlatStyle = FlatStyle.Flat;
            confirmButton.FlatAppearance.BorderSize = 0;
            confirmButton.Click += ConfirmButton_Click;
            buttons.Controls.Add(confirmButton);

            CancelButton = cancelButton;
            AcceptButton = confirmButton;
            UpdateSummary();
        }

        private string BuildActionText(CloseTarget target)
        {
            string prefix = target.IsChildProcess ? "子进程：" : string.Empty;
            if (string.Equals(_mode, "windows", StringComparison.OrdinalIgnoreCase))
            {
                return prefix + "发送 WM_CLOSE（进程保留）";
            }
            return target.HasVisibleWindow
                ? prefix + "先正常关闭，" + _timeoutSeconds + " 秒后仍运行则强制结束"
                : prefix + "确认后立即强制结束";
        }

        private void UpdateSummary()
        {
            int selected = 0;
            int childCount = 0;
            int elevatedCount = 0;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                CloseTarget target = row.Tag as CloseTarget;
                if (target == null || target.Skipped)
                {
                    continue;
                }
                if (row.Cells[0].Value is bool && (bool)row.Cells[0].Value)
                {
                    selected++;
                    if (target.IsChildProcess) childCount++;
                    if (target.RequiresElevation) elevatedCount++;
                }
            }
            int liveCount = _targets.Count(target => !target.Skipped);
            string text = "共 " + liveCount + " 个目标，已选 " + selected + " 个";
            if (childCount > 0)
            {
                text += "（含 " + childCount + " 个子进程）";
            }
            if (elevatedCount > 0)
            {
                text += "；" + elevatedCount + " 个需要管理员权限";
            }
            _summaryLabel.Text = text;
            _summaryLabel.ForeColor = elevatedCount > 0 ? Color.Firebrick : Color.DimGray;
        }

        private void ConfirmButton_Click(object sender, EventArgs e)
        {
            List<CloseTarget> selected = new List<CloseTarget>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                CloseTarget target = row.Tag as CloseTarget;
                if (target == null || target.Skipped)
                {
                    continue;
                }
                if (row.Cells[0].Value is bool && (bool)row.Cells[0].Value)
                {
                    selected.Add(target);
                }
            }

            if (selected.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "请至少选择一个要处理的目标；取消请点【取消】。",
                    "没有选中目标",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            Result = new ClosePreviewResult
            {
                Confirmed = true,
                DryRun = _dryRunBox.Checked,
                SelectedTargets = selected
            };
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
