# T-017 独立验收准备

> 2026-10-03，Codex；T-017 已直接下达 Gemini，当前开发中。
> 本文件只细化已批准 HOTKEY_SERVICE.md 的验收证据，不改变当前工单/契约，不要求新增功能；下述生产场景尚未执行。

## 1. 独立键码期望数据

本轮实际读取 Microsoft [Virtual-Key Codes](https://learn.microsoft.com/en-us/windows/win32/inputdev/virtual-key-codes)，从页面表格提取官方符号/十六进制 VK，再按正式白名单建立 WPF 风格键名到 VK 的对应。OEM 指向 VK，不声称不同物理键盘布局会输入相同字符。

数据和页面留存：`C:/Users/28910/AppData/Local/Temp/iris-t017-review-prep`。

- virtual-key-codes.html：本轮实际获取的页面。
- hotkey-expected-vectors.json：canonical 键名、官方符号/VK、大小写/空白变体，以及 modifier 输入校验的期望。
- VectorCheck.csproj / Program.cs：仅在隔离准备项目中用 WPF KeyInterop 交叉核对数据；不引用仓库项目、不调用新实现、不创建窗口、不注册热键、不发按键。Platform 生产仍禁止依赖 WPF。

生成命令的实际摘要：

```text
KEY_MAP canonical=96 officialMatches=96
ENABLED_VALIDATION cases=1536 valid=1412 invalid=124
```

输入校验矩阵由正式规则推导：每个 canonical 键名配 modifier 0..15；F12 拒绝注册；无修饰只允许 Snapshot/F13–F24；仅 Shift+字母拒绝。另列出未知键字符串与未知 modifier 位的反例。字段命名为 passesInputValidation，**不是 Windows 注册一定成功**，系统保留/冲突仍由原生注册结果判断。

JSON SHA256：

```text
5AE162867A50A563CBB06BDBD10560EFCD317DEDF1B699D96838CF194CE3E48D
```

`dotnet run --project <上述目录>/VectorCheck.csproj -c Release` 退出代码 0，原始输出：

```text
EXPECTED_DATA_CHECK keys=96 mismatches=0 productVerified=False
```

只证明期望数据与官方表/现有 WPF 名称相符，不是 KeyNameMapper 或 HotkeyService 的验收。收到交付后需实际调用实现与这些期望比较。

## 2. 服务层场景（待执行）

| 编号 | 输入/交错 | 应观察的实际证据 |
|---|---|---|
| H01 | 构造、无效键、禁用项、首次有效注册 | 构造零原生调用；无效输入不改旧注册；禁用项不校验 Key，但对已有项执行实际注销；首次有效注册才创建 HWND/绑定线程 |
| H02 | 全键名与修饰组合 | 实际 mapper/validation 对照独立数据；大小写/空白允许、数字字符串/未列别名拒绝；F12 可解析但不调用 RegisterHotKey |
| H03 | 注册/同 Action 重配 | native 注册带 MOD_NOREPEAT；新 ID、不复用；注销旧项失败时不叠加新项；没有仅覆盖字典的假成功 |
| H04 | 旧消息与伪匹配消息 | 已注册有效 ID 的匹配 VK/modifiers 触发一次；未知/旧 ID、错误字段、停止后消息不触发；不得把 NOREPEAT 当作实际按下的修饰键 |
| H05 | ID 耗尽 | 保持已有资源/分发状态，不回绕复用、不清空消息队列；耗尽测试隔离配额，不能污染其它测试。有限测试分配器需与真实默认范围对照 |
| H06 | 多项注销一项失败 | 尽力注销其它项，失败项仍有资源/重试入口但无旧分发资格；再次注销成功后才释放，不把 Clear 字典当原生完成 |
| H07 | 初始化/销毁失败 | WndProc/实例关联和窗口类在实际原生退出前存活；部分创建失败、DestroyWindow/注销类失败不遗忘所有者；正常释放幂等 |
| H08 | 所有者线程 | UI/所属线程注册、消息/Pressed、注销和销毁；其它线程调用明确失败且无原生副作用；不以 Task.Run 隐式换线程 |

触发消息用实际实现的窄原生接缝，不自写一份 ID→Action 字典充当被测对象。WndProc 失败不得跨托管边界逃逸；要验证实际分支，不能只测外部包装器的 catch。

## 3. App 层场景（待执行）

| 编号 | 输入/交错 | 应观察的实际证据 |
|---|---|---|
| H09 | 默认设置与未交付动作 | 实际 Handler 集合过滤生效；InputTranslate 按默认绑定注册，Clipboard 默认禁用；未来截图/划词绑定保留 settings 但没有原生注册 |
| H10 | 无关 settings 更新 | 深拷贝后的 Hotkeys 内容相同仍不重注册，不能仅依赖引用相等；主题/Provider 变更不消耗 ID |
| H11 | 两动作交换组合键 | 所有受影响旧绑定先撤销，再按稳定顺序注册新项；不因暂时占用旧键产生错误冲突；未变化项保持 |
| H12 | 后台 Changed 连续到达 | 只有一个当前应用及一个最新待快照；旧应用不覆盖新结果；正确处理部分注册/注销失败，提示反映实际状态 |
| H13 | Pressed→Dispatcher | 使用实际协调器/Dispatcher/Handler，Trigger=Hotkey；每 Action 待处理有界；WndProc 不等待完整业务任务，MOD_NOREPEAT 与异步防重入分别证明 |
| H14 | 退出中排队消息/设置 | 先拒绝新准入/注销 native，再完成窗口和已接纳工作；队列中旧消息不会重开窗口，设置更新不会重新注册 |
| H15 | 挂起前置读取后退出 | 不合作 fake clipboard 已 entered，退出先取消并停 native，仍持有 caller/资源；放行后真实 Handler/Presenter/协调器全部退出 |
| H16 | 退出失败与再次退出 | native 清理失败保留所有者，后续可重试；并发 Stop 共用实际屏障，不在 Dispatcher 停止后再请求 UI 销毁 |

AppHostBuilder 必须用实际注册路径；不能把手写服务集合的集成测试单独称为组合根证明。当前 Program.Main 为 STA 只是主程序事实，普通容器解析、测试解析和后台 settings Changed 仍需覆盖。

## 4. 证据及测试收尾

核对提交与全部 diff 后再运行共享解决方案构建/测试，避免与 Gemini 正在编译的输出冲突。每个失败闸门应先证明 entered，再做负向状态检查；finally 放行并 await 完整工作、销毁自有窗口、释放 provider。不要靠短 Delay、Task 名称或日志注释证明实际生命周期。

已有消息窗口可行性探针见 T-017_WORK_ORDER_CANDIDATE.md §8；它没有注册任何键。本轮同样没有 RegisterHotKey/键盘输入/用户设置操作。模拟 WM_HOTKEY、成功调用 RegisterHotKey、实际物理按键触发是不同证据，不能混写。

T-017 当前仍由 Gemini 执行；本稿与期望向量不代表已通过任何生产热键验收。

## 5. 开发期间的直接协作补充

已把本稿和临时键码数据位置发送到 Antigravity 原对话，Gemini 已读取。后续只读查看初版 HotkeyService 时发现 `_nextId` 为实例字段，以及 CreateMessageWindow 失败后的 UnregisterClass 返回值被忽略；这分别对应本稿 H03/H05 与 H07 的既有要求。

已将这两点作为 T-017 自检补充排入开发对话队列，明确不新增任务。它们是开发中观察，不是最终验收结论；交付时重新检查实际实现。应先确认队列补充已被处理、Gemini 停止修改，再锁定最终提交做独立测试，避免对中间提交下结论。

## 6. KeyNameMapper 开发快照对照

2026-10-03 在不占用 Gemini 共享构建输出的前提下，复制当前 KeyNameMapper.cs 和既有 HotkeyBinding.cs 到系统临时目录 `iris-t017-mapper-snapshot`；复制前、复制后与目标副本的 SHA256 一致，避免读取半写入文件。该独立 net10.0 项目没有仓库 ProjectReference，也不运行原生注册代码。

当时源指纹：

```text
486BC73D902F96A7A0F014687056E7FBDAFBECC96D9AFA3D433901DC71139AAE KeyNameMapper.cs
622AB72193A6008D100D0A6F7096BCC56A82A91A32CD68D41D113A5DA5C00E36 HotkeyBinding.cs
```

对照本稿 §1 的独立官方键码期望与输入规则数据，执行 `dotnet run --project C:/Users/28910/AppData/Local/Temp/iris-t017-mapper-snapshot/Probe.csproj -c Release`，退出代码 0，原始输出：

```text
MAPPER_SNAPSHOT mappings=192 validations=1536 invalidCases=16 mismatches=0
SCOPE sourceSnapshotOnly=True nativeRegistrationTested=False finalDeliveryVerified=False
```

此结果只证明这份源快照的键名/输入校验与期望一致。最终交付应先核对指纹，源码变化时按新实现复测；即使源码一致，也不能据此免除 HotkeyService、App 接线、线程或原生资源生命周期验收。

## 7. 当前实现追加自检（交付前，尚未执行故障探针）

本节来自 2026-10-03 开发中源码只读检查，不是最终交付评审；仍属正式规范 §1/§5/§6 与 H07/H12–H16 的范围。

1. NativeHotkeyAdapter.UnregisterClass 在 finally 无条件从 _activeDelegates 移除 WNDPROC，即使原生注销失败也释放该引用。应仅在成功注销后释放；与创建窗口失败后的类归属保留一起覆盖。服务内部 NativeWndProc 与包装后的 WNDPROC 不是同一个委托，不能用前者仍存活代替后者生命周期证据。
2. HotkeyCoordinator.OnHotkeyPressed 直接调用 DispatchActionAsync，随后才 TrackDispatchTask。应证明业务不会内联运行在 WndProc 上，并在业务开始前纳入退出跟踪。需要明确排队语义；若 IUiDispatcher.InvokeAsync 在 CheckAccess=true 时内联执行，单纯换成该调用仍不足以保证消息回调快速返回。
3. WaitForInFlightDispatchesAsync 的 3 秒 WaitAsync 超时被吞掉后正常返回，无法证明已接纳分发全部退出。正常退出要求保留屏障直至真实完成；超时如需诊断不可被当作成功关闭 Host/Dispatcher 的资格。
4. ApplySettingsCore 注销失败仅日志记录，未进入失败通知；_activeBindings 仍保存旧绑定但服务已撤销其分发资格。设置恢复旧组合时，不能仅凭语义相同认定该旧注册有效；应保留清理失败状态、重试并如实提示。
5. FormatKeyCombo 直接回显 binding.Key.Trim()，无效键可能是任意文本，不符合安全规范化组合要求。失败通知应使用受限规范化键名或固定无效占位文案；ErrorCode 对应安全提示，避免原始无效输入泄入提示/日志。

交付报告需列实际测试名和输出，不能将上述静态路径检查描述为已复现或已修复。

## 8. 开发版程序集独立故障探针

2026-10-03 复制当前已构建的 Platform/Core DLL 到 `C:/Users/28910/AppData/Local/Temp/iris-t017-service-review/lib`；逐个核对源复制前/后与副本 SHA256 相同。该目录的 Probe.csproj 直接引用副本，无仓库 ProjectReference，无共享输出，使用 fake INativeHotkeyAdapter，真实原生调用为零。

```text
Iris.Core.dll SHA256=37442A100E025E738A6FBADE18823470521A692867B8535826FADA3321267877
Iris.Platform.Windows.dll SHA256=8E421E90BC8628461E2A1CF0B379097995826406E97A409F861AA9DCA9F74BE0
```

执行 `dotnet run --project C:/Users/28910/AppData/Local/Temp/iris-t017-service-review/Probe.csproj -c Release --property:RestoreSources=C:/Users/28910/AppData/Local/Temp/iris-t017-service-review/lib`，进程退出代码 0（仅表示探针正常运行，以下故障断言不符合预期）：

```text
PROCESS_ID_UNIQUENESS firstSuccess=True secondSuccess=True firstId=1 secondId=1 unique=False expectedUnique=True
CREATE_ROLLBACK registerSuccess=False liveClasses=1 unregisterAttempts=1
CREATE_ROLLBACK_RETRY liveClasses=1 unregisterAttempts=1 expectedLiveClasses=0 expectedAttempts=2
SCOPE copiedDevelopmentBinary=True finalDelivery=False realNativeCalls=0
```

这将 §5 的两条静态观察提升为该开发二进制上的复现证据；不是最终交付结论。修正交付后更新隔离副本并复测，不把旧 DLL 的失败套用到新代码。

## 9. 开发版 App 分发/屏障独立探针

隔离目录 `C:/Users/28910/AppData/Local/Temp/iris-t017-app-review` 复制当前已构建的 App/Core DLL，复制前后指纹一致。使用真实 HotkeyCoordinator、ActionDispatcher、WpfUiDispatcher 和 STA Dispatcher，fake IHotkeyService 触发事件，GatedHandler 不响应取消直到 finally 中放行。不创建窗口，不访问用户配置/剪贴板，不注册真实热键。

```text
Iris.Core.dll SHA256=37442A100E025E738A6FBADE18823470521A692867B8535826FADA3321267877
Iris.App.dll SHA256=864A0E84F72AC2BC71E04A2D74D513AFCD6E60E05708BE2DCDCD6DAC8F248E4A
```

首次执行到超时日志路径时缺失副本依赖 Microsoft.Extensions.Telemetry.Abstractions，退出代码 1，不算完整验证。补入当前 App 输出中的对应 DLL 引用后，运行 `dotnet run --project C:/Users/28910/AppData/Local/Temp/iris-t017-app-review/Probe.csproj -c Release --no-restore`，退出代码 0，真实输出：

```text
PRESSED_INLINE handlerEntered=True insidePressed=True expectedInsidePressed=False
EXIT_BARRIER returnedBeforeRelease=True handlerFinished=False nativeStopped=True expectedReturnedBeforeRelease=False
CLEANUP handlerFinished=True expected=True
SCOPE copiedDevelopmentBinary=True realCoordinatorDispatcherWpfAdapter=True windowsCreated=0 realHotkeys=0 finalDelivery=False
```

证明该开发二进制确实在 Pressed 调用栈内进入 Handler；退出等待在 Handler 尚未完成时返回，finally 放行后 Handler 完成。此处使用受控 Handler，不声称真实翻译 Presenter 的退出屏障也提前释放；该组合仍需最终交付验证。探针需在新版本排队语义下调整为先泵 UI 至 Handler entered 后再验证屏障，不能将未开始工作当作已完成工作。

## 10. 部分初始化修正后的重入检查

开发中新实现已在 CreateMessageWindow 之前保存 className/atom/ownerThread；但当前 Register 仍仅用 `_hwnd == 0` 判断是否初始化，再次 Register 会无条件 RegisterClass 并覆盖上一份类归属，而且不先检查已绑定线程。应覆盖“创建失败 + 回滚注销失败 → 再次 Register（同线程/跨线程）→ Dispose”，以及“DestroyWindow 成功 + UnregisterClass 失败 → 再次 Register”。残留窗口类必须先由原线程完成清理或明确拒绝新注册，不能被新类覆盖。此项是原 H07/H08 的后续路径，不新增需求。

NativeMessageWindowTests 当前测试名包含 Invisible，注释含 Verify window is not visible，但只断言 Create/Destroy/Unregister，messageReceived 未被断言，也未查询可见性。交付应如实限定为窗口创建/销毁验证；若报告声称不可见、消息投递或线程验证，需增加实际对应检查并确保 Create 失败后也能在 finally 清理已注册类。

## 11. 第一轮修正后的开发二进制复测

沿用 §8 的隔离目录与 fake-native 探针，更新副本并核对复制前/后指纹相同。此次 Platform DLL SHA256 为 `5EA5B8097B6B45809D7459407C81A8332F2A828CEF83997F733EC624FF8BD2A3`，Core 指纹仍为 §8 所列值。增加“失败后再次 Register”场景，运行同一 Probe.csproj（Release / --no-restore），退出代码 0，输出：

```text
PROCESS_ID_UNIQUENESS firstSuccess=True secondSuccess=True firstId=1 secondId=2 unique=True expectedUnique=True
CREATE_ROLLBACK registerSuccess=False liveClasses=1 unregisterAttempts=1
CREATE_ROLLBACK_RETRY liveClasses=0 unregisterAttempts=2 expectedLiveClasses=0 expectedAttempts=2
REGISTER_AFTER_PARTIAL_FAILURE retrySuccess=True liveClassesAfterDispose=1 expectedLiveClasses=0
SCOPE copiedDevelopmentBinary=True finalDelivery=False realNativeCalls=0
```

两个已知原反例已改善：跨实例 ID 不同；失败后直接 Dispose 可重试成功。§10 的连续注册路径在该二进制上仍复现残留类记录，不能据前两项通过签收。消息已经排入 Gemini 原对话，待其处理后再锁定最终交付复审。

## 12. App 排队与退出屏障修正复测

沿用 §9 隔离目录，更新经过复制前/后指纹核对的程序集：App SHA256 `0904C3989D84BB46F42A1B5A35043D22FD1230DBA2E42F6AE1DFB4C680C90BED`，Core 同 §8，Platform 同 §11。WpfUiDispatcher 的 resolver 显式捕获最初的 owner Dispatcher；触发后泵该 Dispatcher 到 Handler 确认 entered，再停止准入、等待退出，以免将尚未启动的任务误认为已完成。

`dotnet run --project C:/Users/28910/AppData/Local/Temp/iris-t017-app-review/Probe.csproj -c Release --no-restore` 退出代码 0：

```text
PRESSED_INLINE handlerEntered=False insidePressed=False expectedInsidePressed=False
QUEUED_ENTRY handlerEntered=True insidePressed=False expected=True/False
EXIT_BARRIER returnedBeforeRelease=False handlerFinished=False nativeStopped=True expectedReturnedBeforeRelease=False
CLEANUP handlerFinished=True expected=True
SCOPE copiedDevelopmentBinary=True realCoordinatorDispatcherWpfAdapter=True windowsCreated=0 realHotkeys=0 finalDelivery=False
```

实际证明此次开发二进制的 Handler 已离开 Pressed 栈后进入，并在原 3 秒超时阈值之后仍保留退出等待；finally 放行后完成。只覆盖该受控路径，未替代最终提交的全量回归、真实前置读取或部分初始化连续重试验证。

## 13. 连续注册残留修正复测

再次更新 §8 隔离目录的 Platform 副本，复制前/后/副本指纹一致：`7811654BEA4EF6D8F0B83D75E37206C355E8B75B5165264E3EB4208B64594FEB`。执行原服务探针（Release / --no-restore），退出代码 0：

```text
PROCESS_ID_UNIQUENESS firstSuccess=True secondSuccess=True firstId=1 secondId=2 unique=True expectedUnique=True
CREATE_ROLLBACK registerSuccess=False liveClasses=1 unregisterAttempts=1
CREATE_ROLLBACK_RETRY liveClasses=0 unregisterAttempts=2 expectedLiveClasses=0 expectedAttempts=2
REGISTER_AFTER_PARTIAL_FAILURE retrySuccess=True liveClassesAfterDispose=0 expectedLiveClasses=0
SCOPE copiedDevelopmentBinary=True finalDelivery=False realNativeCalls=0
```

此前 §11 的连续注册残留反例在此开发版已通过；原两项反例仍通过。最终交付的提交指纹、全量回归及跨线程拒绝等完整证据仍需复审，不能仅凭三个探针场景签收整个工单。

## 14. T-017-R1 基线调度失败独立复现

e936216 提交后，独立 Release 构建及全量测试已完成（build 0 警告/0 错误，00:00:06.71；test 总计 1602、成功 1601、失败 0、跳过 1，13s 241ms）。R1 签发后，在不与 Gemini 共享输出的隔离 App 探针目录更新已构建副本，源复制前/后/副本指纹一致：

```text
Iris.Core.dll SHA256=7F9358141BB323133A272284F077659D5EFC36CBFF8358A5BE7139997FDFA890
Iris.App.dll SHA256=8593499C98DE07A7F0D01B17360F6BCAFA8D69D73613D2E363030FA21B749FB1
Iris.Platform.Windows.dll SHA256=A9141C4682F5D8A1BFF0B371C4FD26ED35B088DA95321881CD14E06A5D015B7B
```

增加受控 IUiDispatcher 单次返回 faulted Task 的场景：启动默认 F 成功；下一次发布 B 的调度失败；再发布 C 时调度器已恢复，但实际协调器未再次调用它，仍保持 F。沿用真实 HotkeyCoordinator，不另写参考状态机；fake settings/原生服务无用户数据或系统热键副作用。

执行 `dotnet run --project C:/Users/28910/AppData/Local/Temp/iris-t017-app-review/Probe.csproj -c Release --no-restore`，退出代码 0（打印期望差异，非全部断言成功）：

```text
SCHEDULING_RECOVERY lastRegisteredKey=F uiCalls=2 expectedLastKey=C
PRESSED_INLINE handlerEntered=False insidePressed=False expectedInsidePressed=False
QUEUED_ENTRY handlerEntered=True insidePressed=False expected=True/False
EXIT_BARRIER returnedBeforeRelease=False handlerFinished=False nativeStopped=True expectedReturnedBeforeRelease=False
CLEANUP handlerFinished=True expected=True
SCOPE copiedDevelopmentBinary=True realCoordinatorDispatcherWpfAdapter=True windowsCreated=0 realHotkeys=0 finalDelivery=False
```

首行是 R1 调度失败恢复路径的实际反例；后续行是原分发/等待路径的保持验证。探针末行的 development/finalDelivery 是脚本保留的范围标签，此轮 DLL 明确来自 e936216 基线独立构建，不能误称已经验证 R1 交付。消费者空闲交接竞态仍为工单所述静态交错，未将此单次调度故障冒充竞态复现。

## 15. R1 开发中调度完成回调检查

首次 R1 草稿把空闲判断和处理标记清除放进同一锁，方向符合要求；调度故障通过线程池 ContinueWith 清理时仍须核对三个交错（未做该新草稿的运行时复现）：

- 回调执行前调度失败，但完成回调尚未运行；新的 Changed 已到达并保存在 pending，因 processing=true 未调度。完成回调只置 false 会让这份新快照再次失去消费者。要区分失败请求与后来到达的新请求，不能靠“用户再改一次”恢复。
- ProcessPendingSettingsAsync 自己 catch 先清标记、再让 Task fault；新消费者已启动，旧 Task 的延迟完成回调无条件清 false 会错误释放新消费者的处理资格。完成回调必须只修改自己拥有的调度代次。
- InvokeAsync 可能以 canceled Task 完成；仅检查 IsFaulted 无法释放未执行回调的调度资格。停止后不重试，正常可恢复场景仍应有明确收尾。

以上均属于 R1-1 的原调度失败收尾要求。验证需操控旧任务完成与后续快照到达的顺序，保持一个消费者/一个最新快照，不通过循环自动重试同一失败快照解决。

### 基线上的延迟故障/取消探针

继续使用 §14 的同一基线 DLL 副本，增加可控制完成时间的 IUiDispatcher：先阻止 B 的调度回调执行，再发布 C，之后将 B 的调度 Task 分别设为 faulted / canceled；两种情况都在受控观察期内检查 C 是否真正进入 Register。代码在 finally 完成测试 Task 并停止协调器，fake 原生服务不创建任何系统资源。

同一隔离 Probe.csproj 的新增实际输出：

```text
DEFERRED_SCHEDULING cancel=False newestApplied=False lastRegisteredKey=F uiCalls=2 expectedNewestApplied=True
DEFERRED_SCHEDULING cancel=True newestApplied=False lastRegisteredKey=F uiCalls=2 expectedNewestApplied=True
```

这是 e936216 基线上的补充反例，不是 R1 草稿的测试结果。R1 交付后更新副本再验证；先发布新快照、再完成旧任务的顺序由探针直接控制，2 秒观察期只用于判断后续处理是否发生，不用于制造交错。

## 16. R1 调度代次开发版探针对照

更新独立副本并校验复制前/后指纹：App 为 `F10AB78C48E2EB9CCC2ABB349EB7B4F4D4441C4353AB0A06F7147B08792A266D`；Core/Platform 同 §14。延迟故障/取消两项首次复跑均已应用 C。原单次故障用例立即打印仍显示 F，这是同步观察早于异步恢复，不可直接归为新版本失败；补入与延迟用例一致的有界完成观察后，执行同一 Probe.csproj（Release / --no-restore），退出代码 0：

```text
SCHEDULING_RECOVERY recovered=True lastRegisteredKey=C uiCalls=3 expectedLastKey=C
DEFERRED_SCHEDULING cancel=False newestApplied=True lastRegisteredKey=C uiCalls=3 expectedNewestApplied=True
DEFERRED_SCHEDULING cancel=True newestApplied=True lastRegisteredKey=C uiCalls=3 expectedNewestApplied=True
PRESSED_INLINE handlerEntered=False insidePressed=False expectedInsidePressed=False
QUEUED_ENTRY handlerEntered=True insidePressed=False expected=True/False
EXIT_BARRIER returnedBeforeRelease=False handlerFinished=False nativeStopped=True expectedReturnedBeforeRelease=False
CLEANUP handlerFinished=True expected=True
SCOPE copiedDevelopmentBinary=True realCoordinatorDispatcherWpfAdapter=True windowsCreated=0 realHotkeys=0 finalDelivery=False
```

这些场景已改善，仍非 R1 最终验收。旧完成回调与新消费者代次交错、实际前置读取/Bootstrapper 退出及完整回归仍需交付证据。
