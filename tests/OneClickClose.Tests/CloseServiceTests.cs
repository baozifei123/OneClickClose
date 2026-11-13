using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OneClickClose.Tests
{
    public sealed class CloseServiceTests
    {
        private static Process StartCharmap()
        {
            string charmapPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "charmap.exe");
            Process process = Process.Start(new ProcessStartInfo
            {
                FileName = charmapPath,
                UseShellExecute = true
            });
            Assert.NotNull(process);

            DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                if (NativeMethods.GetVisibleWindows().Any(window => (int)window.ProcessId == process.Id))
                {
                    return process;
                }
                Thread.Sleep(100);
            }
            throw new InvalidOperationException("测试窗口未能启动（PID " + process.Id + "）");
        }

        private static AppCandidate BuildCandidate(Process first, Process second)
        {
            return new AppCandidate
            {
                ProcessId = first.Id,
                ProcessIds = new[] { first.Id, second.Id },
                Name = first.ProcessName,
                DisplayName = "测试字符映射表",
                Path = ProcessNameUtility.GetProcessPathSafe(first),
                Windows = new List<WindowInfo>(),
                Kind = "窗口",
                Titles = string.Empty,
                Protected = false
            };
        }

        private static void KillIfAlive(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
            }
        }

        [Fact]
        public async Task PreviewContainsBothPidsAndCancelSendsNothing()
        {
            Process first = null;
            Process second = null;
            try
            {
                first = StartCharmap();
                second = StartCharmap();

                CloseService service = new CloseService();
                List<CloseTargetGroup> groups = service.CreateTargets(new[] { BuildCandidate(first, second) });

                Assert.Single(groups);
                Assert.Equal(2, groups[0].Targets.Count);
                Assert.All(groups[0].Targets, target => Assert.False(target.Skipped));
                Assert.All(groups[0].Targets, target => Assert.True(target.HasVisibleWindow));

                string preview = service.BuildPreviewText(groups, 3);
                Assert.Contains(first.Id.ToString(), preview);
                Assert.Contains(second.Id.ToString(), preview);
                Assert.Contains("有窗口", preview);

                string captured = null;
                bool executed = await service.ExecuteAsync(
                    groups,
                    "all",
                    3,
                    text =>
                    {
                        captured = text;
                        return false;
                    },
                    null);

                Assert.False(executed);
                Assert.Equal(preview, captured);
                Assert.False(first.HasExited);
                Assert.False(second.HasExited);
            }
            finally
            {
                KillIfAlive(first);
                KillIfAlive(second);
            }
        }

        [Fact]
        public async Task ConfirmedExecutionClosesBothProcesses()
        {
            Process first = null;
            Process second = null;
            try
            {
                first = StartCharmap();
                second = StartCharmap();

                CloseService service = new CloseService();
                List<CloseTargetGroup> groups = service.CreateTargets(new[] { BuildCandidate(first, second) });

                string captured = null;
                bool executed = await service.ExecuteAsync(
                    groups,
                    "all",
                    3,
                    text =>
                    {
                        captured = text;
                        return true;
                    },
                    null);

                Assert.True(executed);
                Assert.False(string.IsNullOrEmpty(captured));

                DateTime deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline && (!first.HasExited || !second.HasExited))
                {
                    Thread.Sleep(100);
                }
                Assert.True(first.HasExited);
                Assert.True(second.HasExited);
            }
            finally
            {
                KillIfAlive(first);
                KillIfAlive(second);
            }
        }

        [Fact]
        public async Task ExecuteSelectedAsyncOnlyProcessesCheckedTargets()
        {
            Process first = null;
            Process second = null;
            try
            {
                first = StartCharmap();
                second = StartCharmap();

                CloseService service = new CloseService();
                List<CloseTargetGroup> groups = service.CreateTargets(new[] { BuildCandidate(first, second) });
                CloseTarget selected = groups[0].Targets.Single(target => target.ProcessId == first.Id);

                ClosePreviewResult result = new ClosePreviewResult
                {
                    Confirmed = true,
                    DryRun = false,
                    SelectedTargets = new List<CloseTarget> { selected }
                };
                bool executed = await service.ExecuteSelectedAsync(
                    groups,
                    "all",
                    3,
                    targets => result,
                    null);

                Assert.True(executed);
                DateTime deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline && !first.HasExited)
                {
                    Thread.Sleep(100);
                }
                Assert.True(first.HasExited);
                Assert.False(second.HasExited);
            }
            finally
            {
                KillIfAlive(first);
                KillIfAlive(second);
            }
        }

        [Fact]
        public async Task DryRunDoesNotCloseAnything()
        {
            Process first = null;
            Process second = null;
            try
            {
                first = StartCharmap();
                second = StartCharmap();

                CloseService service = new CloseService();
                List<CloseTargetGroup> groups = service.CreateTargets(new[] { BuildCandidate(first, second) });
                ClosePreviewResult result = new ClosePreviewResult
                {
                    Confirmed = true,
                    DryRun = true,
                    SelectedTargets = groups[0].Targets.ToList()
                };

                bool executed = await service.ExecuteSelectedAsync(
                    groups,
                    "all",
                    3,
                    targets => result,
                    null);

                Assert.False(executed);
                Assert.False(first.HasExited);
                Assert.False(second.HasExited);
            }
            finally
            {
                KillIfAlive(first);
                KillIfAlive(second);
            }
        }

        [Fact]
        public void WindowSelectionLimitsTargetWindows()
        {
            Process first = null;
            Process second = null;
            try
            {
                first = StartCharmap();
                second = StartCharmap();

                List<WindowInfo> firstWindows = NativeMethods.GetVisibleWindows()
                    .Where(window => (int)window.ProcessId == first.Id)
                    .ToList();
                Assert.NotEmpty(firstWindows);

                AppCandidate candidate = BuildCandidate(first, second);
                candidate.SelectedWindowHandles = new HashSet<IntPtr>
                {
                    firstWindows[0].Handle
                };

                CloseService service = new CloseService();
                List<CloseTargetGroup> groups = service.CreateTargets(
                    new[] { candidate },
                    false,
                    true);

                CloseTarget firstTarget = groups[0].Targets.Single(target => target.ProcessId == first.Id);
                CloseTarget secondTarget = groups[0].Targets.Single(target => target.ProcessId == second.Id);
                Assert.False(firstTarget.Skipped);
                Assert.Single(firstTarget.Windows);
                Assert.True(secondTarget.Skipped);
                Assert.Equal("未选择窗口，已跳过", secondTarget.Status);
            }
            finally
            {
                KillIfAlive(first);
                KillIfAlive(second);
            }
        }

        [Fact]
        public async Task StartTimeMismatchSkipsProcess()
        {
            Process process = null;
            try
            {
                process = StartCharmap();
                CloseService service = new CloseService();
                List<CloseTargetGroup> groups = service.CreateTargets(new[] { BuildCandidate(process, process) });
                CloseTarget target = groups[0].Targets.First(item => item.ProcessId == process.Id);
                target.StartTimeUtc = DateTime.UtcNow.AddMinutes(-10);

                ClosePreviewResult result = new ClosePreviewResult
                {
                    Confirmed = true,
                    SelectedTargets = new List<CloseTarget> { target }
                };
                bool executed = await service.ExecuteSelectedAsync(
                    groups,
                    "all",
                    1,
                    targets => result,
                    null);

                Assert.True(executed);
                Assert.False(process.HasExited);
                Assert.Equal("进程身份已变化，已跳过", target.Status);
            }
            finally
            {
                KillIfAlive(process);
            }
        }

        [Fact]
        public async Task ChildProcessesAreIncludedWhenEnabled()
        {
            Process parent = null;
            int childId = 0;
            try
            {
                string cmdPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "cmd.exe");
                parent = Process.Start(new ProcessStartInfo
                {
                    FileName = cmdPath,
                    Arguments = "/c ping 127.0.0.1 -n 30 >nul",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                Assert.NotNull(parent);

                DateTime deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline && childId == 0)
                {
                    ProcessMetadata metadata = ProcessMetadataProvider.Get();
                    HashSet<int> descendants = ProcessTree.GetDescendantIds(
                        new[] { parent.Id },
                        metadata.ParentMap);
                    foreach (int id in descendants)
                    {
                        try
                        {
                            using (Process child = Process.GetProcessById(id))
                            {
                                if (child.ProcessName.IndexOf("ping", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    childId = id;
                                    break;
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                    if (childId == 0)
                    {
                        Thread.Sleep(100);
                    }
                }
                Assert.True(childId > 0, "测试子进程 ping 未启动");

                AppCandidate candidate = new AppCandidate
                {
                    ProcessId = parent.Id,
                    ProcessIds = new[] { parent.Id },
                    Name = parent.ProcessName,
                    DisplayName = "命令行测试",
                    Path = ProcessNameUtility.GetProcessPathSafe(parent),
                    Windows = new List<WindowInfo>(),
                    Kind = "后台",
                    Protected = false
                };

                CloseService service = new CloseService();
                List<CloseTargetGroup> groups = service.CreateTargets(
                    new[] { candidate },
                    true,
                    false);

                Assert.Contains(groups[0].Targets, target =>
                    target.ProcessId == childId && target.IsChildProcess);

                ClosePreviewResult result = new ClosePreviewResult
                {
                    Confirmed = true,
                    SelectedTargets = groups[0].Targets.ToList()
                };
                bool executed = await service.ExecuteSelectedAsync(
                    groups,
                    "all",
                    1,
                    targets => result,
                    null);

                Assert.True(executed);
                DateTime exitDeadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < exitDeadline && !parent.HasExited)
                {
                    Thread.Sleep(100);
                }
                Assert.True(parent.HasExited);
                Assert.False(ProcessNameUtility.ProcessExists(childId));
            }
            finally
            {
                KillIfAlive(parent);
                if (childId > 0)
                {
                    try
                    {
                        using (Process child = Process.GetProcessById(childId))
                        {
                            child.Kill();
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
    }
}
