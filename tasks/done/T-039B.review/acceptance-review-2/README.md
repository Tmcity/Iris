# T-039B 架构师独立验收（第 2 轮：R2 复核）

2026-10-17 · 架构师 · 目标提交 `4d19a46cccfa1041a3838011abf4330ee035c89c`（父 `fe3f788`）。第 1 轮见 `../acceptance-review-1/README.md`。

## 1. 结论

R2 提出的 10 项中，**9 项经独立复验通过**；但**第 2 条的修正方式在生产 DI 拓扑下不成立**（其新增测试因手搭单例注册而无法暴露），且 R2 改动了产品源码后，T-039B 的包/来源绑定已过期。**T-039B 仍不结案**，已签发 `T-039B-R3`。

## 2. 提交与范围核对（实测）

- `4d19a46` 父提交正确、单一提交、reflog 无 amend；工作树仅 `.codex/` 未跟踪；`tasks/current_task.md` 已移除。
- 改动 26 个文件；除 `src/Iris.Platform.Windows/Maintenance/MaintenanceWorkDirectory.cs`（**不在 R2 允许清单内**，未在报告“偏差”中披露）外均在允许范围内。
- `tasks/BACKLOG.md` 的差异仅为架构师第 1 轮追加的段落，开发未改 BACKLOG。
- 被删除的测试断言行均已等价迁移或加强：`UpdateHandoffTests` 由 `OperationCanceledException` 改为 `IrisException(ErrorCode.Unknown)` 并新增 `CallerCancellationThrowsOperationCanceledException` 覆盖调用方取消仍抛 OCE；Anthropic 三条断言原样保留并移入显式设置环境变量的 try 块；维护测试的 `Signal` 改为带进程存活探测的循环等待。

## 3. 架构师独立构建与全量（实测）

- `dotnet build Iris.slnx -c Release --no-incremental`：**0 警告 0 错误**，`Time Elapsed 00:00:25.85`（`architect-r2-release-build.log`）。
- `dotnet test Iris.slnx -c Release --no-build`：**总计 3707 / 失败 0 / 成功 3705 / 已跳过 2**（`architect-r2-full-run.log`）。第 1 轮确认的两项脆弱用例本轮均通过。
- 与开发报告一致（开发在非交互会话为 3704/3 跳过；差异仅为桌面光标用例在交互式会话执行）。

## 4. 针对性探针（实测）

1. **第 9 条（环境凭据）**：人为设置 `ANTHROPIC_AUTH_TOKEN=sk-fake-probe-token`、`ANTHROPIC_API_KEY=sk-fake-probe-key` 后运行 `LocalProxy_WithoutKey_IsConfiguredAndSendsNoApiKeyOrAuthorization`：**1/1 通过**，不再发出 Authorization。产品侧 `ClientOptions.Credentials = null` 生效，用例本身也已改为自行设置环境变量，不依赖外部环境。
2. **第 3 条（维护测试稳定性）**：在 5 个并行 `Iris.App.Tests` 宿主压满负载的同时连续运行该用例 5 轮，**5 轮 4/4 全过**（第 1 轮为修复前 15 次中失败 2 次）。冷启动预热 + 有限重试 + 45 秒独立预算的修法有效。

## 5. 确认的新缺陷：第 2 条修正在生产 DI 下不成立（代码与注册层级可推演）

事实（均为当前提交实际内容）：

- `AppHostBuilder.cs:87` 将 `IAppLifetime → AppLifetime` 注册为**单例**，故 `AppLifetime` 注入的是**根提供程序**。
- `AppHostBuilder.cs:118` 将 `SettingsViewModel` 注册为 **Scoped**；`AppHostBuilder.cs:52-53` 显式开启 `ValidateScopes = true`。
- `SettingsWindowPresenter.cs:252-262` 为每个设置窗口 `CreateScope()` 并在**该作用域**内解析 `SettingsWindow` 与 `SettingsViewModel`。

因此 `AppLifetime.ShutdownAsync` 失败分支 finally 中的 `_serviceProvider.GetService<SettingsViewModel>()` 会在开启作用域校验的根提供程序上解析 Scoped 服务而抛 `InvalidOperationException`；即便不抛，根作用域实例**也不是窗口会话中那个 VM**，`ResetHandoffTransferred()` 无法复位真实标志。该异常还会从 `finally` 抛出，覆盖原本要上报的停止失败。

R2 新增的 `UpdateHandoffLifecycleTests` 用自建 `ServiceCollection` 把 `SettingsViewModel` 注册为 `AddSingleton` 且未启用作用域校验，所以用例通过——这与工单第 1 条“用实际类型与真实门闩验证，禁止只断言 fake”的要求不符：该用例断言的是自搭的 DI 拓扑，而不是产品实际的单例/作用域组合。

本条按纪律列为**已确认缺陷**（依据为注册层级与 DI 作用域语义，非已执行探针）；R3 必须先复现再修正。

## 6. 包与来源绑定已过期（架构师裁定）

R2 修改了产品源码（`App/Updates`、`App/Services`、`Providers/Anthropic`、`Platform.Windows/Maintenance`），而 T-039B 的实际包（`release-0.2.0-final-3`、`release-0.2.1-final-3`）、`delivery-binding.json` 与来宾验证结果都来自 `fe3f788` 源码。按 `T-039B.review/final-coverage-notes.md`（“较晚的资源责任或 Main/维护修正不能只用较早包的结果宣告最终验收”）与 `DESKTOP_RELEASE.md` §7，须由 R3 重新打包并重新绑定。R2 工单中“本单不需要新的来宾场景”的说明是架构师的判断失误，已在本轮纠正。

## 7. 其余项核对结果

| 项 | 结论 |
|---|---|
| 1 交接取消语义区分 | 通过（代码区分 user-cancel / 撤销 / 对端退出 / 超时；UI 显示 Failed + 错误详情，保留重试；测试覆盖真实门闩） |
| 3 维护测试稳定性 | 通过（§4.2） |
| 4 提交后工作目录回收 | 通过（`_directory = null` + `MaintenanceWorkDirectory.Clean` 有界回收、上限 32、忽略身份不可用的 Win32Exception）；**范围外文件未披露**，见 §2 |
| 5 锁外通知 | 通过（`notifyUser` 标志后在锁外通知） |
| 6 并行实现标注 | 通过（补 XML 文档标注仅供测试；未删除，符合“或明确标注”的选项） |
| 7 用例重命名 | 通过（`UpdatePreferenceOnlySaveHasNoApplyPending_AndSecondSaveIsNoOp`） |
| 8 资源键属性顺序 | 通过（20 键统一为 `<data name=… xml:space=…>`） |
| 9 环境凭据 | 通过（§4.1） |
| 10 GitTree 说明 | 通过（写入报告 R2 段第 10 行；绑定文件本身未改，工单允许“报告或绑定文件”） |
| 2 停止失败回滚 | **不通过**（§5） |
| — 重打包与重绑定 | **未做**（§6） |
