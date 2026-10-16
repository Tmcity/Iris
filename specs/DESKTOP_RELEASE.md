# 桌面安装、更新与卸载

2026-10-13 · ARCH v1.57 · CCR-039 · T-039B/C。

## 1. 交付目标与分层

父T-039完整交付为：可重跑的`build/pack.ps1`、实际Windows x64安装包、缺少.NET10桌面运行时时的自动引导、用户可用的检查/下载/安装更新入口、卸载保留/删除数据选择，以及干净Windows环境验收。T-039A已在`6256a3fba611b2cd94fc8dfa32480584c16d7bfe`完成真实SDK实证与IPC修正；其原型不作为成品。

B完成全部生产实现、实际打包和隔离Windows安装/本地升级/卸载验证；C完成GitHub分发与匿名在线升级的最终闭环。B不得以“后续接线”留下无作用的按钮、假更新器或仅能运行Payload的包。父单在C完成前保持进行中。M6继续延期，OCR继续复用翻译原文框。

发布元数据：产品/PackageId为`Iris`，RID为`win-x64`，Velopack channel为`win`，正式仓库为`https://github.com/Tmcity/Iris`。主应用维持`Iris.App.exe`及框架依赖发布，版本由打包参数统一传入主应用、helper、包和元数据，不能不同步修改显示版本。

- Core只同步CCR-039批准的`GeneralSettingsPatch.CheckForUpdates`属性和对应增量编辑实现。原构造/解构、UiLanguage和全部既有设置保持。
- Providers新增`HttpClientNames.Updates = "iris.updates"`及专用命名客户端，复用现有网络快照/传输生命周期，不引用Velopack或App。
- App负责SDK适配、更新状态/会话、设置UI、启动分流和原Host退出接线；更新接口/收据保持App内部，不新增Core更新服务端口。
- 新增`Iris.Maintenance`：net10.0-windows10.0.19041.0、WinExe，无WPF/WinForms；允许引用Core、Platform.Windows及固定Velopack。发布为Windows x64 self-contained单文件，使用必要的原生交互边界。这使安装入口在尚无.NET10时可运行；**主Iris仍为框架依赖，必须真实引导安装Windows Desktop Runtime**。不引入NativeAOT/MSVC的新工具链要求。
- Platform可提供内部维护锁、标准路径/注册项、进程身份和原生UI等共享边界；允许对Maintenance及其测试增加必要内部友元。Platform仍仅引用Core，不能引用App/Maintenance/Velopack。
- App对Maintenance只能有`ReferenceOutputAssembly=false`的构建/部署依赖，与UiaHost一样不在主进程加载helper。更新架构测试的明确项目/包白名单，不以unrestricted放开新工程。

Velopack NuGet和vpk统一固定`1.2.161`，由本单在集中包清单/本地工具配置中加入；其余既有包不升级。可增加Maintenance测试工程及必要离线验证工具。

## 2. 用户行为与常规设置

常规设置页提供“启动时检查更新”复选框、当前版本、更新状态、手动检查、下载、取消和“安装并重启”入口。复用现有设置窗口/导航，不新增公开IrisAction，不要求用户阅读SDK实现细节。

`General.CheckForUpdates`已有默认true，本单不改默认值、JSON键或schema。新增字段与现有General/Provider/History/OCR/Network等一次联合提交：入口捕获、Base/Target冲突、空计划、未编辑字段合并、readonly、保存/取消/Reload/RetryApply均保持。纯开关保存无需ApplyPending，不隐式保存其它草稿或触发安装。

主实例成功完成原启动后，若为受支持的安装版且持久化开关开启，后台自动检查一次；不阻塞原启动完成计时。关闭开关停止尚在运行的自动检查并完整收尾，不取消用户主动发起的操作；重新开启不在本次运行制造额外自动检查。手动检查独立于自动开关。

检查不下载，下载不安装，Ready不自动重启。用户明确点击安装并重启才产生一次应用意图。状态至少区分：不支持的运行模式/未配置、尚无兼容Release、检查中、已是最新、有更新、下载/进度、可安装、取消中、清理待重试、失败、停止。空feed、404、离线、限流、坏清单均不能显示“已是最新”。

启动检查失败只作安全日志/非打断状态；发现新版本可用现有通知入口提示，不弹强制安装窗口。手动错误在当前窗口可读。关闭窗口撤销该窗口的显示订阅，按明确的操作所有权取消并等待其主动操作；退出应用必须排空所有操作。进度/迟到结果不能重新打开窗口或覆盖新会话状态。

