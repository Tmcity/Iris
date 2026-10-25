# Iris 架构师接手记录

**当前有效状态（2026-10-25，覆盖下方R4阻塞/未完成历史）**：T-039B/R4在最终535ec169…（主实现0bc11fc7…）独立验收通过，Release零警告错误，全量3728/3726成功/0失败/2既有跳过。candidate-3/final-8的本地恢复15/15、来源1258项、审计和视觉通过；两项实际来宾已成功，142项独立复核、两图及真实终态确认。缓存辅助由来宾内官方签名/SHA512验证、Setup实际安装，未预装.NET；默认native下载失败未关闭，C必须核对默认完整引导及正式匿名用户UI线上升级。结论 `T-039B.review/acceptance-review-4/FINAL_REVIEW.md`，旧失败/候选和用户工具保持；无运行中自有Sandbox，架构师最终建测已结束。下一步签T-039C，父T-039保持进行中，T-040随后，M6延期，尚未push/发布。

**最新收尾（2026-10-25，仍未整单完成）**：R4 首轮本地通过后，架构师又实际发现完整 LOCALIZATION 审计缺项（5个新资源目录键与6个未批准站点），已在同一工单修正，source/compiled/catalog 独立修后审计通过，例外清单未放宽。当前为 candidate-3 / final-8，源码树 `d7ca8024ba8a7feea9fa75c61d7785b1edd77b5a`；开发全量3728/3726成功/0失败/2既有跳过，架构师新候选独立15/15通过、1258项字节核对匹配。旧包/旧快照/所有失败保留。真实来宾仍因 Sandbox Disabled、无当前可用 VM 权限而待用户回复；/NoRestart 脚本没有执行。架构师已明确归还运行窗口，工程师只同步可审报告，不重复建测、最终提交或结案。详见 `acceptance-review-4/PARTIAL_REVIEW.md`、`localization-after-1`、`independent-runtime-candidate3-binding.json` 及 r4/active_round.json，接续须刷新真实状态。

**最新跟进（2026-10-25，本轮尚未完成）**：R4 首轮开发 Release/full 为 3720/3718 成功/0 失败/2 既有跳过，真实 scoped/Dispatcher/helper 回归已补；final-5 清单阶段失败被保留，final-6 两版本生成成功。架构师独立 1258 项源/固定程序集/包字节核对匹配，但随后在真实应用 Dark 主题的独立双进程界面复核中发现恢复动作不可读（两语言×两宽四张失败，Light 四张可读），已实际送达并由工程师确认修复，见 `T-039B.review/acceptance-review-4/source-feedback-2`。当前工程师执行轮 `01a16f81-5efb-7840-a578-f40e756f437b`，仍同一 R4；架构师已明确归还建测窗口，不能用首轮全绿或 final-6 当最终验收。

真实来宾另受环境限制：宿主现为 Windows 26200，Sandbox 功能 Disabled（CIM InstallState=2），wsb/WindowsSandbox 入口缺失；Hyper-V 存在但当前账户读取被拒绝。架构师已就 /NoRestart 启用方案或现有 VM 入口向用户发出一次待答问题；**没有授权回复、没有执行宿主变更**。待批准脚本与独立预检见 R4 coordination / `host-isolation-preflight-1.json`。工单已追加环境补充，必须保留 current_task，未创建最终提交；工程师只负责本地修复/新包和绑定，环境由架构师处理，不重复向用户索要。B/父单保持进行中。

**最新有效状态（2026-10-25，覆盖下方历史执行/传话安排）**：用户目标为持续推进至本轮开发完成，M6/T-033 仍延期。HEAD `05452e0a85a36982ef8bfd22d5a981e581eeafcf` 为 T-039B-R3；B 尚未验收，唯一工单 `tasks/current_task.md` 是 T-039B-R4。R3 独立 Release 0 警告/错误、全量 3707/3705 成功/0 失败/2 既有跳过，final-4 指纹核对通过；但真实更新停止依赖接入后候选丢失、无法恢复的反例实际失败，见 `T-039B.review/acceptance-review-3/README.md`。不要因测试全绿或旧包成功而结案。

工程师已改为用户创建的新会话 `01a16ef2-a0bb-71a1-8230-57165c05cc5b`，架构师直接传单/协调和独立复审，覆盖此前 Gemini / 用户手动传话的临时安排。R4 执行轮 `01a16efb-17fe-7c13-b06d-d96408ee117c` 已实际读取并确认基线/边界，正在实施；交接证据和增量只读观察工具见 `T-039B.review/coordination/r4-handoff-20261025/`。架构师当前无构建测试占用，窗口交给工程师；接续须刷新实际线程/进程/工作树，不依据本段断言某进程仍活着。

下一验收准备见 `T-039B.review/acceptance-review-4/REVIEW_GATES.md`，尚未形成通过结论。历史两个更新取消/跨进程锁探针已按字节补归档至 `update-final-cancel/executed-candidate-1`，9 个程序集与旧 candidate-1 匹配，但不代表 R3/R4 最终代码通过。B 通过后才签 C（公开 GitHub 资产和真实匿名用户 UI 在线升级），再做 T-040 全功能/NFR；C/T-040 当前未签。远端匿名只读仍为初始 main `877491e…`，不得丢远端/本地历史；B 不 push、不发布。`.codex/` 用户工具原样保留、不提交，不读私钥，真实安装仅在自有 Sandbox。

**载荷清单已独立闭环，最终冷安装仍在观察（2026-10-15）**：开发接收了包清单补充，当前仍01a13e10-afe8-7e51-98e0-1650c3ed77c1，最后itemCount159，未交付。0.2.0-final-3/0.2.1-final-3的实际97项载荷清单、逐项大小/SHA、顶层7/9项以及外层附加载荷均独立匹配；staging另名保存，见package-final-3-observation，关闭该元数据缺陷。原final-2与C盘不足失败不覆盖。当前自有Sandbox为69e00710-ca7b-4fb0-83a7-6ceaa37781c2：外围20分钟观察期限已失败，原安装器/Setup仍在运行时下载阶段，尚无最终安装完成；开发已声明继续观察原进程、不重复启动。宿主官方运行时下载也慢，原下载记录有超时与部分字节；只供后续隔离诊断，不在宿主安装。接续先查实际进程/最新intent，不擅停此诊断来宾。架构师无构建/测试窗口占用，两个独立更新探针仍未编译/执行，完整B/C/父目标继续。

**最新独立复核与待补清单（2026-10-15）**：guest-update-2无诊断干预的本地0.2.0→0.2.1路径已独立核对16个必要观察、91个输入指纹并查看新窗口；settings/secrets/history/log四类实际服务读验通过，4份数据文件报告哈希前后相同，见guest-update-2-observation。须保留控制父进程由专用宿主替换为PowerShell的边界，C在线真实UI链未验证。update-ui-2的64图也已通过8张联系表逐格查看并绑定，四个状态/两语言/两主题/两尺寸可读，不扩大为所有状态或安装按钮已执行。原独立运行探针仍待最终稳定构建的串行复验。

实际PS5.1打包先因C盘临时空间不足失败，开发仅将该打包进程及子进程TEMP/TMP切到F盘专用目录，随后0.2.0-final-2/0.2.1-final-2均生成成功。架构师校验顶层文件哈希与外层载荷SHA一致，但发现payload-manifest为102项staging清单，实际包内97项（排除8个PDB，新增3个vpk文件）；已作为确定的元数据偏差发入当前开发轮，userMessage 01a13e88-84f6-77b0-b7ec-2683ae927374，见package-final-2-observation及coordination/package-manifest-receipt.json，等待修正实际包清单/重新绑定。不是包内容被破坏，不重复发送。

开发当前继续01a13e10-afe8-7e51-98e0-1650c3ed77c1，未交付/未提交/未结案B。最终包的干净来宾、数据/自启动/快捷方式、稳定完成图和提交后失败提示等正在收尾；旧2ed0bc7d来宾已由开发收尾，后续有新自有实例，接续须从最新intent与wsb实际列表核对，勿沿用旧存活假设。架构师无构建/测试窗口占用，不push，不动.codex。

**当前开发轮已因限流接续，仍为完整B（2026-10-15）**：原轮01a13b95-7293-7130-9c6a-562e43d01b22已明确failed/completedAt非空，错误rateLimitExceeded。架构师在同一开发聊天发送接续消息01a13e10-9cd2-7660-85c4-e5e501733a08，现执行轮01a13e10-afe8-7e51-98e0-1650c3ed77c1，开发已明确确认接续；不是重开聊天，不改模型，不重复启动来宾工作。final-coverage-notes.md已被实际读取，要求补稳定完成图、内容级数据/系统注册清理、实际PS5.1打包、最终源码产物绑定。当前架构师仍未占用构建/测试窗口。

**新独立观察（2026-10-15）**：guest-install-5严格静默保留已独立逐项核对，源输入匹配；matrix-4的129项记录中54个必要断言另行确认，6个双语选择/重装/删除失败重试路径功能通过。11张图已逐格查看，10张可读，zh-delete-completed仅标题栏，待补稳定实际图；详见两个observation目录。系统标准按钮语言按LOCALIZATION第19行允许，不擅加翻译要求。PS5.1只做了静态语法解析，未执行完整打包；包装器初轮Get-FileHash失败及导入系统模块后的成功均保留。

guest-update-1引擎记录实际0.2.1/NativeExit0/ParentExit0，外围却exactly-one-restart=false失败；process-diagnostic.json曾记录新App PID3088，开发随后作诊断Exit，不能据外围后续无进程断言从未重启。详见guest-update-1-observation.md；Run-Child等待重定向输出EOF而新App可能继承写句柄仅为待验证线索，未冒称独立实测根因。此线索已从实际界面发送一次，接收绑定见coordination/local-update-observation-receipt.json，不再排第二份CLI消息。自有Sandbox当前使用2ed0bc7d-2497-4e88-9f4e-e000b9105fde，保留用于后续工作，结束时必须显式停止自有实例；旧1035d4ff环境已停止，不能沿用下方旧保留状态。

**最新为guest-install-4严格判定失败并保留来宾（2026-10-15）**：开发仍在B执行轮01a13b95-7293-7130-9c6a-562e43d01b22，最近itemCount214，未交付。修订脚本已实际观察外部Maintenance PID5044及启动时间/路径，入口exit0后外部拥有者exit2，因此maintenance-exit-zero-5044=false，整轮明确失败；安装、无.NET→10.0.13、实际窗口等前置检查为真。开发在定位安全阶段记录，不把入口成功当卸载成功。自有Sandbox 1035d4ff-d461-4bab-b9a4-f9a006808d27由--retain保留，见T-039B.evidence/guest-install-4/retained.json；接续先查询其真实状态并与开发协调，不擅自停止或重启该仍用于诊断的来宾。曾查询的output/maintenance-failure-1.json不存在，不能据文件名猜测根因。原guest-install-3失败和独立观察仍保留。HEAD仍6256a3fb，当前无架构师构建/测试窗口占用。

**独立更新探针准备（2026-10-15，未执行）**：T-039B.review/update-final-cancel现有Program.cs、CrossProcessSdkLock.cs和prepare.py。默认场景验证资源Dispose期间才开始的实际取消回调仍被join；--cross-process-lock用自有worker实际持有.velopack_lock，只在SDK子类观察base.AcquireUpdateLock真实任务，验证取消仍等待外部锁且放行后零源下载。两者均未编译/运行，不能算通过或新增实测失败；prepare.py也尚未执行。后续在稳定最终构建与另行协调的短串行窗口绑定程序集/源码后执行，保留所有编译与运行结果。架构师当前无构建/测试/VM进程占用，开发继续原B修正和隔离验收。

**已看到真正安装成功，但整轮静默卸载失败（2026-10-15）**：guest-install-3的自有Sandbox cc685426-67ef-4b92-bd57-89334d6990fa实际输出已独立核对：无共享.NET10→真实安装入口exit0→共享Core/Desktop10.0.13，Iris0.2.0及Maintenance卸载注册存在，安装未自启，UiaHost直接启动/EOF exit0，实际英文设置窗口已查看。整轮result的Failure为“Uninstall did not remove the installed app.”，不可据安装成功结案。原始报告快照、安装器与截图SHA及观察范围在T-039B.review/guest-install-3-observation。来宾管理员状态true，未验证非管理员依赖安装。开发继续处理失败及原完整矩阵，架构师未占用构建/测试窗口。

R4来宾判定补充已由架构师实际界面发送，进入原B执行轮的userMessage 01a13c06-cb5b-7290-ba9b-5d8f33511ef2；正文仅被UI加尾换行，接收见coordination/source-review-4-ui-receipt.json，开发也明确确认。要求关键false/缺失条件失败、等待外部维护拥有者终态后才结束场景；新脚本已经在修改，旧轮次结果必须保留。B.evidence已补* -text保存原始字节，.codex仍未修改。

**B已恢复并接收R3，当前无架构师占用的验证窗口（2026-10-15）**：归还通知01a13b93-955e-78d1-ba0a-529e7f9bbf3b已使原开发聊天进入执行轮01a13b95-7293-7130-9c6a-562e43d01b22，开发明确确认恢复构建/测试且技术协调不再交给用户。R3新增的两条源码意见已通过实际界面发送进入同一轮，userMessage 01a13bcc-2bdb-7b20-aae7-e0cbd333853f，开发明确“收到source-review-3”。见coordination/source-review-3-ui-receipt.json；除UI追加一个末尾换行外消息内容完全保持。此次CLI队列项在官方queue/list可见但目标界面未显示可引导条，已通过thread/queue/delete只移除自发R3原项并确认deleted=true，再从已核对为空的开发输入框一次发送，未重复投递，未改队列数据库或.codex工具。当前版本界面发送后立即进入运行轮，无需点击不存在的额外引导按钮；保留当次截图和工具结果，不复用旧坐标。

开发已重生成安装包；首轮真实Sandbox在来宾脚本运行前因ExistingLogin未就绪失败，尚未运行安装器，已停止自有实例，正在修复就绪检查。R3仍为源码审查，完整B未交付/未验收。架构师的update-final-cancel仍仅为未编译草稿；开发已另行发现并修正资源Dispose期间的取消封口时序，需以实际定向/最终候选证据验收。

**维护独立重放通过，串行窗口归还（2026-10-15）**：2026-10-13已对固定bda86bce候选复用原探针二进制完成两项独立重放，child-ownership-after-1与recovery-same-owner-after-1均Passed=true/exit0；具体边界与绑定见T-039B.review/native-child-ownership/README.md及after-1-binding.json。原失败保留，未运行真实安装器/用户数据。开发原B轮现已明确completed，仍未交付；本次继续同一B，恢复构建/测试，不再保留旧串行暂停。架构师当前没有在运行的构建/测试；后续独立运行另行串行协调，不让开发持续等待。update-final-cancel仅为未编译/未执行草稿，不是新失败或已通过证据。通信仍由架构师发送并在运行中实际插队，不让用户代答技术资源协调问题。

**已恢复架构师实际插队（2026-10-13）**：通信/串行复验补充01a131b7-2bd9-7f73-89ad-7290395a28d8已发送，并实际点击指定开发聊天队列的“引导”。点击前核对目标聊天及消息、窗口身份/边界/前台/遮挡；点击后截图显示消息进入正文，App Server又在原B执行轮读到对应userMessage 01a131bd-657b-7643-a7b8-321c054b1669。见T-039B.review/coordination/steer-20261013.json。node_repl两次初始化均报kernel assets/os error3，Windows原生窗口操作成功；不需要用户代点，不重复两条已接收审查。已请开发在维护定向修正后提供串行复验窗口，尚未收到可运行通知，架构师暂只读代码/准备探针。

**通信规则与B接收状态更新（2026-10-13，覆盖下方旧安排）**：用户明确“后续补充由你点击插队发送”，后续由架构师发送补充并执行插队/引导，不再等待用户代点。开发原执行轮01a1312f-18f0-7bf2-bd9e-76c2367237ee已明确回复“两组补充已收到”，并实际读取source-review-1/2和current_task尾部，开始优先修复更新与维护资源责任。消息01a13175-62b1-7a23-a1f2-56a6e701a3bd和01a13194-f4fc-7a02-a0b5-fb3c4dfd06e3无需重发；本次架构师尚未进行UI点击，不能把已接收误记为代点成功。B继续原轮，未交付/未验收；现有maintenance-tests-3.log只证明5/5初轮定向测试通过，不覆盖独立发现的两条失败或实际安装验收。

**T-039B维护拥有关系已独立发现两条失败（2026-10-13）**：native-child-ownership固定真实Maintenance程序集，自有门闩worker证明started记录回调抛错后Run已fault返回而child仍活着；探针最终放行/join，child exit0，before-2无探针错误。另用真实MaintenanceEngine/文件gate、fake系统与删除，证明同一helper在首次数据清理失败后Recover仍被拒，DeleteAttempts=1且事务继续封口，recovery-same-owner-before-1 exit1。均未运行真实安装器或触及用户数据，原件/指纹保留。source-review-2另列撤销/EOF模态框、阶段预算及不明事务恢复的源码意见，尚不算实测。补充01a13194-f4fc-7a02-a0b5-fb3c4dfd06e3已排队，用户点击引导，以实际接收为准；B继续原轮、不重发、不另签任务。

**T-039B独立封装预检完成，更新底座已发源码预审（2026-10-13）**：开发继续执行轮01a1312f-18f0-7bf2-bd9e-76c2367237ee，已有偏好/HTTP/协调器/维护身份与gate代码，尚未交付。架构师自有.NET10单文件附加载荷在干净离线Win11 26100、前后无共享.NET10的环境中3项通过，沙箱已停止；仅合成载荷，不是真正Iris安装器。证据T-039B.review/installer-envelope，首轮模块归属假设、PowerShell空退出码的失败均保留。可行性补充01a13161-da8f-7461-ac0b-dc867bcc8528已被开发实际确认收到；协调器通知异常/实际任务、同步取消与旧_stop竞争、PrepareApply归属等源码预审已绑定source-review-1，补充01a13175-62b1-7a23-a1f2-56a6e701a3bd已排队，后者仍以实际接收为准。不重发、不代点；源码预审不是已执行失败。B和父任务均未验收。

**T-039B已实际接单（2026-10-13）**：工单消息`01a1312f-038c-7bb3-a85c-1ccf7fbc1b56`进入执行轮`01a1312f-18f0-7bf2-bd9e-76c2367237ee`，开发明确接单并开始读取App/Providers/设置及Sandbox资料。继续该轮，不重复发单、不把暂时interrupted且completedAt为空判作结束。基线6256a3fb，current_task / DESKTOP_RELEASE.md为唯一范围，架构师准备的声明/规范/原编译失败随B保留；仍由用户点击补充引导。

