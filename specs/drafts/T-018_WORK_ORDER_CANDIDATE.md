# 工单候选 T-018：设置窗口、常规草稿与开机启动

> 2026-09-30，Codex；承接 SETTINGS_EDITING_PREPARATION.md。
> 已于 2026-09-30 随 T-022A-R1 验收转为正式 SETTINGS_EDITING.md / T-018 / CCR-010。下文保留签发前候选与 DI 原型记录；当前实施以正式规范和 tasks/current_task.md 为准。

| 字段 | 候选内容 |
|---|---|
| 基线 | 待填：T-022A 最终验收提交 |
| 目标 | 提供可从托盘打开的真实设置窗口、可取消的常规编辑和明确的保存结果 |
| 前置 | 既有 Settings/Theme/ActionDispatcher；不依赖尚未实现的热键或翻译窗口 |
| 后续 | T-019 增加翻译服务页和凭据提交协调；T-015 再接翻译窗口 |

## 1. 功能范围

实现 SettingsWindow、ViewModel、ISettingsWindowPresenter、Core/Workflows/OpenSettingsHandler、托盘设置入口、常规保存服务，以及 Platform.Windows/SystemIntegration/AutoStartService。

首批仅开放四个可实际生效的常规项：Theme、LaunchAtStartup、ShowStartupNotification、LogLevel。日志级别使用现有规范化集合 Verbose/Debug/Information/Warning/Error，不擅自添加 Fatal。语言切换、更新检查及其它页面未交付前不开放无效控件；保存保留所有未编辑字段。

使用已有 WPF-UI/资源机制；必要的小型 internal ViewModel/命令帮助代码可在 App 内实现，不先添加新的 MVVM 包。文本走资源，业务逻辑不放在 code-behind。

不实现翻译服务配置、密钥读写、热键、翻译窗口、自动更新或安装器。不得为展示未来页面创建空功能服务。

## 2. DI 接线与窗口生命周期

构造链应为 Tray → ActionDispatcher → OpenSettingsHandler → SettingsWindowPresenter；Presenter 只接收 UI 调度和窗口会话工厂等构造期依赖。

**避免循环依赖**：Presenter 不直接注入 ActionDispatcher；也不在构造时创建 Window/ViewModel。窗口里的命令确有需要时，可以在后续实际创建的会话中取得 Dispatcher，此时 Dispatcher 已完成构造。通知服务保持既有构造期不依赖 Tray 的模式。

建议 singleton Presenter 持有当前窗口会话，首次 Show 才创建专属 DI scope，其中解析 Window 和 ViewModel；Closed 后释放 scope。不要从根容器解析 scoped 编辑状态，也不要将“关闭即释放”的 SettingsWindow 注册为永久复用的单例。

重复 Show 导航/激活当前窗口，不重新载入或丢弃草稿；未知/尚未开放 pageKey 回到 general，不能记录未经处理的 Payload/pageKey。关闭后再次 Show 创建新会话。开始/结束预览、保存及迟到回调均绑定会话身份，旧会话不能修改新窗口。

OpenSettingsHandler 只依赖 ISettingsWindowPresenter，通过 Core 的 DI 扩展注册；实际 App 组合根提供真实 Presenter，纯 Core 容器测试需提供 fake Presenter。托盘的设置菜单改为经 IActionDispatcher 派发 OpenSettings，不复制窗口创建逻辑。WPF 事件入口妥善观察异步错误。

## 3. 常规编辑模型与冲突规则

T-018 可仅建立包含四个标量字段的 internal draft/edit snapshot，不必预先克隆或编辑整个 Provider 集合。保留每个字段的基线值、目标值和 dirty 标志；Current 永远只读。

保存输入先在 UI 线程固定，保存期间禁止重复保存或修改该份输入。实际 UpdateAsync 的 mutate 在其写锁内执行如下检查：

