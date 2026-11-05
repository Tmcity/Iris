# T-036C 开发恢复交接（2026-10-08）

**2026-11-05 当前有效状态**：T-039C仍是唯一活跃工单。用户授权的0.2.6已仅推原标签；实际CI41663111425/attempt1失败，官方digest已与原ZIP匹配、51原件冻结且receipt已同步失败。固定0.2.7源码fc44a3a78c2729c25dfb91296c51602b843a94f9完整Release0警告/0错误、全量3759/3757成功/0失败/2既有跳过；721来源、18资产/97载荷及旧正式full独立匹配，唯一candidate1已通过，本地正常合并e4951aa。原UIA反例、初版027热键全量失败和修前源均保持，生产/契约/schema/依赖不变。新Sandbox2fc7c168…中用户已手工重装正式0.2.2并准备开关false、虚构服务和历史；97安装载荷、原App/PID7312、V3/PID8152、服务/PID7328、实际POST200和SQLite旧历史均独立通过。首次安装约10min已定位到.NET MSI签名策略检查四次约120s等待；487s运行时/约1s Iris部署的原日志保留，具体证书网络原因与后续NFR仍待处理。正常审阅归档06b282bd928249519ea07b3eea999238f1202328已完成，407原始证据共11625584字节匹配index和提交blob；新鲜只读预检通过，缺少本版本授权在任何标签/发布目录创建前退出2。用户已明确授权仅v0.2.7。首次执行在标签创建前遭匿名API限额403，原receipt与源代码保留；官网页面和正式旧full重新匿名读取/哈希匹配、同一新来宾数据和进程预检已通过。正在继续同一已授权标签，尚未创建/推送；无需重复授权。main/codex远端仍285df3a，正式前版仅0.2.2。C/父T-039、真实升级/退出/native顺序/一次正常重启/卸载和T-040/NFR未结案，M6延期。不推分支，不重写原失败标签/CI/候选，不在宿主安装Iris，不改.codex或读取私钥，不创建工程师聊天/子代理。完整接续见 [SESSION_STATUS_20261105.md](F:/Iris/tasks/T-039C.coordination/SESSION_STATUS_20261105.md)。

**0.2.5最终源校正（2026-11-02）**：原4e9484a/验证1/候选1保持，最后修正发布说明旧候选文字后的发布source为ee2d7118597d07b891066256349098877e65f794/tree9dff0cd9e8f7ef4da5ac1aa82d3b233c885a0883。完整验证2 Release0/0/full3757/3755成功/0失败/2既有error183跳过，candidate2打包与独立721来源/18资产/97载荷/正式0.2.2前版核对通过；本地外层a557738f…/fullc215ffe7…。新source已正常本地整合，PREPUBLICATION_REVIEW_V025已更新此检查点，待用户新确认仅v0.2.5→ee2d711，不推分支，不指向下面旧源或报告。旧用户Sandbox13:19UTC仍原0.2.2/PID6604、V2watcher6956 Ready及原三项数据哈希。C/父T-039/T-040/NFR未结案、M6延期；下面源/候选/过程均为完整历史。

**2026-11-02 新接续入口**：当前T-039C已授权0.2.4但CI失败未发布，原tag/digest/原件保留；最新source4e9484a/tree907154dd以两个Core夹具的完整线程/门控所有权修正完成最终Release0/0/full3757/3755成功/2既有跳过，0.2.5 candidate1/721源码/18资产/97载荷与正式0.2.2前版独立通过。正常本地整合完成，新PREPUBLICATION_REVIEW_V025_20261102.md待用户单独确认仅v0.2.5→4e9484a，新tag/分支未授权推送。旧用户Sandbox及0.2.2/合成Key历史保留，实际UI升级/单次重启/卸载未完成，C/父T-039/T-040/NFR未结案、M6延期。下方旧任务/会话为历史，不重开，不改.codex。

