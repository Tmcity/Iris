# T-041 阶段 3（翻译弹窗）实施计划

> **For agentic workers:** 按用户工作模式由总控（Controller）在当前分支 `codex/t041-acrylic-ui` 顺序派发 `sdd-implementer` 执行 TDD 实现（严禁子代理 `git commit`），每任务完成后按路径 `git add` 暂存并派发 `sdd-reviewer`（必要时走修复轮与 `sdd-rereviewer`），全部完成后走 `sdd-final-reviewer` 与便携副本真机验证；阶段 3 最终提交前必须征得用户确认。

**Goal:** 按已批准设计文档 §5 与 §7 更新后的 `specs/TRANSLATION_WINDOW.md`，将 `TranslateWindow` 重构为 Windows 11 亚克力极简布局（顶栏仅保留源语言+检测语言灰字、钉住、设置；源文本卡片与底部状态/停止/字数；目标语言分割行；结果卡片含头像首字母、模型、耗时、缓存与「上次结果」标记及图标化复制/重试按钮），并在 `TranslateWindowViewModel` 中实现可注入 `TimeProvider` 的停顿 1 秒自动翻译防抖（§5.3）。

**Architecture:** 严格保持翻译编排、历史记录、自动复制、窗口位置记忆（`WindowPlacementHelper`）与 `specs/CONTRACTS.cs` 契约不变。新增的唯一交互行为是 §5.3 停顿 1000 ms 自动翻译防抖，全部走既有 `ExecuteTranslateFromUser()` 路径（复用现有历史与自动复制规则）。窗口继续使用阶段 2 任务 4B 落地的零边距外层 `Grid`（`Background="{DynamicResource IrisAcrylicTintBrush}"`）和内层 `DockPanel`（`Margin="12,8,12,12"`），保留 `ContentScrollViewer` 在 `Height < 440` 时保证 `CardsScrollViewer.MinHeight = 160` 的短窗规则。

**Tech Stack:** C# / .NET 10 / WPF、WPF-UI 4.3.0（不升级、不新增包）、xUnit v3 + Shouldly + NSubstitute + `Microsoft.Extensions.TimeProvider.Testing`（`FakeTimeProvider`，已在 `Iris.App.Tests.csproj` 引用）、`dotnet build Iris.slnx -c Release`、`dotnet test Iris.slnx -c Release`、`build/audit-localization.ps1`。

**基线 HEAD:** `6fbe387`（阶段 2 设置窗口本地提交），分支 `codex/t041-acrylic-ui`。

---

## 1. 约束清单（每任务必须遵守）

