# 亚克力 UI 改造 · 阶段 2：设置窗口 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把设置窗口 `src/Iris.App/Settings/SettingsWindow.xaml` 改成 Windows 11 设置风格：行卡片、复选框换开关、带图标与选中标记的导航；「翻译服务」页与 OCR 页从左右分栏改成「列表 → 详情」钻取（面包屑返回，Esc 仍是放弃并关闭）；同时按用户裁决给三个亚克力窗口叠背板着色层，并把纯色回退补齐两套主题值后真正启用。

**Architecture:** 见下文「设计裁决（S0）」。本阶段只动视图层（XAML、代码隐藏里的纯视图逻辑、主题层资源字典、材质/主题服务里与回退字典合并有关的部分）与视图模型上的纯视图状态属性；不改保存、校验、热键、网络等业务逻辑，不改 `specs/`。

**Tech Stack:** C# / .NET 10 / WPF、WPF-UI 4.3.0（不升级、不新增依赖）、xunit.v3 + Shouldly + NSubstitute、Microsoft.Testing.Platform runner、`dotnet test Iris.slnx -c Release`、Windows PowerShell 5.1（本机无 `pwsh`）。

> **计划状态（2026-11-24 已补全并自检）：** 全部 15 个任务（Task 1–14，含 Task 4B）的裁决、文件结构、验证命令与带完整代码/清单的详细步骤均已补齐并通过一致性自检，**待用户审阅认可后开始按序实施**。全部代码与命令尚未在工作树中编译、运行过；行号一律对应提交 `dc08c48`，每个任务开工第一步重新 `grep` 定位。

---

## 1. 用户裁决与流程约束

来源：`C:\Users\28910\AppData\Local\Temp\iris-t041-phase2-research\DECISIONS.md`（U1–U4：2026-11-15；U5–U8：2026-11-17；均为用户经 AskUserQuestion 明确回答）。以下八条是用户本人的决定，执行时直接照做，不再当作待定项。

### 1.1 用户已做的裁决

| 编号 | 裁决 | 约束与背景 |
|---|---|---|
| U1 | **背板着色：按样张叠着色。** 在亚克力窗口底上叠一层应用自己的半透明底色：浅色约 70% 的 `#F3F3F3`、深色约 68% 的 `#242424`（样张的 `--m-tint`）。设置、历史、翻译三个亚克力窗口一起用。 | 不得覆盖 WPF-UI 已有的键；新键放 `Iris.Light.xaml` / `Iris.Dark.xaml`；`Iris.SolidFallback.xaml` 要有对应的不透明值。阶段 1 真机实测：浅色背板压在深色桌面上灰度约 135，样张约 170；导航栏与底栏目前是不透明底（浅色 237、深色 28）。 |
| U2 | **行卡片图标：普通页带图标。** 常规、快捷键、划词、截图、网络、历史等普通页面的每个设置行左侧带图标。 | 用 `ui:SymbolIcon`，不用 `Image`，不依赖 Segoe Fluent。「翻译服务」与 OCR 的详情页字段行**不带**图标。 |
| U3 | **新增翻译服务后自动进入它的详情页。** | 点面包屑返回列表。 |
| U4 | **纯色回退：阶段 2 补齐并启用。** 给 `Iris.SolidFallback` 补深色值（按主题给两套不透明值），在系统不支持亚克力时合并它，并加测试。 | 阶段 1 没合并的原因：只有浅色一套，合并后深色主题会白卡白字。 |
| U5 | **背板着色层本阶段就给历史窗口和翻译窗口一并加。**（2026-11-17，DECISIONS.md 第 5 条；原第 6 节 Q2） | 只加着色，不动这两个窗口的版面（版面仍属阶段 3、4）。单开一个任务：Task 4B。见 S0-6。 |
| U6 | **OCR 页保存 / 重新加载后留在详情，由视图层按 `Id` 把选中项写回原来那一项。**（2026-11-17，DECISIONS.md 第 6 条；原第 6 节 Q1 的选项 (b)） | 不改视图模型的既有方法（不动 `MergeOcrSettings`）。见 S0-10。 |
| U7 | **只读模式下仍可进入详情查看。**（2026-11-17，DECISIONS.md 第 7 条；原第 6 节 Q4） | 列表容器不再整体绑 `CanEdit`；列表只在**保存过程中**冻结；行内的开关、「⋯」菜单、拖动把手、添加按钮各自跟随 `CanEdit` 禁用。只读时能点进详情看、不能改。翻译服务页与 OCR 页都这样做（OCR 页的落实程度受协调方裁决 C4 限制，见 1.2A 与第 6.3 节）。见 S0-8、S0-9、S0-10。 |
| U8 | **新增 OCR 服务后也自动进入新服务的详情页**（与翻译服务一致）。（2026-11-17，DECISIONS.md 第 8 条；原第 6 节 Q3） | 视图层在点击添加后判断是否真的加成功再进入。见 S0-10。 |

### 1.2 用户未裁决、按默认处理的

| 编号 | 事项 | 默认做法 |
|---|---|---|
| N1 | 切主题时约 20–25 ms 的一帧中间态 | 阶段 2 不处理；完成报告列为遗留问题。 |

（原 N2「OCR 页保存/重载后留在详情还是回列表」已由用户裁决，见 U6。第 6 节的 Q5 用户未被问到，仍按默认处理，见第 6 节。）

### 1.2A 协调方裁决（2026-11-17 至 2026-11-23，属【计划裁决】，九条都进偏差清单）

来源：DECISIONS.md「协调方对子代理提出的三个问题的裁决」。

| 编号 | 裁决 | 落在哪里 |
|---|---|---|
| C1 | 列表行样式 `IrisListRowItem` **不做独立的「按下」态**：`ListBoxItem` 没有 `IsPressed`，用「选中 + 悬停」近似会在松开后不恢复。只做悬停、选中、键盘焦点三态；不为此新增附加属性。 | Task 2、S0-11 |
| C2 | 金丝雀 `SelectionSettingsRenderingTests`：**每次渲染取图之前同步地再对齐一次 Iris 主题字典**（对齐与取图之间不含 `await`），不把它挪进不并行的集合。 | Task 3、S0-13 |
| C3 | Task 3 里那六条「夹具守卫」断言**保留**（让 Task 3 有一次先红后绿）。 | Task 3 |
| C4 | **不改 `SelectedOcrProfile` 的 setter**（`SettingsViewModel.Ocr.cs:52`，`CanEdit` 为假时拒收，已核实，见 2.7）。OCR 列表行的「进入详情」入口（名字区、右箭头、Enter）：`CanEdit` 为真时全部可用；`CanEdit` 为假且不在保存中时（只读、录制快捷键中等），只有**当前选中行**的入口可用，其余行的入口禁用（看得出不可点）；保存中整张列表冻结。翻译服务页不受此限制（只读时任意行可进）。**这是对 U7 的部分落实**，进偏差清单，交计划时向用户说明（第 6.3 节）。 | S0-10、Task 11、第 6.3 节 |
| C5 | （2026-11-19）**行内校验错误文字用主题层的新键 `IrisRowError`**，不在每行重复内联样式。`TargetType="TextBlock"`，`BasedOn="{StaticResource IrisRowDescription}"`，一个触发器 `Text=""` → `Visibility=Collapsed`；**不设 `Foreground`**（各行原来的 `Foreground` 画刷留在元素上）。用法：`<TextBlock Style="{StaticResource IrisRowError}" Text="{Binding …}" Foreground="{DynamicResource …原来的键}"/>`，放在行卡片第 1 列文字栈的最后。 | Task 2（定义与测试）、S0-4、S0-11；Task 5–9、11 使用 |
| C6 | （2026-11-19；核实口径 2026-11-23 协调方实测后修订）**行卡片图标逐行选**，不是整页同一个。每行选一个语义贴切的 `SymbolRegular` 成员（20 号规格，名字以 `20` 结尾）；写步骤时必须核实成员名存在；找不到贴切的就用该页的页面图标作退路。**核实口径**：以 NuGet 缓存里 WPF-UI 4.3.0 的 `lib\net462\Wpf.Ui.dll` 的只读反射为准——Windows PowerShell 5.1 里 `$t = [Reflection.Assembly]::LoadFrom("$env:USERPROFILE\.nuget\packages\wpf-ui\4.3.0\lib\net462\Wpf.Ui.dll").GetType('Wpf.Ui.Controls.SymbolRegular'); [Enum]::GetNames($t) -contains '成员名'`（实测共 9235 个成员；两份程序集出自同一版源码）。对 `lib\net10.0-windows7.0\Wpf.Ui.dll` 做「按 NUL 切开后整串匹配」只能当辅证：命中 1 次说明存在，命中 0 次**不能**说明不存在——元数据字符串堆会把一个名字存成更长名字的后缀，实测 `History20` 在两份程序集里整串都是 0 次、只见于 `ChatHistory20`，而反射确认它存在。最终以构建为准。步骤里逐行列表：行 → 首选图标 → 退路图标。 | S0-4；Task 5–9 的详细步骤 |
| C7 | （2026-11-19）**页级说明文字**（紧跟页标题的那一条，如截图页的 `ScreenshotSettingsDescription`）保留在页标题下面、可编辑容器之外，用 `Style="{StaticResource IrisRowDescription}" Margin="{StaticResource IrisCardGap}"`。不新增键。 | S0-4；Task 5–9、11 |
| C8 | （2026-11-19）**凡是在应用级直接切 WPF-UI 主题的设置窗口渲染测试夹具都纳入 Task 3**（对齐 Iris 主题字典 + 跑完还原），包括 `OcrSettingsRenderingTests.cs:88–93` 的 `Prepare`；其余夹具由 Task 3 逐个 grep 核实。 | Task 3、S0-13 |
| C9 | （2026-11-23）**Task 4B 的翻译窗口用「在根 `DockPanel` 外包一层只带 `Background` 的 `Grid`」落实着色**（`TranslateWindow.xaml` 插 2 行，既有行不动、不重新缩进）。原因：根 `DockPanel` 自带 `Margin="12,8,12,12"`，直接给它加底色会留一圈没着色的边。这属于 S0-6 所说的「等价做法」，不算违反 U5（没有改任何既有控件、名字与布局属性）。**记入偏差清单**：可视树多一个面板元素，`window.Content` 的类型由 `DockPanel` 变为 `Grid`。Task 14 的真机截图清单补四个看点：翻译窗口四角有无着色溢出、原 `Margin` 那一圈是否与内部同色、头部拖动与四边缩放是否如常、三个窗口底色并排是否一致。 | Task 4B、S0-6；Task 14（截图清单） |
| C10 | （2026-11-23，应用户「请把工作进度接入git并管理」的要求）**进度快照放在本地旁支 `codex/t041-acrylic-ui-wip`**，用 `docs/superpowers/plans/phase-2-work/checkpoint.sh "<这次变了什么>"` 生成。一份快照 = 工作分支暂存区的树 + 本计划文件 + `docs/superpowers/plans/phase-2-work/` + `docs/superpowers/handoff/`。脚本只写旁支，不动工作分支、暂存区与工作区。工作分支上仍然是每个阶段一个本地提交；旁支不合并回去、不 push。**记入偏差清单**：旁支上的快照提交是工单之外新增的本地提交。用旁支而不是直接提交到工作分支，是协调方取的默认做法，已告知用户，用户未表示异议也未明确确认。 | 流程（1.3）；Task 14（偏差清单） |
| C11 | （2026-11-23）**Task 10 在 `SettingsWindow` 上加两个 `internal` 只读属性 `TranslationDetailId`、`OcrDetailId`**。原因：S0-8 只写了两个私有 `Id` 字段，它们在 Task 10 里只被赋 `null`、没有读者，会报 `CS0414`，而仓库 `TreatWarningsAsErrors=true`；`src/Iris.App` 里没有 `#pragma warning disable` 的先例，不为此开头。两个属性只读、不进 `specs/CONTRACTS.cs`，Task 11、12 的测试也用它们断言。**记入偏差清单**（S0-8 之外多出的两个成员）。 | S0-8；Task 10、11、12 |

（C1–C3 来自 DECISIONS.md；C4 是协调方读源码核实之后于 2026-11-17 追加的；C5–C8 是协调方 2026-11-19 追加的；C9、C10、C11 与 C6 的核实口径是协调方 2026-11-23 追加 / 修订的。原文都在 `docs/superpowers/plans/phase-2-work/DECISIONS.md`。）

### 1.3 流程约束（每个任务都适用）

1. 只做工单列出的内容；`specs/` 只读；`specs/CONTRACTS.cs` 签名不得改；`tasks/BACKLOG.md` 不动；不读、不改、不提交 `.codex`。
2. `src/` 里不新增中文字面量与中文注释。既有中文一个字都不能动、不能搬动（本地化审计按「路径 + 全文」登记）；既有中文注释只允许整条删除。新增注释一律英文。
3. 新界面文字进 `Strings.resx`、`Strings.en-US.resx`、`build/localization/app-catalog.json`，经 `UiText` 取用。
4. 不新增依赖包，不升级 WPF-UI 4.3.0。
5. **本阶段只有一个本地提交**：阶段内各任务只 `git add` 暂存，不提交；真正的提交在最后一个任务，且**提交前须用户确认**。不 push / reset / clean / rebase / amend。
6. 测试不得联网；代码、日志、异常消息里不得出现 API Key。
7. 完成报告里每个数字都来自实际命令输出；没验证的写「未验证」；所有偏离工单之处、所有改过的既有测试逐条列出。
8. 真机运行只能用 `%TEMP%` 下的便携副本（exe 旁放 `portable.txt`），绝不直接运行 `src/Iris.App/bin` 里的 exe；进程名是 `Iris.App`。
9. 尽量不动业务逻辑：视图模型只允许加纯视图状态（带通知的属性），不改既有方法的行为。

---

## 2. 已查明的事实

来源是调研笔记 `%TEMP%\iris-t041-phase2-research\`（`A-requirements.md`、`B1-window-structure.md`、`B2-services-ocr-styles.md`、`D-tests.md`），行号对应提交 `dc08c48`。写法：`文件:行号`；`SettingsWindow.xaml` 简写 `xaml`，`SettingsWindow.xaml.cs` 简写 `cs`，`SettingsViewModel*.cs` 简写 `VM`。执行每个任务前，先对本任务要改的行范围重新 grep 一次行号（前面的任务会让行号漂移，见 2.11 的 R13）。

### 2.1 窗口骨架与尺寸

- 文件规模：`src/Iris.App/Settings/SettingsWindow.xaml` 1143 行、`SettingsWindow.xaml.cs` 458 行、`HotkeyRecorderBox.xaml` 127 行、`HotkeyRecorderBox.xaml.cs` 57 行、`SettingsWindowPresenter.cs` 612 行、`SettingsWindowSession.cs` 299 行。
- 根元素 `ui:FluentWindow`（xaml:1–15）：`ExtendsContentIntoTitleBar="True"`（:3）、`Width="760"`（:11）、`Height="520"`（:12）、`MinWidth="600"`（:13）、`MinHeight="440"`（:14）。
- 窗口级资源（xaml:16–19）：`WrappingCheckBoxContent`（:17，12 处使用）、`BoolToVis`（:18，19 处使用）。
- 根 `Grid`（xaml:20）三行 Auto / * / Auto（:21–25）；`ui:TitleBar`（:28–30，`ShowMaximize="False"`）；主体 `Grid`（:33）两列 `140` / `*`（:34–37），到 :1047 结束。
- 底栏 `Border`（xaml:1050–1141）：不透明底色（:1053）、`Padding="16,10"`；`StatusMessageTextBlock`（:1073–1089）、只读提示（:1092–1093）、`MouseRuntimeStatusTextBlock`（:1094）、`OcrTestStatusText`（:1097）；`WrapPanel`（:1098–1121）里 7 个按条件显示的原生 `Button`：`NetworkRetryCleanupButton`（:1099）、`OcrRetryCleanupButton`（:1100）、重新加载（:1102）、重试（:1107）、重试待应用（:1112）、重试热键（:1117）、鼠标重试（:1119）；取消 `ui:Button`（:1128–1132，`IsCancel`、`Width=80`）；保存 `ui:Button`（:1133–1138，`IsDefault`、`Click="OnSaveButtonClick"`、`Appearance="Primary"`、`Width=80`）。
- 全文件共 89 个 `x:Name`、13 个 `ui:Card`、9 个 `ScrollViewer`、23 个 `CheckBox`（其中 12 个有名字；三种写法：内容模板 12、子元素 10、无内容 1）、43 行中文注释（42 条指纹登记在 `build/localization/exceptions.json`）。
- 代码隐藏里没有任何材质或主题代码；材质由 `SettingsWindowPresenter.cs:295` 的 `GetService<WindowMaterialService>()?.Attach(window)` 施加（可选解析，注释 :292–294 说明测试里有 22 处构造站点不注册该服务）。**窗口构造函数不得加必需依赖。**
- 每次打开设置窗口都新建作用域、窗口与视图模型（`SettingsWindowPresenter.cs:284–305`）；`ShowAsync(string? pageKey = null)`（:124–148）；`NormalizePageKey`（:580–607）只认八个页面键，其余归 `general`；`SettingsWindowSession.ActivateAndNavigate`（`SettingsWindowSession.cs:88–112`）在 :108 写 `CurrentPageKey`。
- `WindowMaterialService`（`src/Iris.App/Services/WindowMaterialService.cs`，107 行；**本条为写计划时补查**，只 grep 了成员签名）：`internal sealed class`（:12）；`Windows11_22H2Build = 22621`（:14）；公开无参构造（:19–20）转给 `internal WindowMaterialService(Func<int> osBuild, Func<bool> backdropSupported)`（:24）；`internal bool IsAcrylicSupported`（:30）；`internal void ApplyAcrylic(FluentWindow window)`（:37）；`internal void Attach(FluentWindow window)`（:68）；`internal void RefreshOpenWindows()`（:89）。各方法体内容**未读**，见 2.12。

### 2.2 导航

- 导航 `Border`（xaml:40–126）：右侧竖线 `BorderThickness="0,0,1,0"` + `CardStrokeColorDefaultBrush`（:41–42）、不透明 `SolidBackgroundFillColorSecondaryBrush`（:43）、`Padding="6,12,6,8"`（:44）、内层 `ScrollViewer`（:45）、标题 `TextBlock`（:47–50）。
- 八个导航项都是 `Border`，各自带内联 `Style`，用 `DataTrigger`（绑定 `CurrentPageKey`）把背景切成 `SubtleFillColorSecondaryBrush`；点击经 `MouseLeftButtonUp` 进 `OnNav*Click`。没有图标，不可聚焦。
  - 常规 :53–67、翻译服务 :70–84、快捷键 :87–101、划词 :102–114 —— 这四个**没有** `x:Name`。
  - `NavOcr` 与 `NavScreenshot` 同在物理行 :115（该行 1110 个字符）；`NavNetwork` :116–119；`NavHistory` :120–123。
- 导航处理函数：cs:208、:220、:232、:244、:329、:331、:343、:345。常规、翻译服务、快捷键三个会先取消热键录制，其余五个不会。
- 英文界面下导航文字在 140 宽里折行（「Network and proxy」被截）；标题文字左侧约有 20 px 空白（`A-requirements.md` A8）。

### 2.3 页面清单与行范围（按文件内出现顺序）

| 页面键 | 行范围 | 根元素与名字 | 要点 |
|---|---|---|---|
| `network` | xaml:128–167 | `NetworkPageScroll` | 可编辑容器 :133–152（`IsEnabled={Binding CanEdit}`）：`NetworkModeCombo` :135、`NetworkBypassCheck` :137、`NetworkUrlTextBox` :140、`NetworkCredentialsCombo` :143、`NetworkUserNameTextBox` :147、`NetworkPasswordBox` :149（`PasswordChanged`）。**容器外**：`NetworkValidationText` :153、`NetworkEndpointCombo` :155、`NetworkTestButton` :161、`NetworkCancelButton` :162、`NetworkStatusText` :164、`NetworkResultText` :165。 |
| `history` | xaml:168–179 | `HistoryPageScroll` | 可编辑容器 :170；`HistoryEnabledCheckBox` :172、`HistoryMaxEntriesTextBox` :175、错误文字 :177。 |
| `ocr` | xaml:180–286 | `OcrPageScroll` | 见 2.6。 |
| `screenshot` | xaml:287–319 | `ScreenshotPageScroll` | 可编辑容器 :301；四个复选框 :302 / :305 / :308 / :311；`ScreenshotMaskOpacityTextBox` :315；`ScreenshotError` :316。页标题下有一条**页级**说明 `UiText.ScreenshotSettingsDescription`（xaml:300，按 C7 保留）；各行没有逐行说明。 |
| `general` | xaml:320–441 | `GeneralPageScroll`（:321） | 五个 `ui:Card`：语言 :342–351（`UiLanguageCombo` :345、`UiLanguageHint` :349）；更新 :353–370（版本 :356、`CheckForUpdatesCheckBox` :357、状态 :360、详情 :361、进度条 :362、四个 `ui:Button` :363–368）；主题 :373–396；启动 :399–414（两个复选框 :405 / :409）；日志 :417–439。 |
| `translation` | xaml:443–862 | `TranslationLayoutGrid`（:444，根是 `Grid` 不是 `ScrollViewer`） | 见 2.5。 |
| `selection` | xaml:864–934 | `SelectionScrollViewer` | 可编辑容器 :876；`SelectionErrorTextBlock` :878；四张卡：:881–899（`MouseModeComboBox` :884、复选框 :888、`MouseDragTextBox` :890、`MouseHideTextBox` :893、`MouseExcludedTextBox` :896 高 130）、:900–913（`SelectionOrderComboBox` :903、`SelectionTimeoutTextBox` :908）、:914–923（两个复选框 :916 / :919）、:924–932（`SelectionProcessesTextBox` :929 高 130）。 |
| `hotkeys` | xaml:936–1046 | 根 `ScrollViewer`（:937）**没有名字** | 内容 `StackPanel`（:951）`MaxWidth=600`、左对齐、`IsEnabled={Binding CanAccessHotkeysTab}`；`ItemsControl ItemsSource={Binding Hotkeys}`（:963–1044）；每项一张卡（:966）：启用复选框 :991–995（`IsEnabled` 绑到项视图模型的 `CanEdit`）、`local:HotkeyRecorderBox MinWidth="360" MaxWidth="500" HorizontalAlignment="Left"`（:1000）、草稿错误 :1006–1020、运行状态 :1023–1038。 |

- `HotkeyRecorderBox`：鼠标移出即取消录制（`HotkeyRecorderBox.xaml:9`、`HotkeyRecorderBox.xaml.cs:46–56`）；录制/取消按钮的文字与命令写在内联样式里（`HotkeyRecorderBox.xaml:106–117`），改它只能用 `BasedOn`；测试取它的**第一个**内层 `Border`（`HotkeyRecorderBox.xaml:21`）。
- 各页内边距、`MaxWidth`、标题字号互不一致（R12）；每页各有自己的 `ScrollViewer`，须保留（R16）。

### 2.4 代码隐藏按名字依赖的控件

- 字段（cs:18–22）：`CompactLayoutThreshold = 680.0`、`_isCompactMode`、`_suppressPasswordChanged`、`_dragStartPoint`、`_isDragging`。
- `internal void ApplyAdaptiveLayout(double width)`（cs:55–99）：五个调用点 cs:30、:31、:34、`OnPropertyChanged` :37–44、`OnRenderSizeChanged` :46–53；守卫 cs:57 引用 `TranslationLayoutGrid` / `ProfilesPane` / `DetailsPane`；函数体改写 `ProfilesColDef` / `DetailsColDef` / `ProfilesRowDef` / `DetailsRowDef`。
- `SyncPasswordBoxWithDraft`（cs:125–142）用 `FindName("ApiKeyPasswordBox") as PasswordBox`（cs:127）——**必须是原生 `PasswordBox`**，`ui:PasswordBox` 继承自 `TextBox`，换了会得到 `null`。
- `internal bool ForceClose`（cs:173，测试宿主 `WpfTestRunner.cs:143–166` 依赖）；`OnClosing`（cs:175–187）；`OnKeyDown`（cs:189–206）只处理 Esc：正在录制则取消录制，否则 `vm.ExecuteCancel()`。
- `OnAddProfileButtonClick`（cs:249–278）：按 `SettingsViewModel.AvailablePresets` 现场构造 `ContextMenu`，菜单项调 `vm.AddProfileFromPreset(p)`（cs:269），再赋给 `AddProfileButton.ContextMenu`（cs:275–277）。
- `OnProfileSelectionChanged`（cs:280–286）、`OnApiKeyPasswordChanged`（cs:288–299）、`OnSaveButtonClick`（cs:301–304）。
- `internal void FlushFocusedBinding()`（cs:306–320）按名字处理七个文本框（cs:308–314）：`NetworkUrlTextBox`、`NetworkUserNameTextBox`、`OcrTimeoutTextBox`、`ScreenshotMaskOpacityTextBox`、`HistoryMaxEntriesTextBox`、`SelectionTimeoutTextBox`、`SelectionProcessesTextBox`。
- 密码同步：`SyncNetworkPassword`（cs:333–338）、`OnNetworkPasswordChanged`（cs:339–342）、`SyncOcrPasswords`（cs:347–356）、`OnOcrPasswordChanged`（cs:357–361）。
- 拖动排序：cs:363–366、`OnProfilesListMouseMove`（cs:368–397，**从行内任意位置都能起拖**）、`OnProfilesListDragOver`（cs:399–410）、`OnProfilesListDrop`（cs:412–437）、`FindAncestor<T>`（cs:439–457）。
- 合计 19 个元素被代码隐藏按名字引用：7 个布局名（`TranslationLayoutGrid`、`ProfilesPane`、`DetailsPane` 与四个行列定义）、`AddProfileButton`、`ApiKeyPasswordBox`、另外三个密码框（`NetworkPasswordBox`、`OcrApiKeyPasswordBox`、`OcrSecretKeyPasswordBox`）、上面七个文本框。这些元素**不得**放进 `DataTemplate` / `ControlTemplate`（R1）。

### 2.5 「翻译服务」页（xaml:443–862）

- 行列定义：`ProfilesColDef` :458、`DetailsColDef` :459、`ProfilesRowDef` :462、`DetailsRowDef` :463。
- 列表侧 `ProfilesPane`（:467–545，`IsEnabled="{Binding CanEdit}"`）：
  - 工具条 `WrapPanel`（:478）：`AddProfileButton` :481–488、`MoveUpProfileButton` :491–496、`MoveDownProfileButton` :497–502、`DeleteProfileButton` :503–507（测试把后三个强转成 `Wpf.Ui.Controls.Button`）。
  - `ProfilesListBox`（:511–544）：`SelectedItem="{Binding SelectedProfile, Mode=TwoWay}"`（:514）、五个事件处理（:517–521）、`ItemTemplate`（:522–543），其中启用复选框 :529–531 没有内容。
- 详情侧 `DetailsPane`（`ScrollViewer`，:548–861）：内层 `StackPanel` 的 `DataContext="{Binding SelectedProfile}"`，`IsEnabled` 绑到窗口的 `DataContext.CanEdit`（:554–555），`SelectedProfile` 为空时折叠（:556–564）。详情面板靠 `DataContext` 取值（R8），所以进详情前必须先让该项成为选中项。
  - 卡 1（:567–667，标题 `UiText.Text_e8df05872569`，:569）：`DisplayNameTextBox` :577（`UpdateSourceTrigger=LostFocus`）；类型 :580–587；BaseUrl :589–596；警告 :599–604；模型文本框加 24 宽下拉 :606–624；按钮 `SettingsListDraftModels` :628–630、`SettingsTestDraft` :631–633；状态文字 :637–665。
  - 卡 2（:670–697，标题 `Text_a943c0367b80`）：凭据下拉 :674–684；`ApiKeyPasswordBox` :692–694，位于 `Grid IsEnabled={Binding IsReplaceCredential}`（:686）内。
  - 卡 3（:700–746，标题 `Text_9541ee3d4b15`）：超时 :704–711、最大令牌数 :713–720、温度 :722–729、提示词 :731–744。
  - `AdvancedOptionsExpander`（:749–859）：OpenAI 面板 :752–811（复选框 :764 / :767 / :770、额外请求头 :806–810）；Anthropic 面板 :814–857（effort 字面量 :826–844、复选框 :854–856）。
- 预设共 10 个（`src/Iris.Core/Settings/ProviderProfiles.cs:158–170`，`TranslationProviderPresets.All`）；设计文档写「四个预设」，与代码不符，以代码为准，完成报告里提一句。

### 2.6 OCR 页（xaml:180–286）

- 整页是一个纵向 `StackPanel`（:191–285）：标题 :192、说明 :193。
- 可编辑容器（:194–278，`IsEnabled={Binding CanEdit}`）：
  - 六个页面级设置（:195–209）：`OcrPrimaryCombo` :196、`OcrFallbackCombo` :198、`OcrLayoutCombo` :200、`OcrLanguageHintTextBox` :202、`OcrAutoCopyCheck` :203、`OcrSilentNotifyCheck` :206；`OcrSelectionError` :209。
  - 分隔线 :210；服务小标题 :211；两个添加按钮（原生 `Button`、没有名字）：`AddBaiduOcrCommand` :213、`AddWindowsOcrCommand` :214。
  - `OcrProfilesListBox`（:216–230，`MaxHeight=120`，`ItemContainerStyle` 没写 `BasedOn`，:217–221）；`DeleteOcrProfileButton` :231。
  - `OcrDetailsPanel`（:232–277，`DataContext="{Binding SelectedOcrProfile}"`）：`OcrNameTextBox` :243、`OcrEnabledCheck` :244、`OcrTimeoutTextBox` :248、`OcrBaiduOptionsPanel` :249–262（`OcrModeCombo` :251、`OcrDirectionCheck` :252、`OcrParagraphCheck` :255、`OcrLanguageTypeTextBox` :259、`OcrBaseUrlTextBox` :261）、`OcrWindowsOptionsPanel` :263–266（`OcrLanguageTagTextBox` :265）、`OcrCredentialCombo` :268、凭据说明 :269、`OcrApiKeyPasswordBox` :272、`OcrSecretKeyPasswordBox` :274、`OcrProfileError` :276。
- 测试区在可编辑容器**之外**（:279–284）：`OcrTestButton` :280、`OcrCancelTestButton` :281、`OcrTestResultText` :283（绑定 `SelectedOcrProfile.TestMessage`）、说明 :284。取消测试按钮必须继续留在可编辑容器之外（保存期间仍可取消）。
- 若把 `OcrTestResultText` 挪进 `OcrDetailsPanel`，它的绑定路径要随 `DataContext` 改写（`B2-services-ocr-styles.md` B2-8）。

### 2.7 视图模型的选中语义

| 项 | 翻译服务 | OCR |
|---|---|---|
| 属性 | `SelectedProfile`（VM:332） | `SelectedOcrProfile`（VM.Ocr:49） |
| 写入被忽略的条件 | `IsSaving` 时（VM:337） | `!CanEdit` 时（VM.Ocr:52）——只读模式下选不了别的项 |
| 写入的副作用 | VM:308–330 | 旧项 `InvalidateTest()`（VM.Ocr:82） |
| 新增后 | 选中新项（VM:744–745） | 选中新项（VM.Ocr:94） |
| 删除后 | 选相邻项（VM:770–777） | 选第一项（VM.Ocr:101） |
| 保存 / 重载后 | 按 `Id` 找回原项（VM:604–606） | **跳到第一项**（VM.Ocr:130–134、:153） |
| 通知 | 只在引用变化时通知 | 多发（VM.Ocr:77、:156） |

- `AddProfileFromPreset` 在 `!CanEdit` 时静默返回（VM:736）；`AddOcrProfile(kind)` 是公开方法（VM.Ocr:86–95）。
- 草稿的 `Id`：`TranslationProfileDraftViewModel.cs:159`、`OcrProfileDraftViewModel.cs:14`；`IsNew`：`:162`、`:20`。名字属性分别是 `DisplayName`（翻译）与 `DisplayName.Value`（OCR）。
- `CanEdit`（VM:365）包含 `!IsAnyRecording`；`OnPropertyChanged([CallerMemberName])`（VM:2850）；`SettingsViewModel` 是 `internal sealed partial class`，已有多个 partial 文件。

**已核实（协调方 2026-11-17 直接读源码，HEAD `dc08c48`）**——下面四条是事实，不再是待核实项：

1. `IsSaving` 是**公开属性、带变更通知、可直接绑定**：`src/Iris.App/Settings/SettingsViewModel.cs:493–506`（`private set`；值变化时依次 `OnPropertyChanged()`、`OnPropertyChanged(nameof(CanEdit))`、`RaiseAllCommandsCanExecute()`）。所以 `IsSaving` 变化时 `CanEdit` 也发通知。
2. `IsReadOnly => _settingsService.IsReadOnly`（`SettingsViewModel.cs:364`）；`CanEdit => !IsReadOnly && !IsSaving && !_disposed && !_isStopped && !IsAnyRecording && !HasFailedRecordingCleanup`（`:365`）。`OnPropertyChanged(nameof(CanEdit))` 另见 `:1042`、`:1111`、`:2055`、`:2113`、`:2212`。由此：`CanEdit` 为假不等于只读——保存中、录制快捷键中、录制清理失败时也为假；保存中 `CanEdit` 必为假。
3. `SelectedProfile` 的 setter（`SettingsViewModel.cs:332–340`）：`if (IsSaving) return; SetSelectedProfileInternal(value);`——**只读时不拒收**，只在保存中拒收。
4. `SelectedOcrProfile` 的 setter（`SettingsViewModel.Ocr.cs:52`）：`set { if (CanEdit && (value == null || _ocrProfiles.Contains(value))) SelectOcrProfile(value); }`——**`CanEdit` 为假时拒收**（只读、保存中、录制中都拒收）。

### 2.8 阶段 1 主题层现状与缺口

- 合并顺序（`src/Iris.App/App.xaml:8–14`）：`ThemesDictionary` → `ControlsDictionary` → `Themes/Iris.Light.xaml`（:11）→ `Themes/Iris.Styles.xaml`（:12）→ `Resources/EditingMenus.xaml`。
- `ThemeService.ApplyIrisThemeDictionary(ApplicationTheme theme)`：`internal static`，`src/Iris.App/Services/ThemeService.cs:241–269`（调用点 :212，名字表 :235），就地替换 Iris.Light / Iris.Dark 那一项。
- `Themes/Iris.SolidFallback.xaml`（20 行）目前**哪里都没合并**；它的注释（:5–11）写明需要深色版本，并且必须排在主题字典之后合并。
- `Themes/Iris.Styles.xaml`（268 行）共 19 个键，在 `Themes/` 之外**零使用**：

| 键 | 行 | 值 / 说明 |
|---|---|---|
| `IrisCardCornerRadius` / `IrisRowCornerRadius` / `IrisTagCornerRadius` | :9 / :10 / :11 | 8 / 6 / 4 |
| `IrisCardPadding` / `IrisRowPadding` / `IrisRowGap` | :12 / :13 / :14 | 16 / 16,12 / 0,0,0,8 |
| `IrisPillCornerRadius` / `IrisCardGap` / `IrisControlGap` | :20 / :21 / :22 | 12 / 0,0,0,12 / 12,0,0,0 |
| `IrisCard`（`Border`） | :27–33 | |
| `IrisRowCard`（`Border`，`BasedOn IrisCard`） | :36–39 | |
| `IrisRowTitle` / `IrisRowDescription` / `IrisSectionHeader` | :41–46 / :48–53 / :56–58 | |
| `IrisTag` / `IrisTagText` | :61–67 / :69–72 | |
| `IrisSwitchCheckBox` | :81–198 | 开关 40×20 **在左**，`ContentPresenter` 在右、左边距 8，`Content` 为空时该边距收起（:113–118）；无三态、无动画、不设 `AutomationProperties.Name` |
| `IrisIconButton`（`ui:Button`） | :205–258 | 32×32，只画 `Icon`；字符串 `Content` 用作 `ToolTip` 与 `AutomationProperties.Name` |
| `IrisNavItem`（`Border`） | :261–267 | `Padding` 10,8、`Margin` 0,0,0,2；注释 :260 说选中态由代码隐藏施加 |

- 颜色键（每个字典 4 个；Iris 颜色画刷**必须**用 `DynamicResource`）：

| 键 | `Iris.Light.xaml` | `Iris.Dark.xaml` | `Iris.SolidFallback.xaml` |
|---|---|---|---|
| `IrisAcrylicCardFillBrush` | `#66FFFFFF`（:6） | `#1AFFFFFF`（:3） | `#FFFFFFFF`（:16） |
| `IrisAcrylicCardStrokeBrush` | `#14000000`（:7） | `#1FFFFFFF`（:4） | `#FFE0E0E0`（:17） |
| `IrisAcrylicRowFillBrush` | `#4DFFFFFF`（:8） | `#14FFFFFF`（:5） | `#FFF9F9F9`（:18） |
| `IrisAcrylicRowStrokeBrush` | `#0F000000`（:9） | `#19FFFFFF`（:6） | `#FFE5E5E5`（:19） |

  Row 那一对目前没有使用者。
- 样张变量（`A-requirements.md` A5.4）：`--m-tint` 浅 `rgba(243,243,243,.70)` / 深 `rgba(36,36,36,.68)`；`--m-card` 浅 `rgba(255,255,255,.60)` / 深 `rgba(255,255,255,.06)`；`--m-subtle` 浅 `rgba(0,0,0,.05)` / 深 `rgba(255,255,255,.07)`；`--m-divider` 浅 `rgba(0,0,0,.08)` / 深 `rgba(255,255,255,.09)`。样张导航宽 176、项高 36；选中 = subtle 底 + 左侧强调色短线（宽 3、高 16、圆角 2）；样张里导航与底栏都没有不透明底；底栏高 60、上沿一条分隔线。
- 样式缺口（`B2-services-ocr-styles.md` B2-7.d）：列表行容器样式（悬停 / 选中 / 按下 / 焦点 / 禁用）、右箭头、拖动把手、面包屑、页标题、状态文字、行内布局、纵向行变体、自由排布的动作卡、空列表占位。
- **已用反射在 WPF-UI 4.3.0 上核实存在的 `SymbolRegular` 成员**（B2-7.e）：`ChevronRight12/16/20/24`、`ChevronLeft20/24`、`ArrowLeft20/24`、`MoreHorizontal16/20/24`、`ReOrderDotsVertical16/20/24`、`Delete16/20/24`、`ArrowUp16/20/24`、`ArrowDown16/20/24`、`Add16/20/24`、`Copy16/20/24`、`Edit20/24`、`Dismiss16/20/24`、`Checkmark16/20/24`、`Warning16/20/24`、`Info16/20/24`、`PlugConnected20/24`、`Play16/20/24`、`Beaker20/24`、`Key20/24`、`Options20/24`、`Settings20/24`、`Translate20/24`、`Keyboard20/24`、`ScanText20/24`、`Globe20/24`、`History20/24`、`CursorClick20/24`、`Screenshot20/24`、`ScreenCut20`、`Cut20/24`、`Server20/24`、`Cloud20/24`。清单之外的成员一律按「未核实」对待。
- 类型继承（同上）：`ui:Button : Button`；`ui:Card : ContentControl`；`ui:CardExpander : Expander`；`ui:ToggleSwitch : ToggleButton`（不是 `CheckBox`）；`ui:PasswordBox : ui:TextBox : TextBox`（不是原生 `PasswordBox`）；`ui:CardAction : ButtonBase`。
- 阶段 1 计划里已实测、本阶段仍然适用的：`ExtendsContentIntoTitleBar=false` 加背板会抛异常；`ApplicationThemeManager.Apply(theme, None)` 会重置窗口 `Background` 并剥掉亚克力，材质服务必须重新施加；内容从 Y=48 开始；`ui:SymbolIcon` 用随包的 FluentSystemIcons 字体；标记写法 `{ui:SymbolIcon SymbolRegular.Copy24}`；强调色画刷要用 `DynamicResource`；`Themes/*.xaml` 新文件不用改 csproj；`Iris.App.csproj` 的项目引用集合被 `tests/Iris.ArchitectureTests/DependencyRulesTests.cs:86` 钉住。

### 2.9 测试约束

测试都在 `tests/Iris.App.Tests/` 下，全部跑在共享 STA 线程（`TestSupport/WpfTestRunner.cs`）。**没有任何测试用 `x:Name` 生成的字段**，全是字符串 `FindName("…")` 加强转——改名、换控件类型、把控件挪进模板都只会在运行时变红，编译期看不出来。

**(a) 必须保持的名字与类型**

| 名字 | 必须保持的类型 / 条件 | 依据 |
|---|---|---|
| `NavOcr`、`NavScreenshot`、`NavNetwork`、`NavHistory` | `Border`（`NavScreenshot` 被强转） | `OcrSettingsRenderingTests.cs:75` |
| `NetworkPageScroll`、`HistoryPageScroll`、`OcrPageScroll`、`ScreenshotPageScroll`、`GeneralPageScroll`、`SelectionScrollViewer` | `ScrollViewer` | `HistorySettingsUiTests.cs:17`、`OcrSettingsUiTests.cs:26,29` |
| `NetworkUrlTextBox`、`HistoryMaxEntriesTextBox`、`OcrTimeoutTextBox`、`OcrBaseUrlTextBox`、`ScreenshotMaskOpacityTextBox`、`MouseDragTextBox`、`MouseHideTextBox`、`MouseExcludedTextBox`、`SelectionTimeoutTextBox`、`SelectionProcessesTextBox`、`DisplayNameTextBox` | `TextBox`（所以不能换成 `ui:NumberBox`） | 各 UI 测试强转 |
| `NetworkPasswordBox`、`OcrApiKeyPasswordBox`、`OcrSecretKeyPasswordBox`、`ApiKeyPasswordBox` | 原生 `PasswordBox` | `OcrSettingsUiTests.cs:92,97,101,202`、cs:127 |
| `NetworkCancelButton`、`OcrTestButton`、`OcrCancelTestButton`、`OcrRetryCleanupButton` | 按钮；`OcrRetryCleanupButton` 自己带 `Visibility` | `OcrSettingsUiTests.cs:142,151,179` |
| `OcrProfilesListBox` | `ListBox` | `OcrSettingsRenderingTests.cs:38` |
| `UiLanguageCombo` | `ComboBox` | `LocalizationTests.cs:44,161` |
| `CheckForUpdatesCheckBox` | `CheckBox`，且**不在模板里**（测试取它的 `.Parent`） | `UpdatePreferenceUiTests.cs:74`、`UpdateRenderingTests.cs:89` |
| `AddProfileButton` | 可见、可用、`ActualWidth > 70`、可命中、`ContextMenu` 的 `MenuItem.Tag` 是预设 | `LocalizationTests.cs:238–268` |
| `MoveUpProfileButton`、`MoveDownProfileButton`、`DeleteProfileButton` | 现状强转 `Wpf.Ui.Controls.Button`（`SettingsWindowTranslationUiTests.cs:355–357`）——这段在待重写的用例里 | D2 |
| `DetailsPane` | `ScrollViewer`（:417） | 同上 |
| `AdvancedOptionsExpander` | `Expander`（:420；`ui:CardExpander` 继承 `Expander`，可用） | 同上 |
| `StatusMessageTextBlock` | `TextBlock` | D4 |
| 四个行列定义 | `SettingsWindowTranslationUiTests.cs:329–332` 引用——随用例重写一并删除 | D2 |

- `LocalizationRenderingTests.cs:71` 按 `NavGeneral` / `NavTranslation` / `NavHotkeys` / `NavSelection` 找导航项（现在没有这四个名字，用的是 `as` 加 `?.` 所以不红）——**补上这四个名字是兼容的**。
- 不得新增名为 `CloseButton` 的元素（`HistoryRenderingTests.cs:96`）。
- `NetworkSettingsFoundationTests.cs:85`：文字为 `SettingsFutureReadOnly` 的 `TextBlock` 恰好 1 个——只读提示只能留在底栏。
- `MouseRenderingTests.cs:68–74`：按 `Content` 字符串 `.Single` 找六个底栏按钮，要求可见且落在 600×440 内。
- `LocalizationTests.cs:270–285`：`UiText.SelectionHotkeyDescription` 必须是一个独立、未截断的 `TextBlock`。
- 详情不得做成新的页面键（测试用到的页面键全集就是八个）；所有夹具都是「设页面键 + `Show()` + `UpdateLayout()`」后**同步**取控件，所以页面内容不得延迟加载，也不得加过渡动画。

**(b) 测试宿主**

- `WpfTestRunner.cs:223–250` 自建 `Application` 并按 `App.xaml` 的顺序合并字典（`AddIrisDictionaries`，:242–249）。**凡是在 `App.xaml` 里新合并的字典都要在这里同步**；守卫是 `IrisThemeDictionaryTests.cs:206–215`。
- `UpdateRecoveryRenderingTests.cs:41–45` 会整体替换 `app.Resources`。
- 不并行的集合：`ScreenshotRendering`、`Localization`、`MouseSelectionRendering`、`ThemeServiceDispatcher`、`IrisThemeResources`；另有 `AppShutdownerSharedState`。

（上表「依据」栏里的 D2、D4 指 `D-tests.md` 的小节号。）

**(c) 必须重写或改动的既有用例**

- `Settings/SettingsWindowTranslationUiTests.cs`（466 行，7 个方法、10 条用例，没有 `[Collection]`）：
  - 用例 1–6 必须保持绿，**不改**：用例 1 `WindowDimensions_MatchesDefaultAndMinimumSpecifications`（65–80）钉住 760 / 520 / 600 / 440；用例 3 钉住「新增后选中新项」（:129）；用例 4 `ProfileSelection_ClearsApiKeyPasswordBox`（135–171）不 `Show` 就取 `ApiKeyPasswordBox`；用例 6 `UnfocusedInput_FlushesFocusedBinding_BeforeSave`（207–250）要求 `DisplayNameTextBox` 在树里、`LostFocus` 更新、**没有任何进入详情的动作**。
  - 用例 7 `Layout_AtSpecifiedSizeAndTheme_AllControlsAreUsableAndNotClipped`（252–446，四条 `InlineData`）**必须重写**，分块如下：

    | 块 | 行 | 现在断言 | 处理 |
    |---|---|---|---|
    | 0 | 313–326 | 资源对比度 | 保留 |
    | 1 | 329–351 | 四个行列定义、680 分栏 | 删除，改成导航宽度断言加列表 / 详情可见性断言 |
    | 2 | 354–371 | 工具条按钮（`ActualWidth > 40`） | 改在列表态断言 |
    | 3 | 374–382 | 输入框宽度 ≥ 150 | 改在详情态断言 |
    | 4 | 385–397 | 按 `Content` 字符串找按钮、不被裁 | 改在详情态断言 |
    | 5 | 400–414 | 底栏（保存 / 取消 ≥ 70） | 保留 |
    | 6 | 417–433 | `DetailsPane` 可滚动、展开器 | 改在详情态断言 |
    | finally | 435–444 | 只还原 WPF-UI 字典主题 | 补上还原 Iris 字典（见 (e)） |

- `Settings/SettingsDraftAndSaveRegressionTests.cs`：`:515–518`（不 `Show` 取 `ApiKeyPasswordBox`）与 `:1792–1808`（`Scenario8_RealFocusedTextBox_…`，`Show` 之后不导航、不进详情，直接对 `DisplayNameTextBox` 设焦点再保存）——两条都要求详情控件在**列表态下也在树里且有名字**。
- `Settings/OcrSettingsRenderingTests.cs`（128 行，产出 42 张图）：详情态截图前必须加一步「进入详情」；强转 :38 `ListBox`、:75 `Border`；保存按钮 `.Single`；每个可见 `TextBox` 的右边缘 ≤ 窗口宽。
- `Settings/NetworkSettingsRenderingTests.cs`：每个可见 `Button` 的右边缘 ≤ 窗口宽。
- `Settings/SettingsHotkeysIntegrationTests.cs`：W04 `W04_OffScreenRendering_EightVariants_LightDark_DefaultMinSize_100And150Scale_NoClipping`（1507–1711）按序号取录制框、取第一个内层 `Border`、取消 / 保存右边缘 ≤ 宽度 + 2、录制框边框处亮度（浅色 > 130，:1673；深色 < 120，:1677）；W01（909–989）窗口级 Esc 在录制期间不得关窗；W02（:1049–1057）；W04B（1713–1783）重试按钮自身的 `Visibility`（:1762、:1775）。
- `Localization/LocalizationRenderingTests.cs:224–225` 取「宽度大于窗口 25%」的 `ScrollViewer`（760 宽时阈值 190，导航 176 小于它，不会误取）；`:234` 每页最多截 24 张。

**(d) 金丝雀测试**

- `Settings/SelectionSettingsRenderingTests.cs`（87 行）：取像素 (150, 50) 的 R 分量，深色 < 120（:57）、浅色 > 130（:58）；它直接调 `ApplicationThemeManager.Apply`（:30），不经 `ThemeService`。导航变成 176 宽后，x=150 **落在导航里**而不是内容区——导航去掉不透明底、叠着色层之后这条的结果会变，执行骨架任务时必须先算再跑，必要时改取点（属于「改过的既有测试」，要进偏差清单）。

**(e) 主题字典泄漏**

- 多个渲染测试切主题时绕过 `ThemeService`，只换了 WPF-UI 字典、没换 Iris 字典：`SettingsHotkeysIntegrationTests.cs:1525–1530`、`SelectionSettingsRenderingTests.cs:30`、`SettingsWindowTranslationUiTests.cs` 用例 7；`UpdateRenderingTests.cs:52–54`、`HistoryRenderingTests.cs:86–89`、`LocalizationRenderingTests.cs:240–243` 则在窗口级加 WPF-UI 字典而不加 Iris 字典（这三处只出证据图、无断言）。
- 后果：深色 WPF-UI 字典配浅色 Iris 字典。行卡片一上线，W04 的深色亮度断言按读代码推算会落在约 121–129（要求 < 120），**很可能变红**。
- Iris 字典的状态会在测试之间泄漏，所以「分组跑绿」不等于「全量跑绿」。
- 既有先例：`Themes/IrisThemeDictionaryTests.cs:37,41,220` 的 `RestoreLightDictionary()`。
- **2026-11-23 补查（C8）**：`grep -rn 'ApplicationThemeManager.Apply' tests/Iris.App.Tests --include=*.cs` 在 `dc08c48` 上共 24 行、15 个文件（其它测试项目 0 行）。上面六个文件占 9 行；另有**三个渲染设置窗口的夹具同样在应用级直接切 WPF-UI 主题、不动 Iris 字典、跑完不还原**，上面漏了：`Settings/OcrSettingsRenderingTests.cs` 的 `Prepare`（:88–93，应用级 :90、窗口级 :91）、`Settings/NetworkSettingsRenderingTests.cs` 的 `Prepare`（:103–107，应用级 :105）、`Settings/NetworkSettingsFoundationTests.cs:78`。三个类都在不并行的集合 `ScreenshotRendering` 里。其余 11 行（6 个文件）不渲染设置窗口或只是注释，逐行清单与理由见任务 3 Step 1。另一种直接切法——改应用级 `ThemesDictionary.Theme`——也 grep 过：只有 W04（:1525）、用例 7（:262）与 `Translation/TranslateWindowUiTests.cs:87`（翻译窗口）。

### 2.10 本地化流程

- 新增一条界面文字要同时改 **四处**：
  1. `src/Iris.App/Resources/Strings.resx`（追加在 `</root>` 之前）；
  2. `src/Iris.App/Resources/Strings.en-US.resx`（键顺序与上一个一致）；
  3. `build/localization/app-catalog.json`（值与 resx 完全一致）；
  4. 一个 `src/Iris.App/UiText*.cs` partial：`public static string X => global::Iris.App.Resources.AppResources.Get("X");`——必须是 `=>` 属性，不能是 `static readonly` 字段。
- 键缺失时**不会报错**，界面上静默显示「未知错误」那句话；没有任何测试检查两份 resx 是否对齐。所以每加一条都要靠审计脚本兜底。
- 审计：先做 Release 构建，再跑 `& build/audit-localization.ps1 -EvidenceDirectory artifacts/localization-audit`。
- 审计按「路径 + 全文」给中文注释登记指纹（`build/localization/exceptions.json`，42 条对应 `SettingsWindow.xaml` 里的 43 行中文注释）。既有中文注释**只能原样留着或整条删除**，不能改字，不能搬到别的文件。整条删除后 `exceptions.json` 里是否会留下过期登记、脚本是否为此报错——**未确认**（见 2.12）。
- 可直接复用的既有字符串：四个分组名 `Text_e8df05872569`（基本信息 | Basic information）、`Text_a943c0367b80`（凭据管理 | Credentials）、`Text_9541ee3d4b15`（请求参数 | Request parameters）、`SettingsProfileAdvanced`（高级选项 | Advanced options）；面包屑父级 `SettingsNavTranslation`（翻译服务 | Translation providers）、`OcrPageTitle`（OCR 文字识别 | Text recognition (OCR)）；菜单项 `SettingsMoveUp`、`SettingsMoveDown`、`SettingsDeleteProfile`（删除服务 | Delete provider）、`OcrDelete`（删除自定义服务 | Delete custom provider）。

### 2.11 风险编号（任务清单里按编号引用）

| 编号 | 风险 | 对策 |
|---|---|---|
| R1 | 有名字的元素进了模板就脱离窗口名称域；换控件类型会让强转失败 | 行卡片逐行手写，不用模板包住有名字的控件；类型按 2.9(a) 保持 |
| R2 | 「翻译服务」页的布局由 C# 驱动（`ApplyAdaptiveLayout`） | 见 S0-9 |
| R3 | 录制框鼠标移出即取消录制，且有 `MinWidth=360` | 见 S0-3 |
| R4 | `IsEnabled={Binding CanEdit}` 的容器，以及故意放在容器外的元素 | 每页改造后逐个核对「容器内 / 容器外」归属不变。**两个钻取页例外**（【用户】U7，2026-11-17）：「翻译服务」页与 OCR 页的容器按 S0-8、S0-10 的表拆分，核对口径改成「与那两张表一致」；故意放在容器外的元素（OCR 测试区等）仍须在任何冻结容器之外 |
| R5 | 一个元素只能有一个 `Style`；现有内联样式里带触发器 | 内联样式改成 `BasedOn` 主题层样式，触发器留在内联样式里 |
| R6 | 开关样式是「开关在左、文字在右」，目标是「标题在左、开关在右」 | 见 S0-5 |
| R7 | 中文注释不能改字、不能搬动 | 只整条删除；新注释英文 |
| R8 | 详情面板靠 `DataContext` 取选中项 | 进详情前先写选中并核实 |
| R9 | Esc 与 Enter 的窗口级语义（取消 / 默认按钮） | 见 S0-9 |
| R10 | 导航与底栏的不透明底会盖住背板 | 见 S0-6 |
| R11 | 不是每项设置都放得进横向行 | 见 S0-4 的纵向变体 |
| R12 | 各页内边距、`MaxWidth`、标题字号不一致 | 统一到主题层的键 |
| R13 | 文件格式陷阱（超长物理行、行号漂移） | 按行范围**从文件末尾往前**改；每个任务开工先重新 grep 行号 |
| R14 | 窗口级共享资源 | `WrappingCheckBoxContent` 等 12 处使用全部清掉之后才能删；`BoolToVis` 保留 |
| R15 | `ui:Card` 换成 `Border` 后，原来的本地 `Padding` / `Margin` 会叠加 | 换的同时删掉本地 `Padding` / `Margin` |
| R16 | 每页自己的 `ScrollViewer` | 保留，不合并成一个 |

### 2.12 写计划时补查的事实（笔记缺、纯色回退的裁决必须用到）

以下每条都是**写计划时补查**：对 `src/Iris.App` 做了一次 grep，并读了 `WindowMaterialService.cs:26–107`、`ThemeService.cs:228–271` 两小段。

- `WindowMaterialService.IsAcrylicSupported`（`WindowMaterialService.cs:30`）= 系统版本号 ≥ 22621 **且** `WindowBackdrop.IsSupported(WindowBackdropType.Acrylic)`；两个条件都由构造函数注入的委托提供（:24），测试可以直接造出「不支持」的实例。
- 不支持时 `ApplyAcrylic`（:41–44）、`Attach`（:72–75）、`RefreshOpenWindows`（:91–94）都直接返回，窗口保持 WPF-UI 默认的不透明背景。
- `RefreshOpenWindows`（:89–106）遍历 `Application.Windows` 里所有 `ExtendsContentIntoTitleBar` 为真的 `FluentWindow`。
- 高对比度主题下 WPF-UI 自己会否决亚克力（`WindowMaterialService.cs:52–57` 的注释），但 `IsAcrylicSupported` **不反映**这一点。
- `ThemeService` 持有可空的 `_windowMaterialService`（`ThemeService.cs:20`，构造函数可选参数 :33，`HasWindowMaterialService` :75）；应用主题的路径上先 `ApplyIrisThemeDictionary(theme)`（:212），后 `_windowMaterialService?.RefreshOpenWindows()`（:224）。
- `ApplyIrisThemeDictionary`（:241–269）：按 `Source` 结尾匹配 `Iris.Light.xaml` / `Iris.Dark.xaml`（名字表 :235），找到就**原位替换**，找不到就追加到末尾。`Application.Current` 为空时直接返回。
- `WindowMaterialService` 以单例注册（`src/Iris.App/Hosting/AppHostBuilder.cs:82`）；三个窗口各有一处 `Attach`：`SettingsWindowPresenter.cs:295`、`History/HistoryWindowSession.cs:30`、`Translation/TranslateWindowPresenter.cs:514`。
- `src/Iris.App` 的 C# 代码里没有任何地方提到 `SolidFallback`（与 2.8「哪里都没合并」一致）。

### 2.13 未确认、执行时须先核实

下列各条在笔记里就是「未确认」，或者笔记没覆盖。**凡是任务依赖其中一条的，该任务第一步就是核实它**；核实结果与本计划的假设不符时，按该任务写明的退路走，并记进完成报告的偏差清单。

1. 本计划对既有测试「会红 / 会绿」的全部预判都只是读代码推算，一条都没跑过。
2. 第 5 节各任务的 `--filter` 命令没有实际跑过（写法与阶段 1 计划里用过的 `--filter "FullyQualifiedName~…"` 相同）。
3. 把键盘焦点设到一个 `Visibility=Collapsed` 的元素上之后，`FlushFocusedBinding` 与 `LostFocus` 绑定是否还照常工作——关系到 `SettingsWindowTranslationUiTests` 用例 6 与 `SettingsDraftAndSaveRegressionTests` 的 Scenario8 能否不改就绿。
4. `ListBox` 默认 `KeyboardNavigation.TabNavigation=Once` 时，Tab 能否走到行内的「⋯」按钮与启用开关。
5. `Themes/` 下八个测试文件的具体内容（`IrisColorKeyTests`、`IrisStyleResourceTests`、`IrisStyleKeyResolutionTests` 是逐键硬编码还是遍历字典）——决定新增或改动主题层的键时要不要同步改它们。
6. `ui:TitleBar` 的实际高度（阶段 1 计划记的是内容从 Y=48 开始）。
7. WPF-UI 滚动条占不占布局宽度（S0-3 的宽度预算按占 12 算）。
8. 深色下 `ControlFillColorDefaultBrush` 的实际颜色值。
9. `ui:Button` 设了 `Icon` 之后 `Content` 字符串是否原样保留；`ui:SymbolIcon` 的可视树里有没有 `TextBlock`（会不会被「遍历全部 `TextBlock`」的测试扫到）。
10. `CancelCommand` 在保存期间能不能执行；`StatusMessage` 空的时候是空串还是 `null`。
11. `CurrentPageKey` 写入相同的值时是否发通知（S0-8 的「同页重复 `ShowAsync` 保留详情」依赖它不发）。
12. ~~`CanEdit` 是否包含 `!IsSaving`~~ **已核实（2026-11-17）：包含**（`SettingsViewModel.cs:365`，见 2.7「已核实」第 2 条）。保存中凡是绑 `CanEdit` 的容器与控件都不可用。
13. OCR 的删除命令对两个内置服务是否 `CanExecute == false`；OCR 列表可能为空时页面的表现。
14. 重载过程中 `SelectedProfile` 是否短暂变成 `null`。
15. 应用启动时 `ThemeService` 是否必定走到 `ThemeService.cs:212`（S0-7 依赖：回退字典在启动时就要合并）。
16. `build/localization/exceptions.json` 里登记的中文注释被整条删除后，审计脚本是否把「过期登记」当错误；`probe_exceptions.py` 是否检查这一点；`app.en.json` 是否还在被读取。
17. `LocalizationRenderingTests` 每页截图数离 24 张上限还有多远。
18. 有没有测试按 `Content` 文字找 `CheckBox`（笔记没列出；S0-5 去掉开关的 `Content` 之前先 grep `OfType<CheckBox>` 与 `FindVisualChild`）。
19. `TranslateWindowIntegrationTests` 里哪几条真的打开了设置窗口。
20. 服务名有没有长度上限（面包屑与列表行按「单行加省略号」处理，不依赖这一条）。
21. 阶段 1 完成报告的内存基线数字（笔记没读到）——收尾任务里在 `dc08c48` 上重新量。
22. Win10 / 低版本系统上回退的真实观感：本机是 Windows 11，**无法真机验证**，完成报告写「未验证」。
23. ~~（因 U7 新增）视图模型上表示「保存进行中」的现成属性是否可绑定~~ **已核实（2026-11-17）：`IsSaving` 是公开、带通知、可直接绑定的属性**（`SettingsViewModel.cs:493–506`，见 2.7「已核实」第 1 条）。S0-8 的列表冻结直接绑它。
24. ~~（因 U6 新增）`CanEdit` 变化时是否发 `PropertyChanged`~~ **已核实（2026-11-17）：发**（`IsSaving` 的 setter 里，另见 `:1042`、`:1111`、`:2055`、`:2113`、`:2212`；见 2.7「已核实」第 1、2 条）。`IsReadOnly` 取自 `_settingsService.IsReadOnly`（`:364`），它变化时由上述哪一处发通知没有逐处核对——T4-OCR 只依赖「`CanEdit` 由假变真时有通知」，任务 11 写测试时用保存结束这一条路径验证即可。
25. （2026-11-17，因 U7、C4 新增）`CanEdit` 为假时 `OcrProfilesListBox` 仍可点：写入被视图模型拒收（已核实，2.7 第 4 条）之后，列表控件的选中行是否自动读回（S0-10「只读模式」）；任务 11 核实并把 `SelectedItem` 的绑定模式与读回方式写清。
26. （2026-11-17，因 U8 新增）`AddBaiduOcrCommand` / `AddWindowsOcrCommand` 是否同步完成添加（S0-10 的 T3-OCR）；任务 11 核实。
27. （2026-11-17，因 U5 新增）历史窗口与翻译窗口的 XAML 根结构、既有像素断言：本计划没有查，以调查笔记 `E-tint-other-windows.md` 为准（任务 4B）。

---

## 3. 设计裁决（S0）

每条裁决后面的标记说明它的出处：

- 【设计】= 设计文档 / 规格 / 既有测试已经定死，照做；
- 【用户】= 第 1 节的用户裁决；
- 【计划裁决】= 用户、设计、规格都推不出来，由本计划定下，**必须写进完成报告的偏差清单**；
- 【待确认】= 另列入第 6 节「待用户确认」，这里给出默认做法，执行不因此停下。（2026-11-17 之后正文里已没有带这个标记的裁决：原来的几处都由用户裁决 U5–U8 取代；第 6 节只剩 Q5。）

引用的笔记小节：`A-…` = `A-requirements.md`，`B1-…` = `B1-window-structure.md`，`B2-…` = `B2-services-ocr-styles.md`，`D…` = `D-tests.md`；「设计 :N」= 设计文档第 N 行（经 `A-requirements.md` 转引）。

### S0-1 窗口尺寸

- **默认 760×520，最小 600×440，都不改。**【设计】依据：设计 :74；`SettingsWindowTranslationUiTests` 用例 1（65–80）把四个数钉死；A6.12 第 1 条。
- xaml:11–14 四个属性原样保留。`ui:TitleBar`（xaml:28–30）只保留现状，不加应用图标。【设计】（A7.2）

### S0-2 导航

**宽度**

- 窗口宽度 ≥ 680 时导航列宽 **176**；< 680 时 **48**（只剩图标）。【设计】设计 :76、:260。
- 两个数做成主题层的数值键 `IrisNavWidth`（176）、`IrisNavCompactWidth`（48），代码隐藏用 `FindResource` 取，不写死。【计划裁决】依据：A6.12 第 13 条「间距经主题资源」。
- 阈值沿用现有常量 `CompactLayoutThreshold = 680.0`（cs:18）。
- 给导航所在的列定义起名 `NavColumnDef`（现在是 xaml:34–37 里那个 `140`），由 `ApplyAdaptiveLayout` 写它的 `Width`（见 S0-9）。【计划裁决】
- 由此得出：**内容区最窄出现在窗口宽 680 时（680 − 176 = 504），不是 600 时（600 − 48 = 552）**。所有「最小尺寸下不裁切」的预算都要按 504 再算一遍（A7.2）。

**导航项结构**

- 八个导航项仍然是 `Border`，仍然用 `MouseLeftButtonUp` 进现有的 `OnNav*Click`，处理函数一行不改。【设计】设计 :75。
- 给没有名字的四个补名字：`NavGeneral`、`NavTranslation`、`NavHotkeys`、`NavSelection`。【计划裁决】依据：`LocalizationRenderingTests.cs:71` 已经按这四个名字找，补上是兼容的（2.9(a)）。
- 每个导航项的内联 `Style` 改成 `BasedOn="{StaticResource IrisNavItem}"`，原来那个绑定 `CurrentPageKey` 的 `DataTrigger` 留在内联样式里。【计划裁决】依据：R5。选中态继续由 XAML 触发器完成，**不写任何 C#**；`Iris.Styles.xaml:260` 那句「选中态由代码隐藏施加」的英文注释同步改掉。
- 选中态 = `SubtleFillColorSecondaryBrush` 底（现状，WPF-UI 的键）+ 左侧强调色短线。短线是导航项里的一个子 `Border`：宽 3、高 16、圆角 2、左对齐、垂直居中、画刷 `{DynamicResource AccentFillColorDefaultBrush}`；它自己带一个内联样式，默认 `Collapsed`，同一个 `DataTrigger` 命中时 `Visible`。短线叠在同一格里，不占布局宽度。【设计】样张 A5.4。画刷键名是**写计划时补查**的：仓库里已有同样写法（`HotkeyRecorderBox.xaml:33`、`:76`），所以不引入新键。
- 导航项内部是一个两列 `Grid`：第 0 列 `Auto` 放图标，第 1 列 `*` 放文字。文字 `TextBlock` 单行、`TextTrimming="CharacterEllipsis"`、左边距 12。
- `IrisNavItem` 的 `Padding` 从 `10,8` 改成 `8,8`。【计划裁决】理由：窄态下导航项可用宽度 = 48 − 6 − 6 = 36，图标 20，左右各 8 正好居中；高度 8 + 20 + 8 = 36，与样张的项高 36 一致。窄态下第 1 列被挤成 0 宽，文字自然不显示——**窄态不需要任何「隐藏文字」的代码**。
- 每个导航项始终设 `ToolTip` 与 `AutomationProperties.Name`，值都是该页导航文字的那条 `UiText`（宽态、窄态都设）。【设计】设计 :76。
- 导航顶部的标题 `TextBlock`（xaml:47–50）起名 `NavTitleText`，窄态下由 `ApplyAdaptiveLayout` 设为 `Collapsed`，宽态 `Visible`。【计划裁决】
- 导航项仍然不可聚焦（现状如此，键盘可达性不在本阶段范围）；完成报告列为遗留问题。【计划裁决】

**图标**（都在 2.8 的已核实清单里，尺寸统一用 20）

| 页面键 | `SymbolRegular` |
|---|---|
| `general` | `Settings20` |
| `translation` | `Translate20` |
| `hotkeys` | `Keyboard20` |
| `selection` | `CursorClick20` |
| `ocr` | `ScanText20` |
| `screenshot` | `Screenshot20` |
| `network` | `Globe20` |
| `history` | `History20` |

这张表同时是普通页**行图标的退路**（【计划裁决】C6，见 S0-4「图标」）：某一行找不到贴切的成员、或首选成员编译不过时，用该页在这张表里的图标。

**不透明底**：导航 `Border` 的 `Background`（xaml:43）删掉，见 S0-6。

### S0-3 快捷键录制框的宽度

- `HotkeyRecorderBox` 的行为与内部结构**一行不改**（鼠标移出取消录制、内联样式里的按钮文字与命令、第一个内层 `Border`）。只允许动 xaml:1000 上的布局属性。【设计】R3；W01、W04 守着。
- `MinWidth="360"`、`MaxWidth="500"`、`HorizontalAlignment="Left"` **保持不变**。【计划裁决】
- 录制框放在行卡片的**第二行、跨满卡片内宽**（S0-4 的纵向变体），不和标题挤在同一行。
- 宽度预算（最窄情形，窗口宽 680）：504（内容列）− 48（页内边距左右各 24，见 S0-4）− 32（`IrisRowPadding` 左右各 16）− 12（滚动条，**未确认**，见 2.13 第 7 条）= **412 ≥ 360**，余量 52。窗口宽 600 时是 552 − 48 − 32 − 12 = 460。
- 快捷键页内容 `StackPanel` 上的 `MaxWidth=600`（xaml:951）删掉，与其他页统一；`HorizontalAlignment=Left` 改成默认的拉伸。【计划裁决】R12。
- 退路：真机或 W04 显示 680 宽时录制框被裁，就把页内边距从 24 减到 16（只改 `IrisPagePadding` 一个键），不动 `MinWidth`；仍不够再报告，不擅自改 360。

### S0-4 行卡片的形态

**总则**

- 行卡片**逐行手写**：一个 `Border Style="{StaticResource IrisRowCard}"` 里放一个 `Grid`。不做成 `ControlTemplate` / `DataTemplate` / `UserControl`。【设计】设计 :77（不拆 `UserControl`）；R1（有名字的控件进模板就脱离窗口名称域）。可复用的只有主题层的样式键（S0-11），`Grid` 的行列定义每行照抄。
- 一项设置一张行卡片；行卡片之间的间距用 `IrisRowGap`。所有既有说明文字原样保留，一条不删、一条不新编。【设计】设计 :81；A6.12 第 11 条。
- 13 个 `ui:Card` 全部换成 `Border`（`IrisCard` 或 `IrisRowCard`），换的同时删掉原来写在 `ui:Card` 上的本地 `Padding` / `Margin`。【计划裁决】R15。
- 每页保留自己的 `ScrollViewer` 与名字（R16）；`ScrollViewer` 里第一层面板的 `Margin` 统一用新键 `IrisPagePadding`（`24,16,24,16`）。原来各页不一致的内边距、`MaxWidth` 一并删掉。【计划裁决】R12。
- `IsEnabled={Binding CanEdit}` 的容器照旧包住原来包住的那些行；原来故意放在容器外的元素改造后仍在容器外（R4；逐页清单见 2.3）。
- 标题与说明一律换行、不截断、不加省略号。`IrisRowTitle` / `IrisRowDescription` 是否已设 `TextWrapping="Wrap"` 在任务 1 核实，没设就在主题层样式里补。

**两种固定结构**（只有这两种，外加一种「动作卡」；不许临场发明第四种）【计划裁决】依据 R11、A7.2「横 / 纵两种固定变体」

1. **横向行**：`Grid` 三列 `Auto | * | Auto`。
   - 第 0 列：图标（仅普通页，见下）。
   - 第 1 列：`StackPanel`，里面依次是标题（`IrisRowTitle`）、说明（`IrisRowDescription`，没有就不放）、本项的校验错误文字（有就放，放在文字栈**最后**，保留原名字）。校验错误文字一律套 `IrisRowError`（【计划裁决】C5，S0-11）：`<TextBlock Style="{StaticResource IrisRowError}" Text="{Binding …}" Foreground="{DynamicResource …原来的键}"/>`——原来的 `x:Name`、`Text` 绑定、`Foreground` 画刷键都留在元素上（`IrisRowError` 不设前景色）；不在各行重复写「空则折叠」的内联样式。
   - 第 2 列：控件，`Margin="{StaticResource IrisControlGap}"`、`VerticalAlignment="Center"`。
   - 适用：开关、下拉框（`MinWidth="160"`）、数字文本框（`Width="120"`）、单行短文本框（`Width="220"`）。
2. **纵向行**：`Grid` 三列同上，两行 `Auto / Auto`。
   - 第 0 行与横向行相同（第 2 列可以放一个开关，也可以空着）。
   - 第 1 行：控件 `Grid.ColumnSpan="3"`、`Margin="0,8,0,0"`，从卡片内容区左缘起跨满。
   - 适用：多行文本框（`MouseExcludedTextBox`、`SelectionProcessesTextBox`、提示词、额外请求头）、快捷键录制框、网址类长文本（`NetworkUrlTextBox`）、一行里有两个以上控件的情形。
3. **动作卡**：`Border Style="{StaticResource IrisCard}"`，内部自由排布（状态文字、进度条、一排按钮放 `WrapPanel`）。只用于三处：常规页的更新状态区（xaml:353–370 里除开关以外的部分）、网络页的连通性测试区（xaml:153–165）、OCR 详情里的测试区。

**图标**【用户】U2

- 普通页（常规、快捷键、划词、截图、网络、历史）的每张行卡片第 0 列放一个 `ui:SymbolIcon`，套新样式 `IrisRowIcon`（20 大小、右边距 12、垂直居中、次要文字色）。动作卡不带图标。
- 「翻译服务」与 OCR 两页（列表行、详情字段、OCR 页顶部六个页面级设置）**都不带**行图标；第 0 列整列不写。OCR 页顶部六项不带图标是【计划裁决】：U2 列出的普通页不含 OCR。
- 取哪个图标：**逐行选**，不是整页所有行用同一个图标（【计划裁决】C6，2026-11-19）。
  - 每一行选一个语义贴切的 `SymbolRegular` 成员，20 号规格（名字以 `20` 结尾）。
  - **核实成员名**（口径照 C6，2026-11-23 协调方实测后修订）：写任务 5–9 的详细步骤时，每个要写进表里的成员名都必须先核实存在。
    - **为准的**：NuGet 缓存里 WPF-UI 4.3.0 的 `lib\net462\Wpf.Ui.dll` 的只读反射。Windows PowerShell 5.1 里：`$t = [Reflection.Assembly]::LoadFrom("$env:USERPROFILE\.nuget\packages\wpf-ui\4.3.0\lib\net462\Wpf.Ui.dll").GetType('Wpf.Ui.Controls.SymbolRegular'); [Enum]::GetNames($t) -contains '成员名'`。输出 `True` 才能写进步骤；`False` 不得写。（实测共 9235 个成员；net462 与应用实际引用的 net10 两份程序集出自同一版源码。）
    - **只作辅证的**：对应用实际引用的 `lib\net10.0-windows7.0\Wpf.Ui.dll` 做「按 NUL 切开后整串匹配」（`tr '\0' '\n' < Wpf.Ui.dll | grep -a -x -c '成员名'`）。命中 1 次说明存在；命中 0 次**不能**说明不存在——元数据字符串堆会把一个名字存成更长名字的后缀，实测 `History20` 在两份程序集里整串都是 0 次、只见于 `ChatHistory20`，而反射确认它存在。
    - **最终以构建为准**：能编译就用首选；编译报成员不存在就换成退路，并在完成报告里记一笔。
  - **退路**：一律是该页的**页面图标**，即 S0-2 图标表里该页的那一个（常规 `Settings20`、快捷键 `Keyboard20`、划词 `CursorClick20`、截图 `Screenshot20`、网络 `Globe20`、历史 `History20`）。S0-2 那张表因此身兼两职：导航图标，兼行图标的退路。找不到贴切成员的行，首选直接填页面图标。
  - **任务 5–9 各自的详细步骤里必须有一张「行 → 首选图标 → 退路图标」表**，本页每一张带图标的行卡片占一行，一行不漏；「范围与要点」里不写「本页各行都用 X」。
- 下表是撰写骨架时已经逐行选好的几行，**保留**，作为详细步骤那张表的起点：「首选」列有值的行，首选照抄；「首选」列是「—」的行，把「骨架备选」列的成员当首选。两种情形的**退路都填页面图标**——C6 的退路只有页面图标这一种，首选编译不过就直接换页面图标，不经过「骨架备选」。「骨架备选」列是骨架原来的「退路」列，C6 之后不再充当退路；留着只为记录这些成员已核实存在，详细步骤的撰写者认为首选不贴切时可以改选它作首选。表里没有的行，由该任务的详细步骤按上面的规矩逐行补。
- 快捷键页的行出自同一个 `DataTemplate`（xaml:965，`DataType` 是 `local:HotkeyItemDraftViewModel`），不能逐行手写图标；这一页的「逐行」= 逐个 `IrisAction`，做法见任务 9 的「范围与要点」。
- 核对记录：下表出现的十三个成员名，连同六个页面图标里表中没出现的四个（`History20`、`Screenshot20`、`Settings20`、`CursorClick20`），共十七个，已于 2026-11-23 按上面「为准的」那条命令（`lib\net462\Wpf.Ui.dll` 反射，9235 个成员）逐个核对，十七个全部输出 `True`。仍以 net10 那份构建通过为准。

| 页 | 行 | 首选（逐行选，构建时最终核实） | 骨架备选（成员已核实存在） |
|---|---|---|---|
| 常规 | 界面语言 | `LocalLanguage20` | `Globe20` |
| 常规 | 自动检查更新 | `ArrowSync20` | `ArrowDown20` |
| 常规 | 主题 | `Color20` | `Options20` |
| 常规 | 启动相关两项 | `Power20` | `Play20` |
| 常规 | 日志 | `DocumentText20` | `Info20` |
| 快捷键 | 每个快捷键 | — | `Keyboard20` |
| 划词 | 取词顺序、超时 | — | `Options20` |
| 截图 | 遮罩不透明度 | — | `Options20` |
| 网络 | 代理地址 | — | `Server20` |
| 网络 | 凭据方式、用户名、密码 | — | `Key20` |

**分组小标题与页标题**

- 原来写在 `ui:Card` 里的卡片标题改成卡片**上方**的 `TextBlock`，套 `IrisSectionHeader`；文字用原来那条 `UiText`，不新编。
- 每页顶部放页标题，套新样式 `IrisPageTitle`。已有页标题的页（如 OCR，xaml:192）沿用原文字；没有的页用该页导航文字的那条 `UiText`。**不新增字符串。**【计划裁决】
- **页级说明文字**（【计划裁决】C7，2026-11-19）：紧跟页标题的那一条页级说明**保留**，位置在页标题正下方、与页标题同一个父面板，在 `IsEnabled={Binding CanEdit}` 的可编辑容器**之外**。写法：`<TextBlock Style="{StaticResource IrisRowDescription}" Margin="{StaticResource IrisCardGap}" Text="{x:Static app:UiText.…原来那条}"/>`。**不新增键**，文字不改、不新编。
  - 原元素上的本地 `Margin` 换成 `IrisCardGap`（`0,0,0,12`，本地值盖过 `IrisRowDescription` 自带的 `0,3,0,0`）；与样式重复的本地 `TextWrapping`、`FontSize`、`Foreground` 删掉——换行、12 号字、次要文字色由 `IrisRowDescription` 给（`Iris.Styles.xaml:48–53`，2026-11-22 已读代码核实）。
  - 有页级说明的页（2026-11-22 已读 `SettingsWindow.xaml` 核实，共四页）：

    | 页 | 页标题 | 页级说明 | `CanEdit` 容器起点 | 归哪个任务 |
    |---|---|---|---|---|
    | 网络 | :131 | :132 `UiText.NetworkDescription`（现在没写 `Foreground`，套样式后变成次要文字色，这是 C7 带来的外观变化） | :133 | 8 |
    | OCR | :192 | :193 `UiText.OcrPageDescription` | :194 | 11 |
    | 截图 | :299 | :300 `UiText.ScreenshotSettingsDescription` | :301 | 5 |
    | 快捷键 | :953–956 | :957–960 `UiText.SettingsHotkeysDescription` | 本页没有 `CanEdit` 容器（见下） | 9 |

  - 历史、常规、划词三页的页标题后面**没有**页级说明（历史 :171 后面直接是复选框 :172；常规 :337–340 后面直接是卡片 :342；划词 :877 后面直接是 `SelectionErrorTextBlock` :878），不新编、不补。
  - 快捷键页的边角：页标题与页级说明现在**都在**第一层面板 `StackPanel IsEnabled={Binding CanAccessHotkeysTab}`（xaml:951）里面，这一页没有 `CanEdit` 容器。C7 的「可编辑容器之外」说的是 `CanEdit` 容器，对这一页不适用；照 R4「元素与 `IsEnabled` 容器的包含关系不变」处理——页级说明仍紧跟页标题、仍留在 :951 那个面板里，不往外挪。C7 原文没有单说这一页，交计划时向协调方说明。
  - 同一页既有页级说明又有页级错误文字时，从上到下是：页标题 → 页级说明 → 页级错误文字 → 第一张卡片。
- 页级错误文字（`SelectionErrorTextBlock`、`OcrSelectionError`）保持独立 `TextBlock`，位置在页标题之下、第一张卡片之上。

**「翻译服务」详情与 OCR 详情的字段**【计划裁决】

- 四个分组各一张 `IrisCard`（「高级选项」仍是 `Expander`，见 S0-8），分组名用 2.10 列的四条既有字符串，放在卡片上方。
- 组内字段沿用现在的「标签在上、控件在下」纵向排布，**不逐字段拆成行卡片**。理由：这些控件全都有名字且被代码隐藏与测试引用，结构动得越少越安全；设计 :97 只要求四个分组。

### S0-5 开关

- 控件类型仍是 `CheckBox`，套 `Style="{StaticResource IrisSwitchCheckBox}"`；**不换** `ui:ToggleSwitch`。【设计】设计 :82；`UpdatePreferenceUiTests.cs:74` 把 `CheckForUpdatesCheckBox` 强转成 `CheckBox`，而 `ui:ToggleSwitch` 继承的是 `ToggleButton`。
- 23 个 `CheckBox` 全部改成开关，12 个名字原样保留。
- **写法：开关不带 `Content`，放在行卡片第 2 列；标题由第 1 列的 `IrisRowTitle` 文字承担。**【计划裁决】理由：`IrisSwitchCheckBox` 是「开关在左、内容在右」（`Iris.Styles.xaml:81–198`），而目标是「标题在左、开关在右」（R6）；该样式在 `Content` 为空时会收起内容边距（:113–118），正好支持这种用法，不用改模板。笔记里另一种做法（保留 `Content` 但不画，A7.2 第 25 条）要改阶段 1 的模板，不采用。
- 原来写在 `CheckBox` 里的文字（三种写法：内容模板 12 处、子元素 10 处、无内容 1 处）一律搬到行标题 `TextBlock`，用**同一条** `UiText`。
- 每个开关必须设 `AutomationProperties.Name`，值是它那一行标题的同一条 `UiText`。【计划裁决】理由：没有 `Content` 之后读屏器否则读不出名字。列表行模板里的启用开关（xaml:529–531 那个，本来就没内容）也要设：用一条已有的「启用」类字符串，找不到就按 S0-12 新增。
- **行为变化（进偏差清单）**：以前点文字也能切换，改完以后只有开关本身可点。与 Windows 11 设置一致，接受。
- 快捷键页模板里的启用开关（xaml:991–995）同样处理，`IsEnabled` 的绑定不动。
- 开关不做动画（阶段 1 的样式就没有动画，A7.2「不新增动画」）。
- `WrappingCheckBoxContent`（xaml:17）随各页改造逐个失去使用者；12 处全部清掉之后，在最后一个页面任务里删掉这个资源（R14）。`Themes/IrisSwitchStyleTests.cs` 自己另造了一个等价模板（:49），不依赖窗口里的这个资源。
- 开工前先做 2.13 第 18 条的 grep：如果有测试按 `Content` 文字找 `CheckBox`，改成按名字或按 `AutomationProperties.Name` 找，并记进「改过的既有测试」。

### S0-6 背板着色层

**新增颜色键**【用户】U1；键名与十六进制值是【计划裁决】

| 键 | `Iris.Light.xaml` | `Iris.Dark.xaml` | 回退·浅色 | 回退·深色 |
|---|---|---|---|---|
| `IrisAcrylicTintBrush` | `#B3F3F3F3` | `#AD242424` | `#FFF3F3F3` | `#FF242424` |

- 换算：0.70 × 255 = 178.5，取 `B3`（179）；0.68 × 255 = 173.4，取 `AD`（173）。
- 这是 Iris 自己的新键，不覆盖任何 WPF-UI 的键。引用一律 `DynamicResource`。
- 两套主题字典里它的不透明度都小于 255，符合 `IrisColorKeyTests.IrisColorKeys_AreTranslucent` 的口径；回退值不透明度都是 255，符合 `SolidFallbackKeys_AreOpaque`。这两条测试是遍历字典还是逐键硬编码**未确认**（2.13 第 5 条）：逐键硬编码就在任务 1 给它们各补一条新键的用例。

**加在哪个元素上**【计划裁决】

- 设置窗口：根 `Grid`（xaml:20）设 `Background="{DynamicResource IrisAcrylicTintBrush}"`。标题栏在根 `Grid` 的第 0 行里，所以标题栏区域一并被着色，和样张一致。**不**加新的全屏 `Border`、不改窗口自身的 `Background`（那是 `WindowMaterialService` 管的）。
- 不支持亚克力时，这个画刷由回退字典换成不透明值（S0-7），根 `Grid` 的写法不用变。

**导航与底栏**【计划裁决】依据 R10、样张 A5.4（样张里两者都没有不透明底）

- 导航 `Border` 的 `Background`（xaml:43）**删掉**。
- 底栏 `Border` 的 `Background`（xaml:1053）**删掉**；加一条上边线：`BorderThickness="0,1,0,0"`、`BorderBrush="{DynamicResource IrisAcrylicCardStrokeBrush}"`。复用这个既有键是因为它浅色值 `#14000000` 正好是样张 `--m-divider` 的 8%，不必再造分隔线的键。
- 导航与内容区之间不加竖线。

**另两个窗口**【用户】U5（2026-11-17）：

- **本阶段一并给历史窗口与翻译窗口加着色层**，由 **Task 4B** 承担。只加着色，不动这两个窗口的版面、名字与控件（版面仍属阶段 3、4）。
- 用的是同一个键 `IrisAcrylicTintBrush`（Task 1 定义）、同样用 `DynamicResource` 引用；不支持亚克力时由回退字典换成不透明值（S0-7），这两个窗口不需要另写回退逻辑。
- **具体加在哪个元素上**（根面板一行 `Background`，或等价做法）：待调查笔记 `E-tint-other-windows.md` 给出，由 Task 4B 的详细步骤确定。本节不预先指定元素与行号——这两个窗口的 XAML 根结构与既有像素断言不在本计划第 2 节已查明的事实里。
- 影响：三个亚克力窗口的底色在本阶段结束时就一致；代价是本阶段的改动面多出两个窗口文件及其渲染测试（范围扩大由用户裁决，记入完成报告的「范围」一节，不算偏差）。

**金丝雀**：`SelectionSettingsRenderingTests` 取的像素 (150, 50) 改造后落在导航里，而导航去掉了不透明底（2.9(d)）。离屏渲染没有真实背板，着色层后面是什么颜色**未确认**；骨架任务里先算后跑，红了就把取点挪到内容区的行卡片上，并记进「改过的既有测试」。

### S0-7 纯色回退

【用户】U4：补齐两套值、在不支持亚克力时合并、加测试。以下具体做法是【计划裁决】。

**两份字典、五个键**

- `Themes/Iris.SolidFallback.xaml` 保留文件名，作为**浅色**那一套（文件名不改，是因为既有测试按这个名字加载它）；新建 `Themes/Iris.SolidFallback.Dark.xaml` 作为**深色**那一套。新文件名不以 `Iris.Dark.xaml` 结尾，不会被 `ThemeService.cs:235` 的名字表误认成主题字典。
- 两份字典的键集合必须与 `Iris.Light.xaml` / `Iris.Dark.xaml` **完全相同**（本阶段之后是 5 个键），值全部不透明：

| 键 | 回退·浅色 | 回退·深色 |
|---|---|---|
| `IrisAcrylicTintBrush` | `#FFF3F3F3`（新增） | `#FF242424` |
| `IrisAcrylicCardFillBrush` | `#FFFFFFFF`（现值） | `#FF3A3A3A` |
| `IrisAcrylicCardStrokeBrush` | `#FFE0E0E0`（现值） | `#FF3F3F3F` |
| `IrisAcrylicRowFillBrush` | `#FFF9F9F9`（现值） | `#FF353535` |
| `IrisAcrylicRowStrokeBrush` | `#FFE5E5E5`（现值） | `#FF393939` |

- 深色四个值的来历：把 `Iris.Dark.xaml` 里对应的半透明白叠在 `#242424` 上算出的合成色（例：`#1AFFFFFF` 的不透明度 26/255 ≈ 0.102，36 + 0.102 × 219 ≈ 58 → `3A`）。这样回退时卡片与底色的明度关系和亚克力下一致。
- `Iris.SolidFallback.xaml:5–11` 那段说「还缺深色版本」的英文注释改写成现状。

**谁合并、何时合并、何时移除**

- 在 `ThemeService` 里新增一个静态方法（与 `ApplyIrisThemeDictionary` 并排）：

  `internal static void ApplyIrisSolidFallbackDictionary(ApplicationTheme theme, bool acrylicSupported)`

  规则，按顺序：
  1. 从 `Application.Current.Resources.MergedDictionaries` 里移除所有 `Source` 以 `Iris.SolidFallback.xaml` 或 `Iris.SolidFallback.Dark.xaml` 结尾的项；
  2. `acrylicSupported` 为真 → 结束（支持亚克力时回退字典不在场）；
  3. 否则加载与 `theme` 对应的那一份，插到 Iris 主题字典（`Iris.Light.xaml` / `Iris.Dark.xaml`）**紧后面**的位置；找不到主题字典就追加到末尾。排在主题字典之后，同名键才会盖过它。
  4. `Application.Current` 为空时直接返回（与 `ApplyIrisThemeDictionary` 一致）。
- 调用点只有一处：`ThemeService.cs:212` 那行 `ApplyIrisThemeDictionary(theme);` 的下一行加

  `ApplyIrisSolidFallbackDictionary(theme, _windowMaterialService?.IsAcrylicSupported ?? true);`

  - 这样它**天然跟着主题切换走**：每次应用主题都重新选一遍。
  - `_windowMaterialService` 为空（多数测试如此）时按「支持」处理，不合并回退字典，既有测试不受影响。
- `App.xaml` 不改，`WpfTestRunner.AddIrisDictionaries` 也就不用同步（2.9(b)）。
- `WindowMaterialService` 不改。
- 依赖 2.13 第 15 条：启动时必须走到 `ThemeService.cs:212`。任务 1 第一步核实；若启动不经过这里，就在启动时应用初始主题的那一处调用之后补同一行；找不到这样的调用点则停下来报告，不自行另造入口。

**测试**（新文件 `tests/Iris.App.Tests/Themes/IrisSolidFallbackTests.cs`，放进不并行的集合 `IrisThemeResources`，每条在 `finally` 里把回退字典移除并还原浅色主题字典）

1. 四份字典（Light、Dark、两份回退）的键集合两两相同。
2. 两份回退字典的每个画刷不透明度都是 255。
3. `ApplyIrisSolidFallbackDictionary(Dark, false)` 之后，`Application.Current.Resources["IrisAcrylicCardFillBrush"]` 是深色回退值；再调 `(Light, false)` 之后是浅色回退值，且合并字典里回退字典**恰好一份**；再调 `(Light, true)` 之后回退字典零份，同一个键取回半透明值。
4. 经 `ThemeService` 的整条路径：用 `new WindowMaterialService(() => 19045, () => false)` 造一个「不支持」的材质服务交给 `ThemeService`，应用深色主题后断言第 3 条里的深色回退值。`ThemeService` 在测试里怎么构造，照 `tests/Iris.App.Tests/Themes/` 与 `ThemeServicePreviewTests` 里的既有写法；既有测试里若找不到可照抄的构造写法，这一条降级为只测静态方法，并记进偏差清单。

**不做的**

- 高对比度主题下 WPF-UI 会自己否决亚克力，而 `IsAcrylicSupported` 不反映这一点（2.12）。本阶段不处理，完成报告列为遗留问题。
- 不加调试开关来强制回退。真实低版本系统上的观感本机无法验证，完成报告写「未验证」。

### S0-8 钻取状态

**状态放哪**

- 「是否显示详情」是**独立的一位状态**，绝不拿「选中项是否为空」来代替。【设计】B2-4 的结论：选中项在新增、删除、保存、重载时都会变，不能兼任视图状态。
- 放在 `SettingsViewModel` 上，不持久化，不做成新的页面键。【设计】设计 :101；D1 小结。
- **每页一位**，共两个带通知的 `bool` 属性，写在新的 partial 文件 `src/Iris.App/Settings/SettingsViewModel.DetailView.cs` 里：【计划裁决】（设计只说「一个界面状态属性」；B2-4 建议每页一位，避免两页互相串）

  | 属性 | 含义 | 默认 |
  |---|---|---|
  | `IsTranslationDetailOpen` | 「翻译服务」页正在显示详情 | `false` |
  | `IsOcrDetailOpen` | OCR 页正在显示详情 | `false` |

  两个属性都是「值变了才通知」的普通读写属性，setter 里除了赋值和 `OnPropertyChanged()` 之外**什么都不做**。
- **状态的翻转全部由视图（代码隐藏）驱动**，`SettingsViewModel` 的既有方法一个都不改（B2-4 的方案 B）。【计划裁决】理由：第 1.3 节第 9 条。翻转逻辑写在新的 partial 文件 `src/Iris.App/Settings/SettingsWindow.DetailNavigation.cs` 里；新代码不进 `SettingsWindow.xaml.cs` 的既有方法体，除非下面明确点到。
- 代码隐藏另存两个私有字段，记下「当前详情是哪一项」的 `Id`（类型与草稿的 `Id` 相同）：`_translationDetailId`、`_ocrDetailId`；不在详情态时为默认值。另有两个 `internal` 只读属性 `TranslationDetailId`、`OcrDetailId` 读出这两个字段（协调方裁决 C11：没有读者的字段会报 `CS0414`；测试也用它们断言）。

**XAML 上怎么体现**

- `TranslationLayoutGrid`、`ProfilesPane`、`DetailsPane` 三个名字与类型都保留；四个行列定义（xaml:458–463）删除。
- `TranslationLayoutGrid` 变成单格：`ProfilesPane`（列表态）与新的 `TranslationDetailHost`（详情态）叠在同一格。`TranslationDetailHost` 是两行的 `Grid`：第 0 行面包屑（固定，不随内容滚动），第 1 行 `DetailsPane`。
- 可见性用**内联样式里的 `DataTrigger`** 绑 `IsTranslationDetailOpen` 切换：`ProfilesPane` 默认 `Visible`、为真时 `Collapsed`；`TranslationDetailHost` 相反。不新增转换器。
- 详情控件**始终在树里**（只是随宿主折叠），名字不变、不进模板。【设计】设计 :98；用例 4、用例 6、Scenario8 依赖（2.9(c)）。
- 面包屑：`ui:Button`（名字 `TranslationBreadcrumbBackButton`，`Content` 是既有字符串 `SettingsNavTranslation`，套新样式 `IrisBreadcrumbButton`）+ `ui:SymbolIcon`（`ChevronRight16`，已核实）+ `TextBlock`（名字 `TranslationBreadcrumbCurrentText`，绑 `SelectedProfile.DisplayName`，单行加省略号）。不新增格式字符串。【设计】设计 :96；A7.2。
- 面包屑按钮不在任何绑 `CanEdit` 或「保存进行中」的容器里：保存期间、只读模式下也能返回。

**冻结与只读**【用户】U7（2026-11-17）；具体绑法是【计划裁决】

- **列表容器不再整体绑 `CanEdit`。** `ProfilesPane` 上现有的 `IsEnabled="{Binding CanEdit}"`（2.5，xaml:467 起）删除。
- **列表只在保存过程中冻结，判据就是 `IsSaving`（取反）。** `IsSaving` 是公开、带通知、可直接绑定的属性（已核实，2.7「已核实」第 1 条，`SettingsViewModel.cs:493–506`）。写法：在 `ProfilesPane` 那个已有的内联样式里加一条 `DataTrigger`——`Binding IsSaving` 为 `True` 时 `IsEnabled = False`（用触发器取反，不新增转换器；该样式与元素上都不得再写本地的 `IsEnabled` 值，否则触发器不生效）。
- 下表的「只读」一列指 **`CanEdit` 为假且 `IsSaving` 为假**的全部情形：只读（`IsReadOnly`）、录制快捷键中、录制清理失败等（`CanEdit` 的组成见 2.7「已核实」第 2 条）。这些情形下的表现相同。
- 各控件的 `IsEnabled` 归属（「翻译服务」页；OCR 页见 S0-10）：

  | 控件 | `IsEnabled` 由谁决定 | 只读 | 保存中 |
  |---|---|---|---|
  | `ProfilesPane`（含工具条与 `ProfilesListBox`） | `IsSaving`（上面的 `DataTrigger`） | 可用 | 不可用 |
  | `AddProfileButton` | 自己绑 `CanEdit` | 不可用 | 不可用 |
  | 行内启用开关、「⋯」、拖动把手 | 各自绑窗口 `DataContext` 的 `CanEdit`（行模板里用 `RelativeSource` 取窗口的 `DataContext.CanEdit`，写法照 xaml:554–555 的既有做法） | 不可用 | 不可用 |
  | 行的名字区、行尾右箭头 | 不单独绑；随 `ProfilesPane` | **可用（任意行）** | 不可用 |
  | `TranslationBreadcrumbBackButton` | 不绑 | 可用 | 可用 |
  | 详情里的字段容器（`DetailsPane` 内层 `StackPanel`） | 保持现状：绑窗口 `DataContext.CanEdit`（xaml:554–555），不改 | 不可用（看得见、不能改） | 不可用（`CanEdit` 含 `!IsSaving`，已核实） |

- 进入详情的三个入口（名字区、右箭头、行上按 Enter）：只读时**任意行**可用（`SelectedProfile` 的 setter 只在 `IsSaving` 时拒收，已核实，2.7「已核实」第 3 条），保存中不可用。保存中不可用靠两层：容器被禁用；T1 开头的守卫（见下）。
- `DetailsPane`（`ScrollViewer` 本身）不禁用：只读时详情仍可滚动查看。

**翻转规则**（对「翻译服务」页逐条写；OCR 页把属性与字段换成 OCR 那一套，差异见 S0-10）

| # | 触发 | 动作 |
|---|---|---|
| T1 进入 | 行的名字区在原地松开鼠标、点行尾右箭头、焦点在该行 `ListBoxItem` 上按 Enter | **守卫**：`IsSaving` 为真就什么都不做（只读不是守卫条件，U7）。然后 `ProfilesListBox.SelectedItem = 该项`；随后核实 `ReferenceEquals(vm.SelectedProfile, 该项)`，不相等（setter 拒收）就**什么都不做**；相等则记下 `_translationDetailId`，置 `IsTranslationDetailOpen = true`，把键盘焦点移到 `TranslationBreadcrumbBackButton` |
| T2 返回 | 点 `TranslationBreadcrumbBackButton` | 置 `false`，清 `_translationDetailId`；**不写选中项**；把键盘焦点还给列表里的选中行 |
| T3 新增后自动进入【用户】U3 | `OnAddProfileButtonClick` 里菜单项调完 `vm.AddProfileFromPreset(p)`（cs:269）之后 | 调用前记下 `vm.SelectedProfile`；调用后若选中项非空且与之前不是同一个引用，按 T1 的后半段进入。`!CanEdit` 时该方法静默返回、选中项不变，自然不进入 |
| T4 选中项换了 | 详情态下收到 `SelectedProfile` 的变更通知 | 新值非空：同步比较 `Id`，与 `_translationDetailId` 不同 → 按 T2 返回列表（不抢焦点）；相同 → 不动。新值为空：不立刻判断，`Dispatcher.BeginInvoke` 排一次复查，复查时仍为空或 `Id` 不同 → 返回列表 |
| T5 切页 | 收到 `CurrentPageKey` 的变更通知 | 两个属性都置 `false`，两个 `Id` 字段都清掉 |

由这五条推出的各种情形（执行时逐条有测试）：

| 情形 | 结果 | 由哪条规则 |
|---|---|---|
| 删除当前详情的服务 | 回列表 | T4（删除后选中相邻项，`Id` 不同；删到一个不剩时选中为空，走复查） |
| 保存成功 / 重新加载 | **留在详情** | T4（翻译服务按 `Id` 找回原项，VM:604–606；中途短暂为空由复查吸收） |
| 切到别的页再切回来 | 列表 | T5 |
| 同一页重复 `ShowAsync("translation")` | 留在详情 | 无通知（依赖 2.13 第 11 条；若实际会发通知，T5 改成「新旧页面键不同才复位」，代码隐藏自己记上一次的页面键） |
| 关窗后重新打开 | 列表 | 每次打开都是新的视图模型（2.1） |
| 详情态下点导航里的当前页 | 留在详情 | 导航处理函数不改，页面键没变 |
| 保存进行中 | 列表整体不可用（含三个进入口、开关、「⋯」、把手、添加），返回可用 | `ProfilesPane` 的 `IsEnabled` 由 `IsSaving` 的 `DataTrigger` 置假；T1 的守卫；面包屑不在任何冻结容器里 |
| 只读模式（`CanEdit` 为假、未在保存）【用户】U7 | 列表可用、**可进入详情查看**；行内开关、「⋯」、把手与添加按钮不可用；详情字段不可改；返回可用 | 上表各控件的 `IsEnabled` 归属；T1 不以只读为守卫（翻译服务的 setter 只在 `IsSaving` 时拒收，2.7） |
| 只读模式下「⋯」菜单的键盘通路（Shift+F10 / 菜单键） | 不打开菜单 | S0-9：`ContextMenuOpening` 处理里 `CanEdit` 为假就置 `e.Handled = true` |

- T4、T5 挂在既有的 `OnViewModelPropertyChanged`（cs:144–166）上：在该方法里**只加转调**（每个被关注的属性一行，转给新 partial 里的方法），其余不动。T3 在 cs:269 所在的那个 lambda 里加调用。
- T4 的「为空时延后复查」意味着：断言「删光之后回到列表」的测试必须先泵一次调度器消息再断言。
- **进偏差清单**：设计 :104 与 A7.2 的口径是「进入口跟随列表冻结（列表整体绑 `CanEdit`）」；按【用户】U7 改成「列表只在保存中冻结、只读时可进入」，行内控件各自绑 `CanEdit`。
- **进偏差清单**：设计 :101 的字面意思是一个属性，本计划用了两个。

### S0-9 键盘、鼠标、拖动与 `ApplyAdaptiveLayout`

**Esc**【设计】设计 :103；A6.12 第 4 条

- `OnKeyDown`（cs:189–206）**一行不改**：录制中按 Esc 只取消录制；否则放弃并关闭。详情态下 Esc 也是放弃并关闭，**不是**返回列表。
- 不新增任何窗口级按键处理。

**Enter**【计划裁决】依据 R9、A7.2

- 只在两个列表（`ProfilesListBox`、`OcrProfilesListBox`）上各挂一个 `KeyDown` 处理：当且仅当按键是 Enter 且 `e.OriginalSource` 就是 `ListBoxItem` 本身时，按 T1 进入详情并置 `e.Handled = true`。
- 只读模式下 Enter 照常进入（U7）；保存中列表容器被禁用、拿不到键盘焦点，且 T1 有守卫，不进入。
- 焦点在行内的开关、「⋯」、把手上时不处理（它们各自响应空格 / Enter）。
- 保存按钮的 `IsDefault`（xaml:1133–1138）不动：其他地方按 Enter 仍然触发保存。「在行上按 Enter 不会触发保存」必须有测试证明。
- 上下方向键只移动选中，不进详情（`ListBox` 默认行为，不写代码）。

**翻译服务列表行**【设计】设计 :91–93

- 行内从左到右：拖动把手、启用开关、名字加模型（两行文字）、「⋯」、右箭头。
- 进入详情的三个入口：名字区松开鼠标、右箭头、行上按 Enter。把手、开关、「⋯」**不**进入详情。启用开关不改变选中项。
- **`IsEnabled` 归属**【用户】U7：启用开关、「⋯」、把手各自绑窗口 `DataContext` 的 `CanEdit`；名字区与右箭头不单独绑（只读时可用，保存中随 `ProfilesPane` 一起不可用）。完整的表在 S0-8「冻结与只读」。
- 名字区：`MouseLeftButtonUp` 时若 `_isDragging` 为假且松开点仍在该元素内，按 T1 进入。
- 右箭头：`ui:Button` 套 `IrisIconButton`，图标 `ChevronRight20`（已核实），`Content` 用新字符串 `SettingsRowOpenDetails`（S0-12），`Click` 按 T1 进入。
- 「⋯」：`ui:Button` 套 `IrisIconButton`，图标 `MoreHorizontal20`（已核实），`Content` 用新字符串 `SettingsRowMoreActions`。`Click` 时先按 T1 的前半段选中该行并核实（核实失败就返回），再打开菜单。
- 菜单在代码隐藏里现场构造（与 `OnAddProfileButtonClick` 的既有做法一致，cs:249–278），三项：上移、下移、删除，文字用既有的 `SettingsMoveUp`、`SettingsMoveDown`、`SettingsDeleteProfile`；`Command` 就是现在三个工具条按钮（xaml:491–507）绑的那三个命令，原样引用，可用与否由命令自己的 `CanExecute` 决定。**不直接调视图模型的方法，不改命令。**
- 同一个构造方法再挂到 `ProfilesListBox` 的 `ContextMenuOpening`：焦点在行上按 Shift+F10 或菜单键能打开同一份菜单。这是上移 / 下移的键盘通路（A6.12 第 10 条），不依赖 Tab 能否走进行内（2.13 第 4 条）。
- 【用户】U7：`ContextMenuOpening` 处理开头加守卫——视图模型的 `CanEdit` 为假时置 `e.Handled = true` 并返回，不打开菜单（只读时「⋯」按钮本身已被禁用，键盘通路与之保持一致）。OCR 列表的 `ContextMenuOpening` 同样处理。
- `AddProfileButton` 自己绑 `IsEnabled="{Binding CanEdit}"`（原来靠 `ProfilesPane` 整体绑 `CanEdit` 继承，U7 之后容器不再绑）。
- 工具条上的 `MoveUpProfileButton`、`MoveDownProfileButton`、`DeleteProfileButton` 三个按钮**删除**（设计 :92）。`AddProfileButton` 保留为 `ui:Button` 加 `ContextMenu`，英文下宽度仍须大于 70（设计 :90；`LocalizationTests.cs:238–268`）。
- 空列表：只剩「添加服务」按钮，不新增占位文字。

**拖动只能从把手起**【计划裁决】依据 B2-8 第 6 条

- 现状是行内任意位置都能起拖（cs:368–397），这会和「名字区点一下进入详情」打架。
- 把手是一个 `Border`（新样式 `IrisDragHandle`，`Tag="DragHandle"`，背景透明以便命中），里面是 `ui:SymbolIcon`（`ReOrderDotsVertical20`，已核实）；`ToolTip` 与 `AutomationProperties.Name` 用新字符串 `SettingsRowDragToReorder`。
- 在记录起拖点的那个鼠标按下处理（cs:363–366）里多记一个私有布尔字段 `_dragStartedOnHandle`：`e.OriginalSource` 自身或其可视树祖先里有 `Tag` 等于 `"DragHandle"` 的元素、**并且视图模型的 `CanEdit` 为真**才为真（【用户】U7：只读时不能拖；不依赖「被禁用的把手收不到鼠标事件」这一未核实的行为）。`OnProfilesListMouseMove`（cs:368–397）开头加一句：该字段为假就返回。其余拖放代码（cs:399–437）不动。
- 把手的 `Border` 同时绑 `IsEnabled`（窗口 `DataContext.CanEdit`），样式 `IrisDragHandle` 的禁用观感由 Task 2 的既有定义决定；Task 2 没有定义禁用态时，Task 12 不为此改样式，只读时把手外观不变、功能不可用，记入完成报告。

**`ApplyAdaptiveLayout`**【计划裁决】依据 R2、B2-4、设计 :105

- 保留：方法名、`internal`、参数 `double width`、五个调用点（cs:30、:31、:34、:37–44、:46–53）、字段 `_isCompactMode` 与常量 `CompactLayoutThreshold`。
- **分两步落地**：任务 4 只在方法体前部加导航切换，旧的分栏逻辑先留着（任务 4–11 期间「翻译服务」页还靠它）；任务 12 再把旧分栏逻辑删掉。下面写的是任务 12 之后的最终形态。
- 方法体（cs:55–99）整体换掉，新内容只做三件事：
  1. `NavColumnDef` 为空就返回（代替原来 cs:57 那个引用翻译页元素的守卫）；
  2. `_isCompactMode = width < CompactLayoutThreshold`；
  3. 按它设 `NavColumnDef.Width`（`IrisNavCompactWidth` 或 `IrisNavWidth`）与 `NavTitleText.Visibility`。
- 原方法体里若有中文注释，随方法体**整条删除**，不改写、不搬走（第 1.3 节第 2 条；过期登记的问题见 2.13 第 16 条）。
- 旧的「窄于 680 上下堆叠」布局的定义与代码全部删除（设计 :105）。

### S0-10 OCR 页

**钻取后的结构**【设计】设计 :109–111；具体摆法是【计划裁决】（A7.2 的方案 a：详情替换整页）

- `OcrPageScroll` 保留名字与类型（`ScrollViewer`），仍是这一页的根。它里面叠两块，用内联样式的 `DataTrigger` 绑 `IsOcrDetailOpen` 切换可见性：
  - `OcrListPanel`（新名字，列表态）：页标题、页说明、六个页面级设置、服务小标题、两个添加按钮、`OcrProfilesListBox`。
  - `OcrDetailHost`（新名字，详情态）：面包屑、`OcrDetailsPanel`、测试区。
- **六个页面级设置放在列表态顶部**，每项一张横向行卡片（不带图标，S0-4）；`OcrSelectionError` 在它们上方。进详情后它们随 `OcrListPanel` 折叠，但**仍在树里**，名字不变。
- **测试区放在详情态底部**，做成一张动作卡（S0-4）：`OcrTestButton`、`OcrCancelTestButton`、`OcrTestResultText`、说明文字。理由：测试对象就是当前选中的那一个服务。
  - 它必须在 `IsEnabled={Binding CanEdit}` 的容器**之外**（现状如此，xaml:279–284；保存期间仍能取消测试）。
  - 它在 `OcrDetailsPanel` **之外**、与之并列，所以 `OcrTestResultText` 的绑定路径 `SelectedOcrProfile.TestMessage` 不用改（B2-8）。
- 面包屑与「翻译服务」页同构：`OcrBreadcrumbBackButton`（`Content` 是既有字符串 `OcrPageTitle`）+ `ChevronRight16` + `OcrBreadcrumbCurrentText`（绑 `SelectedOcrProfile.DisplayName.Value`）。它不在任何绑 `CanEdit` 或 `IsSaving` 的容器里（见下文「只读模式」的表）。
- 与「翻译服务」页的一处不同：OCR 的面包屑在 `OcrPageScroll` **里面**、随内容滚动；「翻译服务」的面包屑固定在滚动区上方。【计划裁决】理由：`OcrPageScroll` 必须继续是这一页的根（多条测试按名字取它并强转）。进偏差清单。

**列表**

- `OcrProfilesListBox` 保留名字与类型；去掉 `MaxHeight=120`（xaml:216）；`ItemContainerStyle`（xaml:217–221）改成 `BasedOn` 新样式 `IrisListRowItem`。
- 行内从左到右：名字、「⋯」、右箭头。**没有**拖动把手，**没有**上移下移（设计 :110），**没有**行内启用开关。【计划裁决】启用仍由详情里的 `OcrEnabledCheck` 承担，理由：行内开关会去写一个未选中的项，而 `SelectedOcrProfile` 的 setter 在 `!CanEdit` 时拒收（2.7），两头容易不一致。
- 「⋯」菜单只有一项：删除（既有字符串 `OcrDelete`），`Command` 用现在 `DeleteOcrProfileButton`（xaml:231）绑的那个命令。两个内置服务的行**不隐藏**「⋯」，菜单项由命令的 `CanExecute` 置灰（依赖 2.13 第 13 条；若该命令对内置服务不返回假，则改为按草稿的「是否内置」属性设菜单项的 `IsEnabled`，属性名执行时查）。
- `DeleteOcrProfileButton` 删除，由菜单取代。
- 两个添加按钮保持原生 `Button`、原 `Content`、原 `Command`（`AddBaiduOcrCommand`、`AddWindowsOcrCommand`）；除调位置与间距外，只多两样视图层的东西：各自绑 `IsEnabled`（`CanEdit`，U7，见下文「只读模式」）与一个 `Click` 处理（U8，见下文 T3-OCR）。
- 进入、返回、切页复位的规则与 S0-8 的 T1、T2、T5 相同（属性换成 `IsOcrDetailOpen`，字段换成 `_ocrDetailId`，选中项换成 `SelectedOcrProfile`）。**T4 在 OCR 页换成下文的 T4-OCR**（U6）；新增后自动进入见下文 T3-OCR（U8）。`SelectedOcrProfile` 的通知会多发（2.7），各规则都按 `Id` 比较，多发无害。
- 键盘：`OcrProfilesListBox` 同样挂 Enter 处理与 `ContextMenuOpening`（S0-9）。

**新增 OCR 服务之后**【用户】U8（2026-11-17）：**自动进入新服务的详情页**，与翻译服务一致。做法是【计划裁决】，记作规则 **T3-OCR**：

- 两个添加按钮仍绑原命令（`AddBaiduOcrCommand`、`AddWindowsOcrCommand`），不改命令、不改 `AddOcrProfile`。
- 视图层给两个按钮各挂同一个 `Click` 处理（写在 `SettingsWindow.DetailNavigation.cs`）。处理里：
  1. 记下点击前的状态：`OcrProfilesListBox.Items` 里每一项的 `Id` 组成的集合（数量即集合大小）。
  2. `Dispatcher.BeginInvoke`，优先级 `DispatcherPriority.Loaded`（低于 `DataBind`，保证命令已执行、列表与选中项的绑定已更新），排一次「添加复查」。
  3. 复查时同时满足三条才进入：`OcrProfilesListBox.Items.Count` 比点击前大；`vm.SelectedOcrProfile` 非空；它的 `Id` 不在点击前的集合里。进入的动作是 T1 的后半段：记下 `_ocrDetailId`，置 `IsOcrDetailOpen = true`，焦点移到 `OcrBreadcrumbBackButton`。任一条不满足（命令因 `CanExecute` 为假没执行、或添加失败）→ 什么都不做。
- 具体事件接法（`Click` 与命令执行的先后、两个按钮是否需要补名字才能在代码隐藏里挂事件）**在详细步骤里按真实代码确定**。执行时先核实：添加命令是不是同步完成的（看 `AddBaiduOcrCommand` / `AddWindowsOcrCommand` 的定义是否直接调 `AddOcrProfile`，VM.Ocr:86–95）。
  - 同步 → 按上面三步做。
  - 不同步（延后一拍时数量还没变）→ 退路：`Click` 里只记点击前的 `Id` 集合并置一次性标记 `_ocrAddPending`；改在 `SelectedOcrProfile` 的变更通知里判断同样的三条，满足就进入并清标记；切页（T5）与返回（T2）时也清标记。
- 只读或保存中：添加按钮被禁用（见下文「只读模式」），不会触发。

**保存 / 重新加载之后**【用户】U6（2026-11-17）：**留在详情，由视图层按 `Id` 把选中项写回原来那一项**。不改视图模型的任何既有方法（不动 `MergeOcrSettings`）。做法是【计划裁决】，记作规则 **T4-OCR**（代替 S0-8 的 T4，只用于 OCR 页）：

- 背景：视图模型在保存 / 重载后会把选中项跳到第一项（VM.Ocr:130–134、:153）；`SelectedOcrProfile` 的 setter 在 `!CanEdit` 时拒收（2.7，VM.Ocr:52）。
- 视图记着详情里那一项的 `Id`（`_ocrDetailId`），另加一个私有布尔字段 `_ocrRestorePending`。
- 触发：详情态下收到 `SelectedOcrProfile` 的变更通知，或收到 `CanEdit` 的变更通知且 `_ocrRestorePending` 为真。处理函数里**不同步写选中项**（视图模型可能正处在合并过程中间），一律 `Dispatcher.BeginInvoke` 排一次「回写复查」。
- 复查时（仍在详情态才继续）按顺序判断：
  1. 选中项非空且 `Id` 等于 `_ocrDetailId` → 清 `_ocrRestorePending`，结束。
  2. 在 `OcrProfilesListBox.Items` 里按 `Id` 找 `_ocrDetailId` 对应的项。**找不到**（被删了）→ 按 T2 回到列表（不抢焦点），清 `_ocrRestorePending`。
  3. 找到，且 `vm.CanEdit` 为真 → `vm.SelectedOcrProfile = 那一项`；随后核实选中项的 `Id` 等于 `_ocrDetailId`：相等则保持 `IsOcrDetailOpen`、清 `_ocrRestorePending`；不相等（被拒收）→ 置 `_ocrRestorePending = true`，等下一次通知。
  4. 找到，但 `vm.CanEdit` 为假 → 置 `_ocrRestorePending = true`，保持 `IsOcrDetailOpen`，**不写**；等 `CanEdit` 恢复为真的通知再走一遍复查。
- T2（返回）与 T5（切页）都要清 `_ocrRestorePending`。
- 由此推出：保存成功 / 重新加载 → 留在详情、仍是原来那一项；删除当前详情的服务 → 回列表（删除后选第一项，VM.Ocr:101，列表里已没有 `_ocrDetailId`）。
- **已知副作用，进偏差清单**：
  - 回写会触发旧选中项（视图模型跳过去的那一项）的 `InvalidateTest()`（VM.Ocr:82），它的测试结果被清掉。
  - 等待回写期间（上面第 4 步，例如保存尚未结束）详情与面包屑短暂显示的是视图模型当前选中的那一项（通常是第一项）；`CanEdit` 恢复后才换回原项。`CanEdit` 一直不恢复（重载后停在只读）时就一直显示当前选中项，用户可点面包屑返回。
- 断言「保存后留在详情」的测试必须先泵调度器消息再断言。执行时先核实 2.13 第 14 条（重载时选中项是否短暂为空）：短暂为空也走上面的复查（第 2 步按 `Id` 找），不需要另写分支。

**只读模式**【用户】U7（2026-11-17）+ 协调方裁决 **C4**（1.2A，【计划裁决】）：列表只在保存中冻结；`CanEdit` 为假且不在保存中时，**只有当前选中的那一项能进详情查看、不能改**。这是对 U7 的**部分落实**，原因与交代见本小节末尾和第 6.3 节。

- 「只读」一列的含义与 S0-8 相同：`CanEdit` 为假且 `IsSaving` 为假（只读、录制快捷键中、录制清理失败等）。
- 现在那个整体绑 `IsEnabled={Binding CanEdit}` 的可编辑容器（2.6，xaml:194–278）**拆开**，不再有一个容器同时罩住列表与详情：

  | 控件 | `IsEnabled` 由谁决定 | `CanEdit` 为真 | 只读 | 保存中 |
  |---|---|---|---|---|
  | 六个页面级设置所在的容器（列表态顶部） | 绑 `CanEdit` | 可用 | 不可用 | 不可用（`CanEdit` 含 `!IsSaving`，已核实） |
  | `OcrServicesPane`（新名字：包住两个添加按钮与 `OcrProfilesListBox` 的面板） | `IsSaving` 的 `DataTrigger`（写法同 `ProfilesPane`，S0-8） | 可用 | 可用 | 不可用 |
  | 两个添加按钮 | 各自绑 `CanEdit`（命令的 `CanExecute` 仍然生效，两者取与） | 可用 | 不可用 | 不可用 |
  | 行内「⋯」 | 绑窗口 `DataContext.CanEdit` | 可用 | 不可用 | 不可用 |
  | 行的名字区、行尾右箭头（进入详情的入口）——**C4** | 「窗口视图模型的 `CanEdit` 为真」**或**「该行 `ListBoxItem.IsSelected` 为真」；外加随 `OcrServicesPane` | 全部行可用 | **只有当前选中行可用；其余行禁用（看得出不可点）** | 不可用 |
  | 行上按 Enter——**C4** | 代码隐藏里 T1 的守卫（见下） | 任意行可进 | 只有当前选中行可进 | 不进 |
  | `OcrBreadcrumbBackButton` | 不绑 | 可用 | 可用 | 可用 |
  | `OcrDetailsPanel`（详情字段） | 绑窗口 `DataContext.CanEdit`（它自己的 `DataContext` 是 `SelectedOcrProfile`，所以要用 `RelativeSource` 取窗口的，写法照 xaml:554–555） | 可用 | 不可用（看得见、不能改） | 不可用 |
  | 测试区（`OcrTestButton`、`OcrCancelTestButton` 等） | 保持现状：不在任何冻结容器里，由各自的命令决定 | 与现状相同 | 与现状相同 | 与现状相同（保存期间仍能取消测试） |

- **为什么 OCR 页只能做到「当前选中项可看」**（C4）：T1 要先把该行写成选中项，而 `SelectedOcrProfile` 的 setter 在 `CanEdit` 为假时拒收（已核实，2.7「已核实」第 4 条，`SettingsViewModel.Ocr.cs:52`）；详情面板又靠 `DataContext="{Binding SelectedOcrProfile}"` 取值。**不改这个 setter**（那一行守卫属业务逻辑，第 1.3 节第 9 条），也不改 `OcrDetailsPanel` 的 `DataContext` 来源。**进偏差清单；交计划时向用户说明（第 6.3 节）。**
- **行入口的实现提示**（C4；具体写法由 Task 11 详细步骤确定）：行模板里名字区与右箭头的 `IsEnabled` 用 `MultiBinding`，或在它们的内联样式里用 `DataTrigger` / `MultiDataTrigger`，条件是「窗口视图模型的 `CanEdit` 为真（`RelativeSource` 取窗口的 `DataContext.CanEdit`）**或**该行 `ListBoxItem.IsSelected` 为真（`RelativeSource AncestorType=ListBoxItem`）」。约束：不新增中文字面量；禁用后的观感必须看得出不可点——名字区是不是能靠 `IsEnabled` 变灰的控件，Task 11 详细步骤里按真实模板确定，不能变灰就给名字文字加一条同条件的触发器换成 `TextFillColorDisabledBrush`。若要用转换器才能表达「或」，先用两条 `DataTrigger`（默认禁用，任一条件成立则启用）的写法，不新增转换器类。
- **T1 的 OCR 版多一条守卫**（C4）：`IsSaving` 为真 → 什么都不做；`vm.CanEdit` 为假且该行不是 `vm.SelectedOcrProfile`（`ReferenceEquals`）→ 什么都不做；其余照 S0-8 的 T1（写选中项、核实引用相同、记 `_ocrDetailId`、置 `IsOcrDetailOpen`、移焦点）。只读时对当前选中行执行 T1：把 `OcrProfilesListBox.SelectedItem` 设成它本来就是的那一项不产生写入，核实通过，进入。Enter 的处理走同一个 T1，所以键盘与鼠标口径一致。
- **列表与视图模型的选中项脱节：C4 只规避了一部分。** 非选中行的入口（名字区、右箭头）被禁用、Enter 被 T1 挡住，所以「进了一个视图模型没选中的项的详情」不会发生。但**行本身仍可能被点选**：`CanEdit` 为假时列表容器可用，鼠标按在行的空白处或按方向键，`OcrProfilesListBox` 会自己换选中行，而视图模型拒收——此时列表高亮的行与 `vm.SelectedOcrProfile` 不一致，并且 C4 的「`IsSelected` 为真则入口可用」会让那一行的入口亮起来（点了也进不去，T1 的守卫挡着，但观感是错的）。所以 **Task 11 的详细步骤必须把这两点写清并落到代码**：
  1. `OcrProfilesListBox` 的 `SelectedItem` 绑定模式（现状的绑定写法执行时照 xaml:216–230 核实，保持 `TwoWay` 不改）。
  2. 读回方式。先核实：`CanEdit` 为假时点一个非选中行之后，`OcrProfilesListBox.SelectedItem` 与 `vm.SelectedOcrProfile` 是否仍是同一个引用（绑定在写入被拒后是否自动读回；2.13 第 25 条）。
     - 是同一个引用 → 不加代码。
     - 不是 → 在 `OcrProfilesListBox` 的 `SelectionChanged` 上挂一个视图层处理（写在 `SettingsWindow.DetailNavigation.cs`）：两者引用不同时，把 `OcrProfilesListBox.SelectedItem` 设回 `vm.SelectedOcrProfile`。只改列表控件的选中，不写视图模型；设回会再触发一次 `SelectionChanged`，此时两者相同、直接返回，不会循环。
  3. 两种结果都要有一条测试守着（`OcrDetailNavigationTests`：`CanEdit` 为假时点非选中行、按方向键之后，列表选中行与视图模型选中项一致，且只有那一行的入口可用）。
- 「翻译服务」页没有这处限制（它的 setter 只在 `IsSaving` 时拒收，已核实，2.7「已核实」第 3 条）：只读时任意行可进。
- T4-OCR 用到 `CanEdit` 的变更通知：**已核实会发**（2.7「已核实」第 1、2 条；保存结束时 `IsSaving` 的 setter 里就发）。照 T4-OCR 做，不需要退路。
- **进偏差清单**：设计 :104 与 A7.2 的「进入口跟随列表冻结」按 U7 改了口径（同 S0-8）。

### S0-11 主题层新增与改动的键

键名都是【计划裁决】。除颜色键外都加在 `src/Iris.App/Themes/Iris.Styles.xaml`；注释一律英文。

**新增**

| 键 | 类型 | 值 / 作用 | 谁用 |
|---|---|---|---|
| `IrisAcrylicTintBrush` | `SolidColorBrush`（四份颜色字典各一个） | 见 S0-6、S0-7 | 根 `Grid` |
| `IrisNavWidth` | `sys:Double` | 176 | `ApplyAdaptiveLayout` |
| `IrisNavCompactWidth` | `sys:Double` | 48 | 同上 |
| `IrisPagePadding` | `Thickness` | `24,16,24,16` | 每页第一层面板的 `Margin` |
| `IrisPageTitle` | `Style`（`TextBlock`） | 页标题：字号大于 `IrisSectionHeader`，下边距用 `IrisCardGap` | 每页顶部 |
| `IrisRowIcon` | `Style`（`ui:SymbolIcon`） | 20 大小、`Margin="0,0,12,0"`、垂直居中、`TextFillColorSecondaryBrush` | 普通页行卡片第 0 列 |
| `IrisStatusText` | `Style`（`TextBlock`） | 状态 / 结果文字：次要色、可换行 | 动作卡、详情里的状态文字 |
| `IrisRowError` | `Style`（`TextBlock`） | 行内校验错误文字（【计划裁决】C5，2026-11-19）：`BasedOn="{StaticResource IrisRowDescription}"`；一个触发器 `Text=""` → `Visibility=Collapsed`；**不设 `Foreground`**（各行原来的画刷留在元素上）。必须定义在 `IrisRowDescription` 之后 | 行卡片第 1 列文字栈的最后一项（任务 5–9、11） |
| `IrisListRowItem` | `Style`（`ListBoxItem`） | 列表行容器：常态用 `IrisAcrylicCardFillBrush` / `IrisAcrylicCardStrokeBrush`，圆角 `IrisRowCornerRadius`，内边距 `IrisRowPadding`，行距 `IrisRowGap`；悬停、选中、键盘焦点三种交互状态加禁用各有可辨的外观（悬停 / 选中叠 `SubtleFillColorSecondaryBrush`，禁用降不透明度）。**不做独立的「按下」态**【计划裁决】C1：`ListBoxItem` 没有 `IsPressed`，不为此新增附加属性；进偏差清单 | 两个列表的 `ItemContainerStyle` |
| `IrisDragHandle` | `Style`（`Border`） | 透明背景、固定宽 20、光标 `SizeAll` | 翻译服务列表行 |
| `IrisBreadcrumbButton` | `Style`（`ui:Button`） | 无底无边的文字按钮，字号同 `IrisPageTitle`，次要色，悬停变主色，有键盘焦点框 | 两个面包屑 |
| `IrisBreadcrumbCurrentText` | `Style`（`TextBlock`） | 字号同 `IrisPageTitle`，主色，单行加省略号 | 两个面包屑 |

- 右箭头与「⋯」直接用既有的 `IrisIconButton`，不新增键。
- 空列表占位：不做（不新增文字），不新增键。
- 上表里 `IrisPageTitle` 的具体字号、`IrisListRowItem` 各状态的具体画刷，由任务 2 的撰写者按样张定下并写进任务 2 的详细步骤；本骨架只定键名、类型与职责。

**改动阶段 1 的键**

| 键 | 改动 | 理由 |
|---|---|---|
| `IrisNavItem`（`Iris.Styles.xaml:261–267`） | `Padding` 由 `10,8` 改 `8,8`；注释 :260 改写 | S0-2 |
| `IrisRowTitle`、`IrisRowDescription` | 没有 `TextWrapping="Wrap"` 就补上 | S0-4 |
| `Iris.SolidFallback.xaml` | 加 `IrisAcrylicTintBrush`；注释 :5–11 改写 | S0-7 |

**保持不动**：`IrisCard`、`IrisRowCard`、`IrisSwitchCheckBox`、`IrisIconButton`、`IrisTag`、`IrisTagText` 以及全部圆角 / 间距键。`IrisAcrylicRowFillBrush` / `IrisAcrylicRowStrokeBrush` 这一对本阶段之后**仍然没有使用者**（行卡片沿用 `IrisRowCard` 继承来的卡片画刷）；不删，完成报告里提一句，留给后续阶段。

**守卫测试**：`Themes/IrisStyleKeyResolutionTests.cs`、`IrisStyleResourceTests.cs`、`IrisColorKeyTests.cs` 要覆盖到每个新键。它们是遍历还是逐键硬编码**未确认**（2.13 第 5 条）：逐键硬编码就逐个补用例；若有「键总数等于 19」之类的断言，改成新总数并记进「改过的既有测试」。

### S0-12 新增界面文字

键名、中英文都是【计划裁决】。每条都要同时进四处（2.10）：`src/Iris.App/Resources/Strings.resx`、`src/Iris.App/Resources/Strings.en-US.resx`、`build/localization/app-catalog.json`，以及 `UiText` 的一个 partial。`UiText` 这边**新建**文件 `src/Iris.App/UiText.AcrylicSettings.cs` 来放（不往带中文的既有文件里加）。

| 键 | 中文 | 英文 | 用在哪 |
|---|---|---|---|
| `SettingsRowMoreActions` | 更多操作 | More actions | 「⋯」按钮的 `Content`（即提示与读屏名） |
| `SettingsRowOpenDetails` | 查看详情 | Open details | 右箭头按钮的 `Content` |
| `SettingsRowDragToReorder` | 拖动排序 | Drag to reorder | 拖动把手的 `ToolTip` 与读屏名 |
| `SettingsRowEnabled` | 启用 | Enabled | 列表行里无文字的启用开关的读屏名（S0-5） |

- 加之前先在 `Strings.resx` 里按**中文值**搜一遍：某条已有键的中文与英文都与上表完全相同，就复用那条既有键、不新增，并在完成报告里写明复用了哪条。
- 除这四条以外**不新增任何字符串**：面包屑、分组名、菜单项、页标题全部复用既有键（2.10）；不新增说明文字、占位文字、格式字符串。执行中发现确实还缺一条时，先停下来把键名与中英文报给用户，不自行添加。
- `x:Static` 引用 `UiText` 的写法照 `SettingsWindow.xaml` 里既有的写法。
- 键缺失不会报错（2.10），所以任务 10 加完之后立刻跑一次本地化审计，不等到收尾。

### S0-13 测试夹具：主题字典对齐

问题见 2.9(e)：渲染测试切主题时绕过 `ThemeService`，只换了 WPF-UI 的字典，Iris 的颜色字典停在浅色；并且 Iris 字典的状态会漏到后面的测试里。行卡片一旦用上 Iris 的画刷，深色截图就是「深色控件配浅色卡片」，W04 的深色亮度断言按推算会红（D3-9）。

**裁决**【计划裁决】

- **单独一个任务（任务 3），排在所有界面改动之前做。** 这时界面还没变，改完夹具后相关测试应当**改前改后都绿**——这样能把「夹具改动」与「界面改动」造成的红分开。
- 在应用级切主题的**六处**（前三处是骨架原有的；后三处是协调方裁决 C8 于 2026-11-19 要求纳入、2026-11-23 逐个 grep 核实后补的，见 2.9(e)），紧跟着既有的 `ApplicationThemeManager.Apply(...)` 加一行 `ThemeService.ApplyIrisThemeDictionary(theme)`，并在同一次 `WpfTestRunner` 调用结束前还原成浅色（照 `Themes/IrisThemeDictionaryTests.cs:37,41,220` 的 `RestoreLightDictionary()` 先例）。还原写在哪里随各文件的既有结构（用例自己的 `finally`、夹具的还原钩子、或包一层 `try/finally`），逐字代码见任务 3 Step 5：

  | 文件 | 位置 | 说明 |
  |---|---|---|
  | `Settings/SettingsHotkeysIntegrationTests.cs` | W04，:1525–1530 | 八个变体，每换一次主题都要跟一次 |
  | `Settings/SelectionSettingsRenderingTests.cs` | :30 | 金丝雀 |
  | `Settings/SettingsWindowTranslationUiTests.cs` | 用例 7，`finally` 435–444 | 现在只还原 WPF-UI 字典，补上还原 Iris 字典 |
  | `Settings/OcrSettingsRenderingTests.cs` | 辅助方法 `Prepare`，:88–93（应用级 :90、窗口级 :91） | 【计划裁决】C8。42 张证据图；现在没有任何还原。对齐写在 :90 与 :91 之间 |
  | `Settings/NetworkSettingsRenderingTests.cs` | 辅助方法 `Prepare`，:103–107（应用级 :105） | 【计划裁决】C8。现在没有任何还原 |
  | `Settings/NetworkSettingsFoundationTests.cs` | 用例 `ActualFutureSettingsWindow_DisablesEditsSaveAndTests_AllowsCloseAndRendersNotice`，:78（在 `try` :76 之内） | 【计划裁决】C8。`finally`（:96）现在只关会话，补上还原 Iris 字典 |

- **C8 的范围怎么定的**：只收「渲染设置窗口」且「在应用级**直接**调 `ApplicationThemeManager.Apply(主题)`、绕过 `ThemeService`」的夹具。不收的调用点（渲染的是别的窗口、走的是 `ThemeService`、调的是「把当前主题套到某个窗口上」的 `Apply(window)` 重载、或只是注释）在任务 3 Step 1 列了表，逐个写了理由。

- 在窗口级加 WPF-UI 字典的三处（只出证据图、无断言），在同一个窗口的 `MergedDictionaries` 里紧跟着再加一份对应主题的 Iris 颜色字典。因为是窗口级的，不会外漏，不用还原：

  | 文件 | 位置 |
  |---|---|
  | `Updates/UpdateRenderingTests.cs` | :52–54 |
  | `History/HistoryRenderingTests.cs` | :86–89 |
  | `Localization/LocalizationRenderingTests.cs` | :240–243 |

- 设置与还原必须落在**同一次** `WpfTestRunner` 调用里（同一段在 STA 线程上同步执行的代码），中间不能有 `await` 让别的测试插进来。骨架原有的三个用例（W04、金丝雀、用例 7）现在是不是这样写的，任务 3 第一步核实；不是的话把还原挪到与设置相同的那一段里。
- **C8 追加的三处怎么套这一条**：三个类都在集合 `ScreenshotRendering` 里，它的定义带 `DisableParallelization = true`（`Capture/ScreenshotRenderingTests.cs:13`），这个集合里的测试不与任何其它测试并行（xunit.v3 4.0.1 对该属性的说明原文：whether tests in this collection runs in parallel with any other tests；测试项目没有 `xunit.runner.json`，也没有程序集级的并行设置，2026-11-23 grep 核实）。所以「同一次 `WpfTestRunner` 调用」照样要满足（任务 3 Step 5 的 (g)(h)(i) 都满足）；「中间不能有 `await`」这半句防的是并行的测试插进来，对它们不成立也不需要——这三处从对齐到取图之间都要等渲染帧，去不掉，也不用像金丝雀那样在取图前再对齐一次。
- **金丝雀的例外**（协调方裁决 C2，2026-11-17，【计划裁决】，进偏差清单）：`SelectionSettingsRenderingTests` 切主题之后每张图之前都要等渲染帧，上一条做不到。改为**在每次渲染取图之前同步地再对齐一次** Iris 主题字典（对齐与取图之间不含 `await`）；不把它挪进不并行的集合。逐字的代码与位置见任务 3 Step 5 的 (b-2)。
- 任务 3 里撰写者加的六条「夹具守卫」断言**保留**（协调方裁决 C3）。C8 追加的三处照同一口径各加一条，任务 3 Step 3 现在共**九条**；C8 的原文没有说这三处要不要加守卫，这是撰写者照既有写法补的，待协调方确认（任务 3「⚠」第 7 条）。
- `ThemeService.ApplyIrisThemeDictionary` 是 `internal static`，测试项目已经在用它（上面的先例），不需要改可见性。
- 不改 `WpfTestRunner` 的字典合并顺序（本阶段 `App.xaml` 不变，S0-7）。
- 这九处（应用级六处 + 窗口级三处）全部属于「改过的既有测试」，逐条写进完成报告：文件、用例名、改了什么、为什么。
- **「分组跑绿」不等于「全量跑绿」**：每个改动界面的任务除了跑自己的过滤组，还要跑第 5 节开头给的「设置全集」过滤组；全量 `dotnet test Iris.slnx -c Release` 留到任务 13 与任务 14。

---

## 4. 文件结构

路径相对仓库根 `F:\Iris`。「任务」列指第 5 节的任务号。

### 4.1 新建

| 文件 | 职责 | 任务 |
|---|---|---|
| `src/Iris.App/Themes/Iris.SolidFallback.Dark.xaml` | 深色主题的纯色回退颜色字典（5 个不透明画刷） | 1 |
| `src/Iris.App/Settings/SettingsViewModel.DetailView.cs` | 两个纯视图状态属性 `IsTranslationDetailOpen`、`IsOcrDetailOpen` | 10 |
| `src/Iris.App/Settings/SettingsWindow.DetailNavigation.cs` | 代码隐藏的 partial：进入 / 返回 / 复位（T1–T5）、OCR 的新增后自动进入与保存后回写（T3-OCR、T4-OCR）、行菜单构造、列表上的 Enter 处理 | 10 建，11、12 补 |
| `src/Iris.App/UiText.AcrylicSettings.cs` | 四条新文字的 `UiText` 属性 | 10 |
| `tests/Iris.App.Tests/Themes/IrisSolidFallbackTests.cs` | 回退字典的键齐全、不透明、随主题切换、支持亚克力时不在场 | 1 |
| `tests/Iris.App.Tests/Settings/SettingsWindowNavigationLayoutTests.cs` | 导航宽度（600 / 679 / 680 / 760）、八个导航项的名字 / 类型 / 提示 / 读屏名、窄态只剩图标、底栏不横向溢出 | 4 |
| `tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs` | 普通页的行卡片结构：开关样式与读屏名、说明文字都在、不残留 `ui:Card`、680 与 600 宽下不被横向裁切；按页面键做成 `Theory`，每个页面任务加一行 | 5 建，6–9 各加 |
| `tests/Iris.App.Tests/Settings/SettingsViewModelDetailViewTests.cs` | 两个属性默认假、变了才通知、互不影响 | 10 |
| `tests/Iris.App.Tests/Settings/OcrDetailNavigationTests.cs` | OCR 页的进入 / 返回 / 切页复位 / 删除复位 / 保存与重载后留在详情（U6）/ 新增后自动进入（U8）/ 只读时仅当前选中项可进详情、其余行入口禁用、详情内不可改（U7 + C4）/ 保存中列表冻结（U7）/ 内置服务不可删 | 11 |
| `tests/Iris.App.Tests/Settings/TranslationDetailNavigationTests.cs` | 进入详情（三种入口）与返回；新增后自动进入；保存 / 重载后留在详情 | 12 |
| `tests/Iris.App.Tests/Settings/TranslationDetailResetTests.cs` | 切换导航页回到列表；删除当前服务回到列表 | 12 |
| `tests/Iris.App.Tests/Settings/TranslationListFreezeTests.cs` | 两种口径分开断言（U7）：**保存中**列表整体不可用（开关、「⋯」、添加、拖动、进入都不可用），返回可用；**只读**时行内开关、「⋯」、把手、添加不可用，但三个进入口可用、能进详情、详情字段不可改 | 12 |
| `tests/Iris.App.Tests/Settings/SettingsDetailEscapeTests.cs` | 详情态下 Esc 是放弃并关闭而不是返回；行上按 Enter 进入详情且不触发保存 | 12 |

表里最后四个测试类加上 `SettingsWindowNavigationLayoutTests`，对应设计 §8.2 要求的五类新测试（进入 / 返回、切页与删除复位、保存期间冻结、Esc 关闭、窄窗口导航只剩图标）。

### 4.2 修改

**源码与资源**

| 文件 | 改什么 | 任务 |
|---|---|---|
| `src/Iris.App/Themes/Iris.Light.xaml` | 加 `IrisAcrylicTintBrush` | 1 |
| `src/Iris.App/Themes/Iris.Dark.xaml` | 加 `IrisAcrylicTintBrush` | 1 |
| `src/Iris.App/Themes/Iris.SolidFallback.xaml` | 加 `IrisAcrylicTintBrush`；改写注释 :5–11 | 1 |
| `src/Iris.App/Services/ThemeService.cs` | 新增 `ApplyIrisSolidFallbackDictionary`；:212 之后加一行调用 | 1 |
| `src/Iris.App/Themes/Iris.Styles.xaml` | S0-11 的新增键（十一个，含 C5 的 `IrisRowError`）与三处改动 | 2 |
| `src/Iris.App/Settings/SettingsWindow.xaml` | 全文件分段改造（骨架 4；各页 5–9；OCR 11；翻译服务 12；清理 13） | 4–13 |
| `src/Iris.App/Settings/SettingsWindow.xaml.cs` | `ApplyAdaptiveLayout`：加导航切换（4）、删旧分栏部分（12）；`OnViewModelPropertyChanged` 加转调（10、11、12 各一行；11 另加 `CanEdit` 一行，供 T4-OCR 用）；添加服务的 lambda 加自动进入（12）；起拖限制（12）；删无引用的私有成员（13） | 4、10–13 |
| `src/Iris.App/History/HistoryWindow.xaml` | 只加背板着色层（U5）；加在哪个元素上由调查笔记 `E-tint-other-windows.md` 确定；版面、名字、控件都不动 | 4B |
| `src/Iris.App/Translation/TranslateWindow.xaml` | 同上 | 4B |
| `src/Iris.App/Settings/HotkeyRecorderBox.xaml` | **不改**（只动 `SettingsWindow.xaml:1000` 上的布局属性） | — |
| `src/Iris.App/Resources/Strings.resx` | 四条新文字 | 10 |
| `src/Iris.App/Resources/Strings.en-US.resx` | 同上，键顺序一致 | 10 |
| `build/localization/app-catalog.json` | 同上，值与 resx 一致 | 10 |
| `build/localization/exceptions.json` | 仅当审计脚本因「被整条删除的中文注释留下过期登记」而报错时，删掉对应的登记条目（2.13 第 16 条）；不新增任何登记 | 13 |

**既有测试**（每一处都要写进完成报告的「改过的既有测试」）

| 文件 | 用例 | 改什么 | 任务 |
|---|---|---|---|
| `Themes/IrisColorKeyTests.cs`、`IrisStyleKeyResolutionTests.cs`、`IrisStyleResourceTests.cs` | 视其写法而定（2.13 第 5 条） | 覆盖新键；键总数类断言改数 | 1、2 |
| `Settings/SettingsHotkeysIntegrationTests.cs` | W04 | 夹具对齐（S0-13）；页面改造后若取点 / 序号失效再调 | 3、9 |
| `Settings/SelectionSettingsRenderingTests.cs` | 唯一一条 | 夹具对齐；取点 (150, 50) 落进导航后改取点 | 3、4 |
| `Settings/SettingsWindowTranslationUiTests.cs` | 用例 7 | `finally` 还原 Iris 字典（3）；分块重写（12，见 2.9(c) 的表） | 3、12 |
| `Updates/UpdateRenderingTests.cs`、`History/HistoryRenderingTests.cs`、`Localization/LocalizationRenderingTests.cs` | 各一条 | 窗口级补 Iris 颜色字典 | 3 |
| `Settings/OcrSettingsRenderingTests.cs` | 唯一一条 | 夹具对齐与还原（3，S0-13，C8）；详情态截图前加「进入详情」（11） | 3、11 |
| `Settings/NetworkSettingsRenderingTests.cs` | 唯一一条 | 夹具对齐与还原（S0-13，C8） | 3 |
| `Settings/NetworkSettingsFoundationTests.cs` | `ActualFutureSettingsWindow_DisablesEditsSaveAndTests_AllowsCloseAndRendersNotice` | 夹具对齐与还原（S0-13，C8） | 3 |
| `Settings/OcrSettingsUiTests.cs`、`OcrSettingsLifecycleTests.cs` | 取 `OcrTestButton`、两个密码框、`OcrCancelTestButton` 的那些 | 仅当断言要求控件**可见**而它们现在在详情态里时，先进入详情；只取值 / 取 `IsEnabled` 的不动 | 11 |
| `Settings/SettingsDraftAndSaveRegressionTests.cs` | Scenario8（:1792–1808） | 先原样跑；只有确认是「折叠的控件拿不到焦点」导致变红，才在设焦点前加一步进入详情（2.13 第 3 条） | 12 |
| `Localization/LocalizationTests.cs` | :238–268、:270–285 | 预期不用改；红了说明实现违反了 2.9(a)，改实现不改测试 | 6、7、12 |

### 4.3 明确不动

- `specs/` 全部；`tasks/BACKLOG.md`、`tasks/current_task.md`；`.codex/`；`ARCHITECTURE.md`（其中 :1098 / :287 提到 `NavigationView` 与实现不符，只在完成报告里提，不改）。
- `src/Iris.App/App.xaml`、`tests/Iris.App.Tests/TestSupport/WpfTestRunner.cs`（本阶段不新增应用级合并字典）。
- `src/Iris.App/Services/WindowMaterialService.cs`；`SettingsWindowPresenter.cs`、`SettingsWindowSession.cs`；`HotkeyRecorderBox.xaml(.cs)`。
- `SettingsViewModel` 的全部既有文件（只新增一个 partial 文件）；`src/Iris.Core` 全部。
- 历史窗口、翻译窗口的**版面、控件与名字**（阶段 3、4 的事）。这两个窗口的 XAML 本阶段只由 Task 4B 加背板着色层（【用户】U5），除此之外一行不动。
- `Iris.App.csproj`、`Directory.Packages.props`（不加包、不加项目引用；`Themes/*.xaml` 与新的 `.cs` 文件都不需要改 csproj）。

---

## 5. 任务清单

### 5.0 每个任务通用的约定

- 顺序固定，按编号做（Task 4B 排在 Task 4 之后、Task 5 之前）；后一个任务假定前面的都已暂存。
- 每个任务**第一步**：对本任务「Modify」里写的行范围重新 grep 一次行号（前面的任务会让它漂移）；同一个任务内改 `SettingsWindow.xaml` 时从文件末尾往前改（R13）。
- 步骤一律 TDD 顺序：写 / 改测试 → 跑，确认按预期失败 → 实现 → 跑，确认通过 → `git add` 暂存。**任何任务都不提交**，提交只在任务 14。
- 测试命令的前缀（下面各任务只写 `--filter` 的内容）：

  ```
  dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "<过滤式>"
  ```

- 「设置全集」过滤式（凡是改了 `SettingsWindow.xaml` 的任务，跑完自己的过滤组后都要再跑它；预期全绿）：

  ```
  FullyQualifiedName~Iris.App.Tests.Settings|FullyQualifiedName~Iris.App.Tests.Themes|FullyQualifiedName~Iris.App.Tests.Localization|FullyQualifiedName~Iris.App.Tests.Updates|FullyQualifiedName~MouseRenderingTests|FullyQualifiedName~HistoryRenderingTests|FullyQualifiedName~ThemeServicePreviewTests
  ```

- 这些过滤式都没实际跑过（2.13 第 2 条）。任务 1 第一次跑时若发现过滤式不被接受或匹配到 0 条，先把可用的写法定下来、回填到本节，再继续。
- 每个任务都要守的：2.9(a) 的名字与类型；`src/` 不新增中文；既有中文注释只许整条删除；新注释英文；不运行 `src/Iris.App/bin` 下的 exe。
- 既有测试变红时的处理顺序：先判断是不是实现违反了 2.9 的约束（是就改实现）；确属断言对象已经按设计改了形态，才改测试，并把「文件、用例名、改了什么、为什么」记到完成报告的清单里。
- 各任务的「详细步骤」均已按该任务写明的范围展开为完整的 TDD 逐步清单；执行时不得越出该任务写明的范围。

- **开工前置（任务 1 之前做一次）**：
  1. `git status`、`git log -1` 确认在分支 `codex/t041-acrylic-ui`、HEAD 是 `dc08c48`，工作树里除本计划文件与既有的未跟踪文件外没有改动。
  2. `dotnet build Iris.slnx -c Release` 与 `dotnet test Iris.slnx -c Release` 各跑一次，记下汇总行作为「改造前」基线。基线本身若有失败，先报告，不开工。
  3. 用 `build\measure-startup.ps1` 量一次内存，完整输出留作任务 14 的「改造前」数字（笔记里没有阶段 1 的基线数字，2.13 第 21 条）。

### Task 1: 着色键与纯色回退

**目的：** 落地用户裁决 U1 的颜色键与 U4 的纯色回退（S0-6 的键、S0-7 全部）。本任务不碰任何窗口的 XAML，做完后界面没有可见变化。

**Files:**
- Create: `src/Iris.App/Themes/Iris.SolidFallback.Dark.xaml`
- Create: `tests/Iris.App.Tests/Themes/IrisSolidFallbackTests.cs`
- Modify: `src/Iris.App/Themes/Iris.Light.xaml`（10 行，加 1 个键）
- Modify: `src/Iris.App/Themes/Iris.Dark.xaml`（7 行，加 1 个键）
- Modify: `src/Iris.App/Themes/Iris.SolidFallback.xaml`（20 行：注释 :5–11、键 :16–19 之后加 1 个）
- Modify: `src/Iris.App/Services/ThemeService.cs`（:212 之后加一行；:241–269 之后加新方法）
- Test（可能要改）: `tests/Iris.App.Tests/Themes/IrisColorKeyTests.cs`

**范围与要点：**
- 做：五个键、四份字典的值照 S0-7 的表；`ApplyIrisSolidFallbackDictionary` 的四条规则照 S0-7；调用点只有一处。
- 不做：不改 `App.xaml`、`WpfTestRunner`、`WindowMaterialService`；不给任何窗口加 `Background`（那是任务 4）；不处理高对比度。
- 先核实 2.13 第 15 条（启动是否必经 `ThemeService.cs:212`）与第 5 条（`IrisColorKeyTests` 的写法）；退路见 S0-7、S0-6。
- 回退测试每条都要在 `finally` 里移除回退字典并还原浅色主题字典，测试类放进集合 `IrisThemeResources`。

**步骤提纲：**
- [ ] 核实 2.13 第 15、5 条，并用任务 5.0 的前缀试跑一次 `FullyQualifiedName~IrisColorKeyTests`，确认过滤式可用
- [ ] 写 `IrisSolidFallbackTests` 的四条用例（S0-7「测试」1–4）
- [ ] 跑，确认失败（深色回退文件不存在、方法不存在、键不齐）
- [ ] 给四份颜色字典加键 / 建深色回退字典
- [ ] 在 `ThemeService` 里加静态方法与一行调用
- [ ] 按需给 `IrisColorKeyTests` 补新键用例
- [ ] 跑，确认通过；`git add` 上述文件

**详细步骤：**

（以下行号是撰写详细步骤时在 `dc08c48` 上读到的；动手前按 5.0 重新 grep。）

- [ ] **Step 1: 核实启动调用链、既有守卫测试的写法、过滤式可用**

(a) 2.13 第 15 条——**撰写时已读代码核实，成立**。证据链：

| 环节 | 位置 |
|---|---|
| 启动时应用主题 | `src/Iris.App/Hosting/AppBootstrapper.cs:214` `_themeService.Apply(_settingsService.Current.General.Theme);` |
| `ThemeService.Apply` → `ApplyEffectiveTheme()` | `src/Iris.App/Services/ThemeService.cs:77–85`（:84） |
| `ApplyEffectiveTheme` → `ApplyCore`（在 UI 线程上同步执行，否则 `dispatcher.InvokeAsync`） | `ThemeService.cs:121–144`（:132） |
| `ApplyCore`：Light / Dark → `SafeApplyWpfUiTheme` | `ThemeService.cs:146–168`（:154、:159） |
| `ApplyCore`：System → `SafeApplySystemTheme` → `SafeApplyWpfUiTheme` | `ThemeService.cs:165` → `:271–294`（:285） |
| `SafeApplyWpfUiTheme` 里的 `ApplyIrisThemeDictionary(theme);` | `ThemeService.cs:212`（在 :210–213 的 `try` 块里） |

三种主题设置都汇到 :212，所以 S0-7 的调用点成立，不需要走「在启动处补一行」的退路。`AppBootstrapper.cs:214` 之前只有 `_stopRequested` 的提前返回（:204、:206、:208），那是应用正在退出。执行时用下面两条确认行号没漂（期望各 1 行）：

```
grep -n "_themeService.Apply(" src/Iris.App/Hosting/AppBootstrapper.cs
grep -n "ApplyIrisThemeDictionary(theme);" src/Iris.App/Services/ThemeService.cs
```

任一条 0 行：停下来报告，不自行另造入口（S0-7）。

(b) 2.13 第 5 条——**撰写时已读文件核实**：`tests/Iris.App.Tests/Themes/IrisColorKeyTests.cs` 是**逐键硬编码**（`IrisColorKeys_AreTranslucent` 的 `InlineData` 在 :14–17，`SolidFallbackKeys_AreOpaque` 的在 :30–33），并且 `SolidFallbackKeys_AreOpaque`（:36）只按文件名加载浅色那一份回退字典。所以 Step 7 要改它。`SolidFallback_OverridesEveryIrisColorKey`（:42–76）遍历键，不用改。

(c) 试跑过滤式（2.13 第 2 条）：

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~IrisColorKeyTests"
```

期望：9 条通过（4 + 4 + 1），0 失败。

`global.json` 把测试运行器设成了 `Microsoft.Testing.Platform`，这条命令可能不被接受。退路按顺序试，**哪一种可用就把它回填到 5.0**，后面所有任务照用：

1. 报「不接受位置参数」之类：把项目路径改成 `--project tests/Iris.App.Tests/Iris.App.Tests.csproj`。
2. 报「未知选项 `--filter`」或匹配到 0 条：改用 xunit.v3 自带的过滤选项，写法先用 `dotnet test --project tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --help` 核实（凭记忆是 `--filter-class "*IrisColorKeyTests"`，可重复给多个值；**未核实**）。本计划各处的 `FullyQualifiedName~X|FullyQualifiedName~Y` 一律换成对每个 X、Y 各给一个类名通配。
3. 两种都不行：停下来报告。

- [ ] **Step 2: 写 `IrisSolidFallbackTests`（新文件，整文件如下）**

Create `tests/Iris.App.Tests/Themes/IrisSolidFallbackTests.cs`。共 18 条用例：1 + 12 + 2 + 3。构造 `ThemeService` 与「不支持」的材质服务的写法照抄自 `IrisThemeDictionaryTests.cs:60–62、:89–96`，所以 S0-7 测试第 4 条不用降级。

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Iris.App.Services;
using Iris.App.Tests.TestSupport;
using Iris.Core.Settings;
using NSubstitute;
using Shouldly;
using Wpf.Ui.Appearance;
using Xunit;

namespace Iris.App.Tests.Themes;

// 后三条用例会改 Application.Current.Resources.MergedDictionaries，所以放进不并行的集合；
// 改了全局资源的用例都在 finally 里调 Cleanup()。
[Collection("IrisThemeResources")]
public sealed class IrisSolidFallbackTests
{
    private const string PackRoot = "pack://application:,,,/Iris.App;component/";
    private const string LightFile = "Themes/Iris.Light.xaml";
    private const string DarkFile = "Themes/Iris.Dark.xaml";
    private const string FallbackLightFile = "Themes/Iris.SolidFallback.xaml";
    private const string FallbackDarkFile = "Themes/Iris.SolidFallback.Dark.xaml";
    private const string CardFillKey = "IrisAcrylicCardFillBrush";

    // 四份字典都恰好是这五个键（按序数排序后用逗号连起来）。
    private const string ExpectedKeys =
        "IrisAcrylicCardFillBrush,IrisAcrylicCardStrokeBrush,IrisAcrylicRowFillBrush,IrisAcrylicRowStrokeBrush,IrisAcrylicTintBrush";

    [Fact]
    public Task AllFourColorDictionaries_HaveTheSameKeys() => WpfTestRunner.RunAsync(() =>
    {
        // 每一份都对着写死的五个键比，而不是四份互相比：四份一起少一个键时「两两相同」照样成立。
        SortedKeys(LightFile).ShouldBe(ExpectedKeys);
        SortedKeys(DarkFile).ShouldBe(ExpectedKeys);
        SortedKeys(FallbackLightFile).ShouldBe(ExpectedKeys);
        SortedKeys(FallbackDarkFile).ShouldBe(ExpectedKeys);
        return Task.CompletedTask;
    });

    // 值逐个钉住：深色回退色是算出来的合成色，错一位肉眼看不出来。
    [Theory]
    [InlineData(LightFile, "IrisAcrylicTintBrush", 0xB3F3F3F3u)]
    [InlineData(DarkFile, "IrisAcrylicTintBrush", 0xAD242424u)]
    [InlineData(FallbackLightFile, "IrisAcrylicTintBrush", 0xFFF3F3F3u)]
    [InlineData(FallbackLightFile, "IrisAcrylicCardFillBrush", 0xFFFFFFFFu)]
    [InlineData(FallbackLightFile, "IrisAcrylicCardStrokeBrush", 0xFFE0E0E0u)]
    [InlineData(FallbackLightFile, "IrisAcrylicRowFillBrush", 0xFFF9F9F9u)]
    [InlineData(FallbackLightFile, "IrisAcrylicRowStrokeBrush", 0xFFE5E5E5u)]
    [InlineData(FallbackDarkFile, "IrisAcrylicTintBrush", 0xFF242424u)]
    [InlineData(FallbackDarkFile, "IrisAcrylicCardFillBrush", 0xFF3A3A3Au)]
    [InlineData(FallbackDarkFile, "IrisAcrylicCardStrokeBrush", 0xFF3F3F3Fu)]
    [InlineData(FallbackDarkFile, "IrisAcrylicRowFillBrush", 0xFF353535u)]
    [InlineData(FallbackDarkFile, "IrisAcrylicRowStrokeBrush", 0xFF393939u)]
    public Task ColorDictionary_HoldsTheDecidedValue(string file, string key, uint argb) => WpfTestRunner.RunAsync(() =>
    {
        Load(file)[key].ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(ToColor(argb));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(FallbackLightFile)]
    [InlineData(FallbackDarkFile)]
    public Task SolidFallbackDictionary_IsFullyOpaque(string file) => WpfTestRunner.RunAsync(() =>
    {
        var dictionary = Load(file);
        var keys = dictionary.Keys.Cast<object>().ToList();
        // 防空转：键快照为空时下面的循环一条都不跑。
        keys.ShouldNotBeEmpty();
        foreach (var key in keys)
        {
            dictionary[key].ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)255, $"{key} in {file}");
        }

        return Task.CompletedTask;
    });
```

（同一个文件，接下一段。）

```csharp
    [Fact]
    public Task ApplyIrisSolidFallbackDictionary_MergesSwapsAndRemoves() => WpfTestRunner.RunAsync(() =>
    {
        try
        {
            // 起点：浅色主题字典在场、没有回退字典、键取到半透明值。少了这几句，后面的断言可能恒真。
            Cleanup();
            ThemeDictionaryIndex().ShouldBeGreaterThanOrEqualTo(0);
            FallbackSources().ShouldBeEmpty();
            CurrentCardFill().ShouldBe(ToColor(0x66FFFFFFu));

            ThemeService.ApplyIrisSolidFallbackDictionary(ApplicationTheme.Dark, acrylicSupported: false);

            FallbackSources().ShouldHaveSingleItem().EndsWith(FallbackDarkFile, StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
            CurrentCardFill().ShouldBe(ToColor(0xFF3A3A3Au));
            // 位置决定覆盖优先级：必须紧跟在 Iris 主题字典后面。
            FallbackIndex().ShouldBe(ThemeDictionaryIndex() + 1);

            ThemeService.ApplyIrisSolidFallbackDictionary(ApplicationTheme.Light, acrylicSupported: false);

            // 恰好一份：换主题不能把旧的那份留下。
            FallbackSources().ShouldHaveSingleItem().EndsWith(FallbackLightFile, StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
            CurrentCardFill().ShouldBe(ToColor(0xFFFFFFFFu));
            FallbackIndex().ShouldBe(ThemeDictionaryIndex() + 1);

            ThemeService.ApplyIrisSolidFallbackDictionary(ApplicationTheme.Light, acrylicSupported: true);

            FallbackSources().ShouldBeEmpty();
            CurrentCardFill().ShouldBe(ToColor(0x66FFFFFFu));
        }
        finally
        {
            Cleanup();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Apply_ThroughThemeService_MergesTheFallbackWhenAcrylicIsUnsupported() => WpfTestRunner.RunAsync(() =>
    {
        // ThemeService.Apply(AppTheme) 忽略传入值、改用设置里的主题（见 IrisThemeDictionaryTests），
        // 所以主题写进设置；传 Light 却出现深色回退值，才证明走的是整条路径。
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Dark } });
        var unsupported = new WindowMaterialService(() => 19045, () => false);
        using var service = new ThemeService(settings, visualThemeApplier: _ => { }, windowMaterialService: unsupported);
        try
        {
            Cleanup();
            unsupported.IsAcrylicSupported.ShouldBeFalse();
            FallbackSources().ShouldBeEmpty();

            service.Apply(AppTheme.Light);

            FallbackSources().ShouldHaveSingleItem().EndsWith(FallbackDarkFile, StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
            CurrentCardFill().ShouldBe(ToColor(0xFF3A3A3Au));
            FallbackIndex().ShouldBe(ThemeDictionaryIndex() + 1);

            // 跟着主题切换走：设置改成浅色再应用一次，回退字典换成浅色那份，仍然恰好一份。
            settings.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Light } });
            service.Apply(AppTheme.Dark);

            FallbackSources().ShouldHaveSingleItem().EndsWith(FallbackLightFile, StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
            CurrentCardFill().ShouldBe(ToColor(0xFFFFFFFFu));
        }
        finally
        {
            Cleanup();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Apply_ThroughThemeService_RemovesTheFallbackWhenThereIsNoMaterialService() => WpfTestRunner.RunAsync(() =>
    {
        // 没有材质服务（多数测试如此）按「支持亚克力」处理：回退字典不该在场。
        var settings = Substitute.For<ISettingsService>();
        settings.Current.Returns(new AppSettings { General = new GeneralSettings { Theme = AppTheme.Light } });
        using var service = new ThemeService(settings, visualThemeApplier: _ => { });
        try
        {
            Cleanup();
            ThemeService.ApplyIrisSolidFallbackDictionary(ApplicationTheme.Light, acrylicSupported: false);
            // 前置：回退字典确实在场，否则「应用之后没有」什么也证明不了。
            FallbackSources().ShouldHaveSingleItem();

            service.Apply(AppTheme.Light);

            FallbackSources().ShouldBeEmpty();
            CurrentCardFill().ShouldBe(ToColor(0x66FFFFFFu));
        }
        finally
        {
            Cleanup();
        }

        return Task.CompletedTask;
    });
```

（同一个文件，接最后一段：辅助方法。）

```csharp
    private static ResourceDictionary Load(string file) =>
        new() { Source = new Uri(PackRoot + file, UriKind.Absolute) };

    private static string SortedKeys(string file) =>
        string.Join(
            ",",
            Load(file).Keys.Cast<object>().Select(key => key.ShouldBeOfType<string>()).OrderBy(key => key, StringComparer.Ordinal));

    private static Color ToColor(uint argb) =>
        Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));

    private static Color CurrentCardFill() =>
        Application.Current!.Resources[CardFillKey].ShouldBeOfType<SolidColorBrush>().Color;

    private static string[] MergedSources() =>
        Application.Current!.Resources.MergedDictionaries
            .Select(dictionary => dictionary.Source?.OriginalString ?? string.Empty)
            .ToArray();

    private static bool IsFallback(string source) =>
        source.EndsWith(FallbackLightFile, StringComparison.OrdinalIgnoreCase)
        || source.EndsWith(FallbackDarkFile, StringComparison.OrdinalIgnoreCase);

    private static bool IsThemeDictionary(string source) =>
        source.EndsWith(LightFile, StringComparison.OrdinalIgnoreCase)
        || source.EndsWith(DarkFile, StringComparison.OrdinalIgnoreCase);

    private static string[] FallbackSources() => MergedSources().Where(IsFallback).ToArray();

    private static int FallbackIndex() => Array.FindIndex(MergedSources(), IsFallback);

    private static int ThemeDictionaryIndex() => Array.FindIndex(MergedSources(), IsThemeDictionary);

    // 清理不借用被测方法：它要是删不干净，回退字典就漏给后面的测试了。
    // 整个测试进程只有一个 Application，所以同时把 Iris 主题字典还原成浅色。
    private static void Cleanup()
    {
        var dictionaries = Application.Current!.Resources.MergedDictionaries;
        for (var index = dictionaries.Count - 1; index >= 0; index--)
        {
            if (IsFallback(dictionaries[index].Source?.OriginalString ?? string.Empty))
            {
                dictionaries.RemoveAt(index);
            }
        }

        ThemeService.ApplyIrisThemeDictionary(ApplicationTheme.Light);
    }
}
```

- [ ] **Step 3: 跑，确认失败（第一种红：编译不过）**

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~IrisSolidFallbackTests"
```

期望：**编译失败**，错误是 `CS0117`（`ThemeService` 不包含 `ApplyIrisSolidFallbackDictionary` 的定义），出现在新文件里调用该方法的几行；没有任何用例被执行。出现别的编译错误（命名空间、类型名）说明测试文件本身抄错了，先改到只剩 `CS0117`。

- [ ] **Step 4: 在 `ThemeService` 里加静态方法与一行调用**

Modify `src/Iris.App/Services/ThemeService.cs`，两处。从文件靠后的那一处先改。

(a) 新方法，紧接在 `ApplyIrisThemeDictionary` 之后。旧文本（:266–271，方法结尾到下一个方法开头）：

```csharp
        dictionaries.Add(target);
    }

    private void SafeApplySystemTheme()
```

新文本：

```csharp
        dictionaries.Add(target);
    }

    private static readonly string[] IrisSolidFallbackDictionaryNames = ["Iris.SolidFallback.xaml", "Iris.SolidFallback.Dark.xaml"];

    /// <summary>
    /// Merges the opaque Iris colour dictionary when the system has no acrylic backdrop, and
    /// removes it when it does. The dictionary goes right after the Iris theme dictionary: later
    /// entries in MergedDictionaries win, so the opaque values override the translucent ones.
    /// </summary>
    internal static void ApplyIrisSolidFallbackDictionary(ApplicationTheme theme, bool acrylicSupported)
    {
        var dictionaries = Application.Current?.Resources.MergedDictionaries;
        if (dictionaries is null)
        {
            return;
        }

        for (var index = dictionaries.Count - 1; index >= 0; index--)
        {
            if (SourceEndsWithAny(dictionaries[index], IrisSolidFallbackDictionaryNames))
            {
                dictionaries.RemoveAt(index);
            }
        }

        if (acrylicSupported)
        {
            return;
        }

        var fallback = new ResourceDictionary
        {
            Source = new Uri(
                theme == ApplicationTheme.Dark
                    ? "pack://application:,,,/Iris.App;component/Themes/Iris.SolidFallback.Dark.xaml"
                    : "pack://application:,,,/Iris.App;component/Themes/Iris.SolidFallback.xaml",
                UriKind.Absolute),
        };

        for (var index = 0; index < dictionaries.Count; index++)
        {
            if (SourceEndsWithAny(dictionaries[index], IrisThemeDictionaryNames))
            {
                dictionaries.Insert(index + 1, fallback);
                return;
            }
        }

        dictionaries.Add(fallback);
    }

    private static bool SourceEndsWithAny(ResourceDictionary dictionary, string[] names)
    {
        var source = dictionary.Source?.OriginalString;
        return source is not null && names.Any(name => source.EndsWith(name, StringComparison.OrdinalIgnoreCase));
    }

    private void SafeApplySystemTheme()
```

`ApplyIrisThemeDictionary` 本身一个字不改（它 :261 的内联判断留着，不改用新的辅助方法）。

(b) 调用点。旧文本（:210–213）：

```csharp
        try
        {
            ApplyIrisThemeDictionary(theme);
        }
```

新文本：

```csharp
        try
        {
            ApplyIrisThemeDictionary(theme);
            ApplyIrisSolidFallbackDictionary(theme, _windowMaterialService?.IsAcrylicSupported ?? true);
        }
```

新行**必须留在这个 `try` 块里面**：`IrisThemeDictionaryTests.Apply_DoesNotLetAMaterialFailureEscape`（:81–102）给的材质服务一读系统版本号就抛异常，`IsAcrylicSupported` 会把它抛出来；放到 `try` 外面那条既有用例就红，应用启动也会因此崩。:206–209 的既有英文注释不动。

- [ ] **Step 5: 再跑，确认失败（第二种红：字典还没跟上）**

命令同 Step 3。期望：编译通过；18 条里 **12 条失败、6 条通过**。

| 用例 | 期望 |
|---|---|
| `AllFourColorDictionaries_HaveTheSameKeys` | 红：浅色字典还只有四个键 |
| `ColorDictionary_HoldsTheDecidedValue` | 8 红 4 绿：三份既有字典的 `IrisAcrylicTintBrush` 取到 `null`；`Iris.SolidFallback.Dark.xaml` 的五行抛 `IOException`（找不到资源）；浅色回退的四个现值是绿的 |
| `SolidFallbackDictionary_IsFullyOpaque` | 深色那行红（`IOException`），浅色那行绿 |
| `ApplyIrisSolidFallbackDictionary_MergesSwapsAndRemoves` | 红：`(Dark, false)` 加载深色回退文件时抛 `IOException` |
| `Apply_ThroughThemeService_MergesTheFallbackWhenAcrylicIsUnsupported` | 红：异常被 `ThemeService` 的 `try` 吞掉，回退字典 0 份，`ShouldHaveSingleItem` 不成立 |
| `Apply_ThroughThemeService_RemovesTheFallbackWhenThereIsNoMaterialService` | 绿（只用到浅色回退文件，它已经存在） |

条数对不上时先弄清原因再往下走；尤其是「全绿」说明测试没有牙。

- [ ] **Step 6: 给四份颜色字典加键 / 建深色回退字典**

(a) Modify `src/Iris.App/Themes/Iris.Light.xaml`。旧文本（:3–9）：

```xml
    <!-- The only colours Iris owns. They sit on top of the acrylic backdrop, so they are
         translucent. Iris.SolidFallback.xaml holds opaque equivalents for systems without
         acrylic, but nothing merges it yet - phase 1 only creates it (see Iris.Styles.xaml). -->
    <SolidColorBrush x:Key="IrisAcrylicCardFillBrush" Color="#66FFFFFF" />
    <SolidColorBrush x:Key="IrisAcrylicCardStrokeBrush" Color="#14000000" />
    <SolidColorBrush x:Key="IrisAcrylicRowFillBrush" Color="#4DFFFFFF" />
    <SolidColorBrush x:Key="IrisAcrylicRowStrokeBrush" Color="#0F000000" />
```

新文本：

```xml
    <!-- The only colours Iris owns. They sit on top of the acrylic backdrop, so they are
         translucent. Iris.SolidFallback.xaml / Iris.SolidFallback.Dark.xaml hold opaque
         equivalents; ThemeService merges the matching one right after this dictionary on
         systems without acrylic. All four dictionaries must define the same keys. -->
    <!-- Window tint laid over the backdrop: #F3F3F3 at 70% (0.70 * 255 = 179 = B3). -->
    <SolidColorBrush x:Key="IrisAcrylicTintBrush" Color="#B3F3F3F3" />
    <SolidColorBrush x:Key="IrisAcrylicCardFillBrush" Color="#66FFFFFF" />
    <SolidColorBrush x:Key="IrisAcrylicCardStrokeBrush" Color="#14000000" />
    <SolidColorBrush x:Key="IrisAcrylicRowFillBrush" Color="#4DFFFFFF" />
    <SolidColorBrush x:Key="IrisAcrylicRowStrokeBrush" Color="#0F000000" />
```

（:3–5 那段注释写着「还没有任何地方合并回退字典」，本任务之后不再属实，所以一并改写；这一处骨架的 Files 没单列，记进完成报告。）

(b) Modify `src/Iris.App/Themes/Iris.Dark.xaml`。旧文本（:3）：

```xml
    <SolidColorBrush x:Key="IrisAcrylicCardFillBrush" Color="#1AFFFFFF" />
```

新文本：

```xml
    <!-- Window tint laid over the backdrop: #242424 at 68% (0.68 * 255 = 173 = AD). -->
    <SolidColorBrush x:Key="IrisAcrylicTintBrush" Color="#AD242424" />
    <SolidColorBrush x:Key="IrisAcrylicCardFillBrush" Color="#1AFFFFFF" />
```

(c) Modify `src/Iris.App/Themes/Iris.SolidFallback.xaml`。把 :3–19（整段注释加四个画刷）换成下面这段；:12–15 讲两条描边来历的那几句原样保留在新注释的后半：

```xml
    <!-- Opaque equivalents of the IrisAcrylic* keys for LIGHT theme, so the cards keep their
         separation on a flat background. Iris.SolidFallback.Dark.xaml is the dark counterpart.
         ThemeService.ApplyIrisSolidFallbackDictionary merges the one that matches the theme when
         the system has no acrylic backdrop, and removes it otherwise. It is inserted right AFTER
         the Iris theme dictionary: later entries in MergedDictionaries win, and these values must
         win over the translucent set. The key set must stay identical to Iris.Light.xaml.
         The two strokes are alpha 31 and alpha 26 composited over white
         (#1F000000 -> #E0E0E0, #1A000000 -> #E5E5E5), deliberately stronger than the light acrylic
         set (#14000000 -> #EBEBEB, #0F000000 -> #F0F0F0), for the same reason that set is faint:
         a 1px line disappears against a flat fill. -->
    <SolidColorBrush x:Key="IrisAcrylicTintBrush" Color="#FFF3F3F3" />
    <SolidColorBrush x:Key="IrisAcrylicCardFillBrush" Color="#FFFFFFFF" />
    <SolidColorBrush x:Key="IrisAcrylicCardStrokeBrush" Color="#FFE0E0E0" />
    <SolidColorBrush x:Key="IrisAcrylicRowFillBrush" Color="#FFF9F9F9" />
    <SolidColorBrush x:Key="IrisAcrylicRowStrokeBrush" Color="#FFE5E5E5" />
```

文件第 1–2 行（根元素）与最后一行 `</ResourceDictionary>` 不动。

(d) Create `src/Iris.App/Themes/Iris.SolidFallback.Dark.xaml`（整文件；`Themes/*.xaml` 新文件不用改 csproj，2.8）：

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- Opaque equivalents of the IrisAcrylic* keys for DARK theme; see Iris.SolidFallback.xaml
         for when and where this dictionary is merged. The key set must stay identical to
         Iris.Dark.xaml.
         The tint is the dark acrylic tint colour (#242424) made opaque. The other four are the
         translucent whites of Iris.Dark.xaml composited over that tint, so cards keep the same
         lightness relation to the window as they have on acrylic:
         #1AFFFFFF -> #3A3A3A, #1FFFFFFF -> #3F3F3F, #14FFFFFF -> #353535, #19FFFFFF -> #393939
         (channel = 36 + alpha / 255 * 219, rounded). -->
    <SolidColorBrush x:Key="IrisAcrylicTintBrush" Color="#FF242424" />
    <SolidColorBrush x:Key="IrisAcrylicCardFillBrush" Color="#FF3A3A3A" />
    <SolidColorBrush x:Key="IrisAcrylicCardStrokeBrush" Color="#FF3F3F3F" />
    <SolidColorBrush x:Key="IrisAcrylicRowFillBrush" Color="#FF353535" />
    <SolidColorBrush x:Key="IrisAcrylicRowStrokeBrush" Color="#FF393939" />
</ResourceDictionary>
```

- [ ] **Step 7: 给 `IrisColorKeyTests` 补新键（改既有测试）**

Modify `tests/Iris.App.Tests/Themes/IrisColorKeyTests.cs`，三处；既有中文注释一个字不动。

(a) `IrisColorKeys_AreTranslucent`：在 :17 `[InlineData("IrisAcrylicRowStrokeBrush")]` 之后、:18 方法签名之前插入一行：

```csharp
    [InlineData("IrisAcrylicTintBrush")]
```

(b) `SolidFallbackKeys_AreOpaque`：同样在 :33 `[InlineData("IrisAcrylicRowStrokeBrush")]` 之后、:34 方法签名之前插入同一行。

(c) 同一个方法的方法体，把深色回退也纳入。旧文本（:36–39）：

```csharp
        var fallback = new ResourceDictionary { Source = new Uri("pack://application:,,,/Iris.App;component/Themes/Iris.SolidFallback.xaml", UriKind.Absolute) };
        var brush = fallback[key].ShouldBeOfType<SolidColorBrush>();
        brush.Color.A.ShouldBe((byte)255);
        return Task.CompletedTask;
```

新文本：

```csharp
        var fallback = new ResourceDictionary { Source = new Uri("pack://application:,,,/Iris.App;component/Themes/Iris.SolidFallback.xaml", UriKind.Absolute) };
        var brush = fallback[key].ShouldBeOfType<SolidColorBrush>();
        brush.Color.A.ShouldBe((byte)255);
        var darkFallback = new ResourceDictionary { Source = new Uri("pack://application:,,,/Iris.App;component/Themes/Iris.SolidFallback.Dark.xaml", UriKind.Absolute) };
        darkFallback[key].ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)255);
        return Task.CompletedTask;
```

`SolidFallback_OverridesEveryIrisColorKey`（:42–76）不改：四份字典的键集合相等已经由新文件的 `AllFourColorDictionaries_HaveTheSameKeys` 守着。

- [ ] **Step 8: 跑，确认通过**

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~IrisSolidFallbackTests|FullyQualifiedName~IrisColorKeyTests"
```

期望：29 条通过、0 失败（`IrisSolidFallbackTests` 18 条；`IrisColorKeyTests` 由 9 条变 11 条）。

再跑本任务的验证命令（见下方「验证命令」）与 5.0 的「设置全集」，期望全绿、构建零警告。重点看两条既有用例：

- `IrisThemeDictionaryTests.Apply_DoesNotLetAMaterialFailureEscape`——红了说明 Step 4(b) 的新行被放到了 `try` 外面。
- `IrisThemeDictionaryTests.ApplyIrisThemeDictionary_ReplacesTheMergedIrisDictionary`——红了说明有用例把回退字典漏在了共享的 `Application` 里（检查新测试的 `finally`）。

- [ ] **Step 9: 暂存（不提交）**

```
git add src/Iris.App/Themes/Iris.Light.xaml src/Iris.App/Themes/Iris.Dark.xaml src/Iris.App/Themes/Iris.SolidFallback.xaml src/Iris.App/Themes/Iris.SolidFallback.Dark.xaml src/Iris.App/Services/ThemeService.cs tests/Iris.App.Tests/Themes/IrisSolidFallbackTests.cs tests/Iris.App.Tests/Themes/IrisColorKeyTests.cs
git status --short -- src tests
```

期望：上面七个路径都在暂存区（两个 `A`、五个 `M`），`src`、`tests` 下没有别的改动。

**⚠ 与裁决不符之处（任务 1）**

没有需要改裁决的冲突。下面三条是照裁决做时必须知道的细节，都已写进上面的步骤：

1. S0-7 写「`ThemeService.cs:212` 的下一行加调用」。:212 在一个 `try` 块里（:210–213），新行必须加在**块内**；加在块外会让既有用例 `Apply_DoesNotLetAMaterialFailureEscape` 变红（证据：`IrisThemeDictionaryTests.cs:89–98` 的材质服务一读版本号就抛异常，而 `WindowMaterialService.cs:30` 的 `IsAcrylicSupported` 会读它）。附带后果：`IsAcrylicSupported` 抛异常时这一次不合并回退字典，只记一条警告日志。
2. `Iris.Light.xaml:3–5` 的英文注释写着「nothing merges it yet」，本任务之后不属实，Step 6(a) 一并改写了。骨架的 Files 对这个文件只写了「加 1 个键」，S0-11 的改动表也没列这一处。
3. `Iris.Styles.xaml:24–26` 的英文注释同样写着回退字典「not wired up in phase 1」。那个文件不在任务 1 的 Files 里，挪到任务 2 的 Step 6 里改（只改注释，`IrisCard` 样式本身不动）。

**验证命令：** `FullyQualifiedName~IrisSolidFallbackTests|FullyQualifiedName~IrisColorKeyTests|FullyQualifiedName~IrisThemeDictionaryTests|FullyQualifiedName~ThemeServicePreviewTests|FullyQualifiedName~AcrylicWindowTests` —— 预期全绿；随后跑「设置全集」，预期全绿（界面未变）。

**要改的既有测试：** `Themes/IrisColorKeyTests.cs`——仅当它逐键硬编码时，为 `IrisAcrylicTintBrush` 补用例；若它按文件名只加载一份回退字典，把深色那份也纳入 `SolidFallbackKeys_AreOpaque`。

### Task 2: 主题层新增样式键

**目的：** 把后面所有页面任务要用的样式与数值键一次加进主题层（S0-11），让页面任务只「用」不「造」。做完后界面仍没有可见变化（新键没有使用者；`IrisNavItem` 也还没人用）。

**Files:**
- Modify: `src/Iris.App/Themes/Iris.Styles.xaml`（268 行：间距键区 :9–22 之后加数值键；`IrisRowTitle` :41–46、`IrisRowDescription` :48–53 视情况补换行；`IrisNavItem` :260–267 改 `Padding` 与注释；文件末尾追加新样式）
- Test: `tests/Iris.App.Tests/Themes/IrisStyleKeyResolutionTests.cs`、`IrisStyleResourceTests.cs`、`IrisStyleRenderingTests.cs`（给新键加用例；写法照这三个文件里对既有 19 个键的写法）

**范围与要点：**
- 做：S0-11「新增」表里除 `IrisAcrylicTintBrush` 以外的十一个键（含 C5 的 `IrisRowError`）；「改动阶段 1 的键」表里前两行。
- 本任务的撰写者要定下并写明：`IrisPageTitle` 的字号与字重；`IrisListRowItem` 各状态（悬停、选中、键盘焦点三种交互状态加禁用；不做按下态，C1）各用哪个画刷（只能用 WPF-UI 既有键与 Iris 既有键，Iris 颜色画刷用 `DynamicResource`）；`IrisBreadcrumbButton` 的模板。
- `IrisListRowItem` 必须保留键盘焦点的可见提示，且**不能**吞掉 `ListBoxItem` 的选中行为（两个列表的 `SelectedItem` 绑定照常工作）。
- `IrisRowIcon` 设图标大小的写法照 `IrisIconButton`（`Iris.Styles.xaml:205–258`）里的既有写法。
- 不做：不改 `IrisCard`、`IrisRowCard`、`IrisSwitchCheckBox`、`IrisIconButton`、`IrisTag*`；不新增颜色键；不加动画。
- 风险：R5（样式要能被内联样式 `BasedOn`，所以都得是带 `x:Key` 的显式样式，不做隐式样式）。

**步骤提纲：**
- [ ] 读三个守卫测试文件，弄清它们是遍历还是逐键（2.13 第 5 条）
- [ ] 为十一个新键写「能解析、类型正确、数值正确」的用例（`IrisRowError` 另断言空串折叠、非空可见、不设前景色）；为 `IrisNavItem` 的新 `Padding` 改 / 加断言；为 `IrisListRowItem` 写一条离屏渲染用例（选中与未选中外观不同）
- [ ] 跑，确认失败
- [ ] 在 `Iris.Styles.xaml` 里加键、改 `IrisNavItem`、补换行
- [ ] 跑，确认通过；`git add`

**详细步骤：**

（行号是撰写时在 `dc08c48` 上读到的；任务 1 不改 `Iris.Styles.xaml`，所以不会漂，但动手前仍按 5.0 重新 grep。）

- [ ] **Step 1: 核实守卫测试的写法与两个 WPF-UI 事实（撰写时已核实，执行时复核）**

(a) 2.13 第 5 条，三个守卫文件的写法：

| 文件 | 写法 | 对本任务的含义 |
|---|---|---|
| `Themes/IrisStyleResourceTests.cs`（34 行） | **逐键硬编码**，两个 `Theory`（圆角 :16–18、间距 :26–28），只断言类型 | 新键的用例加在这里 |
| `Themes/IrisStyleKeyResolutionTests.cs`（106 行） | **遍历**：加载 `Iris.Styles.xaml`，收集每个样式的 `Setter` 与触发器 `Setter` 上的 `{DynamicResource}` 键，逐个要求能解析 | 新样式**自动被覆盖，本文件不改**。但它收不到写在模板**体内**元素属性上的引用（:18–24 的注释）——所以新模板的体内一律用 `TemplateBinding` / `StaticResource`，`DynamicResource` 只写在 `Setter` 上 |
| `Themes/IrisStyleRenderingTests.cs`（207 行） | 一条 `Theory`，手工拼面板、出证据图 | `IrisListRowItem` 的离屏用例加在这里；既有那条不改 |

没有「键总数等于 19」的断言，也没有任何测试断言 `IrisNavItem` 的 `Padding`（`grep -rn "IrisNavItem" tests/` 为 0 行）。所以本任务**不改任何既有断言**，只新增。

(b) `ui:SymbolIcon` 的大小与颜色：撰写时对 `C:\Users\28910\.nuget\packages\wpf-ui\4.3.0\lib\net462\Wpf.Ui.dll` 做了只读反射——继承链 `SymbolIcon : FontIcon : IconElement : FrameworkElement`，`FontSize`、`Foreground` 两个属性都存在（`Wpf.Ui.xml:2616、:2639` 也有登记）。所以 `IrisRowIcon` 用 `FontSize` 设大小。复核办法：Step 2 的测试里写了 `icon.FontSize`，属性不存在会直接编译失败。

(c) `DefaultControlFocusVisualStyle`（WPF-UI 的键盘焦点框样式键）：撰写时只确认了这个字符串在 `Wpf.Ui.dll` 里出现 26 次，**没有确认它是可解析的资源键**。复核靠两条测试：Step 2 的 `BreadcrumbButton_…` 用 `FindResource` 取它（键不存在就抛异常），`IrisStyleKeyResolutionTests` 也会自动检查。退路：这个键解析不了，就把 Step 6 里两处 `FocusVisualStyle` 的 `Setter` 删掉（`IrisBreadcrumbButton` 靠 `BasedOn` 继承 WPF-UI 按钮自带的焦点框，`IrisListRowItem` 用 WPF 默认的虚线焦点框），同时删掉测试里对应的两条断言，并记进偏差清单。

(d) `Wpf.Ui.Controls` 命名空间里有 `TextBlock`、`Button`，没有 `ListBox`、`ListBoxItem`、`Border`（同一次反射）。同时 `using` 了两个命名空间的测试文件里，`TextBlock` 必须写全限定名。

- [ ] **Step 2: 在 `IrisStyleResourceTests.cs` 里给新键加用例**

Modify `tests/Iris.App.Tests/Themes/IrisStyleResourceTests.cs`，三处；既有中文注释（:12–14）不动。

(a) `using`。旧文本（:1–5）：

```csharp
using System.Threading.Tasks;
using System.Windows;
using Iris.App.Tests.TestSupport;
using Shouldly;
using Xunit;
```

新文本（不要加 `using Wpf.Ui.Controls;`，WPF-UI 的类型在下面写全限定名）：

```csharp
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Iris.App.Tests.TestSupport;
using Shouldly;
using Xunit;
```

(b) 间距键的 `Theory`：在 :28 `[InlineData("IrisRowGap")]` 之后、:29 方法签名之前插入一行：

```csharp
    [InlineData("IrisPagePadding")]
```

(c) 在类的最后一个 `}` 之前（:33 之后）追加下面两段。第一段：

```csharp

    // 以下是阶段 2 的新键。这里断言了具体数值，与上面「不断言数值」的口径不同：
    // 176 / 48 / 24,16,24,16 是设计定下的版面数字，代码隐藏与各页的宽度预算都按它们算。
    [Theory]
    [InlineData("IrisNavWidth", 176d)]
    [InlineData("IrisNavCompactWidth", 48d)]
    public Task NavWidthResources_HoldTheDesignValues(string key, double expected) => WpfTestRunner.RunAsync(() =>
    {
        Application.Current.TryFindResource(key).ShouldBeOfType<double>().ShouldBe(expected);
        return Task.CompletedTask;
    });

    [Fact]
    public Task PagePadding_HoldsTheDesignValue() => WpfTestRunner.RunAsync(() =>
    {
        Application.Current.TryFindResource("IrisPagePadding").ShouldBeOfType<Thickness>().ShouldBe(new Thickness(24, 16, 24, 16));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("IrisPageTitle", typeof(TextBlock))]
    [InlineData("IrisRowIcon", typeof(Wpf.Ui.Controls.SymbolIcon))]
    [InlineData("IrisStatusText", typeof(TextBlock))]
    [InlineData("IrisRowError", typeof(TextBlock))]
    [InlineData("IrisListRowItem", typeof(ListBoxItem))]
    [InlineData("IrisDragHandle", typeof(Border))]
    [InlineData("IrisBreadcrumbButton", typeof(Wpf.Ui.Controls.Button))]
    [InlineData("IrisBreadcrumbCurrentText", typeof(TextBlock))]
    public Task NewStyles_AreDefinedForTheirTargetType(string key, Type targetType) => WpfTestRunner.RunAsync(() =>
    {
        Application.Current.TryFindResource(key).ShouldBeOfType<Style>().TargetType.ShouldBe(targetType);
        return Task.CompletedTask;
    });

    [Fact]
    public Task NavItem_UsesTheCompactPadding() => WpfTestRunner.RunAsync(() =>
    {
        // 8 + 20（图标）+ 8 = 36：窄态导航项正好放下一个图标，项高也是 36（S0-2）。
        new Border { Style = Find("IrisNavItem") }.Padding.ShouldBe(new Thickness(8));
        return Task.CompletedTask;
    });

    [Fact]
    public Task TitleStyles_ShareOneType_LargerThanTheSectionHeader() => WpfTestRunner.RunAsync(() =>
    {
        var sectionHeader = new TextBlock { Style = Find("IrisSectionHeader") };
        var pageTitle = new TextBlock { Style = Find("IrisPageTitle") };
        var current = new TextBlock { Style = Find("IrisBreadcrumbCurrentText") };
        var back = new Wpf.Ui.Controls.Button { Style = Find("IrisBreadcrumbButton") };

        pageTitle.FontSize.ShouldBe(20d);
        pageTitle.FontSize.ShouldBeGreaterThan(sectionHeader.FontSize);
        pageTitle.FontWeight.ShouldBe(FontWeights.SemiBold);
        pageTitle.Margin.ShouldBe(Application.Current.FindResource("IrisCardGap").ShouldBeOfType<Thickness>());

        // 面包屑的两段与页标题同字号同字重：进出详情时标题不跳。
        current.FontSize.ShouldBe(pageTitle.FontSize);
        current.FontWeight.ShouldBe(pageTitle.FontWeight);
        back.FontSize.ShouldBe(pageTitle.FontSize);
        back.FontWeight.ShouldBe(pageTitle.FontWeight);

        current.TextTrimming.ShouldBe(TextTrimming.CharacterEllipsis);
        current.TextWrapping.ShouldBe(TextWrapping.NoWrap);
        return Task.CompletedTask;
    });

    [Fact]
    public Task RowIcon_StatusText_DragHandle_HoldTheirMetrics() => WpfTestRunner.RunAsync(() =>
    {
        var icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Settings20, Style = Find("IrisRowIcon") };
        icon.FontSize.ShouldBe(20d);
        icon.Margin.ShouldBe(new Thickness(0, 0, 12, 0));
        icon.VerticalAlignment.ShouldBe(VerticalAlignment.Center);

        var status = new TextBlock { Style = Find("IrisStatusText") };
        status.FontSize.ShouldBe(12d);
        status.TextWrapping.ShouldBe(TextWrapping.Wrap);

        var handle = new Border { Style = Find("IrisDragHandle") };
        handle.Width.ShouldBe(20d);
        handle.Cursor.ShouldBe(Cursors.SizeAll);
        // 背景必须是「透明画刷」而不是 null：null 背景命中不到，拖不动。
        handle.Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)0);
        return Task.CompletedTask;
    });

    [Fact]
    public Task RowError_CollapsesWhileEmpty_AndKeepsTheBrushSetOnTheElement() => WpfTestRunner.RunAsync(() =>
    {
        // C5：行内校验错误文字。空串时不占位；有文字时可见；样式本身不设前景色。
        var description = new TextBlock { Style = Find("IrisRowDescription") };
        var error = new TextBlock { Style = Find("IrisRowError") };

        error.Text.ShouldBe(string.Empty);
        error.Visibility.ShouldBe(Visibility.Collapsed);
        error.Text = "Invalid value";
        error.Visibility.ShouldBe(Visibility.Visible);
        error.Text = string.Empty;
        error.Visibility.ShouldBe(Visibility.Collapsed);

        // 字号与换行继承自 IrisRowDescription。
        Find("IrisRowError").BasedOn.ShouldBeSameAs(Find("IrisRowDescription"));
        error.FontSize.ShouldBe(description.FontSize);
        error.TextWrapping.ShouldBe(TextWrapping.Wrap);

        // 样式自己不设 Foreground：各行把原来的画刷留在元素上，样式不能与它抢。
        Find("IrisRowError").Setters.OfType<Setter>().ShouldNotContain(setter => setter.Property == TextBlock.ForegroundProperty);
        var brush = new SolidColorBrush(Colors.Red);
        var colored = new TextBlock { Style = Find("IrisRowError"), Foreground = brush, Text = "Invalid value" };
        colored.Foreground.ShouldBeSameAs(brush);
        return Task.CompletedTask;
    });
```

（同一个文件，接第二段。）

```csharp

    [Fact]
    public Task SecondaryTextStyles_ResolveTheSecondaryBrush_AndTheBreadcrumbIsATextButton() => WpfTestRunner.RunAsync(() =>
    {
        var icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Settings20, Style = Find("IrisRowIcon") };
        var status = new TextBlock { Text = "Status", Style = Find("IrisStatusText") };
        var back = new Wpf.Ui.Controls.Button { Content = "Translation", Style = Find("IrisBreadcrumbButton") };
        var host = new Window
        {
            Content = new StackPanel { Children = { icon, status, back } },
            Width = 300,
            Height = 200,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -20000,
            Top = -20000,
        };
        try
        {
            host.Show();
            host.UpdateLayout();

            var secondary = ColorOf("TextFillColorSecondaryBrush");
            // 前置：主色与次要色确实不同，否则下面三条分不出「套上了次要色」和「什么都没套」。
            secondary.ShouldNotBe(ColorOf("TextFillColorPrimaryBrush"));
            icon.Foreground.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(secondary);
            status.Foreground.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(secondary);
            back.Foreground.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(secondary);
            icon.ActualWidth.ShouldBeGreaterThan(0d);

            // 面包屑是「文字按钮」：画出 Content 的文字（与只画图标的 IrisIconButton 相反），无底无边。
            Descendants<TextBlock>(back).ShouldContain(text => text.Text == "Translation");
            back.Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)0);
            back.BorderThickness.ShouldBe(new Thickness(0));
            back.Padding.ShouldBe(new Thickness(0));
            // 键盘可达，且有焦点框。FindResource 在键不存在时会抛异常——这同时是对这个 WPF-UI 键的核实。
            back.Focusable.ShouldBeTrue();
            back.FocusVisualStyle.ShouldBeSameAs(Application.Current.FindResource("DefaultControlFocusVisualStyle"));
            // 悬停要变色：直接断模板结构，离屏测试里造不出真实的鼠标悬停。
            back.Template.Triggers.OfType<Trigger>().Any(trigger => trigger.Property == UIElement.IsMouseOverProperty).ShouldBeTrue();
        }
        finally
        {
            host.Close();
        }

        return Task.CompletedTask;
    });

    private static Style Find(string key) => Application.Current.FindResource(key).ShouldBeOfType<Style>();

    private static Color ColorOf(string key) => Application.Current.FindResource(key).ShouldBeOfType<SolidColorBrush>().Color;

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T value)
            {
                yield return value;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
```

- [ ] **Step 3: 在 `IrisStyleRenderingTests.cs` 里加 `IrisListRowItem` 的离屏用例**

Modify `tests/Iris.App.Tests/Themes/IrisStyleRenderingTests.cs`：在类的最后一个 `}` 之前（:206 `});` 之后）追加下面的用例与两个辅助方法。既有那条 `StylePanel_IsRenderedForReview` 一个字不动。本文件已经 `using` 了需要的全部命名空间；`ListBox` / `ListBoxItem` 写全限定名只是为了一眼看清用的是 WPF 原生类型。

```csharp

    [Fact]
    public Task ListRowItem_SelectedRowLooksDifferent_AndSelectionStillWorks() => WpfTestRunner.RunAsync(() =>
    {
        var style = (Style)Application.Current.FindResource("IrisListRowItem");
        // 三行内容相同，所以行与行之间的像素差只可能来自状态。
        var rows = Enumerable.Range(0, 3).Select(_ => new System.Windows.Controls.ListBoxItem { Content = "Row" }).ToArray();
        var list = new System.Windows.Controls.ListBox { ItemContainerStyle = style };
        foreach (var row in rows)
        {
            list.Items.Add(row);
        }

        var surface = new Border { Child = list, Padding = new Thickness(16) };
        var window = new Window
        {
            Content = surface,
            Width = 460,
            Height = 300,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -20000,
            Top = -20000,
        };
        surface.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
```

（同一个方法，接下一段。）

```csharp
        try
        {
            window.Show();
            window.UpdateLayout();

            // 样式真的套到了容器上：否则下面比的是 WPF-UI 自带的行样式。
            rows[0].Style.ShouldBeSameAs(style);

            // 选中行为没被模板吞掉，两个方向都通。
            list.SelectedIndex = 0;
            rows[0].IsSelected.ShouldBeTrue();
            rows[1].IsSelected = true;
            list.SelectedIndex.ShouldBe(1);
            list.SelectedIndex = 0;
            rows[1].IsSelected.ShouldBeFalse();
            rows[2].IsEnabled = false;
            window.UpdateLayout();

            // 常态：卡面与描边来自 Iris 的两个键；间距来自主题层的两个键。
            var plainBorder = TemplatePart(rows[1], "RowBorder");
            plainBorder.Background.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(BrushColor("IrisAcrylicCardFillBrush"));
            plainBorder.BorderBrush.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(BrushColor("IrisAcrylicCardStrokeBrush"));
            rows[1].Padding.ShouldBe((Thickness)Application.Current.FindResource("IrisRowPadding"));
            rows[1].Margin.ShouldBe((Thickness)Application.Current.FindResource("IrisRowGap"));

            // 选中：状态层有了底色，描边换了一支。未选中：状态层透明。
            TemplatePart(rows[1], "StateLayer").Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBe((byte)0);
            TemplatePart(rows[0], "StateLayer").Background.ShouldBeOfType<SolidColorBrush>().Color.A.ShouldBeGreaterThan((byte)0);
            TemplatePart(rows[0], "RowBorder").BorderBrush.ShouldBeOfType<SolidColorBrush>().Color
                .ShouldNotBe(BrushColor("IrisAcrylicCardStrokeBrush"));

            // 禁用：整行降不透明度。先断常态是 1，否则 0.5 那条可能只是模板写死的。
            plainBorder.Opacity.ShouldBe(1d);
            TemplatePart(rows[2], "RowBorder").Opacity.ShouldBe(0.5d);

            // 键盘焦点框：FindResource 在键不存在时会抛异常。
            rows[1].FocusVisualStyle.ShouldBeSameAs(Application.Current.FindResource("DefaultControlFocusVisualStyle"));

            // 最后看像素：选中行与未选中行在行内同一位置（左内边距里，没有文字）颜色不同。
            var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            bitmap.PixelWidth.ShouldBeGreaterThan(0);
            rows[0].ActualHeight.ShouldBeGreaterThan(20d);
            RowPixel(bitmap, rows[0], surface).ShouldNotBe(RowPixel(bitmap, rows[1], surface));
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    private static Border TemplatePart(System.Windows.Controls.ListBoxItem row, string name) =>
        row.Template.FindName(name, row).ShouldBeOfType<Border>();

    private static Color BrushColor(string key) =>
        Application.Current.FindResource(key).ShouldBeOfType<SolidColorBrush>().Color;

    // 取行内 (6, 高度一半) 处的像素：x=6 落在 16 的左内边距里，避开文字与 1px 描边。
    // 渲染的是 surface（窗口客户区，原点 0,0），所以把行内坐标换算到 surface 上再读。
    private static uint RowPixel(RenderTargetBitmap bitmap, FrameworkElement row, Visual surface)
    {
        var point = row.TransformToAncestor(surface).Transform(new Point(6, row.ActualHeight / 2));
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0);
        return BitConverter.ToUInt32(pixel, 0);
    }
```

- [ ] **Step 4: 跑，确认失败**

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~IrisStyleResourceTests|FullyQualifiedName~IrisStyleRenderingTests"
```

期望：编译通过；`IrisStyleResourceTests` 由 6 条变 23 条，其中**既有 6 条绿、新增 17 条全红**；`IrisStyleRenderingTests` 既有 2 条绿、新增 1 条红。

| 新用例 | 条数 | 期望的红法 |
|---|---|---|
| `ThicknessResources_AreDefined("IrisPagePadding")` | 1 | `TryFindResource` 返回 `null`，`ShouldBeOfType` 失败 |
| `NavWidthResources_HoldTheDesignValues` | 2 | 同上 |
| `PagePadding_HoldsTheDesignValue` | 1 | 同上 |
| `NewStyles_AreDefinedForTheirTargetType` | 8 | 同上 |
| `NavItem_UsesTheCompactPadding` | 1 | 实际 `10,8,10,8`，期望 `8,8,8,8` |
| `TitleStyles_…`、`RowIcon_…`、`SecondaryTextStyles_…`、`RowError_…` | 4 | `FindResource` 抛 `ResourceReferenceKeyNotFoundException` |
| `ListRowItem_SelectedRowLooksDifferent_AndSelectionStillWorks` | 1 | 同上 |

编译失败时先看是不是 `CS0104`（`TextBlock` 二义）——说明 `IrisStyleResourceTests.cs` 里误加了 `using Wpf.Ui.Controls;`。

- [ ] **Step 5: 定下取值；在 `Iris.Styles.xaml` 里加数值键、改 `IrisNavItem`、订正三段注释**

**本任务定下的取值**（S0-11 留给撰写者的部分；数值体系沿用 `Iris.Styles.xaml` 现有的 14 / 12 / 11 字号与 `SubtleFillColor*` 状态色）：

| 键 | 取值 |
|---|---|
| `IrisPageTitle` | 字号 **20**、字重 **SemiBold**、主色、单行加省略号、`Margin` = `IrisCardGap`。比行标题 / 分组标题（14 / SemiBold）高一档。**样张原件不在撰写时可读的范围内，这个字号没有对照样张**——任务 4 第一次出证据图时对照样张，不合适就改这一个数（三处字号同改，测试里的 `20d` 同改） |
| `IrisBreadcrumbButton` / `IrisBreadcrumbCurrentText` | 字号字重同上。按钮：常态 `TextFillColorSecondaryBrush`，悬停 `TextFillColorPrimaryBrush`，按下 `TextFillColorTertiaryBrush`，禁用 `TextFillColorDisabledBrush`；无底无边、`Padding` 0；焦点框 `DefaultControlFocusVisualStyle` |
| `IrisStatusText` | 字号 12（同 `IrisRowDescription`）、次要色、`TextWrapping=Wrap` |
| `IrisRowError`（C5） | `BasedOn` `IrisRowDescription`（字号、换行随它）；`Text` 为空串时 `Visibility=Collapsed`；**不设 `Foreground`**（各行把原来的画刷写在元素上） |
| `IrisListRowItem` 常态 | 底 `IrisAcrylicCardFillBrush`、描边 `IrisAcrylicCardStrokeBrush` 1px；其上叠一个透明的「状态层」 |
| …悬停 | 状态层 = `SubtleFillColorSecondaryBrush` |
| …选中 | 状态层 = `SubtleFillColorSecondaryBrush`，描边换成 `TextFillColorSecondaryBrush`（与 `History/HistoryWindow.xaml:57–60` 的既有写法一致） |
| …按下 | **不做**。【计划裁决】C1（1.2A）：`ListBoxItem` 没有 `IsPressed`；不用「选中 + 悬停」近似，不新增附加属性。选中且悬停时外观与选中相同。进偏差清单 |
| …键盘焦点 | `FocusVisualStyle` = `DefaultControlFocusVisualStyle`（只在键盘导航时出现） |
| …禁用 | 整行 `Opacity` 0.5 |

Modify `src/Iris.App/Themes/Iris.Styles.xaml`，本步五处，**从文件末尾往前改**。

(a) `IrisNavItem`。旧文本（:260–262）：

```xml
    <!-- Navigation entry. The selected state is applied in code-behind by the settings window. -->
    <Style x:Key="IrisNavItem" TargetType="Border">
        <Setter Property="Padding" Value="10,8" />
```

新文本：

```xml
    <!-- Navigation entry. The selected state is not part of this style: each nav item keeps an
         inline style BasedOn this one, with a DataTrigger on the current page key.
         Padding 8 on every side: 8 + 20 (icon) + 8 = 36, the item height, and exactly the width
         left for an item in the compact (icon-only) navigation column. -->
    <Style x:Key="IrisNavItem" TargetType="Border">
        <Setter Property="Padding" Value="8,8" />
```

(b) `IrisCard` 上方的注释。旧文本（:24–26）：

```xml
    <!-- Translucent card surface. IrisAcrylic* come from Iris.Light/Iris.Dark; the opaque
         overrides in Iris.SolidFallback are not wired up in phase 1 (a Dark variant has to land
         first), so these translucent fills are what the cards actually use. -->
```

新文本（`IrisCard` 样式本身不动）：

```xml
    <!-- Translucent card surface. IrisAcrylic* come from Iris.Light/Iris.Dark; on systems without
         acrylic ThemeService merges Iris.SolidFallback(.Dark), which overrides them with opaque
         values. That is why these brushes must stay DynamicResource. -->
```

(c) 预留键的注释与新数值键。旧文本（:16–22）：

```xml
    <!-- Reserved for later phases of this redesign; no style consumes them yet:
         IrisPillCornerRadius -> the settings nav selection pill (phase 2),
         IrisCardGap -> vertical gap between stacked cards,
         IrisControlGap -> left gap before a row's control. -->
    <CornerRadius x:Key="IrisPillCornerRadius">12</CornerRadius>
    <Thickness x:Key="IrisCardGap">0,0,0,12</Thickness>
    <Thickness x:Key="IrisControlGap">12,0,0,0</Thickness>
```

新文本（三个既有键的名字、值、顺序都不变）：

```xml
    <!-- IrisCardGap: vertical gap between stacked cards, also the gap under a page title.
         IrisControlGap: left gap before a row's control.
         IrisPillCornerRadius is still reserved; no style consumes it yet. -->
    <CornerRadius x:Key="IrisPillCornerRadius">12</CornerRadius>
    <Thickness x:Key="IrisCardGap">0,0,0,12</Thickness>
    <Thickness x:Key="IrisControlGap">12,0,0,0</Thickness>

    <!-- Settings window layout metrics. The navigation column is IrisNavWidth wide, or
         IrisNavCompactWidth (icons only) when the window is narrower than the compact threshold;
         code-behind reads both with FindResource. IrisPagePadding is the margin of the first-level
         panel of every settings page. -->
    <sys:Double x:Key="IrisNavWidth">176</sys:Double>
    <sys:Double x:Key="IrisNavCompactWidth">48</sys:Double>
    <Thickness x:Key="IrisPagePadding">24,16,24,16</Thickness>
```

(d) 根元素加 `sys` 命名空间。旧文本（:1–3）：

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">
```

新文本：

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:sys="clr-namespace:System;assembly=System.Runtime"
                    xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">
```

仓库里还没有任何 XAML 用过 `sys:` 命名空间（`grep -rn "xmlns:sys" src/Iris.App` 为 0 行），这个写法**未在本仓库验证**。XAML 编译报找不到 `sys:Double`（`MC3050` 一类）时，把程序集名换成 `mscorlib`：`xmlns:sys="clr-namespace:System;assembly=mscorlib"`；两种都不行就停下来报告。

(e) `IrisRowTitle`（:41–46）与 `IrisRowDescription`（:48–53）：两个都**已经有** `TextWrapping="Wrap"`（:44、:51），S0-11 那一行「没有就补上」不用做，这两个样式不改。

- [ ] **Step 6: 在 `Iris.Styles.xaml` 末尾追加八个新样式**

在 `IrisNavItem` 样式的 `</Style>` 之后、文件最后一行 `</ResourceDictionary>` 之前，依次插入下面三段（按文本定位，不靠行号）。规矩：模板**体内**不写 `DynamicResource`（Step 1(a)）；全部是带 `x:Key` 的显式样式（R5）；不加 `Storyboard`。`IrisRowError`（C5）用 `BasedOn="{StaticResource IrisRowDescription}"`：`StaticResource` 不能前向引用，它必须排在 `IrisRowDescription`（:48–53）**之后**——追加在文件末尾自然满足，以后挪动这段时不得挪到 :48 之前。第一段：

```xml

    <!-- Page title at the top of every settings page: one step above the 14/SemiBold row and
         section titles. The two breadcrumb styles below repeat the same size and weight so the
         heading does not jump when a page drills into a detail view. -->
    <Style x:Key="IrisPageTitle" TargetType="TextBlock">
        <Setter Property="FontSize" Value="20" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Margin" Value="{StaticResource IrisCardGap}" />
        <Setter Property="TextTrimming" Value="CharacterEllipsis" />
        <Setter Property="Foreground" Value="{DynamicResource TextFillColorPrimaryBrush}" />
    </Style>

    <!-- Leading icon of a settings row (column 0 of the row card). -->
    <Style x:Key="IrisRowIcon" TargetType="{x:Type ui:SymbolIcon}">
        <Setter Property="FontSize" Value="20" />
        <Setter Property="Margin" Value="0,0,12,0" />
        <Setter Property="VerticalAlignment" Value="Center" />
        <Setter Property="Foreground" Value="{DynamicResource TextFillColorSecondaryBrush}" />
    </Style>

    <!-- Status and result text inside action cards and detail views. -->
    <Style x:Key="IrisStatusText" TargetType="TextBlock">
        <Setter Property="FontSize" Value="12" />
        <Setter Property="TextWrapping" Value="Wrap" />
        <Setter Property="Foreground" Value="{DynamicResource TextFillColorSecondaryBrush}" />
    </Style>

    <!-- Inline validation message at the end of a row's text stack. It takes no space while it is
         empty. No Foreground here on purpose: each row keeps its own brush on the element. -->
    <Style x:Key="IrisRowError" TargetType="TextBlock" BasedOn="{StaticResource IrisRowDescription}">
        <Style.Triggers>
            <Trigger Property="Text" Value="">
                <Setter Property="Visibility" Value="Collapsed" />
            </Trigger>
        </Style.Triggers>
    </Style>

    <!-- Drag handle of a reorderable list row. The background is Transparent rather than null so
         the whole 20px strip is hit-testable, not just the glyph inside it. -->
    <Style x:Key="IrisDragHandle" TargetType="Border">
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="Width" Value="20" />
        <Setter Property="Cursor" Value="SizeAll" />
    </Style>
```

第二段（列表行容器）：

```xml

    <!-- Row container for the settings lists (ItemContainerStyle). It looks like IrisRowCard, with
         a state layer on top of the card fill:
           hover    -> SubtleFillColorSecondaryBrush layer
           selected -> the same layer plus a stronger stroke
           keyboard focus -> the WPF-UI focus visual (shown for keyboard navigation only)
           disabled -> the whole row at half opacity
         There is no separate pressed look: ListBoxItem has no IsPressed.
         Setters are static (no Storyboard) so an offscreen render shows the new state at once.
         Selection itself is untouched: the template only draws, it handles no input.
         Every DynamicResource sits on a Setter, none on an element inside the template, so
         IrisStyleKeyResolutionTests can see all of them. -->
    <Style x:Key="IrisListRowItem" TargetType="{x:Type ListBoxItem}">
        <Setter Property="Background" Value="{DynamicResource IrisAcrylicCardFillBrush}" />
        <Setter Property="BorderBrush" Value="{DynamicResource IrisAcrylicCardStrokeBrush}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="Padding" Value="{StaticResource IrisRowPadding}" />
        <Setter Property="Margin" Value="{StaticResource IrisRowGap}" />
        <Setter Property="Foreground" Value="{DynamicResource TextFillColorPrimaryBrush}" />
        <Setter Property="HorizontalContentAlignment" Value="Stretch" />
        <Setter Property="VerticalContentAlignment" Value="Center" />
        <Setter Property="FocusVisualStyle" Value="{DynamicResource DefaultControlFocusVisualStyle}" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type ListBoxItem}">
                    <Border x:Name="RowBorder"
                            Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            CornerRadius="{StaticResource IrisRowCornerRadius}">
                        <Border x:Name="StateLayer"
                                Padding="{TemplateBinding Padding}"
                                Background="Transparent"
                                CornerRadius="{StaticResource IrisRowCornerRadius}">
                            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                              VerticalAlignment="{TemplateBinding VerticalContentAlignment}" />
                        </Border>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="StateLayer" Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
                        </Trigger>
                        <Trigger Property="IsSelected" Value="True">
                            <Setter TargetName="StateLayer" Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
                            <Setter Property="BorderBrush" Value="{DynamicResource TextFillColorSecondaryBrush}" />
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter TargetName="RowBorder" Property="Opacity" Value="0.5" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

第三段（面包屑）：

```xml

    <!-- Breadcrumb "back" segment: a text-only ui:Button in the page-title type. Secondary colour
         at rest, primary on hover. Unlike IrisIconButton this template draws Content, not Icon.
         Background stays Transparent (not null) so the whole text box is hit-testable. -->
    <Style x:Key="IrisBreadcrumbButton" TargetType="{x:Type ui:Button}" BasedOn="{StaticResource {x:Type ui:Button}}">
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="BorderBrush" Value="Transparent" />
        <Setter Property="BorderThickness" Value="0" />
        <Setter Property="Padding" Value="0" />
        <Setter Property="FontSize" Value="20" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Foreground" Value="{DynamicResource TextFillColorSecondaryBrush}" />
        <Setter Property="PressedForeground" Value="{DynamicResource TextFillColorTertiaryBrush}" />
        <Setter Property="HorizontalAlignment" Value="Left" />
        <Setter Property="VerticalAlignment" Value="Center" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="FocusVisualStyle" Value="{DynamicResource DefaultControlFocusVisualStyle}" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type ui:Button}">
                    <Border x:Name="ContentBorder"
                            Padding="{TemplateBinding Padding}"
                            Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            CornerRadius="{TemplateBinding CornerRadius}">
                        <ContentPresenter x:Name="ContentPresenter"
                                          HorizontalAlignment="Left"
                                          VerticalAlignment="Center"
                                          Content="{TemplateBinding Content}"
                                          Focusable="False"
                                          TextElement.Foreground="{TemplateBinding Foreground}" />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter Property="Foreground" Value="{DynamicResource TextFillColorPrimaryBrush}" />
                        </Trigger>
                        <!-- After the hover trigger on purpose: later triggers win, so a pressed
                             button under the pointer shows the pressed colour. -->
                        <Trigger Property="IsPressed" Value="True">
                            <Setter Property="Foreground" Value="{Binding PressedForeground, RelativeSource={RelativeSource TemplatedParent}}" />
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Foreground" Value="{DynamicResource TextFillColorDisabledBrush}" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Breadcrumb "current" segment: the name of the item being edited. Single line with an
         ellipsis, because the name is user text of any length. -->
    <Style x:Key="IrisBreadcrumbCurrentText" TargetType="TextBlock">
        <Setter Property="FontSize" Value="20" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="TextWrapping" Value="NoWrap" />
        <Setter Property="TextTrimming" Value="CharacterEllipsis" />
        <Setter Property="VerticalAlignment" Value="Center" />
        <Setter Property="Foreground" Value="{DynamicResource TextFillColorPrimaryBrush}" />
    </Style>
```

`TextFillColorTertiaryBrush` 在仓库里还没人用过（撰写时只确认这个字符串在 `Wpf.Ui.dll` 里出现）。它写在 `Setter` 上，`IrisStyleKeyResolutionTests` 会自动检查能否解析；解析不了就把 `PressedForeground` 那一行的键换成 `TextFillColorSecondaryBrush`（按下回到次要色，与 `IrisIconButton` :212 的做法相同）。

- [ ] **Step 7: 跑，确认通过**

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~Iris.App.Tests.Themes"
```

期望：全绿、构建零警告。其中 `IrisStyleResourceTests` 23 条、`IrisStyleRenderingTests` 3 条、`IrisStyleKeyResolutionTests` 1 条（它现在多检查了 `DefaultControlFocusVisualStyle`、`TextFillColorTertiaryBrush` 两个新出现的键）。

红了先对照这张表：

| 现象 | 多半的原因 | 处理 |
|---|---|---|
| XAML 编译或加载失败，提到找不到 `IrisRowDescription` | `IrisRowError` 被放到了 `IrisRowDescription` 之前（`StaticResource` 不能前向引用） | 把 `IrisRowError` 挪回文件末尾那一段 |
| `RowError_…` 红在第一条 `Visibility`（空串时不是 `Collapsed`） | 触发器的 `Value=""` 没匹配上 | 先看 `error.Text` 的实际值；是 `null` 就在触发器旁再加一条 `Value="{x:Null}"` 的同样触发器，并记进偏差清单（C5 写的是一个触发器） |
| `RowError_…` 红在 `BasedOn` 或 `FontSize` | `BasedOn` 写错了键 | 对照 Step 6 的 XAML |
| XAML 编译失败，提到 `sys:Double` | 命名空间的程序集名 | Step 5(d) 的退路 |
| `IrisStyleKeyResolutionTests` 红，点名某个键 | 那个 WPF-UI 键不存在 | Step 1(c) / 本步上面一段的退路 |
| `ListRowItem_…` 红在像素那一条 | 取点落在了描边或文字上 | 先把位图存成 PNG 看一眼再改取点；不要改成比画刷 |
| `ListRowItem_…` 红在 `rows[0].Style` | `ItemContainerStyle` 没套到「自己就是容器」的项上 | 把三行改成普通对象（`list.Items.Add(new object())`），用 `list.ItemContainerGenerator.ContainerFromIndex(i)` 取容器 |
| `StylePanel_IsRenderedForReview` 红 | 不应该发生（本任务没动它用到的样式） | 查是不是误改了 `IrisRowTitle` / `IrisRowCard` |
| `RowIcon_…` 红在 `icon.FontSize`（实际不是 20） | `ui:SymbolIcon` 的构造函数给 `FontSize` 设了本地值，样式的 `Setter` 盖不过它（撰写时只核实了属性存在，没核实这一点） | 删掉 `IrisRowIcon` 的 `FontSize` 那一行与这条断言，改由各页在图标上直接写 `FontSize="20"`；记进偏差清单 |

给后面页面任务的提醒：`IrisRowIcon` 只能套在**元素写法**的图标上（`<ui:SymbolIcon Symbol="Settings20" Style="{StaticResource IrisRowIcon}" />`）；`Iris.Styles.xaml:201` 注释里那种标记扩展写法 `{ui:SymbolIcon SymbolRegular.Copy24}` 没有地方写 `Style`。

给后面页面任务的提醒（C5）：行内校验错误文字一律写成 `<TextBlock Style="{StaticResource IrisRowError}" Text="{Binding …}" Foreground="{DynamicResource …原来的键}"/>`，放在行卡片第 1 列文字栈的最后；保留原来的 `x:Name`、原来的 `Text` 绑定与原来的 `Foreground` 画刷键，不在各行重复写「空则折叠」的内联样式。原元素上若另有 `Visibility` 绑定（由视图模型的「有无错误」属性控制），保留那条绑定——本地值优先于样式触发器，两者不冲突。

然后跑 5.0 的「设置全集」，期望全绿：新键还没有使用者，`IrisNavItem` 也还没人引用（`grep -rn "IrisNavItem" src/Iris.App --include=*.xaml` 只有 `Iris.Styles.xaml` 自己）。

- [ ] **Step 8: 暂存（不提交）**

```
git add src/Iris.App/Themes/Iris.Styles.xaml tests/Iris.App.Tests/Themes/IrisStyleResourceTests.cs tests/Iris.App.Tests/Themes/IrisStyleRenderingTests.cs
git status --short -- src tests
```

期望：这三个路径是 `M`，加上任务 1 暂存的七个，`src`、`tests` 下没有未暂存的改动。

**⚠ 与裁决不符之处（任务 2）**

1. **已裁决（协调方 2026-11-17，C1，【计划裁决】，进偏差清单）：`IrisListRowItem` 不做独立的「按下」态。** 事实：`ListBoxItem` 没有 `IsPressed` 属性（那是 `ButtonBase` 的）；仓库里两处既有的 `ListBoxItem` 样式（`History/HistoryWindow.xaml:46–65`、`Settings/SettingsWindow.xaml:217–221`）也都没有按下态。原先用「选中 + 悬停」（`SubtleFillColorTertiaryBrush`）近似按下态的 `MultiTrigger` 已从上面的步骤里删掉——它在鼠标松开后不恢复，观感更怪。样式只做悬停、选中、键盘焦点三种交互状态（另有禁用）；不加 `EventTrigger` / `Storyboard`，不为此新增附加属性。S0-11 与 Step 5 的取值表已同步。测试不用改：`ListRowItem_SelectedRowLooksDifferent_AndSelectionStillWorks` 只断言常态、选中、禁用与键盘焦点框，本来就没有针对「选中且悬停」的断言。
2. **「`IrisRowIcon` 设图标大小的写法照 `IrisIconButton`」照不了。** 任务 2「范围与要点」这样写，但 `Iris.Styles.xaml:205–258` 里没有任何设图标大小的地方（图标大小由调用方选的 `SymbolRegular.Copy24` 这类成员带出），`src/Iris.App` 的 XAML 里也没有任何一处实际用了 `ui:SymbolIcon`（`grep -rn "SymbolIcon" src/Iris.App --include=*.xaml` 只命中 :201 那行注释）。上面的步骤改用 `FontSize` 的 `Setter`（属性存在，Step 1(b)）。
3. **`IrisPageTitle` 的字号没有对照样张。** S0-11 写「按样张定下」，但样张原件不在撰写时可读的范围里。20 / SemiBold 是按主题层现有字阶推的，任务 4 出第一张证据图时对照样张复核。
4. **S0-11 没列、但本任务顺手订正了的两段英文注释**（`Iris.Styles.xaml:16–19`、`:24–26`）：前者说 `IrisCardGap` 没有使用者（`IrisPageTitle` 用上之后不属实），后者说回退字典没接线（任务 1 之后不属实）。只改注释，不改键与样式。
5. **`IrisRowTitle` / `IrisRowDescription` 不用改**：两者已经有 `TextWrapping="Wrap"`（:44、:51）。

**验证命令：** `FullyQualifiedName~Iris.App.Tests.Themes` —— 预期全绿；随后跑「设置全集」，预期全绿。

**要改的既有测试：** `Themes/IrisStyleResourceTests.cs` / `IrisStyleKeyResolutionTests.cs`——若有「键总数」或「`IrisNavItem` 的 `Padding` 等于 `10,8`」之类的断言，改成新值（理由：S0-2、S0-11）。

### Task 3: 渲染测试夹具对齐 Iris 主题字典

**目的：** 在界面开始变化之前，先让切主题的渲染测试把 Iris 颜色字典一起切过去，并在结束时还原（S0-13）。本任务只改测试，不改 `src/`。

**Files:**
- Modify: `tests/Iris.App.Tests/Settings/SettingsHotkeysIntegrationTests.cs`（W04，1507–1711；切主题处 :1525–1530）
- Modify: `tests/Iris.App.Tests/Settings/SelectionSettingsRenderingTests.cs`（87 行；:30）
- Modify: `tests/Iris.App.Tests/Settings/SettingsWindowTranslationUiTests.cs`（用例 7 的切主题处与 `finally` 435–444）
- Modify: `tests/Iris.App.Tests/Settings/OcrSettingsRenderingTests.cs`（128 行；辅助方法 `Prepare` :88–93；两处 `await Run(async f =>` :29、:52）——C8
- Modify: `tests/Iris.App.Tests/Settings/NetworkSettingsRenderingTests.cs`（139 行；辅助方法 `Prepare` :103–107）——C8
- Modify: `tests/Iris.App.Tests/Settings/NetworkSettingsFoundationTests.cs`（110 行；切主题处 :78，`finally` :96）——C8
- Modify: `tests/Iris.App.Tests/Updates/UpdateRenderingTests.cs`（:52–54）
- Modify: `tests/Iris.App.Tests/History/HistoryRenderingTests.cs`（:86–89）
- Modify: `tests/Iris.App.Tests/Localization/LocalizationRenderingTests.cs`（:240–243）

**范围与要点：**
- 做：应用级的六个文件（骨架原有的 `SettingsHotkeysIntegrationTests`、`SelectionSettingsRenderingTests`、`SettingsWindowTranslationUiTests`，加上 C8 追加的 `OcrSettingsRenderingTests`、`NetworkSettingsRenderingTests`、`NetworkSettingsFoundationTests`）在应用级跟一行 `ThemeService.ApplyIrisThemeDictionary(theme)`，并在同一次 `WpfTestRunner` 调用结束前还原浅色；窗口级的三个文件（`UpdateRenderingTests`、`HistoryRenderingTests`、`LocalizationRenderingTests`）在窗口级补一份对应主题的 Iris 颜色字典。
- 不做：不改任何断言的阈值、取点、序号；不改 `WpfTestRunner`；不改 `src/`；不改两个共用夹具 `Settings/OcrSettingsUiScope.cs`、`Settings/NetworkSettingsUiScope.cs`（各有多个测试类在用，其中有并行的）。
- C8 的范围以 grep 为准（Step 1 的两张表）：渲染设置窗口、且在应用级直接调 `ApplicationThemeManager.Apply(主题)` 的夹具全部纳入；其余调用点不动，理由逐个写在 Step 1。
- 先核实设置与还原是否在同一次 `WpfTestRunner` 调用里（S0-13）。
- 这是「没有先失败后通过」的任务：界面还没变，预期**改前改后都绿**。改前先跑一遍留底，改后再跑一遍对比；任何一条由绿变红都说明夹具改错了。

**步骤提纲：**
- [ ] 改之前跑下面的验证命令，记下通过 / 失败条数作基线
- [ ] 逐个文件加主题字典对齐与还原
- [ ] 再跑同一条命令，确认条数与基线一致、全绿
- [ ] 再跑「设置全集」，确认全绿；`git add`

**详细步骤：**

（行号是撰写时在 `dc08c48` 上读到的；C8 追加的三个文件是 2026-11-23 在同一个提交上读的。任务 1、2 不改这九个文件，行号不会漂，但动手前仍按 5.0 重新 grep：`grep -n "ApplicationThemeManager.Apply\|ThemesDictionary" <文件>`。）

- [ ] **Step 1: 核实九处的写法（撰写时已读代码核实，执行时复核）**

| 文件 | 集合（是否并行） | 切主题处 | 还原 | 设置之后到渲染之间有没有 `await` |
|---|---|---|---|---|
| `Settings/SettingsHotkeysIntegrationTests.cs` W04 | `AppShutdownerSharedState`（定义上没有 `DisableParallelization`，**并行**） | 两处：:1525–1530（每个主题一次，在 `try` 之外，之后有 `await`）与 :1578–1579（每个变体一次，在 `try` 之内、`window.Show()` 之后） | **没有任何还原**（WPF-UI 字典也不还原，W04 跑完停在深色） | :1578 之后到 `try` 结束（:1681）之间**没有** `await`；`finally`（:1682–1694）里有 |
| `Settings/SelectionSettingsRenderingTests.cs` | 无 `[Collection]`（**并行**） | :30–31，在 `SelectionSettingsTestScope.Run(async f => …)` 的方法体里 | **没有任何还原** | **有**：:38–39 等渲染帧（每张图之前都等） |
| `Settings/SettingsWindowTranslationUiTests.cs` 用例 7 | 无 `[Collection]`（**并行**） | :262–267，在 `try`（:298）之外 | `finally` :435–444，只还原 WPF-UI 字典 | 没有：整个方法体是同步的（`WpfTestRunner.RunAsync(() => { … })`，:259） |
| `Updates/UpdateRenderingTests.cs` | `Localization`（不并行） | :51–53，窗口级 | 窗口级，不用还原 | — |
| `History/HistoryRenderingTests.cs` | `ScreenshotRendering`（不并行） | 辅助方法 `Theme(Window, bool)` :83–90 | 同上 | — |
| `Localization/LocalizationRenderingTests.cs` | `Localization`（不并行） | 辅助方法 `Theme(FrameworkElement, bool)` :237–244 | 同上 | — |

另一条事实：`WpfTestRunner.RunAsync(Func<Task>)`（`TestSupport/WpfTestRunner.cs:47–74`）只是把异步方法体投到共享 STA 线程的调度器上，**不独占**这个线程——方法体每遇到一个 `await` 就把线程让出来，别的并行集合的测试可以在这个空当里跑。所以「设置与还原之间不能有 `await`」只有用例 7 现在就满足；W04 靠把设置放在 :1578、把还原放在 `finally` 的第一句来满足；`SelectionSettingsRenderingTests` 在切主题那一处**满足不了**，改为在每次取图之前同步地再对齐一次来补（协调方裁决 C2，见 Step 5 的 (b-2) 与本任务末尾「⚠」第 2 条）。

复核命令（期望输出 `0`：W04 从 `try` 到渲染断言之间没有 `await`）：

```
awk 'NR>=1574 && NR<=1681 && /await /' tests/Iris.App.Tests/Settings/SettingsHotkeysIntegrationTests.cs | wc -l
```

不是 0：说明 W04 被改过，停下来重新读 W04 再决定还原放哪。

- [ ] **Step 2: 改之前跑一遍留底**

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsHotkeysIntegrationTests|FullyQualifiedName~SelectionSettingsRenderingTests|FullyQualifiedName~SettingsWindowTranslationUiTests|FullyQualifiedName~UpdateRenderingTests|FullyQualifiedName~HistoryRenderingTests|FullyQualifiedName~LocalizationRenderingTests|FullyQualifiedName~IrisThemeDictionaryTests"
```

期望：全绿。把汇总行（总数 / 通过 / 失败 / 跳过）原样记下来，Step 6 要对比。基线本身有红：先报告，不往下做。

- [ ] **Step 3: 先给六处各加一条「夹具守卫」断言**

骨架说本任务「没有先失败后通过」。光靠「改前改后都绿」证明不了对齐的那一行真的起了作用（界面还没用 Iris 的画刷，那一行删掉也是绿的），所以先加一条只读的守卫断言，让深色变体先红一次。它不改任何既有断言的阈值、取点、序号；属于「改过的既有测试」，一并记进完成报告。

守卫的口径六处相同：按当前元素解析 `IrisAcrylicCardFillBrush`，深色主题下不透明度应小于 64（`Iris.Dark.xaml` 是 `0x1A`），浅色应大于 64（`Iris.Light.xaml` 是 `0x66`）。六个文件都已经 `using System.Windows.Media;` 与 `using Shouldly;`，不用加 `using`。

(a) `Settings/SettingsHotkeysIntegrationTests.cs`，W04。旧文本（:1579–1580）：

```csharp
                            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(window);
                            env.ViewModel.CurrentPageKey = "hotkeys";
```

新文本：

```csharp
                            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(window);
                            // 夹具守卫：Iris 颜色字典必须跟着主题走。卡面填充的不透明度深色是 0x1A、浅色是 0x66，以 64 为界。
                            var irisCardFill = (SolidColorBrush)window.FindResource("IrisAcrylicCardFillBrush");
                            if (theme == AppTheme.Dark) irisCardFill.Color.A.ShouldBeLessThan((byte)64);
                            else irisCardFill.Color.A.ShouldBeGreaterThan((byte)64);
                            env.ViewModel.CurrentPageKey = "hotkeys";
```

(b) `Settings/SelectionSettingsRenderingTests.cs`。旧文本（:31–32）：

```csharp
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(f.Window);
                f.Window.Width = size.Width; f.Window.Height = size.Height; f.Show();
```

新文本：

```csharp
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(f.Window);
                // 夹具守卫：Iris 颜色字典必须跟着主题走（深色 0x1A、浅色 0x66，以 64 为界）。
                var irisCardFill = (SolidColorBrush)f.Window.FindResource("IrisAcrylicCardFillBrush");
                if (theme == Wpf.Ui.Appearance.ApplicationTheme.Dark) irisCardFill.Color.A.ShouldBeLessThan((byte)64);
                else irisCardFill.Color.A.ShouldBeGreaterThan((byte)64);
                f.Window.Width = size.Width; f.Window.Height = size.Height; f.Show();
```

(c) `Settings/SettingsWindowTranslationUiTests.cs`，用例 7（放在 `try` 里面，红的时候 `finally` 照常还原）。旧文本（:307–308）：

```csharp
                window.Show();
                vm.CurrentPageKey = "translation";
```

新文本：

```csharp
                window.Show();
                // 夹具守卫：Iris 颜色字典必须跟着主题走（深色 0x1A、浅色 0x66，以 64 为界）。
                var irisCardFill = (SolidColorBrush)window.FindResource("IrisAcrylicCardFillBrush");
                if (theme == Wpf.Ui.Appearance.ApplicationTheme.Dark) irisCardFill.Color.A.ShouldBeLessThan((byte)64);
                else irisCardFill.Color.A.ShouldBeGreaterThan((byte)64);
                vm.CurrentPageKey = "translation";
```

这两行旧文本在文件里可能不止一处，只改用例 7 方法体内（:257–446）的那一处。

(d) `Updates/UpdateRenderingTests.cs`。旧文本（:53–54）：

```csharp
                    f.Window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                    f.Window.SetResourceReference(Control.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
```

新文本：

```csharp
                    f.Window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                    // 夹具守卫：窗口级也要有对应主题的 Iris 颜色字典（深色 0x1A、浅色 0x66，以 64 为界）。
                    var irisCardFill = (SolidColorBrush)f.Window.FindResource("IrisAcrylicCardFillBrush");
                    if (dark) irisCardFill.Color.A.ShouldBeLessThan((byte)64); else irisCardFill.Color.A.ShouldBeGreaterThan((byte)64);
                    f.Window.SetResourceReference(Control.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
```

(e) `History/HistoryRenderingTests.cs`，辅助方法 `Theme`。旧文本（:89–90）：

```csharp
        window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
    }
```

新文本：

```csharp
        window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        // 夹具守卫：窗口级也要有对应主题的 Iris 颜色字典（深色 0x1A、浅色 0x66，以 64 为界）。
        var irisCardFill = (SolidColorBrush)window.FindResource("IrisAcrylicCardFillBrush");
        if (dark) irisCardFill.Color.A.ShouldBeLessThan((byte)64); else irisCardFill.Color.A.ShouldBeGreaterThan((byte)64);
    }
```

(f) `Localization/LocalizationRenderingTests.cs`，辅助方法 `Theme`。旧文本（:242–243）：

```csharp
        element.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        if (element is Window window) window.SetResourceReference(Control.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
```

新文本：

```csharp
        element.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        // 夹具守卫：元素级也要有对应主题的 Iris 颜色字典（深色 0x1A、浅色 0x66，以 64 为界）。
        var irisCardFill = (SolidColorBrush)element.FindResource("IrisAcrylicCardFillBrush");
        if (dark) irisCardFill.Color.A.ShouldBeLessThan((byte)64); else irisCardFill.Color.A.ShouldBeGreaterThan((byte)64);
        if (element is Window window) window.SetResourceReference(Control.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
```

- [ ] **Step 4: 跑，确认深色变体红在守卫上**

命令同 Step 2。期望：编译通过；**带深色变体的用例红，只跑浅色的用例绿**，而且每条红都红在 Step 3 加的那条 `ShouldBeLessThan(64)` 上（实际值 102，即 `0x66`——浅色字典）。

- 已知的确数：W04 1 条红；`SelectionPage_RendersBothThemesAndSizes_WithErrorAndLongList` 1 条红；用例 7 的两条 `Dark` 行红、两条 `Light` 行绿。
- 另三个文件里红几条撰写时没数（取决于各自有几条带深色的用例），以实际为准，把红的用例名记下来——Step 6 它们必须全部回绿。
- `IrisThemeDictionaryTests` 不受影响，全绿。

某个文件的红**不是**红在守卫断言上，或者这次运行比 Step 2 的基线慢一倍以上还没结束（守卫在窗口配置回调里抛异常，夹具的清理路径撰写时没逐个读过）：中止，把那个文件的守卫先撤掉，改成在 Step 5 里与对齐的那一行一起加（那一处就没有红相位，记进完成报告）。

- [ ] **Step 5: 逐个文件加主题字典对齐与还原**

(a) W04，对齐。旧文本（:1578–1579）：

```csharp
                            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(wpfTheme);
                            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(window);
```

新文本（该文件已有 `using Iris.App.Services;`，:18）：

```csharp
                            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(wpfTheme);
                            ThemeService.ApplyIrisThemeDictionary(wpfTheme);
                            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(window);
```

W04，还原。旧文本（W04 方法体里唯一的 `finally`，:1682–1684；同样的 `await` 行在文件里共有 4 处，只改 W04 这一处）：

```csharp
                        finally
                        {
                            await env.ViewModel.CancelRecordingAsync();
```

新文本：

```csharp
                        finally
                        {
                            // 必须是 finally 的第一句、在任何 await 之前：从上面对齐到这里是一段同步代码，
                            // 别的测试插不进来，也就看不到深色的 Iris 字典。
                            ThemeService.ApplyIrisThemeDictionary(Wpf.Ui.Appearance.ApplicationTheme.Light);
                            await env.ViewModel.CancelRecordingAsync();
```

:1525–1530 那一处**不加**（理由见本任务末尾「⚠」第 1 条）。每个变体都在 :1578 重新对齐一次，八个变体都覆盖到了。

(b) `SelectionSettingsRenderingTests.cs`，对齐与 `try`。旧文本（:30–31）：

```csharp
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme);
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(f.Window);
```

新文本（这个文件没有 `using Iris.App.Services;`，所以写全限定名；方法体原本就不随花括号缩进，新加的 `try` 也不缩进，免得动到下面三十行）：

```csharp
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme);
                Iris.App.Services.ThemeService.ApplyIrisThemeDictionary(theme);
                try
                {
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(f.Window);
```

还原。旧文本（:63–64，`foreach (bool bottom …)` 的右花括号与方法体的结尾）：

```csharp
                }
                });
```

新文本：

```csharp
                }
                }
                finally
                {
                    // 整个测试进程只有一个 Application：不还原，深色的 Iris 字典就留给后面的测试了。
                    Iris.App.Services.ThemeService.ApplyIrisThemeDictionary(Wpf.Ui.Appearance.ApplicationTheme.Light);
                }
                });
```

全限定名编译不过（`Iris` 被解析成别的东西）时：改成在文件头 `using Iris.App.Settings;`（:8）的上一行加 `using Iris.App.Services;`，本文件里的三处调用（上面两处加下面 (b-2) 一处）去掉前缀。

(b-2) `SelectionSettingsRenderingTests.cs`，**每次渲染取图之前同步地再对齐一次**（协调方裁决 C2，1.2A）。这个测试在切主题之后、每张图之前都要等渲染帧（:38–39），等帧的空当里别的并行测试的 `finally` 可能把共享的 Iris 字典还原成浅色；所以在最后一个 `await`（:39）之后、取图（:53 `bitmap.Render`）之前的那段同步代码里再对齐一次。旧文本（:49，`foreach (bool bottom …)` 循环体内，全文件唯一）：

```csharp
                    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
```

新文本（在它上面加三行，原行一个字不动）：

```csharp
                    // The awaits above yield the shared UI thread, and a parallel test may have put the Iris
                    // dictionary back to light meanwhile. Re-align right before rendering: no await from here to CopyPixels.
                    Iris.App.Services.ThemeService.ApplyIrisThemeDictionary(theme);
                    f.Window.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
```

- 位置的依据：:40–:55（取控件、断言、建位图、`bitmap.Render`、`CopyPixels`）之间没有任何 `await`，所以从这次对齐到取完像素是一段同步代码，别的测试插不进来。改完后自查：`grep -n "await" tests/Iris.App.Tests/Settings/SelectionSettingsRenderingTests.cs`，确认新加的对齐行与 `bitmap.CopyPixels` 那一行之间没有 `await`。
- `f.Window.UpdateLayout()` 是同步的：字典被换过时，重新解析出来的画刷要经过一次布局才进入渲染内容；字典没变时它是空操作。
- **不**把这个测试类放进不并行的集合，不加 `[Collection]`。
- 这一处每个变体执行两次（`bottom` 为假、为真各一次），共八次；`finally` 里的还原仍只有一处，不变。

(c) 用例 7，对齐。旧文本（:267–269）：

```csharp
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme);

            var initialSettings = new AppSettings
```

新文本（该文件已有 `using Iris.App.Services;`，:9）：

```csharp
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme);
            ThemeService.ApplyIrisThemeDictionary(theme);

            var initialSettings = new AppSettings
```

用例 7，还原。旧文本（:439–444）：

```csharp
                var restoreDict = System.Windows.Application.Current.Resources.MergedDictionaries.OfType<Wpf.Ui.Markup.ThemesDictionary>().FirstOrDefault();
                if (restoreDict != null)
                {
                    restoreDict.Theme = Wpf.Ui.Appearance.ApplicationTheme.Light;
                }
            }
```

新文本：

```csharp
                var restoreDict = System.Windows.Application.Current.Resources.MergedDictionaries.OfType<Wpf.Ui.Markup.ThemesDictionary>().FirstOrDefault();
                if (restoreDict != null)
                {
                    restoreDict.Theme = Wpf.Ui.Appearance.ApplicationTheme.Light;
                }
                ThemeService.ApplyIrisThemeDictionary(Wpf.Ui.Appearance.ApplicationTheme.Light);
            }
```

(d) `Updates/UpdateRenderingTests.cs`，窗口级。旧文本（:52）：

```csharp
                    f.Window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = theme });
```

新文本（写法与 `Themes/IrisStyleRenderingTests.cs:100–106` 相同）：

```csharp
                    f.Window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = theme });
                    f.Window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Iris.App;component/Themes/Iris.{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute) });
```

(e) `History/HistoryRenderingTests.cs`，辅助方法 `Theme`。旧文本（:87–88）：

```csharp
        window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary
        { Theme = dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light });
```

新文本：

```csharp
        window.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary
        { Theme = dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light });
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Iris.App;component/Themes/Iris.{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute) });
```

(f) `Localization/LocalizationRenderingTests.cs`，辅助方法 `Theme`。旧文本（:241）：

```csharp
        element.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = theme });
```

新文本：

```csharp
        element.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = theme });
        element.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Iris.App;component/Themes/Iris.{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute) });
```

三处都是窗口 / 元素级的字典，随窗口关闭一起消失，不用还原。三个文件都已经 `using System.Windows;`（`ResourceDictionary`），`Uri` 来自隐式 `using System;`。

- [ ] **Step 6: 再跑同一条命令，确认与基线一致、全绿**

命令同 Step 2。期望：总数、通过数与 Step 2 记下的基线**完全相同**，0 失败；Step 4 里记下的每一条红都回绿。

任何一条由绿变红，按下表判断，**不改断言的阈值、取点、序号**：

| 现象 | 多半的原因 |
|---|---|
| 守卫断言在浅色变体上红（实际值 26） | 上一个变体 / 上一条用例的深色 Iris 字典没还原：检查 Step 5 的三处 `finally` |
| 守卫断言在深色变体上红（实际值 102） | 对齐的那一行没加上，或加在了守卫之后 |
| 既有的像素 / 亮度断言红 | 不应该发生（`SettingsWindow.xaml` 此时还没有用任何 Iris 画刷）：先确认 `grep -n "IrisAcrylic" src/Iris.App/Settings/SettingsWindow.xaml` 为 0 行，再查是不是任务 1、2 的改动漏进了窗口 |
| `IrisThemeDictionaryTests` 红 | 有用例把深色 Iris 字典漏在了共享的 `Application` 里 |

- [ ] **Step 7: 跑「设置全集」，暂存（不提交）**

用 5.0 的「设置全集」过滤式再跑一次，期望全绿。然后：

```
git add tests/Iris.App.Tests/Settings/SettingsHotkeysIntegrationTests.cs tests/Iris.App.Tests/Settings/SelectionSettingsRenderingTests.cs tests/Iris.App.Tests/Settings/SettingsWindowTranslationUiTests.cs tests/Iris.App.Tests/Updates/UpdateRenderingTests.cs tests/Iris.App.Tests/History/HistoryRenderingTests.cs tests/Iris.App.Tests/Localization/LocalizationRenderingTests.cs
git status --short -- src tests
```

期望：这六个路径是 `M`；`src` 下除任务 1、2 已暂存的文件外没有改动（本任务不改 `src/`）。

把这六处逐条记进完成报告的「改过的既有测试」：文件、用例名（后三个文件写辅助方法名与调用它的用例）、改了什么（守卫断言 + 对齐 / 还原）、为什么（S0-13）。

**⚠ 与裁决不符之处（任务 3）**

1. **W04：S0-13 自己的两条要求互相冲突，步骤里选了「同一段同步代码」那一条。** S0-13 的表把 W04 的位置写成 :1525–1530，同时又要求「设置与还原……中间不能有 `await`」。事实：:1530 在 `try` 之外，它之后、`try`（:1574）之前有 `await using var env`（:1536）与 `await env.Coordinator.StartAsync()`（:1540）；在那里设成深色，中间任何一步抛异常都没有 `finally` 还原，别的并行测试也能在 `await` 的空当里看到深色的 Iris 字典。W04 另有第二处切主题 :1578（每个变体一次，在 `try` 内、`window.Show()` 之后），从它到 `try` 结束没有 `await`。所以步骤把对齐加在 :1578、把还原放在 `finally` 的第一句，:1530 不加。八个变体仍然每个都对齐。**裁决的表没有改**；用户若坚持 :1530 也加，多加一行即可，但那一行没有 `finally` 保护。
2. **已裁决（协调方 2026-11-17，C2，【计划裁决】，进偏差清单）：`SelectionSettingsRenderingTests` 在每次渲染取图之前同步地再对齐一次 Iris 主题字典，不挪进不并行的集合。** 背景：它在 :30 切主题，之后每张图渲染之前都要等渲染帧（:38–39 的 `await NextFrame` 与 `await …InvokeAsync`），这些 `await` 去不掉；它又没有 `[Collection]`，与别的集合并行，等帧时用例 7 或 W04 的 `finally` 可能把共享的 Iris 字典还原成浅色。做法已写进 Step 5 的 (b-2)：在 :49 之前加对齐与一次同步的 `UpdateLayout()`，对齐与取图之间不含 `await`。原先列出的选项 (b)（放进不并行的集合）不做。**本裁决只管 Iris 字典**：WPF-UI 字典的同类空当（用例 7 的 `finally` :439–443 会把它改回浅色）是既有行为，本任务不动，完成报告里列为遗留。
3. **W04 与 `SelectionSettingsRenderingTests` 本来就不还原 WPF-UI 的应用级主题**（两者跑完都停在深色）。这是既有行为，S0-13 只管 Iris 字典，本任务没有动它；写在这里是因为它与第 2 条是同一类外漏。
4. **用例 7 的对齐行在 `try` 之外**（:267 之后，照裁决「紧跟着既有的 `Apply`」）。:269–297 之间若抛异常，Iris 字典与 WPF-UI 字典一样不会被还原——与既有写法的缺口相同，没有扩大。
5. **已裁决：保留（协调方 2026-11-17，C3，【计划裁决】，进偏差清单）。** Step 3 的六条「夹具守卫」断言是撰写者加的，S0-13 与骨架都没有要求；目的是让本任务有一次「先红后绿」。Step 3、Step 4 照做，不删。

**验证命令：** `FullyQualifiedName~SettingsHotkeysIntegrationTests|FullyQualifiedName~SelectionSettingsRenderingTests|FullyQualifiedName~SettingsWindowTranslationUiTests|FullyQualifiedName~UpdateRenderingTests|FullyQualifiedName~HistoryRenderingTests|FullyQualifiedName~LocalizationRenderingTests|FullyQualifiedName~IrisThemeDictionaryTests` —— 预期全绿，且与改前基线条数一致。

**要改的既有测试：** 上面六个文件各一处，理由都是「切主题时同步 Iris 颜色字典并还原，避免深浅混搭与状态外漏」。

### Task 4: 窗口骨架：着色层、导航、底栏

**目的：** 落地 S0-1、S0-2、S0-6（设置窗口部分）与底栏的外观；各页内容本任务不动。

**Files:**
- Create: `tests/Iris.App.Tests/Settings/SettingsWindowNavigationLayoutTests.cs`
- Modify: `src/Iris.App/Settings/SettingsWindow.xaml`——根 `Grid` :20；主体列定义 :34–37；导航 :40–126（去右侧竖线与背景，含超长物理行 :115）；底栏 :1050–1141（去 :1053 的背景、把已有的上边线画刷换成 `IrisAcrylicCardStrokeBrush`，其余不动）
- Modify: `src/Iris.App/Settings/SettingsWindow.xaml.cs`——`ApplyAdaptiveLayout` cs:55–99
- Test: `tests/Iris.App.Tests/Settings/SelectionSettingsRenderingTests.cs`（金丝雀，可能要改取点）

**范围与要点：**
- 做：根 `Grid` 加着色画刷；列定义起名 `NavColumnDef`；导航去右侧竖线与不透明底、标题起名 `NavTitleText`；八个导航项按 S0-2 重写（补四个名字、`BasedOn IrisNavItem`、强调色短线、图标、单行省略、提示与读屏名）；底栏去不透明底并把上边线画刷换成 `IrisAcrylicCardStrokeBrush`。
- **`ApplyAdaptiveLayout` 本任务只「加」不「删」**：在方法体最前面加导航宽窄切换（自带 `NavColumnDef` 为空的守卫），原有的翻译页分栏逻辑（含 cs:57 的守卫）原样留着，等任务 12 再删。这样任务 4–11 期间「翻译服务」页仍按旧的分栏工作。
- 保持：八个导航项仍是 `Border`、仍走原来的 `OnNav*Click`；`NavOcr` / `NavScreenshot` / `NavNetwork` / `NavHistory` 名字不变；底栏里七个按条件显示的按钮、四个状态文字、取消 / 保存的 `Width=80`、`IsCancel` / `IsDefault`、`Click` 全不动；只读提示仍只在底栏出现一次；窗口四个尺寸不动。
- 不做：不给导航项加可聚焦；不加应用图标；不动各页内容。
- 风险：R5、R10、R13（:115 那一行先拆成多行再改，拆行本身不改任何属性值）；导航区里的中文注释只许整条删。
- 已知的中间态：导航变宽后，旧分栏的「翻译服务」页在 760 宽下少了 36 像素。预判 `SettingsWindowTranslationUiTests` 用例 7 仍绿；若它只在块 1–3（2.9(c) 的表）变红，记为「到任务 12 重写前的已知中间态」继续往下做，其他任何变红都必须在本任务内解决。
- 金丝雀：像素 (150, 50) 现在落在导航里。先按 S0-6 的颜色手算预期（着色层叠在窗口背景上的 R 分量），再跑；红了把取点挪到内容区并记入清单。

**步骤提纲：**
- [ ] 写 `SettingsWindowNavigationLayoutTests`：宽度 600 / 679 → 导航 48，680 / 760 → 176；八个导航项按名字取得到、是 `Border`、提示与读屏名等于该页导航文字；只有当前页的短线可见；窄态下导航文字宽度为 0、图标可见、`NavTitleText` 折叠；根 `Grid` 背景是着色画刷、导航与底栏无背景；600 宽下取消 / 保存右边缘不超出窗口
- [ ] 跑，确认失败
- [ ] 改 `SettingsWindow.xaml` 的底栏 → 导航 → 列定义 → 根 `Grid`（从后往前）
- [ ] 在 `ApplyAdaptiveLayout` 前部加导航切换
- [ ] 跑本任务过滤组与「设置全集」；处理金丝雀；`git add`

**详细步骤：**

> **声明：本文件里的全部 XAML / C# / 测试代码都没有编译过、没有运行过。** 撰写时只读了源码与笔记（提交 `dc08c48`），没有执行 `dotnet build`、`dotnet test`，也没有启动应用。凡是「预期红 / 预期绿 / 预期数值」都是读代码推算；每处推算都写了「不符合时怎么办」。
>
> 行号一律对应 `dc08c48`；Task 1–3 不改 `SettingsWindow.xaml` 与 `SettingsWindow.xaml.cs`，所以开工时这两个文件的行号应当没有漂移，但仍按 5.0 的约定先重新 grep（Step 1）。

#### 详细步骤

- [ ] **Step 1: 开工核实——前置任务的产物、行号、改前基线**

(a) 前置任务的产物都在（Task 1、2、3 已暂存）。逐条跑，任何一条与期望不符就停下来报告，不往下做：

```
git status --short -- src/Iris.App/Settings
grep -n 'x:Key="IrisNavItem"\|x:Key="IrisNavWidth"\|x:Key="IrisNavCompactWidth"' src/Iris.App/Themes/Iris.Styles.xaml
grep -n -A1 'x:Key="IrisNavItem"' src/Iris.App/Themes/Iris.Styles.xaml
grep -c 'x:Key="IrisAcrylicTintBrush"' src/Iris.App/Themes/Iris.Light.xaml src/Iris.App/Themes/Iris.Dark.xaml
grep -c 'ApplyIrisThemeDictionary(theme)' tests/Iris.App.Tests/Settings/SelectionSettingsRenderingTests.cs
```

期望：第 1 条**没有输出**（`SettingsWindow.xaml` 与 `.xaml.cs` 相对 `dc08c48` 没有改动——Step 4 的校验依赖这一点）；第 2 条 3 行；第 3 条的第二行是 `<Setter Property="Padding" Value="8,8" />`；第 4 条两个文件各输出 `1`；第 5 条输出 `2`（Task 3 Step 5 的 (b) 与 (b-2) 各一处）。

(b) 重新取行号（5.0 的约定）：

```
grep -n 'x:Name="NavOcr"\|x:Name="NavNetwork"\|x:Name="NavHistory"\|OnNavGeneralClick\|OnNavTranslationClick\|OnNavHotkeysClick\|OnNavSelectionClick\|SolidBackgroundFillColorSecondaryBrush\|UiText.SettingsTitle}\|<ColumnDefinition Width="140" />\|^    <Grid>' src/Iris.App/Settings/SettingsWindow.xaml | cut -c1-110
grep -n 'internal void ApplyAdaptiveLayout\|TranslationLayoutGrid == null' src/Iris.App/Settings/SettingsWindow.xaml.cs
```

期望（`dc08c48`）：XAML 依次是 :20、:35、:43、:47、:53、:70、:87、:102、:115、:116、:120、:1053 共 12 行（:115 那一行同时含 `NavOcr`，只算一行）；C# 是 :55 与 :57。行号整体平移没关系（后面每一步都用「旧文本」定位，不用行号）；**行数不是 12 / 2**，说明文件结构与撰写时不同，停下来报告。

(c) 改前基线。用 5.0 的命令前缀（Task 1 Step 1(c) 若回填过别的写法，以回填后的为准）跑本任务的既有过滤组：

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~AcrylicWindowTests|FullyQualifiedName~SettingsWindowTranslationUiTests|FullyQualifiedName~SettingsWindowPresenterTests|FullyQualifiedName~SelectionSettingsRenderingTests|FullyQualifiedName~NetworkSettingsFoundationTests|FullyQualifiedName~MouseRenderingTests|FullyQualifiedName~IrisThemeDictionaryTests"
```

期望：全绿。把汇总行（通过 / 失败 / 跳过的条数）记下来，记作「基线 A」；Step 12 要与它逐项比对。基线本身有红：停下来报告，不开工。

再用 5.0 的「设置全集」过滤式跑一次（Task 3 的最后一步刚跑过同一组，这里是为了留下条数）：

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~Iris.App.Tests.Settings|FullyQualifiedName~Iris.App.Tests.Themes|FullyQualifiedName~Iris.App.Tests.Localization|FullyQualifiedName~Iris.App.Tests.Updates|FullyQualifiedName~MouseRenderingTests|FullyQualifiedName~HistoryRenderingTests|FullyQualifiedName~ThemeServicePreviewTests"
```

期望：全绿。把汇总行记作「基线 B」，Step 13 用。有红：同样停下来报告，不开工。

- [ ] **Step 2: 写 `SettingsWindowNavigationLayoutTests`（新文件，整文件如下）**

Create `tests/Iris.App.Tests/Settings/SettingsWindowNavigationLayoutTests.cs`。说明：

- 放进不并行的集合 `IrisThemeResources`（定义在 `Themes/IrisThemeResourcesCollectionDefinition.cs`，`DisableParallelization = true`），因为最后一条用例要临时切换应用级的 Iris 颜色字典。
- 装配视图模型的 `CreateServiceProvider` 照抄 `SettingsWindowTranslationUiTests.cs:25–63`（那边是 `private static`，调不到），只去掉了 `initialSettings` 参数与 `AddTransient<SettingsWindow>()`；窗口用无参构造 `new SettingsWindow()`（`AcrylicWindowTests.cs:21` 的既有写法）。
- 短线、图标、文字都没有名字，按结构取：导航项 `Border` 的 `Child` 是 `Grid`，`Grid` 的直接子元素里恰好一个 `Border`（短线）、一个 `ui:SymbolIcon`、一个 `TextBlock`。
- 文件里**不要** `using Wpf.Ui.Controls;`（它有自己的 `TextBlock`、`Button`，会与 `System.Windows.Controls` 的同名类型冲突），WPF-UI 的类型一律写全名。
- 宽度边界（679 / 680）用直接调 `ApplyAdaptiveLayout(width)` 的方式测，不 `Show()`：窗口实际宽度会按显示器缩放取整，在非整数缩放下 680 可能落成 679.x，靠真实窗口测边界不稳。真实窗口只测 600 与 760 两档。

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Iris.App.Hosting;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Iris.App.Tests.Settings;

// T-041 阶段 2 · Task 4：设置窗口骨架（背板着色层、导航、底栏）。
// 导航宽度的两个数（176 / 48）与阈值 680 来自设计文档；这里按字面钉住，不从主题资源里读。
[Collection("IrisThemeResources")]
public sealed class SettingsWindowNavigationLayoutTests
{
    private const double WideNavWidth = 176;
    private const double CompactNavWidth = 48;

    private sealed record NavItem(string Name, string PageKey, Func<string> Text, Wpf.Ui.Controls.SymbolRegular Icon);

    // 顺序就是界面上的显示顺序。Text 用委托而不是字符串：UiText 的值随界面语言变，不能在类型初始化时取死。
    private static readonly NavItem[] NavItems =
    [
        new("NavGeneral", "general", () => UiText.SettingsNavGeneral, Wpf.Ui.Controls.SymbolRegular.Settings20),
        new("NavTranslation", "translation", () => UiText.SettingsNavTranslation, Wpf.Ui.Controls.SymbolRegular.Translate20),
        new("NavHotkeys", "hotkeys", () => UiText.SettingsNavHotkeys, Wpf.Ui.Controls.SymbolRegular.Keyboard20),
        new("NavSelection", "selection", () => UiText.SettingsNavSelection, Wpf.Ui.Controls.SymbolRegular.CursorClick20),
        new("NavOcr", "ocr", () => UiText.OcrPageTitle, Wpf.Ui.Controls.SymbolRegular.ScanText20),
        new("NavScreenshot", "screenshot", () => UiText.ScreenshotPageTitle, Wpf.Ui.Controls.SymbolRegular.Screenshot20),
        new("NavNetwork", "network", () => UiText.NetworkTitle, Wpf.Ui.Controls.SymbolRegular.Globe20),
        new("NavHistory", "history", () => UiText.HistoryTitle, Wpf.Ui.Controls.SymbolRegular.History20),
    ];

    // 照抄 SettingsWindowTranslationUiTests.CreateServiceProvider（那边是 private），去掉了用不到的部分。
    private static ServiceProvider CreateServiceProvider()
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(new AppSettings());

        var autoStartService = Substitute.For<IAutoStartService>();
        var editCoordinator = Substitute.For<ISettingsEditCoordinator>();
        var receiptManager = new SettingsApplyReceiptManager();
        var uiDispatcher = new ImmediateUiDispatcher();

        var services = new ServiceCollection();
        services.AddSingleton(settingsService);
        services.AddSingleton(autoStartService);
        services.AddSingleton<ISettingsEditCoordinator>(editCoordinator);
        services.AddSingleton<ISettingsApplyReceiptManager>(receiptManager);
        services.AddSingleton<IUiDispatcher>(uiDispatcher);
        services.AddSingleton<ThemeService>();

        services.AddScoped<GeneralSettingsSaveCoordinator>(sp => new GeneralSettingsSaveCoordinator(
            settingsService,
            editCoordinator,
            autoStartService,
            receiptManager,
            NullLogger<GeneralSettingsSaveCoordinator>.Instance));

        services.AddScoped<SettingsViewModel>(sp => new SettingsViewModel(
            settingsService,
            sp.GetRequiredService<ThemeService>(),
            sp.GetRequiredService<GeneralSettingsSaveCoordinator>(),
            editCoordinator,
            receiptManager,
            uiDispatcher,
            NullLogger<SettingsViewModel>.Instance));

        return services.BuildServiceProvider();
    }

    // 建窗口、挂视图模型，按需 Show()；无论断言成败都关窗。必须在 WpfTestRunner 的 STA 线程里调用。
    private static void WithWindow(double width, double height, bool show, Action<SettingsWindow, SettingsViewModel> body)
    {
        using var provider = CreateServiceProvider();
        using var scope = provider.CreateScope();
        var vm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
        var window = new SettingsWindow();
        try
        {
            window.DataContext = vm;
            window.Left = -20000;
            window.Top = -20000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Width = width;
            window.Height = height;
            if (show)
            {
                window.Show();
                window.UpdateLayout();
            }

            body(window, vm);
        }
        finally
        {
            window.DataContext = null;
            window.ForceClose = true;
            window.Close();
        }
    }

    private static T Named<T>(SettingsWindow window, string name) =>
        window.FindName(name).ShouldBeOfType<T>($"element named {name}");

    private static Border Nav(SettingsWindow window, NavItem item) => Named<Border>(window, item.Name);

    private static (Border Accent, Wpf.Ui.Controls.SymbolIcon Icon, TextBlock Label) Parts(Border nav)
    {
        var grid = nav.Child.ShouldBeOfType<Grid>();
        return (
            grid.Children.OfType<Border>().ShouldHaveSingleItem(),
            grid.Children.OfType<Wpf.Ui.Controls.SymbolIcon>().ShouldHaveSingleItem(),
            grid.Children.OfType<TextBlock>().ShouldHaveSingleItem());
    }

    // 导航外框与底栏外框都没有名字：从一个有名字的后代沿逻辑树往上找。
    // NavOcr 与 StatusMessageTextBlock 改造前就有名字，所以改造前这里取得到，红会红在后面的断言上。
    private static Border NavHost(SettingsWindow window)
    {
        var panel = Named<Border>(window, "NavOcr").Parent.ShouldBeOfType<StackPanel>();
        var scroll = panel.Parent.ShouldBeOfType<ScrollViewer>();
        return scroll.Parent.ShouldBeOfType<Border>();
    }

    private static Border Footer(SettingsWindow window)
    {
        var statusGrid = Named<TextBlock>(window, "StatusMessageTextBlock").Parent.ShouldBeOfType<Grid>();
        var footerGrid = statusGrid.Parent.ShouldBeOfType<Grid>();
        return footerGrid.Parent.ShouldBeOfType<Border>();
    }

    private static Color ColorOf(object? brush) => brush.ShouldBeOfType<SolidColorBrush>().Color;

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    [Fact]
    public Task Constructor_LeavesTheNavigationWideAtTheDefaultWidth() => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(760, 520, show: false, (window, _) =>
        {
            var column = Named<ColumnDefinition>(window, "NavColumnDef");
            column.Width.IsAbsolute.ShouldBeTrue();
            column.Width.Value.ShouldBe(WideNavWidth);
            Named<TextBlock>(window, "NavTitleText").Visibility.ShouldBe(Visibility.Visible);
        });
    });

    [Theory]
    [InlineData(600d, CompactNavWidth)]
    [InlineData(679d, CompactNavWidth)]
    [InlineData(679.99, CompactNavWidth)]
    [InlineData(680d, WideNavWidth)]
    [InlineData(760d, WideNavWidth)]
    public Task ApplyAdaptiveLayout_SwitchesTheNavigationAtTheCompactThreshold(double width, double expectedNavWidth) => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(760, 520, show: false, (window, _) =>
        {
            var column = Named<ColumnDefinition>(window, "NavColumnDef");
            var title = Named<TextBlock>(window, "NavTitleText");

            // 先切到相反的一档再切回来：证明每次调用都会写导航，不被旧分栏逻辑的「模式没变就返回」挡住。
            window.ApplyAdaptiveLayout(expectedNavWidth == WideNavWidth ? 600 : 760);
            window.ApplyAdaptiveLayout(width);

            column.Width.IsAbsolute.ShouldBeTrue();
            column.Width.Value.ShouldBe(expectedNavWidth);
            title.Visibility.ShouldBe(expectedNavWidth == WideNavWidth ? Visibility.Visible : Visibility.Collapsed);
        });
    });

    [Theory]
    [InlineData(600d, 440d, CompactNavWidth)]
    [InlineData(760d, 520d, WideNavWidth)]
    public Task ShownWindow_LaysOutTheNavigationColumnAtTheExpectedWidth(double width, double height, double expectedNavWidth) => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(width, height, show: true, (window, _) =>
        {
            Named<ColumnDefinition>(window, "NavColumnDef").ActualWidth.ShouldBe(expectedNavWidth, 1d);
            NavHost(window).ActualWidth.ShouldBe(expectedNavWidth, 1d);

            // 默认页是「常规」：它的滚动区左缘必须正好接在导航右缘上（相对根 Grid 量，不受窗口外框影响）。
            var root = window.Content.ShouldBeOfType<Grid>();
            var page = Named<ScrollViewer>(window, "GeneralPageScroll");
            page.IsVisible.ShouldBeTrue();
            page.TransformToAncestor(root).Transform(new Point(0, 0)).X.ShouldBe(expectedNavWidth, 1d);
        });
    });

    [Fact]
    public Task NavItems_AreNamedBordersWithIconLabelToolTipAndAutomationName() => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(760, 520, show: false, (window, _) =>
        {
            foreach (var item in NavItems)
            {
                var nav = Nav(window, item);
                string text = item.Text();
                text.ShouldNotBeNullOrWhiteSpace(item.Name);

                (nav.ToolTip as string).ShouldBe(text, item.Name);
                System.Windows.Automation.AutomationProperties.GetName(nav).ShouldBe(text, item.Name);

                // 这两条来自主题层的 IrisNavItem：证明内联样式是 BasedOn 它，且没有本地值把它盖掉。
                nav.Padding.ShouldBe(new Thickness(8), item.Name);
                nav.Cursor.ShouldBe(Cursors.Hand, item.Name);

                var (accent, icon, label) = Parts(nav);
                icon.Symbol.ShouldBe(item.Icon, item.Name);
                icon.FontSize.ShouldBe(20d, item.Name);
                label.Text.ShouldBe(text, item.Name);
                label.TextWrapping.ShouldBe(TextWrapping.NoWrap, item.Name);
                label.TextTrimming.ShouldBe(TextTrimming.CharacterEllipsis, item.Name);
                label.Margin.ShouldBe(new Thickness(12, 0, 0, 0), item.Name);
                accent.Width.ShouldBe(3d, item.Name);
                accent.Height.ShouldBe(16d, item.Name);
                accent.CornerRadius.ShouldBe(new CornerRadius(2), item.Name);
                ColorOf(accent.Background).ShouldBe(ColorOf(window.FindResource("AccentFillColorDefaultBrush")), item.Name);
            }
        });
    });

    [Fact]
    public Task NavItems_OnlyTheCurrentPageShowsTheAccentBarAndTheSubtleFill() => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(760, 520, show: true, (window, vm) =>
        {
            Color subtle = ColorOf(window.FindResource("SubtleFillColorSecondaryBrush"));
            subtle.ShouldNotBe(Colors.Transparent);

            foreach (var current in NavItems)
            {
                vm.CurrentPageKey = current.PageKey;
                window.UpdateLayout();

                foreach (var item in NavItems)
                {
                    bool selected = item.Name == current.Name;
                    string because = $"{item.Name} while the page is {current.PageKey}";
                    var nav = Nav(window, item);
                    Parts(nav).Accent.Visibility.ShouldBe(selected ? Visibility.Visible : Visibility.Collapsed, because);
                    ColorOf(nav.Background).ShouldBe(selected ? subtle : Colors.Transparent, because);
                }
            }
        });
    });

    [Fact]
    public Task NavItems_StillRouteMouseUpThroughTheExistingHandlers() => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(760, 520, show: false, (window, vm) =>
        {
            vm.CurrentPageKey.ShouldBe("general");

            // 从第二项开始点、最后点第一项：每一次点击都必须真的换页。
            foreach (var item in NavItems.Skip(1).Append(NavItems[0]))
            {
                Nav(window, item).RaiseEvent(new MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                });
                vm.CurrentPageKey.ShouldBe(item.PageKey, item.Name);
            }
        });
    });

    [Fact]
    public Task CompactNavigation_ShowsIconsOnlyAndKeepsToolTips() => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(600, 440, show: true, (window, _) =>
        {
            Named<TextBlock>(window, "NavTitleText").Visibility.ShouldBe(Visibility.Collapsed);

            foreach (var item in NavItems)
            {
                var nav = Nav(window, item);
                var (_, icon, label) = Parts(nav);

                // 48（导航列）− 6 − 6（导航外框的左右内边距）= 36；滚动条不得挤占这 36。
                nav.ActualWidth.ShouldBe(36d, 1d, item.Name);

                // 文字所在的列（内部 Grid 的第 1 列）被挤成 0 宽，文字靠布局裁剪消失。
                // 不断言 label.ActualWidth：被裁剪的元素，ActualWidth 仍是它「想要」的宽度，不一定是 0。
                Grid.GetColumn(label).ShouldBe(1, item.Name);
                nav.Child.ShouldBeOfType<Grid>().ColumnDefinitions[1].ActualWidth.ShouldBeLessThan(1d, item.Name);

                icon.IsVisible.ShouldBeTrue(item.Name);
                icon.ActualWidth.ShouldBeGreaterThan(0d, item.Name);
                icon.TransformToAncestor(nav).Transform(new Point(icon.ActualWidth, 0)).X.ShouldBeLessThanOrEqualTo(nav.ActualWidth + 0.5, item.Name);
                (nav.ToolTip as string).ShouldBe(item.Text(), item.Name);
            }
        });
    });

    [Fact]
    public Task WideNavigation_ShowsEveryLabelOnOneLine() => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(760, 520, show: true, (window, _) =>
        {
            Named<TextBlock>(window, "NavTitleText").IsVisible.ShouldBeTrue();

            double firstHeight = Nav(window, NavItems[0]).ActualHeight;
            firstHeight.ShouldBeGreaterThan(0d);
            foreach (var item in NavItems)
            {
                var nav = Nav(window, item);
                var label = Parts(nav).Label;
                Grid.GetColumn(label).ShouldBe(1, item.Name);
                nav.Child.ShouldBeOfType<Grid>().ColumnDefinitions[1].ActualWidth.ShouldBeGreaterThan(label.Margin.Left, item.Name);
                label.ActualWidth.ShouldBeGreaterThan(0d, item.Name);
                // 单行不折行：八项一样高。
                nav.ActualHeight.ShouldBe(firstHeight, 0.5, item.Name);
            }
        });
    });

    [Fact]
    public Task Footer_SaveAndCancelStayInsideTheMinimumWindow() => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(600, 440, show: true, (window, _) =>
        {
            foreach (string content in new[] { UiText.SettingsCancel, UiText.SettingsSave })
            {
                var button = Descendants<Wpf.Ui.Controls.Button>(window).Single(b => Equals(b.Content, content));
                button.IsVisible.ShouldBeTrue(content);
                button.ActualWidth.ShouldBeGreaterThanOrEqualTo(70d, content);
                var bottomRight = button.TransformToAncestor(window).Transform(new Point(button.ActualWidth, button.ActualHeight));
                bottomRight.X.ShouldBeLessThanOrEqualTo(window.ActualWidth + 1d, content);
                bottomRight.Y.ShouldBeLessThanOrEqualTo(window.ActualHeight + 1d, content);
            }
        });
    });

    [Fact]
    public Task Skeleton_TintsTheRootAndLeavesNavigationAndFooterWithoutAnOpaqueFill() => WpfTestRunner.RunAsync(() =>
    {
        WithWindow(760, 520, show: true, (window, _) =>
        {
            try
            {
                ThemeService.ApplyIrisThemeDictionary(Wpf.Ui.Appearance.ApplicationTheme.Light);
                var root = window.Content.ShouldBeOfType<Grid>();
                Color lightTint = ColorOf(window.FindResource("IrisAcrylicTintBrush"));
                root.Background.ShouldNotBeNull();
                ColorOf(root.Background).ShouldBe(lightTint);

                var navHost = NavHost(window);
                navHost.Background.ShouldBeNull();
                navHost.BorderThickness.ShouldBe(new Thickness(0));

                var footer = Footer(window);
                footer.Background.ShouldBeNull();
                footer.BorderThickness.ShouldBe(new Thickness(0, 1, 0, 0));
                ColorOf(footer.BorderBrush).ShouldBe(ColorOf(window.FindResource("IrisAcrylicCardStrokeBrush")));

                // 着色层与底栏上边线必须是 DynamicResource：换成深色字典后跟着变。
                ThemeService.ApplyIrisThemeDictionary(Wpf.Ui.Appearance.ApplicationTheme.Dark);
                Color darkTint = ColorOf(window.FindResource("IrisAcrylicTintBrush"));
                darkTint.ShouldNotBe(lightTint);
                ColorOf(root.Background).ShouldBe(darkTint);
                ColorOf(footer.BorderBrush).ShouldBe(ColorOf(window.FindResource("IrisAcrylicCardStrokeBrush")));
            }
            finally
            {
                // 整个测试进程只有一个 Application：不还原，深色的 Iris 字典就留给后面的测试了。
                ThemeService.ApplyIrisThemeDictionary(Wpf.Ui.Appearance.ApplicationTheme.Light);
            }
        });
    });
}
```

共 10 个测试方法、15 条用例（两条 `Theory` 分别 5 条、2 条，加 8 条 `Fact`）。

- [ ] **Step 3: 跑，确认失败**

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsWindowNavigationLayoutTests"
```

期望：**编译通过**（测试只用字符串 `FindName`，没有引用任何还不存在的成员），15 条里 **14 条失败、1 条通过**：

| 用例 | 期望的失败现象 |
|---|---|
| `Constructor_…`、`ApplyAdaptiveLayout_…`（5 条）、`ShownWindow_…`（2 条） | Shouldly：`element named NavColumnDef` 应为 `ColumnDefinition`，实际是 `null` |
| `NavItems_AreNamedBorders…`、`NavItems_OnlyTheCurrentPage…` | `element named NavGeneral` 应为 `Border`，实际是 `null` |
| `NavItems_StillRouteMouseUp…` | `element named NavTranslation` 应为 `Border`，实际是 `null` |
| `CompactNavigation_…`、`WideNavigation_…` | `element named NavTitleText` 应为 `TextBlock`，实际是 `null` |
| `Skeleton_…` | `root.Background` 不应为 `null` |
| `Footer_SaveAndCancelStayInsideTheMinimumWindow` | **通过**——它是底栏的回归守卫，改造前后都应该绿 |

与期望不符时：

- 编译失败（`CS0104` 二义性、`CS0246` 找不到类型、`CS1061` 没有某成员）：只改这个新测试文件，把报错的类型写成全限定名；不改任何 `src/` 文件。报的是 `SymbolRegular` 没有某个成员（`CS0117`）：那个图标名在 4.3.0 里不存在——停下来报告（S0-2 的图标表写的是「已核实」，不自行换图标）。
- `Footer_…` 红：与本任务无关的既有问题，把失败消息记下来，停下来报告。
- 其余 14 条里有任何一条**通过**：说明它没守住东西，先看是哪条断言被绕过了，改到它在改造前确实会红再继续。

- [ ] **Step 4: 把超长的那一行拆成两行（只动空白，不动任何属性值）**

Modify `src/Iris.App/Settings/SettingsWindow.xaml`。`NavOcr` 与 `NavScreenshot` 挤在同一个 1110 字符的物理行里（`dc08c48` 的 :115）；先在两个 `Border` 之间断开，后面的步骤才能逐项替换。

旧文本（全文件唯一，是那一行的中段）：

```xml
FontWeight="Medium"/></Border><Border x:Name="NavScreenshot"
```

新文本（第二行前面是 20 个空格，与相邻的导航项对齐）：

```xml
FontWeight="Medium"/></Border>
                    <Border x:Name="NavScreenshot"
```

校验这一步只动了空白（依赖 Step 1(a) 确认过的「改前无改动」）：

```
git show HEAD:src/Iris.App/Settings/SettingsWindow.xaml | tr -d ' \t\r\n' | sha256sum
tr -d ' \t\r\n' < src/Iris.App/Settings/SettingsWindow.xaml | sha256sum
git diff --stat -- src/Iris.App/Settings/SettingsWindow.xaml
awk 'length($0) > 700' src/Iris.App/Settings/SettingsWindow.xaml | wc -l
file src/Iris.App/Settings/SettingsWindow.xaml
```

期望：两个哈希**相同**；`--stat` 是 `2 insertions(+), 1 deletion(-)`；`awk` 那条输出 `0`；`file` 报 `with CRLF line terminators`（报成 `CRLF, LF` 混用也**不用处理**：本机 `core.autocrlf=true`，暂存时 git 会统一成 LF，见「续写时补读到的事实（2026-11-23）」最后一条）。哈希不同：撤掉这次编辑重做，不往下走。

拆完后文件是 1144 行，原 :116 之后的行号都加 1。

- [ ] **Step 5: 底栏——去掉不透明底，上边线换成 Iris 的描边画刷**

Modify `src/Iris.App/Settings/SettingsWindow.xaml`（从文件末尾往前改，这是第一处）。底栏现在**已经有**上边线（`BorderThickness="0,1,0,0"`），只是画刷是 WPF-UI 的 `CardStrokeColorDefaultBrush`；所以这里是「换画刷 + 删背景」，`BorderThickness` 与 `Padding` 不动。

旧文本（`dc08c48` 的 :1049–1054；用 `Grid.Row="2"` 起头的这五行定位，全文件唯一）：

```xml
        <Border Grid.Row="2"
                BorderThickness="0,1,0,0"
                BorderBrush="{DynamicResource CardStrokeColorDefaultBrush}"
                Background="{DynamicResource SolidBackgroundFillColorSecondaryBrush}"
                Padding="16,10">
```

新文本：

```xml
        <Border Grid.Row="2"
                BorderThickness="0,1,0,0"
                BorderBrush="{DynamicResource IrisAcrylicCardStrokeBrush}"
                Padding="16,10">
```

- 它上面那条中文注释（`<!-- 底部状态与操作栏 -->`）**原样留着**，一个字不动。
- 底栏 `Border` 里面的内容（`dc08c48` 的 :1055–1140）一行不碰：七个按条件显示的 `Button`、`StatusMessageTextBlock`、只读提示、`MouseRuntimeStatusTextBlock`、`OcrTestStatusText`、取消 / 保存的 `Width="80"`、`IsCancel`、`IsDefault`、`Click="OnSaveButtonClick"`。

改完自查：

```
grep -c 'SolidBackgroundFillColorSecondaryBrush' src/Iris.App/Settings/SettingsWindow.xaml
grep -n 'IrisAcrylicCardStrokeBrush' src/Iris.App/Settings/SettingsWindow.xaml
```

期望：第一条 `1`（只剩导航那一处，Step 8 删）；第二条 1 行。

- [ ] **Step 6: 导航项（后四项）——历史、网络、截图、OCR 改成「短线 + 图标 + 单行文字」**

Modify `src/Iris.App/Settings/SettingsWindow.xaml`（继续从后往前）。按 历史 → 网络 → 截图 → OCR 的顺序各替换一次；四个 `x:Name`、四个 `MouseLeftButtonUp` 处理函数名**原样保留**。

八个导航项改完后是同一个形状。每一处为什么这样写（Step 7 的四项同理）：

| 写法 | 原因 |
|---|---|
| 外层 `Border` 上的 `CornerRadius="4" Padding="10,6" Margin="0,0,0,4" Cursor="Hand"` 四个本地属性**删掉** | 本地值的优先级高于样式；留着就把 `IrisNavItem` 的圆角 6、内边距 8、间距 2 盖掉（Step 2 的测试断言 `Padding` 是 8） |
| 内联 `Style` 加 `BasedOn="{StaticResource IrisNavItem}"`；`DataTrigger` 原样保留；`<Setter Property="Background" Value="Transparent"/>` 删掉 | S0-2、R5。透明底已由 `IrisNavItem` 提供，重复的那条 `Setter` 不再需要；**触发器一条不删** |
| 外层 `Border` 加 `ToolTip` 与 `AutomationProperties.Name` | S0-2：宽态、窄态都设，值是该页导航文字的那条 `UiText` |
| 内部两列 `Grid`：第 0 列 `Auto` 放图标，第 1 列 `*` 放文字 | S0-2。窄态下第 1 列被挤成 0 宽，文字自然不显示，不需要隐藏文字的代码 |
| 短线 `Border`：`Width="3" Height="16" CornerRadius="2"`、左对齐、垂直居中、`AccentFillColorDefaultBrush`；自带内联样式，默认 `Collapsed`，同一个页面键命中时 `Visible` | S0-2。`Visibility` 只能由样式给，不能写成本地属性（本地值会压住触发器） |
| 短线的 `Margin="-8,0,0,0"` | 把短线从图标格的左缘再往左挪 8，正好贴到导航项的左边缘（8 = `IrisNavItem` 的左内边距），与样张「左缘竖线」一致；负边距让它的期望宽度为 0，不占布局宽度。−8 是撰写者定的，S0-2 没写，见文末 ⚠ 第 3 条 |
| 图标 `ui:SymbolIcon`，`FontSize="20"` 直接写在元素上 | S0-2「尺寸统一用 20」；本地值一定生效（见「WPF-UI 的三个事实」第 2 条） |
| 文字 `TextBlock`：去掉 `TextWrapping="Wrap"`，加 `TextTrimming="CharacterEllipsis"`、`Margin="12,0,0,0"`、`VerticalAlignment="Center"`；`FontWeight="Medium"` 保留 | S0-2：单行、省略号、左边距 12 |

(a) 历史。旧文本（`x:Name="NavHistory"` 起的四行）：

```xml
                    <Border x:Name="NavHistory" CornerRadius="4" Padding="10,6" Margin="0,0,0,4" Cursor="Hand" MouseLeftButtonUp="OnNavHistoryClick">
                        <Border.Style><Style TargetType="Border"><Setter Property="Background" Value="Transparent"/><Style.Triggers><DataTrigger Binding="{Binding CurrentPageKey}" Value="history"><Setter Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}"/></DataTrigger></Style.Triggers></Style></Border.Style>
                        <TextBlock Text="{x:Static app:UiText.HistoryTitle}" TextWrapping="Wrap" FontWeight="Medium"/>
                    </Border>
```

新文本：

```xml
                    <Border x:Name="NavHistory"
                            MouseLeftButtonUp="OnNavHistoryClick"
                            ToolTip="{x:Static app:UiText.HistoryTitle}"
                            AutomationProperties.Name="{x:Static app:UiText.HistoryTitle}">
                        <Border.Style>
                            <Style TargetType="Border" BasedOn="{StaticResource IrisNavItem}">
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding CurrentPageKey}" Value="history">
                                        <Setter Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </Border.Style>
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" />
                            </Grid.ColumnDefinitions>
                            <Border Grid.Column="0" Width="3" Height="16" CornerRadius="2" Margin="-8,0,0,0"
                                    HorizontalAlignment="Left" VerticalAlignment="Center"
                                    Background="{DynamicResource AccentFillColorDefaultBrush}">
                                <Border.Style>
                                    <Style TargetType="Border">
                                        <Setter Property="Visibility" Value="Collapsed" />
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding CurrentPageKey}" Value="history">
                                                <Setter Property="Visibility" Value="Visible" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </Border.Style>
                            </Border>
                            <ui:SymbolIcon Grid.Column="0" Symbol="History20" FontSize="20" VerticalAlignment="Center" />
                            <TextBlock Grid.Column="1" Text="{x:Static app:UiText.HistoryTitle}"
                                       Margin="12,0,0,0" FontWeight="Medium" VerticalAlignment="Center"
                                       TextTrimming="CharacterEllipsis" />
                        </Grid>
                    </Border>
```

(b) 网络。旧文本（`x:Name="NavNetwork"` 起的四行）：

```xml
                    <Border x:Name="NavNetwork" CornerRadius="4" Padding="10,6" Margin="0,0,0,4" Cursor="Hand" MouseLeftButtonUp="OnNavNetworkClick">
                        <Border.Style><Style TargetType="Border"><Setter Property="Background" Value="Transparent"/><Style.Triggers><DataTrigger Binding="{Binding CurrentPageKey}" Value="network"><Setter Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}"/></DataTrigger></Style.Triggers></Style></Border.Style>
                        <TextBlock Text="{x:Static app:UiText.NetworkTitle}" TextWrapping="Wrap" FontWeight="Medium"/>
                    </Border>
```

新文本：

```xml
                    <Border x:Name="NavNetwork"
                            MouseLeftButtonUp="OnNavNetworkClick"
                            ToolTip="{x:Static app:UiText.NetworkTitle}"
                            AutomationProperties.Name="{x:Static app:UiText.NetworkTitle}">
                        <Border.Style>
                            <Style TargetType="Border" BasedOn="{StaticResource IrisNavItem}">
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding CurrentPageKey}" Value="network">
                                        <Setter Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </Border.Style>
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" />
                            </Grid.ColumnDefinitions>
                            <Border Grid.Column="0" Width="3" Height="16" CornerRadius="2" Margin="-8,0,0,0"
                                    HorizontalAlignment="Left" VerticalAlignment="Center"
                                    Background="{DynamicResource AccentFillColorDefaultBrush}">
                                <Border.Style>
                                    <Style TargetType="Border">
                                        <Setter Property="Visibility" Value="Collapsed" />
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding CurrentPageKey}" Value="network">
                                                <Setter Property="Visibility" Value="Visible" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </Border.Style>
                            </Border>
                            <ui:SymbolIcon Grid.Column="0" Symbol="Globe20" FontSize="20" VerticalAlignment="Center" />
                            <TextBlock Grid.Column="1" Text="{x:Static app:UiText.NetworkTitle}"
                                       Margin="12,0,0,0" FontWeight="Medium" VerticalAlignment="Center"
                                       TextTrimming="CharacterEllipsis" />
                        </Grid>
                    </Border>
```

(c) 截图。旧文本是 Step 4 拆出来的那**一整行**（行首 20 个空格，从 `<Border x:Name="NavScreenshot"` 到行尾的 `</Border>`）：

```xml
                    <Border x:Name="NavScreenshot" CornerRadius="4" Padding="10,6" Margin="0,0,0,4" Cursor="Hand" MouseLeftButtonUp="OnNavScreenshotClick"><Border.Style><Style TargetType="Border"><Setter Property="Background" Value="Transparent"/><Style.Triggers><DataTrigger Binding="{Binding CurrentPageKey}" Value="screenshot"><Setter Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}"/></DataTrigger></Style.Triggers></Style></Border.Style><TextBlock Text="{x:Static app:UiText.ScreenshotPageTitle}" TextWrapping="Wrap" FontWeight="Medium"/></Border>
```

新文本：

```xml
                    <Border x:Name="NavScreenshot"
                            MouseLeftButtonUp="OnNavScreenshotClick"
                            ToolTip="{x:Static app:UiText.ScreenshotPageTitle}"
                            AutomationProperties.Name="{x:Static app:UiText.ScreenshotPageTitle}">
                        <Border.Style>
                            <Style TargetType="Border" BasedOn="{StaticResource IrisNavItem}">
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding CurrentPageKey}" Value="screenshot">
                                        <Setter Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </Border.Style>
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" />
                            </Grid.ColumnDefinitions>
                            <Border Grid.Column="0" Width="3" Height="16" CornerRadius="2" Margin="-8,0,0,0"
                                    HorizontalAlignment="Left" VerticalAlignment="Center"
                                    Background="{DynamicResource AccentFillColorDefaultBrush}">
                                <Border.Style>
                                    <Style TargetType="Border">
                                        <Setter Property="Visibility" Value="Collapsed" />
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding CurrentPageKey}" Value="screenshot">
                                                <Setter Property="Visibility" Value="Visible" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </Border.Style>
                            </Border>
                            <ui:SymbolIcon Grid.Column="0" Symbol="Screenshot20" FontSize="20" VerticalAlignment="Center" />
                            <TextBlock Grid.Column="1" Text="{x:Static app:UiText.ScreenshotPageTitle}"
                                       Margin="12,0,0,0" FontWeight="Medium" VerticalAlignment="Center"
                                       TextTrimming="CharacterEllipsis" />
                        </Grid>
                    </Border>
```

(d) OCR。旧文本是 Step 4 拆剩下的那**一整行**，**连同行首的 16 个空格**一起选中（它原来就比相邻几项少缩进 4 格；新文本改成 20 个空格，与相邻几项对齐）：

```xml
                <Border x:Name="NavOcr" CornerRadius="4" Padding="10,6" Margin="0,0,0,4" Cursor="Hand" MouseLeftButtonUp="OnNavOcrClick"><Border.Style><Style TargetType="Border"><Setter Property="Background" Value="Transparent"/><Style.Triggers><DataTrigger Binding="{Binding CurrentPageKey}" Value="ocr"><Setter Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}"/></DataTrigger></Style.Triggers></Style></Border.Style><TextBlock Text="{x:Static app:UiText.OcrPageTitle}" TextWrapping="Wrap" FontWeight="Medium"/></Border>
```

新文本：

```xml
                    <Border x:Name="NavOcr"
                            MouseLeftButtonUp="OnNavOcrClick"
                            ToolTip="{x:Static app:UiText.OcrPageTitle}"
                            AutomationProperties.Name="{x:Static app:UiText.OcrPageTitle}">
                        <Border.Style>
                            <Style TargetType="Border" BasedOn="{StaticResource IrisNavItem}">
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding CurrentPageKey}" Value="ocr">
                                        <Setter Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </Border.Style>
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" />
                            </Grid.ColumnDefinitions>
                            <Border Grid.Column="0" Width="3" Height="16" CornerRadius="2" Margin="-8,0,0,0"
                                    HorizontalAlignment="Left" VerticalAlignment="Center"
                                    Background="{DynamicResource AccentFillColorDefaultBrush}">
                                <Border.Style>
                                    <Style TargetType="Border">
                                        <Setter Property="Visibility" Value="Collapsed" />
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding CurrentPageKey}" Value="ocr">
                                                <Setter Property="Visibility" Value="Visible" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </Border.Style>
                            </Border>
                            <ui:SymbolIcon Grid.Column="0" Symbol="ScanText20" FontSize="20" VerticalAlignment="Center" />
                            <TextBlock Grid.Column="1" Text="{x:Static app:UiText.OcrPageTitle}"
                                       Margin="12,0,0,0" FontWeight="Medium" VerticalAlignment="Center"
                                       TextTrimming="CharacterEllipsis" />
                        </Grid>
                    </Border>
```

改完自查（四项都换完之后）：

```
grep -c 'BasedOn="{StaticResource IrisNavItem}"' src/Iris.App/Settings/SettingsWindow.xaml
grep -c 'CornerRadius="4" Padding="10,6"' src/Iris.App/Settings/SettingsWindow.xaml
grep -c 'Margin="-8,0,0,0"' src/Iris.App/Settings/SettingsWindow.xaml
grep -n 'x:Name="Nav' src/Iris.App/Settings/SettingsWindow.xaml
grep -c 'OnNavOcrClick\|OnNavScreenshotClick\|OnNavNetworkClick\|OnNavHistoryClick' src/Iris.App/Settings/SettingsWindow.xaml
powershell -NoProfile -Command '$ErrorActionPreference = "Stop"; [xml](Get-Content -Raw -Encoding UTF8 "src/Iris.App/Settings/SettingsWindow.xaml") | Out-Null; "xml ok"'
```

期望：依次是 `4`；`4`（前四项还是旧写法，Step 7 改）；`4`；四行，自上而下 `NavOcr`、`NavScreenshot`、`NavNetwork`、`NavHistory`；`4`；`xml ok`。最后一条报错：按报错里的行号看是哪一项的标签没配对，改到输出 `xml ok` 再往下走。Step 6 到此写完。

- [ ] **Step 7: 导航项（前四项）——划词、快捷键、翻译服务、常规：同样改写，并补上四个名字**

Modify `src/Iris.App/Settings/SettingsWindow.xaml`（继续从后往前：划词 → 快捷键 → 翻译服务 → 常规）。写法与 Step 6 的四项一致（每一处的原因见 Step 6 开头那张表）；多出来的只有一点：这四项原来没有名字，这里补上 `NavSelection`、`NavHotkeys`、`NavTranslation`、`NavGeneral`（S0-2；`LocalizationRenderingTests.cs:71` 早就在按这四个名字找）。处理函数名不变。

三条中文注释 `<!-- 导航项：常规 -->`、`<!-- 导航项：翻译服务 -->`、`<!-- 导航项：快捷键 -->` 各在自己那一项的**上一行**，不在下面的旧文本里：替换时不要把注释行选进去，一个字不动、不挪位置。划词那一项上面本来就没有注释，不要给它补。

(a) 划词。旧文本（`MouseLeftButtonUp="OnNavSelectionClick"` 所在行起的 13 行）：

```xml
                    <Border CornerRadius="4" Padding="10,6" Margin="0,0,0,4" Cursor="Hand" MouseLeftButtonUp="OnNavSelectionClick">
                        <Border.Style>
                            <Style TargetType="Border">
                                <Setter Property="Background" Value="Transparent" />
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding CurrentPageKey}" Value="selection">
                                        <Setter Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </Border.Style>
                        <TextBlock TextWrapping="Wrap" Text="{x:Static app:UiText.SettingsNavSelection}" FontWeight="Medium" VerticalAlignment="Center" />
                    </Border>
```

新文本：

```xml
                    <Border x:Name="NavSelection"
                            MouseLeftButtonUp="OnNavSelectionClick"
                            ToolTip="{x:Static app:UiText.SettingsNavSelection}"
                            AutomationProperties.Name="{x:Static app:UiText.SettingsNavSelection}">
                        <Border.Style>
                            <Style TargetType="Border" BasedOn="{StaticResource IrisNavItem}">
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding CurrentPageKey}" Value="selection">
                                        <Setter Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </Border.Style>
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" />
                            </Grid.ColumnDefinitions>
                            <Border Grid.Column="0" Width="3" Height="16" CornerRadius="2" Margin="-8,0,0,0"
                                    HorizontalAlignment="Left" VerticalAlignment="Center"
                                    Background="{DynamicResource AccentFillColorDefaultBrush}">
                                <Border.Style>
                                    <Style TargetType="Border">
                                        <Setter Property="Visibility" Value="Collapsed" />
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding CurrentPageKey}" Value="selection">
                                                <Setter Property="Visibility" Value="Visible" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </Border.Style>
                            </Border>
                            <ui:SymbolIcon Grid.Column="0" Symbol="CursorClick20" FontSize="20" VerticalAlignment="Center" />
                            <TextBlock Grid.Column="1" Text="{x:Static app:UiText.SettingsNavSelection}"
                                       Margin="12,0,0,0" FontWeight="Medium" VerticalAlignment="Center"
                                       TextTrimming="CharacterEllipsis" />
                        </Grid>
                    </Border>
```

(b) 快捷键。旧文本（`MouseLeftButtonUp="OnNavHotkeysClick"` 所在行起的 15 行；它上一行的中文注释不选）：

```xml
                    <Border CornerRadius="4" Padding="10,6" Margin="0,0,0,4" Cursor="Hand" MouseLeftButtonUp="OnNavHotkeysClick">
                        <Border.Style>
                            <Style TargetType="Border">
                                <Setter Property="Background" Value="Transparent" />
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding CurrentPageKey}" Value="hotkeys">
                                        <Setter Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </Border.Style>
                        <TextBlock TextWrapping="Wrap" Text="{x:Static app:UiText.SettingsNavHotkeys}"
                                   FontWeight="Medium"
                                   VerticalAlignment="Center" />
                    </Border>
```

新文本：

```xml
                    <Border x:Name="NavHotkeys"
                            MouseLeftButtonUp="OnNavHotkeysClick"
                            ToolTip="{x:Static app:UiText.SettingsNavHotkeys}"
                            AutomationProperties.Name="{x:Static app:UiText.SettingsNavHotkeys}">
                        <Border.Style>
                            <Style TargetType="Border" BasedOn="{StaticResource IrisNavItem}">
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding CurrentPageKey}" Value="hotkeys">
                                        <Setter Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </Border.Style>
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" />
                            </Grid.ColumnDefinitions>
                            <Border Grid.Column="0" Width="3" Height="16" CornerRadius="2" Margin="-8,0,0,0"
                                    HorizontalAlignment="Left" VerticalAlignment="Center"
                                    Background="{DynamicResource AccentFillColorDefaultBrush}">
                                <Border.Style>
                                    <Style TargetType="Border">
                                        <Setter Property="Visibility" Value="Collapsed" />
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding CurrentPageKey}" Value="hotkeys">
                                                <Setter Property="Visibility" Value="Visible" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </Border.Style>
                            </Border>
                            <ui:SymbolIcon Grid.Column="0" Symbol="Keyboard20" FontSize="20" VerticalAlignment="Center" />
                            <TextBlock Grid.Column="1" Text="{x:Static app:UiText.SettingsNavHotkeys}"
                                       Margin="12,0,0,0" FontWeight="Medium" VerticalAlignment="Center"
                                       TextTrimming="CharacterEllipsis" />
                        </Grid>
                    </Border>
```

(c) 翻译服务。旧文本（`MouseLeftButtonUp="OnNavTranslationClick"` 所在行起的 15 行；它上一行的中文注释不选）：

```xml
                    <Border CornerRadius="4" Padding="10,6" Margin="0,0,0,4" Cursor="Hand" MouseLeftButtonUp="OnNavTranslationClick">
                        <Border.Style>
                            <Style TargetType="Border">
                                <Setter Property="Background" Value="Transparent" />
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding CurrentPageKey}" Value="translation">
                                        <Setter Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </Border.Style>
                        <TextBlock TextWrapping="Wrap" Text="{x:Static app:UiText.SettingsNavTranslation}"
                                   FontWeight="Medium"
                                   VerticalAlignment="Center" />
                    </Border>
```

新文本：

```xml
                    <Border x:Name="NavTranslation"
                            MouseLeftButtonUp="OnNavTranslationClick"
                            ToolTip="{x:Static app:UiText.SettingsNavTranslation}"
                            AutomationProperties.Name="{x:Static app:UiText.SettingsNavTranslation}">
                        <Border.Style>
                            <Style TargetType="Border" BasedOn="{StaticResource IrisNavItem}">
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding CurrentPageKey}" Value="translation">
                                        <Setter Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </Border.Style>
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" />
                            </Grid.ColumnDefinitions>
                            <Border Grid.Column="0" Width="3" Height="16" CornerRadius="2" Margin="-8,0,0,0"
                                    HorizontalAlignment="Left" VerticalAlignment="Center"
                                    Background="{DynamicResource AccentFillColorDefaultBrush}">
                                <Border.Style>
                                    <Style TargetType="Border">
                                        <Setter Property="Visibility" Value="Collapsed" />
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding CurrentPageKey}" Value="translation">
                                                <Setter Property="Visibility" Value="Visible" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </Border.Style>
                            </Border>
                            <ui:SymbolIcon Grid.Column="0" Symbol="Translate20" FontSize="20" VerticalAlignment="Center" />
                            <TextBlock Grid.Column="1" Text="{x:Static app:UiText.SettingsNavTranslation}"
                                       Margin="12,0,0,0" FontWeight="Medium" VerticalAlignment="Center"
                                       TextTrimming="CharacterEllipsis" />
                        </Grid>
                    </Border>
```

(d) 常规。旧文本（`MouseLeftButtonUp="OnNavGeneralClick"` 所在行起的 15 行；它上一行的中文注释不选）：

```xml
                    <Border CornerRadius="4" Padding="10,6" Margin="0,0,0,4" Cursor="Hand" MouseLeftButtonUp="OnNavGeneralClick">
                        <Border.Style>
                            <Style TargetType="Border">
                                <Setter Property="Background" Value="Transparent" />
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding CurrentPageKey}" Value="general">
                                        <Setter Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </Border.Style>
                        <TextBlock TextWrapping="Wrap" Text="{x:Static app:UiText.SettingsNavGeneral}"
                                   FontWeight="Medium"
                                   VerticalAlignment="Center" />
                    </Border>
```

新文本：

```xml
                    <Border x:Name="NavGeneral"
                            MouseLeftButtonUp="OnNavGeneralClick"
                            ToolTip="{x:Static app:UiText.SettingsNavGeneral}"
                            AutomationProperties.Name="{x:Static app:UiText.SettingsNavGeneral}">
                        <Border.Style>
                            <Style TargetType="Border" BasedOn="{StaticResource IrisNavItem}">
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding CurrentPageKey}" Value="general">
                                        <Setter Property="Background" Value="{DynamicResource SubtleFillColorSecondaryBrush}" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </Border.Style>
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" />
                            </Grid.ColumnDefinitions>
                            <Border Grid.Column="0" Width="3" Height="16" CornerRadius="2" Margin="-8,0,0,0"
                                    HorizontalAlignment="Left" VerticalAlignment="Center"
                                    Background="{DynamicResource AccentFillColorDefaultBrush}">
                                <Border.Style>
                                    <Style TargetType="Border">
                                        <Setter Property="Visibility" Value="Collapsed" />
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding CurrentPageKey}" Value="general">
                                                <Setter Property="Visibility" Value="Visible" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </Border.Style>
                            </Border>
                            <ui:SymbolIcon Grid.Column="0" Symbol="Settings20" FontSize="20" VerticalAlignment="Center" />
                            <TextBlock Grid.Column="1" Text="{x:Static app:UiText.SettingsNavGeneral}"
                                       Margin="12,0,0,0" FontWeight="Medium" VerticalAlignment="Center"
                                       TextTrimming="CharacterEllipsis" />
                        </Grid>
                    </Border>
```

改完自查（八项都换完之后）：

```
grep -c 'CornerRadius="4" Padding="10,6"' src/Iris.App/Settings/SettingsWindow.xaml
grep -c 'BasedOn="{StaticResource IrisNavItem}"' src/Iris.App/Settings/SettingsWindow.xaml
grep -n 'x:Name="Nav' src/Iris.App/Settings/SettingsWindow.xaml
grep -o 'Symbol="[A-Za-z0-9]*"' src/Iris.App/Settings/SettingsWindow.xaml
grep -o 'CurrentPageKey}" Value="[a-z]*"' src/Iris.App/Settings/SettingsWindow.xaml | sort | uniq -c
grep -c '<!-- 导航项：' src/Iris.App/Settings/SettingsWindow.xaml
git diff -- src/Iris.App/Settings/SettingsWindow.xaml | grep -c '^[-+].*<!--'
powershell -NoProfile -Command '$ErrorActionPreference = "Stop"; [xml](Get-Content -Raw -Encoding UTF8 "src/Iris.App/Settings/SettingsWindow.xaml") | Out-Null; "xml ok"'
```

期望，逐条：

1. `0`——旧写法一处不剩。
2. `8`。
3. 八行，自上而下 `NavGeneral`、`NavTranslation`、`NavHotkeys`、`NavSelection`、`NavOcr`、`NavScreenshot`、`NavNetwork`、`NavHistory`。
4. 八行，自上而下 `Settings20`、`Translate20`、`Keyboard20`、`CursorClick20`、`ScanText20`、`Screenshot20`、`Globe20`、`History20`（与 S0-2 的图标表逐行相同）。
5. 八个页面键各 `3` 次（改前各 2 次：页面自己的显隐触发器 + 导航底色触发器；现在多了短线的触发器）。哪个键不是 3，就是那一项的页面键抄错了：短线会跟着别的页面亮。
6. `3`——三条中文注释都还在。
7. `0`——差异里没有任何一行注释被增、删、改。
8. `xml ok`。

Step 7 到此写完。

- [ ] **Step 8: 导航外框——标题起名 `NavTitleText`，去掉不透明底和右侧竖线**

Modify `src/Iris.App/Settings/SettingsWindow.xaml`，两处，仍然从后往前。

(a) 导航顶部的标题：只加一个名字，别的属性（`TextWrapping`、`FontSize="18"`、`FontWeight="SemiBold"`、`Margin="8,0,0,12"`）都不动。窄态下由 Step 10 的代码把它设成 `Collapsed`。

旧文本（全文件唯一的一行）：

```xml
                    <TextBlock TextWrapping="Wrap" Text="{x:Static app:UiText.SettingsTitle}"
```

新文本：

```xml
                    <TextBlock x:Name="NavTitleText" TextWrapping="Wrap" Text="{x:Static app:UiText.SettingsTitle}"
```

(b) 导航外框 `Border`：删掉 `Background`（S0-6「导航 `Border` 的 `Background` 删掉」），同时删掉 `BorderThickness="0,0,1,0"` 与它的 `BorderBrush`（S0-6「导航与内容区之间不加竖线」；这条竖线是现状里本来就有的，计划 2.2 没记，见文末 ⚠ 第 1 条）。`Grid.Column` 与 `Padding` 不动。

旧文本（`dc08c48` 的 :40–44）：

```xml
            <Border Grid.Column="0"
                    BorderThickness="0,0,1,0"
                    BorderBrush="{DynamicResource CardStrokeColorDefaultBrush}"
                    Background="{DynamicResource SolidBackgroundFillColorSecondaryBrush}"
                    Padding="6,12,6,8">
```

新文本：

```xml
            <Border Grid.Column="0"
                    Padding="6,12,6,8">
```

- 它上面那条中文注释（`<!-- 左侧导航 -->`）**原样留着**。
- 它里面的 `ScrollViewer`（`VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled"`）与 `StackPanel` 两行不动：导航项多到放不下时仍由这个 `ScrollViewer` 滚动。
- 不给这个 `Border` 起名字（Step 2 的测试从 `NavOcr` 沿逻辑树往上找它）。

改完自查：

```
grep -c 'SolidBackgroundFillColorSecondaryBrush' src/Iris.App/Settings/SettingsWindow.xaml
grep -c 'CardStrokeColorDefaultBrush' src/Iris.App/Settings/SettingsWindow.xaml
grep -c 'BorderThickness="0,0,1,0"' src/Iris.App/Settings/SettingsWindow.xaml
grep -n 'x:Name="NavTitleText"' src/Iris.App/Settings/SettingsWindow.xaml
grep -c 'Padding="6,12,6,8"' src/Iris.App/Settings/SettingsWindow.xaml
powershell -NoProfile -Command '$ErrorActionPreference = "Stop"; [xml](Get-Content -Raw -Encoding UTF8 "src/Iris.App/Settings/SettingsWindow.xaml") | Out-Null; "xml ok"'
```

期望：`0`；`0`（改前两处：导航、底栏，Step 5 与本步各去掉一处）；`0`；1 行；`1`；`xml ok`。Step 8 到此写完。

- [ ] **Step 9: 列定义起名 `NavColumnDef`，根 `Grid` 加着色画刷**

Modify `src/Iris.App/Settings/SettingsWindow.xaml`，最后两处（仍然从后往前）。

(a) 导航所在的列定义：起名，初始宽度从 140 改成 176。

旧文本（全文件唯一）：

```xml
                <ColumnDefinition Width="140" />
```

新文本：

```xml
                <ColumnDefinition x:Name="NavColumnDef" Width="176" />
```

- `176` 在这里只是 XAML 里的初始值：构造函数末尾那句 `ApplyAdaptiveLayout(Width)` 会立刻用主题层的 `IrisNavWidth` / `IrisNavCompactWidth` 把它改写（Step 10）。
- 这里**不能**写成 `Width="{StaticResource IrisNavWidth}"`：`ColumnDefinition.Width` 的类型是 `GridLength`，而 `IrisNavWidth` 是 `sys:Double`，`StaticResource` 不做类型转换，加载窗口时会抛 `XamlParseException`。所以 176 这个数在 XAML 里出现一次，与主题层的数重复（文末 ⚠ 第 4 条）。
- 它下面那一行 `<ColumnDefinition Width="*" />`（内容区）不动；上面的中文注释（`<!-- 主体区域：左侧导航与右侧内容 -->`）不动。

(b) 根 `Grid`：加着色画刷（S0-6）。`    <Grid>` 这个写法在文件里不止一处，所以连同它上一行一起定位。

旧文本（`dc08c48` 的 :19–20）：

```xml
    </Window.Resources>
    <Grid>
```

新文本：

```xml
    </Window.Resources>
    <Grid Background="{DynamicResource IrisAcrylicTintBrush}">
```

- 只加这一个属性。**不**新增全屏 `Border`，**不**动窗口自身的 `Background`（那归 `WindowMaterialService` 管），**不**动根元素上的 `Width="760"`、`Height="520"`、`MinWidth="600"`、`MinHeight="440"`（S0-1）。
- 标题栏 `ui:TitleBar` 在根 `Grid` 的第 0 行里，所以标题栏区域一并被着色；标题栏本身一个字不改，不加应用图标。
- 必须是 `DynamicResource`：切主题、以及不支持亚克力时换成纯色回退，都靠资源字典换值（S0-7），根 `Grid` 的写法不用变。

改完自查（到这里 XAML 全部改完）：

```
grep -n 'IrisAcrylicTintBrush' src/Iris.App/Settings/SettingsWindow.xaml
grep -n 'x:Name="NavColumnDef"' src/Iris.App/Settings/SettingsWindow.xaml
grep -c '<ColumnDefinition Width="140" />' src/Iris.App/Settings/SettingsWindow.xaml
sed -n '11,14p' src/Iris.App/Settings/SettingsWindow.xaml
grep -c '<!--' src/Iris.App/Settings/SettingsWindow.xaml
git diff -- src/Iris.App/Settings/SettingsWindow.xaml | grep -c '^[-+].*<!--'
wc -l < src/Iris.App/Settings/SettingsWindow.xaml
powershell -NoProfile -Command '$ErrorActionPreference = "Stop"; [xml](Get-Content -Raw -Encoding UTF8 "src/Iris.App/Settings/SettingsWindow.xaml") | Out-Null; "xml ok"'
```

期望，逐条：

1. 1 行，在 `</Window.Resources>` 的下一行。
2. 1 行。
3. `0`。
4. 四行，依次是 `Width="760"`、`Height="520"`、`MinWidth="600"`、`MinHeight="440"`，与改前一字不差。
5. `43`——整份文件带注释的行数与改前相同。
6. `0`——没有任何注释行被增、删、改。
7. `1376`（改前 1143；Step 4 加 1、Step 5 减 1、Step 6 加 142、Step 7 加 94、Step 8 减 3；每个导航项的新文本都是 38 行）。不是这个数：对照各步的旧文本、新文本找出多了或少了哪几行——最常见的是把导航项之间的空行或注释行一起替换掉了——改到相符再往下走。
8. `xml ok`。

Step 9 到此写完。

- [ ] **Step 10: 代码隐藏——在 `ApplyAdaptiveLayout` 最前面加导航宽窄切换（只加不删）**

Modify `src/Iris.App/Settings/SettingsWindow.xaml.cs`。只在方法体的第一行之前**插入**一段；方法签名、原有的守卫、`_isCompactMode` 的判断、两段分栏赋值（`dc08c48` 的 cs:57–98）一个字符都不动——「翻译服务」页在任务 12 之前还靠它们（S0-9）。

旧文本（`dc08c48` 的 cs:55–60）：

```csharp
    internal void ApplyAdaptiveLayout(double width)
    {
        if (TranslationLayoutGrid == null || ProfilesPane == null || DetailsPane == null)
        {
            return;
        }
```

新文本：

```csharp
    internal void ApplyAdaptiveLayout(double width)
    {
        // Navigation column: icon and label at or above the compact threshold, icon only below it.
        // This block runs on every call. The two-pane logic below keeps its own guard and its own
        // "mode unchanged" early return until the drill-in page replaces it.
        if (NavColumnDef != null && NavTitleText != null)
        {
            bool navCompact = width < CompactLayoutThreshold;
            NavColumnDef.Width = new GridLength((double)FindResource(navCompact ? "IrisNavCompactWidth" : "IrisNavWidth"));
            NavTitleText.Visibility = navCompact ? Visibility.Collapsed : Visibility.Visible;
        }

        if (TranslationLayoutGrid == null || ProfilesPane == null || DetailsPane == null)
        {
            return;
        }
```

每一处为什么这样写：

- **放在最前面、不放进原有守卫之后**：原有逻辑在「模式没变」时会提前 `return`（cs:63–66）。导航切换要是写在它后面，就只在跨过 680 的那一刻执行一次；写在前面则每次调用都把导航摆对，与 `_isCompactMode` 无关。Step 2 的 `ApplyAdaptiveLayout_SwitchesTheNavigationAtTheCompactThreshold` 守的就是这一点。
- **自带 `!= null` 守卫**：`InitializeComponent()` 解析到根元素的 `Width="760"` 时会触发 `OnPropertyChanged`（cs:37–44），那时带名字的元素还没接到字段上，`NavColumnDef`、`NavTitleText` 都是 `null`。原有逻辑用同样的办法防这种情况（cs:57）。构造函数最后那句 `ApplyAdaptiveLayout(Width)`（cs:34）执行时两个字段都已就绪，导航在那一刻第一次被摆对。
- **变量名必须是 `navCompact`，不能叫 `compact`**：同一个方法体后面有 `bool compact = …`（cs:62），内层代码块里再声明同名变量会报 `CS0136`。
- **宽度用 `FindResource` 取，不写死 176 / 48**（S0-2）。`IrisNavWidth`、`IrisNavCompactWidth` 是 `sys:Double`（Task 2），所以拆箱成 `double` 再包成 `GridLength`。这是 `src/Iris.App` 的 C# 里第一处 `FindResource`；键不存在会抛 `ResourceReferenceKeyNotFoundException`——真实应用由 `App.xaml` 合并 `Iris.Styles.xaml`，测试由 `WpfTestRunner` 合并，两边都找得到。
- **阈值沿用既有常量 `CompactLayoutThreshold`**（cs:18，680.0），用 `<`：679.99 是窄态，680 是宽态。不新增常量、不新增字段。
- **`Visibility` 不需要加 `using`**：文件已有 `using System.Windows;`。撰写时反射过 WPF-UI 4.3.0，`Wpf.Ui.Controls` 里没有同名类型；万一编译报 `CS0104`（二义性），把这一行的三处 `Visibility` 都写成 `System.Windows.Visibility`，别的不动。
- **新增的注释只有英文**；新增的两个字符串字面量是资源键（英文标识符），不是界面文字，不进 `Strings.resx`。
- 把相同的值重复写进 `NavColumnDef.Width` 不会引起重新布局（依赖属性在新旧值相等时不发变更通知），所以拖动窗口边框时每次调用都执行这一段没有代价。

改完自查：

```
git diff --stat -- src/Iris.App/Settings/SettingsWindow.xaml.cs
git diff -- src/Iris.App/Settings/SettingsWindow.xaml.cs | grep -c '^-[^-]'
grep -n 'navCompact\|NavColumnDef\|NavTitleText' src/Iris.App/Settings/SettingsWindow.xaml.cs
grep -c 'bool compact = width < CompactLayoutThreshold;' src/Iris.App/Settings/SettingsWindow.xaml.cs
```

期望：`1 file changed, 10 insertions(+)`（三行注释、`if` 一行、两个花括号各一行、三条语句、一个空行），**没有** deletions；第二条 `0`（一行都没删）；第三条 4 行（守卫 1 行、`navCompact` 的声明 1 行、两个赋值各 1 行）；第四条 `1`（原有逻辑还在）。Step 10 到此写完。

- [ ] **Step 11: 跑新测试，确认通过**

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsWindowNavigationLayoutTests"
```

期望：编译通过，**15 条全部通过**（Step 3 里红的 14 条全部转绿，`Footer_…` 仍绿）。

与期望不符时，按现象对号入座；表里没有的现象，把失败消息原样记下来，停下来报告。**不改测试去迁就实现**。

| 现象 | 原因 | 处理 |
|---|---|---|
| 编译失败 `CS0103`：`NavColumnDef` 或 `NavTitleText` 不存在 | Step 8(a) / Step 9(a) 的 `x:Name` 没加上或拼错 | 用那两步自查里的 `grep` 找到后改对 |
| 编译失败 `CS0136` | Step 10 的局部变量写成了 `compact` | 改回 `navCompact` |
| 编译失败 `CS0104`（`Visibility` 二义性） | 见 Step 10 | 该行三处都写成 `System.Windows.Visibility` |
| 编译失败，错误号以 `MC` 开头，消息带 `SettingsWindow.xaml` 的行号 | 那一行的标签或属性名写错（标记编译器比 Step 6–9 的 `xml ok` 查得细） | 按行号对照 Step 6–9 的新文本逐字改 |
| 15 条全都抛 `XamlParseException`，内层消息是找不到名为 `IrisNavItem` 的资源 | 前置任务 2 的样式不在，或测试宿主没合并 `Iris.Styles.xaml` | 回到 Step 1(a) 核实；属前置任务的问题，停下来报告。**不要**把 `BasedOn` 里的 `StaticResource` 换成 `DynamicResource`（`BasedOn` 不接受） |
| 抛 `XamlParseException`，内层消息说某个值不是合法的 `Symbol` | 图标名抄错 | 对照 Step 7 自查第 4 条的八个名字 |
| 抛 `ResourceReferenceKeyNotFoundException`，键是 `IrisNavWidth` 或 `IrisNavCompactWidth` | Task 2 的数值键不在 | 回到 Step 1(a)；停下来报告 |
| `ApplyAdaptiveLayout` 那一行抛 `InvalidCastException` | 那两个键的类型不是 `sys:Double` | 停下来报告，不在代码隐藏里改转换方式 |
| `NavItems_AreNamedBorders…` 红在 `nav.Padding`，实际 `10,8,10,8` | `IrisNavItem` 的 `Padding` 没按 Task 2 改成 8 | 前置任务的问题，停下来报告 |
| 同一条红在 `nav.Padding`，实际 `10,6,10,6` | 那一项外层 `Border` 上的本地属性没删干净 | 按 Step 6 / Step 7 的新文本重改那一项 |
| 同一条红在 `icon.FontSize` | 写在元素上的 `FontSize="20"` 没生效（与「WPF-UI 的三个事实」第 2 条不符） | 记下实际值，停下来报告 |
| 同一条在取 `AccentFillColorDefaultBrush` 时抛 `ResourceReferenceKeyNotFoundException` | 测试宿主里解析不到这个键（仓库里既有的两处用法是 `DynamicResource`，找不到不会报错，所以以前暴露不出来） | 停下来报告 |
| `NavItems_OnlyTheCurrentPage…` 红，消息里是「某项 while the page is 另一页」 | 那一项的两个 `DataTrigger` 里有一个页面键抄错 | 按 Step 7 自查第 5 条找出次数不是 3 的键，改对 |
| `ShownWindow_…(600, 440, 48)` 红，实际 176 | Step 10 新加的那一段没放在方法体最前面，被原有守卫或提前返回挡住了 | 对照 Step 10 的新文本挪到最前面 |
| `ApplyAdaptiveLayout_…` 只有 680 或 679.99 那一条红 | 比较写成了 `<=`，或三目运算的两个键写反 | 对照 Step 10 |
| `CompactNavigation_…` 红在 `nav.ActualWidth`，实际明显小于 36 | 窄态下导航出了竖向滚动条，而且 4.3.0 的滚动条**占布局宽度**（与「WPF-UI 的三个事实」第 1 条不符） | 记下实际宽度，停下来报告；怎么处置（改导航的内边距、隐藏滚动条等）要协调方裁决，见文末 ⚠ 第 8 条 |
| `CompactNavigation_…` 红在 `ColumnDefinitions[1].ActualWidth` | 图标的实际宽度不是 20 | 记下实际值，停下来报告 |
| `WideNavigation_…` 红在「八项一样高」 | 某一项的文字折行了 | 那一项的 `TextBlock` 上还留着 `TextWrapping="Wrap"`：按 Step 6 / Step 7 的新文本删掉 |
| `Skeleton_…` 红在 `navHost.BorderThickness`，实际 `0,0,1,0` | Step 8(b) 没把竖线删掉 | 按 Step 8(b) 改 |
| `Skeleton_…` 红在深色那一段，`root.Background` 还是浅色的值 | 根 `Grid` 的背景写成了 `StaticResource` | 改成 `DynamicResource`（Step 9(b)） |
| `Footer_…` 红 | 本任务没改底栏里的任何内容，不该红 | `git diff -- src/Iris.App/Settings/SettingsWindow.xaml` 看底栏那一段是否多删了行；多删的补回来 |

Step 11 到此写完。

- [ ] **Step 12: 金丝雀先算后跑；跑本任务过滤组，与基线 A 逐项比对**

(a) 先算金丝雀 `SelectionSettingsRenderingTests`（不动它，只算）。它先画一层不透明底（浅色 `WhiteSmoke` = 245，深色 32），再把窗口画上去，取像素 (150, 50) 的 R 分量：深色要 `< 120`，浅色要 `> 130`。改造后这个点上有什么：

| 尺寸 | 导航宽 | (150, 50) 落在哪里 | 那里画了什么 |
|---|---|---|---|
| 760×520 | 176 | 导航外框里：横向在导航列内；纵向在标题栏（高 48）下面 2 像素，处在导航外框 12 像素的上内边距里（导航的第一个元素从 y = 60 起） | 导航外框已无背景、无边线 → 只有根 `Grid` 的着色层 |
| 600×440 | 48 | 内容区：划词页 `SelectionScrollViewer` 的上内边距里（它的 `Padding="24,16"`，内容从 x = 72、y = 64 起；滚到底时内边距里仍然是空的） | 只有根 `Grid` 的着色层 |

所以四种组合（两种主题 × 两种尺寸，各取滚动到顶、到底两张）取到的都是「着色层叠在预先画的底上」：

| 主题 | 着色层（S0-6） | 底 | R 的预期 | 断言 |
|---|---|---|---|---|
| 浅色 | `#B3F3F3F3`：不透明度 179 / 255 ≈ 0.702，R = 243 | 245 | 243 × 0.702 + 245 × 0.298 ≈ **244** | `> 130`，过 |
| 深色 | `#AD242424`：不透明度 173 / 255 ≈ 0.678，R = 36 | 32 | 36 × 0.678 + 32 × 0.322 ≈ **35** | `< 120`，过 |

预判：**金丝雀保持绿，不需要挪取点**。（着色层换成纯色回退时 R 是 243 / 36，结论相同。）

(b) 先算 `SettingsWindowTranslationUiTests` 用例 7 的宽度预算（旧分栏逻辑还在，它按**窗口**宽度 680 分档，不看内容区宽度）：

| 窗口 | 导航 | 内容区宽 | 「翻译服务」页的排法 | 详情区里输入框的宽度（断言要 `>= 150`） |
|---|---|---|---|---|
| 760×520 | 176（改前 140） | 584（改前 620） | 左右分栏：584 − 24（页边距 12 × 2）− 220（列表列）= 340 给详情 | 340 − 8（详情滚动区内边距）− 24（卡片内边距）− 75（标签列）= **233**（改前 269） |
| 600×440 | 48（改前 140） | 552（改前 460） | 上下堆叠：552 − 24 = 528 给详情 | 528 − 8 − 24 − 75 = **421**（改前 329） |

预判：用例 7 的四条（两种尺寸 × 两种主题）**保持绿**；760 宽那两条的余量比改前少 36 像素，600 宽那两条比改前宽裕。卡片边框之类没逐个核实的小量加起来只有几个像素，不影响结论。没有测试覆盖的 680–700 宽：内容区 504–524，同样算下来输入框约 153–173，贴着 150 的下限——这就是计划写明的「到任务 12 重写前的已知中间态」。

(c) 跑本任务的过滤组（计划 Task 4 的「验证命令」）：

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsWindowNavigationLayoutTests|FullyQualifiedName~AcrylicWindowTests|FullyQualifiedName~SettingsWindowTranslationUiTests|FullyQualifiedName~SettingsWindowPresenterTests|FullyQualifiedName~SelectionSettingsRenderingTests|FullyQualifiedName~NetworkSettingsFoundationTests|FullyQualifiedName~MouseRenderingTests|FullyQualifiedName~IrisThemeDictionaryTests"
```

期望：全绿；通过条数 = 基线 A 的通过条数 + 15，失败 0，跳过条数与基线 A 相同。条数对不上：先看是不是新测试类没被发现（文件没建在 `tests/Iris.App.Tests/Settings/` 下，或类不是 `public`）。

(d) 金丝雀红了怎么办。先注意：它的像素断言写在存图**之前**，所以红的那一张图不会被写出来；`%TEMP%\iris-t023b-render` 里同名的图是以前某次运行留下的，不能拿来判断。看失败消息里的实际值：

| 哪一条、实际值 | 含义 | 处理 |
|---|---|---|
| 深色，约 180 | 着色层还是浅色的值（243 × 0.702 + 32 × 0.298 ≈ 180）：Iris 主题字典没跟着切到深色 | 这是 Task 3 夹具对齐的问题，不是取点的问题。**不挪取点**；回 Step 1(a) 核对 Task 3 对这个文件的改动是否都在，不在就停下来报告 |
| 浅色，约 103 | 着色层是深色的值（36 × 0.678 + 245 × 0.322 ≈ 103）：同上，方向相反 | 同上 |
| 其它值 | 取点落在了画出来的东西上，与 (a) 的推算不符 | 按下面改取点；把失败消息里的实际值、改后的取点一起记入偏差清单的「改过的既有测试」 |

只有第三种情况才改 `tests/Iris.App.Tests/Settings/SelectionSettingsRenderingTests.cs`（计划「要改的既有测试」允许的唯一一处）。旧文本（全文件唯一）：

```csharp
                    int sample = ((int)size.Width * 50 + 150) * 4;
```

新文本（`scroll` 是同一个方法体里已有的变量，就是 `SelectionScrollViewer`；文件里已经在用 `Point`）：

```csharp
                    // The navigation column no longer has an opaque fill, so sample inside the page's own
                    // padding instead of at a fixed window coordinate: 8 px into SelectionScrollViewer.
                    var samplePoint = scroll.TransformToAncestor(f.Window).Transform(new Point(8, 8));
                    int sample = ((int)size.Width * (int)samplePoint.Y + (int)samplePoint.X) * 4;
```

- 新取点在划词页滚动区的内边距里（`Padding="24,16"`，向里 8 像素），无论导航宽窄都在内容区；两条阈值（`< 120`、`> 130`）不改。
- S0-6 写的是「挪到内容区的行卡片上」，但划词页到 Task 7 才有行卡片，现在挪不上去；所以先落在内容区的空白处。计划 Task 7 的「要改的既有测试」已经写了「取点若在任务 4 已挪到内容区，这里要确认新取点仍落在预期的元素上」。
- 改了就重跑 `--filter "FullyQualifiedName~SelectionSettingsRenderingTests"`，绿了再往下；仍红：停下来报告，附两次的实际值。

(e) `SettingsWindowTranslationUiTests` 用例 7 红了怎么办（块的划分照计划 2.9(c)，行号见「受影响的既有测试」表）：

| 红在哪一块 | 处理 |
|---|---|
| 块 1（:329–351，行列定义与 680 分栏） | 先重跑 Step 10 的自查，确认原有分栏逻辑一行没删。删过：补回来。没删过仍红：按计划记为「到任务 12 重写前的已知中间态」，把用例名与失败消息记入偏差清单，继续 |
| 块 2（:364–371，`AddProfileButton` 的宽度）、块 3（:374–382，两个输入框 `>= 150`） | 与 (b) 的预算不符，但属计划写明的已知中间态：把用例名、失败消息里的实际宽度记入偏差清单，继续 |
| 块 4（:385–397，两个按钮右缘）、块 5（:400–414，底栏取消 / 保存）、别的任何位置 | 必须在本任务内解决，不能记成中间态。块 5 红：`git diff -- src/Iris.App/Settings/SettingsWindow.xaml` 看底栏那一段，除了 Step 5 的两行之外不应有别的改动，多改的还原。块 4 或别处红且在这份文件里找不到对应的误改：停下来报告，附失败消息 |

(f) 其余几组（`AcrylicWindowTests`、`SettingsWindowPresenterTests`、`NetworkSettingsFoundationTests`、`MouseRenderingTests`、`IrisThemeDictionaryTests`）本任务预判都不受影响。哪条在基线 A 里是绿的、现在红了，就是本任务引入的：

| 现象 | 处理 |
|---|---|
| 抛 `XamlParseException` 或 `ResourceReferenceKeyNotFoundException` | 与 Step 11 表里的同名现象是同一个原因，照那张表处理 |
| `NetworkSettingsFoundationTests`：只读提示的 `TextBlock` 不是恰好 1 个 | 底栏或导航里多出了一份只读提示：对照 `git diff` 删掉多出来的，本任务不该新增任何界面文字 |
| `MouseRenderingTests`：按文字找底栏按钮时报「序列包含多个元素」或找不到 | 导航里混进了 `Button`，或底栏按钮被动过：对照 Step 5–7 的新文本还原 |
| 其它 | 不改这些测试；把用例名与失败消息记下来，停下来报告 |

Step 12 到此写完。

- [ ] **Step 13: 跑「设置全集」，与基线 B 比对**

跑之前先设环境变量 `IRIS_T038_EVIDENCE`，让 `LocalizationRenderingTests` 把双语证据图写到固定的目录（不设的话它每次写进一个带随机串的新目录，Step 14 不好找）。目录在仓库外，不提交。

Git Bash：

```
export IRIS_T038_EVIDENCE="$TEMP/iris-t041-task4/localization"
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~Iris.App.Tests.Settings|FullyQualifiedName~Iris.App.Tests.Themes|FullyQualifiedName~Iris.App.Tests.Localization|FullyQualifiedName~Iris.App.Tests.Updates|FullyQualifiedName~MouseRenderingTests|FullyQualifiedName~HistoryRenderingTests|FullyQualifiedName~ThemeServicePreviewTests"
```

用 PowerShell 的话，第一行换成 `$env:IRIS_T038_EVIDENCE = "$env:TEMP\iris-t041-task4\localization"`，第二行相同。

期望：全绿，构建零警告；通过条数 = 基线 B 的通过条数 + 15，失败 0，跳过条数与基线 B 相同。

这一组里与本任务有关、要专门看一眼的三处，连同预判：

| 测试 | 它依赖什么 | 预判与理由 |
|---|---|---|
| `SettingsHotkeysIntegrationTests` 的 `W04_…` | 录制框（`MinWidth="360"`）不被挤出窗口；录制框边框内侧的亮度浅色 `> 130`、深色 `< 120` | 绿。快捷键页的排法不随窗口宽度分档；内容区改前是 620（760 宽）与 460（600 宽），改后是 584 与 552，两个都落在改前已经通过的 460–620 之间。亮度取点在录制框里，不在导航里；着色层只让它下面的底色变化几级 |
| `LocalizationRenderingTests` 的逐屏截图（每页最多 24 屏） | 页面越窄、视口越矮，屏数越多 | 绿。改后两种尺寸的内容区（584、552）都比改前最窄的 460 宽，视口高度没变，屏数不会超过改前最多的那一种 |
| `OcrSettingsRenderingTests`（按名字取 `NavScreenshot` 并强转成 `Border`） | 名字与类型 | 绿。Step 6 没改名字，也没换类型 |

变红时：

| 现象 | 处理 |
|---|---|
| `W04_…` 红在深色的亮度断言，实际值在 180 上下 | 与 Step 12(d) 第一行同一个原因（Iris 主题字典没对齐），属 Task 3；回 Step 1(a) 核对，不在本任务里改 `W04_…` |
| `W04_…` 红在「按钮右缘」或文字宽度那几条 | 与上表的预判不符：记下是哪个尺寸、哪条断言、实际值，停下来报告 |
| `LocalizationRenderingTests` 红在 `index.ShouldBeLessThan(24)` | 与上表的预判不符：记下是哪种语言、哪种尺寸、哪一页，停下来报告；不放宽 24 这个上限 |
| 基线 B 里是绿的别的用例变红 | 先按 Step 11、Step 12(f) 的表对号入座；对不上的，不改测试，记下用例名与失败消息，停下来报告 |

顺带一提、不需要处理的变化：`LocalizationRenderingTests` 写出的 `manifest.json` 里，设置窗口各张图的 `VisibleText` 会多出八个私用区字符（八个图标各自的字形），因为图标内部也是一个 `TextBlock`；这份清单没有断言。

Step 13 到此写完。

- [ ] **Step 14: 人工看证据图（没有测试能替你对样张）**

检查 Step 12 与 Step 13 产出的离线渲染图（`$TEMP/iris-t041-task4/localization` 下的双语多尺寸截图，以及 `SelectionSettingsRenderingTests`、`OcrSettingsRenderingTests`、`SettingsHotkeysIntegrationTests` 各自证据目录下的 PNG）：

| 看什么 | 对照标准 |
|---|---|
| 窗口底色与通透分区 | 根 `Grid` 铺上 `IrisAcrylicTintBrush` 后，标题栏、导航、内容区、底栏连成同一张底，导航与底栏不再有原有的 `SolidBackgroundFillColorSecondaryBrush` 灰色硬块 |
| 导航列宽与窄态折叠 | `760` 宽（`>= 680`）下导航列 `176` 宽，每项显示左侧 `20px` `SymbolIcon` + `12px` 间距 + 单行文字；`600` 宽（`< 680`）下导航列收成 `48` 宽，只剩居中的 `20px` 图标，不出现横向截断半截文字 |
| 选中态与分隔线 | 当前页导航项带 `SubtleFillColorSecondaryBrush` 圆角底与左侧 `3×16` 主题色短竖线；导航与右侧内容区之间**没有**竖向分隔线（原 `:41–42` 右边框已删）；底栏上边有一条 `1px` `IrisAcrylicCardStrokeBrush` 细横线 |
| 导航高度与滚动条 | `448` 最小窗口高度下观察左侧 `ScrollViewer` 是否出现竖向滚动条（字体实测八项总高约 `318–321 px`，小于可用高度约 `348 px`，预期不出滚动条；若实测出现，确认未挤窄图标并在完成报告如实记录） |
| 双语与缩放 | 中文下八个导航标题完整显示；英文下较长标题（如 `Translation providers`、`Translation history`）单行 `CharacterEllipsis` 省略且不换行撑高导航项；`100%` 与 `150%` DPI 下底栏状态文字与「取消 / 保存」按钮不重叠、不越出窗口右缘 |

- [ ] **Step 15: 暂存（不提交）与本任务自检清单**

确认改动范围与自检项全部通过后，按路径逐个暂存（严禁 `git commit`）：

```bash
git status --short
git add src/Iris.App/Settings/SettingsWindow.xaml src/Iris.App/Settings/SettingsWindow.xaml.cs tests/Iris.App.Tests/Settings/SettingsWindowNavigationLayoutTests.cs tests/Iris.App.Tests/Settings/SelectionSettingsRenderingTests.cs
```

逐条核对本任务自检清单（任一条不成立则回到对应步骤修正）：

| # | 核对项 | 怎么确认 |
|---|---|---|
| 1 | 窗口尺寸与最小尺寸未改（`760×520`，最小 `600×448`），标题栏未加应用图标 | `SettingsWindowNavigationLayoutTests` 与 `SettingsWindowTranslationUiTests` 全绿；检查 `SettingsWindow.xaml:11–30` |
| 2 | 根 `Grid` 挂 `Background="{DynamicResource IrisAcrylicTintBrush}"`；导航与底栏原有的不透明 `Background` 已移除；导航右侧竖线已移除；底栏上边线画刷换为 `IrisAcrylicCardStrokeBrush` | `Skeleton_...` 断言与 `git diff -- src/Iris.App/Settings/SettingsWindow.xaml` |
| 3 | 八个导航项全部有 `x:Name`（`NavGeneral`、`NavTranslation`、`NavHotkeys`、`NavSelection`、`NavOcr`、`NavScreenshot`、`NavNetwork`、`NavHistory`）、`Style="{StaticResource IrisNavItem}"`、`ui:SymbolIcon`（20 号）、`ToolTip` 与 `AutomationProperties.Name` | `SettingsWindowNavigationLayoutTests` 全绿 |
| 4 | `>= 680` 导航 `176` 宽文字可见，`< 680` 导航 `48` 宽文字 `Collapsed`，八页内容尚未改动 | `Nav_CollapsesToIconsBelow680AndExpandsAt680` 全绿；`git diff` 确认 `:128–1047` 八页内部零改动 |
| 5 | `src/` 不新增中文字面量与中文注释，既有中文注释原样保留、一行未改；本任务未改任何 `.resx` / `UiText` | `git diff -- src/Iris.App/Settings/` 检查注释行；`git status --short -- src/Iris.App/Resources build/localization` 无输出 |
| 6 | 只暂存、没提交 | `git log -1 --oneline` 仍为 `dc08c48` |

**验证命令：** `FullyQualifiedName~SettingsWindowNavigationLayoutTests|FullyQualifiedName~AcrylicWindowTests|FullyQualifiedName~SettingsWindowTranslationUiTests|FullyQualifiedName~SettingsWindowPresenterTests|FullyQualifiedName~SelectionSettingsRenderingTests|FullyQualifiedName~NetworkSettingsFoundationTests|FullyQualifiedName~MouseRenderingTests|FullyQualifiedName~IrisThemeDictionaryTests` —— 预期全绿（上面写明的用例 7 中间态除外）；再跑「设置全集」。

**要改的既有测试：** `tests/Iris.App.Tests/Settings/SelectionSettingsRenderingTests.cs:56`——原金丝雀按绝对窗口坐标 `(150, 50)` 取样背景色，导航列从 `140` 扩为 `176` 后该点落入导航列；改为取 `SelectionScrollViewer` 内容区局部坐标 `(8, 8)` 转换到窗口的像素位置（见 Step 12(c)，记入完成报告「改过的既有测试」）。

### Task 4B: 历史窗口与翻译窗口的背板着色层

（【用户】U5，2026-11-17。编号用 4B 是为了不重排既有任务编号；顺序在 Task 4 之后、Task 5 之前。）

**目的：** 落地 S0-6 的「另两个窗口」：给历史窗口与翻译窗口各叠上同一层背板着色（`IrisAcrylicTintBrush`），让三个亚克力窗口的底色在本阶段结束时一致。

**Files:**
- Modify: `src/Iris.App/History/HistoryWindow.xaml`（:18 根 `Grid` 加一行 `Background`，改 1 行）
- Modify: `src/Iris.App/Translation/TranslateWindow.xaml`（在 :29 的 `DockPanel` 外包一层只带 `Background` 的 `Grid`：:29 上方、:356 下方各插 1 行，既有行不动）
- Test: `tests/Iris.App.Tests/Themes/AcrylicWindowTests.cs`（新增 2 条 `[Fact]` 与 1 个私有辅助方法；既有 3 条不动）

**范围与要点：**
- 做：两个窗口各加一处对 `IrisAcrylicTintBrush` 的 `DynamicResource` 引用（根面板一行 `Background`，或调查笔记给出的等价做法）。键由 Task 1 定义，本任务不新增键。
- 不做：**不动版面**（行列定义、边距、尺寸、控件顺序全不动）；**不改这两个窗口的任何名字与控件**；不改它们的代码隐藏；不改窗口自身的 `Background`（那是 `WindowMaterialService` 管的）；不为这两个窗口另写纯色回退逻辑（回退字典按键替换，S0-7）。
- 这两个窗口里若有不透明的整片底色（像设置窗口的导航与底栏那样）盖住了着色层：**本任务不去掉它们**——那属于版面改动（阶段 3、4）。调查笔记若指出着色层因此完全不可见，停下来报告，由协调方定，不自行扩大范围。
- 既有像素断言：先按 S0-6 的颜色手算着色后的预期值，再跑；断言变红时按调查笔记给出的口径处理，每一处改动都记进「改过的既有测试」，不改阈值来凑绿。
- 风险：这两个窗口的根结构与像素断言不在本计划第 2 节已查明的事实里，本任务的一切行号与元素名以调查笔记为准，展开详细步骤前不得凭推测下笔。

**步骤提纲：**
- [ ] 读调查笔记 `E-tint-other-windows.md`，据此定下两个窗口各自加着色的元素与受影响的测试
- [ ] 写（或改）测试：两个窗口的着色元素的 `Background` 是 `IrisAcrylicTintBrush`（按资源引用断言，不比颜色值）
- [ ] 跑，确认失败
- [ ] 改 `HistoryWindow.xaml`（改 1 行）、`TranslateWindow.xaml`（外包一层着色 `Grid`，插 2 行，既有行不动）
- [ ] 跑本任务的测试与受影响的既有渲染测试；处理变红的像素断言；`git add`

**详细步骤：**

> **代码与命令未运行过。** 本文件是写作产物：撰写时只读了仓库（分支 `codex/t041-acrylic-ui`，HEAD `dc08c48`），没有 `dotnet build`、没有 `dotnet test`、没有启动应用。下面的测试代码未编译，XAML 未加载过；每一处靠推断的地方都写了「执行时先核实」。
>
> 依据：用户裁决 U5、设计裁决 S0-6「另两个窗口」、计划 1.3 流程约束、计划 5.0 通用约定、调查笔记 `C:\Users\28910\AppData\Local\Temp\iris-t041-phase2-research\E-tint-other-windows.md`（E1–E6）。

#### 依据的事实

下面的行号是撰写时在 `dc08c48` 上**实际读到**的（标「笔记」的是调查笔记记录、撰写时没有再读源文件核对的）。前面的任务不改这两个窗口文件，行号按理不会漂；动手前仍按 5.0 的约定重新 grep（Step 1）。

##### 历史窗口 `src/Iris.App/History/HistoryWindow.xaml`（113 行）

| 事实 | 位置 | 来源 |
|---|---|---|
| 根元素 `ui:FluentWindow`，`ExtendsContentIntoTitleBar="True"`，根元素上没有 `Background` | :1–7 | 已读 |
| `Window.Resources`（`BoolToVis`、`ActionButton`、`ReadOnlyText`） | :8–17 | 已读 |
| 内容根是 `    <Grid>`（4 个空格缩进，不带任何属性、没有 `x:Name`、没有 `Margin`） | :18 | 已读 |
| 行定义 Auto / * / Auto | :19 | 已读 |
| `ui:TitleBar` 在根 `Grid` 的第 0 行里 | :20 | 已读 |
| `ScrollViewer x:Name="HistoryScroll"`，`Grid.Row="1"` | :21 | 已读 |
| 文件里另一个 `Grid` 开始标签是 `<Grid Grid.Row="2" Margin="16,8">`，所以「整行等于 `    <Grid>`」只有 :18 一处 | :107 | 笔记 E1、E6.1 |
| 窗口内自设 `Background` 的只有 5 行（:30、:52、:56、:58、:93），全是半透明或透明，没有不透明的整片底色 | — | 笔记 E1（:30 已读） |

##### 翻译窗口 `src/Iris.App/Translation/TranslateWindow.xaml`（357 行）

| 事实 | 位置 | 来源 |
|---|---|---|
| 根元素 `ui:FluentWindow`，`ExtendsContentIntoTitleBar="True"`，根元素上没有 `Background` | :1–24 | 已读 |
| `Window.Resources` 只有 `BoolToVis`；:28 是空行 | :25–28 | 已读 |
| 内容根是 `    <DockPanel Margin="12,8,12,12">`（4 个空格缩进，没有 `x:Name`、没有 `Background`，**有 Margin**） | :29 | 已读 |
| :30 空行；:31 是一条**既有中文注释**（顶栏说明），:32 是头部 `Grid`（`Background="Transparent"`，`MouseLeftButtonDown="OnTitleBarMouseDown"`） | :30–32 | 已读 |
| 文件结尾：`        </ScrollViewer>`（:355）、`    </DockPanel>`（:356）、`</ui:FluentWindow>`（:357） | :355–357 | 已读 |
| 文件里只有一个 `DockPanel` 开始标签、一个 `</DockPanel>` | :29、:356 | 笔记 E2、E6.1 |
| 窗口里没有 `ui:TitleBar`；没有任何 `Border` 承担窗口级圆角 / 描边 / 阴影；不是 `AllowsTransparency` 窗口 | — | 笔记 E2 |
| 窗口内自设 `Background` 的只有 4 行（:32、:175、:286、:346），透明或半透明 | — | 笔记 E2（:32、:346 已读） |

**为什么翻译窗口不能只给 `DockPanel` 加一行 `Background`**（笔记 E2）：`Background` 只画在元素自己的布局矩形内，`Margin` 是矩形外的留白。直接加会留下左 12 / 上 8 / 右 12 / 下 12 DIP 一圈没着色的边。`DockPanel` 没有 `Padding`，把 `Margin` 挪走属于改版面（Task 4B 明令不做）。所以改法是在 `DockPanel` **外面**包一层无 `Margin`、只负责着色的 `Grid`：在 :29 上方插一行、在 :356 下方插一行，中间 328 行既有内容一个字不动、**不重新缩进**。这属于 Task 4B「范围与要点」里写明允许的「调查笔记给出的等价做法」。

##### 测试 `tests/Iris.App.Tests/Themes/AcrylicWindowTests.cs`（53 行，已整读）

| 事实 | 位置 |
|---|---|
| using：`System.Threading.Tasks`、`System.Windows`、`Iris.App.History`、`Iris.App.Settings`、`Iris.App.Translation`、`Iris.App.Tests.TestSupport`、`Iris.Core.Platform`、`NSubstitute`、`Shouldly`、`Wpf.Ui.Controls`、`Xunit` | :1–11 |
| `[Collection("IrisThemeResources")]`，`public sealed class AcrylicWindowTests` | :15–16 |
| 既有三条 `[Fact]`，形态都是 `public Task 名字() => WpfTestRunner.RunAsync(() => { …; return Task.CompletedTask; });` | :18–40 |
| 翻译窗口的构造：`new TranslateWindow()`（无参，不 `Show()`） | :28 |
| 历史窗口的构造：`new HistoryWindow(Substitute.For<IDesktopContext>(), Substitute.For<INativeWindowPlacementAdapter>())`（不 `Show()`，替身不用配置） | :37–38 |
| 私有辅助方法 `AssertExtendsTitleBarAndLeftBackdropUnset(FluentWindow window)`，方法体结束于 :52，类结束于 :53 | :42–53 |

##### 测试宿主与受影响的既有测试（全部来自笔记 E4–E6，撰写时没有再读这些测试文件）

- 测试宿主 `tests/Iris.App.Tests/TestSupport/WpfTestRunner.cs:223–250` 自建 `Application`，合并 WPF-UI 字典、`Themes/Iris.Light.xaml`、`Themes/Iris.Styles.xaml`。Task 1 把 `IrisAcrylicTintBrush` 加进 `Iris.Light.xaml` / `Iris.Dark.xaml` 之后，测试里 `DynamicResource IrisAcrylicTintBrush` 就能解析。
- `ThemeService.Dispose` 不还原 Iris 字典：共享 `Application` 里残留的可能是浅色字典也可能是深色字典。所以**新断言不比颜色值**，只按「资源引用的键」与「解析到的实例」断言。
- 集合 `IrisThemeResources` 不与其他集合并行（`Themes/IrisThemeResourcesCollectionDefinition.cs:7`，`DisableParallelization = true`）。
- 涉及这两个窗口的既有断言**没有像素 / 颜色 / 亮度断言**，改完不会变红（笔记 E4 小结、E6.3）；会变的只有三组证据图（PNG 与清单里的 SHA256），它们不与任何基线比对：
  - `History/HistoryRenderingTests.cs`（证据目录：环境变量 `IRIS_T035_EVIDENCE`，没设就是临时目录下新建的 GUID 目录，:25）
  - `Ocr/OcrRenderingTests.cs`（渲染方法 :63–73；证据目录撰写时已补读，见下面「撰写时补查的三件事」第 3 条）
  - `Localization/LocalizationRenderingTests.cs`（证据目录：环境变量 `IRIS_T038_EVIDENCE`，没设就是临时 GUID 目录，:38）
- `Ocr/OcrRenderingTests.cs:66` 把 `window.Content` 转成 `FrameworkElement` 使用；翻译窗口外包一层 `Grid` 后 `Content` 从 `DockPanel` 变成 `Grid`，该转换仍成立。整个测试目录 grep `DockPanel` 无命中。
- 代码隐藏与 `WindowMaterialService` 都不碰内容根的 `Background`（笔记 E3）：材质服务只写 `FluentWindow.WindowBackdropType`，被 WPF-UI 清成透明的是**窗口自身**的 `Background`，着色加在内容根上不会被清掉。

##### 开工前提（由前面的任务保证，Step 1 逐条验）

1. Task 1 已暂存：`IrisAcrylicTintBrush` 在 `src/Iris.App/Themes/Iris.Light.xaml`、`Iris.Dark.xaml` 以及两份回退字典里。键不存在时 `DynamicResource` 静默解析为空、不抛异常——窗口照常显示但没有着色。
2. Task 3 已暂存：`HistoryRenderingTests` 与 `LocalizationRenderingTests` 的 `Theme` 辅助方法会往窗口级 `MergedDictionaries` 补对应主题的 Iris 颜色字典。没落地也不会有断言变红，但深色证据图会是「深色控件 + 浅色着色」（笔记 E5 的 L3，文字对比约 2:1），Step 10 的人工复看就没有意义。
3. Task 4 已暂存：设置窗口根 `Grid` 已铺着色。本任务不依赖它的代码，只依赖执行顺序（5.0）。

##### 撰写时补查的三件事（都已读文件核实）

1. **三个要改的文件都是 CRLF 行尾、无 BOM**（`grep -c $'\r$'` 的结果等于各自行数 113 / 357 / 53；文件头三个字节分别是 `<ui`、`<ui`、`usi`）。新增的行也用 CRLF，不要让编辑器把整份文件的行尾或编码改掉。
2. **本地化审计不按行号、也不按父元素登记**：`build/LocalizationAudit/Program.cs:100–114` 对 XAML 的登记是——属性按「路径 + 属性名=值 + 元素名/属性名 + 该元素自己的 `x:Name`」，文本按「路径 + 文本 + 元素名 + `x:Name`」，注释按「路径 + 注释全文」。所以在翻译窗口 :29 上方插一行（让 :31 的中文注释下移一行）、给 `DockPanel` 多套一层父元素，都不会改变任何既有登记的指纹；**改动既有行的文字或把注释挪进别的文件才会**。这也是「不重新缩进」之外还要「一个字不动」的原因。
3. **三组证据图的输出位置**（Step 4、9、10 要用）：
   - `History/HistoryRenderingTests.cs:25`：`(环境变量 IRIS_T035_EVIDENCE，没设则临时目录)\history-render-<GUID>\`，文件名 `{Light|Dark}-{800|320}-{状态}.png`（:37–38），另有 `manifest.json`（:81）。
   - `Ocr/OcrRenderingTests.cs:27`：`(环境变量 IRIS_T031B_EVIDENCE，没设则临时目录)\ocr-window-<主题>-<GUID>\`，三张：`loading.png`、`source.png`、`error.png`（:46、:51、:57）。位图尺寸取窗口的 `ActualWidth` × `ActualHeight`，画的是整个窗口（:69–70）。
   - `Localization/LocalizationRenderingTests.cs:38`：环境变量 `IRIS_T038_EVIDENCE` **本身就是目录**（没设则临时目录下 `iris-t038-visual-<GUID>`），文件名前缀 `{zh-CN|en-US}-{light|dark}-{small|normal}`（:46），本任务相关的是 `…-translation-*.png` 与 `…-history-*.png`（:105–135、:155–158）。
   - 以上行号对应 `dc08c48`；Task 3 会改前后两个文件的 `Theme` 辅助方法，行号可能下移几行，用例名与文件名规则不变。

---

#### 详细步骤

（行号对应 `dc08c48`；本任务之前的 Task 1–4 都不改 `HistoryWindow.xaml`、`TranslateWindow.xaml`、`AcrylicWindowTests.cs`——计划 4.2 的修改表里这两个 XAML 只挂在 4B 名下——所以行号应当没漂，但仍先核对。测试命令的前缀与过滤式写法一律以计划 5.0 **回填后**的口径为准：Task 1 Step 1(c) 若已把写法改成 `--project …` 或 `--filter-class …`，下面每条命令照同样的规则替换，类名不变。）

- [ ] **Step 1: 核对前提与锚点（只读，不改任何文件）**

在仓库根目录依次跑：

```
git status --short -- src/Iris.App/History/HistoryWindow.xaml src/Iris.App/Translation/TranslateWindow.xaml tests/Iris.App.Tests/Themes/AcrylicWindowTests.cs
grep -n "IrisAcrylicTintBrush" src/Iris.App/Themes/Iris.Light.xaml src/Iris.App/Themes/Iris.Dark.xaml src/Iris.App/Themes/Iris.SolidFallback.xaml src/Iris.App/Themes/Iris.SolidFallback.Dark.xaml
grep -n "IrisAcrylicTintBrush" src/Iris.App/Settings/SettingsWindow.xaml
grep -n "Iris.{(dark" tests/Iris.App.Tests/History/HistoryRenderingTests.cs tests/Iris.App.Tests/Localization/LocalizationRenderingTests.cs
grep -n "^    <Grid>" src/Iris.App/History/HistoryWindow.xaml
grep -n "DockPanel" src/Iris.App/Translation/TranslateWindow.xaml
grep -c "\[Fact\]" tests/Iris.App.Tests/Themes/AcrylicWindowTests.cs
grep -rn "IrisAcrylicTintBrush" src/Iris.App/History src/Iris.App/Translation
```

逐条期望与不符合时的处理：

| # | 期望 | 不符合时 |
|---|---|---|
| 1 | 无输出（三个文件都没被前面的任务动过） | 有输出：`git diff --cached -- <该文件>` 看是谁改的；改动与本任务的锚点无关就继续并按新行号做，否则停下来报告 |
| 2 | 四个文件各至少 1 行（Task 1 已落地） | 任一文件 0 行：**停**，Task 1 没做完，本任务不开工 |
| 3 | 1 行（Task 4 已落地） | 0 行：**停**，顺序不对（5.0 规定 4B 在 4 之后） |
| 4 | 两个文件各至少 1 行（Task 3 已落地；这个写法出自计划 Task 3 的逐字代码） | 任一文件 0 行：再跑 `grep -n "Iris.Dark.xaml" <该文件>` 看 Task 3 是不是换了写法落地。有命中 → 继续；仍然 0 行 → **停**，Task 3 没做完（没有断言依赖它，但 Step 10 的深色证据图会是「深色控件 + 浅色着色」，复看无意义） |
| 5 | 恰好 1 行：`18:    <Grid>` | 0 行或多行、或行号不是 18：打开文件确认「`</Window.Resources>` 的下一行、`<Grid.RowDefinitions>` 的上一行」是哪一行，以它为准 |
| 6 | 恰好 2 行：`29:    <DockPanel Margin="12,8,12,12">` 与 `356:    </DockPanel>` | 行数不是 2：停下来报告（文件结构与调查笔记不符） |
| 7 | `3` | 不是 3：记下实际数 N，后面 Step 3、5、7 里「5 条」一律读作 N+2，「3 条通过」读作 N 条 |
| 8 | 无输出（两个窗口还没人引用这个键） | 有输出：说明已经有人加过着色，停下来报告，不重复加 |

- [ ] **Step 2: 写两条会失败的测试（改 `tests/Iris.App.Tests/Themes/AcrylicWindowTests.cs`，两处插入）**

**断言方式的选择。** 要证明的是「根面板的 `Background` 是对 `IrisAcrylicTintBrush` 的资源引用」。两种可行写法：

- **选用：读本地值。** `{DynamicResource}` 落在依赖属性上时，`ReadLocalValue(Panel.BackgroundProperty)` 返回的不是画刷，而是一个资源引用表达式；用公开的 `System.Windows.Markup.ResourceReferenceExpressionConverter` 把它转回 `DynamicResourceExtension`，就能直接读出 `ResourceKey`。选它的理由：(1) 纯读取，不往窗口或 `Application` 里写任何东西；(2) 断言的正是 XAML 里写的那个键，`StaticResource`、就地写死的画刷、绑定都会被判红；(3) 不依赖颜色值，共享 `Application` 里残留浅色还是深色字典都不影响；(4) 调查笔记 E6.2 在 .NET Framework 的 WPF 上实测过这条路径（**.NET 10 下同名公开 API 行为相同属推断，未实测**）；仓库里按 `DynamicResourceExtension` 取键已有先例（`Themes/IrisStyleKeyResolutionTests.cs:42、:97`）。
- **退路：哨兵画刷。** 往窗口自己的 `Resources` 里放一支同键的新画刷，再断言根面板的 `Background` 变成了这同一个实例（只有 `DynamicResource` 会跟着变）。只用最常见的 API，但要改窗口的资源、也读不出键名本身。**启用条件写在 Step 5**，代码在本 Step 末尾。

**插入 (a)：两条 `[Fact]`。** 锚点是既有私有辅助方法的签名行（`dc08c48` 上是 :42，文件里唯一）：

```csharp
    private static void AssertExtendsTitleBarAndLeftBackdropUnset(FluentWindow window)
```

在这一行**上方**插入下面的内容（插入后，新代码的最后一行 `});` 与锚点行之间留一个空行；锚点行上方原有的那个空行保留在第三条既有 `[Fact]` 与新代码之间）：

```csharp
    [Fact]
    public Task HistoryWindow_RootPanelCarriesTheAcrylicTint() => WpfTestRunner.RunAsync(() =>
    {
        var window = new HistoryWindow(Substitute.For<IDesktopContext>(), Substitute.For<INativeWindowPlacementAdapter>());
        AssertIsTheTintLayer(window.Content.ShouldBeAssignableTo<System.Windows.Controls.Panel>()!);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TranslateWindow_TintLayerCoversTheClientAreaAndLeavesTheLayoutAlone() => WpfTestRunner.RunAsync(() =>
    {
        var window = new TranslateWindow();
        var root = window.Content.ShouldBeAssignableTo<System.Windows.Controls.Panel>()!;
        AssertIsTheTintLayer(root);

        // 版面没动：原来的 DockPanel 原样待在着色层里，边距还是那四个数，自己不带底色。
        // 着色不能直接加在它身上——Background 只画在 Margin 以内，四周会留一圈没着色的边。
        var dock = root.Children.Cast<UIElement>().ShouldHaveSingleItem()
            .ShouldBeOfType<System.Windows.Controls.DockPanel>()!;
        dock.Margin.ShouldBe(new Thickness(12, 8, 12, 12));
        dock.Background.ShouldBeNull();
        return Task.CompletedTask;
    });

```

**插入 (b)：私有辅助方法。** 锚点是既有辅助方法的最后两行加类的结束括号（`dc08c48` 上是 :51–53）：

```csharp
        window.WindowBackdropType.ShouldBe(backdropDefault);
    }
}
```

改成（在 `    }` 与 `}` 之间插入一个空行加新方法，既有行不动）：

```csharp
        window.WindowBackdropType.ShouldBe(backdropDefault);
    }

    private static void AssertIsTheTintLayer(System.Windows.Controls.Panel root)
    {
        // 着色层必须铺满客户区：带 Margin 的面板只会画出一块内嵌矩形，四周露出没着色的背板。
        root.Margin.ShouldBe(new Thickness(0));

        // 按「资源引用」断言，不比颜色值：共享 Application 里留的是浅色还是深色字典都不影响这条。
        // {DynamicResource} 落在依赖属性上时，本地值是资源引用表达式，不是画刷本身。
        var local = root.ReadLocalValue(System.Windows.Controls.Panel.BackgroundProperty);
        local.ShouldNotBe(DependencyProperty.UnsetValue, "the root panel has no Background");
        local.ShouldBeAssignableTo<System.Windows.Expression>("Background must be a {DynamicResource} reference, not a brush set in place");
        var reference = new System.Windows.Markup.ResourceReferenceExpressionConverter()
            .ConvertTo(local, typeof(System.Windows.Markup.MarkupExtension))
            .ShouldBeOfType<DynamicResourceExtension>()!;
        reference.ResourceKey.ShouldBe("IrisAcrylicTintBrush");

        // 键真的解析到了画刷：DynamicResource 拼错键名只会静默留空，不会抛异常。
        root.Background.ShouldNotBeNull();
        root.Background.ShouldBeSameAs(root.FindResource("IrisAcrylicTintBrush"));
    }
}
```

写法说明（都是为了不引出编译问题）：

- **不新增 `using`。** `Thickness`、`UIElement`、`DependencyProperty`、`DynamicResourceExtension`、`Expression` 都在已有的 `System.Windows`（:2）里；`Cast<T>()` 来自 `System.Linq`，`Directory.Build.props:5` 开了 `ImplicitUsings`。
- **`Panel`、`DockPanel`、`Expression`、转换器都写全名。** 文件已 `using Wpf.Ui.Controls;`（:10），再引入 `System.Windows.Controls` 可能出现同名类型歧义（CS0104）；`Expression` 写全名是为了不和 `System.Linq.Expressions` 混淆。
- 窗口的构造与既有三条一样：不 `Show()`，`HistoryWindow` 的两个参数用未配置的 NSubstitute 替身。
- 本 Step 只改测试项目，可以有中文注释；失败消息用英文是为了控制台不乱码。
- 这是**新增**用例，没有修改任何既有用例；既有三条 `[Fact]` 与既有辅助方法一个字不动。

**退路代码（哨兵画刷）——只有 Step 5 判定需要时才换用，换用即「记入偏差清单」。** 把上面的 `AssertIsTheTintLayer` 整个方法换成下面这个（多一个窗口参数），并把两条 `[Fact]` 里的调用分别改成 `AssertIsTheTintLayer(window, window.Content.ShouldBeAssignableTo<System.Windows.Controls.Panel>()!);` 与 `AssertIsTheTintLayer(window, root);`：

```csharp
    private static void AssertIsTheTintLayer(FluentWindow window, System.Windows.Controls.Panel root)
    {
        // 着色层必须铺满客户区：带 Margin 的面板只会画出一块内嵌矩形，四周露出没着色的背板。
        root.Margin.ShouldBe(new Thickness(0));
        root.ReadLocalValue(System.Windows.Controls.Panel.BackgroundProperty)
            .ShouldNotBe(DependencyProperty.UnsetValue, "the root panel has no Background");

        // 键真的解析到了画刷：DynamicResource 拼错键名只会静默留空，不会抛异常。
        root.Background.ShouldNotBeNull();
        root.Background.ShouldBeSameAs(root.FindResource("IrisAcrylicTintBrush"));

        // 哨兵：往窗口自己的资源里放一支同键的画刷。只有 {DynamicResource IrisAcrylicTintBrush}
        // 会跟着换成这同一个实例；StaticResource、写死的画刷、别的键都不会。窗口用完即弃，不污染 Application。
        var sentinel = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x80, 0x12, 0x34, 0x56));
        window.Resources["IrisAcrylicTintBrush"] = sentinel;
        root.Background.ShouldBeSameAs(sentinel);
    }
```

- [ ] **Step 3: 跑，确认两条新用例按预期失败**

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~AcrylicWindowTests"
```

期望：**编译通过、零警告**（这两条测试不依赖任何还不存在的产品代码，所以这次的红是运行时的断言失败，不是编译错误）；共 5 条，**3 条通过、2 条失败**：

| 用例 | 预期失败在哪一行 | 预期现象 |
|---|---|---|
| `HistoryWindow_RootPanelCarriesTheAcrylicTint` | `local.ShouldNotBe(DependencyProperty.UnsetValue, …)` | Shouldly 报 `local` 不应是 `{DependencyProperty.UnsetValue}`，附带消息 `the root panel has no Background`（根 `Grid` 现在没设 `Background`；它的 `Margin` 是 0，所以前一行通过） |
| `TranslateWindow_TintLayerCoversTheClientAreaAndLeavesTheLayoutAlone` | `root.Margin.ShouldBe(new Thickness(0))` | Shouldly 报 `root.Margin` 应为 `0,0,0,0`、实际 `12,8,12,12`（`window.Content` 现在是那个带 Margin 的 `DockPanel`）。这条红正好说明「直接给 `DockPanel` 加 `Background`」过不了 |

与预期不符时：

| 现象 | 处理 |
|---|---|
| 编译错误 CS0104（类型名有歧义） | 把报错的类型写成全名（`System.Windows.…`），不加新的 `using` |
| 编译错误 CS8600 / CS8602 / CS8604（可能为 null） | 在报错的那个 Shouldly 调用后面补 `!` |
| 构建报「不必要的抑制运算符」之类的代码样式诊断（IDE0370 等） | 删掉报错位置的 `!`，别的不动 |
| 编译错误 CS1061：`UIElementCollection` 没有 `Cast` | 在文件头补一行 `using System.Linq;`（按字母序放在 `using System.Threading.Tasks;` 之前） |
| 其他编译错误 | 只改测试文件直到编译通过；**不许为了让测试编译去改 `src/`** |
| 两条新用例有任何一条是绿的 | 停：说明窗口已经带了着色或测试写错了。先 `git diff -- src/Iris.App/History src/Iris.App/Translation` 确认没人动过，再逐行对照 Step 2 的代码 |
| 失败在 `window.Content.ShouldBeAssignableTo<…Panel>()` | 停下来报告：窗口的内容根不是面板，与调查笔记不符 |
| 既有三条里有红的 | 与本任务无关（本 Step 没动产品代码）；先查 Task 1–4 的遗留，不带着红往下做 |

- [ ] **Step 4: 记回归基线，并留一套「改前」证据图（仍然不改产品代码）**

把三组证据图的输出指到仓库外的固定目录，再跑一次本任务的回归过滤集。Windows PowerShell 5.1：

```powershell
$before = Join-Path $env:TEMP 'iris-t041-task4b\before'
New-Item -ItemType Directory -Force $before | Out-Null
$env:IRIS_T035_EVIDENCE  = $before
$env:IRIS_T031B_EVIDENCE = $before
$env:IRIS_T038_EVIDENCE  = Join-Path $before 'localization'
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~AcrylicWindowTests|FullyQualifiedName~HistoryRenderingTests|FullyQualifiedName~HistoryWindowTests|FullyQualifiedName~HistoryLifecycleTests|FullyQualifiedName~OcrRenderingTests|FullyQualifiedName~TranslateWindowUiTests|FullyQualifiedName~TranslateWindowIntegrationTests|FullyQualifiedName~TranslateWindowPresenterTests|FullyQualifiedName~TranslateWindowOperationTests|FullyQualifiedName~LocalizationRenderingTests|FullyQualifiedName~IrisColorKeyTests|FullyQualifiedName~IrisThemeDictionaryTests|FullyQualifiedName~OcrEntrypointTests|FullyQualifiedName~OcrLifecycleTests|FullyQualifiedName~OcrPublicationTests|FullyQualifiedName~TranslationEndToEndTests|FullyQualifiedName~TranslationWorkflowLateArrivalTests"
```

（在 Git Bash 里跑的话，前五行换成：`before="$TEMP/iris-t041-task4b/before"; mkdir -p "$before"; export IRIS_T035_EVIDENCE="$before" IRIS_T031B_EVIDENCE="$before" IRIS_T038_EVIDENCE="$before/localization"`，`dotnet test` 那一行不变。）

这条过滤式就是调查笔记 E6.4 的「核心」加「建议同批加跑」，17 个类名在 `tests/Iris.App.Tests` 里都各有同名的类（撰写时按文件名核对过）。

期望：**失败恰好 2 条**——就是 Step 3 那两条；其余全绿。把汇总行里的「通过 N 条 / 失败 2 条 / 跳过 S 条」原样抄进任务记录，Step 9 要拿它对比。

- 失败数不是 2：多出来的红与本任务无关（产品代码还没动），先查清再继续；不带着不明原因的红往下做。
- 过滤式匹配到 0 条或不被接受：按计划 Task 1 Step 1(c) 的退路换写法（5.0 应当已经回填了可用的那一种）。
- 跑完后 `$before` 下应当出现：`history-render-<GUID>\`、两个 `ocr-window-<Light|Dark>-<GUID>\`、`localization\`。缺哪一个就记下来——Step 10 对应那一组只能看「改后」，不能前后对比。

- [ ] **Step 5: 历史窗口——给根 `Grid` 加一行着色（改 `src/Iris.App/History/HistoryWindow.xaml`，1 行）**

原文锚点（`dc08c48` 上是 :18；整行，4 个空格缩进；`</Window.Resources>` 的下一行、`<Grid.RowDefinitions>` 的上一行；文件里唯一一处不带属性的 `<Grid>`）：

```xml
    <Grid>
```

改成：

```xml
    <Grid Background="{DynamicResource IrisAcrylicTintBrush}">
```

只改这一行，别的一个字符都不动：

- 不加 `x:Name`（测试用 `window.Content` 取它）；不改 :19 的行定义；不动 :20 的 `ui:TitleBar`——它是根 `Grid` 的第 0 行，所以标题栏区域一并被着色，与设置窗口（S0-6）同构。
- 不改根元素 `ui:FluentWindow` 的任何属性：窗口自身的 `Background` 归 `WindowMaterialService` 管。
- 不改 `HistoryWindow.xaml.cs`、`HistoryWindowSession.cs`。
- 行尾保持 CRLF，文件保持无 BOM。

改完立刻核对改动面：

```
git diff --numstat -- src/Iris.App/History/HistoryWindow.xaml
```

期望：`1	1	src/Iris.App/History/HistoryWindow.xaml`（增 1 行、删 1 行）。数字大于 1 说明编辑器动了行尾、编码或别的行：用 `git checkout -- src/Iris.App/History/HistoryWindow.xaml` 还原到暂存区的版本（Step 1 第 1 条已确认这个文件此前没有任何改动，还原不会丢别人的东西），再重做。

- [ ] **Step 6: 跑，确认历史窗口那条转绿、翻译窗口那条仍然红**

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~AcrylicWindowTests"
```

期望：5 条里 **4 条通过、1 条失败**；失败的是 `TranslateWindow_TintLayerCoversTheClientAreaAndLeavesTheLayoutAlone`，现象与 Step 3 相同（`root.Margin` 实际 `12,8,12,12`）。

`HistoryWindow_RootPanelCarriesTheAcrylicTint` 仍然红时，按失败位置分三种，**不许改断言凑绿**：

| 失败位置 | 含义 | 处理 |
|---|---|---|
| `local.ShouldNotBe(UnsetValue)` | XAML 没生效 | 检查 Step 5 是否改对了文件、是否重新构建了（`-c Release`） |
| `local.ShouldBeAssignableTo<System.Windows.Expression>`、`ConvertTo(…)` 抛异常、或 `ShouldBeOfType<DynamicResourceExtension>` | XAML 是对的，但 .NET 10 下「本地值 → `DynamicResourceExtension`」这条读取路径与 .NET Framework 上实测的不一样 | **换用 Step 2 末尾的退路代码（哨兵画刷）**，重跑本 Step；在任务记录里写明换用原因与原始失败输出，**记入偏差清单** |
| `reference.ResourceKey.ShouldBe(…)` | 键名打错 | 改 XAML 的键名，逐字对照 `IrisAcrylicTintBrush` |
| `root.Background.ShouldNotBeNull()`，或 `FindResource` 抛 `ResourceReferenceKeyNotFoundException` | 测试宿主的 `Application` 资源里没有这个键 | 不是本任务能修的：回头查 Task 1（键是否进了 `Iris.Light.xaml` / `Iris.Dark.xaml`）与 `TestSupport/WpfTestRunner.cs` 的 `AddIrisDictionaries`；停下来报告 |
| `ShouldBeSameAs(root.FindResource(…))` | 引用解析到了别的实例 | 停下来报告，附上两边对象的类型与颜色值；不自行改成比颜色 |

- [ ] **Step 7: 翻译窗口——在 `DockPanel` 外包一层只负责着色的 `Grid`（改 `src/Iris.App/Translation/TranslateWindow.xaml`，插入 2 行）**

**只插入两行，不修改、不删除、不移动、不重新缩进任何既有行。** 中间从 `<DockPanel Margin="12,8,12,12">` 到 `</DockPanel>` 的 328 行原样留在原来的缩进上（其中有既有中文注释，本地化审计按「路径 + 全文」登记，动一个字都会让登记失配）。新包的 `Grid` 与 `DockPanel` 同为 4 个空格缩进，看上去像兄弟节点——这是有意的，不要「顺手修缩进」。

按计划 5.0 的习惯从文件末尾往前改。

(a) 结尾。原文锚点（`dc08c48` 上是 :355–357，文件最后三行；`</DockPanel>` 全文件只有这一处）：

```xml
        </ScrollViewer>
    </DockPanel>
</ui:FluentWindow>
```

改成（在 `    </DockPanel>` **下面**插入一行 `    </Grid>`，4 个空格缩进）：

```xml
        </ScrollViewer>
    </DockPanel>
    </Grid>
</ui:FluentWindow>
```

(b) 开头。原文锚点（`dc08c48` 上是 :27–29；`<DockPanel` 全文件只有这一处）：

```xml
    </Window.Resources>

    <DockPanel Margin="12,8,12,12">
```

改成（在 `    <DockPanel Margin="12,8,12,12">` **上面**插入一行，4 个空格缩进；:28 的空行留在原位）：

```xml
    </Window.Resources>

    <Grid Background="{DynamicResource IrisAcrylicTintBrush}">
    <DockPanel Margin="12,8,12,12">
```

要点：

- 新 `Grid` **不带** `Margin`、`x:Name`、行列定义、对齐属性、`CornerRadius`、事件处理器——它只有 `Background` 这一个属性。没有行列定义的 `Grid` 把全部可用空间原样交给唯一的子元素，所以 `DockPanel` 的尺寸、它的 `Margin="12,8,12,12"`、所有子元素的位置都不变。
- 不要把 `Background` 直接加到 `DockPanel` 上、也不要把 `DockPanel` 的 `Margin` 挪到新 `Grid` 上当 `Padding` 用：前者留一圈没着色的边，后者是改版面（阶段 4 的事）。
- 不要为了圆角另加 `Border` 或 `CornerRadius`：这个窗口的圆角、描边、阴影由系统对整个窗口做，不在 XAML 里。
- 不改根元素 `ui:FluentWindow` 的任何属性；不改 `TranslateWindow.xaml.cs`、`TranslateWindowPresenter.cs`。
- 不加注释（`src/` 里新注释只能是英文；这里选择不加，解释写在测试里）。
- 行尾保持 CRLF，文件保持无 BOM。

改完立刻核对改动面：

```
git diff --numstat -- src/Iris.App/Translation/TranslateWindow.xaml
git diff -U0 -- src/Iris.App/Translation/TranslateWindow.xaml
```

期望：第一条输出 `2	0	src/Iris.App/Translation/TranslateWindow.xaml`（增 2 行、删 0 行）；第二条里以 `+` 开头的内容行只有两行（`+    <Grid Background="{DynamicResource IrisAcrylicTintBrush}">` 与 `+    </Grid>`），**没有任何以 `-` 开头的内容行**。出现删除行或多出的新增行：`git checkout -- src/Iris.App/Translation/TranslateWindow.xaml` 还原到暂存区的版本（Step 1 第 1 条已确认此前无改动），再重做；不要试图手工把既有行「改回去」。

- [ ] **Step 8: 跑，确认五条全绿**

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~AcrylicWindowTests"
```

期望：**5 条通过、0 失败**，构建零警告。

`TranslateWindow_TintLayerCoversTheClientAreaAndLeavesTheLayoutAlone` 仍然红时：

| 失败位置 | 含义 | 处理 |
|---|---|---|
| 构建阶段报 XAML 错误（MC3000 之类，标签不匹配） | `</Grid>` 没插在 `</DockPanel>` 与 `</ui:FluentWindow>` 之间，或漏了一行 | 对照 Step 7 的两段「改成」逐字检查 |
| `root.Margin.ShouldBe(new Thickness(0))` | `window.Content` 还是 `DockPanel`（没包上），或新 `Grid` 被加了 `Margin` | 对照 Step 7(b) |
| `AssertIsTheTintLayer` 里的其余断言 | 与 Step 6 的表同理 | 按 Step 6 的表处理（退路已在 Step 6 启用的话，这里用的就是退路代码） |
| `ShouldHaveSingleItem()` | 新 `Grid` 里不止一个子元素，或 `DockPanel` 没在它里面 | 对照 Step 7(a)(b) 的插入位置 |
| `dock.Margin.ShouldBe(new Thickness(12, 8, 12, 12))` | `DockPanel` 那一行被改动了 | 还原该行；既有行不许动 |
| `dock.Background.ShouldBeNull()` | 着色被加到了 `DockPanel` 上 | 去掉，着色只在外层 `Grid` |

- [ ] **Step 9: 跑回归过滤集与「设置全集」，并留一套「改后」证据图**

先把证据目录换成「改后」，再跑与 Step 4 **完全相同**的过滤式。Windows PowerShell 5.1：

```powershell
$after = Join-Path $env:TEMP 'iris-t041-task4b\after'
New-Item -ItemType Directory -Force $after | Out-Null
$env:IRIS_T035_EVIDENCE  = $after
$env:IRIS_T031B_EVIDENCE = $after
$env:IRIS_T038_EVIDENCE  = Join-Path $after 'localization'
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~AcrylicWindowTests|FullyQualifiedName~HistoryRenderingTests|FullyQualifiedName~HistoryWindowTests|FullyQualifiedName~HistoryLifecycleTests|FullyQualifiedName~OcrRenderingTests|FullyQualifiedName~TranslateWindowUiTests|FullyQualifiedName~TranslateWindowIntegrationTests|FullyQualifiedName~TranslateWindowPresenterTests|FullyQualifiedName~TranslateWindowOperationTests|FullyQualifiedName~LocalizationRenderingTests|FullyQualifiedName~IrisColorKeyTests|FullyQualifiedName~IrisThemeDictionaryTests|FullyQualifiedName~OcrEntrypointTests|FullyQualifiedName~OcrLifecycleTests|FullyQualifiedName~OcrPublicationTests|FullyQualifiedName~TranslationEndToEndTests|FullyQualifiedName~TranslationWorkflowLateArrivalTests"
```

（Git Bash：`after="$TEMP/iris-t041-task4b/after"; mkdir -p "$after"; export IRIS_T035_EVIDENCE="$after" IRIS_T031B_EVIDENCE="$after" IRIS_T038_EVIDENCE="$after/localization"`，`dotnet test` 那一行不变。）

期望：**通过 N+2 条、失败 0 条、跳过 S 条**（N、S 是 Step 4 记下的数；多出的 2 条就是两条新用例由红转绿）。总数与 Step 4 不一致说明有用例没跑到或多跑了，先查清。

再跑计划 5.0 的「设置全集」（Task 4B 的「验证命令」要求；本任务没改 `SettingsWindow.xaml`，跑它是为了确认共享 `Application` 里的主题字典没被这两条新用例带偏）：

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~Iris.App.Tests.Settings|FullyQualifiedName~Iris.App.Tests.Themes|FullyQualifiedName~Iris.App.Tests.Localization|FullyQualifiedName~Iris.App.Tests.Updates|FullyQualifiedName~MouseRenderingTests|FullyQualifiedName~HistoryRenderingTests|FullyQualifiedName~ThemeServicePreviewTests"
```

期望：全绿（0 失败）；通过数与 Task 4 结束时记录的「设置全集」通过数相比恰好多 2。Task 4 的任务记录里没留这个数时，只判「0 失败」，并在任务记录里写明「设置全集无改前通过数可比」。

出现红时的判断顺序（照 5.0）：

1. 红的是**涉及这两个窗口**的既有用例（`HistoryRenderingTests`、`OcrRenderingTests`、`LocalizationRenderingTests`、`TranslateWindowUiTests`、`TranslateWindow*Tests`、`History*Tests`）：调查笔记逐条核对过，它们只有几何 / 流程 / 资源画刷断言，按理不会红。先看失败信息是不是几何断言（控件越界、视口高度、命中测试）——是的话说明 Step 7 动了版面（多半是新 `Grid` 带了多余属性，或既有行被改动），**改实现，不改测试**。
2. 红的是像素 / 亮度断言：这两个窗口没有这类断言，红的应当是设置窗口那边的金丝雀（`SelectionSettingsRenderingTests`、`SettingsHotkeysIntegrationTests`），属 Task 3 / Task 4 的范围。确认办法（本阶段不用 stash / reset）：在编辑器里把 Step 5 的那一行改回 `    <Grid>`、把 Step 7 插入的两行删掉，只重跑变红的那一个类。仍然红 → 与本任务无关，照 Step 5、Step 7 把三行改动重新做上（并重跑 Step 8 确认五条全绿），然后报告协调方；转绿 → 确是本任务引起的，同样先把三行改动做回去，停下来报告，不自行改阈值。
3. 本任务**预期不修改任何既有测试**。如果最终确有既有断言必须改，逐条写明「文件、用例名、改了什么、为什么」，**记入偏差清单**与完成报告的「改过的既有测试」。

- [ ] **Step 10: 人工复看证据图（离屏渲染的 PNG；不启动应用）**

Step 4 与 Step 9 把图分别留在 `%TEMP%\iris-t041-task4b\before\` 与 `…\after\` 下（都在仓库外，不提交）。子目录名带 GUID，用通配找：`history-render-*`（历史窗口）、`ocr-window-Light-*` 与 `ocr-window-Dark-*`（翻译窗口）、`localization`（双语全套）。

**(a) 先取样——着色层在浅色下只让底色差约 5 级、深色下约 3 级，肉眼看不出「有没有一圈没着色的边」，必须读像素。** Windows PowerShell 5.1，整段贴进去跑（只读这些 PNG）：

```powershell
Add-Type -AssemblyName System.Drawing
function Probe([string]$png) {
    $b = New-Object System.Drawing.Bitmap $png
    try {
        $w = $b.Width; $h = $b.Height
        $points = [ordered]@{
            TL = @(4, 4); TR = @(($w - 5), 4); BL = @(4, ($h - 5)); BR = @(($w - 5), ($h - 5))
            Header = @([int][math]::Floor($w / 2), 20)
        }
        $cells = foreach ($name in $points.Keys) {
            $c = $b.GetPixel($points[$name][0], $points[$name][1])
            '{0}=#{1:X2}{2:X2}{3:X2}{4:X2}' -f $name, $c.A, $c.R, $c.G, $c.B
        }
        '{0}\{1}  {2}x{3}  {4}' -f (Split-Path (Split-Path $png -Parent) -Leaf), (Split-Path $png -Leaf), $w, $h, ($cells -join '  ')
    } finally { $b.Dispose() }
}
foreach ($side in 'before', 'after') {
    Get-ChildItem (Join-Path $env:TEMP "iris-t041-task4b\$side") -Recurse -Filter *.png |
        Where-Object { $_.Directory.Name -like 'ocr-window-*' -or $_.Directory.Name -like 'history-render-*' } |
        Sort-Object FullName | ForEach-Object { "$side  " + (Probe $_.FullName) }
}
```

每行输出五个取样点的 `#AARRGGBB`：四个角各向内 4 像素（`TL` / `TR` / `BL` / `BR`），加上顶部中间一点（`Header`：横向正中、距顶 20 像素）。

**翻译窗口（`ocr-window-*` 下的 `loading.png`、`source.png`、`error.png`，浅色、深色各三张）的判据——不依赖估算值：**

1. 「改后」的 `TL`、`TR`、`BL`、`BR` 四个值彼此相等，并且等于「改后」的 `Header`。四个角都落在 `DockPanel` 的 `Margin`（左 12 / 上 8 / 右 12 / 下 12）那一圈里，`Header` 落在 `DockPanel` 里面的头部空白处；五点同色 = 着色铺满了整个客户区，**没有未着色的边**。
2. 「改后」的四个角与「改前」同一张图的四个角**不相等**。相等而 `Header` 变了 = 着色只铺在了 `DockPanel` 里面，Step 7 做错了，回去改。
3. `Header` 若恰好落在文字或按钮上（它的值与四角相差很大），不算失败：用看图工具在同一张图的头部另找一个空白点比对，判据仍是「与四角同色」。

**历史窗口（`history-render-*` 下的 `Light-800-empty.png`、`Dark-800-empty.png`、`Light-320-empty.png`、`Dark-320-empty.png`）的判据：** 「改后」的 `BL` 与 `BR` 相等，且都不等于「改前」的 `BL` / `BR`；「改后」的 `Header`（落在标题栏区域）与 `BL` 同色 = 标题栏区域一并被着色。`TL` / `TR` 可能压在标题栏的图标或按钮上，只作参考。

**参考值（全部是调查笔记 E5 按混合公式估算的，没有取过真实像素，允许 ±2；对不上不算失败，把实际值记进任务记录）：**

| 窗口 / 主题 | 改前（空白处） | 改后（空白处） | 说明 |
|---|---|---|---|
| 翻译 · 浅色 | `#FFFAFAFA` | 约 `#FFF5F5F5` | 这个夹具不注册材质服务，窗口底不透明 |
| 翻译 · 深色 | `#FF202020` | 约 `#FF232323` | 同上；走 `ThemeService`，取深色着色值 |
| 历史 · 浅色 | `#00……`（透明）或 `#FFFAFAFA` | `#B3F3F3F3`，或约 `#FFF5F5F5` | 前者是本机支持亚克力、窗口底被清成透明的情形（推断，未实跑） |
| 历史 · 深色 | `#00……`（透明）或 `#FF202020` | `#AD242424`，或约 `#FF232323` | 取到深色着色值的前提是 Task 3 已落地 |

**一个必须拦住的异常：** 深色那几张「改后」的空白处如果是浅灰（`#B3F3F3F3`，或不透明的约 `#FFB4B4B4`），说明渲染夹具在深色轮次仍取到了浅色着色值——那是 Task 3 的夹具对齐没生效，不是本任务的 XAML 问题；停下来报告，不在本任务里改夹具。

**(b) 再用眼看这几张「改后」的图**（历史窗口的图在本机可能带透明通道，看图工具的底色会透上来，属正常）：

| 图 | 看什么 |
|---|---|
| `after\ocr-window-Light-*\source.png`、`after\ocr-window-Dark-*\source.png` | 整窗底色均匀，没有「中间一块、四周一圈」的色差；头部三个按钮、语言控件、输入框、卡片的位置与 `before` 里同名的图逐一对得上（版面没动） |
| `after\ocr-window-Dark-*\error.png` | 深色底上错误条与文字清晰可读 |
| `after\history-render-*\Light-800-empty.png`、`Dark-800-empty.png`、`Dark-320-empty.png` | 标题栏区域与内容区是同一个底色，没有分界；搜索框、按钮、底部「关闭」按钮的位置与 `before` 一致 |
| `after\localization\zh-CN-dark-normal-translation-input.png`、`after\localization\zh-CN-dark-normal-history-list-00.png` | 深色轮次是深色底配浅色字，不是浅灰底配白字 |
| `after\localization\en-US-light-small-translation-input.png` | 最小尺寸（280×300）下着色仍铺满，控件没有被挤动 |

（`localization` 下的文件名按 `LocalizationRenderingTests.cs:46、:105、:157、:226` 的命名规则推出；某一张不存在时用 `Get-ChildItem "$env:TEMP\iris-t041-task4b\after\localization" -Filter '*dark-normal-translation*'` 列出同组实际文件名，看其中第一张。）

**(c) 离屏图看不了的，并入 Task 14 的真机截图清单。** `RenderTargetBitmap` 只画 WPF 的可视树，不含系统合成的亚克力背板、窗口圆角与阴影，所以下面四项**本 Step 无法验证**，在任务记录里标「未验证，待 Task 14」，由 Task 14 用便携副本补看（Task 14 已有「三个亚克力窗口都要看着色」一条，这四项是它在这两个窗口上的具体看点）：

1. 翻译窗口四个圆角处有没有方角的着色溢出（着色层是矩形，靠系统按窗口圆角裁剪）。
2. 翻译窗口四周原先 `Margin` 那一圈，真机上与内部是否同色。
3. 翻译窗口按住头部空白处仍能拖动、四条边仍能拖动缩放（那一圈的鼠标命中对象从窗口模板变成了新 `Grid`，推断无影响）。
4. 历史窗口与翻译窗口在「浅色窗口压在深色桌面上」时的底色，与设置窗口并排是否一致（U5 的目的）。

本 Step 的产出（写进任务记录，Task 14 的完成报告要引用）：取样脚本的完整输出；(a) 的两条判据是否成立；(b) 每张图一句话结论；(c) 四项标「待 Task 14」。

- [ ] **Step 11: 暂存（不提交）**

```
git add src/Iris.App/History/HistoryWindow.xaml src/Iris.App/Translation/TranslateWindow.xaml tests/Iris.App.Tests/Themes/AcrylicWindowTests.cs
git status --short -- src/Iris.App/History src/Iris.App/Translation tests/Iris.App.Tests/Themes/AcrylicWindowTests.cs
git diff --cached --numstat -- src/Iris.App/History/HistoryWindow.xaml src/Iris.App/Translation/TranslateWindow.xaml tests/Iris.App.Tests/Themes/AcrylicWindowTests.cs
git diff --cached -U0 -- src/Iris.App/History/HistoryWindow.xaml src/Iris.App/Translation/TranslateWindow.xaml
```

期望：

- `git status`：恰好三行，都是 `M `（已暂存的修改）；`src/Iris.App/History`、`src/Iris.App/Translation` 下没有别的文件出现（代码隐藏、会话、Presenter 都没动）。
- `--numstat`：`1	1	…/HistoryWindow.xaml`、`2	0	…/TranslateWindow.xaml`、`44	0	…/AcrylicWindowTests.cs`。测试文件的新增行数用的是 Step 2 的主写法（24 + 20 行）；换用了退路代码或按 Step 3 的表补过 `using` 的话行数会不同，**删除数必须是 0**（没有改动任何既有用例）。
- 最后一条：两个 XAML 加起来只有 3 行以 `+` 开头的内容行、1 行以 `-` 开头的内容行（历史窗口那行 `    <Grid>`），全部是 ASCII——`src/` 里没有新增中文，既有中文一个字没动。

**不要 `git commit`。** 整个阶段只在 Task 14 提交一次，且提交前须用户确认（计划 1.3 第 5 条）。`%TEMP%\iris-t041-task4b\` 下的证据图不进仓库，留到 Task 14 写完成报告之后再删。

- [ ] **Step 12: 本任务自检清单**

逐条打勾；任何一条答不上「是」，回到括号里写的 Step。

范围（Task 4B「范围与要点」、用户裁决 U5）：

- [ ] 两个窗口各有且只有一处对 `IrisAcrylicTintBrush` 的引用，都是 `DynamicResource`：`grep -rn "IrisAcrylicTintBrush" src/Iris.App/History src/Iris.App/Translation` 恰好 2 行（Step 5、7）
- [ ] 没有新增颜色键，没有改 `src/Iris.App/Themes/` 下任何文件（键由 Task 1 定义）
- [ ] 版面没动：两个窗口没有任何既有行的行列定义、`Margin`、尺寸、控件顺序被改；翻译窗口的 `DockPanel` 仍是 `Margin="12,8,12,12"`（Step 8 的断言守着）
- [ ] 没有改任何 `x:Name`、没有新增 `x:Name`、没有改任何控件类型；翻译窗口只多了一个只带 `Background` 的 `Grid`（Step 7）
- [ ] 没有改代码隐藏与 Presenter：`git diff --cached --name-only -- src` 里属于本任务的只有两个 `.xaml`（Step 11）
- [ ] 没有改两个窗口根元素 `ui:FluentWindow` 的 `Background` 或 `WindowBackdropType`（既有三条 `AcrylicWindowTests` 仍绿，Step 8）
- [ ] 没有为这两个窗口另写纯色回退逻辑（回退靠 S0-7 的字典按键替换）
- [ ] 没有去掉这两个窗口里任何既有的底色（调查笔记确认它们本来就没有不透明的整片底色，不存在「着色层完全不可见」的情形）

设计裁决 S0-6：

- [ ] 用的是同一个键、同样的引用方式，与设置窗口根 `Grid` 的写法一致
- [ ] 历史窗口的标题栏区域一并被着色（`ui:TitleBar` 在着色的根 `Grid` 第 0 行里；Step 10 的 `Header` 取样）
- [ ] 翻译窗口的着色铺满客户区、没有未着色的边（Step 8 的 `Margin` 断言 + Step 10 的四角取样）

流程约束（计划 1.3、5.0）：

- [ ] `src/` 里没有新增中文字面量与中文注释；既有中文没有被修改、删除或搬动；翻译窗口的既有行没有重新缩进（Step 7、11 的 `-U0` 输出）
- [ ] 三个文件的行尾仍是 CRLF、无 BOM（`--numstat` 的数字与期望一致即可证明没有整文件改写）
- [ ] 没有新增依赖，没有改 `specs/`，没有读写 `.codex`
- [ ] 测试不联网；新增的两条用例不 `Show()` 窗口、不改 `Application` 级资源（主写法纯读取；退路只改局部窗口的 `Resources`）
- [ ] 先红后绿都实际发生过：Step 3（2 红）、Step 6（1 红）、Step 8（全绿）的汇总行都记进了任务记录
- [ ] Step 9 的回归过滤集与「设置全集」都是 0 失败，通过数来自实际输出
- [ ] 只 `git add`，没有提交、没有 push / reset / clean / rebase / amend / stash
- [ ] 没有启动应用，没有运行 `src/Iris.App/bin` 下的 exe

要带进完成报告的记录（Task 14 汇总）：

- [ ] **改过的既有测试：无。** `AcrylicWindowTests.cs` 是新增两条用例与一个辅助方法，既有三条没动
- [ ] **偏差清单**（见文末一节的第 1 条；若启用了 Step 2 的退路代码，再加一条「断言方式由读本地值改为哨兵画刷」及原因）
- [ ] 产出会变但不比对基线的证据图：`HistoryRenderingTests`、`OcrRenderingTests`、`LocalizationRenderingTests` 的 PNG 与清单里的 SHA256
- [ ] 四项「未验证，待 Task 14」的真机看点（Step 10(c)）
- [ ] 范围说明：按用户裁决 U5，本阶段多出两个窗口文件，属范围扩大而非偏差（S0-6 已写明）

---

#### ⚠ 与裁决不符之处 / 需协调方裁决

没有与用户裁决 U5、设计裁决 S0-6 相冲突的地方。下面几条是照裁决做时偏离了字面写法、或需要协调方知情 / 定夺的：

**协调方的处理（2026-11-23）：** 第 1 条——接受（裁决 C9）：外包一层只带 `Background` 的 `Grid` 属于 S0-6 所说的等价做法，不算违反 U5；照下文记入偏差清单。第 3 条——接受：四个看点已写进 Task 14 的真机截图清单。第 2、4 条——知情，按撰写者的做法办。第 5 条——完成报告里写「未验证」。第 6 条——只在完成报告里提一句，本阶段不改那段注释。

1. **翻译窗口不是「根面板一行 `Background`」，而是多包了一层 `Grid`（记入偏差清单）。** Task 4B 提纲写「各一处」，S0-6 写「根面板一行 `Background`，或等价做法」。翻译窗口的根 `DockPanel` 带 `Margin="12,8,12,12"`，直接加会留一圈未着色的边，所以用了「等价做法」：插 2 行、可视树多一个只带 `Background` 的 `Grid`，`window.Content` 的类型从 `DockPanel` 变成 `Grid`。没有改动任何既有控件、名字与布局属性，撰写者判断不违反 U5 的「不改任何名字与控件」；但「多了一个面板元素」是否算在 U5 允许的范围内，**请协调方确认一句**。若不允许，备选只有把 `Margin` 换成外层 `Border` 的 `Padding`（改两处既有属性，更明显地属于改版面），撰写者不建议。
2. **Task 4B 写的「既有像素断言：先按 S0-6 的颜色手算预期值，再跑」在这两个窗口上落空。** 调查笔记逐条核对的结论是：涉及这两个窗口的既有测试没有任何像素 / 颜色 / 亮度断言。本文件用 Step 10 的取样核对（「改前 / 改后」两套证据图 + 不依赖估算值的判据）顶替这一条；E5 的手算值只作参考值列出。不需要裁决，知情即可。
3. **Task 14 的截图清单需要补四个具体看点**（Step 10(c)）：翻译窗口四角有无着色溢出、原 `Margin` 一圈是否同色、头部拖动与四边缩放是否如常、三窗底色并排是否一致。计划 Task 14 现在只写了「三个亚克力窗口都要看着色」，离屏渲染验证不了前三项。**建议协调方把这四项写进 Task 14 的清单**；本文件无权改 Task 14。
4. **提纲是 5 步，本文件展开成 12 步，其中 Step 4「记回归基线并留改前证据图」是提纲里没有的。** 加它是因为流程约束第 7 条要求完成报告里的数字都来自实际输出，而 Step 9 的「通过数 +2」与 Step 10 的前后对比都需要一个改前的基准。只多跑一次测试，不扩大改动范围。
5. **不支持亚克力的系统上的观感未验证。** 那时着色键由回退字典换成不透明的 `#FFF3F3F3` / `#FF242424`（S0-7），这两个窗口的底色会从 WPF-UI 的窗口底（调查笔记从包里读到的值是 `#FAFAFA` / `#202020`）变成这两个值。这是 S0-7 的既定行为，不是本任务新引入的决定；本机（Windows 11 26200）支持亚克力，走不到这条路径，完成报告里应写「未验证」。
6. **顺带发现、不属于本任务、本任务不改：** `src/Iris.App/History/HistoryWindowSession.cs:28–29` 的注释说构造这个会话的测试不注册材质服务，而调查笔记查到 `HistoryTestScope` 走完整的 `AppHostBuilder.Build`，服务是在的（撰写时未再读源文件核对）。Task 4B 明令不改代码隐藏与会话，只在完成报告里提一句。

未核实、已在步骤里写了分支处理的假设（汇总，便于协调方评估风险）：

- `ReadLocalValue` → `ResourceReferenceExpressionConverter` → `DynamicResourceExtension` 这条读取路径只在 .NET Framework 的 WPF 上实测过（调查笔记 E6.2），.NET 10 下未实测 → Step 6 的表给了换用哨兵画刷的条件。
- Step 2 的测试代码未编译（Shouldly 4.3.0 各断言的返回类型是否可空、是否触发代码样式诊断）→ Step 3 的表给了逐类处理办法。
- `dotnet test … --filter "FullyQualifiedName~…"` 在 Microsoft.Testing.Platform 下是否可用，沿用计划 Task 1 Step 1(c) 的结论与退路。
- Step 10 的参考像素值全部是估算；历史窗口在测试里是否真的挂上了亚克力（窗口底是否透明）未实跑确认 → 判据写成了不依赖这些数值的「前后是否变化、各点是否同色」。
- 证据图文件名中 `localization` 目录下的几张是按命名规则推出的 → Step 10(b) 给了列出实际文件名的命令。

**验证命令：** `FullyQualifiedName~AcrylicWindowTests|FullyQualifiedName~HistoryRenderingTests|FullyQualifiedName~HistoryWindowTests|FullyQualifiedName~HistoryLifecycleTests|FullyQualifiedName~OcrRenderingTests|FullyQualifiedName~TranslateWindowUiTests|FullyQualifiedName~TranslateWindowIntegrationTests|FullyQualifiedName~TranslateWindowPresenterTests|FullyQualifiedName~TranslateWindowOperationTests|FullyQualifiedName~LocalizationRenderingTests|FullyQualifiedName~IrisColorKeyTests|FullyQualifiedName~IrisThemeDictionaryTests|FullyQualifiedName~OcrEntrypointTests|FullyQualifiedName~OcrLifecycleTests|FullyQualifiedName~OcrPublicationTests|FullyQualifiedName~TranslationEndToEndTests|FullyQualifiedName~TranslationWorkflowLateArrivalTests` —— 预期全绿；再跑「设置全集」，预期全绿。

**要改的既有测试：** 无。（涉及这两个窗口的既有断言只有几何 / 流程 / 资源画刷断言，没有像素断言；三组证据图的 PNG 与 SHA256 会变，但不比对基线。）

### Task 5: 历史页与截图页（行卡片试点）

**目的：** 用最小的两页把 S0-4（横向行）、S0-5（开关）、U2（行图标）的写法走通，后面各页照这个样子做。

**Files:**
- Create: `tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs`
- Modify: `src/Iris.App/Settings/SettingsWindow.xaml`——截图页 :287–319、历史页 :168–179（先改靠后的截图页）

**范围与要点：**
- 历史页：`HistoryEnabledCheckBox`（:172）→ 横向行加开关；`HistoryMaxEntriesTextBox`（:175）→ 横向行、`Width="120"`，错误文字（:177）进该行第 1 列；两段说明原样保留；可编辑容器（:170）照旧包住这两行。
- 截图页：四个复选框（:302 / :305 / :308 / :311）→ 四张横向行加开关；`ScreenshotMaskOpacityTextBox`（:315）→ 横向行，`ScreenshotError`（:316）进该行第 1 列；页标题下的页级说明 `UiText.ScreenshotSettingsDescription`（:300）按 C7 保留在页标题下面、可编辑容器之外（`IrisRowDescription` + `IrisCardGap`）；各行原本没有逐行说明，不新编；可编辑容器（:301）照旧。
- 图标：**逐行选**（C6），见详细步骤的「行 → 首选图标 → 退路图标」表（规矩与起点见 S0-4「图标」）；本任务两页共七张行卡片（历史 2、截图 5），表里一行不漏。退路为页面图标：历史页 `History20`、截图页 `Screenshot20`。S0-4 的表已为「遮罩不透明度」选了 `Options20`，照抄作该行首选。
- 页标题用 `IrisPageTitle`，页内边距用 `IrisPagePadding`。
- 保持：`HistoryPageScroll`、`ScreenshotPageScroll` 是 `ScrollViewer`；两个文本框是 `TextBox`、名字不变（`FlushFocusedBinding` 按名字找，cs:308–314）；所有绑定表达式原样搬。
- 不做：不动其他页；不删 `WrappingCheckBoxContent`（还有别的页在用）。
- 风险：R1、R4、R6、R7、R15。开工前做 2.13 第 18 条的 grep。

**步骤提纲：**
- [ ] 写 `SettingsRowCardLayoutTests`（按页面键的 `Theory`，本任务加 `history`、`screenshot` 两行）：页内每个 `CheckBox` 用的是开关样式且 `AutomationProperties.Name` 非空；页内没有 `ui:Card`；每张行卡片第 0 列有图标；原有说明文字都还在；680 与 600 宽下页内可见元素右边缘不超出内容区
- [ ] 跑，确认失败
- [ ] 改截图页，再改历史页
- [ ] 跑本任务过滤组与「设置全集」；`git add`

**详细步骤：**

> **声明：本文件里的所有代码（C# 测试、XAML）都没有编译过、没有运行过。** 撰写时只做了只读核实（grep、小段 Read、对 NuGet 缓存里的 `Wpf.Ui.dll` 做只读反射）。对测试「会红 / 会绿」的预判全部是读代码推算。行号对应提交 `dc08c48`；任务 4 会改 `SettingsWindow.xaml` 靠前的部分，这两页的行号会漂移，执行时一律按 Step 1 的 grep 重新定位。

#### 已读到的事实（撰写时核实，执行时按 Step 1 复核）

**两页的现状（`src/Iris.App/Settings/SettingsWindow.xaml`，1143 行，CRLF 换行，UTF-8）**

| 页 | 行 | 内容 |
|---|---|---|
| 历史 | :168 | `<ScrollViewer x:Name="HistoryPageScroll" Grid.Column="1" Padding="20,12" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">` |
| | :169 | 单行写成的 `ScrollViewer.Style`（`CurrentPageKey` = `history` 时可见）——不动 |
| | :170 | `<StackPanel IsEnabled="{Binding CanEdit}">`——**页标题也在这个容器里** |
| | :171 | 页标题 `UiText.HistoryTitle`，`FontSize="22"`、`Margin="0,0,0,20"` |
| | :172 | `HistoryEnabledCheckBox`：`ContentTemplate="{StaticResource WrappingCheckBoxContent}"`、`HorizontalContentAlignment="Stretch"`、`Content="{x:Static app:UiText.HistoryEnabled}"`、`IsChecked="{Binding History.Enabled.Value}"`（没写 `Mode`） |
| | :173 | 说明 `UiText.HistoryEnabledHelp` |
| | :174 | 标签 `UiText.HistoryMaxLabel` |
| | :175 | `HistoryMaxEntriesTextBox`：`Text="{Binding History.MaxEntriesText.Value, UpdateSourceTrigger=PropertyChanged}"` |
| | :176 | 说明 `UiText.HistoryMaxHelp` |
| | :177 | 错误文字（**没有名字**）：`Text="{Binding History.ErrorMessage}"`、`Foreground="{DynamicResource SystemFillColorCriticalBrush}"`、`Margin="0,12,0,0"` |
| | :178–179 | `</StackPanel>`、`</ScrollViewer>` |
| 截图 | :287 | `<ScrollViewer x:Name="ScreenshotPageScroll" …>`（属性与 :168 相同，只是名字不同） |
| | :288–297 | 多行写成的 `ScrollViewer.Style`（`screenshot`）——不动 |
| | :298 | 外层 `<StackPanel>`（**没有** `IsEnabled`） |
| | :299 | 页标题 `UiText.ScreenshotPageTitle`，`FontSize="22"` |
| | :300 | **页级说明** `UiText.ScreenshotSettingsDescription`（次要色，`Margin="0,6,0,18"`）——在可编辑容器**之外** |
| | :301 | `<StackPanel IsEnabled="{Binding CanEdit}">` |
| | :302–313 | 四个 `CheckBox`（子元素写法：里面一个 `TextBlock`），`Margin="0,5,0,5"`，绑定都带 `Mode=TwoWay`：`ScreenshotConfirmCheck`→`Screenshot.ConfirmOnMouseUp.Value` / `UiText.ScreenshotConfirmOnMouseUp`；`ScreenshotMagnifierCheck`→`Screenshot.ShowMagnifier.Value` / `UiText.ScreenshotShowMagnifier`；`ScreenshotHideCheck`→`Screenshot.HideIrisWindows.Value` / `UiText.ScreenshotHideWindows`；`ScreenshotCopyCheck`→`Screenshot.CopyImageToClipboard.Value` / `UiText.ScreenshotCopyImage` |
| | :314 | 标签 `UiText.ScreenshotOpacity` |
| | :315 | `ScreenshotMaskOpacityTextBox`：`Text="{Binding Screenshot.MaskOpacityText.Value, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"` |
| | :316 | `ScreenshotError`（有名字）：`Text="{Binding Screenshot.ErrorMessage}"`、`Foreground="{DynamicResource SystemFillColorCautionBrush}"` |
| | :317–319 | 两个 `</StackPanel>`、`</ScrollViewer>`；:320 是一条**中文注释**（属于常规页，不许碰） |

- **文字来源**：两页的全部文字都是 `{x:Static app:UiText.…}`，没有 XAML 字面量；两页的行范围里**没有任何非 ASCII 字符**（`LC_ALL=C grep` 计数为 0），所以本任务不涉及中文注释。十二个 `UiText` 成员都已存在（`UiText.History.cs:5,51–54`、`UiText.cs:8,64–68,70`）。
- 根元素的命名空间（:4–9）：默认、`x`、`ui`（`http://schemas.lepo.co/wpfui/2022/xaml`）、`app`（`clr-namespace:Iris.App`）、`settings`、`local`。本任务不用加新的 `xmlns`。
- `WrappingCheckBoxContent` 现有 12 处使用（`grep -c 'StaticResource WrappingCheckBoxContent'`）；本任务清掉 1 处（`HistoryEnabledCheckBox`），资源定义（:17）不删。
- 这两页现在**没有** `ui:Card`（13 个 `ui:Card` 都在别的页）。

**代码隐藏**（`SettingsWindow.xaml.cs`）：这两页的十个名字里只有两个被引用——`FlushFocusedBinding()` 里的 `FlushSelectionText(ScreenshotMaskOpacityTextBox);`（cs:311）与 `FlushSelectionText(HistoryMaxEntriesTextBox);`（cs:312）。两者是 `x:Name` 生成的字段，必须留在窗口名称域里、类型是原生 `TextBox`。

**主题层的键**（`src/Iris.App/Themes/Iris.Styles.xaml`）

| 键 | 出处 | 值 |
|---|---|---|
| `IrisRowCard`（`Border`） | 阶段 1，:36–39 | `BasedOn IrisCard`；`Padding` = `IrisRowPadding`（16,12）；`Margin` = `IrisRowGap`（0,0,0,8）；描边 1 |
| `IrisRowTitle` / `IrisRowDescription`（`TextBlock`） | 阶段 1，:41–46 / :48–53 | 14 SemiBold 可换行主色 / 12 可换行次要色、`Margin` 0,3,0,0 |
| `IrisControlGap` / `IrisCardGap`（`Thickness`） | 阶段 1，:22 / :21 | 12,0,0,0 / 0,0,0,12 |
| `IrisSwitchCheckBox`（`CheckBox`） | 阶段 1，:81–198 | 开关 40×20 在左；`Content` 为 `null` 或空串时内容边距收起（:113–118） |
| `IrisPagePadding`（`Thickness`） | 任务 2 Step 5 | 24,16,24,16 |
| `IrisPageTitle`（`TextBlock`） | 任务 2 Step 6 | 20 SemiBold 主色，`Margin` = `IrisCardGap`，单行加省略号 |
| `IrisRowIcon`（`ui:SymbolIcon`） | 任务 2 Step 6 | `FontSize` 20、`Margin` 0,0,12,0、垂直居中、次要色；只能套在**元素写法**的图标上 |
| `IrisRowError`（`TextBlock`） | 任务 2（协调方裁决 C5，2026-11-19 追加） | `BasedOn IrisRowDescription`；一个触发器：`Text=""` → `Visibility=Collapsed`；**不设 `Foreground`**（各行原来的画刷留在元素上） |

**图标**（协调方裁决 C6：逐行选）。两种核实都做了：

- 应用实际引用的那一份 `C:\Users\28910\.nuget\packages\wpf-ui\4.3.0\lib\net10.0-windows7.0\Wpf.Ui.dll`：把文件按 NUL 切开后整行精确匹配（`tr '\0' '\n' < Wpf.Ui.dll | grep -a -x -c '成员名'`）。`TextBulletListAdd20`、`NumberSymbol20`、`CursorClick20`、`ZoomIn20`、`EyeOff20`、`ClipboardImage20`、`Options20`、`Screenshot20` 八个都**精确命中 1 次**。`History20` 精确命中 0 次、子串命中 1 次（只作为 `ChatHistory20` 的后缀出现——元数据字符串堆会把一个名字并进以它结尾的更长名字里，所以单靠字符串检索证明不了它独立存在；撰写时因此没有选 `Record20`、`Save20`、`Database20`、`Archive20` 这几个同样只以后缀出现的名字作首选）。
- `lib\net462\Wpf.Ui.dll` 的只读反射（`[Enum]::GetNames(SymbolRegular)`）：上面九个名字加 `Empty` 全部存在。`History20` 只在这一份上有确证；它同时是任务 4 的导航图标，任务 4 能编译即证明它在 net10 那一份里也存在。

另外同一次反射：`SymbolIcon` 有 `Symbol`（`SymbolRegular`）、`Filled`、`FontSize` 三个属性；`ui:Card` 的基类是 `ContentControl`。

**既有测试里碰这两页的地方**（`tests/Iris.App.Tests/`；全仓库 grep 了十个控件名与十二个 `UiText` 键）

| 位置 | 做什么 | 本任务之后 |
|---|---|---|
| `Settings/HistorySettingsUiTests.cs:17` | `(ScrollViewer)FindName("HistoryPageScroll")` 可见 | 不受影响（名字、类型不变） |
| `Settings/HistorySettingsUiTests.cs:33–36` | `(TextBox)FindName("HistoryMaxEntriesTextBox")`，改绑成 `Explicit` 后设值、保存 | 不受影响（靠 cs:312 的按名字冲刷） |
| `Settings/OcrSettingsUiTests.cs:29` | `(ScrollViewer)FindName("ScreenshotPageScroll")` 可见 | 不受影响 |
| `Settings/OcrSettingsUiTests.cs:46`、`:64–68` | `(TextBox)FindName("ScreenshotMaskOpacityTextBox")` 设值 / 改绑 | 不受影响 |
| `Settings/OcrSettingsRenderingTests.cs:45–50`、`:60–61` | 切到 `screenshot` 页出图；`:47` 把 `ScreenshotPageScroll` 滚到底 | 不受影响；`Render`（:102–106）要求每个可见 `TextBox` 右边缘 ≤ 窗口宽——`Width="120"` 满足 |
| `History/HistoryRenderingTests.cs:69–79` | 切到 `history` 页出 8 张图，无断言 | 不受影响 |
| `Localization/LocalizationRenderingTests.cs:68–75` | 八页逐页滚动出图，每页最多 24 张（:234） | 不受影响（截图页变高，仍远小于 24 屏） |

- **按 `Content` 文字找 `CheckBox` 的测试：没有。** `grep -rn "CheckBox" tests --include=*.cs`（排除 `Themes/`）只有三处：`History/HistoryWindowTests.cs:79`（历史**窗口**的 `KeepFavoritesCheckBox`，与设置窗口无关）、`Updates/UpdatePreferenceUiTests.cs:74` 与 `Updates/UpdateRenderingTests.cs:89`（常规页的 `CheckForUpdatesCheckBox`，按名字找，属于任务 6）。按 `Content` 找控件的既有断言全部针对 `Button`（`UiText.SettingsSave` 等，D7-1），不涉及这两页。所以 2.13 第 18 条对本任务的结论是：**没有要改的既有测试**。
- 全 `tests/` 没有对设置窗口的 `AutomationProperties` 断言，也没有读 XAML 源文件文本的测试（D7-4）。
- 夹具：`Settings/OcrSettingsUiScope.cs`——`Run(Func<OcrSettingsUiScope, Task> body, AppSettings? initial = null)`（:63），`f.Show(string page = "ocr")`（:60：设页面键、`Show()`、`UpdateLayout()`），`f.Window`、`f.Vm`。窗口摆在屏幕外（:54–55）。`HistorySettingsUiTests` 与 `OcrSettingsUiTests` 都挂 `[Collection("AppShutdownerSharedState")]`。
- 校验错误怎么触发（既有测试的写法）：`f.Vm.History.MaxEntriesText.Value = "…"; await f.Vm.ExecuteSaveAsync();`（`HistoryRenderingTests.cs:76`）→ `History.ErrorMessage` = `UiText.HistoryMaxError`；`f.Vm.Screenshot.MaskOpacityText.Value = "NaN"; await f.Vm.ExecuteSaveAsync();`（`OcrSettingsRenderingTests.cs:60–61`）→ `Screenshot.ErrorMessage` = `UiText.ScreenshotOpacityError`。两个 `ErrorMessage` 都是 `string?`，无错时为 `null`，并各自发 `PropertyChanged`。

**本任务不新增任何界面文字**（S0-4「一条不删、一条不新编」、S0-12「除四条以外不新增任何字符串」）：`Strings.resx`、`Strings.en-US.resx`、`build/localization/app-catalog.json`、`UiText*.cs` 四处都**不改**。截图页的四个开关行与不透明度行只有标题、没有说明。

#### 行卡片 XAML 范式（任务 6–9 照抄这一节）

只有两种行形态（S0-4）。两种都是：`Border`（`IrisRowCard`）→ `Grid`（三列 `Auto | * | Auto`）→ 第 0 列图标、第 1 列文字栈、第 2 列控件。逐行手写，不进 `DataTemplate` / `ControlTemplate` / `UserControl`（R1）。缩进按所在位置调整，其余照抄。

##### 形态 A：横排（开关、下拉框、数字文本框、单行短文本框）

```xml
<Border Style="{StaticResource IrisRowCard}">
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="Auto"/>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="Auto"/>
        </Grid.ColumnDefinitions>
        <ui:SymbolIcon Grid.Column="0" Symbol="TextBulletListAdd20" Style="{StaticResource IrisRowIcon}"/>
        <StackPanel Grid.Column="1" VerticalAlignment="Center">
            <TextBlock Style="{StaticResource IrisRowTitle}" Text="{x:Static app:UiText.HistoryEnabled}"/>
            <TextBlock Style="{StaticResource IrisRowDescription}" Text="{x:Static app:UiText.HistoryEnabledHelp}"/>
        </StackPanel>
        <CheckBox x:Name="HistoryEnabledCheckBox" Grid.Column="2" Style="{StaticResource IrisSwitchCheckBox}" Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center" IsChecked="{Binding History.Enabled.Value}" AutomationProperties.Name="{x:Static app:UiText.HistoryEnabled}"/>
    </Grid>
</Border>
```

- **图标逐行选，见各任务的图标表**（协调方裁决 C6）：每行一个语义贴切的 `SymbolRegular` 成员（名字以 `20` 结尾），不是整页同一个；各任务的步骤里给「行 → 首选 → 退路」表，退路 = 该页的页面图标（S0-2 的导航图标）。首选必须先在 `lib\net10.0-windows7.0\Wpf.Ui.dll` 里核实存在（方法见上文「已读到的事实 · 图标」），并登记到 `SettingsRowCardLayoutTests.ExpectedIcons()`。
- 没有说明的行：删掉 `IrisRowDescription` 那一个 `TextBlock`，文字栈里只剩标题；**不新编说明**。
- 第 2 列控件的宽度（S0-4）：开关不设宽；下拉框 `MinWidth="160"`；数字文本框 `Width="120"`；单行短文本框 `Width="220"`。
- 本行的校验错误文字放进第 1 列文字栈的**最后**，就一行，套主题层的 `IrisRowError`（协调方裁决 C5；任务 2 定义）。把绑定与 `Foreground` 换成原来那条的；原来有 `x:Name` 的把名字写回这个 `TextBlock` 上；原来的本地 `Margin`、`TextWrapping` 删掉（样式里有）：

```xml
<TextBlock Style="{StaticResource IrisRowError}" Text="{Binding History.ErrorMessage}" Foreground="{DynamicResource SystemFillColorCriticalBrush}"/>
```

  `IrisRowError` 在 `Text` 为空串时折叠，没有错误的行不会多出一段空白。**不要**在行里再写内联 `TextBlock.Style`。「`Text` 绑到 `null` 时等于空串」是按 WPF 的行为推断的，没有运行验证：若测试显示无错时它没有折叠，退路改在**任务 2 的 `IrisRowError` 样式里**加一个 `<Trigger Property="Text" Value="{x:Null}">` → `Collapsed`，各页的 XAML 不动。

##### 形态 B：竖排（多行文本框、快捷键录制框、网址类长文本、一行里两个以上控件）

```xml
<Border Style="{StaticResource IrisRowCard}">
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="Auto"/>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="Auto"/>
        </Grid.ColumnDefinitions>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        <ui:SymbolIcon Grid.Row="0" Grid.Column="0" Symbol="Options20" Style="{StaticResource IrisRowIcon}"/>
        <StackPanel Grid.Row="0" Grid.Column="1" VerticalAlignment="Center">
            <TextBlock Style="{StaticResource IrisRowTitle}" Text="{x:Static app:UiText.ScreenshotOpacity}"/>
        </StackPanel>
        <TextBox x:Name="ScreenshotMaskOpacityTextBox" Grid.Row="1" Grid.Column="0" Grid.ColumnSpan="3" Margin="0,8,0,0" Text="{Binding Screenshot.MaskOpacityText.Value, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"/>
    </Grid>
</Border>
```

（上面只是骨架示例；本任务的两页**没有**竖排行，`ScreenshotMaskOpacityTextBox` 实际用形态 A。）

- 第 0 行与形态 A 完全相同；第 0 行第 2 列可以空着，也可以放一个开关（写法同形态 A 的 `CheckBox`，加 `Grid.Row="0"`）。
- 第 1 行的控件：`Grid.Row="1" Grid.Column="0" Grid.ColumnSpan="3" Margin="0,8,0,0"`，不设 `Width`（跨满卡片内宽）；原来的 `Height`、`AcceptsReturn`、`MinWidth`、`MaxWidth`、`HorizontalAlignment` 原样保留。
- 第 1 行要放两个以上控件时，外面包一个 `Grid` 或 `WrapPanel`（带上面那组 `Grid.Row` / `Grid.ColumnSpan` / `Margin`），**不要用 `StackPanel`**——`SettingsRowCardLayoutTests` 把「第 0 行第 1 列的那个 `StackPanel`」当作文字栈来找，别的位置出现 `StackPanel` 不会让它红，但会让人读错结构。
- 校验错误文字仍放第 0 行文字栈的最后（同形态 A）。

##### 搬控件时的规矩（两种形态通用）

| | 属性 |
|---|---|
| **必须原样留在原控件上** | `x:Name`；全部绑定表达式（`Mode`、`UpdateSourceTrigger`、转换器、`RelativeSource` 一字不改）；控件自己的 `IsEnabled`（含绑定）；`Visibility`（含绑定）；事件处理（`PasswordChanged`、`Click`、`SelectionChanged` 等）；`AutomationProperties.*`；`ToolTip`；`ItemsSource`、`SelectedValue`、`SelectedValuePath`、`DisplayMemberPath`、`ItemTemplate`；`MaxLength`、`AcceptsReturn`、`TextWrapping`、`Height`、`MinWidth`、`MaxWidth`；`Command` / `CommandParameter`；`Tag` |
| **删掉** | 原控件上只为旧版面服务的本地 `Margin`；`CheckBox` 的 `Content`、`ContentTemplate="{StaticResource WrappingCheckBoxContent}"`、`HorizontalContentAlignment="Stretch"` 与写在里面的子 `TextBlock`（文字用**同一条** `UiText` 搬到行标题） |
| **新加** | `Grid.Column="2"`（竖排时是 `Grid.Row="1"` 那一组）；`Margin="{StaticResource IrisControlGap}"` 与 `VerticalAlignment="Center"`（仅第 2 列的控件）；开关加 `Style="{StaticResource IrisSwitchCheckBox}"` 与 `AutomationProperties.Name`（值 = 本行标题的同一条 `UiText`）；S0-4 规定的宽度 |

- **控件类型不换**：`CheckBox` 仍是 `CheckBox`，`TextBox` 仍是原生 `TextBox`，`PasswordBox` 仍是原生 `PasswordBox`，`ComboBox` 仍是 `ComboBox`（2.9(a)）。
- 非开关控件原来没有 `AutomationProperties.Name` 的**不新增**（不扩范围）；原来有的原样保留。
- 图标必须是元素写法 `<ui:SymbolIcon Symbol="…" Style="{StaticResource IrisRowIcon}"/>`，并且是行 `Grid` 的**直接子元素**、`Grid.Column="0"`。任务 2 若走了 Step 7 表里「`FontSize` 盖不过本地值」的退路（偏差清单里会有一笔），每个图标再补 `FontSize="20"`。
- 整行原来按条件显示的：把条件挪到行卡片的 `Border` 上。`Border` 只能有一个 `Style`，所以写成内联 `<Style TargetType="Border" BasedOn="{StaticResource IrisRowCard}">` 再带原来的触发器（R5）；这时测试里「`Style` 就是 `IrisRowCard` 那个实例」的判断不成立，该页的任务要把这一行单独处理并写明。
- 页的外壳：`ScrollViewer` 保留名字与可见性样式，删掉它自己的 `Padding`；第一层面板加 `Margin="{StaticResource IrisPagePadding}"`；页标题 `TextBlock` 用 `Style="{StaticResource IrisPageTitle}"`（删掉本地 `FontSize` / `FontWeight` / `Margin` / `TextWrapping`）；紧跟页标题的页级说明留在页标题下面、可编辑容器之外，用 `Style="{StaticResource IrisRowDescription}" Margin="{StaticResource IrisCardGap}"`（协调方裁决 C7，不新增键）。
- `IsEnabled="{Binding CanEdit}"` 的容器：原来包住哪些行，改完还包住哪些行；原来在容器外的仍在容器外（R4）。
- 每加一页，在 `SettingsRowCardLayoutTests` 里加五处：`Page()` 的页面键 → `ScrollViewer` 名字、`ExpectedTexts()` 的文字清单、`ExpectedIcons()` 的逐行图标、各条 `Theory` 的 `InlineData`、`NamedControls_…` 的表。

#### 详细步骤

（测试命令沿用 5.0 的前缀 `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "<过滤式>"`。任务 1 Step 1(c) 若把可用写法回填成了别的形式，本任务所有命令照回填后的写法换。）

- [ ] **Step 1: 重新定位两页的行号，核实前置条件**

(a) 行号（任务 4 改过导航与列定义，行号一定漂了）：

```
grep -n 'x:Name="HistoryPageScroll"\|x:Name="OcrPageScroll"\|x:Name="ScreenshotPageScroll"\|x:Name="GeneralPageScroll"' src/Iris.App/Settings/SettingsWindow.xaml
```

期望 4 行。记下：历史页 = `HistoryPageScroll` 那一行到 `OcrPageScroll` 的上一行；截图页 = `ScreenshotPageScroll` 那一行到 `GeneralPageScroll` 往上数第 2 行（`GeneralPageScroll` 的上一行是一条中文注释，属于常规页，**不在本任务范围内，不许碰**）。在 `dc08c48` 上这两个范围是 :168–179 与 :287–319，各 12 行与 33 行；行数不是 12 / 33 说明前面的任务动过这两页，停下来报告。

(b) 两页内没有中文、没有 `ui:Card`（把 `A,B` 换成 (a) 得到的两个范围，各跑一次）：

```
sed -n 'A,Bp' src/Iris.App/Settings/SettingsWindow.xaml | LC_ALL=C grep -c $'[\x80-\xff]'
sed -n 'A,Bp' src/Iris.App/Settings/SettingsWindow.xaml | grep -c '<ui:Card'
```

期望四个数都是 0。

(c) 任务 2 的四个键与阶段 1 的六个键都在：

```
grep -n 'x:Key="IrisPagePadding"\|x:Key="IrisPageTitle"\|x:Key="IrisRowIcon"\|x:Key="IrisRowError"\|x:Key="IrisRowCard"\|x:Key="IrisRowTitle"\|x:Key="IrisRowDescription"\|x:Key="IrisSwitchCheckBox"\|x:Key="IrisControlGap"\|x:Key="IrisCardGap"' src/Iris.App/Themes/Iris.Styles.xaml
```

期望 10 行。少任何一个：任务 2 没做完，停下来报告——尤其是 `IrisRowError`（协调方裁决 C5 于 2026-11-19 追加给任务 2 的键）：没有它就**不要**退回去写内联样式，先让任务 2 补上。同时看一眼 `IrisRowIcon` 里有没有 `FontSize` 那一行 `Setter`：没有，说明任务 2 走了退路——本任务每个 `ui:SymbolIcon` 上都要补 `FontSize="20"`（Step 4、Step 5 的 XAML 里共 7 处）。

(d) 2.13 第 18 条的 grep（撰写时已做，结论见上文「已读到的事实」；执行时复核）：

```
grep -rn "CheckBox" tests --include=*.cs | grep -v "/Themes/"
grep -rn "HistoryEnabled\b\|ScreenshotConfirmOnMouseUp\|ScreenshotShowMagnifier\|ScreenshotHideWindows\|ScreenshotCopyImage" tests --include=*.cs
```

期望：第一条只有三行（`History/HistoryWindowTests.cs` 的 `KeepFavoritesCheckBox`、`Updates/UpdatePreferenceUiTests.cs` 与 `Updates/UpdateRenderingTests.cs` 的 `CheckForUpdatesCheckBox`），第二条 0 行。符合 → 本任务没有要改的既有测试。不符合（前面的任务新增了按 `Content` 文字找这两页开关的断言）→ 把那条断言改成按 `x:Name` 找（`(CheckBox)window.FindName("…")`），并把「文件、用例名、改了什么、为什么」**记入偏差清单**的「改过的既有测试」。

(e) 本任务的图标表（协调方裁决 C6：逐行选；退路 = 该页的页面图标）。「首选」列的七个名字撰写时都已核实存在（net10 那一份程序集里精确命中，net462 那一份反射确认，见「已读到的事实 · 图标」）：

| 页 | 行（标题的 `UiText` 键；括号里是 `Strings.en-US.resx` 里的英文值） | 首选 | 为什么 | 退路 |
|---|---|---|---|---|
| 历史 | `HistoryEnabled`（Record future translations） | `TextBulletListAdd20` | 往列表里添一条 | `History20` |
| 历史 | `HistoryMaxLabel`（Maximum records excluding favorites; …） | `NumberSymbol20` | 条数上限是个数字 | `History20` |
| 截图 | `ScreenshotConfirmOnMouseUp`（Confirm selection when the mouse is released） | `CursorClick20` | 鼠标点按 / 松开 | `Screenshot20` |
| 截图 | `ScreenshotShowMagnifier`（Show magnifier） | `ZoomIn20` | 放大镜 | `Screenshot20` |
| 截图 | `ScreenshotHideWindows`（Hide Iris windows before capture） | `EyeOff20` | 隐藏 | `Screenshot20` |
| 截图 | `ScreenshotCopyImage`（Copy screenshot to clipboard on confirmation） | `ClipboardImage20` | 剪贴板里的图片 | `Screenshot20` |
| 截图 | `ScreenshotOpacity`（Mask opacity (0–0.9)） | `Options20` | S0-4 的表原定 | `Screenshot20` |

七个首选在本任务内互不重复，也都不与八个导航图标（S0-2 的表）重名；其中 `CursorClick20` 是划词页的导航图标、`Options20` 在 S0-4 的表里还分给了划词页的两行——任务 7 选图标时留意别让同一页里出现两个一样的。

执行时复核（期望七个数都是 1；Git Bash 下跑）：

```
D="$USERPROFILE/.nuget/packages/wpf-ui/4.3.0/lib/net10.0-windows7.0/Wpf.Ui.dll"
for m in TextBulletListAdd20 NumberSymbol20 CursorClick20 ZoomIn20 EyeOff20 ClipboardImage20 Options20; do echo "$m $(tr '\0' '\n' < "$D" | grep -a -x -c "$m")"; done
```

某个首选是 0，或 Step 4 / Step 5 构建时报「不是 `SymbolRegular` 的成员」：那一行改用退路列的图标，同时把 Step 2 测试里 `ExpectedIcons()` 的对应项改成退路，并**记入偏差清单**。两个退路（`History20`、`Screenshot20`）就是任务 4 的导航图标，任务 4 能编译即说明存在。

(f) 记下改之前的名字清单，Step 6 用来比对：

```
grep -o 'x:Name="[^"]*"' src/Iris.App/Settings/SettingsWindow.xaml | sort > "$TEMP/t041-task5-names-before.txt"
wc -l < "$TEMP/t041-task5-names-before.txt"
```

（在 `dc08c48` 上是 89；任务 4 之后会多出它补的那几个名字。记下实际数字。）

- [ ] **Step 2: 写 `SettingsRowCardLayoutTests`（新文件）**

Create `tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs`，整文件就是下面三段代码按顺序拼起来。共 13 条用例：2 + 2 + 2 + 4 + 1 + 2。夹具用 `OcrSettingsUiScope`（写法照 `HistorySettingsUiTests.cs:7,11,15–17`）。测试体里找元素一律走可视树（任务 9 的行在 `DataTemplate` 里，逻辑树够不着），往上找祖先走 `FrameworkElement.Parent`。`Wpf.Ui.Controls` 命名空间里有自己的 `TextBlock` 与 `Button`，所以**不要** `using Wpf.Ui.Controls;`，用到的两个类型写全名。

第一段（文件头、行卡片结构、开关）：

```csharp
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Shouldly;
using static Iris.App.Tests.Settings.OcrSettingsUiScope;

namespace Iris.App.Tests.Settings;

// 行卡片版面的守卫（T-041 阶段 2）。任务 5 登记 history、screenshot 两页；
// 任务 6–9 各自把自己的页加进 Page()、ExpectedTexts() 与各条 Theory 的 InlineData。
[Collection("AppShutdownerSharedState")]
public sealed class SettingsRowCardLayoutTests
{
    [Theory]
    [InlineData("history", 2)]
    [InlineData("screenshot", 5)]
    public Task Page_IsBuiltFromIconRowCards(string pageKey, int expectedRows) => Run(f =>
    {
        f.Show(pageKey);
        var page = Page(f, pageKey);
        page.IsVisible.ShouldBeTrue();
        Descendants<Wpf.Ui.Controls.Card>(page).ShouldBeEmpty();

        var cards = RowCards(f, page);
        cards.Length.ShouldBe(expectedRows);
        foreach (var card in cards)
        {
            var grid = card.Child.ShouldBeOfType<Grid>();
            grid.ColumnDefinitions.Count.ShouldBe(3);
            grid.ColumnDefinitions[0].Width.IsAuto.ShouldBeTrue();
            grid.ColumnDefinitions[1].Width.IsStar.ShouldBeTrue();
            grid.ColumnDefinitions[2].Width.IsAuto.ShouldBeTrue();

            // 图标必须是行 Grid 的直接子元素：文本框、滚动条的模板里也可能有 SymbolIcon，不能按后代找。
            var icon = grid.Children.OfType<Wpf.Ui.Controls.SymbolIcon>().ShouldHaveSingleItem();
            Grid.GetColumn(icon).ShouldBe(0);
            ReferenceEquals(icon.Style, f.Window.FindResource("IrisRowIcon")).ShouldBeTrue();

            grid.Children.OfType<StackPanel>()
                .Where(panel => Grid.GetRow(panel) == 0 && Grid.GetColumn(panel) == 1)
                .ShouldHaveSingleItem();
            string title = RowTitle(f, card).Text;
            title.ShouldNotBeNullOrWhiteSpace();
            // 图标逐行选（协调方裁决 C6）：每一行的图标按行标题登记在 ExpectedIcons() 里。
            ExpectedIcons(pageKey).TryGetValue(title, out var expectedIcon).ShouldBeTrue(title);
            icon.Symbol.ShouldBe(expectedIcon, title);
        }

        // 页的外壳：内边距在第一层面板上（不在 ScrollViewer 上），页标题恰好一个。
        page.ReadLocalValue(Control.PaddingProperty).ShouldBe(DependencyProperty.UnsetValue);
        ((FrameworkElement)page.Content).Margin.ShouldBe((Thickness)f.Window.FindResource("IrisPagePadding"));
        var pageTitleStyle = f.Window.FindResource("IrisPageTitle");
        Descendants<TextBlock>(page).Count(text => ReferenceEquals(text.Style, pageTitleStyle)).ShouldBe(1);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("history", 1)]
    [InlineData("screenshot", 4)]
    public Task Page_SwitchesHaveNoContentAndAreNamedAfterTheirRow(string pageKey, int expectedSwitches) => Run(f =>
    {
        f.Show(pageKey);
        var boxes = Descendants<CheckBox>(Page(f, pageKey)).ToArray();
        boxes.Length.ShouldBe(expectedSwitches);
        foreach (var box in boxes)
        {
            ReferenceEquals(box.Style, f.Window.FindResource("IrisSwitchCheckBox")).ShouldBeTrue(box.Name);
            box.Content.ShouldBeNull(box.Name);
            Grid.GetColumn(box).ShouldBe(2, box.Name);
            // 没有 Content 之后读屏器只能靠 AutomationProperties.Name；它必须就是本行的标题。
            string name = AutomationProperties.GetName(box);
            name.ShouldNotBeNullOrWhiteSpace(box.Name);
            name.ShouldBe(RowTitle(f, RowCardOf(f, box)).Text, box.Name);
        }
        return Task.CompletedTask;
    });
```

第二段（既有文字、窄宽度下不出界、名字与绑定）：

```csharp

    // 守卫：改造前后都应该绿。每条既有文字在页内恰好出现一次，一条不丢、一条不重。
    [Theory]
    [InlineData("history")]
    [InlineData("screenshot")]
    public Task Page_KeepsEveryExistingText(string pageKey) => Run(f =>
    {
        f.Show(pageKey);
        var visible = Descendants<TextBlock>(Page(f, pageKey)).Where(text => text.IsVisible).Select(text => text.Text).ToArray();
        foreach (string expected in ExpectedTexts(pageKey))
            visible.Count(text => text == expected).ShouldBe(1, expected);
        return Task.CompletedTask;
    });

    // 680 是导航宽态下内容区最窄的窗口宽度（680 - 176 = 504），600 是窗口最小宽度（S0-2）。
    [Theory]
    [InlineData("history", 680)]
    [InlineData("history", 600)]
    [InlineData("screenshot", 680)]
    [InlineData("screenshot", 600)]
    public Task Page_RowsFitTheContentAreaAtNarrowWidths(string pageKey, double width) => Run(f =>
    {
        f.Window.Width = width; f.Window.Height = 440;
        f.Show(pageKey);
        var page = Page(f, pageKey);
        var panel = (FrameworkElement)page.Content;
        var cards = RowCards(f, page);
        cards.ShouldNotBeEmpty();

        page.ScrollableWidth.ShouldBe(0);
        double panelRight = panel.TransformToAncestor(page).Transform(new Point(panel.ActualWidth + panel.Margin.Right, 0)).X;
        panelRight.ShouldBeLessThanOrEqualTo(page.ActualWidth + 0.5);

        foreach (var card in cards)
        {
            double cardRight = card.TransformToAncestor(panel).Transform(new Point(card.ActualWidth, 0)).X;
            cardRight.ShouldBeLessThanOrEqualTo(panel.ActualWidth + 0.5);

            // 第 2 列的控件太宽时，行 Grid 会按自己想要的宽度排、再被卡片裁掉：右边缘越过卡片内缘就是被裁了。
            double inner = card.ActualWidth - card.BorderThickness.Right - card.Padding.Right;
            var grid = (Grid)card.Child;
            foreach (var child in grid.Children.OfType<FrameworkElement>().Where(child => child.IsVisible))
            {
                double right = child.TransformToAncestor(card).Transform(new Point(child.ActualWidth, 0)).X;
                right.ShouldBeLessThanOrEqualTo(inner + 0.5, child.GetType().Name + " " + child.Name);
            }
            // 标题列是 *，被挤到很窄就会一字一行。
            RowTitle(f, card).ActualWidth.ShouldBeGreaterThanOrEqualTo(120);
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task NamedControls_KeepNameTypeBindingAndEditGate() => Run(f =>
    {
        f.Show("history");
        (string Name, Type Type, DependencyProperty Property, string Path)[] expected =
        [
            ("HistoryEnabledCheckBox", typeof(CheckBox), ToggleButton.IsCheckedProperty, "History.Enabled.Value"),
            ("HistoryMaxEntriesTextBox", typeof(TextBox), TextBox.TextProperty, "History.MaxEntriesText.Value"),
            ("ScreenshotConfirmCheck", typeof(CheckBox), ToggleButton.IsCheckedProperty, "Screenshot.ConfirmOnMouseUp.Value"),
            ("ScreenshotMagnifierCheck", typeof(CheckBox), ToggleButton.IsCheckedProperty, "Screenshot.ShowMagnifier.Value"),
            ("ScreenshotHideCheck", typeof(CheckBox), ToggleButton.IsCheckedProperty, "Screenshot.HideIrisWindows.Value"),
            ("ScreenshotCopyCheck", typeof(CheckBox), ToggleButton.IsCheckedProperty, "Screenshot.CopyImageToClipboard.Value"),
            ("ScreenshotMaskOpacityTextBox", typeof(TextBox), TextBox.TextProperty, "Screenshot.MaskOpacityText.Value"),
        ];
        foreach (var (name, type, property, path) in expected)
        {
            // 前四条是「不许变」的守卫（改造前就绿）：名字、确切类型、绑定路径、仍在 CanEdit 容器里。
            var control = (FrameworkElement)f.Window.FindName(name);
            control.GetType().ShouldBe(type, name);
            var binding = BindingOperations.GetBindingExpression(control, property)
                ?? throw new InvalidOperationException(name + " lost its binding.");
            binding.ParentBinding.Path.Path.ShouldBe(path, name);
            if (control is TextBox)
                binding.ParentBinding.UpdateSourceTrigger.ShouldBe(UpdateSourceTrigger.PropertyChanged, name);
            HasEditGate(control).ShouldBeTrue(name);

            // 后两条是新版面：控件在行卡片第 2 列，数字文本框宽 120（S0-4）。
            Grid.GetColumn(control).ShouldBe(2, name);
            ReferenceEquals(RowCardOf(f, control).Child, control.Parent).ShouldBeTrue(name);
            if (control is TextBox box) box.Width.ShouldBe(120d, name);
        }
        return Task.CompletedTask;
    });
```

第三段（校验错误文字、辅助方法、文件结尾）：

```csharp

    [Theory]
    [InlineData("history", "History.ErrorMessage", "HistoryMaxEntriesTextBox")]
    [InlineData("screenshot", "Screenshot.ErrorMessage", "ScreenshotMaskOpacityTextBox")]
    public Task ErrorText_SitsInItsRowAndCollapsesWhileEmpty(string pageKey, string errorPath, string inputName) => Run(async f =>
    {
        f.Show(pageKey);
        var error = Descendants<TextBlock>(Page(f, pageKey)).Single(text =>
            BindingOperations.GetBindingExpression(text, TextBlock.TextProperty)?.ParentBinding.Path?.Path == errorPath);
        // 行内错误文字统一用主题层的 IrisRowError（协调方裁决 C5），不在行里写内联样式。
        ReferenceEquals(error.Style, f.Window.FindResource("IrisRowError")).ShouldBeTrue();
        // 没有错误时折叠，不给行卡片多撑出一行。
        error.Visibility.ShouldBe(Visibility.Collapsed);

        // 错误文字与它说的那个输入框在同一张行卡片里，位于第 1 列的文字栈中。
        var input = (TextBox)f.Window.FindName(inputName);
        ReferenceEquals(RowCardOf(f, error), RowCardOf(f, input)).ShouldBeTrue();
        Grid.GetColumn((UIElement)error.Parent).ShouldBe(1);

        if (pageKey == "history") f.Vm.History.MaxEntriesText.Value = "abc";
        else f.Vm.Screenshot.MaskOpacityText.Value = "NaN";
        await f.Vm.ExecuteSaveAsync();
        f.Window.UpdateLayout();

        error.Text.ShouldNotBeNullOrWhiteSpace();
        error.Visibility.ShouldBe(Visibility.Visible);
        error.IsVisible.ShouldBeTrue();
    });

    private static ScrollViewer Page(OcrSettingsUiScope f, string pageKey) => (ScrollViewer)f.Window.FindName(pageKey switch
    {
        "history" => "HistoryPageScroll",
        "screenshot" => "ScreenshotPageScroll",
        _ => throw new ArgumentOutOfRangeException(nameof(pageKey), pageKey, null),
    });

    private static string[] ExpectedTexts(string pageKey) => pageKey switch
    {
        "history" =>
        [
            UiText.HistoryTitle, UiText.HistoryEnabled, UiText.HistoryEnabledHelp, UiText.HistoryMaxLabel, UiText.HistoryMaxHelp,
        ],
        "screenshot" =>
        [
            UiText.ScreenshotPageTitle, UiText.ScreenshotSettingsDescription, UiText.ScreenshotConfirmOnMouseUp,
            UiText.ScreenshotShowMagnifier, UiText.ScreenshotHideWindows, UiText.ScreenshotCopyImage, UiText.ScreenshotOpacity,
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(pageKey), pageKey, null),
    };

    // 行标题 → 该行的图标。必须是属性取值时现算（不能做成 static readonly）：UiText 的值随界面语言变。
    private static Dictionary<string, Wpf.Ui.Controls.SymbolRegular> ExpectedIcons(string pageKey) => pageKey switch
    {
        "history" => new()
        {
            [UiText.HistoryEnabled] = Wpf.Ui.Controls.SymbolRegular.TextBulletListAdd20,
            [UiText.HistoryMaxLabel] = Wpf.Ui.Controls.SymbolRegular.NumberSymbol20,
        },
        "screenshot" => new()
        {
            [UiText.ScreenshotConfirmOnMouseUp] = Wpf.Ui.Controls.SymbolRegular.CursorClick20,
            [UiText.ScreenshotShowMagnifier] = Wpf.Ui.Controls.SymbolRegular.ZoomIn20,
            [UiText.ScreenshotHideWindows] = Wpf.Ui.Controls.SymbolRegular.EyeOff20,
            [UiText.ScreenshotCopyImage] = Wpf.Ui.Controls.SymbolRegular.ClipboardImage20,
            [UiText.ScreenshotOpacity] = Wpf.Ui.Controls.SymbolRegular.Options20,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(pageKey), pageKey, null),
    };

    private static Border[] RowCards(OcrSettingsUiScope f, DependencyObject root)
    {
        var style = f.Window.FindResource("IrisRowCard");
        return Descendants<Border>(root).Where(border => ReferenceEquals(border.Style, style)).ToArray();
    }

    private static Border RowCardOf(OcrSettingsUiScope f, FrameworkElement element)
    {
        var style = f.Window.FindResource("IrisRowCard");
        for (DependencyObject? current = element.Parent; current is FrameworkElement parent; current = parent.Parent)
            if (parent is Border border && ReferenceEquals(border.Style, style)) return border;
        throw new InvalidOperationException($"{element.GetType().Name} '{element.Name}' is not inside an IrisRowCard.");
    }

    private static TextBlock RowTitle(OcrSettingsUiScope f, Border card)
    {
        var style = f.Window.FindResource("IrisRowTitle");
        return Descendants<TextBlock>(card).First(text => ReferenceEquals(text.Style, style));
    }

    private static bool HasEditGate(FrameworkElement element)
    {
        for (DependencyObject? current = element.Parent; current is FrameworkElement parent; current = parent.Parent)
            if (BindingOperations.GetBindingExpression(parent, UIElement.IsEnabledProperty)?.ParentBinding.Path?.Path == "CanEdit") return true;
        return false;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T value) yield return value;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
```

写完后对照两点（都来自既有文件，不是新约定）：`Descendants<T>` 与 `OcrSettingsRenderingTests.cs:120–127` 的同名私有方法逐字相同；同步用例以 `return Task.CompletedTask;` 结尾的写法照 `OcrSettingsUiTests.cs:23–34`。`TreatWarningsAsErrors` 开着：编译报可空性警告（多半出在 `FindName` 的强转或 `element.Parent`）时，照同目录既有测试的写法处理（它们都是直接强转、不加 `!`），不要用 `#pragma` 压掉。

- [ ] **Step 3: 跑，确认失败**

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsRowCardLayoutTests"
```

期望：**编译通过**（测试只用到已有的类型与任务 2 已加的资源键），13 条里 **11 条失败、2 条通过**：

| 用例 | 条数 | 预期现象 |
|---|---|---|
| `Page_IsBuiltFromIconRowCards` | 2 红 | 红在 `cards.Length`：应为 2（`history`）/ 5（`screenshot`），实际 0 |
| `Page_SwitchesHaveNoContentAndAreNamedAfterTheirRow` | 2 红 | 红在第一条 `ShouldBeTrue`，消息是 `HistoryEnabledCheckBox` / `ScreenshotConfirmCheck`（样式不是开关） |
| `Page_KeepsEveryExistingText` | **2 绿** | 守卫：改造前文字就都在 |
| `Page_RowsFitTheContentAreaAtNarrowWidths` | 4 红 | 红在 `cards.ShouldNotBeEmpty()` |
| `NamedControls_KeepNameTypeBindingAndEditGate` | 1 红 | 红在 `Grid.GetColumn(control)`：应为 2，实际 0，消息是 `HistoryEnabledCheckBox`（前四条守卫断言已通过） |
| `ErrorText_SitsInItsRowAndCollapsesWhileEmpty` | 2 红 | 红在 `error.Style` 那条 `ShouldBeTrue`：改造前的错误文字还没套 `IrisRowError`（协调方裁决 C5） |

与上表不符时的判断：

- 编译失败 → 测试文件抄错了（命名空间、`using`、类型全名），先改到能编译；不许为了编译去改 `src/`。
- `Page_KeepsEveryExistingText` 红 → 说明某条文字现在就不是「恰好一次」。看消息里是哪一条：出现 0 次多半是承载它的不是 `TextBlock`；出现 2 次是页内另有同文。**先停下来把这条事实报告出来**，不要改断言凑绿。
- `NamedControls_…` 红在前四条守卫 → 前面的任务动过这两页的名字或绑定，停下来报告。
- 抛的是 `ResourceReferenceKeyNotFoundException`（`IrisRowIcon` / `IrisPagePadding` / `IrisPageTitle` / `IrisRowError`）→ 任务 2 的键不在测试宿主的资源里，回 Step 1(c)。`ErrorText_…` 的两条若是抛这个异常而不是断言失败，缺的就是 `IrisRowError`：任务 2 还没按协调方裁决 C5 补上这个键，停下来报告，不许删掉测试里那条 `error.Style` 断言来绕。

- [ ] **Step 4: 改截图页（先改靠后的这一页，R13）**

Modify `src/Iris.App/Settings/SettingsWindow.xaml`。把 Step 1(a) 定位到的截图页整段——从 `<ScrollViewer x:Name="ScreenshotPageScroll"` 那一行到它自己的 `</ScrollViewer>`（`dc08c48` 上是 :287–319，33 行）——**整段替换**成下面的全文。替换范围的下一行是那条中文注释，不许带进去、不许改动。文件是 CRLF 换行，保存时保持 CRLF。

相对原文的改动只有这些：`ScrollViewer` 开标签去掉 `Padding="20,12"`；`ScrollViewer.Style` 那 10 行**一字不改**；外层 `StackPanel` 加页内边距；页标题与页级说明换样式；四个 `CheckBox` 与一个 `TextBox` 各包成一张横排行卡片；`ScreenshotError` 搬进不透明度那一行的文字栈。七个 `x:Name`、五条绑定表达式、`IsEnabled="{Binding CanEdit}"` 容器的包含关系（页标题与页级说明在容器外，五行在容器内）都与原文相同。

全文分两个代码块给出（只是为了排版，中间没有省略任何一行，接起来就是整页）。前半——外壳与前两行：

```xml
            <ScrollViewer x:Name="ScreenshotPageScroll" Grid.Column="1" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
                <ScrollViewer.Style>
                    <Style TargetType="ScrollViewer">
                        <Setter Property="Visibility" Value="Collapsed"/>
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding CurrentPageKey}" Value="screenshot">
                                <Setter Property="Visibility" Value="Visible"/>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </ScrollViewer.Style>
                <StackPanel Margin="{StaticResource IrisPagePadding}">
                    <TextBlock Style="{StaticResource IrisPageTitle}" Text="{x:Static app:UiText.ScreenshotPageTitle}"/>
                    <TextBlock Style="{StaticResource IrisRowDescription}" Margin="{StaticResource IrisCardGap}" Text="{x:Static app:UiText.ScreenshotSettingsDescription}"/>
                    <StackPanel IsEnabled="{Binding CanEdit}">
                        <Border Style="{StaticResource IrisRowCard}">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto"/>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="Auto"/>
                                </Grid.ColumnDefinitions>
                                <ui:SymbolIcon Grid.Column="0" Symbol="CursorClick20" Style="{StaticResource IrisRowIcon}"/>
                                <StackPanel Grid.Column="1" VerticalAlignment="Center">
                                    <TextBlock Style="{StaticResource IrisRowTitle}" Text="{x:Static app:UiText.ScreenshotConfirmOnMouseUp}"/>
                                </StackPanel>
                                <CheckBox x:Name="ScreenshotConfirmCheck" Grid.Column="2" Style="{StaticResource IrisSwitchCheckBox}" Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center" IsChecked="{Binding Screenshot.ConfirmOnMouseUp.Value, Mode=TwoWay}" AutomationProperties.Name="{x:Static app:UiText.ScreenshotConfirmOnMouseUp}"/>
                            </Grid>
                        </Border>
                        <Border Style="{StaticResource IrisRowCard}">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto"/>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="Auto"/>
                                </Grid.ColumnDefinitions>
                                <ui:SymbolIcon Grid.Column="0" Symbol="ZoomIn20" Style="{StaticResource IrisRowIcon}"/>
                                <StackPanel Grid.Column="1" VerticalAlignment="Center">
                                    <TextBlock Style="{StaticResource IrisRowTitle}" Text="{x:Static app:UiText.ScreenshotShowMagnifier}"/>
                                </StackPanel>
                                <CheckBox x:Name="ScreenshotMagnifierCheck" Grid.Column="2" Style="{StaticResource IrisSwitchCheckBox}" Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center" IsChecked="{Binding Screenshot.ShowMagnifier.Value, Mode=TwoWay}" AutomationProperties.Name="{x:Static app:UiText.ScreenshotShowMagnifier}"/>
                            </Grid>
                        </Border>
```

后半——后三行与收尾（紧接上一个代码块的最后一行）：

```xml
                        <Border Style="{StaticResource IrisRowCard}">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto"/>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="Auto"/>
                                </Grid.ColumnDefinitions>
                                <ui:SymbolIcon Grid.Column="0" Symbol="EyeOff20" Style="{StaticResource IrisRowIcon}"/>
                                <StackPanel Grid.Column="1" VerticalAlignment="Center">
                                    <TextBlock Style="{StaticResource IrisRowTitle}" Text="{x:Static app:UiText.ScreenshotHideWindows}"/>
                                </StackPanel>
                                <CheckBox x:Name="ScreenshotHideCheck" Grid.Column="2" Style="{StaticResource IrisSwitchCheckBox}" Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center" IsChecked="{Binding Screenshot.HideIrisWindows.Value, Mode=TwoWay}" AutomationProperties.Name="{x:Static app:UiText.ScreenshotHideWindows}"/>
                            </Grid>
                        </Border>
                        <Border Style="{StaticResource IrisRowCard}">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto"/>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="Auto"/>
                                </Grid.ColumnDefinitions>
                                <ui:SymbolIcon Grid.Column="0" Symbol="ClipboardImage20" Style="{StaticResource IrisRowIcon}"/>
                                <StackPanel Grid.Column="1" VerticalAlignment="Center">
                                    <TextBlock Style="{StaticResource IrisRowTitle}" Text="{x:Static app:UiText.ScreenshotCopyImage}"/>
                                </StackPanel>
                                <CheckBox x:Name="ScreenshotCopyCheck" Grid.Column="2" Style="{StaticResource IrisSwitchCheckBox}" Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center" IsChecked="{Binding Screenshot.CopyImageToClipboard.Value, Mode=TwoWay}" AutomationProperties.Name="{x:Static app:UiText.ScreenshotCopyImage}"/>
                            </Grid>
                        </Border>
                        <Border Style="{StaticResource IrisRowCard}">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto"/>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="Auto"/>
                                </Grid.ColumnDefinitions>
                                <ui:SymbolIcon Grid.Column="0" Symbol="Options20" Style="{StaticResource IrisRowIcon}"/>
                                <StackPanel Grid.Column="1" VerticalAlignment="Center">
                                    <TextBlock Style="{StaticResource IrisRowTitle}" Text="{x:Static app:UiText.ScreenshotOpacity}"/>
                                    <TextBlock x:Name="ScreenshotError" Style="{StaticResource IrisRowError}" Text="{Binding Screenshot.ErrorMessage}" Foreground="{DynamicResource SystemFillColorCautionBrush}"/>
                                </StackPanel>
                                <TextBox x:Name="ScreenshotMaskOpacityTextBox" Grid.Column="2" Width="120" Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center" Text="{Binding Screenshot.MaskOpacityText.Value, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"/>
                            </Grid>
                        </Border>
                    </StackPanel>
                </StackPanel>
            </ScrollViewer>
```

图标（U2；协调方裁决 C6：逐行选）：五行依次是 `CursorClick20`、`ZoomIn20`、`EyeOff20`、`ClipboardImage20`、`Options20`，即 Step 1(e) 表里的首选。Step 1(e) 的复核里哪一行走了退路，这里那一行就写 `Screenshot20`，并同步改 `ExpectedIcons()`。错误文字 `ScreenshotError` 是一行写法、套 `IrisRowError`（协调方裁决 C5），名字、绑定、`Foreground` 都留在元素上。

改完立刻跑一次，确认截图页单独就绿：

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsRowCardLayoutTests"
```

期望：构建零警告；13 条里 **7 条通过、6 条仍红**。通过的是 `screenshot` 的 6 条（`Page_IsBuiltFromIconRowCards`、`Page_SwitchesHave…`、`Page_KeepsEveryExistingText`、`ErrorText_…` 各 1 条，`Page_RowsFit…` 2 条）加上 `Page_KeepsEveryExistingText("history")`。仍红的是 `history` 的 5 条（位置与 Step 3 的表相同）与 `NamedControls_…`（它先检查历史页的控件，仍红在 `HistoryEnabledCheckBox` 的列号）。

红的位置不对时对照：

| 现象 | 多半的原因 | 处理 |
|---|---|---|
| XAML 编译报某个图标名不是 `SymbolRegular` 的成员 | 不应该发生（七个首选在 net10 那一份程序集里都精确命中过，Step 1(e)）；多半是抄错了字 | 先对照 Step 1(e) 的表逐字核对；确实不存在就把那一行换成退路 `Screenshot20`，同步改 `ExpectedIcons()` 的对应项，**记入偏差清单** |
| 窗口构造时抛「找不到名为 `IrisPagePadding` / `IrisPageTitle` / `IrisRowIcon` / `IrisRowError` 的资源」 | 任务 2 的键没进 `Iris.Styles.xaml`，或测试宿主没合并它 | 回 Step 1(c)；不许把 `StaticResource` 改成 `DynamicResource` 来绕，也不许退回去写内联样式 |
| `Page_IsBuiltFromIconRowCards("screenshot")` 红在 `icon.Style` | 图标写成了标记扩展写法，或补 `FontSize` 时把 `Style` 删了 | 图标必须是元素写法并带 `Style`；任务 2 的退路只是**加** `FontSize="20"` |
| `Page_IsBuiltFromIconRowCards("screenshot")` 红在 `TryGetValue(title, …)` 或 `icon.Symbol`，消息是某一行的标题 | XAML 里那一行的图标与 `ExpectedIcons()` 登记的不一致，或行标题用的 `UiText` 键抄错 | 以 Step 1(e) 的表为准，两边改成一致 |
| `Page_RowsFit…("screenshot", …)` 红在标题宽度或右边缘 | 第 2 列的控件比预算宽 | 先把该行三列的 `ActualWidth` 打出来；**不要**把 120 改小来凑绿，报告实际数字 |
| `ErrorText_…("screenshot")` 红在 `error.Style`（不是 `IrisRowError` 那个实例） | 错误文字上漏了 `Style="{StaticResource IrisRowError}"`，或还留着内联 `TextBlock.Style` | 对照上面的 XAML，改成一行写法 |
| `ErrorText_…("screenshot")` 红在保存之前的 `Visibility`（实际 `Visible`） | 空 `TextBlock` 的 `Text` 不是空串（撰写时按「绑定到 `null` 会被强制成空串」推断，未运行验证），`IrisRowError` 的 `Text=""` 触发器没命中 | 退路改在**任务 2 的 `IrisRowError` 样式里**：紧跟现有触发器再加一个 `<Trigger Property="Text" Value="{x:Null}"><Setter Property="Visibility" Value="Collapsed"/></Trigger>`；本任务的 XAML 与测试都不动。属于回改任务 2 已暂存的文件（`Iris.Styles.xaml`），改完重跑任务 2 的过滤集（`FullyQualifiedName~Iris.App.Tests.Themes`），并**记入偏差清单** |
| `ErrorText_…("screenshot")` 红在保存之后 | 错误文字的绑定路径抄错 | 对照上面的 XAML 逐字检查 |

- [ ] **Step 5: 改历史页**

Modify `src/Iris.App/Settings/SettingsWindow.xaml`，历史页（Step 1(a) 定位的范围；截图页在它后面，Step 4 没有让它的行号变）。两处替换，中间那一行单行写成的 `<ScrollViewer.Style>…</ScrollViewer.Style>`（`dc08c48` 上是 :169）**不动**。

(a) 开标签（:168）去掉 `Padding="20,12"`。旧：

```xml
            <ScrollViewer x:Name="HistoryPageScroll" Grid.Column="1" Padding="20,12" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
```

新：

```xml
            <ScrollViewer x:Name="HistoryPageScroll" Grid.Column="1" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
```

(b) 把从 `<StackPanel IsEnabled="{Binding CanEdit}">` 到与它配对的 `</StackPanel>` 的 9 行（:170–178：容器、页标题、复选框、说明、标签、文本框、说明、错误文字、收尾）整段替换成下面的全文。页标题原来就在 `CanEdit` 容器里，改完仍在里面（R4：包含关系不变）。

```xml
                <StackPanel IsEnabled="{Binding CanEdit}" Margin="{StaticResource IrisPagePadding}">
                    <TextBlock Style="{StaticResource IrisPageTitle}" Text="{x:Static app:UiText.HistoryTitle}"/>
                    <Border Style="{StaticResource IrisRowCard}">
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto"/>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="Auto"/>
                            </Grid.ColumnDefinitions>
                            <ui:SymbolIcon Grid.Column="0" Symbol="TextBulletListAdd20" Style="{StaticResource IrisRowIcon}"/>
                            <StackPanel Grid.Column="1" VerticalAlignment="Center">
                                <TextBlock Style="{StaticResource IrisRowTitle}" Text="{x:Static app:UiText.HistoryEnabled}"/>
                                <TextBlock Style="{StaticResource IrisRowDescription}" Text="{x:Static app:UiText.HistoryEnabledHelp}"/>
                            </StackPanel>
                            <CheckBox x:Name="HistoryEnabledCheckBox" Grid.Column="2" Style="{StaticResource IrisSwitchCheckBox}" Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center" IsChecked="{Binding History.Enabled.Value}" AutomationProperties.Name="{x:Static app:UiText.HistoryEnabled}"/>
                        </Grid>
                    </Border>
                    <Border Style="{StaticResource IrisRowCard}">
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto"/>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="Auto"/>
                            </Grid.ColumnDefinitions>
                            <ui:SymbolIcon Grid.Column="0" Symbol="NumberSymbol20" Style="{StaticResource IrisRowIcon}"/>
                            <StackPanel Grid.Column="1" VerticalAlignment="Center">
                                <TextBlock Style="{StaticResource IrisRowTitle}" Text="{x:Static app:UiText.HistoryMaxLabel}"/>
                                <TextBlock Style="{StaticResource IrisRowDescription}" Text="{x:Static app:UiText.HistoryMaxHelp}"/>
                                <TextBlock Style="{StaticResource IrisRowError}" Text="{Binding History.ErrorMessage}" Foreground="{DynamicResource SystemFillColorCriticalBrush}"/>
                            </StackPanel>
                            <TextBox x:Name="HistoryMaxEntriesTextBox" Grid.Column="2" Width="120" Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center" Text="{Binding History.MaxEntriesText.Value, UpdateSourceTrigger=PropertyChanged}"/>
                        </Grid>
                    </Border>
                </StackPanel>
```

逐项对照原文：`IsChecked="{Binding History.Enabled.Value}"` 原来就没写 `Mode`，不要补；`HistoryEnabledCheckBox` 上去掉了 `ContentTemplate`、`HorizontalContentAlignment`、`Content` 三个属性（文字用同一条 `UiText.HistoryEnabled` 搬到行标题并写进 `AutomationProperties.Name`）；错误文字原来没有名字，**不给它起名**；它是一行写法、套 `IrisRowError`（协调方裁决 C5），原来的 `Foreground="{DynamicResource SystemFillColorCriticalBrush}"` 留在元素上，原来的本地 `Margin="0,12,0,0"` 与 `TextWrapping="Wrap"` 删掉（样式里有）。图标按 Step 1(e) 的表逐行选（协调方裁决 C6）：第一行 `TextBulletListAdd20`、第二行 `NumberSymbol20`；Step 1(e) 的复核里哪一行走了退路，这里那一行就写 `History20`，并同步改 `ExpectedIcons()`。Step 4 的对照表同样适用于本页（把 `screenshot` 换成 `history`、退路图标换成 `History20`）。

- [ ] **Step 6: 跑，确认通过；核对名字与残留**

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsRowCardLayoutTests"
```

期望：13 条全部通过、0 失败，构建零警告。

然后做四项不靠测试的核对：

```
grep -o 'x:Name="[^"]*"' src/Iris.App/Settings/SettingsWindow.xaml | sort > "$TEMP/t041-task5-names-after.txt"
diff "$TEMP/t041-task5-names-before.txt" "$TEMP/t041-task5-names-after.txt" && echo NAMES-UNCHANGED
grep -c 'StaticResource WrappingCheckBoxContent' src/Iris.App/Settings/SettingsWindow.xaml
grep -n 'x:Key="WrappingCheckBoxContent"' src/Iris.App/Settings/SettingsWindow.xaml
git diff --stat -- src/Iris.App/Settings/SettingsWindow.xaml
git diff -- src/Iris.App/Settings/SettingsWindow.xaml | LC_ALL=C grep -c $'^[-+].*[\x80-\xff]'
```

期望依次是：

1. 打印 `NAMES-UNCHANGED`（本任务不增、不删、不改任何 `x:Name`）。
2. `11`（改前 12；本任务清掉了 `HistoryEnabledCheckBox` 那一处）。前面的任务若已经清过别处，以「改前的数 − 1」为准。
3. 仍有 1 行——`WrappingCheckBoxContent` 的资源定义**不删**（还有 11 处在用，R14）。
4. `--stat` 的增删行数在一百多行的量级（撰写时按上面的 XAML 估算：新增约 110 行、删除约 30 行，未实测）。数字远大于此（上千行）说明换行符被整文件改掉了：把文件恢复成 CRLF 后再看，不许带着整文件的换行改动往下走。
5. `0`——本任务的差异里没有任何一行含非 ASCII 字符（没有新增中文，也没有碰既有中文注释，R7）。

- [ ] **Step 7: 跑本任务的回归过滤集与「设置全集」，看证据图**

本任务的过滤集（D8 ② 的「历史页、截图页」那一行，加上新文件）：

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsRowCardLayoutTests|FullyQualifiedName~HistorySettingsUiTests|FullyQualifiedName~HistoryRenderingTests|FullyQualifiedName~OcrSettingsUiTests|FullyQualifiedName~OcrSettingsRenderingTests"
```

期望：全绿、0 失败。各自守着什么：

| 测试 | 守着本任务的哪一点 |
|---|---|
| `HistorySettingsUiTests.InvalidCurrentText_IsFlushedAndRetainedWithoutUsingOldValue`（6 条） | `HistoryMaxEntriesTextBox` 仍是窗口字段、仍被 `FlushFocusedBinding` 按名字冲刷（cs:312） |
| `OcrSettingsUiTests.SaveFlushesCurrentNumericText_…`（`timeout: false` 的 3 条） | `ScreenshotMaskOpacityTextBox` 同上（cs:311） |
| `OcrSettingsUiTests.AllOcrAndScreenshotFields_CommitWithGeneralInOneTransaction` | 截图页五个值仍经原绑定写回 |
| `OcrSettingsRenderingTests`（1 条，出 42 张图） | 截图页在 600 / 760 两种宽度、两种主题下每个可见 `TextBox` 右边缘不超出窗口 |
| `HistoryRenderingTests`（1 条） | 历史页能切过去、无效值保存不抛异常 |

再跑 5.0 的「设置全集」：

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~Iris.App.Tests.Settings|FullyQualifiedName~Iris.App.Tests.Themes|FullyQualifiedName~Iris.App.Tests.Localization|FullyQualifiedName~Iris.App.Tests.Updates|FullyQualifiedName~MouseRenderingTests|FullyQualifiedName~HistoryRenderingTests|FullyQualifiedName~ThemeServicePreviewTests"
```

期望：全绿——任务 4 的「范围与要点」里写明的 `SettingsWindowTranslationUiTests` 用例 7 中间态除外（它若在任务 4 结束时就红在块 1–3，这里仍是那个样子；本任务不该让它的红法有任何变化）。出现任务 4 结束时没有的红：按 5.0 的顺序处理——先查是不是本任务的 XAML 违反了 2.9 的约束（是就改 XAML），不许为了绿去改别的页或改既有断言。

最后人工看证据图（没有任何测试比对图像）。两条渲染测试的输出目录：环境变量 `IRIS_T032B_EVIDENCE` / `IRIS_T035_EVIDENCE` 指向的目录，没设就在 `%TEMP%` 下的 `settings-render-<guid>` / `history-render-<guid>`（取修改时间最新的那个）。要看的文件与要点：

| 文件 | 看什么 |
|---|---|
| `Light-760-screenshot.png`、`Light-600-screenshot.png`、`Light-600-screenshot-bottom.png` | 五张行卡片；图标在左、标题居中靠左、开关 / 文本框贴右；600 宽下标题不被挤成一字一行；滚到底时最后一张卡片完整 |
| `Light-invalid-opacity.png` | 不透明度那一行的标题下面出现一行错误文字，卡片跟着变高，其他行不变 |
| `Light-760-settings.png`、`Light-600-settings.png`（历史页） | 两张行卡片，各有标题与说明；英文界面下 `HistoryMaxLabel` 较长（66 个字符），会在标题列里换行，不该被截断 |
| `Light-760-settings-invalid.png` | 第二张卡片的说明下面出现红色错误文字 |
| 上面各图的 `Dark-…` 版本 | 任务 3 已把这两条渲染测试切主题的夹具都对齐了 Iris 主题字典：`HistoryRenderingTests` 的辅助方法 `Theme`（任务 3 原有范围）、`OcrSettingsRenderingTests` 的 `Prepare`（协调方裁决 C8 纳入任务 3）。所以深色图应是**深色卡片底、浅色字**，看的要点与浅色图相同。若深色图里的行卡片仍是浅色底：说明任务 3 漏了对应的夹具——**停下来报告**（写明是哪一条测试的哪一张图），不在本任务里改夹具，也不拿这张图当通过的证据 |

看到裁切、重叠、标题被挤窄：回 Step 4 / Step 5 对照 XAML；确认 XAML 与本文一致仍然如此，把图与窗口宽度报告出来，不自行改 S0-4 的宽度数字。

- [ ] **Step 8: 暂存（不提交）**

```
git add src/Iris.App/Settings/SettingsWindow.xaml tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs
git status --short -- src tests
```

期望：`tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs` 是 `A`，`src/Iris.App/Settings/SettingsWindow.xaml` 是 `M`（任务 4 已经暂存过它，这次是在暂存区里叠加）；`src`、`tests` 下没有未暂存的改动。只有一种情况要多暂存一个文件：Step 4 的表里走了「回改任务 2 的 `IrisRowError`」那条退路——这时再跑一条 `git add src/Iris.App/Themes/Iris.Styles.xaml`（它在任务 2 就已暂存，这次同样是叠加）。**不提交**——整个阶段只有任务 14 的一个提交。Step 1(f)、Step 6 在 `%TEMP%` 下留的两个 `t041-task5-names-*.txt` 可以删掉。

把下面几条写进完成报告的**偏差清单**（都发生在本任务）：

1. 历史页 1 个、截图页 4 个复选框改成开关后，点标题文字不再切换，只有开关本身可点（S0-5 已定为接受；第 6 节 Q5）。
2. 两页的页标题字号由 22 变 20，并由「可换行」变成「单行加省略号」（`IrisPageTitle`，任务 2 的取值）。
3. 两页的内边距由 `ScrollViewer` 上的 `20,12` 变成第一层面板上的 `24,16,24,16`（`IrisPagePadding`，S0-4）。
4. 两个数字文本框由「跨满整页宽」变成固定 `Width="120"`、靠右（S0-4）。
5. 两条校验错误文字搬进了各自的行卡片，改套主题层的 `IrisRowError`（协调方裁决 C5）：字号由 14 变 12，历史页那条去掉了本地的 `Margin="0,12,0,0"`，两条都去掉了本地的 `TextWrapping="Wrap"`（样式里有）；没有错误时由「占着一行空白」改为折叠。`Foreground` 画刷没有变（历史页 `SystemFillColorCriticalBrush`、截图页 `SystemFillColorCautionBrush`）。
6. 截图页的页级说明 `ScreenshotSettingsDescription` 保留在页标题下面、可编辑容器之外，改套 `IrisRowDescription` + `IrisCardGap`（协调方裁决 C7）：与页标题的间距由 6 变 12、与第一行的间距由 18 变 12。
7. 「改过的既有测试」：本任务**无**（Step 1(d) 的结论；若那一步查到并改了，照实列出）。
8. 行图标逐行选（协调方裁决 C6）：历史页 `TextBulletListAdd20`、`NumberSymbol20`；截图页 `CursorClick20`、`ZoomIn20`、`EyeOff20`、`ClipboardImage20`、`Options20`。这七个图标都是新增的界面元素，改造前这两页没有行图标。
9. Step 1、Step 4、Step 6 里凡是走了退路的，各记一笔，写明是哪一行、换成了什么：某一行的图标换成了该页的页面图标（`History20` / `Screenshot20`，并同步改了 `ExpectedIcons()`）；给图标补了 `FontSize="20"`；因为空错误文字没有折叠而回改了任务 2 的 `IrisRowError`（加 `{x:Null}` 触发器）。

- [ ] **Step 9: 本任务自检清单**

逐条对照 Task 5 的「范围与要点」与相关裁决；每条后面是守着它的东西。任何一条答不上「是」，回到对应的 Step。

| # | 要求（出处） | 怎么确认 |
|---|---|---|
| 1 | `HistoryEnabledCheckBox` → 横排行加开关（Task 5；S0-5） | `Page_SwitchesHave…("history", 1)`、`NamedControls_…` |
| 2 | `HistoryMaxEntriesTextBox` → 横排行、`Width="120"`，错误文字进该行第 1 列、套 `IrisRowError`（Task 5；S0-4；协调方裁决 C5） | `NamedControls_…`、`ErrorText_…("history", …)` |
| 3 | 历史页两段说明原样保留（Task 5；S0-4 总则第 2 条） | `Page_KeepsEveryExistingText("history")` |
| 4 | 历史页可编辑容器照旧包住这两行（R4） | `NamedControls_…` 的 `HasEditGate`；Step 5 的 XAML 里页标题仍在容器内 |
| 5 | 截图页四个复选框 → 四张横排行加开关（Task 5；S0-5） | `Page_IsBuiltFromIconRowCards("screenshot", 5)`、`Page_SwitchesHave…("screenshot", 4)` |
| 6 | `ScreenshotMaskOpacityTextBox` → 横排行，`ScreenshotError` 进该行第 1 列、名字保留、套 `IrisRowError`（Task 5；协调方裁决 C5） | `NamedControls_…`、`ErrorText_…("screenshot", …)`；Step 6 的名字比对 |
| 7 | 不新编说明文字、不新增字符串（S0-4；S0-12） | `git status --short -- src/Iris.App/Resources build/localization src/Iris.App/UiText*.cs` 没有本任务造成的改动 |
| 8 | 截图页可编辑容器照旧：页标题与页级说明在外、五行在内（R4） | Step 4 的 XAML；`NamedControls_…` 的 `HasEditGate` |
| 9 | 图标逐行选：历史页 `TextBulletListAdd20`、`NumberSymbol20`；截图页 `CursorClick20`、`ZoomIn20`、`EyeOff20`、`ClipboardImage20`、`Options20`；都是 `ui:SymbolIcon`、不用 `Image`（U2；协调方裁决 C6；Step 1(e) 的表） | `Page_IsBuiltFromIconRowCards` 按 `ExpectedIcons()` 逐行比对；本表下方的计数命令里七个图标各输出 1（哪一行走了退路，就把命令里那个名字换成退路图标再数：`History20` 最多 2、`Screenshot20` 最多 5） |
| 10 | 页标题用 `IrisPageTitle`，页内边距用 `IrisPagePadding`（Task 5；S0-4） | `Page_IsBuiltFromIconRowCards` 末尾三条断言 |
| 11 | `HistoryPageScroll`、`ScreenshotPageScroll` 仍是 `ScrollViewer`，各页自己的 `ScrollViewer` 保留（R16；2.9(a)） | `HistorySettingsUiTests`、`OcrSettingsUiTests` 的强转；测试里 `Page()` 的强转 |
| 12 | 两个文本框仍是原生 `TextBox`、名字不变、没进模板（R1；cs:311–312） | 能编译（代码隐藏用的是生成字段）；`NamedControls_…` 的确切类型断言；Step 7 表里两组冲刷用例 |
| 13 | 所有绑定表达式原样搬（Task 5） | `NamedControls_…` 的路径与 `UpdateSourceTrigger`；人工对照「已读到的事实」的表：四个截图开关与不透明度带 `Mode=TwoWay`，历史页两条不带 |
| 14 | 行卡片逐行手写，没有 `DataTemplate` / `ControlTemplate` / `UserControl`（S0-4 总则第 1 条） | 两页的新 XAML 里没有这三个词；没有新建文件（除测试） |
| 15 | 开关不带 `Content`、在第 2 列，`AutomationProperties.Name` = 行标题的同一条 `UiText`（S0-5） | `Page_SwitchesHave…` |
| 16 | 标题与说明可换行、不截断（S0-4 总则末条） | 用的是 `IrisRowTitle` / `IrisRowDescription`（都带 `TextWrapping="Wrap"`）；`Page_RowsFit…` 的标题宽度 ≥ 120；Step 7 的证据图 |
| 17 | 680 与 600 宽下不裁切（Task 5 提纲；A6.12 第 2 条） | `Page_RowsFit…` 四条 |
| 18 | 不动其他页；不删 `WrappingCheckBoxContent`（Task 5「不做」） | Step 6 的第 2、3 项；`git diff` 只有两页范围内的改动 |
| 19 | `src/` 不新增中文、不碰既有中文注释（1.3 第 2 条；R7） | Step 6 的第 5 项 = 0 |
| 20 | 没有 `ui:Card` 残留在这两页；没有往 `Border` 上叠本地 `Padding` / `Margin`（R15） | `Page_IsBuiltFromIconRowCards` 的 `Card` 断言；新 XAML 里七个 `Border` 只有 `Style` 一个属性 |
| 21 | 没改 `specs/`、视图模型、代码隐藏、主题层、资源文件；没加依赖（1.3） | `git status --short` 里本任务只有 Step 8 的两个路径；唯一的例外是走了「回改任务 2 的 `IrisRowError`」退路时的 `src/Iris.App/Themes/Iris.Styles.xaml`（已记入偏差清单） |
| 22 | 只暂存、没提交（1.3 第 5 条） | `git log -1` 仍是 `dc08c48` |
| 23 | 「行卡片 XAML 范式」一节与实际写进 XAML 的一致（任务 6–9 要照抄） | 走了「给图标补 `FontSize="20"`」退路的，同步改范式一节里的图标那几行，并在交接说明里写明；某一行换了退路图标的，改 Step 1(e) 的表与 `ExpectedIcons()`；换掉的若正好是范式一节示例里用的 `TextBulletListAdd20`（形态 A）或 `Options20`（形态 B），把示例里的那个名字也换掉，其余不动（图标本来就逐行选）；因为空错误文字不折叠而回改了任务 2 的 `IrisRowError` 的，范式一节与各页 XAML 都不动，只在交接说明里写明 |
| 24 | 行内错误文字是一行写法、套 `IrisRowError`，`Foreground` 留在元素上；两页范围内没有内联 `TextBlock.Style`（协调方裁决 C5） | `ErrorText_…` 的 `error.Style` 断言；本表下方的计数命令输出 `IrisRowError 2`、`inline-style 0` |
| 25 | 截图页的页级说明在页标题下面、可编辑容器之外，用 `IrisRowDescription` + `IrisCardGap`（协调方裁决 C7） | Step 4 的 XAML；`Page_KeepsEveryExistingText("screenshot")` |

计数命令（在仓库根目录、Git Bash 里跑；先用 `sed` 只取出这两页，免得把别的页与任务 4 的导航图标算进来）：

```
P=$(sed -n '/x:Name="HistoryPageScroll"/,/<\/ScrollViewer>/p;/x:Name="ScreenshotPageScroll"/,/<\/ScrollViewer>/p' src/Iris.App/Settings/SettingsWindow.xaml)
for m in TextBulletListAdd20 NumberSymbol20 CursorClick20 ZoomIn20 EyeOff20 ClipboardImage20 Options20; do echo "$m $(echo "$P" | grep -c "Symbol=\"$m\" Style=\"{StaticResource IrisRowIcon}\"")"; done
echo "IrisRowError $(echo "$P" | grep -c 'Style="{StaticResource IrisRowError}"')"
echo "inline-style $(echo "$P" | grep -c '<TextBlock.Style>')"
```

期望九行输出：七个图标名后面各是 `1`，然后 `IrisRowError 2`、`inline-style 0`。这条命令的 `sed` 取页范围那一段，撰写时在改造前的文件上跑过（取出 45 行，即历史页 12 行加截图页 33 行）；后面的计数部分只在本文的 XAML 文本上试过，没有在改造后的真实文件上跑过。前提是这两页内部没有嵌套别的 `ScrollViewer`（Step 4、Step 5 的 XAML 里没有）。

#### ⚠ 与裁决不符之处 / 需协调方裁决

撰写时提出的七条都已由协调方处理（裁决原文见计划 1.2A 的 C5–C8）。下面只留结论；标了「记入偏差清单」的，写完成报告时照抄。

1. **截图页的页级说明——已裁决（C7）。** 计划原先写「截图页没有说明文字」，事实是 `SettingsWindow.xaml:300` 有一条页级说明 `UiText.ScreenshotSettingsDescription`（计划 2.3 与本任务「范围与要点」已改准）。按 C7 留在页标题下面、可编辑容器之外，用 `IrisRowDescription` 加 `Margin="{StaticResource IrisCardGap}"`。它上下的间距由 6 / 18 变成 12 / 12（按任务 2 的样式键取值推算）——**记入偏差清单**。
2. **行内校验错误文字——已裁决（C5）。** 用任务 2 的 `IrisRowError`（`BasedOn IrisRowDescription`；`Text` 为空串时折叠；不设 `Foreground`），不在行里写内联样式。后果：这两条错误文字的字号由 14 变 12，历史页那条去掉了 `Margin="0,12,0,0"`——**记入偏差清单**。「`Text` 绑到 `null` 时等于空串」没有运行验证；不折叠时的退路是在任务 2 的 `IrisRowError` 样式里加 `{x:Null}` 触发器（见「行卡片 XAML 范式」一节），各页的 XAML 不动。
3. **`OcrSettingsRenderingTests` 的切主题夹具——已裁决（C8）。** 纳入任务 3：对齐 Iris 主题字典，并在跑完后还原。本任务只在 Step 7 看它的深色证据图；行卡片仍是浅色底就停下来报告，不在本任务里改夹具。
4. **「不出界」检查收窄了对象——已接受。** `SettingsRowCardLayoutTests` 只查行卡片、行 `Grid` 的直接子元素（图标、文字栈、控件）与标题列宽度 ≥ 120，不遍历控件模板内部；`Page_KeepsEveryExistingText` 是改造前后都绿的守卫，不构成先红后绿。相对「步骤提纲」的写法收窄了——**记入偏差清单**。
5. **图标成员怎么算核实过——已接受。** 七个首选在应用实际引用的 `lib\net10.0-windows7.0\Wpf.Ui.dll` 里都整串命中 1 次。退路 `History20` 在那一份里整串命中 0 次，只见于 `ChatHistory20`：元数据的字符串堆会把一个名字存成更长名字的后缀，整串检索因此会漏报，不能据此说它不存在。协调方 2026-11-23 实测：`lib\net462\Wpf.Ui.dll` 的反射确认 `History20` 存在（`SymbolRegular` 共 9235 个成员），而同一份文件里它的整串命中同样是 0 次——所以核实图标名以 net462 那一份的反射为准（裁决 C6 的核实口径），整串检索只作辅证。`History20` 又是任务 4 的导航图标，任务 4 的构建是最终核实。
6. **图标逐行选——已裁决（C6）。** 不再是整页一个图标；七行的首选与退路见 Step 1(e) 的表。
7. **非开关控件不新增 `AutomationProperties.Name`——已接受。** 两个数字文本框改造前就没有读屏名，本任务不补；读屏可达性与改造前持平，没有变好。完成报告的「未做」里提一句。

**验证命令：** `FullyQualifiedName~SettingsRowCardLayoutTests|FullyQualifiedName~HistorySettingsUiTests|FullyQualifiedName~HistoryRenderingTests|FullyQualifiedName~OcrSettingsUiTests|FullyQualifiedName~OcrSettingsRenderingTests` —— 预期全绿；再跑「设置全集」。（与计划原文相同，未改。）

**要改的既有测试：** 无（撰写时 grep 的结论；Step 1(d) 复核）。

### Task 6: 常规页

**目的：** 把常规页的五张 `ui:Card` 改成行卡片与一张动作卡。

**Files:**
- Modify: `src/Iris.App/Settings/SettingsWindow.xaml`——常规页 :320–441（五张卡：语言 :342–351、更新 :353–370、主题 :373–396、启动 :399–414、日志 :417–439；从后往前改）
- Test: `tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs`（加 `general` 一行）

**范围与要点：**
- 语言：`UiLanguageCombo`（:345）→ 横向行；`UiLanguageHint`（:349，「重启后生效」那句）作为该行的说明，文字与名字不变。
- 更新：拆成两张（A7.2）。`CheckForUpdatesCheckBox`（:357）→ 横向行加开关；版本（:356）、状态（:360）、详情（:361）、进度条（:362）、四个 `ui:Button`（:363–368）→ 一张动作卡，按钮放 `WrapPanel`。原有的每一项都保留，一项不删。
- 主题（:373–396）、日志（:417–439）：按 S0-4 的适用规则选结构——单个下拉框用横向行；一行里有两个以上控件用纵向行。控件类型、名字、绑定原样搬。
- 启动（:399–414）：两个复选框（:405 / :409）→ 两张横向行加开关。
- 图标：**逐行选**（C6），见详细步骤的「行 → 首选图标 → 退路图标」表（规矩与起点见 S0-4「图标」）；S0-4 的表已选好的几行（界面语言、自动检查更新、主题、启动相关两项、日志）照抄作首选，「启动相关两项」在详细步骤的表里拆成两行各写各的。退路为页面图标 `Settings20`。动作卡不带图标。
- 保持：`GeneralPageScroll`（:321）；`UiLanguageCombo` 是 `ComboBox`；`CheckForUpdatesCheckBox` 是 `CheckBox`、有名字、**直接写在页面里不进模板**；四个更新按钮仍是 `ui:Button`、`Content` 是字符串；主题下拉框的类型不变（真机脚本靠 UI Automation 操作它，A8）。
- 先读 `UpdateRenderingTests.cs:89` 看它拿 `CheckForUpdatesCheckBox.Parent` 做什么，确保新的父元素（行卡片里的 `Grid`）满足它。
- 风险：R1、R6、R12、R15；英文下四个更新按钮在 680 宽（内容区 504）不得横向溢出，靠 `WrapPanel` 换行。

**步骤提纲：**
- [ ] 给 `SettingsRowCardLayoutTests` 加 `general` 一行，并加一条专门用例：`CheckForUpdatesCheckBox` 的 `Parent` 非空、四个更新按钮都在同一张动作卡里且 680 宽下不溢出
- [ ] 跑，确认失败
- [ ] 从日志卡往前逐张改到语言卡
- [ ] 跑本任务过滤组与「设置全集」；`git add`

**详细步骤：**

> **声明：代码与命令未运行过。** 本文件里的所有代码（C# 测试、XAML）都没有编译过、没有运行过；撰写时只做了只读核实（grep、小段 Read、对 NuGet 缓存里 `Wpf.Ui.dll` 的只读字符串检索）。对测试「会红 / 会绿」的预判全部是读代码推算。行号对应提交 `dc08c48`；Task 4、Task 5 会改 `SettingsWindow.xaml` 靠前的部分，常规页的行号会漂移，执行时一律按 Step 1 的 grep 重新定位。

#### 详细步骤

- [ ] **Step 1：开工核实——重新定位常规页行号，确认前置产物与基线全绿**

在仓库根目录跑：

```bash
git status --short -- src/Iris.App/Settings/SettingsWindow.xaml tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs
grep -n 'x:Name="GeneralPageScroll"\|x:Name="UiLanguageCombo"\|x:Name="UiLanguageHint"\|x:Name="CheckForUpdatesCheckBox"\|<!-- 右侧常规设置内容 -->\|<!-- 右侧翻译服务设置内容 -->' src/Iris.App/Settings/SettingsWindow.xaml
```

期望：`SettingsWindow.xaml` 与 `SettingsRowCardLayoutTests.cs` 无未暂存脏改动（前序 Task 1–5 已暂存）；`grep` 精确打出常规页起止边界（`<!-- 右侧常规设置内容 -->` 到 `<!-- 右侧翻译服务设置内容 -->` 之前）以及四个具名元素。同时清点该区间内的全部中文注释行（应恰为 5 条：`<!-- 右侧常规设置内容 -->`、`<!-- 页面标题 -->`、`<!-- 外观与主题卡片 -->`、`<!-- 系统启动与通知卡片 -->`、`<!-- 日志级别卡片 -->`），后续替换 XAML 时原封不动保留这 5 条注释，不删、不改、不挪出常规页。

跑一次改前基线确认全绿：

```bash
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsRowCardLayoutTests|FullyQualifiedName~UpdatePreferenceUiTests|FullyQualifiedName~UpdateRenderingTests|FullyQualifiedName~LocalizationTests|FullyQualifiedName~LocalizationRenderingTests|FullyQualifiedName~IrisSwitchStyleTests"
```

期望：全绿（0 失败）。

- [ ] **Step 2：在 `SettingsRowCardLayoutTests.cs` 登记 `general` 页并补更新动作卡守卫测试（TDD 红灯）**

修改 `tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs`：

1. **行卡片与开关、文字、窄宽理论用例加 `"general"`**：
   - `Page_IsBuiltFromIconRowCards` 加 `[InlineData("general", 6)]`
   - `Page_SwitchesHaveNoContentAndAreNamedAfterTheirRow` 加 `[InlineData("general", 3)]`
   - `Page_KeepsEveryExistingText` 加 `[InlineData("general")]`
   - `Page_RowsFitTheContentAreaAtNarrowWidths` 加 `[InlineData("general", 680)]`、`[InlineData("general", 600)]`；并在该方法末尾对页面内所有 `IrisCard`（动作卡）同样断言卡片右缘 `<= panel.ActualWidth + 0.5`，且卡片内每个可见按钮右缘不越出卡片内缘 `inner + 0.5`（守住英文窄宽 `680` / `600` 下四个更新按钮在 `WrapPanel` 内自动换行而不横向溢出）。
2. **`Page(f, pageKey)`、`ExpectedTexts(pageKey)`、`ExpectedIcons(pageKey)` 登记 `"general"`**：
   - `Page`：`"general" => (ScrollViewer)f.Window.FindName("GeneralPageScroll"),`
   - `ExpectedTexts("general")`：
     ```csharp
     "general" =>
     [
         UiText.SettingsNavGeneral,
         UiText.LanguageTitle,
         UiText.UpdateTitle,
         UiText.UpdatePreference,
         UiText.SettingsThemeGroup,
         UiText.SettingsThemeDescription,
         UiText.SettingsStartupGroup,
         UiText.SettingsLaunchAtStartup,
         UiText.SettingsShowStartupNotification,
         UiText.SettingsLogGroup,
         UiText.SettingsLogDescription,
     ],
     ```
   - `ExpectedIcons("general")`（下表成员均已通过 `lib\net462\Wpf.Ui.dll` 反射核实存在，退路一律为页面图标 `Settings20`）：

     | 行（按行标题键） | 首选图标 | 退路图标 |
     |---|---|---|
     | `UiText.LanguageTitle`（界面语言） | `LocalLanguage20` | `Settings20` |
     | `UiText.UpdatePreference`（自动检查更新） | `ArrowSync20` | `Settings20` |
     | `UiText.SettingsThemeGroup`（外观与主题） | `Color20` | `Settings20` |
     | `UiText.SettingsLaunchAtStartup`（开机自动启动） | `Power20` | `Settings20` |
     | `UiText.SettingsShowStartupNotification`（启动时显示通知） | `Power20` | `Settings20` |
     | `UiText.SettingsLogGroup`（日志级别） | `DocumentText20` | `Settings20` |

     ```csharp
     "general" => new()
     {
         [UiText.LanguageTitle] = Wpf.Ui.Controls.SymbolRegular.LocalLanguage20,
         [UiText.UpdatePreference] = Wpf.Ui.Controls.SymbolRegular.ArrowSync20,
         [UiText.SettingsThemeGroup] = Wpf.Ui.Controls.SymbolRegular.Color20,
         [UiText.SettingsLaunchAtStartup] = Wpf.Ui.Controls.SymbolRegular.Power20,
         [UiText.SettingsShowStartupNotification] = Wpf.Ui.Controls.SymbolRegular.Power20,
         [UiText.SettingsLogGroup] = Wpf.Ui.Controls.SymbolRegular.DocumentText20,
     },
     ```
3. **扩充 `NamedControls_KeepNameTypeBindingAndEditGate` 与 `HasEditGate`**：
   - 常规页没有外层 `CanEdit` 容器，6 个可编辑控件的 `IsEnabled="{Binding CanEdit}"` 直接写在控件自身上；把 `HasEditGate(FrameworkElement element)` 的循环起点从 `element.Parent` 改为 `element`（自身或祖先绑 `CanEdit` 均返回 `true`）。
   - 为兼容后续纵向行（第 0 列）与不同文本框宽度（数字框 `120`、短文本框 `220`、拉伸框 `null`），将 `expected` 元组扩为 `(string Name, Type Type, DependencyProperty Property, string Path, int Column, double? ExpectedWidth)`（已有的 `HistoryMaxEntriesTextBox` 与 `ScreenshotMaskOpacityTextBox` 填 `2, 120d`，其余勾选框填 `2, null`；且仅在 `property == TextBox.TextProperty` 时断言 `UpdateSourceTrigger.PropertyChanged`），追加常规页两个具名可编辑控件：
     ```csharp
     ("UiLanguageCombo", typeof(ComboBox), Selector.SelectedValueProperty, "InterfaceLanguage.Value", 2, null),
     ("CheckForUpdatesCheckBox", typeof(CheckBox), ToggleButton.IsCheckedProperty, "CheckForUpdates.Value", 2, null),
     ```
     另外单独断言 `UiLanguageHint` 仍是 `TextBlock`、位于 `UiLanguageCombo` 同一张 `IrisRowCard` 第 1 列，且 `Text` 绑定路径仍是 `"InterfaceLanguageHint"`。
4. **新增 `[Fact] GeneralPage_UpdateActionCardKeepsVersionStatusProgressAndFourButtons()`**：
   - 打开 `"general"` 页，断言页面内恰有 1 张 `Style == FindResource("IrisCard")` 的动作卡 `Border`（不带 `SymbolIcon`）。
   - 断言动作卡内存在绑定 `CurrentVersion`、`UpdateStatus`、`UpdateDetail` 的三个 `TextBlock`（`CurrentVersion` 的 `AutomationProperties.Name == UiText.UpdateTitle`），存在 `ProgressBar`（绑定 `UpdateProgress`），且 `WrapPanel` 内依次包含 `Content` 为 `UiText.UpdateCheck`、`UiText.UpdateDownload`、`UiText.UpdateCancel`、`UiText.UpdateInstall` 的四个 `Wpf.Ui.Controls.Button`，四个按钮均无 `CanEdit` 门控（`HasEditGate(btn).ShouldBeFalse()`）。

- [ ] **Step 3：跑新增测试确认失败（红灯）**

```bash
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsRowCardLayoutTests"
```

期望：编译通过；`general` 相关新用例失败（`GeneralPageScroll` 仍有本地 `Padding`、页面内仍有 5 张 `ui:Card`、0 张 `IrisRowCard`、`CheckForUpdatesCheckBox` 与启动两项尚未切 `IrisSwitchCheckBox`），既有 `history`、`screenshot` 用例保持全绿。

- [ ] **Step 4：改写 `SettingsWindow.xaml` 常规页为 6 张行卡片 + 1 张更新动作卡**

只改 `src/Iris.App/Settings/SettingsWindow.xaml` 中 `<!-- 右侧常规设置内容 -->` 到常规页 `</ScrollViewer>` 之间的片段（不动前后的截图页与翻译服务页，原样保留本页 5 条中文注释）：

1. **`GeneralPageScroll` 与外层面板**：
   - 删掉 `GeneralPageScroll` 上的 `Padding="24,16,24,16"`（保留 `x:Name="GeneralPageScroll" Grid.Column="1" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled"` 及 `ScrollViewer.Style`）。
   - 第一层 `<StackPanel>` 删掉 `MaxWidth="600" HorizontalAlignment="Stretch"`，改为 `<StackPanel Margin="{StaticResource IrisPagePadding}">`。
2. **页标题**（保留上方 `<!-- 页面标题 -->`）：
   ```xml
   <TextBlock Style="{StaticResource IrisPageTitle}"
              Text="{x:Static app:UiText.SettingsNavGeneral}" />
   ```
3. **界面语言行卡片**（横向行）：
   ```xml
   <Border Style="{StaticResource IrisRowCard}">
       <Grid>
           <Grid.ColumnDefinitions>
               <ColumnDefinition Width="Auto" />
               <ColumnDefinition Width="*" />
               <ColumnDefinition Width="Auto" />
           </Grid.ColumnDefinitions>
           <ui:SymbolIcon Grid.Column="0" Symbol="LocalLanguage20" Style="{StaticResource IrisRowIcon}" />
           <StackPanel Grid.Column="1" VerticalAlignment="Center">
               <TextBlock Style="{StaticResource IrisRowTitle}" Text="{x:Static app:UiText.LanguageTitle}" />
               <TextBlock x:Name="UiLanguageHint" Style="{StaticResource IrisRowDescription}" Text="{Binding InterfaceLanguageHint}" />
           </StackPanel>
           <ComboBox x:Name="UiLanguageCombo" Grid.Column="2" MinWidth="160"
                     Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center"
                     ItemsSource="{x:Static local:SettingsViewModel.InterfaceLanguages}"
                     IsEnabled="{Binding CanEdit}"
                     SelectedValuePath="Value" DisplayMemberPath="Name"
                     SelectedValue="{Binding InterfaceLanguage.Value, Mode=TwoWay}"
                     AutomationProperties.Name="{x:Static app:UiText.LanguageTitle}" />
       </Grid>
   </Border>
   ```
4. **更新分组小标题 + 自动检查更新开关行卡片 + 更新动作卡**：
   ```xml
   <TextBlock Style="{StaticResource IrisSectionHeader}" Text="{x:Static app:UiText.UpdateTitle}" />
   <Border Style="{StaticResource IrisRowCard}">
       <Grid>
           <Grid.ColumnDefinitions>
               <ColumnDefinition Width="Auto" />
               <ColumnDefinition Width="*" />
               <ColumnDefinition Width="Auto" />
           </Grid.ColumnDefinitions>
           <ui:SymbolIcon Grid.Column="0" Symbol="ArrowSync20" Style="{StaticResource IrisRowIcon}" />
           <StackPanel Grid.Column="1" VerticalAlignment="Center">
               <TextBlock Style="{StaticResource IrisRowTitle}" Text="{x:Static app:UiText.UpdatePreference}" />
           </StackPanel>
           <CheckBox x:Name="CheckForUpdatesCheckBox" Grid.Column="2"
                     Style="{StaticResource IrisSwitchCheckBox}"
                     Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center"
                     IsChecked="{Binding CheckForUpdates.Value, Mode=TwoWay}"
                     IsEnabled="{Binding CanEdit}"
                     AutomationProperties.Name="{x:Static app:UiText.UpdatePreference}" />
       </Grid>
   </Border>
   <Border Style="{StaticResource IrisCard}">
       <StackPanel>
           <TextBlock Style="{StaticResource IrisStatusText}" Text="{Binding CurrentVersion}"
                      AutomationProperties.Name="{x:Static app:UiText.UpdateTitle}" />
           <TextBlock Style="{StaticResource IrisStatusText}" Margin="0,4,0,0" Text="{Binding UpdateStatus}" />
           <TextBlock Style="{StaticResource IrisStatusText}" Margin="0,4,0,0" Text="{Binding UpdateDetail}" />
           <ProgressBar Margin="0,8,0,8" Value="{Binding UpdateProgress, Mode=OneWay}" Minimum="0" Maximum="100" Height="4" />
           <WrapPanel>
               <ui:Button Content="{x:Static app:UiText.UpdateCheck}" Command="{Binding CheckUpdateCommand}" Margin="0,0,6,6" />
               <ui:Button Content="{x:Static app:UiText.UpdateDownload}" Command="{Binding DownloadUpdateCommand}" Margin="0,0,6,6" />
               <ui:Button Content="{x:Static app:UiText.UpdateCancel}" Command="{Binding CancelUpdateCommand}" Margin="0,0,6,6" />
               <ui:Button Content="{x:Static app:UiText.UpdateInstall}" Command="{Binding InstallUpdateCommand}" Margin="0,0,6,6" />
           </WrapPanel>
       </StackPanel>
   </Border>
   ```
5. **外观与主题行卡片**（保留上方 `<!-- 外观与主题卡片 -->`，横向行）：
   - 图标 `Color20`，第 1 列标题 `UiText.SettingsThemeGroup`（`IrisRowTitle`）+ 说明 `UiText.SettingsThemeDescription`（`IrisRowDescription`），第 2 列 `ComboBox`（`MinWidth="160" Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center" SelectedValue="{Binding Theme, Mode=TwoWay}" SelectedValuePath="Tag" IsEnabled="{Binding CanEdit}" AutomationProperties.Name="{x:Static app:UiText.SettingsThemeGroup}"`，内部原样保留 `AppTheme.System` / `Light` / `Dark` 三个 `ComboBoxItem`；去掉旧的 `Width="200" HorizontalAlignment="Left"`）。
6. **系统启动与通知分组小标题 + 两张横向开关行卡片**（保留上方 `<!-- 系统启动与通知卡片 -->`）：
   - `<TextBlock Style="{StaticResource IrisSectionHeader}" Text="{x:Static app:UiText.SettingsStartupGroup}" />`
   - 第一张：图标 `Power20`，标题 `UiText.SettingsLaunchAtStartup`，第 2 列 `CheckBox`（`Style="{StaticResource IrisSwitchCheckBox}" Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center" IsChecked="{Binding LaunchAtStartup, Mode=TwoWay}" IsEnabled="{Binding CanEdit}" AutomationProperties.Name="{x:Static app:UiText.SettingsLaunchAtStartup}"`，去掉 `ContentTemplate="{StaticResource WrappingCheckBoxContent}"` 与 `Content`）。
   - 第二张：图标 `Power20`，标题 `UiText.SettingsShowStartupNotification`，第 2 列 `CheckBox`（`Style="{StaticResource IrisSwitchCheckBox}" Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center" IsChecked="{Binding ShowStartupNotification, Mode=TwoWay}" IsEnabled="{Binding CanEdit}" AutomationProperties.Name="{x:Static app:UiText.SettingsShowStartupNotification}"`，去掉 `ContentTemplate` 与 `Content`）。
7. **日志级别行卡片**（保留上方 `<!-- 日志级别卡片 -->`，横向行）：
   - 图标 `DocumentText20`，标题 `UiText.SettingsLogGroup`，说明 `UiText.SettingsLogDescription`，第 2 列 `ComboBox`（`MinWidth="160" Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center" SelectedValue="{Binding LogLevel, Mode=TwoWay}" SelectedValuePath="Tag" IsEnabled="{Binding CanEdit}" AutomationProperties.Name="{x:Static app:UiText.SettingsLogGroup}"`，内部原样保留 `Verbose` / `Debug` / `Information` / `Warning` / `Error` 五个 `ComboBoxItem`）。

若构建时任意首选图标名在 `net10.0-windows7.0\Wpf.Ui.dll` 报不存在，只把该行 `Symbol` 与 `ExpectedIcons("general")` 对应项一并换成退路 `Settings20`，并在完成报告偏差清单记一笔。

- [ ] **Step 5：跑本任务测试与「设置全集」确认全绿**

先跑本任务过滤组：

```bash
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsRowCardLayoutTests|FullyQualifiedName~UpdatePreferenceUiTests|FullyQualifiedName~UpdateRenderingTests|FullyQualifiedName~LocalizationTests|FullyQualifiedName~LocalizationRenderingTests|FullyQualifiedName~IrisSwitchStyleTests"
```

期望：全绿（0 失败）。特别关注 `UpdateRenderingTests`（`Capture` 里 `CheckForUpdatesCheckBox.Parent` 为行 `Grid`，`((FrameworkElement)card).BringIntoView()` 正常滚动，两段截图正常写出）。若 `LocalizationTests.cs` 命中旧复选框文字断言则按 2.9(a) 改为按 `AutomationProperties.Name` 或行标题查找并记入报告。

再跑「设置全集」：

```bash
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~Iris.App.Tests.Settings|FullyQualifiedName~Iris.App.Tests.Themes|FullyQualifiedName~Iris.App.Tests.Localization|FullyQualifiedName~Iris.App.Tests.Updates|FullyQualifiedName~MouseRenderingTests|FullyQualifiedName~HistoryRenderingTests|FullyQualifiedName~ThemeServicePreviewTests"
```

期望：全绿，0 警告，0 错误。

- [ ] **Step 6：人工复看渲染图 + 按路径暂存（严禁提交）+ 自检清单**

复看 `UpdateRenderingTests` 与 `LocalizationRenderingTests` 产出的常规页浅/深色、中/英文、默认/最小尺寸截图：确认 6 张带图标行卡片与 1 张更新动作卡层次分明、英文 `600` 宽下四个更新按钮自动折行不裁切、开关居右且不显示文字。

按路径暂存（不执行 `git commit`）：

```bash
git add src/Iris.App/Settings/SettingsWindow.xaml tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs
git status --short
```

逐条自检：
1. 常规页内 `ui:Card` 数量为 `0`，`IrisRowCard` 为 `6`（每行左起 20 号 `IrisRowIcon`），`IrisCard` 动作卡为 `1`。
2. 3 个 `CheckBox` 均套 `IrisSwitchCheckBox`、无 `Content`、带 `AutomationProperties.Name`；常规页内不再引用 `WrappingCheckBoxContent`（全文件其余页尚在使用，本任务不删资源定义）。
3. 5 条既有中文注释原样保留，`src/` 无新增中文字面量或中文注释，`.resx` / `app-catalog.json` / `UiText` 零改动。
4. `git log -1 --oneline` 仍为 `dc08c48`。

**验证命令：** `FullyQualifiedName~SettingsRowCardLayoutTests|FullyQualifiedName~UpdatePreferenceUiTests|FullyQualifiedName~UpdateRenderingTests|FullyQualifiedName~LocalizationTests|FullyQualifiedName~LocalizationRenderingTests|FullyQualifiedName~IrisSwitchStyleTests` —— 预期全绿；再跑「设置全集」，预期全绿。

**要改的既有测试：** 预期无（`UpdatePreferenceUiTests.cs:74` 按名字强转 `CheckBox`、`UpdateRenderingTests.cs:89` 对 `.Parent` 调 `BringIntoView()` 均与新版面兼容；若 `LocalizationTests.cs` 命中复选框文字断言则按 2.9(a) 改并记入报告）。

### Task 7: 划词页

**目的：** 把划词页的四张 `ui:Card` 改成行卡片。

**Files:**
- Modify: `src/Iris.App/Settings/SettingsWindow.xaml`——划词页 :864–934（四张卡 :881–899、:900–913、:914–923、:924–932；从后往前改）
- Test: `tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs`（加 `selection` 一行）

**范围与要点：**
- 横向行：`MouseModeComboBox`（:884）、复选框（:888）、`MouseDragTextBox`（:890）、`MouseHideTextBox`（:893）、`SelectionOrderComboBox`（:903）、`SelectionTimeoutTextBox`（:908）、两个复选框（:916 / :919）。
- 纵向行：`MouseExcludedTextBox`（:896）、`SelectionProcessesTextBox`（:929），`Height=130` 保留。
- `SelectionErrorTextBlock`（:878）保持独立，位置在页标题下、第一张卡片上。
- 说明文字全部保留（划词四句、鼠标两句，A6.12 第 11 条）。（注：经核实源码，`UiText.SelectionHotkeyDescription` 不在划词页而在快捷键页的 `IrisAction.SelectionTranslate` 项上，`LocalizationTests.cs:270–285` 测的是 `CurrentPageKey = "hotkeys"`，由 Task 9 守护；划词页原有四张 `ui:Card` 均无独立于设置项之外的卡片标题，按 S0-12「不新增分组名」直接拆为 10 张行卡片，不虚构分组小标题。）
- 图标：**逐行选**（C6），见详细步骤的「行 → 首选图标 → 退路图标」表（规矩与起点见 S0-4「图标」）；本页十张行卡片（上面的八张横向行、两张纵向行），表里一行不漏。S0-4 的表已为「取词顺序、超时」选了 `Options20`，照抄作这两行的首选（在详细步骤的表里拆成两行各写各的）。退路为页面图标 `CursorClick20`。
- 可编辑容器（:876）照旧。
- 保持：`SelectionScrollViewer`；六个文本框的名字与 `TextBox` 类型（其中 `SelectionTimeoutTextBox`、`SelectionProcessesTextBox` 还被 `FlushFocusedBinding` 按名字找）。
- 风险：R1、R4、R11；金丝雀 `SelectionSettingsRenderingTests` 就在这一页上，改完必须重跑。

**步骤提纲：**
- [ ] 给 `SettingsRowCardLayoutTests` 加 `selection` 一行
- [ ] 跑，确认失败
- [ ] 从第四张卡往前改到第一张
- [ ] 跑本任务过滤组与「设置全集」；`git add`

**详细步骤：**

> **声明：** 代码与命令尚未运行过；行号以 `dc08c48` 为参考，开工 Step 1 先 `grep` 重取行号。

#### 详细步骤

- [ ] **Step 1：开工核实——重新定位划词页行号与中文注释**

```bash
git status --short -- src/Iris.App/Settings/SettingsWindow.xaml tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs
grep -n 'x:Name="SelectionScrollViewer"\|x:Name="SelectionErrorTextBlock"\|x:Name="MouseModeComboBox"\|x:Name="SelectionProcessesTextBox"\|<!-- 右侧快捷键设置内容 -->' src/Iris.App/Settings/SettingsWindow.xaml
```

确认划词页起止行号及该区间内中文注释数量（`dc08c48` 核实划词页内为 0 条，紧接其后的 `<!-- 右侧快捷键设置内容 -->` 属快捷键页，严禁触碰）。

- [ ] **Step 2：在 `SettingsRowCardLayoutTests.cs` 登记 `"selection"` 页（TDD 红灯）**

修改 `tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs`：

1. **四个 `[Theory]` 均追加 `"selection"`**：
   - `Page_IsBuiltFromIconRowCards` 加 `[InlineData("selection", 10)]`（并确认断言兼容纵向行：第 1 行存在 `Grid.GetRow == 1 && Grid.GetColumn == 0 && Grid.GetColumnSpan == 3` 的跨列控件时，卡片 `Grid.RowDefinitions.Count` 为 `2`，横向行为 `0` 或 `1`）。
   - `Page_SwitchesHaveNoContentAndAreNamedAfterTheirRow` 加 `[InlineData("selection", 3)]`
   - `Page_KeepsEveryExistingText` 加 `[InlineData("selection")]`
   - `Page_RowsFitTheContentAreaAtNarrowWidths` 加 `[InlineData("selection", 680)]`、`[InlineData("selection", 600)]`
2. **`Page`、`ExpectedTexts`、`ExpectedIcons` 登记 `"selection"`**（下表 10 行首选图标均已通过 `lib\net462\Wpf.Ui.dll` 反射核实为 `True`，退路一律为页面图标 `CursorClick20`）：

   | 行（按行标题键） | 形态 | 首选图标 | 退路图标 |
   |---|---|---|---|
   | `UiText.MouseModeLabel`（鼠标划词方式） | 横向 | `CursorClick20` | `CursorClick20` |
   | `UiText.MouseBeamLabel`（仅在文本光标下触发） | 横向开关 | `CursorHover20` | `CursorClick20` |
   | `UiText.MouseDragLabel`（最小拖拽距离） | 横向数字框 | `Cursor20` | `CursorClick20` |
   | `UiText.MouseHideLabel`（图标自动隐藏毫秒） | 横向数字框 | `Timer20` | `CursorClick20` |
   | `UiText.MouseExcludedLabel`（划词排除进程） | 纵向多行框 | `DismissSquare20` | `CursorClick20` |
   | `UiText.SelectionOrderLabel`（取词读取顺序） | 横向 | `Options20` | `CursorClick20` |
   | `UiText.SelectionTimeoutLabel`（剪贴板读取超时） | 横向数字框 | `Options20` | `CursorClick20` |
   | `UiText.SelectionRestoreLabel`（恢复原剪贴板） | 横向开关 | `ClipboardPaste20` | `CursorClick20` |
   | `UiText.SelectionEmptyLabel`（空选区打开输入翻译） | 横向开关 | `Clipboard20` | `CursorClick20` |
   | `UiText.SelectionProcessesLabel`（禁用剪贴板取词进程） | 纵向多行框 | `ShieldKeyhole20` | `CursorClick20` |

   - `ExpectedTexts("selection")` 包含：`SettingsNavSelection`、`MouseModeLabel`、`MouseExplanation`、`MouseBeamLabel`、`MouseDragLabel`、`MouseHideLabel`、`MouseExcludedLabel`、`SelectionOrderLabel`、`SelectionSafetyDescription`、`SelectionTimeoutLabel`、`SelectionTimeoutDescription`、`SelectionRestoreLabel`、`SelectionRestoreDescription`、`SelectionEmptyLabel`、`SelectionEmptyDescription`、`SelectionProcessesLabel`、`SelectionProcessesDescription`（共 17 条，与改前一条不差）。
3. **`NamedControls_KeepNameTypeBindingAndEditGate` 追加本页 7 个具名输入控件**：
   - `("MouseModeComboBox", typeof(ComboBox), Selector.SelectedValueProperty, "Selection.MouseMode.Value", 2, null)`
   - `("MouseDragTextBox", typeof(TextBox), TextBox.TextProperty, "Selection.MinDragDistanceText", 2, 120d)`
   - `("MouseHideTextBox", typeof(TextBox), TextBox.TextProperty, "Selection.IconAutoHideText", 2, 120d)`
   - `("MouseExcludedTextBox", typeof(TextBox), TextBox.TextProperty, "Selection.ExcludedProcessesText", 0, null)`（纵向行第 1 行跨 3 列，断言 `Grid.GetRow == 1 && Grid.GetColumnSpan == 3 && Height == 130 && AcceptsReturn`）
   - `("SelectionOrderComboBox", typeof(ComboBox), Selector.SelectedValueProperty, "Selection.ReadOrderChoice", 2, null)`
   - `("SelectionTimeoutTextBox", typeof(TextBox), TextBox.TextProperty, "Selection.ClipboardTimeoutText", 2, 120d)`
   - `("SelectionProcessesTextBox", typeof(TextBox), TextBox.TextProperty, "Selection.ClipboardUnsafeProcessesText", 0, null)`（纵向行第 1 行跨 3 列，断言 `Grid.GetRow == 1 && Grid.GetColumnSpan == 3 && Height == 130 && AcceptsReturn`）
   - 同时断言 `SelectionErrorTextBlock` 仍位于 `SelectionScrollViewer` 第一层面板内、第一张行卡片之上，且绑定 `Selection.ErrorMessage`。

- [ ] **Step 3：跑新增测试确认失败（红灯）**

```bash
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsRowCardLayoutTests"
```

期望：`selection` 相关新用例失败（仍有 4 张 `ui:Card`、0 张 `IrisRowCard`、文本框宽仍为 `140`），前序页面保持全绿。

- [ ] **Step 4：改写 `SettingsWindow.xaml` 划词页为 10 张行卡片（从第 4 张旧卡往前改到第 1 张）**

修改 `src/Iris.App/Settings/SettingsWindow.xaml` 划词页片段：

1. `SelectionScrollViewer` 去掉 `Padding="24,16,24,16"`；第一层面板写 `<StackPanel Margin="{StaticResource IrisPagePadding}" IsEnabled="{Binding CanEdit}">`。
2. 页标题 `<TextBlock Style="{StaticResource IrisPageTitle}" Text="{x:Static app:UiText.SettingsNavSelection}" />`；紧跟保留 `SelectionErrorTextBlock`（原 `x:Name`、`Text`、`Visibility`、`Foreground` 不变，边距用 `Margin="{StaticResource IrisCardGap}"`）。
3. 按 Step 2 表格依次写出 10 张 `<Border Style="{StaticResource IrisRowCard}">`：
   - **横向行（第 1–4、6–9 行）**：`Grid` 三列 `Auto | * | Auto`；第 0 列 `<ui:SymbolIcon Grid.Column="0" Symbol="…" Style="{StaticResource IrisRowIcon}" />`；第 1 列 `StackPanel VerticalAlignment="Center"` 放 `IrisRowTitle`（与有说明行的 `IrisRowDescription`）；第 2 列放控件（`Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center"`）：
     - 两个 `ComboBox`（`MouseModeComboBox`、`SelectionOrderComboBox`）设 `MinWidth="160"`，保留原 `ItemsSource`、`DisplayMemberPath`、`SelectedValuePath`、`SelectedValue`。
     - 三个数字 `TextBox`（`MouseDragTextBox`、`MouseHideTextBox`、`SelectionTimeoutTextBox`）设 `Width="120"`（去掉旧 `Width="140" HorizontalAlignment="Left"`），保留原 `Text` 绑定与 `UpdateSourceTrigger=PropertyChanged`。
     - 三个 `CheckBox` 套 `Style="{StaticResource IrisSwitchCheckBox}"`，去掉 `Content` 与 `ContentTemplate="{StaticResource WrappingCheckBoxContent}"`，补 `AutomationProperties.Name` 为对应行标题 `UiText`。
   - **纵向行（第 5 行 `MouseExcludedTextBox`、第 10 行 `SelectionProcessesTextBox`）**：`Grid` 三列 `Auto | * | Auto`、两行 `Auto / Auto`；第 0 行放 `IrisRowIcon` + 第 1 列标题（及第 10 行的说明）；第 1 行放多行 `TextBox`（`Grid.Row="1" Grid.Column="0" Grid.ColumnSpan="3" Margin="0,8,0,0" AcceptsReturn="True" TextWrapping="Wrap" VerticalScrollBarVisibility="Auto" Height="130"`，原 `Text` 绑定与 `UpdateSourceTrigger=PropertyChanged` 原封不动）。

- [ ] **Step 5：跑本任务测试与「设置全集」确认全绿**

```bash
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsRowCardLayoutTests|FullyQualifiedName~SelectionSettings|FullyQualifiedName~MouseSettingsUiTests|FullyQualifiedName~MouseRenderingTests|FullyQualifiedName~LocalizationTests"
```

期望：全绿（`SelectionSettingsRenderingTests` 的背景取点已在 Task 4 挪入 `SelectionScrollViewer` 内容区，两个多行框 `ActualWidth > 100` 且右缘 `<= size.Width` 继续成立；若取点碰上某张行卡片底色导致阈值偏差，按 2.9(c) 微调空隙取点并记入报告「改过的既有测试」）。随后跑「设置全集」确认全绿、0 警告。

- [ ] **Step 6：人工复看渲染图 + 按路径暂存（严禁提交）+ 自检清单**

复看 `SelectionSettingsRenderingTests` 与 `MouseRenderingTests` 产出的浅/深色截图，确认 8 张横向卡与 2 张 130px 高多行纵向卡排布整齐、无裁切。

```bash
git add src/Iris.App/Settings/SettingsWindow.xaml tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs
git status --short
```

自检：`SelectionScrollViewer` 内 `ui:Card` 为 `0`、`IrisRowCard` 为 `10`、`WrappingCheckBoxContent` 引用为 `0`；`src/` 无新增中文；`git log -1 --oneline` 仍为 `dc08c48`。

**验证命令：** `FullyQualifiedName~SettingsRowCardLayoutTests|FullyQualifiedName~SelectionSettings|FullyQualifiedName~MouseSettingsUiTests|FullyQualifiedName~MouseRenderingTests|FullyQualifiedName~LocalizationTests` —— 预期全绿；再跑「设置全集」，预期全绿。

**要改的既有测试：** 预期无（`SelectionSettingsRenderingTests` 的取点在任务 4 已挪到内容区；若落到卡片底色上按 2.9(c) 微调空隙坐标并记入清单）。

### Task 8: 网络页

**目的：** 把网络页改成行卡片加一张连通性测试的动作卡。

**Files:**
- Modify: `src/Iris.App/Settings/SettingsWindow.xaml`——网络页 :128–167
- Test: `tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs`（加 `network` 一行）

**范围与要点：**
- 可编辑容器内（:133–152），每项一张行卡片：`NetworkModeCombo`（:135，横向）、`NetworkBypassCheck`（:137，横向加开关）、`NetworkUrlTextBox`（:140，**纵向**）、`NetworkCredentialsCombo`（:143，横向；凭据三个选项的说明全部保留）、`NetworkUserNameTextBox`（:147，横向，`Width="220"`）、`NetworkPasswordBox`（:149，横向，`Width="220"`，仍是原生 `PasswordBox`，`PasswordChanged` 处理不动）。
- 可编辑容器外的保持在容器外：`NetworkValidationText`（:153）独立放在行卡片之后；`NetworkEndpointCombo`（:155）、`NetworkTestButton`（:161）、`NetworkCancelButton`（:162）、`NetworkStatusText`（:164）、`NetworkResultText`（:165）→ 一张动作卡。
- 图标：**逐行选**（C6），见详细步骤的「行 → 首选图标 → 退路图标」表（规矩与起点见 S0-4「图标」）；本页六张行卡片，表里一行不漏。S0-4 的表已选好的照抄作首选：代理地址 `Server20`；凭据方式、用户名、密码 `Key20`（在详细步骤的表里拆成三行各写各的）。退路为页面图标 `Globe20`。动作卡不带图标。
- 页级说明 `UiText.NetworkDescription`（:132）按 C7 保留在页标题（:131）下面、可编辑容器（:133）之外（`IrisRowDescription` + `IrisCardGap`，见 S0-4）。
- 保持：`NetworkPageScroll`；`NetworkUrlTextBox`、`NetworkUserNameTextBox` 的名字与类型（`FlushFocusedBinding` 按名字找）；`NetworkCancelButton` 保存期间仍可用。
- 原有的按条件启用（用户名 / 密码两行原包在 `:145 <StackPanel IsEnabled="{Binding Network.IsReplacing}">` 内置灰）原样保留，不改绑定表达式。
- 风险：R1、R4；`NetworkSettingsRenderingTests` 要求每个可见 `Button` 右边缘不超出窗口宽。

**步骤提纲：**
- [ ] 给 `SettingsRowCardLayoutTests` 加 `network` 一行，并加一条用例：测试区五个元素不在任何 `IsEnabled` 绑到 `CanEdit` 的祖先之下
- [ ] 跑，确认失败
- [ ] 从动作卡往前改到第一行
- [ ] 跑本任务过滤组与「设置全集」；`git add`

**详细步骤：**

> **声明：** 代码与命令尚未运行过；行号以 `dc08c48` 为参考，开工 Step 1 先 `grep` 重取行号。

#### 详细步骤

- [ ] **Step 1：开工核实——重新定位网络页行号与测试基线**

```bash
git status --short -- src/Iris.App/Settings/SettingsWindow.xaml tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs
grep -n 'x:Name="NetworkPageScroll"\|x:Name="NetworkPasswordBox"\|x:Name="NetworkValidationText"\|x:Name="HistoryPageScroll"' src/Iris.App/Settings/SettingsWindow.xaml
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~NetworkSettings|FullyQualifiedName~SettingsRowCardLayoutTests"
```

确认网络页（`NetworkPageScroll` 至 `HistoryPageScroll` 之前）内无中文注释且基线测试全绿。

- [ ] **Step 2：在 `SettingsRowCardLayoutTests.cs` 登记 `"network"` 页与连通性动作卡测试（TDD 红灯）**

修改 `tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs`：

1. **四个 `[Theory]` 追加 `"network"`**：
   - `Page_IsBuiltFromIconRowCards` 加 `[InlineData("network", 6)]`
   - `Page_SwitchesHaveNoContentAndAreNamedAfterTheirRow` 加 `[InlineData("network", 1)]`
   - `Page_KeepsEveryExistingText` 加 `[InlineData("network")]`
   - `Page_RowsFitTheContentAreaAtNarrowWidths` 加 `[InlineData("network", 680)]`、`[InlineData("network", 600)]`
2. **`Page`、`ExpectedTexts`、`ExpectedIcons` 登记 `"network"`**（下表首选图标均已通过 `lib\net462\Wpf.Ui.dll` 反射核实为 `True`，退路一律为页面图标 `Globe20`）：

   | 行（按行标题键） | 形态 | 首选图标 | 退路图标 |
   |---|---|---|---|
   | `UiText.NetworkModeLabel`（代理模式） | 横向 | `Globe20` | `Globe20` |
   | `UiText.NetworkBypass`（绕过本地地址） | 横向开关 | `GlobeProhibited20` | `Globe20` |
   | `UiText.NetworkUrlLabel`（代理服务器地址） | 纵向长文本 | `Server20` | `Globe20` |
   | `UiText.NetworkCredentialLabel`（凭据方式） | 横向 | `Key20` | `Globe20` |
   | `UiText.NetworkUserName`（用户名） | 横向短文本 | `Key20` | `Globe20` |
   | `UiText.NetworkPassword`（密码） | 横向密码框 | `Key20` | `Globe20` |

   - `ExpectedTexts("network")` 包含：`NetworkTitle`、`NetworkDescription`、`NetworkModeLabel`、`NetworkBypass`、`NetworkBypassDescription`、`NetworkUrlLabel`、`NetworkUrlHint`、`NetworkCredentialLabel`、`NetworkCredentialHint`、`NetworkUserName`、`NetworkPassword`、`NetworkTargetLabel`、`NetworkTargetHint`（共 13 条静态 `TextBlock` 文字；两个测试按钮的 `Content` 在下方专属 `[Fact]` 断言）。
3. **`NamedControls_KeepNameTypeBindingAndEditGate` 追加本页具名可编辑控件**：
   - `("NetworkModeCombo", typeof(ComboBox), Selector.SelectedValueProperty, "Network.Mode.Value", 2, null)`
   - `("NetworkBypassCheck", typeof(CheckBox), ToggleButton.IsCheckedProperty, "Network.BypassLocal.Value", 2, null)`
   - `("NetworkUrlTextBox", typeof(TextBox), TextBox.TextProperty, "Network.Url.Value", 0, null)`（纵向行第 1 行跨 3 列）
   - `("NetworkCredentialsCombo", typeof(ComboBox), Selector.SelectedValueProperty, "Network.Credentials.Value", 2, null)`
   - `("NetworkUserNameTextBox", typeof(TextBox), TextBox.TextProperty, "Network.UserName.Value", 2, 220d)`
   - 单独断言 `NetworkPasswordBox` 确切类型为原生 `System.Windows.Controls.PasswordBox`、`Width == 220d`、位于第 2 列、`HasEditGate` 为真，且祖先链存在绑定 `"Network.IsReplacing"` 的 `UIElement.IsEnabledProperty`。
4. **新增 `[Fact] NetworkPage_TestActionCardStaysOutsideEditGateAndKeepsTestControls()`**：
   - 打开 `"network"` 页，断言 `UiText.NetworkDescription` 套 `IrisRowDescription` 且 `HasEditGate` 为 `false`（C7）。
   - 断言 `NetworkValidationText` 套 `IrisRowError`、初始为空时 `Collapsed`、`HasEditGate` 为 `false`。
   - 断言页面内恰有 1 张 `IrisCard` 动作卡，内部包含 `NetworkEndpointCombo`（自身绑 `CanEdit`）、`NetworkTestButton`、`NetworkCancelButton`、`NetworkStatusText`、`NetworkResultText`，且两个测试按钮与两个状态文字的 `HasEditGate` 均为 **`false`**（保存或只读期间不被外层 `CanEdit` 容器吞掉）。

- [ ] **Step 3：跑新增测试确认失败（红灯）**

```bash
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsRowCardLayoutTests"
```

期望：`network` 相关新用例失败（`NetworkPageScroll` 仍有 `Padding="20,12"`、0 张 `IrisRowCard`、0 张 `IrisCard`），前序页面全绿。

- [ ] **Step 4：改写 `SettingsWindow.xaml` 网络页为 6 张行卡片 + 1 张连通性测试动作卡**

只改 `NetworkPageScroll` 块（不动紧随其后的 `HistoryPageScroll`）：

1. `NetworkPageScroll` 去掉 `Padding="20,12"`；外层 `<StackPanel>` 去掉 `MaxWidth="620" HorizontalAlignment="Stretch"`，改为 `<StackPanel Margin="{StaticResource IrisPagePadding}">`。
2. 页标题与页级说明（在 `CanEdit` 容器外）：
   ```xml
   <TextBlock Style="{StaticResource IrisPageTitle}" Text="{x:Static app:UiText.NetworkTitle}" />
   <TextBlock Style="{StaticResource IrisRowDescription}" Margin="{StaticResource IrisCardGap}"
              Text="{x:Static app:UiText.NetworkDescription}" />
   ```
3. `<StackPanel IsEnabled="{Binding CanEdit}">` 内依次放 6 张 `<Border Style="{StaticResource IrisRowCard}">`：
   - 第 1 行（横向，`Globe20`）：标题 `UiText.NetworkModeLabel`；第 2 列 `NetworkModeCombo`（`MinWidth="160" Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center"`，原属性与绑定全留）。
   - 第 2 行（横向开关，`GlobeProhibited20`）：标题 `UiText.NetworkBypass` + 说明 `UiText.NetworkBypassDescription`；第 2 列 `NetworkBypassCheck`（`Style="{StaticResource IrisSwitchCheckBox}" Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center" IsChecked="{Binding Network.BypassLocal.Value}" AutomationProperties.Name="{x:Static app:UiText.NetworkBypass}"`，去掉 `WrappingCheckBoxContent` 与 `Content`）。
   - 第 3 行（纵向，`Server20`）：第 0 行图标 + 标题 `UiText.NetworkUrlLabel` + 说明 `UiText.NetworkUrlHint`；第 1 行 `NetworkUrlTextBox`（`Grid.Row="1" Grid.Column="0" Grid.ColumnSpan="3" Margin="0,8,0,0" MinHeight="32" HorizontalScrollBarVisibility="Auto"`，原 `Text` 绑定不变）。
   - 第 4 行（横向，`Key20`）：标题 `UiText.NetworkCredentialLabel` + 说明 `UiText.NetworkCredentialHint`；第 2 列 `NetworkCredentialsCombo`（`MinWidth="160" Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center"`，原绑定全留）。
   - `<StackPanel IsEnabled="{Binding Network.IsReplacing}">` 包住第 5、6 两张横向行卡片（均用 `Key20`）：
     - 第 5 行：标题 `UiText.NetworkUserName`；第 2 列 `NetworkUserNameTextBox`（`Width="220" MinHeight="32" Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center"`，原 `Text` 绑定不变）。
     - 第 6 行：标题 `UiText.NetworkPassword`；第 2 列原生 `PasswordBox x:Name="NetworkPasswordBox"`（`Width="220" MinHeight="32" Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center" PasswordChanged="OnNetworkPasswordChanged"`）。
4. 关闭 `</StackPanel>`（结束 `CanEdit` 容器）后，放 `NetworkValidationText` 与连通性测试动作卡：
   ```xml
   <TextBlock x:Name="NetworkValidationText" Style="{StaticResource IrisRowError}"
              Margin="{StaticResource IrisCardGap}" Text="{Binding Network.ErrorMessage}"
              Foreground="{DynamicResource SystemFillColorCautionBrush}" />
   <Border Style="{StaticResource IrisCard}">
       <StackPanel>
           <TextBlock Style="{StaticResource IrisRowTitle}" Margin="0,0,0,6" Text="{x:Static app:UiText.NetworkTargetLabel}" />
           <ComboBox x:Name="NetworkEndpointCombo" ItemsSource="{Binding NetworkEndpoints}"
                     SelectedItem="{Binding SelectedNetworkEndpoint, Mode=TwoWay}"
                     IsEnabled="{Binding CanEdit}" MinHeight="32">
               <ComboBox.ItemTemplate>
                   <DataTemplate>
                       <TextBlock Text="{Binding Name}" TextWrapping="Wrap" MaxWidth="320" />
                   </DataTemplate>
               </ComboBox.ItemTemplate>
           </ComboBox>
           <TextBlock Style="{StaticResource IrisStatusText}" Margin="0,6,0,4" Text="{Binding NetworkEndpointHint}" />
           <TextBlock Style="{StaticResource IrisRowDescription}" Margin="0,0,0,10" Text="{x:Static app:UiText.NetworkTargetHint}" />
           <WrapPanel>
               <Button x:Name="NetworkTestButton" Content="{x:Static app:UiText.NetworkTest}" Command="{Binding TestNetworkCommand}" Margin="0,0,8,8" />
               <Button x:Name="NetworkCancelButton" Content="{x:Static app:UiText.NetworkCancel}" Command="{Binding CancelNetworkCommand}" Margin="0,0,0,8" />
           </WrapPanel>
           <TextBlock x:Name="NetworkStatusText" Style="{StaticResource IrisStatusText}" Margin="0,4,0,8"
                      Text="{Binding NetworkTestStatus}" Visibility="{Binding HasNetworkStatus, Converter={StaticResource BoolToVis}}" />
           <TextBlock x:Name="NetworkResultText" Style="{StaticResource IrisStatusText}" Margin="0,4,0,0"
                      Text="{Binding NetworkResultText}" Visibility="{Binding HasNetworkResult, Converter={StaticResource BoolToVis}}" />
       </StackPanel>
   </Border>
   ```

- [ ] **Step 5：跑本任务测试与「设置全集」确认全绿**

```bash
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsRowCardLayoutTests|FullyQualifiedName~NetworkSettings"
```

期望：全绿（含 `NetworkSettingsFoundationTests`、`NetworkSettingsUiTests`、`NetworkSettingsRenderingTests` 每个可见 `Button` 右缘不越出窗口宽）。随后跑「设置全集」确认全绿、0 警告。

- [ ] **Step 6：人工复看渲染图 + 按路径暂存（严禁提交）+ 自检清单**

复看 `NetworkSettingsRenderingTests` 产出的浅/深色渲染图，确认 6 张行卡片与底部连通性动作卡排布清晰、600 窄宽下 `Width="220"` 输入框与测试按钮不裁切。

```bash
git add src/Iris.App/Settings/SettingsWindow.xaml tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs
git status --short
```

自检：`NetworkPasswordBox` 仍为原生 `PasswordBox`；`NetworkBypassCheck` 已去掉 `WrappingCheckBoxContent`；`src/` 无新增中文；`git log -1 --oneline` 仍为 `dc08c48`。

**验证命令：** `FullyQualifiedName~SettingsRowCardLayoutTests|FullyQualifiedName~NetworkSettings` —— 预期全绿；再跑「设置全集」，预期全绿。

**要改的既有测试：** 无。

### Task 9: 快捷键页

**目的：** 把快捷键页每一项改成纵向行卡片，录制框独占第二行（S0-3）。

**Files:**
- Modify: `src/Iris.App/Settings/SettingsWindow.xaml`——快捷键页 :936–1046（根 `ScrollViewer` :937、内容面板 :951、`ItemsControl` :963–1044、项模板里的卡 :966、启用复选框 :991–995、录制框 :1000、草稿错误 :1006–1020、运行状态 :1023–1038）
- Test: `tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs`（加 `hotkeys` 一行）
- Test（可能要改）: `tests/Iris.App.Tests/Settings/SettingsHotkeysIntegrationTests.cs`（W04）

**范围与要点：**
- 项模板里每一项一张纵向行卡片：第 0 行 = 图标（逐项选，见下一条）、名字（标题）、启用开关（右侧，`IsEnabled` 绑定原样）；第 1 行 = 录制框，跨满；其下再接两行 `Auto` 放草稿错误与运行状态（文字、绑定、可见性条件原样）。
- 图标：**逐行选**（C6），见详细步骤的「行 → 首选图标 → 退路图标」表；退路为页面图标 `Keyboard20`。本页各项出自同一个 `DataTemplate`（:965，`DataType` 是 `local:HotkeyItemDraftViewModel`），所以这一页的「逐行」= 逐个 `IrisAction`：
  - 详细步骤的表按 `Hotkeys` 集合里实际出现的 `IrisAction` 成员一个成员一行（枚举在 `src/Iris.Core/Actions/IrisAction.cs:6–22`，共 12 个成员；哪些会进 `Hotkeys` 由详细步骤的撰写者读填充该集合的代码核实后列全）。
  - 写法：模板里只放**一个** `ui:SymbolIcon`，带一个内联样式 `BasedOn="{StaticResource IrisRowIcon}"`——默认 `<Setter Property="Symbol" Value="Keyboard20"/>`（退路），再为表里首选不是 `Keyboard20` 的每个成员写一条 `<DataTrigger Binding="{Binding Action}" Value="成员名">`，把 `Symbol` 设成该成员的首选。元素上**不写**本地的 `Symbol`（本地值盖过样式触发器）。
  - 已核实（2026-11-22）：`Action` 是项视图模型已有的属性（`HotkeyItemDraftViewModel.cs:65`，类型 `IrisAction`），不改视图模型；`SymbolIcon.Symbol` 是依赖属性（2026-11-23 反射 `lib\net462\Wpf.Ui.dll`，静态字段 `SymbolProperty` 存在、类型 `DependencyProperty`），样式能设。
  - 执行时先核实触发器命中：`SettingsRowCardLayoutTests` 的 `hotkeys` 用例里加一条断言——表里首选不同的两个 `Action`，它们那两行的图标 `Symbol` 不相等。符合 → 照用。不符合 → 把 `Value="成员名"` 改成 `Value="{x:Static actions:IrisAction.成员名}"`，并在窗口根元素加 `xmlns:actions="clr-namespace:Iris.Core.Actions;assembly=Iris.Core"`，再跑同一条断言。
- 页级说明 `UiText.SettingsHotkeysDescription`（:957–960）按 C7 保留、紧跟页标题（:953–956），套 `IrisRowDescription` + `IrisCardGap`；它与页标题一样留在内容面板（:951）里（本页没有 `CanEdit` 容器，见 S0-4）。
- 录制框上的 `MinWidth="360" MaxWidth="500" HorizontalAlignment="Left"` 不动；`HotkeyRecorderBox.xaml(.cs)` 不动。
- 内容面板（:951）：去掉 `MaxWidth=600` 与 `HorizontalAlignment=Left`；`IsEnabled={Binding CanAccessHotkeysTab}` 保留。
- 给根 `ScrollViewer`（:937）补名字 `HotkeysPageScroll`【计划裁决】，方便测试按名字取；不改类型。
- 行卡片本身不可点击、不响应悬停（录制框「鼠标移出即取消录制」，R3；卡片上不要加任何会抢鼠标的东西）。
- 这一页的元素都在 `DataTemplate` 里、本来就没有名字，不涉及 R1。
- 风险最大的是 W04（八个变体）：它按序号取录制框、取第一个内层 `Border`、量边框处亮度（浅色 > 130，深色 < 120）。录制框现在坐在行卡片上，深色下卡片是 `#1AFFFFFF` 叠在着色层上。任务 3 已让 Iris 字典跟着主题走；这里改完先跑 W04，红了先确认取点是否仍落在录制框边框上，再决定动实现还是动取点。
- 宽度核对：680 宽时录制框可用宽度按 S0-3 的预算应为 412；实测不足 360 时走 S0-3 的退路（改 `IrisPagePadding`）。

**步骤提纲：**
- [ ] 给 `SettingsRowCardLayoutTests` 加 `hotkeys` 一行，并加一条用例：窗口宽 680 与 600 时每个录制框的 `ActualWidth` ≥ 360 且右边缘不超出内容区
- [ ] 跑，确认失败
- [ ] 改项模板与内容面板
- [ ] 跑本任务过滤组（重点看 W01、W02、W04、W04B）与「设置全集」；`git add`

**详细步骤：**

> **声明：** 代码与命令尚未运行过；行号以 `dc08c48` 为参考，开工 Step 1 先 `grep` 重取行号。

#### 详细步骤

- [ ] **Step 1：开工核实——重取快捷键页行号、中文注释清单与 `W04` 基线**

```bash
git status --short -- src/Iris.App/Settings/SettingsWindow.xaml tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs
grep -n '<!--.*-->\|SettingsNavHotkeys\|HotkeyRecorderBox' src/Iris.App/Settings/SettingsWindow.xaml | tail -n 25
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsHotkeysIntegrationTests|FullyQualifiedName~LocalizationTests"
```

逐条记下快捷键页（`<!-- 右侧快捷键设置内容 -->` 到紧接底栏 `<!-- 底部状态与操作栏 -->` 之前）内的全部中文注释原文与相对位置，改写 XAML 时一条不少地原样保留在快捷键页对应位置。确认改前 `SettingsHotkeysIntegrationTests`（含 `W04`）与 `LocalizationTests` 全绿。

- [ ] **Step 2：在 `SettingsRowCardLayoutTests.cs` 登记 `"hotkeys"` 页并补录制框宽度与逐动作图标测试（TDD 红灯）**

修改 `tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs`：

1. **展开 `ItemsControl` 容器后再做视觉树断言**：
   - 在 `Page(f, pageKey)` 登记 `"hotkeys" => (ScrollViewer)f.Window.FindName("HotkeysPageScroll"),`（找不到名字时在红灯阶段抛清晰异常）。
   - 注意 `ItemsControl` 的子项容器在 `Show(pageKey)` 后需 `f.Window.UpdateLayout()` 才会生成 `DataTemplate` 视觉树（`f.Show("hotkeys")` 内部若已调 `UpdateLayout()` 直接就绪，否则补一次 `page.UpdateLayout()`）。
2. **四个 `[Theory]` 追加 `"hotkeys"`**：
   - `Page_IsBuiltFromIconRowCards` 加 `[InlineData("hotkeys", 5)]`
   - `Page_SwitchesHaveNoContentAndAreNamedAfterTheirRow` 加 `[InlineData("hotkeys", 5)]`（每个开关的 `AutomationProperties.Name` 绑定 `{Binding DisplayName}`，与同卡第 1 列 `IrisRowTitle` 的 `Text` 完全相等！）
   - `Page_KeepsEveryExistingText` 加 `[InlineData("hotkeys")]`（`ExpectedTexts("hotkeys")` 含页标题 `UiText.SettingsNavHotkeys`、页说明 `UiText.SettingsHotkeysDescription`、以及 5 个默认动作里固定的 `UiText.TrayMenuInputTranslate`、`UiText.TrayMenuClipboardTranslate`、`UiText.SettingsTitle`、`UiText.SettingsNavSelection`、`UiText.SelectionHotkeyDescription`、`UiText.MouseToggle`、`UiText.MouseToggleDescription`）。
   - `Page_RowsFitTheContentAreaAtNarrowWidths` 加 `[InlineData("hotkeys", 680)]`、`[InlineData("hotkeys", 600)]`。
3. **`ExpectedIcons("hotkeys")` 逐 `IrisAction` 图标表**（全部 5 个首选成员均已通过 `lib\net462\Wpf.Ui.dll` 反射核实为 `True`，模板默认退路为页面图标 `Keyboard20`）：

   | `IrisAction` | 行标题来源（`DisplayName`） | 首选图标 | 退路图标 |
   |---|---|---|---|
   | `InputTranslate` | `UiText.TrayMenuInputTranslate` | `Translate20` | `Keyboard20` |
   | `ClipboardTranslate` | `UiText.TrayMenuClipboardTranslate` | `ClipboardTextLtr20` | `Keyboard20` |
   | `OpenSettings` | `UiText.SettingsTitle` | `Settings20` | `Keyboard20` |
   | `SelectionTranslate` | `UiText.SettingsNavSelection` | `CursorClick20` | `Keyboard20` |
   | `ToggleMouseSelection` | `UiText.MouseToggle` | `CursorHover20` | `Keyboard20` |

   ```csharp
   "hotkeys" => new()
   {
       [UiText.TrayMenuInputTranslate] = Wpf.Ui.Controls.SymbolRegular.Translate20,
       [UiText.TrayMenuClipboardTranslate] = Wpf.Ui.Controls.SymbolRegular.ClipboardTextLtr20,
       [UiText.SettingsTitle] = Wpf.Ui.Controls.SymbolRegular.Settings20,
       [UiText.SettingsNavSelection] = Wpf.Ui.Controls.SymbolRegular.CursorClick20,
       [UiText.MouseToggle] = Wpf.Ui.Controls.SymbolRegular.CursorHover20,
   },
   ```
4. **新增 `[Theory] HotkeysPage_RecorderBoxesKeepMinWidth360AndDistinctActionIcons(double width)`（`[InlineData(680)]`、`[InlineData(600)]`）**：
   - 将窗口设为 `width × 440`，切到 `"hotkeys"` 并 `UpdateLayout()`。
   - 断言页面内恰有 5 个 `HotkeyRecorderBox`，每个 `MinWidth == 360d`、`MaxWidth == double.PositiveInfinity`、`ActualWidth >= 360d`，且右缘不越出所在 `IrisRowCard` 内缘。
   - 断言 5 张行卡片的 `SymbolIcon.Symbol` 互不相同（证明 `DataTrigger` 按 `Action` 成功命中，未全部退化成默认 `Keyboard20`）。
   - 断言页面第一层 `StackPanel` 仍绑定 `IsEnabled="{Binding CanAccessHotkeysTab}"`，且页说明 `UiText.SettingsHotkeysDescription` 套 `IrisRowDescription` 留在该面板内。

- [ ] **Step 3：跑新增测试确认失败（红灯）**

```bash
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsRowCardLayoutTests"
```

期望：`hotkeys` 相关新用例失败（找不到 `HotkeysPageScroll`，尚未改 `DataTemplate`），前序页面全绿。

- [ ] **Step 4：改写 `SettingsWindow.xaml` 快捷键页与根元素 `xmlns:actions`**

修改 `src/Iris.App/Settings/SettingsWindow.xaml`：

1. **根元素 `<ui:FluentWindow>` 补命名空间声明**（紧跟现有 `xmlns:settings` 之后）：
   ```xml
   xmlns:actions="clr-namespace:Iris.Core.Actions;assembly=Iris.Core"
   ```
2. **`HotkeysPageScroll` 与外层面板**（原样保留本页全部既有中文注释）：
   - 根 `ScrollViewer` 补 `x:Name="HotkeysPageScroll"`，删掉 `Padding="24,16,24,16"`。
   - 第一层 `<StackPanel>` 删掉 `MaxWidth="600" HorizontalAlignment="Left"`，改为 `<StackPanel Margin="{StaticResource IrisPagePadding}" IsEnabled="{Binding CanAccessHotkeysTab}">`。
   - 页标题与页级说明：
     ```xml
     <TextBlock Style="{StaticResource IrisPageTitle}" Text="{x:Static app:UiText.SettingsNavHotkeys}" />
     <TextBlock Style="{StaticResource IrisRowDescription}" Margin="{StaticResource IrisCardGap}"
                Text="{x:Static app:UiText.SettingsHotkeysDescription}" />
     ```
3. **`ItemsControl` 的 `DataTemplate`（`DataType="{x:Type local:HotkeyItemDraftViewModel}"`）**：
   ```xml
   <DataTemplate DataType="{x:Type local:HotkeyItemDraftViewModel}">
       <Border Style="{StaticResource IrisRowCard}">
           <Grid>
               <Grid.ColumnDefinitions>
                   <ColumnDefinition Width="Auto" />
                   <ColumnDefinition Width="*" />
                   <ColumnDefinition Width="Auto" />
               </Grid.ColumnDefinitions>
               <Grid.RowDefinitions>
                   <RowDefinition Height="Auto" />
                   <RowDefinition Height="Auto" />
                   <RowDefinition Height="Auto" />
                   <RowDefinition Height="Auto" />
               </Grid.RowDefinitions>
               <ui:SymbolIcon Grid.Row="0" Grid.Column="0">
                   <ui:SymbolIcon.Style>
                       <Style TargetType="ui:SymbolIcon" BasedOn="{StaticResource IrisRowIcon}">
                           <Setter Property="Symbol" Value="Keyboard20" />
                           <Style.Triggers>
                               <DataTrigger Binding="{Binding Action}" Value="{x:Static actions:IrisAction.InputTranslate}">
                                   <Setter Property="Symbol" Value="Translate20" />
                               </DataTrigger>
                               <DataTrigger Binding="{Binding Action}" Value="{x:Static actions:IrisAction.ClipboardTranslate}">
                                   <Setter Property="Symbol" Value="ClipboardTextLtr20" />
                               </DataTrigger>
                               <DataTrigger Binding="{Binding Action}" Value="{x:Static actions:IrisAction.OpenSettings}">
                                   <Setter Property="Symbol" Value="Settings20" />
                               </DataTrigger>
                               <DataTrigger Binding="{Binding Action}" Value="{x:Static actions:IrisAction.SelectionTranslate}">
                                   <Setter Property="Symbol" Value="CursorClick20" />
                               </DataTrigger>
                               <DataTrigger Binding="{Binding Action}" Value="{x:Static actions:IrisAction.ToggleMouseSelection}">
                                   <Setter Property="Symbol" Value="CursorHover20" />
                               </DataTrigger>
                           </Style.Triggers>
                       </Style>
                   </ui:SymbolIcon.Style>
               </ui:SymbolIcon>
               <StackPanel Grid.Row="0" Grid.Column="1" VerticalAlignment="Center">
                   <TextBlock Style="{StaticResource IrisRowTitle}" Text="{Binding DisplayName}" />
                   <TextBlock Style="{StaticResource IrisRowDescription}" Text="{Binding Description}" />
               </StackPanel>
               <CheckBox Grid.Row="0" Grid.Column="2"
                         Style="{StaticResource IrisSwitchCheckBox}"
                         Margin="{StaticResource IrisControlGap}" VerticalAlignment="Center"
                         IsChecked="{Binding Enabled, Mode=TwoWay}"
                         IsEnabled="{Binding CanEdit}"
                         AutomationProperties.Name="{Binding DisplayName}" />
               <local:HotkeyRecorderBox Grid.Row="1" Grid.Column="0" Grid.ColumnSpan="3"
                                        Margin="0,8,0,0" MinWidth="360" HorizontalAlignment="Stretch" />
               <!-- 原样保留草稿错误与运行状态两个 TextBlock（挪到 Grid.Row="2" / Grid.Row="3"，Grid.Column="0" Grid.ColumnSpan="3"，原 Text、Foreground、DataTrigger 绑定一字不改） -->
           </Grid>
       </Border>
   </DataTemplate>
   ```
   （注意：将本页原有的中文注释按原顺序完整放回对应区域；去掉旧 `CheckBox` 上的 `WrappingCheckBoxContent` 与 `Content="{x:Static app:UiText.SettingsHotkeyEnable}"`。）

- [ ] **Step 5：跑本任务测试（重点盯 `W04`）与「设置全集」确认全绿**

```bash
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsRowCardLayoutTests|FullyQualifiedName~SettingsHotkeysIntegrationTests|FullyQualifiedName~LocalizationTests"
```

期望：全绿（含 `W01`、`W02`、`W04` 八个变体与 `LocalizationTests.EnglishShortcutSafetyDescriptionHasItsFullWrappedHeight`）。
- **退路 1（窄宽裁切）**：若 `W04` 或 `HotkeysPage_RecorderBoxesKeepMinWidth360AndDistinctActionIcons(680)` 因滚动条宽度导致 `ActualWidth < 360` 或右缘超界，按 S0-3 把 `Iris.Styles.xaml` 里的 `IrisPagePadding` 左右从 `24` 调为 `16`（不动 `MinWidth="360"`），重跑确认全绿并记入偏差清单。
- **退路 2（`W04` 取点位移）**：仅当 `W04` 的 `(border2.ActualWidth - 6, border2.ActualHeight / 2)` 因版面变化落到边框线而非录制框内底时，微调取点坐标（**严禁放宽 `> 130` / `< 120` 亮度阈值**）并记入「改过的既有测试」。

随后跑「设置全集」确认全绿、0 警告。

- [ ] **Step 6：人工复看 `W04` 八张渲染图 + 按路径暂存（严禁提交）+ 自检清单**

逐张查看 `W04` 产出的 `hotkeys_{Light|Dark}_{760x520|600x440}_{100|150}pct.png`，确认 5 张卡片图标各异、开关居右、`Ctrl+Alt+Shift+Win+F10` 与录制提示/取消按钮在 `150%` 缩放下无裁切。

```bash
git add src/Iris.App/Settings/SettingsWindow.xaml tests/Iris.App.Tests/Settings/SettingsRowCardLayoutTests.cs
git status --short
```

自检：6 个普通页（历史、截图、常规、划词、网络、快捷键）全部收官；`HotkeyRecorderBox.xaml(.cs)` 零改动；本页中文注释原样保留、`src/` 无新增中文；`git log -1 --oneline` 仍为 `dc08c48`。

**验证命令：** `FullyQualifiedName~SettingsRowCardLayoutTests|FullyQualifiedName~SettingsHotkeysIntegrationTests|FullyQualifiedName~LocalizationTests` —— 预期全绿；再跑「设置全集」，预期全绿。

**要改的既有测试：** 视 `SettingsHotkeysIntegrationTests.cs` 的 `W04` 实测结果而定——预期无（Task 3 已对齐主题字典且 `MinWidth="360"` 在 680 宽下有充足余量）；仅当取点因版面位移偏离内底时微调取点坐标（不改 `130 / 120` 阈值）并记入报告。

### Task 10: 钻取基础：视图状态、切页复位、新增文字

**目的：** 把两页钻取共用的东西先立起来：视图模型上的两位状态（S0-8）、切页复位（T5）、四条新文字（S0-12）。本任务做完界面没有可见变化。

**Files:**
- Create: `src/Iris.App/Settings/SettingsViewModel.DetailView.cs`
- Create: `src/Iris.App/Settings/SettingsWindow.DetailNavigation.cs`
- Create: `src/Iris.App/UiText.AcrylicSettings.cs`
- Create: `tests/Iris.App.Tests/Settings/SettingsViewModelDetailViewTests.cs`
- Modify: `src/Iris.App/Settings/SettingsWindow.xaml.cs`——`OnViewModelPropertyChanged` cs:144–166（只加转调）
- Modify: `src/Iris.App/Resources/Strings.resx`、`src/Iris.App/Resources/Strings.en-US.resx`、`build/localization/app-catalog.json`

**范围与要点：**
- 视图模型：两个属性照 S0-8 的表；setter 只赋值加通知。**不改 `SettingsViewModel` 的任何既有文件与既有方法。**
- 代码隐藏的新 partial：本任务只放 T5（页面键变了就把两个属性置假、清两个 `Id` 字段）和两个 `Id` 字段；T1–T4 留给任务 11、12。T5 的写法要便于任务 11 往里补「同时清 `_ocrRestorePending` 与 `_ocrAddPending`」（U6、U8；这两个字段由任务 11 引入，本任务不建）。`OnViewModelPropertyChanged` 里只加转调到新 partial 的调用，原有内容不动。
- **U6、U7 依赖的三件事已由协调方核实（2026-11-17，2.7「已核实」），本任务不用再核实、没有「停下来报告」的分支**：`IsSaving` 可直接绑定且带通知；`CanEdit` 变化时发通知；`SelectedOcrProfile` 的 setter 在 `CanEdit` 为假时拒收（据此有了协调方裁决 C4）。本任务只做一步复查：开工时 grep 这三处行号（`SettingsViewModel.cs:493–506`、`:365`、`SettingsViewModel.Ocr.cs:52`）仍与 2.7 记录的一致；不一致（代码在 `dc08c48` 之后动过）才停下来报告。
- 文字：四条照 S0-12 的表；先按中文值搜既有键，完全相同就复用。四处同时改：两份 resx（键顺序一致，追加在 `</root>` 前）、`app-catalog.json`（值与 resx 一致）、`UiText` 的新 partial（`=>` 属性）。
- 新文件里的注释一律英文；三个新 `.cs` 文件里不出现任何中文字面量。
- 核实 2.13 第 11 条（`CurrentPageKey` 写入相同值是否发通知），结果写回 S0-8 那张情形表对应的一行。
- 加完文字立刻跑一次本地化审计（键缺失不会报错，只能靠它）。
- 风险：R7；`UiText` 属性写成 `static readonly` 字段会在切换语言后不更新（2.10）。

**步骤提纲：**
- [ ] 写 `SettingsViewModelDetailViewTests`：两个属性默认假；赋新值发一次通知、赋相同值不发；互不影响；（窗口级一条）两个属性为真时改 `CurrentPageKey`，两个都变假；四条新 `UiText` 属性在中文与英文下都取到 S0-12 表里的值、且不等于「未知错误」那句
- [ ] 跑，确认失败（编译不过也算）
- [ ] 复查 2.7「已核实」的三处行号（`IsSaving`、`CanEdit`、`SelectedOcrProfile` 的 setter）仍与记录一致；不一致才停下来报告
- [ ] 建视图模型 partial；建代码隐藏 partial 并在 `OnViewModelPropertyChanged` 加转调
- [ ] 四处加文字
- [ ] 跑本任务过滤组，确认通过
- [ ] `dotnet build Iris.slnx -c Release` 之后跑 `& build/audit-localization.ps1 -EvidenceDirectory artifacts/localization-audit`（Windows PowerShell 5.1），确认无新增问题
- [ ] `git add`（不含 `artifacts/`）

**详细步骤：**

> **代码与命令未运行过。** 本文件由撰写者只读源码后写成：没有执行 `dotnet build`、`dotnet test`、审计脚本，也没有改过仓库。只有不改任何东西的核对命令（`grep`、`git status` / `git log`、Step 9 的 `diff`、Step 12(a) 与 Step 15 的 `python -c`）在没改过的 `dc08c48` 工作区上试过写法：命令能跑、输出的样子与各 Step 写的一致；改动之后的数值仍然是推算。每个「预期」都是读代码推算；与实际不符时按该 Step 写明的退路走，并记入偏差清单。

#### 已读到的事实（撰写时核对，HEAD `dc08c48`）

##### 来自计划（设计依据）

- Task 10 一节的范围：建 3 个新 `.cs` + 1 个新测试文件；改 `SettingsWindow.xaml.cs` 的 `OnViewModelPropertyChanged`（只加转调）；改两份 resx 与 `app-catalog.json`。「要改的既有测试：无」。做完界面无可见变化。
- S0-8：两个属性 `IsTranslationDetailOpen`、`IsOcrDetailOpen`，默认 `false`，「值变了才通知」，setter 里除赋值与 `OnPropertyChanged()` 外什么都不做；放 `src/Iris.App/Settings/SettingsViewModel.DetailView.cs`。代码隐藏两个私有字段 `_translationDetailId`、`_ocrDetailId`（类型与草稿 `Id` 相同；不在详情态时为默认值）。T5：收到 `CurrentPageKey` 变更通知 → 两个属性置 `false`、两个 `Id` 字段清掉。T1–T4 属任务 11、12。
- S0-8 情形表「同一页重复 `ShowAsync("translation")` → 留在详情」依赖 2.13 第 11 条（`CurrentPageKey` 写相同值不发通知）；若实际会发，T5 改成「新旧页面键不同才复位」，代码隐藏自己记上一次的页面键。
- S0-12：**只有四条**新文字，除此之外不新增任何字符串：

  | 键 | 中文 | 英文 |
  |---|---|---|
  | `SettingsRowMoreActions` | 更多操作 | More actions |
  | `SettingsRowOpenDetails` | 查看详情 | Open details |
  | `SettingsRowDragToReorder` | 拖动排序 | Drag to reorder |
  | `SettingsRowEnabled` | 启用 | Enabled |

  加之前按中文值搜既有键，中英文都完全相同就复用。`UiText` 新建 `src/Iris.App/UiText.AcrylicSettings.cs`，`=>` 属性，不能是 `static readonly` 字段。
- 2.10：四处同时改；键缺失不报错（静默显示「未知错误」那句）；审计 = Release 构建后跑 `& build/audit-localization.ps1 -EvidenceDirectory artifacts/localization-audit`。
- 2.7「已核实」：`IsSaving` `SettingsViewModel.cs:493–506`；`CanEdit` `:365`；`SelectedOcrProfile` setter `SettingsViewModel.Ocr.cs:52`。Task 10 只复查这三处行号。
- 5.0：测试命令前缀 `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "<过滤式>"`；任何任务不提交。
- Task 10 验证过滤式：`FullyQualifiedName~SettingsViewModelDetailViewTests|FullyQualifiedName~SettingsViewModelTests|FullyQualifiedName~Iris.App.Tests.Localization|FullyQualifiedName~SettingsWindowPresenterTests`，再跑「设置全集」（5.0 里的那条过滤式）。
- Task 11 会往 T5 里补「同时清 `_ocrRestorePending` 与 `_ocrAddPending`」（这两个字段由 Task 11 引入，本任务不建）；Task 11、12 各自在 `OnViewModelPropertyChanged` 再加转调。

##### 来自源码

**视图模型（`src/Iris.App/Settings/`）**

- `SettingsViewModel` 是 `internal sealed partial class SettingsViewModel : INotifyPropertyChanged, IDisposable`（`SettingsViewModel.cs:48`），命名空间 `Iris.App.Settings`（文件级命名空间）。没有基类、没有 `SetProperty`；通知只有一个私有方法 `private void OnPropertyChanged([CallerMemberName] string? propertyName = null)`（`SettingsViewModel.cs:2850–2857`，`_disposed` 为真时不发）。
- 既有 partial 文件 7 个：`SettingsViewModel.cs`（2940 行，主体）、`.Language.cs`（21 行）、`.Mouse.cs`（74）、`.Network.cs`（294）、`.Ocr.cs`（359）、`.UpdatePreference.cs`（9）、`.Updates.cs`（192）。其余 partial 的写法都是 `internal sealed partial class SettingsViewModel { … }`，不重复写接口。
- 「值变了才通知」的既有写法（照抄它的形状）：`IsSaving`，`SettingsViewModel.cs:493–506`——`if (_isSaving != value) { _isSaving = value; OnPropertyChanged(); … }`。
- 当前导航页：`public string CurrentPageKey`（`SettingsViewModel.cs:345–361`），字段 `_currentPageKey` 初值 `"general"`（`:94`）。setter 先 `SettingsWindowPresenter.NormalizePageKey(value)`（`SettingsWindowPresenter.cs:580`），**只有归一化后的值与当前不同才赋值并 `OnPropertyChanged()`**（`:351`、`:358`）——即 2.13 第 11 条的答案：写入相同页面键**不发通知**（撰写时读代码核实；Step 2 的 `SamePageKey_KeepsTheDetailFlags` 把它钉住）。
- 复查用的三处（与计划 2.7 一致）：`IsSaving` `SettingsViewModel.cs:493–506`；`CanEdit` `:365`；`SelectedOcrProfile` setter `SettingsViewModel.Ocr.cs:52`（属性声明在 `:49`）。`SelectedProfile` `SettingsViewModel.cs:332–340`。
- 草稿 `Id` 的类型都是 `string`：`TranslationProfileDraftViewModel.cs:159` `public string Id { get; }`；`OcrProfileDraftViewModel.cs:14` `public string Id => _initial.Id;`。所以两个 `Id` 字段是 `string?`，默认值 `null`。

**代码隐藏（`src/Iris.App/Settings/SettingsWindow.xaml.cs`，458 行）**

- `internal partial class SettingsWindow : FluentWindow`（`:16`），命名空间 `Iris.App.Settings`；`using` 在 `:1–12`（含 `System.ComponentModel`）。
- `OnDataContextChanged`（`:101–123`）在 `:116` 订阅 `newVm.PropertyChanged += OnViewModelPropertyChanged;`——所以**只要给窗口设了 `DataContext`，不 `Show()` 也收得到通知**。
- `OnViewModelPropertyChanged`（`:144–166`）：三段 `if`，第一段在 `:146–147`（`Network`）；`SelectedProfile` 那段（`:153–165`）里有一个提前 `return`（`:158`），只在该属性名下才走到。既有的「不在 UI 线程就 `Dispatcher.InvokeAsync`」写法见 `:150–151`、`:155–159`。
- 写 `CurrentPageKey` 的地方：八个导航点击处理（`:216`、`:228`、`:240`、`:246`、`:330`、`:332`、`:344`、`:346`）与 `SettingsWindowSession.cs:108`。本任务都不动。

**本地化**

- `UiText` 是 `internal static partial class UiText`，命名空间 `Iris.App`，7 个既有 partial（`UiText.cs` 272 行含 4 行中文；`UiText.Extra/History/Ipc/Language/Network/Updates.cs` 无中文）。每条都是手写的一行：`public static string X => global::Iris.App.Resources.AppResources.Get("X");`（例：`UiText.Ipc.cs:5`）。没有生成代码、没有索引器——**新增键必须手写属性**。
- `AppResources.Get`（`src/Iris.App/Resources/AppResources.cs:12–16`）：`Manager.GetString(key, UiLanguage.Culture) ?? CoreText.Get("ErrorUnknown")`——键缺失时返回「未知错误」那句，不抛异常。既有测试用 `ErrorMessages.GetDefault(ErrorCode.Unknown)` 表示那句话（`tests/Iris.App.Tests/Localization/LocalizationTests.cs:230`）。
- 界面语言是**进程级静态状态**（`src/Iris.Core/Localization/UiLanguage.cs`）：`Select` 只在尚未选定时生效；`BeginRun()` 清空选定并在 `Dispose` 时还原。测试里切语言的写法：`using var run = AppLanguage.BeginRun(); AppLanguage.Select("en-US");`（`LocalizationTests.cs:229`），并且该测试类必须在不并行的集合里：`[Collection("Localization")]`（定义在 `LocalizationTests.cs:30–31`，`DisableParallelization = true`；`Updates/` 下三个类也用它）。
- `Strings.resx` / `Strings.en-US.resx`（`src/Iris.App/Resources/`，各 1413 行、462 个键、两份键顺序完全一致；CRLF 换行、无 BOM、两空格缩进；最后一条是 `ShutdownIncomplete`，文件以 `</root>` + CRLF 结尾）。一条的完整写法：

  ```xml
    <data name="ShutdownIncomplete" xml:space="preserve">
      <value>应用退出未完成，请重试退出。</value>
    </data>
  ```

- `build/localization/app-catalog.json`（1850 行、462 个键，与 resx 键集合相同；LF 换行、无 BOM、两空格缩进）。每条只有两个字段 `zh-CN`、`en-US`，**没有路径、行号、哈希**：

  ```json
    "UpdateRecoveryRetry": {
      "zh-CN": "重试安装",
      "en-US": "Retry installation"
    }
  ```

  键大致按字母序排（不严格）；审计按字典比较，不看顺序。
- 按中文值搜既有键的结果（撰写时已搜）：`启用` 只有 `SettingsHotkeyEnable`（`Strings.resx:753–754`），它的英文是 `Enable`（不是 `Enabled`）；英文 `Enabled` 只有 `SettingsProfileEnabled`（`:858–859`），它的中文是 `已启用`。`更多操作`、`查看详情`、`拖动排序`、`More actions`、`Open details`、`Drag to reorder` 都没有既有键。**结论：四条都新增，无可复用。**
- 审计（`build/audit-localization.ps1` → `build/LocalizationAudit/Program.cs` + `build/localization/audit.py`）：
  - `audit.py:43–56`：两份 resx 与 catalog 的**键集合必须相等**；每个键 `catalog[key] == {'zh-CN': zh, 'en-US': en}`；值不得为空；英文值里不得有汉字；格式占位符两边一致。
  - `Program.cs:45–57`：扫 `src` 下全部 `.cs` / `.xaml` 的汉字字面量与注释；`static readonly` 字段里出现 `UiText.` / `AppResources.Get(` 的记为 `resource-cache`，一律不过。resx 里的中文值不在扫描范围内（它只扫 `.cs` 与 `.xaml`）。
  - `Program.cs:121–143`（`--compiled`）：拿 `src/Iris.App/bin/Release` 下编译出的程序集里的资源值与 resx 源比对——所以**必须先做 Release 构建**。
  - 仓库里**没有任何测试**校验 resx 与 catalog 一致（`tests/` 下 grep `app-catalog` 0 命中）；`build/localization/probe_exceptions.py` 提到 catalog，但它是例外登记的变异探针，本任务不新增登记，不用跑。

**测试（`tests/Iris.App.Tests/`）**

- 最简单的视图模型夹具：`Settings/SettingsViewModelTests.cs:19–48` 的 `CreateSut`——`Substitute.For<ISettingsService>()`、`new ThemeService(settingsService)`、`new GeneralSettingsSaveCoordinator(settingsService, new TestSettingsEditCoordinator(settingsService), autoStartService, null, NullLogger<GeneralSettingsSaveCoordinator>.Instance)`、`new SettingsViewModel(settingsService, themeService, coordinator, dispatcher)`；普通 `[Fact]`，不经 `WpfTestRunner`；用 `using (vm)` 释放。
- 最简单的「窗口 + 视图模型」夹具：`Settings/SettingsWindowTranslationUiTests.cs:25–63` 的 `CreateServiceProvider` 与 `:82–105` 的用法——`WpfTestRunner.RunAsync(() => { using var provider = …; using var scope = provider.CreateScope(); var vm = …; var window = scope.ServiceProvider.GetRequiredService<SettingsWindow>(); window.DataContext = vm; …; return Task.CompletedTask; })`，不 `Show()`。
- `Iris.App` 对测试项目开了 `InternalsVisibleTo`（`src/Iris.App/Iris.App.csproj:38`）。
- `global.json` 的测试运行器是 `Microsoft.Testing.Platform`；过滤式的退路口径照计划 Task 1 的 Step 1(c)。
- `Directory.Build.props`：`TreatWarningsAsErrors=true`、`EnforceCodeStyleInBuild=true`、`AnalysisLevel=latest-recommended`。**由此带来一个本任务必须处理的问题**：只被赋 `null`、从不被读的私有字段会报 `CS0414` 并被当成错误（见 Step 6 与文末「需协调方裁决」第 1 条）。

**补查（第二轮，仍是只读）**

- `OnPropertyChanged(nameof(CanEdit))` 在 `SettingsViewModel.cs` 的 `:502`、`:1042`、`:1111`、`:2055`、`:2113`、`:2212`，与计划 2.7 第 2 条一致；另有一处 `:2802` 计划没列（无碍）。
- `_currentPageKey` 只在 `CurrentPageKey` 的 setter 里被写（`SettingsViewModel.cs:94` 声明、`:357` 赋值）；写这个属性的调用方（上面列的九处）都在 UI 线程上。所以 T5 的转调**不加** `Dispatcher.CheckAccess()` 守卫——同一个方法里 `Network` 那一段（`:146–147`）也没加。
- `OnViewModelPropertyChanged` 里已经用掉的模式变量名：`networkVm`（`:146`）、`ocrVm`（`:148`）、`vm`（`:161`，在内层块里）。`if` 条件里声明的模式变量作用域是**整个方法体**，新加的转调行必须换名字，否则 `CS0128` / `CS0136`。本任务用 `pageVm`。
- `SettingsWindow.xaml` 第 2 行是 `x:ClassModifier="internal"`，第 7 行是 `xmlns:app="clr-namespace:Iris.App"`；XAML 里取文字的既有写法是 `{x:Static app:UiText.SettingsWindowTitle}`。
- 源文件编码：`.cs` 一律 UTF-8 无 BOM、文件末尾有一个换行；换行符两种都有（`UiText.Ipc.cs`、`SettingsViewModel.Language.cs`、`SettingsWindowTranslationUiTests.cs` 是 LF；`SettingsWindow.xaml.cs`、`UiText.cs`、`SettingsViewModelTests.cs`、`LocalizationTests.cs` 是 CRLF）。`git config core.autocrlf` 是 `true`，仓库里没有 `.gitattributes`。**改既有文件时保持它原来的换行**（两份 resx 与 `SettingsWindow.xaml.cs` 是 CRLF，`app-catalog.json` 是 LF）。
- 构建时哪些代码风格规则算警告（即算错误）——**读配置推得，没有经过构建验证**：
  - `.editorconfig:82` `dotnet_style_readonly_field = true:warning`（IDE0044：只在声明处或构造函数里赋值的私有字段必须 `readonly`）；`:121` `csharp_prefer_static_local_function = true:warning`。`:69` `dotnet_style_prefer_auto_properties = true:suggestion`（IDE0032 只是建议）。`.editorconfig` 里没有任何 `dotnet_diagnostic.*` 行。
  - 本机 SDK 是 10.0.402（`global.json` 要 10.0.100、`rollForward: latestFeature`）。它的 `Sdks/Microsoft.NET.Sdk/codestyle/cs/build/config/analysislevelstyle_recommended.globalconfig` 会把 IDE0005、IDE0032、IDE0051、IDE0052 等 88 条规则设成警告，但同目录上一级的 `Microsoft.CodeAnalysis.CSharp.CodeStyle.targets` 只在「`AnalysisLevelStyle` 与 `AnalysisLevel` 不同」或「有效级别 ≥ 11.0」时才合并它；本仓库两者相同、`latest` 在这个 SDK 上是 10.0，所以**没有合并**。
  - 编译器自己的 `CS0414`（私有字段被赋值但从未读取）不受上面影响，照报、照算错误；`src/Iris.App` 里没有任何 `#pragma warning disable` 的先例。

##### 本任务定下的名字（Task 11、12 照抄）

| 名字 | 在哪 | 可见性 | 说明 |
|---|---|---|---|
| `IsTranslationDetailOpen` | `SettingsViewModel.DetailView.cs` | `public bool`，读写、带通知 | 「翻译服务」页在详情态 |
| `IsOcrDetailOpen` | 同上 | `public bool`，读写、带通知 | OCR 页在详情态 |
| `_translationDetailId`、`_ocrDetailId` | `SettingsWindow.DetailNavigation.cs` | `private string?` | 当前详情那一项的 `Id`；列表态为 `null` |
| `TranslationDetailId`、`OcrDetailId` | 同上 | `internal string?`，只读 | 上面两个字段的只读视图，供测试断言（**计划外新增，记入偏差清单**，原因见 Step 6） |
| `ResetDetailNavigation(SettingsViewModel vm)` | 同上 | `private void` | T5：`OnViewModelPropertyChanged` 收到 `CurrentPageKey` 变更时直接转调它；Task 11 往它的方法体里补清 `_ocrRestorePending`、`_ocrAddPending` |
| `UiText.SettingsRowMoreActions`、`UiText.SettingsRowOpenDetails`、`UiText.SettingsRowDragToReorder`、`UiText.SettingsRowEnabled` | `UiText.AcrylicSettings.cs` | `public static string`（`=>`） | 资源键与属性同名 |

给任务 11、12 撰写者的四点（都是撰写时读到的事实）：

- `OnViewModelPropertyChanged` 这一个方法体里已经用掉的模式变量名是 `networkVm`、`ocrVm`、`vm`，加上本任务的 `pageVm`。再加转调时另取名字，重名会报 `CS0128` / `CS0136`。
- 本任务的 T5 转调没有线程守卫：`CurrentPageKey` 的全部写入点都在界面线程，`ResetDetailNavigation` 也只碰视图模型的两位与两个私有字段。T4 关注的 `SelectedOcrProfile`、`SelectedProfile` 两个通知不一样——既有代码对它们做了「不在界面线程就转回去」的处理（`dc08c48` 上是 `SettingsWindow.xaml.cs:150–151`、`:155–159`），任务 11、12 的转调要放在同样的保护之内。
- XAML 里取这四条文字的写法是 `{x:Static app:UiText.SettingsRowOpenDetails}`（`SettingsWindow.xaml` 第 7 行已有 `xmlns:app="clr-namespace:Iris.App"`）。测试里读两个 `Id` 用 `window.TranslationDetailId`、`window.OcrDetailId`（`Iris.App.csproj:38` 对 `Iris.App.Tests` 开了 `InternalsVisibleTo`）。
- 任务 11 往 `ResetDetailNavigation` 末尾补两行之后，`SettingsViewModelDetailViewTests` 的 16 条仍须全绿（它在任务 11 的验证过滤组里）。

---

#### 步骤

（以下行号是撰写时在 `dc08c48` 上读到的。任务 4 改过 `SettingsWindow.xaml.cs` 靠前的 `ApplyAdaptiveLayout`，`OnViewModelPropertyChanged` 的行号会往后漂，一律按文字锚点定位。测试命令沿用 5.0 的前缀 `dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "<过滤式>"`；任务 1 Step 1(c) 若把可用写法回填成了别的形式，本任务所有命令照回填后的写法换。）

- [ ] **Step 1: 复查三处行号、确认名字没被占用、给本地化审计留一份基线**

(a) 2.7「已核实」的三处（本任务唯一的复查：Task 10「范围与要点」第 3 条）。`SettingsViewModel` 的既有文件在任务 1–9 里不允许改，所以行号必须与下面**完全一致**：

```
grep -n "public bool IsSaving" src/Iris.App/Settings/SettingsViewModel.cs
grep -n "public bool CanEdit =>" src/Iris.App/Settings/SettingsViewModel.cs
grep -n "SelectOcrProfile(value); }" src/Iris.App/Settings/SettingsViewModel.Ocr.cs
git diff --stat dc08c48 -- src/Iris.App/Settings/SettingsViewModel.cs src/Iris.App/Settings/SettingsViewModel.Ocr.cs
```

期望：依次是 `493:`、`365:`、`52:` 各 1 行；第四条**没有输出**（这两个文件自 `dc08c48` 起没动过）。第 52 行全文应是 `set { if (CanEdit && (value == null || _ocrProfiles.Contains(value))) SelectOcrProfile(value); }`。任何一条不符：**停下来报告**，不往下做（Task 10「范围与要点」第 3 条写的唯一停工条件）。

(b) 2.13 第 11 条（`CurrentPageKey` 写入相同值是否发通知）——**撰写时已读代码核实：不发**。执行时复核：

```
grep -n "public string CurrentPageKey" -A 16 src/Iris.App/Settings/SettingsViewModel.cs
```

期望：从 `345:` 起，setter 里先 `var normalized = SettingsWindowPresenter.NormalizePageKey(value);`，然后 `if (_currentPageKey != normalized)` 的块里才有 `_currentPageKey = normalized;` 与 `OnPropertyChanged();`，块外没有通知。符合 → 照下面的步骤做，结果在 Step 13 写回计划；S0-8 情形表里写的那条退路（「T5 改成新旧页面键不同才复位」）用不上，不做。不符合 → 说明这个文件在 `dc08c48` 之后被改过（(a) 的第四条命令应当已经报出来）：**停下来报告**，不自行改 T5 的写法。

(c) 确认要新增的名字没被占用，并按 S0-12 的规矩再按值搜一遍可复用的键：

```
grep -rn "IsTranslationDetailOpen\|IsOcrDetailOpen\|DetailNavigation\|TranslationDetailId\|OcrDetailId\|SettingsRowMoreActions\|SettingsRowOpenDetails\|SettingsRowDragToReorder\|SettingsRowEnabled" src tests build/localization --include=*.cs --include=*.xaml --include=*.resx --include=*.json
grep -n -B1 "<value>更多操作</value>\|<value>查看详情</value>\|<value>拖动排序</value>\|<value>启用</value>" src/Iris.App/Resources/Strings.resx
grep -n -B1 "<value>More actions</value>\|<value>Open details</value>\|<value>Drag to reorder</value>\|<value>Enabled</value>" src/Iris.App/Resources/Strings.en-US.resx
```

期望：第一条 0 行。第二条只命中 `SettingsHotkeyEnable`（`启用`，`dc08c48` 上是 :753–754）。第三条只命中 `SettingsProfileEnabled`（`Enabled`，:858–859）。这两条既有键都只对上了一半（`SettingsHotkeyEnable` 的英文是 `Enable`；`SettingsProfileEnabled` 的中文是 `已启用`），按 S0-12「中文与英文都完全相同才复用」**不复用，四条都新增**。若第二、三条命中了**同一个键**且中英文都与 S0-12 的表相同：那一条不新增——Step 5 里把对应属性改成读那条既有键（`AppResources.Get("既有键名")`，属性名不变），Step 8–10 里跳过它，并**记入偏差清单**写明复用了哪条。第一条若有命中：停下来报告（前面的任务提前用了本任务的名字）。

(d) 定位转调要加在哪：

```
grep -n "private void OnViewModelPropertyChanged" -A 3 src/Iris.App/Settings/SettingsWindow.xaml.cs
```

期望 4 行，第 3 行是 `        if (e.PropertyName == nameof(SettingsViewModel.Network) && sender is SettingsViewModel networkVm)`（`dc08c48` 上方法在 :144，该行在 :146）。这一行是 Step 7 的锚点。找不到这一行：停下来报告。

(e) 本地化审计基线（让 Step 12 的「无新增问题」有东西可比；审计要读 Release 构建的产物）：

```
dotnet build Iris.slnx -c Release
& build/audit-localization.ps1 -EvidenceDirectory artifacts/localization-audit-task10-before
```

第二条在仓库根目录的 **Windows PowerShell 5.1** 里跑（Task 10 步骤提纲的口径）；从别的 shell 调用时写成 `powershell -NoProfile -Command "& build/audit-localization.ps1 -EvidenceDirectory artifacts/localization-audit-task10-before"`。脚本内部会调 `dotnet run`（审计工具）与 `python`（`build/localization/audit.py`），两者都要在 `PATH` 里；报「找不到 python」之类：停下来报告，不改脚本。

期望：构建 0 警告 0 错误；审计退出码 0，输出里有三行可记的东西——`Scanned C# and XAML; Chinese entries including comments: <N1>`、`Compiled resource values matched after XML newline normalization; composite formats parsed: <N2>`、最后一行 JSON 里的 `"resourceCounts"`（其中 `"App": 462`）与 `"resourceErrors": []`。把 N1、N2、`resourceCounts` 原样记下。基线本身退出码非 0：把它列出的条目原样记下（那是任务 1–9 留下的，不是本任务的），报告给协调方，本任务继续，Step 12 的判据改成「相对这份基线没有多出任何一条」。`artifacts/` 在 `.gitignore` 里（:66），不会被暂存。

- [ ] **Step 2: 写 `SettingsViewModelDetailViewTests`（新文件，整文件如下）**

Create `tests/Iris.App.Tests/Settings/SettingsViewModelDetailViewTests.cs`。共 16 条用例（1 + 2 + 1 + 4 + 4 + 4）。夹具 `CreateServiceProvider` 逐行照抄自 `SettingsWindowTranslationUiTests.cs:25–63`（那套「窗口 + 视图模型、不 `Show()`」的写法已有既有用例 `TwoPageNavigation_SwitchesBetweenGeneralAndTranslation` 在用）。整个类放进不并行的集合 `Localization`：最后一组用例要切进程级的界面语言（`UiLanguage` 是静态状态，照 `LocalizationTests.cs:229` 的写法）。

```csharp
using System.Reflection;
using Iris.App.Hosting;
using Iris.App.Resources;
using Iris.App.Services;
using Iris.App.Settings;
using Iris.App.Tests.TestSupport;
using Iris.Core.Errors;
using Iris.Core.Platform;
using Iris.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Iris.App.Tests.Settings;

// Drill-down basics: the two view-only flags, the reset on page change (T5), four new strings.
// Lives in the non-parallel "Localization" collection: the last test switches the process-wide UI language.
[Collection("Localization")]
public sealed class SettingsViewModelDetailViewTests
{
    // Copied from SettingsWindowTranslationUiTests.CreateServiceProvider.
    private static ServiceProvider CreateServiceProvider()
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.Current.Returns(new AppSettings());

        var autoStartService = Substitute.For<IAutoStartService>();
        var editCoordinator = Substitute.For<ISettingsEditCoordinator>();
        var receiptManager = new SettingsApplyReceiptManager();
        var uiDispatcher = new ImmediateUiDispatcher();

        var services = new ServiceCollection();
        services.AddSingleton(settingsService);
        services.AddSingleton(autoStartService);
        services.AddSingleton<ISettingsEditCoordinator>(editCoordinator);
        services.AddSingleton<ISettingsApplyReceiptManager>(receiptManager);
        services.AddSingleton<IUiDispatcher>(uiDispatcher);
        services.AddSingleton<ThemeService>();

        services.AddScoped<GeneralSettingsSaveCoordinator>(sp => new GeneralSettingsSaveCoordinator(
            settingsService,
            editCoordinator,
            autoStartService,
            receiptManager,
            NullLogger<GeneralSettingsSaveCoordinator>.Instance));

        services.AddScoped<SettingsViewModel>(sp => new SettingsViewModel(
            settingsService,
            sp.GetRequiredService<ThemeService>(),
            sp.GetRequiredService<GeneralSettingsSaveCoordinator>(),
            editCoordinator,
            receiptManager,
            uiDispatcher,
            NullLogger<SettingsViewModel>.Instance));

        services.AddTransient<SettingsWindow>();

        return services.BuildServiceProvider();
    }

    // View model only, no window.
    private static Task WithViewModel(Action<SettingsViewModel> body) => WpfTestRunner.RunAsync(() =>
    {
        using var provider = CreateServiceProvider();
        using var scope = provider.CreateScope();
        body(scope.ServiceProvider.GetRequiredService<SettingsViewModel>());
        return Task.CompletedTask;
    });

    // The window is never shown: setting DataContext is enough to subscribe to the view model.
    private static Task WithWindow(Action<SettingsWindow, SettingsViewModel> body) => WpfTestRunner.RunAsync(() =>
    {
        using var provider = CreateServiceProvider();
        using var scope = provider.CreateScope();
        var vm = scope.ServiceProvider.GetRequiredService<SettingsViewModel>();
        var window = scope.ServiceProvider.GetRequiredService<SettingsWindow>();
        window.DataContext = vm;
        body(window, vm);
        return Task.CompletedTask;
    });

    private static void Set(SettingsViewModel vm, string property, bool value)
    {
        if (property == nameof(SettingsViewModel.IsTranslationDetailOpen)) vm.IsTranslationDetailOpen = value;
        else vm.IsOcrDetailOpen = value;
    }

    private static bool Get(SettingsViewModel vm, string property) =>
        property == nameof(SettingsViewModel.IsTranslationDetailOpen) ? vm.IsTranslationDetailOpen : vm.IsOcrDetailOpen;

    [Fact]
    public Task DetailFlags_DefaultToFalse() => WithViewModel(vm =>
    {
        vm.IsTranslationDetailOpen.ShouldBeFalse();
        vm.IsOcrDetailOpen.ShouldBeFalse();
    });

    [Theory]
    [InlineData(nameof(SettingsViewModel.IsTranslationDetailOpen))]
    [InlineData(nameof(SettingsViewModel.IsOcrDetailOpen))]
    public Task DetailFlag_NotifiesOnceWhenTheValueChanges_AndNotWhenItIsTheSame(string property) => WithViewModel(vm =>
    {
        var raised = new List<string?>();
        vm.PropertyChanged += (sender, e) => raised.Add(e.PropertyName);

        Set(vm, property, true);
        Get(vm, property).ShouldBeTrue();
        raised.Count.ShouldBe(1);
        raised[0].ShouldBe(property);

        Set(vm, property, true); // same value: no notification
        raised.Count.ShouldBe(1);

        Set(vm, property, false);
        Get(vm, property).ShouldBeFalse();
        raised.Count.ShouldBe(2);
        raised[1].ShouldBe(property);

        Set(vm, property, false); // same value: no notification
        raised.Count.ShouldBe(2);
    });

    [Fact]
    public Task DetailFlags_AreIndependent_AndNeverTouchTheDraft() => WithViewModel(vm =>
    {
        bool dirtyBefore = vm.IsDirty;
        var raised = new List<string?>();
        vm.PropertyChanged += (sender, e) => raised.Add(e.PropertyName);

        vm.IsTranslationDetailOpen = true;
        vm.IsOcrDetailOpen.ShouldBeFalse();

        vm.IsOcrDetailOpen = true;
        vm.IsTranslationDetailOpen.ShouldBeTrue();

        vm.IsTranslationDetailOpen = false;
        vm.IsOcrDetailOpen.ShouldBeTrue();

        // View-only state: it does not count as a change and raises nothing but its own notification.
        vm.IsDirty.ShouldBe(dirtyBefore);
        raised.Count.ShouldBe(3);
        raised[0].ShouldBe(nameof(SettingsViewModel.IsTranslationDetailOpen));
        raised[1].ShouldBe(nameof(SettingsViewModel.IsOcrDetailOpen));
        raised[2].ShouldBe(nameof(SettingsViewModel.IsTranslationDetailOpen));
    });

    private static void SetDetailId(SettingsWindow window, string field, string? value) =>
        typeof(SettingsWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    // This task has no "enter detail" yet (T1 belongs to tasks 11 and 12), so both pages are put into the detail state directly.
    private static void PutBothPagesIntoDetail(SettingsWindow window, SettingsViewModel vm)
    {
        vm.IsTranslationDetailOpen = true;
        vm.IsOcrDetailOpen = true;
        SetDetailId(window, "_translationDetailId", "translation-item");
        SetDetailId(window, "_ocrDetailId", "ocr-item");
        window.TranslationDetailId.ShouldBe("translation-item");
        window.OcrDetailId.ShouldBe("ocr-item");
    }

    // T5: the page key changed, so both pages go back to their list and both ids are cleared.
    [Theory]
    [InlineData("translation", "general")]
    [InlineData("translation", "ocr")]
    [InlineData("ocr", "translation")]
    [InlineData("ocr", "history")]
    public Task PageChange_ClosesBothDetailViews_AndClearsBothIds(string from, string to) => WithWindow((window, vm) =>
    {
        vm.CurrentPageKey = from;
        PutBothPagesIntoDetail(window, vm);

        vm.CurrentPageKey = to;

        vm.CurrentPageKey.ShouldBe(to);
        vm.IsTranslationDetailOpen.ShouldBeFalse();
        vm.IsOcrDetailOpen.ShouldBeFalse();
        window.TranslationDetailId.ShouldBeNull();
        window.OcrDetailId.ShouldBeNull();
    });

    // Writing a page key that normalizes to the current one raises no notification, so the detail state
    // stays (plan 2.13 item 11; S0-8: showing the same page again keeps the detail view).
    [Theory]
    [InlineData("translation", "translation")]
    [InlineData("translation", " Translation ")]
    [InlineData("ocr", "OCR")]
    [InlineData("general", "about")] // a page that is not open yet falls back to "general"
    public Task SamePageKey_KeepsTheDetailFlags(string current, string written) => WithWindow((window, vm) =>
    {
        vm.CurrentPageKey = current;
        PutBothPagesIntoDetail(window, vm);
        var raised = new List<string?>();
        vm.PropertyChanged += (sender, e) => raised.Add(e.PropertyName);

        vm.CurrentPageKey = written;

        vm.CurrentPageKey.ShouldBe(current);
        raised.ShouldBeEmpty();
        vm.IsTranslationDetailOpen.ShouldBeTrue();
        vm.IsOcrDetailOpen.ShouldBeTrue();
        window.TranslationDetailId.ShouldBe("translation-item");
        window.OcrDetailId.ShouldBe("ocr-item");
    });

    private static string ReadText(string key) => key switch
    {
        nameof(UiText.SettingsRowMoreActions) => UiText.SettingsRowMoreActions,
        nameof(UiText.SettingsRowOpenDetails) => UiText.SettingsRowOpenDetails,
        nameof(UiText.SettingsRowDragToReorder) => UiText.SettingsRowDragToReorder,
        nameof(UiText.SettingsRowEnabled) => UiText.SettingsRowEnabled,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };

    // A missing key does not throw: AppResources.Get returns the "unknown error" sentence, so both languages
    // are checked value by value. Reading one property in Chinese and then in English also proves it is a
    // => property and not a cached value.
    [Theory]
    [InlineData("SettingsRowMoreActions", "更多操作", "More actions")]
    [InlineData("SettingsRowOpenDetails", "查看详情", "Open details")]
    [InlineData("SettingsRowDragToReorder", "拖动排序", "Drag to reorder")]
    [InlineData("SettingsRowEnabled", "启用", "Enabled")]
    public void NewRowText_ResolvesInChineseAndEnglish(string key, string chinese, string english)
    {
        using (AppLanguage.BeginRun())
        {
            AppLanguage.Select("zh-CN");
            ReadText(key).ShouldNotBe(ErrorMessages.GetDefault(ErrorCode.Unknown));
            ReadText(key).ShouldBe(chinese);
            AppResources.Get(key).ShouldBe(chinese); // the property name is also the resource key
        }

        using (AppLanguage.BeginRun())
        {
            AppLanguage.Select("en-US");
            ReadText(key).ShouldNotBe(ErrorMessages.GetDefault(ErrorCode.Unknown));
            ReadText(key).ShouldBe(english);
            AppResources.Get(key).ShouldBe(english);
        }
    }
}
```

写法上的几点（照做，不要「顺手优化」）：

- 两个 `Id` 字段是私有的，本任务又没有任何公开途径把它们设成非空，所以用反射摆状态（先例：`LocalizationTests.cs:205–206`）；读则走 Step 6 加的两个 `internal` 只读属性。字段名写错时 `GetField` 返回 `null`、用例以 `NullReferenceException` 变红——这是对的，说明名字与 Step 6 不一致。
- `SamePageKey_KeepsTheDetailFlags` 的订阅放在 `PutBothPagesIntoDetail` **之后**，所以 `raised` 只记写入页面键那一下的通知。
- 测试文件里的注释也一律英文（Task 10「范围与要点」：新文件里的注释一律英文）。`[InlineData]` 里的四个中文值是要对的期望值，必须留着；测试项目不在本地化审计的扫描范围内（审计只扫 `src`，`build/LocalizationAudit/Program.cs:45`），不会因此多出条目。

- [ ] **Step 3: 跑，确认失败（第一种红：编译不过）**

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsViewModelDetailViewTests"
```

期望：**编译失败，没有任何用例被执行**。错误全部出在新文件 `SettingsViewModelDetailViewTests.cs` 里，只有两种编号，点名的只有下面八个名字：

| 编号 | 说的是 | 点名 |
|---|---|---|
| `CS1061` | 实例上没有这个成员 | `SettingsViewModel` 的 `IsTranslationDetailOpen`、`IsOcrDetailOpen`；`SettingsWindow` 的 `TranslationDetailId`、`OcrDetailId` |
| `CS0117` | 类型上没有这个成员（`nameof(…)` 与静态访问） | `SettingsViewModel` 的 `IsTranslationDetailOpen`、`IsOcrDetailOpen`；`UiText` 的 `SettingsRowMoreActions`、`SettingsRowOpenDetails`、`SettingsRowDragToReorder`、`SettingsRowEnabled` |

出现别的编号（`CS0246` 找不到类型、`CS0103` 找不到名字、`CS0234` 命名空间里没有某名字）：测试文件抄错了或少了 `using`，对照 Step 2 改到只剩上面两种再往下。出现 `xUnit` 开头的分析器错误：把编号与全文记下来报告，不自行改用例的形状。

- [ ] **Step 4: 建视图模型的 partial（新文件，整文件如下）**

Create `src/Iris.App/Settings/SettingsViewModel.DetailView.cs`（UTF-8 无 BOM，文件末尾留一个换行）：

```csharp
namespace Iris.App.Settings;

internal sealed partial class SettingsViewModel
{
    // View-only drill-down state for the two list/detail pages. It is not part of the
    // draft: it is never saved, never counted by IsDirty and never touched by save,
    // reload or merge. Only the settings window's code-behind flips these flags.
    private bool _isTranslationDetailOpen;
    private bool _isOcrDetailOpen;

    public bool IsTranslationDetailOpen
    {
        get => _isTranslationDetailOpen;
        set
        {
            if (_isTranslationDetailOpen != value)
            {
                _isTranslationDetailOpen = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsOcrDetailOpen
    {
        get => _isOcrDetailOpen;
        set
        {
            if (_isOcrDetailOpen != value)
            {
                _isOcrDetailOpen = value;
                OnPropertyChanged();
            }
        }
    }
}
```

要点：

- 声明行只写 `internal sealed partial class SettingsViewModel`，不重复写接口（其余六个 partial 都这样，例：`SettingsViewModel.Language.cs:7`）。
- 形状照抄既有的 `IsSaving`（`SettingsViewModel.cs:493–506`）：值变了才赋值、才通知。`OnPropertyChanged()` 是主文件里的私有方法（`SettingsViewModel.cs:2850`），靠 `[CallerMemberName]` 取属性名，所以括号里不写名字。
- setter 里**只有**赋值和 `OnPropertyChanged()`：不调 `RaiseAllCommandsCanExecute()`、不发 `IsDirty` / `CanEdit` 的通知、不读写任何别的字段（S0-8「状态放哪」）。
- **不改 `SettingsViewModel` 的任何既有文件**：不在构造函数、`Dispose`、保存、重载、合并里加任何一行。视图模型每次打开设置窗口都是新建的，默认值 `false` 就是「关窗后重新打开是列表」（S0-8 情形表）。
- 文件里没有中文；注释是英文。

- [ ] **Step 5: 建 `UiText` 的 partial（新文件，整文件如下）**

Create `src/Iris.App/UiText.AcrylicSettings.cs`（文件名必须以 `UiText` 开头：审计工具按 `UiText*.cs` 找这些文件，`build/LocalizationAudit/Program.cs:14`）：

```csharp
namespace Iris.App;

internal static partial class UiText
{
    public static string SettingsRowMoreActions => global::Iris.App.Resources.AppResources.Get("SettingsRowMoreActions");
    public static string SettingsRowOpenDetails => global::Iris.App.Resources.AppResources.Get("SettingsRowOpenDetails");
    public static string SettingsRowDragToReorder => global::Iris.App.Resources.AppResources.Get("SettingsRowDragToReorder");
    public static string SettingsRowEnabled => global::Iris.App.Resources.AppResources.Get("SettingsRowEnabled");
}
```

要点：

- 逐字照既有 partial 的写法（例：`UiText.Ipc.cs:5`）。四行都必须是 **`=>` 属性**：写成 `public static readonly string X = …;` 的话，值在类型初始化时就定死、切换语言后不更新，而且审计会把它记成 `resource-cache` 直接判不过（`Program.cs:53–57`）。
- 属性名与引号里的资源键必须逐字相同（Step 2 最后一组用例里 `AppResources.Get(key)` 那两行守着这一点）。
- 只有这四条。执行中发现还缺文字：停下来把键名与中英文报给用户，不自行加（S0-12）。
- Step 1(c) 若判定某一条复用既有键：那一行引号里换成既有键名，属性名不变，并记入偏差清单。

- [ ] **Step 6: 建代码隐藏的 partial（新文件，整文件如下）**

Create `src/Iris.App/Settings/SettingsWindow.DetailNavigation.cs`（UTF-8 无 BOM，文件末尾留一个换行）：

```csharp
namespace Iris.App.Settings;

// List -> detail drill-down for the translation-provider page and the OCR page.
// Whether a page shows its detail view is view-only state on SettingsViewModel
// (IsTranslationDetailOpen / IsOcrDetailOpen). Every transition is driven from this
// file, so no existing view-model method has to know about the drill-down.
internal partial class SettingsWindow
{
    // Id of the item whose detail view is open; null while the page shows its list.
    private string? _translationDetailId;
    private string? _ocrDetailId;

    // Read-only views of the two ids, for tests.
    internal string? TranslationDetailId => _translationDetailId;
    internal string? OcrDetailId => _ocrDetailId;

    // Called when the view model reports a new CurrentPageKey. The view model raises
    // that notification only when the normalized key actually changes, so writing the
    // key of the page that is already showing keeps an open detail view.
    private void ResetDetailNavigation(SettingsViewModel vm)
    {
        vm.IsTranslationDetailOpen = false;
        vm.IsOcrDetailOpen = false;
        _translationDetailId = null;
        _ocrDetailId = null;
    }
}
```

要点：

- 声明行是 `internal partial class SettingsWindow`，不写基类（基类在 `SettingsWindow.xaml.cs:16` 与 XAML 生成的那一半里；XAML 第 2 行是 `x:ClassModifier="internal"`，可见性必须一致）。不需要任何 `using`。
- 两个 `Id` 字段是 `string?`：草稿的 `Id` 都是 `string`（`TranslationProfileDraftViewModel.cs:159`、`OcrProfileDraftViewModel.cs:14`），「不在详情态时为默认值」即 `null`（S0-8）。
- **两个 `internal` 只读属性是计划外新增，记入偏差清单。** 原因：本任务里这两个字段只会被赋 `null`、没有任何地方读它们（读它们的 T1–T4 属于任务 11、12），编译器会报 `CS0414`（私有字段被赋值但其值从未使用），而仓库开着 `TreatWarningsAsErrors`，构建直接失败。加一对只读属性既消掉 `CS0414`，又让 Step 2 的用例能断言「切页后两个 `Id` 都清掉了」；任务 11、12 的测试也可以用它们断言「进入详情后记下的是哪一项」。不采用的另一种做法是 `#pragma warning disable CS0414`——`src/Iris.App` 里没有这种先例，而且那样本任务就没法断言 `Id` 被清掉。协调方若不同意加这两个属性，见文末「需协调方裁决」第 1 条的替代方案。
- 执行时先核实：本步之后的第一次构建（Step 7）若在这两行属性上报 `IDE0032`（建议改成自动属性）并算成错误 → 说明撰写时对代码风格规则的判断（见「已读到的事实·补查」）不成立：把两行属性改成方法 `internal string? GetTranslationDetailId() => _translationDetailId;`、`internal string? GetOcrDetailId() => _ocrDetailId;`，Step 2 测试里的 `window.TranslationDetailId` / `window.OcrDetailId` 相应改成方法调用（共 6 处），并记入偏差清单。没报 → 不动。
- `ResetDetailNavigation` 就是 S0-8 的 T5，方法体只有这四行。**任务 11 在这四行后面接着补** `_ocrRestorePending = false;` 与 `_ocrAddPending = false;`（这两个字段由任务 11 引入，本任务不建、不提前占名字）。
- 本任务**不写** T1（进入）、T2（返回）、T3（新增后自动进入）、T4（选中项换了）、列表上的 Enter 处理、行菜单构造、`ContextMenuOpening`——它们属于任务 11、12。不为它们预留空方法。
- 文件里没有中文；注释是英文。

- [ ] **Step 7: 在 `OnViewModelPropertyChanged` 里加转调；跑，确认第二种红**

Modify `src/Iris.App/Settings/SettingsWindow.xaml.cs`（CRLF 换行，保存时保持）。按文字定位到 Step 1(d) 找到的那一段（`dc08c48` 上是 :144–147）：

```csharp
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.Network) && sender is SettingsViewModel networkVm)
            SyncNetworkPassword(networkVm.Network);
```

改成（在方法体最前面插入两行，其余一个字符都不动）：

```csharp
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.CurrentPageKey) && sender is SettingsViewModel pageVm)
            ResetDetailNavigation(pageVm);
        if (e.PropertyName == nameof(SettingsViewModel.Network) && sender is SettingsViewModel networkVm)
            SyncNetworkPassword(networkVm.Network);
```

要点：

- 这是计划点了名的改动（Task 10 的 Files：「`OnViewModelPropertyChanged`（只加转调）」；S0-8：「每个被关注的属性一行」），不算偏差。除这两行外，这个文件本任务不动：`OnKeyDown`（Esc 的语义，S0-9）、八个导航点击处理、`ApplyAdaptiveLayout`、`OnDataContextChanged` 都不碰。
- 模式变量必须叫 `pageVm`：同一个方法里 `networkVm`、`ocrVm`、`vm` 已经被占用（见「已读到的事实·补查」）。
- 放在最前面，是为了不依赖后面 `SelectedProfile` 那一段里的提前 `return`。
- 不加 `Dispatcher.CheckAccess()`：`CurrentPageKey` 只从 UI 线程写入。

核对只多了这两行（任务 4 对这个文件的改动已在暂存区，所以「工作区对暂存区」的差异只剩本任务的）：

```
git diff --numstat -- src/Iris.App/Settings/SettingsWindow.xaml.cs
```

期望：`2	0	src/Iris.App/Settings/SettingsWindow.xaml.cs`。数字很大（几百行增、几百行删）说明编辑器把换行符改了：撤掉这次保存，用保持 CRLF 的方式重改。

然后跑：

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsViewModelDetailViewTests"
```

期望：编译通过、0 警告；16 条里 **12 条通过、4 条失败**。

| 用例 | 条数 | 期望 |
|---|---|---|
| `DetailFlags_DefaultToFalse` | 1 | 通过 |
| `DetailFlag_NotifiesOnceWhenTheValueChanges_AndNotWhenItIsTheSame` | 2 | 通过 |
| `DetailFlags_AreIndependent_AndNeverTouchTheDraft` | 1 | 通过 |
| `PageChange_ClosesBothDetailViews_AndClearsBothIds` | 4 | 通过 |
| `SamePageKey_KeepsTheDetailFlags` | 4 | 通过 |
| `NewRowText_ResolvesInChineseAndEnglish` | 4 | **失败**——都红在中文那一段的第一条断言（`ShouldNotBe`）：资源里还没有这四个键，`AppResources.Get` 返回的是「未知错误」那句（中文值是 `发生未知错误，详情请查看日志`） |

与期望不符时：

| 现象 | 原因 | 处理 |
|---|---|---|
| 编译报 `CS0414`，点名 `_translationDetailId` / `_ocrDetailId` | Step 6 的两个只读属性没加 | 照 Step 6 的全文补上 |
| 编译报 `CS0128` 或 `CS0136` | 转调行的模式变量与方法里已有的重名 | 用 `pageVm` |
| 编译报 `IDE0032` | 见 Step 6「执行时先核实」 | 走那条退路 |
| `PageChange_…` 红，两个属性仍为真 | 转调没加上，或 `nameof` 里写的不是 `CurrentPageKey` | 对照本步的代码 |
| `PageChange_…` / `SamePageKey_…` 以 `NullReferenceException` 红在 `SetDetailId` | 测试里的字段名与 Step 6 不一致 | 两边都必须是 `_translationDetailId`、`_ocrDetailId` |
| `SamePageKey_…` 红在 `raised.ShouldBeEmpty()` | `CurrentPageKey` 的 setter 与 Step 1(b) 核实的不一样 | 停下来报告 |
| `NewRowText_…` 有通过的 | Step 1(c) 没查出某个键已存在 | 回 Step 1(c) |

- [ ] **Step 8: 在 `Strings.resx` 里加四条（中文）**

Modify `src/Iris.App/Resources/Strings.resx`（CRLF 换行、无 BOM、两空格缩进，保存时保持）。在文件最后一行 `</root>` **之前**、最后一条 `</data>` 之后（`dc08c48` 上最后一条是 `ShutdownIncomplete`，:1410–1412）插入：

```xml
  <data name="SettingsRowMoreActions" xml:space="preserve">
    <value>更多操作</value>
  </data>
  <data name="SettingsRowOpenDetails" xml:space="preserve">
    <value>查看详情</value>
  </data>
  <data name="SettingsRowDragToReorder" xml:space="preserve">
    <value>拖动排序</value>
  </data>
  <data name="SettingsRowEnabled" xml:space="preserve">
    <value>启用</value>
  </data>
```

四条的先后就是 S0-12 那张表的先后；`<value>` 里中文前后不留空格、不带句号。resx 里的中文是资源值，不属于「`src/` 里新增中文字面量」（审计只扫 `.cs` 与 `.xaml`）。核对：

```
git diff --numstat -- src/Iris.App/Resources/Strings.resx
grep -c "<data name=" src/Iris.App/Resources/Strings.resx
```

期望：第一条 `12	0	src/Iris.App/Resources/Strings.resx`；第二条 `466`（原来 462）。

- [ ] **Step 9: 在 `Strings.en-US.resx` 里加四条（英文，键的先后与 Step 8 相同）**

Modify `src/Iris.App/Resources/Strings.en-US.resx`（同样 CRLF、无 BOM）。同样插在最后一行 `</root>` 之前：

```xml
  <data name="SettingsRowMoreActions" xml:space="preserve">
    <value>More actions</value>
  </data>
  <data name="SettingsRowOpenDetails" xml:space="preserve">
    <value>Open details</value>
  </data>
  <data name="SettingsRowDragToReorder" xml:space="preserve">
    <value>Drag to reorder</value>
  </data>
  <data name="SettingsRowEnabled" xml:space="preserve">
    <value>Enabled</value>
  </data>
```

核对两份 resx 的键与先后完全一致（撰写时两份就是逐条对齐的）：

```
git diff --numstat -- src/Iris.App/Resources/Strings.en-US.resx
diff <(grep -o '<data name="[^"]*"' src/Iris.App/Resources/Strings.resx) <(grep -o '<data name="[^"]*"' src/Iris.App/Resources/Strings.en-US.resx) && echo SAME-KEYS-SAME-ORDER
```

期望：第一条 `12	0	…Strings.en-US.resx`；第二条只输出 `SAME-KEYS-SAME-ORDER`（在 Git Bash 里跑；有 `diff` 输出就是哪一条键名或先后不一致，照 Step 8 对齐）。

- [ ] **Step 10: 在 `app-catalog.json` 里加四条**

Modify `build/localization/app-catalog.json`（**LF** 换行、无 BOM、两空格缩进，保存时保持；中文直接写，不转成 `\u` 转义）。每条只有 `zh-CN`、`en-US` 两个字段，不登记路径、行号或哈希。这个文件大致按键名字母序排，四个新键落在 `SettingsRetryPendingApply` 与 `SettingsSave` 之间（`dc08c48` 上 `"SettingsSave": {` 在 :1166）。按文字定位到这一段：

```json
  "SettingsRetryPendingApply": {
    "zh-CN": "重试服务应用",
    "en-US": "Retry provider changes"
  },
  "SettingsSave": {
```

改成：

```json
  "SettingsRetryPendingApply": {
    "zh-CN": "重试服务应用",
    "en-US": "Retry provider changes"
  },
  "SettingsRowDragToReorder": {
    "zh-CN": "拖动排序",
    "en-US": "Drag to reorder"
  },
  "SettingsRowEnabled": {
    "zh-CN": "启用",
    "en-US": "Enabled"
  },
  "SettingsRowMoreActions": {
    "zh-CN": "更多操作",
    "en-US": "More actions"
  },
  "SettingsRowOpenDetails": {
    "zh-CN": "查看详情",
    "en-US": "Open details"
  },
  "SettingsSave": {
```

审计比的是整个字典、不看先后（`build/localization/audit.py:55`），按字母序放只是照这个文件的习惯；每条末尾的逗号别漏。核对文件仍是合法 JSON、键数对、四条的值与两份 resx 逐字相同：

```
git diff --numstat -- build/localization/app-catalog.json
python -c "import json;d=json.load(open('build/localization/app-catalog.json',encoding='utf-8'));print(len(d));[print(k,'|',d[k]['zh-CN'],'|',d[k]['en-US']) for k in d if k.startswith('SettingsRow')]"
```

期望：第一条 `16	0	build/localization/app-catalog.json`。第二条先输出 `466`，再输出四行（中文在终端里显示成乱码时，改看 `git diff -- build/localization/app-catalog.json`）：

```
SettingsRowDragToReorder | 拖动排序 | Drag to reorder
SettingsRowEnabled | 启用 | Enabled
SettingsRowMoreActions | 更多操作 | More actions
SettingsRowOpenDetails | 查看详情 | Open details
```

第二条抛 `json.decoder.JSONDecodeError`：多半是漏了逗号或多了逗号，对照上面的片段改。Step 1(c) 若判定某一条复用既有键：Step 8、9、10 三处都不加那一条，上面的数字相应减少（每少一条，resx 各少 3 行、catalog 少 4 行、键数少 1）。

- [ ] **Step 11: 跑，确认通过**

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsViewModelDetailViewTests"
```

期望：编译通过、0 警告；**16 条全部通过，0 失败、0 跳过**（Step 7 红的那四条 `NewRowText_ResolvesInChineseAndEnglish` 现在变绿；其余十二条保持绿）。两份 resx 是随 `Iris.App` 一起编进程序集与 `en-US` 附属程序集的，所以这条命令会重新编译 `Iris.App`，不用另外先构建。

与期望不符时：

| 现象 | 原因 | 处理 |
|---|---|---|
| 某一条 `NewRowText_…` 仍红在中文那一段的第一条断言（`ShouldNotBe`） | 这个键不在 `Strings.resx` 里：键名抄错了一个字母，或文件没存盘。`AppResources.Get`（`src/Iris.App/Resources/AppResources.cs:14`）取不到键时不报错，返回「未知错误」那句 | 对照 Step 8 的片段改 `name="…"` |
| 红在英文那一段的 `ShouldBe(english)`，实际值是中文 | 这个键只在 `Strings.resx` 里、不在 `Strings.en-US.resx` 里（英文资源里找不到时会退回中文资源） | 对照 Step 9 的片段补上 |
| 红在某条 `ShouldBe`，实际值与期望只差空格、标点或大小写 | `<value>` 抄错 | 以 S0-12 的表为准改 `<value>`；同一条在 Step 10 的 catalog 里也要是同一个值 |
| 构建在 `Strings.resx` 或 `Strings.en-US.resx` 上报错 | 插入的片段把 XML 弄坏了（标签没闭合，或插到了 `</root>` 之后） | 对照 Step 8、9 的片段与插入位置 |
| 前十二条里有变红的 | 不是这一步引入的（这一步只动了资源文件） | 回 Step 7 的排查表 |

这一步只证明「运行时两种语言都取得到」。「两份 resx 与 catalog 三处的键与值完全一致」测试不查（仓库里没有这样的测试），由 Step 12 把关。

- [ ] **Step 12: 核对「两份 resx 与 catalog 一致」，构建，跑本地化审计**

(a) 三处一致的快检（不依赖构建，几秒钟出结果；先跑它，有错改起来快）。在仓库根目录跑，Windows PowerShell 5.1 与 Git Bash 都可以：

```
python -c "import json,xml.etree.ElementTree as E;r='src/Iris.App/Resources/';f=lambda p:{d.attrib['name']:d.findtext('value') or '' for d in E.parse(p).getroot().findall('data')};zh=f(r+'Strings.resx');en=f(r+'Strings.en-US.resx');c=json.load(open('build/localization/app-catalog.json',encoding='utf-8'));bad=[k for k in sorted(set(zh)|set(en)|set(c)) if c.get(k)!={'zh-CN':zh.get(k),'en-US':en.get(k)}];print(len(zh),len(en),len(c),bad)"
```

期望只输出一行：`466 466 466 []`。三个数依次是 `Strings.resx`、`Strings.en-US.resx`、`app-catalog.json` 的键数；方括号里列的是「三处对不上」的键（只在其中一两处有，或 catalog 里的值与 resx 不逐字相同）。方括号不空：照 Step 8–10 的片段把点到名的键改齐，再跑到空为止。（这条只读命令撰写时在没改过的仓库上跑过，两种 shell 下都输出 `462 462 462 []`。它与审计脚本 `build/localization/audit.py:46–56` 查的是同一件事，只是不用等构建。）

(b) 构建，再跑审计（Task 10 步骤提纲的原话；审计的第二段要读 Release 构建出来的程序集）：

```
dotnet build Iris.slnx -c Release
& build/audit-localization.ps1 -EvidenceDirectory artifacts/localization-audit
```

第二条的 shell 与调用写法同 Step 1(e)。期望：构建 0 警告 0 错误；审计脚本不抛异常（没有红字），跑完 `$LASTEXITCODE` 是 `0`。拿三处输出与 Step 1(e) 记下的基线逐项比：

| 输出 | 与基线相比 |
|---|---|
| `Scanned C# and XAML; Chinese entries including comments: <N1>` | **相同**。本任务在 `src/` 下没有新增任何中文：三个新文件全英文，`SettingsWindow.xaml.cs` 加的两行没有中文，resx 不在扫描范围（只扫 `.cs` 与 `.xaml`，`build/LocalizationAudit/Program.cs:45–46`） |
| `Compiled resource values matched after XML newline normalization; composite formats parsed: <N2>` | **基线 + 8**（`Iris.App` 的中文、英文资源各多 4 条） |
| 最后一行 JSON | `"resourceCounts"` 里 `"App"` 是 `466`（基线 `462`），`"Core"`、`"Maintenance"` 不变；`"resourceErrors": []`；`"sourceEntries"`、`"commentEntries"`、`"reviewedNonUiEntries"` 三个数与基线相同 |

审计给既有中文条目算的指纹只用到「路径、类别、文字、所在语句、所在成员」，不含行号（`Program.cs:30–31`、`:82–88`），所以 `SettingsWindow.xaml.cs` 里既有的中文条目不会因为前面多了两行而变成「未审」。

与期望不符时：

| 现象 | 原因 | 处理 |
|---|---|---|
| 脚本抛 `Unreviewed source text or resource mismatch.`，JSON 的 `resourceErrors` 里有 `App: key sets differ` | 三处的键集不一样 | (a) 的快检会点出是哪个键；照 Step 8–10 改齐 |
| 同上，`resourceErrors` 里有 `App:<键>: resource differs from reviewed bilingual catalog` | catalog 里这个键的值与 resx 不逐字相同（多了空格、标点不同） | 以 S0-12 的表为准，三处改成一样 |
| 同上，有 `App:<键>: unexplained Chinese in English resource` | 中文贴进了 `Strings.en-US.resx` | 照 Step 9 |
| 同上，有 `App:<键>: empty resource` 或 `Duplicate key: …` | `<value>` 是空的；或同一个键加了两遍 | 照 Step 8、9 |
| 同一句异常，JSON 后面多出若干行 `<路径> <行号> <类别> '<文字>'`，路径是本任务动过的 `src/` 文件 | 类别是 `literal` / `interpolated` / `comment`：新增了中文；类别是 `resource-cache`：有 `static readonly` 字段里写了 `UiText.` 或 `AppResources.Get(`（2.10、R7：切换语言后不更新） | 三个新文件的注释与字面量一律英文；`UiText` 的四条必须是 Step 5 那样的 `=>` 属性 |
| 脚本抛 `Compiled resource or syntax audit failed.`，前面有一行 `Iris.App zh-CN: compiled=462 source=466 assembly=<路径>` 和几行 `Mismatch SettingsRow…` | 审计读到的程序集是改资源之前编的 | 先确认本步的构建命令成功了，再跑一次审计。仍然如此：看 `assembly=` 后面的路径。审计取的是 `src/Iris.App/bin/Release` 下枚举到的第一个 `Iris.App.dll`（`Program.cs:127–128`）；按目录逐层枚举，先到的应当是 `net10.0-windows10.0.19041.0/Iris.App.dll`，也就是本步构建写的那一个。路径里带 `\win-x64\` 说明取到的是另一个输出目录里的旧产物（撰写时那里有一份 2026-11-08 的）→ 停下来报告，不删目录、不改审计工具 |
| Step 1(e) 的基线本来就有失败条目 | 那些不是本任务的 | 判据改成「相对基线没有多出任何一条」，逐条比 |

`artifacts/` 下的证据文件（`source-inventory.json`、`source-inventory.json.ui.json`、`audit-result.json`）不暂存。

- [ ] **Step 13: 跑回归过滤集；把 2.13 第 11 条的核实结果写回计划**

(a) 先跑本任务的过滤组，再跑 5.0 的「设置全集」（任务 1 若按计划 Task 1 Step 1(c) 把过滤式换成了别的写法并回填进 5.0，这里照 5.0 的写法换）：

```
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsViewModelDetailViewTests|FullyQualifiedName~SettingsViewModelTests|FullyQualifiedName~Iris.App.Tests.Localization|FullyQualifiedName~SettingsWindowPresenterTests"
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~Iris.App.Tests.Settings|FullyQualifiedName~Iris.App.Tests.Themes|FullyQualifiedName~Iris.App.Tests.Localization|FullyQualifiedName~Iris.App.Tests.Updates|FullyQualifiedName~MouseRenderingTests|FullyQualifiedName~HistoryRenderingTests|FullyQualifiedName~ThemeServicePreviewTests"
```

期望：两条都是 **0 失败**。把两条的汇总行（总数、通过、失败、跳过）原样记下来。条数怎么对：

- 第一条里 `SettingsViewModelDetailViewTests` 占 16 条，其余全是既有用例。
- 第二条：新测试类的命名空间是 `Iris.App.Tests.Settings`，所以它也在「设置全集」里。上一个任务最后一次跑「设置全集」留有汇总行的话，这一次的总数应当正好多 16（本任务只新增这 16 条，没有删改任何既有用例）；没有留就只看失败为 0，并把这次的汇总行留给任务 11 用。

按推算既有用例一条都不受影响，理由（撰写时逐条查过）：两个新属性默认是 `false`，没有任何既有代码读写它们；`OnViewModelPropertyChanged` 里新加的分支只在页面键变化时把本来就是 `false` 的两位再写一次 `false`（setter 值没变不发通知）并把两个本来就是 `null` 的字段再清一次；四条新文字还没有使用者；`SettingsWindow.xaml` 没动；`tests/Iris.App.Tests` 里没有用反射遍历 `UiText`、`SettingsViewModel` 或 `SettingsWindow` 全部成员的用例，也没有写死资源键总数的用例。

有既有用例变红：先单独把那一条再跑一次（`--filter "FullyQualifiedName~<类名>.<用例名>"`），排除渲染、时序类用例的偶发失败。稳定变红 → 按 5.0「既有测试变红时的处理顺序」判断；本任务「要改的既有测试：无」，所以**不改既有测试**，把用例名与失败信息原样记下，停下来报告。

(b) 写回计划（Task 10「范围与要点」：核实 2.13 第 11 条，结果写回 S0-8 的情形表）。核实本身在 Step 1(b) 已经做完，结论是「不发通知」；`SamePageKey_KeepsTheDetailFlags` 的四条用例在 Step 7 起就是绿的，是这条结论的测试证据。改计划文件 `docs/superpowers/plans/2026-11-15-acrylic-ui-phase-2-settings-window.md` 的两处，都按文字定位（这个文件别人还在改，不记行号）：

第一处，2.13 的第 11 条。原文整行：

```text
11. `CurrentPageKey` 写入相同的值时是否发通知（S0-8 的「同页重复 `ShowAsync` 保留详情」依赖它不发）。
```

改成（划掉问句、写上结论，写法照同一节第 12 条）：

```text
11. ~~`CurrentPageKey` 写入相同的值时是否发通知~~ **已核实（任务 10）：不发**（`SettingsViewModel.cs:345–361`：setter 先经 `SettingsWindowPresenter.NormalizePageKey` 归一，归一后的值与当前值不同才赋值并通知；只差大小写或首尾空白的写法、以及会被归一成 `general` 的未开放页面键，归一后与当前值相同时同样不发）。S0-8 的「同页重复 `ShowAsync` 保留详情」成立，由 `SettingsViewModelDetailViewTests.SamePageKey_KeepsTheDetailFlags` 守住。
```

第二处，S0-8「由这五条推出的各种情形」那张表里的一行。原文整行：

```text
| 同一页重复 `ShowAsync("translation")` | 留在详情 | 无通知（依赖 2.13 第 11 条；若实际会发通知，T5 改成「新旧页面键不同才复位」，代码隐藏自己记上一次的页面键） |
```

改成（只换第三格）：

```text
| 同一页重复 `ShowAsync("translation")` | 留在详情 | 无通知（任务 10 已核实，见 2.13 第 11 条：`CurrentPageKey` 的 setter 只在归一后的页面键与当前值不同时才赋值并通知，`SettingsViewModel.cs:345–361`；T5 不需要自己记上一次的页面键。由 `SettingsViewModelDetailViewTests.SamePageKey_KeepsTheDetailFlags` 守住） |
```

两处的原文找不到（别人已经改过措辞）：不硬套，把上面两段「改成」的文字交给协调方，由协调方落到计划里。计划文件**不进** Step 14 的暂存清单：它目前是未跟踪文件（`git status` 里是 `??`），计划里也没有哪一条 `git add` 带上它；什么时候纳入提交由任务 14 定。

- [ ] **Step 14: 暂存（不提交）**

本任务的八个路径：四个新文件、四个改过的既有文件。`artifacts/` 不暂存（已被 `.gitignore` 忽略），计划文件不暂存（Step 13 末尾）。

```
git add src/Iris.App/Settings/SettingsViewModel.DetailView.cs src/Iris.App/Settings/SettingsWindow.DetailNavigation.cs src/Iris.App/UiText.AcrylicSettings.cs tests/Iris.App.Tests/Settings/SettingsViewModelDetailViewTests.cs src/Iris.App/Settings/SettingsWindow.xaml.cs src/Iris.App/Resources/Strings.resx src/Iris.App/Resources/Strings.en-US.resx build/localization/app-catalog.json
git status --short -- src/Iris.App/Settings/SettingsViewModel.DetailView.cs src/Iris.App/Settings/SettingsWindow.DetailNavigation.cs src/Iris.App/UiText.AcrylicSettings.cs tests/Iris.App.Tests/Settings/SettingsViewModelDetailViewTests.cs src/Iris.App/Settings/SettingsWindow.xaml.cs src/Iris.App/Resources/Strings.resx src/Iris.App/Resources/Strings.en-US.resx build/localization/app-catalog.json
git diff --cached --numstat -- src/Iris.App/Resources/Strings.resx src/Iris.App/Resources/Strings.en-US.resx build/localization/app-catalog.json
git status --short -- src tests build
git log -1 --format=%h
```

期望：

- 第一条没有错误。出现 `warning: in the working copy of '…', LF will be replaced by CRLF the next time Git touches it` 属正常（仓库 `core.autocrlf=true`，换行符是 LF 的文件都会有这一句），不用处理。
- 第二条正好八行，四个新文件是 `A `，四个既有文件是 `M `（第二列是空格）：

  ```
  M  build/localization/app-catalog.json
  M  src/Iris.App/Resources/Strings.en-US.resx
  M  src/Iris.App/Resources/Strings.resx
  A  src/Iris.App/Settings/SettingsViewModel.DetailView.cs
  A  src/Iris.App/Settings/SettingsWindow.DetailNavigation.cs
  M  src/Iris.App/Settings/SettingsWindow.xaml.cs
  A  src/Iris.App/UiText.AcrylicSettings.cs
  A  tests/Iris.App.Tests/Settings/SettingsViewModelDetailViewTests.cs
  ```

- 第三条三行：`16	0	build/localization/app-catalog.json`、`12	0	src/Iris.App/Resources/Strings.en-US.resx`、`12	0	src/Iris.App/Resources/Strings.resx`（这三个文件在本阶段只有本任务动过，所以暂存区相对 `dc08c48` 的差异就是这四条文字）。数字不是这样：有别的改动混进了这三个文件，对照 Step 8–10 找出来。
- 第四条：列出的是任务 1–10 累计暂存的全部路径，每一行的第二列都应当是空格。出现第二列非空的行（` M`、`MM`、`AM`）或 `??` 开头的行：是上面八个路径之一 → 暂存之后又改过，重新 `git add` 那个文件；不是 → 那是别的任务留下的，不动，记下来报告。
- 第五条输出以 `dc08c48` 开头：没有产生新提交。**本任务不提交**，整个阶段只在任务 14 提交一次（5.0）。

- [ ] **Step 15: 本任务自检清单**

逐条过；有一条对不上就回到写明的 Step 改，改完从那个 Step 往后重跑。下面的 `grep` 在 Git Bash 里跑，`python`、`git` 两种 shell 都可以，都在仓库根目录。

先跑这一组只读命令（`#` 开头的是编号，不用敲），把输出与下面的表逐条对：

```
# C1
git status --short -- "src/Iris.App/Settings/SettingsViewModel*.cs"
# C2
grep -rn "ResetDetailNavigation" src/Iris.App --include=*.cs
# C3
grep -nE "KeyDown|Key[.]Enter|Key[.]Escape|ContextMenu|Dispatcher" src/Iris.App/Settings/SettingsWindow.DetailNavigation.cs
# C4
grep -cE "DetailOpen|UiText[.]SettingsRow" src/Iris.App/Settings/SettingsWindow.xaml
# C5
grep -c "=> global::Iris.App.Resources.AppResources.Get(" src/Iris.App/UiText.AcrylicSettings.cs
grep -c "static readonly" src/Iris.App/UiText.AcrylicSettings.cs
# C6
python -c "import re,sys;h=re.compile('[%s-%s]'%(chr(0x3400),chr(0x9fff)));[print(p,len(h.findall(open(p,encoding='utf-8').read()))) for p in sys.argv[1:]]" src/Iris.App/Settings/SettingsViewModel.DetailView.cs src/Iris.App/Settings/SettingsWindow.DetailNavigation.cs src/Iris.App/UiText.AcrylicSettings.cs tests/Iris.App.Tests/Settings/SettingsViewModelDetailViewTests.cs
# C7
git status --short -- specs
# C8
git log -1 --format=%h
```

| # | 期望 |
|---|---|
| C1 | 只有一行：`A  src/Iris.App/Settings/SettingsViewModel.DetailView.cs`（七个既有的 `SettingsViewModel*.cs` 一个都没动） |
| C2 | 正好两行：`SettingsWindow.DetailNavigation.cs` 里的定义、`SettingsWindow.xaml.cs` 里的调用 |
| C3 | 没有输出 |
| C4 | `0` |
| C5 | 第一条 `4`，第二条 `0` |
| C6 | 四行，每行是「路径 汉字个数」：三个 `src/` 文件都是 `0`；测试文件是 `14`（只有四条 `[InlineData]` 里的期望值：4 + 4 + 4 + 2 个字；注释里没有中文） |
| C7 | 没有输出 |
| C8 | 以 `dc08c48` 开头 |

**对照 Task 10「范围与要点」**

- [ ] 视图模型的两个属性照 S0-8 的表：`IsTranslationDetailOpen`、`IsOcrDetailOpen`，`public bool`，默认 `false`，值变了才通知，setter 里只有赋值和 `OnPropertyChanged()`（Step 4 的全文；`DetailFlags_DefaultToFalse`、`DetailFlag_NotifiesOnceWhenTheValueChanges_AndNotWhenItIsTheSame` 绿）。
- [ ] 没有改 `SettingsViewModel` 的任何既有文件与既有方法（C1）。两位不进草稿：不改变 `IsDirty`，除了自己那一条不触发任何通知（`DetailFlags_AreIndependent_AndNeverTouchTheDraft` 绿）。
- [ ] 代码隐藏的新 partial 里只有：两个 `Id` 字段、它们的两个只读属性（计划外，见下面「偏差清单」）、T5 一个方法。没有 T1–T4，没有按键处理，没有菜单（C3）。
- [ ] T5 写成单独一个方法 `ResetDetailNavigation(SettingsViewModel vm)`，方法体是四条赋值；任务 11 在末尾接着补清 `_ocrRestorePending`、`_ocrAddPending` 的两行，本任务没有建这两个字段。
- [ ] `OnViewModelPropertyChanged` 里只加了转调，原有内容没动：Step 7 记下的 `numstat` 是 `2	0`；C2 正好两行。
- [ ] 2.7「已核实」的三处行号复查过，与记录一致（Step 1(a) 的记录）。
- [ ] 文字正好四条，键名、中文、英文与 S0-12 的表逐字相同；先按中文值搜过既有键、没有可复用的（Step 1(c)）；四处都改了且对得上（Step 12(a) 输出 `466 466 466 []`；C5）。两份 resx 键的先后一致、加在 `</root>` 之前（Step 9 的 `SAME-KEYS-SAME-ORDER`；Step 14 的两个 `12	0`）。
- [ ] `UiText` 的四条是 `=>` 属性，不是 `static readonly` 字段（C5；`NewRowText_ResolvesInChineseAndEnglish` 四条绿——同一个属性先取到中文、再取到英文）。
- [ ] 四个新文件的注释全是英文；三个 `src/` 新文件里没有任何中文（C6；Step 12 审计的 N1 与基线相同）。
- [ ] 2.13 第 11 条核实了（不发通知），结果写回了 2.13 与 S0-8 的情形表（Step 13(b)）。
- [ ] 加完文字跑了本地化审计，相对基线没有多出任何问题，`resourceCounts.App` 是 `466`（Step 12(b)）。

**对照 S0-8**

- [ ] 状态在视图模型的新 partial 上、是纯视图状态；代码隐藏的两个字段名是 `_translationDetailId`、`_ocrDetailId`，类型 `string?`，不在详情态时是 `null`。
- [ ] T5：收到 `CurrentPageKey` 的变更通知，两个属性都置 `false`、两个 `Id` 字段都清掉（`PageChange_ClosesBothDetailViews_AndClearsBothIds` 四条绿）。
- [ ] 情形表里本任务管得到的四行都有用例：「切到别的页再切回来 → 列表」（上一条）；「同一页重复 `ShowAsync` → 留在详情」与「详情态下点导航里的当前页 → 留在详情」（`SamePageKey_KeepsTheDetailFlags` 四条绿）；「关窗后重新打开 → 列表」（`DetailFlags_DefaultToFalse`：每次打开都是新的视图模型）。
- [ ] T1–T4、冻结与只读的 `IsEnabled` 归属都没有做（任务 11、12）；`SettingsWindow.xaml` 没有绑这两个属性（C4），界面没有可见变化。

**对照 S0-9**

- [ ] `OnKeyDown` 一行没改；没有新增任何窗口级按键处理（本任务对 `SettingsWindow.xaml.cs` 只多了两行转调；C3）。Esc 的行为与改前完全一样。
- [ ] 两个列表上的 Enter 处理、行菜单、`ContextMenuOpening`、把手起拖都没有做（任务 11、12）；四条新文字只建不用（C4），使用者在任务 11、12。

**通用（5.0 与工程约束）**

- [ ] `src/` 里既有的中文一个字没动、没搬；没有改 `specs/`（C7）；没有碰任何 `.csproj` 与包引用（八个路径里没有）；没有改任何既有测试（Task 10「要改的既有测试：无」）。
- [ ] 新测试不联网（夹具照抄 `SettingsWindowTranslationUiTests`，全是替身）；新代码不写日志、不抛异常、不涉及任何密钥。
- [ ] 本任务过滤组与「设置全集」都是 0 失败，汇总行记下了（Step 13(a)）；构建 0 警告（Step 12(b)）。
- [ ] 八个路径已暂存，`artifacts/` 与计划文件没有暂存，没有提交（Step 14；C8）。

**记入完成报告的偏差清单**

- [ ] 必记：`SettingsWindow` 上计划外新增两个 `internal` 只读属性 `TranslationDetailId`、`OcrDetailId`（S0-8 只写了两个私有字段）。原因：这两个字段在本任务里只被赋 `null`、没有读者，不加读者会报 `CS0414`，而仓库把警告当错误（Step 6；文末第 1 条）。
- [ ] 走了才记：Step 6 的 `IDE0032` 退路（两个属性改成了两个方法）；Step 1(c) 判定复用了某个既有键；Step 1(e) 的审计基线本身不干净、Step 12 的判据因此改成「相对基线没有多出」。
- [ ] 被修改的既有测试：无。

---

#### ⚠ 与裁决不符之处 / 需协调方裁决

**已裁决（协调方裁决 C11，2026-11-23：取做法 (a)，步骤不用改，记入偏差清单）**

1. **`SettingsWindow` 上多了两个计划外的 `internal` 只读属性 `TranslationDetailId`、`OcrDetailId`。** S0-8 与 Task 10 只写了两个私有的 `Id` 字段。事实：本任务里这两个字段只在 T5 里被赋 `null`，读它们的 T1–T4 属于任务 11、12；`string` 字段只被赋常量、又没有读者时编译器报 `CS0414`，而仓库 `TreatWarningsAsErrors=true`、`NoWarn` 里没有它（`Directory.Build.props:6`、`:13`、`:22`）——照 S0-8 的字面写，本任务编不过。步骤选的做法 (a)：加两个只读属性当读者，顺带让测试能断言「两个 `Id` 被清掉」，任务 11、12 的测试也用得上。另外两种做法：
   - (b) 字段声明外包一对 `#pragma warning disable CS0414` / `#pragma warning restore CS0414`，测试改用反射读字段。不多出成员；但 `src/Iris.App` 里没有 `#pragma warning disable` 的先例，任务 11 给字段加上读者之后还要记得删。
   - (c) 两个字段挪到任务 11、12 再建。与 Task 10「本任务只放 T5……和两个 `Id` 字段」不符，T5 的「清两个 `Id`」在本任务也就测不了。

   协调方若选 (b) 或 (c)，要跟着改的地方：Step 2（六处 `window.TranslationDetailId` / `window.OcrDetailId`）、Step 3 的表、Step 6 的全文与要点、Step 15 的偏差清单、前面「本任务定下的名字」那张表。选 (a) 则不用动，记入偏差清单即可。

**与计划字面不完全一致、但没有越出裁决的地方（知会，不需要裁决）**

2. **步骤顺序与多出的一次基线。** 提纲把「复查三处行号」排在「跑，确认失败」之后；步骤把它提到 Step 1（5.0：每个任务第一步重新 grep 行号）。Step 1(e) 多做了一次 Release 构建加审计作基线——提纲没有这一步，加它是为了让提纲里「确认无新增问题」有东西可比。
3. **测试文件比 4.1 那一行写得多。** 4.1 对这个文件的描述是「两个属性默认假、变了才通知、互不影响」；步骤按 Task 10 步骤提纲加了窗口级的切页复位与四条文字的用例，另加一组「写入同一页的页面键不复位」（守 2.13 第 11 条）。整个类放进不并行的 `Localization` 集合，因为文字用例要切进程级的界面语言。
4. **`ResetDetailNavigation` 没有线程守卫。** S0-8 没有要求；写在这里是因为同一个方法里另两个分支有。理由见「本任务定下的名字」后面的第二点。
5. **catalog 的四条按字母序插在 `SettingsRetryPendingApply` 与 `SettingsSave` 之间**，没有追加在文件末尾。计划只要求值与 resx 一致，审计也不看先后；resx 两份则照计划追加在 `</root>` 之前。
6. **2.13 第 11 条的结论（不发通知）是撰写时读代码得到的。** 执行时由 Step 1(b) 复核、`SamePageKey_KeepsTheDetailFlags` 四条用例守住；Step 13(b) 给了写回计划的两段确切文字。S0-8 写的那条退路（代码隐藏自己记上一次的页面键）因此没有做。

**风险（没有经过构建与运行验证的判断）**

7. 全部代码与预期都没有编译、运行过。最可能与实际不符的地方及其去向：
   - 代码风格规则会不会对两个表达式体属性报 `IDE0032` 并算成错误——Step 6 有退路（改成两个方法）。
   - `--filter` 的写法在本仓库的测试平台下是否被接受——沿用任务 1 按计划 Task 1 Step 1(c) 定下并回填到 5.0 的写法。
   - 条数（16 条；Step 7 的 12 绿 4 红）按「每条 `[InlineData]` 算一条」推算；Step 3 的「只有 `CS1061`、`CS0117` 两种编号」同样是推算。实际数字或编号不同、但原因就是那一步写的原因时，照原因处理，并在完成报告里写明实际看到的。
   - 审计读的是 `src/Iris.App/bin/Release` 下枚举到的第一个 `Iris.App.dll`。按读代码的判断它是本次构建的产物，`win-x64` 子目录里那份 2026-11-08 的旧产物排在后面；这一点没有跑过，Step 12 的排查表写了怎么认、认出来就停下来报告。

**验证命令：** `FullyQualifiedName~SettingsViewModelDetailViewTests|FullyQualifiedName~SettingsViewModelTests|FullyQualifiedName~Iris.App.Tests.Localization|FullyQualifiedName~SettingsWindowPresenterTests` —— 预期全绿；再跑「设置全集」。

**要改的既有测试：** 无。

### Task 11: OCR 页：列表与详情

**目的：** 按 S0-10 把 OCR 页改成「六项页面级设置 + 服务列表 → 详情」。先做它，是因为它没有拖动排序，钻取的写法可以在较简单的一页上先验证。（原先「没有自动进入」这条理由已不成立：U8 要求新增后自动进入；U6 还要求保存后回写选中项。顺序不变。）

**Files:**
- Create: `tests/Iris.App.Tests/Settings/OcrDetailNavigationTests.cs`
- Modify: `src/Iris.App/Settings/SettingsWindow.xaml`——OCR 页 :180–286（全局项 :195–209、添加按钮 :213–214、列表 :216–230、删除按钮 :231、详情面板 :232–277、测试区 :279–284）
- Modify: `src/Iris.App/Settings/SettingsWindow.DetailNavigation.cs`（加 T1、T2 的 OCR 版，T3-OCR、T4-OCR，行菜单构造，列表 Enter 处理，`ContextMenuOpening`；T5 里补清 `_ocrRestorePending` 与 `_ocrAddPending`）
- Modify: `src/Iris.App/Settings/SettingsWindow.xaml.cs`——`OnViewModelPropertyChanged` 再加两处转调（`SelectedOcrProfile` 变更 → T4-OCR；`CanEdit` 变更 → T4-OCR 的回写复查）
- Test（要改）: `tests/Iris.App.Tests/Settings/OcrSettingsRenderingTests.cs`；视情况 `OcrSettingsUiTests.cs`、`OcrSettingsLifecycleTests.cs`

**范围与要点：**
- 结构、列表行、菜单、测试区位置、面包屑全部照 S0-10。三项用户裁决照做：
  - **U8 新增后自动进入**：T3-OCR（两个添加按钮仍绑原命令，视图层 `Click` 后延后一拍判断是否真的加成功）。
  - **U6 保存 / 重载后留在详情**：T4-OCR（视图层按 `_ocrDetailId` 回写 `SelectedOcrProfile`；`CanEdit` 为假时先挂起；列表里找不到才回列表）。
  - **U7 + C4 只读时仅当前选中项可进详情、不可改；保存中列表冻结**：原可编辑容器（:194–278）拆开，各控件的 `IsEnabled` 归属照 S0-10「只读模式」的表；新面板 `OcrServicesPane` 包住两个添加按钮与 `OcrProfilesListBox`，由 `IsSaving` 的 `DataTrigger` 冻结；行入口（名字区、右箭头）的 `IsEnabled` 条件是「窗口的 `CanEdit` 为真，或该行 `IsSelected` 为真」；T1 的 OCR 版多一条守卫。
- 前置事实已核实（2.7「已核实」），本任务不用再等任务 10 的结论。**不改 `SelectedOcrProfile` 的 setter**（C4）；U7 在本页只落实了一半，记入偏差清单，并写进完成报告提请用户（第 6.3 节）。
- 本任务的详细步骤必须写清：行入口 `IsEnabled` 的具体 XAML（`MultiBinding` 或 `DataTrigger`，S0-10 的实现提示）；`OcrProfilesListBox` 的 `SelectedItem` 绑定模式与写入被拒后的读回方式（S0-10，2.13 第 25 条）。
- 详情字段：一张 `IrisCard`，字段顺序与「标签在上、控件在下」的排布照现状；百度面板（:249–262）、Windows 面板（:263–266）的显隐条件原样；**不新增分组标题**（OCR 没有现成的分组名字符串）。三个复选框（`OcrEnabledCheck` :244、`OcrDirectionCheck` :252、`OcrParagraphCheck` :255）与两个全局复选框（`OcrAutoCopyCheck` :203、`OcrSilentNotifyCheck` :206）改开关。
- 页级说明 `UiText.OcrPageDescription`（:193）按 C7 保留：紧跟页标题（:192），与页标题一起放在 `OcrListPanel` 里（S0-10），不放进任何 `IsEnabled` 绑到 `CanEdit` 的容器；套 `IrisRowDescription` + `IrisCardGap`（S0-4）。
- 保持：2.6 列出的全部名字与类型都在、都不进模板；`OcrApiKeyPasswordBox`、`OcrSecretKeyPasswordBox` 是原生 `PasswordBox`，`OnOcrPasswordChanged` / `SyncOcrPasswords`（cs:347–361）不动；`OcrTimeoutTextBox` 仍被 `FlushFocusedBinding` 按名字找到；底栏的 `OcrTestStatusText`、`OcrRetryCleanupButton` 不动。
- 详情控件在列表态下是折叠的但仍在树里：`OcrSettingsUiTests` / `OcrSettingsLifecycleTests` 里只取值、设值、看 `IsEnabled` 的断言应当不用改。
- 不做：不加拖动、不加上移下移、不加行内开关；不改 `MergeOcrSettings` 等任何视图模型方法（U6 的回写只经 `SelectedOcrProfile` 的公开 setter）。
- 已知副作用（S0-10，进偏差清单）：回写触发旧选中项的 `InvalidateTest()`；等待回写期间详情短暂显示视图模型当前选中的那一项。
- 既有测试里凡是断言「`CanEdit` 为假时 OCR 列表 / 可编辑容器整体 `IsEnabled` 为假」的，会因 U7 变红：逐条改成 S0-10 表里的新口径，并记入「改过的既有测试」（执行时先 grep `OcrSettings*Tests.cs` 里对 `IsEnabled` 的断言，列出清单再改）。
- 风险：R1、R4、R8；2.13 第 13 条（内置服务删除命令的 `CanExecute`）、第 14 条（重载时选中项是否短暂为空）开工先核实。

**步骤提纲：**
- [ ] 核实 2.13 第 13、14、25、26 条；读 `OcrSettingsRenderingTests.cs` 弄清 42 张图各拍的是哪个状态
- [ ] 写 `OcrDetailNavigationTests`（凡涉及延后一拍的断言，先泵调度器消息再断言）：
  - 默认列表态且六个全局控件可见；点名字 / 右箭头 / 行上 Enter 进入详情（`IsOcrDetailOpen` 为真、面包屑显示服务名、六个全局控件不可见但 `FindName` 仍取得到）；面包屑返回且选中项不变；切到别的页再回来是列表
  - 删除当前自定义服务后回列表；内置服务的删除菜单项不可用；测试按钮与取消按钮不在任何冻结容器内
  - **新增后自动进入（U8）**：点任一添加按钮后 `IsOcrDetailOpen` 为真、选中项是新项、面包屑显示新项的名字；添加命令不可执行时点了不进入
  - **保存后留在详情（U6）**：进入一个**不是第一项**的服务的详情，保存成功后仍在详情、`SelectedOcrProfile.Id` 仍是原来那一项；重新加载同理；`CanEdit` 为假期间不回写、恢复为真后回写
  - **只读时仅当前选中项可进详情、其余行入口禁用、详情内不可改（U7 + C4）**：`CanEdit` 为假且 `IsSaving` 为假时——当前选中行的名字区、右箭头 `IsEnabled` 为真，经三个入口都能进入详情；其余每一行的名字区、右箭头 `IsEnabled` 为假，点了、在该行上按 Enter 都不进入（`IsOcrDetailOpen` 保持假、`SelectedOcrProfile` 不变）；点非选中行的空白处或按方向键之后，列表选中行与 `vm.SelectedOcrProfile` 仍一致；详情内 `OcrDetailsPanel` 的 `IsEnabled` 为假；六个全局设置、两个添加按钮、行内「⋯」的 `IsEnabled` 为假；面包屑可用。另一条：`CanEdit` 为真时每一行的入口都可用
  - **保存中列表冻结（U7）**：保存进行中 `OcrServicesPane` 的 `IsEnabled` 为假、三个入口都进不去；面包屑仍可用；取消测试按钮仍可用
- [ ] 跑，确认失败
- [ ] 改 XAML（从测试区往前）；补代码隐藏 partial
- [ ] 给 `OcrSettingsRenderingTests` 的详情态截图加「进入详情」
- [ ] 跑本任务过滤组与「设置全集」；`git add`

**详细步骤：**

> **声明：** 代码与命令尚未运行过；行号以 `dc08c48` 为参考，开工 Step 1 先 `grep` 重取行号。

#### 详细步骤

- [ ] **Step 1：开工核实——重取 OCR 页行号、核对既有测试 `IsEnabled` 断言与渲染截图逻辑**

```bash
git status --short -- src/Iris.App/Settings/ tests/Iris.App.Tests/Settings/
grep -n 'x:Name="OcrPageScroll"\|x:Name="OcrProfilesListBox"\|x:Name="DeleteOcrProfileButton"\|x:Name="OcrDetailsPanel"\|x:Name="OcrTestButton"\|x:Name="ScreenshotPageScroll"' src/Iris.App/Settings/SettingsWindow.xaml
grep -n 'IsEnabled\|OcrProfilesListBox\|DeleteOcrProfileButton\|OcrDetailsPanel' tests/Iris.App.Tests/Settings/OcrSettings*.cs
```

核实结果预期：
1. `SettingsViewModel.DetailView.cs`、`SettingsWindow.DetailNavigation.cs`、`UiText.AcrylicSettings.cs` 已在暂存区。
2. `OcrSettingsUiTests.cs` / `OcrSettingsLifecycleTests.cs` 中对 `IsEnabled` 的断言仅针对 `OcrTestButton` 与 `OcrRetryCleanupButton`，无断言「`OcrProfilesListBox` 在只读下 `IsEnabled == false`」的用例；`OcrSettingsRenderingTests.cs:253–300` 在访问 `OcrApiKeyPasswordBox` / `OcrSecretKeyPasswordBox` / `OcrBaseUrlTextBox` 渲染详情截图时原假定同屏可见，Step 6 在该处补一步进入详情。

- [ ] **Step 2：新建 `tests/Iris.App.Tests/Settings/OcrDetailNavigationTests.cs`（TDD 红灯）**

新建 `tests/Iris.App.Tests/Settings/OcrDetailNavigationTests.cs`（复用 `OcrSettingsUiScope` 夹具；凡涉及 `Dispatcher.BeginInvoke` 延后一拍的场景，先 `await f.IdleAsync()` 泵完队列再断言），覆盖全部 6 组规约：

1. **默认列表态与三入口钻取 / 面包屑返回 / 切页复位**：
   - 打开 `"ocr"` 页，`f.Vm.IsOcrDetailOpen.ShouldBeFalse()`，`OcrListPanel` 可见、`OcrDetailHost` 折叠，6 个全局控件可见，同时 `f.Window.FindName("OcrNameTextBox")` / `FindName("OcrApiKeyPasswordBox")` 等详情控件仍非空（留在同一名称域树中，R1）。
   - 分别经「点行名字区」、「点行右箭头 (`ChevronRight20`)」、「选中行按 `Enter`」进入详情：`IsOcrDetailOpen` 变真、`f.Window.OcrDetailId == profile.Id`、`OcrBreadcrumbCurrentText.Text == profile.DisplayName.Value`、6 个全局控件 `IsVisible == false`、`OcrDetailsPanel` 与测试卡可见。
   - 点 `OcrBreadcrumbBackButton` 返回列表：`IsOcrDetailOpen` 变假、`OcrDetailId` 为 `null`、`SelectedOcrProfile` 保持不变。
   - 在详情态切到 `"general"` 再切回 `"ocr"`：回到列表态（T5）。
2. **行菜单与删除回列表 + 测试区不被冻结**：
   - 自定义 OCR 服务的行「⋯」菜单含 1 项（`UiText.OcrDelete`）且 `IsEnabled == true`，执行后该项从集合移除并自动退回列表态（`IsOcrDetailOpen == false`、`OcrDetailId == null`）。
   - 内置 OCR 服务（`IsBuiltIn == true`）的行「⋯」菜单中删除项 `IsEnabled == false`。
   - `OcrTestButton`、`OcrCancelTestButton`、`OcrTestResultText` 不在任何 `CanEdit` 或 `IsSaving` 禁用祖先之下。
3. **新增 OCR 服务后自动进入详情（U8 / T3-OCR）**：
   - 在列表态分别点击添加百度、添加 Windows 按钮：`await f.IdleAsync()` 后 `IsOcrDetailOpen` 为真、`SelectedOcrProfile` 为新加项、`OcrDetailId == SelectedOcrProfile.Id`、面包屑显示新项名称。
   - 当添加命令不可执行（如 `!CanEdit`）时，点击不进入详情。
4. **保存或重新加载后留在原详情项（U6 / T4-OCR）**：
   - 添加第二个 OCR 服务使目标项**不是第一项**，进入第二项详情，修改其他字段并执行保存（`ExecuteSaveAsync`）+ `await f.IdleAsync()`：断言仍在详情态（`IsOcrDetailOpen == true`），且 `SelectedOcrProfile.Id` 已被回写为第二项 `Id`（而非被 `MergeOcrSettings` 重置到第一项）！重新加载（`ReloadAsync`）同理。
   - 保存挂起期间（`IsSaving == true`、`CanEdit == false`）收到列表重建时不强写被拒，待保存完成 `CanEdit` 恢复为真后自动完成回写。
5. **只读模式（`CanEdit == false && !IsSaving`，U7 + 裁决 C4）**：
   - 6 个页面级设置、两个添加按钮、所有行的「⋯」按钮 `IsEnabled == false`，`Shift+F10` 不弹出菜单。
   - **当前选中行**的名字区与右箭头 `IsEnabled == true`，经名字区、右箭头、`Enter` 均可进入详情；进入后 `OcrDetailsPanel.IsEnabled == false`（字段只读灰态）、`OcrBreadcrumbBackButton.IsEnabled == true` 可返回。
   - **非选中行**的名字区与右箭头 `IsEnabled == false`（文字呈 `TextFillColorDisabledBrush` 禁用观感），点击名字区、右箭头或按 `Enter` 均不进入详情；点击非选中行空白处或按上下方向键后，`OcrProfilesListBox.SelectedItem` 与 `f.Vm.SelectedOcrProfile` 依然严格同一引用（不脱节！）。
6. **保存中冻结（`IsSaving == true`，U7）**：
   - 保存进行中 `OcrServicesPane.IsEnabled == false`，当前选中行与非选中行的三个入口均进不了详情；若保存前已在详情态，`OcrBreadcrumbBackButton` 仍可用、能返回列表。

- [ ] **Step 3：跑新增测试确认失败（红灯）**

```bash
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~OcrDetailNavigationTests"
```

期望：因 XAML 尚无 `OcrListPanel` / `OcrDetailHost` / `OcrServicesPane` / `OcrBreadcrumbBackButton` 等元素而失败。

- [ ] **Step 4：扩充 `SettingsWindow.DetailNavigation.cs` 与 `SettingsWindow.xaml.cs` 实现 OCR 钻取逻辑（T1–T5）**

1. 在 `src/Iris.App/Settings/SettingsWindow.DetailNavigation.cs` 增加 OCR 钻取状态与处理方法（注释全部使用英文，零中文字面量）：
   - 私有字段：`private bool _ocrRestorePending;`、`private bool _ocrAddPending;`、`private HashSet<string>? _ocrIdsBeforeAdd;`。
   - 在 `ResetDetailNavigation(SettingsViewModel vm)` 末尾补三行：
     ```csharp
     _ocrRestorePending = false;
     _ocrAddPending = false;
     _ocrIdsBeforeAdd = null;
     ```
   - **T1-OCR（`TryOpenOcrDetail(OcrProfileDraftViewModel? profile)`）**：
     - 守卫：`if (profile is null || DataContext is not SettingsViewModel vm || vm.IsSaving) return;`
     - C4 守卫：`if (!vm.CanEdit && !ReferenceEquals(vm.SelectedOcrProfile, profile)) return;`
     - 选中并核实：`if (!ReferenceEquals(OcrProfilesListBox.SelectedItem, profile)) OcrProfilesListBox.SelectedItem = profile;`；`if (!ReferenceEquals(vm.SelectedOcrProfile, profile)) return;`
     - 翻转：`_ocrDetailId = profile.Id; _ocrRestorePending = false; vm.IsOcrDetailOpen = true;`，并 `Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => OcrBreadcrumbBackButton?.Focus()));`。
   - **T2-OCR（`CloseOcrDetail()`）**：
     - 设 `vm.IsOcrDetailOpen = false; _ocrDetailId = null; _ocrRestorePending = false; _ocrAddPending = false; _ocrIdsBeforeAdd = null;`，并在 `Loaded` 优先级把焦点移回 `OcrProfilesListBox` 当前选中项容器（或列表本身）。
   - **入口事件处理**：
     - `OnOcrRowEntryMouseLeftButtonUp` / `OnOcrRowOpenDetailsClick`：从 `sender` 的 `DataContext` 取 `OcrProfileDraftViewModel` 调 `TryOpenOcrDetail`。
     - `OnOcrProfilesListKeyDown`：仅当 `e.Key == Key.Enter` 且 `e.OriginalSource is ListBoxItem { DataContext: OcrProfileDraftViewModel profile }` 时，`e.Handled = true; TryOpenOcrDetail(profile);`（在按钮等子控件上按 Enter 不拦截，让按钮自身处理）。
     - `OnOcrRowMoreActionsClick`：核实 `vm.CanEdit && !vm.IsSaving`，先把该行写入 `OcrProfilesListBox.SelectedItem` 并核实 `ReferenceEquals(vm.SelectedOcrProfile, profile)`；用代码构造单菜单项 `ContextMenu`（`Header = UiText.OcrDelete`，`IsEnabled = vm.CanEdit && !profile.IsBuiltIn`，`Click` 调 `if (vm.DeleteOcrProfileCommand.CanExecute(null)) vm.DeleteOcrProfileCommand.Execute(null);`），挂在按钮上打开。
     - `OnOcrProfilesListContextMenuOpening`：若 `DataContext is not SettingsViewModel { CanEdit: true } || vm.IsSaving`，设 `e.Handled = true;` 阻止键盘 `Shift+F10` / 菜单键打开菜单；否则为当前选中项动态构造上述单菜单项菜单。
   - **C4 列表选中同步兜底（`SyncOcrListBoxSelectionOnReadOnly()`）**：
     - 在 `SettingsWindow.xaml.cs` 的 `OnOcrProfileSelectionChanged` 末尾调它：若 `DataContext is SettingsViewModel { CanEdit: false } vm && !ReferenceEquals(OcrProfilesListBox.SelectedItem, vm.SelectedOcrProfile)`，执行 `OcrProfilesListBox.SelectedItem = vm.SelectedOcrProfile;`（重入时两者已同一引用，立即返回不死循环）。
   - **T3-OCR（U8 新增自动进详情）**：
     - 给两个添加按钮挂 `PreviewMouseLeftButtonDown="OnAddOcrProfilePreviewMouseDown"` 与 `Click="OnAddOcrProfileClick"`（同时支持真实点击与测试里的 `RaiseEvent(ClickEvent)` / 命令触发）：若 `vm.CanEdit && !vm.IsSaving`，记录 `_ocrIdsBeforeAdd = vm.OcrProfiles.Select(p => p.Id).ToHashSet(StringComparer.Ordinal); _ocrAddPending = true;`，排一次 `Dispatcher.BeginInvoke(DispatcherPriority.Loaded, CheckPendingOcrAdd)`；`CheckPendingOcrAdd` 检查 `_ocrAddPending` 为真、集合中出现了不在 `_ocrIdsBeforeAdd` 里的新项（优先取 `vm.SelectedOcrProfile` 若它是新项，否则取新项），清 `_ocrAddPending` 与 `_ocrIdsBeforeAdd`，将该新项选入并打开详情（`_ocrDetailId = added.Id; vm.IsOcrDetailOpen = true;`）。
   - **T4-OCR（U6 保存 / 重载后回写留在详情）**：
     - `ScheduleOcrDetailSelectionCheck()`：当 `vm.IsOcrDetailOpen && _ocrDetailId is not null` 时，置 `_ocrRestorePending = true;` 并 `Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ReconcileOcrDetailSelection)`。
     - `ReconcileOcrDetailSelection()`：若 `!_ocrRestorePending || DataContext is not SettingsViewModel vm || !vm.IsOcrDetailOpen || _ocrDetailId is null` 清 `_ocrRestorePending` 返回；在 `vm.OcrProfiles` 中找 `p.Id == _ocrDetailId` 的 `target`：
       - `target is null`（当前服务已被删除）→ 调 `CloseOcrDetail()` 回列表；
       - `ReferenceEquals(vm.SelectedOcrProfile, target)` → `_ocrRestorePending = false;`；
       - `vm.CanEdit` 为真 → 设 `vm.SelectedOcrProfile = target; OcrProfilesListBox.SelectedItem = target; _ocrRestorePending = !ReferenceEquals(vm.SelectedOcrProfile, target);`；
       - `vm.CanEdit` 为假（如保存进行中）→ 保持 `_ocrRestorePending = true` 等待 `CanEdit` 变回真时再次触发。
2. 在 `src/Iris.App/Settings/SettingsWindow.xaml.cs` 的 `OnViewModelPropertyChanged` 增加两行转调：
   - `e.PropertyName == nameof(SettingsViewModel.SelectedOcrProfile)` 时（在既有 `SyncOcrPasswords` 旁）调 `OnOcrViewModelSelectionChanged(pageVm)`（走 `ScheduleOcrDetailSelectionCheck` 及 `CheckPendingOcrAdd`）。
   - `e.PropertyName == nameof(SettingsViewModel.CanEdit)` 时调 `OnViewModelCanEditChanged(pageVm)`（若 `_ocrRestorePending && pageVm.CanEdit` 则调 `ScheduleOcrDetailSelectionCheck`）。

- [ ] **Step 5：改写 `SettingsWindow.xaml` OCR 页为「列表态 `OcrListPanel` ↔ 详情态 `OcrDetailHost`」**

修改 `src/Iris.App/Settings/SettingsWindow.xaml` 的 `OcrPageScroll` 块（`:180–286`）：

1. `OcrPageScroll` 去掉 `Padding="20,12"`；第一层面板为 `<StackPanel Margin="{StaticResource IrisPagePadding}">`（**不**挂 `CanEdit`）。
2. **`OcrListPanel`（`IsOcrDetailOpen == True` 时 `Collapsed`）**：
   - 页标题 `UiText.OcrPageTitle`（`IrisPageTitle`）+ 页级说明 `UiText.OcrPageDescription`（`IrisRowDescription` + `IrisCardGap`）+ 页级错误 `OcrSelectionError`（`IrisRowError` + `IrisCardGap` + `SystemFillColorCriticalBrush`）。
   - `<StackPanel IsEnabled="{Binding CanEdit}">` 内放 **6 张不带图标的两列行卡片**（`Border Style="{StaticResource IrisRowCard}"` + `Grid` 两列 `* | Auto`，S0-4 line 455）：`OcrPrimaryCombo`（`MinWidth="160"`）、`OcrFallbackCombo`（`MinWidth="160"`）、`OcrLayoutCombo`（`MinWidth="160"`）、`OcrLanguageHintTextBox`（`Width="220"`）、`OcrAutoCopyCheck`（`IrisSwitchCheckBox` + `AutomationProperties.Name`）、`OcrSilentNotifyCheck`（`IrisSwitchCheckBox` + `AutomationProperties.Name`）。
   - 分组小标题 `<TextBlock Style="{StaticResource IrisSectionHeader}" Text="{x:Static app:UiText.OcrServices}" />`。
   - `<StackPanel x:Name="OcrServicesPane">`（内联 `Style`：`DataTrigger Binding="{Binding IsSaving}" Value="True"` → `IsEnabled="False"`）：
     - `WrapPanel Margin="0,0,0,8"` 放添加百度、添加 Windows 两个原生 `Button`（保留原 `Content`、`Command`，加 `IsEnabled="{Binding CanEdit}"`、`PreviewMouseLeftButtonDown="OnAddOcrProfilePreviewMouseDown"`、`Click="OnAddOcrProfileClick"`）；**删掉**旧 `DeleteOcrProfileButton`。
     - `OcrProfilesListBox`（去掉 `MaxHeight="120"`，套 `ItemContainerStyle="{StaticResource IrisListRowItem}"`，挂 `SelectionChanged`、`KeyDown`、`ContextMenuOpening`）：`ItemTemplate` 用两列 `Grid`（`* | Auto`），第 0 列名字区 `Border` 与右侧 `ChevronRight20` 图标按钮均用两条 `DataTrigger`（`Window.DataContext.CanEdit == True` 或 `ListBoxItem.IsSelected == True`）控制 `IsEnabled="True"`（默认 `False`），名字区禁用时文字切 `TextFillColorDisabledBrush`；中间放「⋯」按钮（`MoreHorizontal20`，`IsEnabled` 绑窗口 `DataContext.CanEdit`）。
3. **`OcrDetailHost`（默认 `Collapsed`，`IsOcrDetailOpen == True` 时 `Visible`）**：
   - 面包屑栏：`OcrBreadcrumbBackButton`（`IrisBreadcrumbButton`、`Content="{x:Static app:UiText.OcrPageTitle}"`）+ `ChevronRight16` + `OcrBreadcrumbCurrentText`（`IrisBreadcrumbCurrentText`、`Text="{Binding SelectedOcrProfile.DisplayName.Value}"`）。
   - `<Border Style="{StaticResource IrisCard}">` 包裹 `OcrDetailsPanel`（保留原 `DataContext="{Binding SelectedOcrProfile}"` 与空值折叠样式，加 `IsEnabled="{Binding DataContext.CanEdit, RelativeSource={RelativeSource AncestorType=Window}}"`；内部全部具名字段原样保留，`OcrEnabledCheck`、`OcrDirectionCheck`、`OcrParagraphCheck` 改两列横向开关，`OcrApiKeyPasswordBox`、`OcrSecretKeyPasswordBox` 保持原生 `PasswordBox`）。
   - `<Border Style="{StaticResource IrisCard}">` 测试动作卡（在 `OcrDetailsPanel` 之外、无 `CanEdit`/`IsSaving` 容器包裹）：保留 `UiText.OcrTestDescription`、`OcrTestButton`、`OcrCancelTestButton`、`OcrTestResultText`。

- [ ] **Step 6：更新 `OcrSettingsRenderingTests.cs` 详情态截图前置并跑全套验证**

检查 `tests/Iris.App.Tests/Settings/OcrSettingsRenderingTests.cs`：凡需要渲染详情面板内控件（如 `OcrApiKeyPasswordBox`、`OcrBaseUrlTextBox`）的场景，在切到 `"ocr"` 页后加 `f.Vm.IsOcrDetailOpen = true; f.Window.UpdateLayout();`（列表态截图保持 `IsOcrDetailOpen = false`），并将该处改动记入报告「改过的既有测试」。

跑验证：

```bash
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~OcrDetailNavigationTests|FullyQualifiedName~OcrSettings|FullyQualifiedName~HistorySettingsUiTests|FullyQualifiedName~HistoryRenderingTests|FullyQualifiedName~SettingsViewModelDetailViewTests"
```

期望：全绿。随后跑「设置全集」，确认全绿、0 警告。

- [ ] **Step 7：复看 OCR 列表与详情截图 + 按路径暂存（严禁提交）+ 自检清单**

复看 `OcrSettingsRenderingTests` 产出的浅/深色列表态与详情态截图，确认列表行无拖动把手与行内开关、详情面包屑与测试卡清晰、密码框与百度/Windows 条件面板正常。

```bash
git add src/Iris.App/Settings/SettingsWindow.xaml src/Iris.App/Settings/SettingsWindow.xaml.cs src/Iris.App/Settings/SettingsWindow.DetailNavigation.cs tests/Iris.App.Tests/Settings/OcrDetailNavigationTests.cs tests/Iris.App.Tests/Settings/OcrSettingsRenderingTests.cs
git status --short
```

（若 `OcrSettingsUiTests.cs` 或 `OcrSettingsLifecycleTests.cs` 也有按 U6/U7 调整的用例，一并按路径 `git add` 并逐条登记进报告。）

**验证命令：** `FullyQualifiedName~OcrDetailNavigationTests|FullyQualifiedName~OcrSettings|FullyQualifiedName~HistorySettingsUiTests|FullyQualifiedName~HistoryRenderingTests|FullyQualifiedName~SettingsViewModelDetailViewTests` —— 预期全绿；再跑「设置全集」，预期全绿。

**要改的既有测试：** `Settings/OcrSettingsRenderingTests.cs`（拍详情控件前设 `IsOcrDetailOpen = true` 进入详情，理由：列表与详情不再同屏）；`OcrSettingsUiTests.cs` / `OcrSettingsLifecycleTests.cs` 视实测仅在要求详情控件可见时加进入详情并逐条记入报告。

### Task 12: 「翻译服务」页：列表与详情

**目的：** 按 S0-8、S0-9 把「翻译服务」页从左右分栏改成「列表 → 详情」，并删掉旧的窄窗堆叠布局。本阶段改动最大、风险最高的一个任务，所以排在所有页面的最后。

**Files:**
- Create: `tests/Iris.App.Tests/Settings/TranslationDetailNavigationTests.cs`
- Create: `tests/Iris.App.Tests/Settings/TranslationDetailResetTests.cs`
- Create: `tests/Iris.App.Tests/Settings/TranslationListFreezeTests.cs`
- Create: `tests/Iris.App.Tests/Settings/SettingsDetailEscapeTests.cs`
- Modify: `src/Iris.App/Settings/SettingsWindow.xaml`——「翻译服务」页 :443–862（行列定义 :458–463、`ProfilesPane` :467–545、工具条 :478–507、`ProfilesListBox` :511–544、`DetailsPane` :548–861）
- Modify: `src/Iris.App/Settings/SettingsWindow.xaml.cs`——`ApplyAdaptiveLayout` cs:55–99（删旧分栏部分，只留任务 4 加的导航部分）；添加服务的 lambda cs:249–278（T3）；起拖 cs:363–397（只从把手起）；`OnViewModelPropertyChanged`（加 `SelectedProfile` 变更 → T4 的转调）
- Modify: `src/Iris.App/Settings/SettingsWindow.DetailNavigation.cs`（T1–T4 的翻译服务版、行菜单构造、列表 Enter 处理、`ContextMenuOpening`）
- Test（要改）: `tests/Iris.App.Tests/Settings/SettingsWindowTranslationUiTests.cs`（用例 7，252–446）；视情况 `SettingsDraftAndSaveRegressionTests.cs`（Scenario8，:1792–1808）

**范围与要点：**
- 列表态：`AddProfileButton` 保留（`ui:Button` 加代码里构造的 `ContextMenu`，英文下宽度 > 70；自己绑 `IsEnabled="{Binding CanEdit}"`）；三个工具条按钮删除；`ProfilesListBox` 保留名字、类型、`SelectedItem` 双向绑定（:514）与拖放相关的事件处理；`ItemContainerStyle` 用 `IrisListRowItem`；行模板照 S0-9（把手、启用开关、名字加模型、「⋯」、右箭头）。
- **冻结与只读（【用户】U7）**：`ProfilesPane` 上的 `IsEnabled="{Binding CanEdit}"`（:467 起）删除，改成 `IsSaving` 的 `DataTrigger`；行内启用开关、「⋯」、把手各自绑窗口 `DataContext.CanEdit`；名字区、右箭头不单独绑；T1 开头加「保存中不进入」的守卫；`ContextMenuOpening` 与起拖各加 `CanEdit` 守卫。完整归属照 S0-8「冻结与只读」的表与 S0-9。只读时**任意行**都能进详情看、不能改（本页不受 C4 的限制）；保存中整个列表不可用。
- 详情态：`TranslationDetailHost` = 面包屑 + `DetailsPane`；四个分组照 S0-4（卡 1 :567–667、卡 2 :670–697、卡 3 :700–746 各一张 `IrisCard`，分组名在卡片上方；`AdvancedOptionsExpander` :749–859 仍是原生 `Expander`、默认收起）。四个复选框（:764 / :767 / :770、:854–856）改开关。
- `DetailsPane` 内层面板的 `DataContext="{Binding SelectedProfile}"`、`IsEnabled` 绑定（窗口 `DataContext.CanEdit`，只读时字段不可改）、选中为空时折叠（:554–564）原样保留。
- 保持：`TranslationLayoutGrid`、`ProfilesPane`、`DetailsPane`（`ScrollViewer`）、`DisplayNameTextBox`（`LostFocus` 更新）、`ApiKeyPasswordBox`（原生 `PasswordBox`，`FindName` 取得到，cs:127）、`AdvancedOptionsExpander`（`Expander`）——全部**始终在树里、不进模板**。`OnApiKeyPasswordChanged`、`SyncPasswordBoxWithDraft`、`OnProfileSelectionChanged`、拖放的 `DragOver` / `Drop` 不动。
- 翻转规则 T1–T4、各情形的结果照 S0-8 的两张表，逐条有测试。
- **不改** `SettingsViewModel` 的任何既有方法；菜单项只绑既有命令。
- 用例 1–6 不许改、必须绿。
- 2.13 第 3 条（折叠控件上的焦点与刷新）、第 4 条（Tab 能否进行内）、第 14 条（重载时选中项是否短暂为空）开工先核实。第 12 条与 `IsSaving` 的可绑定性已核实（2.7「已核实」），不用再查。
- 既有测试里凡是断言「`CanEdit` 为假时 `ProfilesPane` 或 `ProfilesListBox` 的 `IsEnabled` 为假」的，会因 U7 变红：执行时先 grep `tests/Iris.App.Tests/Settings` 里对这两个名字的 `IsEnabled` 断言，列出清单，逐条改成 S0-8 的新口径并记入「改过的既有测试」。
- 风险：R1、R2、R4、R5、R8、R9、R13。

**步骤提纲：**
- [ ] 核实上面三条未确认项（2.13 第 3、4、14 条），结果写回 2.13
- [ ] 写四个新测试类（内容见第 4.1 节各自的职责；「保存期间」的场景照 `SettingsDraftAndSaveRegressionTests` 里既有的保存中写法搭）。其中 `TranslationListFreezeTests` 分两组断言：
  - **保存中冻结**：`ProfilesPane.IsEnabled` 为假；名字区、右箭头、行上 Enter 都进不了详情（`IsTranslationDetailOpen` 保持假）；开关、「⋯」、把手、添加不可用；已在详情态时面包屑可用、能返回
  - **只读时行内控件禁用但可进入**（`CanEdit` 为假、未在保存）：`ProfilesPane.IsEnabled` 为真；行内开关、「⋯」、把手与 `AddProfileButton` 的 `IsEnabled` 为假；Shift+F10 不打开菜单；从把手起拖不发生；**任意行**（含非选中行）经三个入口各自都能进入详情；详情字段容器 `IsEnabled` 为假、`DetailsPane` 可见；面包屑能返回
- [ ] 重写用例 7：块 0、块 5 保留；块 1 换成导航宽度与列表 / 详情可见性断言；块 2 在列表态断言「添加服务」与行内按钮；块 3、4、6 先进入详情再断言；「上移 / 下移 / 删除」改经「⋯」菜单断言
- [ ] 跑，确认新测试与用例 7 失败、用例 1–6 仍绿
- [ ] 改 XAML（从 `DetailsPane` 末尾往前）；改代码隐藏三处；补 partial
- [ ] 跑本任务过滤组；原样跑 Scenario8，按 4.2 的规则决定改不改
- [ ] 跑「设置全集」；`git add`

**详细步骤：**

> **声明：** 代码与命令尚未运行过；行号以 `dc08c48` 为参考，开工 Step 1 先 `grep` 重取行号。

#### 详细步骤

- [ ] **Step 1：开工核实——重取翻译服务页与代码隐藏行号、中文注释及测试 `IsEnabled` 断言**

```bash
git status --short -- src/Iris.App/Settings/ tests/Iris.App.Tests/Settings/
grep -n 'x:Name="TranslationLayoutGrid"\|x:Name="ProfilesPane"\|x:Name="ProfilesListBox"\|x:Name="DetailsPane"\|x:Name="AdvancedOptionsExpander"\|<!-- 右侧划词设置内容 -->' src/Iris.App/Settings/SettingsWindow.xaml
grep -n 'ApplyAdaptiveLayout\|OnAddProfileButtonClick\|OnProfilesListPreviewMouseLeftButtonDown\|OnKeyDown' src/Iris.App/Settings/SettingsWindow.xaml.cs
grep -n 'ProfilesPane\|ProfilesListBox' tests/Iris.App.Tests/Settings/*.cs
```

同时记录翻译服务页（`<!-- 右侧翻译服务设置内容 -->` 到 `<!-- 右侧划词设置内容 -->` 之前）内的全部既有中文注释行与原文——凡所属区块仍在（如页注释、服务列表、选定服务详情、基本配置、凭据、生成参数、高级选项、OpenAI/Anthropic 兼容项等）原样保留，仅随被删掉的旧三按钮工具栏整行移除的注释才整行删除（严禁改半句中文注释）。

- [ ] **Step 2：编写四个新测试文件并分块重写 `SettingsWindowTranslationUiTests` 用例 7（TDD 红灯）**

1. **新建 `tests/Iris.App.Tests/Settings/TranslationDetailNavigationTests.cs`**：
   - 默认打开 `"translation"` 为列表态（`IsTranslationDetailOpen == false`，`ProfilesPane` 可见、`TranslationDetailHost` 折叠，`FindName("DisplayNameTextBox")` 与 `FindName("ApiKeyPasswordBox")` 仍非空）。
   - 经「点行名字+模型区」、「点行右箭头 (`ChevronRight20`)」、「选中行按 `Enter`」三入口均可进入详情（`IsTranslationDetailOpen == true`、`TranslationDetailId == profile.Id`、`TranslationBreadcrumbBackButton` 与 `TranslationBreadcrumbCurrentText` 正确、`DetailsPane` 可见、面包屑头固定在 `DetailsPane` 滚动区上方）。
   - 点行内启用开关、点「⋯」按钮、从拖动把手（`Tag == "DragHandle"`）拖拽重排均**不**进入详情；从行名字区拖动**不**触发重排（只从把手起拖，S0-9）。
   - 行内「⋯」菜单含上移、下移、删除三项且绑定原命令，功能与首尾项禁用规则照旧。
   - 从 `AddProfileButton` 预设菜单新增服务后自动进入新服务详情（T3）。
   - 点 `TranslationBreadcrumbBackButton` 返回列表且 `SelectedProfile` 不变（T2）。
2. **新建 `tests/Iris.App.Tests/Settings/TranslationDetailResetTests.cs`**：
   - 详情态切到其他导航页再切回 `"translation"` → 回到列表态（T5）。
   - 详情态下对同一页调 `ShowAsync("translation")` 或再点当前导航项 → 留在详情态。
   - 在详情态删除当前服务（或外部重载移除了该 `Id`）→ 自动退回列表态（T4）；保存未删除的当前服务 → 留在原服务详情态。
3. **新建 `tests/Iris.App.Tests/Settings/TranslationListFreezeTests.cs`（U7）**：
   - **保存中冻结（`IsSaving == true`）**：`ProfilesPane.IsEnabled == false`，名字区、右箭头、行上 `Enter` 均不进详情，行内开关、「⋯」、把手、`AddProfileButton` 不可用；若保存前已在详情态，`TranslationBreadcrumbBackButton` 仍可用并可返回列表。
   - **只读模式（`CanEdit == false && !IsSaving`）**：`ProfilesPane.IsEnabled == true`；`AddProfileButton`、行内启用开关、「⋯」按钮、拖动把手 `IsEnabled == false`，`Shift+F10` 不开菜单，把手拖不动；**任意行（含非当前选中行）**经名字区、右箭头、`Enter` 三入口均可进入详情，进入后详情字段容器 `IsEnabled == false`、`DetailsPane` 可见，面包屑可正常返回。
4. **新建 `tests/Iris.App.Tests/Settings/SettingsDetailEscapeTests.cs`**：
   - 在翻译服务详情态与 OCR 详情态下按 `Escape`：未保存时直接放弃草稿并关闭整个设置窗口（**不**是仅退回列表，设计 :103 / W01）；快捷键录制中按 `Escape` 只取消录制不关窗；保存进行中按 `Escape` 不强关窗口。
5. **分块重写 `tests/Iris.App.Tests/Settings/SettingsWindowTranslationUiTests.cs` 用例 7**（用例 1–6 一字不改！）：
   - 保留窗口尺寸与主题准备块及底栏按钮断言块；
   - 将旧分栏四行列定义断言替换为：导航列宽度（`>= 680` 为 `176`，`< 680` 为 `48`）及列表态 `ProfilesPane` 可见 / `TranslationDetailHost` 折叠断言；
   - 在列表态断言 `AddProfileButton` 可见且宽 `> 70`、列表每行含把手 / 开关 / 名字模型 / 「⋯」/ 右箭头且不越出内容区，验证「⋯」菜单含上移/下移/删除三项；
   - 触发进入详情后断言面包屑头固定在上方、`DetailsPane` 可见，基本配置 / 连接与凭据 / 生成参数三张 `IrisCard` 与 `AdvancedOptionsExpander` 均不横向裁切。

- [ ] **Step 3：跑新增测试与 `SettingsWindowTranslationUiTests` 确认红灯（且用例 1–6 保持绿）**

```bash
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~TranslationDetail|FullyQualifiedName~TranslationListFreezeTests|FullyQualifiedName~SettingsDetailEscapeTests|FullyQualifiedName~SettingsWindowTranslationUiTests"
```

期望：四个新测试类与用例 7 失败，用例 1–6 全绿。

- [ ] **Step 4：扩充 `SettingsWindow.DetailNavigation.cs` 与 `SettingsWindow.xaml.cs`（翻译服务钻取、把手起拖、删旧分栏）**

1. **`src/Iris.App/Settings/SettingsWindow.DetailNavigation.cs`**（英文注释、零中文字面量）：
   - **T1（`TryOpenTranslationDetail(TranslationProfileDraftViewModel? profile)`）**：`if (profile is null || DataContext is not SettingsViewModel vm || vm.IsSaving || _isDragging) return;` 设 `ProfilesListBox.SelectedItem = profile;` 核实 `ReferenceEquals(vm.SelectedProfile, profile)`；设 `_translationDetailId = profile.Id; vm.IsTranslationDetailOpen = true;`，`Loaded` 优先级聚焦 `TranslationBreadcrumbBackButton`。
   - **T2（`CloseTranslationDetail()`）**：设 `vm.IsTranslationDetailOpen = false; _translationDetailId = null;`，`Loaded` 优先级聚焦 `ProfilesListBox` 当前选中项容器。
   - **T3（`OnTranslationProfileAddedFromPreset(SettingsViewModel vm, string? previousId)`）**：若 `vm.SelectedProfile is { } added && !string.Equals(added.Id, previousId, StringComparison.Ordinal)`，设 `_translationDetailId = added.Id; vm.IsTranslationDetailOpen = true;` 并聚焦面包屑返回按钮。
   - **T4（`OnTranslationViewModelSelectionChanged(SettingsViewModel vm)`）**：当 `vm.IsTranslationDetailOpen && _translationDetailId is not null` 时，延后一拍到 `Loaded` 复查：若 `vm.SelectedProfile is null` 或 `vm.Profiles.All(p => p.Id != _translationDetailId)`，调 `CloseTranslationDetail()` 回列表；若 `vm.SelectedProfile.Id != _translationDetailId` 但原项仍在 `vm.Profiles` 中（如重载重建引用），写回该新引用或同步更新。
   - **行入口、Enter 与「⋯」菜单 / `ContextMenuOpening`**：
     - `OnTranslationRowEntryMouseLeftButtonUp` / `OnTranslationRowOpenDetailsClick` → `TryOpenTranslationDetail`。
     - `OnTranslationProfilesListKeyDown`：仅当 `e.Key == Key.Enter && e.OriginalSource is ListBoxItem { DataContext: TranslationProfileDraftViewModel profile }` 时 `e.Handled = true; TryOpenTranslationDetail(profile);`（开关与图标按钮上的 Enter 不拦截）。
     - `OnTranslationRowMoreActionsClick` 与 `OnTranslationProfilesListContextMenuOpening`：`if (DataContext is not SettingsViewModel { CanEdit: true } vm || vm.IsSaving) { e.Handled = true; return; }`；先选中目标行并核实，构造三项 `ContextMenu`（`UiText.SettingsMoveUp` → `vm.MoveUpProfileCommand`、`UiText.SettingsMoveDown` → `vm.MoveDownProfileCommand`、`UiText.SettingsDeleteProfile` → `vm.DeleteProfileCommand`）。
2. **`src/Iris.App/Settings/SettingsWindow.xaml.cs` 四处最小改动**：
   - `ApplyAdaptiveLayout(double width)`：删掉对 `TranslationLayoutGrid`、`ProfilesColDef`、`DetailsColDef`、`ProfilesRowDef`、`DetailsRowDef`、`ProfilesPane`、`DetailsPane` 的分栏切换代码，只保留 Task 4 加的左侧导航列 `680` 宽窄自适应逻辑（保留 `internal void ApplyAdaptiveLayout(double width)` 签名）。
   - `OnAddProfileButtonClick`（预设菜单项点击 lambda）：调用 `vm.AddProfileFromPreset(p)` 前记 `string? prevId = vm.SelectedProfile?.Id;`，调用后加一行 `OnTranslationProfileAddedFromPreset(vm, prevId);`。
   - 拖拽起手限制在把手（`OnProfilesListPreviewMouseLeftButtonDown` / `OnProfilesListMouseMove` / `OnProfilesListDrop`）：加私有字段 `private bool _dragStartedOnHandle;`，仅当 `DataContext is SettingsViewModel { CanEdit: true, IsSaving: false }` 且从 `e.OriginalSource as DependencyObject` 沿可视树向上能找到 `Border { Tag: "DragHandle" }` 时才置 `_dragStartedOnHandle = true` 并记 `_dragStartPoint`；`OnProfilesListMouseMove` 开头 `if (!_dragStartedOnHandle) return;`，松手或拖放结束清零 `_dragStartedOnHandle = false`。
   - `OnViewModelPropertyChanged`：在既有 `SelectedProfile` 分支内加一行 `OnTranslationViewModelSelectionChanged(pageVm);`（`OnKeyDown` 一行不改！）。

- [ ] **Step 5：改写 `SettingsWindow.xaml`「翻译服务」页为列表态与详情态**

修改 `src/Iris.App/Settings/SettingsWindow.xaml` 的 `TranslationLayoutGrid` 块（`:443–862`，妥善保留所属区域仍在的中文注释）：

1. **`TranslationLayoutGrid`**：删掉 `ProfilesColDef`、`DetailsColDef`、`ProfilesRowDef`、`DetailsRowDef` 四个行列定义及本地 `Margin="12,12,12,12"`，保留 `Grid x:Name="TranslationLayoutGrid" Grid.Column="1"` 与 `CurrentPageKey == "translation"` 的可见性 `Style`。
2. **列表态 `ScrollViewer`（`IsTranslationDetailOpen == True` 时 `Collapsed`）**：
   - 内放 `<Grid x:Name="ProfilesPane" Margin="{StaticResource IrisPagePadding}">`（三行 `Auto / Auto / Auto`；内联 `Style` 在 `IsSaving == True` 时设 `IsEnabled="False"`，**去掉**旧的 `IsEnabled="{Binding CanEdit}"`）：
     - 第 0 行：`<TextBlock Style="{StaticResource IrisPageTitle}" Text="{x:Static app:UiText.SettingsNavTranslation}" />`。
     - 第 1 行：`<ui:Button x:Name="AddProfileButton" Content="{x:Static app:UiText.SettingsAddProfile}" IsEnabled="{Binding CanEdit}" Click="OnAddProfileButtonClick" Margin="0,0,0,12" />`（**删掉** `MoveUpProfileButton`、`MoveDownProfileButton`、`DeleteProfileButton`）。
     - 第 2 行：`ProfilesListBox`（保留原 `ItemsSource`、`SelectedItem`、`AllowDrop` 与拖拽事件，加 `ItemContainerStyle="{StaticResource IrisListRowItem}"`、`KeyDown="OnTranslationProfilesListKeyDown"`、`ContextMenuOpening="OnTranslationProfilesListContextMenuOpening"`）：`ItemTemplate` 为五列 `Grid`（`Auto | Auto | * | Auto | Auto`），依次放：
       - 列 0：拖动把手 `Border Style="{StaticResource IrisDragHandle}" Tag="DragHandle" ToolTip="{x:Static app:UiText.SettingsRowDragToReorder}" AutomationProperties.Name="{x:Static app:UiText.SettingsRowDragToReorder}" IsEnabled="{Binding DataContext.CanEdit, RelativeSource={RelativeSource AncestorType=Window}}"` + `ReOrderDotsVertical20`；
       - 列 1：启用开关 `CheckBox Style="{StaticResource IrisSwitchCheckBox}" IsChecked="{Binding Enabled, Mode=TwoWay}" IsEnabled="{Binding DataContext.CanEdit, RelativeSource={RelativeSource AncestorType=Window}}" AutomationProperties.Name="{x:Static app:UiText.SettingsRowEnabled}"`；
       - 列 2：名字+模型点击区 `Border Background="Transparent" Cursor="Hand" MouseLeftButtonUp="OnTranslationRowEntryMouseLeftButtonUp"`（`DisplayName` 套 `IrisRowTitle`、`Model` 套 `IrisRowDescription`，均单行省略号；**不**绑 `CanEdit`）；
       - 列 3：「⋯」按钮 `ui:Button Style="{StaticResource IrisIconButton}" Content="{x:Static app:UiText.SettingsRowMoreActions}" ToolTip="{x:Static app:UiText.SettingsRowMoreActions}" IsEnabled="{Binding DataContext.CanEdit, RelativeSource={RelativeSource AncestorType=Window}}" Click="OnTranslationRowMoreActionsClick"` + `MoreHorizontal20`；
       - 列 4：右箭头按钮 `ui:Button Style="{StaticResource IrisIconButton}" Content="{x:Static app:UiText.SettingsRowOpenDetails}" ToolTip="{x:Static app:UiText.SettingsRowOpenDetails}" Click="OnTranslationRowOpenDetailsClick"` + `ChevronRight20`（**不**绑 `CanEdit`）。
3. **详情态 `Grid x:Name="TranslationDetailHost"`（两行 `Auto | *`；默认 `Collapsed`，`IsTranslationDetailOpen == True` 时 `Visible`）**：
   - 第 0 行（固定面包屑头，`Margin="24,16,24,8"`，不在任何禁用容器内）：`TranslationBreadcrumbBackButton`（`IrisBreadcrumbButton`，`Content="{x:Static app:UiText.SettingsNavTranslation}"`）+ `ChevronRight16` + `TranslationBreadcrumbCurrentText`（`IrisBreadcrumbCurrentText`，`Text="{Binding SelectedProfile.DisplayName}"`）。
   - 第 1 行：`ScrollViewer x:Name="DetailsPane" Grid.Row="1" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled"`，内部 `<StackPanel Margin="24,4,24,16" DataContext="{Binding SelectedProfile}" IsEnabled="{Binding DataContext.CanEdit, RelativeSource={RelativeSource AncestorType=Window}}">`（保留为空时 `Collapsed` 的 `Style`）：
     - 前三张 `ui:Card` 各自改为「卡片上方 `<TextBlock Style="{StaticResource IrisSectionHeader}" Text="…原卡片首行标题…" />` + `<Border Style="{StaticResource IrisCard}">`」；第四组保留原生 `Expander x:Name="AdvancedOptionsExpander"`（默认收起）。
     - 组内 4 个 `CheckBox` 全部改为两列横向开关（左标题 `IrisRowTitle`，右 `IrisSwitchCheckBox` + `AutomationProperties.Name`，去掉 `WrappingCheckBoxContent`）；其余所有具名控件（`DisplayNameTextBox`、`ApiKeyPasswordBox` 等）名字、类型、绑定与事件一字不动。

- [ ] **Step 6：跑本任务过滤组、核查 `Scenario8` 并跑「设置全集」**

```bash
dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~TranslationDetail|FullyQualifiedName~TranslationListFreezeTests|FullyQualifiedName~SettingsDetailEscapeTests|FullyQualifiedName~SettingsWindowTranslationUiTests|FullyQualifiedName~SettingsDraftAndSaveRegressionTests|FullyQualifiedName~LocalizationTests|FullyQualifiedName~SettingsShutdownWithPendingOperationsTests|FullyQualifiedName~SettingsViewModelTests|FullyQualifiedName~SettingsWindowNavigationLayoutTests"
```

- 若 `SettingsDraftAndSaveRegressionTests` 的 `Scenario8` 因列表态下 `DisplayNameTextBox` 折叠导致 `Focus()` 未生效而变红：在 `Scenario8` 设焦点前加 `window.DataContext` 对应 `vm.IsTranslationDetailOpen = true; window.UpdateLayout();` 进入详情，同时记入报告「改过的既有测试」并说明与 `SettingsWindowTranslationUiTests` 用例 6 的差异；若原样全绿则不改。
- 随后跑「设置全集」确认全绿、0 警告。

- [ ] **Step 7：按路径暂存（严禁提交）+ 自检清单**

```bash
git add src/Iris.App/Settings/SettingsWindow.xaml src/Iris.App/Settings/SettingsWindow.xaml.cs src/Iris.App/Settings/SettingsWindow.DetailNavigation.cs tests/Iris.App.Tests/Settings/TranslationDetailNavigationTests.cs tests/Iris.App.Tests/Settings/TranslationDetailResetTests.cs tests/Iris.App.Tests/Settings/TranslationListFreezeTests.cs tests/Iris.App.Tests/Settings/SettingsDetailEscapeTests.cs tests/Iris.App.Tests/Settings/SettingsWindowTranslationUiTests.cs
git status --short
```

（若 `SettingsDraftAndSaveRegressionTests.cs` 有修改，一并按路径 `git add`。）

自检：全文件 `SettingsWindow.xaml` 内 `ui:Card` 已降为 `0`、`WrappingCheckBoxContent` 的使用处已降为 `0`（资源定义留到 Task 13 删）；用例 1–6 未改且全绿；`git log -1 --oneline` 仍为 `dc08c48`。

**验证命令：** `FullyQualifiedName~TranslationDetail|FullyQualifiedName~TranslationListFreezeTests|FullyQualifiedName~SettingsDetailEscapeTests|FullyQualifiedName~SettingsWindowTranslationUiTests|FullyQualifiedName~SettingsDraftAndSaveRegressionTests|FullyQualifiedName~LocalizationTests|FullyQualifiedName~SettingsShutdownWithPendingOperationsTests|FullyQualifiedName~SettingsViewModelTests|FullyQualifiedName~SettingsWindowNavigationLayoutTests` —— 预期全绿；再跑「设置全集」，预期全绿。

**要改的既有测试：** `Settings/SettingsWindowTranslationUiTests.cs` 用例 7 `Layout_AtSpecifiedSizeAndTheme_AllControlsAreUsableAndNotClipped` 分块重写（设计 §8.1 要求）；`Settings/SettingsDraftAndSaveRegressionTests.cs` Scenario8 视焦点实测仅在折叠态拿不到焦点时补一步进入详情并记入清单。

### Task 13: 清理与第一次全量回归

**目的：** 删掉改造后不再有使用者的东西，并第一次跑零警告构建、全量测试、本地化审计，把问题暴露在收尾之前。

**Files:**
- Modify: `src/Iris.App/Settings/SettingsWindow.xaml`——窗口资源 :16–19（删 `WrappingCheckBoxContent`）
- Modify: `src/Iris.App/Settings/SettingsWindow.xaml.cs`——删掉已无引用的私有成员（如有）
- Modify（有条件）: `build/localization/exceptions.json`

**范围与要点：**
- 逐项 grep 确认后再删，查不到 0 处使用就不删：
  - `WrappingCheckBoxContent`：12 处使用应已全部消失 → 删资源定义（R14）。`BoolToVis` **保留**。
  - `ui:Card`：`SettingsWindow.xaml` 里应为 0 处。
  - `ProfilesColDef` / `DetailsColDef` / `ProfilesRowDef` / `DetailsRowDef`、`MoveUpProfileButton` / `MoveDownProfileButton` / `DeleteProfileButton` / `DeleteOcrProfileButton`：`src/` 与 `tests/` 里应为 0 处。
  - 代码隐藏里因此不再被引用的私有字段 / 方法：删。`internal` 成员（`ApplyAdaptiveLayout`、`ForceClose`、`FlushFocusedBinding`）一个不删。
- 中文检查：对暂存区的差异做一次检查——`src/` 下**新增的行**里不得有任何中文字符（资源文件 `Strings.resx` 的四条新值除外）；被删除的中文注释行必须是整行删除，没有「改了几个字」的行。
- 本地化审计若因「已删除的中文注释留下过期登记」报错（2.13 第 16 条）：只删 `exceptions.json` 里对应的过期条目，不新增登记；删了哪几条写进完成报告。审计没为此报错就不动这个文件。
- 检查 `Iris.Styles.xaml:260` 的注释、`Iris.SolidFallback.xaml` 的注释已经与现状一致。
- 不做：不顺手重构、不改名、不调整与本阶段无关的代码。

**步骤提纲：**
- [ ] 逐项 grep，列出实际使用处数
- [ ] 删除确认为零使用的定义与成员
- [ ] `dotnet build Iris.slnx -c Release`，确认 0 警告 0 错误
- [ ] `dotnet test Iris.slnx -c Release`，确认全绿；红了回到对应任务的范围内修
- [ ] `& build/audit-localization.ps1 -EvidenceDirectory artifacts/localization-audit`，确认通过；按上面的规则处理过期登记
- [ ] 做中文检查；`git add`

**详细步骤：**

> **声明：** 代码与命令尚未运行过；执行时每删一项先 `grep` 确认为 0 引用。

#### 详细步骤

- [ ] **Step 1：全仓清点无引用遗留项（先查后删，非 0 不删）**

在仓库根目录执行：

```bash
grep -rn "WrappingCheckBoxContent" src/ tests/
grep -rn "ui:Card" src/Iris.App/Settings/SettingsWindow.xaml
grep -rn "ProfilesColDef\|DetailsColDef\|ProfilesRowDef\|DetailsRowDef\|MoveUpProfileButton\|MoveDownProfileButton\|DeleteProfileButton\|DeleteOcrProfileButton" src/ tests/
```

期望结果：
1. `WrappingCheckBoxContent`：在 `src/Iris.App/Settings/SettingsWindow.xaml` 只剩窗口顶部的 `<DataTemplate x:Key="WrappingCheckBoxContent">` 定义行（使用处 0）；注意 `src/Iris.App/History/HistoryWindow.xaml` 与 `src/Iris.App/Themes/Iris.Styles.xaml` 仍有各自的独立定义/使用，**严禁碰 `HistoryWindow.xaml` 与 `Iris.Styles.xaml` 里的 `WrappingCheckBoxContent`**，只删 `SettingsWindow.xaml` 局部的 `Window.Resources` 定义（保留旁边的 `BoolToVis`！）。
2. `SettingsWindow.xaml` 内 `ui:Card` 为 **0 处**。
3. 四个旧行列定义与四个旧工具栏按钮在 `src/` 与 `tests/` 全部为 **0 处**。

- [ ] **Step 2：删除 `SettingsWindow.xaml` 局部废弃资源与 `SettingsWindow.xaml.cs` 无引用私有成员**

1. 在 `src/Iris.App/Settings/SettingsWindow.xaml` 的 `<ui:FluentWindow.Resources>` 内删掉 `<DataTemplate x:Key="WrappingCheckBoxContent">…</DataTemplate>`（**必须保留** `<BooleanToVisibilityConverter x:Key="BoolToVis" />`）。
2. 检查 `src/Iris.App/Settings/SettingsWindow.xaml.cs` 的全部 `private` 字段与 `private` 方法：若有任何因旧分栏移除而彻底零引用的 `private` 成员则删掉；**所有 `internal` 成员（`ApplyAdaptiveLayout`、`ForceClose`、`FlushFocusedBinding`）一个不删**。
3. 核对 `src/Iris.App/Themes/Iris.Styles.xaml`（`IrisNavItem` 附近注释）与 `src/Iris.App/Themes/Iris.SolidFallback.xaml` 顶部英文注释与阶段 2 现状一致。

- [ ] **Step 3：零警告 Release 全量构建**

```bash
dotnet build Iris.slnx -c Release
```

期望：`0 个警告`、`0 个错误`（`TreatWarningsAsErrors=true` 全程生效）。若有未引用字段/方法警告或 XAML 绑定错误，回到对应文件修正。

- [ ] **Step 4：第一次全解决方案 Release 测试回归**

```bash
dotnet test Iris.slnx -c Release --no-build
```

期望：全部 7 个测试程序集通过，**0 失败**（注：若 `Iris.Platform.Windows.Tests` 的桌面隔离用例 `SnapshotHelper_UsesExclusiveNonInteractiveStation_AndJoinsOwnedProcess` / `IsolatedWindowStation_NativeClipboardIntegration_ExecutesInDedicatedProcess` 如阶段 1 一样自报 `[ENVIRONMENT LIMITATION] Window station creation failed` 而标记为「已跳过」，属宿主窗口站权限限制而非失败；除此之外不得有任何失败）。
- 若出现分组回归未暴露的失败（头号嫌疑：某条渲染测试切了 Iris 主题字典未在 `finally` 还原，见 2.9(e)），定位具体污染源并在对应夹具补 `try/finally` 还原，重跑全量直至 0 失败。

- [ ] **Step 5：本地化审计与源码中文零新增/零篡改校验**

1. 在 Windows PowerShell 5.1 运行本地化审计：
   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/audit-localization.ps1 -EvidenceDirectory artifacts/localization-audit
   ```
   期望：退出码 `0`，`unapproved` 为 `0`。
   - 注（`build/LocalizationAudit/Program.cs` 与 `audit.py` 已核实）：整行删除既有中文注释不会使 `audit.py` 报错；仅当某条被删注释在 `build/localization/exceptions.json` 被脚本报为过期项时，才删除对应过期条目并记入完成报告，未报错则**不动** `exceptions.json`。
2. 校验相对 `dc08c48` 的新增行不含任何计划外中文字符（除 `Strings.resx` 四条新文字外，`src/` 下所有新增行必须 0 汉字，且被改动的既有中文注释只允许整行随废弃块删除、不允许改字或挪位）：
   ```bash
   python -c "
   import subprocess, re
   han = re.compile(r'[\u3400-\u9fff]')
   diff = subprocess.check_output(['git', 'diff', 'dc08c48', '--', 'src/'], text=True, encoding='utf-8')
   cur = None; bad = []
   for line in diff.splitlines():
       if line.startswith('+++ b/'): cur = line[6:]
       elif line.startswith('+') and not line.startswith('+++'):
           if cur != 'src/Iris.App/Resources/Strings.resx' and han.search(line):
               bad.append((cur, line))
   print('Unexpected Chinese additions in src/:', len(bad))
   for item in bad: print(' ', item)
   assert not bad
   "
   ```
   期望：输出 `Unexpected Chinese additions in src/: 0`。若有命中（例如缩进变动导致某条保留的中文注释出现在 `+` 行），检查确认该注释与对应 `-` 行除缩进外逐字一致，且未触发本地化审计 `siteFingerprint` 失效（审计已绿即证明指纹合法；能不改缩进就保持原缩进，使中文注释连 diff 都不上！）。

- [ ] **Step 6：清理临时审计产物并按路径暂存（严禁提交）**

删掉未入库的 `artifacts/localization-audit`（或确认其不在暂存区），按路径暂存本任务改动：

```bash
rm -rf artifacts/localization-audit
git add src/Iris.App/Settings/SettingsWindow.xaml src/Iris.App/Settings/SettingsWindow.xaml.cs
git status --short
```

（若按 Step 5 规则删了 `build/localization/exceptions.json` 的过期项，一并 `git add build/localization/exceptions.json`。）确认 `git log -1 --oneline` 仍为 `dc08c48`。

**验证命令：** `dotnet build Iris.slnx -c Release`（预期 0 警告 0 错误）；`dotnet test Iris.slnx -c Release --no-build`（预期 0 失败）；`powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/audit-localization.ps1 -EvidenceDirectory artifacts/localization-audit`（预期退出码 0）。

**要改的既有测试：** 无（若全量回归发现主题字典状态外漏，在对应夹具补 `finally` 还原并记入报告清单）。

### Task 14: 阶段收尾验证与提交

**目的：** 取得写进完成报告的全部实测数字与证据，写完成报告，经用户确认后做本阶段唯一的一次本地提交。

**Files:**
- Modify: `docs/superpowers/plans/2026-11-15-acrylic-ui-phase-2-settings-window.md`（在文末追加完成报告；阶段 1 的先例是报告写在计划文件里、计划文件随阶段提交一起入库）
- 证据放仓库**之外**：`%TEMP%\iris-t041-evidence\…`

**范围与要点：**
- 报告里每个数字都必须是本任务里实际跑出来的命令输出，原样粘贴；没验证的写「未验证」。
- 真机运行只用便携副本：把 Release 输出复制到 `%TEMP%\iris-t041-real-<guid>`，exe 旁放 `portable.txt`；绝不运行 `src/Iris.App/bin` 下的 exe；进程名 `Iris.App`；用 `--action Exit` 收尾，用 `OpenSettings` 等命令开窗，主题下拉框经 UI Automation 操作（A8）。跑完对比 `%APPDATA%\Iris` 下的文件与 `HKCU\…\Run`，确认没被动过。
- 截图清单由本任务撰写者按 A7.2 的建议展开，至少覆盖：八个页面加两个详情页；浅色 / 深色；中文 / 英文；默认尺寸 760×520 与最小尺寸 600×440；外加 680 宽（内容区最窄）、浅色窗口压在深色桌面上的一组、窗口失去焦点的一张。
- **翻译窗口外包着色层之后的四个看点**（裁决 C9；Task 4B Step 10(c) 说明了离屏渲染为什么验证不了）：① 四角有没有着色溢出到圆角之外；② 原 `Margin` 那一圈是否与内部同色、没有未着色的边；③ 按住头部拖动、四边与四角缩放是否如常；④ 设置、历史、翻译三个窗口并排时底色是否一致。逐项在完成报告里写「已看」或「未验证」。
- **三个亚克力窗口都要看着色**（【用户】U5，Task 4B）：设置窗口之外，历史窗口与翻译窗口各取浅色、深色两张，并各取一张「浅色窗口压在深色桌面上」的；三个窗口的底色并排对照，记录是否一致。只看着色层，不评这两个窗口的版面（版面属阶段 3、4）。
- 钻取的行为截图（【用户】U6、U7、U8）：OCR 页保存后仍在详情的一张；新增 OCR 服务后自动进入详情的一张；只读模式下进入详情（字段灰掉、可查看）的翻译服务页一张（进的是非选中行）；OCR 页两张——当前选中项的详情一张、列表态一张（看得出只有选中行的入口可点，其余行入口是禁用观感，C4）。只读模式在便携副本里怎样触发，执行时按应用的既有行为核实；触发不了就在完成报告里写「未验证」。
- 内存：`build\measure-startup.ps1`，与 5.0 开工前置里量的基线对比。
- 低版本系统上的纯色回退：本机是 Windows 11，**无法真机验证**，报告里写「未验证」，只报告测试覆盖到的部分。
- 提交前重新看 `git status` 与 `git log`，确认暂存区里只有本阶段的文件：**不得**包含 `.codex/`、`tasks/` 下的任何文件、`artifacts/`。
- **提交前须用户确认**：把完成报告与暂存文件清单交给用户，用户明确说提交才提交。不 push、不 amend、不 rebase、不 reset、不 clean。

**步骤提纲：**
- [ ] `dotnet build Iris.slnx -c Release`，粘贴结果行（预期 0 警告）
- [ ] `dotnet test Iris.slnx -c Release`，粘贴汇总行（预期 0 失败）
- [ ] 本地化审计，粘贴结果
- [ ] 做便携副本，按截图清单取证，存到仓库外
- [ ] 跑 `build\measure-startup.ps1`，粘贴完整输出，与基线并列
- [ ] 在本文件末尾写完成报告
- [ ] `git add` 本阶段全部文件（按第 4 节逐个路径加，不用 `git add -A`），`git status` 复核
- [ ] 把报告交给用户，等用户确认
- [ ] 用户确认后提交

**详细步骤：**

> **硬红线：**
> 1. **绝不直接运行 `src/Iris.App/bin` 下的 `Iris.App.exe`**（会污染用户真实 `%APPDATA%\Iris` 数据与自启动注册表）。真机取证只允许在 `%TEMP%\iris-t041-phase2-real-<timestamp>` 便携副本（带 `portable.txt`）中进行，且绝不杀掉非本任务启动的进程。
> 2. **报告里的每一个数字、测试名、耗时、内存读数都必须来自本任务实际命令输出**；没测或受宿主限制没取到的证据如实写「未验证」。
> 3. **写完完成报告并暂存后必须停下来请用户审阅确认**，在用户明确回复同意提交之前**严禁执行 `git commit`**。

#### 详细步骤

- [ ] **Step 1：正式采集构建、全量测试与本地化审计原始输出**

把输出保存到仓库外的证据目录 `%TEMP%\iris-t041-evidence\phase2-<timestamp>`，以便原样粘贴进完成报告：

```bash
EVID="$TEMP/iris-t041-evidence/phase2-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$EVID"
dotnet build Iris.slnx -c Release | tee "$EVID/build.txt"
dotnet test Iris.slnx -c Release --no-build | tee "$EVID/test.txt"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/audit-localization.ps1 -EvidenceDirectory "$EVID/localization-audit" | tee "$EVID/audit.txt"
grep -n "Wpf.Ui" Directory.Packages.props | tee "$EVID/packages.txt"
```

期望：构建 `0 个警告 / 0 个错误`；7 个测试程序集 `0 失败`（逐个程序集记录通过数、失败数、跳过数与耗时）；本地化审计退出码 `0`；`Directory.Packages.props` 中 `Wpf.Ui` 仍为 `4.3.0`、无新增依赖。

- [ ] **Step 2：启动耗时与内存指标前后对比（`build/measure-startup.ps1`）**

`build/measure-startup.ps1` 自带 `%TEMP%\iris-measure-<guid>` 便携沙箱与 `portable.txt`，并在收尾发 `WM_QUERYENDSESSION`/`WM_ENDSESSION` 退出自建进程：

1. 先核对运行前没有任何已运行的 `Iris.App` 进程（若有用户自己开着的实例，停下来报告用户或确认端口/单实例互斥，绝不擅自 `Stop-Process` 非自己启动的进程）。
2. 对当前阶段 2 构建产物跑一次：
   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/measure-startup.ps1 -Configuration Release | Tee-Object -FilePath "$env:TEMP\iris-t041-evidence\measure-after.txt"
   ```
3. 与开工前置（5.0 第 3 条）测得的 `dc08c48` 基线（若开工未存文件，可像阶段 1 那样用 `git archive dc08c48` 导出到 `%TEMP%\iris-t041-baseline-dc08c48` 独立编译运行一次 `measure-startup.ps1`，完全不动当前工作树与暂存区！）并列记录 `StartupMs`、`WorkingSetMB`、`PrivateBytesMB`、`VisibleWindows`、`ExitCode`、`ShutdownLogLines`。

- [ ] **Step 3：便携副本真机运行与完整截图取证（含真实数据未污染校验）**

1. **前快照**：记录 `%APPDATA%\Iris` 下 `settings.json`、`secrets.dat`、`history.db` 的存在性、字节长度与 `LastWriteTime`，以及 `HKCU:\Software\Microsoft\Windows\CurrentVersion\Run` 中 `Iris` 键值快照。
2. **制作便携副本**：
   - 将 `src/Iris.App/bin/Release/net10.0-windows*` 构建产物复制到 `%TEMP%\iris-t041-phase2-portable-<guid>`，在 `Iris.App.exe` 同目录写入 `portable.txt`，并预置离线安全的 `data\settings.json`（禁用启动检查更新与自动联网、准备 ≥ 2 个翻译服务预设与 ≥ 2 个 OCR 服务配置以便验证列表与钻取）。
3. **启动便携副本并自动化取证**（记录启动的 `PID`，全程只操作该 `PID`）：
   - 通过 `Iris.App.exe --action OpenSettings` 打开设置窗口（并通过 `DwmGetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE)` 读取三个窗口的真实系统材质返回值）。
   - **设置窗口矩阵截图**（存至 `$EVID/screenshots/`）：
     - 「浅色 + 中文 + 默认尺寸 `760×520`」全拍 **10 个画面**：常规、翻译服务列表、翻译服务详情（含固定面包屑与展开/收起高级选项）、快捷键、划词、OCR 列表、OCR 详情、截图、网络、历史。
     - 其余组合（浅/深 × 中/英 × `760×520` / `600×440` 最小尺寸，以及导航临界宽度 `680×520`）：覆盖常规页、翻译服务列表、翻译服务详情、快捷键页、OCR 列表/详情。
     - **浅色窗口压在深色背景 / 深色窗口压在浅色背景**对照图，以及**窗口失焦回退态**截图各一组，证明亚克力半透明合成与失焦纯色回退真实生效。
   - **U5 + C9 三个亚克力窗口并排与翻译窗口四看点核验**：
     - 通过 `--action OpenHistory` 与 `--action InputTranslate` 打开历史窗口与翻译窗口，拍浅色、深色及三窗并排对照图。
     - 逐项核验 C9 翻译窗口外包着色层四个看点：① 四角有无着色溢出圆角之外；② 原 `Margin="12,8,12,12"` 那一圈是否与内部同色、无未着色白边；③ 按住头部拖动与四边/四角缩放是否正常；④ 设置、历史、翻译三窗底色是否一致。
   - **U6 / U7 / U8 钻取行为真机验证截图**：
     - U8：点击添加 OCR 服务后自动进入新服务详情页截图；
     - U6：非首项 OCR 服务在详情态点击保存后仍留在原服务详情页截图；
     - U7 + C4：若便携副本可构造只读态（如配置文件只读锁或对应状态），截取翻译服务非选中行进详情只读图、OCR 列表态仅选中行亮起而其余行禁用观感图、OCR 当前选中行进详情只读图；若真机便携副本无非侵入方式触发只读态，如实标记真机截图「未验证」并引用 `TranslationListFreezeTests` 与 `OcrDetailNavigationTests` 自动化证据。
4. **优雅退出与后快照核对**：
   - 运行便携副本 `Iris.App.exe --action Exit` 等待该 `PID` 退出（确认 `Get-Process Iris.App` 无残留自建进程），再次读取 `%APPDATA%\Iris` 与 `HKCU\…\Run` 与前快照逐项比对，确认 **0 改动**，随后清理便携运行目录（保留 `$EVID` 证据目录备查）。

- [ ] **Step 4：撰写阶段 2 完成报告（追加到计划文件末尾）**

在 `docs/superpowers/plans/2026-11-15-acrylic-ui-phase-2-settings-window.md` 文末追加 `## 7. 阶段 2 完成报告`，严格包含第 5 节 Task 14 列出的全部 9 项必填内容：
1. 三条命令原始输出（Release 构建、全量测试逐程序集汇总行、本地化审计输出）。
2. `Directory.Packages.props` 的 `Wpf.Ui` 行原文。
3. `build\measure-startup.ps1` 改造前后完整输出并列。
4. **与工单的偏差**（逐条列出：全部【计划裁决】C1–C11、Q5 开关仅点本体切换、U7 在 OCR 页受 `SelectedOcrProfile` setter 守卫限制只落实当前选中行可进详情 C4、U6 回写触发旧选中项 `InvalidateTest()`、图标替换退路实际发生项等）。
5. **改过的既有测试**（逐文件、逐测试方法名列出改了什么与原因，一条不漏）。
6. **未验证**（Windows 10 低版本系统纯色回退真机观感、高对比度真机观感，以及任何未在真机复现的边角）。
7. **遗留问题**（切主题一帧中间态 N1、导航项不可聚焦、`IrisAcrylicRow*` 留待阶段 3/4、设计文档「四个预设」与代码 10 个预设差异、`ARCHITECTURE.md` 中 `NavigationView` 描述陈旧、`tasks/current_task.md` 状态行未动等）。
8. 真机验证过程记录（便携目录、`$EVID` 路径、截图张数、C9 四看点结论、`%APPDATA%\Iris` 与注册表零改动核对结论）。
9. 以「请求评审」收尾（**不动** `tasks/BACKLOG.md`、`tasks/current_task.md`、`specs/`）。

- [ ] **Step 5：按路径逐个 `git add` 暂存并全面核查暂存区（严禁 `git add -A`）**

按计划第 4 节文件清单逐个路径 `git add`（包含新建/修改的 `src/`、`tests/`、`build/localization/app-catalog.json` 以及追加了完成报告的 `docs/superpowers/plans/2026-11-15-acrylic-ui-phase-2-settings-window.md`）：

```bash
git status --short
git diff --cached --stat
```

严格核对：
- **零未暂存业务/测试改动**；
- 暂存区里**绝不包含** `.codex/`、`tasks/`、`specs/`、`artifacts/`、`docs/superpowers/plans/phase-2-work/`、`docs/superpowers/handoff/`；
- 运行一次 `bash docs/superpowers/plans/phase-2-work/checkpoint.sh "phase 2 implementation and completion report staged, awaiting user commit approval"` 存盘并更新 `STATUS.md`。

- [ ] **Step 6：向用户呈交完成报告与暂存清单，等待用户明确批准后再 `git commit`**

把完成报告核心数据、偏差清单、改过的既有测试清单、`git diff --cached --stat` 暂存文件清单完整发给用户审阅，**明确询问用户是否批准执行阶段 2 的唯一一次本地提交**（含确认提交信息正文）：

```text
T-041 phase 2: restyle the settings window with row cards and list-detail navigation
```

仅在收到用户明确肯定指令后，才执行 `git commit` 并用 `git log -2 --stat` 与 `git status --short` 复核（不 push、不 amend、不 rebase）。

**提交信息：**

```
T-041 phase 2: restyle the settings window with row cards and list-detail navigation

Co-Authored-By: Antigravity <noreply@google.com>
```

**完成报告必须包含**（A3.5）：
1. 三条命令的原始输出：构建结果行、`dotnet test Iris.slnx -c Release` 的汇总行、本地化审计结果。
2. `Directory.Packages.props` 里与 WPF-UI 有关的行（证明没加包、没升级）。
3. `build\measure-startup.ps1` 的完整输出，改造前后并列。
4. 「与工单的偏差」：本计划所有标【计划裁决】的条目；第 6 节各项最终按哪种做法做的；提交结构上的偏差（如有）。
5. 「改过的既有测试」：逐条写文件、用例名、改了什么、为什么（第 4.2 节那张表里实际发生的每一条）。
6. 「未验证」：低版本系统上回退的真实观感；2.13 里到收尾仍没核实的条目。
7. 「遗留问题」：切主题时的一帧中间态（N1）；高对比度下的回退；导航项不可聚焦；`IrisAcrylicRow*` 两个键仍无使用者；设计文档写「四个预设」而代码是 10 个；`ARCHITECTURE.md:1098` / `:287` 提到 `NavigationView` 与实现不符；`tasks/current_task.md` 的状态行过期（都只报告，不改）。
8. 手工验证是怎么做的（便携副本路径、证据目录、截图张数）。
9. 以「请求评审」结尾；`tasks/BACKLOG.md` 不动。

**验证命令：** `dotnet build Iris.slnx -c Release`（0 警告 0 错误）；`dotnet test Iris.slnx -c Release --no-build`（0 失败）；`powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/audit-localization.ps1 -EvidenceDirectory "$EVID/localization-audit"`（退出码 0）；`powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/measure-startup.ps1 -Configuration Release`。

**要改的既有测试：** 无。

---

## 6. 待用户确认

### 6.1 已由用户裁决（2026-11-17，DECISIONS.md 第 5–8 条）

原 Q1–Q4 用户都已回答，**四条都没有选当时的默认做法**；计划正文已按用户的选择改写，这里只留对照。

| 原问题 | 用户的选择 | 编号 | 落在哪个任务 |
|---|---|---|---|
| Q1 OCR 页保存 / 重新加载之后，留在详情还是回列表？ | **留在详情**：原选项 (b)，视图层按 `Id` 把选中项写回原来那一项；不改视图模型的既有方法（不动 `MergeOcrSettings`）。已知副作用：回写触发旧选中项的 `InvalidateTest()`，进偏差清单。 | U6 | 11（规则 T4-OCR，S0-10） |
| Q2 背板着色层，历史窗口与翻译窗口是否在本阶段一并加？ | **本阶段一并加**：只加着色，不动版面；单开一个任务。 | U5 | 4B（S0-6）；真机取证在 14 |
| Q3 新增 OCR 服务之后是否也自动进入详情？ | **自动进入**：视图层在点击添加后判断是否真的加成功再进入。 | U8 | 11（规则 T3-OCR，S0-10） |
| Q4 只读模式下进不了详情，接受吗？ | **不接受**：只读时仍可进入详情查看、不能改；列表容器不再整体绑 `CanEdit`，只在保存中冻结；行内开关、「⋯」、把手、添加按钮各自跟随 `CanEdit`。翻译服务页完全落实；**OCR 页只落实了一半**（协调方裁决 C4，见 6.3）。 | U7 | 11、12（S0-8、S0-9、S0-10）；前置事实已核实（2.7） |

按用户的这四个选择，本计划仍然**不需要修改任何既有方法的行为**：U6 选的是 (b)，只经 `SelectedOcrProfile` 的公开 setter 回写；当初唯一会改既有方法的选项（Q1 的 (c)，改 `MergeOcrSettings`）用户没有选，本计划不做。

### 6.2 仍待用户确认

下面这条给了默认做法，**执行不因此停下**；用户改主意时，只影响表里「受影响的任务」那一列。

| # | 问题 | 默认做法（计划按这个写） | 其他选项 | 受影响的任务 |
|---|---|---|---|---|
| Q5 | 复选框改成「标题在左、开关在右」之后，点文字不再切换，只有点开关本身才切换。接受吗？ | **接受**（与 Windows 11 设置一致）。记入偏差清单。 | 让整张行卡片可点击切换：需要在行卡片上加鼠标处理，并与快捷键页「鼠标移出取消录制」、行内其他控件的点击互相避让。 | 5–12 |

### 6.3 交计划时需向用户说明

**U7 在 OCR 页只落实了一半**（协调方裁决 C4，1.2A）。

- 用户要的：只读模式下仍可进入详情查看，翻译服务页与 OCR 页都这样做。
- 实际做到的：翻译服务页——只读时任意一项都能进详情看、不能改。OCR 页——`CanEdit` 为假且不在保存中时（只读、录制快捷键中等），**只有当前选中的那一项**能进详情看；其余行的入口是禁用的。
- 原因：进详情要先把该项写成选中项，详情面板靠选中项取值；而 `SelectedOcrProfile` 的 setter 有一行守卫（`src/Iris.App/Settings/SettingsViewModel.Ocr.cs:52`，`CanEdit` 为假时拒收写入）。这一行守卫属于业务逻辑，本计划的约束是不改视图模型既有成员的行为（第 1.3 节第 9 条），所以没有动它。翻译服务那边的 setter 只在保存中拒收（`SettingsViewModel.cs:332–340`），没有这个问题。
- 若用户希望完全落实：需要把 `SettingsViewModel.Ocr.cs:52` 的守卫从 `CanEdit` 放宽（例如只在保存中拒收）。这会**改业务逻辑**，并可能影响既有测试（放宽后只读、录制中也能换 OCR 选中项，换选中项会触发旧项的 `InvalidateTest()`）；**本计划未做**，需用户明确同意后另行安排，届时 S0-10 的行入口条件与 Task 11 的相应测试项随之简化成与翻译服务页相同。

---

## 7. 自检

### 7.1 用户裁决 → 任务

| 裁决 | 落在哪 |
|---|---|
| U1 背板着色 | 键与四份字典的值：任务 1；设置窗口用上：任务 4；另两个窗口：任务 4B（U5） |
| U2 普通页行图标 | 样式 `IrisRowIcon`：任务 2；逐页：任务 5–9；详情与 OCR 不带：任务 11、12 |
| U3 新增翻译服务后自动进入 | 规则 T3：任务 12（`TranslationDetailNavigationTests`） |
| U4 纯色回退补齐并启用 | 任务 1（`IrisSolidFallbackTests`） |
| U5 历史窗口与翻译窗口本阶段一并加着色 | 任务 4B（S0-6；`AcrylicWindowTests` 新增 2 条；既有测试无需改）；真机三窗对照：任务 14 |
| U6 OCR 页保存 / 重载后留在详情（视图层按 `Id` 回写） | 规则 T4-OCR：任务 11（`OcrDetailNavigationTests`）；前置核实（`CanEdit` 的通知）：任务 10 |
| U7 只读可进详情、列表只在保存中冻结 | 前置事实已核实（2.7「已核实」：`IsSaving` 可绑定、`CanEdit` 的组成与通知、两个 setter 的拒收条件）。翻译服务页（完全落实，只读时任意行可进）：任务 12（`TranslationListFreezeTests`）。OCR 页（**部分落实**，见 C4）：任务 11（`OcrDetailNavigationTests`） |
| U8 新增 OCR 服务后自动进入 | 规则 T3-OCR：任务 11（`OcrDetailNavigationTests`） |
| C1 列表行不做按下态 | 任务 2（样式与取值表）；S0-11 |
| C2 金丝雀每次取图前同步对齐 | 任务 3 |
| C3 六条夹具守卫断言保留 | 任务 3 |
| C4 不改 `SelectedOcrProfile` 的 setter；OCR 行入口在 `CanEdit` 为假时只有当前选中行可用 | S0-10「只读模式」；任务 11（`OcrDetailNavigationTests`：只读时仅当前选中项可进详情、其余行入口禁用、详情内不可改）；向用户说明：第 6.3 节 |
| C5 行内校验错误文字用 `IrisRowError` | 定义与测试：任务 2（S0-11）；用法：S0-4；逐页使用：任务 5–9（OCR 页：任务 11） |
| C6 行卡片图标逐行选（核实口径：`lib\net462\Wpf.Ui.dll` 反射为准） | 规矩与退路：S0-4「图标」、S0-2 的图标表；各页的「行 → 首选 → 退路」表：任务 5–9 的详细步骤 |
| C7 页级说明文字保留在页标题下、可编辑容器之外 | 写法与四页清单：S0-4；任务 5–9 逐页照做——有页级说明的是任务 5（截图页）、8（网络页）、9（快捷键页），任务 6、7 的页没有、不新编；OCR 页：任务 11 |
| C8 应用级直接切 WPF-UI 主题的设置窗口渲染夹具都纳入任务 3 | 任务 3；夹具清单：S0-13 |
| C9 翻译窗口外包一层着色 `Grid`（等价做法，进偏差清单） | 任务 4B；真机截图的四个看点：任务 14 |
| C10 进度快照放旁支（进偏差清单） | 流程；偏差清单：任务 14 |
| C11 `SettingsWindow` 多两个 `internal` 只读属性（进偏差清单） | 定义：任务 10；使用：任务 11、12；偏差清单：任务 14 |

### 7.2 设计 §4 的要求 → 任务

| 设计行 | 要求 | 任务 | 守着它的测试 |
|---|---|---|---|
| :60 | 低版本系统纯色背景、卡片不透明、布局不变 | 1 | `IrisSolidFallbackTests` |
| :74 | 默认 760×520、最小 600×440 | 4（不改） | `SettingsWindowTranslationUiTests` 用例 1 |
| :75 | 八个导航项带图标、选中有浅底加短线、仍是 `Border` | 4 | `SettingsWindowNavigationLayoutTests` |
| :76、:260 | 导航 176 / 窄于 680 时约 48 只剩图标、提示与读屏名 | 4 | 同上 |
| :77 | 所有页面在同一个 XAML、同一名称域，不拆 `UserControl` | 4–12 | 2.9(a) 的各 `FindName` 测试 |
| :81 | 每项设置一张行卡片，说明全部保留 | 5–9 | `SettingsRowCardLayoutTests` |
| :82 | 仍是 `CheckBox`，套开关样式 | 5–12 | 同上；`UpdatePreferenceUiTests` |
| :83 | 仍是 `TextBox`，不换 `ui:NumberBox` | 5–9、11 | 各 UI 测试的强转 |
| :84 | 密码框仍是原生 `PasswordBox` | 8、11、12 | `OcrSettingsUiTests`、用例 4 |
| :85 | 六个普通页只改版面 | 5–9 | 各页既有 UI 测试 |
| :90 | 「添加服务」仍是 `ui:Button` 加菜单，英文下宽度 > 70 | 12 | `LocalizationTests.cs:238–268` |
| :91 | 列表仍是 `ListBox`；行 = 把手、开关、名字加模型、「⋯」、右箭头 | 12 | `TranslationDetailNavigationTests` |
| :92 | 「⋯」= 上移 / 下移 / 删除，先选中该行再调既有命令 | 12 | 用例 7（重写后）、`TranslationDetailResetTests` |
| :93 | 名字区、右箭头、行上 Enter 进入；把手、开关、「⋯」不进入；方向键只移动选中 | 12 | `TranslationDetailNavigationTests` |
| :96 | 详情标题「翻译服务 › 服务名」，点父级返回 | 12 | 同上 |
| :97 | 四个分组；高级选项仍是展开器、默认收起 | 12 | 用例 7 块 6 |
| :98 | 详情控件保留名字、不进模板 | 12 | 用例 4、6；Scenario8 |
| :101 | 「是否显示详情」是视图模型上的界面状态，不持久化，不是新页面键 | 10 | `SettingsViewModelDetailViewTests` |
| :102 | 切换导航页或删除当前服务时回到列表 | 10、12 | `TranslationDetailResetTests` |
| :103 | Esc 仍是放弃并关闭 | 12（不改代码） | `SettingsDetailEscapeTests`；W01 |
| :104 | 保存期间列表的开关、「⋯」、添加、拖动不可用（按【用户】U7：冻结只在保存中；只读时行内控件禁用但可进入详情——与设计字面口径的差别进偏差清单） | 11、12 | `TranslationListFreezeTests`、`OcrDetailNavigationTests` |
| :105 | 旧的窄窗堆叠布局定义与代码删除 | 12、13 | 任务 13 的 grep |
| :109–111 | OCR：六项页面级设置在上、列表在下、同样钻取、无把手无移动、内置服务不可删 | 11 | `OcrDetailNavigationTests` |
| :115 | 底栏的状态、重试、取消、保存都在；600×440 不横向溢出；保存 / 取消宽度 ≥ 70 | 4 | `MouseRenderingTests`；用例 7 块 5；`SettingsWindowNavigationLayoutTests` |
| §8.1 | 重写分栏断言 | 12 | — |
| §8.2 | 五类新测试 | 4、12 | 第 4.1 节 |
| §9 | 零警告、全量测试、本地化审计、真机截图、内存、回退「未验证」 | 13、14 | — |

### 7.3 A6.12 的 14 条硬约束 → 谁守着

| # | 约束 | 守着它的任务 / 测试 |
|---|---|---|
| 1 | 默认 760×520、最小 600×440 | 任务 4 不改；用例 1 |
| 2 | 内容纵向滚动，不横向裁切 | 每页保留自己的 `ScrollViewer`（R16）；`SettingsRowCardLayoutTests`（680 与 600 宽）；`OcrSettingsRenderingTests`、`NetworkSettingsRenderingTests`、W04 |
| 3 | 底栏的保存 / 取消 / 状态始终可用，可换行，不删任何重试入口 | 任务 4 只动底栏背景与上边线；`MouseRenderingTests.cs:68–74`；W04B |
| 4 | 取消 / 关闭按钮 / Esc 都是放弃并关闭；保存中不关；录制中 Esc 只取消录制 | `OnKeyDown`、`OnClosing` 不改；W01；`SettingsDetailEscapeTests` |
| 5 | 保存期间在控件层与视图模型层都冻结 | 控件层：两个列表容器（`ProfilesPane`、`OcrServicesPane`）改由 `IsSaving` 的 `DataTrigger` 冻结（U7，S0-8 / S0-10；`IsSaving` 可绑定且带通知，已核实），T1 另有「保存中不进入」的守卫；六个普通页的 `CanEdit` 容器归属不变（R4）；详情字段容器仍绑 `CanEdit`（`CanEdit` 含 `!IsSaving`，已核实，所以保存中详情字段也不可用）。视图模型层：不改。`TranslationListFreezeTests`（保存中一组）；`OcrDetailNavigationTests`（保存中列表冻结）；`SettingsDraftAndSaveRegressionTests` |
| 6 | 切页、重复打开保留草稿 | 页面不延迟加载、控件始终在树里；`SettingsWindowPresenterTests`、`SelectionSettingsPersistenceTests` |
| 7 | 没失焦的输入也能进保存；数字文本框保留非法输入 | `FlushFocusedBinding` 与七个名字不动；用例 6、Scenario8；各页 UI 测试 |
| 8 | 切换服务不泄漏密码与测试结果；选中变化使异步结果失效 | 密码同步代码不动；T1 先写选中再进入；用例 4；`OcrSettingsUiTests`、`OcrSettingsLifecycleTests` |
| 9 | 导航不自动发请求、不装钩子、不截图、不动剪贴板 | 钻取只翻转两个布尔属性与写选中项，不调任何命令；`OcrDetailNavigationTests` 与 `TranslationDetailNavigationTests` 里各加一条「进入 / 返回不触发测试与请求」的断言 |
| 10 | 翻译服务可拖动排序，上移 / 下移作键盘替代；OCR 不排序、内置不可删、可新增两种自定义 | 任务 12（把手起拖、「⋯」与 Shift+F10 菜单）；任务 11；对应两个新测试类 |
| 11 | 说明文字一条不删 | 任务 5–9 的 `SettingsRowCardLayoutTests`；`LocalizationTests.cs:270–285` |
| 12 | 中英文、浅深色、默认与最小尺寸、100% 与 150% 下可读不裁切 | W04（八个变体）；用例 7（四个变体）；`LocalizationRenderingTests`；任务 14 真机截图 |
| 13 | 代码隐藏不放业务逻辑；文字走资源；颜色间距走主题资源 | 新 partial 只做视图状态翻转与菜单构造；S0-11、S0-12；任务 13 的中文检查与本地化审计 |
| 14 | 只读模式下不能编辑与保存，能关闭 | 六个普通页的 `CanEdit` 容器不变；`NetworkSettingsFoundationTests.cs:85`；`UpdatePreferenceUiTests.cs:74`。两个钻取页按【用户】U7：只读下**能进详情查看**（翻译服务页任意行；OCR 页按 C4 只有当前选中项，其余行入口禁用），但详情字段容器、行内开关、「⋯」、把手、添加按钮都绑 `CanEdit` 而不可用，「⋯」的键盘通路与起拖另有 `CanEdit` 守卫——所以仍然不能编辑；`TranslationListFreezeTests`（只读一组）、`OcrDetailNavigationTests`（只读时仅当前选中项可进详情、其余行入口禁用、详情内不可改）守着。保存入口与底栏不动 |

### 7.4 骨架自查

- **没有「视情况而定」的裁决**：凡是结果取决于未核实事实的地方，都写了「先核实哪一条 → 符合怎么做 → 不符合走哪条退路」（S0-3、S0-6、S0-7、S0-8、S0-10、S0-13 与第 4.2 节）。
- **没有编造的行号与名字**：行号全部来自笔记或第 2.12 节注明的补查；新起的名字（键、属性、字段、元素、测试类）都标了【计划裁决】或列在第 4 节「新建」里；行图标的成员名按 C6 的口径核实（`lib\net462\Wpf.Ui.dll` 反射为准，最终以构建为准，见 S0-4「图标」），每一行都配了页面图标作退路。
- **与用户裁决无冲突，有一处部分落实**：U1–U8 与协调方裁决 C1–C4 见 7.1；N1 列为遗留；原 N2 与原 Q1–Q4 已由用户裁决（U5–U8，对照表见 6.1）；只有 Q5 仍按默认处理（6.2）。**U7 在 OCR 页只落实了一半**（C4：只读时仅当前选中项可进详情），原因与完全落实所需的改动写在 6.3，交计划时向用户说明。
- **业务逻辑**：`SettingsViewModel` 只新增一个 partial 文件里的两个属性，不改任何既有方法；U6 的回写、U8 的自动进入、U7 的冻结拆分全部在视图层（XAML 与代码隐藏的新 partial）完成，只读写既有的公开属性、只绑既有命令。当初唯一会改既有方法的选项（原 Q1 的 (c)）用户没有选，本计划不做。U7 需要的「保存进行中」判据直接绑视图模型上现成的 `IsSaving`（已核实可绑定、带通知），不新增业务状态；`SelectedOcrProfile` 的 setter 守卫（`SettingsViewModel.Ocr.cs:52`）不改（C4）。`ThemeService` 新增一个静态方法加一行调用，属于主题层的回退合并，不是业务逻辑。
- **范围**：按【用户】U5，本阶段多出 `History/HistoryWindow.xaml` 与 `Translation/TranslateWindow.xaml` 两个文件的着色改动（Task 4B），只加着色、不动版面。
- **提交**：只有任务 14 提交一次，且须用户确认。
- **完整性复核（2026-11-24）**：原骨架留空的各任务「详细步骤」（含 Task 4B 与 Task 14 的真机截图逐张清单）已全部补齐并入本文件，全文 `详细步骤：待补` 占位为 `0`；任务 2 里三个样式的具体取值已在任务 2 的详细步骤里定下；任务 5–9 的每一张带图标行卡片均已通过 `lib\net462\Wpf.Ui.dll` 反射核实 `SymbolRegular` 成员存在并配有页面图标退路。


---

## 8. 阶段 2 完成报告

- **日期**：2026-11-25（含评审修复轮 A.1–A.4、A.5 `IrisNavWidth=212` 与报告补证 B.1–B.7、C）
- **分支**：`codex/t041-acrylic-ui`（基线 `HEAD`：`dc08c48 T-041 phase 1: add the shared theme layer and the window material service`）

### 8.1 构建、全量测试与本地化审计原始输出（`IrisNavWidth=212` 最终实测）

#### 1. `dotnet build Iris.slnx -c Release`

```text
已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:11.76
```

#### 2. `dotnet test Iris.slnx -c Release --no-build`

```text
测试运行摘要: 已通过!
  F:\Iris\tests\Iris.App.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.App.Tests.dll (net10.0|x64) 已通过 (2m 33s 210ms)
  F:\Iris\tests\Iris.Infrastructure.Tests\bin\Release\net10.0\Iris.Infrastructure.Tests.dll (net10.0|x64) 已通过 (7s 314ms)
  F:\Iris\tests\Iris.Maintenance.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Maintenance.Tests.dll (net10.0|x64) 已通过 (4s 737ms)
  F:\Iris\tests\Iris.Providers.Tests\bin\Release\net10.0\Iris.Providers.Tests.dll (net10.0|x64) 已通过 (3s 481ms)
  F:\Iris\tests\Iris.ArchitectureTests\bin\Release\net10.0-windows10.0.19041.0\Iris.ArchitectureTests.dll (net10.0|x64) 已通过 (1s 786ms)
  F:\Iris\tests\Iris.Core.Tests\bin\Release\net10.0\Iris.Core.Tests.dll (net10.0|x64) 已通过 (8s 430ms)
  F:\Iris\tests\Iris.Platform.Windows.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Platform.Windows.Tests.dll (net10.0|x64) 已通过 (3s 553ms)

  总计: 3970
  失败: 0
  成功: 3968
  已跳过: 2
  持续时间: 2m 33s 523ms
```

（注：改造前 `dc08c48` 基线为 `总计: 3843, 失败: 0, 成功: 3841, 已跳过: 2`，本阶段净增 **127 条通过测试**，其中评审修复轮 A.1–A.4 新增 4 条先红后绿 TDD 测试；跳过的 2 条与阶段 1 完全相同，为 `Iris.Platform.Windows.Tests` 中受宿主窗口站权限限制自报 `[ENVIRONMENT LIMITATION] Window station creation failed` 的剪贴板桌面隔离测试。）

#### 3. `build/audit-localization.ps1`

```text
AUDIT_EXIT=0
Scanned C# and XAML; Chinese entries including comments: 1192
Compiled resource values matched after XML newline normalization; composite formats parsed: 1238
{"sourceEntries": 1192, "commentEntries": 974, "reviewedNonUiEntries": 218, "resourceCounts": {"App": 466, "Core": 148, "Maintenance": 5}, "resourceErrors": [], "englishNativeNameExceptions": {"App:LanguageChineseNative": "简体中文"}, "exceptionManifestSha256": "2d620105575400696d1f567460d883cf050ff511b49b6f64d5ea0b5192eb9a75"}
```

### 8.2 `Directory.Packages.props` 依赖核验（未新增包、未升级 `WPF-UI`）

```xml
23:    <PackageVersion Include="WPF-UI" Version="4.3.0" />
```

### 8.3 启动耗时与内存指标前后对比（`build/measure-startup.ps1 -Configuration Release`）

> **读数说明**：基线在起始系统 CPU 负载 **94%** 下测得，改后在 **38%** 负载下测得，**两者均高于脚本标明的 30% 有效门槛，因此启动耗时（`StartupMs`）只能作参考，不作硬性 NFR 达标结论**；三轮 `ExitCode=timeout` 以及第 1 轮 `VisibleWindows=1` 为**阶段 1 完成报告第 38 条已记录过的既有现象**（并非本阶段引入）。

#### 改造前基线（`dc08c48`，2026-11-24 21:28:47，起始系统 CPU 负载 94%）

```text
Iris measure-startup | 2026-11-24 21:28:47 | Microsoft Windows NT 10.0.26300.0 | Configuration=Release Runs=3 IdleSeconds=30
System CPU load at start: 94% (startup-time NFR is only meaningful below 30%)
Binary: F:\Iris\src\Iris.App\bin\Release\net10.0-windows10.0.19041.0 (built 2026-11-24 21:24:18)
--- run 1: log lines written during logoff simulation ---
2026-11-24 21:29:21.347 +08:00 [INF] (Iris.App.App) Windows 会话即将结束（"Logoff"）
2026-11-24 21:29:21.386 +08:00 [INF] (Iris.App.App) Iris 已退出，退出码 0
2026-11-24 21:29:21.422 +08:00 [FTL] (Iris.App.Program) Primary startup or cleanup failed: IrisException
--- second instance (run 1) ---
ExitedWithin5s : True
ElapsedMs      : 213
ExitCode       : 0
LogLinesAdded  : 0

--- per run ---
Run StartupMs VisibleWindows WorkingSetMB PrivateBytesMB ExitCode ShutdownLogLines
--- --------- -------------- ------------ -------------- -------- ----------------
  1    1336.3              1        169.4           75.6 timeout                 3
  2    1397.4              0        124.7           54.7 timeout                 3
  3    1353.3              0          125           54.7 timeout                 3

--- averages --- StartupMs=1,362.3  WorkingSetMB=139.7  PrivateBytesMB=61.7
```

#### 改造后实测（阶段 2，2026-11-25 01:05:43，起始系统 CPU 负载 38%）

```text
Iris measure-startup | 2026-11-25 01:05:43 | Microsoft Windows NT 10.0.26300.0 | Configuration=Release Runs=3 IdleSeconds=30
System CPU load at start: 38% (startup-time NFR is only meaningful below 30%)
Binary: F:\Iris\src\Iris.App\bin\Release\net10.0-windows10.0.19041.0 (built 2026-11-25 00:57:50)
--- run 1: log lines written during logoff simulation ---
2026-11-25 01:06:16.476 +08:00 [INF] (Iris.App.App) Windows 会话即将结束（"Logoff"）
2026-11-25 01:06:16.498 +08:00 [INF] (Iris.App.App) Iris 已退出，退出码 0
2026-11-25 01:06:16.523 +08:00 [FTL] (Iris.App.Program) Primary startup or cleanup failed: IrisException
--- second instance (run 1) ---
ExitedWithin5s : True
ElapsedMs      : 139
ExitCode       : 0
LogLinesAdded  : 0

--- per run ---
Run StartupMs VisibleWindows WorkingSetMB PrivateBytesMB ExitCode ShutdownLogLines
--- --------- -------------- ------------ -------------- -------- ----------------
  1     897.4              1          165           76.5 timeout                 3
  2     892.9              0        124.7           54.6 timeout                 3
  3     918.2              0        124.8           54.7 timeout                 3

--- averages --- StartupMs=902.8  WorkingSetMB=138.2  PrivateBytesMB=61.9
--- NFR (ARCH 1.2): StartupMs <= 1500; PrivateBytesMB <= 100; WorkingSetMB <= 160; VisibleWindows = 0; ExitCode = 0; ShutdownLogLines >= 1 ---
```

（内存前后对比：常驻托盘态第 2、3 轮 `WorkingSetMB` 为 `124.7–124.8 MB` vs 基线 `124.7–125.0 MB`、`PrivateBytesMB` 为 `54.6–54.7 MB` vs 基线 `54.7 MB`，三轮均值 `WorkingSetMB=138.2 MB`、`PrivateBytesMB=61.9 MB`，无内存回退；`StartupMs` 两次采样均高于 30% 负载门槛，仅供参考。）

### 8.4 与工单及设计文档的偏差清单

1. **范围扩及历史窗口与翻译窗口的背板着色层（用户裁决 U5 + 计划裁决 C9）**：除设置窗口外，`HistoryWindow.xaml` 根 `Grid` 与 `TranslateWindow.xaml` 外包零边距 `Grid`（因原根 `DockPanel` 带 `Margin="12,8,12,12"`，直接设 `Background` 会留未着色边环，故采用等价外包 `Grid`，C9）在本阶段一并加上 `{DynamicResource IrisAcrylicTintBrush}`；两个窗口的内部版面与控件未动（按工单：阶段 3 = 翻译弹窗，阶段 4 = 历史窗口）。
2. **普通页行卡片左侧图标（用户裁决 U2 + 计划裁决 C6）**：设计文档正文的行卡片定义未写左侧图标（出自用户选定的常规页样张方案 A）；按 U2 与 C6 为常规、快捷键、划词、截图、网络、历史六个普通页逐行配置了 20 号 `ui:SymbolIcon`（全部首选图标均在构建与测试中直接命中，零行回退到页面退路图标），翻译服务页与 OCR 页不带行图标。
3. **常规页更新区拆为两张卡（S0-4 / A7.2）**：样张示意为单行，按 `DESKTOP_RELEASE.md:24` 规格硬约束拆为「启动时检查更新开关行卡片」+「版本 / 状态 / 详情 / 进度条 / 四操作按钮 `WrapPanel` 动作卡」，七项要素与 `CheckBox` 类型全部保留。
4. **钻取状态属性拆为每页一位及新增两个 `internal` 只读 `Id` 属性（S0-8 + 计划裁决 C11）**：设计文档字面为单个「是否显示详情」属性，本阶段在 `SettingsViewModel.DetailView.cs` 拆为 `IsTranslationDetailOpen` 与 `IsOcrDetailOpen` 两位避免两页串状态；另在 `SettingsWindow.DetailNavigation.cs` 新增 `internal string? TranslationDetailId` 与 `OcrDetailId` 以消除 `TreatWarningsAsErrors=true` 下的 `CS0414` 并供测试直接断言。
5. **列表冻结范围收窄与只读进详情在两页的落地差异（用户裁决 U7 + 计划裁决 C4）**：设计 `:104` 原写列表整体随 `CanEdit` 冻结；按 U7 改为两个列表容器（`ProfilesPane`、`OcrServicesPane`）仅在 `IsSaving == true` 期间冻结。只读且未在保存时：翻译服务页**任意行**均可进入详情只读查看；OCR 页因严守「不改 `SettingsViewModel.Ocr.cs:52` 的 `SelectedOcrProfile` setter 业务守卫」（C4），**仅当前选中行**可进入详情只读查看，其余非选中行入口置灰禁用并在 `SelectionChanged` 自动对齐选中项。
6. **OCR 新增后自动进详情（U8）与保存/重载后回写留在原详情项（U6）**：均在视图层 `SettingsWindow.DetailNavigation.cs` 实现，未改动 `MergeOcrSettings` 等任何视图模型方法；已知副作用为保存/重载回写原详情项时会触发旧选中项（首项）的 `InvalidateTest()`，且保存尚未结束（`!CanEdit`）瞬间详情短暂绑定首项、待 `CanEdit` 恢复后立即切回原项。
7. **OCR 面包屑与翻译服务面包屑的滚动层级差异（S0-10）**：翻译服务面包屑固定在 `DetailsPane` 滚动区上方；OCR 页因既有测试按名字查找并强转根 `ScrollViewer`（`OcrPageScroll`），面包屑置于 `OcrPageScroll` 内部随内容滚动。
8. **开关交互热区与列表行按下态（问题 Q5 默认项 + 计划裁决 C1）**：23 个 `CheckBox` 改为「左标题、右 `IrisSwitchCheckBox`」后仅点击开关本体切换（点左侧标题文字不切换，与 Windows 11 设置一致，Q5）；`IrisListRowItem` 不单独造 `ListBoxItem` 原生不存在的按下态（C1）。
9. **行内错误样式、页级说明、骨架边线、底栏单行排布与宽导航 `212px`（计划裁决 C5、C7、S0-6 + 评审修复 A.1、A.5）**：行内校验错误统一用 `IrisRowError` 空串自动折叠（C5）；截图、网络、快捷键、OCR 四页的页级说明保留在页标题下、`CanEdit` 容器外（快捷键页留在 `CanAccessHotkeysTab` 面板内，C7）；移除导航 `Border` 原右侧竖线，底栏去掉不透明背景并将上边线画刷换为 `IrisAcrylicCardStrokeBrush`（S0-6）；评审修复 A.1 将底栏改为单行两列（左状态与重试 `WrapPanel`、右取消/保存，空 `StatusMessageTextBlock` 自动折叠），保留全部 7 个重试入口且使 `760×520` 默认窗口（含生产常态鼠标状态行）下 8 个导航项全部完整显示、不出滚动条；按用户 A.5 裁决，将 `IrisNavWidth` 由设计 `:76` 的 `176` 调宽至 `212`（与设计 `:76` 的 `176` 不一致，记入本偏差），并同步将网络页 `NetworkUserNameTextBox` / `NetworkPasswordBox` 宽度由 `220` 调为 `200` 以在 `680` 宽下守住行标题宽度 `>= 120px`。
10. **新增 4 条本地化资源键（S0-12）与旁支进度快照（C10）**：新增 `SettingsRowMoreActions`、`SettingsRowOpenDetails`、`SettingsRowDragToReorder`、`SettingsRowEnabled` 四键（未复用英文为 `Enable` / 中文为 `已启用` 的近似旧键）；开发过程快照保存在本地旁支 `codex/t041-acrylic-ui-wip`（不合并、不推送，工作分支保持阶段单次提交）。

### 8.5 改过的既有测试清单（逐文件、逐用例）

| 文件 | 测试方法 / 辅助方法 | 改动内容 | 原因 |
|---|---|---|---|
| `tests/Iris.App.Tests/Themes/IrisColorKeyTests.cs` | `IrisColorKeys_AreTranslucent`、`SolidFallbackKeys_AreOpaque` | 追加 `[InlineData("IrisAcrylicTintBrush")]`，并在不透明断言中纳入新建的 `Iris.SolidFallback.Dark.xaml` | 覆盖新增的第 5 个主题颜色键与深色纯色回退字典（Task 1） |
| `tests/Iris.App.Tests/Themes/IrisStyleResourceTests.cs`、`IrisStyleRenderingTests.cs` | 新增样式解析与 `IrisListRowItem` 像素对比测试（既有断言未删改） | 追加阶段 2 新增的 11 个样式/尺寸键与 `IrisNavItem` 紧凑内边距断言 | 守护 S0-11 全部新增键（Task 2） |
| `tests/Iris.App.Tests/Themes/AcrylicWindowTests.cs` | 新增 `HistoryWindow_RootPanelCarriesTheAcrylicTint`、`TranslateWindow_TintLayerCoversTheClientAreaAndLeavesTheLayoutAlone`（既有断言未删改） | 追加历史窗口与翻译窗口根着色面板守卫及方法间空行整理 | 守护 U5 与 C9 着色层（Task 4B、13） |
| `tests/Iris.App.Tests/Settings/SettingsHotkeysIntegrationTests.cs` | `W04_OffScreenRendering_EightVariants_LightDark_DefaultMinSize_100And150Scale_NoClipping` | 切 WPF-UI 主题后同步调 `ThemeService.ApplyIrisThemeDictionary`，加 C3 卡片透明度守卫，并在 `finally` 首句同步还原浅色字典（像素与宽度断言未改，8 变体原样通过） | S0-13、C3 防止渲染测试深浅字典错配与跨测试泄漏（Task 3） |
| `tests/Iris.App.Tests/Settings/SelectionSettingsRenderingTests.cs` | `Prepare`、`SelectionPage_RendersBothThemesAndSizes_WithErrorAndLongList` | `Prepare` 同步切 Iris 字典并加 C3 守卫；`RenderTargetBitmap` 前加 C2 同步再对齐；在 `Run` 的 STA 回调内加 `try/finally` 还原浅色字典（原 `(150, 50)` 背景取点因导航去不透明底后同享根 `IrisAcrylicTintBrush` 原样通过，未改坐标） | S0-13、C2、C3 渲染夹具主题对齐与 STA 还原（Task 3） |
| `tests/Iris.App.Tests/Updates/UpdateRenderingTests.cs` | `Window` | 窗口资源字典在 WPF-UI 字典后同步合并 `Themes/Iris.{Light\|Dark}.xaml` 并加 C3 守卫 | S0-13、C3 窗口级夹具对齐（Task 3） |
| `tests/Iris.App.Tests/History/HistoryRenderingTests.cs` | `Theme` | 同上合并 `Themes/Iris.{Light\|Dark}.xaml` 并加 C3 守卫 | S0-13、C3 窗口级夹具对齐（Task 3） |
| `tests/Iris.App.Tests/Localization/LocalizationRenderingTests.cs` | `Theme` | 元素资源字典合并 `Themes/Iris.{Light\|Dark}.xaml` 并加 C3 守卫 | S0-13、C3 元素级夹具对齐（Task 3） |
| `tests/Iris.App.Tests/Settings/OcrSettingsRenderingTests.cs` | `Prepare`、`PagesAndLifecycleStates_RenderAtActualSizesInBothThemes` | 1. `Prepare` 同步切 Iris 字典并在每个 `Run` 的 STA 回调内 `try/finally` 还原浅色字典（Task 3，C8）；<br>2. 截取 OCR 详情字段/测试态前设 `f.Vm.IsOcrDetailOpen = true; f.Window.UpdateLayout();`，截取列表态前设 `false`（Task 11） | 1. S0-13/C8 主题对齐；<br>2. OCR 页改为列表↔详情钻取后列表与详情不再同屏 |
| `tests/Iris.App.Tests/Settings/OcrSettingsUiScope.cs` | 构造函数、`Run`、`FakeSettingsService.IsReadOnly` | 增加默认值为 `false` 的可选参数 `bool readOnly = false` 并将替身 `IsReadOnly` 设为可配置（全部既有调用行为不变） | 供 `OcrDetailNavigationTests` 构造真实的只读模式（`CanEdit == false && IsSaving == false`，Task 11） |
| `tests/Iris.App.Tests/Settings/NetworkSettingsRenderingTests.cs` | `Prepare`、`CompleteNetworkPageAndLifecycleStates_RenderInBothThemesAndSizes` | `Prepare` 同步切 Iris 字典并在每个 `Run` 的 STA 回调内 `try/finally` 还原浅色字典 | S0-13、C8 主题字典对齐与还原（Task 3） |
| `tests/Iris.App.Tests/Settings/NetworkSettingsFoundationTests.cs` | `ActualFutureSettingsWindow_DisablesEditsSaveAndTests_AllowsCloseAndRendersNotice` | `ApplicationThemeManager.Apply` 后立即同步切 Iris 字典，并在 `finally` 的 `ShutdownAsync` 之前还原浅色字典 | S0-13、C8 主题字典对齐与还原（Task 3） |
| `tests/Iris.App.Tests/Settings/SettingsWindowTranslationUiTests.cs` | 用例 7 `Layout_AtSpecifiedSizeAndTheme_AllControlsAreUsableAndNotClipped`（用例 1–6 一字未改） | 1. 补 C3 守卫与 `finally` 无条件还原浅色字典（Task 3）；<br>2. 按设计 §8.1 分块重写用例 7：将已删除的四条分栏行列定义与三个工具栏按钮断言替换为导航宽度切换、列表态 `ProfilesPane`/`AddProfileButton`/行操作按钮断言，以及置 `IsTranslationDetailOpen = true` 后的面包屑、详情输入框、操作按钮、高级展开器与底栏不裁切断言（Task 12） | 设计文档 §8.1 明文要求；注：`SettingsDraftAndSaveRegressionTests` 的 `Scenario8` 与本文件用例 6 均使用逻辑焦点 `FocusManager.SetFocusedElement`，折叠态下仍可正常刷入草稿，故 `Scenario8` 与用例 6 原样通过、零修改 |

（另：评审修复轮在新建测试文件 `SettingsWindowNavigationLayoutTests.cs`、`OcrDetailNavigationTests.cs`、`TranslationDetailNavigationTests.cs` 中补充了 4 条先红后绿的 TDD 回归用例：`DefaultWindowWithMouseStatus_FitsAllEightNavItemsWithoutScrollbar`、`CloseOcrDetail_RestoresKeyboardFocusToSelectedListBoxItem`、`AddOcrProfile_AutoEnterMovesFocusToBreadcrumbBackButton`、`DragHandlePressAndRelease_ResetsDragStartedOnHandleFlag`。）

### 8.6 真机便携副本验证记录（含 U5/C9/U6/U7/U8 与评审补证）

- **执行方式**：自动化脚本（Windows PowerShell 5.1 + UI Automation + Win32 `DwmGetWindowAttribute` / `CopyFromScreen`，按 `GEMINI.md:23` 声明为自动化操作真实桌面窗口而非人工鼠标手点）。将 Release 产物复制到 `%TEMP%\iris-t041-phase2-*-portable-*` 临时目录并放置 `portable.txt`，全程未直接运行 `src/Iris.App/bin` 下的可执行文件。
- **证据目录与截图版本区分**：`C:\Users\28910\AppData\Local\Temp\iris-t041-evidence\phase2-final\screenshots`（共 **50 张**真实桌面窗口 PNG，按采集时的版面版本严格区分如下，**不以旧图充当新版面证据**）：
  - **最终版（单行底栏 + `212px` 宽导航，4 张最新重截并亲自复看）**：`en-light-760-general.png`、`en-light-680-general.png`、`01-zh-light-760-general.png`、`12-zh-light-680-hotkeys.png`。实看结论：英文 8 个导航项中 7 项（`General`、`Translation providers`、`Shortcuts`、`Selection translation`、`Screenshot`、`Network and proxy`、`Translation history`）已完全不截断，仅 `Text recognition (OCR)` 在中文系统默认字体度量下仍略截为 `Text recognition (OC...`（约差 10px，悬停 `ToolTip` 显示全文）；`680×520` 下英文常规页与中文快捷键页（`HotkeyRecorderBox` 录入/清除按钮）内容区均无任何裁切，左侧 8 个导航项完整显示、无滚动条。
  - **单行底栏 + `176px` 导航（12 张，A.1 修复后、A.5 加宽前截）**：`09-zh-light-760-network.png`、`10-zh-light-760-history.png`、`13-zh-light-600-general.png`、`16-zh-light-760-unfocused-fallback.png`、`17-u6-ocr-saved-stays-in-second-item-detail.png`、`19-u5-history-window-light.png`、`20-u5-c9-translate-window-light.png`、`21-u5-c9-three-windows-side-by-side-light.png`、`22-zh-dark-760-general.png`、`31-u5-c9-translate-window-dark.png`、`32-u5-history-window-dark.png`、`33-u5-c9-three-windows-side-by-side-dark.png`（验证单行底栏、失焦回退、历史页、U6 便携保存与三窗同框/底色）。
  - **底栏与导航改版之前（旧两行底栏 + `176px` 导航，34 张）**：`02`–`08`、`11`、`14`–`15`、`18`、`23`–`30`、其余 `en-dark-*` / `en-light-600-*` / `en-light-760-translation-*` 及 `u7-*`——仅作各页行卡片/钻取内容参考，不作为新版单行底栏或 `212px` 宽导航的版面证据。
- **「重试…」入口在新单行底栏下的真实桌面截图**：**未验证**（自动化离屏渲染已由 `MouseRenderingTests` 在 `600×440` 与 `760×520` 下同时显示 4 个重试按钮验证无越界裁切）。
- **真实窗口 DWM 系统材质原始读数（浅色 + 深色全部 6 组均已实测）**：
  ```text
  SettingsWindow (Light):  hr=0x00000000 DWMWA_SYSTEMBACKDROP_TYPE=3
  HistoryWindow (Light):   hr=0x00000000 DWMWA_SYSTEMBACKDROP_TYPE=3
  TranslateWindow (Light): hr=0x00000000 DWMWA_SYSTEMBACKDROP_TYPE=3
  SettingsWindow (Dark):   hr=0x00000000 DWMWA_SYSTEMBACKDROP_TYPE=3
  HistoryWindow (Dark):    hr=0x00000000 DWMWA_SYSTEMBACKDROP_TYPE=3
  TranslateWindow (Dark):  hr=0x00000000 DWMWA_SYSTEMBACKDROP_TYPE=3
  ```
  （`3` = `DWMSBT_TRANSIENTWINDOW`，即 Windows 11 真实亚克力材质。）
- **C9 翻译窗口外包零边距着色 `Grid` 四个看点逐项结论**：
  1. ① 四角有无着色溢出到圆角之外：**已看**（`20-u5-c9-translate-window-light.png`、`31-u5-c9-translate-window-dark.png` 四角完全贴合系统圆角裁剪，无溢出）。
  2. ② 原 `Margin="12,8,12,12"` 那一圈是否与内部同色、无未着色边：**已看**（四周 `12,8,12,12` 边距环与内部客户区完全同色，无白边/黑边）。
  3. ③ 按住头部拖动与四边/四角缩放：自动化调用 `MoveWindowDip` 缩放 `460×420` ↔ `310×440` 布局正常；真人鼠标按住顶栏拖动与四边拖放缩放：**未验证**（本次由自动化脚本驱动）。
  4. ④ 设置、历史、翻译三个窗口底色是否一致：**已看**。
     - 活动窗口对照请以各自前台激活时的单窗截图为准：浅色 `01-zh-light-760-general.png` / `19-u5-history-window-light.png` / `20-u5-c9-translate-window-light.png`，深色 `22-zh-dark-760-general.png` / `32-u5-history-window-dark.png` / `31-u5-c9-translate-window-dark.png`，三窗在前台激活态下的亚克力半透明合成与 `IrisAcrylicTintBrush` 底色完全一致。
     - 三窗并排同框图 `21-u5-c9-three-windows-side-by-side-light.png` 与 `33-u5-c9-three-windows-side-by-side-dark.png`（便携配置设置 `translateWindow.hideOnDeactivate=false` 以保持翻译弹窗不随失焦隐藏）中，左起依次为 `Iris 设置`（前台活动窗）、`翻译历史`（非活动窗）、`Iris 翻译`（非活动窗）；按 Windows 11 DWM 既定行为，同一时刻仅前台活动窗呈现动态亚克力半透明，其余两个非活动窗回退为不透明的失焦纯色底板（因此并排图里非活动两窗比前台活动窗略偏平，属系统级非活动窗口表现；非活动窗的失焦回退同时由 `16-zh-light-760-unfocused-fallback.png` 单独复现确认）。
- **补证截图核验（失焦回退 `16` 与网络/历史页 `09`/`10`）**：
  - `16-zh-light-760-unfocused-fallback.png`（`SHA256: 6249BF80...`）：将焦点切给屏幕角落不遮挡设置窗口的小窗后截取，清楚呈现未遮挡的 `SettingsWindow` 在失焦时由亚克力自动回退到系统浅灰不透明底色且标题栏文字变淡。
  - `09-zh-light-760-network.png`（`SHA256: FE58B876...`）与 `10-zh-light-760-history.png`（`SHA256: 738E24F6...`）哈希不同且逐张人工复看无误，`10` 完整展示设置窗口「翻译历史」页（2 张带图标行卡片）。
- **U6 / U7+C4 / U8 真机截图核验**：
  - **U6**（`17-u6-ocr-saved-stays-in-second-item-detail.png`）：进入第 2 项 `Windows Local OCR` 详情，将超时修改为 `18` 后**实际点击了底栏「保存」按钮**；底栏显示 `StatusMessageTextBlock='设置已保存'`，读取便携副本 `%TEMP%\...\data\settings.json` 核实 `ocr-windows.timeoutSeconds=18` 已持久化到便携配置（同时用户真实 `%APPDATA%\Iris\settings.json` 字节数与修改时间零变化），且界面仍留在 `Windows Local OCR` 详情页。
  - **U8**（`18-u8-ocr-added-auto-detail.png`）：点击「添加本地服务」后自动进入新建的 `Windows 本地 OCR` 详情页。
  - **U7 + C4**（通过便携配置 `schemaVersion: 99` 触发真实只读模式）：翻译服务只读下点非选中的第 2 行成功进入 `Anthropic Claude` 只读详情（`u7-readonly-translation-nonselected-detail.png`）；OCR 只读列表态下仅第 1 行选中项亮起可点、第 2 行非选中项呈灰色禁用观感（`u7-c4-readonly-ocr-list.png`），点第 1 行成功进入只读详情（`u7-c4-readonly-ocr-selected-detail.png`）。
- **导航高度实测记录（默认 `760×520` 与最小 `600×440`）**：
  - **默认尺寸 `760×520`**（评审修复 A.1 改单行底栏后）：在生产默认常态（底栏显示 `鼠标监听已关闭` 甚至同时叠加 `设置已保存` / 重试按钮）下，`01-zh-light-760-general.png` 与 `17-u6-ocr-saved-stays-in-second-item-detail.png` 实测 **8 个导航项全部完整位于导航可视区内，无竖向滚动条**，并由 `DefaultWindowWithMouseStatus_FitsAllEightNavItemsWithoutScrollbar` 自动化守卫（`ScrollableHeight == 0`、`ComputedVerticalScrollBarVisibility == Collapsed`）。
  - **最小尺寸 `600×440`**：在底栏仅有一行状态（如仅「鼠标监听已关闭」）时左侧 8 个紧凑图标完整显示无滚动条；当保存后底栏出现两行状态（「设置已保存」+「鼠标监听已关闭」）或叠加重试按钮时，左侧 `48px` 窄导航会出现紧贴图标的竖向细滚动条（记入 8.8 遗留）。
- **用户真实环境零污染核验**：便携运行退出后 `Get-Process Iris.App` 进程数为 `0`；前后快照比对 `%APPDATA%\Iris` 下 `settings.json`（`4501` 字节，`2026-10-05T11:41:10.0608678Z`）、`secrets.dat`（`645` 字节）、`history.db`（`147456` 字节）及 `HKCU:\Software\Microsoft\Windows\CurrentVersion\Run` **完全一致（`True`）**。

### 8.7 未验证项

1. **Windows 10 / 低版本 Windows 11（Build < 22621）上的纯色回退真实桌面观感**：本机为 Windows 11 Build `26300`，系统支持亚克力，纯色回退逻辑已由 `IrisSolidFallbackTests`（18 条用例）验证字典合并、切换与完全不透明画刷解析，低版本系统真机观感**未验证**。
2. **Windows 系统高对比度主题下的回退真机观感**：**未验证**（列为下节遗留问题）。
3. **真人鼠标手动拖拽翻译窗口顶栏、拖拽四边/四角缩放及翻译服务列表真人鼠标拖拽重排手感**：自动化测试与自动化窗口取证已覆盖代码路径与几何，真人手动鼠标操作**未验证**。

### 8.8 遗留问题（本阶段不处理，按工单只报告不改）

1. **切主题时约 20–25 ms 的一帧中间态（N1）**：`ThemeService` 先切 WPF-UI 字典再切 Iris 字典，默认本阶段不处理。
2. **高对比度主题下的回退**：高对比度下 WPF-UI 会自行否决亚克力，而 `WindowMaterialService.IsAcrylicSupported` 目前不感知系统高对比度开关。
3. **左侧导航项不可键盘 Tab 聚焦**：8 个导航项保持原有 `Border` 类型与鼠标点击路由，未在本阶段改为可聚焦按钮控件。
4. **`IrisAcrylicRowFillBrush` 与 `IrisAcrylicRowStrokeBrush` 尚无使用者**：行卡片沿用 `IrisRowCard` 继承的卡片画刷，这两个阶段 1 预留键保留给后续阶段决定。
5. **列表与详情的键盘/鼠标边角交互**：按住 `Enter` 键不放时会在列表与详情之间来回切换；行名点击区只监听 `MouseLeftButtonUp`，在列表上方关闭右键/下拉菜单时若鼠标松开恰好落在行名区可能误进详情。
6. **`SettingsHotkeyEnable` 资源键残留**：快捷键页复选框改为无文字开关后，`SettingsHotkeyEnable` 已无界面使用者，仍保留在 `Strings.resx`、`Strings.en-US.resx`、`build/localization/app-catalog.json` 和 `UiText.cs:223`。
7. **观感与极端尺寸边角**：浅色主题下灰色说明文字对比度约 3:1；`600×440` 下保存后底栏出现两行状态（「设置已保存」+「鼠标监听已关闭」）时，左侧窄导航就会出现紧贴图标的竖向滚动条（并非异常态）；底栏在只读态下（带只读横幅文字）约占窗口高度的 30%。
8. **英文导航最长项在中文系统字体下的轻微截断**：`IrisNavWidth` 按 A.5 调宽至 `212` 后，英文 8 个导航项中 7 项已完全不截断，仅 `Text recognition (OCR)` 在中文 Windows 默认字体度量下仍略截为 `Text recognition (OC...`（约差 10px，悬停 `ToolTip` 显示全文）。
9. **文档与既有代码历史差异（只读不改）**：设计文档写「四个预设」而 `SettingsViewModel.AvailablePresets` 实际有 10 个；`ARCHITECTURE.md:287, 1098` 提及 `NavigationView` 与自绘 `Border` 导航实现不符；`tasks/current_task.md` 状态行仍停在阶段 0 完成。

### 8.9 实施过程协调裁决汇总（SDD Ledger Rulings）

1. **Task 12 首轮直接派发 `sdd-escalation-implementer`**：因计划明文标定 Task 12 为全阶段改动最大、跨文件耦合最高的任务，直接启用高阶推理席位一次跑通全部 4 个新测试类、用例 7 重写与 600+ 行 XAML/C# 改造（若判断失误仅多耗单任务推理成本，无代码副作用）。
2. **终审 `Declined to judge` 两项裁决**：① 切主题约 20–25 ms 中间态按计划 N1 归入 8.8 遗留问题；② 翻译窗口 C9 四看点与三窗真实亚克力底色对照归入 Task 14 便携真机取证并已完成核验。
3. **评审修复轮（A.1–A.4、B.1–B.7、C）**：已按评审意见逐项完成代码修复（各配先红后绿 TDD 测试并通过 `sdd-reviewer` 审查）、补齐深色 DWM 读数与三窗/失焦/历史页/U6 真机取证并刷新全量验证；下一阶段按工单顺序为 **阶段 3 = 翻译弹窗**，**阶段 4 = 历史窗口**。

**请求评审。**
