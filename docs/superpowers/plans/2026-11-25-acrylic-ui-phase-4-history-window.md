# T-041 阶段 4（翻译历史窗口）实施计划

> **For agentic workers:** 按工作流控制器（Controller）在当前分支 `codex/t041-acrylic-ui` 顺序派发 `sdd-implementer` 执行 TDD 实现（严禁子代理 `git commit`），每任务完成后按路径 `git add` 暂存并派发 `sdd-reviewer`（必要时走修复轮与 `sdd-rereviewer`），全部完成后走 `sdd-final-reviewer` 与便携副本真机验证；阶段 4 全部完成并获用户确认后再在 `codex/t041-acrylic-ui` 执行单次本地提交（不 `push`）。

**Goal:** 按已批准设计文档 §6.1、§8、§9 与 `specs/HISTORY_UI.md`（含 T-041 修订），将 `HistoryWindow` 重构为 Windows 11 亚克力左右分栏（实际宽度 `>= 640` 双栏、`< 640` 单列）布局，保持现有 `HistoryViewModel` 业务逻辑与全部测试依赖控件名称/类型不变，补齐宽/窄排布、确认条与 `320×280` 可达性测试，并完成全量构建、7 程序集测试、本地化审计、启动/内存基线对比与便携副本真机截图复看。

**Architecture:**
- 仅修改 UI 样式（`Iris.Styles.xaml`）、`HistoryWindow.xaml`、`HistoryWindow.xaml.cs` 与测试，**不修改 `HistoryViewModel.cs` / `HistoryWindowPresenter.cs` / `HistoryWindowSession.cs` 的任何业务逻辑**（完全遵守 §6.1「只用界面代码和现有属性，不改视图模型逻辑」）。
- 采用单一视觉树实现 `>= 640` 双栏 / `< 640` 单列自适应切换：保留唯一 `x:Name`（`HistoryScroll`、`DetailScroll`、`ConfirmationPanel`、`HistoryList`、`PreviousButton`、`NextButton`、`RetranslateButton`、`SourceTextBox`、`ResultsList`、`CopySourceButton`、`ClearButton`、`ConfirmButton`、`KeepFavoritesCheckBox`、`CloseButton` 等），通过 `HistoryWindow.xaml.cs` 在 `SizeChanged` / `Loaded` / `PlaceInWorkArea` 后按 `ActualWidth >= 640` 切换 `HistoryScroll` / `DetailScroll` 的纵向滚动模式、`ContentHostGrid` 的列宽与行高、左右栏 `Grid.Row`/`Grid.Column`、`ConfirmationPanel` 的 `Grid.ColumnSpan` 以及 `HistoryList.MaxHeight`。
- 列表项直接绑定 `HistoryRow.Summary.IsFavorite` 与 `HistoryRow.Summary.HasTruncatedResults` 呈现「收藏」「结果已截断」小标签；详情区只读文本复用阶段 3 引入的 `IrisBorderlessTextBox`，复制按钮与翻页按钮复用 `IrisIconButton`；承载信息的灰字一律使用 `TextFillColorSecondaryBrush`（仅搜索框占位提示用 `TextFillColorTertiaryBrush`），规避浅色主题对比度不足；所有图标均为 WPF-UI 4.3.0 已核实的 BMP 码位（`<= 0xFFFF`）。

**Tech Stack:** C# / .NET 10 / WPF / WPF-UI 4.3.0 / xUnit v3 + Shouldly + NSubstitute。

**验证命令（全阶段统一）：**
- `dotnet build Iris.slnx -c Release`（0 警告、0 错误）
- `dotnet test Iris.slnx -c Release --no-build`（全量 7 程序集）
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/audit-localization.ps1`（exit 0、`resourceErrors: []`）
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/measure-startup.ps1`（与阶段 3 后基线对比并如实记录每轮与波动）

**基线与红线：**
- 当前分支 `codex/t041-acrylic-ui` `HEAD = 40bf310`（阶段 3 提交）。
- 不碰 `specs/`、`tasks/BACKLOG.md`、`.codex/`；不新增 NuGet 包、不升级 WPF-UI。
- `src/` 内零新增中文字面量与中文注释（`HistoryWindow.xaml`、`HistoryWindow.xaml.cs`、`HistoryViewModel.cs` 当前均无中文注释）。
- 不 `push` / `reset` / `clean` / `rebase` / `amend`；真机验证仅用 `%TEMP%\iris-t041-evidence\phase4-final\portable` 下的便携副本（含 `portable.txt`），绝不运行 `src/Iris.App/bin` 下的 exe、绝不碰真实 `%APPDATA%\Iris` 与注册表、绝不结束非自己启动的进程。

---

## 1. 现状核查摘要（来自只读 Scout 审计）

