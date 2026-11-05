# T-039C：GitHub 正式分发与匿名用户在线升级闭环

**2026-11-05 当前有效状态**：T-039C仍是唯一活跃工单。用户授权的0.2.6已仅推原标签；实际CI41663111425/attempt1失败，官方digest已与原ZIP匹配、51原件冻结且receipt已同步失败。固定0.2.7源码fc44a3a78c2729c25dfb91296c51602b843a94f9完整Release0警告/0错误、全量3759/3757成功/0失败/2既有跳过；721来源、18资产/97载荷及旧正式full独立匹配，唯一candidate1已通过，本地正常合并e4951aa。原UIA反例、初版027热键全量失败和修前源均保持，生产/契约/schema/依赖不变。新Sandbox2fc7c168…中用户已手工重装正式0.2.2并准备开关false、虚构服务和历史；97安装载荷、原App/PID7312、V3/PID8152、服务/PID7328、实际POST200和SQLite旧历史均独立通过。首次安装约10min已定位到.NET MSI签名策略检查四次约120s等待；487s运行时/约1s Iris部署的原日志保留，具体证书网络原因与后续NFR仍待处理。下一步完成正常审阅归档/字节索引核对和新鲜只读预检，再请求仅v0.2.7标签的新用户授权；0.2.7未授权/创建/推送。main/codex远端仍285df3a，正式前版仅0.2.2。C/父T-039、真实升级/退出/native顺序/一次正常重启/卸载和T-040/NFR未结案，M6延期。不推分支，不重写原失败标签/CI/候选，不在宿主安装Iris，不改.codex或读取私钥，不创建工程师聊天/子代理。完整接续见 [SESSION_STATUS_20261105.md](F:/Iris/tasks/T-039C.coordination/SESSION_STATUS_20261105.md)。

2026-10-25 · 架构师签发 · 基线 `7e8529170d53c9372c1ae4bd8dd4ce06952e51b3`（B 验收归档；产品交付 `535ec1693ee4a656524bb1b0403d68ab7ce8ccfd`）。

用户要求持续推进直至本轮开发完成；已指定公开 `https://github.com/Tmcity/Iris` 用于 GitHub Releases，部署密钥写权限此前已验证。2026-10-30 用户因工程师会话持续不可用，明确将剩余工作转交本主会话直接完成；此安排覆盖此前工程师协调/建测窗口归属，保留已交付与未提交成果，不再等待旧会话实施。**当前唯一活动工单 C，由本主会话继续实现、验证与验收；T-040 随 C/父 T-039 完成后推进，M6 延期安排保留。**

T-039B/CCR039实施已独立验收，结论 `tasks/done/T-039B.review/acceptance-review-4/FINAL_REVIEW.md`。最终HEAD独立Release零警告错误、全量3728/3726成功/0失败/2既有跳过，新候选15/15、实际来宾142项独立核对与图像通过；原始失败与缓存辅助边界保留。不返工B无关成果，也不把本单视为仅上传文件。

## 必须完成

### 1. 准备可审的正式发布材料与无凭据流程

- 先实际核对本地HEAD/index/工作树、匿名远端refs与公开Release状态。origin为`git@github.com:Tmcity/Iris.git`；最近匿名refs只有main `877491e1421197ed6394f53d7f4d6ef57ab22e5e`，未有tag。读取当前状态后再行动，不依赖旧结论。
- 保留本地完整历史及远端初始README历史。采用正常合并/分支整合处理无共同祖先和README冲突，不强推、不改写既有提交、不丢弃远端文件。不要reset/clean/rebase/amend。
- 默认新工作分支用`codex/`前缀；不要让遗留master/main命名导致漏推或覆盖。明确最终发布源commit、目标branch、两个版本/tag、资产集合、构建环境及来源绑定。
- 沿用固定PackageId Iris/channel win/RID win-x64、Velopack/vpk1.2.161、主App/UiaHost FDD、Maintenance SC单文件。首期应用full，delta只生成不得冒称应用通过；对外只提供正确外层`Iris-<version>-Setup.exe`。
- 完善现有Desktop release workflow与用户文档。使用GitHub提供的最小contents权限和公开匿名客户端，不在产品/脚本/日志/资产中嵌入部署私钥、PAT、ProviderKey或其它凭据。本机未检出gh，SSH部署密钥只能用于Git，不能把它当Release API token。可设计明确的版本tag触发Actions，由工作流token创建Release，或复用现有可用授权；不改模型/账户/安全设置，不自行索要或读取私钥。
- 真正生成两个递增版本的正式候选（可沿用0.2.0→0.2.1，前提是远端版本未占用；若已占用选唯一新版本并说明），发布资产须有实际full包/feed与长度/SHA、运行时要求、来源commit/源码树和安装文档。文件集合必须匹配实际公开资产，拒绝覆盖已有Release或重写tag。

