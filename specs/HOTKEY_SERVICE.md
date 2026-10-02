# 全局热键、注册归属与生命周期

> 2026-10-03，Codex；T-017 / ARCH v1.27 / CCR-014 正式规范。
> 复用 IHotkeyService；仅澄清契约注释，不新增公开签名、配置字段、错误码或 NuGet 包。

## 1. 组件与线程

Platform.Windows 实现 HotkeyService、KeyNameMapper 和窄原生适配器；使用 CsWin32 自建 HWND_MESSAGE 窗口、窗口类与 WndProc，不引用 WPF/WinForms。构造只保存依赖，无 HWND/注册/I/O。首次有效、启用的 Register 必须由 App 的 UI 线程调用，在该线程创建消息窗口并绑定所有者线程；之后有原生资源的 Register/Unregister/UnregisterAll/Dispose 必须同线程，跨线程调用明确拒绝，不自行 Task.Run 或同步等待 UI。

从未初始化/已经释放的 Dispose 幂等且不需要调度；已 Dispose 的 Register 抛 ObjectDisposedException。窗体、窗口类、WndProc 委托/实例关联保持存活到原生销毁确认，失败的创建/销毁分支同样有所有者，不丢失清理重试入口。

App 内部 HotkeyCoordinator 负责 UI 调用、设置观察、已开放动作过滤和退出；构造不解析会创建 HWND 的服务。AddIrisPlatformWindows 注册服务，但普通 DI 解析不能自动注册用户热键。当前 Program.Main 是 STA，不能据此假定所有测试或未来服务解析都在拥有消息循环的线程。

## 2. 键名与校验

KeyNameMapper 只支持 ARCH §10.3 列表：A–Z、D0–D9、F1–F24、NumPad0–9、Space、Enter、Tab、Escape、Insert、Delete、Home、End、PageUp、PageDown、Left/Up/Right/Down、Snapshot、OemTilde/OemMinus/OemPlus/OemOpenBrackets/OemCloseBrackets/OemPipe/OemSemicolon/OemQuotes/OemComma/OemPeriod/OemQuestion。使用显式 VK 映射；允许首尾空白和 OrdinalIgnoreCase，不接受数字字符串/VK 数字、组合字符串或未列出的别名。不得通过 WPF KeyInterop 引入反向依赖。

校验：HotkeyModifiers 仅允许 Alt/Control/Shift/Win 位；至少一个修饰键，单独 Snapshot / F13–F24 例外；仅 Shift+字母非法。F12 能解析但禁止注册，因为官方保留给调试器。未知/空 Key、未知 modifier 位或违反组合规则返回 Success=false、HotkeyInvalid，且不改变现有注册。null binding 抛 ArgumentNullException，未定义 Action 抛 ArgumentOutOfRangeException，均不进行原生调用。

Enabled=false 不校验 Key 的可注册性，仅注销同 Action；成功或无记录返回 Success=true，实际注销失败返回失败，不能无条件报成功。有效注册调用加入 MOD_NOREPEAT（0x4000），不持久化该位到用户 modifiers。

## 3. 注册记录、ID 与失败

每次有效注册分配新 ID，区间 1..0xBFFF，进程内不复用已消耗 ID。耗尽返回 Unknown 固定安全提示，不回绕、不覆写已有注册、不通过清空线程消息队列回收 ID。ID 是内部实现，不用 Action 数值作为永久注册 ID。

Register 对已有 Action 的有效新绑定先注销再注册；不能假设 Win32 的同 HWND/ID 注册自动替换。输入预校验或 ID 耗尽先于注销。开始替换/注销时撤销旧记录的分发资格；UnregisterHotKey 失败则保留资源记录用于重试，不叠加新注册，旧排队消息不再分发。

RegisterHotKey 失败码 1409 → HotkeyConflict；其它原生注册/初始化失败 → Unknown。返回结果携带原 Action、默认安全文案，日志只含 API、数值错误、Action/规范化 VK/修饰位、异常类型；不输出原始无效键字符串、完整 settings 或 Exception 对象。

Unregister 无记录幂等。UnregisterAll 尽力处理全部记录，失败项保留；Unregister/UnregisterAll 的清理失败抛 IrisException(Unknown)。Dispose 先完成注册清理，再销毁自有窗口/窗口类与回调关联；不能仅 Clear 托管集合或释放仍可能被 native 回调使用的对象。失败保留所有权以供同线程重试，成功后重复 Dispose 无原生操作。

## 4. 消息处理

