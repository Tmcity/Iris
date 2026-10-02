# T-015 独立复审准备

> 2026-10-02，Codex；用户已手动转交 T-015，开发中。
> 本文是架构师的验收准备，不是新工单或规范变更，不指定尚未交付的内部类型，不代表 T-015 已验证。
> 基线 HEAD 为 e55695a；工作区已包含上一轮批准的 v1.25 / CCR-013 声明。准备期间只新增本文件及隔离临时探针，不改当前工单、业务源码、BACKLOG 或开发中的文件。

## 1. 已核对的衔接事实

1. `IUiDispatcher.InvokeAsync` 的契约是等待异步回调的完整 Task；现有 `WpfUiDispatcher` 在后台调用时使用 `DispatcherOperation<Task>.Task.Unwrap()`。取消已开始的回调不等于强制终止它。新 Presenter 必须在 UI 实际发布点检查身份，退出屏障必须等待真实返回任务。
2. `ITranslationOrchestrator` 的空输入/长度判定发生在快照预处理之后。`TranslationOrchestrator.cs` 使用 `_preprocessor.Prepare(request.Text, request.Origin, translationSettings)` 后检查 `preparedText.Length`。UI 若预检查，不能将一个不同的原文计数规则冒充 Core 既有规则；特别关注前后空白、零宽字符与 Selection 合并换行。此项是既有语义衔接，不要求改变 Core。
3. `ResolvedLanguagePair.Source` 仍可能为 Auto。`TargetLanguageResolver` 将 detector.Code 赋给 Source，并设 SourceWasDetected=true；这不保证识别到了具体语言。语言交换不能仅凭“收到 Started”或 SourceWasDetected 就启用；目标仍须保持具体语言/Auto 对应 null 的既有规则。
4. `TranslationFinished` 之前 Core 已清理 Provider 与 Registry 租约，但消费者尚需 EOF / DisposeAsync / UI 收尾。验收要把事件终态、枚举器释放和 App 工作引用释放分开。
5. `IClipboardService.SetTextAsync` 的提交点在 EmptyClipboard 成功之后。取消前置资格不能撤回已承诺的原生发布；fake 应区分进入方法、进入提交点、实际返回，不能只通过一个 canceled flag 证明写入被阻止。
6. `SettingsWindowSession` 的普通关闭会清理作用域；翻译窗口普通隐藏保留内容，不能机械复用其关闭即销毁的策略。可复用真实生命周期闸门/witness 方法，但所有权要按 TRANSLATION_WINDOW.md 判断。

现有接线参考为 `tests/Iris.App.Tests/Translation/TranslationOrchestratorIntegrationTests.cs` 中 `Orchestrator_EndToEnd_CacheAndSqliteHistoryIntegration_MatchesSpecifications`；它覆盖已有编排/缓存/历史，不是新窗口接线证据。`AppHostBuilder.Build` 是实际组合根，需要与自写 ServiceCollection 夹具区分。

## 2. 独立验收矩阵（全部待 T-015 交付后执行）

