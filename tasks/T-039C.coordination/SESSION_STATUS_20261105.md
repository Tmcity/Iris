补充完成：2026-11-05T06:27UTC官方REST API已恢复，run/jobs/artifacts/release均HTTP200；实际成功状态、两份CI/发布ZIP的官方API digest和18项公开资产API digest逐项匹配，Release id434836973。正式0.2.7分发复核完成，当前仍待user UI offer反馈，不重复请求版本授权或推送。

最新权威状态：正式v0.2.7已公开。实际CI41976966535/attempt1/package127077966898/publish127080878399成功；原XML3759全部成功0跳过，原验证ZIP506138字节/官网SHAf74d5800…，原发布ZIP423字节/SHA22e043ef…已冻结。正式匿名18资产/97载荷/721来源及官网每项SHA全部匹配；正式Setup116767767/SHA d294341700e1d9155567fadc56b9c3f1883217bd3cf0ce23102c94f57c83d570，full64627391/SHA f8f9de897b637f8c98a07d601420885bf65529701d014fc52b59f87ce75bace8。API元数据限额仍待resetUTC06:25:19后补。已部署公开027安装载荷核对材料，user UI offer question已发，当前待用户反馈；不要先下载/安装/卸载。保留新Sandbox2fc7c168…原App7312/V3 8152/fixture7328和三项old-ready哈希，真实升级/一次重启/卸载/数据及T040/NFR仍未结案。

最新实际状态：已授权的v0.2.7于2026-11-05T05:54:37UTC正常推送，annotated tag51b6a3d3c334ce604e9479948ef9f10fafd7d7c5→源码fc44a3a78c2729c25dfb91296c51602b843a94f9；main/codex和所有旧标签均保持。原匿名API限额失败receipt1保留，使用官网/正式下载验证的receipt2实际退出0。当前CI为https://github.com/Tmcity/Iris/actions/runs/41976966535，package job127077966898，官网实际显示In progress；API限额resetUTC06:25:19，届时再恢复API核对。v027正式新资产与真实UI升级尚未验证，tag页面200可能仅为annotated tag，不能当Release成功。用户新Sandbox2fc7c168…/原App7312、V3/8152、合成服务7328及old-ready保持，正式资产通过后才让用户分段手工更新。不要再次调用publish来重复本标签推送。

当前补充：用户已明确确认0.2.7，实际授权文件v027-user-approval-20261105-1.json。首次发布执行在任何标签创建前遇共享匿名REST限额403（reset UTC06:25:19），原publication-v0.2.7-20261105-1保持。官网页面/正式下载路径的完整预检已通过；正在以同一源码fc44a3a和同一审阅06b282b继续已授权的单一v0.2.7，不推分支，无需再次确认。详见CONTINUE_APPROVED_V027_20261105.md。

# T-039C 当前接续状态（2026-11-05）

