# 受控 Ctrl+C 键盘模拟

> 2026-10-04，Codex；T-021 / ARCH v1.30。基线为 T-020B/R4 已验收的 361e2c1。本文为正式实施规范；仅增加 Platform.Windows 内部实现，不增加 Core 公开契约。

## 1. 完整目标与边界

实现供后续 ClipboardSelectionReader 使用的 KeyboardSimulator：等待起始修饰键释放、确认目标仍为前台、发送逻辑 Ctrl+C、精确记录本次输入的释放责任、取消/失败/关闭时完整收尾。构造、DI、Host.Build 和普通解析零桌面 I/O、零线程/窗口/hook；只有明确 CopyAsync 调用才读键或发送输入。

不实现 UIA、剪贴板快照/恢复、选区读取、Handler、热键新增入口或划词 UI；这些仍在 T-022/T-023。已验收的 Win32ClipboardService、DesktopContext 和录入服务不重写、不复用为全局输入记录器。不引入 NuGet 包，不改 JSON/default/schema/public contracts。

## 2. 内部模型与生命周期

放在 Iris.Platform.Windows/Selection，以现有 CsWin32 声明生成原生调用。AddIrisPlatformWindows 用 TryAddSingleton 注册 KeyboardSimulator、窄 INativeKeyboardInputAdapter，复用现有 TimeProvider。内部模型可按仓库命名风格调整，但以下语义固定：

```csharp
internal readonly record struct KeyboardCopyTarget(nint WindowHandle, uint ProcessId);
internal enum KeyboardCopyOutcome { Injected, NotReady, TargetChanged, Busy, Failed }
internal sealed record KeyboardCopyResult(
    KeyboardCopyOutcome Outcome,
    bool MayHaveCopied = false,
    ErrorInfo? Error = null);

// internal sealed class KeyboardSimulator : IDisposable, IAsyncDisposable
Task<KeyboardCopyResult> CopyAsync(KeyboardCopyTarget target, CancellationToken cancellationToken = default);
Task RetryCleanupAsync();
Task StopAsync();
bool HasPendingRelease { get; }
```

Target 是调用方已经选定的短暂 HWND/PID 身份，不含窗口标题/选区文本。ToString 不打印句柄、完整输入序列或任意异常。零 HWND/零 PID 抛安全 InvalidArgument；目标为本进程也不执行复制。终端/宿主内终端、排除进程和焦点控件安全性由 T-022 在调用前判定，Simulator 的目标复核不能代替它们。

一次只接纳一个活动 Copy 或清理尝试；忙时直接返回 Busy，不读键、不排无界队列。输入目标在接纳时固定。接纳与活动任务登记先于启动后台工作；原生检查、等待和发送均离开 WPF UI 线程。不使用固定长驻线程、全局 hook、管理员权限或 UIAccess。

StopAsync 在入口永久撤销本实例 Copy 准入，取消尚未开始输入的等待，等待真实活动工作与本次遗留释放；共享在途屏障，失败可再次 Stop/RetryCleanup。停止后 RetryCleanup 只能清理，不恢复准入。DisposeAsync 使用同一屏障；同步 Dispose 只处理未启动/已完整停止资源，活跃工作或遗留按键责任时明确失败，不能同步阻塞 UI。

停止后的 Copy 以安全 InvalidOperationException 拒绝；已 Dispose 后以 ObjectDisposedException 拒绝。Busy 仅表示当前活动/待清理，不表示永久停止。无待释放项时 RetryCleanup 幂等且零 native。

## 3. 起始等待、目标校验和取消点

1. 初始检查 expected HWND 仍有效、其 PID 与 target 一致、当前前台 HWND/PID 仍匹配且不是本进程。查询无有效目标或目标变化返回 TargetChanged，零输入；不通过 SetForegroundWindow/AttachThreadInput 抢回焦点。
2. 在后台读取 L/R Ctrl、L/R Alt、L/R Shift、L/R Win 以及 C 的当前按下位（GetAsyncKeyState 最高位）。最低位“曾按下”不可靠，不使用。每 10ms（TimeProvider）再检查一次，最多 500ms；所有这些键释放后再进入发送。
3. 等待期间检查取消和目标身份；前台/PID 改变即结束。超过 500ms 返回 NotReady，零输入。**不会补发用户起始仍按住的修饰键 KeyUp。** 这是对旧 ARCH §10.4 超时强行释放规则的正式修订：未释放时交由上层 UIA/无结果策略处理；不能制造 Ctrl+Alt+C 或破坏用户仍按住的键。
4. 发送前最后检查取消、起始键状态与目标；在真正进入第一个 SendInput 之前取消抛 OperationCanceledException，无输入。第一次发送调用已经开始后，必须观察其真实返回，不能以 WaitAsync 超时/取消甩开后台副作用。
5. 发送阶段不插入 await/Delay；本轮晚到取消/Stop 不截断应有的 KeyUp 收尾。发出第一个输入以后按实际结果返回 Injected/TargetChanged/Failed，不将已经可能发生的复制包装成“未发生副作用的取消”。

GetAsyncKeyState 返回 0 也可能表示桌面/UIPI 限制，不能从“未见按下”推断权限必然可用。SendInput 仍以实际返回值判定。前台检查和输入投递不是原子操作，不能承诺任意切换竞争中绝对定向投递。

