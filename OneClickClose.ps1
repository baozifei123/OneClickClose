#requires -Version 5.1
<#
.SYNOPSIS
    清场助手：按热键唤出程序清单，勾选后批量关闭。
.DESCRIPTION
    两种模式：
      1. 只关窗口（保留托盘）：向窗口发送 WM_CLOSE，等效于点 X。
      2. 彻底退出（含后台）：先关窗口，超时后强制结束进程；没有窗口的后台程序直接结束。
    保护名单中的程序不会出现在清单里；右键可把程序加入/移出保护名单。
.PARAMETER SelfTest
    只做枚举测试，不启动界面。
.PARAMETER UiTest
    构建界面并刷新一次清单后退出，用于验证 UI 代码。
.PARAMETER CloseTest
    启动一个系统自带的测试窗口并优雅关闭它，验证关闭流程（不会碰你正在运行的程序）。
.PARAMETER ProtectRefreshTest
    使用临时配置验证保护名单操作的响应性。
.PARAMETER RefreshTest
    验证刷新进程清单时 WinForms UI 消息循环不会被长时间阻塞。
.PARAMETER SafetyTest
    启动测试窗口，验证彻底退出预览包含 PID 且取消时不会关闭目标。
.PARAMETER ConfigTest
    在隔离临时目录验证旧配置迁移、预设保存、原子备份与恢复，不改实际 config.json。
.PARAMETER TaskbarTest
    验证普通窗口/工具窗口的任务栏资格判断，以及“显示所有窗口”过滤开关。
.PARAMETER Hidden
    启动后只驻留托盘，不显示面板。
#>
param(
    [switch]$SelfTest,
    [switch]$UiTest,
    [switch]$CloseTest,
    [switch]$ProtectRefreshTest,
    [switch]$RefreshTest,
    [switch]$SafetyTest,
    [switch]$ConfigTest,
    [switch]$TaskbarTest,
    [switch]$Hidden
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Win32 / WinForms 辅助类型
# ---------------------------------------------------------------------------
$nativeCode = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

public class AppWindow
{
    public IntPtr Handle;
    public string Title;
    public uint ProcessId;
    public bool IsMinimized;
    public bool IsTaskbarWindow;
}

public static class NativeMethods
{
    public const int WM_CLOSE = 0x0010;
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const uint GW_OWNER = 4;
    public const int DWMWA_CLOAKED = 14;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hWnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    public static bool IsTaskbarEligibleWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindowVisible(hWnd)) return false;
        if (GetWindowTextLength(hWnd) <= 0) return false;

        int extendedStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        bool appWindow = (extendedStyle & WS_EX_APPWINDOW) != 0;
        bool toolWindow = (extendedStyle & WS_EX_TOOLWINDOW) != 0;
        if (toolWindow && !appWindow) return false;
        if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero && !appWindow) return false;

        try
        {
            int cloaked;
            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return false;
        }
        catch
        {
        }

        StringBuilder className = new StringBuilder(256);
        GetClassName(hWnd, className, className.Capacity);
        string windowClass = className.ToString();
        if (windowClass == "Shell_TrayWnd" ||
            windowClass == "Shell_SecondaryTrayWnd" ||
            windowClass == "Progman" ||
            windowClass == "WorkerW")
        {
            return false;
        }
        return true;
    }

    public static List<AppWindow> GetVisibleWindows()
    {
        List<AppWindow> result = new List<AppWindow>();
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
        {
            if (!IsWindowVisible(hWnd)) return true;

            int length = GetWindowTextLength(hWnd);
            if (length <= 0) return true;

            StringBuilder builder = new StringBuilder(length + 1);
            GetWindowText(hWnd, builder, builder.Capacity);
            string title = builder.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);

            AppWindow window = new AppWindow();
            window.Handle = hWnd;
            window.Title = title;
            window.ProcessId = pid;
            window.IsMinimized = IsIconic(hWnd);
            window.IsTaskbarWindow = IsTaskbarEligibleWindow(hWnd);
            result.Add(window);
            return true;
        }, IntPtr.Zero);
        return result;
    }
}

public class HotKeyForm : Form
{
    private const int HotKeyId = 0x4150;
    private uint modifiers;
    private uint virtualKey;

    public event EventHandler HotKeyPressed;
    public bool Registered { get; private set; }

    public HotKeyForm(uint mods, uint vk)
    {
        modifiers = mods;
        virtualKey = vk;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = new System.Drawing.Rectangle(-32000, -32000, 1, 1);
        Opacity = 0;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Registered = NativeMethods.RegisterHotKey(Handle, HotKeyId, modifiers, virtualKey);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HotKeyId)
        {
            EventHandler handler = HotKeyPressed;
            if (handler != null) handler(this, EventArgs.Empty);
            return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (Registered)
        {
            NativeMethods.UnregisterHotKey(Handle, HotKeyId);
            Registered = false;
        }
        base.Dispose(disposing);
    }
}
'@

Add-Type -TypeDefinition $nativeCode -ReferencedAssemblies 'System.Windows.Forms', 'System.Drawing' -ErrorAction Stop

# ---------------------------------------------------------------------------
# 配置
# ---------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    $script:BaseDir = (Get-Location).Path
} else {
    $script:BaseDir = $PSScriptRoot
}
$script:ConfigPath = Join-Path $script:BaseDir 'config.json'

$script:Config = [ordered]@{
    schemaVersion          = 2
    hotkey                 = 'Ctrl+Alt+X'
    gracefulTimeoutSeconds = 5
    protectedProcesses     = @()
    protectedPaths         = @()
    lastMode               = 'windows'
    profiles               = [ordered]@{}
    activeProfile          = ''
    showAllWindows         = $false
    includeChildProcesses  = $true
    includeSuspendedUwp    = $false
}
$script:LastConfigSaveSucceeded = $false
$script:ConfigSaveError = $null
$script:ConfigLoadError = $null

function Save-Config {
    $script:LastConfigSaveSucceeded = $false
    $script:ConfigSaveError = $null
    $configTempPath = $script:ConfigPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    $configReplaceBackupPath = $script:ConfigPath + '.replace-' + [guid]::NewGuid().ToString('N') + '.tmp'
    $backupPath = $script:ConfigPath + '.bak'
    $backupTempPath = $backupPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    $backupReplaceBackupPath = $backupPath + '.replace-' + [guid]::NewGuid().ToString('N') + '.tmp'

    try {
        $profilePayload = [ordered]@{}
        if ($null -ne $script:Config.profiles) {
            if ($script:Config.profiles -is [System.Collections.IDictionary]) {
                foreach ($profileName in $script:Config.profiles.Keys) {
                    $profilePayload[[string]$profileName] = @(
                        $script:Config.profiles[$profileName] |
                            ForEach-Object { Normalize-ProcessName ([string]$_) } |
                            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                            Sort-Object -Unique
                    )
                }
            } else {
                foreach ($profileProperty in $script:Config.profiles.PSObject.Properties) {
                    $profilePayload[[string]$profileProperty.Name] = @(
                        $profileProperty.Value |
                            ForEach-Object { Normalize-ProcessName ([string]$_) } |
                            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                            Sort-Object -Unique
                    )
                }
            }
        }

        $payload = [ordered]@{
            schemaVersion          = 2
            hotkey                 = [string]$script:Config.hotkey
            gracefulTimeoutSeconds = [int]$script:Config.gracefulTimeoutSeconds
            protectedProcesses     = @($script:Config.protectedProcesses)
            protectedPaths         = @($script:Config.protectedPaths | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
            lastMode               = [string]$script:Config.lastMode
            profiles               = $profilePayload
            activeProfile          = [string]$script:Config.activeProfile
            showAllWindows         = [bool]$script:Config.showAllWindows
            includeChildProcesses  = [bool]$script:Config.includeChildProcesses
            includeSuspendedUwp    = [bool]$script:Config.includeSuspendedUwp
        }
        $json = $payload | ConvertTo-Json -Depth 8
        $encoding = New-Object System.Text.UTF8Encoding($true)
        [System.IO.File]::WriteAllText($configTempPath, [string]$json, $encoding)

        if (Test-Path -LiteralPath $script:ConfigPath -PathType Leaf) {
            # Stage a complete copy of the previous config before atomically replacing either file.
            [System.IO.File]::Copy($script:ConfigPath, $backupTempPath, $false)
            if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
                [System.IO.File]::Replace($backupTempPath, $backupPath, $backupReplaceBackupPath, $true)
            } else {
                [System.IO.File]::Move($backupTempPath, $backupPath)
            }
            [System.IO.File]::Replace($configTempPath, $script:ConfigPath, $configReplaceBackupPath, $true)
        } else {
            [System.IO.File]::Move($configTempPath, $script:ConfigPath)
        }
        $script:LastConfigSaveSucceeded = $true
        if ($null -ne $script:ProfileRestoreButton) {
            $script:ProfileRestoreButton.Enabled = (Test-Path -LiteralPath $backupPath -PathType Leaf)
        }
    } catch {
        $script:ConfigSaveError = $_.Exception.Message
        if ($null -ne $script:StatusLabel) {
            $script:StatusLabel.ForeColor = [System.Drawing.Color]::Firebrick
            $script:StatusLabel.Text = "配置保存失败：$($script:ConfigSaveError)"
        }
    } finally {
        foreach ($tempPath in @($configTempPath, $configReplaceBackupPath, $backupTempPath, $backupReplaceBackupPath)) {
            if (Test-Path -LiteralPath $tempPath -PathType Leaf) {
                try { Remove-Item -LiteralPath $tempPath -Force } catch { }
            }
        }
    }
}

function Load-Config {
    param([switch]$ResetDefaults)

    if ($ResetDefaults) {
        $script:Config = [ordered]@{
            schemaVersion          = 2
            hotkey                 = 'Ctrl+Alt+X'
            gracefulTimeoutSeconds = 5
            protectedProcesses     = @()
            protectedPaths         = @()
            lastMode               = 'windows'
            profiles               = [ordered]@{}
            activeProfile          = ''
            showAllWindows         = $false
            includeChildProcesses  = $true
            includeSuspendedUwp    = $false
        }
    }
    $script:ConfigLoadError = $null

    if (-not (Test-Path -LiteralPath $script:ConfigPath)) {
        Save-Config
        return
    }
    try {
        $raw = Get-Content -LiteralPath $script:ConfigPath -Raw -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($raw)) { return }
        $data = $raw | ConvertFrom-Json
        if ($data.hotkey) { $script:Config.hotkey = [string]$data.hotkey }
        if ($null -ne $data.gracefulTimeoutSeconds) {
            $value = [int]$data.gracefulTimeoutSeconds
            if ($value -ge 1 -and $value -le 60) { $script:Config.gracefulTimeoutSeconds = $value }
        }
        if ($null -ne $data.protectedProcesses) {
            $script:Config.protectedProcesses = @($data.protectedProcesses | ForEach-Object { [string]$_ })
        }
        if ($null -ne $data.protectedPaths) {
            $script:Config.protectedPaths = @($data.protectedPaths | ForEach-Object { [string]$_ })
        }
        if ($null -ne $data.showAllWindows) {
            $script:Config.showAllWindows = [bool]$data.showAllWindows
        }
        if ($null -ne $data.includeChildProcesses) {
            $script:Config.includeChildProcesses = [bool]$data.includeChildProcesses
        }
        if ($null -ne $data.includeSuspendedUwp) {
            $script:Config.includeSuspendedUwp = [bool]$data.includeSuspendedUwp
        }
        if ($data.lastMode -eq 'all' -or $data.lastMode -eq 'windows') {
            $script:Config.lastMode = [string]$data.lastMode
        }

        $profiles = [ordered]@{}
        if ($null -ne $data.profiles) {
            if ($data.profiles -is [System.Collections.IDictionary]) {
                foreach ($profileName in $data.profiles.Keys) {
                    $cleanName = ([string]$profileName).Trim()
                    if (-not [string]::IsNullOrWhiteSpace($cleanName)) {
                        $profiles[$cleanName] = @($data.profiles[$profileName] | ForEach-Object { Normalize-ProcessName ([string]$_) } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
                    }
                }
            } else {
                foreach ($profileProperty in $data.profiles.PSObject.Properties) {
                    $cleanName = ([string]$profileProperty.Name).Trim()
                    if (-not [string]::IsNullOrWhiteSpace($cleanName)) {
                        $profiles[$cleanName] = @($profileProperty.Value | ForEach-Object { Normalize-ProcessName ([string]$_) } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
                    }
                }
            }
        }
        $script:Config.schemaVersion = 2
        $script:Config.profiles = $profiles
        $activeProfile = [string]$data.activeProfile
        if (-not [string]::IsNullOrWhiteSpace($activeProfile) -and $profiles.Contains($activeProfile)) {
            $script:Config.activeProfile = $activeProfile
        } else {
            $script:Config.activeProfile = ''
        }
    } catch {
        $script:ConfigLoadError = $_.Exception.Message
    }
}


# ---------------------------------------------------------------------------
# 通用辅助
# ---------------------------------------------------------------------------
$script:DescCache = @{}
$script:IconCache = @{}
$script:SubsystemCache = @{}
$script:GenericAppIcon = $null

function Normalize-ProcessName {
    param([string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return '' }
    $value = $Name.Trim()
    if ($value.EndsWith('.exe', [System.StringComparison]::OrdinalIgnoreCase)) {
        $value = $value.Substring(0, $value.Length - 4)
    }
    return $value.ToLowerInvariant()
}

function Get-ProcessPathSafe {
    param([System.Diagnostics.Process]$Process)
    try {
        $fresh = Get-Process -Id $Process.Id -ErrorAction Stop
        return $fresh.Path
    } catch {
        return $null
    }
}

function Get-PeSubsystem {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    if ($script:SubsystemCache.ContainsKey($Path)) { return $script:SubsystemCache[$Path] }
    $result = $null
    try {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            if ($stream.Length -lt 512) { return $null }
            $reader = New-Object System.IO.BinaryReader($stream)
            $stream.Position = 0x3C
            $peOffset = $reader.ReadInt32()
            if ($peOffset -le 0 -or ($peOffset + 94) -gt $stream.Length) { return $null }
            $stream.Position = $peOffset
            $signature = $reader.ReadUInt32()
            if ($signature -ne 0x00004550) { return $null }
            $stream.Position = $peOffset + 4 + 20 + 68
            $result = [int]$reader.ReadUInt16()
        } finally {
            $stream.Dispose()
        }
    } catch {
        $result = $null
    }
    $script:SubsystemCache[$Path] = $result
    return $result
}

function Get-AppDisplayName {
    param([string]$Path, [string]$Fallback)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $Fallback }
    if ($script:DescCache.ContainsKey($Path)) { return $script:DescCache[$Path] }
    $name = $Fallback
    try {
        $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
        if (-not [string]::IsNullOrWhiteSpace($info.FileDescription)) {
            $name = $info.FileDescription.Trim()
        } elseif (-not [string]::IsNullOrWhiteSpace($info.ProductName)) {
            $name = $info.ProductName.Trim()
        }
    } catch {
        $name = $Fallback
    }
    $script:DescCache[$Path] = $name
    return $name
}

function Get-AppIcon {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    if ($script:IconCache.ContainsKey($Path)) { return $script:IconCache[$Path] }
    $image = $null
    try {
        $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($Path)
        if ($null -ne $icon) {
            $image = $icon.ToBitmap()
            $icon.Dispose()
        }
    } catch {
        $image = $null
    }
    if ($null -eq $image) {
        try { $image = [System.Drawing.SystemIcons]::Application.ToBitmap() } catch { $image = $null }
    }
    $script:IconCache[$Path] = $image
    return $image
}

function Get-ParentMap {
    $map = @{}
    $script:ProcessPathMap = @{}
    try {
        $items = Get-CimInstance -ClassName Win32_Process -Property ProcessId, ParentProcessId, ExecutablePath -ErrorAction Stop
        foreach ($item in $items) {
            $processId = [int]$item.ProcessId
            $map[$processId] = [int]$item.ParentProcessId
            if (-not [string]::IsNullOrWhiteSpace([string]$item.ExecutablePath)) {
                $script:ProcessPathMap[$processId] = [string]$item.ExecutablePath
            }
        }
    } catch {
        # 拿不到 CIM 元数据时，退化为只保护当前进程；有窗口进程仍可通过 Get-Process 取路径。
    }
    return $map
}

function Get-AncestorSet {
    param([int]$StartId, [hashtable]$ParentMap)
    $set = New-Object 'System.Collections.Generic.HashSet[int]'
    $current = $StartId
    $guard = 0
    while ($current -gt 0 -and $guard -lt 64) {
        if ($set.Contains($current)) { break }
        [void]$set.Add($current)
        if ($ParentMap.ContainsKey($current)) {
            $current = $ParentMap[$current]
        } else {
            break
        }
        $guard++
    }
    return ,$set
}

