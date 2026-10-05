# 鼠标划词图标、自动翻译与设置闭环

> 2026-10-05，Codex；T-024B2 / ARCH v1.38 / CCR-022 正式规范。B1 / 29bc38e 已独立验收。
> 本单完成 T-024 剩余用户流程：非激活图标、原文本缓存点击、自动翻译、鼠标设置/切换与完整退出。复用 A/B1 及原翻译/设置底座。

## 1. 组件与生命周期

App 新增 internal SelectionIconController 和 SelectionIconWindow。Controller 管理模式应用、monitor 事件、120ms settle、预读/dispatch、唯一候选缓存与图标、运行状态和退出；不把平台识别器或 C2 事务复制到 App。通过同一个 IMouseSelectionMonitor、SelectionReadCoordinator、IActionDispatcher、IDesktopContext 与 UI dispatcher 工作。

构造只建立托管状态/保存依赖，不查询桌面、安装 hook、读取选区或创建 HWND。AppBootstrapper 在设置加载后显式 StartAsync；默认 MouseMode.Off 零 monitor.Start、零预读、零图标。Controller Start 时注册 Settings/Monitor 事件，永久退出时解除；设置窗口关闭不停止已经保存启用的鼠标功能。

模式及 monitor options 应用串行，最多一个实际应用与一个最新待办配置。先使旧配置/候选/排队工作失效，再完整停止旧 monitor，最后按最新持久配置启动；不叠两个 hook。只改变 ReadOrder/恢复/等待/复制排除时，撤销旧预读/缓存并更新快照，无需无条件重装 monitor。与本功能无关的 Changed 不重启监听。未保存设置草稿不触发运行时应用。

## 2. 事件、被动图标与缓存

后台 monitor 回调只将小型描述交给 Controller 的有界调度；不能为每个输入创建无界 UI Task。Controller 最多一个实际 settle/read/dispatch 工作和一个最新手势待办。新手势撤销旧工作，但等旧工作/取消/平台收尾实际结束后才开始最新项，不因取消就清掉仍工作的引用。

接纳/真正 UI 显示/点击均核对当前模式、Controller 请求/配置身份与 monitor.Generation。手势 App 必须有已确认 HWND/PID/进程名，非自身、未在 ExcludedProcesses；不记录标题或选区正文。过期事件、停止中的事件、source 不可确认均无操作。

ShowIcon：等待 120ms settle，再以原手势来源为 ExpectedSource 调用同一 Coordinator.ReadPassiveAsync，使用一次捕获的读取设置。**不创建 Presenter operation**，不取消正在进行的翻译，不改原翻译窗文本/可见性。普通无文本、busy 或取消静默结束；错误只记录安全 code/type，可反映在安全运行状态中，不激活窗口或排队普通通知。

非空结果必须仍匹配原来源，原样缓存 Text 与来源/anchor/请求身份，最多一份、最多 C2 既有 65,536 UTF-16 限制。结果不匹配/SourceApp 缺失不得猜测来源。显示前后台复核完整来源；UI 最终接受点可通过内部 native adapter 用 GetForegroundWindow/GetWindowThreadProcessId 的轻量元数据再确认 HWND/PID（不在 UI 查进程路径/窗口标题）。失败则不显示。

MouseDown 在图标外、有效 Invalidated、新手势、相关持久设置/模式变化、IconAutoHideMs 到期、应用停止均使旧缓存失效并隐藏自己的图标。不要调用 TranslatePresenter.HideAsync 去隐藏用户翻译窗。普通 mouse move 不生成新候选。没有前台事件订阅时，不宣称捕获全部 Alt+Tab；显示/点击仍须复核来源，到期必定清理。

MouseDown 在当前可见图标物理 bounds 内时，不先清掉缓存，由唯一 WPF 左键 Click 消费；右键不翻译。一次候选至多分发一次，图标隐藏/消费后的旧 Click 不复活。A 的 MouseDown 只有位置/代次，不臆造 Button 字段；不得因图标自身光标为箭头而提前清缓存。

## 3. 统一 Dispatcher 与 CCR-022 的来源语义

ActionContext 保留原三参数构造/解构，新增可空 init ExpectedSelectionSource: ForegroundAppInfo；ToString 只打印 Trigger。此字段只约束 SelectionTranslate；其它 Handler 不改变行为，不携带 Presenter lease 或任意 object payload。

SelectionTranslateHandler 正式扩展如下，保留 T-023A 的热键/普通行为与唯一 lease 规则：

