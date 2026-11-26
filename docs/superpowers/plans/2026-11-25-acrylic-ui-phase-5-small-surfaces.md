# T-041 阶段 5（划词图标、托盘菜单、更新失败窗口、截图遮罩）实施计划

> **For agentic workers:** 按工作流控制器（Controller）在当前分支 `codex/t041-acrylic-ui` 顺序派发 `sdd-implementer` 执行 TDD 实现（严禁子代理 `git commit`），每任务完成后按路径 `git add` 暂存并派发 `sdd-reviewer`（必要时走修复轮与 `sdd-rereviewer`），全部完成后走 `sdd-final-reviewer`、全量构建/测试/本地化审计与远程测试机便携副本验证；阶段 5 全部完成并获用户确认后再在 `codex/t041-acrylic-ui` 执行单次本地提交（不 `push`）。

**Goal:** 按已批准设计文档 §6.2–§6.5、§8、§9 与 `small-surfaces-v2.html` 样张，完成 T-041 最后 4 个小界面的 Fluent / Windows 11 视觉收官：
1. **划词图标**（`SelectionIconWindow`）：24×24 DIP 透明无框窗口 + 圆角不透明卡片底色 + 细边框 + 强调色翻译图标（`Translate16`），四角非零透明填充防止点击穿透；
2. **托盘菜单**（`TrayIconController.CreateContextMenu`）：为每项 `MenuItem` 添加对应 `SymbolIcon`（全 BMP 码位 `<= 0xFFFF`），顺序、文字、事件与行为零改动；
3. **更新失败窗口**（`UpdateRecoveryWindow`）：保持纯色背景与 `StackPanel -> WrapPanel -> Button` 既有结构，调优间距、清理提示次要文字色与按钮样式，严格保证全 `SolidColorBrush` 且全部对比度 `>= 4.5`；
4. **截图遮罩**（`ScreenshotOverlayWindow`）：保持不透明窗口与放大镜规格（15×15 → 120×120），三个标签改为圆角矩形（半径随 DPI 缩放），4 处内边距收敛为统一常量，选区边框与放大镜中心方块改用进入截图时取一次的系统强调色（不随 Iris 主题变化），中心方块加白色外圈、放大镜加细边框。

**Tech Stack:** C# / .NET 10 / WPF / WPF-UI 4.3.0 / xUnit v3 + Shouldly + NSubstitute。

**验证策略（零本机弹窗）：**
- 本机纯 CLI 运行：`dotnet build Iris.slnx -c Release`（0 警告、0 错误）、6 个非 WPF 测试程序集、`powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/audit-localization.ps1`（exit 0、`resourceErrors: []`）。
- 远程测试机交互会话运行：完整 `Iris.App.Tests.exe`、便携副本界面截图与划词图标四角点击验证（结束后清理远端临时目录与计划任务，并在完成报告中如实列出传输文件清单与跨机运行说明）。

**基线与红线：**
- 当前分支 `codex/t041-acrylic-ui` `HEAD = b35f584`（阶段 4 提交）。
- 不碰 `specs/`、`tasks/BACKLOG.md`、`.codex/`；不新增 NuGet 包、不升级 WPF-UI。
- `src/` 内零新增中文字面量与中文注释，既有中文注释（`TrayIconController.cs:17-19, 145-149`）一个字都不改、不挪位置。
- 不 `push` / `reset` / `clean` / `rebase` / `amend`。

---

## 1. 现状核查与图标 BMP 码位核验

1. **`src/Iris.App/Selection/SelectionIconWindow.cs`（§6.2）**：
   - 当前：`Window`（`24×24`、`WindowStyle.None`、`ShowActivated = false`、`ShowInTaskbar = false`、`Focusable = false`、`WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE (3)`），未设 `AllowsTransparency`，`TranslateButton` 显示 `AppResources.Get("Text_8a6cb14e2f9a")`（“译” / “T”），无中文注释。
   - 测试依赖：`MouseRenderingTests.cs` 断言 `ShowActivated=false`、`ShowInTaskbar=false`、`WindowMessage(WM_MOUSEACTIVATE)==3`、Light/Dark 下 `TextFillColorPrimaryBrush` 亮度、渲染 `24×24` PNG；`MouseInteractionTests.cs` 与 `MousePartialShowFailureTests.cs` 触发 `TranslateButton.Click`。
2. **`src/Iris.App/Tray/TrayIconController.cs`（§6.3）**：
   - `CreateContextMenu()`（L248-348）共 8 个正式菜单项 + 2 个分隔线（以及 `#if DEBUG` 下 3 个调试项 + 1 个分隔线），当前均无 `Icon`；文件含 2 处既有中文注释（L17-19、L145-149），严禁触碰。
   - **已核验全部托盘与划词图标为 WPF-UI 4.3.0 BMP 码位（`<= 0xFFFF`，已避开 `Screenshot16 = 0xF0412` 截断坑）**：
     - 划词图标：`SymbolRegular.Translate16` (`0x0F832`)
     - 输入翻译：`SymbolRegular.Translate16` (`0x0F832`)
     - 剪贴板翻译：`SymbolRegular.ClipboardTextLtr16` (`0x0F0C0`)
     - 截图翻译：`SymbolRegular.Screenshot20` (`0x0F68D`, `FontSize = 15`)
     - 截图 OCR：`SymbolRegular.ScanText16` (`0x0F08C`)
     - 静默截图 OCR：`SymbolRegular.Camera16` (`0x0E299`)
     - 设置…：`SymbolRegular.Settings16` (`0x0F6A8`)
     - 翻译历史：`SymbolRegular.History16` (`0x0E705`)
     - 退出：`SymbolRegular.Power20` (`0x0F60E`, `FontSize = 15`)
     - `#if DEBUG` 调试项：`Bug16` (`0x0E205`)、`Bug16` (`0x0E205`)、`WeatherMoon16` (`0x0EE44`)
   - 测试依赖：`TrayIconControllerTests.cs`、`TrayIconControllerSettingsTests.cs`、`HistoryWindowTests.cs`、`LocalizationRenderingTests.cs`（`MenusAndNotifications` 渲染浅/深托盘菜单截图）。