1. **只读文件与红线**：不改 `specs/`（包括 `specs/CONTRACTS.cs` 签名）、不动 `tasks/BACKLOG.md`、不读/改/提交 `.codex/`、不新增 NuGet 包、不升级 WPF-UI 4.3.0。
2. **本地化审计红线**：`src/` 下不新增中文字面量或中文注释；既有中文注释（如 `TranslateWindow.xaml.cs:103`、`TranslateWindowViewModel.cs` 既有注释）一字不改、不挪行。新增或调整的界面文字统一写入 `src/Iris.App/Resources/Strings.resx`、`src/Iris.App/Resources/Strings.en-US.resx`、`build/localization/app-catalog.json` 并通过 `UiText` 取用。
3. **Git 与真机红线**：当前分支顺序执行，不建分支/worktree；子代理严禁 `git commit`；每任务审查通过后由总控按路径 `git add` 并运行 `bash docs/superpowers/plans/phase-3-work/checkpoint.sh "<说明>"`；阶段 3 最终只做一次本地提交，不 `push`/`reset`/`clean`/`rebase`/`amend`；真机验证只用 `%TEMP%\iris-t041-evidence\phase3-final\portable\` 下带 `portable.txt` 的便携副本，绝不运行 `src/Iris.App/bin` 下的 exe。
4. **测试与报告真实性**：测试禁止真实联网、代码/日志/异常中不得出现 API Key；完成报告中每个数字、测试名与命令输出均原样粘贴自实际命令；未验证项（如 Windows 10 纯色回退、真机中文输入法组字）显式标为「未验证」。

---

## 2. 现状核查摘要（由只读 `scout` 核实）

- **源码文件（`src/Iris.App/Translation/`）**：
  - `TranslateWindow.xaml`（356 行）：已有外层零边距 `Grid`（`Background="{DynamicResource IrisAcrylicTintBrush}"`）和内层 `<DockPanel Margin="12,8,12,12">`；当前顶栏含标题字样与 `CloseButton`（`×`），`PinButton`/`SettingsButton` 使用 emoji 文本；语言栏含 `TranslateButton` 与 `StopButton`；无检测语言灰字与「上次结果」标记；卡片复制/重试为文字按钮。
  - `TranslateWindow.xaml.cs`（194 行）：含 `OnCloseButtonClick`、Esc 隐藏、`OnInputTextBoxPreviewKeyDown`（`Key.ImeProcessed` / `Shift+Enter` / `Enter` / `Ctrl+Enter`）、`OnWindowSizeChanged`（`shortWindow = Height < 440` 时开启 `ContentScrollViewer` 滚动并设 `CardsScrollViewer.MinHeight = 160`；`narrow = Width < 420` 时重排 `LanguageControlsGrid`）。尚无 `TextCompositionManager` 组合输入监听。
  - `TranslateWindowViewModel.cs`（1718 行）：已有 `_resolvedSourceLanguage`、`OnUserInputChanged()`、`ExecuteTranslateFromUser()`、`ExecuteStop()`、`ExecuteSwapLanguages()`、`ApplyRequest(...)`（通过 `_programmaticTextUpdateDepth` 抑制用户编辑回调）、`OnWindowHidden()`、`Dispose()`；当前无防抖计时器、无 `DetectedSourceLanguageText`、无 `HasStaleResults`、无 `IsInputTooLong`。
  - `TranslateWindowPresenter.cs`（944 行）：已有 `OnWindowIsVisibleChanged(bool isVisible)`（第 753 行），当前只更新 `_isVisibleSnapshot`，尚未通知 `_viewModel`。
  - `ProviderResultCardViewModel.cs`（165 行）：已有 `DisplayName`、`Model`、`ElapsedText`、`FromCache`、`CanCopy`、`CopyTooltip`、`CanRetry`、`IsStreaming`、`IsLocallyTruncated`、`IsModelTruncated`、`HasError`、`ErrorMessage`、`ShowGoToSettings`；尚无 `ProviderInitial`。
- **关键既有测试依赖**：
  - `tests/Iris.App.Tests/Translation/TranslateWindowUiTests.cs`：按 `x:Name` 查找 `InputTextBox`、`SourceLangComboBox`、`TargetLangComboBox`、`SwapLanguagesButton`、`CardsScrollViewer`、`StopButton`、`TranslateButton`（需按 §8.1 迁移 `TranslateButton` 断言并补新断言）。
  - `tests/Iris.App.Tests/Ocr/OcrEntrypointTests.cs`（第 39-43 行）：按 `x:Name` 查找 `TranslateButton` 并执行其命令（按 §8.1 改为 Enter 或 `vm.TranslateCommand.Execute(null)`）。
  - `tests/Iris.App.Tests/Localization/LocalizationRenderingTests.cs`（第 100-141 行）：在 `280×300` 下断言 `CardsScrollViewer.ViewportHeight >= 160`，并通过 `Equals(button.Content, UiText.TranslateCopy)` 查找卡片复制按钮并 `HitTest`；复用 `IrisIconButton`（`Content` 保留字符串、仅渲染 `Icon`）可保持此测试完全兼容且继续验证图标按钮可点击。
  - `tests/Iris.App.Tests/Themes/AcrylicWindowTests.cs`（第 51-63 行）：断言 `TranslateWindow` 根 `Grid` 零边距且含 `IrisAcrylicTintBrush`，单一子元素为 `DockPanel` 且 `Margin="12,8,12,12"`、`Background == null`；保留 `<DockPanel Margin="12,8,12,12">` 结构可保持此测试零改动通过。
- **已有资源键与图标**：
  - `UiText` 已存在 `TranslatePinWindow`、`TranslateUnpinWindow`、`TranslateSettings`、`TranslateSwapLanguages`、`TranslateStopButton`、`TranslateStopped`、`TranslateBusy`、`TranslateEmptyPrompt`、`TranslateInputTooLong`、`TranslateFromCache`、`TranslateLastResult`（「上次结果」/「Previous result」）、`TranslateCopy`、`TranslateCopyVisible`、`TranslateRetry`、`TranslateGoToSettings`、`TranslateLocalTruncated`、`TranslateModelTruncated`、`TranslateNoConfiguredProviders`、`TranslateInputPlaceholder`。
  - WPF-UI 4.3.0 已验证存在 `SymbolRegular` / `SymbolFilled`：`Pin16`、`Settings16`、`ArrowSwap16`、`Stop16`、`Copy16`、`ArrowClockwise16`、`Warning16`、`ErrorCircle16`。
  - `Iris.App.Tests.csproj` 已引用 `Microsoft.Extensions.TimeProvider.Testing`（`10.10.0`），`CoreServiceCollectionExtensions.cs:40` 已在 DI 注册 `TimeProvider.System`。

---

## 3. 阶段 3 关键裁决（P3-1 至 P3-6）与工单偏差预登记

1. **P3-1（`TimeProvider` 构造注入兼容性）**：在 `TranslateWindowViewModel` 构造函数末尾新增可选参数 `TimeProvider? timeProvider = null`（默认 `TimeProvider.System`）。DI 容器自动注入已注册的 `TimeProvider`（`AppHostBuilder.cs:134`），既有 8 处 `new TranslateWindowViewModel(...)` 构造调用零破坏，防抖单元测试直接传入 `FakeTimeProvider`。
2. **P3-2（本地化键增改与旧键留存）**：
   - 新增 1 个键 `TranslateTo`（中文 `"译为"`、英文 `"To"`）用于目标语言分割行；
   - 更新既有 `TranslateInputPlaceholder` 文案以反映自动翻译（中文 `"输入文本，停顿后自动翻译；按 Enter 立即翻译"`、英文 `"Type to translate automatically, or press Enter"`），同步更新 `Strings.resx`、`Strings.en-US.resx`、`build/localization/app-catalog.json`；
   - 保留已无界面使用者的 `TranslateButton` 与 `TranslateClose` 资源键（与阶段 2 保留 `SettingsHotkeyEnable` 一致），在完成报告中登记。
3. **P3-3（全局错误分区展示）**：输入校验类错误（`ErrorCode.InputEmpty`、`ErrorCode.InputTooLong`）显示在源文本卡片底栏左侧（红字）并配合红色字数计数（`IsInputTooLong`），其余全局错误（如 `NotConfigured`、`AuthenticationFailed`）显示在结果区顶部的独立错误卡片中（含「去设置」按钮），与样张 `translate-layout-v3.html` 一致且确保 `280px` 窄窗与 `LocalizationRenderingTests` 错误态截图清晰无截断。
4. **P3-4（源/目标语言下拉框样式 `IrisGhostComboBox`）**：基于 WPF-UI 内置 `ComboBox` 样式（`BasedOn="{StaticResource {x:Type ComboBox}}"`）微调为静息无边框透明底、悬停/展开微浅底的紧凑样式，不整套重写 `ComboBox` `ControlTemplate`，避免下拉弹窗定位、键盘导航与深浅主题下拉列表异常。
5. **P3-5（流式卡片进度指示）**：翻译中（`IsStreaming == true`）在结果卡片顶部显示 2px 静态强调色指示条（`AccentFillColorDefaultBrush`），不使用持续 `Storyboard` 动画，确保零额外 CPU 开销与离屏渲染确定性。
6. **P3-6（陈旧结果态与检测语言清除时机）**：
   - `HasStaleResults` 仅在用户编辑（`OnUserInputChanged()`）且 `Cards.Count > 0` 时置 `true`，在 `SubmitMainTranslation`（新一轮翻译开始、`Cards.Clear()`）与 `Dispose()` 时重置 `false`；点击停止（`ExecuteStop()`）不标为陈旧结果。
   - `DetectedSourceLanguageText` 仅在 `SourceLanguage == "auto"` 且 `_resolvedSourceLanguage` 为非空非 `"auto"` 语言代码时返回本地化显示名；用户切换 `SourceLanguage`、点击交换语言或 `ApplyRequest` 替换原文时清空 `_resolvedSourceLanguage` 以防串显示上一轮检测结果。
   - **工单偏差预登记**：本阶段按 `HANDOFF.md` 与 `DECISIONS.md` 规范，保留独立阶段 3 计划与旁支进度快照，并保留 2 个闲置旧资源键（`TranslateButton`、`TranslateClose`）；其余完全对齐设计文档 §5、§7、§8、§9。

---

## 4. 逐任务详细步骤

### Task 1: 本地化资源键与共用主题样式补充

**Files:**
- Modify: `src/Iris.App/Resources/Strings.resx`
- Modify: `src/Iris.App/Resources/Strings.en-US.resx`
- Modify: `build/localization/app-catalog.json`
- Modify: `src/Iris.App/Localization/UiText.cs`
- Modify: `src/Iris.App/Themes/Iris.Styles.xaml`
- Modify: `tests/Iris.App.Tests/Themes/IrisStyleResourceTests.cs`

**Steps:**
1. **TDD 先写失败测试**：在 `tests/Iris.App.Tests/Themes/IrisStyleResourceTests.cs` 中补充断言：
   - 验证 `IrisCompactCard`（`Border`，基于 `IrisCard`，`Padding` 为 `12,10`）存在且可解析；
   - 验证 `IrisGhostComboBox`（`ComboBox`，`Background` 静息为 `Transparent`、`MinHeight` 为 `28`）存在且可解析；
   - 验证 `UiText.TranslateTo` 非空且非键名回退。
2. **运行确认失败**：
   ```bash
   dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~IrisStyleResourceTests"
   ```
3. **实现改动**：
   - 在 `Strings.resx`、`Strings.en-US.resx`、`build/localization/app-catalog.json` 新增 `TranslateTo`（zh-CN: `"译为"`，en-US: `"To"`），并在 `UiText.cs` 新增 `public static string TranslateTo => global::Iris.App.Resources.AppResources.Get("TranslateTo");`。
   - 同步更新 `TranslateInputPlaceholder` 在 `Strings.resx`（`"输入文本，停顿后自动翻译；按 Enter 立即翻译"`）、`Strings.en-US.resx`（`"Type to translate automatically, or press Enter"`）和 `build/localization/app-catalog.json`。
   - 在 `src/Iris.App/Themes/Iris.Styles.xaml` 新增：
     - `IrisCompactCard`（`TargetType="Border"`, `BasedOn="{StaticResource IrisCard}"`, `Padding="12,10"`）
     - `IrisGhostComboBox`（`TargetType="ComboBox"`, `BasedOn="{StaticResource {x:Type ComboBox}}"`, 静息 `Background="Transparent"`、`BorderBrush="Transparent"`、`BorderThickness="0"`、`MinHeight="28"`、`Padding="8,4,6,4"`、`FontSize="12.5"`，悬停或展开 `IsMouseOver` / `IsDropDownOpen` 时设 `Background="{DynamicResource SubtleFillColorSecondaryBrush}"`）。
4. **运行验证通过（含本地化审计）**：
   ```bash
   dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~IrisStyleResourceTests|FullyQualifiedName~LocalizationTests"
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/audit-localization.ps1
   ```
5. **总控暂存、审查与快照**：按路径 `git add` -> `sdd-reviewer` 审查 -> 更新 `phase-3-work/STATUS.md` -> 运行 `checkpoint.sh`。

---

### Task 2: `TranslateWindowViewModel` 与 `ProviderResultCardViewModel` 展示状态扩展（§5.2）

**Files:**
- Modify: `src/Iris.App/Translation/TranslateWindowViewModel.cs`
- Modify: `src/Iris.App/Translation/ProviderResultCardViewModel.cs`
- Modify: `tests/Iris.App.Tests/Translation/TranslateWindowViewModelTests.cs`

**Steps:**
1. **TDD 先写失败测试**：在 `tests/Iris.App.Tests/Translation/TranslateWindowViewModelTests.cs` 新增单元测试覆盖：
   - `DetectedSourceLanguageText_ShowsLocalizedNameOnlyWhenSourceIsAutoAndResolved`：源语言为 `auto` 且收到 `TranslationStarted(Source="en")` 后，`HasDetectedSourceLanguage` 为 `true` 且 `DetectedSourceLanguageText` 等于 `AvailableSourceLanguages` 中 `"en"` 的 `DisplayName`；手动将 `SourceLanguage` 改为 `"en"` 后立即隐藏；改回 `"auto"` 后仍为空（不残留旧检测值）；执行 `SwapLanguagesCommand` 后清空；收到替换文本的新 `ApplyRequest` 后清空。
   - `HasStaleResults_BecomesTrueOnUserEditWithCards_AndResetsOnNewTranslation`：无卡片时编辑 `InputText` 不标陈旧；已有卡片后编辑 `InputText` / 改 `SourceLanguage` / 改 `TargetLanguage` / 点交换语言后 `HasStaleResults` 为 `true`；执行 `ExecuteTranslateFromUser()` 开始新一轮翻译后重置为 `false`；`ExecuteStop()` 后不标为陈旧。
   - `InputLengthAndErrorPartition_ComputesIsInputTooLongAndErrorFlags`：验证 `IsInputTooLong`（`InputLength > MaxInputChars`）、`IsInputValidationError`（`InputEmpty`/`InputTooLong`）、`HasGeneralError`（`NotConfigured`/`AuthenticationFailed`）及 `HasVisibleStatus`（`HasStatus && !IsTranslating && !IsInputValidationError`）的通知与值。
   - `ProviderResultCardViewModel_ProviderInitial_ReturnsUppercaseFirstCharacter`：验证英文、中文、空白/空名回退（如 `"DeepL"` -> `"D"`，`"OpenAI"` -> `"O"`，`"百度翻译"` -> `"百"`，`""` -> `"?"`）。
2. **运行确认失败**：
   ```bash
   dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~TranslateWindowViewModelTests"
   ```
3. **实现改动**：
   - 在 `ProviderResultCardViewModel.cs` 新增 `public string ProviderInitial` 计算属性（不改变构造函数签名和既有中文注释）。
   - 在 `TranslateWindowViewModel.cs` 新增：
     - `DetectedSourceLanguageText`、`HasDetectedSourceLanguage` 及在 `HandleTranslationEventAsync(TranslationStarted)`、`SourceLanguage` setter（用户修改时清 `_resolvedSourceLanguage = null`）、`ExecuteSwapLanguages()`、`ApplyRequest(...)`（`updateText` 时清 `_resolvedSourceLanguage = null`）发出变更通知；
     - `HasStaleResults`（`OnUserInputChanged()` 设 `Cards.Count > 0`，`SubmitMainTranslation` 与 `Dispose()` 置 `false`）；
     - `IsInputTooLong`（在 `InputText` setter、`ApplyRequest`、`OnSettingsChanged` 通知）；
     - `IsInputValidationError`、`HasGeneralError`、`HasVisibleStatus`（在 `Error`、`StatusMessage`、`IsTranslating` 变更时通知）。
4. **运行验证通过**：
   ```bash
   dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~TranslateWindowViewModelTests|FullyQualifiedName~TranslationWorkflow|FullyQualifiedName~TranslationEndToEndTests"
   ```
5. **总控暂存、审查与快照**。

---

### Task 3: 停顿 1 秒自动翻译防抖（§5.3，`TimeProvider` 可注入 + `FakeTimeProvider` 全矩阵测试）

**Files:**
- Modify: `src/Iris.App/Translation/TranslateWindowViewModel.cs`
- Modify: `src/Iris.App/Translation/TranslateWindowPresenter.cs`
- Modify: `tests/Iris.App.Tests/Translation/TranslateWindowViewModelTests.cs`

**Steps:**
1. **TDD 先写失败测试**：在 `tests/Iris.App.Tests/Translation/TranslateWindowViewModelTests.cs` 中使用 `Microsoft.Extensions.Time.Testing.FakeTimeProvider`（严禁真实 `Task.Delay` 等待）新增完整防抖测试矩阵：
   - `AutoTranslateDebounce_InputEdit_FiresAt1000MsAndResetsOnSubsequentEdit`：修改 `InputText` -> 推进 999 ms 不触发 -> 再次修改重置计时 -> 推进 999 ms 仍不触发 -> 再推进 1 ms 触发 `ExecuteTranslateFromUser()`（验证编排器被调用且 `Origin == TextOrigin.Input`）。
   - `AutoTranslateDebounce_LanguageChangeAndSwap_FiresAfter1000Ms`：修改 `SourceLanguage`、`TargetLanguage`、执行 `SwapLanguagesCommand` 均在 1000 ms 后触发自动翻译。
   - `AutoTranslateDebounce_ManualTranslateImmediately_CancelsPendingTimer`：修改 `InputText` 后推进 500 ms，主动调用 `ExecuteTranslateFromUser()` 立即触发第 1 次；再推进 2000 ms 不再触发第 2 次。
   - `AutoTranslateDebounce_ProgrammaticApplyRequest_DoesNotScheduleDebounce`：`ApplyRequest(AutoTranslate = false)` 写入文本后推进 2000 ms 不触发；且若之前存在等待中的用户防抖计时，会被 `ApplyRequest` 取消。
   - `AutoTranslateDebounce_EmptyWhitespaceOrTooLong_DoesNotTriggerOrSetError`：清空文本、纯空白文本、超过 `MaxInputChars` 文本推进 2000 ms 均不触发且不主动弹出 `InputEmpty`/`InputTooLong` 错误。
   - `AutoTranslateDebounce_ImeComposing_SuppressesUntilCommit`：`SetImeComposing(true)` 后编辑文本推进 2000 ms 不触发；`SetImeComposing(false)` 后 999 ms 不触发、1000 ms 触发（含提交文本与预编辑文本相同的情况）。
   - `AutoTranslateDebounce_WindowHiddenOrVisibilityFalse_CancelsAndSuppresses`：编辑后调用 `OnWindowHidden()`（或 `OnWindowVisibilityChanged(false)`）推进 2000 ms 不触发；隐藏态下改文本不触发；重新 `OnWindowVisibilityChanged(true)` 后新编辑可正常 1000 ms 触发。
   - `AutoTranslateDebounce_Dispose_CancelsPendingTimerCleanly`：编辑后调用 `Dispose()`，再推进 2000 ms 不触发且不抛异常。
   - `AutoTranslateDebounce_EditDuringInFlightTranslation_CancelsCurrentAndSchedulesNext`：翻译进行中修改原文，立即取消原请求、标记旧卡片陈旧，并在 1000 ms 后发起新翻译。
   - `AutoTranslateDebounce_Stop_CancelsPendingTimer`：编辑后调用 `StopCommand`（或 `ExecuteStop`），取消等待中的防抖计时。
2. **运行确认失败**：
   ```bash
   dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~AutoTranslateDebounce"
   ```
3. **实现改动**：
   - 在 `TranslateWindowViewModel` 构造函数末尾加 `TimeProvider? timeProvider = null`，初始化 `_timeProvider = timeProvider ?? TimeProvider.System` 与 `_debounceTimer = _timeProvider.CreateTimer(OnDebounceTimerElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan)`。
   - 新增 `internal static readonly TimeSpan AutoTranslateDebounceDelay = TimeSpan.FromMilliseconds(1000);`、`_debounceGeneration`（`Interlocked.Increment` / `Volatile.Read`）、`_isWindowVisible`（默认 `true`）、`_isImeComposing`、`_pendingDebounceAfterIme`。
   - 新增 `internal void SetImeComposing(bool isComposing)` 与 `internal void OnWindowVisibilityChanged(bool isVisible)`。
   - 在 `OnUserInputChanged()` 调用 `ScheduleAutoTranslateDebounce()`；在 `ExecuteTranslateFromUser()`、`ExecuteStop()`、`ApplyRequest(...)`、`OnWindowHidden()`、`Dispose()` 调用 `CancelPendingAutoTranslate()`（`Dispose()` 额外释放 `_debounceTimer`）。
   - 在 `OnDebounceTimerElapsed` 中经 `_uiDispatcher.InvokeAsync` 回到 UI 线程后二次校验 generation、`_disposed`、`_isWindowVisible`、`!_isImeComposing`、非空且 `<= MaxInputChars`，通过后调用 `ExecuteTranslateFromUser()`；捕获退出期取消/释放异常避免未观察异常。
   - 在 `TranslateWindowPresenter.OnWindowIsVisibleChanged(bool isVisible)` 中调用 `_viewModel?.OnWindowVisibilityChanged(isVisible)`。
4. **运行验证通过**：
   ```bash
   dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~TranslateWindow"
   ```
5. **总控暂存、审查与快照**。

---

### Task 4: 重构 `TranslateWindow.xaml` 与 `TranslateWindow.xaml.cs`（§5.1、§5.2、§5.4）

**Files:**
- Modify: `src/Iris.App/Translation/TranslateWindow.xaml`
- Modify: `src/Iris.App/Translation/TranslateWindow.xaml.cs`

**Steps:**
1. **重构 `TranslateWindow.xaml`**：
   - 保持窗口根属性不变（`Width="{Binding WindowWidth, FallbackValue=460}"`、`MinWidth="280"`、`MaxWidth="1200"`、`Height="480"`、`MinHeight="300"`、`Topmost="{Binding Topmost, FallbackValue=False}"`、`ShowInTaskbar="False"`、`ExtendsContentIntoTitleBar="True"`，不设 `WindowBackdropType`）。
   - 保持外层零边距 `<Grid Background="{DynamicResource IrisAcrylicTintBrush}">` 与内层 `<DockPanel Margin="12,8,12,12">`（保护 `AcrylicWindowTests`）。
   - **顶栏（`DockPanel.Dock="Top"`, `Background="Transparent"`, `MouseLeftButtonDown="OnTitleBarMouseDown"`, `Margin="0,0,0,8"`）**：
     - 3 列 `Grid`（`Auto`, `*`, `Auto`）：
       - 列 0：水平 `StackPanel` 放 `SourceLangComboBox`（`Style="{StaticResource IrisGhostComboBox}"`, `MaxWidth="148"`, `AutomationProperties.Name="{x:Static app:UiText.AccessibilitySourceLanguage}"`）+ `DetectedSourceLanguageTextBlock`（`FontSize="12"`, `Foreground="{DynamicResource TextFillColorTertiaryBrush}"`, `TextTrimming="CharacterEllipsis"`, `MaxWidth="72"`, `Margin="4,0,0,0"`, `Visibility="{Binding HasDetectedSourceLanguage, Converter={StaticResource BoolToVis}}"`）。
       - 列 1：透明空白拖动区（`MinWidth="12"`, `Background="Transparent"`）。
       - 列 2：水平 `StackPanel` 放 `PinButton`（基于 `IrisIconButton`、`Width="28" Height="28"`、默认 `Pin16` + `TranslatePinWindow`，`DataTrigger Binding="{Binding IsPinned}" Value="True"` 时切换 `Pin16` `Filled="True"`、`AccentTextFillColorPrimaryBrush`、`SubtleFillColorSecondaryBrush`、`Content="{x:Static app:UiText.TranslateUnpinWindow}"`）与 `SettingsButton`（`Style="{StaticResource IrisIconButton}"`, `Width="28" Height="28"`, `Icon="{ui:SymbolIcon Settings16}"`, `Content="{x:Static app:UiText.TranslateSettings}"`）。
     - 删除标题字样与 `CloseButton`。
   - **主内容区（`ContentScrollViewer` 保持短窗 `Height < 440` 滚动与 `CardsScrollViewer.MinHeight = 160`）**：
     - **Row 0：源文本卡片（`Border Style="{StaticResource IrisCompactCard}"`）**：
       - 上部 `Grid`：透明无边框 `InputTextBox`（保持 `x:Name="InputTextBox"`, `AcceptsReturn="True"`, `TextWrapping="Wrap"`, `MinHeight="48"`, `MaxHeight="200"`, `VerticalScrollBarVisibility="Auto"`, `FontSize="{Binding FontSize, FallbackValue=14}"`, `PreviewKeyDown="OnInputTextBoxPreviewKeyDown"`）+ 底层/顶层非命中测试占位提示 `InputPlaceholderTextBlock`（`IsHitTestVisible="False"`, `Text="{x:Static app:UiText.TranslateInputPlaceholder}"`, `Foreground="{DynamicResource TextFillColorTertiaryBrush}"`, 仅 `InputLength == 0` 时可见）。
       - 下部底栏 `Grid`（`Margin="0,6,0,0"`, `MinHeight="24"`）：
         - 左侧：独立 `StopButton`（`ui:Button`, `Visibility="{Binding IsTranslating, Converter={StaticResource BoolToVis}}"`, `Command="{Binding StopCommand}"`, `Icon="{ui:SymbolIcon Stop16}"`, `Content="{x:Static app:UiText.TranslateStopButton}"`, `Padding="8,3"`, `FontSize="12"`）+ `InputValidationErrorTextBlock`（红字 `SystemFillColorCriticalBrush`, `Visibility="{Binding IsInputValidationError, Converter={StaticResource BoolToVis}}"`）+ `StatusMessageTextBlock`（次要灰字, `Visibility="{Binding HasVisibleStatus, Converter={StaticResource BoolToVis}}"`）。
         - 右侧：`InputCharCountTextBlock`（`MultiBinding StringFormat="{}{0} / {1}"`，默认 `TextFillColorTertiaryBrush`，`DataTrigger Binding="{Binding IsInputTooLong}" Value="True"` 时变红加粗）。
       - 删除 `TranslateButton`。
     - **Row 1：目标语言分割行（`Margin="0,10,0,8"`, `Grid` 4 列 `Auto, Auto, Auto, *`）**：
       - 列 0：`SwapLanguagesButton`（`Style="{StaticResource IrisIconButton}"`, `Width="28" Height="28"`, `Icon="{ui:SymbolIcon ArrowSwap16}"`, `Content="{x:Static app:UiText.TranslateSwapLanguages}"`, `Command="{Binding SwapLanguagesCommand}"`）。
       - 列 1：`TargetLanguageLabelTextBlock`（`Text="{x:Static app:UiText.TranslateTo}"`, `FontSize="12"`, `Foreground="{DynamicResource TextFillColorTertiaryBrush}"`, `VerticalAlignment="Center"`, `Margin="4,0,4,0"`）。
       - 列 2：`TargetLangComboBox`（`Style="{StaticResource IrisGhostComboBox}"`, `MaxWidth="160"`, `AutomationProperties.Name="{x:Static app:UiText.AccessibilityTargetLanguage}"`）。
       - 列 3：1px 水平分割线（`Border Height="1" Margin="8,0,0,0" VerticalAlignment="Center" Background="{DynamicResource DividerStrokeColorDefaultBrush}"`）。
     - **Row 2：结果区（`ScrollViewer x:Name="CardsScrollViewer"`）**：
       - 内含 `StackPanel`：
         - 全局非输入错误卡片 `GeneralErrorBorder`（`Style="{StaticResource IrisCompactCard}"`, `Visibility="{Binding HasGeneralError, Converter={StaticResource BoolToVis}}"`, `ErrorCircle16` 红图标 + `Error.Message` + 可选 `GoToSettingsCommand` 按钮）。
         - `ItemsControl`（`ItemsSource="{Binding Cards}"`）：每张卡片为 `Border Style="{StaticResource IrisCompactCard}"`（`HasStaleResults == True` 时 `Opacity="0.52"`），内部包含：
           - 顶部 2px 流式进度强调条（`Visibility="{Binding IsStreaming, Converter={StaticResource BoolToVis}}"`）；
           - 卡片头（左侧 `WrapPanel`：18×18 首字母徽章 `ProviderInitial`、`DisplayName`、`Model`、`ElapsedText`、缓存 `IrisTag`、`HasStaleResults` 时的 `TranslateLastResult` `IrisTag`；右侧图标按钮：`Copy` `IrisIconButton` `Copy16` 保留 `Content="{x:Static app:UiText.TranslateCopy}"` 且 `ToolTip`/`AutomationProperties.Name="{Binding CopyTooltip}"`，`Retry` `IrisIconButton` `ArrowClockwise16` 保留 `Content="{x:Static app:UiText.TranslateRetry}"`）；
           - 截断警示（`Warning16` + 文本）与单卡错误（`ErrorCircle16` + `ErrorMessage` + `GoToSettings` 按钮）；
           - 只读译文 `TextBox`（无译文且报错时折叠，避免多余空白）。
2. **重构 `TranslateWindow.xaml.cs`**：
   - 删除 `OnCloseButtonClick` 及 `OnWindowSizeChanged` 中已删除控件 `LanguageControlsGrid`/`LanguageActionsPanel` 的窄窗重排代码；保留 `ContentScrollViewer` / `CardsScrollViewer` 的 `shortWindow = e.NewSize.Height < 440` 高度规则与 `ReconstrainWindowPlacement()`，并在极矮窗时适当收紧 `InputTextBox.MaxHeight`（如 `shortWindow ? 96 : 200`）。
   - 保留 `OnTitleBarMouseDown`、`OnWindowPreviewKeyDown`（Esc）、`OnInputTextBoxPreviewKeyDown`（`Key.ImeProcessed`、Shift+Enter、Enter / Ctrl+Enter）及既有中文注释不改动。
   - 在构造函数注册 `TextCompositionManager` 的 `PreviewTextInputStart`、`PreviewTextInputUpdate`、`PreviewTextInput` 及 `InputTextBox.LostKeyboardFocus` 处理方法，同步驱动 `vm.SetImeComposing(...)`。
3. **验证编译与核心非改动测试**：
   ```bash
   dotnet build Iris.slnx -c Release
   dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~AcrylicWindowTests|FullyQualifiedName~LocalizationRenderingTests|FullyQualifiedName~OcrRenderingTests|FullyQualifiedName~TranslateWindowViewModelTests|FullyQualifiedName~TranslateWindowPlacementTests"
   ```
4. **总控暂存、审查与快照**。

---

### Task 5: 更新既有受影响测试并补充阶段 3 窗口 UI / 布局 / 交互测试

**Files:**
- Modify: `tests/Iris.App.Tests/Translation/TranslateWindowUiTests.cs`
- Modify: `tests/Iris.App.Tests/Ocr/OcrEntrypointTests.cs`

**Steps:**
1. **更新 `OcrEntrypointTests.cs`**（对应设计 §8.1）：
   - `RealWindow_OcrFillsSource_TranslationOnlyWhenRequested`（第 39-43 行）：不再查找已删除的 `TranslateButton`，改为校验 `window.FindName("TranslateButton").ShouldBeNull()` 后，通过 `s.Presenter.ViewModel!.TranslateCommand.CanExecute(null).ShouldBeTrue()` 与向 `InputTextBox` 发送 `Key.Enter`（或执行 `TranslateCommand`）触发翻译，继续断言 OCR 填入后未手动触发前不翻译、触发后正常翻译。
2. **更新并扩展 `TranslateWindowUiTests.cs`**（对应设计 §8.1、§8.2）：
   - 更新 `Layout_AtDifferentWidthsAndThemes_ControlsAreAccessibleAndNotClipped`：
     - 覆盖 `Width = 280`、`460`、`1200` × `Light` / `Dark` 主题（并在 `finally` 同步恢复 `Iris.Light`）；
     - 断言 `TranslateButton` 与 `CloseButton` 不存在（`FindName(...) == null`）；
     - 断言 `SourceLangComboBox`、`SwapLanguagesButton`、`TargetLangComboBox`、`PinButton`、`SettingsButton`、`InputTextBox`、`CardsScrollViewer` 均可见且在窗口可见边界内无裁剪（`Left >= 0`、`Right <= ActualWidth + 0.5`）。
   - 更新 `OnWindowHidden_RestoresTranslateAndStopButtonVisibilityAndCanExecute`：移除已删除的 `TranslateButton` 断言，保留并强化 `StopButton.Visibility`（翻译中 `Visible`，隐藏后 `Collapsed`）、`vm.IsTranslating`、`vm.TranslateCommand.CanExecute(null)`、`vm.StopCommand.CanExecute(null)` 断言。
   - 新增 UI 测试：
     - `TopBar_ShowsDetectedSourceLanguageOnlyWhenAutoAndResolved`：验证 `DetectedSourceLanguageTextBlock` 可见性与文字；
     - `PinButton_TogglesPinnedStateAndUpdatesAccessibleTooltip`：验证点击 `PinButton` 切换 `IsPinned` 并将 `Content`/`ToolTip`/`AutomationProperties.Name` 在 `TranslatePinWindow` 与 `TranslateUnpinWindow` 间切换；
     - `StaleResults_FadeResultCardsAndShowLastResultTag`：翻译出卡片后修改 `InputTextBox`，断言结果卡片容器 `Opacity < 1` 且卡片内出现 `UiText.TranslateLastResult` 标签；重新翻译开始后恢复 `Opacity == 1`；
     - `StopButton_VisibleOnlyWhileTranslating_ClickShowsStoppedStatusAndKeepsPartialCards`：翻译中点击 `StopButton`，验证 `StopButton` 隐藏、`StatusMessageTextBlock` 显示 `UiText.TranslateStopped`、既有卡片保留；
     - `EnterToTranslateFalse_RequiresCtrlEnter_AndCancelsPendingDebounce`：验证 `EnterToTranslate = false` 时普通 Enter 不触发、`Ctrl+Enter` 立即触发且取消防抖；
     - `EscapeKey_HidesWindowWhenUnpinned`：验证 Esc 关闭/隐藏行为保持不变。
3. **运行全量 `Translation`、`Ocr`、`Themes`、`Localization` 测试验证**：
   ```bash
   dotnet test tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter "FullyQualifiedName~Translate|FullyQualifiedName~Ocr|FullyQualifiedName~AcrylicWindowTests|FullyQualifiedName~Localization"
   ```
4. **总控暂存、审查与快照**。

---

### Task 6: 全量构建、7 程序集回归、`sdd-final-reviewer` 审查与本地化审计

**Steps:**
1. 执行完整 Release 构建、全量 7 测试程序集运行与本地化审计：
   ```bash
   dotnet build Iris.slnx -c Release
   dotnet test Iris.slnx -c Release --no-build
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/audit-localization.ps1
   ```
2. 派发 `sdd-final-reviewer` 对整个阶段 3 相对基线 `6fbe387` 的 `git diff` 做整阶段终审（规格符合度、线程安全、资源释放、无中文违例、边界宽度与短窗兼容、测试质量）。
3. 若发现问题，按 TDD 修复轮（`sdd-implementer` + `sdd-rereviewer`）闭环后重跑上述全量验证。
4. 更新 `STATUS.md` 并运行 `checkpoint.sh`。

---

### Task 7: 便携副本真机验证（§9.1、§9.2、§9.4）、完成报告撰写与提交确认

**Steps:**
1. **启动与内存基准对比（§9.4）**：运行 `build/measure-startup.ps1`，与阶段 2 后基线对比并原样记录输出（含 T-040 已知超标不属本单范围的说明）。
2. **便携副本真机运行与多状态截图**：
   - 将 `Release` 产物拷至 `%TEMP%\iris-t041-evidence\phase3-final\portable\`，放置 `portable.txt` 与独立配置；
   - 截图并亲自用视觉逐张复看：
     - 中文 / 英文 × 浅色 / 深色 × 默认 `460×480`、窄窗 `280×480`、短窗 `280×300`；
     - 空输入占位态、翻译中（`StopButton` 可见）、完成态（含检测语言灰字/耗时/缓存标签/图标复制重试）、编辑后陈旧态（「上次结果」+ 半透明旧卡片）、钉住态、无配置/错误态、失焦回退态与 DWM `DWMWA_SYSTEMBACKDROP_TYPE`（`3`）读数。
   - 校验结束前仅关闭自己启动的便携进程，确认真实 `%APPDATA%\Iris` 与注册表未变动。
3. **撰写第 5 节《阶段 3 完成报告》**（按 `GEMINI.md`）：
   - 粘贴原始构建、全量 7 程序集测试、`audit-localization.ps1`、`measure-startup.ps1` 输出；
   - 列出所有改动的既有测试与与工单/设计的偏差；
   - 明确标注「未验证」项（Windows 10 纯色回退真机环境、真实中文输入法 IME 组字真机交互）。
4. 更新 `phase-3-work/STATUS.md`、运行 `checkpoint.sh`，向用户汇报全部证据并**等待用户确认后再执行阶段 3 单次本地提交**。

---

## 5. 阶段 3 完成报告

- **日期**：2026-11-25
- **工作分支**：`codex/t041-acrylic-ui`（基于阶段 2 提交 `6fbe387`，随本次提交一并入库本计划与完成报告文档，共 15 个文件；仅本地提交，不 `push`）
- **改动范围（15 个文件）**：
  - `docs/superpowers/plans/2026-11-25-acrylic-ui-phase-3-translate-window.md`（阶段 3 实施计划与完成报告）
  - `build/localization/app-catalog.json`、`src/Iris.App/Resources/Strings.resx`、`src/Iris.App/Resources/Strings.en-US.resx`、`src/Iris.App/UiText.cs`（新增 `TranslateTo`，同步更新 `TranslateInputPlaceholder`）
  - `src/Iris.App/Themes/Iris.Styles.xaml`（新增 `IrisCompactCard`、`IrisGhostComboBox`、`IrisBorderlessTextBox`）
  - `src/Iris.App/Translation/ProviderResultCardViewModel.cs`（新增 `ProviderInitial`、`HasError` 及通知）
  - `src/Iris.App/Translation/TranslateWindowViewModel.cs`（新增 `DetectedSourceLanguageText`、`HasDetectedSourceLanguage`、`HasStaleResults`、`IsInputTooLong`、`IsInputValidationError`、`HasGeneralError`、`HasVisibleStatus`，基于可注入 `TimeProvider` 的 1000ms 停顿自动翻译防抖与 IME / 窗口可见性 / 5 条不触发条件 / QPC 提前回调重排逻辑，以及 `SwapLanguagesCommand.RaiseCanExecuteChanged()` 即时刷新）
  - `src/Iris.App/Translation/TranslateWindowPresenter.cs`（`OnWindowIsVisibleChanged` 通知 `TranslateWindowViewModel.OnWindowVisibilityChanged`）
  - `src/Iris.App/Translation/TranslateWindow.xaml`、`src/Iris.App/Translation/TranslateWindow.xaml.cs`（按 §5.1–§5.4 重构顶栏、原文卡片、目标语言分割行、结果卡片列表及 `TextCompositionManager` IME 监听）
  - `tests/Iris.App.Tests/Ocr/OcrEntrypointTests.cs`、`tests/Iris.App.Tests/Themes/IrisStyleResourceTests.cs`、`tests/Iris.App.Tests/Translation/TranslateWindowUiTests.cs`、`tests/Iris.App.Tests/Translation/TranslateWindowViewModelTests.cs`

### 5.1 原始构建、全量 7 程序集回归与本地化审计输出

1. **`dotnet build Iris.slnx -c Release`**：
```text
已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:02.90
```

2. **`dotnet test Iris.slnx -c Release --no-build`**（7 个测试程序集全量运行）：
```text
测试运行摘要: 已通过!
  F:\Iris\tests\Iris.Platform.Windows.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Platform.Windows.Tests.dll (net10.0|x64) 已通过 (4s 645ms)
  F:\Iris\tests\Iris.Core.Tests\bin\Release\net10.0\Iris.Core.Tests.dll (net10.0|x64) 已通过 (9s 210ms)
  F:\Iris\tests\Iris.Providers.Tests\bin\Release\net10.0\Iris.Providers.Tests.dll (net10.0|x64) 已通过 (4s 611ms)
  F:\Iris\tests\Iris.ArchitectureTests\bin\Release\net10.0-windows10.0.19041.0\Iris.ArchitectureTests.dll (net10.0|x64) 已通过 (2s 350ms)
  F:\Iris\tests\Iris.Maintenance.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Maintenance.Tests.dll (net10.0|x64) 已通过 (6s 345ms)
  F:\Iris\tests\Iris.Infrastructure.Tests\bin\Release\net10.0\Iris.Infrastructure.Tests.dll (net10.0|x64) 已通过 (8s 426ms)
  F:\Iris\tests\Iris.App.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.App.Tests.dll (net10.0|x64) 已通过 (4m 06s 132ms)

  总计: 4008
  失败: 0
  成功: 4006
  已跳过: 2
  持续时间: 4m 06s 693ms