开发目录和便携模式显示当前版本及明确的更新不可用说明，不启动自动网络检查，不把裸vpk Portable.zip发布为Iris便携成品。便携数据仍按现有portable.txt/data规则保留，本单不删除或迁移它。完整zh-CN/en-US资源、当前语言稳定性及明暗/小工作区布局规则延续LOCALIZATION.md。

## 3. 更新网络与可信候选

发布时生成无凭据的`release-source.json`，包含PackageId、channel、RID、仓库和版本等受限元数据；正式值固定上述公开仓库。它属于发布配置，不修改用户settings schema。无安装身份、缺失/非法元数据时安全不可用，不退回任意默认地址。

Updates客户端由Providers DI注册，使用NetworkSnapshotHandler → NetworkRoutingHandler → NetworkTransportManager；Timeout由操作拥有者控制。不能借用LLM/OCR的业务认证、请求修补、重试或会记录完整URL的日志器；不能自行创建默认HttpClient/SocketsHttpHandler绕开现有代理策略。每个实际请求目标按既有版本化快照路由，None/Custom/System、凭据隔离、旧响应持有和失败退休规则保持。

SDK Check/GetReleaseFeed、DownloadString/DownloadBytes没有调用方CT。每次逻辑操作创建捕获本次取消令牌的源/下载器适配，所有实际Send、Read、Write及下一次请求前检查取消；不能只WaitAsync后遗留实际SDK工作。请求仅用于公开GitHub release/feed/asset，不能访问Provider/模型/用户内容接口，也不发送Provider Key、仓库密钥、用户staging ID或硬件标识。代理凭据只走传输级认证。

仅HTTPS；正式初始目标限定api.github.com的本仓库release API和github.com本仓库release路径。资产下载允许有界手动重定向到GitHub正式资产主机（明确列举release-assets.githubusercontent.com、objects.githubusercontent.com等实际需要的精确主机）；最多5跳，禁止降级、userinfo和任意跨源认证转发，逐跳重新校验目的地/路由、清理旧响应。不得打开全局自动重定向；新增主机必须有真实官方依据。日志不得包含签名查询串、原始响应、底层异常消息或任何凭据。

以SDK负责版本/feed/full包操作，Iris负责准入、资源和错误边界。元数据有明确字节/深度/条目预算，发布说明只作有界纯文本。候选必须匹配PackageId/channel/RID、合法且较新的版本、Full类型、单一basename文件名、合理正大小及完整SHA256；防止路径穿越、重复/冲突候选和其它产品包。检查成功只有存在可验证的本产品版本集合时才能判定最新。

首期实际下载/应用使用完整包，显式禁用SDK内部delta应用（MaximumDeltasBeforeFallback=-1）；仍可生成delta产物，但不得声称其应用已验收。需要delta进程支持另行加实际取消/退出证据，不让SDK同步启动patch子进程脱离所有权。

SDK对已有目标文件直接返回，因此下载前后和准备应用前均核对实际长度、SHA256、包身份/manifest；缓存文件名或一次SDK成功不等于Ready。使用流式有界读取，避免把整个安装包读入UI线程/无界内存。取消后即使旧SDK形成完整包，也不得发布旧Ready/进度。offer/operation有独立身份，安装按钮只能消费当前有效候选，不能由调用方构造任意路径或重用旧收据。

## 4. 操作、取消与关闭

一个更新协调器明确拥有实际任务、源/下载器、响应/流、文件、校验、SDK锁和清理。限制同一实例中的检查/下载/应用竞争，取消的同步接受点先使结果失效，再请求取消并等待真实工作。同步取消回调异常、普通故障、I/O/清理故障不能丢掉资源；失败保留原对象供重试，只重试清理，不重发检查/下载/安装。

SDK的锁、校验和finally清理没有完整CT，仍须join。对真正的锁竞争、非合作传输、迟到进度、校验/清理阶段及取消回调失败作受控验证；A持锁后的门闩不是跨进程争锁证明。文件清理限定本次/已识别缓存，不碰用户数据或其它安装。

AppBootstrapper停止接受更新入口和回调，先关闭所有窗口/操作的准入，完整排空更新网络和资源后再停止NetworkTransportManager、Host与UI。重复Stop共享进行中的责任，失败原实例可重试；保留现有Dispatcher/截图/OCR/IPC/设置的退出顺序，不让更新器截断它们。

## 5. Main、维护互斥与应用交接

