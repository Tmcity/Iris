# 剪贴板快照与条件写入

> 2026-10-05，Codex；T-022C1 / ARCH v1.32 正式规范。T-022B 已验收。T-022C 拆为 C1 原生快照/条件写入底座、C2 复制安全/组合读取器/退出接线；两部分都通过才结案 C。公开 Core 契约和默认配置本单不变。

## 1. 范围与非目标

Platform.Windows/Clipboard 新增内部 ClipboardSnapshotService，提供实际 Win32 多格式快照、受版本保护的清空与恢复。它不发送按键、不查询 UIA、不读取前台用户窗口、不接 Handler，不把 snapshot 当作选区结果。C2 才调用这些原语构成完整选区事务并处理终端/密码控件、来源归属与应用退出。

已有 IClipboardService / Win32ClipboardService 的公开行为、注册与已验收实现不变。可以增量复用窄 native 适配器和安全内存帮助方法，不复制其图像解码逻辑；不新增包或生产项目。新服务及结果/快照/版本/适配器全部 internal，注册惰性单例；构造、DI 解析和 Host.Build 不创建线程、HWND 或访问剪贴板。

## 2. 内部能力与结果语义

可以采用等价内部命名，但必须提供以下真实能力：

- CaptureSnapshotAsync：无本程序写入地捕获当前剪贴板支持格式的完整、有界副本和稳定版本；合法空剪贴板也是成功快照。
- ReadVersionAsync：在同一原生读取帧取得当前版本，不把不同时间的序列号与 owner 拼接。
- TryClearAsync(expectedVersion)：只有当前版本与期望完全相等时清空，并返回清空后版本；不相等返回 Changed，零 Empty/Set。
- TryRestoreAsync(snapshot, expectedVersion)：先准备全部格式内存，再持锁比较版本并恢复；不相等返回 Changed，零 Empty/Set。恢复合法空快照等于条件清空。
- StopAsync / RetryCleanupAsync / DisposeAsync：完整管理本服务自有线程、窗口、内存、原生帧和任务；同步 Dispose 不等待活动原生操作，也不丢弃责任。

ClipboardVersion 是本服务在一次成功 Open 帧内采集的 opaque 观察值，至少含原生 uint sequence、owner HWND、owner PID（owner 为 0 时允许 PID=0）。只按相等性比较 sequence，包含回绕/0；成功 Open 提供访问能力证据，不把 uint 当递增时间或复制来源的因果证明。owner 非 0 但身份查询失败须安全失败，不能填入任意 PID。

快照只包含数据格式/私有字节副本与原始版本，不暴露可修改的 byte[]。读取/恢复执行前捕获必要引用，不能受调用方随后修改或 Dispose 影响。快照可释放其托管数据；释放与正在使用的借用关系须明确，不能在已排队恢复过程中把缓冲清零。不可用/已释放/不同服务所有权的快照拒绝为 InvalidArgument。

Capture 可明确返回 UnsupportedFormat、TooLarge、Changed（捕获中版本变化）等无副作用结果，不能返回部分快照并声称完整。原生 I/O、关闭和资源释放失败使用安全 IrisException；清理失败不是普通 Changed/Unsupported。

## 3. 格式与预算

首期支持以下可按 HGLOBAL 原始字节保存的格式：CF_TEXT、CF_OEMTEXT、CF_UNICODETEXT、CF_LOCALE、CF_HDROP、CF_DIB、CF_DIBV5，以及注册格式 HTML Format、Rich Text Format、PNG。保留实际枚举顺序和全部原始字节，不解码/重编码文本、HTML、RTF 或图像，不打开 CF_HDROP 指向的文件。

最多 64 个枚举项；单格式最多 33,554,432 字节；总快照最多 67,108,864 字节。所有加法/指针长度使用 checked 算术；在分配/复制前检查 GlobalSize 与总预算。零长度/无效句柄/无法锁定不能伪造为空数据。空剪贴板由零格式列表表示。