唯一活跃工单仍为 T-039C。0.2.6 已得到用户明确授权，只创建并推送 annotated tag `dc8caf8b0c6501639c61dc26801595340386a4cb` → 源码 `c32fcb283e417ab11715c46207fdc8cfde4baedf`；main/codex 分支没有推送。实际 [CI 41663111425](https://github.com/Tmcity/Iris/actions/runs/41663111425) attempt 1 已失败，打包和发布跳过，正式版本仍只有 0.2.2。原标签、CI 和候选全部保留。

官方 API 已恢复读取，原 artifact `13244736238` / 506283 字节 / SHA256 `0eddaa74a4ecad4296c5c17a74ae1079394ec56a06ee892046ad68afd046bc7b` 独立匹配；51 份原文件、官方响应和公开网页已冻结至 `tasks/done/T-039C.evidence/ci-v026-original-41663111425-1`，发布 state.json 已同步失败。原全量 3758 / 3756 成功 / 1 失败 / 1 跳过；唯一失败为 UIA 夹具在 StartAsync 尚未返回时反射读取 null StartTask。

修复工作树：`F:/Iris/artifacts/t039c/worktree-v027-ci-fix-20261103-1`，分支 `codex/t039c-ci-v026-fixture`。受控同步 factory 门的修前反例 1/1 失败，修后 UIA 定向 16/16 成功。初版 0.2.7 源码 `1f4a0f393dc68cb5904828fe5f186af37cec6015` 的首轮全量又在热键夹具失败：线程池在原发送回调返回后复用相同线程编号。该原始 3759 / 3756 成功 / 1 失败 / 2 跳过及源码已冻结至 `v027-local-before-fix-20261105-1`。热键用例改由既有 WPF owner 线程发送，保留不内联与不同线程断言，增加实际线程池及未释放前不能 join 的检查；定向 1/1 成功。

当前固定源码 `fc44a3a78c2729c25dfb91296c51602b843a94f9`。完整 `local-validation-v027-20261105-1` 已实际退出 0：Release 0 警告 / 0 错误，3759 总计 / 3757 成功 / 0 失败 / 2 既有 error183 环境跳过，发布工具 16/16。唯一 `release-0.2.7-public-base-candidate-1` 已打包并独立核对721来源 / 18资产 / 97载荷与正式旧full，本地正常合并为e4951aa。正常审阅归档06b282bd928249519ea07b3eea999238f1202328已完成：407原始证据 / 11625584字节已匹配index和提交blob。默认只读执行器及缺少授权的拒绝反例、2026-11-05T05:30:53UTC新鲜预检均已通过；**0.2.7 未授权、未创建、未推送，待本版本明确用户确认**。

2026-11-05 的实际 `wsb.exe list --raw` 返回空环境列表。原 Sandbox `590f8848-f6b4-4bdb-a151-85c4a4f19305` 和 PID 6604 / 4604 不再作为当前活实例证据；原安装、数据及 V3 辅助验证仍保留。新材料已生成至 `artifacts/t039c/manual-verification-v022-20261105-1`，Windows PowerShell 5.1 对 11 个脚本解析成功。用户已实际重建新Sandbox2fc7c168-2f34-437a-8319-8974e6f592b2，确认安装正式0.2.2及更新开关false、合成翻译和历史。新来宾无运行时/缓存的原clean证据、正式Setup与97安装载荷、原App/PID7312/开始UTC05:05:27.9287450Z、V3/PID8152、虚构服务/PID7328、POST200匹配Key及SQLite旧历史均已独立核对；新安装及old-ready原件已分别冻结。首次安装约10min的反馈显示Microsoft.NET安装487秒，其中四次MSI签名策略等待合计481.262秒，Iris部署约1秒；具体证书网络原因及安装性能问题仍待处理，升级另行实测。

2026-11-05 远端 main/codex 仍为 `285df3a2986f3379994eb56dd8b75617db9f470d`，v0.2.6 仍指向原源码，v0.2.7 未占用。正式前版 full 仍为 `Iris-0.2.2-full.nupkg`，64627273 字节，SHA256 `c86982f930aafcd142167bbd871d03b588630f7d1d625da968a811daa2dd5d14`。

后续按次序完成独立来源 / 18 资产 / 97 载荷与旧正式 full 核对、正常本地源码合并、原件与 Git index 字节归档、新检查点；新旧来宾就绪证据复核后才请求用户仅 v0.2.7 标签授权，不推 main/codex。新版本实际 CI 成功且公开正式资产匿名读取验证通过之后，用户分段执行设置检查更新 → 下载 Ready → 确认安装/正常重启 → 新版本、旧历史和原服务新翻译 → 独立复核 → 手工卸载默认保留数据。实际退出/native 顺序、恰好一次正常重启、安装字节与数据/注册/快捷方式/共享运行时均待验证。

C/父 T-039 与 T-040/NFR 未结案；T-040 随 C 通过后推进，M6/T-033 延期。不改 `.codex/`，不读 SSH 私钥，不创建工程师聊天或子代理，不在宿主安装真实 Iris，不重写失败记录。

0.2.7执行器：tasks/T-039C.coordination/execute_v027_20261105.py；默认仅预检。固定ReviewArchiveCommit=06b282bd928249519ea07b3eea999238f1202328，Tag=v0.2.7，SourceCommit=fc44a3a78c2729c25dfb91296c51602b843a94f9。只有收到用户实际新确认并保存本版本QuestionItemId/Answer、仅标签范围的授权文件后才能publish。发布时再新鲜读取同一新Sandbox原实例/数据和公开refs；任何观察中断不能作为重复推送的依据。C/父T039与T040仍未结案。
