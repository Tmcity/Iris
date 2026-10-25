# T-039B 开发交付报告（请求独立复审）

基线：`6256a3fba611b2cd94fc8dfa32480584c16d7bfe`。本单交付生产实现、实际包及本地隔离验证；没有 push 或公开发布。父 T-039 与后续 T-039C 由架构师结案/签发，M6 未推进。

## 实现

- Core 仅落实已批准的 CheckForUpdates 可选 init 字段及联合编辑复制/验证/合并/空计划。原构造/解构、UiLanguage、默认/schema 保持。窗口真实保存、readonly、外部合并、冲突、Reload/取消和 RetryApply 均有实际 VM/临时 JSON 验证。
- Updates 命名客户端复用网络快照与传输拥有者；实际 SDK source/downloader 限制 GitHub 来源、重定向、元数据/包预算和完整包准入。操作任务在通知前登记；取消同步封口；保存真实任务及最后通知；校验/Prepare 纳入同一拥有者。资源失败保留原对象重试，取消回调失败不遗弃 SDK/资源。
- 更新 UI 提供版本、偏好、状态及检查/下载/取消/安装并重启。自动检查独立于手动操作，关闭偏好仅取消自动检查；安装不隐式保存草稿。无关窗口不承担自动检查的清理失败。
- STA Main 保持原 Mutex 线程释放；fast hook/普通 CLI/主次实例分流；主实例禁 SDK 默认 auto-apply。外部维护拥有者握手、提交确认、维护门禁及实际原进程退出后才运行 native apply。
- Maintenance 为无 WPF/WinForms 的 self-contained 单文件；App/UiaHost 保持 FDD。安装结束后修复最终卸载项；普通卸载保留/删除/取消，静默保留；优雅退出、数据路径检查、同一 helper 失败重试和可恢复事务均已接线。
- 修正真实来宾暴露的后台线程销毁 WPF 托盘问题：同步 UI 清理回到原 Dispatcher；Host 停止失败不先移除重试入口。主错误与撤销清理错误分别保留。
- `build/pack.ps1` 分别发布/组合 App、uia、Maintenance；固定 SDK/vpk、检查版本和卫星资源；外层附加有界长度/SHA 的内部 Setup。`staging-manifest.json` 保留发布输入，`payload-manifest.json` 从实际 full 包生成。CI/安装文档已准备，未执行发布。

固定包声明（原文）：

```xml
<PackageVersion Include="Velopack" Version="1.2.161" />
```

## 串行 Release / 全量

原日志：`T-039B.evidence/final-release-1.log`、`final-full-1.log`。产品源码在此后未改动；后续修改仅为证据夹具/报告。

```text
已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:09.83
```

```text
总计: 3704
  失败: 0
  成功: 3702
  已跳过: 2
  持续时间: 1m 59s 852ms
```

两个跳过均为既有隔离 Window station 环境限制，原输出保留。没有跳过新增失败。定向原始日志还包括 `update-owner-close-1.log`、`updates-proxy-1.log`、`maintenance-final-candidate-1.log`、`control-pipe-1.log`、`update-ui-2.log`。

## 实际包与来源绑定

最终验证包目录：`artifacts/t039b/release-0.2.0-final-3`、`release-0.2.1-final-3`。明确使用系统 Windows PowerShell 5.1 的日志为 `pack-ps51-final-0.2.0-3.log` / `pack-ps51-final-0.2.1-3.log`。包装器记录实际 PSVersion/PSEdition/ProcessPath，临时目录仅对本次进程与子进程指向 F 盘。

`package-final-3-check.json` 核对两版实际载荷集合/大小/SHA 和顶层哈希；架构师 `T-039B.review/package-final-3-observation` 也独立关闭了旧清单偏差。vpk 的 Run-in-helper 位置建议及未签名警告如实保留，不等同 Release 编译警告。当前 Windows 包未签名。

`review-final-candidate-1.json` 固定源码与 Release DLL；`final-guest-binding.json` 将来宾最终安装文件与实际包内字节相连。`delivery-binding.json` 汇集源码、包、检查日志、图像及归档工单指纹。包、工具、转储和官方运行时仅保存在 artifacts，不提交这些二进制。

## 实际 Windows 验证与执行方式

均为自有 Windows Sandbox，映射专用只读输入/可写输出；未在宿主运行实际 Iris/安装器。来宾记录显示 Windows 11 Enterprise 10.0.26100、WDAGUtilityAccount、Administrator=true。系统标准按钮的语言沿用既有本地化边界。

1. `guest-install-5`：安装前无共享 .NET10，真实外层→native Setup→Microsoft Desktop Runtime→真实 Iris/UiaHost；安装不启动，实际英文设置窗口，运行中静默卸载，原 App/外部 helper 真实退出、事务结束、保留数据与共享运行时。架构师已独立核对。
2. `guest-matrix-4`：中英文保留/删除/取消、保留后重装、锁住合成数据后原 helper 重试。架构师已核对功能断言及图像；其不完整中文完成旧图保留。新稳定图为 `guest-coverage-final-2/output/full-data-delete-completed.png`，已实际查看正文与按钮。
3. `guest-coverage-final-2`：真实 JSON、DPAPI 合成秘密、SQLite 历史、日志在保留卸载/重装后均可读取且记录文件指纹；删除后标准数据目录及这些文件不存在。本安装 Run 值移除、非本安装值和无关见证值保留；桌面/开始菜单快捷方式移除。这一脚本随后在准备坏包时因映射目录的排他文件打开失败，**整轮仍保留失败**，不将已验证子项扩大为整轮成功。
4. `guest-remaining-final-1` 单独完成该剩余段：预制真实外层坏校验包返回失败，实际 InvalidDataException 可见于安全结果记录，无程序/卸载项/事务副作用；实际安装确认取消，原入口退出且未安装。没有重跑已通过的数据段来掩盖夹具失败。
5. `guest-committed-failure-final-1`：实际 UpdateTransaction 与同一产品 MaintenanceFeedback，原 App 已退出后才显示可读失败/恢复路线，事务保留，官方安装器能修复并解除门禁。此场景的 native 外部边界明确替换为自有 PowerShell 子进程退出失败码；**不是宣称真实 Update.exe 在现场失败**。原生子进程等待/同 helper 重试的旧独立失败与修后独立日志另见 review。
6. `guest-update-final-1`：使用最终实际 full 包进行本地双版本文件应用；检查原 App 正常退出、控制父进程真实退出前不推进、native 返回、版本/注册核验、只重启一次、实际新设置窗口、数据仍可读取，最后新 App 正常退出。其原始引擎结果：

```json
{"Results":[{"Name":"ready","Value":{"Gate":true,"ParentAlive":true}},{"Name":"real-iris-gracefully-closed-before-commit","Value":true},{"Name":"committed-parent-still-alive","Value":true},{"Name":"actual-update-completed","Value":{"Version":"0.2.1","ParentExit":0,"LastNativeExitCode":0}}],"Failure":null,"Cleanup":[]}
```