1. **`src/Iris.App/History/`（5 个文件）**：
   - `HistoryWindow.xaml`（105 行）：`ui:FluentWindow`（`800×640`，`MinWidth="280"`，`MinHeight="240"`，`ExtendsContentIntoTitleBar="True"`，未设 `WindowBackdropType`），根 `<Grid Background="{DynamicResource IrisAcrylicTintBrush}">`；当前为单列 `HistoryScroll` + `StackPanel`，`HistoryList` 固定 `MaxHeight="240"`，列表项仅 3 个纯文本 `TextBlock`，操作按钮与复制按钮全为文字按钮。
   - `HistoryWindow.xaml.cs`（40 行）：仅含 `PlaceInWorkArea()`（按工作区限制 `MinWidth/MinHeight/Width/Height` 并居中）与 `Key.Escape` 关闭；未按 `x:Name` 引用任何控件。
   - `HistoryViewModel.cs`（371 行）：`HistoryRow(HistorySummary Summary)` 已直接公开 `Summary`（含 `IsFavorite`、`HasTruncatedResults`）、`Preview`、`Metadata`、`Markers`；`HistoryViewModel` 已具备全部所需属性与命令（`Keyword`、`FavoritesOnly`、`KeepFavorites`、`Rows`、`SelectedRow`、`Detail`、`HasSelection`、`HasDetail`、`IsLoading`、`IsDetailLoading`、`IsEmpty`、`ListError`、`DetailError`、`PageLabel`、`FavoriteLabel`、`Status`、`HasConfirmation`、`IsClearConfirmation`、`ConfirmationText`、`RefreshCommand`、`PreviousCommand`、`NextCommand`、`FavoriteCommand`、`DeleteCommand`、`ClearCommand`、`ConfirmCommand`、`CancelConfirmationCommand`、`CopySourceCommand`、`CopyResultCommand`、`RetranslateCommand`、`CloseCommand`），且写操作/刷新已统一调用 `RaiseCanExecuteChanged()`。
   - `HistoryWindowPresenter.cs` / `HistoryWindowSession.cs`：管理单实例、材质挂载 `WindowMaterialService?.Attach(window)`、截图隐藏参与者与关闭排空流程，阶段 4 不改动。
2. **本地化资源**：`UiText.History.cs`、`Strings.resx`、`Strings.en-US.resx` 已有全部 46 个 `History*` 键（涵盖标题、搜索占位、仅看收藏、刷新、清空、确认/取消、保留收藏、上一页/下一页、收藏/取消收藏、按当前设置重新翻译、删除此记录、复制原文全文、复制此服务译文、结果已截断、原文全文、空/加载/未选中/错误/关闭提示），**阶段 4 无需新增或修改任何资源键**。
3. **测试依赖与契约**：
   - `AcrylicWindowTests.cs:33-48`：断言 `HistoryWindow` 的 `ExtendsContentIntoTitleBar == true`、`WindowBackdropType` 未设置、根内容为 `Panel` 且承载 `IrisAcrylicTintBrush`。
   - `HistoryWindowTests.cs:71-80`：按 `x:Name` 强转 `HistoryList` (`ListBox`)、`SourceTextBox` (`TextBox`)、`CopySourceButton` (`Button`)、`ClearButton` (`Button`)、`ConfirmationPanel` (`Border`)、`KeepFavoritesCheckBox` (`CheckBox`)、`ConfirmButton` (`Button`)。
   - `HistoryRenderingTests.cs:39-56, 97-105`：按 `x:Name` 强转 `HistoryScroll` (`ScrollViewer`)、`PreviousButton` (`Button`)、`RetranslateButton` (`Button`)、`SourceTextBox` (`TextBox`)、`ResultsList` (`ItemsControl`)、`ConfirmationPanel` (`Border`)、`CloseButton` (`FrameworkElement`)；L51 `scroll.ScrollToEnd()` 与 L56 `scroll.ScrollToTop()` 需按 §8.1 改为滚动右栏（并兼顾 `320×280` 单列滚动）。
   - `LocalizationRenderingTests.cs`：不涉及 `HistoryWindow`。
4. **图标码位核验（WPF-UI 4.3.0，全部 BMP `<= 0xFFFF`）**：
   - `Search16` (`0xEA7C`)、`Star16` (`0xF70E`)、`Star16` Filled (`0xF717`)、`ArrowClockwise16` (`0xE0AA`)、`Delete16` (`0xE47B`)、`ChevronLeft16` (`0xF2A9`)、`ChevronRight16` (`0xF2AF`)、`Translate16` (`0xF832`)、`Copy16` (`0xF32A`)、`Warning16` (`0xF868`)、`ErrorCircle16` (`0xF3F0`)。

---

## 2. 阶段 4 关键裁决（P4-1 至 P4-6）

1. **P4-1（单视觉树双模式自适应布局，阈值 `ActualWidth >= 640`）**：
   - 保留 `HistoryScroll`（外层 `ScrollViewer`）与新增 `DetailScroll`（右栏详情 `ScrollViewer`）。
   - `ActualWidth >= 640`（双栏）：`HistoryScroll.VerticalScrollBarVisibility = Disabled`（向 `ContentHostGrid` 传入有限视口高度），`ContentHostGrid` 设为 2 列（`324` + `*`），`Row 0` 为跨两栏的 `ConfirmationPanel`（`Grid.ColumnSpan="2"`，固定在内容区最上方不随列表/详情滚走），`Row 1` 高度 `*` 放 `LeftPane`（`Col=0`，`HistoryList.MaxHeight = PositiveInfinity` 占满高度）与 `RightPane`（`Col=1`，左侧 `1px` 分隔线，`DetailScroll.VerticalScrollBarVisibility = Auto` 独立滚动）。
   - `ActualWidth < 640`（单列，如 `320×280`）：`HistoryScroll.VerticalScrollBarVisibility = Auto`，`ContentHostGrid` 设为 1 列（`*` + `0`），`ConfirmationPanel`（`Row=0, ColumnSpan=1`）位于 `HistoryScroll` 内部最上方，`LeftPane`（`Row=1, Col=0`，`HistoryList.MaxHeight = 240`）与 `RightPane`（`Row=2, Col=0`，顶部 `1px` 分隔线，`DetailScroll.VerticalScrollBarVisibility = Disabled`）依次纵向排布，全部控件均可通过 `HistoryScroll` 滚动到达。
   - **初始布局触发时机**：同时在 `Loaded`、`SizeChanged` 与 `PlaceInWorkArea()` 末尾调用 `ApplyResponsiveLayout()`（取 `ActualWidth > 0 ? ActualWidth : Width`），确保首次离屏渲染与 `PlaceInWorkArea` 缩窗后立即可处于正确模式。