- SelectionIcon：必须带非空白 Payload（已缓存原文）与合法 ExpectedSelectionSource。Controller 在实际 Click 验证候选后，经 Dispatcher 分发 SelectionTranslate。Handler 只取得一次新 operation，后台核对当前源匹配，再 TryShow 原样 Payload、Origin=Selection、Anchor=context.Anchor、AutoTranslate=true、FocusInput=false。**零 Reader 调用、零重新复制**。源已变/取消/过期静默结束，不走“自身前台打开输入”的回退。
- MouseSelection（AutoTranslate）：必须有合法 ExpectedSelectionSource，Payload 为空。Controller settle 后经 Dispatcher 分发，由 Handler 取得一次 operation，使用该期望源构造 Options，调用原读端口一次。无文本静默结束，不应用 OpenInputWhenEmpty，不弹无选区提示；非空及安全错误仍经同一 operation 发布，过期错误不能覆盖新意图。
- 鼠标两分支在接纳前核对相应已保存模式（SelectionIcon=ShowIcon、MouseSelection=AutoTranslate）；不匹配静默结束，不取得 lease。一次捕获的设置复用，不额外读第二份 Selection。非法鼠标参数在新工作前安全 InvalidArgument，不记录 Payload/source。
- 非鼠标 SelectionTranslate 保留原 A 分支；若显式提供 ExpectedSelectionSource，将其传至 Options 并限制原来源，不能因期望来源变成自身就另开输入窗。null 默认不变。

Controller 的单次 dispatch 使用自身跟踪的请求取消源；等待 Dispatcher 真正结束后归还/Dispose 链接，不在之后隐藏图标或切换模式时继续取消已经由窗口接管的翻译。不得把一个旧 Presenter operation 的 token 交给会创建新 operation 的 Handler，造成新操作自取消。

为统一已经用于选区与后续截图的 CaptureFailed，CCR-022 批准 ErrorMessages.GetDefault(CaptureFailed) 文案从“截图失败”改为“内容获取失败，请稍后重试”；代码值与异常签名不变，其它默认错误不改。所有前置错误仍只使用安全默认文案，日志不传异常对象/Message/Detail/正文。

## 4. 非激活图标窗口

窗口是 24×24 DIP 的简洁翻译图标，使用现有主题/UiText/应用视觉资源，不安装新包。ShowActivated=false、ShowInTaskbar=false、无普通标题栏；SourceInitialized 给自己的 HWND 加 WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW，WM_MOUSEACTIVATE 返回 MA_NOACTIVATE。不能用会吞点击的 MA_NOACTIVATEANDEAT，不加 WS_EX_TRANSPARENT，不调用 Activate/SetForegroundWindow。

位于手势 up 物理点右下 12 DIP 偏移；通过既有 Desktop/placement 能力换算该显示器 DPI 与物理矩形并夹在工作区。跨负坐标、屏边、混合 DPI 数据和显示器变化有明确失败/夹取规则，沿用现有坐标规范，不把 DIP 当物理像素。最终定位、显示/隐藏、样式都在自己的 UI/HWND 范围。

每个 Controller 最多一个图标窗口。候选到期隐藏清文本；Mode.Off 和应用退出完整关闭/释放图标，关闭失败保留引用可重试。主题变化跟随实际 App 主题路径；正常初始化不可出现抢焦点或空白全尺寸窗口。可提取 internal native/window 边界便于测试，但必须有实际 WPF Window/样式/消息处理接线证据。

## 5. 鼠标设置与运行状态

SelectionSettingsPatch 原五参数构造/解构保留，新增五个可空 init 字段：MouseMode、RequireIBeamCursor、MinDragDistancePx、IconAutoHideMs、ExcludedProcesses。原持久化 SelectionSettings、JSON/default/schema/包不变。每字段沿用 Base/Target 三方冲突，显式 Target 校验，未编辑字段保留最新；ExcludedProcesses 的 Base/Target 首次等待前复制，实际匹配/文件名规范化与既有 ClipboardUnsafeProcesses 相同，二者不混用。

MouseMode 只允许 Off/ShowIcon/AutoTranslate；MinDragDistancePx [2,50]、IconAutoHideMs [500,10000]，整数文本草稿直接验证，不 clamp 或因 binding 失败保存旧值。RequireIBeamCursor 按 bool。未编辑历史坏值不阻止无关页面保存。Core 仍一次 UpdateAsync 联合提交全部页；mouse-only 不触碰凭据/Provider 观察者/回执配额。

现有“划词翻译”页增加模式、仅文本光标、拖动阈值、图标停留时间、完全排除鼠标划词的进程列表。文本输入立即进入草稿，保存前正确 flush；加入现有 IsDirty、全页冻结/解冻、外部合并、已保存各分支基线、Reload/关闭。界面说明显示图标时会预读选区，关闭剪贴板恢复可能改变剪贴板；不把 HWND/线程/清理状态码塞入产品说明。

Controller 发布 App internal 安全运行快照（版本、期望/实际模式、应用中、运行/错误/可重试），Settings VM 订阅并在关闭解绑。保存成功但 native 安装/停止失败仍是“已保存，应用未完成”，不能回滚设置或显示全部生效；保留其它 Provider/自启/快捷键提示与重试，不把 mouse 错误放进 Provider 回执。提供“重试鼠标监听”，按最新持久配置先清旧责任再应用；重试不提交未保存草稿，期间沿用统一冻结/真实等待。

