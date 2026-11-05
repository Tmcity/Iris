# 最终0.2.8执行准备

固定源码 `0c9b0a1881d07224597163b1e9b2f080ab60d434`，规范树 `6928cec3575944b9154abbc129ac848ab943b872`，审阅归档 `422525535cdc46fcc10a6533d0f88e2cac2a89e8`。原首版028源码2b2bbe7和候选1保持；最终候选2包含发现限流和维护工作目录两项生产修正。

最终完整Release零警告/错误，全量3771总计/3769成功/0失败/2既有跳过。候选2的721来源、19资产、97载荷、外层/feed/history独立通过；原真实升级失败、原工作目录边界失败、同一Sandbox辅助进程rename反例、正式027手工修复与旧数据保持原件均已归档。

`execute_v028_final_20261105.py` 默认只读。实际最终预检通过，固定审阅/source一致、原refs保持、028无标签/公开Release、正式前版027和候选19资产匹配；同一Sandbox已修复027/PID8820、V3/PID8152、服务/PID7328、原数据及开关false保持。缺少本版本授权的publish反例仍在任何发布目录或标签创建前退出2。

收到[最终检查点](F:/Iris/tasks/T-039C.coordination/PREPUBLICATION_REVIEW_V028_FINAL_20261105.md)的明确本版答复后，记录实际QuestionItemId/Answer、Tag/SourceCommit/ReviewArchiveCommit，BranchPushAuthorized=false、OtherVersionsAuthorized=false；再次实时预检后才仅创建和正常推送此一个最终028标签。此前“问题依旧”并未授权发布028，旧执行器execute_v028_20261105.py对应过时首版源，不再使用。

正式资产核对后由用户手工安装最终028建立已修正基线；之后再准备下一个递增正式版本，独立请求该版发布授权，用户执行真正的设置检查/下载Ready/确认安装/native退出和一次正常重启、旧数据与翻译、默认保留数据卸载。首轮002→027的失败和手工027修复不改记为成功UI升级。C/父T039和T040/NFR、首次运行时安装性能问题仍未结案。