2. **P4-2（共用样式补充：`IrisPillCheckBox` 与 `IrisHistoryListItem`）**：
   - 在 `src/Iris.App/Themes/Iris.Styles.xaml` 新增 2 个样式（零中文注释）：
     - `IrisPillCheckBox`（`TargetType="CheckBox"`）：药丸外框（`CornerRadius="14"`、`MinHeight="28"`、`Padding="10,0,12,0"`），内置星标图标（未勾选为空心 `Star16` + `TextFillColorSecondaryBrush`，勾选为实心 `Star16` + `AccentTextFillColorPrimaryBrush` 与强调边框/底色），保持 `CheckBox` 类型、键盘焦点与无障碍语义。
     - `IrisHistoryListItem`（`TargetType="ListBoxItem"`）：匹配 `history-layout.html` 左侧列表项样式（圆角 `6`、无悬停/未选中实底、悬停/选中为 `SubtleFillColorSecondaryBrush`，选中时左侧显示 `3px` 强调色竖条 `AccentFillColorDefaultBrush`，支持键盘焦点可视化）。
3. **P4-3（浅色/深色对比度与只读文本风格统一）**：
   - 继承阶段 3 实测经验：列表元数据、页码文字、原文卡片标题、服务模型名、底部 `Status` 与空态/未选中提示一律使用 `TextFillColorSecondaryBrush`；仅搜索框占位提示使用 `TextFillColorTertiaryBrush`。
   - 原文全文 `SourceTextBox` 与每张服务译文卡片只读 `TextBox` 均应用 `IrisBorderlessTextBox`，点击选中文本时不出现白色不透明方框与蓝色底线。
   - 卡片右上角 `CopySourceButton` 与每条译文卡片的复制按钮采用 `IrisIconButton`（`Icon="{ui:SymbolIcon Copy16}"`，`Content` 绑定 `UiText.HistoryCopySource` / `UiText.HistoryCopyResult`，自动生成对应 `ToolTip` 与 `AutomationProperties.Name`），且 `CopySourceButton` 位于原文卡片（`DetailPanel`）内部，全文加载中或读取失败时自然隐藏（符合 §6.1）。
4. **P4-4（英文与极窄宽度防裁剪）**：
   - 左栏工具行（`FavoritesCheckBox`、刷新、清空）采用 `Grid`/`DockPanel` 结合 `WrapPanel`（宽栏 324px 下中文与英文均可单行不截断，极窄 `< 280` 自动折行）；
   - 右栏操作行（收藏/取消收藏、按当前设置重新翻译、删除此记录）使用 `WrapPanel`（中文 800px 单行展示，英文 800px 或 320px 窄窗自然换行不裁剪）；
   - `ConfirmationPanel` 内部使用 `DockPanel`/`WrapPanel`（警告图标 + 加粗确认文案支持 `TextWrapping="Wrap"` + `KeepFavoritesCheckBox` + 强调样式 `ConfirmButton` + 取消按钮），在 800px 宽与 320px 窄窗下均完整可见不裁剪；
   - 结果卡片头部的 `ProviderName`、`Model`、`Truncated` 徽章与复制按钮按「左 `WrapPanel`（`TextTrimming="CharacterEllipsis"`）+ 右 `Auto` 复制图标按钮」排布。
5. **P4-5（不新增/不修改任何本地化资源键与 ViewModel 逻辑）**：
   - 所有 46 个 `History*` 键已完备，`HistoryViewModel.cs` 零改动；列表项直接绑定 `Summary.IsFavorite` / `Summary.HasTruncatedResults`，右栏收藏按钮图标通过 `DataTrigger` 绑定 `SelectedRow.Summary.IsFavorite` 在空心/实心强调色星标间切换。
6. **P4-6（既有测试修改范围，严格对齐 §8.1）**：
   - 仅更新 `tests/Iris.App.Tests/History/HistoryRenderingTests.cs:51,56`（新增获取 `DetailScroll` 并与 `HistoryScroll` 联动滚动：`detailScroll.ScrollToEnd(); scroll.ScrollToEnd();` 与 `detailScroll.ScrollToTop(); scroll.ScrollToTop();`，使 `800×640` 双栏滚右栏、`320×280` 单列滚单列）。
   - `HistoryWindowTests.cs`、`HistoryLifecycleTests.cs`、`AcrylicWindowTests.cs` 保持零破坏通过；另新增阶段 4 专项布局与交互测试（见 Task 3）。

