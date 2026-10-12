# T-039A 独立复审记录（进行中）

2026-10-10。基线 `6f14e81ebd298d83eda7330008310015efa1937b`。本文件目前记录开发期复核，不是最终验收或父 T-039 结案。

18:20 最新状态：开发已在保存转储后终止确认属于本轮的挂起测试进程；父 dotnet test 随后失败退出。`final-full-2.command.json` 记录进程返回 4294967295，原日志为退出码 -1、Unsupported marker type 0x46，并给出 3651 总计 / 1 失败 / 3648 成功 / 2 跳过。这是中止后的失败汇总，不能与基线完整 3653 项相混淆或称作全绿。架构师复查原父子进程均已不存在；开发转入受控复现。下方“尚未结束”属于此前诊断时点。

18:26 更新：开发 `ipc-early-before.log` 已真实运行而非编译失败。自有原生管道在客户端先关闭、服务器后 Accept 的固定顺序得到 IOException/HResult `800700E8`，普通 managed Disconnect 得到 InvalidOperationException/`80131509`。实际产品的会话拒绝早关闭与截断帧早关闭两个门闩场景均无法接收下一条命令且第一次停止报错；原始摘要 3 项全失败、退出码 2。当前生产未改，测试新增在补单允许目录。该记录是开发的确定性实证，独立探针仍未运行。

18:36 更新：开发在原轮明确确认已合并公开仓库和IPC补充，并读取 current_task 末26行。补充已实际接收，无需重发或代点；最新范围允许需要时单独追加 DisconnectNamedPipe 的 CsWin32 声明。

19:08 更新：候选生产仅在 IpcPipeFactory 的 Accept 对精确 ERROR_NO_DATA 复位同一原生句柄，并追加单项 CsWin32 声明；未改 NamedPipeIpcServer 清理实现。`ipc-early-after-1` 为 BOOL 断言的编译失败，`ipc-early-after-2` 实际6项通过；`ipc-regression` 为不匹配筛选造成0项/exit5，`ipc-regression-2` 系统集成77项通过，`final-release-3` 零警告错误。失败记录均保留，19:05开始的 final-full-3 尚待完成。

新的 NamedPipeIpcTests.Guard 夹具预审已追加 current_task，消息 `01a12575-3b33-7a43-a980-be9828074ec9` 仍以实际接收为准：WaitAsync取消后先遍历/处置Clients而body仍可能追加资源，部分裸I/O尚未使用Guard。需先取消/封口/释放门闩并完整观察body，再完成最终稳定清理，或证明等价所有权；这是源码预审，未称为独立执行失败。当前通过记录不代替该点的闭环。

19:26 独立生产复核：开发 final-full-3 已完成，原始3659/3657成功/0失败/2既有环境跳过、exit0。确认无其它活动构建/测试后，在自有临时目录分别重建同一独立探针、引用固定基线与候选程序集；两次构建均0警告错误。基线两种早断开均失败，候选两种均接纳下一条命令一次、保持原服务器/拒绝竞争者、首次完整Stop成功。详见 `ipc-early-close/README.md` 和 `ipc-independent-*` 原始日志/绑定。未重建生产项目，未运行真实Iris；该结果不关闭夹具收尾预审。

## 已取得的独立证据

- `github-public-access.json`：用户已改用公开的 `https://github.com/Tmcity/Iris`，匿名 HTTP 实际返回 200，repository_public 为 true。覆盖此前暂时私有安排；没有发布或 push。
- `github-release-state.json`：架构师匿名读取 Releases API 返回 200、当前页 0 条 release。仓库公开不等于安装包/更新 feed 已发布；这次是外部发布元数据只读检查，不是应用自动化测试。
- `sandbox-preflight/README.md`：自有离线 Windows 11 Sandbox 创建、查询、停止成功，未运行 Iris/安装器。正式安装、运行时引导、更新与卸载仍未验证。
- `audit-pre-delivery-1.json` 与可重跑脚本 `audit_pre_delivery.py`：独立只读核对现有 24 份固定 Velopack 源码及 14 个产物的长度/SHA；包 CRC、条目及 full 包内 helper/英文卫星与归档清单匹配。输入在本次核对期间未变化，生产差异名单为空。这些结果仅绑定记录的候选指纹，不替代最终提交复验或运行验证。

