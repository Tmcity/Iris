# T-036C：完整网络设置页与草稿连通性测试

**2026-10-08恢复签发**：用户指定新开发对话01a11b01-c46b-74e1-9015-726c77ffbc9e接替故障旧对话。当前工作区已有C实现/测试，仍以0cd9e5216f5bf52074536b91dd2c95681b336b50为已提交基线；本单继续实施，不重新开始。优先完成本文件末尾取消回调清理重试及Reload失效两项补充，保留既有代码/独立证据，完成全部原C范围后归档交付。

2026-10-08 · 架构师签发 · 基线0cd9e5216f5bf52074536b91dd2c95681b336b50，master。

## 前置与唯一范围

B/R1已独立验收，证据T-036B.review与T-036B-R1.review，原始失败和修正记录保持。当前唯一C，完成父T-036剩余全部可用功能。必读specs/NETWORK_SETTINGS_UI.md全文、NETWORK_CONFIGURATION.md及已批准CCR-036/CONTRACTS、既有Settings/OCR设置生命周期；ARCH v1.54。

## 必须交付

1. 现有SettingsWindow开放network导航，完整三模式/本地绕过/地址/保持替换清除凭据、遮蔽密码、已保存服务的测试目标选择、安全结果/取消/重试清理状态；不另建窗口，不读取回填存储密码。
2. Network草稿纳入原全窗口联合计划、冻结/冲突/外部合并、回执配额/ApplyPending/原回执RetryApply、密码保留与清除、readonly。保存与测试刷新真实控件当前文本；未编辑dormant值不阻止显式None/System。
3. 实现已声明IProxyConnectivityTester/IProxyConnectivityTestSession，纯Create、单次逻辑HEAD、真实CaptureDraft、独立非全局池、无Provider密钥/业务调用/正文、正确状态与direct说明、10秒软期限/16KB头预算/不跟随重定向。端点只来自用户已保存的所选Profile，领域+ID正确。
4. 网络测试队列与session完整责任：取消/修订/端点外部变化拒绝迟到结果，Run后等待Dispose，失败原对象重试不重发；保存/Reload/放弃/关闭/Host退出覆盖所有测试和最后UI发布，重复关闭/停止共享责任，无门闩自等待、DI循环或资源遗弃。
5. 规范§6所有真实UI/Core/JSON、真实Tester+Sockets内存流、失败门闩、Host及逐图验证。B底座已验收，不以A原型或全fake替代生产证据。

## 约束与完成

公开契约不改，使用已落地CCR-036声明；无新的错误码/配置字段/schema/default/包。允许必要App/Providers注册与内部退出组合，允许Core给Iris.App增加仅用于共享纯codec的内部友元，避免重复安全解析；不返工已验收B迁移/路由/资源算法。若实际端口或底座确有不足，给具体复现与窄建议由架构师处理，不自行缩减功能或扩接口。

不开始T-037及以后，M6延期，OCR继续复用翻译窗口。测试不碰真实用户数据/代理/socket/HTTP/输入或性能脚本，使用临时文件与ConnectCallback内存流；既有获准SDK smoke保持。失败先放行门闩再join/处置，日志保留。先有意义定向与逐图检查，再串行dotnet build Iris.slnx -c Release和dotnet test Iris.slnx -c Release，零警告错误/全绿。

继续指定开发会话01a11b01-c46b-74e1-9015-726c77ffbc9e，一次一单，不建代理/新会话，不push/reset/clean/rebase/amend；.codex不改删不提交。保留架构师B/R1结案、R1.review全部源码/日志（.log显式纳入）、新规范/ARCH/BACKLOG/CCR/交接随本单提交。

完成归档工单全文和真实报告tasks/done/T-036C.md，移除current_task，创建独立本地提交回报完整hash请求复审。T-036/C保持▶，只有架构师最终结案；不得自行推进IPC或发布阶段。


## 开发期独立复核：取消回调失败不能永久阻止清理重试

2026-10-08，属于C单次会话取消/真实清理失败重试范围。实际ProxyConnectivityTester/Session，窄Source在传入token上注册一次抛错回调，返回有效direct快照；窄HTTP handler正常返回HEAD 200。首次Dispose的CancelAsync报告回调失败后，即使取消注册已经移除，第二次Dispose仍await同一个faulted `_cancelWork`，handler与session一直保留，无法通过原入口恢复。

实际输出：

```text
CANCEL_CALLBACK firstFailed=True; retryFailed=True; heldSessions=1; handlerDisposed=False; sends=1
```

请区分“取消回调已完成但报告失败”和“回调仍在执行”：首次失败保留可观察结果，之后的原session Dispose可继续剩余资源清理，不能永久重放同一faulted Task；不能通过吞掉首次错误或再Run/HEAD解决。覆盖调用方取消、主动Dispose/全局Stop以及一次失败后的原实例重试，仍须等待真正非合作工作退出。

独立目录C:/Users/28910/AppData/Local/Temp/iris-t036c-independent-tester，Program.cs，运行`--cancel-callback`，cancel-callback-1.log和binding.json。创建无副作用、HTTP 200/401/407/302分类、无Origin认证、Run与Dispose分离四组已通过。

诊断最终仅为安全释放夹具，在记录第二次真实失败后反射将已经观察完的faulted取消Task替换为CompletedTask，再经实际Dispose关闭资源；日志明确标FIXTURE ONLY，绝不算产品恢复通过。没有修改生产文件/真实用户配置或发出socket请求。修复后必须无需反射即可原入口成功清理。


## 恢复检查发现：异步Reload须立即撤销旧模型结果发布资格

2026-10-08，开发对话不可用期间的独立工作区检查。recovery-build.log为零警告/错误；recovery-full.log全量3548（3545成功、1失败、2既有跳过），失败为SettingsDraftAndSaveRegressionTests.Scenario4_StaleResults_RejectedOnSwitch_EndpointChange_Edit_Delete_OrReload，Reload后AvailableModels含stale-after-reload。

C将有在途翻译/模型工作时的Reload改为ReloadAfterDraftCleanupAsync，先等_operationQueue排空再SyncBaselineFromCurrent。ExecuteListModelsAsync的最终发布只检查窗口/选择/Revision/_requestSeq，没有用取消令牌拒绝返回值；新Reload等待前未使这些身份失效，因此忽略取消的旧任务仍会发布到现有草稿。应在Reload接受点、首次异步等待前撤销旧结果资格（翻译测试/模型以及相关OCR/网络领域各守自己的身份），同时保留真实排空与最后UI回调屏障。

不要以延后旧测试断言、删除stale-after-reload检查或只要求底层合作取消规避。本条属于C新Reload接线对既有行为的回归。完整原始证据tasks/done/T-036C.review/recovery-full.log，未进行“重跑至绿”。
