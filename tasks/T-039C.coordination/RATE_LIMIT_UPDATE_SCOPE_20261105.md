# T-039C：生产更新发现限流的实证修正范围

正式 0.2.7 的 CI、18 公开资产、97 载荷与来源及官方 API digest 已通过。用户最新实际反馈为“一直无法检查更新，提示请求过于频繁，请稍后再试”；这使此前的正向 offer 反馈不足以证明当前生产检查已完成，原反馈及其只读记录保持，更新/下载验收重新记为未通过。

只读采集显示仍为原 0.2.2 实例和数据，没有新版本包；产品日志记录连续检查操作的 IrisException。主会话匿名 API 也收到 403、limit=60、remaining=0；当前返回 reset 为 2026-11-05T07:22:08UTC（北京时间15:22:08）。公开 Release 下载路径已独立读回成功。限额恢复前保留原实例，避免重复检查；既有授权 v0.2.7 已发布，不重复推送。

实证定位：`UpdateRuntime.LatestGithubSource` 通过 Velopack GithubSource 查询官方 REST releases 接口，因共享匿名额度不足而在读取已公开 feed 前失败。`UpdateHttpDownloader` 将 403/429 正确映射为 RateLimited，但现有发现路径仍使公开更新受 REST 配额阻挡。

在隔离工作树 `F:/Iris/artifacts/t039c/worktree-v028-update-source-20261105-1` / 分支 `codex/t039c-api-free-updates`，以已发布源码 `fc44a3a` 为基线进行最小修正：优先从同一官方仓库的 latest release feed URL 读取；仅当该 feed 明确不存在时保留原兼容 release 选择；包下载固定在提供该版本的 tag URL；继续保留响应/文件所有权、取消、SHA/长度、full-only、来源域和版本约束。

不更改仓库目的地、公开契约/schema、依赖、用户保存的设置/秘密/历史或现有正式资产。受控修前回归使 REST 返回 403 而正式 latest feed 返回 200，先保留实际失败，再修正并覆盖官方 feed、不兼容最新 release、版本固定、取消及域/重定向拒绝。全量 Release/测试和新候选仍须重新完成；任何新的版本标签须在可审材料完成后单独由用户授权。

当前旧 0.2.2 仍使用原发现代码，新源码不会改变已安装实例。旧实例在实际 API 限额恢复后仍须由用户手工再次检查，成功后才下载/安装。真实生产升级/native退出顺序/一次正常重启/旧数据与卸载验收、C/父T-039和T-040/NFR均未结案。
