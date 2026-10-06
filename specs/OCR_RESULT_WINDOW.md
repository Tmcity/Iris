# OCR结果窗口与操作生命周期

> **2026-10-07 用户变更：以下独立OCR窗口设计已撤回，仅作历史记录。** 截图OCR复用翻译窗口，把结果直接写入原文框（Origin=Screenshot、AutoTranslate=false），不显示截图或新建OCR窗口；截图翻译仍自动翻译，静默OCR仍不弹窗。旧预览/缩放/独立窗口生命周期不纳入当前交付，现有OCR后端保持。当前执行以tasks/current_task.md顶部新指令为准。

> 2026-10-06 · T-031A · ARCH v1.48 · CCR-032 · 已定稿。
> 本单完整交付结果窗口及交互/生命周期；T-031B再接三个截图Handler与托盘/快捷键入口，总范围不减。原裸ShowAsync原型不再实施。

## 1. 身份与图像所有权

实现IOcrResultPresenter和IOcrResultOperation。Presenter单例，构造不创建窗口、不读图像、不查询桌面/密钥、不启动OCR。所有公开调用可来自任意线程，WPF变化在UI线程。显示窗口只有一个，关闭即释放，不沿用翻译窗口“关闭=隐藏”的产品行为。

TryBeginOperation在排队/await前接纳新意图并撤销旧操作。已取消caller抛OperationCanceledException且不影响旧意图。退出、上一会话关闭/清理未完成或未回收操作达到8个时返回null，不取消当前有效意图、不启动外部工作。取消回调在锁外异步执行并观察，不能在UI/状态锁内调用阻塞回调。

操作有随机Id、Token、Presenter归属和调用方引用。调用方在全部自有异步工作退出后Dispose；释放幂等，不取消已经由窗口接管的复制/重识别。窗口另有工作引用，不能依靠调用方Dispose提前销毁它还在使用的CTS/图像。Token仅在调用方租约存活期间读取。所有未完成操作、取消回调和失败资源均由Presenter可追踪，成功后不缓存完成操作。

TryShowAsync在入口捕获请求字段，UI实际执行时复核归属、当前Id、取消、释放和关闭状态。外来/null操作或null请求为ArgumentException；旧/取消/已释放操作返回false，不显示/修改窗口、不触发OCR/复制。同操作正在发布时再次发布抛InvalidOperationException，不排多个状态覆盖。不能只在UI排队前检查身份。

首次接受绑定Image引用；同一操作后续请求必须使用同一Image对象，否则ArgumentException。Image/Bytes转交只读，调用方不得再修改；窗口强持原图用于重新识别，禁止用预览图代替原图。请求record字段在入口捕获，不在UI执行时再读可替换的外部请求引用。无需反复复制大图。

IsLoading表示非终态，成功或Error为终态；每个操作最多接受一次终态，再次终态返回false且不重复自动复制。同操作可以loading→terminal，或直接terminal。Text=null不替换同图现有编辑文本，空字符串明确清空。IsLoading与Error同时非空、loading带非null Text、Error带非null Text为ArgumentException。AutoCopy只在成功且非空白Text的终态起效。

新图首次显示清除旧图文本/错误/服务信息、重置缩放；同图重识别保留原编辑文本和缩放，loading时文本只读，成功替换，失败保留原文本并恢复编辑。接纳新操作时若旧窗口仍显示旧loading，应条件取消该loading但保留内容；不能在新操作尚未显示或随后取消时把旧窗口永久留在“正在识别”。迟到旧完成永远不能更新新内容。

## 2. 展示与预览

OcrResultWindow沿用项目WPF-UI、主题和UiText方式。左侧图片预览带滚动、适应窗口/100%/放大/缩小，右侧多行可编辑文本。普通手动缩放范围25%至400%；“适应窗口”须完整容纳图片，可低于25%，不能用手动下限截断Fit结果。Fit低于25%时，缩小不得反向放大（可禁用或保持当前值），放大可进入正常手动范围；小图允许保持100%。此处为C31A-2对原25%范围的澄清，不修改图像内容/后端预算。工具区包含服务下拉、“重新识别”、“复制”、“翻译”；loading、错误、备用提示清晰独立，错误可重试但不清空已编辑文本。loading只读编辑区，其它命令按状态启用。关闭失败保留“重试关闭”或可操作的关闭按钮，不留无入口的悬挂会话。

