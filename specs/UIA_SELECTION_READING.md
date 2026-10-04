# UIA 选区读取与隔离宿主

> 2026-10-04，Codex；T-022B / ARCH v1.31 正式规范。T-022A 桌面上下文与 T-021 键盘模拟已验收。T-022 保留总单，B 交付 UIA 路径，C 再定稿并交付剪贴板事务与组合读取器；B 通过不能结案总单。

## 1. 架构决定与交付范围

UIA 的外部 provider/COM 调用可能长时间不返回。WaitAsync(300ms) 只结束等待，不能结束调用；每次超时重建线程会积累活动资源。为使超时、取消和正常退出可以真正收尾，B 采用**每次请求一个自有子进程**执行 UIA。父进程内最多一个 starting/active/cleaning/failed-cleanup 请求，不创建无界工作或线程。

新增 `Iris.UiaHost` 控制台宿主（net10.0-windows10.0.19041.0，不启用 WPF/WinForms），仅引用 Core 和 Platform.Windows。UIA 原生实现与父端客户端位于 Platform.Windows/Selection，宿主只是极小组合入口，可通过 InternalsVisibleTo 访问必要 internal 实现。Platform 继续仅引用 Core，禁止反向引用宿主。

App 对宿主只增加 `ReferenceOutputAssembly=false` 的构建/部署依赖，不调用宿主程序集；build/publish 输出必须包含 `uia/Iris.UiaHost.exe`、dll、deps/runtimeconfig 和其运行依赖。架构测试必须覆盖新工程、依赖方向、无 UI 框架、无新增外部包；这是架构师批准的明确例外，不扩大既有包白名单。

B 不实现剪贴板读写/快照/恢复、模拟按键、鼠标 hook、SelectionTranslateHandler 或设置 UI；不注册一个残缺的 ISelectionTextReader。Core 既有公开契约、默认设置和 JSON schema 不变，所有新模型、客户端与协议均为内部边界。

## 2. 父端 API、状态与无副作用构造

内部 `UiaSelectionReader` 按需单例注册，依赖窄进程/传输适配器、目标校验适配器和 TimeProvider。构造、Host.Build、DI 解析、打开设置页均不启动进程/COM，不读取前台或选区。

内部操作等价于 `ReadAsync(nint sourceWindow, uint sourceProcessId, CancellationToken)`；捕获不可变目标身份并生成 RequestId。零句柄/零 PID 抛安全 InvalidArgument。结果为内部不可变模型：Status、可空 Text、可空安全 Error。Status 至少区分 Success、Empty、Unsupported、Sensitive、TooLong、TimedOut、TargetChanged、Busy、Failed。

Success 仅带本次非空白文本；其它状态不带文本。调用方取消以 OperationCanceledException 表达。Sensitive/TooLong/TargetChanged/Busy 不得被未来组合读取器误当“可自动模拟 Ctrl+C”的普通空结果；C 单将明确分派。ToString/日志只含状态、阶段和安全错误码，不打印 Text、标题、协议全文或原始异常。

进程启动前、结果发布前均复核源窗口仍有效、前台 HWND/PID 与预期一致、不是 Iris 自身；变化返回 TargetChanged 且丢弃文本。此复核不抢焦点、不激活窗口。调用完成前结果必须仍属于本次 RequestId/活动上下文，迟到响应不能覆盖新请求。

## 3. 进程和 IPC 协议

产品只启动 App 基目录固定 `uia/Iris.UiaHost.exe`，不从 PATH、用户设置或响应中寻找可执行文件；缺失时返回安全 Failed，不回退到进程内 UIA 或自动下载。使用 ProcessStartInfo：UseShellExecute=false、CreateNoWindow=true、隐藏窗口、重定向 stdin/stdout/stderr。不得请求提权、显示控制台/错误对话框、启动 shell 或传入凭据。

单请求宿主流程：启动后输出一次版本 1 的 Ready 帧；读取一条 Request 帧；验证协议、RequestId 和非零目标后才初始化 UIA；输出一次 Reply 帧并退出。不读取用户配置、不建立网络连接、不注册事件监听，不创建用户窗口或子进程。

