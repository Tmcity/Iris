# 当前工单：T-041（亚克力界面重构）

2026-11-11 签发。规范依据：`docs/superpowers/specs/2026-11-11-acrylic-ui-redesign-design.md`（用户已批准）与按 §7 修改后的 `specs/TRANSLATION_WINDOW.md`、`specs/HISTORY_UI.md`、`specs/ARCHITECTURE.md`（v1.58）。工单全文见 `tasks/T-041.work-order-20261111.md`。

T-040（全功能端到端验收、NFR 测量与缺陷闭合）自 2026-11-08 起暂停，保持暂停；其状态、记录与交接文件均不变，见 `tasks/T-040.pause-handoff-20261108.md` 与 `tasks/T-040.progress-20261108.md`。

## 当前实际状态：2026-11-11（阶段 0 完成）

- 分支 `codex/t041-acrylic-ui`（基点 `codex/t040-memory` 的 `1b1a81b`），设计文档提交 `24a495a`。
- 已完成：设计文档；§7 四处规范变更（含 ARCHITECTURE.md 递增 v1.58）；T-041 签发；`current_task.md` 转移；`.superpowers/` 加入 `.gitignore`。
- 待做：阶段 1 主题层与材质服务 → 阶段 2 设置窗口 → 阶段 3 翻译弹窗 → 阶段 4 历史窗口 → 阶段 5 小界面。

## 剩余范围与固定边界

- 全部范围、边界与验收方式以 `tasks/T-041.work-order-20261111.md` 为准，不在此重复。
- 不推送、不打标签、不发布；不 reset / rebase / amend；不碰 `.codex`；`specs/CONTRACTS.cs` 不改。
- 未验证项（无 Windows 10 环境的纯色回退、划词图标四角真实点击、输入法组字行为、真实窗口截图）必须在完成报告中写明方式并标为未验证。