**架构复核检查点**：完成上述本地材料、分支/远端历史处理方案、候选包/清单、CI与验证方案后，先创建可审本地提交并向架构师回报精确commit、拟推refs/tag/资产和必要差异，**等待架构师复核后才首次push/上传/发布/触发发布动作**。这是一单内的工程检查点，不需要用户重复确认已指定的发布目的地。此前的只读远端查询、fetch到明确refs、本地准备与验证可直接做。

### 2. 真实公开分发与生产用户路径

- 通过检查点后，按核准材料正常push整合后的历史和必要refs，实际运行GitHub Actions/发布流程，保存源commit、run/job/产物/Release链接、原始失败/修正后新轮及资产哈希。不能用YAML存在、workflow已启动或CLI exit0替代最终实际成功。
- 无登录/无GitHub凭据验证公开Release/API/feed/full与外层安装入口的实际下载和SHA。不得向来宾映射宿主配置/密钥；不得把部署密钥嵌入客户端或传给下载器。遵守DESKTOP_RELEASE的来源/重定向/预算/无身份策略。
- 在自有Windows Sandbox/等价干净来宾，**从正式公开Release下载**较旧版本并安装运行真实Iris，产生合成设置/秘密/历史/日志。发布较新版本后，通过**真实生产设置UI**检查→下载→Ready→用户确认安装并重启。允许自有UI自动化点击，但不能以专用GuestUpdateHost、fake源、替换UpdateRuntime/进程父路径或直接调用内部协调器替代这一闭环。
- 证明检查不下载、下载不安装，点击确认后完整退出与维护门禁顺序，真实native应用、结果版本、恰好一次重启、数据可读/保留、更新后卸载入口和最终正常退出；保留当前语言画面、实际进程身份/开始时间/退出码、输入/输出/清单指纹。旧B本地或受控结果不充作线上生产用户路径。

### 3. 默认运行时引导与失败闭合

2026-10-30直接接管后，本主会话已实际以source88cde3d的候选在无.NET10/无下载缓存的新Sandbox重現native Peer disconnected，外层code2、nativecode1，产品/桌面运行时未装入；失败保留于direct-default-installer-smoke-1。按RUNTIME_DOWNLOAD_FIX_SCOPE_20261030.md批准Maintenance内部安装前提准备：在门禁与应用排空后由系统HTTP从微软官方源下载桌面运行时，严格来源/预算及官方SHA512/Windows微软签名，完整校验后交标准Setup安装；不修改公共契约/schema、SDK和主App/UiaHost的FDD形式。需要离线失败/取消/缓存竞争回归和新无缓存来宾/正式公开源复验，旧候选保留。

B的新包来宾在当前网络中native完整下载报Peer disconnected，成功轮先在来宾curl校验官方安装器放入正常缓存，再由未修改Setup在无共享.NET10时安装。该边界**没有被记成默认下载故障已解决**。