stdin/stdout 使用 UTF-8 单行 JSON（真实换行由 JSON 转义）；字段与 enum 映射在一份内部协议实现中定义并由父子共同使用。帧至少包含 ProtocolVersion=1、Kind、RequestId（Ready 除外），Request 含 HWND 的 64 位数值与 PID，Reply 含 Status/Text/安全 ErrorCode。不要把任意 ErrorInfo/Exception 默认序列化到管道。

每帧 UTF-8 最多 1,048,576 字节；在读取阶段执行上限，不先无界 ReadToEnd/ReadLine。拒绝错版本、错 RequestId、重复/截断/非法帧、超限字符串与状态/字段不一致。stderr 持续排空但只保留至多 4KiB 的诊断预算，不回显内容；只记录退出码/字节数/安全类型。坏帧后终止并收尾自有进程，不返回部分文本。

## 4. 时间预算、取消与物理清理

父端从接纳起持有启动任务、Process 对象与管道。启动/Ready 预算 1500ms；Ready 后从请求写入开始到完整响应的预算 300ms。二者使用单调时间分别计算：**300ms 是 UIA 请求预算，不声称包含冷启动时间**。首次端到端最多包含启动预算与真实清理时间；报告如实列范围，后续性能验收不能把它写成 300ms 整链保证。

响应成功也要等待宿主退出、管道任务结束和句柄释放后才发布文本。正常响应后的退出宽限最多 1000ms；超时、取消、错误或未按期退出时，终止本次实际持有的子进程并 await 确认退出。Kill 本身不是退出证明。不得按进程名枚举/结束进程，不使用请求或响应提供的 PID 决定杀谁；不结束源应用或无关进程。

强制退出后的确认预算为 1000ms；拒绝终止或仍未确认退出时，返回可见的清理异常并保留进程/上下文供重试，不把它包装成已干净完成的 TimedOut。等待任务本身也必须被观察/释放；单次尝试超时不等于可以遗忘仍在启动或退出的自有资源。

启动尚未返回时取消，也必须等待实际启动/回滚，不能丢弃启动任务后遗留迟到子进程。超时结果与物理资源清理完成分开；清理失败以安全异常返回并保留自有 Process/任务，禁止新 Read，允许再次 StopAsync/RetryCleanupAsync。不能将一次失败缓存为成功。

StopAsync 在调用入口永久关闭新 Read 准入，撤销当前结果发布资格，完整等待启动、执行、终止、管道和释放；并发 Stop/Dispose 共享在途尝试，失败可重试但不重新开放。DisposeAsync 使用同一屏障；同步 Dispose 仅收已停止/未启动资源，不在 UI 线程同步等待。每个等待有实际 entered/ownership 证据，不依赖固定 Delay 猜测已退出。

取消/清理要保持 UI 可调度；父端的 Process/管道/目标查询均在后台执行。不要用 Thread.Abort、遗弃 Task、无限新建 MTA 线程或只 WaitAsync 超时掩盖 COM 悬挂。强制结束父进程不等于正常退出保证，本单不宣称可在父进程突然消失后执行托管收尾。

## 5. 子进程 UIA 实现

UIA 创建、使用与释放均在同一个**无窗口 MTA 线程**。使用现有 CsWin32 生成 UIAutomationClient COM 接口/SDK 常量；不新增 Interop 包，不用 WPF Automation 代替 Platform 分层。显式处理 COM 初始化成功/失败与本线程的释放；COM 元素/文本 range 不跨进程或线程传递。

若可取得 IUIAutomation2，设置 ConnectionTimeout 与 TransactionTimeout 均为 300ms，检查失败；它们不能代替父端的进程退出屏障。不得沿用默认 ConnectionTimeout=2s / TransactionTimeout=20s 后声称请求已硬取消。

读取流程：