---

## 3. 逐任务实施清单（共 5 个任务）

### Task 1: 补充共用样式 `IrisPillCheckBox` 与 `IrisHistoryListItem`（TDD）
- **Files:**
  - Modify: `src/Iris.App/Themes/Iris.Styles.xaml`
  - Modify: `tests/Iris.App.Tests/Themes/IrisStyleResourceTests.cs`
- **Steps:**
  1. **RED**：在 `IrisStyleResourceTests.cs` 新增 `IrisPillCheckBox_ResolvesInLightAndDarkThemes` 与 `IrisHistoryListItem_ResolvesAndShowsAccentIndicatorWhenSelectedInLightAndDarkThemes`，验证两套样式在 `Light` 与 `Dark` 主题下可实例化渲染（`ApplyTemplate` / offscreen `Window`），`IrisPillCheckBox` 保留 `CheckBox` 类型与勾选/未勾选切换，`IrisHistoryListItem` 选中时存在可见的强调色左指示条。确认先红。
  2. **GREEN**：在 `Iris.Styles.xaml` 新增 `IrisPillCheckBox` 与 `IrisHistoryListItem`（零中文注释，遵循现有动态主题画刷与 `DefaultControlFocusVisualStyle`）。
  3. **Verify**：运行 `dotnet build Iris.slnx -c Release`、`IrisStyleResourceTests` 与 `build/audit-localization.ps1`。
  4. **Stage & Review**：按路径暂存 -> `sdd-reviewer` 审查（必要时修复 + `sdd-rereviewer`）-> 更新 `STATUS.md` & `checkpoint.sh`。

### Task 2: 重构 `HistoryWindow.xaml` 与 `HistoryWindow.xaml.cs`（§6.1 全部视觉与自适应布局）
- **Files:**
  - Modify: `src/Iris.App/History/HistoryWindow.xaml`
  - Modify: `src/Iris.App/History/HistoryWindow.xaml.cs`
- **Steps:**
  1. **重构 `HistoryWindow.xaml`**（零中文注释，保持全部契约 `x:Name` 与根 `Grid Background="{DynamicResource IrisAcrylicTintBrush}"` + `ExtendsContentIntoTitleBar="True"` + 不设 `WindowBackdropType`）：
     - Row 0：`<ui:TitleBar Title="{x:Static app:UiText.HistoryTitle}" ShowMaximize="True" />`
     - Row 1：`<ScrollViewer x:Name="HistoryScroll" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Disabled">` 内含 `<Grid x:Name="ContentHostGrid" Margin="16,2,16,12">`：
       - `ConfirmationPanel`（`Border`, `Style="{StaticResource IrisCompactCard}"`，`Row=0, ColumnSpan=2`）：`Warning16`（`SystemFillColorCautionBrush`）+ 加粗 `ConfirmationText` + `KeepFavoritesCheckBox`（默认勾选，仅清空时可见）+ `ConfirmButton`（`Appearance="Primary"`）+ 取消按钮。
       - `LeftPane`（`Grid`, `Row=1, Col=0`）：
         - 搜索框容器：带前导 `Search16` 图标的 `SearchTextBox` + 空值占位提示 `UiText.HistorySearch`（`TextFillColorTertiaryBrush`）；
         - 筛选操作行：`FavoritesCheckBox`（`Style="{StaticResource IrisPillCheckBox}"`）+ 弹性占位 + `RefreshButton`（`ArrowClockwise16` + 文字）+ `ClearButton`（`Delete16` + 文字）；
         - 列表区（`Row="*"`）：加载中 / 错误 / 空列表提示显示在列表位置；`HistoryList`（`ListBox`, `ItemContainerStyle="{StaticResource IrisHistoryListItem}"`），每项含 2 行原文预览、1 行 `Metadata`（`TextFillColorSecondaryBrush`）、条件显示的「收藏」与「结果已截断」小标签；
         - 底部分页栏：`PreviousButton`（`IrisIconButton`, `ChevronLeft16`）、居中 `PageLabel`（`TextFillColorSecondaryBrush`）、`NextButton`（`IrisIconButton`, `ChevronRight16`）。
       - `RightPane`（`Border`, `Row=1, Col=1`，左 1px `DividerStrokeColorDefaultBrush` 分隔线，`Padding="14,0,0,0"`, `Margin="14,0,0,0"`）：
         - 未选中提示：`UiText.HistorySelect`（`HasSelection == False` 时可见）；
         - 选中内容区（`HasSelection == True`）：
           - 顶部操作栏（`WrapPanel`）：`FavoriteButton`（未收藏空心 `Star16` / 已收藏实心强调色 `Star16` + `FavoriteLabel`）、`RetranslateButton`（`Translate16` + `UiText.HistoryRetranslate`）、`DeleteButton`（`Delete16` + `UiText.HistoryDelete`）；
           - `<ScrollViewer x:Name="DetailScroll" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">`：内含 `HistoryDetailsLoading`、`DetailError`、`DetailPanel`（原文卡片含 `UiText.HistorySource` + `CopySourceButton` + 只读 `SourceTextBox`；`ResultsList` 非虚拟化 `ItemsControl`，每张服务卡片含 `ProviderName` + `Model` + `Truncated` 警告小标签 + 图标复制按钮 + 只读 `TextBox`）。
     - Row 2：底栏 `Border`（顶部 1px `DividerStrokeColorDefaultBrush` 分隔线，`Padding="16,10"`），左 `Status`（`TextFillColorSecondaryBrush`）、右 `CloseButton`。
  2. **更新 `HistoryWindow.xaml.cs`**：
     - 保留 `PlaceInWorkArea()` 与 `Key.Escape` 关闭行为不变；
     - 新增常量 `internal const double WideLayoutBreakpoint = 640d;`、`internal bool IsWideLayout { get; private set; }` 与 `private void ApplyResponsiveLayout()`（按 `double width = ActualWidth > 0 ? ActualWidth : Width; bool isWide = width >= WideLayoutBreakpoint;` 切换双栏 / 单列属性，避免重复写入），在 `Loaded`、`SizeChanged` 与 `PlaceInWorkArea()` 末尾调用。
  3. **Verify**：运行 `dotnet build Iris.slnx -c Release`、`AcrylicWindowTests`、`HistoryWindowTests`、`HistoryLifecycleTests` 与 `build/audit-localization.ps1`。
  4. **Stage & Review**：按路径暂存 -> `sdd-reviewer` 审查 -> 更新 `STATUS.md` & `checkpoint.sh`。

