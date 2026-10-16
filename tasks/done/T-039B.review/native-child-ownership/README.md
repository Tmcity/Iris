# 维护子进程拥有关系探针

引用固定候选Iris.Maintenance程序集，直接调用实际MaintenanceSystem.RunAsync。外部进程是本探针自己的无业务worker，只等待本次随机事件，不执行任何Iris/安装/卸载/更新/用户数据操作。

started回调模拟事务记录写失败。子进程到达实际门闩后，调用方应仍持有等待责任，直到门闩放行、进程退出，再传播原始错误。探针在finally释放门闩并亲自等待自有子进程结束；该兜底不是产品清理成功的证明。

只以实际日志/绑定描述运行结果，源码准备不代表通过。

## 实际结果（2026-10-13）

两次独立编译均零警告错误。`../child-ownership-before-2.log`使用固定真实Maintenance程序集得到：StartedCallbackEntered=true、ChildReachedOwnedGate=true、ChildAliveBeforeRelease=true，同时OwnerTaskCompletedBeforeRelease=true/OwnerTaskFaultedBeforeRelease=true。即记录回调失败后，产品任务已经返回故障而真实worker仍在等待。

探针随后在finally放行并亲自join worker，ChildExitCode=0、ProbeError=null、OwnerException=IOException；Passed=false、探针exit1。这是真实产品路径失败，未执行Iris或安装器。`binding.json`绑定程序集/候选源，`probe-v2-binding.json`绑定最终探针。

第一次before-1也观察到过早返回，但探针通过GetProcessById取得的Process未提前保有Handle，结束后查询ExitCode抛InvalidOperationException；原日志和Program.v1.cs.txt保留。第二次仅修正探针句柄观察，产品程序集未改，故障继续存在且清理已证实。

后续为同一候选增加恢复探针，原child-v2源码保存在Program.child-v2.cs.txt，与probe-v2-binding的哈希一致。RecoveryProbe直接调用真实MaintenanceEngine/文件gate，系统进程和删除为外部fake边界；`../recovery-same-owner-before-1.log`显示第一次模拟删除失败后，当前拥有者再次Recover仍失败，删除只调用一次，事务仍阻止启动，exit1。临时文件由探针最终清理，未运行真实安装器、未触及用户数据；恢复探针的指纹单列。

## 修正候选独立重放

2026-10-13已执行，2026-10-15接续核对并归还串行窗口。开发固定候选Maintenance SHA256为`bda86bce94f6a264eb7b8de2d681a6b6ee44e02380519b69a6881c992399e744`。独立核对其227项冻结产物及3份源码快照后，将原探针二进制复制到新的自有目录，仅替换9个候选依赖；没有重编译或修改探针，也没有覆盖原失败。绑定见`after-1-binding.json`。

`../child-ownership-after-1.log`：真实worker仍在门闩时，OwnerTaskCompletedBeforeRelease=false、OwnerTaskFaultedBeforeRelease=false；放行后ChildExitCode=0、OwnerException=IOException、ProbeError=null、Passed=true，exit0。证明记录回调失败后仍保有真实子进程等待责任，并在结束后传播原始错误。

`../recovery-same-owner-after-1.log`：FirstFailure=IOException、RetryFailure=null、NativeCalls=1、DeleteAttempts=2、BlockedBeforeRetry=true、BlockedAfterRetry=false、Passed=true，exit0。证明同一helper可重试数据清理且不重复native卸载。这两项是窄边界实证，不替代完整恢复/交接/安装产品验收；未运行真实安装器或触及用户数据。
