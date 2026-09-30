# T-022A 桌面环境与显示器查询准备稿

> 2026-09-30，Codex。基线 e61fb8d；T-021A 正在实施。
> **准备稿，尚未签发，不是当前工单或契约变更批准。** 不修改当前 T-021A 的实现范围。转为正式规范前须在其交付基线上核对。

## 1. 已确认的缺口

1. ARCH §10.7 当前使用 GetDpiForMonitor(MDT_EFFECTIVE_DPI)，但 App manifest 是 PerMonitorV2。Microsoft 明确要求 per-monitor-aware 调用线程不要使用此 API，推荐 GetDpiForWindow。T-022A 签发时应修订这条旧规范，不能照抄。
2. ForegroundAppInfo.ProcessName 没有说明是否带 .exe；Selection.ExcludedProcesses 明确含 .exe，默认 ClipboardUnsafeProcesses 也含 .exe。建议契约明确返回路径末段（含扩展名），比较时 OrdinalIgnoreCase，避免直接使用 Process.ProcessName 导致黑名单失配。
3. 空显示器列表、部分查询失败、热点插拔、最近显示器并列、GetForegroundApp 的访问失败/切换竞态都没有定义。
4. PixelRect.Right/Bottom 用 int 运算。平台构造几何值前必须检查 long 差值及可表示性，不能把溢出的原生矩形传入既有 Geometry；本单无需泛化重写全部 Core 几何。

## 2. 推荐的最小交付

- Platform.Windows 中实现 IDesktopContext，以及内部 MonitorEnumerator 和少量原生/几何帮助类型。
- 使用既有 MonitorInfo / ForegroundAppInfo / PixelPoint / PixelRect，不新增 UI 类型或把 HMONITOR 作为公共身份。
- 单例 DI，可从 UI 或后台调用；构造只接收依赖，不枚举硬件、不取前台窗口、不创建窗口。
- 本单只查询，不做定位、抓屏、鼠标监听、剪贴板选区、热键或窗口激活。WindowPlacementHelper 仍在 T-015。
- 优先每次取得新的只读快照，暂不增加全局拓扑监听和缓存失效框架。调用之间拓扑可以变化；一次返回的集合及 record 不再受后续调用影响。

## 3. DPI 与显示器路径

推荐候选路径：

1. 在同步原生查询帧中暂设线程 DPI context 为 PerMonitorV2，记录并在 finally 恢复旧值；其间不 await，也不改变进程全局 DPI awareness。
2. EnumDisplayMonitors(NULL, NULL) + GetMonitorInfoW(MONITORINFOEXW) 收集 DeviceName、物理 Bounds/WorkArea 和 Primary 标志。回调中捕获错误并返回 false，由托管外层报告，不让托管异常跨越反向 P/Invoke 回调边界。
3. 若无已有合适 HWND，可在每个显示器中心创建 **未显示** 的原生 top-level STATIC 窗口：WS_POPUP、WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW，不设置 WS_VISIBLE、不调用 ShowWindow/激活 API，不使用 message-only HWND（后者没有屏幕定位语义）。
4. GetDpiForWindow 读取该窗口的 DPI，除以 96 得 DpiScale；用 MonitorFromWindow 验证窗口确实属于目标 HMONITOR。窗口在创建线程的 finally 销毁，查询结束恢复线程上下文。
5. 不把 UI 主窗口的 DPI 应用到其它显示器，也不使用窗口句柄为 0 时的默认值冒充查询成功。不以 1.0 静默替代失败 DPI。

此方法已在本机单屏验证，但尚未证明所有混合 DPI/热插拔情形。正式工单需保留多屏手动验收，并核查镜像/伪显示器的枚举过滤策略；不凭枚举顺序假定第一个是主屏。

输出建议：按 IsPrimary 降序、DeviceName 的 Ordinal 顺序稳定排列。要求 Bounds 为可表示的非空矩形、DpiScale 为有限正值；WorkArea 是有效物理矩形且不越出 Bounds。若设备在查询过程中消失，丢弃这次整个快照并最多重新采集一次；仍失败则安全报错，不返回混有新旧拓扑的部分结果。空快照不伪造主屏。

错误码建议使用现有 Unknown 与固定平台文案，不复用 CaptureFailed 冒充抓屏失败；是否另增精确平台错误码留到正式契约审议，不能由开发自行添加。

## 4. 光标、最近显示器与前台信息

**光标**：GetPhysicalCursorPos，成功返回物理 PixelPoint；失败固定错误。无须通过 WPF Mouse.GetPosition 或缩放换算。只读取，不移动光标。

**GetMonitorAt**：从一次有效快照选择。先用半开 Bounds 判断包含；否则按点到矩形所含像素的最短平方距离选最近项。建议用 Int128 计算极端 int 坐标差的平方，避免 long 平方溢出；并列优先主屏，再按 DeviceName Ordinal。选择与返回都基于同一快照，不分别调用两次 GetMonitors。若最终决定直接使用 MonitorFromPoint，必须说明热插拔后的 HMONITOR 如何与同次快照匹配，并相应更正“并列”规则。

**GetForegroundApp** 推荐步骤：

