# 翻译窗口、会话归属与有界展示

> 2026-10-02，Codex；T-015 / ARCH v1.25 / CCR-013 正式规范。
> 取代 TRANSLATION_SESSIONS_DRAFT.md、T-015_SESSION_DECISIONS_PREPARATION.md 的候选决策；实现与行为仍须 T-015 独立验收。

## 1. 范围与所有者

T-015 交付 TranslateWindow、ViewModel、ProviderResultCard、语言选择、Presenter、定位及退出接线。Core 的已验收 Orchestrator、Registry、缓存/历史协议不变；不重做翻译网络管道。T-016 接 Handler / 托盘，T-017 接热键。

Presenter 为 App 单例，构造只保存依赖，不创建窗口、查桌面或启动工作。窗口/VM 由 Presenter 在 UI 线程按需创建并持有，普通关闭/隐藏保留内容；正常应用退出才关闭窗口并释放所属资源。可以使用一个 Presenter 持有的惰性 scope，不把窗口提前解析到启动路径。不增加常驻线程、通用任务总线或全局 Action 串行锁。

Presenter 同时持有操作、取消收尾、实际翻译消费者、复制任务与窗口创建/清理任务；这些任务不能因换会话或隐藏而从所有权记录消失。UI 状态只在 UI 线程写；公开 IsVisible / IsActive 是线程安全快照，不能从后台直接访问 Window。

## 2. 公开端口（CCR-013）

准确声明见 CONTRACTS.cs / PresentationPorts.cs。新增 opaque `ITranslateWindowOperation : IDisposable`，含只读 Id 与 CancellationToken。用 `TryBeginOperation(cancellationToken)` 接纳意图，用 `TryShowAsync(operation, request)` 发布；移除尚无实现/调用方的匿名 ShowAsync。IsVisible / IsActive / HideAsync 保留。

- TryBeginOperation 可在任意线程同步调用；已取消令牌抛 OperationCanceledException，**不废弃当前意图**。正在退出或操作容量耗尽返回 null，调用方不得开始读取/OCR/翻译，应使用固定的忙碌/退出提示或结束动作。
- 接纳成功才分配不复用的 Guid、成为最新意图，并撤销旧操作发布资格。接纳顺序决定优先级，不以后台完成顺序决定。取消外部回调不在状态锁内执行。
- 全 Presenter 最多保留 **8 个尚未完全回收的操作状态**，包括前置读取持有者、VM 接管、进行中的发布以及异步取消收尾；耗尽后拒绝新操作，不能无限增加 Task/CTS。手动编辑/隐藏仍能立即使旧操作失效，无需分配新 CTS。释放后恢复准入。
- operation 只能用于创建它的 Presenter。null 参数或外来/伪造 operation 抛 ArgumentException（null 可 ArgumentNullException）；本 Presenter 已过期、取消或调用方已 Dispose 的租约返回 false。不得接受仅 Id 恰好相同的伪造对象。
- TryShowAsync 在 UI 回调实际执行时再次检查身份/取消/租约/退出状态；无效发布不创建、不重开窗口、不修改正文/卡片，也不展示迟到错误。有效发布返回 true 表示本次 UI 更新已接受，不表示翻译完成。
- 每个操作最多一个未完成 TryShowAsync；调用方必须 await 后再发布，同操作并发发布抛 InvalidOperationException。不同操作的排队发布受上述容量限制。
- 进入 TryShowAsync 就取得该次调用的工作引用；Dispose 和已排队发布竞争时，在 UI 更新之前 Dispose 胜出则返回 false。UI 接受一次翻译时，VM **先取得独立工作所有权**再返回 true；Handler finally 的 Dispose 只归还调用方持有，不取消已接管的翻译。
- 调用方令牌对已接管工作继续有效，直到该操作的所有工作退出。Dispose 不等同取消。没有其他所有者时才注销 registration / 释放 CTS；失效状态的异步取消回调退出之前不能释放其 CTS。Dispose 幂等、不在 UI 同步等待取消回调，不泄漏每次操作生成的后台取消任务。
- HideAsync 完成表示本次隐藏及资格撤销已生效，**不等待不合作的 I/O**。完整退出另由 App internal ShutdownAsync 等待。

