using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;

namespace OneClickClose.Tests
{
    public sealed class TrayAndHotkeyTests
    {
        private static Process StartTestWindow(TempDirectory temp, string name, string title)
        {
            string source = typeof(OneClickClose.TestWindow.FixtureMarker).Assembly.Location;
            string executable = Path.Combine(temp.Path, name + ".exe");
            File.Copy(source, executable);
            Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = title,
                UseShellExecute = false
            });
            Assert.NotNull(process);
            try
            {
                WaitUntil(
                    () => NativeMethods.GetVisibleWindows().Any(window =>
                        (int)window.ProcessId == process.Id && window.Title == title),
                    "测试窗口未能启动");
                return process;
            }
            catch
            {
                StopTestWindow(process);
                throw;
            }
        }

        private static void StopTestWindow(Process process)
        {
            if (process == null) return;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch { }
            process.Dispose();
        }

        private static void WaitUntil(Func<bool> condition, string failure)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(40);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }
            Assert.True(condition(), failure);
        }

        private static void WaitFor(Task task)
        {
            WaitUntil(() => task.IsCompleted, "界面操作超时");
            task.GetAwaiter().GetResult();
        }

        private static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (Control descendant in Descendants(child)) yield return descendant;
            }
        }

        private static DataGridViewRow RowFor(DataGridView grid, int processId)
        {
            return grid.Rows.Cast<DataGridViewRow>().Single(row =>
            {
                AppCandidate app = row.Tag as AppCandidate;
                return app != null && app.ProcessIds.Contains(processId);
            });
        }

        [Fact]
        public void MinimizeAndCloseHideToTrayAndCanBeRestored()
        {
            StaRunner.Run(delegate
            {
                using (TempDirectory temp = new TempDirectory())
                {
                    ConfigStore store = new ConfigStore(Path.Combine(temp.Path, "config.json"));
                    store.Load();
                    using (MainForm form = new MainForm(store, true, false))
                    {
                        form.Opacity = 0;
                        form.ShowFromSecondInstance();
                        Assert.True(form.Visible);
                        WaitUntil(() => !form.IsRefreshingForTest, "首次扫描超时");

                        form.WindowState = FormWindowState.Minimized;
                        Application.DoEvents();
                        Assert.False(form.Visible);
                        Assert.False(form.IsDisposed);

                        form.ShowFromSecondInstance();
                        Assert.True(form.Visible);
                        Assert.Equal(FormWindowState.Normal, form.WindowState);
                        WaitUntil(() => !form.IsRefreshingForTest, "恢复后扫描超时");
                        form.Close();
                        Assert.False(form.Visible);
                        Assert.False(form.IsDisposed);

                        form.ShowFromSecondInstance();
                        Assert.True(form.Visible);
                        WaitUntil(() => !form.IsRefreshingForTest, "再次恢复后扫描超时");
                    }
                }
            });
        }

        [Fact]
        public void HotkeyRefreshesAndClosesOnlyUnprotectedVisibleItems()
        {
            StaRunner.Run(delegate
            {
                using (TempDirectory temp = new TempDirectory())
                {
                    Process first = null;
                    Process later = null;
                    Process protectedProcess = null;
                    try
                    {
                        string id = Guid.NewGuid().ToString("N");
                        string title = "OneClickCloseTest" + id;
                        string protectedName = "protected-" + id;
                        first = StartTestWindow(temp, "first-" + id, title);
                        protectedProcess = StartTestWindow(temp, protectedName, title);

                        ConfigStore store = new ConfigStore(Path.Combine(temp.Path, "config.json"));
                        store.Load();
                        Assert.True(store.SetProtection(new[] { protectedName }, true) > 0);
                        using (MainForm form = new MainForm(store, true, false))
                        {
                            form.Opacity = 0;
                            form.Show();
                            Descendants(form).OfType<TextBox>().Single().Text = title;
                            WaitFor(form.RefreshForTestAsync("windows"));

                            DataGridView grid = Descendants(form).OfType<DataGridView>().Single();
                            DataGridViewRow firstRow = RowFor(grid, first.Id);
                            DataGridViewRow protectedRow = RowFor(grid, protectedProcess.Id);
                            Assert.True((bool)firstRow.Cells[0].Value);
                            Assert.False(firstRow.Cells[0].ReadOnly);
                            Assert.False((bool)protectedRow.Cells[0].Value);
                            Assert.True(protectedRow.Cells[0].ReadOnly);

                            Assert.True(store.SetProtection(new[] { first.ProcessName }, true) > 0);
                            form.SyncProtectionForTest();
                            Assert.False((bool)firstRow.Cells[0].Value);
                            Assert.True(store.SetProtection(new[] { first.ProcessName }, false) > 0);
                            form.SyncProtectionForTest();
                            Assert.True((bool)firstRow.Cells[0].Value);

                            firstRow.Cells[0].Value = false;
                            WaitFor(form.RefreshForTestAsync("windows"));
                            Assert.False((bool)RowFor(grid, first.Id).Cells[0].Value);

                            later = StartTestWindow(temp, "later-" + id, title);
                            WaitFor(form.RefreshForTestAsync("windows"));
                            Assert.True((bool)RowFor(grid, later.Id).Cells[0].Value);

                            WaitFor(form.CloseUnprotectedFromHotkeyAsync());
                            WaitUntil(() => first.HasExited && later.HasExited, "快捷关闭未关闭测试程序");
                            Assert.False(protectedProcess.HasExited);
                        }
                    }
                    finally
                    {
                        StopTestWindow(first);
                        StopTestWindow(later);
                        StopTestWindow(protectedProcess);
                    }
                }
            });
        }

        private static Button ButtonWithText(Control parent, string text)
        {
            return Descendants(parent).OfType<Button>().Single(button => button.Text == text);
        }

        [Fact]
        public void SelectionButtonsAndBatchProtectOperateOnCheckedRows()
        {
            StaRunner.Run(delegate
            {
                using (TempDirectory temp = new TempDirectory())
                {
                    Process first = null;
                    Process second = null;
                    try
                    {
                        string id = Guid.NewGuid().ToString("N");
                        string title = "OneClickCloseTest" + id;
                        first = StartTestWindow(temp, "select-first-" + id, title);
                        second = StartTestWindow(temp, "select-second-" + id, title);

                        ConfigStore store = new ConfigStore(Path.Combine(temp.Path, "config.json"));
                        store.Load();
                        using (MainForm form = new MainForm(store, false, false))
                        {
                            form.Opacity = 0;
                            form.Show();
                            WaitUntil(
                                () => form.Visible && form.RowCountForTest > 0 && !form.IsRefreshingForTest,
                                "首次扫描超时");
                            Descendants(form).OfType<TextBox>().Single().Text = title;
                            WaitFor(form.RefreshForTestAsync("windows"));

                            DataGridView grid = Descendants(form).OfType<DataGridView>().Single();
                            Assert.True((bool)RowFor(grid, first.Id).Cells[0].Value);
                            Assert.True((bool)RowFor(grid, second.Id).Cells[0].Value);

                            ButtonWithText(form, "全不选").PerformClick();
                            Assert.False((bool)RowFor(grid, first.Id).Cells[0].Value);
                            Assert.False((bool)RowFor(grid, second.Id).Cells[0].Value);

                            ButtonWithText(form, "全选").PerformClick();
                            Assert.True((bool)RowFor(grid, first.Id).Cells[0].Value);
                            Assert.True((bool)RowFor(grid, second.Id).Cells[0].Value);

                            ButtonWithText(form, "反选").PerformClick();
                            Assert.False((bool)RowFor(grid, first.Id).Cells[0].Value);
                            Assert.False((bool)RowFor(grid, second.Id).Cells[0].Value);

                            ButtonWithText(form, "全选").PerformClick();
                            RowFor(grid, second.Id).Cells[0].Value = false;
                            ButtonWithText(form, "保护选中").PerformClick();

                            Assert.Contains(
                                store.Current.ProtectedProcesses,
                                name => ProcessNameUtility.SameName(name, first.ProcessName));
                            Assert.DoesNotContain(
                                store.Current.ProtectedProcesses,
                                name => ProcessNameUtility.SameName(name, second.ProcessName));
                            Assert.False((bool)RowFor(grid, first.Id).Cells[0].Value);
                            Assert.True(RowFor(grid, first.Id).Cells[0].ReadOnly);

                            ButtonWithText(form, "反选").PerformClick();
                            Assert.False((bool)RowFor(grid, first.Id).Cells[0].Value);
                            Assert.True((bool)RowFor(grid, second.Id).Cells[0].Value);
                        }
                    }
                    finally
                    {
                        StopTestWindow(first);
                        StopTestWindow(second);
                    }
                }
            });
        }

        [Fact]
        public void FailedHotkeyScanNeverClosesStaleResults()
        {
            StaRunner.Run(delegate
            {
                using (TempDirectory temp = new TempDirectory())
                {
                    Process target = null;
                    try
                    {
                        string id = Guid.NewGuid().ToString("N");
                        string title = "OneClickCloseTest" + id;
                        target = StartTestWindow(temp, "stale-" + id, title);
                        ConfigStore store = new ConfigStore(Path.Combine(temp.Path, "config.json"));
                        store.Load();
                        using (MainForm form = new MainForm(store, true, false))
                        {
                            form.Opacity = 0;
                            form.Show();
                            Descendants(form).OfType<TextBox>().Single().Text = title;
                            WaitFor(form.RefreshForTestAsync("windows"));
                            DataGridView grid = Descendants(form).OfType<DataGridView>().Single();
                            Assert.True((bool)RowFor(grid, target.Id).Cells[0].Value);

                            store.Current.ProtectedProcesses = null;
                            WaitFor(form.CloseUnprotectedFromHotkeyAsync());
                            Assert.False(target.HasExited);
                            Assert.Contains("扫描失败", Descendants(form).OfType<Label>().Single(label =>
                                label.Text.Contains("扫描失败")).Text);
                        }
                    }
                    finally
                    {
                        StopTestWindow(target);
                    }
                }
            });
        }

        [Fact]
        public void HiddenHotkeyInAllModeShowsPreviewAndHonorsCancel()
        {
            StaRunner.Run(delegate
            {
                using (TempDirectory temp = new TempDirectory())
                {
                    Process target = null;
                    try
                    {
                        string id = Guid.NewGuid().ToString("N");
                        string title = "OneClickCloseTest" + id;
                        target = StartTestWindow(temp, "preview-" + id, title);
                        ConfigStore store = new ConfigStore(Path.Combine(temp.Path, "config.json"));
                        store.Load();
                        store.Current.LastMode = "all";

                        MainForm form = new MainForm(store, true, false);
                        form.Opacity = 0;
                        form.Show();
                        bool started = false;
                        bool sawPreview = false;
                        Task task = null;
                        int ticks = 0;
                        System.Windows.Forms.Timer driver = new System.Windows.Forms.Timer();
                        driver.Interval = 25;
                        driver.Tick += delegate
                        {
                            if (++ticks > 400)
                            {
                                Application.ExitThread();
                                return;
                            }
                            if (!started)
                            {
                                if (form.Visible)
                                {
                                    return;
                                }
                                started = true;
                                Descendants(form).OfType<TextBox>().Single().Text = title;
                                task = form.CloseUnprotectedFromHotkeyAsync();
                                return;
                            }

                            ClosePreviewForm preview = Application.OpenForms
                                .OfType<ClosePreviewForm>().FirstOrDefault();
                            if (preview != null)
                            {
                                sawPreview = true;
                                preview.DialogResult = DialogResult.Cancel;
                                return;
                            }
                            if (task != null && task.IsCompleted)
                            {
                                Application.ExitThread();
                            }
                        };
                        driver.Start();
                        Application.Run(new ApplicationContext(form));
                        driver.Stop();

                        Assert.True(sawPreview, "彻底退出应显示确认预览");
                        Assert.False(target.HasExited);
                        Assert.False(form.Visible, "快捷流程完成后应退回托盘");
                        form.Dispose();
                    }
                    finally
                    {
                        StopTestWindow(target);
                    }
                }
            });
        }
    }
}
