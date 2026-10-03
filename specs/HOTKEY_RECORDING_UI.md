# 快捷键设置页、短时录入与运行时应用状态

> 2026-10-03，Codex；T-020B / ARCH v1.29 / CCR-016 正式规范。基线 e064a54，T-020A 与 CCR-015 已验收。本文替代 T-020B_RECORDING_PREPARATION.md 的待定事项。

## 1. 本单完整目标与边界

交付设置窗口 hotkeys 页、HotkeyRecorderBox、含 Win 修饰键的短时录入、Esc 取消/Backspace 清除、草稿冲突提示、全窗口一次保存、保存后的实际热键状态及重试、录入暂停/恢复和完整退出。T-020B 验收后才可结案 T-020。

本轮可编辑动作固定为 InputTranslate、ClipboardTranslate、OpenSettings，且必须具有实际已注册 Handler。TranslateText 需要文本 Payload，不作为无文本热键录入行；未交付的截图/划词/悬浮球等不开放可操作的假入口，原配置与默认绑定保留。OpenSettings 无默认绑定时显示“未设置/禁用”，草稿 BaseBinding=null，不能在打开页面时写入默认值。

不实现 OCR、划词、命令行、国际化全项目改造、安装更新或性能测量；不修改 IHotkeyService / IActionDispatcher / ISettingsEditCoordinator 现有签名、默认设置、SchemaVersion 或 JSON 格式，不新增 NuGet 包。新增公开声明仅限已批准的 IHotkeyCaptureService 文件。

## 2. 平台捕获端口与所有权

准确声明见 CONTRACTS.cs 与 Core/Platform/IHotkeyCaptureService.cs。Platform.Windows 实现单例服务，经 AddIrisPlatformWindows 的 TryAddSingleton 注册，构造、Host.Build、普通 DI 解析不创建线程、窗口或 hook，不读取键盘状态。

BeginAsync 只在用户明确启动录入后调用，句柄必须非零、仍有效且属于本进程；窗口丢失前台或调用方取消时不得继续收集新按键。一次只允许一个已开始/正在初始化/清理失败的录入会话；并发 Begin 明确失败，不创建无界排队任务。无效句柄报安全参数错误，原生开始失败用既有 Unknown 安全文案，不输出句柄、键序列、原始异常或设置内容。

服务在启动资源前就持有当前会话/初始化任务。Begin 取消需等待本次原生回滚；若回滚失败，资源保留在服务中，可由 StopAsync 再次清理，不能因为尚未返回 session 而丢失所有者。调用方在 Begin 失败后同样完成服务 StopAsync 的收尾。

Session.Completion 仅表示逻辑结果：Captured 带启用的规范化 Binding，Cleared 带 (None, "", false)，Canceled 不带 Binding/Error，Failed 只带安全 Error。它不代表 hook/线程已退出。终态只发布一次；只有 session.DisposeAsync 成功后，App 才可应用录入结果并恢复热键。正常产生终态后服务主动启动清理；该清理任务由服务持有和观察，DisposeAsync/StopAsync 等待同一任务。清理失败可重试，禁止无人持有的 faulted task。

StopAsync 与 session.DisposeAsync 在调用入口同步撤销新捕获资格，再异步注销资源；不依赖 UI 未来回调才停止接纳。不覆盖已确定的 Captured/Cleared 逻辑结果；App 若已关闭/换会话仍应丢弃旧结果。成功后重复停止/Dispose 无原生操作；并发停止共享在途屏障，故障后重试仍指向原资源。

服务 DisposeAsync 永久关闭新 Begin 准入并完成物理清理；失败保留所有权，可再次调用。同步 IDisposable.Dispose 只用于未初始化或已完整停止后的容器收尾；若仍有活跃原生资源，明确抛安全错误，不在 UI 同步等待线程，不把清理失败伪装成完成。正常 App 退出必须先完成异步录入清理，再允许根 Host 同步 Dispose。

## 3. 原生实现机制

使用现有 CsWin32 在专用消息线程安装 WH_KEYBOARD_LL（13），不在 WPF UI 或业务线程执行 hook 回调，不引用 WPF/WinForms。该线程有自己的 Win32 消息循环，队列就绪后才接受停止消息。使用 GetModuleHandle(null) 的本进程模块句柄及被强引用的托管 HOOKPROC thunk；不向其它进程注入 DLL，不申请管理员/UIAccess，不改系统注册表、快捷键保留规则或 LowLevelHooksTimeout。

实现窄原生适配器，覆盖安装/卸载、CallNextHookEx、窗口/进程/前台校验、按键状态初始化和消息循环必要调用。正常产品执行真实 API；测试替换适配器，不能通过生产常关开关假装实现。