## 执行期 IPC 补单

用户已在开发会话选择先补签最小 IPC 诊断/修正后交付，接收记录 `01a12517-452c-7973-87b2-9b75e7e879de`。架构师已在 current_task 末尾正式授权 NamedPipeIpcServer/IpcPipeFactory 及直接相关 IPC 测试；补充消息 `01a12532-abd2-7c60-bd7b-0791835be3ae` 已排队。由用户点击引导，排队不等于实际接收。

`final-full.log` 的环境覆盖失败、`final-full-2.log` 的默认环境清理失败都保留。默认环境全量尚未正常结束，不能报告成功或根据一条已失败测试推断挂起原因。架构师在开发诊断期间不并发构建/执行测试；已有 read-only 产物核对不运行 SDK/应用。

架构师后续只读分析开发转储，挂起测试、已 fault 的服务器循环和取消状态见 `ipc-hang/README.md`；dump 指纹单独保存，未提交 dump。该新证据补充消息 `01a12541-f5b2-7dd1-8680-d0d621122e0e` 已排队，仍以实际接收为准。`ipc-baseline-source/binding.json` 的五份原件直接来自基线 git 对象，供确定性前后对照。

`ipc-baseline-reading/official-source.json` 记录了向当前加载的 .NET 10.0.12 模块产品版本提交获取源码的三次 HTTP 404。随后 `official-reference-source.json` 获取的 v10.0.0 仅为公开参考代码，**不是已证明与当前加载运行时一致的源码**，不能据此声称当前进程故障已确定性复现。下一步必须基于实际当前运行时和自有管道验证。

## 方案复核要点（待正式冻结）

已阅读候选 `recommendation.md` / `ProposedContracts.cs.txt` 及固定 1.2.161 SDK/native 源码。以下是正式实现需闭环的点，不是未执行实验的通过结论：

1. `GithubSource` 在未找到 releases/feed 时可能返回空集合，空集合不能直接翻译为“已是最新”；公开仓库仍需区分尚未发布、网络不可达与已验证当前版本。公开源目标已确定，草稿中的私有源待定表述须更新。
2. 原型 `PhaseManager.AcquireUpdateLock` 在真实锁获得后加门闩；它证明持锁阶段仍须等待，但没有证明真实跨进程锁竞争取消。原型 `OwnedOperation` 只覆盖所跑的取消/清理场景，不是可直接复制的完整生产生命周期；普通故障与取消回调失败也须持有并清理原资源。
3. 新的 Updates 命名客户端复用现有 NetworkSnapshotHandler/NetworkRoutingHandler/NetworkTransportManager，按实际目标捕获代理；GitHub 资产重定向须逐跳有界验证和完整响应清理，不向其它目标携带 Provider/仓库凭据，也不启用全局自动重定向。
4. 现有 AppBootstrapper 会记录 Host.StopAsync 异常后继续退出；仅检查 Main 的零退出码不足以证明更新交接的全部前置清理成功。候选收据、原所有者停止、Host/日志/Mutex 及交接窗口的再次启动必须用实际生产分支验证，不能靠提前启动更新器的等待期限代替。
   固定 native `update.rs` 明确不在入口取得包锁；`commands/apply.rs` 先 operation_wait，之后 `apply_windows_impl.rs` 在自提权检查之后才锁包目录、再 force_stop_package。该锁不能直接当作覆盖整个交接窗口的现成启动闸门。此项为源码结论，尚未运行更新器验证。
5. 安装 hook 之后原生 Setup 才写卸载项，updated hook 则在卸载项重写之后。外层安装/维护入口应覆盖安装不启动、注册修复失败、正在运行的实例、卸载取消和数据清理重试；快速 callback 不能负责交互。包装器自身须在缺少 .NET Desktop Runtime 的机器上运行，不能先依赖它才能启动真正的运行时安装。
6. 静默卸载默认保留数据；删除只在用户明确选择且实际卸载成功后执行，限制到固定标准目录并处理重解析点。便携版路径/更新规则须明确，不能把原 vpk Portable.zip 自动视为 Iris 便携发行。

T-039A 原始报告、IPC 修正、最终 Release/full 和独立运行复验仍待完成。T-039 安装/更新/卸载及干净虚拟环境验收保留全部原范围，T-040 未开始。