未来 T-016：先取得 operation，再调用 GetTextAsync(operation.CancellationToken)，返回后用同一 operation 尝试发布；所有路径 finally Dispose。T-031 在截图/识别之前取得 operation，阶段状态和最终正文复用同一租约。未取得租约不启动前置工作；租约必须持有到该工作真正结束，不可仅因超时就归还仍运行工作的名额。

## 3. 正文、显示与失效

TranslateWindowRequest 不增加可省略的 SessionId 字段。Text=null 保留原文；Text="" 明确清空。自动翻译仅在本次显式提供非空白 Text、AutoTranslate=true、Error=null 且 StatusMessage 为 null/空时触发。空白/错误/加载阶段不自动翻译旧正文；Error 优先于 StatusMessage。显式正文可以显示供编辑，即使本次不翻译。

同一操作允许先显示加载、再显示正文，但一个操作至多提交一次自动主请求；再次重译必须新操作。同一请求重复发布不得悄悄启动多次消费者。显式无效/过长翻译输入按现有 InputEmpty / InputTooLong 显示安全错误；不可静默截取后发送。原文控件保留可修改内容，支持现有 MaxInputChars 校验，超限不发请求。

手动修改原文或源/目标语言立即撤销旧操作资格，取消旧主请求/重试和等待项；旧卡片可保留供查看，明确标记为上一次结果。按 Enter/翻译按钮才取得新操作。程序应用本次请求正文不当成用户编辑反向取消自己；使用明确的更新来源或受控抑制。

用户关闭、Esc、有效失焦隐藏全部走同一失效入口；再显示保留内容不恢复旧 RequestId 的资格。普通停止按钮取消当前主/重试/等待项、撤销副作用资格，保留已显示文本并显示已停止；以后重新提交创建新操作。

截图临时隐藏另称 CaptureSuppression，只作 App 内部未来边界；不能用普通 HideAsync，不能因它产生的失焦取消自身操作。T-015 不实现截图，但不得把该区分堵死。恢复需同时匹配意图与可见性代次。

## 4. 主调用、单卡重试与退出屏障

卡片绑定为 (Operation.Id, ProviderId, RequestId)，不修改 Core TranslationEvent。接受事件和执行延迟 UI 刷新时都检查绑定；不依赖取消及时性。

整个 Presenter 跨会话最多 **一个实际主调用、一个实际单卡重试、一个最新待启动主请求、一个最新待启动重试**。实际调用从开始消费一直计数到 EOF、DisposeAsync、全部 App finally / UI 收尾结束；TranslationFinished 是业务事件，不是实际工作退出。Core 在 Finished 前已经释放 Registry 租约，不能把 App 等待误写成 Core 仍占租约。

新整窗意图使旧请求失效并替换最新等待主项、清空等待重试；必须等旧会话的主调用和重试都完整退出才启动新主调用。等待描述只保留最新项，撤销项必须归还所持引用。UI 可继续编辑并显示等待状态，不同步阻塞。

主 TranslationStarted 按 descriptor 顺序创建卡片。ProviderStarted / Delta / Completed / Failed 只改变对应绑定。Completed 用权威 Text 覆盖；失败或取消不得被迟到 Delta 改回流式。请求在 Started 前失败显示整次错误和重新翻译入口，不伪造可重试卡片。所有异常和终态都清理相应等待/忙碌标记。

仅当前有效操作的终态卡片能重试。同卡正在运行或等待的重复点击忽略；不同卡请求替换唯一待启动重试。重试只替换目标卡 RequestId，清除它的旧文本/缓冲，不中断主请求其它卡片，也不重建全集合。使用会话原始输入、Origin、主 Started 已解析语言、ProviderIds=[id]、BypassCache=true、RecordHistory=false；配置/Prompt 使用重试启动时当前 Registry，不承诺重放旧网络字节。服务已删除/禁用按 Core 显式选择规则显示本卡错误。主 Finished 不能清除尚在执行的重试状态。

退出先原子拒绝新准入、使全部操作失效、取消待办，再异步观察前置租约、窗口创建、真实消费者、复制、取消回调和 UI 收尾，最后 Close/Dispose。多个 ShutdownAsync 等待同一进行中的屏障。AppBootstrapper 在停止主题/托盘、Host 和 Dispatcher 之前等待设置及翻译两条退出路径；失败保留资源与可重试所有权，不报成功、不遗忘隐藏 HWND。不要用超时后 Dispose 正在工作的资源来制造退出完成。取消不合作的测试依赖必须在 finally 放行。