```
（2 条跳过仍为 `Iris.Platform.Windows.Tests` 环境限制的隔离剪贴板 Window Station 测试；较阶段 2 的 3968 条通过净增 38 条新测试，全部通过。）

3. **`powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/audit-localization.ps1`**：
```text
AUDIT_EXIT=0
Scanned C# and XAML; Chinese entries including comments: 1168
Compiled resource values matched after XML newline normalization; composite formats parsed: 1240
{"sourceEntries": 1168, "commentEntries": 950, "reviewedNonUiEntries": 218, "resourceCounts": {"App": 467, "Core": 148, "Maintenance": 5}, "resourceErrors": [], "englishNativeNameExceptions": {"App:LanguageChineseNative": "简体中文"}, "exceptionManifestSha256": "2d620105575400696d1f567460d883cf050ff511b49b6f64d5ea0b5192eb9a75"}
```

### 5.2 启动与内存基准对比（`build/measure-startup.ps1`）

阶段 2 后基线（`Runs=3`）：
- Run 1（启动过第二实例、`VisibleWindows=1`）：`StartupMs=897.4  WorkingSetMB=165.0  PrivateBytesMB=76.5`
- Run 2–3（`VisibleWindows=0`）：`WorkingSetMB=124.7 / 124.8  PrivateBytesMB=54.6 / 54.7`
- 均值：`StartupMs=902.8  WorkingSetMB=138.2  PrivateBytesMB=61.9`

阶段 3 最终测量原始输出（前两次 `measure-startup.ps1` 实测常驻态波动亦见下）：
```text
Iris measure-startup | 2026-11-25 13:38:10 | Microsoft Windows NT 10.0.26300.0 | Configuration=Release Runs=3 IdleSeconds=30
System CPU load at start: 30% (startup-time NFR is only meaningful below 30%)
Binary: F:\Iris\src\Iris.App\bin\Release\net10.0-windows10.0.19041.0 (built 2026-11-25 13:33:41)
--- second instance (run 1) ---
ExitedWithin5s : True
ElapsedMs      : 137
ExitCode       : 0
LogLinesAdded  : 0
--- per run ---
Run StartupMs VisibleWindows WorkingSetMB PrivateBytesMB ExitCode ShutdownLogLines
  1     893.3              1        188.2           93.3 timeout                 3
  2     888.8              0        124.7           54.3 timeout                 3
  3     922.8              0        142.6           70.9 timeout                 3
