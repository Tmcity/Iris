# T-039B-R4 工程交付与复审续接

2026-10-25。同一R4实现及必需来宾已完成，等待架构师独立复审；不自行宣布T-039B/父任务验收，C/T-040未启动，M6延期。以独立本地提交后的实际HEAD为准，原唯一基线为05452e0a85a36982ef8bfd22d5a981e581eeafcf。无新产品修改或重复建测。

当前候选仍为artifacts/t039b/r4-tested-candidate-3，702项源、321项冻结文件；规范源码树d7ca8024ba8a7feea9fa75c61d7785b1edd77b5a。latest final-8两版、97项实际full载荷、feed/安装入口与受测源码绑定均由delivery-inspection-3.json重新按字节核对。Delta只生成，未应用。final-4/失败final-5/final-6/final-7、候选1/2、原失败和工单旧快照均保留。

最终工程师本地结果原文不重跑：release-build-4.log为0警告/0错误；full-tests-3.log为3728总计/3726成功/0失败/2既有Clipboard窗口站跳过；fixed-production-after-3.log为9/9。本地化1197条源/1230编译资源格式匹配、catalog审计exit0，原457对值不动、只加批准5对，例外SHA不变。架构师当前candidate-3独立15/15、1258项字节检查、语言审计与8图按SHA承接的原始范围均在报告/绑定单列；不扩大为真实安装或整单验收。

用户启用Sandbox后已实际核对InstallState=1、入口存在、空实例列表；不再等待环境确认。实际新来宾：

- tasks\done\T-039B.evidence\r4\guest-install-r4-6: 34观察、15布尔、false=0、Failure=None，来宾命令/stop=0/0。
- tasks\done\T-039B.evidence\r4\guest-update-r4-1: 37观察、22布尔、false=0、Failure=None，来宾命令/stop=0/0。

两场景均独立干净Windows、安装前无共享.NET10。默认native完整运行时下载Peer disconnected原失败未关闭；使用架构师明确授权的缓存条件：来宾内curl下载官方元数据/运行时并匹配SHA512、Authenticode Valid，只放入正常Downloads缓存，未预装.NET，由未改final-8 Setup实际安装运行时。此执行方式只证明缓存引导，后续C仍须核对默认真实下载。未改宿主网络/代理/证书/TLS或在宿主安装Iris。

安装/UiaHost/真实App、运行中静默保留卸载、注册/事务/数据/共享运行时及Iris拥有进程终态通过。升级使用0.2.1 full包，真实旧App IPC优雅退出、受控父进程退出屏障、native exit0、单次重启、0.2.1数据可读、0 work目录和无Iris剩余进程通过。升级另持有5个原安装链进程句柄直到真实exit0（runtime-handles-terminal-1）。最终原始快照仍有Windows Installer服务msiexec /V；不宣称系统服务退出，不泛杀它。两个自有Sandbox均stop exit0、随后列表为空，没有仍在运行的本单安装器/来宾。

两张当前截图已逐图实际查看：英文General设置页分别显示0.2.0/0.2.1，文字及Save/Cancel可读；完整指纹/视口限制在reviewed-guests-r4-1.json。架构师guests-independent-final8-1为142/142读取通过；该自动JSON的visuallyReviewed=false保持原样，随后本会话两图人工观察确认另存architect-final8-guest-review-supplement-20261025.md，不以此替代最终HEAD验收。所有失败及诊断按guest-attempt-ledger-1分类，修前输入/脚本指纹和差异在各intent/input-scripts及guest-fixture-changes-1；before-guest-validation-1及before-final-review-sync-1保留两次同步前的报告/绑定/续跑原文。

最新活动工单原文存signed-order-r4-sandbox-enabled-20261025.md；按archive_completed_order.py核对同字节且全部必需来宾完成后才移除current_task，实际移除收据为active-order-archive-receipt-1.json。交付绑定排除自身、验证输出与GitTree之外的所有文档/证据，不自引用；最终提交后用source_binding.py verify-head完整commit复算同一源范围。报告、绑定与本地提交只请求复审。默认公网下载、delta应用、标准用户拒绝提升、系统重启、跨用户、物理便携、GitHub线上用户路径及最终NFR按原披露仍未验证。

两份tasks来宾输出中的运行时exe已先与原artifact输出、artifacts/t039b/r4-runtime-cache-preserved-1稳定副本三方核对大小/SHA256/SHA512，再只清除tasks重复件；runtime-binary-relocation-1.json保留来源路径及字节记录。原日志/结果/图片未改写，安装器/依赖/包/转储只留artifacts，不入Git。需要复算旧运行时字节的读者应按该映射读取artifact原件，不能因tasks重复件不再存在而臆测下载未发生。

BACKLOG及架构师所有未提交规范/复审/协调工作区改动原样保留且不暂存，不作为开发改动。.codex不读改删、不提交。工程提交仅按明确源/工程师报告及证据路径选择文件，不批量add全工作区。无push/reset/clean/rebase/amend，无下一单。
