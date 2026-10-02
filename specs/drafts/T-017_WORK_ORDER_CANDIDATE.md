# T-017 签发候选：全局热键与 App 生命周期

> 2026-10-03，Codex；当前唯一执行单仍为 T-016-R1。
> 本文把 T-017_HOTKEY_PREPARATION.md 的待定点收敛为候选方案，不是给 Gemini 的实施指令。T-016 验收后才同步正式规范/必要契约注释并签发。未注册任何真实热键、未创建消息窗口、未测试候选实现。

## 1. 已核对的启动/退出事实

Program.Main 有 STAThread，构建 Host 后解析 App，再 InitializeComponent / Run；App 构造注入 AppBootstrapper，因此不能让新增 HotkeyService 的 HWND 在任意依赖解析阶段悄悄创建。正常 AppBootstrapper.StopCoreAsync 当前先等设置/翻译退出，再停止同步部件与 Host；加入热键后应先关闭新动作准入，不能等窗口退出之后仍允许按键接纳新工作。

候选采用构造无 I/O 的 Platform 热键对象，第一次 Register/UnregisterAll/Dispose 的有资源操作由 App 的 UI 桥接明确调用。创建 HWND 后绑定所有者线程；只有所有者线程允许注册/注销/销毁。有资源时跨线程调用为明确错误，不能内部 Task.Run 或依赖不存在的消息循环。未创建或已经释放的 Dispose 可幂等结束。

当前 IHotkeyService 注释为“必须在 UI 线程创建与调用”，因此正式签发时通过 CCR 明确构造与首次初始化/所有者线程语义；不改公开方法、返回类型或事件，也不引入 WPF 到 Platform。

## 2. 候选产品与注册策略

- App 根据实际已注册的 IActionHandler.Action 集合选择热键，过滤尚未交付的功能；保留 settings 中的所有绑定，不迁移或改默认值。
- Enabled=false 不注册；默认 ClipboardTranslate 仍禁用，InputTranslate 保持配置中的 Ctrl+Alt+F。后续新功能注册 Handler 后，下一版本可接入其已有配置。
- 只对 Hotkeys 的语义变化重配，不因主题、历史或 Provider 字段更新而重新注册。应用最新设置快照，在 UI 串行合并未处理更新；退出开始后停止接纳。
- KeyNameMapper 使用有限的既定键名白名单和显式 VK 映射，不依赖 WPF KeyInterop，也不接受数字字符串充当 VK。大小写可 OrdinalIgnoreCase，首尾空白处理在正式规范统一；不猜测键盘布局或用户输入文本。
- F12 可在映射表中解析，但注册校验拒绝（调试器保留）；修饰位只能来自 Alt/Control/Shift/Win。沿用至少一个修饰键、单独 Snapshot / F13–F24 例外、禁止仅 Shift+字母的规则。
- 输入校验失败不触碰当前有效原生注册。有效的新绑定按原契约先注销同 Action 旧绑定再注册；注册新键失败不宣称旧键仍有效。不提供虚构的整组事务回滚。

## 3. 原生 ID 与消息归属

- 应用使用官方允许区间内的单调 ID（候选 1..0xBFFF），在当前进程的热键服务生存期间不复用已分配 ID；更新相同组合也分配新 ID。
- 用注册 ID 对应 Action/规范化修饰位/VK/是否仍有效的记录；WM_HOTKEY 只接受当前有效记录。未知、已注销或已被新绑定替换的 ID 丢弃。
- lParam 的高位 VK 与低位实际修饰键须与注册记录匹配；MOD_NOREPEAT 是注册行为标志，不应被当成用户必须按下的额外键。
- ID 耗尽要明确失败且保持现有注册状态，不整数回绕、不通过清空用户消息队列复用旧 ID。正式规范应给出安全错误提示/诊断，不能让测试无限分配后静默覆盖。
- WindowProcedure 委托/函数指针和实例关联保持有效直到本窗口实际销毁；失败回滚或 DestroyWindow 失败时保留所有者以便重试。测试实例不共享可变映射。
- WndProc 不等待完整业务 Task，托管异常不越过原生回调边界。平台处理有效消息后在所有者 UI 线程触发 Pressed，App 负责调度而非在平台中调用翻译流程。

## 4. 失败、注销与释放

- RegisterHotKey 的 1409 映射 HotkeyConflict；键/修饰组合不符合规则映射 HotkeyInvalid。其它原生创建/注册失败使用现有 Unknown 的固定文案和安全数值诊断，不新增错误码。
- 同 Action 重配时，如果 UnregisterHotKey 失败，保留旧原生记录与清理入口，不再叠加新注册，不假装成功。Disabled 的“成功注销”也必须以实际结果为准。
- Unregister 不存在的记录为幂等；UnregisterAll 尽力清理全部记录，失败项继续保留，返回/抛出明确失败，后续可以重试。不能仅 Clear 托管字典。
- Dispose 在原生注册全部处理后销毁自有消息窗口、释放回调资源；任何仍可能被 native 回调引用的对象不能提前释放。重复 Dispose 无双重注销/释放。
- 日志只含 Action、允许的规范化 VK/修饰值、API 名、错误码、异常类型；不记录原始无效 Key 字符串、异常对象或完整 settings。