- 本单必须再用正式公开安装入口验证默认缺失运行时引导；该轮不允许夹具预填Downloads缓存、手动预装.NET或修改证书/TLS校验来绕过。记录实际网络/运行时/用户权限与真实Setup链。
- 若默认路径仍失败，保留真实日志并区分环境传输故障与产品缺陷，直接向架构师回报可执行的修复/环境方案，不自行缩小父任务验收。可以在自有来宾做受控诊断；宿主代理/证书/安全设置不改。必要生产下载/引导改动先说明原因、范围、所有权和用户行为，经架构师在本单批准后实施；不未经评估升级SDK或把App改SC。
- 验证发现真正产品缺陷在本单闭合，保留原失败，受影响本地验证与新版本包/来源重新绑定。不能仅补文档把必需的用户链路降为未验证，再宣布父T-039完成。

### 2026-10-25 实际首轮CI失败后的定向诊断批准（同一C）

首次已核准远端写入完成：main和codex分支均为3bb52042184b4c4aef8ad75dfab9bf0f40c1f666；annotated v0.2.0解引用为dcdfdded311fa00671dfd9747bb2101cbf8964f3，tag必须保持。实际run40193113435失败于已建立隔离站的原生剪贴板用例，full为3728/3726成功/1失败/1跳过，打包和publish跳过。原证据、archive digest和51份原始文件已独立匹配，不能把此失败改记为error183环境跳过。

允许在本单最小扩展 `tests/Iris.Clipboard.IsolatedHelper/` 和对应 `tests/Iris.Platform.Windows.Tests/Clipboard/` 的夹具/协议诊断与修复，必要时调整该自有测试worker构建接线；不得删弱断言、过滤测试、把已运行失败改skip或只靠延长sleep。先冻结修前源码/实际程序集，**仅在自有干净Sandbox**运行真实未改产品的诊断（测试工具运行时可明确准备，但不得充作默认安装引导）；不要在宿主运行可能落入交互剪贴板的helper。记录真实TID、station/desktop/占用owner身份与Open/Close结果、取消接纳、work完成和所有线程/进程完整join；参与者须在同一明确自有隔离站/桌面。若实证为夹具问题只修夹具；如需改产品，先向架构师提交具体最小反例/改动方案再实施。

可按提出的版本方案本地准备 `0.2.1 (PreviousVersion=null)` → `0.2.2 (PreviousVersion=0.2.1)`，前提是匿名远端确认未占用，并保存旧release-plan/候选/原始失败。本地新source独立提交、适当定向/Release/full、两版唯一候选和来源绑定完成后，再回架构师复核精确新source/checkpoint/refs/tag。**本条只批准本地诊断与准备，不批准新push、tag创建/推送、workflow重跑/触发或Release写入**。之前v0.2.1的同旧source有条件批准因首版未发布而未满足；在新检查点前保持未执行，不覆写v0.2.0、不删除失败artifact/草稿、不自行重跑。父C/T-039和真实UI/默认无缓存引导要求保持完整，T-040未签，M6延期。

## 范围、验证与收尾

- 允许`.github/workflows/desktop-release.yml`、`build/pack.ps1`及发布辅助、用户安装/README/发布文档、Git发布分支/历史整合和本单证据。允许正式源接线所需的App内部Updates/启动退出/Maintenance相关最小修正与测试，但应先说明具体实证；超范围契约/默认/schema/依赖版本变化先交架构师，不自行扩张。
- 延续NETWORK_CONFIGURATION/LOCALIZATION/IPC和完整任务所有权规则。自动单元测试不真实联网/生产IPC/宿主用户设置或密钥/Run/选区/剪贴板；本单自有真实来宾允许访问公开GitHub和微软依赖并操作合成数据。真实Iris安装/更新/卸载只在自有隔离Windows，按ID结束自有实例，不操作其它实例，不泛杀dotnet。
- `.codex/`用户工具原样保留，不读改删、不提交；安装包/依赖/工具/转储只在artifacts。所有原始日志、失败、未测条件如实保存，源/二进制/实际公开资产逐项绑定，不改BACKLOG状态。编译测试与架构师串行，当前无架构师建测/来宾占用。
- 验证适配改动执行Release零警告/错误和全量测试；工作流中实际检查也要保留并核对。只跑适当检查，不为文档反复扩大测试。首期发布为未签名，真实标注；不能把SSH部署密钥当Windows签名证书。
- 完成本单生产路径与真实分发后，归档工单全文及报告到`tasks/done/T-039C.md`，文本/图片/指纹证据到`T-039C.evidence`，移除current_task并创建独立本地提交（远端更新仅按检查点核准范围，不覆盖已发布资产）。回报最终commit/远端refs/CI和Release链接、实际匿名与用户UI验证/运行时引导、原始构建测试汇总、全部失败及剩余限制。
- 由架构师独立验收C与父T-039，再签T-040；不自行结案父任务或提前NFR/悬浮球。此后仍由架构师直接读取和协调，不要求用户代传。

