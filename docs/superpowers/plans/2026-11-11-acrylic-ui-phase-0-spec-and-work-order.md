# 亚克力界面重构 · 阶段 0（规范与工单）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把已批准的设计文档 `docs/superpowers/specs/2026-11-11-acrylic-ui-redesign-design.md` 的 §7 四处规范变更落到 `specs/`，签发工单 T-041 并替换 `tasks/current_task.md`，把 `.superpowers/` 加入 `.gitignore`。

**Architecture:** 纯文档改动，不碰 `src/`、`tests/`、`specs/CONTRACTS.cs`。规范变更只有四处：翻译弹窗去掉关闭按钮与翻译按钮、加入停顿 1 秒自动翻译、历史窗口复制按钮允许只显示图标。`ARCHITECTURE.md` 递增版本号并加修订记录；`TRANSLATION_WINDOW.md` 与 `HISTORY_UI.md` 原本没有修订记录区，在头部版本块后加一行。工单 T-041 覆盖全部五个实现阶段，但**只在阶段 0 结束时提交一次**，后续各阶段各自提交（写入完成报告的偏差）。

**Tech Stack:** Markdown 规范文档、Git、PowerShell 5.1 / Git Bash、本地化审计脚本 `build/audit-localization.ps1`、.NET SDK 10.0.402。

**关于 TDD：** 本阶段没有可测代码，是纯文档变更，因此下面用「逐字核对原文 → 编辑 → 用 grep / git diff 验证」代替「先写失败测试」。设计文档 §8 的测试要求属于阶段 1–5。

---

## 前置条件（动手前逐条确认）

- 当前分支是 `codex/t041-acrylic-ui`；`git log --oneline -1` 输出以 `24a495a Add acrylic UI redesign design document` 开头。
- `git status --short -- specs docs .gitignore` 无输出（这三处干净）。`tasks/` 下大量未跟踪文件是仓库既有噪音，不要动，也不要在提交时 `git add tasks/`。
- 文件行尾必须保持原样：`specs/TRANSLATION_WINDOW.md` 是 **LF**、`specs/HISTORY_UI.md` 是 **CRLF**、`specs/ARCHITECTURE.md` 是 **CRLF/LF 混用**、`.gitignore` 是 **CRLF**。三个文件都是 **UTF-8 无 BOM**。用 Edit 工具逐处替换，不要整体重写文件。
- 本阶段：不推送、不打标签、不发布、不 reset / rebase / amend；不触碰 `.codex`；不修改 `specs/CONTRACTS.cs`。
- 本阶段只有**一次**提交，覆盖：设计文档状态行、四处规范变更、T-041 工单与 `current_task.md`、`BACKLOG.md` 一行、`.gitignore`。

---

### Task 1: 设计文档状态行改为「已批准」

**Files:**
- Modify: `docs/superpowers/specs/2026-11-11-acrylic-ui-redesign-design.md:3`

- [ ] **Step 1: 核对原文**

Run: `sed -n '3p' docs/superpowers/specs/2026-11-11-acrylic-ui-redesign-design.md`
Expected: `2026-11-11 · 状态：待用户审阅 · 拟定工单号 T-041 · 分支 `codex/t041-acrylic-ui``

- [ ] **Step 2: 替换**

old_string:
```
2026-11-11 · 状态：待用户审阅 · 拟定工单号 T-041 · 分支 `codex/t041-acrylic-ui`
```
new_string:
```
2026-11-11 · 状态：用户已批准（正文与实现方式一）· 工单号 T-041 · 分支 `codex/t041-acrylic-ui`
```

- [ ] **Step 3: 验证**

Run: `sed -n '3p' docs/superpowers/specs/2026-11-11-acrylic-ui-redesign-design.md`
Expected: 输出以 `2026-11-11 · 状态：用户已批准（正文与实现方式一）` 开头。

---

### Task 2: `specs/TRANSLATION_WINDOW.md` 三处改动

