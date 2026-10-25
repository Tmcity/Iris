架构师工单：请在现有工作区 F:\Iris 执行 T-039B-R4。

用户刚创建并指定本会话为工程师会话，之后由架构师直接转交工单和补充，架构师独立复审。此前 Gemini / 用户手动传话安排已被覆盖；不需要用户再次传话。你只承担开发实现，不自行验收结案或签下一单，不创建新聊天或子代理，不改变模型。

当前 HEAD / 唯一基线：05452e0a85a36982ef8bfd22d5a981e581eeafcf（T-039B-R3）。请先实际核对 HEAD 和工作树，再完整阅读：
1. F:\Iris\tasks\current_task.md（T-039B-R4，唯一活动工单，完整约束以此为准）
2. F:\Iris\GEMINI.md
3. F:\Iris\tasks\done\T-039B.review\acceptance-review-3\README.md 及 rollback-production-1.log / rollback-control-1.log / rollback-probe-binding.json
4. F:\Iris\specs\DESKTOP_RELEASE.md、ARCHITECTURE.md 的依赖/生命周期规则和 CONTRACTS.cs（不改公共契约）；需要历史时读 T-039B.md 的 R2/R3 段与前两轮评审。

当前问题已经独立实测：R3 正确修复了根提供程序解析 Scoped VM，但测试漏传 AppBootstrapper 的生产 updates / settings shutdown 依赖。同一固定产品探针，只加上 updates: coordinator，停止失败后就成为 Stopped / HasCandidate=false / 检查及安装不可用；_closing=true 令 RollbackLastApply 直接返回。不能仅把 _closing=false 重新开放已经停掉的子系统。

本单必须完整交付：真实生产停止顺序下的可见失败反馈和安全可执行恢复；接入实际更新与设置 shutdown 的 scoped/Dispatcher 回归，覆盖窗口仍存及已清理后失败、真实 handoff 准备/撤销和重复失败后成功；用 PowerShell 5.1 重新生成唯一新包（如 final-5）、重新绑定源码/实际载荷/原始结果，并在自有干净 Sandbox 复验受影响的两版本更新及运行中静默保留卸载。修正报告的观察数量、full/delta 与 GitTree 自引用表述。前两轮已确认的成果和 final-4 历史证据保留，不重做无关完整矩阵。

工作区中 BACKLOG 改动、current_task 和 acceptance-review-3 / 本协调记录是架构师本轮成果，不能删除或覆盖；可按工单随交付保留。BACKLOG 仅由架构师修改。.codex/ 是用户工具，原样保留，不改删、不提交，不读取 SSH 私钥。不 push/reset/clean/rebase/amend；不在宿主运行真实 Iris 安装/更新/卸载；测试不联网或读写宿主用户数据/密钥/Run/剪贴板/选区。不改默认值/schema、公共契约或无关版本。真实安装只在自有 Sandbox，保留原失败与字节，不泛杀 dotnet 或操作他人实例。

架构师的独立构建/测试及探针都已结束，本单实现期间构建测试窗口交给你，后续串行协调。既有独立结果：Release 0 警告/错误，全量 3707（3705 成功/0 失败/2 既有跳过），不能代替你改动后的验证。完成后按工单执行 Release/full、归档原文/真实报告/证据、创建独立本地提交，在本会话回报完整 commit、汇总、新包/来宾及失败前后证据。T-039B 与父任务尚未验收，C/T-040 不得开始，M6 延期。

请先简短确认已实际读取的工单、HEAD 和边界，然后继续实施本单；遇到范围内工程问题直接向架构师反馈，不停在计划或等待用户代传。
