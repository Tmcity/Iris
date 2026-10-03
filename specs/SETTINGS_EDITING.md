# 设置窗口与常规编辑

> 2026-09-30，Codex；T-018 / CCR-010。常规编辑部分由准备稿定稿；Provider/凭据提交仍按 PROVIDER_REGISTRY.md 在 T-019 接入。

## 1. 本期能力

T-018 交付真实 SettingsWindow、ViewModel、Presenter、OpenSettingsHandler、托盘设置入口，以及 AutoStartService。常规页只开放 Theme、LaunchAtStartup、ShowStartupNotification、LogLevel；日志级别为既有 Verbose/Debug/Information/Warning/Error。

未实现的语言切换、更新检查和其它配置页暂不开放控件或空页面；它们的已保存字段原样保留。控件文案走 App 资源，沿用 WPF-UI，不增加 MVVM NuGet 包。允许 App 内必要的小型 internal ViewModel/异步命令帮助类型。

## 2. 入口、DI 与窗口会话

Core/Workflows/OpenSettingsHandler 实现既有 IActionHandler，Action=OpenSettings；先检查调用取消，再将 ActionContext.Payload 作为可空 pageKey 交给 ISettingsWindowPresenter。托盘菜单经 IActionDispatcher 派发，不直接复制窗口创建逻辑。

SettingsWindowPresenter 为单例。构造只保存 UI 调度/会话工厂等依赖，不创建 Window/ViewModel；不直接注入 IActionDispatcher，避免 Dispatcher → Handler → Presenter → Dispatcher 的循环。首次 Show 在 UI 线程创建独立窗口会话和 DI scope，Window/ViewModel 在该 scope 内解析；窗口关闭后 scope 释放，不从根容器解析 scoped 编辑状态。

重复 Show 复用当前窗口，导航/激活但不重建草稿；关闭再开创建新会话。pageKey 的既有集合保留，null、未知或尚未开放项转 general，不记录原始 Payload/pageKey。ShowAsync 在窗口显示/导航完成后返回，不等待窗口关闭。

每个会话有独立身份、草稿、主题预览和异步工作所有权。迟到回调必须校验会话，不重新显示旧窗口，也不能释放新会话的资源。构造/Host.Build/解析 Presenter 不要求已有 WPF Application。

## 3. 草稿、冲突与提交输入

T-018 仅编辑四个标量字段，用 internal 编辑快照保存各字段的 base、目标值及 dirty 标志。Current 只读；不复制整份旧 AppSettings 后整体覆盖，也不提前编辑 Provider 集合。

点击保存时在 UI 线程固定一次输入，保存期间禁止重复提交或修改该份输入。常规保存服务在 ISettingsService.UpdateAsync 的 mutate 内针对最新 Current 检查：

- 未编辑字段沿用当前值，包括 UiLanguage、CheckForUpdates、窗口位置、Provider 等。
- 已编辑字段若 Current 既不同于 base，也不同于目标值，则本次未提交，返回冲突，不写盘。
- Current 已等于目标值时不强制产生新版本/写盘。
- 只在 `current.General` 上增量构造新值；参数先验证，不能把 UI 无效值交给 Normalize 静默挽救后声称按原输入保存。

外部 Changed 发生时，未编辑字段可跟随当前值；已编辑字段保留用户输入及其原基线。Changed 处理器快速排队，在 UI 回调中读取适当的最新状态，不能在设置写锁内等待 UI 或再次保存。

保存成功后按实际发布/最新可见值更新基线；保存失败/冲突保留草稿，提供明确的“重新载入当前设置”操作，不自动丢弃或覆盖同字段冲突。同一值反复切换回 base 不应继续标脏。

## 4. 常规保存服务及状态

App 内以一个小型 internal 服务协调保存/应用，VM 不散落持久化步骤；结果类型保持 internal，不新增设置服务公共签名。应明确表达：

| 结果 | 含义 |
|---|---|
| 未提交 | 校验、冲突、写盘失败或提交前取消；草稿保留 |
| 已保存且应用完成 | UpdateAsync 正常返回，目标值已持久化/本来已相同，必要应用完成 |
| 已保存但应用未完成 | 配置已提交，但开机启动注册等后续动作失败；保留已保存事实，允许重试应用 |

UpdateAsync 正常返回是本流程的保存边界；其后的迟到取消不能把结果改写为未保存。冲突可通过安全的 internal 异常/结果中断 mutate，不扩展 ErrorCode。

无 dirty 字段时不写 JSON 或注册表。仅本次明确编辑的 LaunchAtStartup 触发保存后的系统应用；即使配置目标已被其它操作写成相同值，仍可幂等应用该明确意图。注册已匹配时 AutoStartService 不重复写。独立“重试应用”只读取最新已保存的 LaunchAtStartup，不再次提交草稿或写 JSON。

相关保存/应用入口串行协调。平台同步操作在后台执行，提交后不使用调用方迟到取消去跳过已承诺的应用；方法等待真实任务结束，应用失败以安全状态返回，不回滚已经保存的配置。

T-019 将凭据 staging、完整 Profile 发布和 Registry 屏障接入同一保存入口；不能先单独保存 General、再保存 Providers，却宣称整窗原子保存。本单不提前实现 Provider 或密钥写入。

2026-10-01 更新：Core 协调器已由 T-019A / CCR-011 验收。T-019B 按 PROVIDER_SETTINGS_UI.md 将常规适配重载和服务页统一委托 ISettingsEditCoordinator，沿用本文已验收的冲突/冻结/预览/自启/关闭边界；进程级 Provider 回执保留不依赖窗口 scope 存活。