新建模式应用/重试失败可在实际 UI 接受点核对配置代次后，以既有即时安全通知路径给固定 HookInstallFailed 提示；未就绪托盘不排队旧通知、不创建翻译窗。正常停用/取消不弹失败。被动读取失败不取消已有翻译或制造迟到错误窗口。

最小 600×440、普通尺寸、Light/Dark 下全部配置/错误/重试/保存/取消可达。内容纵向滚动；多个系统/Provider 待应用同时存在时底部按钮不得横向溢出，可换行布局，不删掉已有重试入口。

## 6. ToggleMouseSelection

Core 新增 ToggleMouseSelectionHandler，唯一注册现有 IrisAction.ToggleMouseSelection。它只改变持久化 MouseMode，不直接调用 App/native。这个相对动作使用一次 ISettingsService.UpdateAsync 在 mutate 中按最新值切换：非 Off→Off；Off→本次运行记住的最后非 Off 模式，没有记录则 ShowIcon。

记忆只在本进程内，通过实际已保存 Current/Changed 维护（构造可订阅托管事件，不做 I/O，Dispose 解订阅）；不新增 LastMode JSON 字段，不宣称跨重启记忆。写失败不伪造模式成功，取消按设置服务提交点规则；其它 Selection/设置字段来自 mutate 最新值，不覆盖旧快照。此单次相对动作是正式批准的原子 Update，不是设置页面绕过编辑协调器。

快捷键页增加该已交付动作的编辑行，复用原 Recorder/暂停/冲突/保存。HotkeyDefaults 没有 ToggleMouseSelection，保持默认无绑定；null/null 初始不 dirty。预留冲突判断按实际已开放草稿动作排除，避免新行再次与自身冲突。不新设默认组合、不加托盘点击划词菜单或全局键盘监听。

## 7. 退出与错误责任

同步拒绝新 Controller/Dispatcher/读协调器工作后，撤销候选/待办资格。异步停止 monitor、等待本层 read/dispatch/取消/应用及 UI 工作并关闭图标，再等待 B1 Coordinator.StopAsync、原 Composite.StopAsync，之后才现有设置/翻译 Presenter、Host/Dispatcher 释放。B1 的“排空不等于 native cleanup”边界保持。

失败时尽量完成独立收尾，保留主与清理错误及未完成资源；重试只做剩余步骤，不能因隐藏成功就丢掉未关闭 HWND/未卸载 HHOOK。模式切换不能直接 Dispose 活跃 monitor 或 Reader；永久退出禁止恢复监听。同步 Dispose 只允许完整停止或从未使用，不阻塞 UI。回调/事件订阅、Timer/CTS 和注册都在实际工作结束后释放。

## 8. 验收矩阵

1. 实际 Controller+monitor+Coordinator+Composite（fake 仅 OS/process/time）：Off 零工作；ShowIcon 不取 Presenter lease、不取消既有实际翻译；120ms settle、最新待办/取消等待、source/代次/模式变化拒绝旧结果；缓存一次、点击不重读/不重复制；Auto 一次读与 Origin=Selection；无文本无空窗。
2. 实际 Dispatcher/Selection Handler：鼠标参数/模式检查、expected source、点击缓存原文保持、源变化/取消/旧错误拒绝；原热键/自身前台/错误行为回归，新的安全 ToString 与 CaptureFailed 文案。
3. 实际图标 Window/native adapter：样式、MA_NOACTIVATE、唯一 Click 接线、内外 MouseDown、自动隐藏、负坐标/不同 DPI 数据/屏边定位、主题渲染；不把 fake 样式断言写成实机焦点/物理鼠标验证。图标真实 WPF 离屏图像要查看。
4. 模式保存/自动应用、失败仍承认保存、重试最新配置、停止失败/迟到安装、关闭设置不停止监听、永久退出不重开，全部真实任务/线程/自有 HWND 责任等待；故障注入 finally 先放行门闩再完整收尾，保留主/清理异常。
5. Core 五个新字段的同步捕获/输入/双阶段冲突/未编辑保留/一次混合提交；App 文本 binding、dirty 外部更新、冻结/保存待应用/重试/关闭；Toggle 相对最新值与本次运行记忆、无默认绑定行及实际录入接线。
6. 两主题×最小/普通尺寸，含长列表、错误与多种重试同时显示，实际 App 主题路径、布局/帧完成及 PNG 目视检查；串行 Release build 与全量 test，原失败/跳过保留。

新增自动化不安装真实全局 hook、不读用户窗口内容/选区/剪贴板、不注入输入、不动用户配置/密钥/Run、不真实 HTTP/性能脚本、不启动真实 Iris 主应用。只读 T-022A 光标/显示器 smoke 按已明确边界保留。真实焦点/物理输入/应用兼容性/混合 DPI/NFR 尚未执行时如实未验证，T-024 实现结案不冒充 T-040 手动验收。
