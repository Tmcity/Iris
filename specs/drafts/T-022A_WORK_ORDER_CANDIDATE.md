# 工单候选 T-022A：桌面环境、显示器快照与 DPI

> 2026-09-30，Codex。**尚未签发，不得据此开始开发。**
> 当前唯一工单仍为 T-021A-R2。本稿将此前准备收敛成可签发内容；R2 验收后填写基线、批准 CCR 并同步正式规范，再转入 current_task。

| 字段 | 候选内容 |
|---|---|
| 基线 | 待填：T-021A-R2 最终验收提交 |
| 前置 | T-005；按当前串行开发顺序等待 T-021A 结案 |
| 目标 | 为翻译窗口定位与后续选区读取提供真实桌面查询能力 |
| 依据 | ARCH 的分层规则；DESKTOP_CONTEXT_DRAFT.md；DESKTOP_CONTEXT_VALIDATION.md |
| 执行方式 | Gemini 实施、Codex 独立复审；继续由用户手动转交 |

## 1. 交付范围

- 在 Platform.Windows/Capture 中实现 DesktopContext、内部 MonitorEnumerator、最小原生适配器及纯几何选择帮助代码，沿用 ARCH 目录约定。
- 实现既有 IDesktopContext。继续使用 MonitorInfo、ForegroundAppInfo、PixelPoint、PixelRect，不引入公开 HMONITOR、WPF 类型或新的错误码。
- AddIrisPlatformWindows 单例注册；构造不查前台、不枚举硬件、不创建窗口。真实 AppHostBuilder 能构建并解析服务。
- NativeMethods.txt 只增加实际需要的 API；使用既有 Windows SDK/CsWin32，不增加 NuGet 包。
- 按另行批准的 CCR 同步 CONTRACTS.cs 与 Core/Platform/DesktopModels.cs 的注释；不改接口签名、枚举或默认值。

不实施窗口定位/移动、抓屏、UIAutomation、选区恢复、按键注入、热键、鼠标钩子或任何生产 Handler。WindowPlacementHelper 属于 T-015；GdiScreenCapturer 属于 T-025。

## 2. 显示器快照

### 2.1 枚举和筛选

同次采集取得 EnumDisplayDevicesW(NULL, ...) 设备表及 EnumDisplayMonitors/GetMonitorInfoW 信息，以 DeviceName/szDevice（OrdinalIgnoreCase）关联。只保留 ATTACHED_TO_DESKTOP 且未标 MIRRORING_DRIVER 的项；不按友好描述猜测物理设备，不因 remote/virtual 属性一概排除实际桌面。

输出每次新建的不可变快照，按 IsPrimary 降序、DeviceName Ordinal 排序。DeviceName 是当前运行时标识，不保证插拔/换硬件后仍稳定。公共对象不保存供后续调用复用的裸 HMONITOR。

同名冲突、关联缺失、几何/DPI 无效、有效列表为空或采集中设备消失：不发布部分结果，重新采集一次，仍失败按 §5 报错。采集不是 Windows 提供的原子事务；不承诺返回后硬件拓扑不变。永久性资源/权限错误无需盲目重试，所有路径均有明确上限。

### 2.2 物理坐标与 DPI

- 在同步原生采集帧暂设线程为 PerMonitorV2，finally 恢复原 context；禁止跨 await 保持此临时状态，不修改进程全局 DPI awareness。
- Bounds、WorkArea 保留虚拟桌面的物理像素和负坐标。宽高用 long 差值验证后转换；边界须可表示为 int，矩形非空，WorkArea 不越出 Bounds。
- 使用 GetDpiForWindow。无适合 HWND 时，可在目标显示器内部创建不显示、不激活的 top-level STATIC 小窗口（WS_POPUP、WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW），用 MonitorFromWindow 验证归属，再查询 DPI。
- 不设置 WS_VISIBLE，不调用 ShowWindow/SetForegroundWindow，不使用没有屏幕定位语义的 message-only HWND。自己的 HWND 在创建线程销毁，之后恢复原 context。
- DPI 为 0/无效时失败；不能静默回退 1.0，也不能统一套用主屏 DPI。DpiScale 为 DPI/96.0。
- 此路径替换旧 ARCH §10.7 对 GetDpiForMonitor 的直接推荐。该 API 的 per-monitor-aware 使用限制已由官方文档确认。
- 原生枚举回调捕获错误、记录安全元数据并中止，由托管调用外层处理；不让托管异常跨反向 P/Invoke 边界。