3. **`src/Iris.App/Updates/UpdateRecoveryWindow.cs`（§6.4）**：
   - 纯 C# 构建的 `Window`（`540` 宽、`SizeToContent.Height`、`SolidBackgroundFillColorBaseBrush`、`TextFillColorPrimaryBrush`），`Content` 为 `StackPanel`，直接包含 3 个 `TextBlock`（标题、`_detail`、`_cleanup`）和 1 个 `WrapPanel`（内含 `_retry`、`Primary` 与 `_cancel`），无中文注释。
   - 测试强契约：`UpdateRecoveryRenderingTests.cs`（8 组：zh-CN/en-US × 540/380 × Light/Dark）强断言 `(StackPanel)window.Content`、`panel.Children.OfType<WrapPanel>().Single()`、全部画刷均为 `SolidColorBrush`、正文与按钮文字对比度均 `>= 4.5`、`SetBusy(true/false)` 切换按钮启用态。
4. **`src/Iris.App/Capture/ScreenshotOverlayWindow.cs`（§6.5）**：
   - 不透明 `Window`（`Background = Brushes.Black`，未设 `AllowsTransparency`），`ScreenshotCanvas.OnRender` 按 `1 / scale` 变换到物理像素绘制冻结帧、半透明遮罩、选区边框（当前硬编码 `Brushes.DeepSkyBlue`）、放大镜（`124×124` 外框、`120×120` 采样图、中心 `8×8` 硬编码 `Brushes.DeepSkyBlue`，无白色外圈与细边框）及 3 个直角标签（`hint`、`size`、`coords`），4 处内边距分散写成 `12 * scale` / `8 * scale` / `6 * scale` / `4 * scale`（L194, L202, L203, L227），无中文注释。
   - 测试强契约：`ScreenshotR1RenderingTests.cs`（DPI 1/2）通过 `VisualTreeHelper.GetDrawing` 过滤不透明 `GeometryDrawing`（`SolidColorBrush`, `A == 255 && Opacity == 1`）并排除包含 15×15 `ImageDrawing` 的放大镜外框，断言 `opaqueLabels` 间 `overlappingPairs == 0`；并反射校验 `_pointer` 字段。

---

## 2. 阶段 5 关键裁决（P5-1 至 P5-5）

1. **P5-1（划词图标 §6.2：透明窗口 + 防点击穿透 + 不透明卡片底色 + 旧资源键留存）**：
   - 在 `SelectionIconWindow` 构造函数中（窗口句柄创建之前）设置 `WindowStyle = WindowStyle.None; AllowsTransparency = true;` 与 `Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));`（alpha = 1/255，视觉不可见但 Win32 命中测试不穿透四角）。
   - `TranslateButton` 仍为 `System.Windows.Controls.Button`（`24×24`，`Focusable = false`，`IsTabStop = false`，`ToolTip = UiText.MouseIconTranslate`，`AutomationProperties.Name = UiText.MouseIconTranslate`），通过自定义 `ControlTemplate` 渲染外层 `24×24` 非零透明点击区 + 内层圆角 `Border`（`CornerRadius = 6`，`BorderThickness = 1`，不透明卡片底色绑定 `SolidBackgroundFillColorQuarternaryBrush`——Light `#FFFFFFFF` / Dark `#FF2C2C2C` 均 `A == 255`，细边框绑定 `ControlStrokeColorDefaultBrush`，悬停微调底色），`Content` 设为 `Wpf.Ui.Controls.SymbolIcon`（`SymbolRegular.Translate16`, `FontSize = 15`，前景色绑定 `AccentTextFillColorPrimaryBrush`）。无阴影。
   - **回退兜底**：按设计文档 §11 风险约定，若 `AllowsTransparency` 在 `MouseRenderingTests` 或远程测试机实测中触发异常或点击穿透，即刻报告并退回不透明直角方块（仅保留新底色与翻译图标）。
   - 不再在界面引用 `Text_8a6cb14e2f9a`（“译”/“T”），沿用阶段 2/3 惯例保留 `.resx` / `app-catalog.json` 中的该旧键不删，避免跨阶段目录波动，写入完成报告偏差说明。
2. **P5-2（托盘菜单 §6.3：统一辅助方法创建 `SymbolIcon`）**：
   - 在 `TrayIconController.CreateContextMenu()` 内新增轻量私有辅助方法 `CreateMenuIcon(SymbolRegular symbol, double fontSize = 15)`，创建 `new SymbolIcon { Symbol = symbol, FontSize = fontSize }` 赋给每个 `MenuItem.Icon`（含 `#if DEBUG` 项）。不改菜单顺序、文案、`Separator` 或点击事件。
3. **P5-3（更新失败窗口 §6.4：保持纯色结构与 >= 4.5 对比度）**：
   - 保持 `Window` + `SolidBackgroundFillColorBaseBrush` + `StackPanel` -> `WrapPanel` -> `Button` 既有层级；不引入亚克力、不使用 `TextFillColorTertiaryBrush`。
   - 调优间距与视觉层次：根 `StackPanel` 边距 `24, 16, 24, 24`；标题 `FontSize = 20`、`SemiBold`、`Margin(0, 0, 0, 12)`；`_detail` `FontSize = 14`、`Margin(0, 0, 0, 12)`；`_cleanup` `FontSize = 13`、前景色绑定 `TextFillColorSecondaryBrush`（浅色与深色下对纯色底对比度均 `> 5.5 >= 4.5`，由 `UpdateRecoveryRenderingTests` 8 组自动数学验证）、`Margin(0, 0, 0, 20)`；按钮 `Padding(16, 6, 16, 6)`、`MinHeight = 32`、`_retry` 保持 `ControlAppearance.Primary`。
