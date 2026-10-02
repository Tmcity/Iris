# T-017 热键签发前准备

> 2026-10-03，Codex；T-016 开发期间的候选设计与官方依据。
> **不是当前实施工单，也未批准契约变更。** T-016 验收后再定稿 T-017；不修改正式规范、默认配置或正在实现的代码。

## 1. 当前确定的契约与分层

- IHotkeyService 位于 Core/Platform，含 Register、Unregister、UnregisterAll、Pressed、IDisposable；现有注释要求 UI 线程创建/调用，Pressed 在 UI 线程触发。
- 同一 Action 重注册为先注销再注册；Enabled=false 等价于注销并返回成功。不能将“注册新键失败自动保留旧键”当成既有契约保证。
- ARCH §10.3 要求 Platform 自建消息窗口/Win32 WndProc，不引入 WPF/HwndSource，使用 MOD_NOREPEAT。T-017 完成按键服务与启动/设置变更重注册，T-020 才做录入 UI。
- AppBootstrapper 目前有注册热键的 TODO；根容器及启动路径不能在非 UI 线程提前解析一个会创建 HWND 的单例。

## 2. 本次实际读取的官方文档

2026-10-03 使用 HTTP 请求实际读取，均返回 200；页面副本保留在系统临时目录 `iris-t017-official-preparation`：

- [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)
- [UnregisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-unregisterhotkey)
- [WM_HOTKEY](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-hotkey)

核实的事实：

1. MOD_NOREPEAT（0x4000）避免键盘自动重复产生多个热键通知；不代替应用动作的异步防重入。
2. F12 始终保留给调试器，官方明确不应注册为热键。ARCH 的键名映射列表包含 F1–F24，因此须在签发时区分“能解析 F12”与“允许 Register F12”，不能忽略官方限制。
3. 应用的热键 ID 范围为 0x0000–0xBFFF；共享 DLL 的区间不同。实现不应直接接受任意整数 ID 或无界增长后溢出。
4. WM_HOTKEY 的 wParam 是注册 ID；lParam 低位字包含修饰键，高位字是 VK。消息会被放入注册线程的消息队列；不能把一次消息本身当成已通过当前配置/生命周期验证。
5. UnregisterHotKey 属于注册线程；返回零表示失败，需要读取 GetLastError。失败不能仅从托管字典删除后假装已释放。
6. RegisterHotKey 的冲突/系统保留组合具有平台规则；文档提到某些系统默认热键在前台窗口时可能被覆盖，不能因为一次注册成功就宣称全平台行为完全一致。

本次没有调用 RegisterHotKey / UnregisterHotKey，也没有创建或注册原生消息窗口；以上是文档事实，不是原生实现验收。

## 3. T-017 签发前需要定稿的决策

| 问题 | 候选收敛方向 | 原因/验证重点 |
|---|---|---|
| 哪些配置项实际注册 | App 协调器只注册已交付 Handler 对应动作，保留所有配置值；未交付功能以后接入 | HotkeyDefaults 已启用 SelectionTranslate / ScreenshotTranslate / ScreenshotOcr，但这些功能尚未实现，不能无意占用用户组合键 |
| 默认禁用项 | 尊重 Enabled=false，不因功能刚交付自动启用 | ClipboardTranslate 默认禁用，InputTranslate 默认 Ctrl+Alt+F；不能把“可用”误写成“全部已注册” |
| F12 与非法组合 | 解析表可保留 F12，但注册校验拒绝系统保留键；明确空/大小写/OEM/未知位规则 | 不擅自改变现有设置序列化、键名或默认值；行为注释若需补充走 CCR |
| ID 与迟到消息 | 注册代次使用明确 ID 所有者，注销/重配后拒绝旧 ID/不匹配 VK/修饰键的消息；定义有限 ID 耗尽行为 | 不能立即复用同 ID，把旧排队消息解释为新绑定；不能因断言通过而简单清空全部消息队列 |
| 重注册失败 | 按既有先注销语义报告实际结果；注销失败保留真实资源所有权，禁止重复叠加注册 | 不承诺没有契约依据的全组事务/自动恢复；同一组合的内部冲突与系统冲突要分清 |
| UI 线程与生命周期 | App 通过明确 UI 创建/调用桥接拥有 Platform 单例，构造时机和 Dispose 线程可验证 | Platform 不依赖 WPF；不能靠随处 Task.Run 或根容器自动 Dispose 在任意线程销毁 HWND |
| 设置变更 | 在 UI 串行应用最新绑定快照、合并重复更新，退出后不再接纳 | ISettingsService.Changed 可以从后台到达；旧异步重注册不能覆盖新配置 |
| WndProc 分发 | 尽快返回，异常不能穿越原生回调；App 将有效 Pressed 路由现有 Dispatcher，跟踪异步任务 | 不能在 WndProc 同步等待完整翻译，也不能让 ActionDispatcher 的存在代替平台资源回收 |
| 正常退出 | 先阻止新热键动作并注销/销毁自有消息窗口，再观察已接纳动作、Presenter 等待和 Dispatcher 停止 | 不允许停止 Dispatcher 后再试图在 UI 释放注册；注销/销毁失败须如实保留/报告 |

这些方向尚未正式批准，不要求 Gemini 在当前 T-016 中实现。

## 4. 推荐验证证据

先通过窄原生适配器控制 Register/Unregister/窗口创建/销毁失败、线程标识、消息 ID 和排队交错，调用实际 HotkeyService；KeyNameMapper 用独立 VK 预期覆盖正式列表，不能以 mapper 自己的输出生成期望。

必须区分“注册成功”“Pressed 被识别”“Dispatcher 接纳”“Handler 完成”，尤其是 MOD_NOREPEAT 与异步防重入。设置更新期间注入旧 WM_HOTKEY，确认不触发已撤销动作；退出后不得再分发或重建 HWND。

真实按键/系统冲突验证应在正式工单明确安排后执行，优先使用隔离的自有资源；当前准备阶段不抢占用户默认组合键、不注入物理键盘输入。无法验证的 Shell/系统策略/实际冲突条件明确记录，不能由假适配器推导实机通过。

T-016 验收后再综合其实际 Handler/退出接线决定正式方案，不提前修改 BACKLOG、CONTRACTS.cs、ARCH 或设置示例。
