# 鼠标选区监控、手势与完整生命周期

> 2026-10-05，Codex；T-024A / ARCH v1.36 / CCR-020 正式规范。T-023B / 141abec 已独立验收。
> T-024 拆 A（监控底座）与 B（图标、缓存/身份/来源移交、鼠标设置及 App 接线）。两部分均通过才结案，不缩减完整鼠标划词目标。

## 1. 范围与公开端口

A 在 Platform.Windows/Hooks 实现 MouseSelectionMonitor 与纯 GestureRecognizer；通过现有 Platform DI 注册单例 IMouseSelectionMonitor。构造/解析不查询鼠标、桌面或安装 hook，不创建线程/HWND。A 不接 App Controller，不读取选区、不调用剪贴板/键盘/UIA、不显示图标，不改变默认 MouseMode.Off。

原 IMouseSelectionMonitor 只有尚未被生产消费的同步 Start/Stop。CCR-020 替换为 StartAsync(options, cancellationToken=default)、StopAsync()，实现 IDisposable/IAsyncDisposable；保留 IsRunning。新增线程安全 long Generation，初始 0，在接纳新 Start 和首次逻辑 Stop/运行失败失效时递增；幂等的重复 Stop/清理重试不重复推进。它表示发布资格代次，不能复用旧值或当作 HHOOK。

- SelectionGestureEventArgs 原构造/字段保留，新增 init Generation。
- MouseDown 改为 EventHandler<MouseSelectionInputEventArgs>，事件参数含 CursorPosition 和 Generation。
- 新增 Invalidated 事件，参数 MouseSelectionInvalidatedEventArgs 含 Generation 和可空安全 ErrorInfo。正常逻辑撤销/丢包 reset 不带 Error；已知运行故障只给 HookInstallFailed 默认文案，不携带原始 native 异常/来源文本。
- 各参数的 ToString 不打印坐标、来源/标题、排除列表或 Error.Detail。精确声明以 CONTRACTS.cs 为准。

实际手势/按下事件携带其原 run 代次；失效事件携带当前失效代次。B 消费者在真正 UI 接受时核对 Generation/当前模式/自身操作，不因回调曾被调度就认为仍有效。Stop 返回后，旧 run 的所有已接纳订阅回调都已返回；不能把排入 App 的后续异步工作冒充 Monitor 所有，B 自己跟踪它们。

内部重启要为“停旧的失效事件”和“开始新 run”分别推进代次；新 run 不能与旧 run 的最终 Invalidated 共用同一值。取消发生在旧 run 已停、新 run 尚未开始时，不启动新的物理 hook。

## 2. 接纳、启动与重启

StartAsync 在首次等待前检查 caller 取消、验证 options 并深复制 ExcludedProcesses；MinDragDistancePx 必须为 [2,50]，列表和条目不得 null，非法参数用安全 IrisException(InvalidArgument)。文件名按实际字符串 OrdinalIgnoreCase 匹配，不擅自 Trim/补扩展名/取路径 basename。不改写设置。

预取消/参数非法不影响当前 run。接纳时才推进代次并撤销旧发布资格。顺序 Start 对运行中的服务表示先完整停止旧 run，再启动新 options；不能同时保留两个物理 hook。正在 Start/Stop 时新的 Start 明确用 HookInstallFailed 的安全忙碌/清理未完阶段错误拒绝，不建立无界等待队列。App B 会维护一个实际操作和一个最新待办。

在后台启动专用消息线程；所有原生启动工作不捕获调用方 UI 同步上下文。线程先建立消息队列（例如 PeekMessage），再登记线程身份/安装 WH_MOUSE_LL，并准备独立后台消费者。成功返回前 HHOOK、delegate、线程、队列和消费者都有唯一所有者，已具备接纳能力。IsRunning 表示逻辑接纳状态，不宣称能检测 Windows 静默移除 hook。

Start 中取消/Stop 先撤销资格，仍须等尚未返回的原生初始化真正结束并清理迟到资源，然后返回 OCE/对应结果。不能 WaitAsync 超时后遗忘线程/HHOOK。初始化失败、线程创建/安装/消息泵失败均保留可见安全错误；清理同时失败时保留两阶段数值诊断和所有权，不只返回原错误或抹去清理错误。禁止原始异常对象/Message/InnerException/窗口标题进入对外异常或日志。

## 3. 回调快照与有界队列

