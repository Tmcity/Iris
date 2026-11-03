# T-039C：0.2.6 发布检查点

用户已确认的 0.2.5 标签已正常推送，实际 [CI 41461057191](https://github.com/Tmcity/Iris/actions/runs/41461057191) 建测失败；读取前版、打包和发布均跳过。原 annotated tag `95c9c344ac385798503a317b8a9ac2fb57485134` → `ee2d7118597d07b891066256349098877e65f794` 保留，没有重跑或覆写。正式版本仍只有 [0.2.2](https://github.com/Tmcity/Iris/releases/tag/v0.2.2)。

原 ZIP 506060 字节，SHA256 `e3916ed568433386720fbacf81ab41c2c33016f30792fd0c32680ffbd5d69f0f` 与官方 artifact `13148997572` digest 一致，51 份原件与修前源码已冻结。原全量 3757 总计、3755 成功、1 失败、1 跳过；唯一失败是自启跨保存/Reload 用例在后台状态查询完成前断言重试资格。

## 修正与验证

受控的假查询阻塞复现了同一“期望 True、实际 False”错误。修正后的用例使用 WPF 调度器，在状态通知完成后断言；两种情况覆盖普通查询及保存已经完成而查询仍被显式阻塞。跨无关字段保存、Reload、重试和状态提示的原断言均保留，并在退出前观察全部已接纳工作。相邻两个自启外部意图用例也观察状态通知，并在 finally 显式放行假同步应用门。生产实现、公共契约/schema、依赖和全量测试选择没有变化。

| 项目 | 实际结果 |
|---|---|
| 发布源码 | `c32fcb283e417ab11715c46207fdc8cfde4baedf` |
| 规范来源树 | `c39947f03475872a5178929e32b078a383d537e2` |
| 唯一候选 | `artifacts/t039c/release-0.2.6-public-base-candidate-1` |
| 正式前版 | 0.2.2；旧 full 64627273 字节，SHA256 `c86982f930aafcd142167bbd871d03b588630f7d1d625da968a811daa2dd5d14` |
| 来源/资产/载荷 | 721 项提交 blob 与固定工作树字节、18 项资产、97 项载荷匹配；feed/full/外层/provenance 核对通过 |
| 本地外层 SHA256 | `10274bb583095de434874cb15c95839c503d3be4bd21fc6d7f5c38102401ed02` |
| 本地 full SHA256 | `e478bb4b6accab61e5e7dc25229f8306771abf6c914026f75700ad7f45e75d8b` |
| Release | 0 警告、0 错误 |
| 全量 | 3758 总计 / 3756 成功 / 0 失败 / 2 既有 error183 隔离站跳过；原原因保留 |
| 分项 | Core 985/985、App 867/867、维护 39/39、发布工具 16/16；自启定向 18/18 |

只有一个测试文件、release-plan 和 RELEASING 说明发生变化。独立结果在 [review.json](F:/Iris/tasks/done/T-039C.evidence/next-source-v026-20261103-1/review.json)；原筛选零测试、首次 CA1859 编译失败、受控反例及最终建测/打包原始输出均保留。固定 vpk 1.2.161，App/UiaHost FDD、Maintenance SC，未签名；full 应用，delta 只生成。CI 将构建它自己的字节，本地候选 SHA 不充当正式资产 SHA。

## 旧实例与记录器

同一用户 Sandbox `590f8848-f6b4-4bdb-a151-85c4a4f19305`，最新只读 `2026-11-03T14:13:32.5032553Z` 仍为正常 0.2.2/PID 6604，开始时间 `2026-11-02T04:25:01.2204360Z`；启动检查已保存为 false，设置/秘密/历史哈希与原 old-ready 一致，没有新版本包。秘密内容未采集；原虚构 Key POST200 和旧历史已独立核对。

V2 记录器在清理已不存在的事件时停止，原错误、源码和全部事件已保留。新的只读 V3/PID 4604 已 Ready：PowerShell 5.1 解析、真实事件队列中同类缺失清理反例，以及自有辅助 cmd 进程的一次启动/退出/退出码 0 均通过。辅助进程明确标为 AuxiliaryProbe，未执行真实 Iris；它不代替生产更新或重启验收。原 0.2.2 基线与全部数据保持，旧保存入口继续可用。[旧版复核](F:/Iris/tasks/done/T-039C.evidence/manual-old-ready-before-v026-20261103-1/binding.json)与 [V3 证据](F:/Iris/tasks/done/T-039C.evidence/recorder-v3-native-20261103-1/binding.json)已冻结。发布动作前会再次读取实际进程、记录器和数据。

## 请求确认的动作

完成本地审阅归档提交后，由用户单独确认，仅创建 annotated `v0.2.6`，固定指向发布源码 `c32fcb283e417ab11715c46207fdc8cfde4baedf`，正常推送这一标签到 `Tmcity/Iris`，由既有 Actions 发布新版本。main/codex 分支不在本次推送范围。标签不指向本地合并或审阅报告提交。

0.2.5 授权不涵盖 0.2.6；当前 0.2.6 未授权、创建或推送。原 0.2.0/0.2.1/0.2.3/0.2.4/0.2.5 标签、失败和候选保留。若新 CI 失败，保留原件后再审具体修正和新的唯一版本。

实际 CI 成功、草稿资产读回及正式公开资产的长度/哈希/来源/feed/载荷均通过后，才按 [0.2.6 手工清单](F:/Iris/tasks/T-039C.coordination/MANUAL_UPDATE_V026_20261103.md)由用户分段执行检查→下载→Ready→确认安装/真实退出与 native 顺序/恰好一次正常重启/设置 Key 旧历史保留→更新后卸载。C/父 T-039、T-040/NFR 尚未结案，M6/T-033 延期。