**当前唯一工单T-039B（2026-10-13，用户确认开发恢复）**：A在`6256a3fba611b2cd94fc8dfa32480584c16d7bfe`恢复提交并独立验收，工作区原仅.codex未跟踪。现按DESKTOP_RELEASE.md / ARCHv1.57 / CCR039正式实施打包、完整更新UI/网络/生命周期、self-contained独立Maintenance入口与干净Windows实证，主App/UiaHost保持框架依赖。Core只兼容CheckForUpdates字段，Providers只增Updates常量/专用传输；CONTRACTS独立编译已0警告错误，原缺Errors using的CS0246保留在T-039B.review。B交付真实产品/本地实证，C保留公开GitHub线上分发/升级闭环，父T-039未结案。开发仍原会话，一次一单、不新增聊天/子代理；补充引导由用户点击，不push、不动.codex，M6延期。

**T-039A恢复归档并独立验收（2026-10-13）**：开发完成final-release/full-4后多轮服务繁忙，A报告与本地提交未完成；架构师在现有成果上恢复归档，未重写生产。2026-10-13独立Release0警告错误、全量3660/3658成功/0失败/2既有环境跳过，640项输入保持；已运行的独立基线2失败/候选2通过及13项SDK bootstrap/检查下载取消重跑均与最终源/程序集绑定相符。Guard先等待body再清理的补充已落实并进入78项定向/最终全量。正式结论T-039A.review/FINAL_REVIEW.md，报告T-039A.md；原失败/中止保留，截断ZIP移至自有artifacts保留，证据目录禁止EOL改写以维护原始指纹。A结案，父T-039仍须正式安装/更新/卸载和干净Windows验收；后续工单由架构师继续冻结/签发，开发仍原聊天，M6延期、T-040未开始。补充引导由用户点击，不push、不动.codex。

**T-039A候选通过定向/Release，夹具收尾仍待复核（2026-10-10 19:08）**：生产最小修改为IpcPipeFactory精确ERROR_NO_DATA同句柄复位及NativeMethods单项DisconnectNamedPipe；新增6项定向、SystemIntegration77项通过，final-release-3零警告错误。原BOOL编译失败、筛选0项exit5、全量中止与修正前3项失败均保留。final-full-3于19:05开始，尚无完成结果。新Guard夹具存在body未结束即遍历Clients、原裸I/O未全接Guard的源码预审点；current_task已要求完整收尾，补充01a12575-3b33-7a43-a980-be9828074ec9已排队待实际接收（用户点击引导）。这是源码审查，非已运行失败。A未交付/提交；不与开发并发构建/测试，独立baseline/fixed早断开探针仍待运行。

**T-039A补充已实际接收（2026-10-10 18:36）**：原执行轮明确确认公开Tmcity/Iris目标、当前不发布及IPC受控补单，并读取current_task末26行。无需重复发送或代点引导。开发已用真实随机管道门闩取得Accept IOException/800700E8、managed Disconnect InvalidOperationException/80131509，修正前三项失败保留。最新补单允许原两个IPC实现文件、直接测试，以及必要时NativeMethods.txt仅追加DisconnectNamedPipe；其它生产/契约/依赖/build保持。当前转入最小修复，A与父T-039仍未交付验收。

**T-039A IPC转储定位与进程收尾（2026-10-10 18:20）**：独立离线转储已确认挂起在TruncatedWireDisconnectDoesNotDispatchAndNextConnectionStillWorks第二次连接，其服务器loop已fault、未进入连接对象/Disconnect；精确Win32原因仍待真实门闩复现。证据T-039A.review/ipc-hang，另有未运行的独立ipc-early-close探针及基线DLL副本。开发保存转储后终止本轮自有平台测试，父test失败退出；final-full-2原汇总3651/3648成功/1失败/2跳过、exit -1（command.json为4294967295），不是完整基线3653项全绿。当前开发继续受控诊断，生产尚未修改；IPC补单与独立诊断两条消息已排队，仍由用户点击引导。公开仓库匿名Releases API已验证200、当前0个Release，证据github-release-state.json。

**T-039A受控补单：IPC故障先修复（2026-10-10）**：开发会话已收到用户选择“先补签最小IPC诊断／修正范围，完成后再交付”（01a12517-452c-7973-87b2-9b75e7e879de）。current_task末尾现已正式限定NamedPipeIpcServer/IpcPipeFactory及直接IPC测试夹具，保留原失败与确定性修正前后证据，不吞真实清理故障、不丢FirstPipeInstance/原对象重试。默认环境final-full-2仍有清理失败和未结束进程，两者是否同因未确认；先诊断并安全收尾，构建/测试串行。A暂不交付，Velopack生产接入/发布未开放；其它生产、契约、包/default零改动。公开GitHub目标与用户点击补充引导规则保持。

**发布目标已改为公开仓库（2026-10-10，覆盖此前暂时私有决定）**：用户已将 `https://github.com/Tmcity/Iris` 设为公开，并明确就用此仓库。架构师无认证HTTP实际返回200且repository_public=true，证据T-039A.review/github-public-access.json。origin/main只读获取成功，目前为877491e初始README；本地master/历史保持，未merge或push。后续安装包与正式匿名更新源以该仓库的GitHub Releases为目标，不能包含部署凭据。A实证仍在继续，消息引导仍由用户点击。

**T-039A补充已接收，隔离环境预检通过（2026-10-10）**：开发在原执行轮01a1248b-0637-7393-80f4-01b88b95f994已明确接收私有源/SDK补充，无需重发。实际确认RID发布UiaHost位置不符、SDK跳过损坏既有包校验、非合作下载取消后仍可能完成包落盘，正在扩充真实锁/校验/清理阶段和卸载建议。架构师用已安装wsb CLI创建并停止自有离线Sandbox，OS为Windows11企业版10.0.26100，x64共享.NET目录为空；只共享本次空白输出目录并查询环境，未安装或运行Iris。证据T-039A.review/sandbox-preflight。此前Hyper-V/DISM权限错误并未阻止这条可用路径，不需据此先修改宿主权限；真实安装验收仍未执行。

**最新协作方式（2026-10-10，用户更正）**：工单及补充消息仍由架构师发送，补充消息的“引导”由用户点击；架构师不再自动点击Codex消息引导按钮。该更正覆盖此前“发送后自动代点”的要求，不暂停项目开发。当前T-039A SDK/私有仓库补充`01a124ca-b07d-7183-a4d1-dbc61dd1f025`已排队，架构师尚未点击，无需重发；是否进入开发当前轮以实际接收记录为准。

**T-039发布与连接最新约束（2026-10-10）**：用户选择先维持 `Tmcity/Iris` 私有，发布方式稍后确定。已在本机准备专用SSH密钥，用户将公钥添加到仓库Deploy keys；严格主机验证下SSH返回 `Hi Tmcity/Iris! You've successfully authenticated`，仓库只读连接成功。origin现指向该私有仓库，专用SSH配置仅在本地.git/config；私钥在用户SSH目录，不进入源码/证据/客户端，不记录密钥内容。未push或发布。后续客户端更新源须可配置，未配置时明确不可用，不能内置仓库访问凭据或将私有源不可达冒称已是最新。当前仍仅T-039A；GitHub公开分发与真实安装验证尚未完成。

**用户已恢复推进，当前T-039A（2026-10-10，覆盖暂停记录）**：用户明确“继续”，并指定GitHub Releases目标 `https://github.com/Tmcity/Iris`。T-038/CCR038/R1结案保持，基线6f14e81ebd298d83eda7330008310015efa1937b。父T-039完整安装/更新/卸载要求保留，A先用匹配版本Velopack/vpk、实际发布副本和无副作用最小应用证明产物、SDK取消/退出、卸载交互及.NET10运行时路径，随后冻结正式生产契约。current_task已签；开发仍01a11b01-c46b-74e1-9015-726c77ffbc9e，不创建新对话/子代理。用户给的是SSH部署密钥指纹，本机初查无匹配公钥/可用agent、无gh；凭据由架构师继续核对，不存入源码。Hyper-V/可选功能查询实际受权限限制，VM安装尚未尝试。A不得在宿主真实安装或运行Iris，不把原型冒称父任务完成，不push；M6仍延期。

**T-038/CCR038/R1已独立结案，等待用户继续（2026-10-10，最新状态）**：功能9c1ac9d0a044e2f844e82957c841b785da9d88e5、R1修正30177c590d8e05e2161b6e99d3fe6385d68e73fb。R1只修测试等待与夹具观察，原断言保留，生产/契约/资源/布局零差异。提交后独立Release零警告错误、原失败定向通过、全量3653（3651成功、0失败、2既有环境跳过），全部进程exit0；最终410张实际图与逐图查看版本完全匹配。完整审计、已审阅资源逐键比较、固定程序集十组窗口/JSON/菜单/生产Main语言作用域探针与STA退出通过，源/程序集/证据指纹保留。验收总记录T-038.review/README.md及T-038-R1.review/README.md；父单失败全量、误通过审计、原视觉缺陷和夹具退出失败均未覆盖。用户已明确T-038后停止，因此当前没有待实施工单，T-039及后续不签发，直到用户说“继续”；M6继续延期，真实桌面/联网/多屏/NFR未验证。

**2026-10-10最终视觉/审计已独立补齐，仍待R1**：visual-12的61张差异联系表已全部实际查看，196张仅以精确字节相同继承visual-3；提交后独立全量生成的410张图与此已查看版本全部匹配，见T-038.review/visual-candidate-12-review.json及final-visual-1-compare.json。最后独立全仓审计通过（1197项、1170个编译资源值；App437/Core148），见final-audit-1.log；原审计反例的盘点器/检查器/App源及相关例外位置与次数映射仍与最终实现一致，transfer文件保留。开发已在R1执行轮`01a11ef0-bbce-71b1-b133-d6d1c4030a47`明确接单并实施受控门闩，不重发、不把持久化interrupted视为终止。父单全量的外部语言合并等待失败未关闭，R1完成后需最终复验并按用户要求暂停。

**T-038独立全量未通过，唯一修正T-038-R1（2026-10-09）**：开发已在`9c1ac9d0a044e2f844e82957c841b785da9d88e5`交付。提交后独立Release0警告错误，658项源码/4862项交付证据绑定匹配；独立全量3653/3650成功/1失败/2既有跳过，exit2，失败为新增语言外部合并测试第88行预期en-US实际zh-CN。日志在T-038.review/final-full-1，410张本次实际渲染保留。UiDispatcher夹具两次异步UI投递与单次ApplicationIdle之间存在未证明的等待边界，current_task已签T-038-R1，仅定位/修正测试同步并确定性验证，不擅改生产。T-038/CCR038仍未结案；修正和独立验收完成后按用户要求暂停，T-039不启动。

**T-038独立运行候选已完整通过（2026-10-09）**：固定程序集副本的语言/真实JSON/联合设置/实际菜单/生产Main清理作用域探针全部通过，并确认自有STA退出、进程exit0，见 `T-038.review/runtime-final-candidate-3.log`、`runtime-final-candidate-result.json`、`runtime-final-source`。前两次夹具即时开关Popup后的STA退出超时原样保留；排除菜单诊断正常退出，等待实际Popup动画并显式结束Application后全场景通过，未改产品。当前工作区部分DLL已因开发后续构建变化，结果仅绑定固定候选，最终提交后需重绑复验。英文App/Core与实际已审阅资源快照逐键一致，详见 `resource-text-review`。仍待正式交付、最终独立Release/full与完整最终布局验收；T-038完成后按用户要求暂停。

**T-038原审计反例独立复验通过（2026-10-09）**：当前盘点器源码在自有副本独立重建0警告错误，原同字串TextBlock反例重新盘点；基线3项exit0，新增UI后4项exit1且未批准1项，资源错误为空，绑定输入运行前后不变。原误通过证据保留，修正后证据 `T-038.review/audit-exception-after`，包含完整检查器源/资源/日志与SHA，无二进制。此为原场景独立复验，不代替最终全仓审计、语言生命周期或最终视觉验收。

**用户指定T-038结束后暂停（2026-10-09，最新约束）**：用户明确“T-038完成后请先停止工作，直到我说继续后再推进”。当前继续完成T-038开发交付及独立验收，验收结案后将项目目标暂停；T-039及后续工作等待用户明确继续。此约束覆盖下方自动签发下一单的历史安排，不改变T-038完整范围或已接受里程碑。

**T-038视觉补充已实际接收（2026-10-09）**：当前架构会话的Computer Use连接成功；视觉补充消息`01a11c7b-d964-7ad3-988b-f263e1600727`已点击“引导”并移出排队区，开发在原T-038执行轮`01a11bc2-3297-7200-a4b7-59b1d7011778`明确“收到接续信息，继续当前 T-038，不重启任务”，随后实际读取current_task末尾与visual-findings-1。无需用户点击或重发；接收不等于最终修复验收。旧架构会话的os error3不代表本会话连接失败，未修改插件/其它进程。

**2026-10-09 架构师对话接续**：用户指定接续 `01a1153d-bb43-7322-8940-9ba8587bc1e0`，当前在 `01a11c78-026b-74c3-9f44-18c91faf9aa3` 继续原架构师/独立评审职责；开发仍 `01a11b01-c46b-74e1-9015-726c77ffbc9e` 的T-038执行轮，不重复派单。旧架构对话最后因400/input失败；已读取其本地实际记录和当前工作区，T-037结案保持，T-038尚未提交。开发最新已报告语言清理与审计上下文/次数修正，待独立复验。visual-3已查看部分的英文快捷键安全说明截断、正常翻译设置添加按钮不可见，以及小翻译窗口语言值难辨识已加入current_task，候选图证据见T-038.review/visual-findings-1；不把候选问题混称最终源码失败。三条既有补充已实际接收，不重发。继续完成T-038、T-039及T-040，M6延期与其它边界不变。

**2026-10-09 三条T-038补充已实际接收**：用户已回复“点击了”，随后开发在原执行轮01a11bc2-3297-7200-a4b7-59b1d7011778明确“已将这些补充并入 T-038”，逐项确认生产语言作用域覆盖最终清理反馈、按稳定Key本地化预设菜单、审计增加用途上下文与出现次数约束并重跑独立变异反例。对应消息01a11bda-47f9-7901-8287-a2aa7e48b754、01a11c04-4b93-7c52-8a22-230704644b46、01a11c47-6b03-7562-9cef-83893166ceee均不重发、不再请用户点击。继续本轮T-038评审；接收确认不等同修复验收。此前Computer Use在执行JavaScript前报failed to write kernel assets / os error3，未进行点击或猜测修复其它进程，CLI排队不曾被当作即时送达。

**T-038 审计遗漏已独立复现（2026-10-09）**：actual Roslyn inventory+audit.py在最小自有副本的基线3项exit0，新增同字串WPF TextBlock.Text后4项仍exit0/unapproved0，错误复用旧日志指纹。检查器源码与当前版本匹配；证据tasks/done/T-038.review/audit-exception-before（不含二进制）及临时同名probe。已追加current_task要求修复后真实反例重验。这条已是实际审计失败，先前语言scope提示仍为源码预审；不能相互混称。

**T-038 开发期六组独立窗口/资源探针通过**：确认无其它Iris构建/测试进程后，对resource-build-3候选运行，candidate-runtime-1.log六组通过：固定文化跨异文化线程、实际语言ComboBox与History联合一次保存、真实JSON重新加载后选择语言、联合冲突保留/Reload、未编辑字段外部合并、Core非法语言零写入。探针期间仅检测到开发正在编辑UiLanguageEditingTests.cs，产品文件及引用程序集保持，绑定candidate-runtime-inputs/result，实际夹具源candidate-runtime-source。夹具继承的未使用可选HTTP委托有CS0649警告，日志保留，不冒称产品警告。此前App424/Core148资源键对齐、365+175+31候选英译均已审阅；这不代替最终布局、全部审计或生产Main清理作用域验收。临时主目录iris-t038-independent，仍等当前开发交付。

**T-038 文案预审补充**：365项候选App英译已逐项对照，关键含义保持，快照与复核清单在临时iris-t038-independent/app-en-candidate-1.json及app-en-review-1.json；尚非最终资源/视觉验收。例外中的ProviderProfiles预设DisplayName目前仍直接绑定添加菜单Header，已要求显示层按稳定Id本地化，已保存名称不改；同时要求审计防止同字串新UI用法借旧日志指纹豁免。均已追加current_task，不算运行失败。语言作用域补充消息01a11bda-47f9-7901-8287-a2aa7e48b754仍以实际接收记录为准，不重发。

**T-038 开发期语言作用域预审**：早期BeginRun只包DesktopStartupRuntime.RunApplication，窗口循环结束后Program.RunPrimary尚有Host/日志清理及失败提示，可能先恢复为系统语言。已追加current_task，要求真实生产作用域覆盖最终清理反馈，并以所选en-US/环境zh-CN验证；不是已执行失败，不允许测试外层作用域掩盖。临时iris-t038-independent/baseline保存19e15f67的360项UiText与Core错误源，供后续语义核对。

**T-038 已实际接单**：消息01a11bc2-271c-7812-baae-5406526448c4对应执行轮01a11bc2-3297-7200-a4b7-59b1d7011778。开发明确接单，已读取current_task/LOCALIZATION/GEMINI及CCR038和资源现状；继续该轮，不重复下单，不把interrupted且completedAt为空误判终止。下一步独立检查语言联合编辑、跨线程稳定文化、全部资源和实际英文布局，等待最终交付后串行复验。

**当前唯一 T-038（2026-10-08）**：T-037/CCR037在19e15f6748b71aad0886c926e02a9542a347aa9b独立结案，Release0警告错误、全量3624/3622成功/0失败/2既有环境跳过，最终六组实际管道/取消/未Start Host探针通过，621项源码与候选相同、48张网络回归图匹配。证据T-037.review已补齐。开发最终收尾轮01a11b99-14d6-7ea2-9e04-e5c35d008fea已completed并提交，不再等待旧T-037轮。current_task为完整T-038，按LOCALIZATION.md/CCR-038/ARCHv1.56；GeneralSettingsPatch仅兼容UiLanguage init属性，资源涵盖App/Core用户文案，保持本次语言与下次启动语义。开发仍01a11b01-c46b-74e1-9015-726c77ffbc9e，M6延期，不push，.codex不动。

**T-037 开发全量已通过，待交付/独立复验**：platform-tests-delivery-2为42/42，app-tests-6为51/51；full-test-delivery-1为3624总计、3622成功、0失败、2既有环境跳过。管道缓冲为入站4096字节/出站64字节，ACK后有界等待客户端结束；不得将首轮未进入写完状态的测试失败描述为ACK丢失。当前尚无T-037正式提交，独立六组探针仍未运行。待提交后重建并绑定，确认所有补充均纳入，再结案/签T-038。

