using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace OneClickClose
{
    public sealed class WindowSelectionForm : Form
    {
        private sealed class WindowChoice
        {
            public WindowInfo Window { get; set; }

            public override string ToString()
            {
                string suffix = Window.IsMinimized ? "  [最小化]" : string.Empty;
                return Window.Title + suffix;
            }
        }

        private readonly CheckedListBox _list;

        public HashSet<IntPtr> SelectedHandles { get; private set; }

        public WindowSelectionForm(AppCandidate app)
        {
            Text = "选择要关闭的窗口 — " + (app == null ? string.Empty : app.DisplayName);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 420);
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 3;
            layout.Padding = new Padding(12);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46f));
            Controls.Add(layout);

            Label hint = new Label();
            hint.Dock = DockStyle.Fill;
            hint.Text = "只对勾选的窗口发送 WM_CLOSE；未勾选的窗口保持打开。";
            hint.ForeColor = Color.DimGray;
            layout.Controls.Add(hint, 0, 0);

            _list = new CheckedListBox();
            _list.Dock = DockStyle.Fill;
            _list.CheckOnClick = true;
            _list.IntegralHeight = false;
            layout.Controls.Add(_list, 0, 1);

            List<WindowInfo> windows = app == null || app.Windows == null
                ? new List<WindowInfo>()
                : app.Windows;
            HashSet<IntPtr> selected = app == null || app.SelectedWindowHandles == null
                ? null
                : app.SelectedWindowHandles;
            foreach (WindowInfo window in windows)
            {
                WindowChoice choice = new WindowChoice { Window = window };
                bool isChecked = selected == null || selected.Contains(window.Handle);
                _list.Items.Add(choice, isChecked);
            }

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;
            buttons.Padding = new Padding(0, 8, 0, 0);
            layout.Controls.Add(buttons, 0, 2);

            Button cancelButton = new Button();
            cancelButton.Text = "取消";
            cancelButton.AutoSize = true;
            cancelButton.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(cancelButton);

            Button okButton = new Button();
            okButton.Text = "确定";
            okButton.AutoSize = true;
            okButton.BackColor = Color.FromArgb(0, 120, 215);
            okButton.ForeColor = Color.White;
            okButton.FlatStyle = FlatStyle.Flat;
            okButton.FlatAppearance.BorderSize = 0;
            okButton.Click += delegate
            {
                SelectedHandles = new HashSet<IntPtr>(
                    _list.CheckedItems.Cast<WindowChoice>().Select(choice => choice.Window.Handle));
                DialogResult = DialogResult.OK;
                Close();
            };
            buttons.Controls.Add(okButton);

            Button selectNoneButton = new Button();
            selectNoneButton.Text = "全不选";
            selectNoneButton.AutoSize = true;
            selectNoneButton.Click += delegate
            {
                for (int i = 0; i < _list.Items.Count; i++)
                {
                    _list.SetItemChecked(i, false);
                }
            };
            buttons.Controls.Add(selectNoneButton);

            Button selectAllButton = new Button();
            selectAllButton.Text = "全选";
            selectAllButton.AutoSize = true;
            selectAllButton.Click += delegate
            {
                for (int i = 0; i < _list.Items.Count; i++)
                {
                    _list.SetItemChecked(i, true);
                }
            };
            buttons.Controls.Add(selectAllButton);

            CancelButton = cancelButton;
            AcceptButton = okButton;
        }
    }
}
