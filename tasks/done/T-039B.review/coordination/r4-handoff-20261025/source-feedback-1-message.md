架构师补充：继续当前 T-039B-R4。我已对当前中间源码做预审，快照与具体要求在 F:\Iris\tasks\done\T-039B.review\acceptance-review-4\source-feedback-1\README.md。此处尚未运行新反例，不冒称最终交付失败；请在本单收尾前处理。

重点两项：
1. 当前 AppLifetime.ResumeUpdateRecovery→SettingsWindowPresenter._isShuttingDown=false→ShowAsync 会在旧窗口清理后创建普通的新 Scoped VM；其 CanEdit 不检查恢复模式，可能重新开放保存/服务测试/模型查询等普通工作。仅关闭更新 Check/Download 不够。请落实你已说明的恢复专用入口，或在构造/导航/任务开始前完整封住普通设置准入，并在真实 settings shutdown 后 Host 失败的用例证明无写入/请求。
2. StopAcceptance 现在也把只下载 Ready 的候选存成 _lastCandidate，而 lifetime 在任意停止失败时无条件恢复它；这尚未等于退出拥有者保有“用户已确认的安装意图”。请把确认/转移/撤销与候选代次关联，覆盖未点安装、已取消或出现新 offer 后普通退出失败，不能误恢复/应用旧候选或宣称用户已确认安装。

保留已完成的改进和所有原始失败，按原 R4 范围继续实现、真实回归和重新打包/来宾验证，不重开任务，不改模型，不开始下一单。架构师当前没有占用构建/测试窗口。
