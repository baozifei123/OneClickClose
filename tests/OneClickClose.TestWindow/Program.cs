using System;
using System.Windows.Forms;

namespace OneClickClose.TestWindow
{
    public sealed class FixtureMarker
    {
    }

    internal sealed class TestForm : Form
    {
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (Form form = new TestForm())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Left = -32000;
                form.Top = -32000;
                form.Text = args.Length > 0 ? args[0] : "OneClickClose test window";
                Application.Run(form);
            }
        }
    }
}
