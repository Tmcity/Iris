from pathlib import Path
import json
import re
from inspect_delivery import test_reports

evidence = Path(__file__).resolve().parent
root = evidence.parents[3]
report_path = root / "tasks/done/T-039B.md"
active = json.loads((evidence / "active_round.json").read_text(encoding="utf-8"))
inspection_path = evidence / (active["GuestInspection"] if (evidence / active["GuestInspection"]).exists() else active["PackageInspection"])
data = json.loads(inspection_path.read_text(encoding="utf-8"))
source = json.loads((evidence / active["SourceBinding"]).read_text(encoding="utf-8"))
theme_before = test_reports(evidence / (active["RenderingBefore"] + "-reports"))
theme_after = test_reports(evidence / (active["RenderingAfter"] + "-reports"), "Iris.App.Tests.Updates.UpdateRecoveryRenderingTests.")
theme_viewed = json.loads((evidence / active["RenderingReviewed"]).read_text(encoding="utf-8"))
localization = json.loads((evidence / "localization-verification-1.json").read_text(encoding="utf-8"))
exception_sha = next(item["Sha256"] for item in localization["UnchangedScannerBudgetAndResources"] if item["Path"] == "build/localization/exceptions.json")
localization_before_log = (evidence / "localization-inventory-before-2.log").read_text(encoding="utf-8").strip()
localization_after_log = (evidence / "localization-inventory-after-1.log").read_text(encoding="utf-8").strip()
localization_catalog_log = (evidence / "localization-catalog-after-1.log").read_text(encoding="utf-8").strip()
review = root / "tasks/done/T-039B.review/acceptance-review-4"
independent_runtime = json.loads((review / "independent-runtime-candidate2-binding.json").read_text(encoding="utf-8"))
independent_bytes = json.loads((review / "candidate-check-2.json").read_text(encoding="utf-8"))
independent_log = (review / "independent-runtime-candidate2-1.log").read_text(encoding="utf-8")
independent_summary = independent_log[independent_log.rfind("  总计:"):].strip()
current_independent_runtime = json.loads((review / "independent-runtime-candidate3-binding.json").read_text(encoding="utf-8"))
current_independent_bytes = json.loads((review / "candidate-check-3.json").read_text(encoding="utf-8"))
current_independent_localization = json.loads((review / "localization-after-1/verification.json").read_text(encoding="utf-8"))
current_independent_visual = json.loads((review / "recovery-ui/candidate3-visual-carry-forward.json").read_text(encoding="utf-8"))
current_independent_log = (review / "independent-runtime-candidate3-1.log").read_text(encoding="utf-8")
current_independent_summary = current_independent_log[current_independent_log.rfind("  总计:"):].strip()
current_guest_review = json.loads((review / "guests-independent-final8-1.json").read_text(encoding="utf-8"))
text = report_path.read_text(encoding="utf-8")
text = text.replace("**执行结果**：22/22 检查全部通过（`Failure: null`）。", "**执行结果（R4 据原始 JSON 更正）**：24 个观察记录，其中 10 个布尔断言，无 false，`Failure: null`；执行与停止退出码均为 0。")
text = text.replace("**执行结果**：25/25 检查全部通过（`Failure: null`）。", "**执行结果（R4 据原始 JSON 更正）**：26 个观察记录，其中 17 个布尔断言，无 false，`Failure: null`；执行与停止退出码均为 0。")
text = text.replace("（带 Delta 补丁）。", "（生成 delta 产物；来宾实际传入并应用 `Iris-0.2.1-full.nupkg`）。")
text = text.replace("原生 Velopack 应用增量补丁；", "原生 Velopack 应用 full 包；delta 仅生成，未实测应用；")
text = text.replace("按工单要求，未受本次产品改动影响的场景（覆盖矩阵全量卸载保留/删除分支、提交失败恢复、安装取消等）沿用 `release-*-final-3` 的验证结果，全部原始日志与截图均完整保留在相应 `tasks/done/T-039B.evidence/guest-*` 目录中。", "按工单要求保留未受影响的历史场景。R4按输入SHA补注：guest-matrix-4 的安装器来自 release-0.2.0-6；guest-coverage-final-2、guest-remaining-final-1 与 guest-committed-failure-final-1 来自 final-3。原始日志、输入绑定与截图均完整保留，不把这些历史包写为R4新包通过。")
marker = "## T-039B-R4 交付报告"
if marker in text: text = text[:text.index(marker)].rstrip() + "\n"
build = (evidence / (active["ReleaseBuild"] + ".log")).read_text(encoding="utf-8")
build_summary = build[build.rfind("已成功生成。"):].strip()
full = (evidence / (active["FullTests"] + ".log")).read_text(encoding="utf-8")
full_summary = full[full.rfind("  总计:"):].strip()
packages = []
for package in data["ReleasePackages"]:
    installer = next(item for item in package["Files"] if item["Path"].endswith("-Setup.exe"))
    full_package = next(item for item in package["Files"] if item["Path"] == f'Iris-{package["Version"]}-full.nupkg')
    packages.append(f'| {package["Version"]} | `{package["Directory"]}` | {package["PayloadManifestEntries"]} | `{installer["Sha256"]}` | `{full_package["Sha256"]}` |')