**Files:**
- Modify: `specs/TRANSLATION_WINDOW.md:3`（头部加修订记录行）
- Modify: `specs/TRANSLATION_WINDOW.md:36`（§3 取得新操作的条件）
- Modify: `specs/TRANSLATION_WINDOW.md:84`（§7 顶栏与停止）

行尾是 **LF**，用 Edit 工具替换，不要重写整个文件。上面的行号是改动前的；Step 1 插入一行后，§3、§7 那两行各后移一行（37、85），所以后面的步骤用 `grep` 按内容定位，不用行号。

- [ ] **Step 1: 头部加修订记录行（第 3–4 行之后）**

Run: `sed -n '3,4p' specs/TRANSLATION_WINDOW.md`
Expected:
```
> 2026-10-02，Codex；T-015 / ARCH v1.25 / CCR-013 正式规范。
> 取代 TRANSLATION_SESSIONS_DRAFT.md、T-015_SESSION_DECISIONS_PREPARATION.md 的候选决策；实现与行为仍须 T-015 独立验收。
```

Edit，old_string:
```
> 取代 TRANSLATION_SESSIONS_DRAFT.md、T-015_SESSION_DECISIONS_PREPARATION.md 的候选决策；实现与行为仍须 T-015 独立验收。
```
new_string:
```
> 取代 TRANSLATION_SESSIONS_DRAFT.md、T-015_SESSION_DECISIONS_PREPARATION.md 的候选决策；实现与行为仍须 T-015 独立验收。
> 2026-11-11 · T-041 / ARCH v1.58 · 顶栏去掉关闭按钮与翻译按钮；改为按 Enter，或原文 / 语言停止修改 1 秒后自动翻译。界面材质与布局改动见 T-041。
```

- [ ] **Step 2: 改 §3 取得新操作的条件（第 36 行）**

Run: `grep -c "按 Enter/翻译按钮才取得新操作。" specs/TRANSLATION_WINDOW.md`
Expected: `1`（该句只在 §3 那一段出现一次）。

Edit，old_string:
```
按 Enter/翻译按钮才取得新操作。
```
new_string:
```
按 Enter 才取得新操作；原文、源语言或目标语言停止修改 1 秒后也取得新操作（停顿自动翻译，与按 Enter 完全等价，包括写入历史和使用缓存）。停顿自动翻译在以下情形不触发：预处理后原文为空（不报错，也不显示“请输入”提示，该提示只在按 Enter 时出现）、输入法正在组字（组字结束后重新计时）、原文与语言同上一次已发起的翻译完全相同、用户刚点击停止且尚未再次修改，以及由程序写入正文的入口（划词、剪贴板、截图、历史重译；这些入口立即翻译，不经过停顿计时）。原文超限时不发请求，只显示现有超长提示。窗口隐藏、关闭或应用退出时取消等待中的计时。
```

- [ ] **Step 3: 改 §7 顶栏与停止（第 84 行）**

Run: `grep -c "交付 ARCH §14 的顶栏（拖动、钉住、设置、关闭）、原文、多语言选择、交换、翻译/停止、" specs/TRANSLATION_WINDOW.md`
Expected: `1`

Edit，old_string:
```
交付 ARCH §14 的顶栏（拖动、钉住、设置、关闭）、原文、多语言选择、交换、翻译/停止、卡片服务名/模型/耗时/缓存及截断状态、可选只读文本、复制/重试。
```
new_string:
```
交付 ARCH §14 的顶栏（拖动、源语言、钉住、设置）、原文、多语言选择、交换、仅翻译进行中显示的停止、卡片服务名/模型/耗时/缓存及截断状态、可选只读文本、复制/重试。顶栏不含关闭按钮：隐藏靠 Esc，或未钉住时失去焦点（HideOnDeactivate）；固定后只有 Esc 或取消固定。没有独立的翻译按钮：Enter 立即翻译（并按上文取消等待中的计时），原文或语言停顿 1 秒自动翻译。
```

