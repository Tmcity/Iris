# T-039B-R3：R2 复核缺陷修正、重新打包与来源绑定

2026-10-17 · 架构师签发 · master · 基线提交 `4d19a46cccfa1041a3838011abf4330ee035c89c`（T-039B-R2）。

架构师第 2 轮独立验收见 `tasks/done/T-039B.review/acceptance-review-2/README.md`。R2 的 1、3、4、5、6、7、8、9、10 项已复验通过，**不要重做**；本单只做下列三项。`T-039C`/`T-040` 未签发，不得开始。

## 必须完成

1. **【中·必须修正】第 2 条（停止子系统失败后的回滚与复位）在当前 DI 拓扑下不成立。**

   架构师核对的事实：`AppHostBuilder.cs:87` 把 `IAppLifetime → AppLifetime` 注册为单例（因此它持有**根提供程序**）；`AppHostBuilder.cs:118` 把 `SettingsViewModel` 注册为 **Scoped**；`AppHostBuilder.cs:52-53` 开启 `ValidateScopes = true`；`SettingsWindowPresenter.cs:252-262` 为每个设置窗口 `CreateScope()` 并在该作用域内解析 `SettingsWindow` 与 `SettingsViewModel`。

   因此 `AppLifetime.ShutdownAsync` 失败分支 finally 中的 `_serviceProvider.GetService<SettingsViewModel>()` 会因“从根解析 Scoped 服务”抛 `InvalidOperationException`（该异常还会从 finally 抛出，覆盖原本的停止失败），即使不抛也只能拿到根作用域实例，**不是窗口会话中的那个 VM**，`ResetHandoffTransferred()` 无法复位真实标志。

   要求：
   - **先复现再修**：用与生产一致的拓扑复现（启用 `ValidateScopes = true` 的提供程序 + 通过 `CreateScope()` 解析 VM，或直接走真实窗口/主机路径），原始失败日志必须保留归档。若无法复现，停止并报告架构师，不要自行改设计。
   - 修正方式不得从根提供程序解析 Scoped 服务。优先让**标志的真实拥有者**复位：例如由 `SettingsWindowPresenter`（单例、持有 `ActiveSession`）在其生命周期内处理该会话 VM 的交接转移状态，或把该状态放到单例侧、让窗口 VM 按协调器状态恢复。不得新增公共契约、不得改 `CONTRACTS.cs`、不得放宽 `ValidateScopes`。
   - 新增/修改的测试必须使用与生产一致的 DI 生命周期与作用域校验（禁止用自建单例注册绕过；`UpdateHandoffLifecycleTests` 的两个用例需按此改造），并断言“停止失败后窗口 VM 的交接转移状态确实复位、候选保留为 Failed 且可重试”。

2. **【中·必须完成】按 R3 源码重新打包、重新绑定并复验受影响的自有来宾场景。**

   背景：R2 修改了产品源码（`App/Updates`、`App/Services`、`Providers/Anthropic`、`Platform.Windows/Maintenance`），而现存包与 `delivery-binding.json` 绑定的是 `fe3f788` 源码；按 `DESKTOP_RELEASE.md` §7 与 `T-039B.review/final-coverage-notes.md` 必须重新绑定（R2 工单里“本单不需要新的来宾场景”是架构师当时的判断失误，本单纠正）。

   要求：
   - 用系统 Windows PowerShell 5.1 实际执行 `build/pack.ps1`，为 0.2.0 与 0.2.1 生成新的唯一命名产物（不得覆盖 `release-*-final-3`），保留原日志与失败。
   - 从新产物重新生成 `delivery-binding.json`（源码树、载荷清单/大小/SHA、证据、来宾图、归档工单原文），并注明 `GitTree` 语义与本次所用包版本。
   - 在自有干净 Windows Sandbox 复跑**受本次产品改动影响**的场景：本地两版本更新（外部 helper 交接、提交后原生应用、`work-*` 回收路径）与一次安装 + 静默保留卸载；如实记录所用包、用户/权限/OS、截图与原始结果。
   - 不需要重跑未受影响的完整矩阵；未重跑的场景须在报告中写明“沿用哪一版包的结果”。

3. **【低】补齐 R2 报告的偏差披露。**

   `src/Iris.Platform.Windows/Maintenance/MaintenanceWorkDirectory.cs` 不在 R2 工单的允许清单内，须在“与工单的偏差”中说明原因与影响；同时列出 R2 报告未提及的其它既有测试文件改动（如 `UpdateHandoffTests` 之外的部分）。

## 允许范围与约束

- 允许修改：`src/Iris.App/`（更新、设置、Hosting、Services 范围内）、`src/Iris.Platform.Windows/Maintenance/`、`src/Iris.Maintenance/`、`src/Iris.Providers/Translation/Anthropic/`、上述范围的测试、`build/pack.ps1` 之外的构建产物与 `T-039B.md` / `T-039B.evidence`。
- 不得修改：`specs/CONTRACTS.cs`、设置默认值/schema、`Directory.Packages.props` 既有版本、BACKLOG、`.codex/`（原样保留、不提交）。
- 不得删除或削弱既有测试断言与失败证据；不得 `git reset/clean/rebase/amend/push`；不得改写 `4d19a46` 与 `fe3f788`。
- 自动测试仍禁止真实联网与宿主用户设置/密钥/Run/剪贴板/选区；真实安装/更新/卸载仅在本单自有隔离环境。
- 构建/测试与架构师串行：本单内你独占窗口，提交后归还。

## 收尾与回报

- 完成前执行 `dotnet build Iris.slnx -c Release`（零警告零错误）与 `dotnet test Iris.slnx -c Release`（全绿）；原始输出如实粘贴，跳过项保留并说明。
- 在 `tasks/done/T-039B.md` 追加 R3 段落（三项逐条闭合、复现与修正前后原始输出、新包清单与来宾结果、未验证项、与工单偏差），证据入 `T-039B.evidence`，工单全文归档后创建独立本地提交（不 push）。
- 回报：完整 commit hash、`git show --stat` 摘要、构建与全量测试的真实汇总行、第 1 条的复现日志与修正后测试结果、第 2 条的新包路径与来宾原始结果、未验证项。