guest_section = """**尚未验证，当前环境阻塞。** `guest-install-r4-1-run.log` 在调用既有 wsb.exe 前得到 FileNotFoundError；本轮没有进入来宾，没有运行新包实际安装/更新/卸载。`sandbox-preflight-1.log` 原文记录 Windows 11 专业工作站版 10.0.26200、SandboxAliasExists=false、SandboxSystemExeExists=false、SandboxCommands/SandboxPackages 为空、可选功能读取要求提升（exit 3）。架构师补充的 `acceptance-review-4/host-isolation-preflight-1.json` 独立记录 Sandbox Disabled（CIM InstallState=2）和当前账户 Hyper-V 访问被拒绝。隔离环境选择与唯一一次宿主功能启用确认已由架构师接管；截至工单补充，/NoRestart 脚本未获准执行，工程师不再重复询问用户或自行启用功能。未修改宿主系统功能、未在宿主运行 Iris、未用旧结果冒充本轮通过。输入、脚本和唯一失败目录均保留。"""
if "SandboxVerificationR4" in data:
    rows = []
    for guest in data["SandboxVerificationR4"]:
        rows.append(f'| `{guest["EvidenceDir"]}` | `{guest["SandboxId"]}` | {guest["ObservationRecords"]} | {guest["BooleanAssertions"]} | {guest["FalseAssertions"]} | {guest["GuestCommandExitCode"]} / {guest["StopExitCode"]} |')
    guest_section = "| 原始证据目录 | Sandbox | 观察记录 | 布尔断言 | false | 来宾命令 / stop CLI退出码 |\n|---|---|---:|---:|---:|---|\n" + "\n".join(rows)
    guest_section += f"\n\n两项均使用当前active_round.json绑定的新包实际输入；输入路径/大小/SHA、脚本原文、原始结果与截图由 `{active['GuestInspection']}` 重新读取核对。更新使用 full 包，delta 仅生成。GuestUpdateHost 是受控传输/父进程边界，调用实际维护引擎并通过 IPC 关闭真实 Iris，不能据此声称正式 GitHub 用户 UI 线上路径已验证。"
    guest_section += "\n\n用户启用Sandbox后，工程师实际核对Feature InstallState=1、两个入口存在及空实例列表（sandbox-enabled-preflight-1）；架构师host-isolation-enabled-1及sandbox-list-enabled-1原件同时保留，双方均未执行宿主功能更改。pre-guest-input-byte-check-1重读702项源、321项冻结文件、两版97项full载荷与外层入口，仍匹配candidate-3/final-8，未重跑建测或改产品源码。最新活动工单全文另存本段最后指向的Sandbox恢复快照，先前阻塞/本地化快照不动。"
    guest_section += "\n\n来宾下载链路曾失败，原件全部保留：guest-install-r4-2/r4-3的正式Setup均以1结束，外层以2结束，Velopack日志为Peer disconnected；r4-4在curl获取微软元数据时失败，没有启动Iris安装器；r4-5只在自有来宾临时指定公开DNS返回的微软服务IP，正式下载仍失败。guest-network-r4-1为PowerShell将curl verbose stderr当作错误的夹具失败，修正为真实进程流捕获后r4-2/r4-3保留元数据/HEAD、TLS及DNS原文；HEAD或诊断exit0不等于大文件下载成功，更不算安装通过。所有失败来宾均单独关闭，没有在同一安装尚未结束时重复启动。"
    guest_section += "\n\n成功来宾使用显式--prefetch-runtime夹具：只在该干净来宾以curl重试下载微软官方releases.json及Desktop runtime，逐项匹配官方SHA512并确认Authenticode Valid，再放入Velopack正常Downloads文件名；夹具没有安装运行时。原始结果仍证明调用正式final-8安装器前共享.NET10为空，随后由正式Setup启动并等待真实微软运行时安装。此次不宣称Velopack从公网独立完成了整份运行时下载；不修改产品TLS/下载实现，也不借用宿主安装或预装.NET绕过缺失检测。输入/输出绑定、官方元数据、运行时字节/签名及全部运行时日志保留。成功场景没有使用失败的来宾DNS覆盖。"
    guest_section += "\n\n安装/UiaHost/真实App/维护拥有者均等待其真实句柄退出，并分别记录退出码；静默卸载后另断言无Iris拥有的剩余进程、数据和运行时保留、卸载注册及事务移除。更新对真实旧App退出、单次重启、新窗口、再次优雅退出、数据可读、无work目录及无Iris剩余进程分别断言。runtime-process-observation记录长时安装期间的同一父子进程链；超时或下载失败没有被写成安装进程已结束。最后两场景各自stop exit0及空Sandbox列表才更新guest-run-manifest。"
    guest_section += "\n\n升级场景的runtime-handles-terminal-1另实际持有外层安装器、native Setup、下载目录微软运行时、wixstdba及提权运行时的五个原进程句柄，逐个核对PID/开始时间、WaitForExit及exit0，未以PID消失或超时替代。两场景最终快照仍有系统Windows Installer服务msiexec.exe /V；没有杀该服务，也不声称全部Windows系统进程退出。两张当前PNG已通过view_image逐张实际查看，英文General页分别显示0.2.0/0.2.1、正文与Save/Cancel可读，图像指纹和视口局限在reviewed-guests-r4-1.json；未显示的全部更新动作不从截图推定。"
    guest_section += f"\n\n架构师独立来宾读取原文 `acceptance-review-4/guests-independent-final8-1.json` 为{current_guest_review['checks']}项/{current_guest_review['passed']}项通过、失败空，逐项绑定输入/原始结果、真实退出、四类数据服务与四文件前后哈希、门禁/work目录及停止后空列表。其自动读取JSON仍保留visuallyReviewed=false；架构师随后在本会话明确两张原图已逐张查看，原文另归档architect-final8-guest-review-supplement-20261025.md，不修改自动记录或扩大视觉范围。这只确认R4受影响来宾行为，最终HEAD Release/full及完整验收仍由架构师执行。"
    guest_section += "\n\n归档时将两份tasks输出中的微软runtime exe与原artifact输出及新稳定副本三方逐字节核对（大小/SHA256/SHA512）；仅移除tasks下重复exe，原artifact输出与artifacts/t039b/r4-runtime-cache-preserved-1均保留原字节。runtime-binary-relocation-1.json记录旧证据路径、原始artifact来源和保存路径；没有丢日志、改结果或掩盖失败。旧报告/绑定先存before-final-review-sync-1。Git仅收工程师文本/图片/哈希证据，安装器/依赖/包/转储不入Git；独立复审/规范/协调的未提交架构师文件保持原状态。"
    guest_section += "\n\nR4证据目录使用局部.gitattributes的-text保存原始换行；暂存后逐个读取Git blob核对已绑定原始证据的长度/SHA，结果留artifacts/t039b/r4-staged-selection-1.json。该规则只影响tasks下本轮证据，不改全局Git配置或产品源码范围。"