## 2026-11-01 用户手工确认与当前材料

用户要求需要实际操作的项目由其手工确认、验证后反馈。已准备MANUAL_ACCEPTANCE_20261101.md、PREPUBLICATION_REVIEW_20261101.md和仅共享本单input/output的手工Sandbox资料。发布源dfc68cc，Release0/0，全量3754/3752成功/0失败/2既有跳过，两版完整candidate-3及smoke13无缓存/97文件实证已完成；新push/tag/发布与生产UI验收按用户逐项反馈推进，尚未执行。不因本地通过关闭C/父T-039，T-040及M6延期范围保持。

## 2026-11-01 已授权0.2.1实际失败后的本地推进

用户明确确认仅第一版0.2.1。已正常推送main/codex分支至dcc9ccc，annotated v0.2.1对象b9f33fd9755684c889a948dafd79dfd53c15caf8→dfc68cc；原v0.2.0保持。实际run41289210712建测失败、打包/发布skipped，未有正式0.2.1安装资产。原artifact13076313916/ZIP503625字节/SHA256 9c1f5e441d542332d58e7c6694541c3eaedc3e1ba7f8de17006b7984153c2e45精确匹配官方digest，全量3754/3752成功/1失败/1跳过。原始字节与修前源码保留。

唯一失败在Core并发编排测试，夹具把同一已Disposable快照发给两次请求，10ms延迟无法保证第二次在首次释放前取得。按CI_V021_FAILURE_SCOPE_20261101.md最小修正该测试夹具：独立租约、线程安全计数、真实握手验证同时运行与分别释放，保留隔离断言并覆盖先完成再启动顺序；产品不变。Core实际983/983通过。继续固定新source、Release/full和唯一新候选，本地计划0.2.2首版(null)→0.2.3前版0.2.2；v0.2.1及原失败不重写/重跑，任何新tag/远端操作待新材料完整后用户再次确认。旧操作文档为0.2.1历史计划，正式用户安装尚未开始；C/父T-039/T-040与M6延期范围保持。

## 新0.2.2/0.2.3本地材料已齐备

新source fb9f46f，Release0/0/full3755/3753成功/0失败/2既有跳过，两版candidate4/candidate1与720项源码/资产匹配，旧v0.2.0/v0.2.1完整保留。更新手工清单MANUAL_ACCEPTANCE_V022_20261101.md和检查点PREPUBLICATION_REVIEW_V022_20261101.md取代旧待执行版本指令；新tag/远端写入仍待用户确认。新0.2.2正式源安装及之后实际UI升级由用户操作，不再自动启动来宾。原始建测/CI失败和新材料归档后提供精确commit，再请求仅第一新版本0.2.2确认，0.2.3单独保留。

## 正式0.2.2已发布，待用户第1/2项实际反馈

