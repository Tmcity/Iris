# 选区组合读取、复制安全与退出

> 2026-10-05，Codex；T-022C2 / ARCH v1.33 正式规范。T-022B 与 T-022C1/R1 已验收。复用 UIA_SELECTION_READING.md、CLIPBOARD_SNAPSHOTS.md 和 KEYBOARD_SIMULATION.md；本单完成 C 的组合闭环，T-023 才开放 Handler/UI。

## 1. 结构与公开语义

新增 Platform 内部 CompositeSelectionReader，实现现有 Core ISelectionTextReader；公开接口与具体类型注册到同一惰性单例。组合器依赖已验收 UiaSelectionReader、ClipboardSnapshotService、KeyboardSimulator、IDesktopContext 及窄焦点/进程安全适配器。构造、DI、Host.Build、设置页打开不访问桌面或启动原生资源。

ReadAsync 在接纳时捕获独立 options 快照：Order 保留次序并去重，仅接受两种已定义方法；null/无效 options 或超出 [100,2000] 的 ClipboardTimeoutMs 安全 InvalidArgument。空 Order 返回 null 且零平台副作用。UnsafeProcesses 比较文件名，OrdinalIgnoreCase；列表快照不受后续调用方修改影响，不把原始配置内容写入错误。

来源由后台 IDesktopContext.GetForegroundApp 捕获；无有效窗口/PID、Iris 自身或无法确认来源时返回 null。后续 HWND/PID/进程名和焦点身份仍须复核，初始快照不是永久输入授权。

一次最多一个读取/清理上下文。并发新请求安全报告 CaptureFailed / SELECTION_BUSY，不让它变成普通空结果触发上层输入窗。残留清理必须先完成：提供 internal RetryCleanupAsync；新 Read 可在独占准入中先重试旧清理，成功后才开始新的请求，失败不越过责任。Stop 永久撤销准入，不因 Retry 重新开放。

结果仅包含本次非空白文本、实际方法及初始已确认 SourceApp；保留原始文本，不做翻译预处理。文本最多 65,536 UTF-16 code units。普通无结果/不支持/安全拒绝按下节处理；调用方取消在完整收尾后抛 OCE，清理失败优先以安全 IrisException 返回并保留责任。SelectionReadResult 的 ToString 不打印 Text/SourceApp，CCR-017 仅同步注释及该安全输出行为，不改字段、方法签名或默认配置。

## 2. 方法结果与 fallback

按 Order 每种方法最多尝试一次，始终围绕同一来源，不在后续方法中悄悄换成新前台窗口。

| 结果 | 组合策略 |
|---|---|
| UIA Success | 最终复核来源/取消后返回；下层已完整清理 |
| UIA Empty / Unsupported / 已干净完成的 Failed 或 TimedOut | 可以尝试下一方法，但 Clipboard 必须独立通过复制安全检查 |
| UIA Sensitive | 本次结束为 null，不通过另一方法绕过敏感控件拒绝 |
| UIA TooLong | 抛安全 InputTooLong，不静默截断或换方法绕过预算 |
| UIA TargetChanged | 本次结束为 null，不换目标继续 |
| UIA Busy / 任一下层未完成清理 | 安全异常/保留责任，不 fallback |
| Clipboard 安全拒绝、无新文本、普通不支持 | 来源仍稳定时可尝试 Order 后续方法；不伪造成功 |
| Clipboard 文本超限 | 安全 InputTooLong，仍先完成已产生的清理/条件恢复 |
| 任何取消、目标变化或清理失败 | 不发布迟到文本；清理失败不能被 null/取消覆盖 |

## 3. 焦点安全边界

ClipboardUnsafeProcesses 命中时绝不发送 Ctrl+C。除此之外，不能仅靠“不在黑名单”授权复制；必须取得 GetGUIThreadInfo 的有效 focused HWND，验证所属 PID、顶层/父子关联、可见/可用状态以及仍是原始来源/焦点。无输入队列、菜单/移动尺寸模式、无效/变化/无法读取的焦点均拒绝。

首期 clipboard 复制只正向许可系统原生文本控件的完整类名：Edit、RichEdit、RichEdit20A、RichEdit20W、RICHEDIT50W（OrdinalIgnoreCase）。不按前缀接受其它类，不因 Chrome/Code/Word 等进程名推断焦点是编辑器。未列出的自定义编辑器、浏览器渲染区和集成终端仍可走 UIA；没有安全证据时不注入按键。

读取 GWL_STYLE 检查 ES_PASSWORD，失败不视为“非密码”。GetWindowLongPtr 的 0 值与失败通过正确的 LastError 语义区分。Rich Edit 的多行密码同样拒绝。已知终端/控制台窗口或不能确认的宿主焦点拒绝；不得读取 Name/Value/全文来替代选区。