## 5. 展示预算与刷新

以下为 UTF-16 code unit 预算，不等同实际进程内存；不改 T-013 的事件流/输出上限：

| 对象 | 上限/行为 |
|---|---|
| 当前保留的卡片正文 | 每卡 1,048,576，全部卡片合计 4,194,304；跨重试共同记账 |
| 尚未送入 UI 的增量缓冲 | 全窗口合计 262,144；满时等待既有刷新完成产生背压，不无限堆积 |
| 输出刷新回调 | 全窗口最多一个待执行或正在执行；约 33ms 合并一次，跨会话共享名额 |
| 渲染快照 | 最多一个待交付批次，正文总量不超过上述保留正文预算；交付/失效即释放 |
| 复制 | 一个实际写入、一个最新待写入；持有的正文分别不超过单卡上限，替换待项释放旧文本 |

切会话也不能另开一个输出刷新队列。旧回调取消/失效必须唤醒等待容量的消费者；消费者异常也释放缓冲，避免等一个永远不会发生的刷新。Completed 清除同 attempt 的待刷增量后应用权威正文，Failed/取消结束后不再拼接。不得为每个 Delta 建 Task/BeginInvoke 或反复复制整串。

跨重试可能超过保留正文总额：只保留预算允许的前缀，显示**“本地显示已截断”**，与模型 Truncated 分开；不能随后保留隐形全量 string 绕过预算。此卡禁用自动复制；手动按钮明确为“复制可见内容”，只复制实际保留文本，不宣称是完整译文。完整且未达到本地上限的卡片仍复制权威正文。旧卡释放/重试清空时归还预算；prefix 截取不切开 UTF-16 代理对。Core 历史仍按原协议保存，不由 UI 为凑预算删除历史。

卡片元数据数量随本次配置的 Provider 数量变化；上述预算只证明正文、任务和刷新有界，不声称整个应用具有常数堆内存。只读结果关闭 Undo；原文编辑历史设有限 UndoLimit，不能无限保留长文本快照。

## 6. 复制、错误与配置

AutoCopy==FirstResult 时，仅初始主请求第一个在 UI 有效接纳的完整、非模型截断、非本地截断 Completed 能领取一次资格；cache hit 可参与，重试不参与。设置策略在本次主请求提交时捕获。剪贴板动作及其真正完成属于操作所有者；排队和 SetTextAsync 前再校验资格，传操作令牌。已经进入 T-021A 原生提交阶段的写入不承诺撤回；不得为模拟取消恢复用户剪贴板。写入失败给安全提示，不将翻译改判失败，也不让下一张卡反复抢自动复制资格。

手动复制可以复制当前保留的旧结果，是用户新动作；它持有自己的可取消工作引用，隐藏/退出取消尚未提交的写入。实际写入必须被退出屏障观察。

NotConfigured / AuthenticationFailed 提供“去设置”链接；调用既有 ISettingsWindowPresenter.ShowAsync("translation")。设置打开导致的失焦可按既有 HideOnDeactivate 隐藏译文窗口，保持旧内容。处理 Core ErrorInfo 时仍防止未经审查的 Detail/Exception.Message 直出；默认显示安全 Code 对应文案，已批准的安全 Detail 才展示。日志只记操作/请求标识、错误码、异常类型，不记原文/译文/Key/整个请求对象。

## 7. 窗口与定位

交付 ARCH §14 的顶栏（拖动、钉住、设置、关闭）、原文、多语言选择、交换、翻译/停止、卡片服务名/模型/耗时/缓存及截断状态、可选只读文本、复制/重试。显示优先级和请求身份不依赖 View。源含 Auto，目标 Auto 映射 null；交换时只有已解析或明确语言可交换，不把 Auto 当作具体语言。没有解析结果时交换禁用。

EnterToTranslate=true：Enter 翻译、Shift+Enter 换行；false：Ctrl+Enter 翻译、普通 Enter 换行（以现有契约为准，修正旧 ARCH 表中“反过来”的歧义）。IME 组合输入确认 Enter 不触发翻译。原文最大可视高度 200 DIP，超出滚动；结果独立纵向滚动，文字可选择。钉住是窗口会话状态，不持久化新字段；HideOnDeactivate 按钉住生效，弹出语言菜单不能误判为外部失焦。