### Task 3: 更新 `HistoryRenderingTests.cs`（§8.1）并新增阶段 4 布局与交互测试（§8.2）
- **Files:**
  - Modify: `tests/Iris.App.Tests/History/HistoryRenderingTests.cs`
  - Modify: `tests/Iris.App.Tests/History/HistoryWindowTests.cs`（或新增 `tests/Iris.App.Tests/History/HistoryWindowLayoutTests.cs`）
- **Steps:**
  1. 按 §8.1 更新 `HistoryRenderingTests.cs:51,56`：获取 `DetailScroll`（`ScrollViewer`），在 `results-truncated` 前同时滚动 `detailScroll.ScrollToEnd(); scroll.ScrollToEnd();`，在 `loading` 前 `detailScroll.ScrollToTop(); scroll.ScrollToTop();`，并确保 `Render` 在 `800×640` 与 `320×280`、浅/深双主题下全部通过。
  2. 按 §8.2 新增阶段 4 专项测试（`tests/Iris.App.Tests/History/HistoryWindowLayoutTests.cs`）：
     - **宽/窄两种排法切换**：验证 `ActualWidth >= 640`（如 `800×640`）进入双栏模式（`LeftPane` 与 `RightPane` 同行左右并排、`LeftPane` 宽约 `324`、`HistoryScroll.VerticalScrollBarVisibility == Disabled`、`DetailScroll.VerticalScrollBarVisibility == Auto`、`HistoryList.MaxHeight` 为无穷大）；缩窄至 `< 640`（如 `560×640` / `320×280`）切换为单列模式（`RightPane` 排在 `LeftPane` 下方、`HistoryScroll.VerticalScrollBarVisibility == Auto`、`DetailScroll.VerticalScrollBarVisibility == Disabled`、`HistoryList.MaxHeight == 240`）；以及 `639` vs `640` 精确阈值测试。
     - **确认条在两种排法下均可见**：验证宽窗（`800×640`）与窄窗（`320×280`）下触发删除确认与清空确认时，`ConfirmationPanel` 均可见、`KeepFavoritesCheckBox` 仅在清空时可见且默认勾选、宽窗下 `ConfirmationPanel` 横跨左右两栏且在内容区最上方、窄窗下位于 `HistoryScroll` 内部并可滚动到达。
     - **`320×280` 下所有关键控件可滚动到达**：验证 `SearchTextBox`、`FavoritesCheckBox`、`RefreshButton`、`ClearButton`、`HistoryList`、`PreviousButton`、`NextButton`、`FavoriteButton`、`RetranslateButton`、`DeleteButton`、`SourceTextBox`、`CopySourceButton`、`ResultsList`（含其译文卡片复制按钮）、`ConfirmationPanel`、`CloseButton` 在 `320×280` 下均可 `BringIntoView()` 到达且实际可见/不越界。
     - **复制按钮可见性与提示契约**：验证 `CopySourceButton` 位于原文卡片内，未选中或详情加载中/读取失败时不可见，详情加载完成后可见且 `ToolTip` / `AutomationProperties.Name` 为 `UiText.HistoryCopySource`；译文卡片复制按钮 `ToolTip` / `AutomationProperties.Name` 为 `UiText.HistoryCopyResult`。
     - **收藏与截断小标签显示**：验证列表项在 `IsFavorite=true` / `HasTruncatedResults=true` 时分别显示对应小标签，未标记时不显示。
  3. **Verify**：运行 `dotnet build Iris.slnx -c Release`、全量 `History` / `AcrylicWindowTests` / `IrisStyleResourceTests` 测试与 `build/audit-localization.ps1`。
  4. **Stage & Review**：按路径暂存 -> `sdd-reviewer` 审查 -> 更新 `STATUS.md` & `checkpoint.sh`。