- [ ] **Step 4: 验证三处都改了，且没有残留**

Run:
```bash
grep -c "按 Enter/翻译按钮\|设置、关闭）\|交换、翻译/停止" specs/TRANSLATION_WINDOW.md; echo "---"; grep -c "停顿自动翻译" specs/TRANSLATION_WINDOW.md; echo "---"; grep -n "翻译按钮\|关闭按钮" specs/TRANSLATION_WINDOW.md | cut -d: -f1; echo "---"; git diff -U0 -- specs/TRANSLATION_WINDOW.md | grep -c "^@@"
```
Expected:
- 第一条输出 `0`（三句旧措辞都已消失）。
- 第二条输出 `1`（`grep -c` 数的是行数：只有 §3 那一行含“停顿自动翻译”）。
- 第三条输出 `5` 和 `85` 两个行号：新增的修订记录行，以及 §7 那一行。两处都是“去掉 / 不含 / 没有”的表述。
- 第四条输出 `3`（恰好三处改动，没有误伤别的行）。

---

### Task 3: `specs/ARCHITECTURE.md` 版本号与 §14 表格

**Files:**
- Modify: `specs/ARCHITECTURE.md:13-14`（版本号行 + 新增修订记录）
- Modify: `specs/ARCHITECTURE.md:1092`（§14 表格 `TranslateWindow` 行）

文件是 **UTF-8 无 BOM、CRLF/LF 混用**。只做逐处 Edit，绝不重写整个文件（152 KB，重写会毁掉混用的行尾）。上面的行号是改动前的；Step 3 插入一行后，§14 表格那一行后移到 1093。

- [ ] **Step 1: 核对原文**

Run: `sed -n '13,14p' specs/ARCHITECTURE.md`
Expected:
```
> 版本：v1.57 · 2026-10-13 · 状态：**已冻结（首期）**
> v1.57：T-039A实证及IPC修正独立结案；…
```

- [ ] **Step 2: 递增版本号**

Edit，old_string:
```
> 版本：v1.57 · 2026-10-13 · 状态：**已冻结（首期）**
```
new_string:
```
> 版本：v1.58 · 2026-11-11 · 状态：**已冻结（首期）**
```

- [ ] **Step 3: 在版本号行后插入 v1.58 修订记录**

Edit，old_string:
```
> v1.57：T-039A实证及IPC修正独立结案；
```
new_string:
```
> v1.58：T-041亚克力界面重构经用户批准的规范变更。TranslateWindow顶栏去掉关闭按钮与翻译按钮，改为按Enter、或原文/语言停止修改1秒后自动翻译，隐藏靠Esc或未钉住时失焦；TranslateWindow与HistoryWindow的复制仍区分原文与所选服务译文，允许只显示图标按钮。界面材质、布局与其余窗口改动见T-041工单与设计文档；公开契约、设置格式、schema与依赖不变。
> v1.57：T-039A实证及IPC修正独立结案；
```
注意：old_string 只取到 `> v1.57：T-039A实证及IPC修正独立结案；` 为止（分号后还有内容，保留原样不动）。

- [ ] **Step 4: 改 §14 表格里 `TranslateWindow` 那一行**

Run: `grep -c "无边框 Fluent 窗口，可拖动。顶部栏：钉住 / 设置 / 关闭。" specs/ARCHITECTURE.md`
Expected: `1`（只有 §14 表格的 `TranslateWindow` 行）。下面两次 Edit 改的是同一行的前半句和后半句。

Edit，old_string:
```
| `TranslateWindow` | 无边框 Fluent 窗口，可拖动。顶部栏：钉住 / 设置 / 关闭。
```
new_string:
```
| `TranslateWindow` | 无边框 Fluent 窗口，可拖动。顶部栏：源语言 / 钉住 / 设置（无关闭按钮，无翻译按钮；隐藏靠 Esc，或未钉住时失去焦点）。
```

