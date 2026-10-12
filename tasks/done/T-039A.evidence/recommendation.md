# T-039 后续实现建议（待架构师冻结）

本文件是 A 的结论与建议，不是已批准契约或已完成产品。用户最新决定已将 Tmcity/Iris 公开，正式目标为 `https://github.com/Tmcity/Iris` 的匿名 GitHub Releases；架构师匿名验证见 review/github-public-access.json。此前私有讨论及合成 404 反例作为历史和错误处理验证保留，不再作为发布目标未定的理由。客户端不内置仓库凭据。建议构建生成可替换的 `release-source.json`（PackageId、Channel、RuntimeId、GitHubRepository），正式值指向该仓库；缺失配置、尚无兼容 Release、不可达、确实最新分别显示。该元数据无密钥、不改变用户 settings schema；A 不实际发布。

## 发布与运行时

SDK/vpk 固定同一版本 1.2.161。Iris 主应用继续 win-x64、框架依赖发布；实际 runtimeconfig 需要 Microsoft.NETCore.App 与 Microsoft.WindowsDesktop.App 10.0.0。实际 `vpk pack --framework net10.0-x64-desktop --runtime win10.0.19041-x64` 被工具接受，包内规范化为 `net10-x64-desktop`、`osMinVersion=10.0.19041`。不使用 self-contained Iris 绕过缺失运行时的安装验证。

当前 CopyUiaHostToPublish 从整个 bin/TFM 子树复制，RID 发布实际落在 `uia/win-x64/`；运行时需要 `uia/Iris.UiaHost.exe`。发布根还带入了额外 UiaHost 文件。后续正式打包脚本应使用独立、干净 staging：分别 publish App 与 UiaHost；复制 App 发布文件时排除旧 uia 树及根部 Iris.UiaHost.*，只把 UiaHost 的独立 publish 目录放入 staging/uia。对 exe/deps/runtimeconfig/所有依赖及 en-US 卫星作文件清单门禁，禁止递归复制整个 bin。A 已验证独立 publish+组合以及真实包内这些文件的 SHA；没有修改实际 csproj 目标。

Runtime bootstrap 的实际原生路径见 windows/runtimes.rs：检查对应 Program Files/dotnet/shared/Microsoft.WindowsDesktop.App 的同 major/minor 且不低于要求的版本；缺失时读微软 WindowsDesktop/{major.minor}/latest.version，再下载 WindowsDesktop/{version}/windowsdesktop-runtime-{version}-win-x64.exe，并运行依赖安装程序。安装权限、离线、拒绝、重启要求都必须在 VM 验证。RID 的最低 OS 元数据不能等同 .NET 官方支持承诺：本次保存的 .NET 10 支持矩阵还受 Windows 版本/版本类型/生命周期约束，最终支持声明须取 Iris API 最低要求、运行时支持政策和实际 VM 验证的交集。

本地生成的 Setup/Portable 均未签名，实际 vpk 警告保留。仓库部署密钥与 Windows 代码签名是不同事项；A 未读取或处理任何密钥。后续正式发行前确定签名/未签名策略，不把本地未签名包写成可发布验收。

## Main 精确顺序与退出责任

1. Main 仍保持 STA。首先纯参数识别正式 fast-hook 白名单（install/updated/obsolete/uninstall）及严格版本/参数个数；普通 `--translate` 的文本不作为 hook。伪造/缺失版本的 hook 不得落入普通 SDK Run 后继续启动。SDK 在“缺少版本”时会进入普通路径，A 已实测。
2. 真正 fast hook 仅进入轻量 SDK 分支，`SetAutoApplyOnStartup(false)`，不创建 Iris 路径、日志、Host/WPF、普通实例 Mutex 或用户交互；做必要的短维护动作即退出。SDK hooks 是同步委托，在调用线程执行；本次自有进程实测为 STA。回调异常导致 SDK Process.Exit(-1)。真实 Environment.Exit 子进程实证表明外层 finally 不执行，所以绝不能在该分支预先取得正常实例资源。
3. 普通路径先运行原 CommandLineParser；help/非法参数仍零配置读取。再按原机制取得单实例 Mutex。第二实例只执行原有有界 IPC 转发与确认，完全不调用 VelopackApp.Run，不读取或清理更新包。
4. 仅主实例取得 Mutex 后才运行普通 SDK bootstrap，并显式关闭默认 auto-apply。普通 Run 即使关闭 auto-apply 仍读取/清理本地包，不能放在 Main 的无条件第一行。FirstRun/Restarted 回调只记录轻量标志；正常通知推迟到 AppBootstrapper 读取配置、选定固定语言之后。保持 Program 的语言作用域覆盖最终清理反馈。
5. 正常初始化 Host、加载设置和原 UI/IPC 生命周期不变。更新协调器按单操作所有权注册；退出先封闭新检查/下载/应用入口并撤销结果发布，再取消并 join 实际 SDK/源/文件工作。先清理更新 HTTP/流/回调，后停止共享 NetworkTransportManager；失败保留原对象以供原 Stop 重试，不能重建下载或只 await 一个 WaitAsync 包装任务。
6. 用户显式选择安装后，异步校验并产生 PreparedUpdate；仅保存不可变候选凭据，不自动安装/重启。用户取消、版本替换、来源变化、下载取消或清理失败都撤销该候选。保存设置的 ApplyPending 与更新下载状态分离。
7. 原 App 停止流程必须全部成功，包括 Host/服务、日志器以及原 Main 线程的 Mutex 释放。只有成功清理回执允许紧接着调用 `WaitExitThenApplyUpdates` 并自然返回 Main；禁止 ApplyUpdatesAndRestart/ApplyUpdatesAndExit 截断 finally。更新器启动失败返回当前固定语言的安全提示，不隐式重试安装。
8. 60 秒不是允许提前启动更新器的“清理预算”。SDK 仅发 --waitPid；原生 shared/util_common.rs 等待 60 秒出错后记录 Continuing，继续安装流程。A 只做源码核对与进程边界替身，不运行真实 Update.exe 或制造 60 秒杀进程试验。必须在 VM 覆盖长清理失败、末尾交接失败、交接窗口内再次启动 Iris；如需要维护期启动闸门，应在后续正式方案中冻结并验证，不能依赖该等待计时器代替所有权。