保存原始 focused HWND 与来源，等待修饰键结束、准备剪贴板之后、第一次输入之前及 C down 前均重新检查同一焦点和安全条件。不能在同一顶层窗口从编辑区切换到终端后仍沿用旧许可。安全探测都是窄、只读 native 查询，不新装全局 hook、不抢焦点。

以上针对正常 Windows 控件和可观察身份，是本产品的保守许可范围；不能证明任意应用自定义 Ctrl+C 行为，也不能消除检查与系统输入之间的所有竞争。Source HWND 不是 SendInput 的定向参数。

## 4. 键盘准备与复制顺序

必须先等用户释放修饰键，再准备/清空剪贴板，不能先 Clear 后调用现有 CopyAsync 去等 500ms。

允许给 KeyboardSimulator 增加内部 CopyWithPreparationAsync（或等价能力）：接收目标、一次性异步 beforeSend 准备回调和同步安全复核 guard。保留原 CopyAsync 的既有行为/结果；不要把 ClipboardSnapshotService 直接注入键盘模拟器。

1. 在已登记的唯一 copy 上下文中核对目标并按旧规则等待左右 Ctrl/Alt/Shift/Win/C 释放，最多 500ms。
2. 无共享锁调用并 await beforeSend。C2 在这里再次检查安全焦点，取得快照/版本并条件清空；不能完整准备时回调拒绝，零 SendInput。该回调及其实际 native 收尾属于 copy/组合上下文，不能放到无人持有的 Task。
3. 准备返回后重新检查取消/Stop、键状态、目标和原始安全焦点。新按键/焦点变化直接拒绝，不再次长等。准备拒绝可用既有 NotReady 表达，C2 上下文保存准确拒绝原因；不得作为已复制。
4. 进入既有单 INPUT 序列，C down 前再次调用安全 guard。目标/guard 错误仍补偿本次已按下键；只重试 KeyUp，不重新触发 Ctrl+C。

beforeSend/guard 和取消回调不得在共享锁内执行，不在 UI 调用 native；异常安全映射，不透传原始正文。保持 T-021 的 MayHaveCopied、pending release 和 Stop/Retry 责任语义。清理发生前不能向调用方返回成功文本。

## 5. 剪贴板准备、轮询与有界文本观察

Restore=true：CaptureSnapshot 必须完整成功，才能使用其 version 条件清空；UnsupportedFormat/TooLarge/Changed 不清空、不发键。Restore=false：只取稳定 version 再条件清空，不读取无关的原始 payload。所有路径仍使用 C1 的同 Open 帧条件提交，不无条件 Empty。

记录 Clear 后的 version，不能把自身清空造成的 sequence 变化当复制响应。Keyboard 未注入/NotReady/TargetChanged 时不盲目等一段时间读取旧内容；已有清空则按下节清理。Keyboard 失败或仍有自有按键责任，不返回选区文本；先完成释放/保留失败责任。

ClipboardTimeoutMs 是复制发送完成后的响应观察预算，使用 TimeProvider 单调时间。每 15ms 观察 sequence；变化后要求 20ms 的稳定窗口（仍计入同一预算），变化继续观察，不在持有 clipboard 时 await。0 和回绕只按相等性比较，不按增量大小推断来源。可以复用 IClipboardService.GetSequenceNumber 作后台轻量观察。

候选必须与原始来源匹配：在 C1 原生帧取得 version，要求 owner 非 0、owner PID 为已确认来源 PID，且当前前台/焦点仍有效；未知 owner、其它进程或无法确认的 broker 不冒认为本次复制。单靠 sequence 变化不读取未知来源 payload。

允许扩展 C1 一个内部“按 expectedVersion 条件读取 CF_UNICODETEXT”的原语：Open 后先比较完整 version，变化返回 Changed 且不 Get；读取完成后再次核对版本，变化不返回文本。结果携带与文本一致的 version，不能分开 GetText/ReadVersion 后拼接为同一观察。无格式/合法空白为无结果。

文本从借用 HGLOBAL 按 GlobalSize 有界读取：先校验非零/有效大小与 UTF-16 边界，最多复制 (65,536+1)*2 字节并在复制范围内找 NUL；允许更大分配块中较短且已终止的文本，不把分配器 padding 错当超长。上限内找不到终止符时区分实际超长与无效格式。绝不无界 PtrToStringUni，不释放借用句柄、不在 Close 后访问。已有 C1 清理/线程规则全部适用。

稳定和文本观察必须在响应预算内被接受；native 返回时已超预算不发布迟到文本。同步 GetClipboardData 的外部延迟呈现不具有本层硬终止保证，仍等待实际调用及清理，不遗弃 Task/另起线程。不能将 400ms 默认值宣称为包含冷启动、修饰键、快照和恢复的完整耗时上限。

## 6. 条件恢复与外部更新

