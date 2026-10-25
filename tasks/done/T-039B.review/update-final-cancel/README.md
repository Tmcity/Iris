# 资源清理阶段的迟到取消探针

## 已有实际执行记录补归档（2026-10-25）

2026-10-15 的实际编译、迟到取消和跨进程锁探针日志已从固定 artifacts 目录按字节补归档到 `executed-candidate-1/`。三条命令 exit 0；两个运行结果 `Passed=true`，全部自有任务/worker 已等待结束。原始命令时间与日志指纹保留在 `.command.json`。

归档时独立核对 9 个固定输入程序集均与开发 `review-final-candidate-1/App` 相同；12 个归档文本文件与原件逐字节一致，详见 `archive-verification.json`。没有在归档过程中重编译或重跑探针。准备阶段 `binding.json` 内的 `probeCompiledOrRun=false` 原样保留，其后的真实执行以命令/日志证明，不改写旧准备记录。

这些结果只绑定 2026-10-15 的历史候选。348 项源指纹与本日工作区已有 10 项差异，不能把旧结果写成 R3/R4 最终代码通过。下方是原准备说明；旧窗口未获授予、results 为空的记录仍不算执行证据。

## 原准备说明

此探针直接引用候选App程序集，使用真实AppUpdateCoordinator、UpdateOperation、ValidatedUpdateSource及Velopack UpdateManager；只替换发布源/资源释放和设置/HTTP外部边界。不启动窗口，不联网，不访问用户配置或生产IPC。

先让真实检查进入资源DisposeAsync门闩，此时才调用CancelAsync并暂停其实际令牌回调；释放资源门闩后，检查与取消拥有者仍应等待已启动但尚未结束的回调。探针最后始终放行并join全部已观察任务和回调，不把这个兜底记为产品成功。

编译/执行前仅为准备稿，结果以独立运行日志与候选绑定为准。它检查原source-review-1所要求的完整取消责任，不新增产品范围。

另有`--cross-process-lock`准备场景：自有子进程实际以FileShare.None持有本次临时目录的`.velopack_lock`，SDK子类只观察base.AcquireUpdateLock返回的真实任务，不替换其锁实现。取消时应同步失效，协调器仍等待实际锁操作；放行并join自有worker后，不得再进入源的下载。这个场景也尚未编译/运行，不作为跨进程争锁已通过的证据。

`prepare.py`只复制指定已构建程序集与探针到不存在的独立目录，生成离线NuGet.Config、临时项目和二进制指纹；不构建/启动产品。应在另行协调的串行窗口、稳定最终构建后运行，绑定源码和该构建，再编译/运行探针。使用现有run_review.py保存命令、所有失败与实际退出码；不得据生成项目或草稿结案。