同步STA Main及原单实例Mutex同线程释放保持。启动计时从Main最初开始，不能把新增步骤从原日志中剔除。

1. 纯参数识别fast-hook，限定实际SDK支持的首参数和严格版本/参数个数；缺值/非法hook不能落入普通Run。不要扫描翻译文本判断hook。SDK所支持的legacy fast hook只作短无业务分流，不引入新公开CLI用法。
2. fast分支不创建正常实例Mutex、Iris用户数据/日志/Host/WPF，不显示UI；必要短维护动作后退出。SDK Process.Exit不会运行finally，之前不得取得正常运行资源。
3. 普通CLI先走既有解析；help/非法仍无配置/SDK副作用。主/次实例再按身份/Mutex区分，第二实例完全不调用SDK、不读清理包目录或初始化设置/日志/Host。主实例才执行SetAutoApplyOnStartup(false)的正常bootstrap；显式传空普通SDK参数，业务原文不传给SDK/Host。FirstRun/Restarted只记录标志，通知在设置和固定语言确定后进行。
4. 保留实际vpk兼容性检查；1.2.161的CompatUtil扫描IL，分流helper内发现Run时可能给位置建议。不能为消除该建议把SDK无条件移到入口或跳过检查，需记录实际结果。

维护必须有跨进程、同用户的互斥与启动封口，覆盖安装/升级/卸载整个事务和交接窗口。新Main在正常数据初始化前检查维护状态，并在取得/未取得单实例锁后复核，避免开始维护时漏入新实例；此检查不得使第二实例读取用户配置或普通SDK缓存。不能仅依赖“.velopack_lock”：实际native apply先等待父进程，之后才取得包锁。跨会话不能误发命令或强杀其它会话；无法确认全部相关实例已停止就报告忙/可重试。

使用Maintenance作为真实安装/应用/卸载的外部拥有者。更新准备阶段可以启动并握手这个helper，但**不得提前启动Update.exe或它的60秒等待**。主程序产生不可伪造为任意路径的内部候选/交接责任，明确从Host服务转移到Main/维护拥有者，保有完成或撤销所需资源。

只有原应用所有者、Host、日志和Mutex清理确实成功后才发出一次提交信号。现有Host.Stop异常被记录后继续退出的行为不能当作成功证明，需有真实完成结果。helper同时观察提交信号及原进程真实退出；缺信号、取消、清理失败、异常退出或等待超时均不应用更新。Main失败时撤销并join仍属本次的helper，不能留下隐藏等待进程。允许仅在Main无WPF消息循环的最终边界，用明确的同步桥完整等待无UI依赖的撤销清理；不得扩散到业务/UI阻塞等待。

helper在安装根外执行，持有维护门禁，再调用实际Update.exe/SDK支持的应用入口。父进程已经退出后再启动native应用，不把native的超时后Continuing当作安全策略。由helper观察实际子进程完成，关闭SDK隐式重启，验证版本/安装结果后释放门禁并启动新版本一次。结果不明、部分安装或维护入口修复失败须保留可恢复状态并明确提示，不能重跑已完成步骤或无限重启。维护期间helper异常退出也不能让新主实例闯入尚在进行的文件替换；可用持久事务记录/继承的门禁等可靠方式，须覆盖恢复而不是留永久死锁。

所有维护参数、路径、进程身份和候选都由本产品标准安装元数据交叉校验，不接收任意待删除目录。控制通道只限本机同用户/同会话与本次操作身份，不携带用户文本或仓库凭据。需要自有IPC/事件/锁时命名、ACL、失败清理和原对象责任均可测。

## 6. 安装包与卸载入口

`build/pack.ps1`兼容Windows PowerShell5.1，版本为显式参数，检查工具/库一致、产物完整性与退出码。仅使用自有唯一staging/output，不递归清空任意用户路径，不改全局工具/系统配置。

独立publish App（FDD）、UiaHost（FDD）和Maintenance（self-contained单文件），组合干净staging。修正App现有CopyUiaHost输出/publish目标，不能递归复制整棵bin/TFM。只在`uia/`放正确的helper及完整deps/runtimeconfig/依赖/en-US资源，移除误入根部或uia/win-x64的副本；保留主应用全部必要依赖/卫星。通过清单和真实进程启动验证，不只判断exe存在。

真实vpk使用 `--runtime win10.0.19041-x64 --framework net10.0-x64-desktop`。最低API版本不冒充微软OS支持承诺；文档明确实际验证的Windows版本和运行时要求。应用为每用户安装；缺少共享桌面运行时时，标准运行时安装程序可能需要系统提升，须如实验证/提示，不能声称依赖也一定无需管理员权限。