4. **P5-4（截图遮罩 §6.5：单次系统强调色 + 圆角标签 + 统一内边距常量 + `ScreenshotR1RenderingTests` 兼容）**：
   - 系统强调色获取：新增静态辅助 `ResolveSystemAccentColor()` 读取 `SystemParameters.WindowGlassColor`，强制不透明（`Color.FromRgb(c.R, c.G, c.B)`），若获取异常或全黑/透明则回退 `#0078D4`；在进入截图构造 `ScreenshotCanvas` 时**只取一次**并冻结为 `_accentPen`（1px），不随 Iris 主题切换变化。
   - 内边距与圆角常量：将 4 处内联内边距（原 `12 * scale` / `8 * scale` / `6 * scale` / `4 * scale`）收敛为单一常量组（`LabelPaddingX = 8d`, `LabelPaddingY = 5d`, `LabelCornerRadius = 4d`，宽/高增量统一由 `LabelPaddingX * 2 * scale`、`LabelPaddingY * 2 * scale` 计算，绘制偏移统一为 `(x + LabelPaddingX * scale, y + LabelPaddingY * scale)`），标签改用 `dc.DrawRoundedRectangle(chrome, null, ..., radius, radius)`（`radius = LabelCornerRadius * scale`）。
   - 放大镜视觉增强（零破坏 `ScreenshotR1RenderingTests` 的 `opaqueLabels=3` 断言）：
     - 放大镜外框：`dc.DrawRoundedRectangle(chrome, magnifierBorderPen, new Rect(mx, my, 124, 124), 4, 4)`（细边框 `Pen` 使用半透明中性色 `Color.FromArgb(153, 128, 128, 128)`，外框仍包含 15×15 `ImageDrawing`）；
     - 中心方块：先绘制 1px 白色外圈 `dc.DrawRectangle(null, whiteOuterPen, new Rect(mx + 57, my + 57, 10, 10))`，再绘制 1px 系统强调色内框 `dc.DrawRectangle(null, _accentPen, new Rect(mx + 58, my + 58, 8, 8))`（均 `Brush = null`，不新增不透明填充 `GeometryDrawing`）。
5. **P5-5（既有测试零破坏 + 按 §8.2 补齐 4 个小界面专项测试）**：
   - 不删改任何既有测试断言（设计文档 §8.1 阶段 5 无需改既有测试）；新增针对 4 个小界面的单元/UI 测试覆盖 §6.2–§6.5 与 §8.2 的每一条新增契约。

---

## 3. 逐任务实施清单（共 6 个任务）

### Task 1: 划词图标重构（§6.2，`SelectionIconWindow`）与单元测试（§8.2）
- **Files:**
  - Modify: `src/Iris.App/Selection/SelectionIconWindow.cs`
  - Create/Modify: `tests/Iris.App.Tests/Selection/SelectionIconWindowTests.cs`
- **Steps:**
  1. **RED**：新增 `SelectionIconWindowTests.cs`，验证：`Width == 24`、`Height == 24`、`AllowsTransparency == true`、`WindowStyle == WindowStyle.None`、`ShowActivated == false`、`ShowInTaskbar == false`、窗口背景为非零近透明 `SolidColorBrush`（`A == 1`）、`TranslateButton` 为 `Button` 且保留 `ToolTip == UiText.MouseIconTranslate`、`AutomationProperties.Name == UiText.MouseIconTranslate`、`Title == UiText.MouseIconTranslate`、`Content` 为 `SymbolIcon(Translate16)` 而非“译/T”文本、在 Light/Dark 主题下圆角卡片底色不透明（`A == 255`）且无投影 `Effect == null`，以及 `Click` 正常触发 `Clicked`。
  2. **GREEN**：按 P5-1 重构 `SelectionIconWindow.cs`（零中文注释）。
  3. **Verify**：本机编译 + 本地化审计 + 远程测试机跑 `Selection` 相关测试（含 `MouseRenderingTests`、`MouseInteractionTests`、`SelectionIconWindowTests`）。
  4. **Stage & Review**：按路径暂存 -> `sdd-reviewer` -> `STATUS.md` & `checkpoint.sh`。

### Task 2: 托盘菜单图标（§6.3，`TrayIconController`）与测试
- **Files:**
  - Modify: `src/Iris.App/Tray/TrayIconController.cs`
  - Modify: `tests/Iris.App.Tests/Tray/TrayIconControllerTests.cs`
- **Steps:**
  1. **RED**：在 `TrayIconControllerTests.cs` 新增测试验证 `CreateContextMenu()` 返回的全部 `MenuItem` 均设置了非空 `Wpf.Ui.Controls.SymbolIcon` 且码位均为 BMP（`<= 0xFFFF`），同时原菜单顺序、文字、`Separator` 位置不变。
  2. **GREEN**：按 P5-2 在 `TrayIconController.cs` 的 `CreateContextMenu()` 为所有 `MenuItem` 添加 `SymbolIcon`（严禁触碰 L17-19、L145-149 既有中文注释）。
  3. **Verify**：本机编译 + 本地化审计 + 远程测试机跑 `TrayIconControllerTests`、`TrayIconControllerSettingsTests`、`LocalizationRenderingTests`（`MenusAndNotifications`）。
  4. **Stage & Review**：按路径暂存 -> `sdd-reviewer` -> `STATUS.md` & `checkpoint.sh`。

### Task 3: 更新失败窗口视觉微调（§6.4，`UpdateRecoveryWindow`）与对比度验证
- **Files:**
  - Modify: `src/Iris.App/Updates/UpdateRecoveryWindow.cs`
  - Modify: `tests/Iris.App.Tests/Updates/UpdateRecoveryRenderingTests.cs`（仅检查是否需补一条非 `TextFillColorTertiaryBrush` / 按钮样式断言，不削弱原 8 组强断言）
- **Steps:**
  1. 按 P5-3 调整 `UpdateRecoveryWindow.cs` 间距、`_cleanup` 前景色（`TextFillColorSecondaryBrush`）与按钮内边距/最小高度（保持 `StackPanel -> WrapPanel -> Button` 结构与纯色背景）。
  2. 确认所有画刷仍为 `SolidColorBrush` 且 `UpdateRecoveryRenderingTests` 全部 8 组（zh-CN/en-US × 540/380 × Light/Dark）对比度 `>= 4.5` 通过。
  3. **Stage & Review**：按路径暂存 -> `sdd-reviewer` -> `STATUS.md` & `checkpoint.sh`。