| 编号 | 对应已签发范围 | 可控交错与必须观察的证据 | 防止无效测试 |
|---|---|---|---|
| W01 | 操作准入/回收 | 持有失效但未 Dispose 的旧租约，达到容量后新准入拒绝；归还引用且取消回调退出后恢复 | 不能只数当前有效操作；取消回调挂起时仍占回收名额；已取消的新令牌不能废弃原有效操作 |
| W02 | 发布身份 | A 的 UI 发布排队，接纳 B 或 Dispose A 后再执行 A；返回 false、正文/可见性/卡片无 A 写入 | fake UI 只控制执行顺序，检查实际 Presenter；先证明回调确实被接纳进队列 |
| W03 | 租约移交 | TryShow 接受翻译后 caller Dispose，消费者继续；随后 caller token 取消仍传到工作；反向对照在 UI 接受前 Dispose | 不能只检查 Token 属性，观察实际消费者/副作用入口和状态；同 Id 的外来租约仍拒绝 |
| W04 | 前置迟到 | 旧取文本阶段挂起 → 新输入/隐藏 → 旧读取返回 → 旧 TryShow；不得重开窗口或覆盖/复制 | 仅用公开租约端口和 fake 前置读取，不提前实现 T-016/031；旧输入和新输入用不同哨兵 |
| W05 | 实际调用槽位 | 旧主请求取消后仍停在枚举释放，连续提交新主，只有最新等待项；放行前无第二主调用，放行后只启动最新项 | 不把旧 SessionId 消失当作工作退出；实际 Invoke/MoveNext/Dispose 计数分别记录 |
| W06 | 单卡重试 | A 终态、B 仍输出时重试 A；A 新 Started 不重建 B，主 Finished 不清除 A 的忙碌；同卡去重/异卡等待替换 | 给主 A 与重试 A 分配不同 RequestId 和正文哨兵；检查 B 对象身份及连续输出，不只最终文本 |
| W07 | 刷新/取消背压 | 阻塞真实输出刷新回调，灌入足量 Delta，再切会话/隐藏；缓冲封顶，旧回调退出后等待消费者可继续清理 | 单独记录待执行与正在执行的回调；取消 CTS 数量不能代替待刷正文预算；避免 UI 自己等待 UI |
| W08 | 权威终态 | Delta 与 Completed 用不同文本；Completed 后放行旧刷新；失败/取消后也放行旧 Delta | 必须出现旧刷新实际排队并被执行/拒绝的证据；只在无 Delta 时测试 Completed 没有辨识度 |
| W09 | 跨重试文本预算 | 多卡初始总量在限制内，逐卡重试增长至超过总限；检查所有保留正文/待刷/快照，截断卡显示与复制来源一致 | 单次请求上限测试不能证明跨重试总量；不能只测计算函数而忽略 VM/队列另存完整 string |
| W10 | 自动与手动复制 | cache hit 首个完整主结果；模型/本地截断；重试；写入前失效与已提交后失效；失败后不重复自动抢占 | fake 记录真正进入提交点及最终返回；手动复制旧结果是新动作，不应复用已取消的翻译令牌 |
| W11 | 创建/退出 | 从未打开就退出、Show 中途失败、隐藏 HWND、Close 失败重试、重复 Stop；卡住取消回调/复制/UI finally 后检查 scope 尚在 | 实际 Hwnd/Closed/scope dispose witness；不能把 ActiveSession=null 作为唯一退出证明 |
| W12 | 设置到卡片 | 真实 App 注册路径 + 真实配置协调/Registry/Orchestrator，隔离外部端口；设置保存顺序后检查 Started/卡片顺序与重试配置 | 记录解析到的实际对象类型和共享实例；fake Registry/直接返回成功的 fake Commit 不证明完整接线 |
| W13 | 布局及键盘 | 两主题、窄/默认宽度，长名称、错误、空状态、流式、截断；目标展开/滚动进入实际祖先视口；输入事件与命令路径 | TextBox.ActualWidth 或 Window.Width 不等于可见宽度；IME/真实多屏未执行就标未验证，不以代码赋值冒充键盘操作 |
| W14 | 定位/位置保存 | 负坐标、目标 DPI、边缘翻转/夹紧、小工作区、旧 LastBounds；保存期间更新服务字段，完成后两个字段都保留 | 几何输出独立预期，不用被测 helper 再算期望；只移动自有测试 HWND；实际保存串行任务纳入退出 |
| W15 | C19B-1 | 实际设置窗口列表操作逐个定位，高级项展开，准确找到底部状态，正确主题资源下检查标签完整提示 | 不复用“找到第一个 TextTrimming 控件”的旧选择方式；保留已通过的业务代码 |