用户确认仅0.2.2，正常推送main/codex→285df3a，annotated v0.2.2对象a4cf0938b4e291005dfdb3fa4dfeabc646d6b6c6→fb9f46f；旧v0.2.0/v0.2.1保持。实际run41293681371/package与publish均成功，原validation及publication ZIP与官方digests匹配，原full3755/3755通过/0失败/0跳过；发布receipt为published-and-listed/Public=true。正式Release v0.2.2/id430484182，16个资产已匿名下载、原长度/SHA与source/tree/feed/full/外层prefix逐项通过。正式外层116767621字节/SHA256 ddae984ba4bcc65e5398ecd7a036afe0485966c5d61fe9b1603cc9bb487e7a0c，与本地候选SHA不混用。

已提供MANUAL_FIRST_INSTALL_V022_20261101.md及真实公开元数据给用户手工全新Sandbox安装、关闭启动检查并保存、合成设置/DPAPI/历史/日志和old-ready证据；实际用户安装未执行/未收到反馈，不记通过。0.2.3未创建/推送/授权，必须等旧版就绪和单独用户确认再发布。C/父T-039、生产UI升级/默认正式源引导/单次重启/保留数据/更新后卸载及T-040/NFR仍未结案，M6延期保留。

## 2026-11-02 用户Sandbox入口尚未成功

用户已说明双击wsb文件无窗口，之前没问题/继续不作为实际0.2.2安装通过。只读配置、路径、选定入口与wsb0.8.107/list-empty核对已完成；准备Open-Iris-Sandbox.cmd显式start/share/connect入口，PS5.1语法/目录/配置检查通过，实际启动和桌面可见由用户执行反馈。参考MANUAL_SANDBOX_LAUNCH_20261102.md与SANDBOX_MANUAL_LAUNCH_FIX_20261102.md；保留原配置/系统设置，0.2.3未授权/发布。以正式0.2.2为前版的新0.2.3候选public-base-candidate-2本地已完成，97载荷/18资产，旧0.2.2full SHA c86982…；实际用户UI链仍未完成，C/父T-039/T-040不结案，M6延期。

## 用户已确认Sandbox桌面可见

用户反馈出现了，实际list包含本单ID590f8848-f6b4-4bdb-a151-85c4a4f19305，start/两次share日志与原connection-requested状态保留于manual-sandbox-visible-20261102-1。窗口已由用户确认可见，不等于Iris已安装；下一段按MANUAL_SANDBOX_VISIBLE_NEXT_20261102.md先用户点击记录clean、下载正式0.2.2并安装、保存installed和反馈版本。0.2.3仍未授权/推送，合成Key/历史旧版就绪及完整生产UI升级仍欠，C/父T-039/T-040/M6状态保持。

## 2026-11-02 正式旧版安装及合成数据已独立核对

用户依次确认“安装成功，0.2.2”和“开关已保存、翻译成功、历史可打开”。同一 Sandbox590f8848… 的无.NET10/无缓存初态、正式安装器/完整包、依赖安装及最终97/97文件在 manual-public-v022-install-20261102-1 已核对；本轮在原PID6604（04:25:01.2204360Z开始）仍运行时读回 CheckForUpdates=false、一个启用的合成服务/凭据集引用、实际POST200与虚构Key匹配、history integrity_check=ok及原合成记录。秘密只记哈希，未收集内容。本轮冻结证据 manual-old-ready-v022-20261102-1；原早期models401、启动更新警告和所有旧失败均保留。

原进程记录保留；辅助cmd生命周期探针实测退出0且启动/退出事件可乱序送达，新增只读V2记录器保存原始时间和未解析事件供SDK日志关联，实际PID6956/Ready/原App基线6604已核对。辅助新增文件在原input投影未及时出现的两次 -196608 失败、原日志及精确源编码探针成功保留；同一本单input新增只读入口C:\IrisAcceptanceUpgrade，未增加宿主秘密共享。Save-Offer/Ready/Updated/Uninstalled-State.cmd及Stop-Manual-Evidence.cmd已准备；PS5.1共13份脚本语法通过，实际来宾old-ready补充采集exit0。