## 更新网络、校验与取消

建议新增独立命名客户端 `HttpClientNames.Updates = "iris.updates"`，由既有 Providers DI 注册，复用 NetworkSnapshotHandler → NetworkRoutingHandler → NetworkTransportManager。不复用 LLM/OCR 的认证、安全修补或业务重试管线，也不把更新器 SDK 依赖引入 Providers；SDK 适配留在 App，使用 IHttpClientFactory 取得该命名客户端。代理/凭据仍来自现有不可变 NetworkProxySnapshot，原传输代次和失败退休/资源收尾规则保持，不新建默认 SocketsHttpHandler 绕过代理规则。

实际 SDK Check/GetReleaseFeed 和 DownloadString/DownloadBytes 无调用方 CT。每个更新操作创建自己的 IUpdateSource/下载器适配实例，绑定本操作 token；每个新的请求、读、重定向、写入和结果发布前检查取消。所有 await 真正完成后才释放该操作资源。DownloadUpdatesAsync 虽有 token，但锁获取、校验及 finally 清理不整体可取消，必须 join；A 已在三个真实 SDK 阶段加窄门闩验证。同步清理门闩只存在于原型测试，不是生产实现建议。

不要仅信任 SDK 下载成功：已有目标文件时 SDK 会直接返回，不校验内容；原型实测损坏缓存仍返回成功，独立长度+SHA256 门禁拒绝其 Ready 状态。后续实现下载前验证候选 PackageId/渠道/RID/版本、文件名只允许 basename、大小上限、必须完整 SHA256、受限来源；下载后以及应用前重新校验实际包、nuspec 身份和文件指纹。下载取消后即使旧 SDK 操作仍形成完整包，也不能发布可安装状态。候选 receipt 不允许调用方填入任意路径或重新使用旧 OfferId。

默认不请求 Provider/模型/提示词接口；不发送 Provider Key、用户文本、代理凭据 HTTP 头、用户 staging ID 或硬件标识。代理凭据只用于传输级代理认证。允许的请求集合应来自批准的公开更新源，明确 HTTPS 主机/路径与手动重定向策略；跨主机重定向不转发认证。日志只记录安全状态/错误类别，不记录下载签名查询串、原始 SDK 异常或完整远端响应。发布说明只作受限纯文本展示。

启动自动检查仅在 CheckForUpdates=true、来源已配置且安装模式受支持时执行一次；手动检查可在自动开关关闭时执行。检查不下载，下载不安装，Ready 也不自动重启。未配置、无兼容发布、无访问权限、离线、最新版本是不同 UI 状态。A 用实际 GithubSource+假下载器验证合成 404；它仍是必要负例，不表示最新公开目标现在不可访问。A 的更新原型未对 Tmcity/Iris 发送 HTTP；公开访问是架构师的独立证据。

增量包已真实生成，但 SDK 的增量应用会直接启动补丁进程并同步等待，取消和进程回收需要另行严证。建议首个正式下载路径显式 `MaximumDeltasBeforeFallback = -1`，保留真实 full 更新；不可把本次生成 delta 当作“增量应用已验收”。若父任务需启用增量安装，则增加真实 patch 子进程取消/停止测试后再开放。

## 卸载保留/删除数据：外层入口，而非 fast hook UI

官方卸载文档明确 fast callback 不允许 UI 且不能取消卸载。固定原生 uninstall.rs 先 force_stop_package，再 run_hook(...,60)，无论 hook 返回均继续卸载。C# 注释写 30 秒，原生此调用点传 60；两者如实保留，设计不依赖更长数值。不能在 hook 弹窗等待用户，也不能用它替代 Iris 正常退出。