**T-037 ACK补充已接收并处理**：同一主执行轮已明确读到工单末尾的ACK要求。首轮延迟读取测试因默认缓冲下Write本身未完成而未进入目标场景，不能称ACK丢失；调整为明确的小型有界管道缓冲后，实际延迟读取/超时后再接入两场景通过，日志platform-tests-delivery-2。取消补充的修正前6项失败另见cancellation-fences-before.log，修正前源码保留。当前正在最终full-test-delivery-1，尚未归档提交/独立验收；两条补充消息均不用重发。

**T-037 独立探针已扩为六组，仍未执行**：临时iris-t037-independent另加真实ACK延迟读取，观察服务器已写确认及随后结束边界后才放行客户端读取，不用Delay猜测。确认结束补充消息01a11b74-232c-7c90-96d9-c78e25c3e6e9已排队；待开发实际接收，不能误称已送入当前执行轮。

**T-037 确认结束预审**：早期 Server 写 ACK 后立即 Disconnect，须验证 Windows 会丢弃未读缓冲这一边界。已追加当前单及IPC规范，要求实际管道暂停客户端 ACK 读取、服务器写入完成后预算内放行仍 Accepted；不能用无取消同步 drain。此项尚未独立运行，不写成实测失败。

**T-037 补充已实际收到**：同一执行轮01a11b3c-a32c-7712-9dd6-868443a8a73c已明确“收到，这项补充并入T-037”，并执行读取current_task末35行，覆盖取消阶段、未Start释放与Main计时/日志补充。主轮仍在实施，不再把01a11b48-9e9c-7143-abfc-61090992f7a4当作尚未收到而重复发送。最新原始日志platform-tests-5为33项、app-tests-3为47项定向通过（开发进度文字中的27不是最新组合总数）；补充回归、最终Release/full及交付尚未完成。

**T-037 独立探针已准备但未运行**：`C:/Users/28910/AppData/Local/Temp/iris-t037-independent` 的 Program.cs/Review.csproj/README 覆盖真实随机本机管道、迟到Write/Accept/部分Read取消后无新I/O、真实Host未Start同步Dispose五组。只在自有探针进程限制ThreadPool为单worker，使无谓ForceYield不依赖调度运气；不改产品运行配置、不测性能。待开发及排队补充完成、程序集稳定后执行，不与开发并发构建/测试，不将准备代码当作结果。

**T-037 开发期预审补充**：早期 Client 写入后/codec 分片读取之间缺少显式取消检查，非合作旧操作结束后可能再启动下一 Read；Server 迟到 Accept 也须核对。另检查从未Start的Coordinator同步Dispose：不能因Server清理无条件ForceYielding尚未完成而误报失败。已追加 current_task，要求实际组件/窄门闩回归；这些是源码预审，尚无独立执行失败结论。补充消息01a11b48-9e9c-7143-abfc-61090992f7a4已排队，不声称已插入当前轮、不重复发送；交付时确认补充轮是否还在执行，避免并发构建。仍属原T-037，不另开工单。

**T-037 已实际接单**：签发消息01a11b3c-862d-7161-b7b6-dbbb424aea7e，执行轮01a11b3c-a32c-7712-9dd6-868443a8a73c，开发明确接单并已读取完整current_task/IPC规范及原启动代码。当前completedAt为空的interrupted记录不代表终止，不重复派单；继续在01a11b01-c46b-74e1-9015-726c77ffbc9e复审交付。

**当前唯一 T-037（2026-10-08）**：T-036C/父T-036/CCR036 在11263780f2a34dd9ee4dc998d80dfdf605dacaf0独立验收结案，Release 0警告错误、原Reload定向1通过、全量3553/3551成功/0失败/2既有环境跳过；实际Tester/UI及48张最终图通过。提交后13个DLL重新绑定，608项源码与开发最终清单一致。证据tasks/done/T-036C.review。当前工单按IPC_COMMAND_LINE.md / CCR-037 / ARCHv1.55实现完整IPC/CLI，开发仍01a11b01-c46b-74e1-9015-726c77ffbc9e。C恢复轮01a11b11-747c-79b2-847d-940d18c6b192已completed并交付；旧阻塞全部解除，不重做C或重发恢复单。M6延期、OCR复用翻译原文、不push、.codex不动。

**新开发对话已实际接单（2026-10-08）**：恢复消息01a11b11-6b4a-7601-9ff6-395869f14405对应执行轮01a11b11-747c-79b2-847d-940d18c6b192。已读取C补充并修正取消回调清理重试、Reload同步失效；recovery-tester-1为31/31、recovery-ui-host-1为34/34，正在最终视觉/构建/全量与归档。当前轮completedAt仍为空，不误判中断、不重复派单；最终提交与架构师复验尚未完成。下方旧“等待替代链接/阻塞”已解除，开发目标仅01a11b01-c46b-74e1-9015-726c77ffbc9e。

**2026-10-08恢复推进（覆盖下方旧阻塞与会话指向）**：用户已指定新开发对话01a11b01-c46b-74e1-9015-726c77ffbc9e，新对话已确认开发工程师角色且工作区为F:/Iris。唯一任务仍T-036C，恢复现有未提交实现，优先取消回调faultedTask清理重试与Reload旧结果失效，再最终验证/归档。旧对话最新记录interrupted且completedAt已非空，不再向旧对话派工。A/B/R1成果保留，M6延期，T-037不提前，不push。

**恢复检查已完成，等待开发对话替代链接**：recovery-build零警告错误；recovery-full为3548/3545成功/1旧模型列表Reload回归/2环境跳过。新异步Reload等待排空前未撤销模型发布身份，具体分析已追加current_task与DEVELOPER_RECOVERY。取消回调粘住faultedTask问题仍未修。短恢复轮01a1183c-f7bf-7e42-b6fb-6a9b82bc27a4仍无消息/执行记录；已通过异步输入工具请求新开发对话链接，不重发旧轮、不自行创建用户对话。

**开发连接恢复中（2026-10-08）**：指定对话的C执行轮与排队取消补充轮均明确failed/400 input。已发送唯一短恢复消息01a1183c-f642-7242-8be2-5c4a9193ce7d，对应恢复轮01a1183c-f7bf-7e42-b6fb-6a9b82bc27a4目前interrupted/completedAt=null/itemCount=0，尚不能判终止；不要重复发送。现有C工作区未重置或提交，源码指纹保存在临时iris-t036c-recovery/worktree-source-sha256.json；完整恢复说明specs/DEVELOPER_RECOVERY.md。48张候选视觉已独立查看，取消回调修正和最终交付仍未完成。

**C候选视觉已完整独立查看**：visual-final的48张图已按12张原尺寸contact逐格检查，明暗/600和760/凭据遮蔽/目标与无目标/非法/运行/结束/HTTP说明/失败与重试/只读/待应用均可读，元数据及SHA在临时iris-t036c-visual-review/reviewed.json。当前仍是开发候选，最终产物须比对；取消回调faultedTask的独立补充未验收通过前不结案。

**C真实UI独立组合通过（开发期）**：iris-t036c-independent-ui/ui-3.log实际SettingsWindow/VM/Session+Core+临时Json+真实Tester通过两场景：userinfo粘贴拆分后测试零写入，再History/Network联合一次保存并清空密码；手动清理重试最后UI发布暂停时保存保持等待，放行后仅一次保存/一次HEAD。引用/源码哈希见binding.json。ui-1/2为审查夹具using/警告记录，未改生产。取消回调faultedTask的独立补充仍待C处理，不能凭正常路径通过结案。

**2026-10-08 C测试器独立检查**：iris-t036c-independent-tester/basic-1.log四种HTTP状态的实际Tester/Session纯Create、HEAD/无Origin认证、Run/Dispose分离通过；wire-1.log另三种实际Sockets+ConnectCallback场景（代理认证成功、302不跟随、20KB头被16KB限制拒绝）通过，原始快照源是窄fake，不冒称全UI/Core组合完成。cancel-callback-1.log复现取消回调异常后重复Dispose永久等待同一faulted_cancelWork，资源不释放；首次/重试均失败，最后仅为夹具清理反射替换已观察Task，不算产品成功。已追加current_task并排队消息01a117cf-bb3e-73f0-9d3a-f6175927462e，待C处理，不重复发送。

**T-036C已实际接单**：消息01a117b2-d59b-78e2-8919-889c058904aa，执行轮01a117b2-d7d5-7e03-9f74-45c57f86689a明确接单并读取规范/已有OCR会话与设置关闭底座。下一步独立评审实际Tester与UI，不重复下达；父单待C结案。

**当前唯一T-036C（2026-10-08）**：0cd9e5216f5bf52074536b91dd2c95681b336b50的B/R1已独立构建0警告错误、全量3496/3494成功/2既有跳过，七组传输与三种清理顺序最终探针通过；证据R1.review，B原失败保留。C按NETWORK_SETTINGS_UI.md/NETWORK_CONFIGURATION/ARCHv1.54完成完整network页面、联合保存及已批准HEAD会话与退出，不改Core公开契约/默认/包，不提前T-037。父T-036/CCR036待C完成，开发会话不变，不push。

**R1已实际接单**：消息01a1176a-32ca-7533-a087-a7f8d703abde，执行轮01a1176a-3615-7011-8e68-93ddc7eb4a18明确接单并确认Stop把进行中的清理误当失败；正在采用原子接管/共享完成责任修复。后续观察此轮，不重复派单；C仍未开始。

**当前唯一T-036B-R1（2026-10-08）**：B交付eac0c2f0独立构建0警告错误，全量3488/3485成功/1失败/2跳过；已用实际NetworkResponseContent门闩确定性复现Stop重复Dispose在途清理而提前失败。只修Providers内容清理协调/等待及回归，保留其余B成果，不改公开契约/迁移/路线，不开始C。完整证据T-036B.review，current_task已签。

**2026-10-08 T-036B修正与真实传输独立验证**：iris-t036b-foundation-fixed/migration-1.log重新绑定新Core/Infrastructure，六个迁移/只读/畸形userinfo场景全部通过，原失败保留。iris-t036b-independent-transport/transport-1.log五组实际Core+两命名管道+Sockets ConnectCallback内存流通过：原客户端重试A/新请求B、活响应阻止Stop、两条SSE在EOF前返回及消费者/响应排空、迟到A不倒退B。四张readonly图片已逐张查看，记录同目录visual-review.json。首轮全量full-1为3485/3480成功/3失败/2跳过：契约XML注释、旧DI工厂包装、真实WPF夹具直接执行callback的fake边界；开发正在修正，未结案。WPF测试换为真实WpfUiDispatcher，生产保存逻辑未据该fake失败直接判缺陷。

**T-036B畸形userinfo实际反例已下达**：iris-t036b-independent-foundation/migration-4.log六场景中普通迁移/legacy地址保护/超intfuture只读通过；用户名-only空密码拒绝、畸形authority加载后普通保存将合成密码写新.bak、直接Update同类URL将密码写主配置三场景失败。Core/Infrastructure仍为binding.json冻结DLL（review-state.json已核对）。完整要求追加current_task；消息01a11700-2c09-7831-9007-917cb1d2e9e4已排队，正常Update也已在原消息要求中，无需再发重复提示。无真实用户数据/HTTP。迁移guard不能以Uri.TryCreate失败等同无userinfo；保留其它B成果。

**T-036B开发期真实迁移复核**：iris-t036b-independent-foundation/migration-1.log三个规则通过（普通userinfo迁移、错误legacy地址保护、超intfuture只读），用户名-only旧URL被拒绝。A拆分规则允许HTTP/HTTPS空密码；已在NETWORK_CONFIGURATION和current_task明确并给出实际DLL复现，SOCKS5非空约束不变。此为开发中窄边界，非最终验收；尚未对生产HTTP管道作最终独立验证。

**T-036B已实际下达并接单**：消息01a116cd-b578-7590-86cd-a48baa67ec06，执行轮01a116cd-cf07-7c61-a3df-ed8edf905b2b明确接单、读取规范并已开始同步Core契约/共享校验。原A流式排队补充已完成确认，无需重发。继续观察B，C不提前开始。

**当前唯一工单T-036B（2026-10-07）**：A在97b8d02f8d9316632dc21ad6f78e3d3f15691dae通过独立方案验收，受保护文件零差异、最终原型匹配独立运行；证据T-036A.review。CCR036 / NETWORK_CONFIGURATION.md / ARCHv1.53正式冻结，完整声明已独立编译。B实现实际Core/迁移/两命名传输/资源与Host底座，C保留完整network UI与HEAD测试；不复制A简化原型充当生产。开发对话不变，一次一单，不push，M6延期。

**T-036A归档源码差异补验**：candidate-vs-archived初次只MigrationChecks不同，归档版新增“只条件删除实际导入的固定旧键”，避免userinfo迁移误删无关固定键。独立副本同步该文件后migration-final.log全部通过；其余模块与all-1所测相同，final-source-match.json现全部true。原差异检查保留，不用新源冒称旧日志。

**T-036A完整独立原型运行已通过，待交付提交**：iris-t036-independent-candidate/all-1.log执行current/runtime/actual-coordinator/persistence/migration/pipelines/connectivity/streaming全部模式正常退出，源码已对照归档prototype-final（candidate-vs-archived.json）。生产src/tests/CONTRACTS仍零改动；不重复跑生产全量。正式B草稿已准备，A最终hash/归档/声明复核后才冻结并下达，C保留完整网络UI/连通性测试。

**T-036A流式修正已独立复跑通过**：独立复制原型到iris-t036-independent-candidate，streaming-1.log及all-1.log均正常退出；包含获取流零预读、EOF前SSE、同步/取消/非合作取消/读错/Copy及两实际命名管道chunked响应与旧池保留、Dispose失败重试。A现已归档工单并准备本地提交，仍须最终提交/源码绑定复核。B工单草稿在临时iris-t036b-order-draft.md：完成配置/迁移/真实传输底座，C后续完整UI+连通性；正式B还须覆盖迟到A请求在B发布后不得倒退当前代次、重试中间响应的失败清理、完整容量与停止，不能把A简化模型视作生产完成。

**流式补充已被开发实际接收**：执行轮01a115e9-8985-72c0-93c8-1b6b20e45729在本轮item247明确读到共享current_task补充，已读取独立stream-2日志与Program.cs，正在A内补无缓冲转发和非零流/取消/错误/处置验证。无需因排队消息尚未独立执行而再次重发；原A范围不变。

**T-036A精确提案已初审（未冻结）**：已完整读取T-036A.proposal.md及proposed-contracts.cs.txt。提案单独定义INetworkConfigurationObserver接收固定settings/generation，不回调Capture，已处理同协调器门闩自等待风险；版本化绑定凭据/显式Clear/未来版本只读/安全逻辑备份方向保持。当前原型仍缺已下达的无缓冲流转发补充，不能以空响应通过接受A。进一步审查说明在临时iris-t036-design-review.md；网络codec跨Infrastructure内部共享和IsReadOnly旧实现适配需在正式签发时明确。

**T-036A流转发对照已验证**：iris-t036-stream-review/stream-3.log保留原wrapper EOF前不返回反例，并验证独立最小ForwardingComparison可在EOF前取得流/首字节且响应Dispose才归还lease。仅对照，不是完整wrapper验收。已排队的补充消息仍须开发处理，当前原PipelineChecks尚未覆盖CreateContentReadStreamAsync；不要据prototype-final-run-1的空响应通过而接受流式兼容。提案复核另需确认快照Capture与Commit共用gate时，Provider观察者不得重入等待同gate造成死锁；以最终设计为准，不把未见实现假设当作缺陷。

**T-036A流式原型反例已下达**：独立iris-t036-stream-review/stream-2.log证明原CaptureHandler.LeaseContent的ReadAsStreamAsync在非零流EOF前不返回（默认缓冲），仅空响应的生命周期探针不足。已在current_task追加本单内修正/非零SSE/流与代次收尾验证要求，消息01a11632-3bf4-7010-b3c4-2aa89b9237e4排队至原开发对话。computer-use官方连接报failed to write kernel assets/os error3；已根据当前chat开始时间确认node_repl PID44360，官方repair脚本只读诊断directories=[]，无法确定安全修复路径，未修复/未UI插队/未用其它前台输入替代。共享工单保留完整要求，原A继续，不重复派单或误判停止。

**T-036A独立运行时核验**：临时iris-t036-independent-runtime/runtime-3.log以实际DynamicWebProxy及安装的.NET10.0.12反射构造HttpConnectionPoolManager，System模式捕获合成A凭据后切换Custom B，真实代理返回B但runtime仍保留A凭据且对B的GetCredential返回A；没有SendAsync/socket。官方v10.0.12源码另确认IWebProxy异常被吞并转直连，连接池key不含可变自定义凭据。开发独立baseline-run-2也已用真实SocketsHttpHandler+ConnectCallback内存流复现缓存/错配/连接复用/异常直连，并确定版本化凭据+请求快照+新传输池方向。后续核验应覆盖重试固定同一快照、响应流超出headers的generation存活；尚未冻结契约，不改生产。

**T-036A已接单**：消息01a115e9-7d88-74c3-a612-0737d8c5cddc，执行轮01a115e9-8985-72c0-93c8-1b6b20e45729明确接单并读取工单；后续继续观察此轮，不重复发送。

**2026-10-07当前状态：T-035/CCR035已结案，唯一工单T-036A。** d8b49b7c独立Release0警告/错误，全量3400/3398成功/2既有跳过；最终15探针与64图匹配，T-035.review已归档。T-036完整范围保持；A先交付可运行原型/实证和精确推荐契约，解决凭据跨文件提交、代理/凭据动态一致性、旧userinfo迁移与草稿连通性测试。A不改src/tests/CONTRACTS，下一生产实施由架构师冻结签发。开发对话不变，不push，M6延期。

**T-035首次全量与补充视觉（待最终交付）**：release-full-1为3400总计/3123成功/275失败/2既有跳过；开发full-failure-analysis-1区分271项缺新IHistoryWindowPresenter与4项其它提示。实际日志核对旧DI构造失败；托盘顺序需加入历史菜单，两项错误保留测试依赖该夹具，另旧IME/ShiftEnter测试未固定普通Enter的模拟修饰键。开发仅补依赖/菜单预期和复用既有TestKeyboardModifiersProvider隔离物理键状态，旧业务断言保持，legacy-targeted-1的80项通过。首次失败保留，仍需全量恢复。独立已核对64张最新图：50张与先前所看一致，14张新增/变化通过4张changed-contact逐格查看，记录临时reviewed-visuals-2.json（history-render-5184a7e535a741bba886e1c4ca3aa943）。