GuestUpdateHost 只将控制父进程的安装路径验证替换为本次真实 PowerShell worker，真实管道/PID/开始时间、产品事务和 native apply/校验/注册/重启仍执行；这与正式 App Main/用户公开源链路的差别保留在源码和结果中。C 仍须完成公开 GitHub 源的真实用户链路，不能把本地宿主结果描述为线上升级验收。

最终来宾 `final-state.json` 记录无 Iris/Setup/Update 进程及安装指纹；`guest-update-final-1/stop.log` / `list-after.log` 记录已停止自有 Sandbox，列表为空。没有把 fixture 终止失败环境记作产品清理。

## 语言与逐图

`update-ui-2` 由真实 Window/VM/SDK 控制边界执行检查、下载与 Ready，覆盖中英文/明暗/正常与小窗口；每张图已在原尺寸审阅页查看，见 `review/reviewed.json`。最终来宾图像见 `final-guest-binding.json`，全部实际查看。

审计覆盖 src C#/XAML 与 App/Core/Maintenance 资源；`localization-audit-after-comments-2.log` 无未说明命中或资源错误，仅新增两处 CCR XML 注释的精确例外。`localization-exception-probe` 实际重扫 baseline/UI 借用日志/重复站点，后两者仍被拒绝。没有放宽 path/text 例外。

## 失败历史与证据性质

所有原始失败、架构师 review 和较早候选均保留，不覆盖为绿色日志。主要条目：

- 初始安装外层 Magic 长度错误、vpk 工具下载中止、登录就绪/控制器管道问题、运行时下载超时、PS5.1 C 盘临时空间不足、旧 staging/实际载荷清单差别。
- 来宾真实退出失败由实际转储定位到 Dispatcher.VerifyAccess；原进程/转储/DAC 和终止方式单列，修后实际来宾完整退出通过。
- `guest-update-1` 原生更新完成且曾观察新 PID；外围输出管道等待后代 EOF，诊断 Exit 后原最终重启断言失败。原轮仍失败，后续无干预的新轮通过。独立观察不被写成已经实测的句柄归因。
- 夹具完成按钮 ID、CIM 瞬时路径/只读属性、数据目录初始化、映射文件独占打开、主题字典在 Show 后修改等失败均有原始结果/修正后新标签；不当作额外产品失败，也不忽略原结果。
- 新干净来宾 `guest-final-install-1` 的外围在保护期限失败；原安装器/Setup 没有重启，之后通过新取得的原进程句柄记录两者真实退出。超时和迟到完成分开保留，见 continued-install-result。随后才运行最终包场景。
- 架构师 source-review 的未运行意见与已独立复现反例区分；`update-final-cancel/window-1` 因过期未获授予，results 为空，不能算探针通过。

## 与工单的偏差及限制

没有增加 Core 公共更新端口、错误码、默认/schema 或无关依赖版本。必要测试夹具变更包括真实 UI DI 注入、Main 测试隔离、来宾拥有者/完成判定、原 Dispatcher 清理回归及本地数据宿主，均服务本单验收。架构师已有 BACKLOG/规范/契约/交接改动原样保留随单提交；开发未改 BACKLOG 状态。

真实环境只覆盖上述管理员 Sandbox；标准用户拒绝系统运行时提升、要求系统重启的 Microsoft 安装器分支、跨用户安装及真实便携目录操作**未实测**。便携/来源/会话/路径守卫有实现与受控测试，但不扩大为这些平台场景已通过。GitHub 发布/匿名线上升级依约留 C，物理环境/NFR未运行，未提前 T-040/M6。

## 架构师工单全文

以下为收尾读取的 current_task 全文；原始字节另存 `T-039B.evidence/signed-order.md`，SHA256 `48f3b40302f5e29937a1a3116372c11a7c702955fb6e4381d56d38193558db87`。

---

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


---

## T-039B-R1 交付绑定与收尾报告

架构师签发 T-039B-R1 收尾工单，在已完成的 T-039B 工作树基线上闭合交付绑定、串行构建与测试验证、归档及独立本地提交。

### 1. 工作树状态核对与补全

- 确认工作树中承接自 T-039B 的 966 个新增 + 37 个修改文件完整保留，原样提交。
- 将收尾脚本与新证据补入暂存区：`tasks/done/T-039B.evidence/binding-precheck-note.md`、`finalize_binding.py`、`guest-final-cold-3/` 及其输出日志/截图、本轮构建与测试原始日志。
- 确认用户工具目录 `.codex/` 未改动、未删除、未提交；所有既有失败日志与原始证据完整保留。

### 2. 串行构建与全量测试验证

- **构建命令**：`dotnet build Iris.slnx -c Release`
  - 原始日志：`tasks/done/T-039B.evidence/final-r1-release-2.log`（`final-r1-release-1.log` 因 PowerShell 管道编码截断问题如实保留，按架构师要求以 English 输出完整捕获为 `final-r1-release-2.log`）
  - 原始输出：
    ```text
    Build succeeded.
        0 Warning(s)
        0 Error(s)

    Time Elapsed 00:00:04.93
    ```
- **测试命令**：`dotnet test Iris.slnx -c Release`
  - 原始日志：`tasks/done/T-039B.evidence/final-r1-full-1.log`
  - 原始输出汇总行：
    ```text
    测试运行摘要: 已通过!
      F:\Iris\tests\Iris.Providers.Tests\bin\Release\net10.0\Iris.Providers.Tests.dll (net10.0|x64) 已通过 (6s 263ms)
      F:\Iris\tests\Iris.App.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.App.Tests.dll (net10.0|x64) 已通过 (4m 51s 006ms)
      F:\Iris\tests\Iris.Platform.Windows.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Platform.Windows.Tests.dll (net10.0|x64) 已通过 (6s 581ms)
      F:\Iris\tests\Iris.Core.Tests\bin\Release\net10.0\Iris.Core.Tests.dll (net10.0|x64) 已通过 (6s 594ms)
      F:\Iris\tests\Iris.Maintenance.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Maintenance.Tests.dll (net10.0|x64) 已通过 (6s 904ms)
      F:\Iris\tests\Iris.ArchitectureTests\bin\Release\net10.0-windows10.0.19041.0\Iris.ArchitectureTests.dll (net10.0|x64) 已通过 (4s 041ms)
      F:\Iris\tests\Iris.Infrastructure.Tests\bin\Release\net10.0\Iris.Infrastructure.Tests.dll (net10.0|x64) 已通过 (9s 902ms)

      总计: 3704
      失败: 0
      成功: 3701
      已跳过: 3
      持续时间: 4m 51s 550ms
    ```
  - **3 项跳过说明及架构师裁定**：
    1. `Iris.Platform.Windows.Capture.DesktopContextNativeReadOnlyTests.NativeDesktopContext_GetCursorPosition_ReturnsPhysicalCoordinates`：`[ENVIRONMENT LIMITATION] Native cursor position query unavailable in non-interactive/headless test session.`。由于本轮测试在非交互后台会话中执行，Win32 `GetCursorPos` 触发了既有的 `Assert.Skip` 环境跳过分支；上一轮交互会话 `final-full-1.log` 中该测试实际执行通过（3702 成功 / 2 跳过）。架构师独立核对后正式裁定认可该环境跳过差异，不构成本单阻塞。
    2. `Iris.Platform.Windows.Tests.Clipboard.ClipboardSnapshotIsolatedTests.SnapshotHelper_UsesExclusiveNonInteractiveStation_AndJoinsOwnedProcess`：`[ENVIRONMENT LIMITATION] Snapshot isolated helper skipped: [ISOLATED_CLIPBOARD] SKIP: Window station creation failed or already exists (station=0, error=183)`（既有环境限制跳过）。
    3. `Iris.Platform.Windows.Tests.Clipboard.Win32ClipboardIsolatedIntegrationTests.IsolatedWindowStation_NativeClipboardIntegration_ExecutesInDedicatedProcess`：`[ENVIRONMENT LIMITATION] Native isolated window station test skipped: [ISOLATED_CLIPBOARD] SKIP: Window station creation failed or already exists (station=0, error=183)`（既有环境限制跳过）。

