# T-039B / R4 最终独立验收

2026-10-25 · 架构师。**T-039B（含 R2/R3/R4）验收通过，CCR-039 的 B 实施部分结案。父 T-039 未完成，公开分发与正式匿名用户链路留 T-039C，T-040 尚未开始，M6 延期。**

主实现 `0bc11fc7bbd099eaf205717fadb1cfd7b4f87007`，交付最终 HEAD `535ec1693ee4a656524bb1b0403d68ab7ce8ccfd`。后者仅补收一份已绑定的中间补丁原字节，未改写旧提交。受测/打包源码树为 `d7ca8024ba8a7feea9fa75c61d7785b1edd77b5a`，702 项明确源码/构建范围；它不声称等于含证据的整个提交树。实际包：0.2.0 / 0.2.1 `final-8`，未签名。

## 对实际最终提交的独立验证

- `architect-final-r4-release-1.log` / command：`dotnet build Iris.slnx -c Release --no-incremental`，exit0，**0 警告 / 0 错误**，原始耗时 `00:00:37.69`。
- `architect-final-r4-full-1.log` / command：`dotnet test Iris.slnx -c Release --no-build`，exit0：

```text
  总计: 3728
  失败: 0
  成功: 3726
  已跳过: 2
  持续时间: 3m 18s 297ms
```

两个跳过均为既有 Clipboard 隔离窗口站 error=183；本次没有新增失败或跳过。构建、全量及此前受控验证串行执行；期间 HEAD 未变、生产/测试/构建文件无工作区差异，工程师已明确归还窗口。

## 工单闭合依据

| 要求 | 实际独立依据与结论 |
|---|---|
| 停止失败后真实拥有者保有更新意图 | 一次性 PreparedUpdate 关联 owner/generation；真实 helper ready 后转移至 singleton AppLifetime。恢复专用窗口不重开普通设置 scope 或已关闭的网络；源码预审1两项已修正。 |
| 完整生产停止顺序、scope/Dispatcher与撤销 | 新冻结候选独立执行15/15通过；真实 settings/updates 依赖、ValidateScopes、真实Dispatcher、产品管道/事务worker、窗口尚存/已释放后故障、重复失败后成功、撤销清理失败及未确认/取消/新offer否定场景均执行。9个worker结果安全，321个输入字节保持。外部安装/注册/重启边界受控，未扩成真实安装证明。 |
| 用户可见恢复入口的明暗/语言 | 独立运行曾发现4个Dark动作不可读；修后8图逐张查看并核对SHA。candidate-3新图与已审通过原图字节完全一致，并绑定新DLL承接视觉；未假称架构师另跑修后渲染。 |
| 原完整本地化规范 | 独立审计曾实际失败，修后 source/compiled/catalog 两阶段exit0，1197条源记录、1230项编译资源/格式匹配。旧457对值保持，新增批准5对；例外清单不变、规则不放宽。 |
| 新包与来源 | candidate-check-3：1258项独立核对匹配；两版各97实际full载荷、feed、清单、外层Magic/大小/SHA及Maintenance前缀、702源和321冻结文件一致。版本/运行时FDD与自有worker不入产品载荷均检查。 |
| 干净来宾安装与运行中静默保留卸载 | guest-install-r4-6：34观察/15布尔，Failure=null；无共享.NET10开始，真实Setup安装Desktop10.0.13，Iris/UiaHost可运行，实际App/外部helper/native退出0，程序/卸载项/事务解除，数据与共享runtime保留。 |
| 新包两版本full应用 | guest-update-r4-1：37观察/22布尔，Failure=null；原App真实退出后native apply返回0，0.2.1单次重启，注册项正确，settings/DPAPI合成secret/SQLite/history/log读验及四文件哈希保持，work目录0，新App正常退出；5个安装链真实句柄全exit0。 |
| 实际来宾输入与终态 | guests-independent-final8-1：142/142独立只读断言/字节核对通过；两次Sandbox已停止、列表为空。两张实际0.2.0/0.2.1窗口原图已查看。Windows Installer常驻服务msiexec /V单列，未声称该系统服务已退出。 |
| 提交与归档 | final-commit-independent-binding-2：702源码blob对应最终提交；859份工程证据在交付commit中逐原字节匹配，46份架构师独立记录按原约定由验收提交归档。公共契约/default/schema/无关包没有R4变更，二进制未入Git，current_task已按全文归档移除。 |

原失败、旧包/旧候选、原审计和不完整图像保持。最初最终提交校验把明确留待架构师归档的46份独立记录误当成工程师必须提交的文件，断言失败；原始结果保留在 final-commit-independent-binding-1，所有这些文件本地SHA本就匹配。修正校验范围后没有字节不一致，不将该校验假设当作产品失败。

## 验收边界与 C 必须接续事项

当前网络下默认 native 运行时完整下载实际报 Peer disconnected，原失败及DNS/传输诊断保留。成功的两轮先在**来宾内**下载微软运行时、按官方元数据SHA512和微软有效签名核对，再放入正常Downloads缓存；Iris开始前仍无共享.NET10，由**未修改的真实Setup**自动检测并安装运行时。架构师已在本单明确允许该第二种路径证明受影响的安装/退出/更新，未允许手动预装运行时替代，也未把默认下载故障写成关闭。

因此本次 B 通过的是正式产品实现、本地操作与上述缓存辅助条件下的真实引导。T-039C **必须**验证公开资产的真实匿名用户UI检查/下载/安装重启，并重新核对无夹具预填缓存的运行时默认引导；不能用B专用GuestUpdateHost或本次缓存条件替代。未完成该线上/默认引导门禁前，父T-039不得结案。匿名远端refs当前仅确认初始main，无公开tag；本次未push/发布，Release状态由C实际核对。

标准用户拒绝提升、Microsoft安装要求重启、跨用户、物理便携/硬件环境及NFR沿用真实未测披露；未据管理员Sandbox扩大平台承诺。T-040继续承担最终全功能手动/NFR验收。