**T-035独立复核追加（仍在开发）**：iris-t035-independent-review/preparation-1.log四项真实窗口准备/恢复检查通过，含旧receipt不影响关闭后新窗口和实际定位失败完整回滚。已逐格查阅history-render-0de5b7ba988c4c259d4dfedfb478fd13的56张图（10张contact），深浅主题/窄窗/确认/关闭失败/历史设置均可读，SHA记录临时reviewed-visuals.json。开发正在补行操作与服务标题视图及最终验证，最终图片/提交仍须绑定，不能直接据开发期探针结案。已审查实际Host重译测试，使用真实TranslateWindowPresenter和窄Provider通信，全文/current model/收藏保持断言存在。既有DI测试只新增History依赖与计数断言，未删业务校验。

**T-035开发期独立检查（尚未验收）**：临时iris-t035-core-review/run-1.log实际Core编辑协调器六组通过（纯历史/最新字段/冲突/已达Target/非法范围在stage前拒绝/Update内冲突回滚）。临时iris-t035-independent-review/settings-2.log六组实际设置窗检查、history-1.log两组实际历史窗+临时SQLite、lifecycle-1.log三组迟到查询/非合作复制/写入UI收尾检查均通过。产物为开发中复制冻结DLL，仍须最终提交重绑；历史重译在本独立探针使用窄Presenter替身，不能冒称实际翻译端到端。settings-1为夹具在异步外部更新前过早断言，改为等待UI队列哨兵后通过，原失败保留，生产未改。开发目前已通过业务/生命周期测试，正在修渲染夹具并继续视觉/全量；不重发工单。

**T-035已实际下达并接单**：消息01a1155e-feb3-7f80-aebc-58bbd054af0c；执行轮01a1155f-23d5-7593-b242-4704b45ca2e2明确接单，已读取工单/历史规范/契约并检查真实保存器、仓储及截图准备接线。不因持久化轮状态暂为interrupted且completedAt=null误判停止，不重复派单。

**2026-10-07当前唯一工单T-035（覆盖下方历史状态）**：T-032/B/R1在b0607304755f22b5b115e802ad4bdf685e64e1de独立复审通过，Release0警告/错误、全量3338（3336成功/2环境跳过），生产相对B零变化，九组窗口探针和42张视觉证据保留。M5实现结案，真实桌面/NFR仍待T-040。按HISTORY_UI.md/ARCHv1.52/CCR035完成HistoryWindow/Presenter/OpenHistory、托盘入口与历史联合设置；精确HistoryPatch声明已预置CONTRACTS，由开发落地。M6延期，开发会话01a112a7-c953-7630-9ee9-d2be2123dc70不变，一次一单、不push。

**2026-10-07接续状态（覆盖下方历史当前状态）**：原架构对话无法继续，用户已在新对话授权接续。开发对话仍01a112a7-c953-7630-9ee9-d2be2123dc70。T-032B交付03b42a3经独立构建0警告/错误，全部九组窗口探针通过；独立全量3338/3335成功/1既有队列测试失败/2环境跳过。当前唯一补单T-032B-R1，仅修两个既有队列测试的同步门闩/收尾，src零改动，详见current_task与T-032B.review。B/父单未结案，M7未开始；M6延迟、OCR复用翻译窗口、不push等约定保持。

**T-032B已实际下达**：消息01a11336-0736-7d52-b048-6fe75224459b，执行轮01a11336-1cad-7f62-b71d-f89c4615dbf7已建立，指定开发会话不变。完整UI规范为OCR_SETTINGS_UI.md，A验收证据T-032A.review已准备随B提交；尚未收到实施交付，不因等待超时重发。

**当前唯一工单T-032B（2026-10-07）**：A/CCR034在83787af04d77d06ccb46d0fac708b5e683771716完成独立Release0警告/错误12.75s、全量3301/3299成功/2既有跳过44.318s、最终38项独立检查通过，证据T-032A.review，12项源码指纹匹配。现B依据OCR_SETTINGS_UI.md/ARCHv1.51/current_task完成全部OCR/截图设置UI，复用A增量Plan、凭据和单次测试会话，清理失败必须保有会话并可点击重试；Core契约/默认/后端不改。仍使用01a112a7-c953-7630-9ee9-d2be2123dc70，M6延期，OCR不恢复独立窗口。

**T-032A旧UI无调用诊断补充**：开发selection-diagnostic-1.log两处Additional Info均为add_Changed；PageAndDraft原第25行同一行串联Reader/Secrets/Hotkey三个断言，Shouldly报告首个Reader表达式不能直接据此认定调用了ReadAsync（ISelectionTextReader根本没有Changed事件）。独立calls-1逐字段观察已证实Reader为空、Secrets为add_Changed，未见真实选区或密钥读写。应保持业务零读写断言、在准确的替身/构造订阅边界处理预期依赖，不将该提示泛化为读取用户内容。仍待开发修正与全量复核。

**T-032A扩展独立检查及首轮全量**：development-credentials-1为11项通过（跨域同ID凭据分离、显式/legacy共享引用含禁用peer保留、ApplyPending旧值保留/原回执不重stage、提交后取消、输入列表冻结），连同先前commit13/draft13为37项；development-resources-1在会话存活时Provider/凭据/捕获草稿及正对照均回收，新增1项。首轮全量3301/3297成功/2失败/2既有跳过，失败为旧SelectionSettingsUiTests无调用断言，开发正诊断。独立临时iris-t032a-ui-call-review/calls-1.log用实际既有UI夹具观察构造/Show/Save：Secrets仅add_Changed，无Get/Set/Remove；Reader各阶段为空，不能宣称该项已复现或已解决。自有窗口/STA正常清理，不改原测试断言。最终仍待全量与提交。

**T-032A独立开发期26项通过**：临时iris-t032a-independent-review已接实际SettingsEditCoordinator与OcrProviderRegistry，MemorySettings/Secrets/Provider仅外边界。development-commit-1为13项通过（联合一次提交/未编辑最新值/语言归一/非法遮罩与主备在stage前拒绝/提交内冲突整单回滚与暂存删除）；development-draft-1为13项通过（纯构造、仅Test一次不Recognize、不写配置密钥、禁用草稿、Dispose失败原实例重试、非合作取消关闭等待、取消+清理错误可观察、Keep冲突不读密钥）。独立夹具Track(close)已补显式丢弃返回值消除编译提示，实际close在finally和作用域中均等待，不弃置任务。当前尚在开发，最终须重绑完整复核；跨领域同ID/共享引用等扩展检查仍待补。

**T-032A已实际下达**：消息01a112f5-5e4c-7e90-9721-52cee48291d4，执行轮01a112f5-6a2d-7721-bfd8-67d9bbbe381c已建立，目标会话01a112a7-c953-7630-9ee9-d2be2123dc70。CCR034声明已独立编译0警告/错误（临时iris-ccr034-contract-check/build.log，1.15s）。尚待接单确认/实施，不因等待超时重发；T-031/B已结案，42项最终探针及Release/full证据在T-031B.review。

**当前唯一工单T-032A（2026-10-07）**：T-031/B及CCR033在cafc5ebdfe75c0c18a7b638ee4519dff47ea1e27独立验收结案，Release0警告/错误16.97s、全量3234/3232成功/2既有跳过46.283s、最终42项独立场景全过、六张最终图匹配已审查图。证据T-031B.review。T-032拆A增量编辑/凭据/单次草稿测试会话、B完整设置页，原范围保留；A依据OCR_SETTINGS_EDITING.md/ARCHv1.50/CCR034/current_task，CONTRACTS已预置，生产由指定新开发会话01a112a7-c953-7630-9ee9-d2be2123dc70同步。OCR仍复用翻译原文框，M6延迟，不恢复旧窗口。

**T-031B开发全量已通过（待交付复审）**：release-build-4为0警告/错误1.79s；release-full-4为3234/3232成功/0失败/2既有跳过、2m10.522s，已确认进程56232/62072退出。第三轮仅剩TranslationEndToEndTests自建DI缺端口，现已补齐，旧业务断言未删。独立development-dispatcher-1另9项通过（实际Dispatcher+三个Handler在非合作OCR门闩下互斥，忽略请求取消不影响当前调用），连同入口20、副作用8、Host4、caller1为当前开发期42项。开发正在复核探针并归档；最终仍按提交构建重绑独立验收，不提前结案。

**T-031B完整Host独立预检已通过**：临时iris-t031b-independent-review新增HostChecks，实际AppHostBuilder/Dispatcher/三个Handler/真实ScreenshotService与Preparation构造图、Bootstrapper停止和Host异步处置共4项通过，development-host-2.log，进程正常退出。无启动主程序或截图；仅设置/密钥/剪贴板/UI边界用自有替身，真实截图构造图未mock。首轮host-1为独立夹具未显式加载Wpf.Ui导致XAML主题解析失败，已补加载，非产品缺陷。开发共用测试夹具补截图端口并更新托盘菜单断言，fixture-targeted-2已通过；尚待最终全量与交付，不据开发期探针直接结案。

**T-031B视觉首轮与第二轮全量分类**：已保全并逐张查看临时iris-t031b-independent-review/render-pass-1六张最新明暗source/loading/error，manifest记录原路径与SHA256；普通OCR确实在现有翻译原文框，无截图预览，真实翻译按钮可用，加载/错误安全可读。开发release-full-2进程正常结束但3234项中224失败：219项同为旧自建DI夹具缺IScreenshotService，另5项为托盘序号或依赖该夹具的异常注入断言。不能把这些写成224个独立产品缺陷，也不能直接忽略；开发已读取日志并在修正组合根/夹具。当前DI按需依赖改动保持截图准备算法，最终仍需实际完整Host退出与全量验证。

**T-031B独立扩展与全量首轮情况**：临时iris-t031b-independent-review的development-entries-5为20项通过（新增真实翻译按钮已启用及手动继续一次），development-effects-1为8项通过（编辑/caller撤销排队复制、租约归还保留已接管复制、退出等待非合作剪贴板及HWND释放、托盘不可用无延后、原caller/停止拒绝排队静默通知）。开发已将原文复制并入既有单写队列。首轮全量出现旧夹具注册缺失/主题超时及DI循环，进程26888/父23268曾存活；独立只读栈full-stall-stack-1.log显示AppHostBuilder factory循环解析。开发独立定位并已保留证据、终止该自有进程，正在以ScreenshotPreparation内部按需解析热键/鼠标依赖打破环，不能把此首轮未完整收尾计通过。新di-targeted-1另有两个旧Host测试处置鼠标UI所有者失败，开发已读取处理，尚待完整根/退出验证。

**T-031B开发期独立入口复核**：新会话已落地三个Handler及托盘/DI/反馈，当前真实翻译操作已直接检查原caller。临时iris-t031b-independent-review/development-caller-cancel-1.log将基线accepted=True反例修正为False。development-entries-4.log为实际三个Handler（反射构造内部类型）、实际Presenter/Window/VM及窄捕获/OCR/剪贴板/托盘边界18项全过：purpose/原图/同身份loading终态/Anchor/普通0翻译与自动复制/截图翻译1次且不复制OCR原文/备用提示一次/静默保留既有窗口与操作且计数15。前3轮为探针缺IO using、NullLogger构造及自制字符串计数14→15的夹具修正，日志保留，不当产品缺陷。代码仍在开发，按钮状态和复制队列正在调整，最终须重绑复核，不据此验收。

**新开发会话已确认接单**：01a112a7-c953-7630-9ee9-d2be2123dc70收到完整角色与T-031B交接（消息01a112aa-00be-7233-9252-b50ee0db6f49，执行轮01a112aa-094c-70e3-b7d4-fd95b1d3b104），明确“已接单，立即继续唯一工单”，已实际读取工单/CCR/架构/仓库规则。临时观察脚本iris-watch-new-developer.py已改为新会话；不再观察或派工至故障旧会话，.codex工具未改。

**当前开发会话切换（2026-10-07，用户指定）**：新开发会话01a112a7-c953-7630-9ee9-d2be2123dc70接替出问题的01a10fa5-77e8-77e1-82d9-1ea1aa587d61。唯一任务仍T-031B，基线ce8a9a777fbd3fb5f953c6963b2125a7ceedf38d；切换时src/tests无B实现改动。新会话已就位等待指令，接着读取current_task/OCR_ENTRYPOINTS/CCR033并实施，不再向旧会话派工。本条覆盖下方历史会话指向；M6延迟、OCR复用翻译原文框、不显示截图的用户决定保持。

**T-031B已接单，独立验证准备**：开发轮01a11274-7a5d-7500-a43b-6ff4420686ab已明确接单并读取规范/现有翻译操作、复制队列与托盘退出。临时iris-t031b-independent-review已建立自有STA/UI门闩、实际TranslateWindowPresenter/Window/VM与窄外边界，baseline仅填原文0翻译请求通过并正常收尾。新增原caller后注册回调的基线探针复现accepted=True/写入迟到文本；current_task已附路径，属于B原先直接检查caller状态的要求，待B实现后重绑复核，不重开旧窗口方案。当前尚无B生产改动或交付。

**T-031B已实际下达**：消息01a11274-7536-74e1-991d-b4005d3c7663，执行轮01a11274-7a5d-7500-a43b-6ff4420686ab已建立，未收到完成报告，不因45s等待超时重发。CCR-033独立声明编译0警告/错误，日志临时iris-ccr033-contract-check/build-2.log；首轮缺既有全局Errors using的夹具错误保留build.log，补齐与既有Contract检查相同using环境后通过，未改无关声明。A验收已按新范围结案，B是当前唯一实施单。

**当前唯一工单T-031B（2026-10-07）**：A在ce8a9a777fbd3fb5f953c6963b2125a7ceedf38d按用户新范围独立验收（Release0警告/错误34.15s，全量3155/3153成功/2既有跳过40.621s，最终UIA清理6项通过），证据T-031A.review。当前B按OCR_ENTRYPOINTS.md/ARCHv1.49/CCR-033/current_task实现三个Handler、托盘/配置热键，普通OCR仅填翻译原文、不展示截图；原文复制与固定即时提示绑定UI接受点，静默有独立停止反馈。CONTRACTS已预置，Core由开发同步；M6延期，T-032未开始。旧窗口任务和排队提醒不恢复。B准备签发发送，未宣称开发已接单。

**T-031A按用户变更交付待复审（2026-10-07）**：独立OCR窗口/专用测试与DI、准备、退出接线已移除；历史源码txt、失败记录及图像保留，旧方案不算交付。Core未发布展示声明按工单暂留兼容，UIA仅两个测试文件作确定性时序维护，App/后端生产保持。最终revised-scope-build零警告/错误，revised-scope-full为3155/3153成功/0失败/2既有跳过；报告tasks/done/T-031A.md，current_task已全文归档。CCR032撤回状态保持，父/A▶、B☐；OCR写翻译原文框与三个入口由B接线，尚未实现，等待独立复审。

**新范围当前实况（2026-10-07）**：开发已确认用户变更并移除本轮独立OCR窗口、专有测试、DI/准备/退出接线，旧源以txt归档retired-window-source，历史日志/18图保留。src/tests检索无OcrResultWindow/Iris.App.Ocr/IOcrResultShutdownCoordinator；App当前无实质diff，Core仅暂留未发布OCR展示声明。revised-scope-build为Release0警告/错误、10.01s；revised-scope-full为3155/3153成功/0失败/2既有跳过、1m02.703s，进程64056/53712已退出。UIA维护在两个测试文件，实际15项通过，生产保持。尚待正式提交和按新范围独立验收；B将实现OCR写翻译原文框，不能宣称此入口已实现，更不能重跑旧窗口探针作为当前交付要求。

**OCR新范围已排队通知开发**：消息01a1123c-ceee-7b33-a8a1-8d0e9ef43b35发往指定开发会话01a10fa5-77e8-77e1-82d9-1ea1aa587d61，CLI已返回Queued；内容要求重读current_task顶部用户新范围、移除独立窗口方案并复用翻译原文框。未插队、未声称开发已确认，不因等待超时重发。下方旧窗口测试通过仅为历史成果，不再用于旧方案结案。

**最高优先当前范围（2026-10-07，用户决定）**：OCR不使用新窗口，直接把识别文本写入翻译窗口原文框，不显示截图。current_task顶部已覆盖旧T-031A：移除独立OCR窗口/专有UI与DI、准备/退出接线，保留后端和历史证据；B用现有翻译操作条件发布（截图OCR AutoTranslate=false，截图翻译仍true，静默不弹窗）。CCR-032旧方案撤回，不再验收独立窗口或为其追加测试。当前UIA夹具维护保留，待新范围提交复审；M6仍延迟实现。

**UIA首处修正独立5项通过，另处观察竞争待维护**：DrainScenario已改为明确等grace wait进入→SignalExit→保持read未完成→放行stderr→等待stdout释放；原断言/失败收尾保留。临时iris-t031a-uia-fixed-review复制修正测试及原夹具，实际正常/Stop、两类body失败及body+cleanup异常共5项通过、进程正常退出。开发uia-timing-fixed为14/15通过，另处RetryCleanup...反射读_activeCleanupTask尚未赋值导致TargetException；当前工单§4已限定等待真实任务发布后再做原等待者断言，仍仅同文件测试维护。

**T-031A再次全量通过但旧夹具竞争仍待维护**：release-build-final-2为0警告/错误、5.17s；release-full-final-2为3185/3183成功/0失败/2既有跳过、1m37.661s，已核对进程64060/51500退出。Platform测试仍0diff，故此绿结果仅证明本轮时序通过，不能推翻下条受控复现。开发收尾前须按current_task §4处理测试时序，不以反复重跑关闭竞争；OCR产品与视觉已通过，不扩大生产范围。

**旧UIA夹具时序环已受控复现**：临时iris-t031a-uia-timing-review保存实际生产DLL绑定、原测试/夹具副本及reproduce-1.log。仅在ReplyAndExit的EnqueueReply与SignalExit间以OnWaitForExit门闩控制顺序，确认先进入grace Wait再SignalExit，原测试第80行稳定Timeout；原finally完成后异常传播，独立诊断进程已退出。退出0仅指成功复现预期失败，不是通过验收。开发未改动定向15/15通过不能消除此竞争；current_task §4/§5开头已补实证与窄测试维护要求，勿修改UIA生产。

**T-031A最终全量出现旧UIA夹具失败**：release-full-final为3185/3182成功/1失败/2既有跳过，1m41.151s，进程正常结束但整轮失败；App项目通过。UiaStartupCleanupTests第80行等stdout Dispose超时。独立源码审查发现ReplyAndExit的EnqueueReply→SignalExit之间可能被真实Reader消费，进入含stderr drain的WaitForExit，而FakeTimeProvider不推进、测试又等Dispose才开stderr，疑似确定性时序环。current_task §4已追加窄范围授权：受控门闩确认后只维护旧测试/夹具，保留核心断言及完整收尾，不改UIA生产/不加sleep或超时掩盖。开发轮itemCount228已读取失败行，尚待复现/修正；三项OCR修正及18图已通过，不重开其缺陷。

