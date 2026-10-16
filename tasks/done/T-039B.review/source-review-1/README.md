# 更新底座源码预审

2026-10-13。位置和指纹绑定当前候选源码。本记录是静态路径审查，**尚不是独立执行失败**；开发仍在实现，不把缺少后续模块当成已交付缺陷。

1. **通知异常可留下未启动的操作。** AppUpdateCoordinator.Begin先登记_entry，再直接Notify，之后才调用RunAsync；Changed任何订阅者抛错，Run未启动而Entry.Done永远无人完成，Stop会等待它。Run的finally又先Done.TrySetResult再Notify，实际Run Task被丢弃，通知异常/未完工作没有同一完成责任。需隔离通知订阅者错误、先登记实际任务/保证启动和收尾，并让Stop等待真实任务及必要回调完成；用抛异常/重入订阅者作确定性验证。
2. **取消的接受点与轮次归属。** CancelAsync仅改generation/状态，实际Entry.Cancel在CancelCoreAsync的ForceYield之后；StopAcceptance也未立即取消，因此令牌在入口返回时仍可未取消，SDK/传输的后续I/O可能进入。另一个窗口是旧_entry已清除而旧_stop尚未完成时Begin可接纳新Entry，随后CancelAsync却复用旧_stop；旧CancelCore还可能把新Checking快照改成Idle。须在同步封口点请求非阻塞取消，限制或按身份正确处理新准入/旧_stop复用，验证取消后无新I/O及旧停止不能完成新操作的责任。
3. **PrepareApply的真实校验未被协调器拥有。** PrepareApplyAsync在_entry之外异步读取/哈希包，只观察调用方token；StopAsync/CloseOwnerAsync无法等待它，且多个Prepare可并发消费同一候选。将准备阶段纳入实际操作/身份/取消/清理责任，或由明确拥有者提供等价原子转移与停止等待。不能只在最终返回前比较_candidate；测试要在实际校验暂停时关闭owner/Stop，证明仍等待，且至多一次交接。正常结束后的迟到进度也不能访问已Dispose的CTS.Token属性而抛错。
4. **资源取得到登记之间的失败。** UpdateRuntime.Create先取得HttpClient/downloader，再创建/验证locator；后续抛错时这些对象尚未交给Entry.Operation。调整顺序或提供可重试的创建失败清理。下载写入失败时也应确保本次文件/响应在进入SDK后续清理前尽力完整收尾，保留真正Dispose失败的原件；不能因SDK的清理先遇到打开的partial而把残留文件当作已清干净。此项需实际取消/写入/Dispose故障门闩验证。
5. **发布源状态与兼容选择。** OpenAsync把所有404都变为NoCompatibleRelease，包括仓库/资产不可达和下载包丢失；它们应与200但无兼容版本区分。LatestGithubSource在确认存在本channel feed前就Take(1)，最新发布不带feed时会忽略较早的兼容版本；应先按真实兼容资产选择并避免重复合并历史。保留坏清单的显式失败，不降格为已是最新。

额外核对维护文件边界：IsBlocked的OpenOrCreate会在查询时产生lock文件，且目前只检查stateRoot而未检查transaction.lock本身是否是重解析点。主/次实例准入查询及真正Acquire都应满足规范的最小副作用、路径检查和原锁所有权；真实恢复/删除逻辑完成后再整体复核，当前不猜测其尚未实现的行为。

以上均落在DESKTOP_RELEASE.md既有范围内，不要求新增Core端口或另开工单。保留原候选/失败后，以实际产品类型和受控外部边界补齐验证。
