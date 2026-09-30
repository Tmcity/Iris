# 桌面环境与显示器查询

> 2026-09-30，Codex；T-022A / CCR-009。由已核查的桌面准备稿、设备筛选/几何验证及工单候选稿定稿。
> 实现既有 IDesktopContext；不包含窗口定位、抓屏、UIAutomation、选区恢复或按键注入。

## 1. 边界与注册

DesktopContext、内部 MonitorEnumerator 与最小原生/几何帮助类型位于 Platform.Windows/Capture。AddIrisPlatformWindows 注册单例；构造只接收依赖，不访问实时桌面或创建窗口。Core 不引入 WPF/Win32；不新增 NuGet 包、公开句柄类型或错误码。

方法可从任意线程调用，保持既有同步查询契约。不通过 Task.Run(...).Result 包装查询，不占用 App 的 WPF Dispatcher，也不新建常驻 UI 线程或全局任务队列。临时原生资源均在单次调用中有确定所有权；不为纯查询服务增加会破坏在途调用的 Dispose。

## 2. 显示器快照

### 2.1 枚举与筛选

先以 EnumDisplayDevicesW(NULL, index, ...) 取得当前会话 adapter/view 表，再以 EnumDisplayMonitors(NULL, NULL) 与 GetMonitorInfoW(MONITORINFOEXW) 取得显示器信息。每次初始化结构 cb；以 DISPLAY_DEVICE.DeviceName 和 MONITORINFOEX.szDevice 做 OrdinalIgnoreCase 关联，不用友好描述或监视器子设备名代替。

只保留 ATTACHED_TO_DESKTOP 且未标 MIRRORING_DRIVER 的项。EnumDisplayMonitors 包括不可见镜像伪显示器；同时，remote/virtual 设备可能是真正的用户桌面，不能因这些属性一概删除。

每次返回新的深度只读快照，按 IsPrimary 降序、DeviceName Ordinal 排序。原生缓冲、内部集合及后续采集不影响已返回值。DeviceName 只作运行时标识，不保证跨插拔/硬件变更稳定；公共对象不保存供未来使用的 HMONITOR。

关联缺失、同名信息冲突、几何/DPI 无效、有效列表为空或中途设备消失时，不发布残缺结果；丢弃本轮并最多重新采集一次，仍失败则报错。永久资源/权限失败无需盲目重试。各枚举并非原子事务，有限重采不承诺返回后拓扑保持不变；窗口定位调用方仍须处理之后的变化。

### 2.2 物理坐标和 DPI

同步原生采集帧临时使用 PerMonitorV2 线程 context，finally 恢复先前 context，期间不 await，不更改进程全局 DPI awareness。矩形保留虚拟桌面物理像素及负坐标。宽高先用 long 差值验证，非空且转换为 int 后端点仍可表示；WorkArea 有效且不越出 Bounds。

使用 GetDpiForWindow，不在 per-monitor-aware 线程直接调用 GetDpiForMonitor。无合适 HWND 时，在目标显示器内部创建未显示的 top-level STATIC 小窗口（WS_POPUP、WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW），通过 MonitorFromWindow 确认归属，再读 DPI。禁止设置 WS_VISIBLE、调用 ShowWindow/激活 API；message-only HWND 没有屏幕定位语义，不用于此处。自己的 HWND 在创建线程销毁，然后恢复旧 context。

DPI=0 或无效视为失败；不静默用 1.0，也不把主屏 DPI 应用于其它屏幕。DpiScale=DPI/96.0，须有限且大于 0。原生枚举回调捕获错误并中止，由托管外层统一报告，不能让托管异常跨越反向 P/Invoke 边界。

### 2.3 最近显示器

GetMonitorAt 只取得一份有效快照（其采集内部可按上文重采一次）。包含关系是半开区间；矩形最后一个整数像素为 X+Width−1 / Y+Height−1。点在某轴范围内时该轴距离为 0，否则为点到最近边缘像素的距离。

按 dx²+dy² 最小者选择；多个矩形覆盖该点也属于距离 0 的并列。并列主屏优先，再按 DeviceName Ordinal，结果不依赖枚举顺序。端点加减先转 long，平方和用 Int128，防止极端 int 坐标差平方溢出。此规则不宣称与 MonitorFromPoint 未公开的并列策略完全相同。

## 3. 光标与前台

GetCursorPosition 使用 GetPhysicalCursorPos，返回物理 PixelPoint；只读、不移动光标。

GetForegroundApp：

