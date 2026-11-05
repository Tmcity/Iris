# 当前交接：最终0.2.8 CI失败，接续0.2.9

2026-11-05 最新：用户明确确认最终 0.2.8 后，只创建并正常推送 annotated v0.2.8→0c9b0a1；实际 CI 42015639513 attempt 1 在两项 host composition 测试中失败，publish skipped，没有正式 0.2.8 安装资产。原标签、ZIP/官网 digest、51 原件和失败源码完整保留，不重跑替换原结果。

新 0.2.9 source 0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2 已修正测试程序集 WPF 初始化，并将主题回调测试与其他共享 UI 阻塞夹具分开调度；冷启动反例、host 13 项、原主题超时、受控共享线程反例和主题 8 项通过均已留存。最终全量复验正在执行，候选/独立核对/归档与新授权仍待完成。正式前版仍为 0.2.7，0.2.9 未授权，分支推送和后继版本未授权；正式新资产通过后用户先手工安装修正版，再单独准备下一正式版的真实 UI 升级验收。

## 最终0.2.8发布前记录

最终执行材料提交：fed6cbf57e7a9a9995ade106d347b23c68a554bc；分支codex/t039c-public-release。最终审阅422525535cdc46fcc10a6533d0f88e2cac2a89e8，最终发布源码0c9b0a1881d07224597163b1e9b2f080ab60d434，规范树6928cec3575944b9154abbc129ac848ab943b872。

原002→027真实UI升级已达到Ready并正常退出，但native Update.exe因维护助手继承current目录为cwd导致共享冲突32，worker exit1，首轮失败已冻结。User正式027手工修复后，当前同一Sandbox2fc7c168-2f34-437a-8319-8974e6f592b2为027/PID8820，開始UTC09:31:12.4245570Z；97安装载荷、settings/secrets/history原三项哈希保持，V3/PID8152和合成服务/PID7328保持，无未完成transaction。

最终028包含API-free feed优先发现、版本tag固定下载、handoff/outside helper工作目录及helper cwd normalize修正。源0c9b0a1在artifacts/t039c/worktree-v028-update-source-20261105-1；最终完整validation-v028-20261105-2通过Release0/0、3771/3769成功/0失败/2原183跳过。candidate2的19资产/97载荷/721来源独立通过：Setup SHA a595455e28ebe036fb151110d73a1dc1def4e3c6d72a880467b349d77688de34，full SHA50a0723141c32034f9137bd0fb4d2832706c0a53c7a20f83ff28401fb56d5665。

execute_v028_final_20261105.py默认只读，已从最终review4225255和修复027基线成功预检；缺少授权的publish在创建任何目录/标签前退出2。不可使用旧execute_v028_20261105.py（对应source2b2和旧002基线）。首版028/candidate1/2cedde2/d1b594f与失败原件保留。最终028当前没有标签、没有正式资产、没有用户明确授权。

最新用户发布问题已发送：仅最终v0.2.8→0c9b0a1，不推main/codex、不授权下一版本；正式资产成功核对后用户先手工安装最终028建立已修正基线，再单独准备并确认下一递增版本，执行真正UI检查/下载/安装/native退出和一次正常重启/旧数据与翻译/卸载默认保留数据。等待用户明确选择确认发布最终0.2.8；此前对旧版问题的“问题依旧”不是发布确认。

正式027源码fc44a3a、tag51b6a3d3、CI41976966535/attempt1的3759全部通过，18正式资产及原CI/发布ZIP官网和API digest已确认，Release id434836973。远端main/codex均285df3a，所有旧标签保持。不得重复推送027或重跑其CI，不在宿主安装真实Iris，不读改workspace.codex，不读取SSH私钥，不新建聊天或子代理。

C/父T039、真正UI升级/一次正常重启/卸载及T040/NFR仍未结案；首次.NET安装MSI签名策略等待约8分钟问题已实证归档，M6延期。当前完整目标保持active，不能以候选/发布准备或手工修复当项目完成。
