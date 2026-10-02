# 输入、剪贴板与传入文本翻译入口

> 2026-10-03，Codex；T-016 / ARCH v1.26 正式规范。T-015-R5 / 5c5f78d 已验收。
> 本规范使用现有 IActionHandler、IActionDispatcher、ITranslateWindowPresenter 与 IClipboardService，不新增公开契约或配置字段。

## 1. 分层与注册

Core/Workflows 实现 InputTranslateHandler、ClipboardTranslateHandler、TranslateTextHandler；分别对应 IrisAction.InputTranslate / ClipboardTranslate / TranslateText。AddIrisCore 通过 TryAddEnumerable 注册各一个单例 IActionHandler，保持原 OpenSettingsHandler；不注册尚未实施的动作占位类。

Handler 构造无 I/O，只依赖 Core 端口及允许的 Logging Abstractions，不引用 App/WPF/Win32。不直接调用 Orchestrator、Provider、SecretStore 或窗口实例；实际翻译和重试由已验收 Presenter/VM 接管。

公开入口首先检查 context 非 null、调用方取消；之后按下面分支执行。所有文本保持原样，不手工截断、Trim 后替换请求或复制到另一份历史；预处理/长度/语言由既有窗口和 Core 执行。

## 2. 统一租约规则

- 除 InputTranslate 的直接隐藏分支外，在任何异步前置读取之前调用 TryBeginOperation(cancellationToken)。null 表示未接纳（关闭中或容量不足），直接结束，不读取剪贴板、不启动翻译、不用匿名 Show 兜底。
- 每次操作使用同一租约完成所有阶段的 TryShowAsync。返回 false 就安静结束，不追加通知、不重试成新意图，不复活旧结果。
- 在 finally/using 中 Dispose caller 持有；先等待自己发起的读取/发布真正结束，再归还。TryShow 返回后不等待整个翻译流；VM 已持有独立工作，caller Dispose 不取消它。
- 传给前置读取的是 operation.CancellationToken；调用方令牌后续取消仍按 T-015 的规则作用于接管后的工作。不要自行用 WaitAsync/超时丢弃仍运行的读取，也不加第二套 CTS 生命周期。
- OperationCanceledException 交给 Dispatcher 的静默取消边界，或在 Handler 中安静结束并归还租约，不提示“未知错误”。

## 3. InputTranslateHandler

沿用 ARCH §6.2：若 IsVisible && IsActive，则 await HideAsync 后结束，不分配新租约、不翻译。其余情况取得租约并发布：

| 字段 | 值 |
|---|---|
| Text | context.Payload（null 保留输入，空字符串明确清空） |
| Origin | TextOrigin.Input |
| Anchor | context.Anchor |
| FocusInput | true |
| AutoTranslate | context.Payload != null；实际空白/错误过滤仍由 T-015 规则决定 |

可见但不活跃时聚焦/展示，不误走隐藏；隐藏后的重新展示保留正文但不重新启动旧请求。测试用 ActionTrigger.Hotkey 验证第二次触发隐藏，此单不安装实际热键。

## 4. ClipboardTranslateHandler

取得租约后调用 IClipboardService.GetTextAsync(operation.CancellationToken)。不写剪贴板、不读图片、不实现模拟 Ctrl+C 或恢复流程。

- 成功取得非空白文本：同租约发布原始 Text、Origin=Clipboard、Anchor=context.Anchor、AutoTranslate=true、FocusInput=false。即使窗口当前可见/活跃也提交新文本，不走 InputTranslate 的切换隐藏分支。
- null、空串或纯空白：同租约发布 Text=""、Origin=Clipboard、AutoTranslate=false、FocusInput=true、Error=new ErrorInfo(InputEmpty, ErrorMessages.GetDefault(InputEmpty))。打开可输入的空白状态，不因 Text=null 或 AutoTranslate 默认值误译旧正文。
- 读取抛 IrisException：同租约发布安全 ErrorInfo（仅 Code 与 ErrorMessages.GetDefault(Code)）、Text=null、Origin=Clipboard、AutoTranslate=false、FocusInput=false；旧操作的错误应由 TryShow 返回 false 抑制，不改新会话。
- 未知读取异常：同上使用 Unknown 默认错误。日志仅记录错误码/异常类型，不记录 Exception 对象、Message/Detail/Data、正文、Payload 或 clipboard 内容。返回错误发布仍需 await。
- 发布器自身异常交给 Dispatcher 统一安全边界，不对它递归发布第二个错误。catch 的范围限制在前置读取，不把失败的错误展示再次捕获并循环展示。

