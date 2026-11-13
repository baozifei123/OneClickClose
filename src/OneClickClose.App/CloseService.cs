using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OneClickClose
{
    public sealed class CloseService
    {
        private static readonly HashSet<string> SuccessStatuses = new HashSet<string>(
            new[]
            {
                "已关闭",
                "已强制退出",
                "窗口已关闭（进程保留）",
                "窗口已关（进程保留）"
            },
            StringComparer.Ordinal);

        public List<CloseTargetGroup> CreateTargets(IEnumerable<AppCandidate> selected)
        {
            return CreateTargets(selected, false, false);
        }

        public List<CloseTargetGroup> CreateTargets(
            IEnumerable<AppCandidate> selected,
            bool includeChildProcesses,
            bool applyWindowSelection)
        {
            List<WindowInfo> visibleWindows = NativeMethods.GetVisibleWindows();
            Dictionary<int, List<WindowInfo>> windowsByPid = new Dictionary<int, List<WindowInfo>>();
            foreach (WindowInfo window in visibleWindows)
            {
                int processId = (int)window.ProcessId;
                List<WindowInfo> list;
                if (!windowsByPid.TryGetValue(processId, out list))
                {
                    list = new List<WindowInfo>();
                    windowsByPid[processId] = list;
                }
                list.Add(window);
            }

            ProcessMetadata metadata = includeChildProcesses ? ProcessMetadataProvider.Get() : null;
            bool currentElevated = ProcessPrivilege.IsCurrentProcessElevated();
            int? currentSessionId = null;
            HashSet<int> selfAncestors = null;
            if (metadata != null)
            {
                using (Process self = Process.GetCurrentProcess())
                {
                    currentSessionId = ProcessNameUtility.GetSessionId(self);
                    selfAncestors = ProcessTree.BuildAncestorSet(self.Id, metadata.ParentMap);
                }
            }

            List<CloseTargetGroup> groups = new List<CloseTargetGroup>();
            foreach (AppCandidate app in selected)
            {
                CloseTargetGroup group = new CloseTargetGroup { App = app };
                HashSet<int> rootIds = new HashSet<int>(GetProcessIds(app));
                HashSet<int> groupProcessIds = new HashSet<int>();
                bool hasWindowSelection = applyWindowSelection &&
                    app.SelectedWindowHandles != null &&
                    app.SelectedWindowHandles.Count > 0;

                foreach (int processId in rootIds)
                {
                    if (!groupProcessIds.Add(processId))
                    {
                        continue;
                    }

                    Process live = null;
                    try
                    {
                        live = Process.GetProcessById(processId);
                    }
                    catch
                    {
                        live = null;
                    }

                    if (live == null)
                    {
                        group.Targets.Add(CreateSkippedTarget(
                            processId, app.DisplayName, app.Name, "已退出"));
                        continue;
                    }

                    try
                    {
                        string liveName = live.ProcessName;
                        if (!ProcessNameUtility.SameName(liveName, app.Name))
                        {
                            group.Targets.Add(CreateSkippedTarget(
                                processId, app.DisplayName, liveName, "PID 已变化，已跳过"));
                            continue;
                        }

                        string livePath = ProcessNameUtility.GetProcessPathSafe(live);
                        if (string.IsNullOrWhiteSpace(livePath))
                        {
                            livePath = ProcessMetadataProvider.GetProcessPath(processId);
                        }
                        if (!string.IsNullOrWhiteSpace(app.Path) &&
                            !string.IsNullOrWhiteSpace(livePath) &&
                            !string.Equals(livePath, app.Path, StringComparison.OrdinalIgnoreCase))
                        {
                            group.Targets.Add(CreateSkippedTarget(
                                processId, app.DisplayName, liveName, "程序路径已变化，已跳过"));
                            continue;
                        }

                        List<WindowInfo> targetWindows;
                        if (!windowsByPid.TryGetValue(processId, out targetWindows))
                        {
                            targetWindows = new List<WindowInfo>();
                        }
                        if (hasWindowSelection)
                        {
                            targetWindows = targetWindows
                                .Where(window => app.SelectedWindowHandles.Contains(window.Handle))
                                .ToList();
                            if (targetWindows.Count == 0)
                            {
                                group.Targets.Add(CreateSkippedTarget(
                                    processId, app.DisplayName, liveName, "未选择窗口，已跳过", true));
                                continue;
                            }
                        }

                        group.Targets.Add(new CloseTarget
                        {
                            ProcessId = processId,
                            ProcessName = liveName,
                            DisplayName = app.DisplayName,
                            Path = livePath ?? string.Empty,
                            Windows = new List<WindowInfo>(targetWindows),
                            WindowTitles = BuildWindowTitles(targetWindows),
                            StartTimeUtc = GetStartTimeUtc(live),
                            RequiresElevation = !currentElevated && ProcessPrivilege.IsProcessElevated(processId),
                            IsChildProcess = false,
                            RootProcessId = processId
                        });
                    }
                    catch
                    {
                        group.Targets.Add(CreateSkippedTarget(
                            processId, app.DisplayName, app.Name, "进程信息不可用，已跳过"));
                    }
                    finally
                    {
                        live.Dispose();
                    }
                }

                if (includeChildProcesses && metadata != null && rootIds.Count > 0)
                {
                    HashSet<int> descendants = ProcessTree.GetDescendantIds(rootIds, metadata.ParentMap);
                    foreach (int childId in descendants.OrderBy(id => id))
                    {
                        if (!groupProcessIds.Add(childId))
                        {
                            continue;
                        }
                        if (selfAncestors != null && selfAncestors.Contains(childId))
                        {
                            continue;
                        }

                        Process live = null;
                        try
                        {
                            live = Process.GetProcessById(childId);
                        }
                        catch
                        {
                            live = null;
                        }
                        if (live == null)
                        {
                            continue;
                        }

                        try
                        {
                            string liveName = live.ProcessName;
                            if (ProcessSafety.IsHardProtected(liveName) ||
                                string.Equals(ProcessNameUtility.Normalize(liveName), "applicationframehost", StringComparison.Ordinal))
                            {
                                continue;
                            }

                            int? sessionId = ProcessNameUtility.GetSessionId(live);
                            if (sessionId.HasValue && currentSessionId.HasValue &&
                                sessionId.Value != currentSessionId.Value)
                            {
                                continue;
                            }

                            string livePath;
                            metadata.PathMap.TryGetValue(childId, out livePath);
                            if (string.IsNullOrWhiteSpace(livePath))
                            {
                                livePath = ProcessNameUtility.GetProcessPathSafe(live);
                            }
                            if (string.IsNullOrWhiteSpace(livePath))
                            {
                                continue;
                            }

                            int rootId = rootIds.FirstOrDefault(root =>
                                ProcessTree.IsDescendantOf(childId, root, metadata.ParentMap));
                            if (rootId <= 0)
                            {
                                continue;
                            }

                            List<WindowInfo> childWindows;
                            if (!windowsByPid.TryGetValue(childId, out childWindows))
                            {
                                childWindows = new List<WindowInfo>();
                            }

                            group.Targets.Add(new CloseTarget
                            {
                                ProcessId = childId,
                                ProcessName = liveName,
                                DisplayName = app.DisplayName,
                                Path = livePath,
                                Windows = new List<WindowInfo>(childWindows),
                                WindowTitles = BuildWindowTitles(childWindows),
                                StartTimeUtc = GetStartTimeUtc(live),
                                RequiresElevation = !currentElevated && ProcessPrivilege.IsProcessElevated(childId),
                                IsChildProcess = true,
                                RootProcessId = rootId
                            });
                        }
                        catch
                        {
                        }
                        finally
                        {
                            live.Dispose();
                        }
                    }
                }

                groups.Add(group);
            }
            return groups;
        }

        public string BuildPreviewText(List<CloseTargetGroup> groups, int timeoutSeconds)
        {
            List<CloseTarget> liveTargets = groups
                .SelectMany(group => group.Targets)
                .Where(target => !target.Skipped)
                .ToList();
            int visibleCount = liveTargets.Count(target => target.HasVisibleWindow);
            int backgroundCount = liveTargets.Count - visibleCount;
            int childCount = liveTargets.Count(target => target.IsChildProcess);
            int elevatedCount = liveTargets.Count(target => target.RequiresElevation);

            List<string> lines = new List<string>();
            lines.Add("选中 " + groups.Count + " 个应用组，共 " + liveTargets.Count + " 个进程" +
                (childCount > 0 ? "（含 " + childCount + " 个子进程）" : string.Empty) + "。");
            lines.Add("有窗口：" + visibleCount + " 个（先发 WM_CLOSE，最长等 " + timeoutSeconds +
                " 秒，未退出再强杀）；无可见窗口：" + backgroundCount + " 个（确认后直接强杀）。");
            if (elevatedCount > 0)
            {
                lines.Add("其中 " + elevatedCount + " 个进程需要管理员权限；当前不是管理员时可能显示“失败/权限不足”。");
            }
            lines.Add(string.Empty);

            foreach (CloseTargetGroup group in groups)
            {
                lines.Add(group.App.DisplayName + "  [" + group.App.ProcessCount + " 个进程]");
                foreach (CloseTarget target in group.Targets)
                {
                    if (target.Skipped)
                    {
                        lines.Add("  PID " + target.ProcessId + ": " + target.Status);
                        continue;
                    }

                    string action = target.HasVisibleWindow
                        ? "有窗口：先正常关闭，" + timeoutSeconds + " 秒后仍运行则强制结束"
                        : "无可见窗口：确认后立即强制结束";
                    string detail = "  " + target.ProcessName + "  PID " + target.ProcessId +
                        (target.IsChildProcess ? " [子进程]" : string.Empty) +
                        (target.RequiresElevation ? " [需要管理员]" : string.Empty) +
                        " — " + action;
                    if (!string.IsNullOrWhiteSpace(target.WindowTitles))
                    {
                        detail += Environment.NewLine + "      窗口：" + target.WindowTitles;
                    }
                    lines.Add(detail);
                }
            }
            return string.Join(Environment.NewLine, lines.ToArray());
        }

        public async Task<bool> ExecuteAsync(
            List<CloseTargetGroup> groups,
            string mode,
            int timeoutSeconds,
            Func<string, bool> confirm,
            IProgress<string> progress)
        {
            if (confirm != null && string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase))
            {
                string preview = BuildPreviewText(groups, timeoutSeconds);
                if (!confirm(preview))
                {
                    if (progress != null)
                    {
                        progress.Report("已取消，没有发送关闭请求");
                    }
                    return false;
                }
            }

            List<CloseTarget> targets = groups
                .SelectMany(group => group.Targets)
                .Where(target => !target.Skipped)
                .ToList();
            await Task.Run(() => ExecuteCore(targets, mode, timeoutSeconds, progress));
            return true;
        }

        public async Task<bool> ExecuteSelectedAsync(
            List<CloseTargetGroup> groups,
            string mode,
            int timeoutSeconds,
            Func<List<CloseTarget>, ClosePreviewResult> confirm,
            IProgress<string> progress)
        {
            List<CloseTarget> allTargets = groups
                .SelectMany(group => group.Targets)
                .ToList();
            List<CloseTarget> selectedTargets = allTargets
                .Where(target => !target.Skipped)
                .ToList();

            if (confirm != null && string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase))
            {
                ClosePreviewResult result = confirm(allTargets);
                if (result == null || !result.Confirmed)
                {
                    if (progress != null)
                    {
                        progress.Report("已取消，没有发送关闭请求");
                    }
                    return false;
                }
                if (result.DryRun)
                {
                    if (progress != null)
                    {
                        progress.Report("演练完成，没有发送关闭请求");
                    }
                    return false;
                }

                HashSet<CloseTarget> available = new HashSet<CloseTarget>(selectedTargets);
                if (result.SelectedTargets != null)
                {
                    selectedTargets = result.SelectedTargets
                        .Where(target => target != null && available.Contains(target))
                        .Distinct()
                        .ToList();
                }
            }

            if (selectedTargets.Count == 0)
            {
                if (progress != null)
                {
                    progress.Report("没有可执行的目标");
                }
                return false;
            }

            await Task.Run(() => ExecuteCore(selectedTargets, mode, timeoutSeconds, progress));
            return true;
        }

        public static void GetGroupSummary(
            CloseTargetGroup group,
            out int processedCount,
            out int totalCount,
            out bool allSucceeded)
        {
            List<string> statuses = group.Targets
                .Where(target => !target.IsUserExcluded)
                .Select(target => target.Status)
                .Where(status => !string.IsNullOrEmpty(status))
                .ToList();
            totalCount = statuses.Count;
            processedCount = statuses.Count(status => SuccessStatuses.Contains(status));
            allSucceeded = totalCount > 0 && processedCount == totalCount;
        }

        private static void ExecuteCore(
            List<CloseTarget> liveTargets,
            string mode,
            int timeoutSeconds,
            IProgress<string> progress)
        {
            bool windowsOnly = !string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase);
            ProcessMetadata identityMetadata = ProcessMetadataProvider.Get();
            IDictionary<int, int> parentMap = identityMetadata.ParentMap;

            foreach (CloseTarget target in liveTargets)
            {
                using (Process live = GetLiveProcess(target, parentMap))
                {
                    if (live == null)
                    {
                        target.Status = "进程身份已变化，已跳过";
                        continue;
                    }

                    if (!target.HasVisibleWindow)
                    {
                        if (windowsOnly)
                        {
                            target.Status = "窗口已关闭（进程保留）";
                        }
                        continue;
                    }

                    foreach (WindowInfo window in target.Windows)
                    {
                        if (!NativeMethods.IsWindow(window.Handle))
                        {
                            continue;
                        }
                        uint ownerProcessId;
                        NativeMethods.GetWindowThreadProcessId(window.Handle, out ownerProcessId);
                        if (ownerProcessId == (uint)target.ProcessId)
                        {
                            NativeMethods.PostMessage(
                                window.Handle,
                                NativeMethods.WM_CLOSE,
                                IntPtr.Zero,
                                IntPtr.Zero);
                        }
                    }
                }
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                List<CloseTarget> pending;
                if (windowsOnly)
                {
                    HashSet<int> liveProcessIds = new HashSet<int>(
                        NativeMethods.GetVisibleWindows().Select(window => (int)window.ProcessId));
                    pending = liveTargets
                        .Where(target => !target.Skipped &&
                            target.HasVisibleWindow &&
                            liveProcessIds.Contains(target.ProcessId))
                        .ToList();
                }
                else
                {
                    pending = liveTargets
                        .Where(target => !target.Skipped &&
                            target.HasVisibleWindow &&
                            ProcessNameUtility.ProcessExists(target.ProcessId))
                        .ToList();
                }

                if (pending.Count == 0)
                {
                    break;
                }

                int remaining = (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalSeconds);
                if (remaining < 0)
                {
                    remaining = 0;
                }
                if (progress != null)
                {
                    progress.Report("正在关闭... 剩余 " + pending.Count + " 个进程（最长等待 " + remaining + "s）");
                }
                Thread.Sleep(200);
            }

            if (windowsOnly)
            {
                HashSet<int> liveProcessIds = new HashSet<int>(
                    NativeMethods.GetVisibleWindows().Select(window => (int)window.ProcessId));
                foreach (CloseTarget target in liveTargets)
                {
                    if (target.Skipped)
                    {
                        continue;
                    }

                    using (Process live = GetLiveProcess(target, parentMap))
                    {
                        if (live == null)
                        {
                            target.Status = ProcessNameUtility.ProcessExists(target.ProcessId)
                                ? "进程身份已变化，已跳过"
                                : "已关闭";
                        }
                        else
                        {
                            target.Status = liveProcessIds.Contains(target.ProcessId)
                                ? "窗口未响应"
                                : "窗口已关（进程保留）";
                        }
                    }
                }
            }
            else
            {
                foreach (CloseTarget target in liveTargets)
                {
                    if (target.Skipped)
                    {
                        continue;
                    }

                    Process live = GetLiveProcess(target, parentMap);
                    if (live == null)
                    {
                        target.Status = ProcessNameUtility.ProcessExists(target.ProcessId)
                            ? "进程身份已变化，已跳过"
                            : "已关闭";
                        continue;
                    }

                    try
                    {
                        live.Kill();
                        target.Status = live.WaitForExit(2000) ? "已强制退出" : "强杀未完成";
                    }
                    catch
                    {
                        target.Status = ProcessNameUtility.ProcessExists(target.ProcessId)
                            ? "失败/权限不足"
                            : "已关闭";
                    }
                    finally
                    {
                        live.Dispose();
                    }
                }
            }
        }

        private static Process GetLiveProcess(
            CloseTarget target,
            IDictionary<int, int> parentMap)
        {
            Process live;
            try
            {
                live = Process.GetProcessById(target.ProcessId);
            }
            catch
            {
                return null;
            }

            try
            {
                if (!ProcessNameUtility.SameName(live.ProcessName, target.ProcessName))
                {
                    live.Dispose();
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(target.Path))
                {
                    string livePath = ProcessNameUtility.GetProcessPathSafe(live);
                    if (string.IsNullOrWhiteSpace(livePath))
                    {
                        livePath = ProcessMetadataProvider.GetProcessPath(target.ProcessId);
                    }
                    if (!string.IsNullOrWhiteSpace(livePath) &&
                        !string.Equals(livePath, target.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        live.Dispose();
                        return null;
                    }
                }

                if (target.StartTimeUtc.HasValue)
                {
                    DateTime liveStartUtc;
                    try
                    {
                        liveStartUtc = live.StartTime.ToUniversalTime();
                    }
                    catch
                    {
                        liveStartUtc = DateTime.MinValue;
                    }
                    if (liveStartUtc != DateTime.MinValue &&
                        Math.Abs((liveStartUtc - target.StartTimeUtc.Value).TotalSeconds) > 1.0)
                    {
                        live.Dispose();
                        return null;
                    }
                }

                if (target.IsChildProcess &&
                    target.RootProcessId > 0 &&
                    parentMap != null &&
                    !ProcessTree.IsDescendantOf(target.ProcessId, target.RootProcessId, parentMap))
                {
                    live.Dispose();
                    return null;
                }

                return live;
            }
            catch
            {
                live.Dispose();
                return null;
            }
        }

        private static IEnumerable<int> GetProcessIds(AppCandidate app)
        {
            if (app.ProcessIds != null && app.ProcessIds.Length > 0)
            {
                return app.ProcessIds.Distinct().OrderBy(id => id);
            }
            if (app.ProcessId > 0)
            {
                return new[] { app.ProcessId };
            }
            return new int[0];
        }

        private static DateTime? GetStartTimeUtc(Process process)
        {
            try
            {
                return process.StartTime.ToUniversalTime();
            }
            catch
            {
                return null;
            }
        }

        private static string BuildWindowTitles(List<WindowInfo> windows)
        {
            if (windows == null || windows.Count == 0)
            {
                return string.Empty;
            }
            return string.Join(" / ", windows.Take(2).Select(window => window.Title).ToArray());
        }

        private static CloseTarget CreateSkippedTarget(
            int processId,
            string displayName,
            string processName,
            string status,
            bool isUserExcluded = false)
        {
            return new CloseTarget
            {
                ProcessId = processId,
                ProcessName = processName ?? string.Empty,
                DisplayName = displayName ?? string.Empty,
                Path = string.Empty,
                Windows = new List<WindowInfo>(),
                WindowTitles = string.Empty,
                Status = status,
                IsUserExcluded = isUserExcluded
            };
        }
    }
}
