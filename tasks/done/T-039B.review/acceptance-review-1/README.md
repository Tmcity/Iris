# T-039B 架构师独立验收（第 1 轮）

2026-10-17 · 架构师 · 目标提交 `fe3f78897cb28cc9804d876495abba9ccc9f3e8b`（父 `6256a3fba611b2cd94fc8dfa32480584c16d7bfe`）。

本记录是架构师对 T-039B（含 R1 收尾）的独立复验，不是开发报告转述。凡标“实测”的均在本机实际执行；凡标“推证”的说明依据。

## 1. 结论

- **主体实现、实际包、交付绑定与本地隔离实证：核对一致，可接受。**
- **本单暂不结案**：独立复验确认了 3 项具有产品/验收影响的缺陷与若干低危项，另发现 1 项超出 B 范围的凭据缺陷；已签发 `T-039B-R2` 要求闭合后再结案。
- 父 T-039 保持 ▶；`T-039C` 未签发。

## 2. 提交与绑定核对（实测）

| 项目 | 结果 |
|---|---|
| 提交指纹 / 父提交 | `fe3f788…`，父 `6256a3fb…`，树 `251ad6e…` |
| reflog / 远端 | 单一 `commit`，无 amend；无远端配置，故无 push |
| 工作树 | 仅 `.codex/` 未跟踪；无未暂存或未提交改动 |
| 包顶层哈希 | 0.2.0 的 8 项、0.2.1 的 10 项逐项与绑定值一致 |
| `payload-manifest.json` | 两版均 97 项，与绑定记录一致；含 `release-source.json` 与 `maintenance/Iris.Maintenance.exe` |
| `SHA256.json` | 7 / 9 项；绑定额外含 `SHA256.json` 自身（无法自哈希），属合理差异 |
| 绑定证据 11 项 / 来宾图 8 张 | 逐项 SHA-256 全部匹配 |
| `signed-order.md` | SHA-256 `48f3b403…`，与记录一致；与 `T-039B.md` 附录去空白后完全一致 |
| `ExplicitlyIncludedLogs: 392` | 等于 `T-039B.evidence`（346）与 `T-039B.review`（46）的 `.log` 之和，定义可复现 |
| 提交内二进制 | 证据目录无 dll/exe/nupkg/pdb/so；`artifacts/` 被忽略 |
| 本地化例外 | 仅新增 2 条已批准的 CCR XML 注释例外，无修改或删除既有条目 |

**记录性说明（不阻塞）**：绑定中的 `GitTree` 为 `git write-tree` 结果，即**不含 `delivery-binding.json` 自身**的树；它与提交树 `251ad6e` 的差异只有该文件。报告未写明这一点，R2 需补一句说明。

## 3. 架构师独立构建与全量测试（实测）

- `dotnet build Iris.slnx -c Release --no-incremental`：**Build succeeded / 0 Warning(s) / 0 Error(s)**，`Time Elapsed 00:00:25.44`（`architect-release-build.log`）。
- 全量三次结果（`dotnet test Iris.slnx -c Release --no-build`）：

| 轮次 | 环境 | 结果 | 失败项 |
|---|---|---|---|
| 1 | 我误加 `DOTNET_CLI_UI_LANGUAGE=en` | 3704 / 0 失败 / 3658 成功 / 2 跳过 → 实为 44 失败 | 见 §4 第 1 条 |
| 2 | 未设该变量，但继承 `ANTHROPIC_AUTH_TOKEN` | 3704 / 2 失败 / 3700 成功 / 2 跳过 | 见 §4 第 2、3 条 |
| 3 | 不设上述任一变量 | 3704 / **1 失败** / 3701 成功 / 2 跳过 | `UpdateTransactionTests.ActualControlPipeWaitsForOriginalProcessAndPreservesCommittedFailures(native-failure)` |

开发报告的 3704 / 0 / 3701 / 3（第三项为桌面光标环境跳过）在本机不同会话形态下**可复现**：交互式会话中该项会执行并通过（第 3 轮仅 2 项跳过），因此报告的“3 项跳过”属实、不是掩盖。

## 4. 失败项归因（实测，逐项隔离）

1. **44 项失败 = 我的环境变量所致，与产品无关。** 对照实验：`ErrorMessagesTests` 在 `DOTNET_CLI_UI_LANGUAGE=en` 下 4 项中 1 项失败（期望中文文案、实际英文），不设该变量 4/4 通过，设 `zh-CN` 4/4 通过。结论：测试的文案断言依赖进程 UI 语言；新增 CI 工作流已固定 `DOTNET_CLI_UI_LANGUAGE: zh-CN`，属已知脆弱点，记录为后续测试维护项（不改产品）。
2. **Anthropic “无密钥”用例 = 我的环境凭据所致**，但暴露了产品行为：`env -u ANTHROPIC_*` 后 1/1 通过；带该变量时无配置密钥的请求仍发出 `Authorization: Bearer <环境变量值>`。详见 §6 第 1 条。
3. **`NetworkSettingsTests.InPlaceValueWithoutEventIsDetected_AndContinuouslyChangingStoreIsBounded` = 既有计数断言脆弱**：断言“连续变更存储的读取次数”恰为 8，负载下实测 12；隔离复跑 5/5 通过。该断言受异步密钥变更通知影响，属既有（T-036C 期）测试脆弱，非本单改动（本提交未触及网络设置读取路径）。记录为后续测试维护项。
4. **`UpdateTransactionTests`（本单新增类）= 测试稳健性缺陷，已确认。** 失败发生在 `Signal(pipeReady)`——即**产品事务尚未启动**、等待真实 PowerShell 子进程就绪的 15 秒硬预算超时（`UpdateTransactionTests.cs:37`、`Signal:70-71`）。负载下累计 15 次运行失败 2 次；隔离 10 连跑 10/10 通过。判定：测试对子进程启动等待过紧，需修 R2；产品逻辑本身在这些运行中未表现出缺陷。