遵循 LowLevelMouseProc 官方线程模型，delegate 强引用到真实回调/线程退出。nCode<0 不解析 lParam，直接 CallNextHookEx；其它消息也继续 CallNextHookEx，永不吞用户输入。所有异常都在 native 回调边界安全收束，至少尝试一次 CallNextHookEx，不能把托管异常跨 P/Invoke 回调抛给系统。

只复制 MSLLHOOKSTRUCT 必需值：消息类型、物理坐标、DWORD time、injected flags；不把 lParam 指针留给后台。为取得按下时的元数据，本单明确允许在 button down 快照中进行小量非消息式查询 GetCursorInfo / GetAsyncKeyState / GetForegroundWindow / GetWindowThreadProcessId，button up 只复核窗口/PID，move 只复制事件值。除此之外仍遵守 hook 只快照/入队：不打开进程、不读取路径/标题、无 UI/选区/剪贴板/键盘注入/订阅者调用、无日志 I/O、无 await 或阻塞等待。

标准 IDC_IBEAM HCURSOR 在后台启动准备阶段取得，是共享资源，不 DestroyCursor。RequireIBeamCursor=true 时未知/不可读/隐藏/非 I 型 cursor 均不作为候选；false 不要求 I 型。Shift 用 GetAsyncKeyState 的高位按下状态，在 down 捕获，不用 up 后的新值倒推。自定义 I 型光标可能保守拒绝，不能冒称可识别全部 Provider。

队列最多 256 个小型值快照，TryWrite 非阻塞，消费者 continuation 不得同步运行在 hook 上。满队列时标记丢失 epoch，后台丢弃该 epoch 的残余事件、reset 识别器并发一次合并的 Invalidated；恢复后的干净 down 才能重新起序列，不能把丢失前 down 与后来的 up 拼成手势。不能 DropWrite 后无感知地继续识别，不能无限缓存 mouse move。

worker 丢弃距 native 当前 tick 超过 250ms 的积压事件；按 uint 差值处理 time 回绕，无法排序/时间倒退则 reset。该阈值只用于拒绝迟到事件，不是强制终止任意同步 native 查询的保证。metadata 查询失败安全拒绝该候选，并在后台处理诊断，不在 hook 写日志。

## 4. 来源与手势

worker 使用桌面端口核对当前 HWND/PID 与 down/up 捕获值，确认非自身、未在排除进程中且进程名有效；无法确认或源变化则 reset，不发布手势。查询仍在后台，查询后再次核对时效/代次。事件 App 保留 ProcessName/PID/HWND，但 WindowTitle 固定为 null，不为鼠标监控缓存用户窗口标题。监控识别不替代 B/C2 的选区及复制安全检查。

GestureRecognizer 无 Win32/WPF/时钟单例/I/O；接受冻结配置、值事件、已确认来源和 click metrics。只在 left up 完成手势；单次普通点击不发布。规则优先级：Drag → ShiftClick → TripleClick → DoubleClick。

- Drag：一次合法 left down 到 up，期间位移曾达到 MinDragDistancePx 的欧氏距离阈值（含等号）；极端正负坐标差值/平方不得溢出。完成后清点击序列。
- ShiftClick：down 时 Shift 为真、未达拖动阈值；up 时才发布并清点击序列。
- Double/Triple：同一来源的非拖动、无 Shift 左击；相邻 down 的 uint 时间差不超过 GetDoubleClickTime，点在系统双击矩形内。第三次可发 Triple，随后下一次重新从单击计数；B 将用最新待办/取消处理二击后紧接三击。
- 使用系统双击时间/矩形；不要把矩形总宽高当每侧半径。worker 通过既有 Desktop 最近显示器快照的 DpiScale 与 GetSystemMetricsForDpi 获得物理尺寸；不在 per-monitor-aware 流程使用未换算的 GetSystemMetrics。metrics 无法确认时不猜测默认值来发双/三击。
- 空间边界固定为以相邻前一次 down 为中心的半开矩形：left=x-floor(width/2)、top=y-floor(height/2)，宽高即系统返回值；所有运算先扩为 long。避免偶数宽高时把两个端点都包含导致范围多出一个像素。非正宽高不发双/三击。
- 非左按下、wheel、源/代次变化、丢包/超龄/乱序均 reset。injected/lower-IL injected 不触发手势，撤销已有候选；MouseDown 仍表达收到的任意按钮 down，用于 B 隐藏图标。所有事件均不阻止原输入。

