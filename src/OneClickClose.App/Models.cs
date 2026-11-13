using System;
using System.Collections.Generic;
using System.Linq;

namespace OneClickClose
{
    public sealed class WindowInfo
    {
        public IntPtr Handle { get; set; }
        public string Title { get; set; }
        public uint ProcessId { get; set; }
        public bool IsMinimized { get; set; }
        public bool IsTaskbarWindow { get; set; }
        public bool IsCloakedUwp { get; set; }
    }

    public sealed class AppCandidate
    {
        public int ProcessId { get; set; }
        public int[] ProcessIds { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Path { get; set; }
        public List<WindowInfo> Windows { get; set; }
        public string Kind { get; set; }
        public string Titles { get; set; }
        public bool Protected { get; set; }
        public bool IsForeground { get; set; }
        public bool IsTaskbarApp { get; set; }
        public bool IsSuspendedUwp { get; set; }
        public bool RequiresElevation { get; set; }
        public HashSet<IntPtr> SelectedWindowHandles { get; set; }

        public AppCandidate()
        {
            ProcessIds = new int[0];
            Name = string.Empty;
            DisplayName = string.Empty;
            Path = string.Empty;
            Windows = new List<WindowInfo>();
            Kind = "后台";
            Titles = string.Empty;
            SelectedWindowHandles = null;
        }

        public int ProcessCount
        {
            get { return ProcessIds == null ? 0 : ProcessIds.Length; }
        }

        public int WindowCount
        {
            get { return Windows == null ? 0 : Windows.Count; }
        }
    }

    public sealed class CloseTarget
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; }
        public string DisplayName { get; set; }
        public string Path { get; set; }
        public List<WindowInfo> Windows { get; set; }
        public string WindowTitles { get; set; }
        public string Status { get; set; }
        public DateTime? StartTimeUtc { get; set; }
        public bool RequiresElevation { get; set; }
        public bool IsChildProcess { get; set; }
        public bool IsUserExcluded { get; set; }
        public int RootProcessId { get; set; }

        public CloseTarget()
        {
            ProcessName = string.Empty;
            DisplayName = string.Empty;
            Path = string.Empty;
            Windows = new List<WindowInfo>();
            WindowTitles = string.Empty;
            Status = string.Empty;
        }

        public bool Skipped
        {
            get { return !string.IsNullOrEmpty(Status); }
        }

        public bool HasVisibleWindow
        {
            get { return Windows != null && Windows.Count > 0; }
        }
    }

    public sealed class CloseTargetGroup
    {
        public AppCandidate App { get; set; }
        public List<CloseTarget> Targets { get; set; }

        public CloseTargetGroup()
        {
            Targets = new List<CloseTarget>();
        }

        public IEnumerable<CloseTarget> LiveTargets
        {
            get { return Targets.Where(target => !target.Skipped); }
        }
    }

    public sealed class ClosePreviewResult
    {
        public bool Confirmed { get; set; }
        public bool DryRun { get; set; }
        public List<CloseTarget> SelectedTargets { get; set; }

        public ClosePreviewResult()
        {
            SelectedTargets = new List<CloseTarget>();
        }
    }

    public sealed class AppConfig
    {
        public int SchemaVersion { get; set; }
        public string Hotkey { get; set; }
        public int GracefulTimeoutSeconds { get; set; }
        public List<string> ProtectedProcesses { get; set; }
        public List<string> ProtectedPaths { get; set; }
        public string LastMode { get; set; }
        public Dictionary<string, List<string>> Profiles { get; set; }
        public string ActiveProfile { get; set; }
        public bool ShowAllWindows { get; set; }
        public bool IncludeChildProcesses { get; set; }
        public bool IncludeSuspendedUwp { get; set; }

        public AppConfig()
        {
            SchemaVersion = 2;
            Hotkey = "Ctrl+Alt+X";
            GracefulTimeoutSeconds = 5;
            ProtectedProcesses = new List<string>();
            ProtectedPaths = new List<string>();
            LastMode = "windows";
            Profiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            ActiveProfile = string.Empty;
            ShowAllWindows = false;
            IncludeChildProcesses = true;
            IncludeSuspendedUwp = false;
        }

        public static AppConfig CreateDefault()
        {
            return new AppConfig();
        }

        public AppConfig Clone()
        {
            AppConfig copy = new AppConfig();
            copy.SchemaVersion = SchemaVersion;
            copy.Hotkey = Hotkey;
            copy.GracefulTimeoutSeconds = GracefulTimeoutSeconds;
            copy.ProtectedProcesses = ProtectedProcesses == null
                ? new List<string>()
                : new List<string>(ProtectedProcesses);
            copy.ProtectedPaths = ProtectedPaths == null
                ? new List<string>()
                : new List<string>(ProtectedPaths);
            copy.LastMode = LastMode;
            copy.ActiveProfile = ActiveProfile;
            copy.ShowAllWindows = ShowAllWindows;
            copy.IncludeChildProcesses = IncludeChildProcesses;
            copy.IncludeSuspendedUwp = IncludeSuspendedUwp;
            copy.Profiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (Profiles != null)
            {
                foreach (KeyValuePair<string, List<string>> profile in Profiles)
                {
                    copy.Profiles[profile.Key] = profile.Value == null
                        ? new List<string>()
                        : new List<string>(profile.Value);
                }
            }
            return copy;
        }
    }
}
