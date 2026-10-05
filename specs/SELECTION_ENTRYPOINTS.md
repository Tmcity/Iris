# 划词翻译入口与操作通知

> 2026-10-05，Codex；T-023A / ARCH v1.34 / CCR-018 正式规范。T-022C2-R1 / ceda922 已独立验收。
> T-023 总范围保持 Handler + 基础设置；本单 A 实施入口与通知，B 后续实施联合保存与设置 UI。T-024 鼠标入口不提前实施。

## 1. 注册与操作范围

Core/Workflows 新增 SelectionTranslateHandler，对应 IrisAction.SelectionTranslate，AddIrisCore 用 TryAddEnumerable 注册单例 IActionHandler。构造只保存端口，不读设置/桌面/选区，不创建窗口或执行 I/O。HotkeyCoordinator 继续从真实 handlers 派生已开放动作，因此既有默认 Ctrl+Alt+D 在本单开放；不修改默认绑定、不增加真实注册测试或托盘划词菜单（点击托盘会改变前台语义）。

入口检查 context 和调用方取消，然后先 TryBeginOperation。null 准入直接结束；同一租约持有至读取与发布任务真正结束，再 finally Dispose。不得 WaitAsync 提前脱离实际读取、创建第二个意图或直接调用翻译 Orchestrator。使用 operation.CancellationToken；取消安静结束，旧成功/空结果/失败都必须由发布身份屏障拒绝。

本单只处理当前前台选区，不猜测 ActionContext.Payload 是缓存选区。ActionContext 与 TranslateWindowRequest 均没有 SourceApp 字段，不增造字段；result.SourceApp 不能写入日志。T-024 将另行规定鼠标缓存文本移交，不能复用“点击图标后重读前台”的历史草图。

## 2. 捕获与三个结果分支

租约取得后捕获一次 Settings.Current.Selection，深复制 ReadOrder 与 ClipboardUnsafeProcesses，再构造 SelectionReadOptions（Order、RestoreClipboard、ClipboardTimeoutMs、ClipboardUnsafeProcesses）；OpenInputWhenEmpty 也来自同一次快照。进行中的读取不受后续设置变更影响。

保留 ARCH §6.3 的 Iris 自身前台特例并明确其含义：通过 IDesktopContext.GetForegroundApp 的 ProcessId 与 Environment.ProcessId 比较；确认是自身时不调用 Reader，以同一租约显示输入界面，Text=null、Origin=Input、AutoTranslate=false、FocusInput=true、Anchor=context.Anchor，保留原文且不自动重译、不因窗口已活跃而隐藏。不调用 InputTranslateHandler 重新分配租约。无法确认自身时走 Reader，由已验收 C2 执行真实来源/焦点安全检查。

外部来源 await ISelectionTextReader.ReadAsync(options, operation.CancellationToken)：

| 结果 | 同一操作的发布 |
|---|---|
| 非空白 Text | TryShowAsync：原样 Text、Origin=Selection、Anchor=context.Anchor、AutoTranslate=true、FocusInput=false；不 Trim/截断，不自己做软换行合并 |
| null/空串/空白，OpenInputWhenEmpty=true | TryShowAsync：Text=""、Origin=Selection、Anchor=context.Anchor、AutoTranslate=false、FocusInput=true，Error=安全 SelectionEmpty 默认错误；明确清空而不误译旧正文 |
| null/空串/空白，OpenInputWhenEmpty=false | TryNotifyAsync(operation, Warning, SelectionEmpty)，不创建/激活/隐藏翻译窗口，不修改已有正文 |

读取/设置捕获/桌面查询的已知 IrisException 使用其 Code + ErrorMessages.GetDefault(Code)，未知异常使用 Unknown；同一租约 TryShowAsync 安全错误，Text=null、Origin=Selection、AutoTranslate=false、FocusInput=false、Anchor=context.Anchor。取消不发布。日志最多记录阶段、错误码、异常类型，不传异常对象/Message/Detail/Data/Text/SourceApp/窗口标题。只捕获前置工作异常，TryShow/TryNotify 的异常不递归发布，交给 Dispatcher 的既有安全边界。

## 3. CCR-018：带操作身份的通知发布

