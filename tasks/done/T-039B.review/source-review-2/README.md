# 维护流程预审与原生子进程责任

2026-10-13。候选文件/SHA见binding.json。第一项及第二项的“同一拥有者重试”已有独立执行证据；其它修复/撤销/时序判断仍为源码推导，不能统称已运行失败。

1. **已独立复现：started记录回调抛错会丢失原生子进程等待。** MaintenanceSystem.RunAsync在Process.Start后直接调用started，然后才await WaitForExitAsync。门闩worker实测中，started模拟事务记录失败，Run任务已fault而child仍活着；探针最终自行放行/join，child exit0。证据native-child-ownership/README.md及child-ownership-before-2.log。应从取得真实Process起用完整finally保有其等待责任，即使记录PID/开始时间失败也不能只Dispose句柄就返回。保留主/等待错误，事务记录仍需表达结果未知，不能因此放开新启动。
2. **同一进程的失败重试与修复不能永久拒绝。** Uninstall的数据删除失败留下OwnerPid为当前helper的transaction，Program的Retry转入RecoverAsync，后者IsAlive(OwnerPid)会拒绝当前仍活着的自己，形成反复失败。更新回滚/部分安装造成Version不等时，Recover抛错，而安装入口必须先Recover才允许Install，可能使真正的重新安装修复也无法进行。需要区分其它仍在运行的拥有者、当前拥有者的保留失败责任和已退出但结果不明的事务；为数据清理重试及重新安装修复提供实际可执行路径，不要求用户手删journal。
3. **native-starting的不明状态不能仅凭旧身份文件清除。** Install初始记录Version为空；如果native已启动但记录worker失败/拥有者崩溃，Recover只读到一个旧的合法release-source.json就可能Configure并Complete。该情况不能证明native已经结束或新安装成功。实际进程身份、阶段/结果和必要产物校验需支持安全恢复，尤其第一项记录失败路径不能留下可误放行的空隙。
4. **准备撤销与预期EOF不能依赖错误弹窗结束。** UpdateHandoff在整个Prepare持有Semaphore，Revoke仅等待该Semaphore，未先封口/取消Prepare；helper若提前退出或未连上，父端WaitForConnection只能依赖调用方token。反向取消通过关闭控制管道发送，但--handoff不带--silent，Program通用catch会显示MaintenanceUi.Failed，父端Revoke又在等helper退出，可能把普通撤销挂在用户未确认的模态窗上。需要拥有建立阶段任务/内部取消、观察对端退出并完整join；明确区分撤销/父退出与真正需要展示的安装故障。
5. **时间预算应属于阶段。** Program在任何交互之前启动统一5分钟CTS，该预算覆盖用户停留在安装/卸载对话框及后续恢复。用户决定时间不应消耗真实安装/退出准备预算；各阶段取消/失败须保留或清除相应事务责任，不在未发生破坏时留下无法继续的自有preparing记录。native已经启动后仍须等待真实结束，超时不授权强制推进。

后续还需在真实管道暂停读取、对端提前退出和主进程清理失败下验证commit/撤销交接；不能仅以WriteAsync返回或Process.Start返回替代对方实际接纳/完成。CloseApplications需符合原规范的同安装/同会话及真实退出/单实例边界，避免只凭一轮进程名字扫描忽略尚在启动或其它占有相关数据的实例。这些项属于既有B范围，当前不扩大产品目标。

第二项追加实证：`recovery-same-owner-before-1.log`直接调用固定候选的真实MaintenanceEngine/文件gate，仅替换系统进程、注册项及数据删除边界。模拟native已成功且第一次删除失败后，Recover仍抛IOException，NativeCalls=1、DeleteAttempts=1、BlockedBeforeRetry/AfterRetry均true，探针exit1；没有真正运行安装器/删除用户数据。观察后由探针清理其自有临时记录，该兜底不算产品恢复成功。此结果只证明同一拥有者重试问题，其余版本回滚/重新安装判断仍待验证。