### Task 4: 截图遮罩圆角标签、统一内边距常量与系统强调色（§6.5，`ScreenshotOverlayWindow`）与测试
- **Files:**
  - Modify: `src/Iris.App/Capture/ScreenshotOverlayWindow.cs`
  - Create/Modify: `tests/Iris.App.Tests/Capture/ScreenshotOverlayStyleTests.cs`
- **Steps:**
  1. **RED**：新增 `ScreenshotOverlayStyleTests.cs`，验证：
     - 窗口仍不透明（`AllowsTransparency == false`、`Background == Brushes.Black`）；
     - `OnRender` 绘制的 3 个标签为圆角矩形（`RectangleGeometry` 且 `RadiusX == 4 * scale`、`RadiusY == 4 * scale`），在 DPI 1 与 DPI 2 下半径随 DPI 缩放，底色随 Light/Dark 切换为白/黑；
     - 选区边框与放大镜中心方块使用相同的系统强调色画刷且不随 Light/Dark 切换变化；
     - 放大镜中心方块具备白色外圈、放大镜外框具备细边框。
  2. **GREEN**：按 P5-4 修改 `ScreenshotOverlayWindow.cs`（提取单一内边距常量消除 4 处重复、单次采样系统强调色、圆角标签、放大镜细边框与中心白圈，零中文注释）。
  3. **Verify**：本机编译 + 本地化审计 + 远程测试机跑全量 `Capture` 测试（含 `ScreenshotR1RenderingTests` 确认 `opaqueLabels=3 overlappingPairs=0`、`ScreenshotRenderingTests`、`ScreenshotOverlayStyleTests`）。
  4. **Stage & Review**：按路径暂存 -> `sdd-reviewer` -> `STATUS.md` & `checkpoint.sh`。

### Task 5: 全量构建、7 程序集回归、`sdd-final-reviewer` 整阶段终审与本地化审计
- **Steps:**
  1. 本机纯 CLI 跑 `dotnet build Iris.slnx -c Release`（0 警告、0 错误）、6 个非 WPF 程序集测试与 `build/audit-localization.ps1`（exit 0）。
  2. 生成 `phase5-full.diff` 并派发 `sdd-final-reviewer` 审查 4 个小界面与全部新增测试（若有问题走修复轮 + `sdd-rereviewer` 闭环）。
  3. 远程测试机交互会话运行完整 `Iris.App.Tests.exe`（全绿，零本机弹窗）。