新窗口用Anchor（物理坐标）定位并限制于工作区；null沿用既有定位helper。建议默认960×600 DIP，最小尺寸约560×360，但小工作区时以可见可操作为先，不能因MinWidth/Height溢出屏幕。仅首次显示激活窗口；同操作的迟到成功/失败不得再次抢焦点/搬动窗口。布局使用可伸缩列/滚动，长中文/英文/Provider标签不得遮住操作按钮。不新增持久化窗口设置字段。

原图验证/预览准备通过已有IImagePreprocessor，使用显示用约束：MaxEncodedBytes=285212672、非Base64、MaxLongSide=32768、MinShortSide=0、Png/Jpeg/Bmp、无表单预算。内部真实头部/解码预算由已验收预处理器负责；不得直接让未经验证的头部触发无限WPF解码。对有效图保持原像素，不统一降分辨率；后台创建可冻结的BitmapSource（流OnLoad并释放），UI只接收冻结对象。同图状态更新不重复解码。预览失败显示安全ImageInvalid/ImageTooLarge等默认错误及占位，不崩溃或记录原字节。

预览任务受操作/会话取消和实际清理追踪；过期后不能发布BitmapSource。首次显示可等待准备完成再接受UI，或先接受受身份约束的占位并由窗口拥有预览任务；无论方式均必须在关闭/退出中等待真实预览工作，不能遗忘Task。失败创建窗口若已部分Show，需清理实际窗口；Close失败继续持有原窗口/scope并拒绝新实例。不能显示幽灵窗口或假报释放。

ProviderName是传入的显示标签；未提供时以ProviderId查窗口已有服务列表标签，找不到显示逻辑ID。此标签标识配置，不声称重建旧请求时的不可变名称，不修改OcrOutcome模型或后端Service。UsedFallback显示固定“已使用备用 OCR”信息（每次成功结果一次），不使用不存在的ErrorCode。

## 3. 服务列表与重新识别

显示窗口时从IOcrProviderRegistry短期快照复制Id/DisplayName/IsConfigured等显示数据，按配置顺序呈现已启用项；不把Provider留在VM或下拉模型中。await释放快照后再结束刷新，失败原快照保有并可重试，不能吞错。Changed后台通知有界合并为一个运行中刷新和一个最新待处理刷新；UI接受时校验会话仍存活。没有窗口时不为列表创建窗口。

优先保持用户已选择且仍存在的ID，否则选当前Active存在项，再选首项，没有项则禁用重识别。下拉变更不自动发起识别；未配置项可显示“未配置”，明确点击重识别仍走原Service规则（可能备用）。列表刷新失败不毁掉已显示文本，提供安全错误/重试；不直接读SecretStore、创建Provider或测试网络。

重新识别按钮同步捕获当前原图、选中ID及一次AutoCopyOnOcr设置，接纳新操作，取消并完整观察旧工作。用IOcrService.RecognizeAsync(originalImage, selectedId, token)，不复制预处理、回退或Normalizer算法。结果按同一操作条件发布；空文字/服务错误显示默认错误并保留旧文本。成功映射Text、Raw.ProviderId、UsedFallback及捕获的AutoCopy；原图/缩放保持。不得在控件选择事件里隐式联网，不因清理重试重复识别。

最多一个当前重识别工作，快速重复命令不得无界堆积；采用一个最新待处理意图，撤销被替代意图并观察其任务。旧Provider不合作取消时保持真实等待，但UI不得接受旧结果。关闭/退出清除待处理意图并等待当前工作。不要在自己持有操作租约的任务里先await等待该租约归还的关闭屏障。

## 4. 编辑、复制与转翻译

编辑框用PropertyChanged或等效明确刷新保证命令取得当前可见编辑文本；复制/翻译不能读取上一轮未提交的VM旧值。空白文本禁用两命令。复制调用已有IClipboardService.SetTextAsync；翻译调用IActionDispatcher.DispatchAsync(IrisAction.TranslateText, new ActionContext(ActionTrigger.InApp, Payload:编辑文本), token)，不直接操纵TranslateWindowViewModel。在会话关闭/失效后不接纳新命令。

AutoCopy仅在成功UI接受点认领一次，并由窗口拥有复制任务，TryShowAsync返回true不必等待复制完成。手动复制捕获用户点击时文本。复制队列至多一个运行中、一个最新待处理；新图/操作/关闭撤销尚未接受的旧自动复制。自动复制还要核对文本修订，用户编辑后未接受的自动复制不得覆盖其新意图；手动已接受复制保留点击时文本。