Edit，old_string:
```
翻译/停止按钮、结果卡片列表（服务名 · 模型 · 耗时；可选只读文本；复制 / 重试；错误/截断状态）。Esc 隐藏。完整生命周期与预算见 TRANSLATION_WINDOW.md。 |
```
new_string:
```
原文或语言停止修改 1 秒自动翻译，等同按 Enter；仅翻译进行中显示停止按钮。结果卡片列表（服务名 · 模型 · 耗时；可选只读文本；复制 / 重试；错误/截断状态）。完整生命周期与预算见 TRANSLATION_WINDOW.md。 |
```

- [ ] **Step 5: 验证**

Run:
```bash
grep -n "v1\.58" specs/ARCHITECTURE.md | cut -d: -f1; echo "---"; grep -c "顶部栏：源语言 / 钉住 / 设置（无关闭按钮，无翻译按钮" specs/ARCHITECTURE.md; grep -c "翻译/停止按钮" specs/ARCHITECTURE.md
```
Expected:
- `v1.58` 命中 `13` 和 `14` 两个行号：版本号行与新增的修订记录行。
- 后两条依次输出 `1` 和 `0`。

- [ ] **Step 6: 确认没有误改被设计文档排除的两处**

Run: `git diff -U0 -- specs/ARCHITECTURE.md | grep -c "^@@"; grep -c "NavigationView" specs/ARCHITECTURE.md`
Expected: 第一条输出 `2`（头部一处、§14 表格一处，再无别的改动）；第二条不为 `0`（`SettingsWindow` 行里的 `NavigationView` 还在）。设计 §7 排除的两处——`SettingsWindow` 行写的 `NavigationView`、§5.5 附近的托盘菜单描述——与现状本来就不一致，本次**不改**，只在完成报告里提及。

---

### Task 4: `specs/HISTORY_UI.md` 头部与复制规则

**Files:**
- Modify: `specs/HISTORY_UI.md:3`（头部加修订记录行）
- Modify: `specs/HISTORY_UI.md:23`（复制规则）

行尾是 **CRLF**。

- [ ] **Step 1: 头部加修订记录行**

Edit，old_string:
```
2026-10-07 · T-035 · ARCH v1.52 · CCR-035。
```
new_string:
```
2026-10-07 · T-035 · ARCH v1.52 · CCR-035。
2026-11-11 · T-041 · ARCH v1.58 · 复制原文与复制所选服务译文仍须明确区分，允许用图标按钮并以所在卡片和鼠标提示区分。
```

- [ ] **Step 2: 改复制规则**

Run: `grep -c "复制明确区分复制原文和复制所选服务译文。每次显式动作取得GetAsync全文" specs/HISTORY_UI.md`
Expected: `1`（Step 1 插入一行后，这一行从 23 后移到 24）。

Edit，old_string:
```
复制明确区分复制原文和复制所选服务译文。每次显式动作取得GetAsync全文
```
new_string:
```
复制明确区分复制原文和复制所选服务译文（允许用图标按钮，以所在卡片和鼠标提示区分，不必显示文字）。每次显式动作取得GetAsync全文
```

- [ ] **Step 3: 验证**

Run: `sed -n '3,4p;24p' specs/HISTORY_UI.md; git diff -U0 -- specs/HISTORY_UI.md | grep -c "^@@"`
Expected: 第 4 行是新修订记录；第 24 行含 `（允许用图标按钮，以所在卡片和鼠标提示区分，不必显示文字）`；最后输出 `2`。

---

### Task 5: `.gitignore` 加入 `.superpowers/`

**Files:**
- Modify: `.gitignore`（在文件末尾追加）

- [ ] **Step 1: 确认现状**

Run: `grep -n "superpowers\|codex" .gitignore; echo "exit=$?"`
Expected: 无输出，`exit=1`（两者都不在）。**这也说明 `.codex` 本来就被忽略不到，但本单不碰它。**