- 未编辑字段沿用最新 Current。
- 已编辑字段若 Current 既不同于 base，也不同于目标值，则本次未提交并返回冲突。
- Current 已等于目标值时可作无操作；不为了清 dirty 强制写盘或通知。
- 应用变更使用 `current with { General = current.General with { ... } }`，不把整个旧 AppSettings 或旧 General 原样覆盖回来。

窗口收到外部 Changed 时，未编辑字段可跟随最新值；已编辑字段保留原基线和用户输入，以便在真正提交时识别冲突。事件回调快速排队，不能在设置写锁内等待 UI/再次保存。

保存成功后以实际发布/最新可见设置重新建立基线；保存失败或冲突保留草稿。提供明确的“重新载入当前设置”操作，由用户主动放弃本次草稿，不能自动解决同字段冲突。

## 4. 保存入口和返回状态

App 内新增小型 internal 常规保存服务，VM 不直接组合多个持久化调用。保存服务接收固定的编辑快照和 CancellationToken，通过既有 ISettingsService 完成一次增量更新。

返回状态应能区分：

| 状态 | 含义 / UI 行为 |
|---|---|
| 未提交 | 校验、冲突、写盘失败或提交前取消；草稿保留 |
| 已保存且应用完成 | 更新基线/dirty 状态，仍可继续编辑 |
| 已保存但应用未完成 | JSON 已提交，开机启动等后续应用失败；显示已保存事实和重试应用入口 |

状态类型保持 internal，避免 T-018 提前冻结一套 Provider 保存公共契约。T-019 会把凭据 staging/联合 Profile 更新/发布屏障接入同一保存入口；不能届时先保存 General、再独立保存 Providers，却称为原子保存。

无变更时不重复写配置或注册表。只对明确修改的 LaunchAtStartup 执行本次系统应用；重试应用读取最新已保存意图，不再次写 JSON。相关保存/应用入口由同一服务串行协调。

## 5. 主题预览

ThemeService 增加内部预览所有权，不新增 Core 主题端口：有效预览覆盖最新持久值。选择主题只预览，不写设置；取消/关闭恢复此时 Current 的主题，而非窗口打开时的值。

Changed/SystemEvents 已排队回调执行时重新计算当前有效主题，不能按陈旧事件值覆盖预览。仅有当前预览所有者能结束预览；旧窗口 Dispose/回调不会清除新窗口预览。保存成功后释放预览，保存失败时继续保留供修正。

所有效果在 UI 调度边界内执行。ThemeService 处于停止/已释放状态时，迟到预览回调不得再次应用资源或恢复订阅。

## 6. AutoStartService 候选精确语义

仅管理 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 的 Iris 值，不触碰其它值、HKLM、RunOnce 或 StartupApproved。

- IsEnabled 表示该值与本应用预期启动命令匹配，不保证 Windows 一定执行。缺失返回 false，读取失败报告安全 StorageFailure。
- SetEnabled(true) 写入带引号的实际 Iris apphost 绝对路径（REG_SZ）；已匹配时无操作。不能注册无参数的 dotnet.exe，不追加用户输入。
- SetEnabled(false) 移除约定的 Iris 值；缺失时无操作。默认不在程序启动时重新登记，避免覆盖系统侧改变。
- 无有效 apphost 路径、命令超出官方 260 字符限制或注册表失败，报告安全 StorageFailure，不截断、不带原始异常链/路径。
- 注册表读写为同步平台操作，由常规保存服务在后台调用；不得阻塞 UI。JSON 提交成功后调用失败，返回“已保存但应用未完成”，不虚假回滚。

当前使用现有 apphost 路径；安装器/更新后的稳定启动入口与迁移交给 T-039，不创造尚不存在的 Update.exe 路径。

仅需为 IAutoStartService 的行为补充契约注释，具体 CCR 在正式签发时批准，签名不变。

## 7. 关闭与应用退出