**T-031A修正后第四轮视觉已核对**：render-pass-4保全18张PNG、manifest和fingerprints，逐张查看且所有SHA256匹配。明暗normal/loading/error/close-failure/high-dpi-zoom、400×280正常/列表错误顶部与滚动底部、4K Fit均已查看。小窗顶部不要求同时显示全部工具，整体滚动后复制/翻译及安全错误可见，编辑文本未塌缩；结合此前实际按钮边界探针，C31A-3修正已获验证。4K图完整适应、其它常规图未见新增遮挡。开发review-regressions-2为6/6通过、11.817s；尚待修正后的最终全量和交付，不能把离屏DPI当物理多屏验证。

**T-031A三项修正独立复核已通过（开发期）**：重绑23:50:27新App.Tests输出，四组development-fixed-*-1.log共10/10通过且全部进程正常结束：caller取消先于转发时终态accepted=False；排队自动复制writes=0；4K Fit为0.1190104、extent457×257在viewport457×428内，缩小不反向放大；400×280正常编辑132/内边距20/字体15，列表失败后114/20/15，整体滚动后复制/翻译/关闭按钮实际边界可见。开发review-regressions-1另5/5通过。C31A-1/2/3不再记为当前未修缺陷，旧失败证据保留；尚待最终渲染查看、修正后Release/full、交付及最终绑定完整探针，未验收A或签发B。

**T-031A三项反馈已确认并进入修正**：开发轮itemCount176已重读current_task全文和独立IdentityChecks，明确承诺不据首次全绿交付。当前源码已增加Operation.Caller并在Valid/自动复制接受点检查；Fit不再夹25%，ZoomOut低于25%不放大；Window加入LayoutScroll整体滚动和内容最小高度。尚无绑定这些修正的新DLL（最近核对App.Tests输出Iris.App.dll仍22:45:27），不可运行旧DLL冒称修正通过。后续待开发构建后重绑复核caller-cancel/caller-auto-copy/fit-observation/small-content及最终完整组，重新查看最终渲染。排队提醒已发送一次，无需重发；本轮通过共享工单已真正传达。

**T-031A开发首次全量已结束，尚不可结案**：release-build-1为Release0警告/0错误、8.03s；release-full-1为3180项、3178成功/0失败/2既有隔离window station error183跳过、1m33.158s，已核对dotnet37260/App.Tests36068退出。开发轮itemCount166仍未交付；C31A-1/2/3尚未见生产修正，现有全绿不能推翻独立反例。提醒消息01a111c2-541e-7bb0-890f-6a98f9b6fa50已排队，不重发；最终必须重读current_task §5–7落实再验证，A不验收/B不签发。

**T-031A交付前提醒已排队（未插队）**：向指定开发会话发送一次提醒，消息01a111c2-541e-7bb0-890f-6a98f9b6fa50，CLI明确返回Queued。要求最终验证/归档前重读current_task全文及§5–7，落实C31A-1/2/3；若先交付，下一轮仍仅补本单，禁止自行开始B。同时通知M6延期。Computer Use不可用，未声称已即时插话或开发已确认；不要因等待超时重复发送。window-tests-11开发定向25/25通过、15.637s，但当前源码仍未覆盖三项反馈，不能据此结案。

**T-031A操作上限独立预检**：新增IdentityChecks.AdmissionCapacityAsync及程序admission组，重绑当前App.Tests输出后实际3/3通过（development-admission-1.log）：8个未归还操作时拒绝第9个、拒绝不撤销当前可发布结果、释放旧操作并完成回收后可接纳新意图。真实离屏窗口/STA正常结束，无用户内容；这是开发期新增覆盖，最终仍重绑复核。Computer Use本轮官方连接仍native pipe unavailable/os error2，未操作插队或发送排队提醒；C31A-1/2/3保留在current_task，开发执行轮itemCount148仍未终态。

**T-031A Host定向修复已通过**：实际测试已去掉STA同步using Host，先await真实Bootstrapper.StopAsync再await Host.DisposeAsync，finally汇总停止/清理错误。开发window-tests-10为4/4通过、4.508s且进程结束。window-tests-9异常退出证据保留，不抹成通过。C31A-1/2/3仍待生产修正；独立ResourceChecks小窗观察已加正常/错误编辑内高和预览基础断言，尚未重绑运行，后续不能把旧观察日志当新断言通过。

**T-031A停滞轮次已结束 / B准备更新**：开发会话执行轮仍未终态，itemCount141；已确认自有测试PID20568并由开发终止，父14676也已退出。window-tests-9最终记录14m13s、仅2项成功但测试进程退出-1，整轮失败，绝不计全通过；开发承诺先实际Bootstrapper排空再异步Dispose Host，目前仍待代码落实。本架构师未另起测试。临时iris-t031-preparation.md已置顶纠正旧裸Show/T-030B状态，并按实际Dispatcher/托盘/默认热键/退出顺序整理B矩阵：通用Notify二次排队无法证明过期拒绝，须定稿身份固定通知与静默独立停止责任；只准备未签发B。

**T-031A第三轮视觉补齐**：已实际逐张查看render-pass-3此前未查看的10张（明暗normal/loading/error/close-failure/high-dpi-zoom），未发现新增遮挡，按钮与提示可读；两张small沿用本轮此前查看结果。因此第三轮12张均已查看，但400×280实际列表刷新失败导致编辑区域塌缩的问题仍以current_task §7及development-small-content-error.log为准，不以正常大窗或离屏高DPI图片替代该项验证，也不宣称真实多显示器已测。

**T-031A Host测试等待独立证据**：已复核进程14676/20568仍存活，window-tests-9停在ActualHostHasSingleLazyPresenterAndNoDispatcherCycle的WPF STA同步Host.Dispose。实际测试解析截图准备协调器，而SelectionIconController异步释放需要UI；当前OCR Presenter无Dispose接口，不能直接归咎其释放。原栈保存在临时iris-t031a-independent-review/window-stall-stack-1.txt，current_task §4已补诊断与完整失败路径收尾要求；开发轮已自行读取栈，架构师未杀进程/重启。C31A-1/2/3源码检查仍未落实，继续作为交付前项。

**最新范围调整（2026-10-06，用户决定）**：M6 悬浮球 / T-033 标为延迟实现，不纳入本轮开发与发布验收；保留未来需求、现有契约与默认配置。当前 T-031A 继续；M5 完成后跳过 T-033，按依赖推进 M7 未完成任务。本条覆盖历史记录中的悬浮球本轮实施安排。

**T-031A小工作区第二轮**：render-pass-2仅两张small与pass1不同，已查看，尺寸裁切已修正但编辑/预览区塌缩。实际400×280测得编辑框24 DIP、内边距20、字体15；预览控件仅8 DIP。已在current_task C31A-3追加紧凑布局可用性要求（不是只检查按钮/画布）；保留旧两轮图。其它10张字节未变，已有视觉检查保持。

**T-031A视觉首轮**：临时iris-t031a-independent-review/render-pass-1保全12张开发原图、manifest及散列，均已实际查看且散列匹配。正常明暗/加载/错误/关闭失败/高分辨率缩放未发现新的遮挡；两张small右侧被裁。独立尺寸观察确认主题最小460×320却输出400×280，C31A-3已加current_task §7并置顶提醒：按真实小工作区调整minimum、断言Actual尺寸、重新渲染查看，不能只扩大画布冒称小窗口通过。原图保留，不把该证据问题直接判为生产布局失败。C31A-1/2/3仍为交付前必查项。

**T-031A资源与Fit澄清**：独立closed-gc已通过，排除测试Application.MainWindow根后Window/VM/Bitmap/Image/operation均回收；当前截图显示身份也不保留关闭窗。4K自制图观察Fit被25%夹住，extent960×540大于viewport457×356。原规范歧义由架构师在current_task §6及OCR_RESULT_WINDOW §2澄清为C31A-2：手动25%–400%，Fit允许更小且缩小不得反向放大；原观察不计开发违约。已将C31A-1/2提示置于工单开头，交付前需落实。无需重跑已通过的A/B后端验证，独立窗口目录保存当前证据。

**T-031A独立预检扩展 / 插话通道**：临时iris-t031a-independent-review新增commands10项、integration4项、截图隐藏/新显示3项全部通过；当前明确待修为current_task §5的C31A-1取消终态/自动复制两个反例。为及时插话按用户授权尝试Computer Use官方连接，仍返回native pipe unavailable / os error2，未读取或操作Codex窗口、未发送排队重复消息，未使用其它前台输入绕行。反馈已落到当前工单和本交接；继续直接CLI观察开发状态，UI故障不阻止代码审查。

**T-031A开发期C31A-1反例**：当前实际窗口独立6项5过/1失败。后注册caller取消回调在Presenter转发前执行，caller已取消却TryShow终态返回true并显示LATE_AFTER_CALLER_CANCEL。定向development-caller-cancel.log明确accepted=True/failure=none；全部窗口/STA已退出。已写入current_task第5节作为原范围交付前自检，不新增API/范围，不等到交付后再说明。临时iris-t031a-independent-review包含真实UI/窄外边界源与原始日志；开发轮仍在运行，未发送会排队的重复工单。

**T-031A实际下达确认（2026-10-06）**：消息01a11179-409c-7222-bbc7-a3f30d8ea5fe、执行轮01a11179-517b-7942-b961-3ffb22654f1a已进入指定开发会话，开发明确确认操作/窗口/退出及真实自有WPF/离屏视觉范围，实际读取既有Presenter/WindowPreparationIntent/DI等。已启动，未交付/未验收，等待超时未取消/重发。CCR-032声明已独立编译0警告/错误，日志临时iris-t030-contract-check/ccr032-build.log；完整规则以新正式OCR_RESULT_WINDOW.md为准，旧iris-t031-preparation候选不再覆盖正式决定。

**当前唯一工单T-031A（2026-10-06）**：T-030B/父T-030/CCR-031在0f7dd54c42ce1d84a164a5973f41a0753d4e18cb独立验收结案（Release0警告/错误、全量3155中3153成功/2既有跳过、Core152+App8共160项）。证据T-030B.review。现按OCR_RESULT_WINDOW.md/ARCH v1.48/CCR-032完成结果窗口/身份/编辑复制转翻译重识别/截图准备和退出；B随后交付三个Handler及托盘/热键，完整原范围保留，T-032未开始。仍直接协调01a10fa5-77e8-77e1-82d9-1ea1aa587d61。

**T-030B开发交付待复审（2026-10-06）**：工单全文及报告 tasks/done/T-030B.md。最终串行 Release 0 警告/错误，全量 3155（3153 成功/0 失败/2 既有 error183 跳过），Core 定向 139 项通过；实际 Registry/Service/Normalizer 重配与实际 WinRT 预处理/Bootstrapper 停止失败重试组合通过。原始失败与旧夹具维护说明、源码/DLL散列均归档 T-030B.evidence。CCR-031仅已落地待复审，父/B▶、A☑；未开始 T-031/032，等待架构师独立验收。

**T-030B独立准备扩展完成（开发期）**：临时iris-t030b-independent-review/development-probe-3.log实际Core Service/Normalizer/Registry152项全过，含27固定文本向量、三阶段全错误码、12选择边界、原图/同代设置、取消/清理及GC。development-app-probe.log实际AppBootstrapper/DI Service/WinRtImagePreprocessor8项全过；Registry/Provider/Host用受控边界，无用户内容/网络。最终交付后仍独立Release/full、prepare.py与prepare_app.py重绑再跑两个项目，不把开发期结果直接结案。实际A已验收，不重写其Registry/预处理/Provider。

**T-030B开发期独立预检**：临时iris-t030b-independent-review已有27固定Normalizer向量、三阶段全ErrorCode回退表、原图/约束、caller/Stop门闩、停止等待者取消、原快照故障重试及抛错取消回调。实际Service/Normalizer的development-probe.log 137项全通过/退出0，manifest绑定开发当前Core.Tests DLL；不是最终验收。实际Registry同代配置/App退出/GC等仍待补，交付后仍须独立Release/full和重绑。A已结案，不重复返工其生产。

**T-030B实际下达确认（2026-10-06）**：消息01a1114c-907e-7693-b60b-86da29b35475进入指定新开发会话，执行轮01a1114c-9c2e-7cb1-a53b-037ceac6bea1；开发明确确认并读取current_task/OCR_PIPELINE/CCR、SoftLineBreakMerger/模型/CONTRACTS增量。已启动，未交付/未验收；等待超时未取消，不重发。CCR-031独立声明编译0警告/错误，日志临时iris-t030-contract-check/ccr031-build.log。A正式独立验收75项和全量已归档，不重复使用其开发期草稿结论。

**当前唯一工单T-030B（2026-10-06）**：T-030A/CCR-030在cfaf2045c62322d743f6b6466644f88c49baf6a0通过独立Release0警告/错误、全量3014（3012成功/2既有跳过）、Core51+App/Provider/Host24共75项实际探针。证据T-030A.review。现按OCR_PIPELINE.md/ARCH v1.47/CCR-031/current_task完成OcrService、精确回退、Normalizer及退出，父T-030范围保留；A已结案，入口/设置页不提前。开发会话仍01a10fa5-77e8-77e1-82d9-1ea1aa587d61，旧候选未定项以正式规范为准。

**T-030A开发交付待复审（2026-10-06）**：实现与归档见 tasks/done/T-030A.md。最终串行 Release 构建 0 警告/错误，全量 3014（3012 成功/2 既有 error183 跳过/0 失败）；原始开发失败及最终源码/DLL 指纹保存在 T-030A.evidence。已完成实际 Registry/Coordinator、Windows 位图/百度流清理与实际 Generic Host/自有 WPF owner 退出组合；未实施 B。CCR-030 仅已落地待复审，父/A 保持▶、B☐；当前单全文已归档并移除 current_task。尚待架构师独立验收。

**T-030A退出测试最新修正（尚未最终验收）**：开发已恢复AppBootstrapper的preparationPending保护，截图准备Stop失败时不关闭设置/翻译owner；原PreparationShutdownTests因此保留，不按早期分析直接改成允许关闭。旧SelectionShutdownTests的动作门闩时序已调整，首轮被确认挂起的测试进程由开发终止，未完整收尾事实见app-targeted-1-process.txt及原日志。第二轮23项完整返回：21成功/2失败，剩余为ScreenCaptureShutdownTests的独立Presenter顺序及AppBootstrapperTests旧原始异常类型断言，开发继续处理。此前Core690全过与独立Core51/App24均为开发期证据，最终交付后重新绑定；未签B。

**T-030A App定向等待诊断（开发仍在处理）**：架构师只读读取开发测试PID42728托管栈，原文临时iris-t030a-independent-review/app-stall-stack-1.txt；dotnet-stack 10.0.745401仅装系统临时iris-diagnostics-tools，不改项目依赖。看到SelectionShutdownTests.RunScenario的ClipboardSnapshotService空闲worker。结合该旧测试failDestroy分支：先await Stop抛错、之后才释放actionRelease；当前批准的新Stop路径先排空ActionDispatcher再汇总早期错误，导致受控动作与测试等待互锁。需要开发把失败分支改为先观察新允许的Presenter/排空等待，再放行动作后验证Stop失败/Host未停及重试，保留真实资源退出断言。另PreparationShutdownTests旧ActiveSession非null断言在新独立owner关闭行为下已失效，原异常日志保留。此处是现有单内测试时序分析，不要求回退正确独立清理语义或提前结案；原测试进程尚未由架构师终止/重启。

**T-030A扩展资源预检通过**：development-app-probe-4.log实际组合24项全过，含先前Bootstrapper13、实际Windows Provider/WinRT位图6、实际百度Provider原响应流重试4、真实Generic Host排空后处置1。外部OCR引擎/HTTP替身，自制PNG，无用户内容/网络。Core开发期51项保留；正式最终提交后需独立Release/full，分别prepare.py和prepare_app.py重绑运行Review.csproj/AppReview.csproj。临时iris-t030a-independent-review含全部源/日志/manifest；App早次仅缺System.IO的harness编译错误已修，原日志保留。开发尚未交付，不结案。

**T-030A App开发期预检**：临时iris-t030a-independent-review/AppReview.csproj已执行actual Bootstrapper+Registry，13项全过（development-app-probe.log）。窗口owner/capture/IHost是fake边界，没有真实窗口；覆盖正常等租约、owner失败可重试、早期故障仍清OCR且Host不早停。尚未交付；真实Provider资源组合、真实Generic Host处置与最终重绑/全量仍待完成。Core51项开发期通过保持，不重跑旧全量冒称本单验收。

**T-030A开发期预检结果**：开发第二次全解决方案构建0警告/错误后，独立实际Core Registry/DI/Coordinator程序已跑，development-probe-2.log 51项全通过；首轮仅harness删除计划缺TranslationOrder，补齐后通过，原失败保留。证据/执行DLL指纹在临时iris-t030a-independent-review。开发仍未交付，不能据此结案；正式最终Release/full、实际Provider/App退出组合及重新绑定仍待完成。上文/下文早期“未运行”为历史准备状态。

**T-030A开发与独立准备（实施中）**：已见实际OcrProviderRegistry.cs/OcrRegistrySnapshot.cs及凭据解析共享核心落地，执行轮仍未完成，尚无验收结论。临时iris-t030a-independent-review现有RegistryChecks/PublicationChecks/FingerprintChecks/外边界和Program/prepare.py；前者按独立契约编译0警告/错误（preparation-contract-build-4.log），后者实际DI程序未编译/运行。准备覆盖租约/清理/取消、构造失效、八组凭据、字段/重排/无关变更；真实Provider/Coordinator/Host/GC专项仍待补。后续B研究在iris-t030b-preparation.md，未签发，不改变A范围。正式交付后重绑最终DLL再独立验证，不用准备结果结案。

**T-030A独立准备进展**：临时iris-t030a-independent-review新增Boundaries.cs和RegistryChecks.cs，13项覆盖懒构造/同代快照/最后租约异步等待/原资源重试/停止等待者取消。外边界已编译，按独立CONTRACTS声明编译也0警告/错误；尚未绑定实际Registry，未执行产品检查，其它发布竞争/凭据/Coordinator/Host仍待补齐。勿把准备通过计入验收。

**T-030A接收确认**：执行轮01a110ea-30dd-7232-8685-12b18d6d5920已明确回复将实施本单，并实际读取current_task/OCR_PROVIDER_REGISTRY/CCR/交接、检查CONTRACTS差异及HEAD。当前已启动，未交付/未验收；下方首次快照尚无确认是历史状态，不需重发。