### Task 6: 远程测试机便携副本验证（含划词图标四角点击与 4 界面截图复看）、完成报告与暂存
- **Steps:**
  1. 传便携副本/测试程序到远程测试机 `C:\Temp\iris-p5-test\`，捕获并亲自视觉复看：
     - 划词图标（浅/深、以及真实鼠标点击圆角外 4 个角像素位置验证 `Clicked` 触发不穿透！）；
     - 托盘菜单（浅/深，验证全部 8 项图标清晰无乱码）；
     - 更新失败窗口（由 `UpdateRecoveryRenderingTests` 输出浅/深 × 中/英 PNG 并复看）；
     - 截图遮罩（由 `ScreenshotRenderingTests` / `ScreenshotR1RenderingTests` 输出浅/深 × DPI 1/2 PNG 并复看圆角标签、系统强调色选区与放大镜白圈/细边）。
  2. 清理远程测试机全部临时目录与计划任务，验证无残留进程。
  3. 按 `GEMINI.md` 撰写本文档第 5 节《阶段 5 完成报告》（如实写明跨机测试、远程传输清单与清理结果、`Text_8a6cb14e2f9a` 键留存偏差、以及未在本机实测项），将全部改动含本计划文档暂存，提交前呈交用户确认。

---

## 4. 预计修改的既有测试与偏差/风险预登记

- **预计修改的既有测试**：0 处破坏性修改（仅在 `TrayIconControllerTests.cs` 追加图标断言；`MouseRenderingTests`、`UpdateRecoveryRenderingTests`、`ScreenshotR1RenderingTests`、`ScreenshotRenderingTests`、`LocalizationRenderingTests` 均保持原断言通过，离屏渲染哈希按 §8.1 变化并人工复看 PNG）。
- **偏差预登记**：
  - P5-1：保留已无界面使用者的旧资源键 `Text_8a6cb14e2f9a`（“译”/“T”），与阶段 2/3 策略一致。
- **已知风险**：
  - `SelectionIconWindow` 启用 `AllowsTransparency = true` 后需在 `MouseRenderingTests`（`ApplicationThemeManager.Apply(icon)`）与远程测试机实测四角真实鼠标点击；若有兼容或穿透异常，按 §11 退回不透明直角方块方案。

---

## 5. 阶段 5 完成报告

- **日期**：2026-11-26
- **工作分支**：`codex/t041-acrylic-ui`（当前 `HEAD` 仍为阶段 4 提交 `b35f584`，阶段 5 全部 9 个改动文件含本计划文档已按路径暂存，等待用户确认后再做单次本地提交；不 `push`）
- **改动范围（9 个文件）**：
  - `docs/superpowers/plans/2026-11-25-acrylic-ui-phase-5-small-surfaces.md`（阶段 5 实施计划与完成报告）
  - `src/Iris.App/Selection/SelectionIconWindow.cs`（§6.2：`AllowsTransparency = true`、非零近透明 `Color.FromArgb(1, 0, 0, 0)` 点击面 + `BackgroundProperty.OverrideMetadata` 强制保持防止 `ApplicationThemeManager.Apply` 覆盖、`CornerRadius="6"` 不透明卡片底色 `SolidBackgroundFillColorQuarternaryBrush` + 细边框 + 强调色 `Translate16` `FontSize=15` 图标、无阴影、不再显示“译/T”）
  - `src/Iris.App/Tray/TrayIconController.cs`（§6.3：在 `CreateContextMenu()` 与 `CreateScreenshotItem()` 中为全部菜单项添加 BMP 码位 `SymbolIcon`，菜单顺序、文字、分隔线与事件处理器不变，原有 L17–19、L145–149 中文注释原封不动）
  - `src/Iris.App/Updates/UpdateRecoveryWindow.cs`（§6.4：保持纯色背景与 `StackPanel -> WrapPanel -> Button` 既有结构，优化间距、`_cleanup` 改用 `TextFillColorSecondaryBrush`、按钮内边距 `16,6` 与最小高度 `32`，全 `SolidColorBrush` 且全部对比度 `>= 4.5`；另按 §5.2 本机验证发现，暗色下原生标题栏随主题 —— `SourceInitialized` 时调用 `DwmSetWindowAttribute(hwnd, 20, ...)`）
  - `src/Iris.App/Capture/ScreenshotOverlayWindow.cs`（§6.5：提取 `LabelPaddingX=8` / `LabelPaddingY=5` / `LabelCornerRadius=4` 统一替换 4 处内联内边距，三个标签改圆角矩形且半径随 DPI 缩放、底色仍随 Iris 主题，选区边框与放大镜中心方块改用进入截图时单次读取的系统强调色 `ResolveSystemAccentColor()`、不随 Iris 主题变化，中心方块加 1px 白色外圈、放大镜加细边框）
  - `tests/Iris.App.Tests/Selection/SelectionIconWindowTests.cs`（新增划词图标透明防穿透底色、`ApplicationThemeManager.Apply` 后 `Color.A == 1` 保持、`Translate16` `FontSize=15`、浅/深不透明圆角卡片与点击测试）
  - `tests/Iris.App.Tests/Tray/TrayIconControllerTests.cs`（新增 `ContextMenu_AllMenuItems_HaveBmpSymbolIcons` 校验全部菜单项图标非空且码位 `<= 0xFFFF`）
  - `tests/Iris.App.Tests/Updates/UpdateRecoveryRenderingTests.cs`（保留原 8 组强断言并新增 `ActualHeight >= 32` 校验）
  - `tests/Iris.App.Tests/Capture/ScreenshotOverlayStyleTests.cs`（新增不透明窗口、DPI 缩放圆角标签、浅/深主题标签色、系统强调色一致性、放大镜细边框与 `10×10` 白色外圈测试）

### 5.1 原始构建、全量 7 程序集回归、本地化审计与启动/退出测量输出

> **运行说明**：最终版本的全量 7 个测试程序集（含 `Iris.App.Tests` 1119 条）已**在本机桌面**完整运行；阶段内另有一次远程测试机（Windows 10.0.26100.33438，.NET 10.0.14）对照运行，仅作补充证据，最终读数以本机为准。

1. **`dotnet build Iris.slnx -c Release --no-incremental`**（本机，最终修复后全量重建；`BUILD_EXIT=0`）：`已成功生成。 0 个警告 0 个错误 已用时间 00:00:23.98`
```text
已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:04.11
```

2. **全量 7 个测试程序集汇总**（本机 `dotnet test Iris.slnx -c Release --no-build`，`TEST_EXIT=0`；总计 `4024`，失败 `0`，成功 `4022`，已跳过 `2`）：
```text
测试运行摘要: 已通过!
  F:\Iris\tests\Iris.Providers.Tests\bin\Release\net10.0\Iris.Providers.Tests.dll (net10.0|x64) 已通过 (3s 618ms)
  F:\Iris\tests\Iris.Infrastructure.Tests\bin\Release\net10.0\Iris.Infrastructure.Tests.dll (net10.0|x64) 已通过 (7s 205ms)
  F:\Iris\tests\Iris.Platform.Windows.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Platform.Windows.Tests.dll (net10.0|x64) 已通过 (3s 817ms)
  F:\Iris\tests\Iris.Core.Tests\bin\Release\net10.0\Iris.Core.Tests.dll (net10.0|x64) 已通过 (8s 423ms)
  F:\Iris\tests\Iris.App.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.App.Tests.dll (net10.0|x64) 已通过 (2m 39s 920ms)
  F:\Iris\tests\Iris.Maintenance.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Maintenance.Tests.dll (net10.0|x64) 已通过 (4s 728ms)
  F:\Iris\tests\Iris.ArchitectureTests\bin\Release\net10.0-windows10.0.19041.0\Iris.ArchitectureTests.dll (net10.0|x64) 已通过 (1s 706ms)

  总计: 4024
  失败: 0
  成功: 4022
  已跳过: 2
  - 持续时间：`2m 41s 049ms`
```
   - 2 条跳过均为既有环境限制（`[ENVIRONMENT LIMITATION] ... Window station creation failed or already exists (station=0, error=183)`）：`ClipboardSnapshotIsolatedTests.SnapshotHelper_UsesExclusiveNonInteractiveStation_AndJoinsOwnedProcess` 与 `Win32ClipboardIsolatedIntegrationTests.IsolatedWindowStation_NativeClipboardIntegration_ExecutesInDedicatedProcess`，与阶段 1–4 的跳过项相同。
   - 远程测试机对照运行（同一分支、标题栏修复之前）：`Iris.App.Tests  Total: 1119, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 307.797s`（`APP_TESTS_EXIT=0`）；最终修复已由上述本机全量运行覆盖。
   - 较阶段 4 的 4017 条通过净增 5 条新测试；其中 `ScreenshotR1RenderingTests` 实测 `LABEL_LAYOUT opaqueLabels=3 overlappingPairs=0`，`UpdateRecoveryRenderingTests` 8 组浅/深 × 中/英 × 540/380 对比度全部 `>= 4.5`。

3. **`powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/audit-localization.ps1`**（本机）：
```text
AUDIT_EXIT=0
Scanned C# and XAML; Chinese entries including comments: 1168
Compiled resource values matched after XML newline normalization; composite formats parsed: 1240
{"sourceEntries": 1168, "commentEntries": 950, "reviewedNonUiEntries": 218, "resourceCounts": {"App": 467, "Core": 148, "Maintenance": 5}, "resourceErrors": [], "englishNativeNameExceptions": {"App:LanguageChineseNative": "简体中文"}, "exceptionManifestSha256": "2d620105575400696d1f567460d883cf050ff511b49b6f64d5ea0b5192eb9a75"}
```

4. **`powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/measure-startup.ps1`**（本机；脚本自动把 Release 产物拷到 `%TEMP%` 便携沙箱，3 轮 × 30 秒静置，`MEASURE_EXIT=0`）：
```text
Iris measure-startup | 2026-11-26 03:28:46 | Microsoft Windows NT 10.0.26300.0 | Configuration=Release Runs=3 IdleSeconds=30
System CPU load at start: 24% (startup-time NFR is only meaningful below 30%)
Binary: F:\Iris\src\Iris.App\bin\Release\net10.0-windows10.0.19041.0 (built 2026-11-26 03:02:52)
--- run 1: log lines written during logoff simulation ---
2026-11-26 03:29:19.109 +08:00 [INF] (Iris.App.App) Windows 会话即将结束（"Logoff"）
2026-11-26 03:29:19.127 +08:00 [INF] (Iris.App.App) Iris 已退出，退出码 0
2026-11-26 03:29:19.150 +08:00 [FTL] (Iris.App.Program) Primary startup or cleanup failed: IrisException
--- second instance (run 1) ---
ExitedWithin5s : True
ElapsedMs      : 147
ExitCode       : 0
LogLinesAdded  : 0
--- per run ---
Run StartupMs VisibleWindows WorkingSetMB PrivateBytesMB ExitCode ShutdownLogLines
--- --------- -------------- ------------ -------------- -------- ----------------
  1     940.2              1        169.2           82.1 timeout                 3
  2     941.4              0          125           54.5 1                       3
  3     974.9              0        124.9           54.5 timeout                 3
