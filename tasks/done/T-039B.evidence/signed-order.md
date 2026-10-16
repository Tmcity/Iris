# T-039B：正式桌面打包、更新与维护入口

2026-10-13 · 架构师签发 · master · 基线 `6256a3fba611b2cd94fc8dfa32480584c16d7bfe`。

T-039A已由架构师恢复归档并独立验收：最终Release零警告错误，全量3660/3658成功/0失败/2既有环境跳过，固定源码/产物、独立IPC前后对照及SDK/13项bootstrap证据已绑定。见tasks/done/T-039A.md及T-039A.review/FINAL_REVIEW.md。不要重复A实现/测试修正；保存其全部原始失败。

用户已确认开发会话恢复，可继续接单。仍由本架构师签发/复审，开发只在既有会话`01a11b01-c46b-74e1-9015-726c77ffbc9e`实施，不创建新聊天/子代理。用户于2026-10-13明确改为“后续补充由你点击插队发送”：补充由架构师发送并点击引导，覆盖此前用户手动点击的安排；以开发实际接收为准，排队不冒称已读，不重复发送已有消息。

## 目标与唯一规范

完整实施 `specs/DESKTOP_RELEASE.md`（ARCH v1.57 / CCR-039）。本单交付真正可安装的Iris、可用的检查/下载/安装并重启、卸载保留/删除/取消，以及干净Windows隔离环境实证。父T-039的公开GitHub线上分发/升级闭环由C收尾，不能用B或A替代父单完成。M6仍延期，OCR仍复用翻译原文框。

正式源为公开的 `https://github.com/Tmcity/Iris`，PackageId Iris / channel win / RID win-x64，固定Velopack/vpk 1.2.161。主应用继续框架依赖，必须由真实安装链引导.NET10 Windows Desktop Runtime。新增无WPF的self-contained单文件Maintenance是独立安装/卸载入口，不能把主应用改为self-contained掩盖运行时验收。

## 必须交付

1. **实际打包**：PowerShell5.1兼容`build/pack.ps1`、版本化元数据、匹配vpk本地工具、独立App/UiaHost/Maintenance发布、正确uia/与卫星布局、完整文件/版本/SHA门禁。生成外层单一安装入口、真正full包/feed和两版本验证产物；修正csproj复制规则，不递归带入bin旧目录。原生Setup为内部载荷，外层不能在.NET10缺失时无法启动。
2. **生产更新与网络**：真实SDK适配及专用Updates命名客户端，现有代理/凭据/响应所有权保持。严格来源/重定向/预算和Full候选/缓存准入，明确区分尚无Release、不可达和最新；检查不下载、下载不安装。完整覆盖实际SDK工作/锁/流/校验/清理及取消回调，失败保留原对象重试，绝不WaitAsync遗弃实际操作。
3. **实际UI和联合设置**：General.CheckForUpdates编辑、版本/状态、手动检查、下载/取消、用户确认安装并重启全部可用；完整保存/冲突/readonly/外部合并/回执边界、zh-CN/en-US和明暗/小工作区可读。所有主动操作与窗口/退出拥有者接线，不放无作用按钮，不暗中保存草稿或自动重启。
4. **启动/退出/维护互斥**：严格fast hook与普通CLI分流、第二实例零SDK/配置/Host，普通主实例禁auto-apply并保留固定语言/Mutex线程亲和。维护封口覆盖交接窗口；只有真实清理成功且原进程退出后才运行native更新，不能提前启动其60秒倒计时。实现可撤销、可恢复的外部维护拥有者及真实结果验证，保持A的早断开/IPC所有权修正。
5. **完整安装与卸载**：Setup结束后才配置最终卸载入口，更新hook与外层结果核对保持正确顺序；保留/删除/取消在fast hook之外。静默默认保留，正常退出在文件卸载前完成。系统注册、自启动、标准用户数据/重解析点、保留后重装、删除失败可执行重试和辅助进程清理均须落实；不强杀用户实例，不删除共享运行时或便携数据。
6. **真实隔离验证与分发准备**：在自有干净Windows Sandbox/VM用实际Iris产物验证无桌面运行时安装、运行、UiaHost、安装不启动、卸载选择、静默、数据保留/删除和本地两版本升级。保留环境、用户权限、执行方式、截图/原始结果及未验证项。B准备GitHub Actions/用户文档/产物清单但不push或发布，C完成正式匿名源线上路径，不以本地结果冒称线上通过。

## 允许范围与约束

Core只同步CCR-039的可选CheckForUpdates属性及必要增量编辑处理；Providers只增加Updates常量/专用HTTP注册及必要复用接线，不变更既有LLM/OCR协议。App允许更新/设置/启动退出、资源、csproj发布规则和必要内部模型；Platform允许必要维护原生/路径/进程/注册边界，既有行为保持。新增Iris.Maintenance及其测试、solution项目、明确架构白名单、必要内部友元、集中Velopack版本和build/CI/安装文档。不得新增Core公共更新接口、改既有构造/解构、schema/default或无关包版本；其它契约变化先交架构师。

既有规范NETWORK_CONFIGURATION、IPC_COMMAND_LINE、LOCALIZATION与GEMINI继续有效。所有门闩从取得资源起受finally保护，先取消/释放、完整等待真实body/IO后再最终清理；保留主错误与清理错误。未经验证的候选/原型不得标记通过。