if (evidence / "guest-attempt-ledger-1.json").exists():
    ledger = json.loads((evidence / "guest-attempt-ledger-1.json").read_text(encoding="utf-8"))
    guest_section += "\n\n逐次原始结果分类见 `r4/guest-attempt-ledger-1.json`：\n\n| 唯一标签 | runner exit | 来宾命令exit | 观察 | 布尔 | 失败原文 |\n|---|---:|---:|---:|---:|---|\n"
    for attempt in ledger["Attempts"]:
        guest_section += f'| {attempt["Label"]} | {attempt["RunnerExitCode"]} | {attempt["GuestCommandExitCode"]} | {attempt["ObservationRecords"]} | {attempt["BooleanAssertions"]} | {attempt["Failure"]} |\n'
status = "请求架构师独立复审；不自行验收 T-039B / 父 T-039。" if "SandboxVerificationR4" in data else "实现、自动回归和新包已完成；本报告为来宾环境待补充时的真实状态，尚非完整交付或验收请求。"
remaining = "真实final-8来宾现已完成，工程师按原单核对归档、移除活动工单并创建独立本地提交；最终commit及同范围源码树复算记录随交付回报，请求架构师复审，未自行结案或开始C/T-040。" if "SandboxVerificationR4" in data else "真实final-8来宾与最终提交仍欠，保持current_task，不提交、不结案、不开始C/T-040。"
section = f'''\n---\n\n{marker}\n\n2026-10-25 · 唯一基线 `{source["BaseCommit"]}`。{status}

### 缺陷、生产修复与恢复边界

修前已再次执行冻结 R3 探针：`r4/frozen-r3-production-before-1.log`，exit 1，原始结果为 `Stopped / HasCandidate=false / InstallCanExecute=false / CheckCanExecute=false`，原 `IOException` 与自有 STA 完整退出记录均保留。架构师 acceptance-review-3 原源码、程序集绑定和所有原失败保持原样。

R4 用一次性 `PreparedUpdate` 收据关联候选、拥有会话及代次。真实安装命令先经过用户确认、实际本地校验与 helper ready，之后 `AppLifetime.TryRequestUpdateShutdown` 才消费收据并接管安装意图。普通 Ready、确认取消、接受后取消、后续新 offer 的旧收据均不构成退出恢复意图。

`AppBootstrapper` 接入真实 updates/settings 停止依赖，退出开始关闭设置准入并冻结真实 VM；实际资源清理仍在原有设置 shutdown 阶段。`AppLifetime` 保有转移后的责任，停止失败先撤销并 join 原 helper，保留主异常、撤销清理异常和失败历史，再由仍存活的 singleton 创建独立 `UpdateRecoveryWindow`。原设置窗口尚在时仅复位其实际 VM 标志并发布失败文本；已正常释放时不通知旧 VM、不创建新设置 scope、不重新开放 Presenter。专用窗口只有“重试安装 / 取消更新并退出”。重试先完成原撤销/排空，再重校验同一 full 候选、重新准备 helper并重试原停止责任；普通检查、下载、保存、服务测试、模型查询和自启请求保持关闭。取消更新先 join helper后丢弃对应收据，后续普通退出不准备或应用旧候选。Main 最终提交屏障保持，失败退出不 native apply、不重启。

修后以冻结的 `{active["CandidateDirectory"]}` 执行完整生产拓扑：`r4/{active["FixedProduction"]}.log`，9 成功、0 失败、0 跳过；源码及 {len(source["TestedCandidateFiles"])} 个实际受测文件见 `r4/{active["SourceBinding"]}`。这不是把旧探针换 DLL 后宣称同一二进制通过：修后测试新增真实 settings shutdown、Dispatcher、helper握手和恢复收据路径，全部拓扑差别在源码与本段公开。

`{active["FullTests"]}-results/retained-failure.json` 与 `disposed-failure.json` 实测均为 `Failed / HasCandidate=true / HasConfirmedUpdate=true`，只有一个 Scoped VM；前者 VM仍存且 transferred=false，后者原 scope已释放且未向其发通知。通过专用窗口实际重试，两个停止阶段各重复失败后成功；普通工作直接调用和按钮都不写入/发请求，feed/download计数各保持1。`*-worker-*.json` 是受控外部 worker 的原始结果：产品管道、候选校验、事务门禁与原进程退出屏障真实，安装/注册/重启边界为惰性受控替身。成功停止后完整调用 Main提交边界并重复调用，原测试进程仍活着，native/restart为0；随后用本次随机取消事件完整 join 自有 worker，门禁解除。不能把这一层扩大为自动测试已执行真实安装；原进程退出后的真实 full 应用由来宾证据承担。

`ShutdownFailureAndRevokeCleanupFailure_PreserveBothErrors_AndRetryCleanupBeforePreparingAgain` 保留原 Host错误和目录撤销错误；再次失败不会多启动 helper，解除自有清理故障后成功。否定场景见 `ordinary-*.json` 和 `cancel-transferred.json`，不创建或恢复未经确认/已取消/陈旧的安装意图。

### 恢复窗口主题补充修复（同一R4）

架构师source-feedback-2实际逐图指出冻结候选1的Dark按钮近白对白不可读，原图与旧DLL指纹不动。新增回归使用App.xaml相同的全局ThemesDictionary/ControlsDictionary与生产ApplicationThemeManager.Apply，按各自稳定语言生命周期校验实际语言；测试不向Window另套字典、不改Window前景或背景来掩盖缺陷。修前原文 `r4/{active["RenderingBefore"]}.log` 为 {theme_before["Total"]} 总计 / {theme_before["Failed"]} 失败 / {theme_before["Passed"]} 成功，Dark四种组合动作实际对比度均为1，原图及xUnit报告保留。

产品UpdateRecoveryWindow通过动态SolidBackgroundFillColorBaseBrush/TextFillColorPrimaryBrush接入全局主题，动作改用已有WPF-UI主题Button，重试为Primary。未修改Theme设置默认值、草稿或宿主主题。当前全量 `r4/{active["RenderingAfter"]}.log` 的xUnit按UpdateRecoveryRenderingTests类筛选，相同回归为 {theme_after["Total"]} 总计 / {theme_after["Failed"]} 失败 / {theme_after["Passed"]} 成功；已逐张查看 {len(theme_viewed["Frames"])} 张zh-CN/en-US × Light/Dark × 380/540原图，正文、两动作与按钮背景均可辨认。实际颜色合成后的最小动作对比度原值为 {theme_viewed["MinimumActionContrast"]}，正文为 {theme_viewed["MinimumBodyContrast"]}；图像SHA、真实主题/语言及加载DLL见各帧JSON和 `r4/{active["RenderingReviewed"]}`。此处是无Host/网络/安装的WPF客户端区域渲染证据，不能扩大为来宾安装或线上更新通过。

主题修复时形成的候选2/final-7和架构师逐图复核均保留；当前本地化消息修正后又执行本段最终Release/full，固定候选3并生成新的唯一{active["PackageTag"]}。旧受测快照、修前原图和此前全部失败不动，`r4/before-theme-fix/binding.json`与`before-localization-fix/binding.json`分别绑定两次补充修正前的报告、绑定和脚本原文；全过程仍为同一R4。

### LOCALIZATION完整收尾（当前源码实测）

按最新工单与`acceptance-review-4/localization-before-1/approved-catalog-additions.json`仅加入五对既有资源值。App catalog由457到462，原457对逐项保持不变；App/Core/Maintenance的中英文resx原字节不变。GeneralDraftSnapshot.CheckForUpdates与SaveAsync兼容成员保留“仅供测试、生产走SettingsEditPlan/CommitPlanAsync”的英文XML说明。UpdateHandoff四个站点使用原有对应ErrorCode的本地化默认消息：三个Unknown、一个Timeout，保留原IrisException/调用方OperationCanceledException分流、retryable、inner对象与helper撤销/join责任；未新增资源或改变公共契约、默认值、schema。

工程师修前保存`before-localization-fix`原字节与工具/输入绑定，再实际执行现有工具；`localization-catalog-before-2.log`为exit1、六处未批准站点及catalog缺键。修前source/compiled原文：

```text
{localization_before_log}
```

修后同一扫描与编译资源工具原文（`localization-inventory-after-1.log`，exit0）：

```text
{localization_after_log}
```

修后catalog/source审计原文（`localization-catalog-after-1.log`，exit0）：

```text
{localization_catalog_log}
```

工具Program.cs、audit.py与exceptions.json指纹保持修前一致；例外预算SHA原值为`{exception_sha}`，具体未改记录见`localization-verification-1.json`。审计实际加载的六份编译资源程序集按原选择算法另冻结于artifacts/t039b/r4-localization-compiled-1，路径/大小/SHA见localization-after-1/compiled-binding.json。没有执行migration、放宽扫描或扩大例外预算。handoff-localization-tests-1.log实际15成功/0失败/0跳过，覆盖两语言下受控内部取消/撤销映射及错误对象责任；受控内部取消不是实际30秒超时实测，真实helper与完整关停拓扑仍由下述冻结回归承担。旧审计、旧编译资源与旧来宾不替代当前结果。

### Release / full 原始输出

Release命令和完整原文见 `r4/{active["ReleaseBuild"]}.command.json` / `.log`（前一轮 no-incremental 也保留）：

```text
{build_summary}
```

全量命令 `dotnet test Iris.slnx -c Release --no-build --no-ansi --no-progress --report-xunit-xml ...`，完整原文和实际项目报告见 `r4/{active["FullTests"]}.log` / `{active["FullTests"]}-reports/`：

```text
{full_summary}
```

两项跳过均为既有 Clipboard 隔离窗口站 error=183，完整测试名和原因保留在日志及 xUnit XML；本轮 DesktopContext 光标用例实际执行通过。定向回归25成功的早期运行也保留；最终固定程序集回归及全量是本段最终源码验证，未拿架构师旧全量替代。

### {active["PackageTag"]} 新包与非自引用来源

原final-4 / final-6 / final-7 / 历史来宾证据不动。final-5首次打包的包字节和日志保留：实际Windows PowerShell 5.1自动模块发现未找到Get-FileHash，在清单阶段exit1；显式加载系统Utility/Security模块后生成的final-6亦按原字节保留。本次本地化消息修复后以同一系统PowerShell 5.1包装脚本生成新的唯一{active["PackageTag"]}两版，没有修改宿主模块/用户配置或产品打包脚本。实际日志记录PowerShell `5.1.26100.9549`、Desktop、系统powershell.exe，而非沿用工单给出的旧补丁号。

| 版本 | 新包目录 | 实际 full 载荷项 | 外层安装器 SHA256 | full包 SHA256 |
|---|---|---:|---|---|
{chr(10).join(packages)}

`r4/{active["PackageInspection"]}` 逐项重读两版实际 lib/app载荷的集合/长度/SHA、SHA256清单、外层 footer的 magic/长度/内部Setup SHA及Maintenance前缀字节，均通过；拒绝测试worker混入。`r4/{active["FeedInspection"]}`另将releases.win.json每项名称/长度/SHA1/SHA256及legacy RELEASES每项名称/长度/SHA1与实际full/delta产物交叉核对。vpk对fast-hook位置的既有建议与未签名警告原文保留，不能与Release编译警告混为一类。包未签名。

GitTree=`{source["GitTree"]}`，严格只含 `src/**`、`tests/**`、`build/**`、`.github/**` 与 `Iris.slnx` / `Directory.Build.props` / `Directory.Packages.props` / `global.json`，共 {len(source["SourceFiles"])} 个源码/构建文件；排除 tasks/evidence、文档、输出、.codex及绑定自身。由 Git blob构造规范树；`r4/source_binding.py verify-head <commit>` 从最终提交同一集合重算并核对，不声称等于包含报告/绑定的整个提交树。工作区原始字节SHA与规范Git blob分别保存，换行警告原文保留。`r4/historical-delivery-binding-r3.json` 和 `historical-report-r3.md` 保存更正前原字节；R3原Tree实为较早index快照、绑定自身与最终提交不同，现不再自引用或以旧快照冒充最终证据树。

### 本轮真实来宾

{guest_section}

### 架构师侧已执行的独立复核范围

原始 `acceptance-review-4/independent-runtime-candidate2-1.log` / `.command.json` 及 `independent-runtime-candidate2-binding.json` 实际记录冻结candidate-2的生产拓扑回归，退出码0：

```text
{independent_summary}
```

独立绑定记录全部冻结输入保持不变，源码树为 `{independent_runtime["sourceGitTree"]}`。其外部安装/注册/重启边界为受控替身，证明范围是scoped/Dispatcher/helper准备、失败恢复与撤销；不能扩大为真实安装或线上路径。

`acceptance-review-4/candidate-check-2.json` / `.log` 实际记录final-7字节检查 {independent_bytes["checks"]} 项、通过 {independent_bytes["passed"]} 项、失败列表为空。`recovery-ui/after-observation-1/reviewed.json`记录架构师独立逐张查看修后8图及核对图片/实际主题/语言/加载DLL；这是对开发生成图片的独立观察，不冒称另行执行了修后渲染探针。该专用恢复界面的深色可读性仅在视觉范围确认关闭。上述历史结果限定候选2/源码树c6fd6c81f2d6b553d6b8a30390794c2d6a00683d/final-7，当前候选3的独立结果在下方单列，不用旧候选直接推定。

### 当前candidate-3 / final-8独立证据同步（不重跑建测）

架构师原始 `acceptance-review-4/independent-runtime-candidate3-1.log` / `.command.json` 记录冻结candidate-3的交接/语言与生产生命周期受控回归，exit0：

```text
{current_independent_summary}
```

`independent-runtime-candidate3-binding.json`核对xUnit原报告、{len(current_independent_runtime["workerResults"])}个worker结果及321个冻结输入保持，绑定源码树 `{current_independent_runtime["sourceGitTree"]}`。这些回归的安装/注册/重启边界仍为受控替身；不扩大为真实安装、公开线上路径或整单验收。

`candidate-check-3.json`实际记录当前final-8只读字节检查 {current_independent_bytes["checks"]} 项、通过 {current_independent_bytes["passed"]} 项、失败空。`localization-after-1/verification.json`及该目录README/原始两阶段命令记录当前独立审计：{current_independent_localization["sourceEntries"]}条源记录、{current_independent_localization["compiledResourceValuesMatched"]}项编译资源/格式匹配、未批准{current_independent_localization["unapproved"]}、资源错误{current_independent_localization["resourceErrors"]}，原{current_independent_localization["originalCatalogPairsUnchanged"]}对值不变，仅加批准{current_independent_localization["approvedNewPairs"]}对；例外SHA仍`{current_independent_localization["exceptionManifestSha256"]}`，未放宽规则。工程师仅同步这些原件及指纹，没有为同步证据重建或重跑审计。

`recovery-ui/candidate3-visual-carry-forward.json`按{len(current_independent_visual["frames"])}张当前PNG与此前逐张独立查看通过的PNG逐字节相同承接视觉复核，并独立绑定当前加载App DLL `{current_independent_visual["appDllSha256"]}`。它不声称新执行了一轮架构师渲染，也不是把旧候选直接扩大为当前源码证明。报告/绑定/续跑说明将当前独立结果与工程师Release/full、历史独立结果分别指向原文；源码和包未改。{remaining}

### 历史结果沿用与更正

R3安装原JSON实际24个观察、10个布尔断言；更新实际26个观察、17个布尔断言，两者 Failure=null 且无false。上方R3报告已按这两个实际类别更正。R3实际传入并应用full包，delta仅生成；没有为修正文案追加delta安装声明。R3旧测试没有接入生产updates/settings停止，也没有真实PrepareAsync，不能把R3报告中“重试可点击”的原断言扩大为生产恢复已通过，R4以本段新拓扑替代该结论。

未受R4产品改动影响的旧完整矩阵逐项沿用：中英文取消/保留/删除、保留后重装、删除后重装和锁定自有数据后的删除重试来自 `guest-matrix-4` 的release-0.2.0-6（输入安装器SHA=`7c00e7c731ca391a712ce03c06c7f2581b3ea0e28de923df8774a217f1e56e09`）；补充覆盖来自 `guest-coverage-final-2` / `guest-remaining-final-1` 的final-3；提交后native失败反馈来自 `guest-committed-failure-final-1` 的final-3。final-4安装与两版本更新只作为R3历史，通过范围不冒充R4新包。原手动/自动化方式、全部失败和截图局限继续沿用，未重做无关矩阵。

### 与工单的偏差及边界

1. `Iris.slnx`、App测试csproj与新增 `tests/Iris.UpdateHandoffWorker` 是验证真实进程/IPC/事务门禁必需的构建夹具；加入solution使Release不误选Debug。最新工单已明确将该worker、工程引用/复制规则与方案登记纳入本单测试接线范围，保证新鲜Release输出；worker不进入发布载荷，不改产品依赖版本，不放宽架构白名单；维护/平台产品代码未改。
2. 原两个 UpdateHandoffLifecycleTests去掉直接RollbackApply和手设bool的证明路径，改为实际安装命令、确认UI边界替身和真实helper责任转移；旧源码与失败均在R3复审证据和r4中间快照保留。普通Check不再可点，是保留关停封口；安装恢复改由真正存活的独立动作承担，并实测到执行结果，不以按钮代替行为。
3. 原设置scope清理顺序不变，只提前封准入；恢复专用窗口不创建普通VM。增加 UpdateHandoffEnvironment 的自有进程启动/目录删除窄替换边界，生产默认实现仍使用实际Process.Start与受限删除。
4. 本轮夹具失败（原文均保留）：新增主题回归首次编译CA1305已按InvariantCulture修正；directed-build-1 参数解析、directed-build-2的测试编译/分析错误、directed-tests-1给MTP传入不支持选项导致0测试、final-5模块发现、来宾入口缺失均保留原文/退出码，未当作产品通过或静默覆盖。实际系统补丁号与工单旧环境不同如上。
5. 架构师并行增加的 BACKLOG、handoff及复审/协调记录保持原样，工程师不修改其内容；.codex原样保留且不读取/提交。不push/reset/clean/rebase/amend，不动其它实例，无宿主真实Iris安装/更新/卸载。
6. 默认native下载在本网络环境失败后，按本会话架构师明确允许的第二种路径推进；补充原文在`architect-runtime-cache-supplement-20261025.md`。这是缓存条件下的干净缺失运行时引导，未把手动预装或HEAD成功当作native下载成功。夹具修前实际执行的安装脚本/输入SHA在guest-install-r4-2/r4-3保存；`guest-fixture-changes-1`另保留旧基础升级脚本和当前差异，明确旧基础不是已执行的修前R4升级证明。新增缓存/终态观测只改tasks夹具，冻结产品、final-8及本地审计规则不动；未修改宿主代理/证书或放松TLS。

标准用户拒绝提升、系统重启分支、跨用户、物理便携环境、GitHub线上匿名用户链路及最终NFR仍为**未验证**，沿用既有披露。C/T-040未开始，M6延期。R4初版、环境阻塞、范围及本地化补充快照均保留原字节；最新Sandbox恢复补充全文另归档为 `r4/{active["OrderSnapshot"]}`，与活动工单逐字节核对。工单在开发期间增加范围说明导致旧快照校验不一致的记录单列在 `r4/order-snapshot-drift-1.log`，随后绑定指向新快照，未覆盖旧件或改变校验。{remaining}
'''
report_path.write_text(text.rstrip() + "\n" + section, encoding="utf-8", newline="\n")
print("Updated R3 corrections and current R4 report from actual logs/JSON")
