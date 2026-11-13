using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace OneClickClose
{
    internal static class Program
    {
        private const string MutexName = @"Local\OneClickClose.Native.SingleInstance";
        private const string ShowEventName = @"Local\OneClickClose.Native.ShowPanel";

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                LogException(e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                LogException(e.ExceptionObject as Exception);
            };

            bool startHidden = args.Any(argument =>
                string.Equals(argument, "--hidden", StringComparison.OrdinalIgnoreCase));

            bool createdNew;
            bool eventCreatedNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            using (EventWaitHandle showEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                ShowEventName,
                out eventCreatedNew))
            {
                if (!createdNew)
                {
                    try
                    {
                        showEvent.Set();
                    }
                    catch
                    {
                        MessageBox.Show(
                            "清场助手已经在运行了（请看系统托盘）。",
                            "清场助手",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                    return;
                }

                string configPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "config.json");
                ConfigStore store = new ConfigStore(configPath);
                store.Load();
                try
                {
                    StartupManager.MigrateLegacyShortcut();
                }
                catch
                {
                }

                MainForm form = new MainForm(store, startHidden);
                // Force the window handle before the listener thread can post a wake-up message.
                IntPtr mainHandle = form.Handle;
                Thread listener = new Thread(delegate()
                {
                    while (true)
                    {
                        try
                        {
                            showEvent.WaitOne();
                            if (form.IsDisposed)
                            {
                                break;
                            }
                            form.BeginInvoke((Action)form.ShowFromSecondInstance);
                        }
                        catch (ObjectDisposedException)
                        {
                            break;
                        }
                        catch
                        {
                            break;
                        }
                    }
                });
                listener.IsBackground = true;
                listener.Start();

                if (startHidden)
                {
                    Application.Run(new ApplicationContext(form));
                }
                else
                {
                    Application.Run(form);
                }

                try
                {
                    showEvent.Set();
                    listener.Join(500);
                }
                catch
                {
                }
                GC.KeepAlive(mutex);
            }
        }

        private static void LogException(Exception exception)
        {
            if (exception == null)
            {
                return;
            }
            try
            {
                string logPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "OneClickClose-error.log");
                File.AppendAllText(
                    logPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                    exception + Environment.NewLine + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
