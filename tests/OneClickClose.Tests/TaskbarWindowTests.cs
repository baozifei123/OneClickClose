using System;
using System.Windows.Forms;
using Xunit;

namespace OneClickClose.Tests
{
    public sealed class TaskbarWindowTests
    {
        private sealed class ToolWindowForm : Form
        {
            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams parameters = base.CreateParams;
                    parameters.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW;
                    return parameters;
                }
            }
        }

        [Fact]
        public void NormalFormIsTaskbarEligibleAndToolWindowIsNot()
        {
            StaRunner.Run(delegate
            {
                using (Form normal = new Form())
                {
                    normal.Text = "OneClickClose Taskbar Test";
                    normal.ShowInTaskbar = true;
                    normal.Opacity = 0;
                    normal.Show();
                    Application.DoEvents();
                    Assert.True(NativeMethods.IsTaskbarEligibleWindow(normal.Handle));
                    normal.Hide();
                }

                using (ToolWindowForm tool = new ToolWindowForm())
                {
                    tool.Text = "OneClickClose Tool Test";
                    tool.ShowInTaskbar = false;
                    tool.Opacity = 0;
                    tool.Show();
                    Application.DoEvents();
                    Assert.False(NativeMethods.IsTaskbarEligibleWindow(tool.Handle));
                    tool.Hide();
                }
            });
        }
    }
}