--- averages --- StartupMs=901.6  WorkingSetMB=151.8  PrivateBytesMB=72.8
--- NFR (ARCH 1.2): StartupMs <= 1500; PrivateBytesMB <= 100; WorkingSetMB <= 160; VisibleWindows = 0; ExitCode = 0; ShutdownLogLines >= 1 ---
```
（另附同日稍早一次 3 轮测量 Run 2–3 的常驻态为 `WorkingSetMB=135.5 / 117.1`、`PrivateBytesMB=51.9 / 36.3`。）
- **客观说明（不只看均值）**：
  - 第 1 轮（启动过第二实例、`VisibleWindows=1`）`WorkingSetMB=188.2 MB` / `PrivateBytesMB=93.3 MB`，**Working Set 高于 160 MB 阈值**（阶段 2 同一轮为 `165.0 MB / 76.5 MB`，同样高于 160 MB，且 T-040 已知 Working Set 超标不属本单处理范围）；
  - 多次常驻态测量（`VisibleWindows=0`）波动较大：`PrivateBytesMB` 在 `36.3–70.9 MB` 之间浮动（`WorkingSetMB` 在 `117.1–142.6 MB` 之间浮动）；
  - 3 轮均值 `StartupMs=901.6 ms`、`WorkingSetMB=151.8 MB`、`PrivateBytesMB=72.8 MB`，二次实例 `137 ms` 退出码 `0`。

### 5.3 审查闭环、提交前修补与便携副本真机验证（12 张真机截图逐张复看）

- **SDD 审查闭环**：Task 1–5 均经 `sdd-reviewer`（及对应修复轮 + `sdd-rereviewer` `all_addressed`）；整阶段经 `sdd-final-reviewer` 审出 6 项问题（含真实 Windows QPC vs `TickCount64` 提前回调丢防抖、窄窗底栏 `StackPanel` 截断、`ApplyRequest` 边界等），与真机视觉缺陷一并修复并通过 `sdd-rereviewer` `all_addressed`：
  1. 新增 `IrisBorderlessTextBox` 样式消除 WPF-UI 默认 `TextBox` 聚焦白/黑底盒与蓝底线，将 `InputPlaceholderTextBlock` 置于 `InputTextBox` 之上并在原文卡片外框添加 `IsKeyboardFocusWithin` 聚焦边框；
  2. 修复 WPF-UI 4.3.0 `SymbolRegular.ArrowSwap16`（`0xF02A4` 截断为 `ʤ`）为 BMP 码位 `ArrowSwap20`（`0xF18D`，`FontSize=14`）；
  3. 放宽 `TargetLangComboBox` `MaxWidth` 至 `184`，使英文 `Chinese (Simplified)` 在 `460px` 与 `280px` 均完整不截断；
  4. 补充 `ProviderResultCardViewModel.HasError` 及通知，修复成功译文卡片显示多余红色 `(!)` 图标的问题。
- **提交前复核修补（全部通过 TDD / 重截并亲自复看）**：
  1. **浅色灰字对比度**：将 `TranslateWindow.xaml` 承载信息的 4 处灰字（检测源语言 `DetectedSourceLanguageTextBlock`、「译为」`TargetLanguageLabelTextBlock`、字数计数 `InputCharCountTextBlock`、结果卡片耗时 `ElapsedText`）由 `TextFillColorTertiaryBrush` 改为 `TextFillColorSecondaryBrush`，占位提示 `InputPlaceholderTextBlock` 保持 `TextFillColorTertiaryBrush`；重截并复看 `01`、`02`。
  2. **过期卡片仅内容变淡、`上次结果` 徽章不透明**：将 `HasStaleResults` 的 `0.52` 透明度触发器由整张结果卡片外框下沉到卡片内容元素（徽标、服务名、模型、耗时、缓存标签、操作按钮、告警/错误区、译文正文），`上次结果` 标签及其全部视觉祖先保持 `Opacity="1.0"`；重截并复看 `03`，并更新 `StaleResults_FadeResultCardsAndShowLastResultTag` 断言。
  3. **交换语言按钮禁用态根因与修复**：查明根因为 `TranslateWindowViewModel.cs` 仅在 `SourceLanguage`、`TargetLanguage`、`ApplyRequest` 与 `HandleTranslationEventAsync`（`TranslationStarted`）触发了 `OnPropertyChanged(nameof(CanSwapLanguages))`，**未调用 `((RelayCommand)SwapLanguagesCommand).RaiseCanExecuteChanged()`**，导致 `auto` 源语言在翻译开始解析出实际语言后，按钮停留在禁用浅灰直到下一次输入/窗口事件触发 `CommandManager.RequerySuggested`。已在全部 5 处状态变化点（含 `ExecuteSwapLanguages`）补齐 `RaiseCanExecuteChanged()`，按先红后绿新增单元测试 `SwapLanguagesCommand_RaisesCanExecuteChanged_WhenResolvedOrLanguagesChange` 与 UI 断言，重截 `02`、`06`、`12` 均已即时恢复正常可用态（`04`/`05` 因 `HOLD_STREAM` 示例文本未被识别为确定源语言、`CanSwapLanguages == false` 而保持禁用态，符合预期）。
  4. **流式进度条 2px 始终占位**：将结果卡片顶部 2px 流式条由 `Visibility` 折叠改为 `Background` 在 `Transparent` 与 `AccentFillColorDefaultBrush` 之间切换（内边距同步调整为 `12,8,12,10`），消除 `04`（流式中）与 `05`（停止/完成）之间 2px 卡片高度跳动，并在 UI 测试中断言停流前后卡片渲染高度一致。
- **便携副本真机运行（`%TEMP%\iris-t041-evidence\phase3-final\screenshots`，12 张 PNG）**：
  - 仅在 `%TEMP%` 独立便携目录（含 `portable.txt`）运行，使用本机 `127.0.0.1` loopback SSE 模拟服务（零外部联网，零真实 API Key），完成 12 次本地模拟翻译请求，退出后 `Remaining Iris.App: 0`，真实 `%APPDATA%\Iris` 与 `HKCU\...\Run` 完全未变动（`True`）。
  - 实测 DWM 材质：`TranslateWindow` 浅色与深色均返回 `hr=0x00000000 DWMWA_SYSTEMBACKDROP_TYPE=3`（Acrylic）。
  - **真实失焦态复测（`08-zh-light-460-unfocused.png`）**：首版截图因仅对无边框背景窗调用 `SetForegroundWindow` 未真正夺走前台焦点，导致 `08` 与 `02` 几乎一致；已改用独立可见置顶窗口实际鼠标点击夺焦，脚本实测 `Deactivated=True`（`TranslateHwnd != ForegroundHwnd`），重截并复看 `08-zh-light-460-unfocused.png` 确认输入框蓝色焦点描边已消失且 DWM 亚克力切换为中性纯色失焦回退外观。
  - 12 张截图（`01`–`12`）均已亲自打开复看，关键交互与视觉状态全部对齐 `translate-layout-v3.html`。

### 5.4 修改的既有测试、偏差、遗留与未验证说明

- **修改的既有测试（仅 2 处，均符合设计文档 §8.1）**：
  1. `tests/Iris.App.Tests/Ocr/OcrEntrypointTests.cs`（`RealWindow_OcrFillsSource_TranslationOnlyWhenRequested`）：移除已删除的 `TranslateButton` 查找，改为断言 `TranslateButton` 与 `CloseButton` 不存在并通过 `TranslateCommand.Execute(null)` 触发翻译。
  2. `tests/Iris.App.Tests/Translation/TranslateWindowUiTests.cs`（`Layout_AtDifferentWidthsAndThemes_ControlsAreAccessibleAndNotClipped` 与 `OnWindowHidden_RestoresTranslateAndStopButtonVisibilityAndCanExecute`）：移除 `TranslateButton` 旧引用，断言 `TranslateButton` / `CloseButton` 已移除，扩展 `1200 / 460 / 280 / 280×300` 双向边界校验并补全阶段 3 UI/交互回归测试。
- **与设计/工单的偏差**：
  1. 按计划预登记裁决 P3-2 保留 2 个已无界面使用者的旧资源键（`TranslateButton`、`TranslateClose`）以避免资源审计/跨阶段历史波动；
  2. 新增共用样式 `IrisBorderlessTextBox`（因真机截图发现 WPF-UI 默认 `TextBox` 聚焦底盒与底线遮挡占位文本）；
  3. 交换按钮图标采用 `ArrowSwap20`（`0xF18D`）替代初稿 `ArrowSwap16`（WPF-UI 4.3.0 码位 `0xF02A4` 截断为 `ʤ`）。
- **已知遗留**：
  1. **280px 极窄宽度下两张结果卡片标题折行不一致**（见 `06-zh-light-280x480-narrow.png`、`12-en-light-280x480-completed.png`）：因卡片头部元信息使用水平 `WrapPanel` 按内容实际宽度折行，较长服务名/模型（如 `DeepSeek` + `deepseek-chat`）在模型处折行，较短服务名/模型（如 `OpenAI` + `gpt-4o-mini`）在耗时处折行，造成相邻两张卡片标题栏折行位置不一致；无截断，记为后续视觉细化遗留项。
- **未验证项（显式标注）**：
  1. **Windows 10 纯色回退真机环境**（未在真实 Windows 10 宿主机验证；纯色回退字典切换路径已由自动化主题测试覆盖）；
  2. **真实中文输入法 IME 组字真机交互**（未进行人工真机 IME 键入；`TextCompositionManager` 接线、`LostKeyboardFocus` 恢复与 ViewModel IME 闸门逻辑均已由单元/UI 测试覆盖）。