**T-030A直接下达记录（2026-10-06）**：指定新开发会话已建立队列消息01a110ea-2b7a-72e3-a2ea-085da602bed3及执行轮01a110ea-30dd-7232-8685-12b18d6d5920。45秒等待超时，未取消/重发；首次快照暂态interrupted、completedAt为空且仅1条输入，尚无可见确认/实施，不伪称已开始改代码。后续按此轮观察。CCR-030独立声明编译0警告/错误（临时iris-t030-contract-check，补既有Core.Errors全局using以匹配源拆分环境）；T-030A独立验收准备在iris-t030a-independent-review/README.md，目前只是检查清单，尚未执行产品验证。

**当前唯一工单T-030A（2026-10-06）**：T-029/CCR-029在6010f23aa1e3aa55e7c657e33584246be9c14e28通过独立Release0警告/错误、全量2971（2969成功/2既有跳过）、47项实际探针，证据T-029.review。真实Provider合成图SDK路径成功，真实多语言/桌面/NFR未验证。现按OCR_PROVIDER_REGISTRY.md/ARCH v1.46/CCR-030/current_task实现注册表、异步租约、发布屏障/清理重试、配置观察者与App生命周期；父T-030分A/B且全部原范围保留，B/入口/设置页未开始。指定开发会话仍01a10fa5-77e8-77e1-82d9-1ea1aa587d61。下方旧“当前/准备”记录按历史保留；T-029最终探针已经运行，不重复当待执行。

**T-029独立验收准备**：临时iris-t029-independent-review已完成Provider/真实位图+fake engine的Unicode/语言/能力/Bounds/非法输出、在途/排队/总预算/重试/GC以及实际DI与真实SDK合成图片检查，Program/prepare.py齐备；preparation-build-4.log编译0警告/错误，完整程序尚未运行。正式交付后独立Release/full，刷新最终Platform.Tests输出DLL再执行。长补充平面文本取消检查点仅为候选检查，不能据未运行草稿判缺陷。独立直接SDK预检result.log与开发actual Provider smoke的同目录result.json须区分。

**T-029 SDK前置独立事实（2026-10-06，非Provider验收）**：按当前工单获准范围，在临时iris-t029-sdk-smoke运行真实WinRT能力/合成图识别，退出0：MaxImageDimension=10000，installedLanguages=[en-US,zh-Hans-CN]，选en-US，800×180自有白底PNG识别一行HELLO 123，matchesFixture=true。无用户截图/内容/配置修改、无网络。result.log/Program.cs/Smoke.csproj在该目录，输入/16组Unicode预期在iris-t029-independent-review。说明本机SDK可用，不证明未来Provider已实现或其它机器/语言/NFR。正式交付仍需刷新实际产品做独立验证。

**T-029实际下达记录（2026-10-06）**：消息01a1109f-7ff1-70d0-a4e2-d45104a7aadf进入指定开发会话，执行轮01a1109f-9352-73f1-8e61-4f15a34efcb1。开发已确认Provider/Factory完整范围，区分fake与获准真实SDK合成smoke，并实际读取工单/规范/契约。已启动、未交付/未验收；send超时未取消，不重发。用户无需传话。

**最新唯一工单T-029（2026-10-06）**：T-028B/父T-028/CCR-028已验收结案，最终fe05ccbf84913b8e23928031ed24cecf5b43f924独立Release0警告/错误（27.11秒）、全量2893中2891成功/2跳过（1分25秒191毫秒）、实际65项全通过。HTTP200/401原流重试、总期限、timer/GC及两条实际命名管道9传输/2Token都已通过，不按旧线索返工。证据T-028B.review。现签WINDOWS_OCR.md/ARCH v1.45/CCR-029/current_task，实现本地Provider，不提前Registry/编排/入口。

T-029官方5页已读，临时iris-t029-official-docs；当前无包身份限制文字，不据记忆要求改安装，也不据文档存在假称运行成功。正式单允许只读能力及真实SDK合成图片smoke，主要测试fake OCR边界；不访问用户图像/自动安装语言。原准备稿iris-t029-preparation.md的未定问题已由正式规范解决（项目CJK定义不含韩文）。

**T-028B独立验收准备（尚未运行产品探针）**：临时iris-t028b-independent-review已有Program/prepare.py、Wire/Token/Error/Lifecycle/Ownership/Pipeline检查与纯Mock HTTP/时钟/流/Timer/日志观察器。完整准备编译preparation-build-5.log为0警告/错误，未执行；正式交付后独立Release/full，刷新最终Providers.Tests输出DLL后运行Review.csproj。重点包含实际命名管道9次重试及传输异常日志、入口到worker耗时后的总预算、HTTP200/401内容释放失败原流重试、timer/GC。开发已自行修复已见stream/timer/log_id及异常日志问题；上述检查线索不是最终缺陷。不要拿准备DLL或旧草稿判返工。

**T-028B实际下达记录（2026-10-06）**：消息01a11037-318d-7123-8f3c-10a009dac0dc进入指定开发会话，执行轮01a11037-3492-7563-b7fe-cd24a8c62552。开发已明确确认完整Provider/Factory及Mock HTTP范围，实际读取工单/BAIDU_OCR/CCR及契约增量。当前已启动，未交付/未验收；send超时不代表取消，不重发。不发送真实OCR/Token请求。

**最新唯一工单T-028B（2026-10-06）**：T-028A/CCR-027已验收，最终f091e1565b81ed63cb4e614940f710631940a6dc独立Release0警告/错误（44.92秒）、全量2754中2752成功/2既有跳过（1分14秒333毫秒）、实际56项全通过，证据T-028A.review。现在签发BAIDU_OCR.md/ARCH v1.44/CCR-028/current_task，完整Provider范围保留。Token获取者取消排空后其它等待者可重取、Test不依赖调用前旧缓存、最多3逻辑识别/9底层尝试、模式整份表单上限/安全解析/段落/处置规则均已定稿，不使用临时候选未定选项。直接下达新开发会话01a10fa5-77e8-77e1-82d9-1ea1aa587d61。

**T-028A实际下达记录（2026-10-06）**：消息01a11007-1a32-7330-9d6a-1317b59de289进入指定开发会话，执行轮01a11007-23e1-7d62-82d0-8309ff83ca14。开发已确认只补新约束/精确统计/回归，保留T-027退出；已实际读取工单/正式规范/契约增量。已启动、未交付/未验收，send超时未取消，不重发；无需用户传话。

**最新唯一工单T-028A（2026-10-06）**：T-027在b3f1f1b67eafc20bab12290dba40051b4cc0ab51验收结案；独立Release0警告/错误（12.95秒）、全量2722中2720成功/2跳过（41秒247毫秒）、实际WinRT/服务/Host/故障/GC40项全通过。证据T-027.review。官方百度图像上传要求Base64再URL编码；纯合成白BMP原2359350/Base643145800字节却得到9437321字节表单（无HTTP）。现按OCR_FORM_IMAGE_BUDGET.md/ARCH v1.43/CCR-027签发A可选精确表单图像预算，默认null/原构造不变，B保留全部Provider功能。旧T027完成能力不返工，当前工单详见current_task。

T-028B后续准备仍在临时iris-t028-preparation.md，官方6页全文与一次失败/重试记录在iris-t028-official-docs；除已签A的预算增量，其它HTTP与业务重试、Token代次/取消、错误脱敏、段落语义仍需B正式规范，不让开发据草稿扩单。最新指定开发会话仍01a10fa5-77e8-77e1-82d9-1ea1aa587d61。

**T-027独立验收准备（开发中，尚未执行产品探针）**：临时目录C:\Users\28910\AppData\Local\Temp\iris-t027-independent-review已准备15张合成格式/alpha/精确Base64余数输入、8组固定几何期望，以及GeometryChecks/ContractChecks/LifecycleChecks/OwnershipChecks和仅外部codec替身PreparationBoundary。两份公开检查的ReviewChecks.csproj曾单独编译零警告；完整Program/Review.csproj尚未运行。正式交付后独立Release/full，执行prepare.py刷新实际App.Tests输出产品DLL/源码指纹再运行Review.csproj；不可把草稿或开发74项通过当最终独立结论。AppBootstrapper真正失败收尾仍需最终代码/测试与必要组合检查。T-026/B已结案，不因该准备回退状态。

**T-027实际下达记录（2026-10-06）**：消息01a10fe3-5d3f-7703-879c-4fc7b04f9546进入指定新开发会话，执行轮01a10fe3-6f3b-7551-b870-3076e231f0a7。开发明确确认实际WinRT编解码/生命周期/App退出范围，已全文读取工单/正式规范/CCR并核对基线。当前已启动、未交付/未验收；send超时没有取消，不重发。正式规范覆盖旧候选未定项，用户无需传话。

**最新唯一工单T-027（2026-10-06）**：T-026B-R2/父T-026已验收结案，最终4805b224f041f2bc437ee9460290ef7bc8d0bc41独立Release0警告/错误（5.52秒）、全量2638中2636成功/2跳过（44秒344毫秒）、35项实际检查全通过/STA退出；28张已查看原图和独立7图指纹保持。证据T-026B-R2.review。现签发IMAGE_PREPROCESSING.md/ARCH v1.42/CCR-026/current_task，完整实现图像预处理与异步收尾，不扩OCR Provider/入口。旧候选中未定问题已由正式规范解决，勿按候选重新索要许可。直接下达新开发会话01a10fa5-77e8-77e1-82d9-1ea1aa587d61；下方R2执行记录为历史。

**T-026B-R2实际下达记录（2026-10-06）**：消息01a10fcc-004e-71a2-b5cb-d2c42d172ab3已进入新开发会话，执行轮01a10fcc-16ad-70e2-8a32-71dc827430f2。开发已确认只补建立期间同伴关闭、保留R1成果，且实际读取工单/精确反例并核对工作区。已启动，未交付/未验收；send等待超时未取消任务，不重发。

**最新唯一工单T-026B-R2（2026-10-06）**：开发R1执行轮已completed，交付7e6687d0a17206edb613354a0022cb1618a18686。独立Release0警告/错误、全量2634中2632成功/2跳过、原34项全通过；扩展35项仅建立第三窗时第二窗实际Closed后会话10秒不结束失败，caller取消后全关闭/清理零错误/STA退出。R2只补建立期同伴Closed，保留所有R1/C26B-1成果，父/B仍▶，不开始T-027。证据T-026B-R1.review；原28张已查看PNG指纹一致。仍使用新开发会话01a10fa5-77e8-77e1-82d9-1ea1aa587d61直接下达，旧会话不再使用。

**T-026B-R1实际下达记录（2026-10-06）**：新开发会话 `01a10fa5-77e8-77e1-82d9-1ea1aa587d61` 已接收队列消息 `01a10fac-bd37-78e3-a55e-4321221a510e`，执行轮 `01a10fac-cedb-7d31-9d5b-782a85e5d428`。开发明确确认先读正式工单/交接/规范，按六组及C26B-1修复，保留基线证据并另存验证。当前已启动，未交付/未验收；不重发。读取的暂态interrupted且completedAt为空不作为终止依据。下方“正下达/待下达”为历史签发阶段。

**新开发会话接替（2026-10-06）**：用户指定 `01a10fa5-77e8-77e1-82d9-1ea1aa587d61`，已读取确认其工作区F:\Iris、master基线5cda8cc73及已进入开发角色的回复。架构师正直接下达现有T-026B-R1；不再向旧故障会话发送。下方等待新ID/恢复旧会话均为历史，不再阻塞当前工单。接收和执行轮以随后实际记录为准。

**最新状态（2026-10-06，T-026B-R1已签发待下达）**：架构师已保全并提交B交付快照`5cda8cc73f24d0b29778153c337e0c93531a9c85`，未改生产/测试源码；独立Release0警告/错误，全量2621（2618成功/2跳过/1既有fake竞态），独立实际service/Window/PNG/视觉34项27过/7失败。R1处理确认后迟到输入、意外Closed、最后查询后的焦点许可、安全诊断、右键坐标、标签避让及C26B-1 fake退出维护，详见current_task及B.md第9节/B.review。父/B仍▶、A☑，CCR-025待修正，不开始T-027。

指定开发会话的原轮与一次续接均400 invalid_request_error；API compact恢复被active writer拒绝，本机控制代理10050，Computer Use native pipe仍os2。没有重启Codex/改用户配置/删或回滚会话。已请求用户提供可用新开发会话ID或恢复原会话；目前没有把R1再次投递到坏会话，不伪称开发已接收。后续拿到ID先同步开发角色/范围，再直接下单；用户无需传话。下方B实施/恢复记录按时间保留。

**当前状态（2026-10-06，T-026B恢复交付待复审）**：原开发轮01a10da9-84d4-7893-8f34-d28a9036a0b4在最后全量完成后failed（400 invalid_request_error param=input），续接轮01a10e12-f4fc-7ae2-b281-311cc32451e1同样失败且无实施。最终开发日志已核实：112项定向、Release0警告/错误、全量2621（2619成功/2既有跳过）。尚未有开发完成报告/提交，故架构师保全工作树，补齐T-026B.md交付快照归档、更新最终22PNG并保留14张旧版，不改生产/测试源码。current_task已归档移除，父/B仍▶、CCR-025待独立复审；这不是验收结案。

恢复尝试：官方App Server支持thread/compact/start，但单独resume被active writer拒绝；本机控制代理socket报10050；Computer Use重新连接仍native pipe unavailable/os error2，未进行UI输入。原会话未被删除/回滚，用户工具源码/配置未修改，未重启应用。已向用户请求可用开发会话ID或原会话恢复确认，同时继续本地交付和独立复审；未自行新建用户会话。

独立程序在系统临时目录iris-t026b-independent-review，开发期32项预演26过/6失败，另加确认释放失败对照/标签重叠两项尚待最终运行。最终复审须绑定恢复提交重新运行，不能把旧预演当最终结论。32项说明和R1候选见该目录README及iris-t026b-review-preparation；新旧渲染查看记录在iris-t026b-reviewed-renders/pass1/pass2，最终PNG需散列核对。当前无新开发工单，下一张修正单由架构师在复审后签发。

**当前唯一工单（2026-10-06，T-026B）**：T-026A已在c7282698028da861dbc9dfc6e9430b9cdd1e842b最终验收，CCR-024结案。R3独立Release0警告/错误、定向1通过、全量2565（2563成功/2既有跳过）、直接交付helper异常/迟到回调检查通过，R2生产95项证据由相同src保留。见A.md第14节/R3.review。现已签发SCREENSHOT_INTERACTION.md / ARCH v1.41 / CCR-025和current_task，完整实现每屏overlay/物理框选移动/8倍放大镜/确认取消/可选复制/条件焦点及ScreenshotService，不扩到OCR/Handler/设置页。父T-026/B为▶、A为☑；下文R3等待与候选稿均为历史，旧暂停早已解除。

**T-026B实际下达记录**：队列消息01a10da9-6f03-7c90-8ab0-5c19aab24e63已进入指定开发会话，执行轮01a10da9-84d4-7893-8f34-d28a9036a0b4。开发已明确确认读取工单/新规范/注释并开始核对实际准备与捕获接口，实施overlay/服务/复制/退出及离屏渲染。当前实施中，未交付/未验收，不重发。send等待45秒超时没有取消任务，后已确认实际运行；无需用户再次传话。

CCR-025只预置两处XML注释，需B同步Core；复制失败仅安全警告并保留确认图像，交互确认前失焦/拓扑取消，确认后外部前台仅阻止焦点恢复；B意图观察保持到最后结果结算，不能随A租约提前丢失。正式规范已替代临时候选，不从旧草稿再追加范围。架构师本轮验收/规范/交接/状态文档和R3.review由B保留提交，生产未在架构师侧修改，未推送。

**当前唯一工单（2026-10-06，T-026A-R3，仅测试维护）**：R2最终b913db018a6332fcc4fceb01644fe07f4714fe7a已完成生产专项独立95项（原11+84顺序）全过，STA退出，Release0警告/错误。首次全量2565中2562成功/2跳过/1失败，旧自启测试在Save完成但新查询未就绪时过早断言；单项重跑通过，受控actual VM/Coordinator观察证实fresh query完成retry=true、旧回调释放后仍true。详见R2.md第10节/R2.review，失败原文保留。R3只修该测试等待/失败收尾，src/契约0改动；A生产成果保留，不再返工，父/A仍▶，待最终全量后结案，B未开始。开发R2轮已completed，直接签发R3。

**T-026A-R3实际下达记录**：队列消息01a10d93-3747-75c3-a37f-1a69f01f2f49，执行轮01a10d93-3db3-7af1-b00f-a114bc5dab1c。开发已确认只维护该测试/专用等待支撑，生产保持不变；当前正在实施，未交付/未最终验收，不重发。send等待45秒超时不代表终止，后续确认正常执行。R3完成后核对src/契约0diff、独立定向/全量；原95项生产结果按相同源码保留，不需因纯测试维护重写或重新归因。B候选仍在临时目录，A最终结案后再签发。

**当前唯一工单（2026-10-06，T-026A-R2）**：R1提交69ab481cc0893cc019ed45e1ba8e4da1f4edc4f0独立Release0警告/错误，全量2557（2555成功/2既有跳过），原九项探针全过。扩展两个actual owner/Window组合反例：新Show后只开始下一operation被旧恢复隐藏（对照初交DLL，属R1回退）；新Show原生失败并清理后被旧恢复复活（原有未补齐路径）。最终11项9过/2失败，STA退出。证据T-026A-R1.md第10节/R1.review；保留R1三项修复，只签R2，父/A仍▶，B未开始。下文R1执行记录为历史；开发R1轮已completed。

**T-026A-R2实际下达记录**：队列消息01a10d7b-b007-71b3-a8e8-a005c56863d9已进入指定开发会话，执行轮01a10d7b-be0c-7a12-aad8-8c1c0317812e。开发已确认读取工单/扩展反例，保留R1三项修复并处理两项可见性归属；当前正在实施，未交付/未验收，不重复发送。send等待45秒超时没有取消，随后已确认正常执行。

B后续准备稿位于系统临时目录iris-t026b-spec-candidate.md，覆盖真实每屏overlay/物理框选/8倍放大镜/复制/焦点/退出候选。仍未签发，不得把候选作为R2扩单；须先验收A，再定稿复制失败语义/焦点等并签B。

**当前唯一工单（2026-10-06，T-026A-R1）**：T-026A初交cb5981e7877bcdb3bad127934096b3f1290e18b3已完成独立构建/全量：0警告/错误、2547项（2545成功/2既有跳过）。实际产品探针9项中6通过、3失败：同operation的原生定位show先于撤销通知，恢复内Show→Hide后旧恢复复活窗口，关闭失败的可见设置窗口被准备忽略。证据T-026A.md第8节/T-026A.review；保留C25-1与其余成果，只签R1三项，A/父T-026保持▶，B/T-027未开始。下文初交实施状态均为历史；用户恢复推进仍有效。开发原轮已明确completed，可直接下发新工单。