--- averages --- StartupMs=952.2  WorkingSetMB=139.7  PrivateBytesMB=63.7
--- NFR (ARCH 1.2): StartupMs <= 1500; PrivateBytesMB <= 100; WorkingSetMB <= 160; VisibleWindows = 0; ExitCode = 0; ShutdownLogLines >= 1 ---
```
   - 3 轮均值 `StartupMs=952.2 ms`、`WorkingSetMB=139.7 MB`、`PrivateBytesMB=63.7 MB`；较阶段 3 本机基线（`StartupMs=901.6` / `WorkingSetMB=151.8` / `PrivateBytesMB=72.8`，来自其原始输出）启动 `+50.6 ms`、工作集 `-12.1 MB`、私有字节 `-9.1 MB`；二次实例 `147 ms` 退出码 `0`。
   - 每轮 `ExitCode` 的 `timeout`/`1` 与 `[FTL] ... IrisException` 行属于本机测量脚本在注销模拟下的既有读数：阶段 1 完成报告的改动前后两份测量（基线 `HEAD 513ab5d` 与工作区）每轮同样为 `timeout`；阶段 2 实施前基线（`2026-11-24 21:29:21`）与阶段 3 本机测量（`2026-11-25 13:30:45`、`13:38:43`）也各有同一 `FTL` 行。阶段 5 未改启动/退出路径，该现象不是本阶段引入；本工单只如实报告，不在 UI 范围内修复，列入遗留。
   - 第 1 轮 `WorkingSetMB=169.2 MB` 高于 `160 MB` 阈值（第 1 轮含第二实例探测，阶段 1–4 同一轮读数同样超标），属 T-040 既有范围，不属本工单。

### 5.2 审查闭环、远程测试机真机验证（含划词图标四角真实点击）与清理说明

- **SDD 终审闭环**：`sdd-final-reviewer` 指出 3 项问题（补 `SelectionIconWindow` 图标 `FontSize = 15`、修复 `SelectionIconWindowTests` 与 `ScreenshotOverlayStyleTests` 测试夹具，以及提示校验 `ApplicationThemeManager.Apply` 对 `SelectionIconWindow.Background` 的覆写风险），在远程测试机实测中**坐实了 `ApplicationThemeManager.Apply` 会把作为 `MainWindow` 的 `SelectionIconWindow.Background` 覆写为不透明（`A = 255`）的生产缺陷**；已在 `SelectionIconWindow` 静态构造函数中通过 `BackgroundProperty.OverrideMetadata(..., static (_, _) => HitTestBrush)` 强制保持 `Color.FromArgb(1, 0, 0, 0)`，并经 `sdd-rereviewer` 确认 `all_addressed` 且 `SelectionIconWindowTests` 在远程测试机实测通过。
- **划词图标四角真实 Win32 点击不穿透验证（远程测试机交互会话，`P5Harness.exe`）**：
  - 将真实的 `SelectionIconWindow`（装配真实 `SelectionIconNative` + `SelectionIconSystem` + `NativeWindowPlacementAdapter`）显示在 `(200, 200)`（`24×24`，`AllowsTransparency=True`），底层放置监听 `MouseDown` 的背景窗体，对圆角外的 4 个角落像素与中心发送真实 Win32 `SetCursorPos` + `mouse_event` 点击：
```text
HARNESS_EXIT=0
SELECTION_ICON_RECT=(200,200,24x24) AllowsTransparency=True
CLICK_TopLeftCorner=(201,201) iconDelta=1 bgClickThrough=0
CLICK_TopRightCorner=(222,201) iconDelta=1 bgClickThrough=0
CLICK_BottomLeftCorner=(201,222) iconDelta=1 bgClickThrough=0
CLICK_BottomRightCorner=(222,222) iconDelta=1 bgClickThrough=0
CLICK_Center=(212,212) iconDelta=1 bgClickThrough=0
SELECTION_ICON_TOTAL_CLICKS=5/5 bgClickThroughTotal=0
```
- **4 个小界面截图视觉复看（拉回本地临时证据目录并逐张打开复看）**：
  - **划词图标**：`live-selection-icon-light.png`、`live-selection-icon-dark.png`（4× 放大确认 6px 圆角不透明卡片、细边框、强调色 `Translate16` 图标、无阴影、四角无实色边角）；
  - **托盘菜单**：`zh-CN-light-normal-tray-menu.png`、`zh-CN-dark-normal-tray-menu.png`、`en-US-light-normal-tray-menu.png`（8 个正式菜单项图标清晰无乱码、顺序与分隔线不变）；
  - **更新失败窗口**：`live-update-recovery-zh-CN-Light.png`、`live-update-recovery-zh-CN-Dark.png`、`live-update-recovery-en-US-Light.png`、`live-update-recovery-en-US-Dark.png`（纯色底、间距层次与按钮样式对齐 `small-surfaces-v2.html`，全 8 组对比度自动化断言 `>= 4.5`；远程测试机暗色抓图中的浅色原生标题栏已在本机用真实 `ThemeService` 复现并修复，见下条本机验证）；
  - **截图遮罩**：`Light-selection.png`、`Dark-selection.png`、`small-selection-labels-dpi2.png`（确认圆角标签随主题切换黑白底且 DPI 缩放、选区与放大镜中心用系统强调色、中心 1px 白色外圈、放大镜细边框、小选区无重叠）。
- **本机真实桌面观感验证（`%TEMP%` 便携副本 + 真实 `ThemeService`；Windows 11 `10.0.26300.0`；证据 `%TEMP%\iris-t041-evidence\phase5-final\local-shots`，逐张亲自打开复看）**：
  - **a 历史窗口**（`a1`–`a4`，`800×640`，浅/深 × 聚焦/失焦）：真实桌面背景下亚克力透底、圆角与阴影正常，DWM `DWMWA_SYSTEMBACKDROP_TYPE=3`；失焦实测 `TargetHwnd != ForegroundHwnd`、`Deactivated=True`，阴影与聚焦态不同；暗色标题栏像素实测 `#2D2D2D`、标题白字，无“白条”。
  - **b 划词图标**：便携副本内无法用合成鼠标触发真实划词（生产 `MouseSample.Injected` 与 `GestureRecognizer` 明确过滤注入事件），改用本机临时 harness 装配**真实** `SelectionIconWindow` + `ThemeService` + `SelectionIconNative/SelectionIconSystem/NativeWindowPlacementAdapter`，覆盖真实 `RichTextBox` 浅/深文档，`Light/Dark × 浅/深底` 4 组截图 + 4× 放大（6px 圆角半透明卡片、细边框、强调色图标）；对 `24×24` 窗口四角与中心发送真实 Win32 `SetCursorPos`+`mouse_event`：