本次保存原始快照时，必须保存每个可恢复阶段的版本责任。没有复制响应、发送前拒绝或取消时，仅当当前仍是自身 Clear 后版本才恢复；接受有效来源候选后，按该候选的 version 条件恢复。恢复前比较与 Empty/Set 由 C1 在同一 Open 帧执行。

版本改变返回 Changed：保留当前内容，不重试无条件覆盖。未知来源变化也保留当前内容；RestoreClipboard 是安全条件允许时的恢复，不是覆盖别人新复制的授权。恢复需要的快照借用在清理结束前保留，不能提前 Dispose/清零。

取消/Stop 后执行必要清理使用独立清理令牌，不因为请求令牌已取消就跳过恢复。任何 native/按键/恢复失败保留上下文、快照和未完成阶段；Retry 仅清理，不发布旧文本、不再次 Ctrl+C。恢复已明确因外部变化而跳过可视为清理责任结束，不能当成恢复成功写回。

sequence+owner/PID 无法证明一次变化与本次 Ctrl+C 的严格因果关系，尤其无法区分同一来源进程的并发复制。保证范围是按已观察的完整 version 防止随后的可观察更新被覆盖；不冒称通用跨进程事务隔离。该限制及自定义编辑器许可范围须进入最终手动验收说明。

## 7. 最终发布与 App 退出

组合调用完成前观察所有实际 UIA/keyboard/clipboard 工作与 cleanup Task；成功文本仅在最后来源检查之后、同一发布点复核调用方取消/Stop/操作身份，拒绝旧请求迟到结果。普通无结果不携带任何文本。

StopAsync 同步撤销组合准入/发布，后台取消并等待当前 operation（包含 beforeSend 和必要的条件恢复）；随后完成 KeyboardSimulator/UiaSelectionReader 的停止和残留清理，最后停止 ClipboardSnapshotService 并 Join 原生线程。不能先永久停止 C1 再发现还需要恢复。所有入口共享实际在途尝试，失败可重试但不重新开放。

AppBootstrapper 在热键/动作准入和录入关闭之后、Presenter/Host/Dispatcher 最终释放之前等待选区读取器 Stop。未使用过的组合器停止仍零 native I/O；DI 解析不启动资源。关闭失败保留根上下文供现有退出重试，不因 Host.Dispose 同步释放掩盖活跃线程/按键/子进程。

当前只接服务和生命周期，不启用 SelectionTranslateHandler、划词热键、鼠标监听或设置 UI。T-023/T-024 分别完成入口与鼠标功能。

## 8. 自动化验证

- 实际 composite + 窄外部边界验证 Order/快照/去重、来源、结果预算、fallback 表、无副作用构造、Busy、取消和失败后同实例重试；不复制组合状态机到 fake。
- 实际 KeyboardSimulator 扩展验证先等按键再调用准备、准备后新按键/焦点变化、C down 前拒绝/异常补偿、Stop 等待准备及资源回滚。自动化绝不发送真实输入。
- 实际 C1 条件文本读/恢复验证自清空不算响应、无新复制不返回旧文本、owner 不匹配零 Get、padding/无 NUL/超长、稳定窗口/超时、外部更新/0/回绕、提交/取消与失败重试。
- 实际组合根 AppBootstrapper 测试关闭顺序，先解除测试门闩再 await，确认全部自有 Task/线程/子进程结束；保留 body/cleanup 复合错误，不修改无关已验收模块为其让路。
- 原生 clipboard 只在既有独占非交互 helper 验证，创建失败按 error=183 等实际原因跳过。真实应用/Provider/终端兼容性、物理输入与性能留后续人工验收，不在自动化中读取用户窗口/剪贴板、注入键或访问用户配置/密钥/Run/HTTP。

完成后串行 Release build、全量 test。C1 已验收 native 基础与 B 的隔离宿主成果保留；本单若未改部署不重复发布/性能脚本。

## 9. 依据与已知限制

Microsoft Clipboard owner/sequence、GetGUIThreadInfo、编辑控件样式与密码消息在 2026-10-05 已实际 HTTP 200 查阅；缓存见系统临时目录 iris-t022c-*.txt 与 iris-t022c2-*.txt。关键来源：

- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclipboardsequencenumber
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclipboardowner
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getguithreadinfo
- https://learn.microsoft.com/en-us/windows/win32/controls/edit-control-styles
- https://learn.microsoft.com/en-us/windows/win32/controls/em-setpasswordchar
- https://learn.microsoft.com/en-us/windows/win32/controls/about-rich-edit-controls
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowlongptrw

CopyWithPreparation、正向许可控件集、版本接受/恢复策略、预算和停止顺序均为本项目设计，不冒称 Windows 对所有应用的普遍保证。最终实机清单仍包括记事本、Word、Chrome/Edge、VS Code、微信/QQ、PDF、Windows Terminal；终端不得通过本产品注入 Ctrl+C，未知/不支持必须真实报告，不能用 fake 测试宣称全部应用可用。