**T-026A-R1实际下达记录**：队列消息01a10d6b-ed58-7210-bcda-9c99d02ee6ee已进入指定开发会话，执行轮01a10d6c-069f-7800-8ca8-c8ce4062bbcd。开发已明确确认完整读取R1/独立证据，仅修三项并保留失败证据；当前实施中，未交付/未验收，不重复发送。send等待45秒超时并未取消或重发，后续回传确认已执行。

**最新恢复安排（2026-10-06）**：用户明确“继续推进吧”，此前T-025结案后的暂停解除。当前唯一工单T-026A，按SCREENSHOT_PREPARATION.md / ARCH v1.40 / CCR-024实施，直接协调原开发会话。下文暂停和T-026未签发记录均为历史，不再据此拒绝本单；仍一次一单、不推送。

**T-026A实际下达记录（2026-10-06）**：队列消息`01a10d35-e08b-7a61-ba82-c12bbe38bc78`已进入指定开发会话，执行轮`01a10d36-027a-7fb3-8140-dc0a014e16b1`。开发已明确确认读取本单，并开始核对/复用实际窗口、录入Coordinator与MouseController，增加暂停和意图版本；当前正在实施，未交付/未验收，不重复发送。最初持久记录interrupted/空items并非终态，后已回传消息。Computer Use连接与重连返回“native pipe unavailable / os error 2”，没有进行UI输入；CLI/App Server桥接已确认正常，本轮无需插队。

**历史：T-025验收与当时暂停（2026-10-05）**：当时已落实暂停、没有活动工单；2026-10-06用户明确恢复后，由上文T-026A签发状态替代。实现与证据见tasks/done/T-025.md第9节。

**历史暂停安排（2026-10-05，现已恢复）**：用户当时要求“T-025完成后先暂停开发，不要再推进了”，T-025验收后已执行暂停。2026-10-06用户“继续推进吧”已解除这项暂停，不再用此历史记录阻止当前已签发的T-026A。

T-025已于2026-10-05正式签发，以下历史候选稿及旧状态均服从“当前唯一有效状态”。实际下达确认另记；不要因旧T-024进行中记录重复下单。

**T-025 实际下达记录（2026-10-05）**：消息 `01a10bae-5757-7993-9490-cb36679bce0d` 已进入指定开发会话，执行轮 `01a10bae-781c-7ce3-8c61-a3b06024b507`。开发已确认完整读取工单/SCREEN_CAPTURE.md/ARCH v1.39/CCR-023，并开始GDI捕获、异步三格式编码与退出接线；新增测试仅fake OS/合成像素，保留现有架构师文档和复审证据。当前T-025进行中，未交付/未验收，不重复发送。45秒send等待超时没有取消任务。T-026仍未签发，准备事实在临时iris-t026-preparation.md（含DwmFlush只同步调用进程、不flush整个session的官方限制），不能提前实现。

复审准备补充：临时 `iris-t025-independent-review/CropChecks.cs` 是尚未编译/执行的负坐标、半开交集、padded stride与真实三格式解码验证逻辑；最终必须接actual capturer + fake OS，不能默认DI真截屏，也不把准备稿当证据。T-026候选补证已实际运行 `iris-t026-suppression-observation`（3a8450e产物，退出0）：现有BeginCaptureSuppression仅抑制失焦撤销，同operation的新TryShow仍可重新显示；普通Hide会撤销旧发布，最终STA已退出。它是未来隐藏/恢复设计的事实依据，不是T-025或既有翻译功能的返工要求；不授权提前改Presenter。完整输出见临时T-026准备文档。

T-025草稿补证（非正式验收）：`iris-t025-retention-observation/quiescent-result.log`已用4×3原始帧/1×1实际编码验证调用方放弃后均可GC回收，句柄0且Stop后线程退出；最初同步GC的短暂引用不是缓存缺陷，必须先让完成回调展开。`iris-t025-native-cancel-observation/result.log`已确认caller未取消时底层自行取消映射安全CaptureFailed、原异常标记不泄露且资源释放。各目录assemblies.json绑定实际草稿DLL；源码已增加_active清除、worker委托捕获清空及cancelled()分类，不按旧草稿线索返工。正式独立程序现含Program/CropChecks/LifecycleChecks/CaptureChecks及prepare.py，仍未编译/执行，待最终交付重读接口并刷新产物。T-026更完整候选见临时iris-t026-design-candidate.md，尚未签发。

上述T-025正式独立程序后已补RetentionChecks，并完成准备性编译（prepare-build.log，零警告/错误），尚未运行；precommit-manifest仅为未提交代码记录。正式交付后刷新DLL再执行，不把编译当验收。按最新用户要求，本单验收后暂停，不再推进T-026。

历史探针准备（现已执行并复现，最终证据见第11节）：T-024B2 独立探针模板在系统临时目录 iris-t024b2-independent-review，尚未编译/执行。prepare.py 在最终交付后复制现有服务链 fixture/外部 fake，并仅给外部 placement 增加“Show后的第二次定位失败”开关；Program.cs 使用实际Controller/monitor/Coordinator/Composite/Window观察失败后的可见性与重试/停止，不把准备稿当产品反例。最终fixture如变动须先适配，保持产品状态机不替换。

历史 T-024B2 复审准备（最终只确认下述部分显示失败为阻断，其余不据草稿返工）：系统临时 iris-t024b2-review-preparation.md 记录当前草稿中待最终复核的部分显示失败、取消与dispatch完成、多清理错误、timer持有及视觉失效场景。尚未运行产品反例，不能据草稿直接判返工；完整交付后重读最终代码并做实际Window/native边界验证。

T-025 准备（T-024B2 实施期间，未签发）：系统临时目录 iris-t025-preparation.md 记录原始截图/GDI资源/异步编码候选，iris-t025-official-docs 保存已实际读取的13页微软文档。已确认 ReleaseDC 同线程、DIB bits 访问前 GdiFlush、GetDIBits 不能用于仍选入DC的bitmap、WinRT BitmapEncoder 的异步完成/stream顺序。CropAsync/预算/捕获worker生命周期仍待本轮鼠标闭环验收后正式 CCR，不授权提前实现。

补充可执行依据：iris-t025-codec-probe 已用合成2×2像素验证内建PNG/JPEG/BMP内存编码/解码，退出0，无新NuGet/真实屏幕/用户文件；PNG/BMP逐像素一致，JPEG符合有损性质。结果仅支持技术选型，未验证截图或大图/取消/NFR。

历史 T-024B1 独立复审准备（现已执行通过，见归档第 9 节）：系统临时目录 iris-t024b1-independent-review 已准备实际 Coordinator→Composite→C1/Keyboard/UIA 探针，尚未编译或运行。交付后执行 prepare.py 刷新实际 App DLL 引用及外部 fake；Program.cs 覆盖 HWND/PID/名字失配与标题/大小写对照，以及被动文本读取/恢复 Close 两道门闩下的显式抢占和待办取消，预期零提前 UIA、原剪贴板恢复。不得把准备稿当作通过证据。

全量回归范围澄清（2026-10-05，T-024A 归档审计）：DesktopContextNativeReadOnlyTests 自 3147224 / T-022A 起已存在且已验收，实际调用 GetCursorPosition/GetMonitors/GetMonitorAt，T-022A 归档也明确记录光标用例通过。后续全量测试保留这两项既有只读 smoke；不得把“新增测试只用 fake”扩大成“全套没有任何 native 桌面查询”。它们不读取真实前台进程/窗口标题、选区或剪贴板，不输入/装 hook；新增鼠标测试禁真实交互仍保持。此前未验证真实应用/桌面兼容性的声明指功能端到端，不等于没有运行该只读回归。具体边界已同步 MOUSE_SELECTION_MONITOR.md §6 与 DEVELOPER_HANDOFF。

历史复审准备（T-023B 实施期间，现已完成，最终结果见第 8 节）：系统临时目录 `iris-t023b-ui-review` 已准备真实 WPF/同款主题的离屏渲染与非法超时文本验证，尚未执行，须交付后刷新引用并按实际字段接线核对；`iris-t023b-independent-review` 为实际 Core 协调器列表捕获/冲突/历史进程名语义探针，同样尚未运行。开发观察可用临时 `iris_watch_dev_compact.py <turnId>`，仅返回新消息和真实最新命令摘要，绝不据游标文件推断完成；旧 watcher 仍可使用。

历史 T-023B 补充检查线索（最终已修复并通过实际产品验证）：首次 App 定向日志 iris-t023b-ui1.log 为 14 项（6 成功/8 失败），开发正在修新页面保存前绑定刷新问题，尚未交付。架构师独立框架探针 iris-binding-observation/result.log 输出 `WPF_LOSTFOCUS before=731 pending=True after=400 source=400 inputPreserved=False`，证明 LostFocus 待提交文本可被源 PropertyChanged(null) 覆盖；没有加载产品程序集或显示窗口。实际产品尚未复现，不能据此直接判定最终 B 失败。已在 iris-t023b-ui-review/Program.cs 加入两个真实 TextBox 的 pending 值遇无关 Changed 更新检查，待最终交付后执行；如果实现已修复则通过，不重复返工。

> 2026-10-05 更新；由 Codex 接替 Claude Code 的架构师职责。当前状态以 `tasks/BACKLOG.md` 与 `tasks/current_task.md` 为准；本文件记录交接事实与后续注意事项。

**T-024B2-R1 实际下达记录（2026-10-05）**：消息 01a10b64-0b0b-78e1-8c6b-1737a3d20ef6 已进入指定开发会话，执行轮 01a10b64-28b2-73e0-bf1f-d93f340edbdf；开发确认将按 R1 与 B2 第11节实际反例修复部分显示失败收尾。当前进行中，未交付/未验收，不重复下发；T-025 保持未开始。

**T-024B2 实际下达记录（2026-10-05）**：消息 01a10ae5-0cce-7ee0-ad80-608025beec95 已进入指定开发会话，执行轮 01a10ae5-0d9f-7872-9266-ac89717f3f7a。开发确认开始读取完整工单/交互规范并实施图标、缓存、自动翻译、联合设置和退出接线。当前进行中，未交付/未验收，不重复下发；候选设计以已签署 MOUSE_SELECTION_INTERACTION.md / CCR-022 为准。

**T-024B1 实际下达记录（2026-10-05）**：消息 01a10abb-dbb9-7bf1-a3bd-b612c41c8e43 已进入指定开发会话，执行轮 01a10abc-01ce-7e63-881b-85be61fc4e75。开发确认开始读取工单/协调规范并实施，同时确认全量保留两项既有只读桌面回归、新增测试使用 fake 外部边界。B1 正在进行，未交付/未验收，不重复下发。B2 的候选方案在系统临时目录 iris-t024b-design-preparation.md；其中 ActionContext、缓存/图标、自动模式、鼠标设置与 Toggle 尚未签发。

**T-024A 实际下达记录（2026-10-05）**：队列消息 01a10a93-944b-7363-8122-e609dc71a494 已进入指定开发会话，执行轮 01a10a93-b94d-7413-8c5a-a9936c347776；开发已确认完整读取当前工单/鼠标规范并按 A 范围实施。正在进行，未交付/未验收，不重复下发。原临时 T-024A 候选稿被已签发 MOUSE_SELECTION_MONITOR.md / CCR-020 取代；B 的缓存来源/操作移交仍需后续正式定稿。

**T-023B 实际下达记录（2026-10-05）**：消息 01a109a9-9381-7101-9ee5-29ada92f2faf 已进入指定开发会话，执行轮 01a109a9-a76d-7631-935b-89e8f9776813；开发已确认读取工单/正式规范并开始核对保存流程。正在实施，未交付/未验收，不重复下发。系统临时目录 iris-t023b-independent-review 已准备实际 Core 协调器的外部边界探针，尚未构建或执行，须待本单交付后引用当前 DLL 运行。

**T-023A 实际下达记录（2026-10-05）**：队列消息 01a10960-04a3-7b93-b8d3-07237eed5322 已进入指定开发会话，执行轮 01a10960-1b4d-71b3-8a64-3578f5a0ceb4。开发已确认读取正式工单，并说明使用实际 UI 通知接受点与共享发布/退出屏障；正在实施，尚未交付或验收。不要因 bridge 的 notLoaded/interrupted 暂态或等待超时重发。下一阶段准备稿 iris-t023b-preparation.md、iris-t024-preparation.md 及官方鼠标 API 原文均在系统临时目录，未签发。

**当前唯一有效状态（2026-10-06）**：用户已恢复推进，基线master / 5ceb182f0fbb3cfb536a81befe5e6f433cbf7473。T-025/CCR-023已验收，当前唯一工单T-026A：实际owner的临时隐藏/新意图/恢复、旧录入清理、鼠标暂停及App退出，依据SCREENSHOT_PREPARATION.md / ARCH v1.40 / CCR-024。T-026父范围拆A准备和B完整overlay/ScreenshotService，总范围不减少；B/T-027不提前实施。C25-1只补强两项既有测试，生产不返工。开发会话仍为01a1088c-7a7f-7db3-a153-7fae5cfedf4d；等待本轮实际下达确认，不重复发送。真实桌面/混合DPI/焦点/NFR未验证。`.codex/`原样未跟踪，不推送。

**当前协作方式（2026-10-05 最新用户指令）：直接协调 Codex 开发会话。** Gemini 额度耗尽，用户将开发职责交给会话 01a1088c-7a7f-7db3-a153-7fae5cfedf4d，由本架构师直接说明角色、下达工单并读取交付，用户不再传话。使用用户提供的 .codex/codex_threads.py 与官方 App Server；运行中追加要求需确认插队生效。旧手动转交/Antigravity 控制记录均属历史。角色说明已被开发会话确认（turn 01a10897-8592-7e32-b35a-3595727df562 completed），R6 已在 turn 01a10899-0c31-71b1-88cc-818fb3571432 completed 后交付并通过独立复审，C1 与 R1 已交付并通过独立复审（最后交付 turn 01a108dc-ba80-73b3-98a3-2af688007ae9 completed）；C2 已在 turn 01a108f2-da8d-7653-a3cb-fc17f0e9d0b9 completed 后交付；首次复审已完成，接下来直接下达 R1。详细职责见 specs/DEVELOPER_HANDOFF.md。不推送。

**主动交付下一工单（2026-10-03 用户明确要求）**：验收通过后直接准备并给出下一步完整工单和可转交指令，不让用户再追问索取。仍保持一次一个实施工单；必要的架构/契约先由 Codex 定稿，不以“下一步待规划”代替交付工单。

**追加提示操作要求（2026-10-05）**：Codex 桌面会将运行中消息放入队列。追加提示需点击对应“插队发送 / Send now”并确认进入当前执行轮；不得覆盖用户未发送的输入。不要仅因读取超时或 notLoaded/interrupted 暂态而重复下单。

**T-021 直接下达记录（2026-10-04）**：已在 Antigravity 的 Iris / Executing Software Development Task 会话发送 current_task.md / KEYBOARD_SIMULATION.md / ARCH v1.30 的实施指令，包含 C20B-1、R4 分项报告更正与禁推送约束。发送后一次界面状态切换导致误触停止，已明确发送“继续执行刚才完整 T-021，任务未取消”，并观察到消息进入会话、输入框清空和 Working 状态。后续不要重复签发 T-021；等待实际完成报告及提交再复审。发送不代表实现已验收。

**T-021 原范围补正（2026-10-04）**：Gemini 初次提交 689a5911bd8a5689b64c55bba1ebfc6e65a0322e，尚未验收。Codex 读取开发代码发现清理结果丢失、按下 Ctrl 后目标查询异常不补偿、并发清理未登记、原始异常日志及测试计时/收尾缺口，已在原会话直接发送自检补充并确认 Gemini 正在分析修改。该提示发送时前一轮已结束，未出现排队/Send now 控件；它已直接进入当前执行轮，不额外点击底部停止按钮。current_task.md 已恢复原工单并附本次自检，完成后追加原归档、独立补正提交，不推送，不开启 T-022。

**已完成的直接协作记录（现已暂停）**：配额恢复后 Gemini 接续 R2 并提交 cec13f0，Codex 独立验收并提交结案文档 9b9991a；随后直接签发 T-020A，收到 6303d29 交付并开始独立复审。之后用户恢复手动传话。下文 T-016/T-017“待实施”等描述均属历史，不得重发旧单或据历史授权继续控制。

历史连接故障为 `failed to write kernel assets: 系统找不到指定的路径。 (os error 3)`，本轮已不再阻塞。工单已下达不代表已完成或验收通过；目标仍保持 active。

上文 T-016 复审段落中的“用户手动转交”描述当时状态，现由本条最新授权覆盖。技术目标仍为完整交付 Iris，T-016-R1 当前工单不变；目标保持 active，本轮未宣称完成或标记 blocked。

接续复核证据：tasks/done/T-019B-R6.md §6；独立探针 `C:/Users/28910/AppData/Local/Temp/iris-t019b-r6-resume-review`，Light / Dark 分进程运行，图片同目录。C19B-1 的非阻断文字/测试维护已附入 T-015。此前 `iris-t019b-r6-review` 是旧中断过程的临时探针，不能把其不等待展开动画的旧图片当作最终布局。项目距离完成仍有输入/热键、划词、截图/OCR、悬浮球、历史 UI、网络设置与打包发布；下一顺序保持 T-015 → T-016 → T-017。

## 历史状态（以下“当前/最新”均是当时记录，服从上文）

**当前状态（R5 复审后，2026-10-02）**：HEAD 234971401c56e21cc5b33ee38f19c8187e445a6a；当前工单 T-019B-R6。真实 Registry/取消分支/新 scope 重试证据已确认；Release 零警告/零错误，全量 1396 成功/1 跳过。最终以 App 同款 Light 资源渲染发现 600×440 详情水平裁切，另 Case7b/7d 失败清理未完整等待/放行。R6 只修布局与测试收尾；渲染探针和两张 PNG 在临时目录 iris-t019b-ui-review，详见 R5 报告末尾。继续直接控制 Antigravity，不需要用户传话，不推送。

**当前状态（R4 复审后，2026-10-02）**：HEAD 14f92b26737238650dc8dc14e2c12ceb7e3e5549；当前 T-019B-R5 主要是测试/证据收尾。生产统一解冻及全部 25 项诊断已独立通过，Release 零警告/零错误，全量 1396 成功/1 环境跳过；Case7 的真实 Registry、实际取消结果与新窗口/scope 重试仍不符强制验收要求。保留生产成果，不再泛化返工；详细见 R4 报告末尾与 current_task。继续直接控制 Antigravity，与 Gemini 原 Iris 对话协作，不需用户传话、不推送。