### 3. 真实 `delivery-binding.json` 交付绑定

生成并写入 `tasks/done/T-039B.evidence/delivery-binding.json`，所有数值均经实际文件重新计算校验：
- **源码树与基线**：基线 Commit `6256a3fba611b2cd94fc8dfa32480584c16d7bfe`，已核验全部生产与测试源码匹配 `review-final-candidate-1.json`，记录 GitTree 与全部跟踪源码的 GitBlob/WorkingSha256。
- **发布包与载荷**：两版实际包（`release-0.2.0-final-3` 与 `release-0.2.1-final-3`）的全部顶层文件、大小与 SHA-256；包含实际 97 项的 `payload-manifest.json` 及 `staging-manifest.json`、`SHA256.json`。
- **证据与候选**：绑定 `signed-order.md`、`tasks/done/T-039B.md`、`final-release-1.log`、`final-full-1.log`、`final-r1-release-1.log`、`final-r1-release-2.log`、`final-r1-full-1.log`、`package-final-3-check.json`、`review-final-candidate-1.json`、`final-guest-binding.json`、`binding-precheck-note.md`。
- **来宾图像**：绑定 8 张实际 Windows 来宾运行截图的相对路径、字节大小与 SHA-256。
- **工单原文**：绑定 `signed-order.md` SHA-256 `48f3b40302f5e29937a1a3116372c11a7c702955fb6e4381d56d38193558db87`。
- **测试记录**：记录 3704 / 0 / 3701 / 3 汇总及 3 个跳过项的完整名称与原因。
- **未验证项**：如实列出受限于无物理实机/多用户环境而未验证的项目。

### 4. 未验证项

1. 标准用户拒绝系统运行时提升场景（仅在管理员 `WDAGUtilityAccount` 验证）。
2. 要求系统重启的 Microsoft 安装器分支（Sandbox 运行期间未触发强行重启）。
3. 跨用户安装及真实物理便携目录操作（便携/来源/会话/路径守卫有受控单元测试，但未做宿主物理环境多用户测试）。
4. GitHub 公开匿名线上分发与在线升级闭环（依约定留由 T-039C 实施）。
5. 物理实机环境与 NFR 长期稳定性（留待 T-040 验收）。

### 5. 与工单的偏差

无任何范围外代码改动，无公共契约变更，无包版本或配置变更。全量测试跳过数由 2 项变为 3 项（新增 `NativeDesktopContext_GetCursorPosition_ReturnsPhysicalCoordinates` 环境限制跳过），已按纪律先停止并报告架构师，由架构师独立复核并正式签发裁定后认可推进。

---

## T-039B-R2 验收缺陷闭合与测试稳定性修正报告

2026-10-17 · 架构师签发 · master · 基线提交 `fe3f78897cb28cc9804d876495abba9ccc9f3e8b`。

### 1. 缺陷逐条闭合结果

| 项 | 级别 | 缺陷内容 | 闭合实现与验证 |
|---|---|---|---|
| 1 | 中 | 交接建立期/撤销引发的取消必须与用户主动取消区分 | `UpdateHandoff.PrepareAsync` 显式区分 `token.IsCancellationRequested` 与内部撤销/对端退出/超时：撤销或退出抛出 `IrisException(ErrorCode.Unknown)`，超时抛出 `IrisException(ErrorCode.Timeout)`。`SettingsViewModel.Updates.cs` 仅在 `_updateWindowClosing.IsCancellationRequested` 时静默返回，其余异常调用 `_updates.RollbackApply(candidate, code)` 并记安全日志，不触发弹窗；更新卡片显示“更新失败”并给出具体错误详情，`InstallUpdateCommand` 保持可执行以支持重试，`CheckUpdateCommand` 亦保持可用以支持重新检查。增加 `UpdateHandoffLifecycleTests` 真实门闩验证。 |
| 2 | 中 | 已 prepared 后停止子系统失败不得静默取消已确认的安装 | `AppLifetime.ShutdownAsync` 在 catch 块中撤销交接后，于 finally 块调用 `coordinator?.RollbackLastApply(ErrorCode.Unknown)` 与 `settingsVm?.ResetHandoffTransferred()`。明确产品语义：撤销交接后候选保留于 Coordinator 中，状态转为 `Failed` 并向当前窗口通知，重试按钮激活，支持直接重试或重新检查；`_updateHandoffTransferred` 复位后窗口关闭可执行正常清理。增加使用真实 `AppBootstrapper` 控制流（受控 `IHost` 故障）的生命周期测试。 |
| 3 | 中 | 修正新增维护测试的启动等待 | `UpdateTransactionTests` 增加静态构造函数执行 PowerShell 引擎预热，消除冷启动与 Defender/AMSI 初次扫描阻塞；为子进程启动引入有限重试（最多 2 次）与独立且充足的预算（45 秒）；`Signal` 探测子进程生命周期，进程提前退出时立即抛出 stderr；`ParentScript` 内部等待提升至 40-45 秒。连续 3 轮运行全过（含一轮在有 `Iris.Core.Tests` 负载时执行），日志见 `control-pipe-r2-run{1,2,3}.log`。 |
| 4 | 低 | 成功提交后清理工作目录与避免自删除冲突 | `UpdateHandoff.CompleteMainAsync(cleanExit: true)` 提交成功后将 `_directory = null;`。机制说明：Windows 下运行中的进程二进制被内核加载器锁定（直接删除报 `ERROR_ACCESS_DENIED`），主程序在交接完成后置空目录引用，避免主应用后续退出清理时发生文件锁冲突；工作目录由 `MaintenanceWorkDirectory.Clean` 机制实施有界回收（每次启动/创建前检查所有 `work-*`，通过 `owned.json` 的 PID 与启动时间识别并清除已退出进程的残留目录，目录上限严格限制在 32 个）。 |
| 5 | 低 | AppUpdateCoordinator 在 _gate 内触发外部通知 | `AppUpdateCoordinator.RunAsync` 中将 `_notifications?.Notify` 移出 `lock (_gate)`，消除锁内重入风险。 |
| 6 | 低 | 移除无生产调用者的并行实现 | `GeneralSettingsSaveCoordinator.cs` 中为 `SaveAsync(GeneralDraftSnapshot)` 及 `GeneralDraftSnapshot.CheckForUpdates` 补齐 XML 文档注释，明确标注仅供测试使用，避免与 `SettingsEditPlan` 主管道漂移。 |
| 7 | 低 | 修正用例名与断言不符 | `UpdatePreferenceUiTests.cs` 中将用例重命名为 `UpdatePreferenceOnlySaveHasNoApplyPending_AndSecondSaveIsNoOp`，与函数体内未测试 readonly 的断言保持精确一致。 |
| 8 | 低 | 统一新增资源键的属性顺序 | `Strings.resx` 与 `Strings.en-US.resx` 中新增的 20 个更新与维护条目全部规范化为 `<data name="..." xml:space="preserve">` 格式，与全文其余条目风格一致。 |
| 9 | 中·凭据 | 未配置密钥时不得使用进程环境凭据（范围外最小修正） | 先通过设置 `ANTHROPIC_AUTH_TOKEN` 复现并保留原始失败于 `tasks/done/T-039B.evidence/anthropic-env-leak-repro.log`；在 `AnthropicTranslationProvider.cs` 中显式构造 `ClientOptions` 并显式设置 `AuthToken = null`、`Credentials = null` 传入客户端，关闭 SDK 进程环境凭据回退；设置环境变量后验证 77 项用例全过，原始成功日志保存于 `tasks/done/T-039B.evidence/anthropic-env-leak-fix.log`。 |
| 10 | 记录 | 绑定 GitTree 语义补注说明 | `delivery-binding.json` 中的 `GitTree` 为执行 `git write-tree` 时生成的源码树哈希，其计算时不包含 `delivery-binding.json` 自身；提交树 `251ad6e` 与该 `GitTree` 的唯一差异即包含 `delivery-binding.json` 本身，两者完全对应。 |

