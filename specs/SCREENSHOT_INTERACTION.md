# 截图覆盖层与交互规范（T-026B / ARCH v1.41）

2026-10-06，Codex架构师签发。T-026A已在c7282698028da861dbc9dfc6e9430b9cdd1e842b最终验收，CCR-024结案；本单完成父T-026剩余的全部覆盖层和ScreenshotService。窗口/输入准备继续遵循SCREENSHOT_PREPARATION.md，平台抓屏/编码继续遵循SCREEN_CAPTURE.md。

CCR-025仅澄清IScreenshotService及ScreenshotSettings.CopyImageToClipboard的行为注释，不改任何签名/字段/默认值/JSON/schema/包。本单不实现OCR、Handler/入口、设置页或悬浮球；入口在T-031，配置页在T-032。用户已恢复持续推进，当前只实施这张工单。

## 1. 端口与完整生命周期

App单例ScreenshotService实际实现已有IScreenshotService.CaptureRegionAsync(ScreenshotPurpose,CancellationToken)，三种purpose共用截图UI，只返回已有ScreenshotResult(ImageData,PixelRect)。仅同步CCR-025的两处获批注释，不新增Core接口/字段，不改默认配置/JSON/schema/包。构造、DI解析及未用Stop不得创建Window/HWND、枚举屏幕、注册系统事件或开线程。App内部可有最小native/render/overlay外部边界，复用现有DesktopContext、准备协调器和GdiScreenCapturer，不重写底层状态机。

同一时刻只有一个实际会话（准备、抓屏、框选、确认编码、复制、收尾均在内）。在短锁内原子取得准入，忙时CaptureFailed，不排队、不取消别人。非法purpose安全InvalidArgument，已取消先OCE。一次读取ScreenshotSettings快照，所有await后仍使用同一份，不把用户后来设置反向应用到已开始截图。

先登记会话/准备责任，再调用已验收ScreenshotPreparationCoordinator.PrepareAsync。hide=false仍暂停录入和鼠标，hide=true使用A临时隐藏。订阅lease撤销、caller取消与App停止，首个结束原因固定，退出另有永久标志；所有回调/CancelAsync任务在结束屏障内。新显示意图及时取消本会话，并允许新展示继续，不能同步等待恢复。

B自身的显示意图观察必须覆盖整个会话直至最终结果结算，包括A准备租约已归还、焦点/通知仍在UI排队中的阶段。可以复用owner已有的内部观察收据，或增加最小App内部观察能力；不复制一套窗口状态机。不能只依赖已Dispose的准备租约撤销信号，导致恢复后的新展示被遗漏。最后的撤销复核、结果Task结算与会话准入释放为一致的内部状态转换，中间不再await外部工作。

准备后在实际UI完成本进程隐藏更新，并用可替换render同步边界等待必要的本进程渲染；实际等待一次DispatcherPriority.Render队列，再在后台调用DwmFlush，检查HRESULT并完整等待，不用固定16ms Delay冒充重绘。已读取微软DwmFlush文档：只等待调用进程排队DirectX变化，不flush整个session，不声称桌面原子快照。同步期间被取消也必须等真实调用返回才释放责任。

只抓一次冻结frame，复用实际IScreenCapturer。框选/放大镜不能再次抓屏。抓屏/Crop异步任务必须完整观察，不能WaitAsync(callerToken)提前丢下native；调用前后、最终发布前均复核撤销。UI取消返回null；caller取消只有收尾成功后OCE；新显示意图、确认前交互期间的外部失焦/拓扑改变为交互取消null。确认后仍尊重caller、新显示意图及停止；此后新的外部前台只阻止焦点恢复，不把程序自行关闭overlay触发的Deactivated当成取消。真正失败及清理失败安全上报，调用方取消+清理失败仍为失败。ImageInvalid/ImageTooLarge等已定义安全业务错误可保留；其它native错误CaptureFailed。禁止输出截图像素、标题、正文、设备名、原始异常Message/InnerException。

## 2. 每屏视图与物理像素

以frame.Monitors及VirtualBounds作为这次不可变几何。每个显示器一个ScreenshotOverlayWindow，WindowStyle=None、ResizeMode=NoResize、Topmost、ShowInTaskbar=false、无透明整窗；使用不透明冻结背景+局部半透明遮罩。不得做跨整个虚拟桌面的单一WPF大窗口。多屏初始化时避免每个Show依次抢焦点；只激活一个可交互本会话窗口。