自动测试禁止真实联网、生产管道、宿主用户设置/密钥/Run/选区/剪贴板或性能脚本。可创建自有随机IPC、临时文件及受控边界；真实安装/更新/卸载仅在本单自有隔离Windows环境内。允许下载固定官方依赖、微软运行时、只读公开仓库资料；不读取SSH私钥、不修改宿主代理/证书/系统安装。Sandbox只共享本单专用产物/输出目录，按规范结束自有实例，不操作其它实例。

`.codex/`为用户工具，原样保留，不改删、不提交。保持完整git历史，不push/reset/clean/rebase/amend。BACKLOG由架构师更新。本单包含全部上述生产交付与验收，不能自行拆掉困难项或提前结案父T-039；工程决策直接反馈架构师，不向用户重复索要已授权范围。

## 收尾

2026-10-15串行安排：Maintenance固定候选的两条独立重放均已通过，旧复验窗口已归还，可以继续构建和测试。架构师当前没有在运行的构建/测试，后续独立验证另行短时协调；本单其余完整交付仍未验收。

与架构师串行构建/测试，完成前执行`dotnet build Iris.slnx -c Release`零警告和`dotnet test Iris.slnx -c Release`完整通过。归档工单全文及真实报告到`tasks/done/T-039B.md`，证据到`T-039B.evidence`，二进制/工具保存在artifacts，所有失败/跳过/指纹/实际VM方式保留。当前架构师规范与工单随交付保存，移除current_task后创建独立本地提交，回报完整hash请求复审；不得自行开始C/T-040或重新开启M6。

架构师已另行用最小自有探针验证.NET10 self-contained单文件附加载荷：干净离线Win11中正常/损坏/越界三个场景与真实退出码通过，前后无共享.NET10，见T-039B.review/installer-envelope。未运行真实Iris/安装器，不能替代本单产品验收。保留其失败历史；单文件运行时不一定以单独coreclr.dll模块被枚举，不能把该假设当作自包含检测。来宾脚本如需要可靠退出码，可参考直接持有Process/句柄并完整等待的最终脚本，仍只操作自有环境。

执行期源码预审已归档T-039B.review/source-review-1：优先闭合通知抛错导致未启动Entry/丢失真实Run任务、取消的同步封口及旧_stop/新Entry竞争、PrepareApply校验的操作所有权与原子交接；同时核对创建失败资源登记、下载异常清理、404/兼容feed分类和维护锁文件路径。均是本单规范的具体应用，不新增用户范围/公共契约，也未冒称已运行失败。请用实际类型和门闩验证，保留原源码/失败，完成后再作完整交付。

维护预审见T-039B.review/source-review-2。其第一项已独立执行：真实MaintenanceSystem.RunAsync在started事务记录回调抛错后，任务已失败返回而自有worker仍活着；探针已放行并join，不曾运行安装器。需从Process.Start成功即保有实际子进程，记录回调失败仍完整等待。其它关于当前helper自身失败重试被IsAlive拒绝、native-starting不明状态的恢复、准备撤销/EOF误弹模态框、统一5分钟覆盖用户交互预算等为源码预审；分别验证后闭环，不把推导冒称实测。保持原B范围及全部失败证据。

同一helper重试问题随后也已实际复现：recovery-same-owner-before-1使用真实维护引擎/文件gate、fake外部系统与删除，首次数据清理失败后恢复仍IOException，DeleteAttempts=1且事务保持阻止启动。临时状态由探针回收，未执行真实安装器/用户数据删除。以source-review-2新增实证段落为准，余下恢复/撤销项仍按源码预审分别验证。

2026-10-15后续源码意见见T-039B.review/source-review-3：检查“用户选择删除但native卸载本身失败”时Recover要求安装修复、Install又要求先清数据的循环；以及Program对所有handoff错误静默退出会掩盖真正提交后的更新失败。两者是R2既有恢复/错误区分要求的具体分支，尚未独立执行，不新增范围或暂停构建。前述两个原失败已在固定bda86bce候选独立重放通过，见native-child-ownership/README.md，不能据此结案这些不同分支或整个B。

真实来宾脚本的判定意见见T-039B.review/source-review-4：关键布尔观察false/窗口未出现不能仅记录后仍exit0，卸载须等本次外部Maintenance及native拥有关系达到必要终态后再宣告通过/结束Sandbox。夹具超时终止须与产品完成分开，原始结果保留，新脚本用新label。原B完整场景矩阵保持；不要求暂停当前构建测试。

架构师已独立核对guest-install-5的严格静默保留结果与matrix-4的功能结果；完整范围仍以本单为准。收尾覆盖说明见T-039B.review/final-coverage-notes.md：matrix-4的zh-delete-completed原图只有标题栏，须补稳定画面；实际PowerShell5.1完整打包、数据内容/快捷方式/自启动所有权、验证宿主边界和最终源码产物绑定按原单补齐并如实报告，不暂停当前实现。

实际PS5.1的final-2两版产物已独立检查，见T-039B.review/package-final-2-observation：顶层哈希和外层载荷哈希匹配，但payload-manifest把102项staging当作实际载荷，包内97项（排除8个PDB，新增3个vpk文件）。请按实际包内容生成准确载荷清单，必要时另存发布输入清单；旧产物/记录保留，最终清单和校验重新绑定。

上述清单偏差已在final-3两版产物中独立复核关闭：实际97项载荷集合、逐项大小/SHA、顶层文件与外层附加载荷均匹配，staging另名保留，见package-final-3-observation。无需重复该项返工；最终来源绑定与全部运行验收仍按本单完成。