- [ ] **Step 2: 在末尾追加**

Run: `tail -3 .gitignore`
Expected: 末行是 `*.swp`。

Edit，old_string:
```
*.swp
```
new_string:
```
*.swp
# Local agent scratch: brainstorm mockups and plan run artifacts
.superpowers/
```
（`.gitignore` 末尾是 `*.swp`，该字符串在文件中唯一，可安全替换。）

- [ ] **Step 3: 验证 `.superpowers/` 已生效且 `.codex` 仍未被处理**

Run:
```bash
git check-ignore -v .superpowers/ ; echo "---"; git check-ignore -v .codex/ ; echo "codex-exit=$?"
```
Expected: 第一条输出命中 `.gitignore` 的新行；第二条**无输出**、`codex-exit=1`（`.codex` 仍未被忽略，符合“不碰 `.codex`”）。

---

### Task 6: 签发 T-041 工单

**Files:**
- Create: `tasks/T-041.work-order-20261111.md`
- Modify: `tasks/current_task.md`（整体替换为 T-041）
- Modify: `tasks/BACKLOG.md`（只在末尾加一行签发记录；设计 §10 规定只加这一行，任务表格和其他任务的状态都不动）

工单格式照 `tasks/T-040.work-order-20261106.md`（仓库里唯一的工单文件）：`# T-0XX：标题` → 签发日期行 → 基线段 → `## 本单完整范围`（编号）→ `## 执行方式与边界`（列表）→ `## 接续顺序`。

- [ ] **Step 1: 写工单文件**

用 Write 创建 `tasks/T-041.work-order-20261111.md`，内容：