function Parse-Hotkey {
    param([string]$Text)
    $modifiers = [uint32]0
    $virtualKey = [uint32]0
    $parts = @($Text -split '\+')
    foreach ($part in $parts) {
        $token = $part.Trim()
        if ([string]::IsNullOrWhiteSpace($token)) { continue }
        switch ($token.ToLowerInvariant()) {
            'ctrl'    { $modifiers = $modifiers -bor [uint32]0x0002; continue }
            'control' { $modifiers = $modifiers -bor [uint32]0x0002; continue }
            'alt'     { $modifiers = $modifiers -bor [uint32]0x0001; continue }
            'shift'   { $modifiers = $modifiers -bor [uint32]0x0004; continue }
            'win'     { $modifiers = $modifiers -bor [uint32]0x0008; continue }
        }
        $upper = $token.ToUpperInvariant()
        if ($upper.Length -eq 1 -and $upper -match '^[A-Z0-9]$') {
            $virtualKey = [uint32][int][char]$upper
        } elseif ($upper -match '^F([1-9]|1[0-9]|2[0-4])$') {
            $number = [int]$Matches[1]
            $virtualKey = [uint32](0x6F + $number)
        }
    }
    if ($virtualKey -eq 0 -or $modifiers -eq 0) {
        return @{ Modifiers = [uint32]0x0003; VirtualKey = [uint32][int][char]'X' }
    }
    return @{ Modifiers = $modifiers; VirtualKey = $virtualKey }
}

# ---------------------------------------------------------------------------
# 程序清单枚举
# ---------------------------------------------------------------------------
$script:HardProtected = @(
    'system', 'registry', 'memory compression', 'idle',
    'smss', 'csrss', 'wininit', 'winlogon', 'services', 'lsass', 'lsaiso',
    'svchost', 'dwm', 'fontdrvhost', 'sihost', 'taskhostw', 'ctfmon',
    'textinputhost', 'explorer', 'searchhost', 'startmenuexperiencehost',
    'shellexperiencehost', 'runtimebroker', 'securityhealthservice',
    'securityhealthsystray', 'msmpeng', 'nissrv', 'taskmgr', 'wmiprvse',
    'wudfhost', 'sogouinput', 'sogoucloud', 'qqpinyin', 'baidupinyin', 'wetype',
    'dllhost', 'unsecapp', 'lockapp', 'systemsettingsbroker', 'openconsole',
    'conhost', 'rtkauduservice64', 'nvcontainer', 'windowspackagemanagerserver',
    'widgets', 'widgetservice'
)

function Get-AppCandidates {
    param(
        [ValidateSet('windows', 'all')]
        [string]$Mode = 'windows',
        [IntPtr]$ForegroundHandle = [IntPtr]::Zero
    )

    $windows = [NativeMethods]::GetVisibleWindows()
    $windowsByPid = @{}
    foreach ($window in $windows) {
        $windowPid = [int]$window.ProcessId
        if (-not $windowsByPid.ContainsKey($windowPid)) {
            $windowsByPid[$windowPid] = New-Object System.Collections.ArrayList
        }
        [void]$windowsByPid[$windowPid].Add($window)
    }

    $processes = @(Get-Process -ErrorAction SilentlyContinue)
    $processById = @{}
    foreach ($process in $processes) {
        $processById[[int]$process.Id] = $process
    }

    $parentMap = Get-ParentMap
    $pathMap = $script:ProcessPathMap
    $ancestors = Get-AncestorSet -StartId ([int]$PID) -ParentMap $parentMap

    $currentSessionId = $null
    try {
        $currentSessionId = (Get-Process -Id $PID -ErrorAction Stop).SessionId
    } catch {
        $currentSessionId = $null
    }

    if ($ForegroundHandle -eq [IntPtr]::Zero) {
        $ForegroundHandle = [NativeMethods]::GetForegroundWindow()
    }

    $userProtected = @($script:Config.protectedProcesses | ForEach-Object { Normalize-ProcessName $_ })
    $modeProtected = @($script:HardProtected)
    if ($Mode -eq 'all') {
        $modeProtected += 'applicationframehost'
    }

    $apps = New-Object System.Collections.ArrayList

    foreach ($process in $processes) {
        $processId = [int]$process.Id
        if ($processId -le 4) { continue }
        if ($ancestors.Contains($processId)) { continue }
        if ($null -ne $currentSessionId -and $process.SessionId -ne $currentSessionId) { continue }

        $processName = $process.ProcessName
        if ([string]::IsNullOrWhiteSpace($processName)) { continue }
        $normalizedName = Normalize-ProcessName $processName
        if ($modeProtected -contains $normalizedName) { continue }

        $hasWindow = $windowsByPid.ContainsKey($processId)

        if ($Mode -eq 'windows') {
            if (-not $hasWindow) { continue }
        } else {
            if (-not $hasWindow) {
                $pathForSubsystem = $null
                if ($pathMap.ContainsKey($processId)) { $pathForSubsystem = $pathMap[$processId] }
                if ([string]::IsNullOrWhiteSpace($pathForSubsystem)) { continue }
                if ((Get-PeSubsystem -Path $pathForSubsystem) -ne 2) { continue }

                # 排除主程序拉起的辅助进程（渲染器、更新器、扩展宿主等）
                if ($parentMap.ContainsKey($processId)) {
                    $parentId = [int]$parentMap[$processId]
                    if ($parentId -gt 0 -and $parentId -ne $processId -and $processById.ContainsKey($parentId)) {
                        $parentProcess = $processById[$parentId]
                        $parentName = Normalize-ProcessName $parentProcess.ProcessName
                        $parentIsUserApp = ($parentProcess.SessionId -eq $currentSessionId) -and
                                           (-not ($modeProtected -contains $parentName)) -and
                                           (-not $ancestors.Contains($parentId))
                        if ($parentIsUserApp) { continue }
                    }
                }
            }
        }

        $appWindows = @()
        if ($hasWindow) { $appWindows = @($windowsByPid[$processId]) }

        $kind = '后台'
        if ($appWindows.Count -gt 0) {
            $kind = '窗口'
            foreach ($appWindow in $appWindows) {
                if ($appWindow.Handle -eq $ForegroundHandle) { $kind = '前台'; break }
            }
        }

        $path = $null
        if ($pathMap.ContainsKey($processId)) { $path = $pathMap[$processId] }
        if ([string]::IsNullOrWhiteSpace($path) -and $hasWindow) {
            $path = Get-ProcessPathSafe -Process $process
        }
        $displayName = Get-AppDisplayName -Path $path -Fallback $processName

        $titles = ''
        if ($appWindows.Count -gt 0) {
            $firstTitles = @($appWindows | Select-Object -First 2 | ForEach-Object { $_.Title })
            $titles = ($firstTitles -join ' / ')
            if ($appWindows.Count -gt 2) { $titles += "  (+$($appWindows.Count - 2))" }
        }

        $isProtected = ($userProtected -contains $normalizedName)

        $app = [PSCustomObject]@{
            ProcessId    = $processId
            ProcessIds   = @($processId)
            ProcessCount = 1
            Name         = $processName
            DisplayName  = $displayName
            Path         = $path
            Windows      = $appWindows
            WindowCount  = $appWindows.Count
            Kind         = $kind
            Titles       = $titles
            Protected    = $isProtected
            IsForeground = ($kind -eq '前台')
            IsTaskbarApp = (@($appWindows | Where-Object { $_.IsTaskbarWindow }).Count -gt 0)
        }
        [void]$apps.Add($app)
    }

    $applicationGroups = New-Object System.Collections.ArrayList
    $groupedApps = $apps | Group-Object -Property {
        if (-not [string]::IsNullOrWhiteSpace($_.Path)) {
            'path:' + $_.Path.Trim().ToLowerInvariant()
        } else {
            'pid:' + $_.ProcessId
        }
    }
    foreach ($group in $groupedApps) {
        $members = @($group.Group)
        $processIds = @($members | ForEach-Object { [int]$_.ProcessId } | Sort-Object -Unique)
        $groupWindows = New-Object System.Collections.ArrayList
        $titleParts = New-Object System.Collections.ArrayList
        $windowCount = 0
        $isForeground = $false
        $allProtected = $true
        $groupIsTaskbar = $false
        foreach ($member in $members) {
            $windowCount += [int]$member.WindowCount
            if ($member.IsForeground) { $isForeground = $true }
            if (-not $member.Protected) { $allProtected = $false }
            if ($member.IsTaskbarApp) { $groupIsTaskbar = $true }
            foreach ($memberWindow in @($member.Windows)) { [void]$groupWindows.Add($memberWindow) }
            if (-not [string]::IsNullOrWhiteSpace($member.Titles) -and -not ($titleParts -contains $member.Titles)) {
                [void]$titleParts.Add($member.Titles)
            }
        }
        $groupKind = '后台'
        if ($windowCount -gt 0) { $groupKind = '窗口' }
        if ($isForeground) { $groupKind = '前台' }
        $groupTitles = @($titleParts.ToArray() | Select-Object -First 3) -join ' / '
        if ($titleParts.Count -gt 3) { $groupTitles += "  (+$($titleParts.Count - 3))" }

        [void]$applicationGroups.Add([PSCustomObject]@{
            ProcessId    = [int]$processIds[0]
            ProcessIds   = $processIds
            ProcessCount = $processIds.Count
            Name         = $members[0].Name
            DisplayName  = $members[0].DisplayName
            Path         = $members[0].Path
            Windows      = @($groupWindows.ToArray())
            WindowCount  = $windowCount
            Kind         = $groupKind
            Titles       = $groupTitles
            Protected    = $allProtected
            IsForeground = $isForeground
            IsTaskbarApp = $groupIsTaskbar
        })
    }

    $sorted = $applicationGroups | Sort-Object `
        @{ Expression = { if ($_.Protected) { 1 } else { 0 } } }, `
        @{ Expression = { if ($_.IsForeground) { 0 } else { 1 } } }, `
        @{ Expression = { $_.DisplayName } }

    return @($sorted)
}

# ---------------------------------------------------------------------------
# 界面
# ---------------------------------------------------------------------------
$script:Form = $null
$script:Grid = $null
$script:StatusLabel = $null
$script:SearchBox = $null
$script:RadioWindows = $null
$script:RadioAll = $null
$script:ShowAllWindowsCheck = $null
$script:RefreshButton = $null
$script:CloseButton = $null
$script:ProtectSelectedButton = $null
$script:SelectAllButton = $null
$script:SelectNoneButton = $null
$script:ProfileCombo = $null
$script:ProfileApplyButton = $null
$script:ProfileSaveButton = $null
$script:ProfileDeleteButton = $null
$script:ProfileRestoreButton = $null
$script:ProfileTestName = $null
$script:ProfileTestDecision = $null
$script:NotifyIcon = $null
$script:HotKeyForm = $null
$script:UiReady = $false
$script:SuppressGridEvents = $false
$script:CurrentMode = 'windows'
$script:ContextRowIndex = $null
$script:ProtectItem = $null
$script:UnprotectItem = $null
$script:ReallyExit = $false
$script:Busy = $false
$script:HotkeyFailed = $false
$script:RefreshState = $null
$script:PendingRefresh = $null
$script:LastRefreshDurationMs = 0L
$script:LastRefreshRenderMs = 0L
$script:LastRefreshSucceeded = $false

function Update-SelectionStatus {
    if (-not $script:UiReady) { return }
    $count = 0
    $danger = $false
    $dangerNames = @('windowsterminal', 'cmd', 'powershell', 'pwsh', 'conhost', 'openconsole')
    foreach ($row in $script:Grid.Rows) {
        if ($null -eq $row.Tag) { continue }
        if ($row.Visible -and $row.Cells[0].Value -eq $true) {
            $count++
            if (-not $row.Tag.Protected -and ($dangerNames -contains (Normalize-ProcessName $row.Tag.Name))) {
                $danger = $true
            }
        }
    }
    if ($danger) {
        $script:StatusLabel.ForeColor = [System.Drawing.Color]::Firebrick
        $script:StatusLabel.Text = "已选 $count 个程序   |   注意：包含终端窗口，关闭会终止其中运行的任务"
        return
    }
    $modeText = '只关窗口（保留托盘）'
    $script:StatusLabel.ForeColor = [System.Drawing.Color]::DimGray
    if ($script:CurrentMode -eq 'all') {
        $modeText = '彻底退出（强制结束后台，未保存数据可能丢失）'
        $script:StatusLabel.ForeColor = [System.Drawing.Color]::FromArgb(180, 80, 0)
    }
    $script:StatusLabel.Text = "已选 $count 个程序   |   模式：$modeText"
}

function Apply-Filter {
    if ($null -eq $script:Grid) { return }
    $text = ''
    if ($null -ne $script:SearchBox) { $text = $script:SearchBox.Text.Trim() }
    $showAllWindows = $true
    if ($null -ne $script:ShowAllWindowsCheck) { $showAllWindows = [bool]$script:ShowAllWindowsCheck.Checked }

    foreach ($row in $script:Grid.Rows) {
        if ($null -eq $row.Tag) { continue }

        $isTaskbarApp = $true
        $taskbarProperty = $row.Tag.PSObject.Properties['IsTaskbarApp']
        if ($null -ne $taskbarProperty) { $isTaskbarApp = [bool]$taskbarProperty.Value }
        if (-not $showAllWindows -and -not $isTaskbarApp) {
            $row.Visible = $false
            continue
        }

        if ([string]::IsNullOrWhiteSpace($text)) {
            $row.Visible = $true
            continue
        }
        $haystack = "$($row.Tag.DisplayName) $($row.Tag.Name) $($row.Tag.Titles)"
        $row.Visible = $haystack.IndexOf($text, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
    }
    Update-SelectionStatus
}

function Update-AppListRows {
    param([object[]]$Apps)

    $previousSuppress = $script:SuppressGridEvents
    $script:SuppressGridEvents = $true
    $script:Grid.SuspendLayout()
    try {
        # Prepare detached rows first so a construction failure leaves the current list intact.
        $preparedRows = New-Object System.Collections.ArrayList
        foreach ($app in $Apps) {
            $icon = $script:GenericAppIcon
            if ($app.WindowCount -gt 0) {
                $windowIcon = Get-AppIcon -Path $app.Path
                if ($null -ne $windowIcon) { $icon = $windowIcon }
            }
            $row = New-Object System.Windows.Forms.DataGridViewRow
            $row.CreateCells($script:Grid)
            $row.Tag = $app
            $row.Cells[0].Value = $false
            $row.Cells[1].Value = $icon
            $row.Cells[2].Value = $app.DisplayName
            if ($app.ProcessCount -gt 1) {
                $row.Cells[3].Value = "$($app.Name) ($($app.ProcessCount) 个进程)"
            } else {
                $row.Cells[3].Value = "$($app.Name) (#$($app.ProcessId))"
            }
            $row.Cells[4].Value = ''
            if ($app.WindowCount -gt 0) { $row.Cells[4].Value = $app.WindowCount }
            $row.Cells[5].Value = $app.Kind
            $row.Cells[6].Value = $app.Titles
            $row.Cells[7].Value = ''

            $row.Cells[2].ToolTipText = $app.Path
            $row.Cells[3].ToolTipText = "PID: $(@($app.ProcessIds) -join ', ')`n$($app.Path)"
            $row.Cells[6].ToolTipText = $app.Titles

            if ($app.Protected) {
                $row.Cells[0].ReadOnly = $true
                $row.Cells[5].Value = '已保护'
                $row.DefaultCellStyle.ForeColor = [System.Drawing.Color]::Gray
            }
            if ($app.IsForeground -and -not $app.Protected) {
                $row.DefaultCellStyle.Font = $script:BoldFont
            }
            [void]$preparedRows.Add($row)
        }

        $script:Grid.Rows.Clear()
        if ($preparedRows.Count -gt 0) {
            $preparedArray = [System.Windows.Forms.DataGridViewRow[]]$preparedRows.ToArray()
            $script:Grid.Rows.AddRange($preparedArray)
        }
        Apply-Filter
        Update-SelectionStatus
        $script:Grid.ClearSelection()
    } finally {
        $script:Grid.ResumeLayout($true)
        $script:Grid.Invalidate()
        $script:SuppressGridEvents = $previousSuppress
    }
}

function New-CandidateScanPowerShell {
    param(
        [string]$Mode,
        [IntPtr]$ForegroundHandle
    )

    $functionNames = @(
        'Normalize-ProcessName',
        'Get-ProcessPathSafe',
        'Get-PeSubsystem',
        'Get-AppDisplayName',
        'Get-ParentMap',
        'Get-AncestorSet',
        'Get-AppCandidates'
    )
    $parts = New-Object System.Collections.ArrayList
    [void]$parts.Add('param($WorkerMode, $WorkerForegroundHandle, $WorkerConfig, $WorkerHardProtected)')
    foreach ($functionName in $functionNames) {
        $functionInfo = Get-Command -Name $functionName -CommandType Function -ErrorAction Stop
        [void]$parts.Add("function $functionName {`r`n$($functionInfo.Definition)`r`n}")
    }
    [void]$parts.Add('$script:Config = $WorkerConfig')
    [void]$parts.Add('$script:HardProtected = $WorkerHardProtected')
    [void]$parts.Add('$script:DescCache = @{}')
    [void]$parts.Add('$script:SubsystemCache = @{}')
    [void]$parts.Add('$script:ProcessPathMap = @{}')
    [void]$parts.Add('Get-AppCandidates -Mode $WorkerMode -ForegroundHandle $WorkerForegroundHandle')

    $worker = [System.Management.Automation.PowerShell]::Create()
    try {
        [void]$worker.AddScript(($parts -join [Environment]::NewLine))
        [void]$worker.AddArgument($Mode)
        [void]$worker.AddArgument($ForegroundHandle)
        [void]$worker.AddArgument($script:Config)
        [void]$worker.AddArgument($script:HardProtected)
        return $worker
    } catch {
        $worker.Dispose()
        throw
    }
}