遇任何不支持格式（包括 CF_BITMAP、调色板/元文件、owner-display、私有/GDI 对象范围、其它注册格式）时返回 UnsupportedFormat，不进行本程序的清空或写入。该底座不宣称能恢复任意 OLE/应用私有对象。C2 将决定不能完整保存时的复制策略；不得在 C1 静默丢弃不支持格式。

枚举不先 Get 全部无界内容；超过数量预算立刻终止。先验证全部格式属于支持集，再读取数据。注册格式用系统返回的有界名字按 Windows 的不区分大小写语义完整匹配（OrdinalIgnoreCase）；空格/前后缀仍属于名称，不做 Trim 或模糊匹配。不相信外部任意名称意味着数据可执行或可访问路径。

GetClipboardData 句柄为借用，绝不 GlobalFree，Close 后不再访问。GlobalLock 后按 GlobalSize 复制；GlobalUnlock 的返回值与错误码按 Win32 约定判定。快照持有独立托管副本，不保存借用句柄。

捕获开始和读取完成后在同一 Open 帧核对版本及格式集合；延迟呈现/隐式转换可能使 sequence/枚举变化，不能把混合状态当稳定快照。变化时丢弃本次副本，返回 Changed，由上层重新尝试；不无限重读。

## 4. 线程、Open 帧与条件提交

所有本服务原生调用、owner HWND 创建/销毁及清理在同一个惰性启动的专用后台 MTA 执行，处理所属窗口的 Win32 消息。一次只执行一个操作；队列/准入有界，等待可取消。可以使用专用命令泵，不建设跨模块通用调度框架；不使用 WPF/WinForms 或 App Dispatcher。

保留一个仅本服务所有的不可见 message-only owner HWND，用于 OpenClipboard/EmptyClipboard/SetClipboardData；不得显示、激活或影响用户前台。它允许清空后仍有可识别的 owner，生命周期由服务持有。所有数据立即呈现，禁止 SetClipboardData(format, NULL) 延迟呈现或让 worker 依赖用户桌面窗口的消息循环。

每个原生帧 Open→读取/比较/Empty/Set→Close 不跨线程、不 await。Open 最多 10 次总尝试、间隔 30ms，等待期间不持剪贴板；沿用基础服务的取消与 ClipboardBusy 区分。其它原生失败不得冒充占用后重新执行破坏性写入。

条件清空/恢复必须在 Open 成功后、仍持有系统剪贴板锁时比较 sequence+owner+PID，并紧接最后取消检查与 Empty。禁止先比较、Close、再重新 Open 写入。外部更新返回 Changed，已准备的未发布内存释放，不写剪贴板。

恢复在提交前准备每个格式的 GMEM_MOVEABLE 块并填充/解锁。准备任一项失败则零 Empty/Set。Empty 成功后进入提交阶段：晚到取消不跳过剩余 Set/Close/清理，不将已发生写入伪装为未发生；Set 失败安全报告，不承诺 Win32 批量 Set 原子回滚。

成功 Set 的块属于系统，本地不能再改/释放；失败或尚未 Set 的块仍属本服务，逐项清理。一个释放失败不能跳过其它责任，也不能重复释放已成功转移的块。返回 Applied/Restored 之前已完成 Close 和本次全部本地清理，再给出实际写入后版本。

## 5. 停止、错误与责任

操作上下文从接纳起完整登记；取消/Stop 撤销尚未提交的操作，等待正在运行的实际 native 调用及 finally，不能只让等待 Task 超时后释放活动槽。Stop 入口永久关闭准入，取消回调在共享锁/UI 之外执行；并发 Stop/Dispose 共享在途尝试。

Close/GlobalUnlock/GlobalFree/窗口销毁等失败须记录未完成阶段，保持其原生线程和资源所有权，阻止新业务操作，允许 RetryCleanupAsync/再次 Stop 在同一线程重试；不把失败缓存成成功。同步 Dispose 对尚有线程/窗口/活动或失败责任明确拒绝，异步 Dispose 才完成真实退出。

