# 清场助手（OneClickClose）

[![build](https://github.com/baozifei123/OneClickClose/actions/workflows/build.yml/badge.svg)](https://github.com/baozifei123/OneClickClose/actions/workflows/build.yml)

Windows 上的批量关闭小工具：按热键弹出当前程序清单，勾选后一键关闭；需要时可以把没有窗口、躲在托盘或后台的程序一起彻底退出。

## 下载

- [GitHub Releases](https://github.com/baozifei123/OneClickClose/releases)
- 推荐：`OneClickClose-Setup-1.0.0.exe`（按用户安装，中文向导，可选开机启动）
- 便携：`OneClickClose-Portable-1.0.0.zip`（解压后运行 `OneClickClose.exe`）

项目现在包含两个版本：

- **原生 C# WinForms 版（推荐，持续维护）**：`src/OneClickClose.App`，目标 .NET Framework 4.8，无需额外运行时，可制作按用户安装包。
- **PowerShell 5.1 版（维护冻结）**：`OneClickClose.ps1` + `Start-Hidden.vbs`，仅作为回退和调试入口；后续新功能（进程树、设置面板、逐窗口选择等）只在原生版实现，PowerShell 版只接受关键修复。

> 本版把用户可见名称从“一键收摊”改为“清场助手”；EXE 仍为 `OneClickClose.exe`，安装器 AppId、配置路径和单实例名不变。首次启动会把旧的 `一键收摊.lnk` 开机启动项迁移为 `清场助手.lnk`，安装器升级时清理旧的开始菜单/桌面快捷方式，保护名单和预设不会丢失。
>
> 如果旧版已经设置开机启动，建议先安装并运行新版；如果直接运行便携版，迁移后的开机启动会指向便携版所在目录。需要固定到安装目录时，可在设置里关闭再开启开机启动。

## 快速开始（原生版）

### 安装包

运行 `dist\installer\OneClickClose-Setup-1.0.0.exe`：

- 按当前用户安装到 `%LOCALAPPDATA%\Programs\OneClickClose`，不需要管理员权限。
- 安装向导为简体中文；可选创建桌面快捷方式和“开机自动启动（隐藏到托盘）”。安装后也可以在“设置”里随时开关开机启动。
- 卸载时保留 `config.json`，不会删除个人保护名单和预设。

### 便携版

复制 `dist\app` 整个目录到任意可写位置，运行 `OneClickClose.exe`。配置文件生成在 EXE 同目录。

### 使用

1. 启动后按 `Ctrl + Alt + X`，或左键单击托盘图标，打开清单；再按一次热键会隐藏面板。
2. 勾选要关闭的程序，选择模式，点“关闭选中”。
3. “彻底退出（含后台）”默认同时结束选中程序启动的子进程；顶栏“包含子进程”可关闭该行为。
4. “只关窗口”模式下，右键程序 → “选择要关闭的窗口...” 可以只对部分窗口发送 `WM_CLOSE`。
5. 点“演练”可以在不发送任何关闭请求的情况下查看逐项目标、子进程、权限和动作。
6. 需要固定保留的程序：勾选后点“保护选中”，或右键 → “加入保护名单（永不关闭）”；也可按完整路径保护。
7. 点“设置”或托盘 → “设置”可修改热键、优雅关闭超时、默认视图、子进程默认值、挂起 UWP 显示和开机启动。
8. 如果列表里出现“权限：管理员”，而当前不是管理员，可从托盘 → “以管理员身份重启”（会触发 UAC）。
9. `Esc` 或右上角 X 只是隐藏；真正退出请右键托盘图标 → “退出”。
10. 启动参数 `--hidden`：只驻留托盘，不显示面板；第二次启动会唤醒已运行的实例，而不是弹“已在运行”。

## 它能做什么

- 全局热键唤出/隐藏清单，按程序分组显示图标、进程名、窗口数、窗口标题和权限标记。
- **默认只显示底部任务栏上有按钮的程序**；勾选顶部“显示所有窗口”可恢复显示所有可见窗口和后台项；设置里还可选择显示挂起的 UWP。
- 刷新扫描在后台线程执行；刷新期间旧清单仍可见，已勾选状态和逐窗口选择会按程序路径尽量保留。
- 同一可执行文件路径的进程归为一项，勾选后处理组内全部实例；路径未知的进程按 PID 单独显示。
- 两种关闭模式：
  - **只关窗口（保留托盘）**：向可见窗口发送 `WM_CLOSE`，等效于点 X；程序愿意留在托盘就留着。可逐窗口选择只关闭部分窗口。
  - **彻底退出（含后台）**：确认前用表格列出具体应用、进程名、PID、窗口标题、子进程和将执行的关闭/强杀动作；可逐项取消勾选，也可以只演练不执行。确认后先请求关闭，超时再强杀。
- **子进程**：“彻底退出”默认包含选中程序启动的子进程，避免只关主进程、后台助手继续运行；子进程会单独列出，可在预览里取消。系统关键进程、本程序父进程链不会被子进程扩展带入。
- **管理员权限**：非管理员运行时，权限列会标记“管理员”；可从托盘一键以管理员身份重启。管理员目标仍会重新核对启动时间和身份，降低 PID 复用误杀风险。
- 保护名单：系统关键进程、脚本自身的父进程链不会出现在清单里；可右键单个加入/移出，也可勾选多个后点“保护选中”批量加入；还支持按完整可执行文件路径保护。
- 可保存、应用和删除命名保护预设；每次配置保存自动更新 `config.json.bak`，也可从界面恢复。视图类偏好使用不轮换备份的轻量保存，不会冲掉有意义的配置备份。
- 设置面板：热键、优雅关闭等待秒数、默认显示所有窗口、彻底退出是否含子进程、是否显示挂起 UWP、开机启动。
- 终端警告：勾选项里包含终端窗口时，状态栏变红提示。
- 执行关闭前会复核 PID、进程名、可执行文件路径、进程启动时间；子进程还会复核父进程链，降低 PID 被复用后误关其他程序的风险。

## 两种模式的区别

### 只关窗口（保留托盘）

- 对每个可见窗口调用 `WM_CLOSE`，等效于点窗口右上角的 X。
- 程序可以选择保存后退出，也可以选择只隐藏到托盘继续运行。
- 不会强制杀进程，不会丢未保存数据。
- 如果程序忽略关闭请求，结果列会显示“窗口未响应”。

### 彻底退出（含后台）

- 先向可见窗口发 `WM_CLOSE`，给程序 `gracefulTimeoutSeconds` 秒（默认 5 秒）保存和退出。
- 超时后仍未退出的进程，执行强制结束（`Process.Kill()`）。
- 没有可见窗口的后台程序没有优雅关闭通道，会在确认后直接强制结束。
- 默认同时结束选中程序启动的子进程（如 Electron/Chromium 的渲染器、更新器、助手进程）；可在顶栏或设置里关闭“包含子进程”。子进程会单独列出并计入预览。
- 执行前会用表格逐项显示本次目标的应用名、进程名、PID、窗口标题、权限，以及“发送 `WM_CLOSE` 后等待并强杀”或“确认后直接强杀”等动作；可取消勾选个别目标，或勾选“仅演练，不执行”。取消时不会发送关闭消息。
- 未保存内容可能丢失；只对你确认的程序使用此模式。

## 保护名单

默认硬保护（不会出现在清单中）：

- Windows 关键进程：`explorer`、`dwm`、`csrss`、`winlogon`、`services`、`lsass`、`svchost`、`sihost`、`ctfmon`、`TextInputHost` 等。
- 安全/系统组件：Defender 相关进程、`RuntimeBroker`、`dllhost`、`LockApp`、`OpenConsole`、音频/显卡容器等。
- 输入法：搜狗、QQ 拼音、百度拼音、微信输入法等。
- 本程序自身的整条父进程链（防止把自己或启动它的终端关掉）。

需要临时放过某个程序（例如 Listary、Snipaste、PowerToys）：

- 在清单里右键该程序 → “加入保护名单（永不关闭）”。
- 保护中的程序会以灰色显示，右键可“移出保护名单”。
- 需要只保护某个安装位置时，右键 → “按路径加入保护”；同名但不同路径的程序不会被一起保护。路径规则在 `config.json` 的 `protectedPaths` 中。
- `config.json` 里的 `protectedProcesses` 也可以直接编辑，写进程名即可（如 `"WeChat"`、`"DingTalk"`，带不带 `.exe` 都行）。

路径保护规则不包含在命名预设里：预设只保存/替换进程名列表，路径规则独立保留。应用预设不会关闭任何程序。

### 命名保护预设

工具带提供保护预设下拉框及“应用”“保存当前名单”“删除”“恢复备份”按钮：

- **保存当前名单**：输入预设名称，将当前 `protectedProcesses` 保存为一个可复用名单；覆盖同名预设会再次确认。
- **应用**：确认后用该预设替换当前用户保护名单；只改变保护规则，不会关闭任何程序。
- **删除**：只删除预设，不改变当前保护名单。
- 手工加入/移出保护名单后，当前预设关联会清除；需要时可再保存为新预设。

系统硬保护和父进程链不受用户预设影响。

## 配置与备份

原生版和 PowerShell 版使用同一份 schema：

```json
{
  "schemaVersion": 2,
  "hotkey": "Ctrl+Alt+X",
  "gracefulTimeoutSeconds": 5,
  "protectedProcesses": [],
  "protectedPaths": [],
  "lastMode": "windows",
  "profiles": {
    "工作": ["teams", "outlook"]
  },
  "activeProfile": "工作",
  "showAllWindows": false,
  "includeChildProcesses": true,
  "includeSuspendedUwp": false
}
```

- `schemaVersion`：当前为 `2`。旧格式（不含该字段、`profiles`）仍可读取；第一次保存时才写入 v2。新增字段都是可选的，旧配置缺省时使用安全默认值。
- `hotkey`：支持 `Ctrl` / `Alt` / `Shift` / `Win` 组合，主键支持 `A-Z`、`0-9`、`F1-F24`。例如 `"Ctrl+Shift+Q"`。
- `gracefulTimeoutSeconds`：优雅关闭的等待秒数，1–60。
- `protectedProcesses`：当前用户保护名单，进程名数组；读取旧版配置时会原样保留。
- `protectedPaths`：按完整可执行文件路径保护；与进程名规则叠加生效，不包含在命名预设中。
- `lastMode`：上次使用的模式，`windows` 或 `all`。
- `profiles`：预设名称到进程名数组的映射。
- `activeProfile`：当前关联预设的名称；手工修改保护名单或路径规则后会清空。
- `showAllWindows`：启动时是否默认显示所有窗口；顶栏切换会即时保存。
- `includeChildProcesses`：彻底退出时是否默认包含子进程；默认 `true`。
- `includeSuspendedUwp`：是否在任务栏视图中显示挂起的 UWP 应用；默认 `false`。

原生版默认把 `config.json` 放在 EXE 同目录。每次保存新配置前，旧文件都会自动写入/轮换为同目录的 `config.json.bak`。视图开关这类高频偏好使用轻量保存，只原子更新 `config.json`，不轮换 `.bak`，避免把保护名单备份冲掉。旧格式配置在首次保存升级时也会作为备份保留。修改热键后原生版会重新注册，无需重启；PowerShell 版需要退出并重启。

### 恢复配置备份

1. 点击工具带的“恢复备份”。没有 `config.json.bak` 时按钮不可用；先保存一次名单/预设即可生成备份。
2. 确认恢复。程序先把当前配置保存为 `config.json.pre-restore-<时间戳>-<随机标识>`，再从 `.bak` 恢复。
3. 恢复后的名单/预设立即读入；原生版会重新注册热键，PowerShell 版需要退出并重启。
4. 恢复点文件不会自动删除，可在程序退出后按需手动清理。

不要在程序运行时同时手工编辑配置文件，以免下一次保存覆盖外部更改。

## 从 PowerShell 版迁移

- 把旧版 `config.json` 复制到原生版 EXE 同目录即可；schema v2 直接读取，新增字段使用默认值。
- 原生版使用独立的单实例互斥量，可以和 PowerShell 版同时打开做对比测试，但两个版本不能同时注册同一个全局热键。
- PowerShell 版已进入维护冻结：它仍能读取/保存旧字段，并会原样保留新增的原生版字段，但不会使用这些新功能。确认原生版稳定后，可以删除 `Start-Hidden.vbs` 和旧快捷方式。

## 文件

| 文件 | 作用 |
| --- | --- |
| `src/OneClickClose.App/` | 原生 C# WinForms 主程序 |
| `tests/OneClickClose.Tests/` | 原生版 xUnit 回归测试 |
| `installer/OneClickClose.iss` | Inno Setup 按用户安装包脚本 |
| `installer/ChineseSimplified.isl` | 安装器简体中文语言文件（来自 Inno Setup 官方仓库） |
| `scripts/sign.ps1` | 代码签名/验证脚本（需要证书） |
| `.github/workflows/build.yml` | GitHub Actions：测试、发布、打包、可选签名 |
| `OneClickClose.ps1` | PowerShell 5.1 版主程序（维护冻结） |
| `Start-Hidden.vbs` | PowerShell 版无窗口启动 |
| `config.json` | 用户设置与保护名单；首次运行自动生成 |
| `config.json.bak` | 每次配置保存前自动保留的上一份配置 |
| `config.json.pre-restore-*` | 手动恢复备份前，自动保留的当前配置快照 |

## 构建与测试

需要：

- Windows 10/11 x64。
- .NET SDK 10（用于编译 `net48`，会自动还原 .NET Framework 4.8 参考程序集）。
- .NET Framework 4.8（Windows 10/11 自带）。
- Inno Setup 6（仅制作安装包时需要）。

```powershell
dotnet build .\src\OneClickClose.App\OneClickClose.App.csproj -c Release
dotnet test .\tests\OneClickClose.Tests\OneClickClose.Tests.csproj -c Release

dotnet publish .\src\OneClickClose.App\OneClickClose.App.csproj `
  -c Release -o .\dist\app `
  -p:DebugType=None -p:DebugSymbols=false

& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" .\installer\OneClickClose.iss
```

产物：

- `dist\app\OneClickClose.exe`：便携版入口（同目录 DLL 必须一起保留）。
- `dist\installer\OneClickClose-Setup-1.0.0.exe`：按用户安装包。

原生版测试覆盖：

- 旧配置迁移、schema v2、新视图字段默认值、预设保存/应用/删除、路径保护、备份轮换、轻量保存不轮换备份、恢复点、保存失败回滚。
- Toolhelp 进程元数据（当前进程路径/父 PID）、权限判断、进程树祖先/后代/环检测。
- 按路径分组、未知路径 PID 独立、保护标记、排序、挂起 UWP/权限标记聚合。
- 任务栏窗口资格（普通窗口入选、工具窗口排除）、组内任一窗口入选、默认过滤、“显示所有窗口”和“包含挂起 UWP”开关。
- 热键解析与回退。
- 两个 charmap 测试进程的同组预览、取消不关闭、确认后关闭。
- 只执行预览中勾选的目标、演练不关闭、逐窗口目标过滤、启动时间不匹配跳过、子进程扩展与关闭。
- 设置/预览/窗口选择窗体构建、离屏 WinForms 扫描与列表构建冒烟测试。

### 代码签名

`scripts/sign.ps1` 使用 Windows SDK 的 `signtool.exe` 对 `dist\app\OneClickClose.exe` 和安装包签名并验证：

```powershell
# 使用当前用户证书存储中的证书
.\scripts\sign.ps1 -CertificateThumbprint <THUMBPRINT>

# 或使用 PFX；密码建议放在环境变量，避免出现在命令行历史
$env:ONECLICKCLOSE_PFX_PASSWORD = '<password>'
.\scripts\sign.ps1 -PfxPath .\cert.pfx
```

没有代码签名证书时脚本会明确报错；仓库不包含任何证书或密码。正式分发前应购买受信任的代码签名证书，并持续积累 SmartScreen 信誉。

### CI

`.github/workflows/build.yml` 在 Windows runner 上执行 `dotnet test`、Release publish、便携 ZIP、Inno Setup 编译，并上传 `dist` 产物；配置 `SIGNING_CERTIFICATE_BASE64` / `SIGNING_CERTIFICATE_PASSWORD` secrets 后会自动调用签名脚本。当前仓库尚未配置远程，工作流在推送后生效。

PowerShell 版回归命令（需要 Windows PowerShell 5.1，WinForms 测试建议加 `-STA`）：

```powershell
powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File .\OneClickClose.ps1 -SelfTest
powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File .\OneClickClose.ps1 -RefreshTest
powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File .\OneClickClose.ps1 -SafetyTest
powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File .\OneClickClose.ps1 -ConfigTest
powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File .\OneClickClose.ps1 -TaskbarTest
powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File .\OneClickClose.ps1 -ProtectRefreshTest
powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File .\OneClickClose.ps1 -CloseTest
powershell.exe -STA -NoProfile -ExecutionPolicy Bypass -File .\OneClickClose.ps1 -UiTest
```

## 已知限制

- **托盘图标没有官方枚举 API**。模式二用“当前用户会话 + GUI 子系统 + 父进程过滤”来识别后台程序，不直接枚举托盘图标；个别驱动/助手进程可能出现在列表里，加入保护名单即可。
- **任务栏过滤是窗口样式启发式**，不是读取任务栏按钮本身：会排除工具窗口、owner 窗口和 DWM cloaked 窗口。被最小化/挂起的 UWP 应用（如“设置”）默认可能不显示；勾选顶部“显示所有窗口”，或在设置里打开“显示挂起的 UWP”即可查看。
- **UWP 应用**的窗口由 `ApplicationFrameHost` 托管。模式一可以正常关闭这些窗口；模式二会保护该宿主进程，避免一次杀掉所有 UWP 应用。
- **管理员权限的程序**：非管理员运行时，权限列会标记“管理员”，`WM_CLOSE` 和强杀都可能被 UIPI 拦截，结果列会显示“失败/权限不足”。可从托盘 → “以管理员身份重启”触发 UAC 后重试。
- **子进程**：默认包含选中程序的子进程；这会更彻底，但也可能结束终端里正在运行的任务、更新器或辅助进程。预览表会逐项列出，可取消勾选或关闭“包含子进程”。
- **程序忽略 `WM_CLOSE`**：模式一不会强杀，结果列显示“窗口未响应”；改用模式二。
- **保存时间较长的程序**：把 `gracefulTimeoutSeconds` 调大，避免在保存完成前被强杀。
- 本工具不会替你保存文档；强制结束的后果需要自行承担。
- 若某个程序把窗口做成无标题/工具窗口，可能识别不到；目前只处理有标题的可见顶层窗口。
- 原生版未做代码签名；从网络下载后运行可能触发 SmartScreen 提示。正式分发前建议使用代码签名证书。
- 便携版把配置写在 EXE 同目录；如果放在 `Program Files` 等不可写目录，保存会失败。请使用按用户安装包，或把程序放到可写目录。

## PowerShell 版（维护冻结）

PowerShell 版保留为回退和调试入口，不再接收新功能；原生版的新功能（进程树、设置面板、逐窗口选择、演练、挂起 UWP、路径保护、管理员重启等）不会同步到 PowerShell 版。

日常隐藏运行：双击 `Start-Hidden.vbs`；或直接运行 `OneClickClose.ps1` 显示面板。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\OneClickClose.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\OneClickClose.ps1 -Hidden
```

PowerShell 版仍支持任务栏过滤、异步刷新、保护预设、配置备份和原有回归测试；热键和超时在启动时读取，恢复备份后需要重启。它会保留原生版写入的新配置字段，但不会使用这些字段。

## 未来 EXE 分发建议

当前已提供原生 .NET Framework 4.8 版本、简体中文 Inno Setup 安装包和 CI/签名脚本。后续可在功能稳定后补充：

- 准备受信任的代码签名证书，并把证书/密码配置为 CI secrets；仓库已提供 `scripts/sign.ps1` 和可选签名步骤。
- 可选自动更新（例如 Velopack）。
- 如果需要脱离 .NET Framework，再评估迁移到 .NET 10 + 自包含发布；代价是体积更大或需要安装桌面运行时。

## 设计参考

实现时参考了以下开源项目的思路（未复制无许可代码）：

- [voolyvex/pcpow](https://github.com/voolyvex/pcpow)（MIT）：优雅关闭 → 超时 → 强杀，保护进程树。
- [ntaraujo/killer](https://github.com/ntaraujo/killer)（MIT）：多选列表批量结束进程。
- [aimansddiq/close-all-windows](https://github.com/aimansddiq/close-all-windows)（无 license）：`EnumWindows` + `WM_CLOSE` 的 PowerShell 写法，仅作技术参考。
- [Aldaviva/KillUnwantedProcesses](https://github.com/Aldaviva/KillUnwantedProcesses)（Apache-2.0）：保护名单与条件判断思路。

安装器简体中文语言文件来自 [Inno Setup 官方仓库](https://github.com/jrsoftware/issrc) 的 `Files/Languages/ChineseSimplified.isl`。

## 许可证

[MIT License](LICENSE)。

测试环境：Windows 11 22621 / .NET Framework 4.8 / .NET SDK 10。