function Lock-RefreshUi {
    $lockedCells = New-Object System.Collections.ArrayList
    foreach ($row in $script:Grid.Rows) {
        if ($null -eq $row.Tag) { continue }
        [void]$lockedCells.Add([PSCustomObject]@{ Cell = $row.Cells[0]; WasReadOnly = [bool]$row.Cells[0].ReadOnly })
        $row.Cells[0].ReadOnly = $true
    }
    $script:CloseButton.Enabled = $false
    $script:ProtectSelectedButton.Enabled = $false
    $script:SelectAllButton.Enabled = $false
    $script:SelectNoneButton.Enabled = $false
    $script:RefreshButton.Enabled = $false
    if ($null -ne $script:Grid.ContextMenuStrip) { $script:Grid.ContextMenuStrip.Enabled = $false }
    return ,$lockedCells
}

function Restore-RefreshUi {
    param([System.Collections.ArrayList]$LockedCells)
    if ($null -ne $LockedCells) {
        foreach ($entry in $LockedCells) {
            if ($null -ne $entry.Cell.DataGridView) { $entry.Cell.ReadOnly = $entry.WasReadOnly }
        }
    }
    $script:CloseButton.Enabled = $true
    $script:ProtectSelectedButton.Enabled = $true
    $script:SelectAllButton.Enabled = $true
    $script:SelectNoneButton.Enabled = $true
    $script:RefreshButton.Enabled = $true
    if ($null -ne $script:Grid.ContextMenuStrip) { $script:Grid.ContextMenuStrip.Enabled = $true }
}

function Start-AppListRefresh {
    param(
        [string]$Mode,
        [IntPtr]$ForegroundHandle = [IntPtr]::Zero
    )

    $lockedCells = Lock-RefreshUi
    $script:LastRefreshSucceeded = $false
    $worker = $null
    $timer = $null
    try {
        $worker = New-CandidateScanPowerShell -Mode $Mode -ForegroundHandle $ForegroundHandle
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $asyncResult = $worker.BeginInvoke()

        $timer = New-Object System.Windows.Forms.Timer
        $timer.Interval = 75
        $timer.Add_Tick({ Complete-AppListRefresh })
        $script:RefreshState = [PSCustomObject]@{
            Mode            = $Mode
            Worker          = $worker
            AsyncResult     = $asyncResult
            Timer           = $timer
            Watch           = $watch
            LockedCells     = $lockedCells
        }
        $timer.Start()
        $modeText = '只关窗口'
        if ($Mode -eq 'all') { $modeText = '彻底退出（含后台）' }
        $script:StatusLabel.Text = "正在扫描 $modeText 清单；旧清单保留，暂不可操作..."
    } catch {
        if ($null -ne $timer) { try { $timer.Dispose() } catch { } }
        if ($null -ne $worker) { try { $worker.Dispose() } catch { } }
        Restore-RefreshUi -LockedCells $lockedCells
        $script:StatusLabel.Text = "扫描启动失败：$($_.Exception.Message)"
    }
}

function Complete-AppListRefresh {
    $state = $script:RefreshState
    if ($null -eq $state -or -not $state.AsyncResult.IsCompleted) { return }

    try { $state.Timer.Stop(); $state.Timer.Dispose() } catch { }
    $script:RefreshState = $null
    $script:LastRefreshDurationMs = $state.Watch.ElapsedMilliseconds

    $apps = @()
    $errorText = $null
    try {
        $apps = @($state.Worker.EndInvoke($state.AsyncResult))
        if ($state.Worker.Streams.Error.Count -gt 0) {
            $errorText = [string]$state.Worker.Streams.Error[0]
        }
    } catch {
        $errorText = $_.Exception.Message
    } finally {
        try { $state.Worker.Dispose() } catch { }
    }

    $pending = $script:PendingRefresh
    $script:PendingRefresh = $null
    if ($null -eq $pending -and $state.Mode -ne $script:CurrentMode) {
        $pending = [PSCustomObject]@{ Mode = $script:CurrentMode; ForegroundHandle = [IntPtr]::Zero }
    }

    if ($null -ne $pending) {
        Restore-RefreshUi -LockedCells $state.LockedCells
        Start-AppListRefresh -Mode $pending.Mode -ForegroundHandle $pending.ForegroundHandle
        return
    }

    if (-not [string]::IsNullOrWhiteSpace($errorText)) {
        Restore-RefreshUi -LockedCells $state.LockedCells
        $script:StatusLabel.Text = "扫描失败，保留旧清单：$errorText"
        return
    }

    try {
        $renderWatch = [System.Diagnostics.Stopwatch]::StartNew()
        Update-AppListRows -Apps $apps
        $renderWatch.Stop()
        $script:LastRefreshRenderMs = $renderWatch.ElapsedMilliseconds
        Restore-RefreshUi -LockedCells $state.LockedCells
        $script:LastRefreshSucceeded = $true
        $visibleCount = @($script:Grid.Rows | Where-Object { $_.Visible }).Count
        $script:StatusLabel.Text = "扫描完成：显示 $visibleCount 个程序（候选 $($apps.Count) 个），用时 $($script:LastRefreshDurationMs)ms"
    } catch {
        Restore-RefreshUi -LockedCells $state.LockedCells
        $script:StatusLabel.Text = "更新清单失败，保留当前清单：$($_.Exception.Message)"
    }
}

function Refresh-AppList {
    param(
        [string]$Mode,
        [IntPtr]$ForegroundHandle = [IntPtr]::Zero,
        [switch]$Force
    )

    if (-not $script:UiReady -or $script:Busy) { return }
    if ($null -ne $script:RefreshState) {
        if ($Force -or $Mode -ne $script:RefreshState.Mode) {
            $script:PendingRefresh = [PSCustomObject]@{ Mode = $Mode; ForegroundHandle = $ForegroundHandle }
        } elseif ($script:PendingRefresh) {
            $script:PendingRefresh = $null
        }
        return
    }
    Start-AppListRefresh -Mode $Mode -ForegroundHandle $ForegroundHandle
}

function Wait-ForAppRefresh {
    param([int]$TimeoutSeconds = 30)
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($null -ne $script:RefreshState -and $watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 25
    }
    if ($null -ne $script:RefreshState) {
        $stateMode = [string]$script:RefreshState.Mode
        $completed = [bool]$script:RefreshState.AsyncResult.IsCompleted
        $pendingMode = ''
        if ($script:PendingRefresh) { $pendingMode = [string]$script:PendingRefresh.Mode }
        throw "候选程序扫描超过 $TimeoutSeconds 秒（进行中=$stateMode, 已完成=$completed, 排队=$pendingMode, 当前模式=$($script:CurrentMode)）"
    }
}


function Set-Mode {
    param([ValidateSet('windows', 'all')][string]$Mode)
    if ($script:CurrentMode -eq $Mode -and $script:UiReady) { return }
    $script:CurrentMode = $Mode
    $script:Config.lastMode = $Mode
    Save-Config
    Refresh-AppList -Mode $Mode
}

function Show-Picker {
    if ($null -eq $script:Form) { return }
    if ($script:Busy) { return }
    $foreground = [NativeMethods]::GetForegroundWindow()
    if ($script:Form.Visible) {
        $script:Form.Activate()
        return
    }
    $script:Form.Show()
    [void][NativeMethods]::SetForegroundWindow($script:Form.Handle)
    $script:Form.Activate()
    Refresh-AppList -Mode $script:CurrentMode -ForegroundHandle $foreground
}

function Get-AppProcessIds {
    param($App)
    $ids = @()
    if ($null -ne $App.ProcessIds) {
        $ids = @($App.ProcessIds | ForEach-Object { [int]$_ } | Sort-Object -Unique)
    }
    if ($ids.Count -eq 0 -and $null -ne $App.ProcessId) { $ids = @([int]$App.ProcessId) }
    return $ids
}

function Get-LiveProcessForTarget {
    param($Target)
    $live = Get-Process -Id $Target.ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $live) { return $null }
    if ((Normalize-ProcessName $live.ProcessName) -ne (Normalize-ProcessName $Target.ProcessName)) { return $null }

    if (-not [string]::IsNullOrWhiteSpace($Target.Path)) {
        $livePath = Get-ProcessPathSafe -Process $live
        if (-not [string]::IsNullOrWhiteSpace($livePath) -and
            -not [string]::Equals($livePath, $Target.Path, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $null
        }
    }
    return $live
}

function New-CloseTargets {
    param([System.Collections.ArrayList]$Selected)

    $visibleWindows = [NativeMethods]::GetVisibleWindows()
    $windowsByPid = @{}
    foreach ($window in $visibleWindows) {
        $windowProcessId = [int]$window.ProcessId
        if (-not $windowsByPid.ContainsKey($windowProcessId)) { $windowsByPid[$windowProcessId] = New-Object System.Collections.ArrayList }
        [void]$windowsByPid[$windowProcessId].Add($window)
    }

    $targets = New-Object System.Collections.ArrayList
    foreach ($item in $Selected) {
        $item | Add-Member -MemberType NoteProperty -Name Outcomes -Value (New-Object System.Collections.ArrayList) -Force
        foreach ($processId in (Get-AppProcessIds -App $item.App)) {
            $outcome = [PSCustomObject]@{
                ProcessId = $processId
                Name      = $item.App.Name
                Status    = ''
                Target    = $null
            }
            $live = Get-Process -Id $processId -ErrorAction SilentlyContinue
            if ($null -eq $live) {
                $outcome.Status = '已退出'
                [void]$item.Outcomes.Add($outcome)
                continue
            }
            if ((Normalize-ProcessName $live.ProcessName) -ne (Normalize-ProcessName $item.App.Name)) {
                $outcome.Status = 'PID 已变化，已跳过'
                [void]$item.Outcomes.Add($outcome)
                continue
            }

            $livePath = Get-ProcessPathSafe -Process $live
            if (-not [string]::IsNullOrWhiteSpace($item.App.Path) -and
                -not [string]::IsNullOrWhiteSpace($livePath) -and
                -not [string]::Equals($livePath, $item.App.Path, [System.StringComparison]::OrdinalIgnoreCase)) {
                $outcome.Status = '程序路径已变化，已跳过'
                [void]$item.Outcomes.Add($outcome)
                continue
            }

            $targetWindows = @()
            if ($windowsByPid.ContainsKey($processId)) { $targetWindows = @($windowsByPid[$processId].ToArray()) }
            $target = [PSCustomObject]@{
                GroupItem       = $item
                Outcome         = $outcome
                ProcessId       = $processId
                ProcessName     = $live.ProcessName
                Path            = $livePath
                Windows         = $targetWindows
                HasVisibleWindow = ($targetWindows.Count -gt 0)
                WindowTitles    = (@($targetWindows | ForEach-Object { $_.Title } | Select-Object -First 2) -join ' / ')
            }
            $outcome.Target = $target
            [void]$item.Outcomes.Add($outcome)
            [void]$targets.Add($target)
        }
    }
    return ,$targets
}

function New-ClosePreviewText {
    param(
        [System.Collections.ArrayList]$Selected,
        [System.Collections.ArrayList]$Targets,
        [int]$TimeoutSeconds
    )

    $lines = New-Object 'System.Collections.Generic.List[string]'
    $visibleCount = @($Targets | Where-Object { $_.HasVisibleWindow }).Count
    $backgroundCount = $Targets.Count - $visibleCount
    [void]$lines.Add("选中 $($Selected.Count) 个应用组，共 $($Targets.Count) 个进程。")
    [void]$lines.Add("有窗口：$visibleCount 个（先发 WM_CLOSE，最长等 $TimeoutSeconds 秒，未退出再强杀）；无可见窗口：$backgroundCount 个（确认后直接强杀）。")
    [void]$lines.Add('')

    foreach ($item in $Selected) {
        [void]$lines.Add("$($item.App.DisplayName)  [$($item.App.ProcessCount) 个进程]")
        foreach ($outcome in $item.Outcomes) {
            if ($null -eq $outcome.Target) {
                [void]$lines.Add("  PID $($outcome.ProcessId): $($outcome.Status)")
                continue
            }
            $target = $outcome.Target
            $action = '无可见窗口：确认后立即强制结束'
            if ($target.HasVisibleWindow) { $action = "有窗口：先正常关闭，$TimeoutSeconds 秒后仍运行则强制结束" }
            $detail = "  $($target.ProcessName)  PID $($target.ProcessId) — $action"
            if (-not [string]::IsNullOrWhiteSpace($target.WindowTitles)) { $detail += "`r`n      窗口：$($target.WindowTitles)" }
            [void]$lines.Add($detail)
        }
    }
    return ($lines -join [Environment]::NewLine)
}

function Confirm-ForceCloseTargets {
    param(
        [System.Collections.ArrayList]$Selected,
        [System.Collections.ArrayList]$Targets,
        [int]$TimeoutSeconds
    )

    $previewText = New-ClosePreviewText -Selected $Selected -Targets $Targets -TimeoutSeconds $TimeoutSeconds
    if ($null -ne $script:SafetyTestDecision) {
        $script:SafetyTestPreviewText = $previewText
        return ($script:SafetyTestDecision -eq 'Yes')
    }

    $dialog = New-Object System.Windows.Forms.Form
    $dialog.Text = '确认彻底退出'
    $dialog.StartPosition = 'CenterParent'
    $dialog.FormBorderStyle = 'FixedDialog'
    $dialog.MaximizeBox = $false
    $dialog.MinimizeBox = $false
    $dialog.ShowInTaskbar = $false
    $dialog.Size = New-Object System.Drawing.Size(760, 470)
    $dialog.Font = $script:Form.Font

    $layout = New-Object System.Windows.Forms.TableLayoutPanel
    $layout.Dock = 'Fill'
    $layout.ColumnCount = 1
    $layout.RowCount = 3
    $layout.Padding = New-Object System.Windows.Forms.Padding(10)
    [void]$layout.RowStyles.Add((New-Object System.Windows.Forms.RowStyle -ArgumentList @([System.Windows.Forms.SizeType]::Absolute, [single]54)))
    [void]$layout.RowStyles.Add((New-Object System.Windows.Forms.RowStyle -ArgumentList @([System.Windows.Forms.SizeType]::Percent, [single]100)))
    [void]$layout.RowStyles.Add((New-Object System.Windows.Forms.RowStyle -ArgumentList @([System.Windows.Forms.SizeType]::Absolute, [single]44)))
    $dialog.Controls.Add($layout)

    $warning = New-Object System.Windows.Forms.Label
    $warning.Dock = 'Fill'
    $warning.TextAlign = 'MiddleLeft'
    $warning.ForeColor = [System.Drawing.Color]::Firebrick
    $warning.Text = '确认后将按下方清单关闭程序；未保存内容可能丢失。点【取消】不会发送任何关闭请求。'
    [void]$layout.Controls.Add($warning, 0, 0)

    $details = New-Object System.Windows.Forms.TextBox
    $details.Dock = 'Fill'
    $details.Multiline = $true
    $details.ReadOnly = $true
    $details.ScrollBars = 'Vertical'
    $details.WordWrap = $true
    $details.Text = $previewText
    [void]$layout.Controls.Add($details, 0, 1)

    $buttons = New-Object System.Windows.Forms.FlowLayoutPanel
    $buttons.Dock = 'Fill'
    $buttons.FlowDirection = 'RightToLeft'
    $buttons.WrapContents = $false
    $buttons.Padding = New-Object System.Windows.Forms.Padding(0, 6, 0, 0)
    [void]$layout.Controls.Add($buttons, 0, 2)

    $cancelButton = New-Object System.Windows.Forms.Button
    $cancelButton.Text = '取消'
    $cancelButton.AutoSize = $true
    $cancelButton.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    [void]$buttons.Controls.Add($cancelButton)

    $confirmButton = New-Object System.Windows.Forms.Button
    $confirmButton.Text = '确认彻底退出'
    $confirmButton.AutoSize = $true
    $confirmButton.BackColor = [System.Drawing.Color]::Firebrick
    $confirmButton.ForeColor = [System.Drawing.Color]::White
    $confirmButton.FlatStyle = 'Flat'
    $confirmButton.FlatAppearance.BorderSize = 0
    $confirmButton.DialogResult = [System.Windows.Forms.DialogResult]::Yes
    [void]$buttons.Controls.Add($confirmButton)

    $dialog.CancelButton = $cancelButton
    $dialog.AcceptButton = $confirmButton
    try {
        return ($dialog.ShowDialog($script:Form) -eq [System.Windows.Forms.DialogResult]::Yes)
    } finally {
        $dialog.Dispose()
    }
}