“取消”、X、Esc 按放弃未保存编辑处理，不隐式保存；保存进行中暂不受理普通关闭，保持编辑会话直到结果明确，UI 仍可正常响应。

应用退出使用单独内部 ShutdownAsync：阻止新设置会话/保存，取消提交前工作并等待实际任务退出；已提交后的应用任务也观察到结果后，才释放窗口会话和设置服务。可在 AppBootstrapper 的同步服务清理之前接入，不要求修改公开 Presenter 签名或添加全局任务框架。

禁止通过 WaitAsync 超时丢掉保存任务，再销毁其仍需使用的 scope/设置锁。测试用受控工作建立保存与退出交错。关闭后的回调可以完成资源释放，但不得重新显示窗口或改写新会话状态。

## 8. 验收清单

- 实际组合根 Build/Resolve Tray、Dispatcher、Presenter 无循环依赖、无提前构造 Window；第一次 Show 才创建会话，重复 Show 不重建，Closed 后 scope 释放。
- Core Handler 取消/页键转发；托盘真实入口派发同一个动作；未知页面不构造占位页。
- 四个常规字段的 dirty/no-op/同字段冲突、未编辑字段外部更新；提交时使用最新 Current，保存不改变隐藏字段或 Provider/窗口位置等其它设置。
- 主题预览、取消、外部 Changed、系统主题变化、旧窗口迟到回调、保存失败和应用退出；断言实际主题应用边界，而不是只改 VM 属性。
- JSON 失败与注册表失败能区分未提交/已提交，重试应用不重复写配置；期望命令正确引用路径并限制长度。
- 注册表测试使用 fake 或隔离测试子键，不改用户真实 Run 项；手工注册/注销若执行，单独说明方式与结果。
- 保存中退出时，任务实际结束后才释放其资源；既有主题/日志/托盘/Host 测试继续通过。

UI 自动测试可用自有窗口/会话工厂，正常资源与基本布局可在独立 STA 环境验证；真实点击、DPI/主题视觉效果需要单独标注。不得把 fake Window 模型等同真实 WPF 窗口验收。

## 9. 本轮 DI 原型证据

独立项目：`C:\Users\28910\AppData\Local\Temp\iris-t018-di-prep\Probe.csproj`。只引用已使用版本的 Microsoft.Extensions.DependencyInjection 10.0.12，不引用仓库项目、不创建 WPF 窗口。

原型验证两套候选图：Presenter 直接依赖 Dispatcher 时，ValidateOnBuild 拒绝循环；改为按需创建 scoped Window/Editor 后，解析单例不创建窗口，重复打开复用，关闭/容器释放能释放编辑 scope。

实际命令：`dotnet run --project C:\Users\28910\AppData\Local\Temp\iris-t018-di-prep\Probe.csproj -c Release`

实际输出：

```text
DIRECT_PRESENTER_TO_DISPATCHER cycle-rejected=True
RESOLVE_TRAY_AND_DISPATCHER windows=0 editors=0
REOPEN windows=2 editors=2 disposed-before-host=1
HOST_DISPOSAL editors-disposed=2
```

原型首次编译因目标类型 new() 在 BuildServiceProvider 的 bool/ServiceProviderOptions 重载间产生歧义；显式指定 ServiceProviderOptions 后运行成功。这不是仓库构建错误。原型中的 Window/Editor 是普通测试类型，只证明 DI 和 scope 生命周期，不证明尚未实现的 SettingsWindow 或保存流程。

## 10. 正式签发步骤

填写 T-022A 的验收提交；把本候选与 SETTINGS_EDITING_PREPARATION.md 中确定的 T-018 规则转入正式规范，批准 IAutoStartService/设置 Presenter 的必要注释澄清；再签发唯一 current_task。T-019 凭据编辑公共端口仍另行审议，T-018 不提前实现密钥写入。

本轮没有改当前返工工单、生产/测试代码或用户注册表，只新增候选稿及临时 DI 原型。