### Task 4: 全量构建、7 程序集回归、`sdd-final-reviewer` 整阶段终审与本地化审计
- **Steps:**
  1. 串行运行 `dotnet build Iris.slnx -c Release`（0 警告、0 错误）、`dotnet test Iris.slnx -c Release --no-build`（全量 7 程序集全绿）与 `build/audit-localization.ps1`（exit 0）。
  2. 生成整阶段 `phase4-full.diff` 并派发 `sdd-final-reviewer` 审查跨文件接缝、响应式布局边界、线程/生命周期安全、无障碍、中文字面量/注释红线与测试非空洞性。
  3. 若审出任何问题，按 TDD 修复轮 + `sdd-rereviewer` 闭环后重跑全量验证。

### Task 5: 便携副本真机截图复看（§9.2）、启动/内存基准对比（§9.4）与完成报告撰写
- **Steps:**
  1. 运行 `build/measure-startup.ps1`，与阶段 3 基线对比并如实记录全部 3 轮明细（含第 1 轮与常驻态波动）。
  2. 在 `%TEMP%\iris-t041-evidence\phase4-final\portable` 放置 `Release` 便携副本与 `portable.txt`（独立临时 SQLite `history.db` 预填多条含收藏、多服务译文、截断结果、长中英文记录；不碰真实 `%APPDATA%\Iris` 与注册表）：
     - 实测并记录 `HistoryWindow` 浅/深主题的 `DWMWA_SYSTEMBACKDROP_TYPE`（应为 `3` = Acrylic）；
     - 捕获并**亲自逐张视觉复看**真实桌面截图：
       - 中文 / 英文 × 浅色 / 深色 × 默认 `800×640`（双栏：空态、未选中态、选中多服务+收藏+截断态、仅看收藏筛选态、清空确认条态、删除确认条态）、单列窄窗（`560×640` 与最小 `280×240` / `320×280`）、以及真实点击夺焦的失焦态（验证 `Deactivated=True` 与 DWM 失焦回退）；
     - 复看 `HistoryRenderingTests` 离屏截图（验证哈希变化后画面无错位/重叠）。
     - 退出仅自己启动的便携进程，验证 `Remaining Iris.App: 0` 与 `%APPDATA%\Iris`、`HKCU\...\Run` 完全未变动。
  3. 按 `GEMINI.md` 撰写本文档第 5 节《阶段 4 完成报告》，更新 `phase-4-work/STATUS.md`、运行 `checkpoint.sh`，将全部改动（含本计划文档）暂存并呈交用户审阅确认后再做单次本地提交。

---

## 4. 预计修改的既有测试与潜在偏差预登记

- **预计修改的既有测试（仅 1 处，已列在设计文档 §8.1）**：
  - `tests/Iris.App.Tests/History/HistoryRenderingTests.cs:51,56`：改为滚动新增的右栏 `DetailScroll`（同时兼顾单列 `HistoryScroll`），其余断言不变。
- **与设计/工单的偏差预登记**：
  - 无新增偏差；Windows 10 纯色回退真机环境按 §9.3 仍在完成报告中标注为「未验证」（纯色字典路径由自动化主题测试覆盖）。

---

## 5. 阶段 4 完成报告

- **日期**：2026-11-26
- **工作分支**：`codex/t041-acrylic-ui`（基于阶段 3 提交 `40bf310`，随本次提交一并入库本计划与完成报告文档，共 7 个文件；仅本地提交，不 `push`）
- **改动范围（7 个文件）**：
  - `docs/superpowers/plans/2026-11-25-acrylic-ui-phase-4-history-window.md`（阶段 4 实施计划与完成报告）
  - `src/Iris.App/Themes/Iris.Styles.xaml`（新增 `IrisPillCheckBox` 与 `IrisHistoryListItem`）
  - `src/Iris.App/History/HistoryWindow.xaml`、`src/Iris.App/History/HistoryWindow.xaml.cs`（§6.1 亚克力左右分栏 / `< 640` 单列自适应重构，列表信息行允许换行避免截断语言对与结果数，确认条操作区统一左对齐保证换行后与上一行同侧对齐；保留全部契约 `x:Name` 与类型，零改动 `HistoryViewModel.cs` / `HistoryWindowPresenter.cs` / `HistoryWindowSession.cs`）
  - `tests/Iris.App.Tests/Themes/IrisStyleResourceTests.cs`（新增 `IrisPillCheckBox` 与 `IrisHistoryListItem` 双主题离屏渲染测试）
  - `tests/Iris.App.Tests/History/HistoryRenderingTests.cs`（按 §8.1 调整 L40/L52/L58 同步滚动 `DetailScroll` 与 `HistoryScroll`）
  - `tests/Iris.App.Tests/History/HistoryWindowLayoutTests.cs`（按 §8.2 新增双栏/单列 `640` 断点、宽/窄及 `en-US 640px` 确认条无折叠无裁剪与换行左对齐断言、`320×280` 与 `280×240` 控件可达性、`CopySourceButton` 显隐与提示、收藏/截断小标签测试）

### 5.1 原始构建、全量 7 程序集回归与本地化审计输出

> **跨机运行说明**：为避免在本机桌面弹出 WPF 测试窗口打扰用户，以下全量 7 程序集汇总数 **`4019`（成功 `4017`、失败 `0`、跳过 `2`）是两台机器实测结果相加**：6 个非 WPF 程序集（`2905` 条）在本机运行，**`Iris.App.Tests`（`1114` 条）全部在远程 `win-server`（`192.168.8.106` / `WIN-JFBOFDSAH6T`，Windows 10.0.26100.33438，.NET 10.0.14）交互会话中运行，未在本机运行**。