native定位最终使用monitor物理Bounds，DIP仅在WPF边界换算；不能以工作区替代整个显示器Bounds。保留负坐标、显示器空洞/镜像重叠、100/125/150/200%DPI。采用当前实际窗口的屏幕转换/受测native物理点边界取得输入坐标，不用错误的全局DPI或对负浮点简单截断。几何加减/union/offset先long/checked，再收窄；禁止溢出PixelRect.Right/Bottom。

冻结帧创建WPF BitmapSource/分屏视图时用真实Stride，不把每屏当作连续紧密数据。大数组/BitmapSource准备在后台，可共享一个Freeze后的全帧源及各屏裁剪视图，避免每个overlay复制整个virtual frame。UI显示区域和物理像素精确对应；屏幕DPI不改变Crop结果的像素宽高。WPF中冻结Source/事件/Closed回调和服务完成Task不得形成隐蔽图像缓存。

初始Show/SourceInitialized/定位过程中出现的自有DPI事件要与会话建立后外部改变区分。建立后至接受确认前，WM_DISPLAYCHANGE/相关DPI变化或当前monitor快照与frame不一致即取消，不在不再对齐的旧frame上接受新选区，不无限重建覆盖层/重抓屏。所有覆盖层建立后、接受确认前分别复核当前显示器快照，初始化排队期间的变化不能漏掉；结果只用于同一仍有效会话。事件在资源登记后挂接、离开时完整解绑；未使用状态不能因订阅启动隐藏线程。

## 3. 选择语义

App可添加仅服务自有WPF窗口、渲染和焦点的窄内部native适配器，复用已有物理定位能力；不修改T-025及其它已验收Platform服务，不添加包。内部纯几何helper可测试，但正式测试必须从实际overlay输入/会话驱动，不能helper通过就宣称UI接线完成。屏幕坐标为离散物理像素点，monitor使用半开Bounds。起点锁定所属monitor；拖出屏幕时点夹到同一monitor的最后有效像素，不能把跨屏矩形交给Crop再靠union求交纠正。

精确规则：两端像素均包含，left=min(x0,x1)、right=max(x0,x1)+1，最终保存半开PixelRect；所以(10,20)到(13,23)是4×4，反向相同；单点1×1忽略。任一边<4物理像素均无有效选区（4×3仍忽略）。贴最后一个像素可选满屏，不少一行/列。必须最后确认Region完全落在锁定monitor且宽高至少4，输出Region等于Crop实际图像像素宽高，不能把裁剪前越界矩形返回。

左键在空白/旧选区外开始新框选并替换全会话旧选区；一个会话只保留一个选区。ConfirmOnMouseUp=true：首次有效鼠标释放只确认一次；误点继续等待，不结束截图。false：释放保留选区，选区内再次按下可拖动现有矩形，保持尺寸且夹紧当前monitor；外面按下重新选择；不增加Icebox中的调整手柄或标注。Enter确认有效且不在拖动中的选区，双击有效选区确认（避免第一次down启动移动造成二次确认）。右键有选区则清除，无选区则取消；交互阶段Esc始终取消。

使用自有WPF输入事件和鼠标捕获，不安装新全局hook，不模拟按键。交互仍活跃时，鼠标捕获失败、LostMouseCapture和真正外部失焦须结束当前交互/会话，不能卡在拖动中。结束时先阻止重复事件，再释放实际鼠标捕获。多overlay之间焦点切换属于本会话，不误判外部失焦；窗口初始化/关闭过程中不把自己的事件递归当新会话。

R1复审澄清：确认前正在交互的任一自有overlay意外Closed，即结束整场，不能留其余窗口继续等待一个已缺屏的交互；正常清理及已确认后的关闭不反转确认结果。输入封口后无关的迟到指针/坐标读取错误应忽略，但已接纳命令内部的捕获/释放失败仍必须报告并完整清理。这些属于本节原生命周期要求，不新增功能。

R2复审澄清：上述意外Closed规则从窗口登记建立阶段起适用，不能仅依赖已进入交互的标记。建立后续屏幕时，此前已登记同伴窗关闭同样令整场失效；后续Show/Activate不得绕过失效检查，已确认结果和服务正常清理仍按原规则处理。

## 4. 视觉与放大镜

