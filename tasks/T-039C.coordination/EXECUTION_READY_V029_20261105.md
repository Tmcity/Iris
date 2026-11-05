# 0.2.9 执行准备已通过

固定源码 `0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2`，规范树 `fcb432c3df3b5930bbc2900f43235384130b97ae`；审阅归档提交 `7662ed580eb924756885db7ab3a7d7a04e8ea8ea`。本地完整 Release 零警告/错误，全量 3771/3769 成功/0 失败/2 既有环境跳过；候选1的 19 资产/97 载荷/721 来源和正式前版 0.2.7/0.2.2 历史均独立核对通过。430 份原始证据共 10626856 字节与 Git index 完全匹配。

`execute_v029_20261105.py` 默认只读。实际预检 `v029-execution-preflight-20261105T122533411998Z` 已通过：固定 source/review 和来源保持、当前全部远端 refs 保持、029 无标签/公开 Release、正式前版027及匿名新读 full SHA/长度匹配、候选19资产匹配；同一 Sandbox 正式027/PID8820、V3/PID8152、服务/PID7328正常，原三项数据哈希/开关false/合成配置保持，无未完成事务。

缺少授权、传入旧最终028授权的两项实际 publish 反例均在创建任何发布目录或标签前退出2。当前没有0.2.9标签/发布目录、没有远端写入请求，028授权不能用于029。

0.2.8 原始 CI 失败与原 ZIP/源码保持。12:28 UTC 官方匿名 API 已恢复，run/jobs/artifacts 都 HTTP200，Release HTTP404；实际 source/attempt1/建测failure/publish skipped和原505370字节 ZIP 的官方 API digest全部匹配，补充绑定见 [原CI官方API补验](F:/Iris/tasks/done/T-039C.evidence/ci-v028-api-supplement-20261105-1/binding.json)。原403响应和初次“尚未补验”的历史绑定保持，未重写原件。

收到 [0.2.9检查点](F:/Iris/tasks/T-039C.coordination/PREPUBLICATION_REVIEW_V029_20261105.md) 的本版明确答复后，记录实际QuestionItemId/Answer、Tag/SourceCommit/ReviewArchiveCommit，BranchPushAuthorized=false、OtherVersionsAuthorized=false；重新实时预检后仅创建和正常推送 annotated v0.2.9→固定source。main/codex分支、旧标签、Release和原CI保持；后继版本另行确认。旧execute_v028_final仍绑定原失败source，不再执行发布。

正式029 CI和全部匿名公开资产通过后，用户先手工安装修正版，保留同一Sandbox/原资料；随后再准备并单独确认下一递增正式版，用真正UI检查/下载Ready/确认安装/native退出和一次正常重启/旧数据与新翻译/卸载默认保留资料完成C。首次002→027失败、027手工修复分别保留，未扩大成成功UI升级。C/父T039/T040/NFR及首次运行时安装性能、M6延期仍未结案。