```text
LOCAL_SELECTION_ICON_RECT=(365,248,24x24) AllowsTransparency=True
LOCAL_CLICK_TopLeft=(366,249) iconDelta=1 docClickThrough=0
LOCAL_CLICK_TopRight=(387,249) iconDelta=1 docClickThrough=0
LOCAL_CLICK_BottomLeft=(366,270) iconDelta=1 docClickThrough=0
LOCAL_CLICK_BottomRight=(387,270) iconDelta=1 docClickThrough=0
LOCAL_CLICK_Center=(377,260) iconDelta=1 docClickThrough=0
LOCAL_SELECTION_ICON_CLICKS=5/5 docClickThroughTotal=0
```
    - **白框真实根因、代码修复与交叉背景重截**：`Probe4` 证实 `SelectionIconWindow` 已显示后若触发 `ApplicationThemeManager.Apply(..., WindowBackdropType.None)`，WPF-UI 会将 `HwndSource.CompositionTarget.BackgroundColor` 从 `#00FFFFFF` 改写为 `#FFFFFFFF`（不透明纯白），导致圆角外四角出现白色方框；已先在 `SelectionIconWindowTests.cs` 补写失败断言，再于 `SelectionIconWindow.cs` 的 `BackgroundProperty` coerce 回调与 `ApplicationThemeManager.Changed` 中强制恢复 `CompositionTarget.BackgroundColor = Colors.Transparent`。修复后重截 `b2-selection-icon-zh-CN-light-on-dark-bg.png`（浅色图标压深色 `#1E1E1E` 代码文字）与 `b3-selection-icon-zh-CN-dark-on-light-bg.png`（深色图标压浅色 `#F8F8F8` 文章文字）及各自 `-zoom4x.png`，逐张复看确认四角完全透明、透出背后文字、无白框，文件名与内容一致。
  - **c 托盘菜单**：真实任务栏托盘图标右键，zh-CN 浅/深与 en-US 浅 3 组，8 个菜单项图标齐全、顺序与分隔线不变、文字无截断。
  - **d 截图遮罩**：`ScreenshotOverlayWindow.cs` 原 1px 整数坐标描边导致 WPF 50% 抗锯齿半透明（`#07416D`/`#669DC8`）；已修复为 `0.5px` 像素对齐（`SnapStrokeRect` + 放大镜中心框 `57.5/58.5`，`ScreenshotOverlayStyleTests` 3/3 通过）。改用正确的 `--action ScreenshotTranslate` 触发方式，在跨深色（`#181818`）与浅色（`#F5F5F5`）内容的桌面上全屏（`2560×1440`）重截 `d1-screenshot-overlay-zh-CN-light-selection.png` 与 `d2-screenshot-overlay-zh-CN-dark-selection.png` 并逐张打开复看：顶部提示标签、选区（`1001×461`）与右下放大镜完整清晰；像素实测 `d1`/`d2` 选区四边在深色区与浅色区上均精确为 **1 px 宽、100% 不透明系统强调色 `#006FC4`（`(0, 111, 196)`）**（选区周长精确命中 `2920` 个 `(0, 111, 196)` 像素）。
  - **e 更新失败窗口**：`UpdateRecoveryWindow.cs`（mtime `03:02:48`）早于 5 张 `e-*` 截图（`03:02:56–03:02:59`），为改后截图；浅色 `_cleanup` 使用 `TextFillColorSecondaryBrush`（`#9E000000`）叠 `#FFF3F3F3` 理论合成 `#5C5C5C`（`6.03:1`），PNG 笔画边缘采样 `#898989` 系 13px 细中文 ClearType 抗锯齿混合所致。
  - **f 三窗合拍**：`f1-settings-en-US-light-760x520.png`（英文 760 宽导航 `Text recognition (OC..` 截断已记入遗留）、`f2-translate-zh-CN-light-460x480-completed.png`；注：`f3-history-zh-CN-light-800x640-selected.png` 与 `a1` 为同一文件（SHA-256 `10fdf0b9008b6b932d14196ef2f1307b3632a8f876c7950c6c07c8434675ed1c`），f 组未单独重截历史窗口；本机截图桌面背景为深色终端，亮色/花色壁纸未实测。
  - **隔离与清理**：便携副本前后快照 `User %APPDATA%\Iris & HKCU\...\Run identical? True`、`Remaining Iris.App: 0`；全部临时目录（`%TEMP%\iris-p5-local-recovery`、`%TEMP%\iris-p5-local-selicon` 等）已删除，无残留进程。