处理初始化中取消、owner HWND 创建失败、命令唤醒/消息循环异常和停止竞争；启动未完成也要持有真实线程及其回滚。不得按名称/PID 结束进程或调用 Thread.Abort，不无限创建替代线程。

错误只含安全阶段、API 名、数值码；不保存原始异常正文/完整异常链，不记录文本、格式内容、文件路径列表、图像字节或内容摘要。ToString 仅含计数/状态，不能默认打印快照。

GetClipboardData 可能触发外部延迟呈现，Win32 同步调用本身没有本服务的硬截止保证。本单不承诺结束任意挂起的外部 provider；只允许一个真实执行线程/上下文，并等待其实际返回。不得用超时后丢弃 Task 的方式伪造退出。C2 的复制响应预算与该限制须明确分开。

## 6. 条件写入可以证明什么

本服务保证：比较版本与 Empty/Set 在同一持锁帧中；观察版本之后发生的、可观察到的外部变化会使条件写入失败，不被无条件覆盖。它不证明一次变化来自某个 Ctrl+C，不证明 owner PID 与按键之间存在因果关系，不构成跨进程的通用事务隔离。

C2 还必须决定复制候选接受、终端/焦点安全、超时/取消恢复、其它来源新复制保护，以及同来源无法区分的并发更新限制。C1 不用任意 sequence 变化推定“选区读取成功”。

## 7. 测试与部署边界

- 实际服务/worker + 窄 native fake：验证惰性构造、同一 MTA/窗口消息、Open 预算、格式/内存上限、原始字节保真、版本稳定性、0/回绕、owner 改变和条件写入的原子比较。
- 前置准备失败零写入；取消在提交前/后的差异；Set 部分成功不重复释放系统内存；Close/Free/Destroy 多错误继续清理、保留责任、同线程重试；停止初始化/活动/失败清理并发。
- 失败注入复用实际执行段，所有自有门闩先在 finally 放行，再等 native worker/任务/线程退出。不能用 Delay 代替 entered。
- 原生集成仅通过既有独立 helper 的新建非交互窗口站，使用 CWF_CREATE_ONLY；worker 创建任何 HWND 前也必须处于 helper 的自有桌面。创建/绑定失败保持环境跳过，禁止退回 WinSta0、复用已有站或备份用户剪贴板后测试；不能在通用测试进程切换窗口站。
- 不运行真实输入、UIA 选区、用户设置/密钥/Run、HTTP 或性能脚本。执行 Release build 和全量 test；本单无新宿主/部署目标，不需要重复 UIA 发布烟雾。

## 8. 官方依据与后续工作

2026-10-05 实际读取 Microsoft GetClipboardOwner、GetClipboardSequenceNumber、Standard Clipboard Formats、Using the Clipboard、GetGUIThreadInfo，均 HTTP 200；缓存位于系统临时目录 iris-t022c-owner.txt / sequence.txt / formats.txt / using.txt / guithread.txt。来源链接：

- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclipboardowner
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclipboardsequencenumber
- https://learn.microsoft.com/en-us/windows/win32/dataxchg/standard-clipboard-formats
- https://learn.microsoft.com/en-us/windows/win32/dataxchg/using-the-clipboard
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getguithreadinfo

GetClipboardOwner 允许无 owner 仍有数据；sequence 按窗口站维护且会受延迟呈现影响；私有/GDI 格式释放协议不同。线程泵、预算、支持集和条件写入是本项目架构选择，不冒称 Windows 的普遍保证。

C2 将在本底座通过后同步 A08/ARCH 的完整复制流程、必要的 ISelectionTextReader 契约注释及最小生命周期接线。总目标不缩减，T-023 的 Handler/UI 仍在后续工单。
