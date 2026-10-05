# 鼠标来源约束与选区读取协调

> 2026-10-05，Codex；T-024B1 / ARCH v1.37 / CCR-021 正式规范。T-024A / ac4ff58 已独立验收。
> B1 交付原来源约束、App 读协调与退出；B2 再接被动图标、缓存点击、自动翻译及鼠标设置。当前不安装真实鼠标监听或开放图标入口。

## 1. 目标与分层

未来 ShowIcon 模式的预读不应分配 Presenter 操作并取消正在进行的翻译，也不应令显式热键因后台预读而直接遇到 BUSY。继续只使用一个实际 CompositeSelectionReader 及其已验收剪贴板/键盘/UIA 事务；不创建第二个读实例，不平行访问剪贴板。

App 新增 internal SelectionReadCoordinator，实现 ISelectionTextReader，公开端口 ReadAsync 表示现有显式读取；仅 App 内部增加 ReadPassiveAsync(options, token) 供 B2 的被动预读。它只协调请求/取消/持有，不复制 C2 的平台事务或清理状态机，不分配翻译 lease、不弹通知、不读取设置/创建窗口。

AppHostBuilder 将 ISelectionTextReader 接到同一单例 Coordinator，后者依赖同一单例 Composite；仅使用 Platform 注册时仍可直接解析 Composite。现有 Handler 不改业务行为。构造和 DI 解析都无 I/O。B1 不创建实际 passive 触发入口，测试直接通过内部方法验证已签发底座。

## 2. CCR-021：固定可选原来源

SelectionReadOptions 原四参数构造/解构不变，增加可空 init ExpectedSource: ForegroundAppInfo，默认 null 保持当前前台读取语义；ToString 返回固定类型名，避免新字段泄露窗口标题/列表。ReadAsync、SelectionReadResult、持久化设置/default/schema/包不变。

非 null ExpectedSource 必须有非零 WindowHandle、正 ProcessId、非空白实际 ProcessName（无路径分隔符、冒号或 NUL）；无效值在接纳新工作前抛安全 InvalidArgument。不 Trim/改写名字；身份只比较 HWND、PID 和 OrdinalIgnoreCase 的 ProcessName，标题不参与，捕获时丢弃标题。

Composite 在首次等待前冻结 options 和 ExpectedSource。已有 pending cleanup 仍按原责任先处理；之后捕获本次实际前台，仅在其与 ExpectedSource 匹配且通过原来源确认时开始新 UIA/Clipboard 工作。失配为 null，零新宿主/UIA/键盘/快照/清空操作；不能把后来的另一个前台当本次鼠标来源。后续继续沿用 op.Source 的完整 guard，不降级换目标。ExpectedSource=null 的已验收行为保持。

“已有清理”与“新读取”必须区分：目标变化不能阻止必要的旧快照条件恢复，也不能把清理失败吞成普通 null。新请求若尚无旧责任，失配应只有来源确认查询而无其它平台副作用。该扩展不提供 OS 进程/HWND 永不复用或严格按键因果证明。

## 3. 同步捕获与准入

Coordinator 两个读取入口在返回 Task/第一次等待前检查参数、caller 取消并独立复制 Order/ClipboardUnsafeProcesses/ExpectedSource。可把现有 C2 Freeze 提为内部共享纯 helper，保持原合法值/空 Order/null/枚举/范围规则，避免两套校验漂移；helper 不能启动 native 工作。非法或预取消的显式请求不得干扰当前被动请求。

最多持有一个实际内层 Read 和一个已保留的显式待办，无无界 Task/等待队列：

| 当前状态 | 新 passive | 新显式 ReadAsync |
|---|---|---|
| 空闲 | 接纳并执行 | 接纳并执行 |
| 有被动实际读取，尚无显式待办 | 返回 null，不排队 | 原子保留一个待办，撤销被动请求，等其实际退出后执行 |
| 有显式实际读取或已有显式待办 | 返回 null，不排队 | 安全 CaptureFailed/BUSY，保留已接纳请求 |
| 正在停止 | 静默无结果或取消，不启动工作 | 取消/停止错误，不重新接纳 |
| 已 Dispose | ObjectDisposedException | ObjectDisposedException |

被动抢占只取消 Coordinator 自己为旧请求持有的令牌，不调用全局 Composite.StopAsync（否则会永久关闭服务），也不停止全局按键/UIA/剪贴板服务。旧被动调用不发布迟到文本，按取消结束；B2 会静默处理。不能新开一个 Composite 来绕过 BUSY。

