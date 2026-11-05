# T-039C 与父 T-039 完成性审计

2026-11-06，依据固定发布源码227b3d88938543766a27d86a94ade52f9e5b89b7、正式公开资产、原始CI及用户实际Sandbox记录逐项复审。C的正式分发、默认运行时安装、真实生产UI更新和默认保留资料卸载链路通过；父T-039的剩余用户路径闭合。完整项目目标仍未完成：T-040全功能/NFR和缺陷复核继续，M6/T-033按用户指令延期。

| 必须完成的要求 | 权威证据与核对结论 |
|---|---|
| 正常历史整合、已审分支/tag、原失败保留 | 原main/codex285df3a及所有原tag保持；用户逐版授权，v0.2.9→0b3267f、v0.2.10→227b3d8仅正常推单一annotated tag。初始GitHub README归档及合并双父历史保留；旧0.2.0/1/3/4/5/6/8失败source与CI均未改写或重跑替代。见publication-v0.2.9/0.2.10的before/after refs、审批与真实push日志。 |
| 两递增正式版本与实际完成CI | 0.2.9 CI42028799541/attempt1为3771/3770成功/1既有183跳过；0.2.10 CI42042550153/attempt1为3771全部成功。两份原验证/发布ZIP与官网和API完整digest匹配；各自发布回执published-and-listed/Public=true。 |
| 实际正式full/feed/外层、资产与来源 | 029的19资产和0210的20资产全部匿名下载、每项长度/SHA与官网/API相符；各97实际载荷、721Git来源blob、规范树与外层prefix/身份/运行时验证通过。0210 SourceTree69d4faf228d9d0b98d77ecb4a8dac582ad33421b，Setup116769024/SHA56166ec3e71a0fad72b1eb135e119635e63720ebe0a8fcf301f58d7ef8e053c7，full64628628/SHAdca307a0afc1827e27bc7ec2614e5ead3a30978d4c5d318e4d0d842ebc73f136。首次RELEASES504/92字节原件保留，单项重读后新目录2完成全部验证。 |
| 固定PackageId/channel/RID/SDK/FDD与最小发布权限 | 当前pack/pipeline/workflow与来源清单保持Iris/win/win-x64、vpk1.2.161；App/UiaHost FDD、Maintenance/外层SC。构建job仅contents:read，发布job用临时workflow token且仅contents:write；SSH私钥未读取/复制至产品或来宾。源/测试/schema/依赖没有0210发布计划之外变更。 |
| 正式旧版在无.NET/无缓存干净来宾默认安装 | manual-first-install-v022-20261105-1中当前自有Sandbox2fc7c168… clean记录Runtime=[]、cache=false、没有Iris/数据/包。正式022外层116767621/SHA ddae984ba4bcc65e5398ecd7a036afe0485966c5d61fe9b1603cc9bb487e7a0c实际下载，标准默认链安装Desktop/Core10.0.14，最终97载荷、7312进程与V3指纹匹配。没有预填运行时、手动安装.NET或关闭TLS/证书。默认native失败与后续生产下载修正原件保留。 |
| 合成设置/秘密/历史/日志，以及真实旧基线 | 当前用户手工022→修复027→固定029，旧settings/secrets哈希和原history row1保持。029新POST13:34:01/HTTP200/虚构凭据匹配产生row2，简中版本图与97正式文件匹配；actual5672实例和开关false经只读新采核对。初次缺新请求的失败未改成通过，用户补做与新记录后才完整验收。 |
| 真正设置UI检查不下载 | 用户点击生产029检查更新，实际截图显示029、有可用更新0210、下载可用/安装禁用；同一5672/原数据保持，没有0210包。ui-offer-v0210-20261105-1含实际图、原快照/两条SQLite行及原件SHA。未调用内部协调器/fake源/GuestUpdateHost。 |
| 真正下载、Ready不安装 | 用户点击生产UI下载，实际Ready图显示更新已验证0210/安装可用，当前仍029/5672。缓存full64628628/SHAdca307…与正式包相符；原数据哈希和两条历史保持。ui-ready-v0210-20261105-1含实际图、正式包只读校验、V3Ready与数据。 |
| 明确确认后正常退出、原生应用、一次正常重启 | 用户点击安装并重启。旧5672的App日志23:53:26.958正常exit0；native5516在23:53:27开始apply，成功备份/替换current、验证/退出0。6616 obsolete和7456 updated短进程通过SDKhook日志分类，均exit0；不计正常重启。仅新8980正常实例于15:53:28.2094930Z创建、完成bootstrap，当前图/97正式文件显示0210。V3原始事件及交付时间完整保留，未误用滞后TIME_CREATED代替精确进程开始。ui-upgrade-v0210-20261106-1全部交叉核对。 |
| 更新后资料可读与原服务新翻译 | settings/secrets与旧基线相同；SQLite old row1/2逐字段保持，新“Iris UI upgrade 0210”row3和15:54:52新POST200/虚构凭据匹配。日志原前缀保留。DB哈希变化对应新增历史，未伪造文件不变。版本简中图已实际查看，用户反馈所有要求完成且无问题。 |
| 更新后注册入口与原生默认保留资料卸载 | 注册命令指向current/maintenance/Iris.Maintenance.exe --uninstall，显示0210。用户系统设置卸载、选默认Yes保留；原8980 exit0，native8280 --uninstall --silent exit0、SDK卸载hook8384成功，安装目录/程序/快捷方式/卸载项实际移除，系统Iris搜索空图已查看。ui-retained-uninstall-v0210-20261106-1。 |
| 卸载终态、共享运行时、四类资料、自启动 | 新只读检查无产品/更新/维护进程，无transaction.json，HKCU Run的Iris值实际不存在。APPDATA/Iris及settings/secrets/history文件哈希与升级后相同，三条历史完整、quick_check=ok、两份日志保持；Core/Desktop10.0.14仍安装。用户明确反馈卸载成功、保留资料、列表消失。 |
| 辅助工作/恢复文件与原失败的生命周期 | Iris安装目录与VelopackTemp事务工作已清理，持久事务解除。外部Iris.Maintenance仍有受界限约束的recovery/最后自有执行副本、0字节lock及历史last-failure。工作marker与固定命名限定所属，MaintenanceWorkDirectory.Clean只在owner结束后/下一次维护清理，最多32个；真实卸载开始后已清理前次9bf副本，保留当前ce7副本。此为已验维护入口复制契约的可追踪保留，不是仍安装的Iris或未完成native事务，未强删正在执行自身文件。历史last-failure指向08:48/PID4936，不能误当成功0210升级的新失败。 |
| 原始数据与归档保真 | 对9个必需证据集的236份原件再次计算长度/SHA全部匹配绑定。所有本轮原件以-text保存，提交前与Git blob逐字节核对；只明确归档本单文件，未触碰.codex/私钥或其它应用。最后卸载原件将在结案提交纳入。 |

