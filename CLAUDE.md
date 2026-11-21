# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概览

清场助手（OneClickClose）是 Windows 10/11 x64 的 WinForms 批量关闭工具：扫描任务栏/可见窗口，按可执行文件分组，支持“只关窗口”和“彻底退出”两种模式，带保护名单、执行前预览和两组全局热键。

- 主程序：`src/OneClickClose.App`（C# 7.3 / .NET Framework 4.8 / WinForms，x64）
- 测试：`tests/OneClickClose.Tests`（xUnit，net48）；`tests/OneClickClose.TestWindow` 是测试辅助窗口程序
- 安装包：`installer/OneClickClose.iss`（Inno Setup 6，按当前用户安装）
- 根目录 `OneClickClose.ps1` 是维护冻结的 PowerShell 5.1 旧版，只接受关键修复；新功能只进原生版

## 常用命令

仓库没有 `.sln`，直接指定 csproj。需要 .NET SDK 10；`net48` 参考程序集由 NuGet 自动还原。

```powershell
dotnet build .\src\OneClickClose.App\OneClickClose.App.csproj -c Release
dotnet test .\tests\OneClickClose.Tests\OneClickClose.Tests.csproj -c Release
```

运行单个测试类或测试方法：

```powershell
dotnet test .\tests\OneClickClose.Tests\OneClickClose.Tests.csproj -c Release --filter "FullyQualifiedName~CloseServiceTests"
dotnet test .\tests\OneClickClose.Tests\OneClickClose.Tests.csproj -c Release --filter "FullyQualifiedName=OneClickClose.Tests.CloseServiceTests.PreviewContainsBothPidsAndCancelSendsNothing"
```

测试会启动真实 GUI 进程（如 `charmap.exe`）并枚举窗口，依赖 Windows 桌面会话，不能当作跨平台/无头单元测试运行。

本地运行主程序：

```powershell
dotnet run --project .\src\OneClickClose.App\OneClickClose.App.csproj
dotnet run --project .\src\OneClickClose.App\OneClickClose.App.csproj -- --hidden
```

打包便携版与安装包：

```powershell
dotnet publish .\src\OneClickClose.App\OneClickClose.App.csproj -c Release -o .\dist\app -p:DebugType=None -p:DebugSymbols=false
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" .\installer\OneClickClose.iss
```

`scripts/sign.ps1` 需要证书；没有证书会明确报错。

PowerShell 旧版（维护冻结）的回归测试，其余 `-*Test` 参数见 README：

```powershell
powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File .\OneClickClose.ps1 -SelfTest
```

## 架构

### 启动与单实例

`Program.Main`（`src/OneClickClose.App/Program.cs`）负责：

- 用 `Local\OneClickClose.Native.SingleInstance` 互斥量保证单实例；第二个实例通过 `Local\OneClickClose.Native.ShowPanel` 事件唤醒已有窗口，不再启动新进程。
- 从 EXE 同目录读取 `config.json`，并调用 `StartupManager.MigrateLegacyShortcut()` 迁移旧的“一键收摊”开机启动项。
- 带 `--hidden` 时用 `ApplicationContext` 常驻托盘；否则正常显示主窗体。
- 未处理异常追加到 EXE 同目录的 `OneClickClose-error.log`。

### 扫描 → 过滤 → 预览 → 关闭

1. `ProcessScanner.Scan` 枚举可见窗口和进程，排除其他会话、系统关键进程和本程序父进程链，再按可执行文件路径分组（`GroupCandidates`）；无路径的进程按 PID 独立成组。“只关窗口”模式只保留有窗口的进程；“彻底退出”模式额外纳入当前用户会话内的 GUI 子系统后台进程（读取 PE 头判断）。
2. `CandidateFilter.Matches` 负责视图过滤：默认只显示任务栏程序，`showAllWindows` 时显示全部，`includeSuspendedUwp` 时保留挂起的 UWP。
3. `MainForm` 手工构建 UI 并维护勾选状态：未保护项默认勾选，但用户手动取消的项在刷新后必须保留。
4. `CloseService.CreateTargets` 在动手前重新读取实时进程，复核 PID、进程名和路径，受保护或已变化的进程标记为跳过；需要时扩展子进程。
5. `CloseService.ExecuteAsync` 在 `all` 模式下先弹预览；有窗口的进程先发 `WM_CLOSE`，超过 `gracefulTimeoutSeconds` 再强杀；无窗口进程确认后直接结束。`windows` 模式只发 `WM_CLOSE`，不提供强杀。执行阶段还会复核进程启动时间，降低 PID 复用误杀。

### 保护规则

- `ProcessSafety.HardProtectedNames`：Windows 关键进程、安全组件和输入法；`applicationframehost` 仅在“彻底退出”模式下加入硬保护。
- 用户保护：`AppConfig.ProtectedProcesses`（进程名）与 `ProtectedPaths`（完整路径）叠加生效。
- 保护检查在扫描、目标构建、子进程扩展三处分别执行；新增关闭路径时必须保持同样的检查。

### 配置与 UI

- `ConfigStore` 读写 EXE 同目录的 `config.json`，schema v2，旧配置自动迁移。保存采用临时文件 + `File.Replace` 原子替换；`Save()` 默认轮换 `config.json.bak`，高频视图偏好用 `Save(false)`，避免冲掉有意义的备份。恢复备份前会先写 `config.json.pre-restore-<时间戳>-<随机>`。
- `MainForm` 是纯代码构建的 WinForms UI（无 Designer 文件），包含主列表、托盘、两组全局热键，以及设置/预览/窗口选择对话框。热键通过 `NativeMethods.RegisterHotKey` 注册，修改后立即重注册；注册失败会回滚旧热键。
- `HotkeyParser` 把 `Ctrl+Alt+X` 解析为 Win32 修饰键和虚拟键，支持 `A-Z`、`0-9`、`F1-F24`。
- `StartupManager` 在用户启动文件夹写 `清场助手.lnk`，参数为 `--hidden`；优先用 `WScript.Shell`，无 COM 环境下退化为重命名旧快捷方式。

## 改动约定

- 保持 C# 7.3 语法（项目未启用更高 `LangVersion`），不要引入依赖 .NET Core / .NET 5+ API 的代码，目标框架是 `net48`。
- 涉及关闭行为的改动必须同步补 `tests/OneClickClose.Tests` 的 xUnit 测试；CI 会执行 `dotnet test`、Release publish、便携 ZIP 打包和 Inno Setup 编译。
- 发版时版本号需要同步：`app.manifest` 的 `assemblyIdentity`、`installer/OneClickClose.iss` 的 `MyAppVersion`/`VersionInfoVersion`、`.github/workflows/build.yml` 里的 ZIP 文件名。
- 不要在程序运行时手工编辑 `config.json`，程序下次保存会覆盖外部修改。
