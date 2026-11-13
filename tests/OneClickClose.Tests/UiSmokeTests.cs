using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;

namespace OneClickClose.Tests
{
    public sealed class UiSmokeTests
    {
        [Fact]
        public void MainFormBuildsRowsFromScanner()
        {
            StaRunner.Run(delegate
            {
                using (TempDirectory temp = new TempDirectory())
                {
                    ConfigStore store = new ConfigStore(Path.Combine(temp.Path, "config.json"));
                    store.Load();

                    using (MainForm form = new MainForm(store, true))
                    {
                        form.Opacity = 0;
                        form.Show();

                        Task task = form.RefreshForTestAsync("windows");
                        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                        while (!task.IsCompleted && DateTime.UtcNow < deadline)
                        {
                            Application.DoEvents();
                            Thread.Sleep(20);
                        }

                        Assert.True(task.IsCompleted, "刷新测试超时");
                        task.GetAwaiter().GetResult();
                        Assert.True(form.RowCountForTest > 0, "候选列表不应为空");
                        form.Hide();
                    }
                }
            });
        }
        [Fact]
        public void SettingsPreviewAndWindowSelectionFormsBuild()
        {
            StaRunner.Run(delegate
            {
                using (SettingsForm settings = new SettingsForm(AppConfig.CreateDefault(), false))
                {
                    settings.CreateControl();
                    Assert.NotNull(settings);
                }

                List<CloseTarget> targets = new List<CloseTarget>
                {
                    new CloseTarget
                    {
                        ProcessId = 123,
                        ProcessName = "demo",
                        DisplayName = "演示程序",
                        Windows = new List<WindowInfo>
                        {
                            new WindowInfo { Handle = new IntPtr(1), Title = "主窗口" }
                        },
                        WindowTitles = "主窗口",
                        IsChildProcess = true,
                        RequiresElevation = true
                    }
                };
                using (ClosePreviewForm preview = new ClosePreviewForm(targets, "all", 5, false))
                {
                    preview.CreateControl();
                    Assert.NotNull(preview);
                }

                AppCandidate app = new AppCandidate
                {
                    DisplayName = "演示程序",
                    Windows = new List<WindowInfo>
                    {
                        new WindowInfo { Handle = new IntPtr(1), Title = "主窗口" }
                    }
                };
                using (WindowSelectionForm selection = new WindowSelectionForm(app))
                {
                    selection.CreateControl();
                    Assert.NotNull(selection);
                }
            });
        }
    }
}