source仍fb9f46f247e2ed4f6b2ee849e67b4b7d9afbdb11/tree60422257ce3c47f3eb83b291b02a84d4c75add8a；public-base-candidate-2的720项源码、18资产/97载荷、包含正式0.2.2旧full均独立匹配。本轮无产品/测试/契约/schema/依赖改动，不重复全量建测；原源Release0/0、本地full3755/3753成功/2既有跳过及正式0.2.2 CI3755/3755成功/0跳过保留。新的0.2.3 CI仍未运行，候选SHA不冒充正式CI包SHA。

见PREPUBLICATION_REVIEW_V023_20261102.md与MANUAL_UPDATE_V023_20261102.md。匿名refs仍main/codex285df3a，三个原标签保持；匿名API200只列正式v0.2.2/16资产，v0.2.3未占用。此前用户只确认第一版0.2.2；第二版尚未授权/创建标签/发布，下一步单独确认仅创建并推送v0.2.3→fb9f46f，不扩大到分支推送。确认后才执行实际CI/公开资产读回，再交用户分段真实UI检查→下载→Ready→确认安装/单次重启/原设置Key历史保留及更新后卸载。C/父T-039/T-040/NFR未结案，M6延期。原Sandbox由用户保留用于后续，未停止或控制真实Iris。

## 2026-11-02 原0.2.3实际失败与新0.2.4完整本地准备

用户仅确认第二版0.2.3，已正常推送annotated b4b8be76…→fb9f46f；没有推分支或其他标签。实际CI41397874525建测失败、打包/publish skipped，唯一Core网络测试全局秘密读取计数预期8/实际12；原artifact13120282071、ZIP504414字节/SHA256 9a33ebe306fb94f929e99ec32cad0caa01f7fff765978a2806abb209f51cb61c精确匹配官方digest，51原件保留，不重跑、不改skip或覆写tag。独立控制其他键读取反例同样失败，计数/变更钩子改为仅当前代理键，新增真正并发读取回归，保留8次/4轮及其他键不被修改断言。

source3372f79的首轮本地full3756/3752成功/2失败/2既有跳过保持；两项UI测试分别过早断言异步自启重试状态，以及先完成写入再期待取消回调进入。自启改为观察状态通知；剪贴板先确认取消进入，再完成直接外部writer promise，并保留回调未退出时必须等待的断言。两个仍失败的异步writer中间改写原件同样保持。仅三个测试文件、发布计划和说明改动；产品src/契约/schema/依赖无差异。

最终发布source942b0ddeb62d9eb3e4f244574c0c093ab2942ffc/tree0146030d392a0dfef7e481e2e467c3fdd67bf8f6，Release0/0/full3756/3754成功/0失败/2既有error183跳过，Core984/App866/维护39、发布工具16通过。正式0.2.2以前版构建的release-0.2.4-public-base-candidate-1：18资产/97实际载荷、720源码blob/工作字节独立匹配，旧full c86982…保持；本地外层047d1acf…/full964621da…与未来CI字节分开。源分支已正常合并回本主分支，标签目标仍为具体发布source942b0dd，报告提交不冒充打包源。next-source-v024-20261102-1及原CI/本地失败原件完整归档。

最新用户Sandbox590f8848…只读07:03:17UTC仍为原正常0.2.2/PID6604、V2watcher6956 Ready，settings/secrets/history哈希与原old-ready一致，未有新版本包；用户实际准备已通过，产品UI仍由用户操作。PREPUBLICATION_REVIEW_V024_20261102.md与MANUAL_UPDATE_V024_20261102.md已完成。此前0.2.3授权不扩展到0.2.4或分支推送；新tag未授权/创建/推送。下一步精确新检查点用户确认，仅v0.2.4→942b0dd，实际CI/匿名资产完整校验后再逐段真实UI检查→下载→Ready→安装确认/完整退出/native顺序/单次重启/旧设置Key历史保留及更新后卸载。C/父T-039/T-040/NFR不结案，M6延期；原旧tag/候选/失败、user .codex及当前Sandbox保持。

## 2026-11-02 原0.2.4实际失败与新0.2.5完整准备