```markdown
# T-041：亚克力界面重构

2026-11-11 · 由开发会话签发并直接执行。用户 2026-11-11 在本主会话批准设计文档 `docs/superpowers/specs/2026-11-11-acrylic-ui-redesign-design.md` 的正文与实现方式一，并授权本次由开发（而非架构师）执行该文档 §7 的规范修改与工单签发。T-040 保持暂停，其范围与 NFR 指标不由本单接续。

基线为 `codex/t040-memory` 的 `1b1a81b`；工作分支 `codex/t041-acrylic-ui`，本分支首个提交为设计文档 `24a495a`。`origin/main` 落后该基点约 85 个提交，本单不与之同步。

## 本单完整范围

1. 按设计 §7 修改规范并递增 ARCHITECTURE.md 版本号：TranslateWindow 顶栏去掉关闭按钮与翻译按钮，改为按 Enter、或原文/语言停止修改 1 秒后自动翻译；HistoryWindow 复制允许只显示图标按钮。规范变更的原文与目标文本见设计文档 §7 表格。
2. 阶段 1：新建共用主题层 `src/Iris.App/Themes/`（样式 + 浅深两套颜色键），在 `App.xaml` 与测试资源环境（`tests/Iris.App.Tests/TestSupport/WpfTestRunner.cs`、`UpdateRecoveryRenderingTests.cs`）合并同一份；实现窗口材质服务（亚克力 / 纯色 + 是否支持的判定），对 SettingsWindow、TranslateWindow、HistoryWindow 应用亚克力，其余窗口保持纯色；修正 `ThemeService` 切换主题时默认带 Mica 且作用于 `Application.MainWindow` 的行为，改为明确不应用材质，由材质服务独占管理。
3. 阶段 2：按设计 §4 重构设置窗口。导航加图标与选中态、宽度规则改为 680 阈值（≥680 为 176，<680 只显示图标）；翻译服务页与 OCR 页改为列表 → 详情；各设置项改行卡片；勾选框改开关外观；新增“是否显示详情”界面状态（不保存、不新增页面编号）；删除窄于 680 时列表与详情上下叠放的列定义与代码。Esc 仍为放弃并关闭设置，不作返回。
4. 阶段 3：按设计 §5 重构翻译弹窗。顶栏只留源语言、固定、设置；删除关闭按钮与翻译按钮；进行中才显示停止；补上检测语言灰字与“上一次结果”标记；实现 §5.3 停顿 1 秒自动翻译（计时来源可注入，测试用假时钟），不触发条件逐条实现；删除窄于 420 时重排控件的代码。
5. 阶段 4：按设计 §6.1 重构翻译历史窗口。实际宽度不小于 640 时左右分栏，小于 640 时单列，只用界面代码与现有属性，不改视图模型逻辑；右栏独立滚动；确认条横跨两栏；两种复制改为卡片内图标按钮。
6. 阶段 5：按设计 §6.2–6.5 处理小界面。划词图标改透明窗口内的圆角卡片（24×24 DIP、整块可点）；托盘菜单每项加图标；更新失败窗口保持纯色只调间距与按钮样式；截图遮罩标签改圆角、选区与放大镜中心用系统强调色。
7. 每个阶段结束跑设计 §9 的验证：Release 构建零警告、全部测试通过、本地化审计通过；三个亚克力窗口用真实窗口截图（浅色、深色、中文、英文、默认与最小尺寸）。离屏渲染拍不到亚克力，未取到的证据写“未验证”。改动前后各测一次内存并如实报告，本单不承诺降到 160 MB 指标内。

## 执行方式与边界

- 仅一个当前工单。不推送、不打标签、不发布、不安装；不 reset / rebase / amend。不读取、修改或提交 `.codex`。`specs/` 对开发者只读，但本单经用户单独授权按设计 §7 修改列出的四处，其余 `specs/` 文件不动，`specs/CONTRACTS.cs` 的任何签名不改。
- 不新增设置字段，不改设置文件格式、数据库结构与公开契约；不引入新依赖包，不升级 WPF-UI（4.3.0）。唯一新增行为是翻译弹窗的停顿自动翻译（设计 §5.3）。
- `src` 里不新增中文字面量与中文注释；新文字进 `Strings.resx`、`Strings.en-US.resx`、`build/localization/app-catalog.json`，经 `UiText` 取用。测试不涉及真实网络；代码、日志与异常消息中不得出现 API Key。
- 测试只记录图片哈希的渲染测试在本次会全部变化，必须人工复看，不能只更新哈希。
- 完成报告按 `GEMINI.md`：每个数字/版本/日志行/测试名来自实际命令输出；未验证项写“未验证”；列出与工单的偏差与全部被改动的现有测试。提交结构（设计文档一个提交、规范与工单一个提交、之后每阶段一个提交）与“一张工单一个提交”的习惯不同，写进偏差。

## 接续顺序

先完成设计 §10 的阶段 0（本文件、§7 规范修改、`current_task.md` 转移、`.gitignore`）并提交；再按阶段 1 → 5 逐个实施，每阶段结束提交一次并跑设计 §9 的验证。阶段之间不重新审阅设计文档；若发现设计文档与规范或现有行为冲突，停下并在报告中说明，不擅自扩大范围。需要真实窗口截图、真实点击（划词图标四角）与输入法组字的项目，交付时写清手动验证方式并标为未自动化。
```

- [ ] **Step 2: 替换 `tasks/current_task.md`**

先读现有文件确认内容（T-040 暂停状态）：
Run: `cat tasks/current_task.md`

它的 T-040 暂停信息已保留在 `tasks/T-040.pause-handoff-20261108.md` 与相关记录中，因此直接整体替换。用 Write 写入 `tasks/current_task.md`：

```markdown
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
```

- [ ] **Step 3: `BACKLOG.md` 末尾加一行签发记录**

Run: `grep -c "下一唯一T-040全功能/NFR/缺陷复核，项目目标未完成，M6延期。" tasks/BACKLOG.md`
Expected: `1`（这是文件最后一条记录的结尾，以 `**2026-11-06 T-039C/父T039结案，T-040签发**` 开头）。