安装与更新的真实顺序不同：install.rs 先 install hook，再 write_uninstall_entry，最后仅在非 silent 模式启动 App。因此 install hook 写同一 UninstallString 会被覆盖，且仅首次启动修复无法覆盖安装不启动。apply_windows_impl.rs 则先 update_uninstall_entry，再 updated hook。

建议后续新增一个小型 Windows 原生维护/安装入口项目 `Iris.Maintenance`（原生 Win32 或 NativeAOT，实现选择需架构师批准），职责与主 Iris FDD 应用分开，不能以它代替 .NET Desktop Runtime 安装。正式发行对外提供该外层安装入口，原 vpk Setup 作为其内嵌/随附内部载荷，不单独冒称完整安装入口：

1. 外层入口运行真实 vpk Setup 并等待完成。为覆盖安装不启动，使用原生 silent 安装路径；外层负责必要的交互/安装结果展示。只有 Setup 成功且已安装 manifest/路径校验通过后，才写本产品 HKCU 卸载项的 UninstallString 与 QuietUninstallString 为维护入口。覆盖失败必须显示安装收尾失败及可重试状态，不能伪报完整成功；不依赖普通首次运行。
2. 后续更新 updated fast hook 在 SDK 重写卸载项之后作短、无 UI 的注册修复；失败保留明确诊断，并让正式更新验收捕获。安装 hook 不承担同一值的最终覆盖。修复/重装仍经过外层入口，测试原生 Setup 旁路会恢复 SDK 默认值的边界，不能把旁路发行给用户却承诺同样交互。
3. 卸载入口先复制/启动受验证的维护代码到安装根目录之外的自有临时位置，并确认原入口退出，避免 SDK force_stop_package 把维护 UI/清理进程一起结束。正式实现校验固定安装根、已安装 manifest 与可执行路径，不接受任意待删除目录。
4. 普通卸载先让用户选择保留（默认）/删除/取消。此时未启动 Update.exe，取消有效且不存在 hook 超时。静默卸载默认保留数据，不显示 UI；显式无人值守删除策略需单独批准，不因“silent”自动删除。
5. 确认后，通过原 IPC Exit 请求让同一安装的主实例按原流程排空，并等待真实进程和 Mutex 释放。枚举同用户其它会话的同安装实例时也不能交给原生 force-stop 解决；无法完成优雅退出就停在重试/取消，静默模式返回失败。不新增会强制关闭或丢失用户操作的 Core Action。
6. 只有进程退出后，调用真实 Update.exe 的卸载入口并等待其结果。其原生逻辑负责程序文件/快捷方式/产品卸载项；自有维护进程不位于被删除的根目录，继续观察完成。卸载成功且用户选择删除时，才删除该安装模式对应的、经过标准路径与重解析点检查的 Iris 数据目录。删除失败保留独立清理重试，不重新运行已完成卸载。
7. 既有标准数据路径是 `%APPDATA%/Iris`，由 AppPaths 源码确认，位于默认 `%LocalAppData%/{packId}` 安装根之外，因此 SDK 默认卸载不应删除它。该事实不等于实际卸载已验证。便携模式的 `portable.txt` 和 `data` 位于程序目录，必须单独处理：不将裸 vpk Portable.zip 当作 Iris 便携发行，不在本阶段承诺便携自动更新/卸载。最终方案须验证程序目录替换不丢失 data；若暂不支持便携更新，应显式禁用并保留手动迁移说明，由架构师冻结，不能静默沿用安装版路径。

上述安装包装器、卸载 UI、注册修复、真实安装/删除、跨会话关闭、运行时缺失/重启等均未在 A 运行。Sandbox 可用性是架构师的预检证据，不是这些功能已通过。父 T-039 必须补齐真实实现和 VM 验收才能结案。

## 契约与后续验收

精确建议在 ProposedContracts.cs.txt：GeneralSettingsPatch 只加可选 CheckForUpdates init 属性，保留原四参数构造/解构及 UiLanguage；新增命名客户端常量在已有 Providers 契约中，值与原常量分开；更新协调/状态/候选/交接端口仅 App internal。所有建议仍待 CCR，不修改当前 CONTRACTS 或包清单。

Core 计划同步复制、三方比较、空计划、未编辑合并纳入 CheckForUpdates；App 草稿/IsDirty/冻结/readonly/保存/取消/Reload 一并接入。已有 General.CheckForUpdates=true 默认和 schema 不变；关闭自动检查不能取消用户明确发起的手动操作，成功保存不额外制造 ApplyPending，也不保存原 RetryApply 之后新增草稿。

正式验收至少覆盖：无运行时 Win11 VM 的安装/拒绝/离线/非管理员路径；安装不启动与静默安装后的卸载项；已有安装双版本更新、失败保留旧版本；第二实例/非法参数零数据与零 SDK 普通路径；长清理失败和手动取消、来源变化/坏缓存/校验失败；明暗双语设置与更新状态；普通卸载三选择、静默保留、正在运行/其它会话、数据删除失败重试、更新后卸载项仍正确；便携数据路径和原生包旁路行为。所有真实外部网络与签名/发布步骤另按最终发布源授权执行。
