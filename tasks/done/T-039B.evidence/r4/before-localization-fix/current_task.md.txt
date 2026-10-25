# T-039B-R4：修复生产关停后的更新回滚与重试

2026-10-25 · 架构师签发 · master · 基线 `05452e0a85a36982ef8bfd22d5a981e581eeafcf`（T-039B-R3）。

2026-10-25 最新协作安排：用户已创建新工程师会话 `01a16ef2-a0bb-71a1-8230-57165c05cc5b`，本单由该会话实施；架构师直接转交工单、协调补充和独立复审，用户不再负责传话。此安排覆盖此前 Gemini / 用户手动传话的安排。一次一单，本单是当前唯一活动工单，不操作旧工程师会话，不创建聊天或子代理。T-039 / B 保持进行中，T-039C、T-040 未签发，M6 延期。

先读 `tasks/done/T-039B.review/acceptance-review-3/README.md`。前两轮已确认成果、R3 的 Scoped 生命周期修正、final-4 实际包/来宾成功证据及 R2 偏差披露均保留，不重做无关实现。

## 必须修复

### 1. 生产停止流程封口后，已确认的更新不能回滚为可恢复状态

架构师已实测复现，非只读推导：同一探针仅把 R3 测试中的 `AppBootstrapper` 补上生产已有的 `updates: coordinator` 依赖，停止失败后的状态就由 `Failed / HasCandidate=true / 安装可用` 变成 `Stopped / HasCandidate=false / 检查和安装均不可用`。

原始证据：

- `T-039B.review/acceptance-review-3/rollback-control-1.log`：exit 0，R3 原接线通过。
- `T-039B.review/acceptance-review-3/rollback-production-1.log`：exit 1，补上生产接线后原断言失败。
- 源码、程序集指纹与命令记录均在同一目录；可重建探针在 `artifacts/t039b/independent-r3-rollback-1`。

原因：`AppBootstrapper.StopAsync` / `StopCoreAsync` 先调用更新协调器的 `StopAcceptance` / `StopAsync`；前者设置 `_closing=true` 并清候选。`AppLifetime` 的失败分支随后调用 `RollbackLastApply`，后者遇到 `_closing` 立即返回。R3 测试未把更新协调器传给 bootstrapper，故没有经过实际封口。生产还会在 Host 停止前清理设置会话，不能再用未参与停止的活动窗口证明真实失败反馈和恢复可用。

要求：

- 为“已准备更新后停止失败”建立显式、由真实拥有者负责的恢复路径。用户必须得到可见且符合当前语言的失败反馈，候选/安装意图不能被静默丢失，恢复入口必须实际可执行。
- 区分关停准入封口、prepared 候选所有权、失败回滚和最终退出。不能只删 `_closing` 检查或把 `_closing=false`，从而把已停止的网络/窗口/Host 当作仍在运行；不得重新开放普通工作或遗弃实际任务来换取按钮可点。
- 在安全的恢复路径下完整验证重新准备/重试已确认更新；若窗口仍活着，要复位**真实会话 VM**；若该窗口已被正常清理，不能向已处置 VM 发通知或依赖已消失按钮，须由仍存活的拥有者提供实际恢复入口。
- 保留失败原因及撤销清理错误；提交前撤销并 join helper，不能在失败退出时启动 native apply 或自动重启，也不能使持久门禁永久卡住。
- 不新增公共契约，不改 `CONTRACTS.cs`、schema/default，不放宽 `ValidateScopes`。

### 2. 补齐能验证生产顺序的回归测试

- 测试保持 `ValidateScopes=true`，singleton lifetime/presenter/coordinator、Scoped VM、真实 per-window scope。
- 接入**真实** `AppBootstrapper` 的更新协调器与设置 shutdown coordinator；WPF 操作使用正确的 Dispatcher。禁止通过不给这些依赖、改注册生命周期、直接调用 `RollbackApply`、仅手设标志来证明生产恢复通过。
- 确定性覆盖至少两个实际顺序：窗口尚存时某个停止阶段失败；窗口清理完成后 Host 停止失败。验证错误可见、候选/意图责任保留、恢复可执行，以及重复失败和随后成功不会重复提交/应用/重启。
- 真正经过 handoff 的准备/撤销边界；外部进程/安装/native 边界可使用自有受控替身，使用随机 IPC/临时目录。须明确实测到哪一层，不把 `new UpdateHandoff` 或 `SetHandoffTransferredForTesting` 写成 helper 已 prepared。
- 重放架构师反例或等价的完整生产拓扑，并保留修前失败与修后通过。原有断言与全部失败证据不得删除或削弱。

