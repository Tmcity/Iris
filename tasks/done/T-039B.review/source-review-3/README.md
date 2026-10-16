# 恢复路径与更新失败提示的后续源码审查

2026-10-15。这是当前候选源码路径推导，**尚未独立编译/执行复现**；不要与两个已独立通过的Maintenance反例重放混淆。源文件及SHA见binding.json，继续原T-039B与source-review-2的恢复/提示要求，不新增公共契约或产品范围。

1. **选择删除数据后native卸载失败，恢复与安装修复仍可能相互拒绝。** 可按真实Engine、fake系统边界构造：UninstallAsync(delete:true)的native返回非零，程序尚未卸载，留下Kind=uninstall、DeleteData=true、Phase=verify、WorkerExitCode非零。RecoverAsync因未记录native成功而要求显式安装修复；InstallAsync(repair:true)核对旧事务后又无条件因uninstall/DeleteData拒绝，要求先完成数据删除。Program的普通卸载在IsBlocked时也先Recover，因而无法进入一次有明确责任的卸载重试。需要可执行的修复/重试路线，保留用户原删除意图及“真正卸载成功后才删除数据”的门槛，同时不重复已成功的native步骤。现有同一helper数据清理重试通过，不覆盖native本身失败的这一分支。

2. **所有handoff异常都不显示提示，掩盖了真实提交后的更新故障。** Program把handoff定义为首参数--handoff，最终catch只有!silent && !handoff才MaintenanceUi.Failed。UpdateTransaction在原进程退出后若native返回非零、版本核验/注册修复失败，会留下事务并抛错；此时原UI已退出，helper又无条件静默退出。正常撤销/预期EOF无需错误弹窗，但真实应用失败应有可读结果和可执行恢复指引。请区分明确的撤销与提交后故障，在自有隔离环境核对实际提示、事务保留和重新启动/修复入口，不仅验证catch的返回码。

这两条不要求暂停当前构建/测试；继续原B完整交付。后续以实际运行结果更新结论，保留本次源码和所有失败。