**2026-11-02 最新入口**：T-036C及下方旧会话均为历史。当前T-039C已执行用户仅授权v0.2.3，但实际CI失败、未发布，原tag/原件/digest全保留。源码942b0dd/tree0146030d完成最小三个测试夹具修正，最终Release0/0/full3756/3754成功/2既有跳过与0.2.4唯一candidate1（正式0.2.2前版）/720来源/18资产/97载荷均通过。正常本地整合已完成，下一PREPUBLICATION_REVIEW_V024_20261102.md须用户单独授权仅v0.2.4→942b0dd；当前不推任何新tag或分支。用户旧Sandbox/Iris0.2.2/合成Key历史保持，实际UI升级/重启次数/卸载未完成；C/父T-039/T-040/NFR不结案、M6延期。不要重开旧任务/聊天或改.codex。

**2026-11-02 当前接续入口**：以下 T-036C 恢复及旧开发会话记录均为历史。唯一任务是 T-039C，由本主会话直接推进、用户手工进行实际安装更新卸载。正式 0.2.2 已安装，关闭启动检查/合成已保存 Key/旧历史已独立核对；原应用 PID6604 与只读 V2 记录器 PID6956 就绪。0.2.3 候选以正式 0.2.2 为前版，720 项 source fb9f46f/tree60422257、18 资产/97 载荷通过；下一个入口 PREPUBLICATION_REVIEW_V023_20261102.md，第二版仍须用户单独授权，仅 v0.2.3→fb9f46f。真实 UI 升级/重启次数/保留数据/更新后卸载未完成，C/父 T-039/T-040/NFR 未结案，M6延期。不重开旧会话、旧工单或改 .codex。

**本恢复任务已结案（2026-10-08）**：新开发对话成功交付11263780f2a34dd9ee4dc998d80dfdf605dacaf0；取消回调及Reload原反例经独立复验修复，Release/全量及实际Tester/UI/48图通过。当前唯一任务已转T-037，详见current_task和最新PROJECT_HANDOFF；下方恢复/阻塞/待办均保留作历史，不再作为待执行指令。

**恢复交付完成、请求独立复审（2026-10-08，覆盖下方待修复/阻塞状态）**：工单全文及开发报告为 `tasks/done/T-036C.md`，当前单按签发要求归档移除。两项补充修复、真实取消按钮收尾验证、独立 Tester/UI 探针均完成；最终 Release 零警告/错误，全量总计 3553、成功 3551、失败 0、既有环境跳过 2。交付图逐张查看，全量生成图与之逐文件 SHA 一致；产物及原始日志绑定在 `tasks/done/T-036C.evidence/final-delivery-binding.json`。下方保留恢复历史及原失败事实。开发交付不代表架构师验收，父 T-036/C 与 CCR-036 结案权限保持。

**连接已恢复并接单**：新目标01a11b01-c46b-74e1-9015-726c77ffbc9e，消息01a11b11-6b4a-7601-9ff6-395869f14405，执行轮01a11b11-747c-79b2-847d-940d18c6b192。取消清理及Reload补充已有实现；实际定向31/31与34/34通过，仍待最终归档提交及独立复审。下方故障/阻塞仅为历史，不再请求替代链接，不重发工单。

**2026-10-08恢复推进（覆盖下方旧阻塞与会话指向）**：用户已指定新开发对话01a11b01-c46b-74e1-9015-726c77ffbc9e，新对话已确认开发工程师角色且工作区为F:/Iris。唯一任务仍T-036C，恢复现有未提交实现，优先取消回调faultedTask清理重试与Reload旧结果失效，再最终验证/归档。旧对话最新记录interrupted且completedAt已非空，不再向旧对话派工。A/B/R1成果保留，M6延期，T-037不提前，不push。

当前仓库F:/Iris，master，最后提交0cd9e5216f5bf52074536b91dd2c95681b336b50。当前唯一工单tasks/current_task.md为T-036C，尚未交付/提交。工作区所有C代码与测试必须保留，不重做A/B、不reset/clean/rebase/amend、不push、不改删或提交.codex，不建额外代理。

开发角色负责实现/测试/归档；架构师独立验收和签下一单。T-036父单及C保持进行中，A/B/R1已验收。M6延期；OCR复用翻译窗口，不显示截图；T-037以后不提前开始。

