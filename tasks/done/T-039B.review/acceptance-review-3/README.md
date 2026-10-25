# T-039B 第 3 轮独立复审：R3

2026-10-25 · 架构师 · 目标提交 `05452e0a85a36982ef8bfd22d5a981e581eeafcf`，父提交 `4d19a46cccfa1041a3838011abf4330ee035c89c`。

用户已明确：实现转交 Gemini，由用户传话；架构师负责复审与工单，不再直接操作旧 Codex 开发会话。此次未发送开发消息、未 push、未发布、未改产品源码或既有测试。

## 结论

**R3 暂不通过，签发 T-039B-R4。** 根提供程序解析 Scoped VM 的问题已修正，重新打包与证据字节核对通过，R2 偏差披露已补齐；但“停止失败后保留候选并可重试”在接入生产的更新停止依赖后仍失败。新测试虽然采用正确的 DI 生命周期，却遗漏了实际停止依赖，因而未暴露该问题。

前两轮已确认的结果保留，父 T-039 / T-039B 继续进行；T-039C、T-040 未开始。

## 独立构建与全量测试（实测）

`architect-r3-release-build.log` / `.command.json`：`dotnet build Iris.slnx -c Release --no-incremental`，exit 0，原始汇总：

```text
已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:57.19
```

`architect-r3-full-run.log` / `.command.json`：`dotnet test Iris.slnx -c Release --no-build`，exit 0，原始汇总：

```text
  总计: 3707
  失败: 0
  成功: 3705
  已跳过: 2
  持续时间: 2m 14s 594ms
```

两项跳过均为既有 Clipboard 隔离窗口站 error=183。开发的第三项桌面光标环境跳过在本次实际执行并通过，与第 2 轮裁定一致。运行前确认没有开发构建/测试；Release、全量、下列探针按顺序执行。

## 必须修复：更新关停后无法回滚（实测）

生产接线与执行顺序：

- `AppHostBuilder` 注册单例 `AppUpdateCoordinator`，并由 DI 注入 `AppBootstrapper`。
- `AppBootstrapper.StopAsync` 第 300 行先调用 `_updates?.StopAcceptance()`，`StopCoreAsync` 第 344 行再等待 `_updates.StopAsync()`，然后才到设置窗口清理和 Host 停止。
- `AppUpdateCoordinator.StopAcceptance` 第 272 行设置 `_closing=true` 并清除 `_candidate`。
- `AppLifetime` 捕获停止失败后调用 `RollbackLastApply`，但后者第 247 行遇到 `_closing` 立即返回；候选没有恢复。
- R3 的 `UpdateHandoffLifecycleTests` 第 135 行手工构造 `AppBootstrapper`，未传 `updates` 或 `settingsShutdownCoordinator`，测试中的协调器和窗口实际不参与停止流程。

为排除推导误差，将当前 R3 用例及其辅助源复制到自有探针，使用独立重建后的固定产品 DLL。**同一探针二进制**只通过参数切换上述构造函数是否传入 `updates: coordinator`，其余状态、故障注入和断言相同。源码、97 个固定依赖的指纹见 `rollback-probe-binding.json`、`rollback-probe-source/`；可重建目录为 `artifacts/t039b/independent-r3-rollback-1`。

原 R3 接线对照：`rollback-control-1.log`，exit 0：

```json
{"WireProductionUpdates":false,"Phase":"Failed","HasCandidate":true,"UpdateHandoffTransferred":false,"InstallCanExecute":true,"CheckCanExecute":true,"ShutdownException":"IOException"}
{"WireProductionUpdates":false,"RequirementPassed":true}
{"AllOwnedStaWorkJoined":true}
```

补上生产已有的更新依赖：`rollback-production-1.log`，exit 1：

```json
{"WireProductionUpdates":true,"Phase":"Stopped","HasCandidate":false,"UpdateHandoffTransferred":false,"InstallCanExecute":false,"CheckCanExecute":false,"ShutdownException":"IOException"}
{"WireProductionUpdates":true,"RequirementPassed":false,"ErrorType":"ShouldAssertException","Detail":"coordinator.Snapshot.Phase\r\n    should be\r\nUpdatePhase.Failed\r\n    but was\r\nUpdatePhase.Stopped"}
{"AllOwnedStaWorkJoined":true}
```

