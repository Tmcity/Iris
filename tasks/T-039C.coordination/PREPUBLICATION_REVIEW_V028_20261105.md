# T-039C：0.2.8 更新发现修正检查点

正式 [0.2.7](https://github.com/Tmcity/Iris/releases/tag/v0.2.7)已成功发布，原 CI、Source `fc44a3a`、所有标签和资产完整保持。用户最新实际反馈原 0.2.2 持续出现“请求过于频繁”，此前正向 offer 反馈不足以证明成功；真实更新/下载/升级仍未通过。

主会话也独立读取到共享匿名 REST 额度 60 用尽。修前受控用例使 REST 返回 403，而同仓库正式 latest feed 返回 200，实际复现相同 IrisException 限流错误。官网直接 latest feed 已实际返回 200，字节与正式 0.2.7 feed 相同。

## 修正结果

更新发现优先读取同一官方仓库的 `releases/latest/download/releases.win.json`。仅该 feed 明确 404 时保留原 newest-compatible-release 选择；其它错误、取消和限流继续报告原结果。下载固定到已验证版本的 `releases/download/v<version>/<filename>`，保留 SHA/长度、full-only、官方域、重定向、所有权和取消规则；增加的 URI 许可仅为精确 latest feed。

未改变目的仓库、用户设置、秘密/历史、契约/schema 或依赖。原初次 SDK 适配的三项失败与源码设计记录保持；最终使用直接 feed 字节与 SDK feed 解析，避免 SDK 自动附加平台查询参数，15 项更新传输定向已通过。

| 项目 | 核对结果 |
|---|---|
| 发布源码 | `2b2bbe70f765f9805471a0d555874f8e2383923a` |
| 规范来源树 | `5e15542f003811e262e80737113ce11b3c8b8b83`，独立 Git tree 原始字节匹配 |
| 正常本地源码整合 | `15339a3cced656bc68eb8142a0f0fc5939e3e9b4` |
| 完整 Release | 0 警告、0 错误 |
| 全量测试 | 3770 总计 / 3768 成功 / 0 失败 / 2 既有 error183 环境跳过；完整选择不变 |
| 定向与修前实证 | 15/15 成功；修前 REST403 单项 1/1 失败，原结果保留 |
| 来源范围 | 721 提交 blob 与工作树匹配；两项 App Updates 实现、一个测试文件、release-plan 与 RELEASING 变化 |
| 唯一候选 | `F:/Iris/artifacts/t039c/release-0.2.8-public-base-candidate-1` |
| 资产/载荷 | 19 资产 / 97 载荷；包含正式 0.2.2 与 0.2.7 history，保留来源/外层/feed 绑定 |
| 本地 Setup | 116770862 字节，SHA256 `e5bf367248446a63f484afaff41a34f6ff992bb75ddac7ad96c8b4d1c041f19d` |
| 本地 full | 64630114 字节，SHA256 `18ef33f21d4256acc6dc8029e52568e8b5215c26682a786cc7601d00962f3c05` |
| 正式前版 history | 0.2.7；full 64627391 字节 / SHA256 `f8f9de897b637f8c98a07d601420885bf65529701d014fc52b59f87ce75bace8` |

[独立复核](F:/Iris/tasks/done/T-039C.evidence/next-source-v028-20261105-1/review.json)包含实际原验证/反例/中间失败/候选与源码记录。候选 SHA 为本地构建值，后续实际 CI 将另行核对正式字节。旧 0.2.2 安装不会因本地修正而变化，仍须在其限额恢复后用户手工真实检查、下载和升级。

## 后续发布范围

源码、原件、用户真实限流失败和 Git 字节审阅归档完成后，才能请求仅创建并推送 annotated `v0.2.8` → `2b2bbe70f765f9805471a0d555874f8e2383923a`，由现有 Actions 发布修正版本。用户此前仅授权 0.2.7，0.2.8 需要单独确认；main/codex 没有推送范围，所有已发布版本保持。

当前 0.2.8 未授权、无标签、未发布。当前旧实例/数据由用户保留，恢复后的单次 UI 检查问题已发送；实际结果仍待用户反馈。真正升级、native 退出顺序、一次正常重启、设置/秘密/旧历史和新翻译、默认保留数据卸载、C/父T-039和T-040/NFR均未结案；首次安装性能问题和 M6 延期保持。