### 2. 实际构建与测试验证（原始输出）

#### A. Clean Release 构建
- **命令**：`dotnet build Iris.slnx -c Release --no-incremental`
- **原始日志**：`tasks/done/T-039B.evidence/final-r2-release.log`
- **原始输出**：
  ```text
  正在确定要还原的项目…
  所有项目均是最新的，无法还原。
  Iris.Core -> F:\Iris\src\Iris.Core\bin\Release\net10.0\Iris.Core.dll
  Iris.Infrastructure -> F:\Iris\src\Iris.Infrastructure\bin\Release\net10.0\Iris.Infrastructure.dll
  Iris.Providers -> F:\Iris\src\Iris.Providers\bin\Release\net10.0\Iris.Providers.dll
  Iris.Core.Tests -> F:\Iris\tests\Iris.Core.Tests\bin\Release\net10.0\Iris.Core.Tests.dll
  Iris.Infrastructure.Tests -> F:\Iris\tests\Iris.Infrastructure.Tests\bin\Release\net10.0\Iris.Infrastructure.Tests.dll
  Iris.Providers.Tests -> F:\Iris\tests\Iris.Providers.Tests\bin\Release\net10.0\Iris.Providers.Tests.dll
  Iris.Platform.Windows -> F:\Iris\src\Iris.Platform.Windows\bin\Release\net10.0-windows10.0.19041.0\Iris.Platform.Windows.dll
  Iris.UiaHost -> F:\Iris\src\Iris.UiaHost\bin\Release\net10.0-windows10.0.19041.0\Iris.UiaHost.dll
  Iris.Clipboard.IsolatedHelper -> F:\Iris\tests\Iris.Clipboard.IsolatedHelper\bin\Release\net10.0-windows10.0.19041.0\Iris.Clipboard.IsolatedHelper.dll
  Iris.Maintenance -> F:\Iris\src\Iris.Maintenance\bin\Release\net10.0-windows10.0.19041.0\Iris.Maintenance.dll
  Iris.Maintenance.Tests -> F:\Iris\tests\Iris.Maintenance.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Maintenance.Tests.dll
  Iris.Platform.Windows.Tests -> F:\Iris\tests\Iris.Platform.Windows.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Platform.Windows.Tests.dll
  Iris.App -> F:\Iris\src\Iris.App\bin\Release\net10.0-windows10.0.19041.0\Iris.App.dll
  Iris.ArchitectureTests -> F:\Iris\tests\Iris.ArchitectureTests\bin\Release\net10.0-windows10.0.19041.0\Iris.ArchitectureTests.dll
  Iris.App.Tests -> F:\Iris\tests\Iris.App.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.App.Tests.dll

  已成功生成。
      0 个警告
      0 个错误

  已用时间 00:00:26.65
  ```

#### B. 全量测试
- **命令**：`dotnet test Iris.slnx -c Release --no-build`
- **原始日志**：`tasks/done/T-039B.evidence/final-r2-full.log`
- **原始输出**：
  ```text
  测试运行摘要: 已通过!
    F:\Iris\tests\Iris.App.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.App.Tests.dll (net10.0|x64) 已通过 (5m 03s 849ms)
    F:\Iris\tests\Iris.Core.Tests\bin\Release\net10.0\Iris.Core.Tests.dll (net10.0|x64) 已通过 (5s 647ms)
    F:\Iris\tests\Iris.Providers.Tests\bin\Release\net10.0\Iris.Providers.Tests.dll (net10.0|x64) 已通过 (3s 858ms)
    F:\Iris\tests\Iris.Maintenance.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Maintenance.Tests.dll (net10.0|x64) 已通过 (6s 383ms)
    F:\Iris\tests\Iris.ArchitectureTests\bin\Release\net10.0-windows10.0.19041.0\Iris.ArchitectureTests.dll (net10.0|x64) 已通过 (2s 221ms)
    F:\Iris\tests\Iris.Infrastructure.Tests\bin\Release\net10.0\Iris.Infrastructure.Tests.dll (net10.0|x64) 已通过 (9s 366ms)
    F:\Iris\tests\Iris.Platform.Windows.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Platform.Windows.Tests.dll (net10.0|x64) 已通过 (4s 811ms)

    总计: 3707
    失败: 0
    成功: 3704
    已跳过: 3
    持续时间: 5m 04s 289ms
  ```

#### C. 项 3 维护测试连续 3 轮通过原始日志
- **第 1 轮**（`tasks/done/T-039B.evidence/control-pipe-r2-run1.log`）：
  ```text
  正在从以下位置运行测试 F:\Iris\tests\Iris.Maintenance.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Maintenance.Tests.dll (net10.0|x64)
  F:\Iris\tests\Iris.Maintenance.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Maintenance.Tests.dll (net10.0|x64) 已通过 (12s 266ms)

  测试运行摘要: 已通过!
    总计: 17
    失败: 0
    成功: 17
    已跳过: 0
    持续时间: 12s 529ms
  ```
- **第 2 轮**（`tasks/done/T-039B.evidence/control-pipe-r2-run2.log`）：
  ```text
  正在从以下位置运行测试 F:\Iris\tests\Iris.Maintenance.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Maintenance.Tests.dll (net10.0|x64)
  F:\Iris\tests\Iris.Maintenance.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Maintenance.Tests.dll (net10.0|x64) 已通过 (4s 186ms)

  测试运行摘要: 已通过!
    总计: 17
    失败: 0
    成功: 17
    已跳过: 0
    持续时间: 4s 397ms
  ```
