# 0.2.7 执行准备（等待本版本用户确认）

发布源码固定为 `fc44a3a78c2729c25dfb91296c51602b843a94f9`，规范来源树 `81aea2fa6cad8a3dcde34b081185dd03aae9c31c`；正常本地审阅归档提交为 `06b282bd928249519ea07b3eea999238f1202328`。407 份原始证据、共 11625584 字节已逐项匹配 Git index，并通过 `git cat-file --batch` 再次匹配该提交的实际 blob 字节。源码、建测、候选、失败原件和新来宾基线均保持。

`execute_v027_20261105.py` 默认只读预检。2026-11-05T05:30:53UTC 实际预检退出 0：原 main/codex 和七个旧标签保持；0.2.7 本地/远端标签及 Release 均不存在；正式 0.2.2 可匿名读取；候选 18 项实际资产字节与已归档审阅匹配；同一新 Sandbox 原 App/PID 7312、V3/PID 8152、合成服务/PID 7328、开关 false 和原三项数据哈希保持。

显式 publish 未提供本版本授权文件的反例实际退出 2，发生于发布目录/标签创建之前。0.2.7 当前没有用户发布授权，没有新标签、远端写请求或发布目录。[执行准备原件](F:/Iris/tasks/done/T-039C.evidence/v027-execution-readiness-20261105-1/binding.json)包含完整预检、原命令、源字节和拒绝记录。

收到用户对[0.2.7 检查点](F:/Iris/tasks/T-039C.coordination/PREPUBLICATION_REVIEW_V027_20261105.md)的新明确答复后，记录实际 question item 与 Answer，固定 Tag/SourceCommit/ReviewArchiveCommit，BranchPushAuthorized=false、OtherVersionsAuthorized=false；执行器再次读取当前状态后，才创建并正常推送这一个 annotated 标签。没有 main/codex 推送、重写旧标签、重跑旧 CI 或覆盖资产的范围。

实际推送进程会保持可观察；等待过期或观察中断不能作为重复推送的依据。标签已推送不等于发布成功，后续须跟踪最终 CI/Release 以及匿名正式资产字节，之后才由用户分段执行生产 UI 升级和卸载。新来宾及数据继续保留。

首次安装的 .NET MSI 签名策略长等待保持性能问题记录，升级耗时另行实测。C/父 T-039、真实退出/native 顺序、一次正常重启、数据/卸载验收和 T-040/NFR 尚未结案；M6 延期。
