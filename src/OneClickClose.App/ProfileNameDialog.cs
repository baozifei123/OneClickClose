using System;
using System.Drawing;
using System.Windows.Forms;

namespace OneClickClose
{
    public sealed class ProfileNameDialog : Form
    {
        private readonly TextBox _textBox;

        public string ProfileName { get; private set; }

        public ProfileNameDialog()
        {
            ProfileName = string.Empty;
            Text = "保存保护预设";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Size = new Size(420, 150);
            Font = new Font("Microsoft YaHei UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;

            Label label = new Label();
            label.Text = "预设名称（最多 40 个字符）：";
            label.AutoSize = true;
            label.Location = new Point(12, 14);
            Controls.Add(label);

            _textBox = new TextBox();
            _textBox.Location = new Point(14, 40);
            _textBox.Width = 375;
            Controls.Add(_textBox);

            Button saveButton = new Button();
            saveButton.Text = "保存";
            saveButton.Location = new Point(222, 78);
            saveButton.Click += SaveButton_Click;
            Controls.Add(saveButton);

            Button cancelButton = new Button();
            cancelButton.Text = "取消";
            cancelButton.Location = new Point(312, 78);
            cancelButton.DialogResult = DialogResult.Cancel;
            Controls.Add(cancelButton);

            AcceptButton = saveButton;
            CancelButton = cancelButton;
            Shown += delegate { _textBox.Select(); _textBox.Focus(); };
        }

        private void SaveButton_Click(object sender, EventArgs e)
        {
            string value = _textBox.Text.Trim();
            if (value.Length == 0)
            {
                MessageBox.Show(this, "请输入预设名称。", "保存保护预设", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (value.Length > 40)
            {
                value = value.Substring(0, 40);
            }
            ProfileName = value;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
