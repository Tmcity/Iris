# R4 开发期源码预审 1

2026-10-25 · 仅为已快照的中间源码意见，**没有运行下列反例**，不把意见当作最终交付失败。快照与 SHA 见 binding.json。仍在同一 R4 内处理。

1. **恢复会重新打开完整普通设置准入。** 当前 `AppLifetime` 在停止失败后调用 `presenter.ResumeUpdateRecovery()`，再 `ShowAsync("general")`。Presenter 把 `_isShuttingDown=false`，若旧会话已经释放，就通过正常 CreateScope 路径创建新的完整 SettingsViewModel。新 VM 的 `_isStopped=false`，`CanEdit`（SettingsViewModel.cs:365）不检查恢复模式或应用关闭状态，因此普通保存/编辑/服务测试等入口可能重新开放。仅给 Check/Download 加 `IsAcceptanceClosed` 不足以封住其它功能，也与“独立恢复界面、普通设置仍关闭”的已报告方案不符。

   请让已清理窗口后的恢复只接纳恢复所需动作。可以独立内部恢复界面；如果复用原窗口，须在创建 scope/VM 之前建立恢复专用准入，使普通编辑、提交、服务测试、模型查询、录入及导航等不启动新工作，不能只改按钮显示或在任务开始后依赖下游失败。接入真实 settings shutdown 的 Host 失败测试，验证原 VM 已释放后实际恢复入口可用，普通动作不写入、不发请求。

2. **恢复状态尚未绑定“用户已确认的安装意图”。** 当前 `StopAcceptance` 会把尚处 Ready 的 `_candidate` 也存入 `_lastCandidate`，`AppLifetime` 对任意停止失败无条件 `RollbackLastApply`。因此“已下载但从未点击安装”也可能被当成已确认更新恢复；旧候选还可能在后来的普通检查/退出后被恢复。当前 lifetime 没有明确保有用户接受的候选/代次。请把恢复责任与真实确认、handoff 责任转移及撤销关联，不能以任意 `_lastCandidate != null` 推断用户已同意安装。

   增加相应否定场景：只下载 Ready 后普通退出失败、取消已接受更新或产生新 offer 后普通退出失败，均不得自动准备/应用旧候选或错误宣称已经确认安装。恢复重试仍必须完整校验候选和 join helper，不重开普通网络/Host。

保留已修复的 Scoped 生命周期、普通更新网络封口与原始失败。完成后按 R4 验证真实交接准备/撤销、重复失败后成功和新包/来宾来源绑定；不扩展到 C/T-040。