正式对外是单一可运行的Iris安装入口，原生vpk Setup作为内部载荷。采用先生成不含Setup的Maintenance并放入应用包，再将vpk Setup装入外层安装入口的无循环构建流程；可使用有长度/SHA/边界验证的单文件附加载荷等实现。不能要求用户手工执行多个脚本或先装.NET才能启动外层。外层在自有临时目录启动真实Setup并完整等待，覆盖不自动启动的安装模式；保留交互取消/安装失败/运行时拒绝与重启需求。

Setup成功且实际manifest/路径正确后，外层才配置本产品HKCU卸载项，使UninstallString/QuietUninstallString指向Maintenance，并校验写入结果；失败显示安装收尾待修复，不冒称成功。安装fast hook早于原生卸载项写入，不能用它作最终覆盖。更新hook在原生重写之后作短无UI修复，维护helper在更新结束后再次核对实际注册项。不能依赖普通首次启动完成修复。

卸载流程：

1. 注册入口启动安装根之外的自有维护副本，确认原入口退场，防止被native force_stop_package杀掉。验证固定安装身份，不允许任意路径删除。
2. 普通卸载在任何破坏动作之前提供保留（默认）/删除/取消；取消立即完整收尾，尚未启动Update.exe。静默卸载保留数据，无UI；本期不开放静默删除用户数据。
3. 封闭新启动，使用既有同会话IPC Exit/实际进程观察让应用优雅退出。不能为发送Exit而启动一个新的正常Iris实例，也不把accepted当作进程已退出。无法排空、其它会话仍运行或无法确认身份时允许重试/取消，不强杀绕过。
4. 真实卸载器移除程序/快捷方式/卸载项；维护入口完整等待并验证结果。清理本产品关联的自启动Run值，但仅当其仍指向本安装，不能删除被其它安装或用户改成的值。用户数据保留时仍移除系统自启注册。
5. 只有卸载成功且用户选择删除时，删除标准`%APPDATA%/Iris`数据，包括设置、秘密存储、历史与日志。检查规范路径/重解析点与其它相关实例；拒绝越界，不跟随junction/symlink删除目标。删除失败保留独立、可实际执行的重试入口和已卸载事实，不能再次卸载或静默宣告清空。

安装中断/重复运行、更新后再卸载、数据保留后重装均必须可恢复。暂存辅助进程/文件需有限、可追踪清理；不删共享.NET运行时。

## 7. 验证和发布边界

自动测试不真实联网、不访问宿主Iris用户目录/生产管道/Run/剪贴板/选区。用真实产品类型、当前公开API与窄外部边界，覆盖联合设置、窗口状态、HTTP路由/转向/预算、缓存准入、取消所有阶段、失败清理重试、原程序启动分流/退出与维护互斥；不要把自写替代实现当产品。

在自有干净Windows11 Sandbox或等价隔离VM执行真实产物验证。已有wsb预检资料在T-039A.review/sandbox-preflight；只共享本单安装器/合成数据和输出目录，安装包只读映射，禁止映射宿主用户配置/密钥。允许为这项真实环境验收访问微软官方运行时下载和指定公开发布源；这不是放开单元测试联网。明确记录来宾用户/权限/OS、安装前没有.NET10桌面运行时、安装后真实共享运行时及应用/UiA helper启动。

必须覆盖外层安装在无.NET10时运行、取消/失败、安装不启动、卸载项、中文/英文卸载三选择、静默保留、运行中正常退出、更新后的注册项、坏包与清理失败、数据保留/删除和重装。用同一实际Iris产物完成本地两版本文件应用/重启/数据保留验证，不能只运行A的Payload。可用受控更新传输/专用验证宿主，但须记录与真实用户UI联网路径的差别，C必须用正式公开GitHub源完成该路径。

B生成实际包、清单/SHA、源绑定、复跑命令、离屏UI图及真实VM截图/日志，准备GitHub Actions构建/Release工作流和用户安装文档；不执行push/上传/公开发布。C在具体材料就绪后处理现有远程初始README与本地历史、发布权限及线上验证，不能强推或遗漏父单验收。正式包未签名时如实标注，不把部署SSH密钥当Windows签名。

交付前串行Release零警告和完整全量测试，保留所有失败/环境跳过。架构师独立验收后更新BACKLOG；原始证据按字节保存，不提交工具/安装包/转储/私钥/.codex。