1. GetForegroundWindow 返回 0 时返回 null，这是窗口失去激活时可能出现的正常状态。
2. PID 输出变量先清零，再调用 GetWindowThreadProcessId；返回 threadId=0 或 PID=0 时返回 null。失败时 PID 可能不被 API 改写，禁止复用上次值。
3. 只以 PROCESS_QUERY_LIMITED_INFORMATION 打开进程，以 QueryFullProcessImageNameW(flags=0) 查询 Win32 路径，固定容量 32,768 字符，不无限扩容。仅保留文件名和实际扩展名（通常 .exe），不自行补扩展名或保留完整路径。
4. 无权限、退出、缓冲不足或无法得到有效文件名，返回 null。不调用 Process.MainModule、不遍历全机进程、不申请调试权限、不用磁盘读取验证程序文件。
5. WindowTitle 可空。其它进程只用 GetWindowTextW 读取顶层 caption，容量 1,025 WCHAR（最多保留 1,024 字符），不读取控件内容；返回 0 时标题为 null。
6. 同进程且不在窗口所属线程时直接使用 null 标题，避免跨线程 WM_GETTEXT 等待。标题是否可得不影响已识别进程的有效性。
7. 结束前重新采样前台 HWND/PID；变化则丢弃整份结果并最多重做一次，仍不稳定返回 null，不能拼接不同窗口的信息。

结果只是短暂快照。未来发送输入前必须重新确认目标；本接口不授予后续按键注入权限。进程文件名与既有黑名单以 OrdinalIgnoreCase 比较，本单不改设置集合。

## 4. 失败与资源

| 场景 | 行为 |
|---|---|
| 无前台、窗口/PID 过期、访问拒绝、来源不稳定 | 返回 null |
| 已识别进程，caption 不可用或同进程跨线程 | 返回快照，WindowTitle=null |
| 光标查询失败 | IrisException(Unknown)，固定文案“无法获取鼠标位置” |
| 显示器采集/几何/DPI/关联无效，有限重采后仍失败 | IrisException(Unknown)，固定文案“无法获取显示器信息” |
| 只有资源释放/context 恢复失败 | IrisException(Unknown)，固定文案“桌面查询资源清理失败” |

不复用 ClipboardBusy、CaptureFailed 或存储错误。执行主失败优先，只有清理失败时第一个清理失败成为主失败，其余安全记录；正常返回 null 的路径也须完整清理。HWND、进程句柄、线程 context 不泄漏、不重复释放。

Detail/日志只含 API 名、数值错误、固定原因码和必要异常类型；不传原始异常对象，不记录 WindowTitle、完整映像路径或 ForegroundAppInfo.ToString。查询层不直接通知用户。

## 5. 验证范围

测试必须调用实际实现，通过窄的原生适配器建立失败/竞态，不用手写期望列表自验。覆盖设备标志/名称关联、集合不可变、有限重采、负坐标/共边界/空隙/重叠/并列、极端整数、无效几何/DPI、资源/context 清理、前台切换/拒绝访问及隐私边界。

本机原生验证只进行只读显示器/光标查询和不可见 DPI 窗口；不自动读取用户窗口标题作为测试数据，不改缩放/排列或激活窗口。前台边界使用自有窗口/假端口。多屏、混合 DPI、RDP、热插拔未具备实机条件时明确报告未验证，不将合成数据等同实机通过。

已完成的准备证据：DESKTOP_CONTEXT_DRAFT.md 中单屏 100% DPI/隐藏窗口探针；DESKTOP_CONTEXT_VALIDATION.md 中 10,000 组逐像素差分、Int128/BigInteger 对照及设备关联探针。它们只证明方案可行性，不代替新实现的测试。

## 6. 官方依据与来源

准备阶段已实际读取并记录以下官方 API 文档：GetDpiForMonitor、GetDpiForWindow、SetThreadDpiAwarenessContext、EnumDisplayMonitors、DISPLAY_DEVICEW、EnumDisplayDevicesW、GetPhysicalCursorPos、GetWindowTextW、QueryFullProcessImageNameW、GetWindowThreadProcessId、GetForegroundWindow。完整链接与探针原始输出保留在 specs/drafts/DESKTOP_CONTEXT_DRAFT.md、DESKTOP_CONTEXT_VALIDATION.md、T-022A_WORK_ORDER_CANDIDATE.md。

本规范取代三份桌面候选稿的待决状态。TRANSLATION_SESSIONS_DRAFT.md 仍是后续 T-015 的未批准方案，不由 T-022A 实施。