**当前状态（R3 复审后，2026-10-02）**：HEAD 3a59b35de9c8806f48e1466772e12e0d8e261958；当前工单 T-019B-R4。R3 原三类边界及既有 24 项诊断通过，Release 零警告/零错误，全量 1396 成功/1 环境跳过；重试期间新增项在结束后仍冻结，回执删除实际提交/容量取消/双重试失败的正式证据仍缺失。报告方法名已更正，但注释不能作为已执行断言。详见 R3 报告末尾与临时探针 iris-t019b-r3-review。保持直接控制 Antigravity，与 Gemini 原对话协作；未验收不前进 T-015，不自动推送。

**当前状态（R2 复审后，2026-10-02）**：HEAD 57c954f350689710e97017fde8dce97571e5ae75，R1/R2 已一起独立提交。R2 未验收，当前单 T-019B-R3。既有 20 项诊断达到预期，Release 零警告/零错误，全量 1396 成功/1 跳过；同步启动 Drain/空闲标志、当前 Prompt 删除、冻结重试期间外部成员合并仍有反例。证据在 tasks/done/T-019B-R2.md 末尾，临时探针 iris-t019b-r2-review。Codex 已恢复并成功直接控制 Antigravity，Gemini 停止修改等待复审结论；下一步直接发出 R3 并继续复审。旧探针需按正确退出顺序调整闸门，不可因为应等待而回退 Drain。项目目标仍是持续开发完整交付，不在 T-019B 阶段宣称完成。

**最新状态（T-019B-R1 复审，2026-10-02）**：R1 未独立提交，HEAD 仍 f8a59ba；保留全部工作区成果。旧 12 项反例通过，Release 零警告/零错误，全量 1396 成功/1 环境跳过。保存后旧选中/集合冻结、Prompt 刷新、队列重入/提前 Drain、异步重试 UI 收尾及证据问题导致不通过；当前单 T-019B-R2，详见 R1 报告末尾。临时探针 iris-t019b-r1-review 含源码指纹和实际 App/Dispatcher 的受控反例。用户本轮已恢复 Antigravity 控制，连接成功，由 Codex 直接下达工单与读取 Gemini 交付，不再要求用户传话；不自动推送。

**历史复审（2026-10-02，R1 签发时）**：T-019B / f8a59ba 未通过，当时签发 T-019B-R1。独立 Release 零警告/零错误，全量 1388 成功/1 隔离窗口站环境跳过。临时目录 iris-t019b-review 的真实 App/Dispatcher 与 fake 外部边界反例确认队列永久停止、凭据丢失、保存捕获/冻结、外部更新/旧结果、隐私和 UI 自启查询问题；另要求删除生产 General 保存旁路、补齐拖拽和真实接线证据、更正报告。详见 tasks/done/T-019B.md 末尾与 current_task.md。T-019 / T-019B 保持 ▶，CCR-012 待复审，T-015 不开始；继续用户手动转交，不自动提交/推送。

## 1. 历史来源与中断位置

2026-10-02 直接下达记录：已通过 computer-use 在 Antigravity 的 Iris / Executing Software Development Task 输入并发送 T-019B-R2 指令，发送后消息显示于会话，输入框清空且显示 Working。要求阅读 current_task.md、保留未提交 R1、完成 R2 后独立本地提交并在原对话报告，不推送。此记录证明已下达，不代表 R2 已实施或验收。

已取得以下两份完整的本地 Claude Code JSONL 文件，逐条解析成功，未发现缺失的父消息引用，也没有上下文压缩边界记录：

- `C:\Users\28910\.claude\projects\F--Iris\cd92bb57-8d5a-41d0-b8ae-031a792e7d6a.jsonl`：初始需求、架构设计、T-001 至 T-007 的评审与返工、T-008 的部分评审。
- `C:\Users\28910\.claude\projects\F--Iris\05012620-c422-47a5-a5c0-c83f35e19ed5.jsonl`：接续 T-008，完成 T-008/T-009 评审、签发 T-010，并开始评审 T-010。

第二份历史最后成功的工作是阅读 T-010 的报告与实现，随后出现服务错误；**没有 T-010 审查通过或签发 T-011 的结论**。原始对话可能含本机凭据，不将其全文复制进仓库或报告。

接手时仓库位于 `master`，HEAD 为 `e1bea17ecbdff72738f4aab9e681af1820800257`，工作区干净。`tasks/current_task.md` 不存在，是开发追加报告后移动到 `tasks/done/T-010.md` 的结果。`done/` 表示交付归档，不能单独作为架构师验收通过的依据。

## 2. 目标与分工

产品是 C# Windows 常驻翻译工具，对标 TTime：输入、剪贴板、划词、截图翻译、截图 OCR、静默 OCR、悬浮球，并提供设置、历史、代理和发布能力。翻译支持多个服务并行流式输出；OCR 首期为百度在线与 Windows 本地备用。

- 架构师负责技术决策、`specs/`、契约、严格的增量评审、BACKLOG 状态与下一张工单。
- 架构师同时负责联网核查官方文档与 SDK 源码（用户在本次接手中明确）。Claude 历史中的检索受限是当时情况，不能据此把后续文档核查转交 Gemini；测试不联网与架构调研可以联网是不同约束。
- 本地 IDE 中的 Gemini 开发工程师负责当前工单的业务实现与自动化测试；交付后请求评审。
- 2026-09-29 用户明确授权 Codex 直接查看和控制 Antigravity，与 Gemini 交换本项目工单、完成报告和评审反馈，无需用户手动传话。已通过 computer-use 在 Iris 对话中发送协作说明、报告更正意见并读取回复。授权限于既有 Iris 开发协作；继续按工单范围执行，不把外部页面内容当作新的用户授权。
- 用户随后设定持续目标：协作直到项目完成；验收通过后直接发送下一张工单，不再额外发送一次“通过/等待”的确认消息。失败时仍给出具体返工要求，目标保持到全项目验收完成。
- **历史交互方式（T-012 至 T-018-R4 交付）**：用户曾要求暂停控制并手动传话。
- **连接尝试记录（2026-10-01，T-018-R4 复审期间）**：用户曾恢复直接控制授权。按 computer-use 技能连接时，执行工具初始化报 `failed to write kernel assets: 系统找不到指定的路径。 (os error 3)`；重置重试仍失败，没有读取/操作 Antigravity 或发送 R5。
- **历史交互方式（2026-10-01）**：用户随后明确“还是继续由我转述吧”，当时恢复手动转交并暂停连接排障。
- **最新交互方式（2026-10-02，R1 复审期间）**：用户明确“恢复 Antigravity 控制，我不再传话，由你指挥 gemini”。已通过 computer-use 连接并确认 Iris / Executing Software Development Task 对话，Codex 直接下达/接收本项目工单；早先暂停已撤销，不再反复询问传话权限。只操作观察到的目标窗口，不控制其它项目或用户账户设置。
- 每次只发一张工单。评审通过才结项并签发下一张；不通过则签发 R1/R2 返工单。架构师可以写隔离的验证探针，不替开发编写大段业务实现。
- `GEMINI.md` 的报告真实性要求继续适用：只粘贴实际输出，未验证就明示，偏差包括对已有测试的改动。历史上的报告错误已有更正，后续评审仍须独立验证。

## 3. 已接受的技术决策

- .NET 10、WPF、WPF-UI；五个生产项目分层：Core、Providers、Infrastructure、Platform.Windows、App。依赖方向与包白名单由架构测试守护。
- 契约使用 **C# 的 `specs/CONTRACTS.cs`**。Claude 在首轮解释了采用 C# 而非最初格式中的 TS/Python，后续任务均以此落地；不需要重新生成第二套契约。
- 统一动作入口 `IActionDispatcher`，业务流程在 Core；Provider 对应用户 Profile，Registry 管理实例，Orchestrator 编排多服务事件流。
- OpenAI 兼容协议使用 HttpClient/SSE；Anthropic 使用官方 SDK，当前锁定 `12.50.0`，凭据/地址须显式隔离 SDK 的环境变量默认值，关闭 SDK 重试。
- 设置与 DPAPI 密钥分离；历史使用 SQLite/Dapper。已冻结规范、数据库 Schema 与设置示例继续有效。
- 当前 NFR 内存指标为 Private Bytes ≤ 100 MB、Working Set ≤ 160 MB；早期 BACKLOG/历史工单中的 120 MB 是旧值。启动 ≤ 1.5 秒需在 CPU 负载低于 30% 时评估。以 ARCH §1.2 为准，不凭估计重新放宽。

## 4. 当前进度与本次推进

| 范围 | 已确认状态 |
|---|---|
| M0 / T-001–T-005 | 已经原架构师验收，含 T-003-R1、T-005-R1 |
| M1 / T-006–T-009 | 已验收：语言、文本/Prompt、HTTP 基础设施、OpenAI 兼容 Provider |
| M1 / T-010 | R2 已提交为 81c0ddd；架构师逐个确认提交中的四个文件与原验收指纹一致 |
| M1 / T-010A/B | T-010A/R1（587484a）、T-010B（21f6224）均已通过，CCR-001/002 结案 |
| M1 / T-011–T-013 | T-011/T-012/T-034/T-013-R1（99787b8）全部通过，CCR-003–006 结案，M1 完成 |
| M2 / T-014–T-017 | T-014/C14-1、T-021A/C21-1、T-022A-R1、T-018-R6、T-019A-R3（7e993b3）通过；CCR-011 结案，当前 T-019B 服务页接线 |
| M3–M7 | 设置界面、划词、截图/OCR、悬浮球、历史 UI 与发布仍待后续工单；历史仓储已在 T-034 完成 |

目前有托盘外壳和经过自动化测试的翻译 Provider，尚无可供用户输入并显示译文的完整 UI 流程。本次没有验证真实服务连通性、桌面操作或性能，不把自动化通过描述成产品端到端可用。

接手首轮在未修改业务源码的基础上完成 Release 构建（零警告/零错误）和全量测试（726 成功、0 失败），并通过不联网的临时探针复现 T-010 的缺陷。详细证据追加在 `tasks/done/T-010.md`；当轮 ARCH 升为 v1.12，契约签名未变。

R1 复审更新：提交 `5fa53dc8509b3eff4856e72704cfadab75a08bf5` 独立构建零警告、759 个测试通过；补充探针确认无效 SSE 续期与缺失 error.type 的误分类仍存在，R2 只修原范围的剩余问题。独立架构审查见 `specs/ARCHITECTURE_AUDIT.md`，其中新问题不计入 R1/R2 的验收范围。

R2 复审通过：独立构建零警告、779 个测试通过，46 个定向用例连续五轮通过；216 组输入/864 次分片差分无偏差，有效/无数据心跳在普通与 beta 路径均正确。复审当时 HEAD 仍是 R1，结果不属于 5fa53dc；后续 R2 已独立提交为 81c0ddd 并核验。

后续规范已更新为 ARCH v1.13。CCR-001 已由架构师批准，ToErrorInfo 的单处行为修改由 T-010A 同步到 CONTRACTS.cs 与落地文件；签发时保持两处原定义一致，以便复现 R2 基线。

T-010A 评审更新：当前实现尚未提交，独立构建零警告、809 个测试通过。CCR-001 及异常链/重定向改动正确；URL 正文清理仍有实际假凭据回显，短凭据重复匹配有二次扫描，已签发 T-010A-R1。报告中的 R2 基线表错误，实际 R2 提交经独立哈希核验无误，二者必须区分。

T-010A-R1 最终复审：提交 `587484a4487188031d5d637141b4c430c9c18af6` 通过，独立构建零警告、824 个测试通过；同一泄漏与耗时探针确认修复，R2 基线表更正闭环，CCR-001 结案。

当前目标规范为 ARCH v1.14，T-010B 只处理设置快照。CCR-002 已批准四处契约注释变更，由工单同时同步到 CONTRACTS.cs 和两个 Core 落地文件；不改签名、默认值或 JSON schemaVersion。A03/A04 的联合提交与 Provider 生命周期设计仍留待 T-011 签发前完成。

T-010B 复审通过：提交 21f6224，独立构建零警告、840 个测试通过，设置服务独立探针通过；CCR-002 结案。当前目标规范升为 ARCH v1.15，A03/A04 已在 PROVIDER_REGISTRY.md 定稿，CCR-003 批准 Registry 快照接口与可空凭据引用字段，并已独立编译验证。T-011 只实现读取/注册表/租约/初始化；写入协调器与 OCR 注册表分别在 T-019/T-030 接入。C10B-1 是测试覆盖改进，不是新的生产返工。

## 5. 后续架构工作

1. T-018-R6 / 11d4ac2 已验收，CCR-010 结案。独立 Release 零警告/零错误，全量 1328 成功/1 隔离窗口站环境跳过，cursor 通过；局部失败重试由 Presenter 完成实际窗口/scope 清理，R5 退出反例保持通过。证据见 tasks/done/T-018-R6.md 末尾，两个既有临时探针无需从头重建。
2. T-019A-R3 / 7e993b3 已验收，CCR-011 结案。独立 Release 零警告/零错误，全量 1369 成功/1 隔离窗口站环境跳过，cursor 通过；模型发现/翻译/保存对照及 iris-t019a-r1-review 的捕获/隐私/字段/回执反例无回退。证据见 tasks/done/T-019A-R3.md 末尾。当前工单为 T-019B，正式规范 PROVIDER_SETTINGS_UI.md / ARCH v1.24；用户手动转交，Antigravity 控制及排障暂停，不自动提交/推送。
3. 剩余顺序为 T-019B → T-015 → T-016 → T-017。B 提供两页共享草稿、全窗口一次 Core 提交、凭据意图和异步测试、进程级回执（未完成+预留最多 32）及独立自启状态。保存前停止/观察草稿网络任务，所有新操作接入 T-018 生命周期。A 已通过仍不代表服务配置 UI 可用，B 后才结案总单。剩余键盘模拟/选区/抓屏仍在 T-021/T-022/T-025。
4. T-015 前须定稿 A07 的窗口操作身份、迟到结果拒绝及单卡 attempt；Action 防重入不替代会话取消和结果归属。M2 验收需从设置保存服务/凭据到输入翻译、取消/重试的真实组合根路径，不能声称手工编辑 DPAPI secrets 即可使用。未交付平台/UI/配置任务不提前算完成。
5. 原生剪贴板测试应在独立 helper 中以 MTA 入口先切换新建非交互窗口站/桌面，再初始化 COM/STA。早期 flags=0 文本探针可运行且交互序列号不变，但不证明独占；最终 CWF_CREATE_ONLY 创建返回 183，当前环境原生独占验证未完成。禁止复用已有站或退回用户剪贴板，不能把跳过写成通过；详见规范 §6。探针位于系统临时目录 iris-clipboard-design-probe，不进入产品。
6. 三份桌面草案的规则已并入正式 DESKTOP_CONTEXT.md，原稿保留官方来源与探针证据。TRANSLATION_SESSIONS_DRAFT.md 仍是 T-015 前待审方案，不由 T-022A 实施。独立协议变异项目位于临时目录 iris-t021a-r2-mutations，已恢复基线；不要把其中 .cs.txt 变异副本用于产品。
7. SETTINGS_EDITING_PREPARATION.md 的 T-018 部分和 T-018_WORK_ORDER_CANDIDATE.md 已转为正式规范并验收；原 DI 模型在临时目录 iris-t018-di-prep，不应再代替实际窗口验证。C21-1 最新变异目录 iris-t022a-c21-check 已恢复基线。
8. PROVIDER_EDITING_DRAFT.md 与 PROVIDER_FORM_VALIDATION_DRAFT.md 已转为正式 PROVIDER_EDITING.md / CCR-011；历史稿保留证据，签名以 CONTRACTS.cs 为准。回执是调用方持有的 opaque 对象，不含凭据/JSON/Provider，不重新发布旧计划；A 实现 Retry，B 接持有与展示策略。
9. 两种 Provider 空模型列表 guard 已在 a1c6570 改动并通过相关测试，R1 保留；Keep 坏引用不能变成有效 None，但 Existing Replace/Clear 使用新显式值，不能因旧引用坏就拒绝合法测试。既有准备探针 iris-t019-catalog-prep / iris-t019-contract-check 分别只证明旧门槛和声明编译，业务复审以 iris-t019a-review 为准。
10. T-019B 预设已定稿：2026-10-01 再次读取 DeepSeek 入门和 Claude 状态表，HTTP 200；DeepSeek 官方 BaseUrl 为 https://api.deepseek.com，claude-opus-5 仍 Active。CCR-012 只改 DeepSeek 新建 BaseUrl 与 SuggestedModel=null，架构师已同步两份声明，现有 Profile 不迁移。资料位于 iris-t019b-official-prep（deepseek_signoff / anthropic_signoff），其它地域/账户依据保留在已采用的准备稿。

架构文件沿用原协作方式由开发随返工提交一起带入 Git；本次接手不自动提交或推送。

## 6. 2026-09-29 对话恢复与最新复审

前一 Codex 对话 `01a0e6dd-8ed9-7902-abbe-9fea7fb08f07` 在用户交付 T-011-R1 后上下文压缩失败。本次已从本地 sessions 解析主记录及续接记录，确认失败期间没有新的评审结论，不从“done”目录或开发报告推定验收。当前环境没有可调用的聊天读取接口，因此使用本地记录；没有改写对话数据库。

恢复基线为 master 的 `7b019712129735caf926300a2f4dba4b872421de`，开始时工作区干净。按原分工完成独立复审，结论与原始证据追加到 `tasks/done/T-011.md`，签发 R2。隔离探针和变异副本均在系统临时目录，仓库业务源码与测试未修改，未执行真实服务请求、桌面/性能验证，未提交或推送。

后续用户要求直接控制 Antigravity，已成功接入其 Iris 对话。开发完成 R2 提交 `3be662dee93a585ffe4b3d1b91206d07e1320229` 后，Codex 直接读取交付并复审，发现报告日志文本与源码不一致，直接要求 Gemini 更正；开发提交纯文档更正 `0744d7b4264d35132a899070c8b392cfe8cf473b`，明确撤回推测补写的日志与不满足 CPU 条件的启动 NFR 结论。独立复核后 T-011 / CCR-003 结案。退出日志原文、冷启动 NFR、真实服务/桌面端到端未在本轮独立验证，不能混入通过结论。

Antigravity 当前项目为 Iris，对话标题 `Executing Software Development Task`，应用标识 `electron.app.Antigravity`；窗口句柄会变化，后续从工具返回的应用/窗口重新取得，不硬编码。发送消息须核对实际输入框和发送后的对话内容；用户操作或其它窗口遮挡时先刷新窗口状态。普通构建/测试仍由命令工具直接执行，不经 UI 操作终端。此处记录的是可用的直接协作方式，并未设置定时或常驻后台监控。