2026-10-03 更新：T-020A 按 HOTKEY_SETTINGS_EDITING.md / CCR-015 给同一 SettingsEditPlan 增加快捷键增量；一次提交、未编辑字段跟随最新配置和提交后状态语义保持。T-020B 才接快捷键页，禁止以单独 UpdateAsync 绕过全窗口联合保存。

2026-10-03 / T-020B：HOTKEY_RECORDING_UI.md / CCR-016 现为快捷键页、录入/关闭和应用状态的正式补充。设置会话释放前须完成短时录入的物理清理；保存计划包含 HotkeyChanges，并保留 Provider 回执与自启状态各自语义。不得同步等待录入线程或用窗口 Dispose 代替异步清理屏障。

## 5. 主题预览

ThemeService 增加 App 内部预览所有权。有效预览覆盖最新持久主题；只有用户实际选择主题时才改变预览，不写 ISettingsService。

取消/关闭结束本会话的预览，恢复此时 Current.General.Theme，不恢复打开窗口时的陈旧值。Changed/SystemEvents 的迟到回调执行时重新计算有效主题，不用旧事件值覆盖当前预览。旧会话无权清除新会话预览。

保存成功后释放预览，保存失败时继续保留供修正；若保存后用户继续编辑，可重新取得本会话预览。所有主题应用在 UI 线程。停止/已释放的 ThemeService 不接受迟到应用或重新订阅。

## 6. AutoStartService

平台实现位于 Platform.Windows/SystemIntegration，通过 AddIrisPlatformWindows 注册。构造不读写注册表；测试可注入路径解析及注册表边界。

仅管理 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 下的 Iris 值，不触碰其它值、HKLM、RunOnce、StartupApproved。默认不在启动或打开设置窗口时重写它。

- 预期命令为带引号的当前 Iris apphost 绝对路径（例如实际构建生成的 Iris.App.exe），使用 REG_SZ，不追加用户输入/启动参数。
- 路径需非空、完整、为有效 apphost 表达，不包含引号/NUL/换行；不能将 dotnet.exe 当成应用命令。命令长度含引号不得超过官方 260 字符上限，不截断。无可用路径时安全失败。
- IsEnabled 只表示本应用预期命令以 String 类型注册；比较可使用 OrdinalIgnoreCase。缺失/不匹配返回 false；读取失败抛 IrisException(StorageFailure)。不保证 Windows 会实际执行，不推断系统策略。
- SetEnabled(true) 已匹配时无操作，否则写预期命令；SetEnabled(false) 只删约定值，缺失时无操作。禁用不依赖能够构造启用命令。
- 读写失败使用固定安全文案的 StorageFailure；Detail 只可包含操作名、异常类型/数值诊断，不带原始异常链、可执行文件路径或注册表原值。

这些同步平台方法由 App 保存/状态查询服务在后台调用，不阻塞 UI。JSON 中的 LaunchAtStartup 是已保存意图，实际注册可不同；页面可展示应用不一致并提供重试，但不能把差异伪装成 JSON 未保存。

T-039 负责安装/更新后的稳定启动路径与迁移；本单不创建不存在的 Update.exe/stub。注册表自动测试只用 fake 或隔离测试子键，不用用户真实 Run 值作夹具。

## 7. 关闭和正常退出

“取消”、X、Esc 放弃未保存编辑并关闭，不隐式保存。保存进行中暂不受理普通关闭，编辑会话保持到结果明确；不阻塞 Dispatcher 消息循环。

正常托盘退出在 AppBootstrapper.StopAsync 的同步服务清理之前，调用内部设置 ShutdownAsync：阻止新会话/保存，取消提交前工作，等待已经接纳的保存/应用任务实际退出，然后关闭会话、释放 scope/预览，再继续释放 ThemeService、SettingsService 等依赖。无需改 IAppLifetime 或 Presenter 公共签名，也不新建全局任务框架。

不要用 WaitAsync 超时丢掉原任务后销毁其依赖。保存失败在边界安全观察后仍允许退出，不能由迟到回调重开窗口。系统强制终止无法承诺完成异步保存；本单不阻塞或取消系统注销，也不把既有 OnExit/SessionEnding 的同步收尾宣称为已等待保存完成。

2026-10-01，T-018-R4 失败边界澄清：保存操作已结束但返回失败，与窗口/会话清理本身失败不同。若窗口拒绝关闭或会话清理尚未完成，Settings Shutdown 不能正常返回成功；失败应由正常退出调用方观察。AppBootstrapper 此时保留其依赖，不继续同步 Dispose、Host.StopAsync 或应用 Shutdown，安全提示退出未完成并允许重试。ShowAsync 的创建失败也必须等待本次回滚完成后交付失败；可见性不代替 Closed 状态。该澄清不改变公共接口或强制系统结束规则。

## 8. 验证与来源

核心验证是实际组合根无循环/无提前窗口、独立编辑会话、增量冲突/无操作保存、主题预览归属、提交前后取消和注册失败状态、正常退出等待所有权，以及真实 WPF 基本交互。

允许在独立测试进程中注入临时 IAppPaths、fake AutoStart/测试子键与实际窗口，验证可视流程；不新增产品级数据目录开关，不写用户真实设置/Run 项。若无法执行视觉/点击验证，明确记录未验证，不能用普通类模型替代。

准备证据保留于 SETTINGS_EDITING_PREPARATION.md 和 T-018_WORK_ORDER_CANDIDATE.md：已核查 Run/RunOnce 官方 260 字符与延迟执行规则；独立 Microsoft DI 原型验证了循环依赖拒绝和延迟窗口 scope 方案。它们不是 T-018 实现验收。T-019 与 T-015 的未批准部分仍只作准备稿。