使用既有 TranslateWindowSettings 的 Width、FontSize、Topmost、Placement、LastBounds；宽度覆盖规范化范围 280–1200 DIP，不提高最小宽度来掩盖布局。Light/Dark、中文长状态、280 DIP 和默认 460 DIP 均有真实资源布局验证。普通隐藏保留窗口；窗口切 DPI 或结果高度增长时仍能限制在 WorkArea 内。

WindowPlacementHelper 把 IDesktopContext 的物理快照和纯几何算法与自己的 HWND 操作隔离。仅 App 内部窄原生适配器调用 SetWindowPos/GetWindowRect，Core 不引入 Win32；不新增公开平台契约或包。SourceInitialized 后物理定位，不能用 WPF Left/Top 跨屏。FollowCursor 为锚点右下 +16 物理像素，越界翻到左/上再夹紧；RememberLast 对已有物理矩形找目标屏并夹紧，无效/屏幕消失回主屏；ScreenCenter 使用锚点所在屏 WorkArea。

目标屏 DPI 决定 DIP 尺寸对应的物理宽高，处理 WM_DPICHANGED/WPF DpiChanged 后的实际尺寸重新夹紧；不直接把虚拟坐标除以某个 DPI 当作跨屏 Left。负坐标、工作区小于窗口、整数溢出和热插拔失败须明确处理；工作区较小时允许窗口缩至工作区并保持滚动。只对本窗口设置位置，不移动光标/其它应用，不调用 SetProcessDpiAwareness。RememberLast 只在有效用户移动/缩放后合并保存本字段，使用最新设置，不覆盖服务/常规草稿；最多一个写入和一个最新待保存位置，退出等待。

## 8. 验证及后续边界

先测试实际 App 内部生命周期实现再接 View；所有竞态用闸门、任务句柄和实际绑定断言，不只调用同名自写模型。公开声明编译通过不等于这些行为已实现。

必须覆盖：取消准入不淘汰、容量/跨 Presenter 拒绝、Dispose 与发布/移交竞争、令牌继续取消已接管工作、旧前置读取迟到、新意图替换、隐藏后旧回调、同卡重复/异卡排队、主 Finished 与重试重叠、EOF 后 DisposeAsync 挂起、UI finally 挂起、跨会话唯一刷新/文本预算、Completed 权威覆盖、两类截断/复制策略、复制提交前后取消、失败重开/退出重试。关闭前后 scope/HWND/任务 witness 要真实。

组合根使用真实 Registry / Orchestrator / 设置服务，外部 Provider、SecretStore、Clipboard、AutoStart、桌面边界替换为 fake，路径独立。经已验收设置 VM 保存配置，然后通过 Presenter 展示并运行多 Provider 流，验证顺序、错误、缓存/历史参数、取消/重试；不以直接调用 fake VM 替代该接线。此单无需 T-016 托盘入口，测试调用公开展示端口即可。

布局测试加载真实主题/控件资源、展开/滚动到目标控件并验证其祖先视口；模式/资源切换使用实际 ThemeService 或独立正确初始化的主题，不能只断言枚举。多屏、混合物理 DPI、人工键鼠/IME、主观材质、真实服务、性能如未执行均明确“未验证”；合成几何与离屏渲染不能冒充实机。

T-016 再交付用户入口；T-017 后完成 M2 实际组合根流程。T-015 不包含截图/OCR、划词、悬浮球、历史 UI 或联网验收，不把本单测试通过当作全产品完成。

## 9. 定位官方依据

本轮实际读取 Microsoft [SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos) 和 [WM_DPICHANGED](https://learn.microsoft.com/en-us/windows/win32/hidpi/wm-dpichanged)。后者说明 lParam 提供新 DPI 对应的建议矩形，应通过 SetWindowPos 调整位置/尺寸。WPF 已处理 DPI 时不要再次无条件乘倍率；在其实际尺寸变化后按目标工作区检查即可。SetWindowPos 的激活/Z 序标志与窗口本次 FocusInput/Topmost 意图分开处理，普通重新夹紧不应意外激活窗口。

获取页面留在系统临时目录 iris-t015-official。此前 DesktopContext 的采集/DPI 官方依据继续见 DESKTOP_CONTEXT.md §6；本轮只完成文档核查，不据此声称真实多屏定位已验证。