## 5. 独立评审（架构师另发起的三个并行只读评审）

三份评审均以工作树 = `fe3f788` 为前提，结论如下（我已抽查其关键代码点）：

- **维护/打包方向**：source-review-1/2/3/4 所列功能项**全部闭合**（含 `Process.Start` 后保有真实子进程并完整等待、记录回调抛错后仍等待、同 helper 重试、`native-starting` 不明状态的显式修复、用户选删除但 native 卸载失败的循环、撤销/EOF 不误弹模态、`Program` 提交后失败可见、维护锁文件路径与 `RequireNoLinks`、门闩 finally 与主/清理错误分离）；架构白名单覆盖新工程。
- **更新链路方向**：source-review-1 的 5 条全部闭合（通知抛错逐订阅者隔离、Work 先登记、取消封口、Prepare 所有权、下载失败清理、404/限流分类、兼容 feed 先过滤）；source-review-2/3 条目逻辑闭合。
- **设置/本地化/测试真实性方向**：联合保存接线正确（捕获/冲突/ClonePlan/空计划均有 `CheckForUpdates` 分支，纯开关保存 `PendingApply==null`）；无死按钮；两语言 457 键集合相等且与目录双向一致，新增 21 键均被引用；例外清单未放宽；新增测试均使用真实产品类型，既有断言无削弱。
- **我方裁定**：更新卡片四个按钮不随 `CanEdit` 禁用**可接受**——更新操作不属于设置编辑，只读窗口下仍允许检查/下载/安装；但 R2 需补一条测试或注释固定该语义，避免日后被当成遗漏。

## 6. 确认缺陷与 R2 要求

**A. 产品缺陷（R2 必修）**

1. 【中】交接建立期或撤销引发的取消被静默吞掉：`SettingsViewModel.Updates.cs:65-66` 只 `catch (OperationCanceledException) { }`，而超时（`UpdateHandoff.cs:40` 30 秒预算）、对端退出（`WatchExitAsync`→`RequestRevoke`）、显式撤销都归为同一异常。用户点“安装并重启”后可能无任何反馈、无日志，且无法与主动取消区分。
2. 【中】已 prepared 后任一停止子系统失败 → `AppLifetime.cs:41-48` 撤销交接并抛出，而 `_updateHandoffTransferred=true`（`SettingsViewModel.Updates.cs:92`）使 `CloseUpdatesAsync` 不再处理：一次已确认的安装被静默取消，用户无提示，重试也不会重新应用。
3. 【中】`UpdateTransactionTests` 子进程启动等待过紧（§4.4）。

**B. 低危（R2 内一并处理）**

4. `UpdateHandoff.CompleteMainAsync(true)` 成功后未清理 `_directory`（`UpdateHandoff.cs:101-102`），`%LocalAppData%\Iris.Maintenance\work-*`（含完整 helper 副本）仅靠下一次惰性回收。
5. `AppUpdateCoordinator.cs:141` 在 `_gate` 内触发外部通知，存在潜在重入风险。
6. `GeneralSettingsSaveCoordinator.SaveAsync(GeneralDraftSnapshot)` 与其 `CheckForUpdates` 快照字段在 `src/` 无生产调用者，属双实现漂移隐患。
7. `UpdatePreferenceUiTests.UpdatePreferenceOnlySaveHasNoApplyPendingAndFutureReadonlyCannotEdit` 用例名与断言不符。
8. 新增 20 条资源键的 `xml:space` 属性顺序与文件其余条目不一致（纯风格）。

**C. 验收期独立发现（超出 B 范围，R2 内最小修正）**

9. 【中·凭据】环境存在 `ANTHROPIC_AUTH_TOKEN`/`ANTHROPIC_API_KEY` 时，即使 Iris 未配置密钥（`AnthropicTranslationProvider.cs:76-79` 显式 `AuthToken = null`），Anthropic 请求仍携带 `Authorization: Bearer <环境值>`；测试 `LocalProxy_WithoutKey_IsConfiguredAndSendsNoApiKeyOrAuthorization` 因此失败。凭据来源不可控，属安全相关缺陷。

**D. 记录性说明**

10. 绑定 `GitTree` 语义补一句说明（§2）。
11. 已知测试脆弱点（§4.1、§4.3）记为后续测试维护项，不在 R2 修改产品。

## 7. 实测证据文件

- `architect-release-build.log`：架构师 Release 干净重建原始输出。
- `architect-full-run-1.log`：第 3 轮全量原始输出（含维护测试失败明细）。
- 其余两轮与本记录列出的隔离探针命令与输出见 §3、§4 的正文摘录；含环境凭据值的两份中间日志已删除，未归档（避免把可用的环境凭据写入仓库）。