WM_HOTKEY 只接受本服务当前有效 ID；同时核对 lParam 的 VK 和四个实际 modifier 位，MOD_NOREPEAT 不当作需按下的额外键。未知/退休 ID、重配前消息、字段不匹配、停止接纳后的消息均丢弃。

Pressed 在窗口所有者 UI 线程触发，平台不依赖 IActionDispatcher 或业务窗口。WndProc 必须尽快返回，禁止同步等待翻译/剪贴板 Task；托管异常不能跨原生回调边界。异常记录安全类型/原因，不能导致注册表与原生资源失联。

## 5. App 注册与设置更新

启动在 settings.Load 与既有服务准备后、UI 仍可调度时初始化协调器。只注册实际 IActionHandler 集合中已交付动作且 Enabled=true 的绑定；不改变默认配置。当前 InputTranslate 默认启用，ClipboardTranslate 默认禁用；未实现的截图/划词等默认绑定保留配置但不占用系统组合键。

设置 Changed 可来自后台。只比较 Hotkeys 的语义内容，普通主题/Provider/历史更新不触发重注册。UI 上串行应用最新快照，至多一个当前应用任务和一个最新待应用快照；停止后不再接纳。相同绑定不反复注销/注册。

为支持两动作交换键组合，App 对受影响旧绑定先完成撤销，再按 Action 数值稳定顺序注册新绑定；未变化项保持。此层不承诺整组原子事务或旧配置回滚，逐项保留并展示实际结果。某项注销失败则保留其清理状态，不覆盖或重复叠加；其它项可继续。所有冲突/无效项汇总为可读安全提示，包含动作及规范化组合，不因一项失败使应用退出。

## 6. 分发与退出

App 将有效 Pressed 转为 ActionTrigger.Hotkey，经现有 IActionDispatcher 分发，不直接调用 Handler/VM。入口先限制每 Action 的待处理描述，避免为每个消息无限建 Task；UI 队列回调执行时检查协调器是否停止。平台 WndProc 不承载耗时业务；MOD_NOREPEAT 不替代 T-014 防重入。

正常退出分两步：先关闭热键/设置重配准入，在 UI 注销/销毁自己的消息窗口并取消待处理动作；然后配合既有设置/翻译 Presenter 完整退出，观察已接纳的分发任务，最后停止主题/托盘、Host、Dispatcher。不得先停 Dispatcher 再注销，也不能提前释放仍持有前置读取的租约。重复停止共用在途屏障，故障保留资源和重试状态。

App.OnExit 的同步兜底只做当前 UI 线程能够完成的原生停止/注销，不同步等待业务 Task。正常路径必须已经完成异步屏障；不能把兜底当作正常完整退出。根容器最终 Dispose 不得再次从后台访问活跃 HWND。

## 7. 验证范围

测试调用实际实现，通过窄原生适配器控制创建、注册、注销、销毁与消息到达；不以参考状态机替代生产服务。键映射期望独立于被测 mapper，覆盖边界、OEM、F12、非法 flags、单键和仅 Shift+字母。检查 MOD_NOREPEAT、所有者线程、旧 ID/错 VK/错 modifiers 消息拒绝、ID 耗尽、重复动作、冲突与部分失败重试。

App 用真实注册路径/协调器/Dispatcher、fake 原生服务验证已开放动作过滤、默认禁用保持、普通设置不重配、键交换、后台 Changed 合并、冲突汇总、退出期间排队消息及真实前置读取等待。所有闸门在 finally 放行，等待完成后释放自有资源；未初始化、初始化中途失败、正常退出和失败再试分别覆盖。

本轮允许与必要时使用自有不可见消息窗口做原生消息循环验证，不在自动化中占用用户 Ctrl+Alt 等真实全局组合、不发送物理键盘输入或改用户设置。真实按键/系统冲突及多环境性能未执行时明确“未验证”，模拟 WM_HOTKEY 不等于物理键盘通过。T-020 再实现快捷键设置 UI，T-017 不提前开放未交付功能。

## 8. 依据与准备证据

已实际读取 Microsoft [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)、[UnregisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-unregisterhotkey)、[WM_HOTKEY](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-hotkey)。关键限制含 F12 保留、ID 区间、MOD_NOREPEAT、注册线程消息归属与注销返回值。

准备稿 T-017_WORK_ORDER_CANDIDATE.md §8 的隔离消息窗口探针已确认不可见、自有消息在创建线程处理及销毁/注销；未注册热键，不代替本单 HotkeyService 及 App 接线验收。