背景冻结，MaskOpacity直接控制选择外遮罩；选区内显示原图。边框/尺寸和坐标用物理像素，文字与操作提示沿用当前主题/UiText，不在页面暴露HRESULT/stage/租约等实现细节。无确认选区时简短显示框选/Esc提示，手动确认时显示Enter/双击与右键清除提示。

已核对当前SettingsNormalizer：Screenshot.MaskOpacity范围0.0～0.9，默认0.45；B不得新增另一套不同范围或改变持久默认。测试包含0/0.45/0.9，直接替身返回NaN/无效几何等不合法配置时应安全失败/按现有规范处理，不能将NaN传入WPF属性后泄漏原异常。

ShowMagnifier=true时固定8倍最近邻采样，不做模糊缩放；采样来自原冻结frame，不来自叠了遮罩的视觉树。固定15×15源物理像素→120×120物理像素显示，中心标识对应同一输入快照的物理点；靠monitor边缘用边缘重复保持中心对应，不能读取数组外部或相邻屏内容。采样区域和8倍倍率固定，不随DPI改变物理放大倍率。放大镜/尺寸标签自动翻边并夹在当前屏，极小可用区域可缩减chrome/隐藏非关键提示，不能挡住确认或越出视口。

离屏渲染最少覆盖浅/深色、无选区/有效选区、各边角、负坐标/长尺寸、不同MaskOpacity/ShowMagnifier及小视口；提供实际Window渲染PNG并逐张查看。合成DPI图片只证明逻辑/布局，不宣称实机混合DPI或焦点已验收。

## 5. 确认、可选复制与收尾

确认原子封口选区与输入，后台CropAsync(Png)一次，全部overlay仍由会话登记。具体关闭/裁剪顺序可以先关闭视图再编码，或编码后关闭，但不能让UI确认Task早于实际Crop/关闭/准备归还完成。原始frame直到实际Crop结束且所有视图不再引用才可释放；成功结果只归调用方，不由服务完成会话长期持有。

可选CopyImageToClipboard：确认且仍有效的ImageData只调用一次已有IClipboardService.SetImageAsync，完整等真实完成。剪贴板有独立预算，大图复制失败不能偷偷缩放。复制失败保留已确认图片并给一次安全警告“截图已完成，但图片未能复制到剪贴板。”（走UiText），继续返回截图供后续OCR；不把失败当作成功复制。通知失败只记安全阶段信息，不丢已经确认的图片。CCR-025只澄清IScreenshotService/CopyImageToClipboard注释，字段/签名/默认值不变。任一清理重试都不重做已经执行的复制，不回滚已提交剪贴板；迟到取消等待真实写入，再按会话原因结束，不能强称已撤销系统写入。

复制之前检查caller/新意图/停止，复制之后仍检查并完整收尾。若准备租约先归还而复制仍未完成，须仍有有效的会话意图保护，不能因提前退订漏掉新展示；简单实现可保持准备租约到复制完成。警告在真实UI接受点核对会话仍有效，不向较新用户交互弹过时通知；内容只用既有安全默认文案。

清理顺序为阻止输入/取消在途工作→完整等待已发出的抓屏/编码/复制→释放捕获/解绑并关闭所有overlay→归还A准备租约（停止时不恢复）→解绑caller/回调/丢弃服务图像引用。独立安全清理尽力执行并保留全部阶段错误；关闭失败保留实际Window/HWND与依赖，不只清列表/丢引用。关闭异常可能发生在实际关闭后，重试要按实际状态检查，不再Show/创建替代窗。后续请求或Stop先重试遗留责任，成功后才能新抓屏。

窗口尚未真正关闭时，保留依赖它的准备租约和图像责任，不提前恢复输入/窗口；“尽力执行独立清理”不允许销毁仍被失败overlay使用的依赖。真正Closed/自有HWND终结作为释放依据，不把IsVisible=false等同已Close。创建窗口应先取得并登记纯托管view，再进行可能产生HWND/订阅/显示的初始化；失败不得留下未返回、无人持有的窗口责任。UI排队后的Show/物理定位在真实接受点重验会话，任何原生show/activate副作用之前检查撤销，不能因晚到队列重新显示已结束的overlay。

窗口/图像收尾、最终结果Task结算和service会话清除必须一致，不让Stop漏掉已接纳但尚未结算的调用，也不让service继续强持有Task结果中的ImageData作为缓存。Lease未归还的情况由B主动收，不无限等外部Dispose。