## 5. App 接线与两阶段退出候选

App 内部增加热键生命周期协调器，不增加通用任务总线；实际 Platform 服务仍通过既有 IHotkeyService 暴露。

启动：设置加载及已交付 Handler 注册可用后，切 UI 初始化/注册热键，订阅 Pressed；不在容器构造、后台 Host.Start 或测试普通解析时意外注册。

Pressed：快速接纳受限的动作描述，通过现有 IActionDispatcher 与 ActionTrigger.Hotkey 分发；不能在 WndProc 里同步等待 Handler。App 的排队描述也要按 Action 有界/去重，不能先为每个 native 消息无限创建 Task 再依赖 Dispatcher 拒绝。

退出建议分两步：

1. 关闭热键准入/设置重配，UI 注销注册并清理消息窗口，取消尚未执行的动作描述；已接纳工作保持可观察。
2. 继续既有设置/翻译 Presenter 的完整退出，再观察热键分发任务收尾；最后停止主题/托盘、Host 和 Dispatcher。

目的是避免先停止 Dispatcher 再要求 UI 注销，也避免热键继续重开正在关闭的窗口。失败保留资源与可重试状态，多次退出等待同一在途屏障。热键协调器的取消不得绕过 T-015 的前置租约和实际读取退出。

## 6. 正式签发前审查项

1. 在 CCR 中明确 constructor/owner-thread、失败返回与幂等 Dispose 注释，机械同步 CONTRACTS.cs 和 IHotkeyService.cs；签名保持不变。
2. 明确同 Action 的非法新绑定与合法重注册顺序、ID 耗尽、内部重复组合的稳定判定，避免各测试自行猜测。
3. 原生常量/结构以 CsWin32 及官方文档核对，不能在 ARCH 或代码中复制旧 Win32 示例的线程/ID 假设。
4. 受控测试调用实际服务，通过窄原生适配器验证 HWND 创建/销毁、注册/注销失败、线程、旧消息拒绝和资源重试；KeyNameMapper 用独立 VK 期望表。
5. 组合根测试覆盖构造无 HWND/注册、启动时真实接线、设置变更只重配相关绑定，以及带在途 Handler 的退出顺序。普通 settings 更新不能触发全键重注册。
6. native/真实按键验收单独记证据。当前准备阶段不注册真实组合键、不发送键盘输入，不把 fake WM_HOTKEY 当物理按键通过。

## 7. 官方依据

沿用已实际读取并留存于 `iris-t017-official-preparation` 的 Microsoft 文档：RegisterHotKey、UnregisterHotKey、WM_HOTKEY，链接及核实事实见 T-017_HOTKEY_PREPARATION.md。正式实现前如需新的窗口 API 细节，再补对应官方依据。

本轮只静态核对当前启动/退出与契约并形成候选设计；没有修改生产代码、正式架构、当前工单或任务状态，不要求 Gemini 在 T-016-R1 中提前实现任何上述内容。

## 8. 隔离消息窗口可行性验证（后续准备补记）

本节补充执行证据，不改变上文仍未签发的候选状态。2026-10-03 在系统临时目录 `iris-hotkey-message-window-prep` 创建无仓库 ProjectReference、无额外 NuGet 包的 net10.0 控制台探针；主线程 STA，自行注册唯一窗口类并创建 HWND_MESSAGE 消息窗口。

向该自有窗口 PostMessage 一个合成 WM_APP 范围消息，通过 GetMessage/DispatchMessage 接收，核对回调仍在创建线程；回调销毁窗口并结束消息循环，finally 注销窗口类且保留委托至清理结束。没有 RegisterHotKey/UnregisterHotKey 调用，没有键盘输入或用户窗口操作，也没有创建可见窗口。该探针只证明消息窗口机制在当前环境可行，不是 HotkeyService、热键冲突或真实键盘验收。

Program.cs SHA256：

```text
1C3F823CD748C404A5D4AA8615546318B3D6822A55AE22E2E4B60645021172D9
```

执行 `dotnet run --project C:/Users/28910/AppData/Local/Temp/iris-hotkey-message-window-prep/Probe.csproj -c Release`，退出代码 0；原始输出：

```text
MESSAGE_WINDOW created=True visible=False expected=True/False
MESSAGE_DELIVERY received=1 onOwnerThread=True callbackFailure=none expected=1/True/none
MESSAGE_DESTROY destroyed=True handleStillValid=False expected=True/False
CLASS_CLEANUP unregistered=True expected=True
```

候选正式实现仍须使用项目批准的 CsWin32/原生适配器、真实生命周期和故障注入验证；不能将本次纯可行性探针冒充未来生产实现测试，也不把候选稿标为已验收。