内层任务结束包括 C2 的实际收尾。显式待办在等待期间取消或 Stop 时也要等自己触发取消的旧请求及取消回调结束，再释放保留槽位/令牌责任；不得 WaitAsync 提前返回后遗忘仍运行的被动工作。真正开始显式内层 Read 前重新检查自己的 caller、停止状态与待办身份。

## 4. 完成、错误与取消

每个已接纳请求持有独立的取消源、注册、实际任务与可观察完成。State 锁里只接纳/撤销/登记，不调用 native、用户取消回调或 await。Coordinator 发起的抢占/Stop 取消在后台锁外执行，完整等待 CancelAsync/自身发起的取消工作；外部 caller 的已取消状态不被重新包装成普通空结果。

显式的有效结果/普通 null/平台安全异常按原端口返回；没有本层匿名通知或窗口兜底。已取消/被替代的请求不能因内层不合作而返回迟到文本，但仍等内层真正结束。内层错误不可静默伪装成功；原有 C2 清理失败与同实例重试保持。被动错误安全交还内部调用方，不自动重读、不再次发送复制。

取消源/注册不能在实际内层或本层取消任务尚使用时释放，成功完成槽位要等本层 finally。失败后可继续下一次读取，前提是底层按自身规则完成 pending cleanup；不猜测或清空 C2 的 pending 状态，不根据异常字符串伪造“已恢复”。

## 5. App 退出与所有权

Coordinator 内部提供同步 StopAcceptance 和可等待 StopAsync/DisposeAsync；关闭接纳后取消、排空已接纳 actual/pending、等待注册与取消任务结束。并发 Stop 共享当前等待；失败责任保持可重试，Stop 后不再重启读取（新的应用实例才创建新 Coordinator）。同步 Dispose 只允许从未使用或完整停止，不能阻塞 UI 或遗忘工作。

Coordinator 拥有自己的调度/取消资源，不拥有 Composite 的 DI 生命周期。Stop 排空本层，即便某个读任务以平台错误结束也不能据此声称 native cleanup 完成；该读调用方仍收到错误，之后 Composite.StopAsync 再按既有规则完成/报告底层 pending 清理。

AppBootstrapper.StopAsync 先同步拒绝 Dispatcher 和 Coordinator 新准入；在既有 Composite.StopAsync 前 await Coordinator.StopAsync，再保持原设置/翻译 Presenter、动作等待、Host/Dispatcher 退出顺序。不要先停止 Composite 再让 Coordinator 的待办尝试新 Read。B2 将把 Controller/monitor 停止放在相关阶段之前，本单不提前实现它们。

## 6. 必须提供的证据

1. 实际 Composite：ExpectedSource 的 HWND/PID/名字分别失配零新平台操作；忽略标题和名字大小写；原四字段/解构/default 路径保持；调用后改原列表/来源变量不改变捕获；有效源通过 UIA/Clipboard 原链；等待期间源改变仍拒绝；已有 pending cleanup 不被失配跳过。
2. 实际 Coordinator：两个入口同步捕获；非法/预取消显式不打断被动；busy passive 无排队/新 I/O；显式取消被动后直到原真实 task/清理放行才启动新读；上限一个实际加一个显式待办；第二显式/新被动不会替换已接纳意图。
3. 待办/caller/Stop 的各取消时点、迟到成功/错误、取消回调门闩、内层清理失败与同实例恢复；任务/取消源/注册全部实际结束后才完成；Stop 后零新开始，构造/未使用 Dispose 零 I/O。
4. 关键抢占/停止/失败用例必须把 Coordinator 接到实际 Composite + 实际 Keyboard/UIA/C1，仅替换 OS/process/time 外部边界，证明不是一个 fake Reader 瞬间取消就宣称平台已清理。可在 App 测试中链接既有外部 fake 源文件，不引入生产对测试依赖或复制产品状态机。
5. 实际 App DI 的同实例关系、Dispatcher→既有 SelectionTranslateHandler→新端口→原 Composite 接线，以及 AppBootstrapper 退出顺序；已有替身/类型断言按新组合真实同步，保留范围偏差记录。

所有门闩先确认 entered，从资源取得起 finally 先放行再等真实 tasks/threads；主异常和清理异常同时保留。串行 Release build / 全量 test。保留既有 T-022A 两项只读光标/显示器 smoke，报告明确区分；新增测试不触真实用户桌面/选区/剪贴板/输入/hook、配置/密钥/Run、HTTP/性能脚本，不启动真实 Iris。真实第三方兼容性/NFR 仍未验证。