### 2.3 GetMonitorAt

仅使用一次有效快照（采集内部可按上文重采一次）。先按半开 Bounds 判定包含；否则按点到矩形所含整数像素的最短平方距离选择。最后像素是 X+Width−1 / Y+Height−1；dx/dy 的加减先用 long，平方和用 Int128。

距离并列或多个范围覆盖同一点时，主屏优先，再按 DeviceName Ordinal。结果不依赖原生枚举顺序。规则不宣称与 MonitorFromPoint 未公开的并列选择完全相同。

## 3. 光标与前台信息

GetCursorPosition 使用 GetPhysicalCursorPos，只读物理坐标，不移动光标；失败按 §5 报错。

GetForegroundApp 的候选固定流程：

1. GetForegroundWindow 取得 HWND；为 0 则返回 null，这是窗口切换时的正常状态。
2. 将 PID 输出变量初始化为 0，调用 GetWindowThreadProcessId；返回 threadId=0 或 PID=0 则返回 null。失败时 PID 可能不被 API 改写，不能复用旧值。
3. 只以 PROCESS_QUERY_LIMITED_INFORMATION 打开进程，QueryFullProcessImageNameW(flags=0) 查询 Win32 路径。使用固定上限 32,768 字符缓冲，不无限扩容；成功后仅保留路径末段，含实际扩展名（通常 .exe），不强行补写扩展名。
4. 不调用 Process.MainModule、不遍历全机进程、不申请调试权限、不用文件系统读取验证可执行文件。访问拒绝、退出、缓冲不足或无法取得有效文件名均返回 null。
5. WindowTitle 可空。其它进程只用 GetWindowTextW 读取顶层 caption，固定容量 1,025 WCHAR（最多保留 1,024 字符），不先按长度无限分配、不查询控件文本。返回 0 时标题为 null。
6. 同进程且不在窗口所属线程时，标题直接为 null，避免跨线程 WM_GETTEXT 等待。是否有标题不决定进程名/窗口快照是否有效。
7. 最后重新采样前台 HWND 及 PID；与开始不符则丢弃整份结果并最多重做一次，仍不稳定返回 null。不得返回 A 窗口的进程名配 B 窗口的标题。

该结果仍是短暂快照。未来 T-022 发送按键前必须另行确认目标，不能把一次查询结果视为持续有效的输入授权。进程名与现有黑名单按 OrdinalIgnoreCase 比较；本单只返回实际文件名，不修改设置集合。

## 4. 清理与线程要求

所有方法可从任意线程调用。原生操作保持线程亲和性，且不占用应用 WPF Dispatcher；不新建常驻 UI 线程或全局任务队列。查询方法保持同步契约，不假装通过 Task.Run(...).Result 获得异步能力。

HWND、进程句柄、DPI context 均有明确所有权。收尾不能重复释放、不吞掉失败；执行主失败优先，只有清理失败时报告清理失败，多个清理错误安全记录其余项。需保证已确认的“不存在/不可读前台”正常 null 路径不会遗漏自身资源清理。具体编码可沿用已验收的原生边界惯例，避免复制带缺陷的早期剪贴板实现。

本单没有跨调用持有的原生资源，不为纯查询服务增加会破坏并发调用的 Dispose。

## 5. 错误与日志定案候选

