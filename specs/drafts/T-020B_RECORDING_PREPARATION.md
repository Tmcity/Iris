# T-020B 热键录入与暂停：官方机制准备

> 2026-10-03，Codex。T-020A 正在由 Gemini 实施。本稿只准备 B 单，不批准新的平台端口/全局钩子，不改变 A 工单范围。正式实施仍需 A 验收后定稿。

## 1. 实际读取的官方材料

已下载并读取以下 Microsoft Learn 页面正文，本地缓存位于 `C:/Users/28910/AppData/Local/Temp/iris-t020b-official-prep`：

- [LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc)
- [KBDLLHOOKSTRUCT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-kbdllhookstruct)
- [KeyEventArgs.SystemKey](https://learn.microsoft.com/en-us/dotnet/api/system.windows.input.keyeventargs.systemkey?view=windowsdesktop-10.0)
- [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)

LowLevelKeyboardProc 官方正文原句：

> When this callback function is called in response to a change in the state of a key, the callback function is called before the asynchronous state of the key is updated. Consequently, the asynchronous state of the key cannot be determined by calling GetAsyncKeyState from within the callback function.

> This hook is called in the context of the thread that installed it. The call is made by sending a message to the thread that installed the hook. Therefore, the thread that installed the hook must have a message loop.

> If nCode is less than zero, the hook procedure must return the value returned by CallNextHookEx.

文档还明确：处理过的消息可返回非零以阻止后续 hook/目标窗口接收；回调有 LowLevelHooksTimeout 限制，Windows 10 1709 起最大 1000ms；建议把耗时工作交给其它线程并立即返回。不得通过修改系统注册表延长超时。

RegisterHotKey 对 MOD_WIN 的原句：

> Keyboard shortcuts that involve the WINDOWS key are reserved for use by the operating system.

因此录入/草稿语义有效/Win32 注册成功是三种不同事实；即使控件能识别 Win+键，也不能保证所有系统保留组合可占用。F12 始终保留给调试器的既有拦截继续有效。

## 2. 应选择的机制边界

仅 WPF KeyDown/KeyUp 可以处理普通焦点键事件和 SystemKey，但不能据此声称已抑制 Shell/系统快捷键；仅 Raw Input 监测也不能作为阻止系统动作的证据。若 B 单为完整 Win 录入引入 WH_KEYBOARD_LL，必须先签发窄平台捕获端口与生命周期规范，不把捕获逻辑散落在 ViewModel 中。

候选捕获范围仅为用户明确激活的录入控件会话：非录入状态不保留全局捕获；目标窗口/会话失效时停止新捕获，其它应用前台输入不得被存储/展示/记录。只产生受限键名、modifier 和控制结果，不累计文字、键序列或日志。非法/未处理/nCode<0 路径继续 hook 链，托管异常不能逃出 native 回调。

需要在正式设计中选择 UI 所有者线程或独立消息线程。官方要求消息循环且建议专用线程；若用专用线程，应定义停止/注销失败/回调根引用与异步线程退出屏障，不能为了捕获一键引入无界消息或 UI 同步等待死锁。

modifier 状态不能在回调内用 GetAsyncKeyState 推断新状态。需要明确 KeyDown/KeyUp 的内部状态转移、开始录入时已按住修饰键的处理、重复键、失焦以及被消费按键的释放归属。Escape 取消与 Backspace 清除不能误触 SettingsWindow 自身关闭；Win 键释放不能因为提前结束捕获而引发意外 Shell 动作。此处尚未用真实键盘验证。

## 3. 与 T-017 的暂停/恢复接线

Iris 当前全局注册会影响录入自身组合；WPF Handled 不能撤销已经生成的 WM_HOTKEY。录入前应取得 App 内部暂停所有权，成功撤销 Iris 注册并拒绝迟到/待分发消息后再进入捕获。失败显示安全状态并允许重试，不假装进入已暂停录入模式。

不得复用永久 shutdown 状态作为临时暂停。释放暂停租约按最新持久配置重配，不恢复进入录入前的陈旧快照；暂停期间 Changed 只保留最新意图；旧会话释放不能解除新会话暂停；正常退出先停止录入/暂停准入，再清理原生与已有动作。

已有热键动作的已接纳任务可能仍在读剪贴板，暂停不应无理由取消正在进行的用户翻译。B 需区分未开始的待分发描述与已经开始的工作，定义准备录入期间的 UI 状态与最终准入；不能仅对 Dictionary.Clear 后声称队列已失效。

## 4. B 单保存与状态

复用 T-020A HotkeyChanges 与 IHotkeyBindingPolicy，当前页与 General/Providers 一次捕获、一次 Core 提交。运行时注册结果按目标绑定/配置代次表达，不复用 Provider 回执；保存成功但原生注册失败仍是已保存。录入的未提交草稿不能被临时注册为正式快捷键。

重试应用应使用最新已保存目标，普通主题/Provider 更新不反复重注册；页关闭后未完成的资源清理仍有进程级所有者。B 的具体内部状态接口、暂停 lease 与捕获端口待 A 验收后冻结。

## 5. 尚未验证与后续工作

本次只有官方文档读取和代码层边界分析：未安装 hook、未注册任何全局键、未发送键盘输入，也未改用户设置。B 实施需 fake 原生边界的实际实现测试、真实 WPF 焦点/关闭验证与自有资源退出检查；真实 Win/Shell/IME 行为若未实测必须明确标为未验证。不得把合成 KeyEventArgs 或方法名当成物理键盘验收。

## 6. 安装与卸载边界补充

另已实际读取 [SetWindowsHookExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw) 与 [UnhookWindowsHookEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-unhookwindowshookex)，缓存为上述目录的 install-hook.html / unhook.html。

卸载文档原句：

> The hook procedure can be in the state of being called by another thread even after UnhookWindowsHookEx returns. If the hook procedure is not being called concurrently, the hook procedure is removed immediately before UnhookWindowsHookEx returns.

B 若使用全局捕获，必须区分停止接纳、native 卸载成功、已进入回调退出与委托/线程释放；不得把 Unhook 返回 true 直接当作允许丢弃回调引用。卸载失败仍有明确所有者和重试入口，不能将其隐藏为普通 Dispose 完成。

安装失败返回 NULL，应取数值 GetLastError，记录安全原因，不进入“正在录入”的假成功状态。SetWindowsHookEx 的通用文档与 LowLevelKeyboardProc 的安装线程上下文/不注入其它进程特殊行为需结合使用；不能把其它 hook 类型的线程/DLL 规则直接套用。实际 hMod/线程参数和 CsWin32 声明需在 B 正式规范冻结前进一步核查。

KeyEventArgs.SystemKey 页面确认其值是“将由系统处理的事件所引用键”；这只说明 WPF 事件属性含义，不证明 Shell 保留组合一定能到达焦点控件或可被控件独占。