用户已确认仅0.2.4，annotated09ee251c…→942b0dd正常推送，没有推分支。CI41407242768建测失败、打包/publish skipped，无正式0.2.4资产；原artifact13124257001/ZIP505866字节/SHA6140c82bf59d2d373b1e9a31c4cfa551be0e04e0cf48e00411d9c5cb67cce685与官方digest完全匹配，51原件保留。原full3756/3753成功/2失败/1跳过，两个Core测试在约5秒失败；六秒延迟反例复现原场景27同步门5秒自行失效。最小夹具修正仅使测试控制线程独立、完整join，显式保持阻塞并在finally放行/观察全部任务；生产读与factory仍真线程池且被断言；0秒与6秒延迟场景、编辑锁/网络阶段释放/旧键删除与取消等到factory退出/一次provider释放断言保留，3/3针对性通过。原编译错误及全部受控失败不覆盖。

最终source4e9484a20b40338595ad34a4843e652436a1de6f/tree907154ddcce091c6c0dfa1058df01f8621d06203；Release0/0/full3757/3755成功/0失败/2既有error183跳过，Core985/App866/维护39、发布工具16通过。仅两个Core测试文件/一个测试驱动helper与release-plan/说明改动，src/公共契约/schema/依赖/测试选择不变。0.2.5唯一release-0.2.5-public-base-candidate-1以前版真实正式0.2.2构建，721来源blob和工作字节、18资产/97载荷及旧full c86982…全部独立通过；外层23184e57…/full33005d43…为本地实测，不冒充未来CI字节。源分支已正常本地合并，标签须固定发布source4e9484a而非整合/报告提交。

新PREPUBLICATION_REVIEW_V025_20261102.md与MANUAL_UPDATE_V025_20261102.md已完成，0.2.5未授权/创建/推送。当前main/codex285df3a/原五个tag保持，新的用户确认只请求v0.2.5→4e9484a，不扩展0.2.4授权或推分支。原用户Sandbox12:09UTC仍为同一0.2.2/PID6604、V2watcher6956 Ready及settings/secrets/history原哈希，真实合成Key/旧历史此前已证；UI仍用户手工操作。实际新CI/草稿读回/全部公开资产字节通过后才逐段检查→下载→Ready→确认安装/完整退出/native顺序/一次重启/原设置Key历史/更新后卸载。C/父T-039/T-040/NFR未结案、M6延期；全部旧失败/tag/候选及user .codex保持，不重开工程师或在宿主安装Iris。


## 0.2.5最终来源检查点

**0.2.5最终源校正（2026-11-02）**：原4e9484a/验证1/候选1保持，最后修正发布说明旧候选文字后的发布source为ee2d7118597d07b891066256349098877e65f794/tree9dff0cd9e8f7ef4da5ac1aa82d3b233c885a0883。完整验证2 Release0/0/full3757/3755成功/0失败/2既有error183跳过，candidate2打包与独立721来源/18资产/97载荷/正式0.2.2前版核对通过；本地外层a557738f…/fullc215ffe7…。新source已正常本地整合，PREPUBLICATION_REVIEW_V025已更新此检查点，待用户新确认仅v0.2.5→ee2d711，不推分支，不指向下面旧源或报告。旧用户Sandbox13:19UTC仍原0.2.2/PID6604、V2watcher6956 Ready及原三项数据哈希。C/父T-039/T-040/NFR未结案、M6延期；下面源/候选/过程均为完整历史。

最终本地SHA256：外层a557738f3cf56a594b9c49d0a5c2204248e450764dd36767f0dff9544eaa9581，full c215ffe7f7a64701a29f48b04c902583204450fc7ba18c1c58714d8e9449198f。PREPUBLICATION_REVIEW_V025确认动作仅v0.2.5→ee2d711，不扩展此前0.2.4授权；实际新CI/草稿读回/正式18资产通过后才交用户真实UI升级、单次重启、数据保留与卸载。原tag/失败/全部source/candidate以及user .codex保持。