回收断言分别检查实际原始pixel byte[]、结果ImageData.Bytes及创建的BitmapSource/closed Window可释放引用。调用方先放弃结果并让完成栈展开，在仍存活的同一service上、GC前不执行Stop/Retry/第二次请求；带可回收对照。关闭后解绑Image.Source/DataContext/会话回调等按真正所有权处理，不只让CLR窗口对象从列表消失而留下HWND/渲染源。空闲NFR仍以ARCH §1.2为准，不能用截图峰值内存替代空闲指标或反向声称active大图有100MB硬上限。

## 6. 焦点与退出

只查询恢复所需的最小HWND/PID，不读前台标题/正文。截图开始保存原窗口身份；确认/用户Esc时，关闭前前台仍是本会话owned overlay才取得候选恢复资格。关闭/归还准备后，在同一次真实UI/native接受点再次核对无新显示意图、非退出、原窗口仍存活且PID匹配；只有当前前台为0、仍是本次自有overlay或已是原目标时才允许条件恢复（目标已在前台无需再调用）。当前前台为任何新的其它窗口时均跳过，不把同PID的另一个窗口当原目标。外部失焦、调用方取消、新意图和停止路径不夺回焦点。SetForegroundWindow失败只作安全诊断，不AttachThreadInput、不输入注入、不无限重试；不读该API未承诺的陈旧LastError。真实行为留待手动验收。

AppBootstrapper最先停止B新准入，同时关闭A准入；先等B的overlay/图像完整结束，再A，再T-025 capturer和现有独立输入/读取责任，最后Presenter/Host/Dispatcher。B失败不跳过其它可独立完成的收尾，但未完成时不假成功销毁仍被引用的owner。Stop/Retry共享在途Task、成功幂等、失败同实例可重试。保留T-026A/R1与T-025已有退出顺序成果，不建立第二套全局生命周期。

## 7. 自动化验收

主体actual ScreenshotService/Window/输入路由/准备owner与已有capturer/codec；fake只替换native、桌面、GDI、剪贴板、时间/存储/网络/应用终结边界。测试可以以合成帧驱动actual服务，也要一条完整actual GDI fake→真实frame→实际overlay→真实Crop编码→fake clipboard组合链。不能把整个准备协议或overlay都替换为fake后宣称闭环。

新增测试的前台操作、物理定位、DwmFlush和鼠标捕获/释放等外部边界均使用替身；通过自有Window的实际输入处理路由验证行为，不夺取用户的真实鼠标捕获、不向真实桌面发送输入。独立离屏渲染只用合成帧，不能把真实桌面截图当fixture。

覆盖未用零资源、无窗口/hide=false、三purpose、busy、每屏冻结与准确物理region、反向/贴边/负坐标/多DPI/镜像/空洞；小矩形/重复确认/拖动/右键/Esc/失焦；抓屏/编码/复制门闩取消与Stop完整等；每个创建/show/定位/Close与准备恢复失败；新显示意图/拓扑变化时无迟到图像/复制/恢复；可选复制一次及安全失败；多错误/重试原Window/GC原frame和成功ImageData不由service持有；App实际退出链。所有门闩先entered，finally先放行再等所有实际任务/线程/Window/注册，主/清理异常皆保留。

定向后串行Release build/full test，不运行真实Iris/截屏/用户剪贴板/选区/输入/global hook/配置/密钥/Run/HTTP/性能脚本。既有T-022A只读native smoke及两项error183跳过保留。真实桌面隐藏/混合DPI/HDR/焦点/4K≤60ms、热键到overlay≤150ms仍未验证，不用合成图/计时充抵NFR。

## 8. 交付边界与依据

必须接实际App组合根/停止序列和已有端口；不能只交纯几何helper、fake overlay状态机或未注册的孤立组件。父T-026的每屏冻结、单屏物理选区/移动、8倍放大镜、确认/清除/取消、隐藏恢复及可选复制全部保留。不会在本单提前把OCR/入口/配置页标完成。

原始验证/渲染图片与报告归档T-026B，正确声明仅合成像素/自有窗口自动化；真实桌面、DPI、焦点和NFR仍待后续实机验收。不得以实现完成代替性能实测，也不得修改ARCH §1.2指标。

呈现同步依据：[Microsoft DwmFlush](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmflush)。已读取官方说明：只等待调用进程的排队DirectX更新，不刷新整个session。该限制与T-025多块BitBlt并非桌面原子事务的声明同时保留。