在 ITranslateWindowPresenter 增加且仅增加：

```csharp
Task<bool> TryNotifyAsync(ITranslateWindowOperation operation, NotificationLevel level, ErrorCode code);
```

- 与 TryShow 共用同一操作身份、准入容量、进行中发布所有权及退出屏障。同操作 Show/Notify 或 Notify/Notify 同时进行抛 InvalidOperationException；不能新增独立无界通知任务表。null/外来 operation 抛 ArgumentException（null 可 ArgumentNullException）；过期/取消/调用方已释放/退出中返回 false。
- 通过真正的 UI 调度，在最终 UI 接受点校验身份后立即尝试通知；不得校验后调用旧 Notify 再排入第二个无身份 UI 队列。该接受点是意图顺序的提交边界；后来的新意图不要求撤回已交给 Windows 的通知。
- 标题使用固定 Iris 文案，正文只由 ErrorMessages.GetDefault(code) 产生；SelectionEmpty 已存在且文案为“未检测到选中的文本”，不新增 ErrorCode、不接收任意消息/正文/异常。非法枚举参数可抛 ArgumentOutOfRangeException，须在任何副作用前验证并记录测试。
- App 内部在 TrayNotificationService / TrayIconController 增加实际使用的同步立即尝试路径，只有已创建且未释放的托盘可接受。无托盘、尚未就绪、已释放或原生通知失败返回 false，不能进入原最多 10 项的普通待通知队列，也不能改为打开输入窗、等待托盘就绪或重试为新操作。成功表示 App 已提交通知，不保证 Windows 通知策略一定显示气泡。
- 不创建翻译 window/scope/VM，不取得翻译会话所有权，不占主/重试翻译槽。复用发布持有时不得意外释放同一 operation 原有的 VM session ownership；Notify 的完成仅归还它自己取得的发布持有。
- 任意线程均可调用，UI 异步排队期间仍持有发布责任，caller Dispose/取消及 Shutdown 不能让状态被提前销毁；实际调度任务结束后才释放。失败保留安全可见错误，不启动递归通知，不把通知失败算作翻译窗口激活成功。
- 既有 INotificationService.Notify、普通启动通知排队和托盘创建重试语义保持。只新增本单需要的 App internal 边界，不引入新的 Core 通用通知服务或平台项目依赖。

## 4. 验收证据

使用实际 Handler/Presenter/Dispatcher/HotkeyCoordinator 及实际通知路径作为主体，fake 只替换 Reader、native、外部 provider/配置等边界。最低覆盖：

1. 无准入/预取消不读取；自身前台不读；设置仅捕获一次且两个列表隔离；成功原文/Origin/Anchor；空的两种分支；已知/未知异常/取消；发布 false 不兜底；发布异常只一次；最后完整归还。
2. Reader 闸门真实 entered 后插入新意图/隐藏/取消，依次放行旧成功、空、错误，均不修改新状态。相同 Action 完整读取期间防重入，不改变不同 Action 并发规则。
3. 通知真正进入 UI 队列后变旧/取消/Dispose/退出，最终拒绝；合法通知只一次且无需创建窗口；未就绪/释放/原生失败 false，不进入普通队列。真实 TrayNotificationService → TrayIconController 内部路径需测试，不能仅断言 fake Presenter 接收了参数。
4. Notify/Notify、Show/Notify 并发，外来/null 操作与非法枚举；等待中的发布占用退出屏障，释放后回到原容量；通知不会清除原有会话持有、破坏随后正常翻译/重试。
5. 实际组合根恰好一个 SelectionTranslateHandler、同一 Reader/Presenter；默认热键成为 delivered action；从 fake 热键事件经真实 Dispatcher → Handler → Presenter → Window/VM 验证选区结果与 Origin=Selection 的既有预处理。不要注册用户真实全局键/读取真实前台或创建系统托盘。

新增异步测试从第一项资源/闸门起 finally 保护，先放行所有自有闸门再 await 实际任务/线程/服务退出；同时保留主异常和清理失败，不用固定 Delay 猜测 entered。串行 Release build 与全量 test，保留两项环境跳过与历史首轮失败记录。真实桌面应用、物理输入、通知气泡和性能没有执行时明确未验证。