1. **`dotnet build Iris.slnx -c Release`**（本机）：
```text
已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:15.57
```

2. **全量 7 个测试程序集实测汇总（两机相加：总计 4019，失败 0，成功 4017，已跳过 2）**：
   - 本机运行（6 个非 WPF 程序集，`dotnet test <proj> -c Release --no-build`，合计 `2905` 条：`2903` 通过、`0` 失败、`2` 跳过）：
     - `Iris.Core.Tests`：总计 985，失败 0，成功 985，已跳过 0
     - `Iris.Infrastructure.Tests`：总计 188，失败 0，成功 188，已跳过 0
     - `Iris.Providers.Tests`：总计 584，失败 0，成功 584，已跳过 0
     - `Iris.Platform.Windows.Tests`：总计 1098，失败 0，成功 1096，已跳过 2（隔离剪贴板 Window Station 环境限制）
     - `Iris.Maintenance.Tests`：总计 39，失败 0，成功 39，已跳过 0
     - `Iris.ArchitectureTests`：总计 11，失败 0，成功 11，已跳过 0
   - 远程 `win-server`（`192.168.8.106`）运行 `Iris.App.Tests.exe`（最终提交前 Release 产物，未在本机运行）：
```text
=== TEST EXECUTION SUMMARY ===
   Iris.App.Tests  Total: 1114, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 339.605s
APP_TESTS_EXIT=0
```
（较阶段 3 的 4006 条通过净增 11 条新测试，7 个程序集全部通过。）

3. **`powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/audit-localization.ps1`**（本机）：
```text
AUDIT_EXIT=0
Scanned C# and XAML; Chinese entries including comments: 1168
Compiled resource values matched after XML newline normalization; composite formats parsed: 1240
{"sourceEntries": 1168, "commentEntries": 950, "reviewedNonUiEntries": 218, "resourceCounts": {"App": 467, "Core": 148, "Maintenance": 5}, "resourceErrors": [], "englishNativeNameExceptions": {"App:LanguageChineseNative": "简体中文"}, "exceptionManifestSha256": "2d620105575400696d1f567460d883cf050ff511b49b6f64d5ea0b5192eb9a75"}
```

### 5.2 启动与内存测量（`build/measure-startup.ps1`）

本次 `measure-startup.ps1` 在远程 `win-server`（`192.168.8.106` / `WIN-JFBOFDSAH6T`，Windows 10.0.26100.0，虚拟显卡 `Microsoft Basic Display Adapter`，起始 CPU 3%）执行，原始输出如下：
```text
Iris measure-startup | 2026-11-25 20:37:07 | Microsoft Windows NT 10.0.26100.0 | Configuration=Release Runs=3 IdleSeconds=30
System CPU load at start: 3% (startup-time NFR is only meaningful below 30%)
Binary: C:\Temp\iris-p4-test\repo\src\Iris.App\bin\Release\net10.0-windows10.0.19041.0 (built 2026-11-25 20:09:12)
--- second instance (run 1) ---
ExitedWithin5s : True
ElapsedMs      : 393
ExitCode       : 0
LogLinesAdded  : 0
--- per run ---
Run StartupMs VisibleWindows WorkingSetMB PrivateBytesMB ExitCode ShutdownLogLines
  1      3848              1        152.4           46.3 timeout                 3
  2    3718.8              0        113.1             33 timeout                 3
  3    3744.2              0          113           32.8 timeout                 3
--- averages --- StartupMs=3,770.3  WorkingSetMB=126.2  PrivateBytesMB=37.4
--- NFR (ARCH 1.2): StartupMs <= 1500; PrivateBytesMB <= 100; WorkingSetMB <= 160; VisibleWindows = 0; ExitCode = 0; ShutdownLogLines >= 1 ---
```
- **客观说明**：
  - 阶段 4 未在本机桌面重测 `measure-startup.ps1`；上述 `StartupMs=3770.3 ms` 为 `win-server` 虚拟化环境读数，不与阶段 3 本机基线（`StartupMs=901.6 ms`）直接横比，本机启动耗时标为「未验证」；
  - `win-server` 三轮内存：Run 1（启动过第二实例、`VisibleWindows=1`）`WorkingSetMB=152.4 MB` / `PrivateBytesMB=46.3 MB`，Run 2–3 常驻态 `WorkingSetMB=113.1 / 113.0 MB`、`PrivateBytesMB=33.0 / 32.8 MB`，二次实例 `393 ms` 退出码 `0`。

### 5.3 审查闭环、远程 `win-server` 使用说明与真机截图复看

- **SDD 审查闭环与提交前修补**：Task 1–3 均首轮获 `sdd-reviewer` `APPROVED`；整阶段 `sdd-final-reviewer` 审出 5 项问题（2 项 Important + 3 项 Minor）并经 `sdd-rereviewer` 全部 `all_addressed`；提交前复核进一步完成 2 项界面修补并重截复看：
  1. 列表行元信息 `Metadata` 由单行省略改为 `TextWrapping="Wrap"`，使 324px 左栏下中英文的语言对与结果数均完整显示不截断（重截并复看 `03`、`11`）；
  2. `ConfirmationActionsPanel` 统一改为 `HorizontalAlignment="Left"`（不再在宽模式下右对齐导致 `en-US` `640px` 换行后按钮悬空于 `x≈181`），使换行后的「Confirm deletion / Cancel」按钮与上一行「Keep favorites…」严格左对齐（重截并复看 `12`，并在 `HistoryWindowLayoutTests.cs` 增补左对齐断言）。