| 场景 | 行为 |
|---|---|
| 无前台、HWND/PID 已过期、进程访问拒绝或来源不稳定 | 返回 null，后续调用可重新查询 |
| 已识别进程但 caption 不可用/同进程跨线程 | 返回快照，WindowTitle=null |
| 光标查询失败 | IrisException(Unknown)，固定文案“无法获取鼠标位置” |
| 显示器查询/几何/DPI/关联无效，有限重采后仍失败 | IrisException(Unknown)，固定文案“无法获取显示器信息” |
| 本地资源释放/上下文恢复失败，且没有更早主失败 | IrisException(Unknown)，固定文案“桌面查询资源清理失败” |

不新增错误码；不复用 ClipboardBusy、CaptureFailed 或存储错误伪装桌面查询失败。Detail/日志只含 API 名、数值 Win32 错误或固定原因码、必要异常类型名；不传原始 Exception，不记录 WindowTitle、完整映像路径或 ForegroundAppInfo.ToString。正常前台消失/拒绝访问不弹通知，查询层也不直接调用 INotificationService。

## 6. 正式测试必须覆盖

- 测试调用真实 DesktopContext/MonitorEnumerator/选择帮助代码，通过窄原生适配器驱动；不能只在测试中手工构造预期调用列表再验证自己。
- attached/mirror/remote 标志与名称大小写关联，缺失关联和拓扑变化的有界重采；发布集合不可外部修改，也不会随下次枚举变化。
- 负坐标、共边界、空隙、重叠、并列、极端整数，错误尺寸及 NaN/Infinity/零 DPI；可参考准备探针的独立逐像素基准，但不把临时模型通过当作产品测试。
- HWND/DPI 查询失败、创建或销毁失败、旧 context 恢复失败及复合清理错误；断言成功/失败下均不显示或激活窗口。
- 前台为 0、无效 HWND 留下未变 PID、访问拒绝、查询中切换/退出、同进程跨线程标题跳过；验证进程句柄的释放和日志隐私。
- 真实 AppHostBuilder 构建、解析和单例；不靠占位实现或构造时访问桌面来完成注册。
- 正常本机原生验证只进行只读显示器/光标查询及不可见 DPI 窗口；不自动读取用户的窗口标题作为测试数据，不更改屏幕缩放/排列或抢焦点。必要的前台读取边界使用自有窗口/假端口验证。

多屏、混合 DPI、RDP、热插拔若环境不具备，明确记录未验证；不要用合成数据宣称实机通过。已有单屏 100% 与设备关联探针见两份准备文档。测试等待和资源回收有界，失败不会留后台线程/窗口，也不把超时后的未结束工作写成已清理。

## 7. 签发时同步清单

1. 填入 R2 验收提交；把本候选中有效规则整理为正式 DesktopContext 规范，更新 ARCH 的 DPI 路径。
2. 批准注释型 CCR：MonitorInfo 的物理坐标/不可变快照；ForegroundAppInfo 的文件名/可空标题/时效；IDesktopContext 的失败出口和最近选择规则。签名保持不变。
3. 修改 BACKLOG：T-021A/CCR-008 结案后，T-022A 才改为 ▶。保留后续 T-018 → T-019 → T-015 → T-016 → T-017 顺序。
4. 将正式工单放入 current_task；完成后要求完整工单与报告共同归档、独立提交。Release 构建零警告，全量可运行测试通过；验证方式和环境限制如实列出。

## 8. 本轮新增核查来源

- [QueryFullProcessImageNameW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew)：支持 PROCESS_QUERY_LIMITED_INFORMATION，flags=0 返回 Win32 路径，长度参数按字符且成功返回长度不含 NUL。
- [GetWindowThreadProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowthreadprocessid)：无效 HWND 返回 0，失败时输出 PID 变量不变。
- [GetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getforegroundwindow)：失去激活等情况下可能返回 NULL。

以上官方资料已实际读取。本轮没有重复执行已有 DPI/几何探针，也没有运行或修改正在返工的生产代码/测试；仅整理这个候选工单。前台查询的完整实现仍须按正式工单验证。