C实现：Providers的ProxyConnectivityTester/Session、专属HEAD transport与网络生命周期组合；App网络草稿/页面、端点选择、联合保存和完整UI收尾接线。实际Tester定向27项和UI/Host组合26项日志已存在，48张候选图在tasks/done/T-036C.evidence/visual-final，架构师逐图检查已完成，但最终产物尚未绑定。

待完成：
1. current_task末尾取消回调失败补充：Session.Dispose等待faulted _cancelWork会永久阻止重试。首次错误应报告，之后原会话可继续清理，不Run/HEAD重发。复现iris-t036c-independent-tester/cancel-callback-1.log。日志中的FIXTURE ONLY反射仅释放诊断资源，不能计产品成功。
2. 原会话最后计划核对取消按钮是否在真实测试已结束后及时禁用，完整保存/关闭工作屏障保持；当前代码和新图日志以实际文件为准，不假设计划已完成。
3. 定向修正、最终Release与全量测试、所有失败/清理记录保留、候选/最终图像SHA绑定、工单全文与报告归档、本地独立提交并请求复审。当前没有完成这些最终交付步骤。

架构师独立证据：iris-t036c-independent-tester/basic-1.log四种状态与纯Create/HEAD/拥有者通过；wire-1.log实际Sockets内存流成功/不跟随302/16KB头预算通过；iris-t036c-independent-ui/ui-3.log实际WPF/JSON/Core/Tester粘贴测试后联合保存及清理最后UI发布阻止保存通过；iris-t036c-visual-review/reviewed.json记录48张已看图。临时目录均位于C:/Users/28910/AppData/Local/Temp。所有是开发期证据，不能当最终验收。

原开发对话01a112a7-c953-7630-9ee9-d2be2123dc70的C执行轮01a117b2-d7d5-7e03-9f74-45c57f86689a和排队补充轮01a11825-bfa5-7812-9fde-d86c54d35dbb均已终态failed，错误400 invalid_request_error/input。架构师已发送一次短恢复消息01a1183c-f642-7242-8be2-5c4a9193ce7d，恢复轮01a1183c-f7bf-7e42-b6fb-6a9b82bc27a4当前仍待确认，不根据interrupted/completedAt=null误判停止或重复发送。


## 恢复检查发现：异步Reload须立即撤销旧模型结果发布资格

2026-10-08，开发对话不可用期间的独立工作区检查。recovery-build.log为零警告/错误；recovery-full.log全量3548（3545成功、1失败、2既有跳过），失败为SettingsDraftAndSaveRegressionTests.Scenario4_StaleResults_RejectedOnSwitch_EndpointChange_Edit_Delete_OrReload，Reload后AvailableModels含stale-after-reload。

C将有在途翻译/模型工作时的Reload改为ReloadAfterDraftCleanupAsync，先等_operationQueue排空再SyncBaselineFromCurrent。ExecuteListModelsAsync的最终发布只检查窗口/选择/Revision/_requestSeq，没有用取消令牌拒绝返回值；新Reload等待前未使这些身份失效，因此忽略取消的旧任务仍会发布到现有草稿。应在Reload接受点、首次异步等待前撤销旧结果资格（翻译测试/模型以及相关OCR/网络领域各守自己的身份），同时保留真实排空与最后UI回调屏障。

不要以延后旧测试断言、删除stale-after-reload检查或只要求底层合作取消规避。本条属于C新Reload接线对既有行为的回归。完整原始证据tasks/done/T-036C.review/recovery-full.log，未进行“重跑至绿”。

## 历史阻塞（2026-10-08，已由新开发对话恢复）

连续三个目标执行回合未能恢复指定开发对话。原C与补充轮已明确400失败，唯一短恢复轮仍没有消息或命令记录；其completedAt为空，不据此声称该轮已终止，也不重复发送。已完成可独立进行的源码/视觉/构建/全量恢复检查，剩余取消回调重试与Reload失效修复、最终交付需开发角色接续。已请求新的开发对话链接，尚未收到；等待用户提供替代目标后按当前工单和本交接继续，目标范围不变。
