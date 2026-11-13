# 一键收摊（OneClickClose）

Windows 11 上的批量关闭小工具：按热键弹出当前程序清单，勾选后一键关闭。  
用 PowerShell 5.1 + WinForms 写成，单文件、无需安装、无需编译。

## 它能做什么

- 全局热键唤出清单，按程序分组显示图标、进程名、窗口数、窗口标题
- 两种关闭模式：
  - **只关窗口（保留托盘）**：向可见窗口发送 `WM_CLOSE`，等效于点 X；程序愿意留在托盘就留着
  - **彻底退出（含后台）**：先关窗口，超时后强制结束进程；没有窗口的后台程序直接结束
- 保护名单：系统关键进程、脚本自身的父进程链不会出现在清单里；可右键单个加入/移出，也可勾选多个后点“保护选中”批量加入
- 终端警告：勾选项里包含终端窗口时，状态栏变红提示
- 热键可配置，配置保存在脚本同目录的 `config.json`

## 文件

| 文件 | 作用 |
| --- | --- |
| `OneClickClose.ps1` | 主程序 |
| `Start-Hidden.vbs` | 无窗口启动，日常挂托盘用 |
| `config.json` | 首次运行自动生成 |
| `README.md` | 本文 |

## 快速开始

1. 双击 `Start-Hidden.vbs`。  
   不会有窗口弹出，只在系统托盘出现一个图标。
2. 按 `Ctrl + Alt + X`，或左键单击托盘图标，打开清单。
3. 勾选要关闭的程序，选择模式，点“关闭选中”。需要固定保留的程序可勾选后点“保护选中”；已保护程序会变灰并取消勾选。
4. 关闭面板：按 `Esc` 或点右上角 X（只是隐藏，不会退出程序）。
5. 真正退出：右键托盘图标 → 退出。

> 第一次运行如果热键被别的软件占用，状态栏会提示；点托盘图标即可打开。  
> 想让启动时直接显示面板，可以不用 VBS，改为运行 `OneClickClose.ps1`。

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
- 执行前会弹窗确认；未保存的内容可能丢失。

## 保护名单

默认硬保护（不会出现在清单中）：

- Windows 关键进程：`explorer`、`dwm`、`csrss`、`winlogon`、`services`、`lsass`、`svchost`、`sihost`、`ctfmon`、`TextInputHost` 等
- 安全/系统组件：Defender 相关进程、`RuntimeBroker`、`dllhost`、`LockApp`、`OpenConsole`、音频/显卡容器等
- 输入法：搜狗、QQ 拼音、百度拼音、微信输入法等
- 本脚本自身的整条父进程链（防止把自己或启动它的终端关掉）

需要临时放过某个程序（例如 Listary、Snipaste、PowerToys）：

- 在清单里右键该程序 → “加入保护名单（永不关闭）”。
- 保护中的程序会以灰色显示，右键可“移出保护名单”。

`config.json` 里的 `protectedProcesses` 也可以直接编辑，写进程名即可（如 `"WeChat"`、`"DingTalk"`，带不带 `.exe` 都行）。

## 配置

`config.json` 示例：

```json
{
  "hotkey": "Ctrl+Alt+X",
  "gracefulTimeoutSeconds": 5,
  "protectedProcesses": [],
  "lastMode": "windows"
}
```

- `hotkey`：支持 `Ctrl` / `Alt` / `Shift` / `Win` 组合，主键支持 `A-Z`、`0-9`、`F1-F24`。例如 `"Ctrl+Shift+Q"`。
- `gracefulTimeoutSeconds`：优雅关闭的等待秒数，1–60。
- `protectedProcesses`：用户保护名单，进程名数组。
- `lastMode`：上次使用的模式，`windows` 或 `all`。

修改后重启程序生效（热键和超时在启动时读取）。删掉 `config.json` 会恢复默认值。

## 开机自启

按 `Win + R`，输入 `shell:startup`，回车。把 `Start-Hidden.vbs` 的快捷方式放进打开的文件夹即可。

## 自检 / 测试

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\OneClickClose.ps1 -SelfTest
powershell -NoProfile -ExecutionPolicy Bypass -File .\OneClickClose.ps1 -CloseTest
powershell -NoProfile -ExecutionPolicy Bypass -File .\OneClickClose.ps1 -ProtectRefreshTest
powershell -NoProfile -ExecutionPolicy Bypass -File .\OneClickClose.ps1 -UiTest
```

- `-SelfTest`：输出现有窗口、两种模式的候选清单、热键注册与回调测试。
- `-CloseTest`：启动系统自带的字符映射表并优雅关闭它，验证关闭链路。
- `-ProtectRefreshTest`：使用临时配置触发真实的单项保护、取消保护和批量保护菜单/按钮操作，确认名单与界面状态同步且每次操作耗时小于 250ms；不会修改实际配置或关闭程序。
- `-UiTest`：离屏构建界面并输出 `ui-test.png` 截图。

## 已知限制

- **托盘图标没有官方枚举 API**。模式二用“当前用户会话 + GUI 子系统 + 父进程过滤”来识别后台程序，不直接枚举托盘图标；个别驱动/助手进程可能出现在列表里，加入保护名单即可。
- **UWP 应用**的窗口由 `ApplicationFrameHost` 托管。模式一可以正常关闭这些窗口；模式二会保护该宿主进程，避免一次杀掉所有 UWP 应用。
- **管理员权限的程序**：非管理员运行时，`WM_CLOSE` 和强杀都可能被 UIPI 拦截，结果列会显示“失败/权限不足”。需要时以管理员身份运行 PowerShell 再启动脚本。
- **程序忽略 `WM_CLOSE`**：模式一不会强杀，结果列显示“窗口未响应”；改用模式二。
- **保存时间较长的程序**：把 `gracefulTimeoutSeconds` 调大，避免在保存完成前被强杀。
- 本工具不会替你保存文档；强制结束的后果需要自行承担。
- 若某个程序把窗口做成无标题/工具窗口，可能识别不到；目前只处理有标题的可见顶层窗口。

## 设计参考

实现时参考了以下开源项目的思路（未复制无许可代码）：

- [voolyvex/pcpow](https://github.com/voolyvex/pcpow)（MIT）：优雅关闭 → 超时 → 强杀，保护进程树。
- [ntaraujo/killer](https://github.com/ntaraujo/killer)（MIT）：多选列表批量结束进程。
- [aimansddiq/close-all-windows](https://github.com/aimansddiq/close-all-windows)（无 license）：`EnumWindows` + `WM_CLOSE` 的 PowerShell 写法，仅作技术参考。
- [Aldaviva/KillUnwantedProcesses](https://github.com/Aldaviva/KillUnwantedProcesses)（Apache-2.0）：保护名单与条件判断思路。

测试环境：Windows 11 22621 / PowerShell 5.1。