- **远程机器 `win-server`（`192.168.8.106`）使用与清理说明**：
  - 经 SSH 传至 `win-server` 的 `C:\Temp\iris-p4-test\` 的全部文件为：
    - `portable-app.tar.gz`（解包至 `C:\Temp\iris-p4-test\portable\` 并放置 `portable.txt`）
    - `app-tests.tar.gz`（解包至 `C:\Temp\iris-p4-test\app-tests\`）
    - `history-seeded.db`（由本地 Python 生成的 56 条 V2 样本 SQLite 库，不含任何用户真实数据）
    - `capture-remote-p4.ps1`、`measure-startup.ps1`、`run-remote-tests-and-measure.ps1`、`run-remote-apptests-only.ps1`
  - 临时创建的计划任务 `IrisCapP4`、`IrisVerifyP4`、`IrisAppTestsP4` 均已删除；验证结束后已删除整个 `C:\Temp\iris-p4-test\` 目录（`DirExists=False`），并确认远端无残留 `Iris.App` / `Iris.App.Tests` 进程（`RemainingProcs=0`）。
- **远程 `win-server` 13 张截图复看与材质说明（`%TEMP%\iris-t041-evidence\phase4-final\win-server-screenshots`）**：
  - 在 `win-server` 上对浅/深双主题 `HistoryWindow` 读取 `DwmGetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE)` 均返回 `hr=0x00000000 DWMWA_SYSTEMBACKDROP_TYPE=3`（仅证明窗口属性已设置）；
  - **重要说明**：因 `win-server` 为 Windows Server 2025 虚拟显示适配器环境（`Microsoft Basic Display Adapter`），13 张截图中的窗口均呈现**不透明纯色直角外观（`#EAEAEA` / `#333333`），亚克力透底与圆角未生效**，且 `09-zh-light-800-unfocused.png` 与 `03-zh-light-800-selected.png` 逐像素相同（尽管焦点切换时 `GetForegroundWindow != HistoryHwnd`）。因此这组截图**仅用于验证布局、排版、控件可见性、双栏/单列切换、确认条与中英文文本**，「亚克力观感」与「失焦态外观」均标为**未验证**。
  - 13 张截图（含最新重截的 `03`、`11`、`12`）均已亲自打开复看，确认布局结构、信息行完整换行、英文 `640px` 确认条左对齐、药丸仅看收藏、左 3px 选中竖条、收藏/截断小标签及 `320×280` 排布正确。

### 5.4 修改的既有测试、偏差、遗留与未验证说明

- **修改的既有测试（仅 1 处，完全对应设计文档 §8.1）**：
  1. `tests/Iris.App.Tests/History/HistoryRenderingTests.cs:40,52,58`：获取新增的右栏 `DetailScroll`，在 `results-truncated` 与 `loading` 状态同步滚动 `DetailScroll` 与 `HistoryScroll`，其余断言与离屏渲染契约不变。
- **与设计/工单的偏差**：无。
- **已知遗留**：
  1. **单列模式双滚动条与 240 高列表横切**：窄窗单列模式下 `HistoryList`（内部纵向滚动、`MaxHeight="240"`）与外层 `HistoryScroll` 形成双滚动条，且固定 `240` 高度会把底部一条记录横向截断；
  2. **主题灰字与层次对比度**：浅色主题次要灰字对比度约 `3:1`、搜索框占位文字约 `2:1`；深色主题次要文字与正文视觉层级偏近；
  3. **英文右栏操作按钮折行**：`en-US` 下右栏操作按钮（`Remove favorite` / `Retranslate with current settings` / `Delete this record`）在 `800px` 宽下折成 2 行、更窄宽度下折成 2–3 行；
  4. **英文列表元数据 `"1 results"` 单复数问题**：`11`/`13` 中单条结果展示为 `1 results`，经 `git log -S HistoryMetadataFormat` 核实为 T-038（提交 `9c1ac9d`）既有资源键 `Strings.en-US.resx:123`（`{0:g} · {1} · {2} → {3} · {4} results`）的既有问题，阶段 4 未改动资源键与 `HistoryViewModel`。
- **未验证项（显式标注）**：
  1. **亚克力观感**（`win-server` 虚拟显卡环境下窗口呈不透明纯色直角，仅验证了 `DWMWA_SYSTEMBACKDROP_TYPE=3` 属性已设置，未在支持真实合成亚克力的本机 GPU 桌面验证观感）；
  2. **失焦态外观**（`win-server` 上 `09` 与 `03` 逐像素相同，无法作为失焦外观证据）；
  3. **Windows 10 纯色回退真机环境**（纯色回退字典切换路径仅由自动化主题测试覆盖）；
  4. **本机物理机启动耗时重测与本机运行 `Iris.App.Tests`**（为避免打扰本机桌面，两项均在远程 `win-server` 执行）。
