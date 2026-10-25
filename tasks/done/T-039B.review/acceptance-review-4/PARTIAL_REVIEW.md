# R4 独立复审阶段记录（未结案）

**最新补充（2026-10-25，覆盖下方 candidate-2 历史状态）**：继续收尾时独立完整本地化审计实际失败（5个目录缺项、6个未批准源码站点），同一 R4 已修正；架构师修后实际重跑 source/compiled/catalog 两阶段均 exit0，1197 条源记录、1230 项编译资源/格式匹配，无未批准站点/资源错误，旧457对目录值不变，仅新增批准5对，例外清单 SHA 不变。见 localization-before-1 / localization-after-1。

当前冻结候选为 **candidate-3 / final-8**，702 项源码树 `d7ca8024ba8a7feea9fa75c61d7785b1edd77b5a`。开发最新 Release 零警告/错误，全量3728/3726成功/0失败/2既有跳过；架构师已实际独立执行新候选15条交接/语言/生产拓扑回归，15成功/0失败/0跳过、exit0，9个受控worker结果安全，321项冻结输入保持。新包1258项独立只读核对全部匹配，见 candidate-check-3 与 independent-runtime-candidate3-binding.json。8张新图与此前独立查看的通过图片逐字节相同，并绑定新 DLL，视觉复核仅按 SHA 承接。

当前依然未进入真实来宾、未收到 Sandbox 宿主功能变更授权、未创建最终提交；最终提交的独立 Release/full 仍在必需来宾完成后。下方 candidate-2 的记录作为完整历史保留，不扩作当前候选的直接证明。

2026-10-25 · 架构师。HEAD 仍为 `05452e0a85a36982ef8bfd22d5a981e581eeafcf`，本单在工作树内，尚无最终交付提交。

当前受测源码树为 `c6fd6c81f2d6b553d6b8a30390794c2d6a00683d`（明确范围的 702 项源/配置）；固定候选 `artifacts/t039b/r4-tested-candidate-2`，实际包为两版 `final-7`。这份记录不宣称完整 R4/B/父任务通过。

## 已核对

- 源码预审 1 的普通设置准入被重新开放、恢复未绑定真实确认两项，已在同一 R4 修正。专用恢复窗口不创建普通设置 scope；一次性准备收据关联 owner/generation，真实 helper ready 后才向 lifetime 转移。
- 开发 Release 重建原文为 0 警告/错误；`full-tests-2.log` 为 3724 总计、3722 成功、0 失败、2 既有 Clipboard 窗口站跳过。此项是核对开发原文，不冒称架构师全量执行。
- **架构师实际独立执行**冻结候选的生产拓扑回归：9 总计、9 成功、0 失败、0 跳过，exit0。原文 `independent-runtime-candidate2-1.log`，xUnit 报告和绑定 `independent-runtime-candidate2-binding.json`。
- 独立运行覆盖真实 scoped/ValidateScopes/Dispatcher、更新与设置停止接线、真实产品协议 worker、窗口保留与已释放后故障、重复失败/成功、撤销清理失败、未确认/取消/新 offer 否定情况。9 个 worker 结果均记录 Failure=null、native/restart=0、门禁解除；这是受控外部边界和父进程尚活着时的屏障证据，**没有实际安装/应用更新**。321 个固定输入运行后字节保持。
- 独立真实渲染先发现恢复界面四个 Dark 组合的动作不可读，保留原图、原始命令及语言夹具修正历史；开发新增回归实际重现 4/8 失败后修正生产主题资源。架构师已逐张查看并校验修后 8 图，候选 App SHA `b43abf9473adb385f6c679bad51a903fddfcdbb5b63cafe3780729d0e1ccea44` 与图片加载记录一致；各语言/宽度明暗均可读。见 `source-feedback-2` 和 `recovery-ui/after-observation-1/reviewed.json`。修后图来自开发真实渲染，架构师独立查看，不冒称架构师重新运行了该修后渲染。
- `candidate-check-2.json` / 原始日志：**1258 项独立只读核对全部匹配**，包含 702 源文件的当前 SHA/Git blob、独立 Git 对象格式实现重算的源码树、321 冻结文件、两版各 97 实际 full 载荷、feed、顶层清单、外层安装入口与 Maintenance 前缀。顶层各 8/10 项、full feed 各 1/2 项。此项证明所核对字节与声明相符，不代替最终提交或来宾执行。
- final-5 的 PowerShell 5.1 清单失败、final-6 旧候选、所有原始失败/旧源/图像保留；新 final-7 使用真实系统 PowerShell 5.1。新增 worker 已明确加入 solution，最终 Release 日志使用 Release 输出。

## 仍须完成

1. 新包在自有干净 Windows 的安装 + 运行中静默保留卸载、本地两版本 full 更新。旧来宾不能证明 R4 最终包。
2. 实际来宾输入/产物/原始结果/进程终态和截图复核，随后更新完整交付绑定。
3. 完成后创建独立本地提交，以实际最终 HEAD 核对源树和允许范围；最终提交的独立 Release/full 尚未进行，依最终改动与要求执行，不拿本记录代替。
4. B 验收后才签 C，C 仍须公开 GitHub 源用户 UI 的真实匿名升级；T-040 的完整手动/NFR 仍在后续，M6 延期。

## 环境与协作

当前 Windows 26200 上 Sandbox Feature 为 Disabled（CIM InstallState=2），入口不存在；Hyper-V 服务存在，Get-VM 当前账户被拒绝。已就明确 /NoRestart 的宿主功能启用方案或现有 VM 入口向用户请求一次回复，**未收到授权、未运行宿主变更**；脚本仅在系统 PowerShell 5.1 进行了静态语法解析（0 错误）。

工程师只在用户指定会话执行；本次独立运行的串行窗口已明确归还。R4/current_task 保留，暂不最终提交，不 push，不开始 C/T-040。此环境缺口不被写成产品通过、范围缩减或整个项目完成。