W09 可使用多张卡各保留较短正文、再逐卡重试到单卡上限的序列；每次独立调用都在 Core 上限内，才能暴露 UI 跨重试累计问题。代理对边界需覆盖预算恰好落在高/低代理之间；预期只检查完整有效前缀，不人为要求补全超出预算的字符。

## 3. 已运行的框架/夹具准备探针

临时目录：`C:/Users/28910/AppData/Local/Temp/iris-t015-review-preparation`。

项目只使用 .NET / WPF 与从现有代码复制的 IUiDispatcher/WpfUiDispatcher，无仓库 ProjectReference，不编译共享 App 输出，不启动产品窗口或外部服务。自己的 STA Dispatcher 在线程结束前请求关闭并 Join。准备探针的同步线程启动/Join 只发生在独立控制线程，不作为生产 UI 异步实现模板。

源指纹（SHA256，复制文件与当时仓库源一致）：

```text
35BA81F49850D3383D16CC0824CFC097D71AA2DFF208019059C0353D11FF98D3 IUiDispatcher.cs
37FF130844A214468E55AE959689DF91A6A8D2D450DB4C51A25C7A2743914D5E WpfUiDispatcher.cs
62D16FA1A5CFE625DD5FD6F93E0A51696AE998AC4B641559F6AFFB45E867BF65 Program.cs
```

`dotnet --version` 实际输出：

```text
10.0.401
```

执行命令：

```powershell
dotnet run --project "$env:TEMP\iris-t015-review-preparation\Probe.csproj" -c Release
```

退出代码 0；原始输出：

```text
QUEUED_CANCELLATION canceled=True callbackRan=False expected=True/False
STARTED_CALLBACK ui=True incompleteAfterCancel=True exited=True expected=True/True/True
CANCEL_LIFETIME requestedWithWorkPending=True unregisterPending=True uiResponsive=True callbackOnUi=False callbackExited=True expected=True/True/True/False/True
ENUMERATOR_CLEANUP inject=False eofWithDisposePending=True disposed=True consumerExited=True caughtExpectedFailure=False
ENUMERATOR_CLEANUP inject=True eofWithDisposePending=True disposed=True consumerExited=True caughtExpectedFailure=True
PREPARATION_PASS productBehaviorVerified=False
STA_CLEANUP joined=True
```

这些诊断实际验证：

- 阻塞 Dispatcher 后接纳排队调用并取消，释放阻塞后该回调没有执行。
- 异步回调已开始后取消，在另一个 UI heartbeat 已完成时，原调用仍未退出；放行内部 await 后才完成。
- 在 UI 发起 CancelAsync 后，IsCancellationRequested 已为 true，但取消回调及 registration.DisposeAsync 仍等待；UI heartbeat 能执行；放行后观察所有任务完成。
- 自有 IAsyncEnumerator 返回 EOF 后停在 DisposeAsync，消费者尚未退出；finally 放行后完整退出；主动抛预期异常也能保留该异常并清理结束。

最后一项只验证将来可复用的枚举器闸门夹具，不是 T-015 的消费者验证。以上均不能证明尚未交付的租约、卡片、预算、定位或退出实现正确。不能把 PREPARATION_PASS 改写成 T-015 通过。

## 4. 收到交付后的使用顺序

先核对提交和完整 diff，再将报告中的测试名/证据映射到 W01–W15。按实际内部结构选择 fixture，不为匹配准备稿而要求开发改类名。必要的新独立探针必须调用实际交付实现；从上述夹具迁移门闩，不复制预期逻辑成为被测对象。

先走租约/调用屏障和结果归属，再走实际组合根与 UI。原始错误、未执行、环境不支持、夹具自身错误必须区分。所有测试失败也先放行闸门，再观察任务/关闭自有窗口，最后 Dispose；不得强杀后声称清理通过。

开发进行期间不重复跑共享解决方案构建/测试；收到交付并锁定基线后按工单执行。当前仍由用户手动传话，未直接联系或控制 Antigravity。暂无需要提前转交的新要求。