nCode<0 无条件 CallNextHookEx 并返回其结果；未处理事件也保持链路。只在有效录入会话且目标设置窗口为前台时捕获、消费相关按键；前台切换后立即停止新捕获并取消会话，其它应用的新按键不保存、不展示、不日志记录，按原链传递。回调只做有界状态更新/快照入槽，绝不等待 UI、翻译、剪贴板或 Task；托管异常不得跨 native 边界。

Current 为不可变最新预览，Revision 单调递增；最多一个待通知快照和一个通知 worker，ProgressChanged 在 hook 回调之外触发并捕获订阅者异常。App 读取 Current 并按 session Id/Revision 调度，不把旧通知覆盖到新控件。Key 只可为白名单规范名或 null，不以 VK 数字/原始输入生成任意文本。

卸载须检查 UnhookWindowsHookEx 返回值。失败保留 hook 句柄、委托根、会话和消息线程；成功后仍须确认已进入回调退出及消息线程收尾，才释放关联状态。用所有者线程的串行命令和已完成屏障证明，不用固定 Delay 猜测 native 已退出。不得 Join/Wait/Result 阻塞 WPF 线程。

## 4. 一次录入的按键规则

安装完成后先进入“请松开按键”准备态。只在显式录入开始后的窗口前台状态下，在回调之外初始化必要按键/修饰键状态；已按住的启动键必须松开，准备完成前不生成候选。新状态由 KeyDown/KeyUp 维护，不能在 LowLevelKeyboardProc 内调用 GetAsyncKeyState 判断刚变化键的异步状态。

左右 Ctrl/Alt/Shift/Win 合并为 HotkeyModifiers 对应位。一个候选由修饰键集合和一个主键组成；对自动重复去重。规范键名/VK 使用 T-017 映射，最终有效性使用 IHotkeyBindingPolicy。F12、仅 Shift+字母、非法单键及未知键不完成录入，预览给安全无效提示，允许继续重录或取消。

候选修饰位取主键首次 KeyDown 瞬间的状态，随后释放修饰键不能把已捕获组合改写成无修饰单键；释放阶段只决定何时安全完成，不重新解释候选。

同时处理 WM_KEYDOWN/WM_KEYUP 与 WM_SYSKEYDOWN/WM_SYSKEYUP，识别 KBDLLHOOKSTRUCT 的按下/释放及 Alt 上下文；不把系统键路径遗漏为普通 WPF Key.System。注入标记仅是事件来源元数据，不记录原输入；是否接受仍以显式会话、前台归属和合法键组合为准，不以全局记录键序列实现录入。

无修饰 Escape 取消本次录入，保留录入前草稿；无修饰 Backspace 清除，最终草稿为禁用空键。它们在录入态不得冒泡关闭设置窗口或编辑普通文本。已准备的有效候选在主键和参与的修饰键均释放后才产生 Captured；正常完成前消费本次已消费 KeyDown 的对应 KeyUp，避免 Win 等释放事件引起意外操作。单独修饰键不形成绑定，多个非修饰键同时按下时拒绝该候选并等待松开后重录。

失焦、取消、关闭、退出必须立即撤销新输入资格并进入停止；不能为等待用户一直按住的键而无限推迟卸载，不通过模拟按键“补释放”。系统安全组合/安全桌面不能绕过；支持 Win 修饰键的可录入组合，不保证 Win+L/Ctrl+Alt+Delete 等系统专用组合可拦截或可注册。

## 5. App 暂停与恢复

增加 App 内部 HotkeyRecordingCoordinator（进程级）、暂停 lease 与运行时状态模型，可按现有内部风格命名。它们不属于公开契约。构造无 I/O；VM/控件不能自己调用 RegisterHotKey 或直接篡改 HotkeyCoordinator 集合。

录入准备顺序：

1. 用 owner/session 标识取得唯一暂停资格；立刻关闭新 Pressed 准入并推进输入代次。
2. 在热键窗口 owner UI 线程注销全部 Iris 当前注册，失败保留清理状态并报告“无法开始录入”，禁止安装捕获 hook。
3. 排队但尚未执行的旧热键动作在实际 UI 执行点再次校验代次/暂停/停止状态并退出，不能在恢复后补执行。可在 HotkeyCoordinator 将分发显式调度到 UI，检查后再进入真实 IActionDispatcher；仍须离开 WndProc 栈、仍每 Action 有界，不直接调用 Handler。
4. 等待已接纳且已开始的前置 Handler 工作真实退出，再允许录入；不为录入而取消正在进行的用户翻译。准备期间 UI 显示等待并允许取消，等待自身不得同步阻塞 UI。已提交给 Presenter 的流式结果无需等整场翻译完成，但不能有会再次发布/抢焦点的前置动作漏出屏障。
5. 前台/控件/会话仍有效时调用 IHotkeyCaptureService.BeginAsync。

