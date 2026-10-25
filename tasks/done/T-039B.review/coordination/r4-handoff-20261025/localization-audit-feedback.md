架构师补充：继续同一 R4。收尾核对发现原 LOCALIZATION 审计尚未覆盖 R2/R4 新增内容；我已实际执行现有工具，source/compiled 1230 项匹配，catalog/source 审计 exit1，原始结果和输入快照见 F:\Iris\tasks\done\T-039B.review\acceptance-review-4\localization-before-1\README.md。

实际两组问题：App 资源462键而 app-catalog457，缺少恢复界面的5键；另有6个未批准源码站点（GeneralSettingsSaveCoordinator 两处测试专用XML注释、UpdateHandoff 四处中文异常消息）。五对现有中英文文案已逐项审校，允许按 approved-catalog-additions.json 同步，现有值不动；current_task 已明确加入这个收尾范围。

请保留测试兼容成员的说明，可转为英文XML；交接异常使用相应错误码的既有本地化消息或正规资源，保留类型/错误码/重试/错误对象责任。不要扩大例外预算或放宽扫描，不运行 migration。先保存并复现修前审计，再修后完整运行 source/compiled/catalog 审计，记录真实结果；修改产品消息后按原单执行受影响验证、最终建测、新冻结源/唯一新包和绑定，保留 final-7 及旧快照，不覆盖。无需重做无关功能。

Sandbox 确认仍待用户回复，未执行宿主变更；这不妨碍本地收尾。架构师当前没有建测任务，窗口交给你。工单保留，C/T-040 不启动，不以旧审计或旧来宾代替当前要求。
