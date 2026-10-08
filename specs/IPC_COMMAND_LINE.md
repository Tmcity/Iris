# 命名管道 IPC 与命令行

2026-10-08 · T-037 · ARCH v1.55 · CCR-037。

## 1. 范围与分层

完整实现 ARCH §10.1 的第二实例转发、主实例接收及本次启动参数。使用已存在的 IpcCommand、IpcConstants、IActionDispatcher 和业务 Handler。Platform.Windows/SystemIntegration 实现命名管道客户端/服务器及必要原生身份边界；App 实现纯参数解析、接纳/分发与启动退出接线。现有 Platform→App 内部友元足够，不新增 Core IPC 服务端口或 NuGet 包。

Core 只同步 CCR-037 预置的 IpcCommand 安全 ToString 与说明，构造、属性、解构和常量值不变。M6 继续延期，不补 ToggleFloatingBall Handler；不提前国际化迁移、更新器或打包，不改已验收的网络/OCR/历史算法与配置。

现有 Core 未注册 Exit Handler，允许在 App 内增加仅调用 IAppLifetime.RequestShutdown 的生命周期适配 Handler；它必须立即返回，不能直接等待会反向排空自身 Dispatch 的 StopAsync。其余既有业务 Handler 保持。

## 2. 参数与用户反馈

支持以下互斥形式，参数按操作系统交给 Main 的 string[] 解析，不重新拆空格/引号：

- 无参数：新主实例保持现有托盘启动行为；第二实例发送 InputTranslate。
- `--action <IrisAction>`：只接受已定义的枚举名称，可忽略名称大小写；禁止数字、空值、未知值。TranslateText 要求使用带文本的 `--translate`，给出固定用法提示。已定义但未注册的动作仍由现有 Dispatcher 提示尚未开放。
- `--translate <text>`：生成 TranslateText，完整保留 Unicode、换行、引号及首尾空格；空白文本给出固定错误提示，不提交命令。不得拼接多余参数猜测原文。
- `--help` 或 `-h`：显示固定用法，退出码 0，不启动主实例或发送命令。

缺值、重复开关、混用两种动作形式、未知开关或多余参数均无副作用地退出码 2。固定用法/失败提示使用现有 UiText 体系；不能回显原始参数、Payload、JSON、管道名称/SID或底层异常消息。WinExe 没有控制台时仍有可见反馈，使用可替换的启动期原生对话框边界，测试不显示真实对话框。

有效第二实例收到接纳确认后退出码 0；连接/协议/接纳失败退出码 1。确认只表示主实例已接纳分发责任，不代表截图已完成、翻译/API 成功或动作未被既有防重入规则忽略。

## 3. Main 与主实例启动

解析/校验应在用户数据目录、日志器、Host/WPF 初始化之前完成。第二实例只做必要的用户/会话身份、Mutex 检查和 IPC，不读设置/密钥、不创建数据目录或 Serilog sink、不构建 Host/App。未知参数不能作为 Generic Host 的任意配置选项；含原文的 args 不传给 Host 命令行配置源或日志，初始命令与 Host 配置分开传递。

保留同步 `[STAThread] Program.Main`。单实例 Mutex 必须由获取它的同一个 Main 线程在 finally 释放，即使 Host/日志器收尾失败；不改成 await 后可能跨线程释放 Mutex 的 async Main。仅批准第二实例在尚无 WPF Application/Dispatcher 的启动边界，用一个明确的同步桥等待完全异步、无 UI 上下文依赖且有总期限的客户端。该例外不扩散到业务/UI/后台服务中的 `.Wait`/`.Result`。

保留 Main 入口的启动计时起点，不能将解析、身份查询与 Mutex 的耗时排除在原启动日志之外。主实例已成功建立日志器后发生的启动故障，在 CloseAndFlush 之前保留安全故障记录（阶段/异常类型/已知错误码），不记录原始参数或异常消息；第二实例仍不创建日志器。

主实例完成设置、翻译/OCR Registry、数据库、主题/托盘及 Host 启动后，开放 IPC 并接纳本次初始命令，Trigger=CommandLine、Anchor=null，Payload 原样传递。启动命令最多一次，不能在多个入口重复解析后重复派发；无参数主实例不额外打开输入窗。构造/DI 验证不创建管道；启动失败或停止先发生不能稍后重新开放接收/执行初始命令。

## 4. 本机身份与管道

保留名称约定 `IpcConstants.PipeNamePrefix + 当前用户 SID`，Mutex 前缀保持。客户端只连本机 `.`；双方使用异步命名管道与 CurrentUserOnly。服务器首次创建使用 FirstPipeInstance，不能悄悄加入另一个进程占用的同名服务；占用/权限问题安全失败，不生成随机生产名称绕开单实例约束。

双方同时核对管道对端会话与本进程会话，避免 Local Mutex 与机器范围管道在同一用户多个登录会话间误转发。允许 Platform 通过 GetNamedPipeClientSessionId / GetNamedPipeServerSessionId 等必要只读原生元数据实现，调用失败或会话不同即拒绝；客户端在写任何 Payload 前验证。客户端使用 Identification 等足够且不允许对方代用其身份执行操作的身份模拟级别。不得修改全局 ACL、提升权限或放开为所有用户。

生产名称/身份由一个内部边界提供，测试仅用自己创建的随机名称与自有进程；不能探测/连接当前真实 Iris 管道。保持现有前缀+SID意味着其它登录会话已占名时可以明确启动失败，不得把命令送到那一会话，也不在本单修改已落地名称常量。

