# 0.2.9 发布检查点

0.2.9 已完成固定源码、完整 Release/full 验证、唯一新候选和独立来源/资产核对；审阅归档为 `7662ed580eb924756885db7ab3a7d7a04e8ea8ea`，最后只读执行预检已通过。此前用户只确认最终 0.2.8；0.2.9 尚未授权或创建标签。完整执行材料见 [执行准备](F:/Iris/tasks/T-039C.coordination/EXECUTION_READY_V029_20261105.md)。

12:28 UTC 补充：官方匿名 API 已恢复，原 run/jobs/artifacts 的 source/attempt1/最终失败与跳过状态、505370 字节原 ZIP 的完整 API digest均匹配，Release返回HTTP404。见 [官方API补验绑定](F:/Iris/tasks/done/T-039C.evidence/ci-v028-api-supplement-20261105-1/binding.json)。下段和归档提交中的403/待补验描述保留该时点的原始状态。

已授权的 [0.2.8 原 CI](https://github.com/Tmcity/Iris/actions/runs/42015639513) attempt 1 在两项 host composition 测试中失败，publish skipped，没有正式 0.2.8 安装资产。原 annotated tag `8c1f34e73014d21236d3a77b960cb4e24a1bec8b`→`0c9b0a1881d07224597163b1e9b2f080ab60d434` 保持。原 ZIP 505370 字节、SHA256 `9dff4e9062a80911c2b351bbdbec213d1b2e80f7a5adaf58fe629cf115f2639b` 与官网完整 artifact digest 匹配，51 原件及失败源已归档；匿名 REST artifact 元数据补验仍遇限流，原响应保留。

| 项目 | 本地最终核对 |
|---|---|
| 发布源码 | `0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2` |
| 规范来源树 | `fcb432c3df3b5930bbc2900f43235384130b97ae` |
| 与最终 0.2.8 的差异 | WPF 测试程序集初始化、主题测试集合、发布计划与说明；生产 src、workflow、依赖保持 |
| 修前/修后 | 冷启动 host 反例 1/1 失败→1/1 成功，host 13/13 成功；原本地主题超时和受控共享调度线程反例保持，主题 8/8 成功 |
| Release 构建 | 0 警告、0 错误 |
| 完整测试 | 3771 总计 / 3769 成功 / 0 失败 / 2 既有 error183 环境跳过 |
| 调度与断言 | 原主题回调/五秒超时/全部断言保持；完整报告中主题测试时间区间不与其他 App 测试重叠，其余集合并行安排保持 |
| 唯一新候选 | `F:/Iris/artifacts/t039c/release-0.2.9-public-base-candidate-1` |
| 来源/资产/载荷 | 721 Git blob/工作字节、19 实际资产、97 full 载荷逐项匹配，规范树独立计算通过 |
| 本地 Setup SHA256 | `370940ddcba17c92f6778a863f85f6b93f2b4ee4c7f563e4c6e80357b242b302` |
| 本地 full SHA256 | `f6cb7e169b0881a5ff2ecb2437a52b02716396ea68841a786e51dbc8cd22675b` |
| 正式前版 | 0.2.7 full 64627391 字节 / SHA256 `f8f9de897b637f8c98a07d601420885bf65529701d014fc52b59f87ce75bace8`；正式 0.2.2 full 历史保持 |
| 现有用户基线 | 同一 Sandbox 的正式 0.2.7/PID8820；开关 false、原 settings/secrets/history 哈希和日志前缀保持，合成服务/记录器运行，无未完成事务 |

[最终候选独立核对](F:/Iris/tasks/done/T-039C.evidence/next-source-v029-final-20261105-1/review.json)、[原 CI 失败分析](F:/Iris/tasks/done/T-039C.evidence/ci-v028-failure-analysis-20261105-1/binding.json)、[首轮本地失败及反例](F:/Iris/tasks/done/T-039C.evidence/next-source-v029-local-failure-20261105-1/binding.json)和[刷新后的旧版基线](F:/Iris/tasks/done/T-039C.evidence/manual-old-ready-v027-before-v029-20261105-1/binding.json)均已形成。原 0.2.8 本地候选、失败 CI 和 0.2.2→0.2.7 首轮真实 UI 升级失败保留。手工修复成功没有改记为首轮 UI 升级通过。

## 本次仅请求 0.2.9 标签授权

完成本地审阅归档、Git index 原始字节核对和实时只读预检后，仅创建并正常推送 annotated `v0.2.9`→上述固定源码，由现有 GitHub Actions 生成、回读并发布正式资产。本次不推 main/codex 分支，不改写原标签、Release 或 CI，不授权下一版本。本地候选哈希独立记录；正式 CI 的实际资产须另行匿名核对。

0.2.9 正式资产通过后，用户先手工下载安装修正版，沿用同一 Sandbox/合成数据，检查版本及原历史、原服务新翻译并保存状态。当前 0.2.7 带有原维护助手工作目录实现，因此本次先通过正式安装器建立已修正基线。之后再准备下一个递增版本，单独请求其发布授权，用户分段执行实际设置 UI 检查→下载 Ready→确认安装→native 顺序与一次正常重启→旧数据和新翻译→更新后卸载默认保留数据验收。

C/父 T-039 尚未验收；T-040/NFR、首次 .NET 安装耗时问题和 M6 延期仍保持。用户上一答复“确认发布最终0.2.8，随后手工安装修正版”对应最终 0.2.8，未授权 0.2.9 或任何后继版本。
