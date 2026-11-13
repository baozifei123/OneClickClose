using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OneClickClose
{
    public sealed class ProcessScanner
    {
        private readonly Dictionary<string, string> _displayNameCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int?> _subsystemCache =
            new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        private readonly object _cacheLock = new object();

        public Task<List<AppCandidate>> ScanAsync(
            AppConfig config,
            string mode,
            IntPtr foregroundHandle,
            CancellationToken cancellationToken)
        {
            return Task.Run(() => Scan(config, mode, foregroundHandle, cancellationToken), cancellationToken);
        }

        public List<AppCandidate> Scan(
            AppConfig config,
            string mode,
            IntPtr foregroundHandle,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<WindowInfo> windows = NativeMethods.GetVisibleWindows();
            Dictionary<int, List<WindowInfo>> windowsByPid = new Dictionary<int, List<WindowInfo>>();
            foreach (WindowInfo window in windows)
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

            ProcessMetadata metadata = ProcessMetadataProvider.Get();
            Dictionary<int, int> parentMap = metadata.ParentMap;
            Dictionary<int, string> pathMap = metadata.PathMap;

            Process currentProcess = Process.GetCurrentProcess();
            int currentProcessId;
            int? currentSessionId;
            try
            {
                currentProcessId = currentProcess.Id;
                currentSessionId = ProcessNameUtility.GetSessionId(currentProcess);
            }
            finally
            {
                currentProcess.Dispose();
            }

            bool currentElevated = ProcessPrivilege.IsCurrentProcessElevated();
            HashSet<int> ancestors = ProcessTree.BuildAncestorSet(currentProcessId, parentMap);

            HashSet<string> userProtected = new HashSet<string>(
                (config == null ? new List<string>() : config.ProtectedProcesses)
                    .Select(ProcessNameUtility.Normalize)
                    .Where(name => name.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> userProtectedPaths = new HashSet<string>(
                (config == null || config.ProtectedPaths == null
                    ? new List<string>()
                    : config.ProtectedPaths)
                    .Select(ProcessNameUtility.NormalizePath)
                    .Where(path => path.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            HashSet<string> modeProtected = new HashSet<string>(
                ProcessSafety.HardProtectedNames,
                StringComparer.OrdinalIgnoreCase);
            if (string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase))
            {
                modeProtected.Add("applicationframehost");
            }

            if (foregroundHandle == IntPtr.Zero)
            {
                foregroundHandle = NativeMethods.GetForegroundWindow();
            }

            List<AppCandidate> apps = new List<AppCandidate>();
            Process[] processes = Process.GetProcesses();
            Dictionary<int, Process> processById = new Dictionary<int, Process>();
            foreach (Process process in processes)
            {
                try
                {
                    processById[process.Id] = process;
                }
                catch
                {
                }
            }

            try
            {
                foreach (Process process in processes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int processId;
                    try
                    {
                        processId = process.Id;
                    }
                    catch
                    {
                        continue;
                    }
                    if (processId <= 4)
                    {
                        continue;
                    }
                    if (ancestors.Contains(processId))
                    {
                        continue;
                    }

                    int? sessionId = ProcessNameUtility.GetSessionId(process);
                    if (sessionId.HasValue && currentSessionId.HasValue && sessionId.Value != currentSessionId.Value)
                    {
                        continue;
                    }

                    string processName;
                    try
                    {
                        processName = process.ProcessName;
                    }
                    catch
                    {
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(processName))
                    {
                        continue;
                    }

                    string normalizedName = ProcessNameUtility.Normalize(processName);
                    if (modeProtected.Contains(normalizedName))
                    {
                        continue;
                    }

                    string path;
                    pathMap.TryGetValue(processId, out path);
                    if (string.IsNullOrWhiteSpace(path) && windowsByPid.ContainsKey(processId))
                    {
                        path = ProcessNameUtility.GetProcessPathSafe(process);
                    }
                    bool pathProtected = !string.IsNullOrWhiteSpace(path) &&
                        userProtectedPaths.Contains(ProcessNameUtility.NormalizePath(path));

                    bool hasWindow = windowsByPid.ContainsKey(processId);
                    if (string.Equals(mode, "windows", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!hasWindow)
                        {
                            continue;
                        }
                    }
                    else if (!hasWindow)
                    {
                        string pathForSubsystem;
                        if (!pathMap.TryGetValue(processId, out pathForSubsystem) ||
                            string.IsNullOrWhiteSpace(pathForSubsystem))
                        {
                            continue;
                        }
                        if (GetPeSubsystem(pathForSubsystem) != 2)
                        {
                            continue;
                        }

                        int parentId;
                        Process parentProcess;
                        if (parentMap.TryGetValue(processId, out parentId) &&
                            parentId > 0 && parentId != processId &&
                            processById.TryGetValue(parentId, out parentProcess))
                        {
                            string parentName;
                            try
                            {
                                parentName = ProcessNameUtility.Normalize(parentProcess.ProcessName);
                            }
                            catch
                            {
                                parentName = string.Empty;
                            }
                            int? parentSessionId = ProcessNameUtility.GetSessionId(parentProcess);
                            bool sameSession = parentSessionId.HasValue && currentSessionId.HasValue
                                ? parentSessionId.Value == currentSessionId.Value
                                : parentSessionId.Equals(currentSessionId);
                            bool parentIsUserApp = sameSession &&
                                !modeProtected.Contains(parentName) &&
                                !ancestors.Contains(parentId);
                            if (parentIsUserApp)
                            {
                                continue;
                            }
                        }
                    }

                    List<WindowInfo> appWindows = hasWindow
                        ? new List<WindowInfo>(windowsByPid[processId])
                        : new List<WindowInfo>();

                    string kind = "后台";
                    if (appWindows.Count > 0)
                    {
                        kind = "窗口";
                        foreach (WindowInfo appWindow in appWindows)
                        {
                            if (appWindow.Handle == foregroundHandle)
                            {
                                kind = "前台";
                                break;
                            }
                        }
                    }

                    string displayName = GetDisplayName(path, processName);
                    string titles = string.Empty;
                    if (appWindows.Count > 0)
                    {
                        titles = string.Join(" / ",
                            appWindows.Take(2).Select(window => window.Title).ToArray());
                        if (appWindows.Count > 2)
                        {
                            titles += "  (+" + (appWindows.Count - 2) + ")";
                        }
                    }

                    apps.Add(new AppCandidate
                    {
                        ProcessId = processId,
                        ProcessIds = new[] { processId },
                        Name = processName,
                        DisplayName = displayName,
                        Path = path ?? string.Empty,
                        Windows = appWindows,
                        Kind = kind,
                        Titles = titles,
                        Protected = userProtected.Contains(normalizedName) || pathProtected,
                        IsForeground = kind == "前台",
                        IsTaskbarApp = appWindows.Any(window => window.IsTaskbarWindow),
                        IsSuspendedUwp = appWindows.Any(window => window.IsCloakedUwp),
                        RequiresElevation = !currentElevated && ProcessPrivilege.IsProcessElevated(processId)
                    });
                }
            }
            finally
            {
                foreach (Process process in processes)
                {
                    try { process.Dispose(); }
                    catch { }
                }
            }

            return GroupCandidates(apps);
        }

        public static List<AppCandidate> GroupCandidates(IEnumerable<AppCandidate> apps)
        {
            Dictionary<string, List<AppCandidate>> groups =
                new Dictionary<string, List<AppCandidate>>(StringComparer.OrdinalIgnoreCase);
            List<string> order = new List<string>();

            foreach (AppCandidate app in apps)
            {
                string key;
                if (!string.IsNullOrWhiteSpace(app.Path))
                {
                    key = "path:" + app.Path.Trim().ToLowerInvariant();
                }
                else
                {
                    key = "pid:" + app.ProcessId;
                }

                List<AppCandidate> members;
                if (!groups.TryGetValue(key, out members))
                {
                    members = new List<AppCandidate>();
                    groups[key] = members;
                    order.Add(key);
                }
                members.Add(app);
            }

            List<AppCandidate> result = new List<AppCandidate>();
            foreach (string key in order)
            {
                List<AppCandidate> members = groups[key];
                AppCandidate first = members[0];
                int[] processIds = members
                    .SelectMany(member => member.ProcessIds == null || member.ProcessIds.Length == 0
                        ? new[] { member.ProcessId }
                        : member.ProcessIds)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray();

                List<WindowInfo> groupWindows = new List<WindowInfo>();
                List<string> titleParts = new List<string>();
                bool isForeground = false;
                bool allProtected = true;
                bool isTaskbarApp = false;
                bool isSuspendedUwp = false;
                bool requiresElevation = false;
                foreach (AppCandidate member in members)
                {
                    if (member.Windows != null)
                    {
                        groupWindows.AddRange(member.Windows);
                    }
                    if (member.IsForeground)
                    {
                        isForeground = true;
                    }
                    if (!member.Protected)
                    {
                        allProtected = false;
                    }
                    if (member.IsTaskbarApp)
                    {
                        isTaskbarApp = true;
                    }
                    if (member.IsSuspendedUwp)
                    {
                        isSuspendedUwp = true;
                    }
                    if (member.RequiresElevation)
                    {
                        requiresElevation = true;
                    }
                    if (!string.IsNullOrWhiteSpace(member.Titles) && !titleParts.Contains(member.Titles))
                    {
                        titleParts.Add(member.Titles);
                    }
                }

                string kind = "后台";
                if (groupWindows.Count > 0)
                {
                    kind = "窗口";
                }
                if (isForeground)
                {
                    kind = "前台";
                }

                string groupTitles = string.Join(" / ", titleParts.Take(3).ToArray());
                if (titleParts.Count > 3)
                {
                    groupTitles += "  (+" + (titleParts.Count - 3) + ")";
                }

                result.Add(new AppCandidate
                {
                    ProcessId = processIds.Length > 0 ? processIds[0] : first.ProcessId,
                    ProcessIds = processIds,
                    Name = first.Name,
                    DisplayName = first.DisplayName,
                    Path = first.Path,
                    Windows = groupWindows,
                    Kind = kind,
                    Titles = groupTitles,
                    Protected = allProtected,
                    IsForeground = isForeground,
                    IsTaskbarApp = isTaskbarApp,
                    IsSuspendedUwp = isSuspendedUwp,
                    RequiresElevation = requiresElevation
                });
            }

            return result
                .OrderBy(candidate => candidate.Protected ? 1 : 0)
                .ThenBy(candidate => candidate.IsForeground ? 0 : 1)
                .ThenBy(candidate => candidate.DisplayName ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private int? GetPeSubsystem(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            lock (_cacheLock)
            {
                int? cached;
                if (_subsystemCache.TryGetValue(path, out cached))
                {
                    return cached;
                }
            }

            int? result = null;
            try
            {
                using (FileStream stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite))
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    if (stream.Length >= 512)
                    {
                        stream.Position = 0x3C;
                        int peOffset = reader.ReadInt32();
                        if (peOffset > 0 && peOffset + 94 <= stream.Length)
                        {
                            stream.Position = peOffset;
                            uint signature = reader.ReadUInt32();
                            if (signature == 0x00004550)
                            {
                                stream.Position = peOffset + 4 + 20 + 68;
                                result = reader.ReadUInt16();
                            }
                        }
                    }
                }
            }
            catch
            {
                result = null;
            }

            lock (_cacheLock)
            {
                _subsystemCache[path] = result;
            }
            return result;
        }

        private string GetDisplayName(string path, string fallback)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return fallback;
            }

            lock (_cacheLock)
            {
                string cached;
                if (_displayNameCache.TryGetValue(path, out cached))
                {
                    return cached;
                }
            }

            string name = fallback;
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(info.FileDescription))
                {
                    name = info.FileDescription.Trim();
                }
                else if (!string.IsNullOrWhiteSpace(info.ProductName))
                {
                    name = info.ProductName.Trim();
                }
            }
            catch
            {
                name = fallback;
            }

            lock (_cacheLock)
            {
                _displayNameCache[path] = name;
            }
            return name;
        }
    }
}