手势结果含 up 的物理 CursorPosition、Gesture、确认的 App 与原 run Generation，不包含选区文本。A 不实现 120ms settle/read；B 才定稿该阶段及缓存点击。

## 5. 发布与完整停止

事件在单个后台消费者中有序调用，原生回调绝不直接调用用户订阅者。调用每个订阅者之前确认 run/代次有效，并在状态锁外调用；已取得的发布持有纳入 Stop 屏障。某个订阅者抛错，安全记录异常类型/阶段，继续其它订阅者，不破坏输入链/线程清理。

StopAsync 从任意线程调用，同步关闭接纳、使旧代次失效、设置 IsRunning=false；异步卸载 HHOOK、结束消息泵、丢弃未处理队列并等待所有已入 native 回调、订阅回调、consumer、消息线程真实退出，再释放 delegate/其他资源。逻辑失效的 Invalidated 在后台发送，不在 UI/native 调用栈里执行用户代码；Stop 本身也等待这次已接纳发布。

并发 Stop/异步 Dispose 等待同一进行中的清理任务。UnhookWindowsHookEx 失败不清句柄，不释放 delegate；消息投递失败、GetMessage -1、线程/consumer 收尾异常必须可见，保留未完成资源可再次 Stop 重试。已经成功的卸载/释放不重复；物理资源未回收时禁止新 Start。不得超时后杀自有托管线程或 Dispose 正在使用的资源以伪造成功。

StopAsync/DisposeAsync 是不可放弃的完整收尾入口，特意不接 caller token；StartAsync 的 caller 取消仍按 §2 完整收尾。外部/用户取消回调不得持状态锁或在 UI 上同步执行；内部原子失效标记可在调用线程完成。

正常结束允许下一次 Start；Dispose 后永久拒绝新启动。同步 Dispose 仅用于从未使用或完整停止状态，否则明确拒绝并保留所有权；DisposeAsync 先完整 Stop，成功后释放。已知运行故障也关闭发布、发安全 Invalidated 错误并尝试真实清理；失败责任保留给 Stop 重试，不能靠 IsRunning=false 冒充已物理清理。

## 6. A 验收

纯手势表驱动：阈值边界、正负/极端坐标、拖回原处、单/二/三/四击、Shift、系统时空矩形、回绕、乱序、跨源、非左/wheel/注入/丢包、I 型开关/未知 cursor。

实际 Monitor + fake 外部 native/message/time/desktop：构造/DI 零 I/O、单启动及重启、UI 调用不在 UI 执行启动/消费、预取消不废弃当前 run、安装/启动/消息泵失败、迟到安装取消/Stop、回调和订阅仍在执行、取消回调不在锁/UI执行、队列满无假手势、代次旧事件拒绝、卸载/投递失败同实例重试及真实线程退出。测试必须覆盖实际 native adapter 的调用形状与资源记账，不能只测自写 fake 状态机。

至少一个失败注入用例在门闩 entered 后抛主异常，finally 先释放所有门闩，再等真实 task/thread/monitor 退出；主异常和清理异常同时保留。同步 Dispose 不得掩盖未停止资源。正常/失败/重试后线程和句柄数回到基线。

最后串行 Release build 与全量 test。禁止真实用户鼠标/按键/hook/选区/剪贴板/窗口扫描、配置/密钥/Run、HTTP 与性能脚本；不启动真实 Iris。A 仅假外部边界；回调 <1ms、真实资源管理器拖拽/混合 DPI/第三方应用兼容性未执行即写未验证，留后续人工/NFR 验收。

## 7. 已核查官方依据

- [LowLevelMouseProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc)：安装线程消息循环、CallNextHookEx、专用线程立即移交 worker，以及静默卸载不可检测限制。
- [MSLLHOOKSTRUCT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-msllhookstruct)：per-monitor-aware 坐标、DWORD 时间与 injected flags。
- [UnhookWindowsHookEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-unhookwindowshookex)：成功返回后仍可能有回调尚未结束。
- [GetCursorInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getcursorinfo)、[GetDoubleClickTime](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdoubleclicktime)、[GetSystemMetrics](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetrics)：cursor 查询、时间阈值、矩形含义及 DPI 限制。
- [GetSystemMetricsForDpi](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetricsfordpi) 明确按传入 DPI 缩放适用指标；[GetAsyncKeyState](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate) 最高位表示当前按下，不使用最低位作可靠按下状态。

原文快照在系统临时目录 iris-t024-official-docs。官方文档核查不等于真实 hook/兼容性已验证。