该结果同时确认 R3 的 Scoped 解析异常不再覆盖原 `IOException`，真实窗口 VM 的转移标志也已复位；失败点是协调器被永久封口，候选与安装重试入口消失。

此探针没有执行真实安装、联网、生产 IPC 或用户数据操作。复制的 R3 测试本身未调用 `handoff.PrepareAsync`，只设置 VM 标志，因此本探针**不宣称**已验证真实 prepared helper 的撤销。源代码另显示生产会关闭设置会话；这一顺序本次只作源码审查，R4 必须接入实际设置停止依赖验证，不把未执行的后续分支写成实测。

探针第一次编译因复制夹具缺少 `System.IO` 全局 using 而失败（`rollback-probe-build-1.log`）；补齐后编译零警告/错误（`rollback-probe-build-2.log`）。该失败仅属架构师夹具，原日志与初稿保留。两个运行均已等待自有 STA 完整退出。

## final-4 包与来宾证据核对（实测读取，未重跑来宾）

`verify_delivery.py` 的结果见 `delivery-check.json` / `.log` / `.command.json`。1722 项字节、集合和结果检查均通过，包含：

- `delivery-binding.json` 中 698 项源码的工作区 SHA-256 与 HEAD Git blob、26 项证据和 10 张来宾图的指纹。
- 两版 final-4 顶层文件各 8 / 10 项，`SHA256.json` 清单各 7 / 9 项；两版 full 包各 97 个实际 `lib/app/` 载荷与清单的路径、长度、SHA-256 完全一致。
- 两版外层安装入口的 Magic、长度、内部 Setup SHA-256，以及外层前缀与包内 Maintenance 字节完全一致。
- R3 两个来宾原始输入文件与 intent 哈希多重集合一致，使用的确是 final-4 安装器/full 包；执行与停止 exit 0，停止后的 Sandbox 列表为空。
- `guest-install-r3-2` 原始结果有 **24 个观察记录，其中 10 个布尔断言**；`guest-update-r3-1` 有 **26 个观察记录，其中 17 个布尔断言**；两者 `Failure=null` 且没有 false 值。更新引擎另记录原进程退出屏障、native exit 0、0.2.1，工作目录观察为 Count=0。
- R3 归档工单 SHA 匹配。绑定树与最终 HEAD 的差异仅为 `delivery-binding.json` 自身，产品/测试/构建文件没有差异。

实际打开并查看 R3 两张原图：安装后为英文 0.2.0 设置窗口，更新后为英文 0.2.1 设置窗口，当前显示的正文与保存/取消按钮可读。这两张图没有显示完整更新操作按钮，不扩大为全部更新 UI 或线上路径验收；原有 UI 矩阵评审保留。

R3 的两个成功场景和原网络下载失败均保留。本次没有启动新 Sandbox，也没有重做旧完整矩阵。标准用户拒绝运行时提升、要求重启分支、跨用户与物理便携环境等仍沿用已披露限制。

## 报告与绑定的低危修正

1. R3 报告和绑定中的“22/22”“25/25”与实际结果记录数不一致，应按观察、布尔断言、退出码分开如实记录，不能直接混称全部检查。
2. 本地更新输入是 `Iris-0.2.1-full.nupkg`；`GuestUpdateHost` 传入此包，产品执行 `apply --package <该 full 包>`。R3 报告“原生 Velopack 应用增量补丁”超出本次证据。final-4 的确生成了 delta 产物，但本轮实测是 full 包应用，应修正文案。
3. `GitTreeSemantics` 需说明绑定文件自身的排除/前一快照差异；目前声称精确反映全部证据树，实际与最终树仅此文件不同。R4 应固定一个明确定义、可重算且不含自引用的树，不靠反复改写自身树哈希。

R2 范围外文件和测试变动的披露已补齐，本轮不要求重做。上述三项可随 R4 收尾修正，不单独拆单。正式修复要求见 `tasks/current_task.md`。