1. 验证预期前台 HWND/PID；获取 FocusedElement，并核对元素所属来源。无法确认与预期窗口关联时返回 TargetChanged/Unsupported，不读取其它应用的数据。不能简单把请求中的 PID 当成已经验证的事实。
2. 先检查 IsPassword；密码/敏感控件返回 Sensitive，Text=null。无法安全读取必要属性时返回安全失败/不支持，不降级抓取控件名称或 Value。
3. 优先取得 TextPattern2，必要时使用 TextPattern；检查 SupportedTextSelection，调用 GetSelection。无 pattern/不支持返回 Unsupported；null/零 ranges、仅 caret 的退化 range 或全部空白返回 Empty。
4. 只读取返回的选中 ranges，不调用 DocumentRange.GetText(-1)、ValuePattern、控件 Name 或全窗口遍历代替选区。最多 32 个 ranges，总共最多 65,536 UTF-16 code units，range 间以一个 `\n` 分隔并计入预算。
5. 每次 GetText 使用剩余预算+1，收到文本后仍验证长度；超限返回 TooLong，不静默截断成成功。不把 maxLength 当外部 provider 必定遵守的保证。保留选中文本内容，不做翻译预处理。
6. 读取结束再次核对预期前台来源，变化则丢弃；在本 MTA 释放所有自有 COM/range/BSTR 资源，再完成响应。HRESULT 只映射安全状态/码，不输出任意 provider 错误文本。

UIA 元素的 ProcessId、可用 NativeWindowHandle/窗口归属可用于证明来源；虚拟元素若没有 HWND，要结合可验证的祖先窗口关系（遍历深度有界，最多 32）。不能确认来源时停止，不伪造 SourceApp。跨进程宿主/浏览器控件的实机兼容范围由实际证据限定，不承诺全部 provider 支持。

## 6. 构建/部署与自动化证据

- 新宿主加入 Iris.slnx、架构测试覆盖与 App build/publish 产物。父端产品路径只指向部署目录；发布目录烟雾验证不得依赖源码目录或本机已安装的开发副本。
- 实际 UIA worker 逻辑 + fake COM/native 适配器：验证 MTA、TextPattern2/1、范围顺序、caret/空白、敏感控件、长度/数量预算、来源变化、HRESULT、释放路径；不以另一份参考状态机替代。
- 实际父端客户端 + fake process/transport/time：验证 Ready/查询/退出预算、错帧/过大/迟到结果、取消启动、COM 永不返回的受控替身、Busy、永久停止、清理失败保留与重试、无 UI 阻塞。
- 实际自有宿主进程烟雾：仅握手、无效 Request、EOF 等不进入 UIA 的路径；另在不给有效请求的情况下验证实际 Kill 后 await 退出与管道释放。可用传输测试接缝构造沉默宿主；不向真实用户窗口发有效 UIA 请求。
- 所有故障路径先 finally 放行自有闸门，再等工作/子进程/流结束；保留 body 与 cleanup 错误。构造/DI/Host 解析零启动、零 UIA；测试检查源码中产品真实 COM 路径未被常关开关禁用。
- 运行 Release build、全量 test，并用自有临时输出 `dotnet publish Iris.App -c Release` 验证部署文件与实际宿主握手。不得启动真实 Iris 主程序或读取用户配置来做烟雾。

自动化不读取真实用户选区/剪贴板，不模拟输入、不装 hook、不激活源窗口、不访问用户设置/密钥/Run，不做 HTTP/性能脚本。真实记事本/Word/浏览器/终端/UIA provider、冷启动延迟与完整划词流程未验证则明确记录，后续单独人工验收。

## 7. 官方依据与后续范围

2026-10-04 实际读取 Microsoft [UIA Threading](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading)、[TextPattern.GetSelection](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtextpattern-getselection)、[TextRange.GetText](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtextrange-gettext)、[TransactionTimeout](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomation2-put_transactiontimeout)、[ConnectionTimeout](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomation2-put_connectiontimeout) 与 [.NET Process.Kill](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-10.0)，均 HTTP 200，缓存于系统临时目录 iris-t022-uia-official。

官方说明 UIA 应在无窗口 MTA 执行，COM 对象关联原 apartment；GetSelection 在无选择时可给退化空 range；GetText 支持长度预算；Kill 异步完成、须另等退出。进程隔离、预算、来源校验与协议为本项目的架构选择，不将它们冒称 Windows 保证。

T-022C 才定稿 A08 的剪贴板清空/序列归属/多格式恢复、外部更新保护、终端与焦点控件的键盘输入安全、UIA/clipboard 顺序组合以及 App 正常退出接线；T-023 才开放 Handler/设置入口。总目标和现有默认配置不缩减。