复制到外部端口前在UI实际接受点复核会话/操作，使用取消token并完整等待原生写入退出。IClipboardService已提交（EmptyClipboard成功）后保证发布/清理，不能因迟到取消承诺回滚或再次写入。错误使用默认安全消息，保留文本并允许新的显式复制；清理重试不重做写入。禁止读取用户剪贴板验证自己是否写入成功。取消回调/复制失败不得成为未观察的async void异常。

错误展示仅使用ErrorMessages.GetDefault(code)，不直接显示ErrorInfo.Message/Detail或任意原始异常链；日志仅安全阶段/类型/错误码，不输出图像、编辑文本、凭据或整份请求。Provider显示标签可以显示在窗口，但不作为日志正文。

## 5. 截图准备、关闭与退出

Presenter通过App内部IScreenshotPreparationParticipant适配器接入已有ScreenshotPreparationCoordinator，复用WindowPreparationIntent的Observe/ShowAttempt/Advance/条件恢复，不重写其身份算法。参与观察不创建窗口。用户显示/关闭/新图意图推进版本；截图准备临时隐藏不视为用户关闭、不取消结果会话。关闭中/关闭失败仍可见的窗口必须阻止截图准备，不能被忽略后截入自身。

用户关闭、意外Closed、失败Show都撤销当前发布/命令资格，取消识别/预览/复制/列表刷新；等待全部真实任务、取消回调、调用方操作引用和异步快照释放。正常Closed后释放Window/scope、事件、DataContext/图像Source和原图/文本引用。不强制Marshal.ReleaseComObject或给BitmapSource虚构Dispose。关闭即释放，重新打开创建新会话。

Closing事件内不得直接抛异常或同步阻塞：若需异步等待，取消这次Close并安排可观察的关闭流程，任务完成后再实际Close。外部取消Close或资源失败时保存同一窗口/scope，安全显示关闭失败及重试入口；重复关闭共用当前关闭任务，失败后能重试。关闭/清理未完成时不创建第二个窗口。意外Closed也要观察仍在途工作，不把隐藏和Closed混为一谈。

App内部新增OCR窗口停止/清理协调能力，保持Core公开端口只做展示。停止入口先封闭OCR窗口新意图/命令，再沿用Service/动作封口。StopCore在截图覆盖层与准备依赖成功收尾后，关闭并排空OCR窗口，然后才停止预处理器；已有Core OCR服务停止仍等待实际OCR。截图准备/overlay尚未清理成功时不得销毁它依赖的OCR窗口。OCR窗口失败需保有责任、加入安全汇总、Host不得退出；其它无依赖清理仍尝试。Registry停止不能死等仍被失败窗口持有的租约，沿用A的可重试待所有者模式。

DI不得形成Dispatcher→Handler→Presenter→Dispatcher或ScreenshotService→Preparation→OCR Presenter→ScreenshotService环。Presenter构造不直接要求依赖全部Handlers的IActionDispatcher；可在按需窗口scope/命令执行时解析已有Dispatcher。只注册一个Presenter及其端口/准备参与者，不启动时解析窗口/VM。

## 6. 验收与范围

测试接实际Presenter/Window/VM/Service或实际Core端口组合，外部OCR/预处理/剪贴板/桌面用受控替身；预览可用自制PNG和真实WinRT/WPF。覆盖首次loading→成功/错误、旧操作迟到、同操作重复终态、外来/取消/释放身份、UI排队后撤销、切图/编辑/重识别、复制接受点/失败、手动翻译确实走Dispatcher、列表快照刷新/释放失败及关闭重试。

必须覆盖实际窗口Show后失败、真实Closing取消和随后成功Close、自有窗口/图像/任务引用回收（正对照），截图准备隐藏/恢复与新意图/关闭竞争，以及真实Bootstrapper退出依赖。不以空模拟窗口或全mock生命周期证明HWND释放；不在WPF事件直接抛异常制造宿主崩溃。失败测试先解除门闩，再等待实际任务和释放自有资源。

提交并实际查看离屏渲染产物：正常/正在识别/错误/关闭失败、Light/Dark、长中英文/服务名、窄小工作区、高DPI、缩放与滚动。确认左右区、按钮、状态和滚动条不遮挡，文字可读。不得启动真实截图、读用户剪贴板、注入按键、发真实云请求或运行性能脚本；真实桌面焦点/多屏/NFR后续T-040。

A不注册截图翻译/OCR/静默OCR Handler，不新增托盘/快捷键动作，不做T-032设置页。OcrService/Registry/Provider/预处理/剪贴板和翻译公开契约保持，Core仅CCR-032；默认配置/schema/包不变。完整原功能由后续B接线，不以A测试窗宣称产品端到端完成。