每次暂停独立等候本次在途工作；不能重用并永久缓存一次已完成的正常退出 WaitForInFlightDispatches 结果。旧暂停 lease 的释放不能解除新暂停；取消准备只取消本次开始流程，不遗忘已开始的工作或错误清理。

暂停期间 settings Changed 仍只保留一个最新快照，但不重新注册；释放先完成 hook 清理，再撤销暂停并按此时最新持久配置应用，绝不恢复录入开始时的旧绑定。保留 T-017 同线程/新 ID/先注销后注册/交换组合/失败记录协议，不 Dispose 再复活原 IHotkeyService。

若 hook 清理失败，暂停仍被持有，显示“结束录入失败，可重试”，不允许新录入、不恢复快捷键。若 hook 已清理而恢复注册部分失败，暂停可结束，错误交给进程级 HotkeyCoordinator 持有和重试；成功录入的候选不因旧配置恢复冲突而丢失，但显示实际运行状态。

## 6. 运行时状态与重试

HotkeyCoordinator 维护线程安全的不可变快照和变化通知，App internal 即可。至少表达每个已交付动作的目标绑定、配置/应用代次、Disabled、Applying、Registered、Paused、Conflict、Invalid、Failed；注销失败不能显示 Disabled/Registered。没有 Handler 的已有配置只表示尚未开放，不调用占位 Handler。

状态必须来自实际 Register/Unregister 结果，不能由 settings.Enabled 推断成功。日志只含安全类型、Action、规范化键或数值错误，不回显无效原始键或 Exception 对象。

页打开/重开读取当前快照，不重注册；无关设置变化不消耗 ID。保存成功后由既有 Changed 驱动实际应用，页面可先显示“已保存，正在应用”，随后按目标代次显示成功/冲突。旧代次结果不能覆盖新目标；暂停期间显示“已保存，录入结束后应用”，不能虚报失败或成功。

“重试应用”仅重试最新已保存目标的失败/未应用项及残留清理，不提交草稿、不写 JSON、不重试 Provider 回执、不反复重注册成功且未变化项。录入/停止期间不并行重试；正常退出后拒绝重试。可读错误包含动作与规范化组合，无效输入显示固定占位文案。

## 7. 设置页与联合保存

在现有 SettingsWindow 导航加入 hotkeys，NormalizePageKey 允许该页；其它未开放页行为保持。页面沿用 WPF-UI、UiText/AppResources 与现有浅色/深色资源。每行至少显示动作名、Enabled、只读组合显示/录入入口、草稿错误和实际已保存绑定的运行状态；草稿状态与运行状态清楚区分。

点击录入入口或焦点控件按 Enter/Space 才开始，构造/Loaded/仅获得焦点不自动安装 hook。准备、录入、结束清理和清理失败状态可见。Esc 仅取消录入，不能顺带关闭设置窗口；鼠标离开当前录入控件、导航到其它页、关闭窗口均取消并收尾。成功录入设置本行草稿 Enabled=true；Backspace 清除为禁用空键；普通禁用保留上次规范组合。程序性晚到结果必须验证当前 owner/session、窗口未关闭且仍可编辑。

每个可编辑 Action 独立保留 base/target/dirty；默认缺失 Base=null，不在打开页面时补写。外部 Changed 对未编辑行更新基线，已编辑行保留输入和原基线。草稿校验使用 A 的 IHotkeyBindingPolicy，并按完整候选 Hotkeys 检查重复；若与尚未开放动作的预留配置重复，提示该动作及预留含义，不能称其为系统占用。

SettingsViewModel 的 IsDirty、保存可用性、捕获、冻结、取消、重载和保存成功后的基线更新都包含快捷键行。计划用 `SettingsEditPlan.HotkeyChanges`，经现有 GeneralSettingsSaveCoordinator → ISettingsEditCoordinator 一次提交；不得保留“没有 General/Provider 修改便直接成功”的旧快捷路径忽略 HotkeyChanges。未编辑节保持最新配置，混合页面一次提交和凭据/回执协议保持。

录入进行中或清理未完成时禁用保存提交；程序性 ExecuteSaveAsync 也必须先结束本次录入/等待清理再固定一份输入，不能提交半个候选。已完成录入即刻写入草稿，不依赖控件再失焦才更新。必要的异步保存前准备接口限 App internal，不用 Action 同步回调阻塞等待异步结束。保存期间所有草稿输入冻结，失败保留，成功按实际最新状态重建基线。

