# T-039C：0.2.7 发布检查点

用户授权的 0.2.6 已仅推送原 annotated tag `dc8caf8b0c6501639c61dc26801595340386a4cb` → `c32fcb283e417ab11715c46207fdc8cfde4baedf`。[实际 CI 41663111425](https://github.com/Tmcity/Iris/actions/runs/41663111425) attempt 1 建测失败，打包及发布跳过，原标签、CI 和候选保留。正式版本仍只有 [0.2.2](https://github.com/Tmcity/Iris/releases/tag/v0.2.2)。

官方 artifact `13244736238` 的 506283 字节 ZIP 已独立匹配官方 API digest `sha256:0eddaa74a4ecad4296c5c17a74ae1079394ec56a06ee892046ad68afd046bc7b`；51 份原文件、公开网页、官方 API 响应、修前源码与发布失败 receipt 均已归档。原全量 3758 / 3756 成功 / 1 失败 / 1 跳过。唯一失败为 UIA 夹具在 StartAsync 同步 factory/rollback 尚未返回时反射读取 null StartTask。

## 修正及独立验证

受控 factory 门复现相同反射错误，修后观察器等待实际 StartTask 发布，再等待 reader 的直接 late-start await；真实 stderr drain、Stop/read 不能提前完成、取消结果及一次 dispose 的断言全部保留，UIA 定向 16/16 成功。

初版 0.2.7 源码 `1f4a0f393dc68cb5904828fe5f186af37cec6015` 的首次完整本地验证另有热键夹具失败：线程池在发送回调返回后复用了测试发送线程的编号。原 3759 / 3756 成功 / 1 失败 / 2 跳过、原日志/XML 和源码均保留。修后的热键测试由既有 WPF owner 线程发送，检查发送栈不内联、不同线程、实际线程池及未释放前不能 join；定向 1/1 成功。生产源码、契约/schema、依赖与完整测试选择没有改变。

| 项目 | 核对结果 |
|---|---|
| 发布源码 | `fc44a3a78c2729c25dfb91296c51602b843a94f9` |
| 规范来源树 | `81aea2fa6cad8a3dcde34b081185dd03aae9c31c`，独立按 Git tree 原始字节计算匹配 |
| 来源范围 | 721 项提交 blob 与实际工作树字节匹配；两项测试文件、release-plan 和 RELEASING 变化，生产源码无变化 |
| 完整验证 | Release 0 警告 / 0 错误；3759 总计 / 3757 成功 / 0 失败 / 2 既有 error183 环境跳过；发布工具 16/16 |
| 唯一候选 | `F:/Iris/artifacts/t039c/release-0.2.7-public-base-candidate-1` |
| 资产与载荷 | 18 项资产、97 项实际 full 载荷，feed/版本/外层/provenance 与原始验证绑定匹配 |
| 本地候选 Setup | 116769834 字节；SHA256 `f66cc74886ea273b439ce241993f11d95ad5882be477a0464d043057c082d294` |
| 本地候选 full | 64629076 字节；SHA256 `ea4aff9842f8e1155c69f920e108e126f6c9a8409409c8d68827cc16ad5e68ed` |
| 正式前版 full | 0.2.2；64627273 字节；SHA256 `c86982f930aafcd142167bbd871d03b588630f7d1d625da968a811daa2dd5d14`，与新鲜官方 API digest 及候选中的旧包匹配 |
| 本地正常整合 | `e4951aa` 合并固定发布源码，两个源码提交与既有历史全部保持 |

[独立候选审阅](F:/Iris/tasks/done/T-039C.evidence/next-source-v027-20261105-1/review.json)与全部原始验证、反例、打包、源码记录已准备。辅助审阅工具最初有日志路径拼接错误，修正后完整复核通过；原工具输出及源码版本保留。Git index 的原始证据字节将在正常归档提交前再次核对。

固定 vpk 1.2.161；App/UiaHost FDD、Maintenance SC，未签名；full 应用，delta 只生成。CI 会生成自己的字节，本地候选 SHA 不充当正式公开资产 SHA。

## 当前手工旧版基线

2026-11-05 查询发现原 Sandbox 已不存在，历史安装、数据、记录器证据完整保留。用户已在新 Sandbox `2fc7c168-2f34-437a-8319-8974e6f592b2` 重新手工安装正式 0.2.2，并明确确认关闭启动检查已保存、合成翻译成功、历史可打开。

新来宾初始无 .NET 10、无运行时缓存；正式 Setup SHA 与 97/97 安装载荷匹配。当前原 Iris.App/PID 7312，开始 UTC `2026-11-05T05:05:27.9287450Z`；V3/PID 8152 和虚构服务/PID 7328 在实际只读查询中保持运行。`CheckForUpdates=false`、启用的当前合成 OpenAI 兼容服务、虚构 Key 的真实 POST200 及 SQLite 中旧文本/结果已独立复核。V3 的辅助进程验证不能代替实际升级的 native 顺序和一次正常重启。

| 基线文件 | SHA256 |
|---|---|
| settings.json | `84d85182866599005f6802db616fb89094d6c19dd1d7ade8ca15db9fa11d30e8` |
| secrets.dat | `b977aecf397ecc9dc57f2301181a20372f7e7bc37b468091cd4fb1764e97f9d7` |
| history.db | `c4960f0964e6d1ad267f6e735da6ddaea70e4b7ef63bb4d22a0dd76c6e67a497` |

秘密内容未读取或采集。[新安装证据](F:/Iris/tasks/done/T-039C.evidence/manual-first-install-v022-20261105-1/binding.json)和[升级前基线](F:/Iris/tasks/done/T-039C.evidence/manual-old-ready-before-v027-20261105-1/binding.json)已冻结。发布前须重新读取实际进程、心跳、数据和匿名公开状态。

用户反馈首次安装约 10 分钟。原日志显示 .NET MSI 运行时安装 487 秒，其中四次软件限制策略数字签名核验等待合计 481.262 秒，Iris 部署日志窗口约 1 秒。CAPI2 详细日志未启用，具体证书网络失败尚未证明；[耗时记录](F:/Iris/tasks/T-039C.coordination/INSTALL_PERFORMANCE_V022_20261105.md)保留为待处理性能问题，生产升级另行记录耗时。

## 待用户确认的发布范围

本地审阅归档固定并完成最终只读预检后，仅创建 annotated `v0.2.7` 指向 `fc44a3a78c2729c25dfb91296c51602b843a94f9`，正常推送此一个标签，由现有 Actions 发布新版本。main/codex 远端保持 `285df3a2986f3379994eb56dd8b75617db9f470d`；保留所有旧标签、CI、候选与历史；本次没有分支推送范围。

用户此前仅授权至 0.2.6，0.2.7 须单独明确确认。收到确认后才执行该标签发布，并跟踪实际 CI；成功且正式公开资产匿名回读核对通过后，按[生产 UI 分段清单](F:/Iris/tasks/T-039C.coordination/MANUAL_UI_UPGRADE_V027_20261105.md)由用户手工完成检查更新、下载、确认安装、正常重启与旧数据验证，升级独立通过后再卸载默认保留数据。

C/父 T-039、实际退出/native 顺序、恰好一次正常重启、升级和卸载数据保留、T-040/NFR 尚未结案，M6 延期。0.2.7 当前没有授权、标签或公开资产。