### 3. 重新打包与证据闭合，并修正文案

本单会再次修改更新/退出产品代码。完成后必须用系统 Windows PowerShell 5.1 为 0.2.0 / 0.2.1 生成新的唯一命名产物（例如 final-5），不得覆盖 final-4 及任何原失败。

- 按最终源码重新绑定源文件、实际 payload 清单/大小/SHA、安装入口、原始日志、来宾输入和截图；`GitTree` 使用明确定义且可重算的非自引用范围，并验证与实际最终提交源码一致。
- 在自有干净 Sandbox 复验受本次改动影响的本地两版本更新，以及一次安装 + 运行中静默保留卸载。旧完整矩阵无关场景可以沿用，但逐项说明来自哪版包；不能用旧包冒称最终代码通过。
- 修正 R3 报告和绑定中的“22/22”“25/25”：原结果实际为 24 / 26 个观察记录（其中布尔断言为 10 / 17），按类别如实记录；新轮次只从实际输出取数。
- 修正“原生应用增量补丁”的表述：R3 实际传入并应用的是 full 包，delta 仅为生成产物。无须为修正文案另做 delta 安装实测。
- 沿用已披露未实测项，不扩大为标准用户拒绝提升、系统重启、跨用户、便携实机、GitHub 线上链路已通过。C 和 T-040 保持后续范围。

## 允许范围和执行约束

- 允许更新/退出恢复所需的 `src/Iris.App/Updates/`、`Services/`、`Hosting/`、`Settings/`、对应资源和测试；确有需要可改 `src/Iris.Maintenance/` 与 `src/Iris.Platform.Windows/Maintenance/` 及其测试，报告原因与影响。构建证据夹具、打包产物、`T-039B.md` / `T-039B.evidence` 允许随单更新。
- 必需的自有测试 worker、测试工程引用/复制规则及 `Iris.slnx` 对该 worker 的明确登记属于本单测试接线范围；保证 Release 方案构建使用新鲜的 Release worker，不借用先前输出，不放宽架构白名单为任意工程。
- 本轮独立完整本地化审计已实际失败，详见 `acceptance-review-4/localization-before-1/README.md`。允许 `build/localization/app-catalog.json` 仅同步其中已审校的五对现有资源值；修正现有交接异常的消息本地化与测试兼容 XML 说明，并完成 source/compiled/catalog 全量审计。不得扩张例外预算、放宽检查或重跑 migration；保留原始失败及类型/错误码/所有权语义。这是原 LOCALIZATION 要求的收尾，不另开下一单。
- 不改其它 Provider、无关依赖版本、公共契约或设置格式；BACKLOG 仅架构师修改。
- `.codex/` 是用户工具，原样保留，不读 SSH 私钥，不改删或提交 `.codex/`；不 `push/reset/clean/rebase/amend`，保留全部历史。
- 自动测试不真实联网、不读写宿主用户设置/密钥/Run/选区/剪贴板，不运行性能脚本。真实 Iris 安装/更新/卸载仅在本单自有 Sandbox 中进行；不操作其它实例。
- 构建与测试串行。架构师本轮验证已结束，窗口归还；后续独立验证另行协调，不泛杀 dotnet。

## 收尾回报

2026-10-25 环境补充：当前宿主 Sandbox 功能为 Disabled（Win32_OptionalFeature InstallState=2），WindowsSandbox.exe/wsb.exe 不存在；Hyper-V 服务存在，但当前账户 Get-VM 被拒绝。架构师已接管隔离环境入口/管理员确认，独立记录在 `acceptance-review-4/host-isolation-preflight-1.json`，不要求工程师向用户重复索取。启用宿主功能的脚本仅为待批准方案，未执行；不能在宿主运行 Iris 来绕过该缺口。在新包的真实来宾复验完成前，R4 保持未完成，保留 current_task；可先完成本地报告/来源清单和冻结候选，不用旧来宾替代，也不提前结案或开始 C。后续归档须保留此前工单快照，并同步本补充。

执行 Release 构建（0 警告/错误）与全量测试，保留真实日志/退出码/跳过说明。追加 `T-039B.md` 的 R4 报告：缺陷修前/后、生产拓扑和故障阶段、实际反馈/恢复动作、新包/来源绑定/来宾结果、未验证项及全部工单偏差。归档本工单全文并在核对后移除 current_task，创建新的独立本地提交，不改写旧提交。

在本工程师会话回报架构师：完整 commit hash、改动摘要、原始构建/全量汇总、新反例的修前与修后记录、新包和来宾路径。架构师直接读取并复审，不再要求用户代传。仅请求复审，不自行宣布 T-039B 或父任务结案，不开始下一单。