配置保存成功而系统热键应用失败仍显示“已保存，部分快捷键未应用”，提供重试。不得把该错误混入 ISettingsApplyReceipt 或重新执行混合保存；General 自启状态、Provider 发布待完成和热键状态可同时展示且各自重试。

## 8. 关闭与正常退出

SettingsWindowSession / Presenter 的取消、X、Esc 和正常退出，必须在关闭窗口/释放 VM scope 前完成录入控制器的自有停止。失败保留窗口/会话和清理所有权，可重试；不能在 Dispose 中 catch 后直接释放仍有活跃 hook 的控制器。关闭请求异步等待时保持 UI 可调度，不用 Wait/Result，不无限重复 Close。

AppBootstrapper 正常退出先使 HotkeyCoordinator 永久停止准入/恢复资格，再启动录入控制器的停止（其入口同步撤销捕获资格），完整等待 hook/线程清理及原热键原生停止。录入 pause lease 此时不得恢复注册。随后按已有顺序等待 Settings/Translate Presenter 与已接纳工作退出，再停主题、托盘、Host、Dispatcher。任一资源停止失败须阻止释放其依赖并允许再次退出，共享在途屏障且不缓存失败为成功。

同步 OnExit 只是已停止资源的幂等兜底，不承担异步线程等待。强制进程终止不承诺完成持久化或清理；正常路径不可依赖 OS 最终回收冒充成功。

## 9. 必须的自动化证据

| 范围 | 必须验证的实际路径 |
|---|---|
| 平台状态机 | 实际服务+fake native：构造零调用、单会话、句柄/前台校验、准备期已按下键、左右 modifier、Win 组合、重复/非法/F12、Esc/Backspace、松开后完成 |
| 原生边界 | install/unhook/消息线程启动失败与取消回滚；清理失败保留句柄/委托/线程再重试；回调边界不抛；nCode<0/未处理消息转发；停止后新键不被收集 |
| 非干扰 | 前台转到另一模拟窗口即取消；不保存其它窗口输入；正常捕获的 KeyDown/KeyUp 消费成对；完成/停止后不再吞新键 |
| 暂停恢复 | 真实 HotkeyCoordinator+fake native：先停注册再录入；旧排队消息拒绝；在途真实 Handler 前置读取未退出时不进入捕获；不取消正在执行的用户操作；取消准备可退出 |
| 配置交错 | 暂停中连续 Changed 最后目标生效；两键交换；旧 lease/旧状态回调不能恢复陈旧配置；恢复失败/重试不重复成功项；退出时零重新注册 |
| 草稿保存 | 真实 VM/保存协调器/Core：只改热键也提交；三页混合仅一次 UpdateAsync；控件未失焦已完成值进入计划；冻结/取消/外部更新/冲突/成功基线均覆盖 |
| 关闭屏障 | 录入初始化中、录入中、卸载失败、已出逻辑结果但未清理、窗口关闭和 App 退出的交错；真实 scope/容器保持到退出完成；失败可恢复 |
| 真实 WPF | 真正 HotkeyRecorderBox、窗口导航与绑定；Esc 不误关、Backspace 清空；两个主题、默认与最小窗口尺寸、100%/150% 等效缩放的可读性和无裁切 |
| 组合根 | AppHostBuilder 实际解析无环、单例与 scope 正确、无 HWND/hook/线程提前创建；普通 Host Dispose 不碰活跃录入资源 |

自动化不得安装真实全局键盘 hook、注册用户真实热键或发送键盘输入；全部 native capture 事件通过窄 fake 适配器注入到实际实现。不得把“返回 fake session”当平台实现验收，也不得用纯参考状态机替代。WPF 验证可用自有不可见窗口/代码事件及离屏渲染，不能操作用户窗口/设置/剪贴板/Run。禁止真实网络请求与性能脚本。

所有等待都先证明 entered，finally 放行并 await 真实任务/资源收尾；源自清理的错误必须可见，不允许空 catch 宣称完成。保留 xUnit 最小共享状态隔离，不以全局关闭并行或扩大超时掩盖竞态。真实按键、系统保留组合、IME/安全桌面未执行时明确未验证，后续 T-040 单独人工验收。

## 10. 官方依据

已实际读取 Microsoft Learn 的 LowLevelKeyboardProc、KBDLLHOOKSTRUCT、SetWindowsHookExW、UnhookWindowsHookEx、RegisterHotKey 与 WPF KeyEventArgs.SystemKey；原文引用及缓存记录见 T-020B_RECORDING_PREPARATION.md。低级回调在安装线程执行且先于异步键状态更新；必须有消息循环、快速返回；Unhook 返回后回调仍可能在途；Win 组合系统保留和 F12 调试器保留均不能由 UI 承诺绕过。