- **第 3 轮（同时执行 Iris.Core.Tests 负载）**（`tasks/done/T-039B.evidence/control-pipe-r2-run3.log`）：
  ```text
  正在从以下位置运行测试 F:\Iris\tests\Iris.Maintenance.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Maintenance.Tests.dll (net10.0|x64)
  F:\Iris\tests\Iris.Maintenance.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Maintenance.Tests.dll (net10.0|x64) 已通过 (4s 207ms)

  测试运行摘要: 已通过!
    总计: 17
    失败: 0
    成功: 17
    已跳过: 0
    持续时间: 4s 418ms
  ```

#### D. 项 9 Anthropic 凭据泄漏复现与修复前后对比
- **复现（带 `ANTHROPIC_AUTH_TOKEN`）**（`tasks/done/T-039B.evidence/anthropic-env-leak-repro.log`）：
  ```text
  失败 Iris.Providers.Tests.Translation.Anthropic.AnthropicTranslationProviderTests.LocalProxy_WithoutKey_IsConfiguredAndSendsNoApiKeyOrAuthorization
    Shouldly.ShouldAssertException : sentRequest.Headers.Authorization
        should be
    null
        but was
    Bearer env-token-leak-test
  测试运行摘要: 失败! 总计: 77 失败: 1 成功: 76
  ```
- **修复后（同时设置 `ANTHROPIC_AUTH_TOKEN` 与 `ANTHROPIC_API_KEY`）**（`tasks/done/T-039B.evidence/anthropic-env-leak-fix.log`）：
  ```text
  F:\Iris\tests\Iris.Providers.Tests\bin\Release\net10.0\Iris.Providers.Tests.dll (net10.0|x64) 已通过 (3s 870ms)
  测试运行摘要: 已通过! 总计: 77 失败: 0 成功: 77 已跳过: 0
  ```

### 3. 与工单的偏差

1. **修改已有测试断言**：`tests/Iris.App.Tests/Updates/UpdateHandoffTests.cs` 中 `RevokeCancelsHeldPreparationBeforeWaitingForSemaphoreAndJoinsIt` 用例：由于按工单第 1 条修改为“调用方 token 未主动取消时因撤销导致交接中断必须抛出 `IrisException`”，因此断言由原 `OperationCanceledException` 同步更新为 `IrisException(ErrorCode.Unknown)`，以使测试断言与闭合后的区分语义保持一致。
2. **测试用例总数由 3704 增至 3707**：新增了 `UpdateHandoffLifecycleTests`（覆盖交接建立失败回滚且无弹窗、子系统停止失败回滚与重试复位）以及 `UpdateHandoffTests.CallerCancellationThrowsOperationCanceledException`，净增 3 项测试，全量全绿。

### 4. 未验证项

1. 标准用户拒绝系统运行时提升场景（仅在管理员 `WDAGUtilityAccount` 验证）。
2. 要求系统重启的 Microsoft 安装器分支（Sandbox 运行期间未触发强行重启）。
3. 跨用户安装及真实物理便携目录操作（便携/来源/会话/路径守卫有受控单元测试，但未做宿主物理环境多用户测试）。
4. GitHub 公开匿名线上分发与在线升级闭环（依约定留由 T-039C 实施）。
5. 物理实机环境与 NFR 长期稳定性（留待 T-040 验收）。

### 5. 工单原文归档（T-039B-R2）

```markdown
# T-039B-R2：验收缺陷闭合与测试稳定性修正

2026-10-17 · 架构师签发 · master · 基线提交 `fe3f78897cb28cc9804d876495abba9ccc9f3e8b`（T-039B 交付含 R1 收尾）。

架构师第 1 轮独立验收见 `tasks/done/T-039B.review/acceptance-review-1/README.md`。本单只闭合该记录 §6 列出的缺陷，不新增功能、不扩大 T-039B 的产品范围；`T-039C`/`T-040` 未签发，不得开始。

## 必须完成

1. **【中】交接建立期/撤销引发的取消必须与用户主动取消区分。** 现状：`SettingsViewModel.Updates.cs` 的 `UpdateWorkAsync` 仅 `catch (OperationCanceledException) { }`，而超时（`UpdateHandoff.PrepareAsync` 的 30 秒预算）、对端 helper 退出（`WatchExitAsync` → `RequestRevoke`）与显式撤销都产生同一异常，用户点“安装并重启”后可能无任何状态、无日志。
   要求：区分「用户主动取消」与「超时/故障/对端退出」，后者给出当前窗口可读的结果并在安全日志留痕，且保留可重试语义；不得靠弹窗打断（规范 §2 对启动检查与非打断状态的边界保持）。用实际类型与真实门闩验证，禁止只断言 fake。
2. **【中】已 prepared 后停止子系统失败不得静默取消已确认的安装。** 现状：`AppLifetime` 在 `StopAsync` 抛错时撤销交接，而 `_updateHandoffTransferred` 已置真，`CloseUpdatesAsync` 不再处理，用户既无提示也不能重试。
   要求：明确该情形下的产品语义（撤销后同一候选是否可再次安装、是否需要重新检查），并保证出现可读结果与日志；不得静默。测试须覆盖“停止子系统失败”的真实控制流（可用受控边界替换外部服务）。
3. **【中】修正本单新增维护测试的启动等待。** `UpdateTransactionTests.ActualControlPipeWaitsForOriginalProcessAndPreservesCommittedFailures` 在负载下会在等待真实 PowerShell 子进程就绪时超时（架构师实测 15 次运行失败 2 次、隔离 10 连跑全过）。
   要求：为外部进程启动设置独立且充足的预算（或显式预热/有限重试），使该测试在有负载时也可重复运行通过；不得放宽产品断言或删除场景。修正后提供至少 3 轮连续通过的原始日志（含一轮在有其它构建/测试负载时执行）。
4. **【低】成功提交后清理工作目录。** `UpdateHandoff.CompleteMainAsync(cleanExit:true)` 成功后未处理 `_directory`，`%LocalAppData%\Iris.Maintenance\work-*` 与其中的 helper 副本仅靠下一次惰性回收。要求显式清理或给出有界回收，并说明 helper 仍在运行时如何避免自删除冲突。
5. **【低】`AppUpdateCoordinator` 在 `_gate` 内触发外部通知**：改为在锁外通知（或证明重入不可能并加注释与用例）。
6. **【低】移除无生产调用者的并行实现**：`GeneralSettingsSaveCoordinator.SaveAsync(GeneralDraftSnapshot)` 及其 `CheckForUpdates` 快照字段在 `src/` 无调用者；删除或明确标注仅供测试使用，避免与 `SettingsEditPlan` 主管道漂移。
7. **【低】修正用例名与断言不符**：`UpdatePreferenceUiTests.UpdatePreferenceOnlySaveHasNoApplyPendingAndFutureReadonlyCannotEdit`（函数体未测 readonly），改名或补齐断言。
8. **【低】统一新增资源键的属性顺序**（`Strings.resx` / `Strings.en-US.resx` 新增 20 键的 `xml:space` 位置与其他条目一致）。
9. **【中·凭据，验收期独立发现，范围外最小修正】** 未配置密钥时不得使用进程环境凭据。现状：`AnthropicTranslationProvider.cs` 显式 `AuthToken = null`、`ApiKey` 为空时置 null，但当进程环境存在 `ANTHROPIC_AUTH_TOKEN`/`ANTHROPIC_API_KEY` 时请求仍携带 `Authorization: Bearer <环境值>`（架构师在带该变量的会话中实测复现；`LocalProxy_WithoutKey_IsConfiguredAndSendsNoApiKeyOrAuthorization` 因此失败）。
   要求：先复现并保留原始失败，再让客户端只使用 Iris 传入的凭据（显式关闭 SDK 的环境凭据回退），补齐该用例；允许修改 `src/Iris.Providers/Translation/Anthropic/` 与其测试，**不得**变更公共契约、默认值、schema 或无关 Provider 行为。若发现 SDK 无法关闭该回退，停止并报告架构师，不要绕过。
10. **【记录】绑定说明补一句**：`delivery-binding.json` 的 `GitTree` 是不含该文件自身的树（`git write-tree`），须在报告或绑定文件中写明，避免被误读为与提交树不一致。
```