取消/new input/隐藏发生在读取期间时，旧成功、空返回和错误都不得修改/重开窗口。覆盖这一点需要实际 Presenter 接线，而不是只确认 fake Presenter 收到参数。

## 5. TranslateTextHandler

用于现有 ActionContext.Payload 的明确文本提交；不切换隐藏、不读取剪贴板。Payload 为 null 或纯空白时抛 InputEmpty 的安全 IrisException，不创建新操作、不误译保留文本。非空白 Payload 原样交给窗口，零宽等预处理后为空的情形继续由既有 Core 规则处理。

取得租约后发布 Text=context.Payload、Anchor=context.Anchor、AutoTranslate=true、FocusInput=false。来源映射：CommandLine → TextOrigin.CommandLine，DragDrop / FloatingBall → TextOrigin.FloatingBall，其余触发 → TextOrigin.Input。

这只是当前 ActionContext 可表达的通用入口。后续 OCR/历史窗口需要 Screenshot/History 等精确 Origin 时，按对应工单直接使用公开展示租约端口，不能根据正文或控件名称猜来源；本单不增加 ActionContext 字段，也不实现那些窗口。

## 6. 托盘入口与生命周期

TrayIconController 在现有正式右键菜单中增加“输入翻译”“剪贴板翻译”，置于设置/退出之前；保留已交付菜单、托盘创建重试和通知。点击分别 await IActionDispatcher.DispatchAsync，对应 Trigger 均为 TrayMenu；托盘左键单击也路由 InputTranslate / TrayMenu。不得直接解析 VM 或调用 Presenter 绕过 Dispatcher。

已核对本地锁定包 H.NotifyIcon.Wpf 的 net10.0-windows7.0 XML 文档：TaskbarIcon.LeftClickCommand 表示图标左击执行的 ICommand，TrayLeftMouseUp 也有对应事件。选用一种接线（优先 LeftClickCommand），不要同时绑定两个入口造成一次点击多次分发；不新增全局鼠标钩子，不升级包。保持双击延迟的库默认行为，不为消除延迟叠加第二次执行。

新增命令/事件的 Task 有明确观察者与安全错误边界；构造/初始化不执行命令，Dispose 后旧菜单/命令不再分发，解绑事件。若正常退出需要等待已接纳前置读取，继续依靠 Presenter 的租约屏障；不以 Dispose 托盘强行销毁进行中的任务。

为验证接线可增加 App internal 的命令/菜单测试入口，但必须是生产实际使用的对象。无需在自动化中创建真实系统托盘图标；可沿用已有创建抑制/菜单构建测试方法。真实 Shell 左键交互若未执行就明确记录，不把直接调用命令说成人工点击。

## 7. 组合根与验收

实际 AppHostBuilder 注册必须解析到三个新 Handler、既有 OpenSettingsHandler、同一单例 Dispatcher/TranslatePresenter；ValidateOnBuild / ValidateScopes 不成环，构造不创建窗口。保持 T-014 的逐 Action 防重入和既有截图互斥规则，不改为全局串行。

至少覆盖：取消前不取租约/读取；空准入不工作；InputTranslate 的可见/活跃组合与 null/empty Payload；Clipboard 的成功/空/已知异常/未知异常/取消；TranslateText 的来源映射和原文保持；租约 finally 归还；Show 返回 false 不兜底重开；同一 Action 的完整读取期间防重入；不同输入意图使旧读取成功/空/失败均无法覆盖。

端到端测试从真实设置 VM 保存隔离服务配置、真实 Registry/Orchestrator，经实际 Dispatcher → Handler → Presenter → Window/VM 显示有序结果，并验证 caller 归还后重试仍可用。Provider、SecretStore、Clipboard、AutoStart、桌面/原生定位等外部边界使用 fake，路径隔离；测试禁止真实 HTTP、用户剪贴板/设置/启动项或性能脚本。

补托盘菜单和左击命令实际接线、关闭后不分发的测试；保持已有设置菜单测试。新增字符串走 UiText/资源约定。异常不重复通知、不泄露文本/凭据。

T-016 完成后可以从托盘开始输入/剪贴板翻译；T-017 再接实际全局热键。真实服务、人工键鼠/IME、多屏/DPI 与性能仍按各阶段单独验收，不提前宣布 M2 或全项目完成。