## 5. 有界线路与确认

每连接处理一条命令，UTF-8 无 BOM 的紧凑 JSON + LF，可接收 CRLF；处理后发送一条有界固定确认并关闭/断开此连接，不执行附带的第二条命令。JSON 属性和枚举规则来自 `new JsonSerializerOptions(IrisJson.Options)`，只在私有副本上关闭 WriteIndented；不改全局 Options，否则设置文件会回归。字符串中的换行必须经过 JSON 转义。

每帧最多 256 KiB UTF-8 字节（不含行终止符），增量读取时即限制预算；不先无界 ReadLine/ReadToEnd 后才检查。严格 UTF-8，拒绝截断、坏编码、非对象、缺失/重复 action、未知属性、非字符串枚举/数字、无效枚举及类型错误；属性重复按 IrisJson 的不区分大小写规则判断。Payload 仅可用于 TranslateText 且必须为非空白字符串，其它动作要求空/缺省。不得将坏帧内容放进错误、日志或通知。接收端和发送端使用同一有效命令规则。

客户端有从连接到确认的 10 秒软总期限；服务端每条连接从接纳起到完成帧/确认有 10 秒软期限，慢客户端不能永久阻塞后续连接。使用 TimeProvider 控制时间，取消后仍 join 实际 I/O/清理，不以 WaitAsync 超时遗弃任务。可以串行接收或使用明确的小并发上限；连接、帧缓冲、待分发责任均不得无界积压。

确认使用内部固定 ASCII 状态行（例如 accepted/invalid/busy/stopping），最大 64 字节，不携带文本。仅成功登记完整分发责任后才能回 accepted。客户端可等待主实例启动期间的连接可用；一旦开始写命令，禁止自动重连重发，避免确认丢失造成重复动作。部分写入、确认丢失或超时用固定提示说明“未能确认是否已接收”，不声称动作一定未执行。断开后的清理重试也不得重新分发命令。

ACK 写入完成不证明对端已读取，服务端不能通过立即 DisconnectNamedPipe 丢弃正常客户端尚未读取的确认。连接结束也须受原期限与完整清理约束；以真实管道的受控延迟读取验证，不用无法取消的同步 flush/drain 阻塞 UI 或共享线程池。

## 6. 动作责任与退出

所有动作经过实际 IActionDispatcher，不直接调用业务 Handler，不绕开防重入/截图互斥/错误通知。接收只负责有界校验、登记和确认，不能等用户完成截图或翻译才允许其它连接/Exit。被接纳的完整 Dispatch Task 有明确拥有者，所有路径都观察完成/错误；不能用未追踪的 Task.Run/async void 丢弃。

AppBootstrapper.StopAsync 的同步接受点先封闭 IPC 与初始命令接纳，再封闭现有 Dispatcher/窗口/网络等准入。停止监听和取消在途读写的责任与完整动作等待分开：先让既有窗口/截图/业务拥有者按原顺序收尾，再等待完整分发，避免 Exit 通过 IPC 触发停止时等待自身，或在关闭窗口之前等待依赖窗口退出的 Handler。Host 和 Dispatcher 销毁前必须完成所有管道任务、确认写入、实际 stream 清理和已登记分发。

重复 Stop 共享同一次未完成清理；真实清理失败保留原对象与失败责任，后续重试只收尾，不能重新接纳/发送/执行旧命令。关闭准入后不再通知 UI 的迟到成功。正常退出不能只依赖 Host 的五秒超时；同步退出回退至少封口并请求取消，不冒称替代完整异步停止。

## 7. 验证与完成

验证实际解析器/codec、真实 NamedPipeServerStream/ClientStream 的自有随机管道组合、实际接纳/Dispatcher、真实 Host/Bootstrapper 组合；fake 仅替换外部 native/用户业务边界。覆盖 Unicode/换行往返、紧凑单行且全局 IrisJson 未变、数字枚举与重复属性拒绝、分片/截断/超限/坏 UTF-8、慢连接取消、断连/确认丢失不重发、对端会话不符写入零次、权限/占名失败、连续客户端及停止后拒绝。

使用门闩证明取消仍等待非合作 I/O、重复 Stop 共用责任、清理失败原实例恢复、已接纳 Dispatch 的最后 UI 回调仍被等待、Exit 不自等待、停止中的启动不重新开放。每个门闩从取得资源起受 finally 保护，先放行再 join；保留首次失败与清理错误。

主/第二实例和非法参数测试必须调用实际启动分支，边界注入计数证明第二实例零 Host/日志/用户路径初始化，并验证 Mutex 同线程释放；不通过真正运行 Iris.exe 访问用户环境来代替测试。实际 ScreenshotTranslate Handler 配 fake 截图/OCR/Presenter 证明映射；实际 TranslateTextHandler 配受控 Presenter 证明 CommandLine 来源和原文不变。

允许本单创建自有随机命名管道/Mutex和必要当前进程/用户/会话元数据查询；禁止真实生产管道、用户配置/密钥、桌面输入/剪贴板/选区/截图、远程网络或性能脚本。不启动真实主应用，不冒称已完成多用户/RDP/物理桌面验收；保留既有获准只读 native/合成 SDK smoke。

完成后串行 Release build 零警告错误、全量 test 全绿。归档完整工单及真实报告 `tasks/done/T-037.md`，保留所有日志与当前架构师 C 结案证据（含被 ignore 的 .log），本地独立提交后请求复审。不 push，不改写历史，不修改或提交 .codex。