function Invoke-CloseSelected {
    if (-not $script:UiReady -or $script:Busy -or $null -ne $script:RefreshState) { return }

    $selected = New-Object System.Collections.ArrayList
    foreach ($row in $script:Grid.Rows) {
        if ($null -eq $row.Tag) { continue }
        if ($row.Visible -and $row.Cells[0].Value -eq $true -and -not $row.Tag.Protected) {
            [void]$selected.Add([PSCustomObject]@{ Row = $row; App = $row.Tag })
        }
    }
    if ($selected.Count -eq 0) {
        $script:StatusLabel.Text = '请先勾选要关闭的程序'
        return
    }

    $mode = $script:CurrentMode
    $timeout = [int]$script:Config.gracefulTimeoutSeconds
    if ($timeout -lt 1) { $timeout = 5 }

    $targets = New-CloseTargets -Selected $selected
    if ($targets.Count -eq 0) {
        foreach ($item in $selected) {
            $statuses = @($item.Outcomes | ForEach-Object { $_.Status } | Where-Object { $_ })
            if ($statuses.Count -gt 0) { $item.Row.Cells[7].Value = $statuses -join '; ' }
        }
        $script:StatusLabel.Text = '选中的进程已退出或身份已变化；未发送关闭请求'
        return
    }

    if ($mode -eq 'all') {
        if (-not (Confirm-ForceCloseTargets -Selected $selected -Targets $targets -TimeoutSeconds $timeout)) {
            $script:StatusLabel.Text = '已取消，没有发送关闭请求'
            return
        }
    }

    $script:Busy = $true
    $script:CloseButton.Enabled = $false
    $script:ProtectSelectedButton.Enabled = $false
    $script:RefreshButton.Enabled = $false
    $script:SelectAllButton.Enabled = $false
    $script:SelectNoneButton.Enabled = $false
    $script:SearchBox.Enabled = $false
    $script:RadioWindows.Enabled = $false
    $script:RadioAll.Enabled = $false
    if ($null -ne $script:Grid.ContextMenuStrip) { $script:Grid.ContextMenuStrip.Enabled = $false }
    $script:StatusLabel.Text = '正在关闭...'

    try {
        foreach ($target in $targets) {
            if ($target.Outcome.Status) { continue }
            $live = Get-LiveProcessForTarget -Target $target
            if ($null -eq $live) {
                $target.Outcome.Status = '进程身份已变化，已跳过'
                continue
            }
            if (-not $target.HasVisibleWindow) {
                if ($mode -eq 'windows') { $target.Outcome.Status = '窗口已关闭（进程保留）' }
                continue
            }

            foreach ($window in $target.Windows) {
                if (-not [NativeMethods]::IsWindow($window.Handle)) { continue }
                [uint32]$ownerPid = 0
                [void][NativeMethods]::GetWindowThreadProcessId($window.Handle, [ref]$ownerPid)
                if ($ownerPid -eq [uint32]$target.ProcessId) {
                    [void][NativeMethods]::PostMessage($window.Handle, [uint32]0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
                }
            }
        }

        $deadline = [DateTime]::UtcNow.AddSeconds($timeout)
        while ([DateTime]::UtcNow -lt $deadline) {
            $liveWindows = [NativeMethods]::GetVisibleWindows()
            $livePids = @{}
            foreach ($liveWindow in $liveWindows) { $livePids[[int]$liveWindow.ProcessId] = $true }

            $pending = 0
            foreach ($target in $targets) {
                if ($target.Outcome.Status -or -not $target.HasVisibleWindow) { continue }
                if ($mode -eq 'windows') {
                    if ($livePids.ContainsKey($target.ProcessId)) { $pending++ }
                } else {
                    $alive = Get-Process -Id $target.ProcessId -ErrorAction SilentlyContinue
                    if ($null -ne $alive) { $pending++ }
                }
            }
            if ($pending -eq 0) { break }
            $remaining = [int][Math]::Ceiling(($deadline - [DateTime]::UtcNow).TotalSeconds)
            if ($remaining -lt 0) { $remaining = 0 }
            $script:StatusLabel.Text = "正在关闭... 剩余 $pending 个进程（最长等待 ${remaining}s）"
            [System.Windows.Forms.Application]::DoEvents()
            Start-Sleep -Milliseconds 200
        }

        if ($mode -eq 'all') {
            foreach ($target in $targets) {
                if ($target.Outcome.Status) { continue }
                $live = Get-LiveProcessForTarget -Target $target
                if ($null -eq $live) {
                    $stillRunning = Get-Process -Id $target.ProcessId -ErrorAction SilentlyContinue
                    if ($null -eq $stillRunning) {
                        $target.Outcome.Status = '已关闭'
                    } else {
                        $target.Outcome.Status = '进程身份已变化，已跳过'
                    }
                    continue
                }
                try {
                    $live.Kill()
                    if ($live.WaitForExit(2000)) {
                        $target.Outcome.Status = '已强制退出'
                    } else {
                        $target.Outcome.Status = '强杀未完成'
                    }
                } catch {
                    $stillThere = Get-Process -Id $target.ProcessId -ErrorAction SilentlyContinue
                    if ($null -eq $stillThere) {
                        $target.Outcome.Status = '已关闭'
                    } else {
                        $target.Outcome.Status = '失败/权限不足'
                    }
                }
            }
        } else {
            $liveWindows = [NativeMethods]::GetVisibleWindows()
            $livePids = @{}
            foreach ($liveWindow in $liveWindows) { $livePids[[int]$liveWindow.ProcessId] = $true }
            foreach ($target in $targets) {
                if ($target.Outcome.Status) { continue }
                $live = Get-LiveProcessForTarget -Target $target
                if ($null -eq $live) {
                    $stillRunning = Get-Process -Id $target.ProcessId -ErrorAction SilentlyContinue
                    if ($null -eq $stillRunning) {
                        $target.Outcome.Status = '已关闭'
                    } else {
                        $target.Outcome.Status = '进程身份已变化，已跳过'
                    }
                } elseif ($livePids.ContainsKey($target.ProcessId)) {
                    $target.Outcome.Status = '窗口未响应'
                } else {
                    $target.Outcome.Status = '窗口已关（进程保留）'
                }
            }
        }

        $success = 0
        $failed = 0
        $successfulStatuses = @('已关闭', '已强制退出', '窗口已关闭（进程保留）', '窗口已关（进程保留）')
        foreach ($item in $selected) {
            $statuses = @($item.Outcomes | ForEach-Object { $_.Status } | Where-Object { $_ })
            $groupSuccess = @($statuses | Where-Object { $successfulStatuses -contains $_ }).Count
            $groupFailed = $statuses.Count - $groupSuccess
            if ($statuses.Count -eq 0) {
                $item.Row.Cells[7].Value = '没有可执行目标'
                $failed++
            } elseif ($groupFailed -eq 0) {
                if ($statuses.Count -eq 1) { $item.Row.Cells[7].Value = $statuses[0] }
                else { $item.Row.Cells[7].Value = "$groupSuccess 个进程已处理" }
                $success++
            } else {
                $item.Row.Cells[7].Value = "$groupSuccess/$($statuses.Count) 个进程已处理"
                $failed++
            }
        }
        if ($failed -eq 0) {
            $script:StatusLabel.Text = "完成：成功处理 $success 个应用组"
        } else {
            $script:StatusLabel.Text = "完成：成功 $success 个，未完成 $failed 个应用组（详见结果列）"
        }
    } catch {
        $script:StatusLabel.Text = "执行出错：$($_.Exception.Message)"
    } finally {
        $script:Busy = $false
        $script:CloseButton.Enabled = $true
        $script:ProtectSelectedButton.Enabled = $true
        $script:RefreshButton.Enabled = $true
        $script:SelectAllButton.Enabled = $true
        $script:SelectNoneButton.Enabled = $true
        $script:SearchBox.Enabled = $true
        $script:RadioWindows.Enabled = $true
        $script:RadioAll.Enabled = $true
        if ($null -ne $script:Grid.ContextMenuStrip) { $script:Grid.ContextMenuStrip.Enabled = $true }
    }
}

function Select-AllVisible {
    if (-not $script:UiReady -or $script:Busy -or $null -ne $script:RefreshState) { return }
    $script:SuppressGridEvents = $true
    try {
        foreach ($row in $script:Grid.Rows) {
            if ($null -eq $row.Tag) { continue }
            if ($row.Visible -and -not $row.Tag.Protected) {
                $row.Cells[0].Value = $true
            }
        }
    } finally {
        $script:SuppressGridEvents = $false
    }
    Update-SelectionStatus
}

function Select-None {
    if (-not $script:UiReady -or $script:Busy -or $null -ne $script:RefreshState) { return }
    foreach ($row in $script:Grid.Rows) {
        if ($null -eq $row.Tag) { continue }
        if (-not $row.Tag.Protected) { $row.Cells[0].Value = $false }
    }
    Update-SelectionStatus
}

function Set-ProcessProtectionState {
    param(
        [string[]]$ProcessNames,
        [bool]$Protect
    )

    $names = @($ProcessNames | ForEach-Object { Normalize-ProcessName $_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
    if ($names.Count -eq 0) { return 0 }
    $previousProtected = @($script:Config.protectedProcesses)
    $previousActiveProfile = [string]$script:Config.activeProfile

    if ($Protect) {
        $existing = @($script:Config.protectedProcesses | ForEach-Object { Normalize-ProcessName $_ })
        foreach ($name in $names) {
            if (-not ($existing -contains $name)) {
                $script:Config.protectedProcesses = @($script:Config.protectedProcesses) + $name
                $existing += $name
            }
        }
    } else {
        $script:Config.protectedProcesses = @(
            $script:Config.protectedProcesses | Where-Object {
                $names -notcontains (Normalize-ProcessName $_)
            }
        )
    }
    $script:Config.activeProfile = ''
    Save-Config
    if (-not $script:LastConfigSaveSucceeded) {
        $script:Config.protectedProcesses = $previousProtected
        $script:Config.activeProfile = $previousActiveProfile
        return 0
    }

    $previousSuppress = $script:SuppressGridEvents
    $script:SuppressGridEvents = $true
    try {
        foreach ($item in $script:Grid.Rows) {
            if ($null -eq $item.Tag -or $names -notcontains (Normalize-ProcessName $item.Tag.Name)) { continue }
            $item.Tag.Protected = $Protect
            if ($Protect) {
                $item.Cells[0].Value = $false
                $item.Cells[0].ReadOnly = $true
                $item.Cells[5].Value = '已保护'
                $item.DefaultCellStyle.ForeColor = [System.Drawing.Color]::Gray
                $item.DefaultCellStyle.Font = $script:Form.Font
            } else {
                $item.Cells[0].ReadOnly = $false
                $item.Cells[5].Value = $item.Tag.Kind
                $item.DefaultCellStyle.ForeColor = [System.Drawing.Color]::Empty
                if ($item.Tag.IsForeground) {
                    $item.DefaultCellStyle.Font = $script:BoldFont
                } else {
                    $item.DefaultCellStyle.Font = $script:Form.Font
                }
            }
        }
    } finally {
        $script:SuppressGridEvents = $previousSuppress
    }
    Update-SelectionStatus
    return $names.Count
}

function Protect-Selected {
    if (-not $script:UiReady -or $script:Busy -or $null -ne $script:RefreshState) { return }
    $selectedNames = New-Object System.Collections.ArrayList
    foreach ($row in $script:Grid.Rows) {
        if ($null -eq $row.Tag -or -not $row.Visible -or $row.Tag.Protected) { continue }
        if ($row.Cells[0].Value -ne $true) { continue }
        $name = Normalize-ProcessName $row.Tag.Name
        if (-not [string]::IsNullOrWhiteSpace($name) -and -not ($selectedNames -contains $name)) {
            [void]$selectedNames.Add($name)
        }
    }
    if ($selectedNames.Count -eq 0) {
        $script:StatusLabel.Text = '请先勾选要加入保护名单的程序'
        return
    }

    $count = Set-ProcessProtectionState -ProcessNames @($selectedNames.ToArray()) -Protect $true
    if (-not $script:LastConfigSaveSucceeded) {
        $script:StatusLabel.ForeColor = [System.Drawing.Color]::Firebrick
        $script:StatusLabel.Text = "保护名单未保存，已撤销更改：$($script:ConfigSaveError)"
        return
    }
    $script:StatusLabel.ForeColor = [System.Drawing.Color]::DimGray
    $script:StatusLabel.Text = "已将 $count 个程序名称加入保护名单（同名实例一起保护）"
}

function Toggle-ContextProtection {
    param([bool]$Protect)
    if ($null -ne $script:RefreshState -or $script:Busy) { return }
    if ($null -eq $script:ContextRowIndex) { return }
    if ($script:ContextRowIndex -lt 0 -or $script:ContextRowIndex -ge $script:Grid.Rows.Count) { return }
    $row = $script:Grid.Rows[$script:ContextRowIndex]
    if ($null -eq $row.Tag) { return }

    $name = Normalize-ProcessName $row.Tag.Name
    if ([string]::IsNullOrWhiteSpace($name)) { return }
    [void](Set-ProcessProtectionState -ProcessNames @($name) -Protect $Protect)
}

function Sync-VisibleProtectionState {
    if ($null -eq $script:Grid -or $script:Busy -or $null -ne $script:RefreshState) { return }
    $protectedNames = @($script:Config.protectedProcesses | ForEach-Object { Normalize-ProcessName ([string]$_) })
    $previousSuppress = $script:SuppressGridEvents
    $script:SuppressGridEvents = $true
    try {
        foreach ($row in $script:Grid.Rows) {
            if ($null -eq $row.Tag) { continue }
            $isProtected = $protectedNames -contains (Normalize-ProcessName $row.Tag.Name)
            $row.Tag.Protected = $isProtected
            if ($isProtected) {
                $row.Cells[0].Value = $false
                $row.Cells[0].ReadOnly = $true
                $row.Cells[5].Value = '已保护'
                $row.DefaultCellStyle.ForeColor = [System.Drawing.Color]::Gray
                $row.DefaultCellStyle.Font = $script:Form.Font
            } else {
                $row.Cells[0].ReadOnly = $false
                $row.Cells[5].Value = $row.Tag.Kind
                $row.DefaultCellStyle.ForeColor = [System.Drawing.Color]::Empty
                if ($row.Tag.IsForeground) { $row.DefaultCellStyle.Font = $script:BoldFont }
                else { $row.DefaultCellStyle.Font = $script:Form.Font }
            }
        }
    } finally {
        $script:SuppressGridEvents = $previousSuppress
    }
    Update-SelectionStatus
}

function Refresh-ProtectionProfileList {
    if ($null -eq $script:ProfileCombo) { return }
    $selected = [string]$script:Config.activeProfile
    $script:ProfileCombo.BeginUpdate()
    try {
        $script:ProfileCombo.Items.Clear()
        foreach ($profileName in @($script:Config.profiles.Keys | Sort-Object)) {
            [void]$script:ProfileCombo.Items.Add([string]$profileName)
        }
        if (-not [string]::IsNullOrWhiteSpace($selected) -and $script:ProfileCombo.Items.Contains($selected)) {
            $script:ProfileCombo.SelectedItem = $selected
        } elseif ($script:ProfileCombo.Items.Count -gt 0) {
            $script:ProfileCombo.SelectedIndex = 0
        }
    } finally {
        $script:ProfileCombo.EndUpdate()
    }
    $script:ProfileDeleteButton.Enabled = ($script:ProfileCombo.Items.Count -gt 0)
    $script:ProfileApplyButton.Enabled = ($script:ProfileCombo.Items.Count -gt 0)
}

function Confirm-ProfileAction {
    param(
        [string]$Message,
        [string]$Title,
        [System.Windows.Forms.MessageBoxIcon]$Icon = [System.Windows.Forms.MessageBoxIcon]::Question
    )
    if ($null -ne $script:ProfileTestDecision) {
        $decision = [string]$script:ProfileTestDecision
        $script:ProfileTestDecision = $null
        return ($decision -eq 'Yes')
    }
    $answer = [System.Windows.Forms.MessageBox]::Show(
        $Message,
        $Title,
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        $Icon,
        [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
    return ($answer -eq [System.Windows.Forms.DialogResult]::Yes)
}

function Prompt-ProtectionProfileName {
    param([string]$InitialValue = '')
    if ($null -ne $script:ProfileTestName) {
        $value = [string]$script:ProfileTestName
        $script:ProfileTestName = $null
        return $value.Trim()
    }
    $dialog = New-Object System.Windows.Forms.Form
    $dialog.Text = '保存保护预设'
    $dialog.StartPosition = 'CenterParent'
    $dialog.FormBorderStyle = 'FixedDialog'
    $dialog.MaximizeBox = $false
    $dialog.MinimizeBox = $false
    $dialog.ShowInTaskbar = $false
    $dialog.Size = New-Object System.Drawing.Size(420, 150)
    $dialog.Font = $script:Form.Font

    $label = New-Object System.Windows.Forms.Label
    $label.Text = '预设名称（最多 40 个字符）：'
    $label.AutoSize = $true
    $label.Location = New-Object System.Drawing.Point(12, 14)
    $dialog.Controls.Add($label)

    $textBox = New-Object System.Windows.Forms.TextBox
    $textBox.Text = $InitialValue
    $textBox.Location = New-Object System.Drawing.Point(14, 40)
    $textBox.Width = 375
    $dialog.Controls.Add($textBox)

    $saveButton = New-Object System.Windows.Forms.Button
    $saveButton.Text = '保存'
    $saveButton.Location = New-Object System.Drawing.Point(222, 78)
    $saveButton.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $dialog.Controls.Add($saveButton)

    $cancelButton = New-Object System.Windows.Forms.Button
    $cancelButton.Text = '取消'
    $cancelButton.Location = New-Object System.Drawing.Point(312, 78)
    $cancelButton.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $dialog.Controls.Add($cancelButton)
    $dialog.AcceptButton = $saveButton
    $dialog.CancelButton = $cancelButton
    $dialog.Add_Shown({ $textBox.Select(); $textBox.Focus() })

    try {
        if ($dialog.ShowDialog($script:Form) -eq [System.Windows.Forms.DialogResult]::OK) {
            $value = $textBox.Text.Trim()
            if ($value.Length -gt 40) { $value = $value.Substring(0, 40) }
            return $value
        }
        return $null
    } finally {
        $dialog.Dispose()
    }
}

function Save-ProtectionProfile {
    if ($script:Busy -or $null -ne $script:RefreshState) { return }
    $name = Prompt-ProtectionProfileName
    if ([string]::IsNullOrWhiteSpace($name)) { return }

    $existingName = $null
    foreach ($profileName in $script:Config.profiles.Keys) {
        if ([string]::Equals([string]$profileName, $name, [System.StringComparison]::OrdinalIgnoreCase)) {
            $existingName = [string]$profileName
            break
        }
    }
    if ($null -ne $existingName) {
        if (-not (Confirm-ProfileAction -Message "保护预设【$existingName】已存在，是否覆盖？" -Title '清场助手')) { return }
        $name = $existingName
    }

    $previousProfiles = [ordered]@{}
    foreach ($profileName in $script:Config.profiles.Keys) {
        $previousProfiles[[string]$profileName] = @($script:Config.profiles[$profileName])
    }
    $previousActive = [string]$script:Config.activeProfile
    $currentNames = @($script:Config.protectedProcesses | ForEach-Object { Normalize-ProcessName ([string]$_) } | Where-Object { $_ } | Sort-Object -Unique)
    $script:Config.profiles[$name] = $currentNames
    $script:Config.activeProfile = $name
    Save-Config
    if (-not $script:LastConfigSaveSucceeded) {
        $script:Config.profiles = $previousProfiles
        $script:Config.activeProfile = $previousActive
        $script:StatusLabel.Text = "保护预设未保存：$($script:ConfigSaveError)"
        return
    }
    Refresh-ProtectionProfileList
    $script:StatusLabel.ForeColor = [System.Drawing.Color]::DimGray
    $script:StatusLabel.Text = "已保存预设【$name】（$($currentNames.Count) 个保护程序）"
}

function Apply-ProtectionProfile {
    if ($script:Busy -or $null -ne $script:RefreshState) { return }
    if ($null -eq $script:ProfileCombo.SelectedItem) { return }
    $name = [string]$script:ProfileCombo.SelectedItem
    if (-not $script:Config.profiles.Contains($name)) { return }

    if (-not (Confirm-ProfileAction -Message "应用预设【$name】会替换当前用户保护名单；不会关闭任何程序。继续吗？" -Title '应用保护预设')) { return }

    $oldNames = @($script:Config.protectedProcesses)
    $oldActive = [string]$script:Config.activeProfile
    $script:Config.protectedProcesses = @($script:Config.profiles[$name])
    $script:Config.activeProfile = $name
    Save-Config
    if (-not $script:LastConfigSaveSucceeded) {
        $script:Config.protectedProcesses = $oldNames
        $script:Config.activeProfile = $oldActive
        $script:StatusLabel.Text = "预设未应用（配置未保存）：$($script:ConfigSaveError)"
        return
    }
    Sync-VisibleProtectionState
    Refresh-ProtectionProfileList
    $script:StatusLabel.ForeColor = [System.Drawing.Color]::DimGray
    $script:StatusLabel.Text = "已应用预设【$name】；当前保护名单已替换"
}

function Delete-ProtectionProfile {
    if ($script:Busy -or $null -ne $script:RefreshState) { return }
    if ($null -eq $script:ProfileCombo.SelectedItem) { return }
    $name = [string]$script:ProfileCombo.SelectedItem
    if (-not $script:Config.profiles.Contains($name)) { return }

    if (-not (Confirm-ProfileAction -Message "确定删除保护预设【$name】？当前保护名单不会改变。" -Title '删除保护预设' -Icon ([System.Windows.Forms.MessageBoxIcon]::Warning))) { return }

    $oldProfiles = [ordered]@{}
    foreach ($profileName in $script:Config.profiles.Keys) {
        $oldProfiles[[string]$profileName] = @($script:Config.profiles[$profileName])
    }
    $oldActive = [string]$script:Config.activeProfile
    $script:Config.profiles.Remove($name)
    if ($oldActive -eq $name) { $script:Config.activeProfile = '' }
    Save-Config
    if (-not $script:LastConfigSaveSucceeded) {
        $script:Config.profiles = $oldProfiles
        $script:Config.activeProfile = $oldActive
        $script:StatusLabel.Text = "预设未删除：$($script:ConfigSaveError)"
        return
    }
    Refresh-ProtectionProfileList
    $script:StatusLabel.Text = "已删除预设【$name】；当前保护名单未改变"
}

function Restore-ConfigBackup {
    param([switch]$SkipConfirmation)
    $backupPath = $script:ConfigPath + '.bak'
    if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
        $script:StatusLabel.Text = '还没有配置备份；修改保护名单或预设后会自动生成 config.json.bak'
        return $false
    }
    if (-not $SkipConfirmation) {
        $answer = [System.Windows.Forms.MessageBox]::Show(
            '将使用 config.json.bak 恢复热键、关闭模式和保护名单。当前 config.json 会先保存为带时间戳的恢复点；恢复后请退出并重启程序使热键生效。继续吗？',
            '恢复上次配置',
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Warning,
            [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
        if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return $false }
    }

    $restoreTempPath = $script:ConfigPath + '.restore-' + [guid]::NewGuid().ToString('N') + '.tmp'
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $restorePointPath = $script:ConfigPath + '.pre-restore-' + $stamp + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    try {
        $backupJson = Get-Content -LiteralPath $backupPath -Raw -Encoding UTF8
        $null = $backupJson | ConvertFrom-Json -ErrorAction Stop
        [System.IO.File]::Copy($backupPath, $restoreTempPath, $false)
        if (Test-Path -LiteralPath $script:ConfigPath -PathType Leaf) {
            [System.IO.File]::Replace($restoreTempPath, $script:ConfigPath, $restorePointPath, $true)
        } else {
            [System.IO.File]::Move($restoreTempPath, $script:ConfigPath)
        }
        Load-Config -ResetDefaults
        Refresh-ProtectionProfileList
        Sync-VisibleProtectionState
        $script:CurrentMode = [string]$script:Config.lastMode
        if ($script:CurrentMode -eq 'all') {
            if ($null -ne $script:RadioAll) { $script:RadioAll.Checked = $true }
        } else {
            if ($null -ne $script:RadioWindows) { $script:RadioWindows.Checked = $true }
        }
        if ($null -ne $script:StatusLabel) {
            $script:StatusLabel.ForeColor = [System.Drawing.Color]::DarkGreen
            $script:StatusLabel.Text = '已从 config.json.bak 恢复；当前配置已另存为恢复点，退出并重启以应用热键'
        }
        return $true
    } catch {
        if ($null -ne $script:StatusLabel) {
            $script:StatusLabel.ForeColor = [System.Drawing.Color]::Firebrick
            $script:StatusLabel.Text = "恢复配置失败：$($_.Exception.Message)"
        }
        return $false
    } finally {
        if (Test-Path -LiteralPath $restoreTempPath -PathType Leaf) {
            try { Remove-Item -LiteralPath $restoreTempPath -Force } catch { }
        }
    }
}

function Initialize-Ui {
    [System.Windows.Forms.Application]::EnableVisualStyles()
    if ($null -eq $script:GenericAppIcon) {
        $script:GenericAppIcon = [System.Drawing.SystemIcons]::Application.ToBitmap()
    }
    try {
        [System.Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)
    } catch {
        # 已创建过控件时该调用会失败，忽略
    }

    $script:BoldFont = New-Object System.Drawing.Font -ArgumentList @('Microsoft YaHei UI', [single]9, [System.Drawing.FontStyle]::Bold)
    $baseFont = New-Object System.Drawing.Font -ArgumentList @('Microsoft YaHei UI', [single]9)

    $form = New-Object System.Windows.Forms.Form
    $form.Text = '清场助手 — 选择要关闭的程序'
    $form.StartPosition = 'CenterScreen'
    $form.Size = New-Object System.Drawing.Size(1040, 640)
    $form.MinimumSize = New-Object System.Drawing.Size(860, 440)
    $form.ShowInTaskbar = $false
    $form.TopMost = $true
    $form.KeyPreview = $true
    $form.Font = $baseFont
    $form.Icon = [System.Drawing.SystemIcons]::Application
    $script:Form = $form

    $root = New-Object System.Windows.Forms.TableLayoutPanel
    $root.Dock = 'Fill'
    $root.ColumnCount = 1
    $root.RowCount = 4
    $root.Padding = New-Object System.Windows.Forms.Padding(10)
    [void]$root.RowStyles.Add((New-Object System.Windows.Forms.RowStyle -ArgumentList @([System.Windows.Forms.SizeType]::Absolute, [single]38)))
    [void]$root.RowStyles.Add((New-Object System.Windows.Forms.RowStyle -ArgumentList @([System.Windows.Forms.SizeType]::Absolute, [single]36)))
    [void]$root.RowStyles.Add((New-Object System.Windows.Forms.RowStyle -ArgumentList @([System.Windows.Forms.SizeType]::Percent, [single]100)))
    [void]$root.RowStyles.Add((New-Object System.Windows.Forms.RowStyle -ArgumentList @([System.Windows.Forms.SizeType]::Absolute, [single]42)))
    $form.Controls.Add($root)

    # 顶部：模式 + 筛选 + 刷新
    $top = New-Object System.Windows.Forms.FlowLayoutPanel
    $top.Dock = 'Fill'
    $top.FlowDirection = 'LeftToRight'
    $top.WrapContents = $false
    $top.Padding = New-Object System.Windows.Forms.Padding(0, 7, 0, 0)
    [void]$root.Controls.Add($top, 0, 0)

    $profilePanel = New-Object System.Windows.Forms.FlowLayoutPanel
    $profilePanel.Dock = 'Fill'
    $profilePanel.FlowDirection = 'LeftToRight'
    $profilePanel.WrapContents = $false
    $profilePanel.Padding = New-Object System.Windows.Forms.Padding(0, 5, 0, 0)
    [void]$root.Controls.Add($profilePanel, 0, 1)

    $profileLabel = New-Object System.Windows.Forms.Label
    $profileLabel.Text = '保护预设：'
    $profileLabel.AutoSize = $true
    $profileLabel.Margin = New-Object System.Windows.Forms.Padding(0, 4, 4, 0)
    [void]$profilePanel.Controls.Add($profileLabel)

    $script:ProfileCombo = New-Object System.Windows.Forms.ComboBox
    $script:ProfileCombo.Width = 150
    $script:ProfileCombo.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
    $script:ProfileCombo.Margin = New-Object System.Windows.Forms.Padding(0, 1, 6, 0)
    [void]$profilePanel.Controls.Add($script:ProfileCombo)

    $script:ProfileApplyButton = New-Object System.Windows.Forms.Button
    $script:ProfileApplyButton.Text = '应用'
    $script:ProfileApplyButton.AutoSize = $true
    $script:ProfileApplyButton.Margin = New-Object System.Windows.Forms.Padding(0, 0, 4, 0)
    [void]$profilePanel.Controls.Add($script:ProfileApplyButton)

    $script:ProfileSaveButton = New-Object System.Windows.Forms.Button
    $script:ProfileSaveButton.Text = '保存当前名单'
    $script:ProfileSaveButton.AutoSize = $true
    $script:ProfileSaveButton.Margin = New-Object System.Windows.Forms.Padding(0, 0, 4, 0)
    [void]$profilePanel.Controls.Add($script:ProfileSaveButton)

    $script:ProfileDeleteButton = New-Object System.Windows.Forms.Button
    $script:ProfileDeleteButton.Text = '删除'
    $script:ProfileDeleteButton.AutoSize = $true
    $script:ProfileDeleteButton.Margin = New-Object System.Windows.Forms.Padding(0, 0, 4, 0)
    [void]$profilePanel.Controls.Add($script:ProfileDeleteButton)

    $script:ProfileRestoreButton = New-Object System.Windows.Forms.Button
    $script:ProfileRestoreButton.Text = '恢复备份'
    $script:ProfileRestoreButton.AutoSize = $true
    $script:ProfileRestoreButton.Margin = New-Object System.Windows.Forms.Padding(0, 0, 8, 0)
    [void]$profilePanel.Controls.Add($script:ProfileRestoreButton)

    $profileHint = New-Object System.Windows.Forms.Label
    $profileHint.Text = '应用会替换当前名单，不会关闭程序'
    $profileHint.AutoSize = $true
    $profileHint.ForeColor = [System.Drawing.Color]::DimGray
    $profileHint.Margin = New-Object System.Windows.Forms.Padding(0, 5, 0, 0)
    [void]$profilePanel.Controls.Add($profileHint)

    $script:RadioWindows = New-Object System.Windows.Forms.RadioButton
    $script:RadioWindows.Text = '只关窗口（保留托盘）'
    $script:RadioWindows.AutoSize = $true
    $script:RadioWindows.Margin = New-Object System.Windows.Forms.Padding(0, 4, 14, 0)
    [void]$top.Controls.Add($script:RadioWindows)

    $script:RadioAll = New-Object System.Windows.Forms.RadioButton
    $script:RadioAll.Text = '彻底退出（含后台）'
    $script:RadioAll.AutoSize = $true
    $script:RadioAll.Margin = New-Object System.Windows.Forms.Padding(0, 4, 20, 0)
    [void]$top.Controls.Add($script:RadioAll)

    $script:ShowAllWindowsCheck = New-Object System.Windows.Forms.CheckBox
    $script:ShowAllWindowsCheck.Text = '显示所有窗口'
    $script:ShowAllWindowsCheck.AutoSize = $true
    $script:ShowAllWindowsCheck.Margin = New-Object System.Windows.Forms.Padding(0, 4, 14, 0)
    $script:ShowAllWindowsCheck.Checked = $false
    [void]$top.Controls.Add($script:ShowAllWindowsCheck)

    $filterLabel = New-Object System.Windows.Forms.Label
    $filterLabel.Text = '筛选：'
    $filterLabel.AutoSize = $true
    $filterLabel.Margin = New-Object System.Windows.Forms.Padding(0, 7, 4, 0)
    [void]$top.Controls.Add($filterLabel)

    $script:SearchBox = New-Object System.Windows.Forms.TextBox
    $script:SearchBox.Width = 190
    $script:SearchBox.Margin = New-Object System.Windows.Forms.Padding(0, 4, 14, 0)
    [void]$top.Controls.Add($script:SearchBox)

    $script:RefreshButton = New-Object System.Windows.Forms.Button
    $script:RefreshButton.Text = '刷新 (F5)'
    $script:RefreshButton.AutoSize = $true
    $script:RefreshButton.Margin = New-Object System.Windows.Forms.Padding(0, 3, 0, 0)
    [void]$top.Controls.Add($script:RefreshButton)

    # 中部：程序列表
    $grid = New-Object System.Windows.Forms.DataGridView
    $grid.Dock = 'Fill'
    $grid.AllowUserToAddRows = $false
    $grid.AllowUserToDeleteRows = $false
    $grid.AllowUserToResizeRows = $false
    $grid.RowHeadersVisible = $false
    $grid.SelectionMode = 'FullRowSelect'
    $grid.MultiSelect = $false
    $grid.BackgroundColor = [System.Drawing.Color]::White
    $grid.BorderStyle = 'None'
    $grid.CellBorderStyle = 'SingleHorizontal'
    $grid.ColumnHeadersHeightSizeMode = 'DisableResizing'
    $grid.ColumnHeadersHeight = 30
    $grid.RowTemplate.Height = 30
    $grid.EditMode = 'EditOnEnter'
    $grid.Font = $baseFont
    $grid.GridColor = [System.Drawing.Color]::Gainsboro
    $script:Grid = $grid

    $colCheck = New-Object System.Windows.Forms.DataGridViewCheckBoxColumn
    $colCheck.HeaderText = ''
    $colCheck.Width = 40
    $colCheck.Name = 'colCheck'
    [void]$grid.Columns.Add($colCheck)

    $colIcon = New-Object System.Windows.Forms.DataGridViewImageColumn
    $colIcon.HeaderText = ''
    $colIcon.Width = 36
    $colIcon.Name = 'colIcon'
    $colIcon.ImageLayout = 'Zoom'
    $script:BlankIcon = New-Object System.Drawing.Bitmap -ArgumentList @(1, 1)
    $colIcon.DefaultCellStyle.NullValue = $script:BlankIcon
    [void]$grid.Columns.Add($colIcon)

    $colName = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
    $colName.HeaderText = '程序'
    $colName.Width = 200
    $colName.Name = 'colName'
    $colName.ReadOnly = $true
    [void]$grid.Columns.Add($colName)

    $colProcess = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
    $colProcess.HeaderText = '进程'
    $colProcess.Width = 190
    $colProcess.Name = 'colProcess'
    $colProcess.ReadOnly = $true
    [void]$grid.Columns.Add($colProcess)

    $colCount = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
    $colCount.HeaderText = '窗口'
    $colCount.Width = 52
    $colCount.Name = 'colCount'
    $colCount.ReadOnly = $true
    [void]$grid.Columns.Add($colCount)

    $colKind = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
    $colKind.HeaderText = '类型'
    $colKind.Width = 72
    $colKind.Name = 'colKind'
    $colKind.ReadOnly = $true
    [void]$grid.Columns.Add($colKind)

    $colTitle = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
    $colTitle.HeaderText = '窗口标题'
    $colTitle.Name = 'colTitle'
    $colTitle.ReadOnly = $true
    $colTitle.AutoSizeMode = 'Fill'
    [void]$grid.Columns.Add($colTitle)

    $colResult = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
    $colResult.HeaderText = '结果'
    $colResult.Width = 170
    $colResult.Name = 'colResult'
    $colResult.ReadOnly = $true
    [void]$grid.Columns.Add($colResult)

    foreach ($column in $grid.Columns) {
        $column.SortMode = 'NotSortable'
    }

    [void]$root.Controls.Add($grid, 0, 2)

    # 底部：状态 + 操作按钮
    $bottom = New-Object System.Windows.Forms.TableLayoutPanel
    $bottom.Dock = 'Fill'
    $bottom.ColumnCount = 2
    $bottom.RowCount = 1
    [void]$bottom.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle -ArgumentList @([System.Windows.Forms.SizeType]::Percent, [single]100)))
    [void]$bottom.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle -ArgumentList @([System.Windows.Forms.SizeType]::AutoSize)))
    [void]$root.Controls.Add($bottom, 0, 3)

    $script:StatusLabel = New-Object System.Windows.Forms.Label
    $script:StatusLabel.Dock = 'Fill'
    $script:StatusLabel.TextAlign = 'MiddleLeft'
    $script:StatusLabel.ForeColor = [System.Drawing.Color]::DimGray
    $script:StatusLabel.Text = '就绪'
    [void]$bottom.Controls.Add($script:StatusLabel, 0, 0)

    $buttonPanel = New-Object System.Windows.Forms.FlowLayoutPanel
    $buttonPanel.Dock = 'Fill'
    $buttonPanel.AutoSize = $true
    $buttonPanel.FlowDirection = 'RightToLeft'
    $buttonPanel.WrapContents = $false
    $buttonPanel.Padding = New-Object System.Windows.Forms.Padding(0, 7, 0, 0)
    [void]$bottom.Controls.Add($buttonPanel, 1, 0)

    $script:CloseButton = New-Object System.Windows.Forms.Button
    $script:CloseButton.Text = '关闭选中'
    $script:CloseButton.AutoSize = $true
    $script:CloseButton.BackColor = [System.Drawing.Color]::FromArgb(0, 120, 215)
    $script:CloseButton.ForeColor = [System.Drawing.Color]::White
    $script:CloseButton.FlatStyle = 'Flat'
    $script:CloseButton.FlatAppearance.BorderSize = 0
    $script:CloseButton.Padding = New-Object System.Windows.Forms.Padding(10, 2, 10, 2)
    [void]$buttonPanel.Controls.Add($script:CloseButton)

    $script:ProtectSelectedButton = New-Object System.Windows.Forms.Button
    $script:ProtectSelectedButton.Text = '保护选中'
    $script:ProtectSelectedButton.AutoSize = $true
    $script:ProtectSelectedButton.Margin = New-Object System.Windows.Forms.Padding(0, 0, 6, 0)
    [void]$buttonPanel.Controls.Add($script:ProtectSelectedButton)

    $selectNoneButton = New-Object System.Windows.Forms.Button
    $script:SelectNoneButton = $selectNoneButton
    $selectNoneButton.Text = '全不选'
    $selectNoneButton.AutoSize = $true
    [void]$buttonPanel.Controls.Add($selectNoneButton)

    $selectAllButton = New-Object System.Windows.Forms.Button
    $script:SelectAllButton = $selectAllButton
    $selectAllButton.Text = '全选'
    $selectAllButton.AutoSize = $true
    [void]$buttonPanel.Controls.Add($selectAllButton)

    # 右键保护菜单
    $menu = New-Object System.Windows.Forms.ContextMenuStrip
    $script:ProtectItem = $menu.Items.Add('加入保护名单（永不关闭）')
    $script:UnprotectItem = $menu.Items.Add('移出保护名单')
    $grid.ContextMenuStrip = $menu

    # 事件
    $grid.Add_CurrentCellDirtyStateChanged({
        if ($script:Grid.IsCurrentCellDirty -and $script:Grid.CurrentCell -is [System.Windows.Forms.DataGridViewCheckBoxCell]) {
            $script:Grid.CommitEdit([System.Windows.Forms.DataGridViewDataErrorContexts]::Commit)
        }
    })
    $grid.Add_CellValueChanged({
        param($sender, $eventArgs)
        if ($script:SuppressGridEvents) { return }
        if ($eventArgs.ColumnIndex -eq 0) { Update-SelectionStatus }
    })
    $grid.Add_CellDoubleClick({
        param($sender, $eventArgs)
        if ($script:RefreshState -or $script:Busy) { return }
        if ($eventArgs.RowIndex -lt 0) { return }
        $row = $script:Grid.Rows[$eventArgs.RowIndex]
        if ($null -eq $row.Tag -or $row.Tag.Protected) { return }
        $row.Cells[0].Value = -not [bool]$row.Cells[0].Value
    })
    $grid.Add_CellMouseDown({
        param($sender, $eventArgs)
        if ($eventArgs.Button -eq [System.Windows.Forms.MouseButtons]::Right -and $eventArgs.RowIndex -ge 0) {
            $script:ContextRowIndex = $eventArgs.RowIndex
            $script:Grid.ClearSelection()
            $script:Grid.Rows[$eventArgs.RowIndex].Selected = $true
        }
    })
    $menu.Add_Opening({
        if ($null -eq $script:ContextRowIndex) {
            $script:ProtectItem.Enabled = $false
            $script:UnprotectItem.Enabled = $false
            return
        }
        $row = $script:Grid.Rows[$script:ContextRowIndex]
        if ($null -eq $row.Tag) { return }
        $script:ProtectItem.Enabled = -not $row.Tag.Protected
        $script:UnprotectItem.Enabled = $row.Tag.Protected
    })
    $script:ProtectItem.Add_Click({ Toggle-ContextProtection -Protect $true })
    $script:UnprotectItem.Add_Click({ Toggle-ContextProtection -Protect $false })

    $script:RefreshButton.Add_Click({
        Refresh-AppList -Mode $script:CurrentMode -Force
    })
    $script:CloseButton.Add_Click({ Invoke-CloseSelected })
    $script:ProtectSelectedButton.Add_Click({ Protect-Selected })
    $script:ProfileApplyButton.Add_Click({ Apply-ProtectionProfile })
    $script:ProfileSaveButton.Add_Click({ Save-ProtectionProfile })
    $script:ProfileDeleteButton.Add_Click({ Delete-ProtectionProfile })
    $script:ProfileRestoreButton.Add_Click({ [void](Restore-ConfigBackup) })
    $selectAllButton.Add_Click({ Select-AllVisible })
    $selectNoneButton.Add_Click({ Select-None })
    $script:SearchBox.Add_TextChanged({ Apply-Filter })
    $script:ShowAllWindowsCheck.Add_CheckedChanged({ Apply-Filter })

    $script:RadioWindows.Add_CheckedChanged({
        if ($script:RadioWindows.Checked -and $script:UiReady) { Set-Mode -Mode 'windows' }
    })
    $script:RadioAll.Add_CheckedChanged({
        if ($script:RadioAll.Checked -and $script:UiReady) { Set-Mode -Mode 'all' }
    })

    $form.Add_KeyDown({
        param($sender, $eventArgs)
        if ($eventArgs.KeyCode -eq [System.Windows.Forms.Keys]::Escape) {
            $script:Form.Hide()
            $eventArgs.Handled = $true
        } elseif ($eventArgs.KeyCode -eq [System.Windows.Forms.Keys]::F5) {
            Refresh-AppList -Mode $script:CurrentMode -Force
            $eventArgs.Handled = $true
        }
    })
    $form.Add_FormClosing({
        param($sender, $eventArgs)
        if (-not $script:ReallyExit) {
            $eventArgs.Cancel = $true
            $script:Form.Hide()
        }
    })

    # 初始模式
    $script:CurrentMode = [string]$script:Config.lastMode
    if ($script:CurrentMode -ne 'all') { $script:CurrentMode = 'windows' }

    $script:UiReady = $true
    if ($script:CurrentMode -eq 'all') {
        $script:RadioAll.Checked = $true
    } else {
        $script:RadioWindows.Checked = $true
    }
    Refresh-ProtectionProfileList
    $script:ProfileRestoreButton.Enabled = (Test-Path -LiteralPath ($script:ConfigPath + '.bak') -PathType Leaf)
    Update-SelectionStatus
    if (-not [string]::IsNullOrWhiteSpace($script:ConfigLoadError)) {
        $script:StatusLabel.ForeColor = [System.Drawing.Color]::Firebrick
        $script:StatusLabel.Text = "配置读取失败；可点【恢复备份】：$($script:ConfigLoadError)"
    }
}

function Initialize-Tray {
    $notify = New-Object System.Windows.Forms.NotifyIcon
    $notify.Icon = [System.Drawing.SystemIcons]::Application
    $notify.Text = '清场助手'
    $notify.Visible = $true

    $menu = New-Object System.Windows.Forms.ContextMenuStrip
    $openItem = $menu.Items.Add('打开面板')
    [void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
    $exitItem = $menu.Items.Add('退出')
    $notify.ContextMenuStrip = $menu

    $openItem.Add_Click({ Show-Picker })
    $exitItem.Add_Click({ Exit-App })
    $notify.Add_MouseClick({
        param($sender, $eventArgs)
        if ($eventArgs.Button -eq [System.Windows.Forms.MouseButtons]::Left) { Show-Picker }
    })
    $script:NotifyIcon = $notify
}

function Initialize-Hotkey {
    $parsed = Parse-Hotkey -Text ([string]$script:Config.hotkey)
    try {
        $hotkeyForm = New-Object HotKeyForm([uint32]$parsed.Modifiers, [uint32]$parsed.VirtualKey)
        [void]$hotkeyForm.Handle
        $hotkeyForm.Add_HotKeyPressed({ Show-Picker })
        $script:HotKeyForm = $hotkeyForm
        if (-not $hotkeyForm.Registered) {
            $script:HotkeyFailed = $true
            $script:StatusLabel.Text = "热键 $($script:Config.hotkey) 注册失败（可能被占用），请从托盘图标打开"
            if ($null -ne $script:NotifyIcon) {
                try {
                    $script:NotifyIcon.ShowBalloonTip(
                        5000,
                        '清场助手',
                        "热键 $($script:Config.hotkey) 注册失败（可能被占用）。请从托盘图标打开，或修改 config.json 里的 hotkey。",
                        [System.Windows.Forms.ToolTipIcon]::Warning)
                } catch { }
            }
        }
    } catch {
        $script:HotkeyFailed = $true
        $script:StatusLabel.Text = '热键注册失败，请从托盘图标打开'
        if ($null -ne $script:NotifyIcon) {
            try {
                $script:NotifyIcon.ShowBalloonTip(
                    5000,
                    '清场助手',
                    '热键注册失败，请从托盘图标打开，或修改 config.json 里的 hotkey。',
                    [System.Windows.Forms.ToolTipIcon]::Warning)
            } catch { }
        }
    }
}

function Exit-App {
    if ($script:Busy) {
        [void][System.Windows.Forms.MessageBox]::Show('正在执行关闭，请稍候再退出。', '清场助手', 'OK', 'Information')
        return
    }
    $script:ReallyExit = $true
    if ($null -ne $script:RefreshState) {
        $scan = $script:RefreshState
        $script:RefreshState = $null
        try { $scan.Timer.Stop(); $scan.Timer.Dispose() } catch { }
        try { $scan.Worker.Stop() } catch { }
        try { $scan.Worker.Dispose() } catch { }
    }
    try { if ($null -ne $script:NotifyIcon) { $script:NotifyIcon.Visible = $false; $script:NotifyIcon.Dispose() } } catch { }
    try { if ($null -ne $script:HotKeyForm) { $script:HotKeyForm.Dispose() } } catch { }
    try { if ($null -ne $script:Form) { $script:Form.Dispose() } } catch { }
    try { if ($null -ne $script:Mutex) { $script:Mutex.ReleaseMutex() } } catch { }
    try { if ($null -ne $script:Context) { $script:Context.ExitThread() } } catch { }
}

# ---------------------------------------------------------------------------
# 入口
# ---------------------------------------------------------------------------
Load-Config -ResetDefaults

if ($SelfTest) {
    try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
    Write-Host "PowerShell: $($PSVersionTable.PSVersion)"
    Write-Host "配置路径: $script:ConfigPath"
    Write-Host "热键: $($script:Config.hotkey) / 超时: $($script:Config.gracefulTimeoutSeconds)s"
    try {
        $parsed = Parse-Hotkey -Text ([string]$script:Config.hotkey)
        $testHotkey = New-Object HotKeyForm -ArgumentList @([uint32]$parsed.Modifiers, [uint32]$parsed.VirtualKey)
        [void]$testHotkey.Handle
        $hkStatus = '失败（可能被占用）'
        if ($testHotkey.Registered) { $hkStatus = '成功' }
        Write-Host "热键注册测试: $hkStatus"

        $script:HotKeyTestFired = $false
        $testHotkey.Add_HotKeyPressed({ $script:HotKeyTestFired = $true })
        [void][NativeMethods]::PostMessage($testHotkey.Handle, [uint32]0x0312, [IntPtr]0x4150, [IntPtr]::Zero)
        [System.Windows.Forms.Application]::DoEvents()
        $callbackStatus = '未触发'
        if ($script:HotKeyTestFired) { $callbackStatus = '成功' }
        Write-Host "热键回调测试: $callbackStatus"

        $testHotkey.Dispose()
    } catch {
        Write-Host "热键注册测试: 异常 $($_.Exception.Message)"
    }
    $visible = [NativeMethods]::GetVisibleWindows()
    Write-Host "可见顶层窗口数: $($visible.Count)"
    $windowsWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $appsWindows = Get-AppCandidates -Mode 'windows'
    $windowsWatch.Stop()
    Write-Host "模式一候选程序数: $($appsWindows.Count)，扫描耗时: $($windowsWatch.ElapsedMilliseconds)ms"
    foreach ($app in ($appsWindows | Select-Object -First 30)) {
        Write-Host ("  {0,-30} PID={1,-7} 窗口={2} 类型={3}  {4}" -f $app.Name, $app.ProcessId, $app.WindowCount, $app.Kind, $app.Titles)
    }
    $allScanWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $appsAll = Get-AppCandidates -Mode 'all'
    $allScanWatch.Stop()
    Write-Host "模式二候选程序数: $($appsAll.Count)，扫描耗时: $($allScanWatch.ElapsedMilliseconds)ms"
    foreach ($app in ($appsAll | Select-Object -First 40)) {
        $sub = Get-PeSubsystem -Path $app.Path
        Write-Host ("  {0,-30} PID={1,-7} 窗口={2} 类型={3} Subsystem={4}" -f $app.Name, $app.ProcessId, $app.WindowCount, $app.Kind, $sub)
    }
    exit 0
}

if ($CloseTest) {
    try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
    $testProcess = $null
    foreach ($exe in @('charmap.exe', 'mspaint.exe', 'notepad.exe')) {
        try {
            $testProcess = Start-Process -FilePath $exe -PassThru -ErrorAction Stop
            break
        } catch {
            $testProcess = $null
        }
    }
    if ($null -eq $testProcess) {
        Write-Host 'FAIL：无法启动测试程序'
        exit 1
    }
    $testName = $testProcess.ProcessName
    try {
        $windowDeadline = [DateTime]::UtcNow.AddSeconds(5)
        $testWindows = @()
        while ([DateTime]::UtcNow -lt $windowDeadline) {
            Start-Sleep -Milliseconds 200
            $testWindows = @([NativeMethods]::GetVisibleWindows() | Where-Object { $_.ProcessId -eq $testProcess.Id })
            if ($testWindows.Count -gt 0) { break }
        }
        if ($testWindows.Count -eq 0) {
            Write-Host "FAIL：测试程序 $($testProcess.ProcessName) 没有可见窗口"
            try { $testProcess.Kill() } catch { }
            exit 1
        }
        foreach ($window in $testWindows) {
            [void][NativeMethods]::PostMessage($window.Handle, [uint32]0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        }
        if ($testProcess.WaitForExit(5000)) {
            Write-Host "PASS：已优雅关闭测试程序 $testName"
            exit 0
        }
        Write-Host 'FAIL：测试程序未在 5 秒内关闭'
        try { $testProcess.Kill() } catch { }
        exit 1
    } catch {
        Write-Host "FAIL：$($_.Exception.Message)"
        try { $testProcess.Kill() } catch { }
        exit 1
    }
}

if ($ConfigTest) {
    try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
    $originalConfig = $script:Config
    $originalConfigPath = $script:ConfigPath
    $testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('OneClickClose-ConfigTest-' + [guid]::NewGuid().ToString('N'))
    $testError = $null
    $legacyPreserved = $false
    $migrationWritten = $false
    $profileSaved = $false
    $profileApplied = $false
    $profileDeleted = $false
    $backupRestored = $false
    $restorePointPreserved = $false
    $protectSaveFailureHandled = $false

    try {
        [void][System.IO.Directory]::CreateDirectory($testRoot)
        $script:ConfigPath = Join-Path $testRoot 'config.json'
        @'
{
  "hotkey": "Ctrl+Shift+Z",
  "gracefulTimeoutSeconds": 9,
  "protectedProcesses": ["legacy-alpha", "legacy-beta"],
  "lastMode": "all"
}
'@ | Set-Content -LiteralPath $script:ConfigPath -Encoding UTF8

        Load-Config -ResetDefaults
        $legacyNames = @($script:Config.protectedProcesses | ForEach-Object { Normalize-ProcessName $_ } | Sort-Object)
        $legacyPreserved = (($legacyNames -join ',') -eq 'legacy-alpha,legacy-beta') -and
                           ($script:Config.hotkey -eq 'Ctrl+Shift+Z') -and
                           ($script:Config.gracefulTimeoutSeconds -eq 9) -and
                           ($script:Config.lastMode -eq 'all')

        Initialize-Ui
        $script:Form.Opacity = 0
        $script:Form.Show()
        [System.Windows.Forms.Application]::DoEvents()
        $script:ProfileTestName = '工作'
        $script:ProfileSaveButton.PerformClick()
        $profileSaved = $script:Config.profiles.Contains('工作') -and
                        (($script:Config.profiles['工作'] -join ',') -eq 'legacy-alpha,legacy-beta')

        $firstBackupPath = $script:ConfigPath + '.bak'
        $firstBackupData = (Get-Content -LiteralPath $firstBackupPath -Raw -Encoding UTF8) | ConvertFrom-Json
        $legacyBackupGood = ($null -eq $firstBackupData.schemaVersion) -and
                            ((@($firstBackupData.protectedProcesses | Sort-Object) -join ',') -eq 'legacy-alpha,legacy-beta')
        $currentData = (Get-Content -LiteralPath $script:ConfigPath -Raw -Encoding UTF8) | ConvertFrom-Json
        $migrationWritten = ($currentData.schemaVersion -eq 2) -and
                            ($currentData.profiles.PSObject.Properties.Name -contains '工作') -and
                            $legacyBackupGood

        $script:Config.protectedProcesses = @('temporary-current')
        $script:ProfileCombo.SelectedItem = '工作'
        $script:ProfileTestDecision = 'Yes'
        $script:ProfileApplyButton.PerformClick()
        $appliedNames = @($script:Config.protectedProcesses | ForEach-Object { Normalize-ProcessName $_ } | Sort-Object)
        $profileApplied = (($appliedNames -join ',') -eq 'legacy-alpha,legacy-beta') -and
                          ($script:Config.activeProfile -eq '工作')

        $script:Config.protectedProcesses = @('newer-state')
        $script:Config.activeProfile = ''
        Save-Config

        $backupProfileData = (Get-Content -LiteralPath $firstBackupPath -Raw -Encoding UTF8) | ConvertFrom-Json
        $backupHasProfileAndLegacyNames = ($backupProfileData.schemaVersion -eq 2) -and
                                           ($backupProfileData.profiles.PSObject.Properties.Name -contains '工作') -and
                                           ((@($backupProfileData.protectedProcesses | Sort-Object) -join ',') -eq 'legacy-alpha,legacy-beta')

        $restoreSucceeded = Restore-ConfigBackup -SkipConfirmation
        $restoredNames = @($script:Config.protectedProcesses | ForEach-Object { Normalize-ProcessName $_ } | Sort-Object)
        $backupRestored = $restoreSucceeded -and
                          $backupHasProfileAndLegacyNames -and
                          (($restoredNames -join ',') -eq 'legacy-alpha,legacy-beta') -and
                          $script:Config.profiles.Contains('工作')
        $restorePoints = @(Get-ChildItem -LiteralPath $testRoot -Filter 'config.json.pre-restore-*' -File -ErrorAction SilentlyContinue)
        if ($restorePoints.Count -gt 0) {
            $restorePointData = (Get-Content -LiteralPath $restorePoints[0].FullName -Raw -Encoding UTF8) | ConvertFrom-Json
            $restorePointPreserved = (@($restorePointData.protectedProcesses) -contains 'newer-state')
        }

        $script:ProfileTestDecision = 'Yes'
        $script:ProfileCombo.SelectedItem = '工作'
        $script:ProfileDeleteButton.PerformClick()
        $deletedNames = @($script:Config.protectedProcesses | ForEach-Object { Normalize-ProcessName $_ } | Sort-Object)
        $profileDeleted = (-not $script:Config.profiles.Contains('工作')) -and
                          (($deletedNames -join ',') -eq 'legacy-alpha,legacy-beta')

        $protectTestRowIndex = $script:Grid.Rows.Add()
        $protectTestRow = $script:Grid.Rows[$protectTestRowIndex]
        $protectTestRow.Tag = [PSCustomObject]@{
            ProcessId = 0
            Name = 'write-failure-target'
            DisplayName = '写入失败测试'
            Path = ''
            Windows = @()
            WindowCount = 0
            Kind = '后台'
            Titles = ''
            Protected = $false
            IsForeground = $false
        }
        $protectTestRow.Cells[0].Value = $true
        $protectTestRow.Cells[2].Value = '写入失败测试'
        $protectTestRow.Cells[3].Value = 'write-failure-target'
        $protectTestRow.Cells[4].Value = 0
        $protectTestRow.Cells[5].Value = '后台'
        $beforeProtectNames = @($script:Config.protectedProcesses | ForEach-Object { Normalize-ProcessName $_ } | Sort-Object) -join ','
        $script:ConfigPath = Join-Path (Join-Path $testRoot 'missing-directory') 'config.json'
        $script:ProtectSelectedButton.PerformClick()
        $afterProtectNames = @($script:Config.protectedProcesses | ForEach-Object { Normalize-ProcessName $_ } | Sort-Object) -join ','
        $protectSaveFailureHandled = (-not $script:LastConfigSaveSucceeded) -and
                                     ($beforeProtectNames -eq $afterProtectNames) -and
                                     (-not [bool]$protectTestRow.Tag.Protected) -and
                                     ($protectTestRow.Cells[0].Value -eq $true) -and
                                     ($script:StatusLabel.Text -like '保护名单未保存，已撤销更改：*')
        $script:ConfigPath = Join-Path $testRoot 'config.json'
    } catch {
        $testError = $_.Exception.Message
    } finally {
        $script:ProfileTestName = $null
        $script:ProfileTestDecision = $null
        if ($null -ne $script:Form) { try { $script:Form.Hide(); $script:Form.Dispose() } catch { } }
        $script:Config = $originalConfig
        $script:ConfigPath = $originalConfigPath
        if (Test-Path -LiteralPath $testRoot -PathType Container) {
            try { Remove-Item -LiteralPath $testRoot -Recurse -Force } catch { }
        }
    }

    if ($null -ne $testError) {
        Write-Host "FAIL：$testError"
        exit 1
    }
    $verdict = 'PASS'
    if (-not $legacyPreserved) { $verdict = 'FAIL：旧配置保护名单或设置未保留' }
    if (-not $migrationWritten) { $verdict = 'FAIL：schemaVersion 2 迁移或旧格式备份失败' }
    if (-not $profileSaved -or -not $profileApplied -or -not $profileDeleted) { $verdict = 'FAIL：保护预设保存/应用/删除流程异常' }
    if (-not $backupRestored) { $verdict = 'FAIL：配置备份未恢复旧名单与预设' }
    if (-not $restorePointPreserved) { $verdict = 'FAIL：恢复前配置未保存在时间戳恢复点' }
    if (-not $protectSaveFailureHandled) { $verdict = 'FAIL：保护名单保存失败时未回滚并显示错误' }
    Write-Host ("{0}：旧配置兼容={1}，schema 迁移={2}，预设保存/应用/删除={3}/{4}/{5}，备份恢复={6}，恢复点={7}，保护保存失败回滚={8}" -f $verdict, $legacyPreserved, $migrationWritten, $profileSaved, $profileApplied, $profileDeleted, $backupRestored, $restorePointPreserved, $protectSaveFailureHandled)
    if ($verdict -ne 'PASS') { exit 1 }
    exit 0
}

if ($SafetyTest) {
    try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
    $testProcesses = New-Object System.Collections.ArrayList
    $testError = $null
    $cancelPreventedClose = $false
    $previewListsBothPids = $false
    $scannerGroupingOk = $false
    $confirmedCloseBoth = $false
    $groupProcessCount = 0
    $script:SafetyTestDecision = 'No'
    $script:SafetyTestPreviewText = ''

    try {
        $charmapPath = Join-Path $env:windir 'System32\charmap.exe'
        for ($index = 0; $index -lt 2; $index++) {
            $proc = Start-Process -FilePath $charmapPath -PassThru -ErrorAction Stop
            [void]$testProcesses.Add($proc)
            $deadline = [DateTime]::UtcNow.AddSeconds(8)
            $procWindows = @()
            while ([DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 100
                $procWindows = @([NativeMethods]::GetVisibleWindows() | Where-Object { $_.ProcessId -eq $proc.Id })
                if ($procWindows.Count -gt 0) { break }
            }
            if ($procWindows.Count -eq 0) { throw "测试窗口未能启动（PID $($proc.Id)）" }
        }
        $testPids = @($testProcesses | ForEach-Object { [int]$_.Id })
        if (@($testPids | Sort-Object -Unique).Count -ne 2) { throw '测试未能创建两个独立的 charmap 进程' }

        $scannerGroupingOk = $true
        $seenGroupKeys = @{}
        foreach ($candidate in (Get-AppCandidates -Mode 'windows')) {
            $candidateIds = @(Get-AppProcessIds -App $candidate)
            if ($candidateIds.Count -ne [int]$candidate.ProcessCount) { $scannerGroupingOk = $false }
            $groupKey = 'pid:' + $candidate.ProcessId
            if (-not [string]::IsNullOrWhiteSpace($candidate.Path)) {
                $groupKey = 'path:' + $candidate.Path.Trim().ToLowerInvariant()
            }
            if ($seenGroupKeys.ContainsKey($groupKey)) { $scannerGroupingOk = $false }
            $seenGroupKeys[$groupKey] = $true
        }

        Initialize-Ui
        $script:Form.Opacity = 0
        $script:Form.Show()
        [System.Windows.Forms.Application]::DoEvents()

        $allWindows = @([NativeMethods]::GetVisibleWindows() | Where-Object { $testPids -contains [int]$_.ProcessId })
        $path = Get-ProcessPathSafe -Process $testProcesses[0]
        $titles = @($allWindows | ForEach-Object { $_.Title } | Select-Object -Unique) -join ' / '
        $app = [PSCustomObject]@{
            ProcessId = $testPids[0]
            ProcessIds = $testPids
            ProcessCount = 2
            Name = $testProcesses[0].ProcessName
            DisplayName = '测试字符映射表'
            Path = $path
            Windows = $allWindows
            WindowCount = $allWindows.Count
            Kind = '窗口'
            Titles = $titles
            Protected = $false
            IsForeground = $false
            IsTaskbarApp = $true
        }
        $rowIndex = $script:Grid.Rows.Add()
        $row = $script:Grid.Rows[$rowIndex]
        $row.Tag = $app
        $row.Cells[0].Value = $true
        $row.Cells[1].Value = $script:GenericAppIcon
        $row.Cells[2].Value = $app.DisplayName
        $row.Cells[3].Value = 'charmap (2 个进程)'
        $row.Cells[4].Value = $app.WindowCount
        $row.Cells[5].Value = $app.Kind
        $row.Cells[6].Value = $app.Titles
        $row.Cells[7].Value = ''
        $script:CurrentMode = 'all'

        Invoke-CloseSelected
        Start-Sleep -Milliseconds 350

        $cancelPreventedClose = $true
        foreach ($proc in $testProcesses) {
            $stillRunning = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
            $stillVisible = @([NativeMethods]::GetVisibleWindows() | Where-Object { $_.ProcessId -eq $proc.Id }).Count -gt 0
            if ($null -eq $stillRunning -or -not $stillVisible) { $cancelPreventedClose = $false }
        }
        $previewListsBothPids = $true
        foreach ($testPid in $testPids) {
            if ($script:SafetyTestPreviewText -notmatch "PID\s+$testPid\b") { $previewListsBothPids = $false }
        }
        $groupProcessCount = [int]$app.ProcessCount

        $script:SafetyTestDecision = 'Yes'
        Invoke-CloseSelected
        $closeDeadline = [DateTime]::UtcNow.AddSeconds(8)
        while ([DateTime]::UtcNow -lt $closeDeadline) {
            $aliveCount = @($testProcesses | Where-Object { $null -ne (Get-Process -Id $_.Id -ErrorAction SilentlyContinue) }).Count
            if ($aliveCount -eq 0) { break }
            Start-Sleep -Milliseconds 50
        }
        $confirmedCloseBoth = @($testProcesses | Where-Object { $null -eq (Get-Process -Id $_.Id -ErrorAction SilentlyContinue) }).Count -eq 2
    } catch {
        $testError = $_.Exception.Message
    } finally {
        $script:SafetyTestDecision = $null
        if ($null -ne $script:Form) { try { $script:Form.Hide(); $script:Form.Dispose() } catch { } }
        foreach ($proc in $testProcesses) {
            try {
                $stillRunning = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
                if ($null -ne $stillRunning) {
                    $procWindows = @([NativeMethods]::GetVisibleWindows() | Where-Object { $_.ProcessId -eq $proc.Id })
                    foreach ($window in $procWindows) {
                        [void][NativeMethods]::PostMessage($window.Handle, [uint32]0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
                    }
                    if (-not $proc.WaitForExit(1500)) { $proc.Kill() }
                }
            } catch { try { $proc.Kill() } catch { } }
        }
    }

    if ($null -ne $testError) {
        Write-Host "FAIL：$testError"
        exit 1
    }
    $verdict = 'PASS'
    if (-not $scannerGroupingOk) { $verdict = 'FAIL：扫描结果未按程序路径正确分组' }
    if ($groupProcessCount -ne 2) { $verdict = 'FAIL：同路径进程未作为一个应用组测试' }
    if (-not $previewListsBothPids) { $verdict = 'FAIL：预览没有列出两个目标 PID' }
    if (-not $cancelPreventedClose) { $verdict = 'FAIL：取消预览后测试进程或窗口被关闭' }
    if (-not $confirmedCloseBoth) { $verdict = 'FAIL：确认后没有处理完组内两个测试进程' }
    Write-Host ("{0}：扫描分组正确={1}，应用组进程数 {2}，预览包含两个 PID，取消保留两个窗口，确认后退出两个测试进程={3}" -f $verdict, $scannerGroupingOk, $groupProcessCount, $confirmedCloseBoth)
    if ($verdict -ne 'PASS') { exit 1 }
    exit 0
}

if ($ProtectRefreshTest) {
    try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
    $originalConfig = $script:Config
    $originalConfigPath = $script:ConfigPath
    $testConfigRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('OneClickClose-ProtectRefreshTest-' + [guid]::NewGuid().ToString('N'))
    $testConfigPath = Join-Path $testConfigRoot 'config.json'
    $testError = $null
    $targetName = $null
    $actionElapsed = 0L
    $batchNamesCount = 0
    $protectedAfterAction = $false
    $unprotectedAfterAction = $false
    $batchProtectionOk = $false

    try {
        [void][System.IO.Directory]::CreateDirectory($testConfigRoot)
        $script:ConfigPath = $testConfigPath
        $script:Config = [ordered]@{
            hotkey                 = 'Ctrl+Alt+X'
            gracefulTimeoutSeconds = 5
            protectedProcesses     = @()
            lastMode               = 'windows'
        }

        Initialize-Ui
        $script:Form.Opacity = 0
        $script:Form.Show()
        [System.Windows.Forms.Application]::DoEvents()
        Refresh-AppList -Mode 'windows'
        Wait-ForAppRefresh -TimeoutSeconds 30
        $baselineRows = $script:Grid.Rows.Count
        for ($i = 0; $i -lt $script:Grid.Rows.Count; $i++) {
            $candidate = $script:Grid.Rows[$i]
            if ($null -ne $candidate.Tag -and -not $candidate.Tag.Protected) {
                $targetRow = $candidate
                $script:ContextRowIndex = $i
                break
            }
        }
        if ($null -eq $targetRow) { throw '没有可用的窗口程序来驱动保护操作' }
        $targetName = Normalize-ProcessName $targetRow.Tag.Name

        $protectWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $script:ContextRowIndex = $targetRow.Index
        $script:ProtectItem.Enabled = $true
        $script:ProtectItem.PerformClick()
        $protectWatch.Stop()
        $protectedAfterAction = @($script:Config.protectedProcesses | ForEach-Object { Normalize-ProcessName $_ }) -contains $targetName
        $protectedRows = @($script:Grid.Rows | Where-Object { $null -ne $_.Tag -and (Normalize-ProcessName $_.Tag.Name) -eq $targetName })
        $protectedRowsOk = ($protectedRows.Count -gt 0)
        foreach ($protectedRow in $protectedRows) {
            if (-not $protectedRow.Tag.Protected -or -not $protectedRow.Cells[0].ReadOnly -or $protectedRow.Cells[5].Value -ne '已保护') {
                $protectedRowsOk = $false
            }
        }

        $unprotectWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $script:ContextRowIndex = $targetRow.Index
        $script:UnprotectItem.Enabled = $true
        $script:UnprotectItem.PerformClick()
        $unprotectWatch.Stop()
        $unprotectedAfterAction = -not (@($script:Config.protectedProcesses | ForEach-Object { Normalize-ProcessName $_ }) -contains $targetName)
        $unprotectedRows = @($script:Grid.Rows | Where-Object { $null -ne $_.Tag -and (Normalize-ProcessName $_.Tag.Name) -eq $targetName })
        foreach ($unprotectedRow in $unprotectedRows) {
            if ($unprotectedRow.Tag.Protected -or $unprotectedRow.Cells[0].ReadOnly -or $unprotectedRow.Cells[5].Value -ne $unprotectedRow.Tag.Kind) {
                $unprotectedAfterAction = $false
            }
        }
        $batchRows = New-Object System.Collections.ArrayList
        $batchNames = New-Object System.Collections.ArrayList
        foreach ($candidate in $script:Grid.Rows) {
            if ($null -eq $candidate.Tag -or -not $candidate.Visible -or $candidate.Tag.Protected) { continue }
            $candidateName = Normalize-ProcessName $candidate.Tag.Name
            if ([string]::IsNullOrWhiteSpace($candidateName) -or $batchNames -contains $candidateName) { continue }
            [void]$batchNames.Add($candidateName)
            [void]$batchRows.Add($candidate)
            if ($batchNames.Count -ge 2) { break }
        }
        if ($batchNames.Count -lt 2) { throw '需要至少两个不同的可见程序来测试批量保护' }
        foreach ($batchRow in $batchRows) { $batchRow.Cells[0].Value = $true }

        $batchWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $script:ProtectSelectedButton.PerformClick()
        $batchWatch.Stop()
        $batchNamesCount = $batchNames.Count
        $batchNamesArray = @($batchNames.ToArray())
        $configNames = @($script:Config.protectedProcesses | ForEach-Object { Normalize-ProcessName $_ })
        $batchProtectionOk = $true
        foreach ($batchName in $batchNamesArray) {
            if ($configNames -notcontains $batchName) { $batchProtectionOk = $false }
        }
        foreach ($candidate in $script:Grid.Rows) {
            if ($null -eq $candidate.Tag -or $batchNamesArray -notcontains (Normalize-ProcessName $candidate.Tag.Name)) { continue }
            if (-not $candidate.Tag.Protected -or -not $candidate.Cells[0].ReadOnly -or $candidate.Cells[0].Value -eq $true -or $candidate.Cells[5].Value -ne '已保护') {
                $batchProtectionOk = $false
            }
        }

        $actionElapsed = [Math]::Max([Math]::Max($protectWatch.ElapsedMilliseconds, $unprotectWatch.ElapsedMilliseconds), $batchWatch.ElapsedMilliseconds)
    } catch {
        $testError = $_.Exception.Message
    } finally {
        if ($null -ne $script:Form) { try { $script:Form.Hide(); $script:Form.Dispose() } catch { } }
        $script:Config = $originalConfig
        $script:ConfigPath = $originalConfigPath
        if (Test-Path -LiteralPath $testConfigRoot -PathType Container) {
            try { Remove-Item -LiteralPath $testConfigRoot -Recurse -Force } catch { }
        }
    }

    if ($null -ne $testError) {
        Write-Host "FAIL：$testError"
        exit 1
    }
    $verdict = 'PASS'
    if (-not $protectedAfterAction -or -not $protectedRowsOk) { $verdict = 'FAIL：保护菜单操作未正确更新名单或界面' }
    if (-not $unprotectedAfterAction) { $verdict = 'FAIL：取消保护菜单操作未正确恢复界面' }
    if (-not $batchProtectionOk) { $verdict = 'FAIL：批量保护未正确更新名单或界面' }
    if ($actionElapsed -ge 250) { $verdict = 'FAIL：保护操作同步阻塞超过 250ms' }
    Write-Host ("{0}：单项保护/取消与批量保护 {1} 个程序通过，单次最大耗时 {2}ms" -f $verdict, $batchNamesCount, $actionElapsed)
    if ($verdict -ne 'PASS') { exit 1 }
    exit 0
}

if ($RefreshTest) {
    try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
    $timer = $null
    $testError = $null
    $scanMs = 0L
    $refreshCallMs = 0L
    $modeSequenceMs = 0L
    $maxUiGapMs = 0L
    $baselineRows = 0
    $refreshedRows = 0
    $oldRowsPreserved = $false
    $modeChangeQueued = $false
    $script:RefreshTestWatch = $null
    $script:RefreshTestLastTick = 0L
    $script:RefreshTestMaxGap = 0L

    try {
        Initialize-Ui
        $script:Form.Opacity = 0
        $script:Form.Show()
        [System.Windows.Forms.Application]::DoEvents()
        Refresh-AppList -Mode 'windows'
        Wait-ForAppRefresh -TimeoutSeconds 30
        $baselineRows = $script:Grid.Rows.Count
        if ($baselineRows -eq 0) { throw '初次扫描没有候选程序，无法进行刷新测试' }

        $timer = New-Object System.Windows.Forms.Timer
        $timer.Interval = 20
        $timer.Add_Tick({
            $now = $script:RefreshTestWatch.ElapsedMilliseconds
            if ($script:RefreshTestLastTick -gt 0) {
                $gap = $now - $script:RefreshTestLastTick
                if ($gap -gt $script:RefreshTestMaxGap) { $script:RefreshTestMaxGap = $gap }
            }
            $script:RefreshTestLastTick = $now
        })
        $script:RefreshTestWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $timer.Start()
        for ($warm = 0; $warm -lt 3; $warm++) {
            Start-Sleep -Milliseconds 30
            [System.Windows.Forms.Application]::DoEvents()
        }
        $script:RefreshTestWatch.Restart()
        # Use a nonzero baseline before entering the blocking refresh so the first delayed tick is measured.
        $script:RefreshTestLastTick = 1L
        $script:RefreshTestMaxGap = 0L

        $script:CurrentMode = 'windows'
        $sequenceWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $refreshWatch = [System.Diagnostics.Stopwatch]::StartNew()
        Refresh-AppList -Mode 'windows' -Force
        $refreshWatch.Stop()
        $refreshCallMs = $refreshWatch.ElapsedMilliseconds
        $oldRowsPreserved = ($script:Grid.Rows.Count -eq $baselineRows)

        # Queue a mode change while the window-only scan is still running.
        $script:CurrentMode = 'all'
        Refresh-AppList -Mode 'all'
        $modeChangeQueued = ($null -ne $script:PendingRefresh -and $script:PendingRefresh.Mode -eq 'all')
        Wait-ForAppRefresh -TimeoutSeconds 45
        $sequenceWatch.Stop()
        $modeSequenceMs = $sequenceWatch.ElapsedMilliseconds

        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 30
        [System.Windows.Forms.Application]::DoEvents()
        $timer.Stop()

        $scanMs = $script:LastRefreshDurationMs
        $maxUiGapMs = $script:RefreshTestMaxGap
        $refreshedRows = $script:Grid.Rows.Count
    } catch {
        $testError = $_.Exception.Message
    } finally {
        if ($null -ne $timer) { try { $timer.Stop(); $timer.Dispose() } catch { } }
        if ($null -ne $script:Form) { try { $script:Form.Hide(); $script:Form.Dispose() } catch { } }
    }

    if ($null -ne $testError) {
        Write-Host "FAIL：$testError"
        exit 1
    }
    $verdict = 'PASS'
    if ($refreshCallMs -ge 250) { $verdict = 'FAIL：启动刷新同步阻塞超过 250ms' }
    if ($script:LastRefreshRenderMs -ge 250) { $verdict = 'FAIL：列表替换同步阻塞超过 250ms' }
    if ($maxUiGapMs -ge 750) { $verdict = 'FAIL：刷新期间 UI 消息循环停顿超过 750ms' }
    if (-not $oldRowsPreserved) { $verdict = 'FAIL：扫描完成前旧清单被清空或替换' }
    if (-not $modeChangeQueued -or $script:CurrentMode -ne 'all') { $verdict = 'FAIL：扫描期间切换模式未排队并应用' }
    if ($refreshedRows -eq 0) { $verdict = 'FAIL：刷新后候选列表为空' }
    if ($script:LastRefreshSucceeded -ne $true) { $verdict = 'FAIL：后台扫描未成功完成' }
    Write-Host ("{0}：刷新调用 {1}ms，窗口→后台模式切换总耗时 {2}ms，最后扫描 {3}ms，行渲染 {4}ms，UI 最大消息间隔 {5}ms，旧清单保留={6}，刷新前/后行数 {7}/{8}" -f $verdict, $refreshCallMs, $modeSequenceMs, $scanMs, $script:LastRefreshRenderMs, $maxUiGapMs, $oldRowsPreserved, $baselineRows, $refreshedRows)
    if ($verdict -ne 'PASS') { exit 1 }
    exit 0
}

if ($TaskbarTest) {
    try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
    $testError = $null
    $normalEligible = $false
    $toolExcluded = $false
    $filterDefault = $false
    $filterShowAll = $false

    try {
        Initialize-Ui
        $script:Form.Opacity = 0
        $script:Form.Show()
        [System.Windows.Forms.Application]::DoEvents()

        $normalForm = New-Object System.Windows.Forms.Form
        $normalForm.Text = 'OneClickClose Taskbar Test'
        $normalForm.ShowInTaskbar = $true
        $normalForm.Opacity = 0
        $normalForm.Show()
        [System.Windows.Forms.Application]::DoEvents()
        $normalEligible = [NativeMethods]::IsTaskbarEligibleWindow($normalForm.Handle)
        $normalForm.Hide()
        $normalForm.Dispose()

        $toolForm = New-Object System.Windows.Forms.Form
        $toolForm.Text = 'OneClickClose Tool Test'
        $toolForm.ShowInTaskbar = $false
        $toolForm.Opacity = 0
        $toolForm.Show()
        [System.Windows.Forms.Application]::DoEvents()
        $toolExcluded = -not [NativeMethods]::IsTaskbarEligibleWindow($toolForm.Handle)
        $toolForm.Hide()
        $toolForm.Dispose()

        $script:Grid.Rows.Clear()
        $taskbarRowIndex = $script:Grid.Rows.Add()
        $taskbarRow = $script:Grid.Rows[$taskbarRowIndex]
        $taskbarRow.Tag = [PSCustomObject]@{
            DisplayName = 'Taskbar App'
            Name = 'taskbar-app'
            Titles = 'Taskbar'
            IsTaskbarApp = $true
            Protected = $false
            Kind = '窗口'
        }
        $taskbarRow.Cells[2].Value = 'Taskbar App'

        $toolRowIndex = $script:Grid.Rows.Add()
        $toolRow = $script:Grid.Rows[$toolRowIndex]
        $toolRow.Tag = [PSCustomObject]@{
            DisplayName = 'Tool App'
            Name = 'tool-app'
            Titles = 'Tool'
            IsTaskbarApp = $false
            Protected = $false
            Kind = '窗口'
        }
        $toolRow.Cells[2].Value = 'Tool App'

        $script:ShowAllWindowsCheck.Checked = $false
        Apply-Filter
        $filterDefault = $taskbarRow.Visible -and (-not $toolRow.Visible)

        $script:ShowAllWindowsCheck.Checked = $true
        Apply-Filter
        $filterShowAll = $taskbarRow.Visible -and $toolRow.Visible
    } catch {
        $testError = $_.Exception.Message
    } finally {
        if ($null -ne $script:ShowAllWindowsCheck) { try { $script:ShowAllWindowsCheck.Checked = $false } catch { } }
        if ($null -ne $script:Form) { try { $script:Form.Hide(); $script:Form.Dispose() } catch { } }
    }

    if ($null -ne $testError) {
        Write-Host "FAIL：$testError"
        exit 1
    }
    $verdict = 'PASS'
    if (-not $normalEligible) { $verdict = 'FAIL：普通窗口未被识别为任务栏程序' }
    if (-not $toolExcluded) { $verdict = 'FAIL：工具窗口未被排除' }
    if (-not $filterDefault) { $verdict = 'FAIL：默认过滤未隐藏非任务栏程序' }
    if (-not $filterShowAll) { $verdict = 'FAIL：勾选显示所有窗口后未恢复全部行' }
    Write-Host ("{0}：普通窗口={1}，工具窗口排除={2}，默认过滤={3}，显示全部={4}" -f $verdict, $normalEligible, $toolExcluded, $filterDefault, $filterShowAll)
    if ($verdict -ne 'PASS') { exit 1 }
    exit 0
}

if ($UiTest) {
    try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
    Initialize-Ui
    $script:Form.Opacity = 0
    $script:Form.Show()
    [System.Windows.Forms.Application]::DoEvents()
    Refresh-AppList -Mode $script:CurrentMode
    Wait-ForAppRefresh -TimeoutSeconds 30
    $count = $script:Grid.Rows.Count
    $shotPath = Join-Path $script:BaseDir 'ui-test.png'
    try {
        $bitmap = New-Object System.Drawing.Bitmap -ArgumentList @([int]$script:Form.Width, [int]$script:Form.Height)
        $rect = New-Object System.Drawing.Rectangle -ArgumentList @(0, 0, [int]$script:Form.Width, [int]$script:Form.Height)
        $script:Form.DrawToBitmap($bitmap, $rect)
        $bitmap.Save($shotPath, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        Write-Host "UI 构建成功，列表行数: $count，截图: $shotPath"
    } catch {
        Write-Host "UI 构建成功，列表行数: $count（截图失败：$($_.Exception.Message)）"
    }
    $script:Form.Hide()
    $script:Form.Dispose()
    exit 0
}

# 单实例
$script:Mutex = New-Object System.Threading.Mutex($false, 'Local\OneClickClose_SingleInstance')
$ownsMutex = $false
try {
    $ownsMutex = $script:Mutex.WaitOne(0, $false)
} catch {
    $ownsMutex = $false
}
if (-not $ownsMutex) {
    [void][System.Windows.Forms.MessageBox]::Show('清场助手已经在运行了（请看系统托盘）。', '清场助手', 'OK', 'Information')
    exit 0
}

Initialize-Ui
Initialize-Tray
Initialize-Hotkey

$script:Context = New-Object System.Windows.Forms.ApplicationContext

if (-not $Hidden) {
    Show-Picker
} else {
    $script:StatusLabel.Text = "已驻留托盘 | 热键 $($script:Config.hotkey)"
}

[System.Windows.Forms.Application]::Run($script:Context)