## 4. 单事件确认与本次释放责任

本单以**单个 INPUT 的实际返回值**为确认单位，按顺序调用 SendInput：LCtrl down → C down → C up → LCtrl up。每次 cInputs=1，返回 1 才确认该事件插入。这样明确本次哪些 KeyDown 被接纳、哪些 KeyUp 已完成；不依赖未声明的“部分批量成功一定是某个前缀”假设。

INPUT 使用 INPUT_KEYBOARD=1，wVk 分别 VK_LCONTROL (0xA2)、VK_C (0x43)，wScan/time/dwExtraInfo 初始化为 0；down flags=0，up flags=KEYEVENTF_KEYUP (0x0002)。不用 UNICODE/VK_PACKET、PostMessage/SendMessage 或键盘 hook。cbSize 取实际 CsWin32 INPUT ABI 大小，不在 x64 上沿用 32 位常量。窄 adapter 应有可测试的真实 INPUT 构造路径。

每个 KeyDown 返回 1 后记录本次尚需释放的键；KeyUp 返回 1 后移除。C down 插入成功或其调用结果无法确认时，MayHaveCopied=true；它表示复制可能发生，**不表示剪贴板已经更新或目标应用完成复制**。四个事件确认完成返回 Injected，MayHaveCopied=true，Error=null。

在 LCtrl down 和 C down 之前复核目标身份；中途目标变化不得向新目标继续发送 KeyDown，只结束本次自有 KeyUp，返回 TargetChanged 与准确 MayHaveCopied。多个单事件调用之间 Windows/其它程序可能插入输入；本方案不声称具备四事件单批的不可交错保证。上层 T-022 仍须在取结果前复核源身份。

任一发送返回 0/异常/不合法数量，停止后续正常 KeyDown 序列，对本次未释放的键按 C、LCtrl 的反向顺序做一次有界补偿；每个待释放键在该次补偿最多尝试一次，不忙等重试。0 确认没有插入当前事件；异常等无法确认当前 KeyDown 是否发生时保守保留该次计划键的释放责任。绝不释放其它键，也不把用户进入等待前的按住状态当成本程序所有。

补偿成功仍返回本次 Failed（安全 Unknown 与可选 Win32 数值），不重发整组 Ctrl+C，不冒称原复制或目标状态已回滚。补偿失败保留待释放集合与活动所有权，HasPendingRelease=true；后续 Copy 返回 Busy，RetryCleanup/Stop 只重试仍待释放的 KeyUp。每次成功释放的键不重复发送，不回放旧 Copy 请求。目标切换或调用方取消不能让本次已注入的 Ctrl 永久留在 down。

原生错误可能来自 UIPI，但 SendInput 返回值/GetLastError 本身不证明 UIPI。日志只记操作阶段、数值错误与安全异常类型，不输出键盘历史、目标标题、原始异常或 clipboard 文本。不能保证用户在发送间隙同时按住同一物理键时完全无影响；该输入流限制须如实记录，不用隐藏注入/权限提升绕过。

## 5. 必须的自动化验证

实际 KeyboardSimulator + fake native + FakeTimeProvider；不能只测第二份参考状态机。

- 构造/解析/未调用零 native；单例 DI；已停止不接纳；Busy 不排队、不读取键。
- 左右修饰键与 C 未释放、释放后发送、500ms 边界、取消、前台/PID 改变，均证明发送次数为零或正确开始；不注入用户起始 KeyUp。
- 真正使用的 INPUT 构造验证 type、VK、flags、清零字段、cInputs=1、cbSize；完整正常顺序与 MayHaveCopied。
- 各个 down/up 返回 0、抛错、异常返回数量；只释放本次尚欠 KeyUp；成功项不重复；Failed 不自动再执行 Copy。
- 补偿失败保留责任，Busy/RetryCleanup/Stop 及并发停止；真实 native 调用阻塞闸门时取消/Stop 仍等待，放行后收尾与结果一致。
- C down 前目标变化只清理 Ctrl；C down 已开始后目标变化/取消不得漏 KeyUp，不虚报已确认取得选区文本。
- 真实 DI/Host 构建与普通同步 Dispose 无提前 I/O；活跃实例异步停止完成后容器收尾。所有闸门先确认 entered，finally 放行并 await 真实工作，错误可见。

自动化**不得调用真实 SendInput、读用户键盘/剪贴板、注册真实热键或 hook、访问用户设置/密钥/Run、真实 HTTP、性能脚本**。真实平台实现正常使用 Win32，测试替换窄适配器。物理键盘、不同布局/IME、UIPI 与目标应用兼容性未执行则标未验证。

## 6. 官方依据与后续接线

已读取 Microsoft [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)、[GetAsyncKeyState](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate)、[GetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getforegroundwindow)，缓存与事实见 T-021_SELECTION_PREPARATION.md。2026-10-04 又实际读取 [INPUT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-input)、[KEYBDINPUT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput)，均 HTTP 200；缓存目录 iris-t021-official-prep。

T-022 须另行定稿 A08：终端与焦点控件安全、UIA 有界超时、读取互斥、剪贴板清空/序列号归属、多格式快照/外部更新保护、正常退出接线。T-021 通过不代表划词翻译已交付，不在本单开放入口。没有公开契约签名变化；此内部设计与 ARCH 行为说明由架构师直接批准。