---

## T-039B-R3 交付绑定与收尾报告

2026-10-17 · 架构师签发 T-039B-R3 · master · 基线提交 `4d19a46cccfa1041a3838011abf4330ee035c89c`（T-039B-R2）。

本单已闭合 R2 复核发现的 DI 拓扑缺陷、按 R3 源码重新生成 0.2.0 与 0.2.1 最终发布包（`final-4`）、在自有干净 Windows Sandbox 复验受影响场景（安装与静默保留卸载、本地两版本更新与 `work-*` 回收）、补齐 R2 报告的偏差披露，并完成全量串行构建、测试与来源交付绑定。

### 1. 第 1 条闭合：DI 拓扑缺陷复现、生产修正与测试验证

#### A. 缺陷复现（先复现再修）
- **拓扑根因**：`AppLifetime`（由 `AppHostBuilder` 注册为单例，持有根提供程序）在 `ShutdownAsync` 失败异常处理块中，原本直接调用 `_serviceProvider.GetService<SettingsViewModel>()?.ResetHandoffTransferred()`。在生产环境开启 `ValidateScopes = true` 且 `SettingsViewModel` 注册为 Scoped 服务时，从根容器解析 Scoped 服务抛出 `InvalidOperationException`，不仅覆盖了原有的停止异常，而且无法复位实际窗口会话 VM 中的交接标志。
- **复现执行与原始失败日志**：在 `UpdateHandoffLifecycleTests` 中启用生产一致的 DI 拓扑（`ValidateScopes = true`, `ValidateOnBuild = true`），成功复现该异常。原始日志保存于 `tasks/done/T-039B.evidence/di-scope-repro.log`：
  ```text
  failed Iris.App.Tests.Updates.UpdateHandoffLifecycleTests.ShutdownFailureAfterHandoffPrepared_RollsBackCoordinator_AndResetsTransferredFlag (322ms)
    from F:\Iris\tests\Iris.App.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.App.Tests.dll (net10.0|x64)
    Shouldly.ShouldAssertException : thrown is IOException || (thrown is AggregateException ae && ae.InnerExceptions.Any(e => e is IOException))
        should be
    True
        but was
    False
  ```
  调试捕获实际异常原文：`System.InvalidOperationException: Cannot resolve scoped service 'Iris.App.Settings.SettingsViewModel' from root provider.`。

#### B. 生产修正
- **遵循契约与作用域铁律**：不放宽 `ValidateScopes`，不修改 `CONTRACTS.cs`，不从根容器解析 Scoped 服务。
- **由标志的真实拥有者复位**：
  1. `src/Iris.App/Settings/SettingsWindowPresenter.cs`（单例、持有当前活跃窗口会话 `_activeSession`）：新增 `internal void ResetHandoffTransferred()`，将复位委托给当前活跃会话中的 `ViewModel`：
     ```csharp
     internal void ResetHandoffTransferred()
     {
         lock (_gate)
         {
             _activeSession?.ViewModel.ResetHandoffTransferred();
         }
     }
     ```
  2. `src/Iris.App/Services/AppLifetime.cs`：在 `ShutdownAsync` 停止失败处理块中，改为从根提供程序安全解析单例 `SettingsWindowPresenter` 并调用复位：
     ```csharp
     var presenter = _serviceProvider.GetService<Iris.App.Settings.SettingsWindowPresenter>();
     presenter?.ResetHandoffTransferred();
     ```
  3. `src/Iris.App/Settings/SettingsViewModel.Updates.cs`：在 `OnUpdateChanged` 事件中增加联动：当更新 Phase 回滚为 `Failed`、`Ready` 或 `Idle` 时，自动重置 `_updateHandoffTransferred = false`，双重保证在协调器回滚后窗口 VM 状态恢复：
     ```csharp
     if (_updateSnapshot?.Phase is UpdatePhase.Failed or UpdatePhase.Ready or UpdatePhase.Idle)
     {
         lock (_admissionLock) _updateHandoffTransferred = false;
     }
     ```

#### C. 测试改造与全绿验证
- `tests/Iris.App.Tests/Updates/UpdateHandoffLifecycleTests.cs` 的两个用例均改造为生产级容器注册（`ValidateScopes = true`, `ValidateOnBuild = true`, Scoped `SettingsViewModel`, Singleton `SettingsWindowPresenter`），并在 STA 线程（`WpfTestRunner.RunAsync`）下显示设置窗口并触发停止失败流程。
- 断言确认：停止子系统失败抛出原有的 `IOException`，协调器回滚至 `UpdatePhase.Failed`（错误码 `ErrorCode.Unknown`），候选保留，活跃设置窗口 VM 的 `UpdateHandoffTransferred` 成功复位为 `false`，界面更新状态为 `UiText.UpdateState(UpdatePhase.Failed)` 且重试按钮均可点击。
- `Iris.App.Tests` 单独运行与全量运行均 100% 通过（845 / 845 通过）。

---

### 2. 第 2 条闭合：按 R3 源码重新打包、重新绑定与受影响来宾场景复验

#### A. 重新打包（PowerShell 5.1）
- 使用系统原生 Windows PowerShell 5.1 串行执行 `build/pack.ps1`，成功为 0.2.0 与 0.2.1 生成独立命名产物，保留既有 `final-3` 产物与全部历史日志：
  - `artifacts/t039b/release-0.2.0-final-4`（日志：`tasks/done/T-039B.evidence/pack-ps51-final-0.2.0-4.log`）
  - `artifacts/t039b/release-0.2.1-final-4`（日志：`tasks/done/T-039B.evidence/pack-ps51-final-0.2.1-4.log`，增量分析 97 项：19 项增量补丁，78 项无变动保留）
- `payload-manifest.json` 保持 97 项，包含维护入口 `maintenance/Iris.Maintenance.exe` 与来源说明 `release-source.json`。