Edit，old_string:
```
下一唯一T-040全功能/NFR/缺陷复核，项目目标未完成，M6延期。
```
new_string（中间空一行，与文件里各条记录之间的写法一致）:
```
下一唯一T-040全功能/NFR/缺陷复核，项目目标未完成，M6延期。

**2026-11-11 T-041签发（亚克力界面重构）** — 设计文档docs/superpowers/specs/2026-11-11-acrylic-ui-redesign-design.md经用户批准，并授权本次由开发执行其§7的四处规范修改（ARCHITECTURE.md升至v1.58）和工单签发。T-040保持暂停，其范围与NFR指标不由T-041接续；其余任务状态未改。
```
**只加这一条。不在任务表格里加行，不改 T-040 或任何其他任务的状态。**

- [ ] **Step 4: 验证**

Run:
```bash
grep -c "T-041" tasks/BACKLOG.md; git diff -U0 -- tasks/BACKLOG.md | grep -c "^@@"; grep -c "^| T-040 | ▶ |" tasks/BACKLOG.md; ls tasks/T-041.work-order-20261111.md; grep -c "T-041" tasks/current_task.md
```
Expected: 依次为 `1`（BACKLOG 里只有新加的那一行提到 T-041）、`1`（只有一处改动）、`1`（T-040 那一行原样还在）、工单文件路径、一个大于 `0` 的数。

---

### Task 7: 全量验证与提交

**Files:** 无新增改动。

- [ ] **Step 1: Release 构建零警告**

Run: `dotnet build Iris.slnx -c Release`
Expected: 以 `Build succeeded.` 结束，`0 Warning(s)`、`0 Error(s)`。（本阶段未改任何代码，此步是确认没有误伤。）

- [ ] **Step 2: 全部测试通过**

Run: `dotnet test Iris.slnx -c Release`
Expected: 所有测试通过，0 失败。若出现失败，本阶段没改代码，先 `git stash` 后重跑确认是否为环境既有问题，不要改测试去凑。

- [ ] **Step 3: 本地化审计通过**

Run（PowerShell 工具里，仓库根目录；这台机器没有 `pwsh`，用 Windows PowerShell 5.1）: `powershell -NoProfile -ExecutionPolicy Bypass -File build/audit-localization.ps1; "exit=$LASTEXITCODE"`
Expected: `exit=0`，无 `unapproved`。脚本需要 `dotnet` 和 `python`（本机是 `D:\anaconda3\python`），证据写到 `artifacts\localization-audit`（不入库）。审计只扫 `src/` 下的 `.cs` 与 `.xaml`，本阶段全是文档改动，预期通过——如果**失败**，说明审计范围与预期不符，停下报告，不要改 `exceptions.json` 去凑。

- [ ] **Step 4: 人工核对 diff**

Run: `git status --short -- specs docs src tests tasks/BACKLOG.md tasks/current_task.md tasks/T-041.work-order-20261111.md .gitignore`
Expected: 7 个已修改路径（` M`）——`docs/superpowers/specs/2026-11-11-acrylic-ui-redesign-design.md`、`specs/TRANSLATION_WINDOW.md`、`specs/HISTORY_UI.md`、`specs/ARCHITECTURE.md`、`tasks/BACKLOG.md`、`tasks/current_task.md`、`.gitignore`；2 个未跟踪路径（`??`）——`tasks/T-041.work-order-20261111.md` 和 `docs/superpowers/plans/`。**不应出现任何 `src/`、`tests/`、`specs/CONTRACTS.cs` 路径。**

Run: `git diff --stat -- specs docs .gitignore tasks/BACKLOG.md tasks/current_task.md`
Expected: 行数增删很少（每处 1–3 行），`ARCHITECTURE.md` 的改动行数是个位数。若某文件显示几十行变动，说明行尾被改写，`git checkout -- <file>` 重做该文件。

- [ ] **Step 5: 提交**

