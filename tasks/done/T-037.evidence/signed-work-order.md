# T-037：命名管道 IPC 与命令行

2026-10-08 · 架构师签发 · master · 基线 `11263780f2a34dd9ee4dc998d80dfdf605dacaf0`。

T-036C、父 T-036 与 CCR-036 已独立验收结案：Release 零警告错误，原 Reload 定向 1 项、真实 Tester/UI 探针通过，全量 3553（3551 成功、0 失败、2 既有环境跳过），48 张最终独立图与已查看版本一致。完整证据 `tasks/done/T-036C.review`；保留旧失败。

## 唯一范围

必读 `specs/IPC_COMMAND_LINE.md` 全文、ARCH v1.55 / §0 / §10.1、CCR-037 与 CONTRACTS 的 IpcCommand 声明，以及现有 Program、Bootstrapper、ActionDispatcher 和业务 Handler。完整交付第二实例转发、主实例接收、初始命令处理、参数错误/帮助与真实退出，不以 parser 或孤立管道原型代替接线。

1. 纯参数解析：无参数主实例保持托盘启动，第二实例 InputTranslate；--action、--translate、--help/-h；严格互斥/数字拒绝/文本保持/固定安全提示与退出码。第二实例不初始化数据、日志器、Host/WPF；原文不流入通用 Host 配置。
2. Platform 的实际本机、同用户/同会话命名管道，前缀+SID保持，CurrentUserOnly/首次占名与对端会话校验。紧凑 UTF-8 JSON 行、有界读取/确认/责任、10秒软期限、确认丢失不重发；全局 IrisJson 不变。
3. 实际 Dispatcher 与主实例启动接线，完整记录异步责任，先封口再排空，Exit 不自等待、窗口依赖先正确退出；重复停止共享责任，失败原资源重试、不重发命令。
4. 规范 §7 的实际组件/真实自有管道、启动分支和关停门闩验证，以及最终串行 Release/full。假边界不能替代整个被测实现，日志如实保存。

## 契约与权限边界

CCR-037 已批准并预置：只同步 IpcCommand 安全 ToString 与注释，原签名/常量不变；无新 Core IPC 端口、错误码、设置字段、默认/schema或包。允许必要 Platform/SystemIntegration/native 元数据边界、App Program/Host/Dispatcher接纳生命周期及测试接线；不重写已验收的业务/网络/OCR/历史底座。只能在尚未创建 WPF 的第二实例 Main 边界使用规范批准的同步异步桥，保持 Mutex Main 同线程释放。

允许测试创建自己的随机命名管道/Mutex及当前进程/用户/会话只读元数据；禁止连接真实 Iris 管道、启动真实主应用或访问用户配置/秘密/桌面内容/输入/外网/性能脚本。原有获准 SDK/只读 smoke 保持。M6延期、OCR仍复用翻译原文；T-038以后不提前。

开发对话唯一 `01a11b01-c46b-74e1-9015-726c77ffbc9e`，一次一单，不建代理/新会话；不 push/reset/clean/rebase/amend，不改删或提交 .codex。保留架构师本轮 C 结案与全部复审源、日志、图像、指纹、新规范/CCR/交接并随本单提交。

完成时归档工单全文及实际报告至 `tasks/done/T-037.md`、移除 current_task，创建独立本地提交，回报完整 hash 请求复审。BACKLOG 的状态只由架构师修改，不自行结案或开始下一任务。

## 开发期代码预审补充：取消后不能继续启动下一阶段 I/O

2026-10-08，当前早期 NamedPipeIpcClient.RunCoreAsync 在 WriteAsync 返回后直接进入 ReadLineAsync；IpcProtocol.ReadLineAsync 的循环仅把 token 传给底层 ReadAsync，没有在各次读取前显式检查。若底层忽略取消，写入在期限/停止之后才结束，会继续启动一个新的读取；已在途读取取消后返回不完整片段时，也可能再读一次。Server 的非合作 Accept 迟到返回后同样须检查封口/取消，再决定是否进入连接处理。

这是源码控制流预审，尚未运行独立故障探针，不将其写成已实测失败。请按规范现有软期限/完整等待要求核对并修正，加入实际 Client/Server/codec 配窄 I/O 门闩的回归：写入或 Accept 先 entered，取消/推进期限，再放行忽略取消的旧操作；新 Read/分发次数应为零。部分帧读取的旧操作取消后返回，也不得继续下一 Read。保留真正仍在执行的旧 I/O 等待、原资源清理/失败重试、确认丢失不重发，不以丢弃旧任务解决。

同时验证尚未 Start 的实际 IpcCommandCoordinator/真实 Host 可以正常同步 Dispose，以及主实例启动失败前未开放 IPC 的清理。早期 Coordinator.Dispose 先启动 Server.StopAsync 再立即检查 IsCompletedSuccessfully，而 Server.CleanupAsync 无条件 ForceYielding，即使从未监听、没有在途工作也可能仅因调度尚未完成而报错。不能依靠线程池恰好先完成让此路径偶然通过；要区分无在途资源的同步释放与必须真正异步等待的已启动状态。已启动管道仍保持完整 async drain，不为同步 Dispose 提前放弃责任。此条也是源码预审，待实际回归验证。

Main 重构还须保持既有入口计时起点及主实例日志器建立后的安全故障记录；当前早期代码在 Acquire 之后才取 Stopwatch 时间，且 RunPrimary 的异常直接经过 finally 关闭日志器，丢掉原主实例故障日志。按 IPC规范§3保留起点与脱敏故障元数据，不恢复原始参数/异常内容日志，也不为此运行性能脚本。App 内只请求退出且立即返回的 ExitActionHandler 已明确允许，属于必要生命周期适配。

## 开发期代码预审补充：确认写入不等于客户端已经读取

早期 Server 在 ACK 的 WriteAsync 完成后结束 connection cancellation 对象并立即 Disconnect。Windows 的 DisconnectNamedPipe 会丢弃管道内尚未读取的数据，不能将写入完成等同于对端已读完确认。请加入实际随机管道、仅在客户端底层 ACK 读取处设置门闩的场景：实际服务器确认写入已完成、客户端暂未读取，然后在预算内放行读取，正常命令仍应收到 Accepted，且只接纳一次。

核对正确的连接结束责任（例如有界异步等待客户端完成读取并关闭），保持10秒软期限、坏/慢客户端不会永久占住服务、额外帧不分发、Stop/Exit不自等待。不要用无取消的同步 WaitForPipeDrain/FlushFileBuffers 长时间占住 UI 或共享线程池，亦不能提前丢弃在途清理。此项也是源码/平台语义预审，尚无独立运行失败日志；若采用其它可靠结束方式，以实际延迟读取与关闭验证说明。