已明确的实际边界：Windows Sandbox为Windows11 10.0.26100，单x64正式目标；不扩大成所有Windows10/设备实测。包未签名；delta生成且在feed中保留，但生产客户端应用的是full，未声明delta应用成功。失败重试/取消/删除选择等其它维护规范已在A/B的固定产品和真实来宾证据中独立验收，本次C补充实际匿名用户链路。

冷安装约10分钟的记录真实：标准微软MSI Apply约487秒，其中四次签名策略检查合计481.262秒，Iris文件部署日志窗口约1秒。当前微软证书下载均成功的诊断不足以证明原延迟原因，不能改写系统信任策略或声称性能已达标；该实际性能/环境调查纳入T-040。冷安装成功证明T-039功能路径，时间目标是否合格另按ARCH§1.2和T-040完整测量。

历史窗口在旧029阶段有4次InvalidOperationException警告，已保留用户“刷新/复制、具体不确定”原文线索；0210重启后已采日志没有新History操作警告，用户按清单复验后报告无问题。T-040仍须明确刷新/复制/重译/取消等全面复验，不能从C的三个合成历史扩大成全功能通过。

审计结论：T-039C及父T-039所缺正式分发、无缓存默认安装、真实UI full升级/一次正常重启与默认保留资料卸载均已证明。项目的全部目标未完成，下一唯一工单为T-040；不以本结论标记goal complete。