- 采样前台 HWND → PID → 以 PROCESS_QUERY_LIMITED_INFORMATION 查询完整映像路径 → 仅取文件名（含 .exe）。关闭自己的进程句柄，不保存完整路径。
- 最后重新采样 HWND/PID；变化则最多重试一次，仍变化返回 null。窗口句柄/PID 会复用，本次结果只是短暂快照，不是随后发送 Ctrl+C 的授权。
- 无前台、窗口退出、进程查询被拒绝：返回 null，不虚构 ProcessName。之后 T-022 遇到无法确认来源时应跳过键盘注入。
- WindowTitle 可空，建议固定上限缓冲，不随 GetWindowTextLength 无限分配；只取顶层 caption，不查询其它进程控件内容。不记录标题、完整路径或返回对象的 ToString。
- GetWindowText 对同进程窗口可能发送 WM_GETTEXT；本进程窗口的跨线程读取需谨慎，不能把“读取其它进程 caption 不被无响应目标拖住”推广到自身窗口。建议本进程且非窗口所属线程时允许返回 null 标题；具体方式在工单中固定。
- 不读取全机所有进程，不为查询申请调试权限，不调用 UIAutomation。

## 5. 待同步的契约注释范围

正式签发时另走 CCR，目前不修改 CONTRACTS.cs 或落地文件：

- MonitorInfo：物理像素、DpiScale 的含义、快照不可变、DeviceName 只作本次/运行时设备标识，不保证跨硬件变更稳定。
- ForegroundAppInfo：ProcessName 含扩展名、WindowTitle 可空、信息会过期，不能替代操作前的目标重验。
- IDesktopContext：各失败出口、集合语义、包含/最近规则、线程与 DPI 上下文恢复。

不在 T-022A 改 IClipboardService、GetSequenceNumber、选区契约或已经验收的 Dispatcher。

## 6. 测试矩阵草案

| 场景 | 预期证明 |
|---|---|
| 单屏 / 多屏 / 左侧负坐标 / 上方屏幕 | Bounds/WorkArea 保留物理坐标，不把负坐标夹到 0 |
| 100% / 150% / 200% 合成 DPI | 每个 MonitorInfo 使用自己的比例，不能统一用主屏比例 |
| 边界点、屏幕间空隙、距离并列 | 半开包含和最近选择规则一致；极端 int 输入不溢出 |
| 枚举成功但中途设备移除 | 只重试有限次，不发布混合/残缺快照 |
| 创建窗口/DPI 查询/销毁异常 | 无可见窗口、无激活操作；自己的 HWND 总被清理，线程 context 总被恢复 |
| 返回集合再尝试外部修改 | 不影响之后调用，且不是暴露内部可变数组/list |
| 前台在查询中切换、退出或拒绝访问 | 不返回拼接了两个窗口的信息，不泄露路径/标题 |
| cmd.exe / powershell.exe / 大小写变体 | ProcessName 格式与既有黑名单一致（本单不发送任何按键） |
| 实际 App 组合根 | 解析成功、单例；构造不需要 WPF Application/实时硬件访问 |

自动测试以窄原生边界 + 合成几何为主；实际探针不替代多屏/混合 DPI 验收。不得为了测试更改用户显示器分辨率、缩放或排列。

## 7. 本轮实际探针

项目：`C:\Users\28910\AppData\Local\Temp\iris-m2-desktop-prep\Probe.csproj`，同目录 Program.cs。独立项目，无仓库 ProjectReference，不争用开发中的 Core/App 输出。

命令：`dotnet run --project C:\Users\28910\AppData\Local\Temp\iris-m2-desktop-prep\Probe.csproj -c Release`

实际输出：

```text
MONITOR device=\\.\DISPLAY1 bounds=(0,0,2560,1440) work=(0,0,2560,1392) dpi=96 primary=True mapped=True pmv2=True hidden=True
DESKTOP monitors=1 distinct-dpi=1 physical-cursor-query=True
CLEANUP context-restored=True foreground-unchanged=True
```

仅证明本机一块 100% 缩放显示器上的查询路径。探针没有显示窗口、激活窗口、抓屏、读取前台标题/文本、触碰剪贴板或改变显示设置；创建的隐藏窗口均已销毁。负坐标、多屏、混合 DPI、RDP 与热插拔本轮未实机验证。

## 8. 本轮实际读取的官方资料

- [GetDpiForMonitor](https://learn.microsoft.com/en-us/windows/win32/api/shellscalingapi/nf-shellscalingapi-getdpiformonitor)：原文 “This API is not DPI aware and should not be used if the calling thread is per-monitor DPI aware.”
- [GetDpiForWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow)：结果依赖目标窗口 DPI awareness，per-monitor-aware 窗口使用所在显示器 DPI；无效 HWND 返回 0。
- [SetThreadDpiAwarenessContext](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setthreaddpiawarenesscontext)：返回旧 context，可用于恢复；失败返回 NULL。
- [EnumDisplayMonitors](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaymonitors)：NULL HDC/clip 枚举全部显示器，使用虚拟屏幕坐标。
- [GetPhysicalCursorPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getphysicalcursorpos)：返回物理光标坐标。
- [GetWindowTextW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowtextw)：其它进程顶层 caption 与同进程 WM_GETTEXT 的行为不同。

## 9. 签发前仍须完成

复核 T-021A 交付及新增的原生适配惯例；定稿镜像/伪显示器策略、失败文案/错误码及 HWND/PID 竞态注释；用合成布局固定最近屏幕和整数边界测试。然后更新 ARCH 与 CCR，并将本稿中的有效规则转为正式 T-022A 工单。当前只保留这份准备稿，不提前创建第二张 current_task。