```bash
git add docs/superpowers/specs/2026-11-11-acrylic-ui-redesign-design.md docs/superpowers/plans/2026-11-11-acrylic-ui-phase-0-spec-and-work-order.md specs/TRANSLATION_WINDOW.md specs/HISTORY_UI.md specs/ARCHITECTURE.md tasks/T-041.work-order-20261111.md tasks/current_task.md tasks/BACKLOG.md .gitignore
git status --short --cached
git commit -m "$(cat <<'EOF'
T-041 phase 0: approve design doc, apply spec changes, sign work order

Apply the four spec changes listed in the design document's §7: the
TranslateWindow top bar loses its close and translate buttons and gains
pause-triggered auto-translation; the HistoryWindow copy actions may use
icon-only buttons. Bump ARCHITECTURE.md to v1.58.

Sign T-041, move current_task.md to it, add the BACKLOG row, and ignore
.superpowers/. T-040 stays paused.

Co-Authored-By: Claude Fable 5.2 <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 6: 确认提交内容**

Run: `git show --stat HEAD`
Expected: 恰好 9 个文件（设计文档、本计划、三个规范、工单、current_task、BACKLOG、.gitignore）。**不含 `.codex`，不含任何 `src/` 或 `tests/` 路径，不含 `tasks/T-039C.coordination/` 之类的未跟踪噪音。**

- [ ] **Step 7: 报告**

把以下内容写进阶段 0 的交付说明（作为 T-041 完成报告的一部分，不另建文件）：
- 与工单的偏差：设计文档 §10 规定的提交结构与“一张工单一个提交”的习惯不同，已按 §10 执行；本阶段把设计文档状态行改动并入阶段 0 提交（用户 2026-11-11 确认）。
- 被改动的现有文件清单（7 个）与每个的实际改动行数，来自 `git show --stat HEAD`。
- 未处理但已发现的不一致：`specs/ARCHITECTURE.md:1097` 写 SettingsWindow 用 WPF-UI `NavigationView`、`:286` 的托盘菜单描述，都与现状本来就不一致，按设计 §7 本次不改。
- `specs/PROJECT_HANDOFF.md:223/225/257` 历史记录里出现“真实翻译按钮”，属历史交接记录而非规范性界面要求，未改。
- 未验证项：本阶段无运行时行为变化，无未验证项；设计 §9 的截图与内存验证属阶段 1–5。

---

## 自检（写完计划后对照设计文档核对）

**设计 §7 四条覆盖情况：**

| # | 设计 §7 条目 | 落在哪个 Step |
|---|---|---|
| 1 | TW §3 取得新操作的条件 + §5.3 不触发条件 | Task 2 Step 2 |
| 2 | TW §7 与 ARCH §14 顶栏「钉住/设置/关闭」→「源语言/钉住/设置」；「翻译/停止」→ 仅进行中显示停止 | Task 2 Step 3、Task 3 Step 4 |
| 3 | 同上两处：无关闭按钮，隐藏靠 Esc / 失焦 | Task 2 Step 3、Task 3 Step 4 |
| 4 | HIST §23 复制区分原文与所选服务译文，可只用图标按钮 | Task 4 Step 2 |

**设计 §10 阶段 0 的四项交付：** 设计文档状态行（Task 1）、§7 规范修改（Task 2–4）、T-041 工单（Task 6）、`.gitignore`（Task 5）。

**不做的事（已确认排除）：** `ARCHITECTURE.md:1097`、`:286`、`specs/CONTRACTS.cs`、任何 `src/` 与 `tests/` 文件、`.codex`。

---

**Phase 0 计划到此结束。** 阶段 1（主题层、材质服务、主题切换修正）的计划要等主题层调研代理返回 ThemeService、DI 组合根、三个窗口的创建点、测试资源环境与 WPF-UI 4.3.0 真实 API 的事实后单独编写，文件名暂定 `2026-11-11-acrylic-ui-phase-1-theme-layer.md`。