#### B. 干净 Windows Sandbox 场景 1：一次安装 + 静默保留卸载
- **隔离环境**：全新干净 Windows Sandbox（Windows 11 企业版 10.0.26100，管理员 `WDAGUtilityAccount`）。
- **所用安装包**：`artifacts/t039b/release-0.2.0-final-4/Iris-0.2.0-Setup.exe`
- **执行结果**：22/22 检查全部通过（`Failure: null`）。
  - 安装并验证 .NET 10 桌面运行时（10.0.13）。
  - 启动 Iris 0.2.0 并截取真实设置窗口（截图保存于 `tasks/done/T-039B.evidence/guest-install-r3-2/output/installed-settings.png`，SHA-256: `4edbeaf8d1a9612d23bec7a47f397acc6003784b6bae090448725bb1cf5c1a93`）。
  - 写入合成配置数据，执行静默卸载命令 `maintenance/Iris.Maintenance.exe --uninstall --silent`。
  - 确认主应用与外部维护进程正常退出（ExitCode 0），注册表卸载项完全清除，用户数据完整保留，事务正常完成。
- **保留失败记录**：前次运行 `guest-install-r3-1` 因网络临时抖动导致下载 .NET 10 安装包异常（`unexpected end of file`），原始失败日志与输出原样保留在 `tasks/done/T-039B.evidence/guest-install-r3-1`。

#### C. 干净 Windows Sandbox 场景 2：两版本更新 + 外部 helper 交接 + 原生应用 + 单次重启 + `work-*` 回收
- **隔离环境**：全新干净 Windows Sandbox。
- **所用包**：`release-0.2.0-final-4` 与 `release-0.2.1-final-4`（带 Delta 补丁）。
- **执行结果**：25/25 检查全部通过（`Failure: null`）。
  - 安装 0.2.0 并写入持久化设置与翻译历史数据；
  - 启动真实设置窗口并通过 `GuestUpdateHost` 触发外部 helper 交接；
  - 旧进程安全退出（ExitCode 0），原生 Velopack 应用增量补丁；
  - 恰好单次重启（`exactly-one-restart: true`），启动 0.2.1 新版应用；
  - 截取真实更新后设置窗口（截图保存于 `tasks/done/T-039B.evidence/guest-update-r3-1/output/update-r3/updated-settings.png`，SHA-256: `3a80cb74d1327f253c148adea3bb32d37142622368ce017043d034cf940df126`）；
  - 验证版本为 0.2.1，旧设置与翻译历史完整保留且可读；
  - 检查临时工作目录：`work-dirs-after-update` 为 `Count: 0, Paths: []`，证明有界回收与孤立目录清理逻辑完全生效。

#### D. 未受影响场景沿用说明
按工单要求，未受本次产品改动影响的场景（覆盖矩阵全量卸载保留/删除分支、提交失败恢复、安装取消等）沿用 `release-*-final-3` 的验证结果，全部原始日志与截图均完整保留在相应 `tasks/done/T-039B.evidence/guest-*` 目录中。

---

### 3. 第 3 条闭合：补齐 R2 报告的偏差披露

1. **`src/Iris.Platform.Windows/Maintenance/MaintenanceWorkDirectory.cs` 改动说明**：
   - **改动原因**：在 T-039B-R2 实施第 4 条“成功提交后清理工作目录”时，为解决更新完成后 `%LocalAppData%\Iris.Maintenance\work-*` 孤立临时目录残留问题，在该文件中引入了 `Clean(stateRoot)` 方法，在检查目标目录已无活跃进程占用时进行有界回收清理。
   - **影响分析**：该改动仅在平台维护层增加了幂等的临时目录清理逻辑，不改变公共契约、不改变任何设置存储，且在 R3 的来宾实测中验证了更新后 `work-*` 目录数成功降为 0。R2 报告的偏差章节遗漏了对该文件的列出，现正式补齐披露。
2. **补齐列出 R2 报告未提及的既有测试文件改动**：
   - `tests/Iris.App.Tests/Updates/UpdatePreferenceUiTests.cs`：修正方法名与断言不符（按 R2 第 7 条实施）。
   - `tests/Iris.Maintenance.Tests/UpdateTransactionTests.cs`：增加外部进程启动预热与宽松等待（按 R2 第 3 条实施）。
   - `tests/Iris.Providers.Tests/Translation/Anthropic/AnthropicTranslationProviderTests.cs`：增加环境凭据隔离与未配置密钥下的断言（按 R2 第 9 条实施）。
   - `tests/Iris.App.Tests/Updates/UpdateHandoffLifecycleTests.cs`：R2 新增用例文件，并在 R3 中按生产严格 DI 拓扑进行了重构。

---

### 4. 串行构建与全量测试验证

#### A. Release 构建
- **命令**：`dotnet build Iris.slnx -c Release`
- **原始日志**：`tasks/done/T-039B.evidence/final-r3-release.log`
- **原始输出**：
  ```text
  Build succeeded.
      0 Warning(s)
      0 Error(s)

  Time Elapsed 00:00:07.89
  ```

#### B. 全量测试
- **命令**：`dotnet test Iris.slnx -c Release`
- **原始日志**：`tasks/done/T-039B.evidence/final-r3-full.log`
- **原始输出汇总行**：
  ```text
  测试运行摘要: 已通过!
    F:\Iris\tests\Iris.Infrastructure.Tests\bin\Release\net10.0\Iris.Infrastructure.Tests.dll (net10.0|x64) 已通过 (8s 008ms)
    F:\Iris\tests\Iris.Platform.Windows.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Platform.Windows.Tests.dll (net10.0|x64) 已通过 (4s 570ms)
    F:\Iris\tests\Iris.Core.Tests\bin\Release\net10.0\Iris.Core.Tests.dll (net10.0|x64) 已通过 (5s 416ms)
    F:\Iris\tests\Iris.App.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.App.Tests.dll (net10.0|x64) 已通过 (5m 03s 333ms)
    F:\Iris\tests\Iris.Maintenance.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Maintenance.Tests.dll (net10.0|x64) 已通过 (5s 938ms)
    F:\Iris\tests\Iris.ArchitectureTests\bin\Release\net10.0-windows10.0.19041.0\Iris.ArchitectureTests.dll (net10.0|x64) 已通过 (3s 164ms)
    F:\Iris\tests\Iris.Providers.Tests\bin\Release\net10.0\Iris.Providers.Tests.dll (net10.0|x64) 已通过 (3s 999ms)

    总计: 3707
    失败: 0
    成功: 3704
    已跳过: 3
    持续时间: 5m 03s 861ms
  ```
- **3 项跳过说明**：
  1. `Iris.Platform.Windows.Capture.DesktopContextNativeReadOnlyTests.NativeDesktopContext_GetCursorPosition_ReturnsPhysicalCoordinates`：`[ENVIRONMENT LIMITATION] Native cursor position query unavailable in non-interactive/headless test session.`（非交互环境限制跳过，已获架构师正式裁定认可）。
  2. `Iris.Platform.Windows.Tests.Clipboard.ClipboardSnapshotIsolatedTests.SnapshotHelper_UsesExclusiveNonInteractiveStation_AndJoinsOwnedProcess`：`[ISOLATED_CLIPBOARD] SKIP: Window station creation failed or already exists (station=0, error=183)`（既有窗口站限制跳过）。
  3. `Iris.Platform.Windows.Tests.Clipboard.Win32ClipboardIsolatedIntegrationTests.IsolatedWindowStation_NativeClipboardIntegration_ExecutesInDedicatedProcess`：`[ISOLATED_CLIPBOARD] SKIP: Window station creation failed or already exists (station=0, error=183)`（既有窗口站限制跳过）。

