# T-036C 开发恢复交接（2026-10-08）

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