- **远程测试机传输清单与清理确认**：
  - 传至远程测试机 `C:\Temp\iris-p5-test\` 的全部文件：`app-tests.tar.gz`（解包至 `app-tests\`）、`p5-harness.tar.gz`（由本地 `%TEMP%\iris-p5-harness` 编译的临时测试程序 `P5Harness.exe`，解包至 `harness-bin\`）、`run-p5-focused.ps1`、远程测试运行脚本、`run-p5-harness-remote.ps1`、`run-p5-final-apptests.ps1`。
  - 全部临时计划任务（`IrisP5Focused`、`IrisP5Verify`、`IrisP5Harness`、`IrisP5Tests`）已删除；验证结束后已删除远端整个 `C:\Temp\iris-p5-test\` 目录（`DirExists=False`）及本地 `%TEMP%\iris-p5-harness` 临时目录，并确认远端无残留 `Iris.App` / `Iris.App.Tests` / `P5Harness` 进程（`RemainingProcs=0`）。

### 5.3 修改的既有测试、偏差与未验证说明

- **修改的既有测试（无破坏性修改，仅追加断言）**：
  1. `tests/Iris.App.Tests/Tray/TrayIconControllerTests.cs`：新增 `ContextMenu_AllMenuItems_HaveBmpSymbolIcons`；
  2. `tests/Iris.App.Tests/Updates/UpdateRecoveryRenderingTests.cs`：在原 8 组强断言末尾追加 `buttons.ShouldAllBe(button => button.ActualHeight >= 32)`。
- **与设计/工单的偏差**：
  1. 沿用阶段 2/3 策略保留已无界面使用者的旧资源键 `Text_8a6cb14e2f9a`（“译”/“T”，位于 `Strings.resx:1116`、`Strings.en-US.resx:1116`、`app-catalog.json`），避免跨阶段资源目录波动；
  2. 阶段 5 未改变启动期常驻路径（4 个小界面均为按需创建的轻量小窗/菜单）；为满足完成报告的性能/退出证据，仍在本机运行了 `build/measure-startup.ps1`，读数见 §5.1 第 4 条。
- **已知遗留（本工单不处理，只报告）**：
  1. **本机 `measure-startup.ps1` 注销模拟下的退出读数为 `timeout`/`1` 且伴随 `[FTL] ... IrisException`**：阶段 1 起既有（基线 `HEAD 513ab5d` 与阶段 2 前、阶段 3 测量均有），不是阶段 5 引入；详见 §5.1 第 4 条；
  2. 阶段 2 遗留：切主题一帧中间态 N1、导航项不可聚焦、`IrisAcrylicRow*` 两个资源键无使用者、设计文档“四个预设”与代码 10 个预设不符、`ARCHITECTURE.md` 中 `NavigationView` 描述陈旧、`tasks/current_task.md` 状态行过期；
  3. 阶段 3 遗留：`280px` 极窄宽度下两张结果卡片标题折行位置不一致；
  4. 阶段 4 遗留：窄窗单列模式双滚动条与 `240` 高列表横切、浅色主题次要灰字约 `3:1` 与搜索框占位文字约 `2:1`、en-US 右栏操作按钮在 `800px` 宽下折成 2 行、既有资源 `1 results` 单复数问题（T-038，`Strings.en-US.resx:123`）。
- **未验证项（显式标注）**：
  1. **Windows 10 / 低版本系统纯色回退真机观感**（纯色字典路径由自动化主题测试覆盖）；
  2. **系统高对比度主题真机观感**；
  3. **真实物理鼠标在便携副本内拖选文本触发划词图标的端到端流程**（b 组为真实 `SelectionIconWindow` harness + 真实 Win32 点击，不是物理拖选）；
  4. **真实升级失败流程触发更新失败窗口**（e 组由真实窗口 harness 挂 `ThemeService` 显示，不是真实升级失败触发）；
  5. **真人鼠标拖拽窗口顶栏/四边缩放/历史列表重排手感**（自动化几何已覆盖）；
  6. **真实中文输入法 IME 组字交互**（`TextCompositionManager` 接线与 ViewModel 闸门由单元/UI 测试覆盖）；
  7. **切换 DPI（本机单显示器 100%）后亚克力保持**。
- **T-041 五阶段未验证总表**：

| 阶段 | 未验证项 | 最终状态 |
|---|---|---|
| 0 | 无运行时改动 | 无 |
| 1 | Windows 10 / 低版本纯色回退真机观感 | 仍未验证 |
| 1 | 系统高对比度主题真机观感 | 仍未验证 |
| 1 | 切 DPI 后亚克力保持 | 仍未验证 |
| 1/2 | 真实窗口截图 | 已由阶段 2–5 真机截图补验 |
| 1/2 | 非活动窗口（失焦）外观 | 已由阶段 5 本机 a2/a4 补验 |
| 1/2 | 真人鼠标拖拽/双击最大化/四边缩放/列表重排 | 仍未验证（自动化几何覆盖） |
| 2 | Windows 10 纯色回退、高对比度 | 仍未验证 |
| 3 | 真实中文输入法 IME 组字 | 仍未验证（自动化接线覆盖） |
| 4 | 亚克力观感 | 已由阶段 5 本机 a/f 组补验 |
| 4 | 失焦态外观 | 已由阶段 5 本机 a2/a4 补验 |
| 4 | 本机启动耗时 | 已由阶段 5 本机 `measure-startup.ps1` 补测（§5.1 第 4 条） |
| 4 | 本机运行 `Iris.App.Tests` | 已由阶段 5 本机全量运行补验（§5.1 第 2 条） |
| 5 | 更新失败窗口暗色原生标题栏 | 已修复并本机复验（§5.2） |
| 5 | 真实物理划词拖拽端到端 | 仍未验证（真实窗口 harness + 真实点击已验） |
| 5 | 真实升级失败流程触发 | 仍未验证 |
| 5 | 本机运行 `Iris.App.Tests` / 本机启动耗时 | 已补验（§5.1 第 2、4 条） |