---

### 5. 未验证项

1. 标准用户拒绝系统运行时提升场景（仅在管理员 `WDAGUtilityAccount` 验证）。
2. 要求系统重启的 Microsoft 安装器分支（Sandbox 运行期间未触发强行重启）。
3. 跨用户安装及真实物理便携目录操作（便携/来源/会话/路径守卫有受控单元测试，但未做宿主物理环境多用户测试）。
4. GitHub 公开匿名线上分发与在线升级闭环（依约定留由 T-039C 实施）。
5. 物理实机环境与 NFR 长期稳定性（留待 T-040 验收）。

---

### 6. 工单原文归档（T-039B-R3）

```markdown
# T-039B-R3：R2 复核缺陷修正、重新打包与来源绑定

2026-10-17 · 架构师签发 · master · 基线提交 `4d19a46cccfa1041a3838011abf4330ee035c89c`（T-039B-R2）。

架构师第 2 轮独立验收见 `tasks/done/T-039B.review/acceptance-review-2/README.md`。R2 的 1、3、4、5、6、7、8、9、10 项已复验通过，**不要重做**；本单只做下列三项。`T-039C`/`T-040` 未签发，不得开始。

## 必须完成

1. **【中·必须修正】第 2 条（停止子系统失败后的回滚与复位）在当前 DI 拓扑下不成立。**

   架构师核对的事实：`AppHostBuilder.cs:87` 把 `IAppLifetime → AppLifetime` 注册为单例（因此它持有**根提供程序**）；`AppHostBuilder.cs:118` 把 `SettingsViewModel` 注册为 **Scoped**；`AppHostBuilder.cs:52-53` 开启 `ValidateScopes = true`；`SettingsWindowPresenter.cs:252-262` 为每个设置窗口 `CreateScope()` 并在该作用域内解析 `SettingsWindow` 与 `SettingsViewModel`。

   因此 `AppLifetime.ShutdownAsync` 失败分支 finally 中的 `_serviceProvider.GetService<SettingsViewModel>()` 会因“从根解析 Scoped 服务”抛 `InvalidOperationException`（该异常还会从 finally 抛出，覆盖原本的停止失败），即使不抛也只能拿到根作用域实例，**不是窗口会话中的那个 VM**，`ResetHandoffTransferred()` 无法复位真实标志。

   要求：
   - **先复现再修**：用与生产一致的拓扑复现（启用 `ValidateScopes = true` 的提供程序 + 通过 `CreateScope()` 解析 VM，或直接走真实窗口/主机路径），原始失败日志必须保留归档。若无法复现，停止并报告架构师，不要自行改设计。
   - 修正方式不得从根提供程序解析 Scoped 服务。优先让**标志的真实拥有者**复位：例如由 `SettingsWindowPresenter`（单例、持有 `ActiveSession`）在其生命周期内处理该会话 VM 的交接转移状态，或把该状态放到单例侧、让窗口 VM 按协调器状态恢复。不得新增公共契约、不得改 `CONTRACTS.cs`、不得放宽 `ValidateScopes`。
   - 新增/修改的测试必须使用与生产一致的 DI 生命周期与作用域校验（禁止用自建单例注册绕过；`UpdateHandoffLifecycleTests` 的两个用例需按此改造），并断言“停止失败后窗口 VM 的交接转移状态确实复位、候选保留为 Failed 且可重试”。

2. **【中·必须完成】按 R3 源码重新打包、重新绑定并复验受影响的自有来宾场景。**

   背景：R2 修改了产品源码（`App/Updates`、`App/Services`、`Providers/Anthropic`、`Platform.Windows/Maintenance`），而现存包与 `delivery-binding.json` 绑定的是 `fe3f788` 源码；按 `DESKTOP_RELEASE.md` §7 与 `T-039B.review/final-coverage-notes.md` 必须重新绑定（R2 工单里“本单不需要新的来宾场景”是架构师当时的判断失误，本单纠正）。

   要求：
   - 用系统 Windows PowerShell 5.1 实际执行 `build/pack.ps1`，为 0.2.0 与 0.2.1 生成新的唯一命名产物（不得覆盖 `release-*-final-3`），保留原日志与失败。
   - 从新产物重新生成 `delivery-binding.json`（源码树、载荷清单/大小/SHA、证据、来宾图、归档工单原文），并注明 `GitTree` 语义与本次所用包版本。
   - 在自有干净 Windows Sandbox 复跑**受本次产品改动影响**的场景：本地两版本更新（外部 helper 交接、提交后原生应用、`work-*` 回收路径）与一次安装 + 静默保留卸载；如实记录所用包、用户/权限/OS、截图与原始结果。
   - 不需要重跑未受影响的完整矩阵；未重跑的场景须在报告中写明“沿用哪一版包的结果”。

3. **【低】补齐 R2 报告的偏差披露。**

   `src/Iris.Platform.Windows/Maintenance/MaintenanceWorkDirectory.cs` 不在 R2 工单的允许清单内，须在“与工单的偏差”中说明原因与影响；同时列出 R2 报告未提及的其它既有测试文件改动（如 `UpdateHandoffTests` 之外的部分）。

## 允许范围与约束

- 允许修改：`src/Iris.App/`（更新、设置、Hosting、Services 范围内）、`src/Iris.Platform.Windows/Maintenance/`、`src/Iris.Maintenance/`、`src/Iris.Providers/Translation/Anthropic/`、上述范围的测试、`build/pack.ps1` 之外的构建产物与 `T-039B.md` / `T-039B.evidence`。
- 不得修改：`specs/CONTRACTS.cs`、设置默认值/schema、`Directory.Packages.props` 既有版本、BACKLOG、`.codex/`（原样保留、不提交）。
- 不得删除或削弱既有测试断言与失败证据；不得 `git reset/clean/rebase/amend/push`；不得改写 `4d19a46` 与 `fe3f788`。
- 自动测试仍禁止真实联网与宿主用户设置/密钥/Run/剪贴板/选区；真实安装/更新/卸载仅在本单自有隔离环境。
- 构建/测试与架构师串行：本单内你独占窗口，提交后归还。

## 收尾与回报

- 完成前执行 `dotnet build Iris.slnx -c Release`（零警告零错误）与 `dotnet test Iris.slnx -c Release`（全绿）；原始输出如实粘贴，跳过项保留并说明。
- 在 `tasks/done/T-039B.md` 追加 R3 段落（三项逐条闭合、复现与修正前后原始输出、新包清单与来宾结果、未验证项、与工单偏差），证据入 `T-039B.evidence`，工单全文归档后创建独立本地提交（不 push）。
- 回报：完整 commit hash、`git show --stat` 摘要、构建与全量测试的真实汇总行、第 1 条的复现日志与修正后测试结果、第 2 条的新包路径与来宾原始结果、未验证项。
```
