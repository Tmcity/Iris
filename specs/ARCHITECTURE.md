# Iris 架构设计说明书

> 版本：v1.0 · 2026-09-25 · 状态：**已冻结（首期）**
> 配套规范：`specs/CONTRACTS.cs`（契约唯一真相源）· `specs/DB_SCHEMA.sql` · `specs/settings.example.json`
> 读者：本地 IDE 开发工程师。**动手写任何代码之前，请完整阅读 §0、§4、§15、§16。**

---

## 0. 给开发者的十条铁律

1. **契约优先**：接口、record、枚举一律以 `specs/CONTRACTS.cs` 为准。不得自行增删改签名；确需变更，写入 `tasks/CONTRACT_CHANGE_REQUESTS.md`，等架构师批复。
2. **依赖方向不可逆**：`Iris.Core` 不引用任何项目、UI 框架或 Win32（§4.2）。有架构测试守护，违反即红。
3. **一次只做一个工单**：只实现 `tasks/current_task.md` 列出的范围。发现范围外的问题记到工单的“遗留问题”一节，不要顺手改。
4. **不写死密钥与地址**：API Key 只存 `ISecretStore`；日志、异常消息、`ToString()` 里不得出现 Key 或 `Authorization` 头。
5. **全链路异步 + 可取消**：所有 I/O 方法 `async`，末参 `CancellationToken`；禁止 `.Result`、`.Wait()`、`async void`（WPF 事件处理器除外，且必须 try/catch）。
6. **已知失败抛 `IrisException(ErrorCode)`**；用户取消抛 `OperationCanceledException`；禁止吞异常（至少记日志）。
7. **UI 线程纪律**：WPF 对象只在 UI 线程访问；UIAutomation、网络、磁盘 I/O 不在 UI 线程执行；钩子回调里只入队。
8. **坐标一律物理像素**：跨层传递只用 `PixelPoint`/`PixelRect`，只在 View 层换算为 WPF DIP（§10.5）。
9. **测试随代码提交**：Core / Providers / Infrastructure 的每个公开行为都要有单元测试；外部 HTTP 一律用 MockHttp，**测试中禁止真实联网**。
10. **完成前自检**：`dotnet build -c Release` 零警告，`dotnet test` 全绿，按工单“验收标准”逐条自测后再提交。

---

## 1. 产品概述

Iris 是一个 Windows 桌面翻译工具（对标 TTime / Pot），常驻系统托盘，通过全局热键、悬浮球、划词图标触发：

| 功能 | 触发方式 | 流程摘要 |
|---|---|---|
| 输入翻译 | 热键 / 托盘 / 悬浮球 | 打开翻译窗口 → 用户输入 → Enter 翻译 |
| 划词翻译 | 热键；可选“划选后显示图标 / 直接翻译” | 读取前台应用选中文本 → 翻译窗口 |
| 截图翻译 | 热键 / 托盘 / 悬浮球 | 冻结屏幕 → 框选 → OCR → 翻译窗口 |
| 截图 OCR | 热键 / 托盘 | 冻结屏幕 → 框选 → OCR → OCR 结果窗口（可编辑、复制、转翻译） |
| 静默 OCR | 热键 | 框选 → OCR → 直接复制到剪贴板 + 通知 |
| 剪贴板翻译 | 热键 | 读取剪贴板文本 → 翻译窗口 |
| 悬浮球 | 常驻悬浮窗 | 单击执行配置的动作；右键菜单；拖入文本即翻译 |

翻译由大模型 API 完成（OpenAI 兼容协议覆盖大多数国内外服务 + Anthropic 原生），**多个服务并行、流式输出、结果卡片并列展示**。OCR 由在线 API（百度智能云）完成，Windows 本地 OCR 作为离线兜底。

### 1.1 首期范围与非目标

- **首期（本文档覆盖）**：上表全部功能 + 设置、历史、代理、单实例/命令行、安装包与更新。
- **非目标（Icebox，见 BACKLOG 末尾）**：TTS 朗读、单词词典模式、贴图（截图钉在屏幕上）、截图时自动识别窗口边界、腾讯云 OCR、多模态大模型 OCR、DeepL 等传统翻译 API、ARM64 构建、macOS/Linux。

### 1.2 非功能指标（NFR）

| 指标 | 目标 |
|---|---|
| 冷启动到托盘可用 | ≤ 1.5 s |
| 热键 → 截图覆盖层可见 | ≤ 150 ms（单 4K 屏） |
| 热键 → 翻译窗口可见（划词） | ≤ 300 ms（不含选区读取超时） |
| 首个译文字符（流式） | 取决于服务；Iris 自身附加开销 ≤ 50 ms |
| 空闲内存 | ≤ 120 MB（Working Set） |
| 空闲 CPU | ≈ 0%（开启鼠标钩子时 < 0.5%） |
| 崩溃 | 未处理异常全部记录日志并提示，不得静默退出 |

---

## 2. 关键架构决策（ADR 摘要）

| # | 决策 | 理由 | 放弃的方案 |
|---|---|---|---|
| ADR-01 | **.NET 10 LTS + WPF** | LTS 支持到 2028-11；WPF 对透明/无焦点/置顶窗口、HwndSource、DPI 的控制最成熟 | WinUI 3（透明异形窗口、全局覆盖层支持弱）；.NET 9（2026-11 结束支持） |
| ADR-02 | **Clean Architecture（端口-适配器）**，业务流程（Workflows）放在 `Iris.Core` | 最核心的编排逻辑可脱离 Windows 做单元测试；UI 与 Win32 可替换 | 传统三层 / 逻辑写在 ViewModel 里 |
| ADR-03 | **所有触发源统一路由到 `IActionDispatcher`** | 热键、托盘、悬浮球、划词图标、命令行共享同一套流程，没有重复代码 | 每个入口各写一套 |
| ADR-04 | **Provider = Profile 实例**（用户可添加多个同类服务） | 同一协议可接 DeepSeek、通义等多家；卡片并列对比 | 每种服务一个写死的类 |
| ADR-05 | **OpenAI 兼容协议手写 HttpClient + SSE**；**Anthropic 用官方 `Anthropic` NuGet SDK** | 兼容服务之间差异大（reasoning_content、`<think>`、max_tokens 字段名），手写可控；Anthropic 有官方 C# SDK，直接用 SDK 最可靠 | 用 OpenAI 官方 SDK 对接所有兼容服务（容错性差） |
| ADR-06 | **流式事件模型**：`IAsyncEnumerable<TranslationEvent>` | 多服务交错输出、天然支持取消，UI 用 `await foreach` 消费 | 回调 / IObservable（Rx 学习成本高） |
| ADR-07 | **密钥与设置分离**：`settings.json` 明文可导出，`secrets.dat` 用 DPAPI 加密 | 设置可分享/备份而不泄露 Key | Key 加密后内嵌在 settings.json |
| ADR-08 | **截图 = 先冻结整屏再框选**，每个显示器一个覆盖窗口 | 选区期间画面静止；多屏混合 DPI 下单一跨屏 WPF 窗口会错位 | 单窗口覆盖整个虚拟桌面 |
| ADR-09 | **选区读取：UIAutomation 优先，模拟 Ctrl+C 兜底** | UIA 不污染剪贴板；很多应用不支持 UIA TextPattern，需要剪贴板兜底 | 仅剪贴板 |
| ADR-10 | **SQLite + Dapper**（历史），**JSON 文件**（设置） | 零部署、FTS5 支持中文检索；设置需要人类可读 | EF Core（对这个规模太重）、LiteDB |
| ADR-11 | **Velopack** 打包与自动更新 | 支持增量更新、自动安装 .NET 桌面运行时、无需管理员权限 | MSIX（签名与沙箱限制多）、ClickOnce |

---

## 3. 技术栈

> 版本号：在 T-001 中以 NuGet 上**当时最新的稳定版**写入 `Directory.Packages.props`（集中包管理），之后升级需单独提工单。

| 领域 | 选型 | 用于 |
|---|---|---|
| 运行时 / SDK | .NET 10 SDK（`global.json` 锁定 10.0.1xx，`rollForward: latestFeature`） | 全部 |
| UI | WPF（`UseWPF`）+ **WPF-UI**（`WPF-UI`，Fluent 风格控件 / Mica / 主题） | Iris.App |
| MVVM | **CommunityToolkit.Mvvm**（源生成器 `[ObservableProperty]` / `[RelayCommand]`） | Iris.App |
| 托盘 | **H.NotifyIcon.Wpf** | Iris.App |
| 宿主 / DI / 配置 | **Microsoft.Extensions.Hosting**（Generic Host） | Iris.App（组合根） |
| 日志 | **Serilog** + Serilog.Extensions.Hosting + Serilog.Sinks.File（按天滚动，保留 7 天） | 全部（只依赖 `Microsoft.Extensions.Logging.Abstractions`） |
| HTTP | IHttpClientFactory + **Microsoft.Extensions.Http.Resilience** | Iris.Providers |
| Anthropic | **Anthropic**（官方 C# SDK） | Iris.Providers |
| Win32 互操作 | **Microsoft.Windows.CsWin32**（源生成 P/Invoke，`NativeMethods.txt` 声明） | Iris.Platform.Windows |
| UIAutomation | `UIAutomationClient` COM（通过 `Interop.UIAutomationClient` 或 CsWin32 生成） | Iris.Platform.Windows |
| 本地 OCR / 图像 | WinRT `Windows.Media.Ocr`、`Windows.Graphics.Imaging`（TFM `net10.0-windows10.0.19041.0`） | Iris.Platform.Windows |
| 数据库 | **Microsoft.Data.Sqlite** + **Dapper** | Iris.Infrastructure |
| 打包更新 | **Velopack** | Iris.App / build |
| 单元测试 | **xUnit v3**、**NSubstitute**、**Shouldly**、**RichardSzalay.MockHttp** | tests/* |

**最低系统**：Windows 10 2004（19041）x64。**DPI**：Per-Monitor V2（`app.manifest`）。

---

## 4. 分层架构

### 4.1 分层视图

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ Iris.App  (WPF, 组合根)                                                        │
│  Views / ViewModels / Controls · Tray · Presenters(实现 Presentation 端口)      │
│  ActionDispatcher · AppBootstrapper · IPC Server · ScreenshotService(覆盖层)    │
└───────┬──────────────────┬──────────────────────┬─────────────────┬──────────┘
        │                  │                      │                 │
        ▼                  ▼                      ▼                 ▼
┌───────────────┐ ┌──────────────────┐ ┌──────────────────────┐ ┌──────────────┐
│ Iris.Providers│ │Iris.Infrastructure│ │ Iris.Platform.Windows│ │  (第三方库)    │
│ OpenAI 兼容    │ │ JSON 设置         │ │ 热键 / 鼠标钩子        │ │              │
│ Anthropic(SDK)│ │ SQLite 历史       │ │ 选区读取 / 剪贴板       │ │              │
│ 百度 OCR       │ │ AppPaths          │ │ 屏幕抓取 / 图像处理     │ │              │
│ SSE / 错误映射  │ │ 设置迁移           │ │ Windows OCR / DPAPI   │ │              │
└───────┬───────┘ └────────┬─────────┘ └──────────┬───────────┘ └──────────────┘
        │                  │                      │
        └──────────────────┴──────────┬───────────┘
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│ Iris.Core  (net10.0, 零平台依赖)                                                │
│  Models · Errors · Settings · 端口接口(Platform/Presentation/Storage/Security)  │
│  领域服务实现：TranslationOrchestrator · OcrService · LanguageDetector ·          │
│              TargetLanguageResolver · PromptRenderer · TextPreprocessor ·       │
│              OcrTextNormalizer · TranslationCache · ProviderRegistry            │
│  Workflows：各 IrisAction 的 IActionHandler                                      │
└──────────────────────────────────────────────────────────────────────────────┘
```

### 4.2 项目与依赖规则（架构测试强制）

| 项目 | TFM | 允许引用的 Iris 项目 | 允许的外部依赖 |
|---|---|---|---|
| `Iris.Core` | `net10.0` | 无 | 仅 `Microsoft.Extensions.*.Abstractions`（Logging / DependencyInjection / Options） |
| `Iris.Providers` | `net10.0` | Core | Microsoft.Extensions.Http(.Resilience)、Anthropic |
| `Iris.Infrastructure` | `net10.0` | Core | Microsoft.Data.Sqlite、Dapper |
| `Iris.Platform.Windows` | `net10.0-windows10.0.19041.0` | Core | CsWin32、UIAutomation；**不启用 UseWPF / UseWindowsForms** |
| `Iris.App` | `net10.0-windows10.0.19041.0`，`UseWPF`，`WinExe` | 全部 | WPF-UI、CommunityToolkit.Mvvm、H.NotifyIcon.Wpf、Hosting、Serilog、Velopack |

Core 程序集禁止引用：`PresentationFramework`、`PresentationCore`、`WindowsBase`、`System.Windows.Forms`、`Microsoft.Windows.SDK.NET`、任何 `Iris.*`。

### 4.3 为什么 Workflows 在 Core

`ScreenshotTranslateHandler` 等流程只依赖端口接口（`IScreenshotService`、`IOcrService`、`ITranslateWindowPresenter` …），因此可以在 `Iris.Core.Tests` 里用 NSubstitute 完整测试“截图取消 / OCR 失败 / 无文字 / 成功”的所有分支，而不需要启动 WPF。

---

## 5. 模块职责

### 5.1 Iris.Core

| 目录 | 内容 | 实现者 |
|---|---|---|
| `Errors/` | `ErrorCode`、`ErrorInfo`、`IrisException`、`ErrorMessages`（ErrorCode → 默认中文提示的静态表） | Core |
| `Languages/` | `LanguageCodes`、`LanguageCatalog`（`ILanguageCatalog` 实现，含 §7.3 的语言表）、`ScriptLanguageDetector`、`TargetLanguageResolver` | Core |
| `Imaging/` | `PixelPoint/PixelRect/ImageData/ImageConstraints`，`IImagePreprocessor`（端口） | 端口，Platform 实现 |
| `Common/` | `ProviderDescriptor`、`ProviderCredentials`、`ProviderTestResult`、`PagedResult<T>` | — |
| `Translation/` | 模型、事件、`ITranslationProvider` 等接口；`TranslationOrchestrator`、`TranslationProviderRegistry`、`PromptRenderer`、`PromptTemplateProvider`、`LruTranslationCache`、`TextPreprocessor` | Core |
| `Ocr/` | 模型与接口；`OcrService`、`OcrProviderRegistry`、`OcrTextNormalizer` | Core |
| `Actions/` | `IrisAction`、`ActionContext`、`IActionHandler`、`IActionDispatcher`（端口，App 实现）、IPC 契约 | — |
| `Settings/` | `AppSettings` 全部配置 record、Profile、预设、`ISettingsService`、`ISettingsMigration` | 端口，Infrastructure 实现 |
| `Security/` | `ISecretStore`、`SecretKeys` | 端口，Platform 实现 |
| `Storage/` | `IAppPaths`、历史模型、`ITranslationHistoryRepository`、`IDatabaseInitializer` | 端口，Infrastructure 实现 |
| `Platform/` | `IDesktopContext`、`IHotkeyService`、`ISelectionTextReader`、`IMouseSelectionMonitor`、`IClipboardService`、`IScreenCapturer`、`IAutoStartService` | 端口，Platform 实现 |
| `Presentation/` | `ITranslateWindowPresenter`、`IScreenshotService`、`IOcrResultPresenter`、`INotificationService`、`ISettingsWindowPresenter`、`IHistoryWindowPresenter`、`IFloatingBallController`、`IAppLifetime` | 端口，App 实现 |
| `Workflows/` | 每个 `IrisAction` 一个 `IActionHandler` | Core |
| `Serialization/` | `IrisJson` | Core |
| `DependencyInjection/` | `AddIrisCore(this IServiceCollection)` | Core |

### 5.2 Iris.Providers

| 目录 | 内容 |
|---|---|
| `Http/` | `HttpClientNames`、`ProviderJson`、`SseEvent` + `SseParser`（`static IAsyncEnumerable<SseEvent> ReadEventsAsync(Stream, CancellationToken)`）、`HttpErrorMapper`（HTTP 状态码 / 网络异常 → `IrisException`，§11.2）、`DynamicWebProxy`（§12.4） |
| `Translation/OpenAICompatible/` | `OpenAICompatibleTranslationProvider`（实现 `ITranslationProvider` + `IModelCatalogSource`）、Factory、`ThinkTagStripper`（流式剥离 `<think>`）、`Wire/` |
| `Translation/Anthropic/` | `AnthropicTranslationProvider`（实现 `ITranslationProvider` + `IModelCatalogSource`）、Factory |
| `Ocr/Baidu/` | `BaiduOcrProvider`、`BaiduTokenCache`、`BaiduErrorMapper`、`BaiduLanguageMap`、Factory、`Wire/` |
| `DependencyInjection/` | `AddIrisProviders(this IServiceCollection)`：注册命名 HttpClient 与各 Factory |

### 5.3 Iris.Infrastructure

`Settings/JsonSettingsService`、`Settings/Migrations/*`、`Storage/AppPaths`、`Storage/SqliteConnectionFactory`、`Storage/DatabaseInitializer`、`Storage/SqliteHistoryRepository`、`DependencyInjection/AddIrisInfrastructure`。

### 5.4 Iris.Platform.Windows

| 目录 | 内容 |
|---|---|
| `Interop/` | `NativeMethods.txt`（CsWin32 声明）、`Win32Helpers`（错误码、句柄安全包装） |
| `Hotkeys/` | `HotkeyService`（隐藏消息窗口 + `RegisterHotKey`）、`KeyNameMapper`（WPF Key 名 ↔ VK，用内置表，不引用 WPF） |
| `Hooks/` | `MouseSelectionMonitor`（专用线程 + `WH_MOUSE_LL`）、`GestureRecognizer`（纯逻辑，可单测） |
| `Selection/` | `UiaSelectionReader`、`ClipboardSelectionReader`、`CompositeSelectionReader`（实现 `ISelectionTextReader`）、`KeyboardSimulator`（`SendInput`） |
| `Clipboard/` | `Win32ClipboardService`、`ClipboardSnapshot`（内部类型） |
| `Capture/` | `GdiScreenCapturer`、`MonitorEnumerator`、`DesktopContext` |
| `Imaging/` | `WinRtImagePreprocessor`、`PngEncoder` 工具 |
| `Ocr/` | `WindowsOcrProvider` + Factory |
| `Security/` | `DpapiSecretStore` |
| `SystemIntegration/` | `AutoStartService`、`SingleInstanceGuard`（命名 Mutex）、`NamedPipeIpc`（服务端 / 客户端）。**目录不得命名为 `System`**，否则命名空间 `Iris.Platform.Windows.System` 会在项目内遮蔽 BCL 的 `System` |

### 5.5 Iris.App

| 目录 | 内容 |
|---|---|
| 根 | `App.xaml(.cs)`、`Program.cs`（自定义 `Main`：单实例检查 → 启动 WPF）、`app.manifest` |
| `Hosting/` | `AppHostBuilder`（组合根，调用各层 `AddIris*`）、`AppBootstrapper`（启动序列 §10.1）、`ActionDispatcher`、`GlobalExceptionHandler` |
| `Tray/` | `TrayIconController`（菜单：输入翻译 / 截图翻译 / 截图 OCR / 划词开关 / 悬浮球开关 / 历史 / 设置 / 退出） |
| `Services/` | Presentation 端口实现：`TranslateWindowPresenter`、`ScreenshotService`、`OcrResultPresenter`、`TrayNotificationService`、`SettingsWindowPresenter`、`HistoryWindowPresenter`、`FloatingBallController`、`SelectionIconController`、`AppLifetime`、`WindowPlacementHelper`、`ThemeService` |
| `Views/` | §14 所列窗口 |
| `ViewModels/` | 与 View 一一对应 |
| `Controls/` | `ProviderResultCard`、`LanguagePicker`、`HotkeyRecorderBox`、`SelectionAdorner` |
| `Resources/` | `Strings.resx`（zh-CN，默认）、`Strings.en-US.resx`、图标 |

---

## 6. 核心流程

### 6.1 统一分发

```
[热键 WM_HOTKEY] ─┐
[托盘菜单]       ─┤
[悬浮球单击/拖放] ─┤──► IActionDispatcher.DispatchAsync(action, ctx)
[划词图标 / 自动] ─┤        │ ① 防重入：同 Action 执行中则忽略；截图类(3/4/5)互斥
[命令行 / IPC]   ─┘        │ ② 切到 UI 线程，找到 IActionHandler
                           │ ③ try { await handler.ExecuteAsync } catch → 通知 + 日志
                           ▼
                     Workflows/*Handler (Core)
```

### 6.2 输入翻译（`InputTranslateHandler`）

1. 若翻译窗口 `IsVisible && IsActive` → `HideAsync()`（热键二次按下即关闭），结束。
2. 否则 `ShowAsync(new TranslateWindowRequest { Text = ctx.Payload, Origin = Input, FocusInput = true, AutoTranslate = ctx.Payload != null })`。
3. 用户在窗口中按 Enter → `TranslateWindowViewModel.TranslateCommand` → Orchestrator（§7）。

### 6.3 划词翻译（`SelectionTranslateHandler`）

1. `app = IDesktopContext.GetForegroundApp()`；若是 Iris 自身进程 → 按输入翻译处理。
2. `result = await ISelectionTextReader.ReadAsync(options from Settings.Selection)`。
3. `result == null` → `OpenInputWhenEmpty ? 打开空输入窗口 : Notify(Warning, "未检测到选中文本")`。
4. 否则 `ShowAsync(Text = result.Text, Origin = Selection, Anchor = ctx.Anchor ?? 光标, AutoTranslate = true)`。

**鼠标划词（可选）**：`SelectionIconController`（App）在 `MouseMode != Off` 时启动 `IMouseSelectionMonitor`。收到 `SelectionGestureDetected` 后：
- 若进程在 `ExcludedProcesses` 中，或前台是 Iris 自身 → 忽略；
- `ShowIcon`：在光标右下 (+12, +12) DIP 处显示 `SelectionIconWindow`（**不激活**），`IconAutoHideMs` 后或收到 `MouseDown`（点在图标外）时隐藏；点击图标 → `Dispatch(SelectionTranslate, Trigger=SelectionIcon, Anchor=图标位置)`；
- `AutoTranslate`：延迟 120 ms 后直接 Dispatch。

### 6.4 截图翻译（`ScreenshotTranslateHandler`）

```
shot = await IScreenshotService.CaptureRegionAsync(Translate)        // null = 用户取消 → return
await presenter.ShowAsync({ Text=null, Origin=Screenshot, Anchor=选区右下角, StatusMessage="正在识别文字…", AutoTranslate=false })
try   outcome = await IOcrService.RecognizeAsync(shot.Image)
catch IrisException e → await presenter.ShowAsync({ Error = e.ToErrorInfo() }); return
await presenter.ShowAsync({ Text=outcome.Text, Origin=Screenshot, AutoTranslate=true })
if outcome.UsedFallback → Notify(Info, "已使用备用 OCR", ...)（每次会话仅提示一次）
```

### 6.5 截图 OCR / 静默 OCR

- **截图 OCR**：取消则返回 → `IOcrResultPresenter.ShowAsync({ Image, IsLoading = true })` → 识别 → `ShowAsync({ Image, Text, ProviderName })`；`AutoCopyOnOcr` 时复制文本；失败时 `ShowAsync({ Image, Error })`。
- **静默 OCR**：识别 → `IClipboardService.SetTextAsync` → `SilentOcrShowNotification` 时通知“已复制 N 个字符”；失败 → 通知错误。

### 6.6 截图服务（`ScreenshotService`，App）

1. `HideIrisWindows` 时隐藏翻译窗与悬浮球，等待 1 帧（~16 ms），让 DWM 完成重绘。
2. `frame = await IScreenCapturer.CaptureVirtualScreenAsync()`（GDI BitBlt 整个虚拟桌面，物理像素）。
3. 为 `frame.Monitors` 中的每个显示器创建一个 `ScreenshotOverlayWindow`：无边框、置顶、`ShowInTaskbar=false`，窗口大小与位置 = 该显示器的物理 Bounds（按该显示器 DPI 换算为 DIP）；背景为 frame 中对应区域的 `BitmapSource`，上面叠加半透明遮罩（`MaskOpacity`）。
4. 用户在任一覆盖窗口内拖拽框选（选区不跨屏）：实时显示选区尺寸（物理像素）；`ShowMagnifier` 时在光标旁显示 8× 放大镜与像素坐标。
5. 确认：`ConfirmOnMouseUp` 为 true 时松开鼠标即确认，否则按 Enter / 双击确认。取消：Esc，或在无选区时右键（有选区时右键 = 清除选区）。
6. 选区 < 4×4 物理像素视为误点，忽略。
7. 返回 `ScreenshotResult(IScreenCapturer.Crop(frame, region), region)`，关闭全部覆盖窗口，恢复之前隐藏的窗口；`CopyImageToClipboard` 时同时复制图片。

### 6.7 悬浮球（`FloatingBallController` + `FloatingBallWindow`）

- 窗口：圆形、`Topmost`、`WS_EX_TOOLWINDOW`（不出现在 Alt+Tab 和任务栏）、`WS_EX_NOACTIVATE`（点击不抢焦点，保证随后划词读取的仍是用户原来的前台窗口）。
- 左键单击（按下到松开位移 < 4 px）→ `Dispatch(Settings.FloatingBall.ClickAction, Trigger=FloatingBall)`；拖动 → 移动窗口，松开时 `EdgeSnap` 吸附到最近的屏幕边缘，`AutoHideAtEdge` 时收进屏幕边缘只露出 1/3，鼠标悬停时滑出。
- 右键菜单：输入翻译 / 截图翻译 / 截图 OCR / 划词开关 / 设置 / 隐藏悬浮球。
- 拖入文本（`AcceptDrop`，DataFormats.UnicodeText）→ `Dispatch(TranslateText, Payload=text, Trigger=DragDrop)`。
- 位置持久化到 `Settings.FloatingBall.Position` / `MonitorDeviceName`（拖动结束后防抖 500 ms 写入）。

---

## 7. 翻译管道

### 7.1 编排（`TranslationOrchestrator`）

```
TranslateAsync(request, ct)
 ├─ text = ITextPreprocessor.Prepare(request.Text, request.Origin)
 │    空 → throw InputEmpty；超长 → throw InputTooLong
 ├─ langs = ITargetLanguageResolver.Resolve(text, request.SourceLanguage, request.TargetLanguage)
 ├─ providers = 选取（见 CONTRACTS 注释）；为空 → throw NotConfigured
 ├─ yield TranslationStarted(langs, descriptors)
 ├─ channel = Channel.CreateUnbounded<TranslationEvent>()
 ├─ 对每个 provider（SemaphoreSlim(MaxConcurrentProviders) 限流）启动任务：
 │    key = CacheKey(provider, prompt 指纹, langs, SHA256(text))
 │    命中缓存 → ProviderStarted + ProviderCompleted(FromCache)
 │    否则   → ProviderStarted → await foreach chunk: ProviderDelta → ProviderCompleted
 │    catch IrisException → ProviderFailed(e.ToErrorInfo())
 │    catch Exception    → ProviderFailed(Unknown) + LogError
 │    （OperationCanceledException 且 ct 已取消 → 不产出事件，直接结束）
 ├─ 所有任务结束后 channel.Writer.Complete()
 ├─ await foreach evt in channel.Reader.ReadAllAsync(ct) → yield evt
 ├─ 写缓存（未截断的成功结果）、写历史（条件见 CONTRACTS）
 └─ yield TranslationFinished(succeeded, failed)
```

要点：
- 用 `CancellationTokenSource.CreateLinkedTokenSource(ct)` 管理子任务；消费者提前停止枚举（`DisposeAsync`）时也要取消全部子任务。
- `Elapsed` 用 `Stopwatch` 从 ProviderStarted 开始计时。
- 历史写入失败不影响事件流（记 Warning 日志即可）。

### 7.2 ViewModel 消费规范（`TranslateWindowViewModel`）

- 每次翻译前取消上一次的 CTS（新请求立即取代旧请求）。
- `TranslationStarted` → 按 Providers 创建 `ProviderResultCardViewModel`（状态：等待中）。
- `ProviderDelta` → 追加到对应卡片的 StringBuilder；**以约 30 fps 节流刷新 UI**（`DispatcherTimer`，或累积 ≥ 33 ms 再赋值），避免高频 PropertyChanged。
- `ProviderCompleted` → 用 `Text` 覆盖内容（权威值），显示耗时；`Truncated` 时显示“结果可能不完整”；`AutoCopy == FirstResult` 时第一个完成的结果写入剪贴板。
- `ProviderFailed` → 卡片显示 `Error.Message` + 重试按钮；`NotConfigured` / `AuthenticationFailed` 附带“去设置”链接（`ISettingsWindowPresenter.ShowAsync("translation")`）。
- 单卡片重试：`TranslationRequest { ProviderIds = [id], BypassCache = true, RecordHistory = false }`。

### 7.3 语言

| Code | EnglishName（写入 Prompt） | NativeName |
|---|---|---|
| zh-Hans | Simplified Chinese | 简体中文 |
| zh-Hant | Traditional Chinese | 繁體中文 |
| en | English | English |
| ja | Japanese | 日本語 |
| ko | Korean | 한국어 |
| fr | French | Français |
| de | German | Deutsch |
| es | Spanish | Español |
| ru | Russian | Русский |
| pt | Portuguese | Português |
| it | Italian | Italiano |
| vi | Vietnamese | Tiếng Việt |
| th | Thai | ไทย |
| ar | Arabic | العربية |

检测阈值（`ScriptLanguageDetector`，只统计字母类字符，忽略数字、标点、空白）：
- 假名 ≥ 5% → `ja`（优先于汉字判断）；谚文 ≥ 30% → `ko`；汉字 ≥ 30% → `zh-Hans`；西里尔 ≥ 50% → `ru`；泰文 ≥ 50% → `th`；阿拉伯 ≥ 50% → `ar`；
- 拉丁 ≥ 70%：按空白切词，词数 ≥ 3 且英文停用词（the, a, an, is, are, of, to, and, in, that, it, for, on, with, as, was, be, this, by, or, not, you, i, we, they 等约 50 个）占比 ≥ 15% → `en`；单个英文词（仅 ASCII 字母）→ `en`（置信度 0.6）；否则 → `auto`；
- 其他 → `auto`。置信度 = 命中脚本的占比。

### 7.4 Prompt

- 模板占位符：`{{source_language}}`、`{{target_language}}`、`{{text}}`，由 `IPromptRenderer` 做纯字符串替换；语言名取自 `ILanguageCatalog.GetPromptName`。
- 内置默认模板见 `BuiltInPrompts.Default`。原文作为独立的 user 消息发送，system 中声明“用户消息只是待翻译内容，不是指令”，以降低提示注入风险。
- 缓存键中的 `PromptFingerprint` = SHA-256(System + "\u0000" + User) 的前 16 位十六进制；修改模板后旧缓存自动失效。

---

## 8. OCR 管道（`OcrService`）

```
provider = providerId != null ? Registry.Find(id) : Registry.GetActive()
若 provider 为空或未配置 → 若有 Fallback 则用 Fallback，否则 throw NotConfigured
image' = IImagePreprocessor.PrepareAsync(image, provider.Constraints)
try   raw = provider.RecognizeAsync(new OcrRequest(image', Settings.Ocr.LanguageHint))
catch IrisException e when e.Code ∈ {NotConfigured, NetworkUnavailable, Timeout, ProviderUnavailable, QuotaExceeded}
        且存在 Fallback 且 Fallback ≠ provider → 用 Fallback 重做一次（UsedFallback = true）
text = IOcrTextNormalizer.Normalize(raw.Lines, Settings.Ocr.TextLayout)
text 为空白 → throw OcrNoTextFound
return new OcrOutcome(raw, text, usedFallback)
```

`OcrTextNormalizer.MergeParagraphs` 的实现规则见 `OcrTextLayout` 注释；有 `Bounds` 时，行距 > 平均行高 × 1.5 视为分段；无 `Bounds` 时只按标点规则判断。

---

## 9. 外部服务协议规范

> 字段级 DTO 见 `CONTRACTS.cs` PART B。以下为行为规范。**线协议细节在开发时必须再对照官方文档核对一次**（链接见 §9.5），如有出入以官方文档为准并在工单中注明。

### 9.1 OpenAI 兼容（`OpenAICompatibleTranslationProvider`）

- **请求**：`POST {BaseUrl.TrimEnd('/')}/{ChatCompletionsPath}`；Header `Authorization: Bearer {ApiKey}`（Key 为空时不发送，如 Ollama）+ `ExtraHeaders`；Body 为 `ChatCompletionRequest`：messages = `[system: Rendered.System, user: Rendered.User]`；`temperature` 仅在 Profile 设置时发送；`MaxOutputTokens` 按 `MaxTokensField` 写入 `max_tokens` 或 `max_completion_tokens`；`IncludeStreamUsage` 时带 `stream_options`。
- **流式**：`HttpCompletionOption.ResponseHeadersRead`；非 2xx → 读出 body → `HttpErrorMapper`。2xx → `SseParser` 逐条解析：`data == "[DONE]"` 结束；每条反序列化为 `ChatCompletionChunk`，取 `choices[0].delta.content` 作为 Delta（**忽略 `reasoning_content`**）；记录 `finish_reason`；`usage` 出现时记下。
- **finish_reason**：`stop` → Completed；`length` → 最后一个 Chunk 标记 `Truncated`；`content_filter` → 抛 `ContentRefused`。
- **非流式**（`Stream=false`）：解析 `ChatCompletionResponse`，一次性产出一个 Chunk。
- **`<think>` 剥离**（`StripThinkTags`）：流式状态机——文本开头出现 `<think>` 时进入丢弃状态，直到 `</think>` 为止，并去掉其后的前导空白；标签可能被拆在两个 Delta 之间，需缓冲最多 8 个字符再判断。
- **200 响应中的错误**：部分兼容服务出错时仍返回 200 且 body 为 `{"error":{...}}`，或 SSE 中出现 `data: {"error":...}` → 按 `ErrorEnvelope` 解析并抛出。
- **模型列表**：`GET {BaseUrl}/models` → `ModelListResponse.data[].id` 排序后返回。
- **OpenAI 特殊情况**：`error.code == "insufficient_quota"`（HTTP 429）→ `QuotaExceeded`，而不是 `RateLimited`。

流式响应示例（测试夹具 `tests/Iris.Providers.Tests/Fixtures/openai_stream_ok.txt`）：

```
data: {"id":"c1","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}

data: {"id":"c1","choices":[{"index":0,"delta":{"content":"你好"},"finish_reason":null}]}

data: {"id":"c1","choices":[{"index":0,"delta":{"content":"，世界"},"finish_reason":null}]}

data: {"id":"c1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

data: [DONE]
```

### 9.2 Anthropic（`AnthropicTranslationProvider`，官方 SDK）

- **包**：NuGet `Anthropic`（官方 C# SDK）。非 beta 类型在 `Anthropic.Models.Messages`，beta 类型在 `Anthropic.Models.Beta.Messages`，异常在 `Anthropic.Exceptions`。**不要手写 Messages API 的 HTTP 请求。**
- **客户端**：每个 Profile 一个 `AnthropicClient`，传入 `ApiKey = credentials.ApiKey`；`BaseUrl` 非空时覆盖服务地址；尽量注入由 `IHttpClientFactory` 创建的 `HttpClient`（命名客户端 `iris.llm`），使代理设置生效——SDK 的具体属性名以编译结果为准（参考 SDK README / examples）。
- **请求参数**：`Model = profile.Model`（默认预设 `claude-opus-5`）；`MaxTokens = profile.MaxOutputTokens ?? 16000`；`System = Rendered.System`；`Messages = [User: Rendered.User]`；`AnthropicOptions.Effort` 非空时设置 `OutputConfig = new OutputConfig { Effort = ... }`（翻译场景默认 `low`，以降低延迟）。
  - **不发送 `Temperature`**（新模型会拒绝非默认采样参数并返回 400），忽略 `profile.Temperature`。
  - **不设置 `Thinking`**：Claude Opus 5 默认启用自适应思考，用 effort 控制深度即可。Haiku 4.5 等不支持 effort 的模型，用户需把 `Effort` 置为 null（设置页在模型名包含 `haiku` 时自动置空并给出提示）。
  - 不要构造以 assistant 消息结尾的“预填充”（新模型会返回 400）。
- **流式**：`client.Messages.CreateStreaming(params)`，`await foreach` 遍历 `RawMessageStreamEvent`：
  - `TryPickContentBlockDelta` 且 `delta.Delta.TryPickText(out var t)` → 产出 `t.Text`。**只取 text delta**，thinking / signature delta 一律忽略（启用思考时流中会出现 thinking 内容块）。
  - `TryPickDelta`（message_delta）→ 读 `StopReason` 与 usage。
  - `stop_reason`：`end_turn` / `stop_sequence` → Completed；`max_tokens` → Truncated；`refusal` → 抛 `ContentRefused`（已输出的部分内容作废）。
- **拒答回退**（`RefusalFallback = true`，仅官方地址生效）：走 beta 路径 `client.Beta.Messages`，按 SDK README 设置服务端 fallback。首选 `fallbacks: "default"` + beta `server-side-fallback-2026-07-01`；如果当前 SDK 版本不支持字符串形式，改用数组形式 `Fallbacks = [new(Model.ClaudeOpus4_8)]` + `AnthropicBeta.ServerSideFallback2026_06_01`。使用自定义 `BaseUrl`（反代）时禁用回退。T-010 以编译通过为准确定最终写法，并在工单报告中记录。
- **异常映射**：`AnthropicRateLimitException` → RateLimited；`Anthropic5xxException` → ProviderUnavailable；其余 `AnthropicApiException` 按 HTTP 状态码走 §11.2；网络异常 → NetworkUnavailable。按“最具体优先”的顺序逐个 catch。
- **模型列表**：`await client.Models.List()` → `page.Items[].ID`。
- **可测试性**：若 SDK 支持注入 `HttpClient`，测试用 MockHttp 返回下方的 SSE 夹具；若不支持，在 Provider 内部抽象出 `IAnthropicStreamSource`（internal 接口）作为测试缝。

SSE 线协议参考（夹具 `anthropic_stream_ok.txt`）：

```
event: message_start
data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5","content":[],"stop_reason":null,"usage":{"input_tokens":25,"output_tokens":1}}}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"你好"}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":5}}

event: message_stop
data: {"type":"message_stop"}
```

错误体：`{"type":"error","error":{"type":"rate_limit_error","message":"..."}}`；流中也可能出现 `event: error`（如 `overloaded_error`）。

### 9.3 百度智能云 OCR（`BaiduOcrProvider`）

- **凭据**：`ApiKey` + `SecretKey`（均从 `ISecretStore` 读取，`SecretKeys.ApiKey/SecretKey(profileId)`）。
- **Token**：`POST {BaseUrl}/oauth/2.0/token?grant_type=client_credentials&client_id=…&client_secret=…` → `BaiduTokenResponse`。`BaiduTokenCache` 在内存中缓存 token，过期时间 = 获取时间 + `expires_in` − 1 天；并发请求共享同一个获取任务（`SemaphoreSlim` 或 `Lazy<Task>`）；有 `error` 字段 → `AuthenticationFailed`。
- **识别**：`POST {BaseUrl}{ModePath}?access_token={token}`，`FormUrlEncodedContent`：`image`（Base64）、`detect_direction`（"true"/"false"）、`paragraph`（"true"/"false"）、`language_type`。
- **language_type 映射**（`BaiduLanguageMap`）：auto → `CHN_ENG`（`*_basic` 标准版）/ `auto_detect`（高精度版）；zh-Hans/zh-Hant → `CHN_ENG`；en → `ENG`；ja → `JAP`；ko → `KOR`；fr → `FRE`；de → `GER`；es → `SPA`；pt → `POR`；it → `ITA`；ru → `RUS`；其他 → 同 auto。`BaiduOptions.LanguageType` 非空时直接使用该值。**开发时请对照官方文档确认各接口支持的取值。**
- **图像约束**（保守值，核对官方文档后可以放宽）：`MaxEncodedBytes = 4 MB`（Base64 计）、`MaxLongSide = 4096`、`MinShortSide = 15`、格式 `[Jpeg, Png, Bmp]`。
- **结果**：`words_result[].words` → `OcrLine`（含位置版本带 `location` → `Bounds`；有 `probability.average` → `Confidence`）。`paragraph=true` 且有 `paragraphs_result` 时，按段落把同段的行合并为一行（行间按 §8 的规则连接）后再交给 Normalizer。
- **错误**（业务错误以 HTTP 200 返回，必须先检查 `error_code`）：

| error_code | 处理 |
|---|---|
| 110 / 111 | 清除 token 缓存 → 重新获取 → **只重试一次**；仍失败 → AuthenticationFailed |
| 14 | AuthenticationFailed |
| 6 | PermissionDenied（未开通该接口） |
| 17 / 19 | QuotaExceeded |
| 18 | RateLimited（Retryable = true，退避 500 ms 后重试一次） |
| 216200 / 216201 | ImageInvalid |
| 216202 | ImageTooLarge |
| 216100 / 216101 | BadRequest |
| 216630 / 282000 | ProviderUnavailable |
| 其他 | Unknown（Detail 带上 error_code 与 error_msg） |

- **TestAsync**：只获取 token（不消耗识别额度）。

### 9.4 Windows 本地 OCR（`WindowsOcrProvider`）

- `OcrEngine.TryCreateFromLanguage(new Language(tag))`；tag 优先级：`WindowsOptions.LanguageTag` → LanguageHint 映射（zh-Hans → "zh-Hans-CN" …）→ `TryCreateFromUserProfileLanguages()`；都失败 → `OcrLanguageUnsupported`（提示用户在“设置 → 时间和语言”中安装对应语言的 OCR 功能）。
- 图像约束：`MaxLongSide = OcrEngine.MaxImageDimension`，`MinShortSide = 40`（小图放大后识别率更好），格式 `[Png, Bmp]`；`ImageData` → `SoftwareBitmap`（BitmapDecoder，格式 Bgra8）。
- **后处理**：Windows OCR 会在 CJK 字符之间插入空格——对每行执行“两个 CJK 字符之间的单个空格删除”。
- 并发：`OcrEngine` 非线程安全，Provider 内用 `SemaphoreSlim(1)` 串行化。

### 9.5 需要核对的官方文档

- 百度 OCR：通用文字识别（标准版 / 高精度版 / 含位置版）与 Access Token 鉴权文档，入口 `https://ai.baidu.com/ai-doc/OCR/`。
- OpenAI Chat Completions 流式：`https://platform.openai.com/docs/api-reference/chat`。
- Anthropic C# SDK：`https://github.com/anthropics/anthropic-sdk-csharp`（README 与 examples）。
- Windows OCR：`Windows.Media.Ocr` 命名空间文档。

---

## 10. 平台层要点与已知陷阱

### 10.1 启动序列（`AppBootstrapper`）

1. `Program.Main`：`SingleInstanceGuard.TryAcquire()`（Mutex `IpcConstants.MutexNamePrefix + SID`）。失败 → 把命令行解析成 `IpcCommand` 发给主实例（没有参数时发送 `InputTranslate`）→ 退出码 0。
2. 构建 Host：Serilog → `IAppPaths` → `ISettingsService.LoadAsync`（处理 SettingsCorrupted）→ `IDatabaseInitializer.InitializeAsync` → 注册其余服务。
3. 应用主题 / UI 语言 → 创建托盘图标。
4. 按 `Settings.Hotkeys` 注册热键；冲突项汇总成一条通知：“以下快捷键被占用：…”。
5. `MouseMode != Off` → 启动鼠标监视；`FloatingBall.Enabled` → 显示悬浮球。
6. 订阅 `ISettingsService.Changed`：热键差异重注册、鼠标监视启停、悬浮球显隐、主题切换（Provider 注册表自行订阅）。
7. 启动命名管道服务器，接收 `IpcCommand` → Dispatch（Trigger = CommandLine）。
8. `ShowStartupNotification` → 托盘气泡“Iris 已在后台运行”。
9. 处理本次启动的命令行参数。

**命令行**：`Iris.exe --action <IrisAction>`，`Iris.exe --translate "<text>"`（= TranslateText）。

**退出**：`IAppLifetime.RequestShutdown()` → 停止管道服务 → 停止鼠标钩子 → `UnregisterAll` 热键 → 释放托盘图标 → `Log.CloseAndFlush()` → `Application.Shutdown()`。

### 10.2 线程模型

| 线程 | 负责 |
|---|---|
| WPF UI 线程 | 所有窗口；`HotkeyService`（热键绑定在注册线程的消息窗口上）；`ActionDispatcher` 在此启动 Handler |
| 鼠标钩子线程（专用，后台，自带 `GetMessage` 消息循环） | 只运行 `LowLevelMouseProc`：把事件写入 `Channel`，立即 `CallNextHookEx` 返回 |
| 线程池 | 手势识别消费者、网络、OCR、磁盘、UIAutomation 调用 |

- **低级钩子必须在几毫秒内返回**：超过系统的 `LowLevelHooksTimeout` 后，Windows 会**静默卸载钩子**。回调中禁止日志 I/O、禁止跨线程同步调用。
- 在 Core / Providers / Infrastructure / Platform 中 `await` 一律加 `ConfigureAwait(false)`；App 层（ViewModel）不加。
- 禁止在 UI 线程调用 UIAutomation：当目标元素属于本进程时会死锁。

### 10.3 全局热键

- Platform 用 CsWin32 自行创建消息窗口（`CreateWindowEx(HWND_MESSAGE)` + 自定义 WndProc 处理 `WM_HOTKEY`），不依赖 WPF 的 `HwndSource`。必须在 UI 线程构造，因为 UI 线程有消息循环。
- `RegisterHotKey` 加上 `MOD_NOREPEAT`，避免按住不放时重复触发。
- `GetLastError() == ERROR_HOTKEY_ALREADY_REGISTERED (1409)` → `HotkeyConflict`。
- Key 名映射表只覆盖：A–Z、D0–D9、F1–F24、NumPad0–9、Space、Enter、Tab、Escape、Insert、Delete、Home、End、PageUp、PageDown、Left/Up/Right/Down、Snapshot（PrintScreen）、Oem 系列（OemTilde、OemMinus、OemPlus、OemOpenBrackets、OemCloseBrackets、OemPipe、OemSemicolon、OemQuotes、OemComma、OemPeriod、OemQuestion）。
- 至少需要一个修饰键，单独的 Snapshot / F13–F24 除外；不允许仅 Shift + 字母。违反 → `HotkeyInvalid`。

### 10.4 选区读取

**UIAutomation**：`IUIAutomation.GetFocusedElement()` → `TextPattern` / `TextPattern2` → `GetSelection()` → 拼接各 range 的 `GetText(-1)`。整体超时 300 ms（UIA 调用放在 `Task.Run` 中执行，用 `WaitAsync` 限时）。很多应用（Chromium 系需要开启无障碍，部分 Electron / Qt 应用）不支持，返回空即可，交给下一个方法。

**剪贴板兜底**（按顺序，全部在后台线程执行）：
1. 前台进程在 `ClipboardUnsafeProcesses` 中 → 跳过（**在终端里 Ctrl+C 是中断信号，会杀掉用户正在运行的程序**）。
2. 等待用户松开热键修饰键：轮询 `GetAsyncKeyState(VK_CONTROL/VK_MENU/VK_SHIFT/VK_LWIN/VK_RWIN)`，间隔 10 ms，最多 500 ms。**否则模拟的 Ctrl+C 会叠加用户仍按着的 Alt，变成 Ctrl+Alt+C。** 超时仍未松开 → 用 `SendInput` 补发这些键的 KeyUp。
3. 快照剪贴板：枚举格式（`EnumClipboardFormats`），保存 `CF_UNICODETEXT`、`CF_HDROP`、`CF_DIB`、`HTML Format`、`Rich Text Format` 的数据；其余格式不保证恢复（这是已知限制）。
4. 记录 `seq0 = GetClipboardSequenceNumber()` → `SendInput`（Ctrl 按下、C 按下、C 抬起、Ctrl 抬起）。
5. 每 15 ms 轮询一次，直到序列号 ≠ seq0 或超过 `ClipboardTimeoutMs`；序列号变化后再等 20 ms（部分应用分多次写入剪贴板）。
6. 读取 `CF_UNICODETEXT`；`RestoreClipboard` 时写回快照。
7. 序列号没有变化 → 视为“没有选中文本”，返回 null（不要读取旧的剪贴板内容）。

### 10.5 DPI 与坐标

- `app.manifest` 声明 `<dpiAwareness>PerMonitorV2</dpiAwareness>`。
- Platform 与 Core 只使用物理像素。View 层换算：`dip = px / monitor.DpiScale`。窗口定位的正确做法：在 `SourceInitialized` 之后调用 `SetWindowPos`（物理像素），不要用 WPF 的 `Left`/`Top` 跨屏定位，否则在混合 DPI 下会出错。
- **跟随光标定位**（`WindowPlacementHelper`）：窗口出现在锚点右下方 (+16, +16) 物理像素处；超出锚点所在显示器的 WorkArea 时，翻到锚点左侧 / 上方；最后夹紧到 WorkArea 内。

### 10.6 不抢焦点的窗口

`SelectionIconWindow`、`FloatingBallWindow`：在 `SourceInitialized` 中给扩展样式加上 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`，`ShowActivated = false`，并处理 `WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`。翻译窗口：划词、截图触发时 `ShowActivated = true`（允许用户直接复制结果），失焦隐藏遵循 `HideOnDeactivate` 和钉住状态。

### 10.7 屏幕抓取

- `GetDC(NULL)` + `BitBlt(SRCCOPY | CAPTUREBLT)` 抓取虚拟桌面（`SM_XVIRTUALSCREEN` 等），通过 `GetDIBits` 取出 32 位 BGRA 数据。
- 显示器信息：`EnumDisplayMonitors` + `GetMonitorInfo` + `GetDpiForMonitor(MDT_EFFECTIVE_DPI)`。
- 已知限制：受 DRM 保护的视频区域会是黑色；HDR 显示器颜色可能偏差（首期接受）。

---

## 11. 错误处理

### 11.1 原则

- 边界（Orchestrator 的每个 Provider 任务、ActionDispatcher、ViewModel 命令、全局异常处理器）负责把异常转换为 `ErrorInfo` 并展示；中间层只抛出不展示。
- `ErrorMessages.GetDefault(ErrorCode)` 提供默认中文文案；Provider 可以附加更具体的 Detail。
- 全局：`AppDomain.UnhandledException`、`Dispatcher.UnhandledException`（设 `Handled = true`，并通知“发生未知错误，详情见日志”）、`TaskScheduler.UnobservedTaskException` 都记为 Fatal/Error 日志。

### 11.2 HTTP → ErrorCode 映射（`HttpErrorMapper`）

| 条件 | ErrorCode | Retryable |
|---|---|---|
| `HttpRequestException`（DNS / 连接失败）/ `SocketException` | NetworkUnavailable | ✓ |
| Provider 的超时 CTS 触发（用户 ct 未取消） | Timeout | ✓ |
| 400 / 422 | BadRequest | |
| 401 | AuthenticationFailed | |
| 402 | QuotaExceeded | |
| 403 | PermissionDenied | |
| 404 | EndpointNotFound | |
| 408 / 504 | Timeout | ✓ |
| 413 | InputTooLong | |
| 429（OpenAI `insufficient_quota` 除外） | RateLimited | ✓ |
| 500 / 502 / 503 / 529 | ProviderUnavailable | ✓ |
| 响应无法解析 | InvalidResponse | |

Message 使用默认文案；Detail = `"HTTP {status}: {error.message 截断到 300 字符}"`。

### 11.3 重试策略（`Microsoft.Extensions.Http.Resilience`）

- `iris.llm`：只重试 1 次（总共 2 次尝试），条件为连接失败 / 408 / 429 / 5xx，遵守 `Retry-After`（上限 5 s）。**不设 TotalRequestTimeout / AttemptTimeout**（流式响应可能持续较长时间，超时由 Provider 的 CTS 控制）。重试只发生在响应头返回之前，因此不会造成重复输出。
- `iris.ocr`：标准弹性管道，重试 2 次，单次尝试超时 10 s，总超时 = Profile.TimeoutSeconds。

---

## 12. 配置、密钥、存储、网络

### 12.1 数据目录

```
%APPDATA%\Iris\                 （便携模式：{exe目录}\data\）
├─ settings.json               设置（IrisJson 格式，示例见 specs/settings.example.json）
├─ settings.json.bak           上一次成功写入前的备份
├─ secrets.dat                 DPAPI 加密的 {key: base64(cipher)} JSON
├─ history.db                  SQLite（DDL 见 specs/DB_SCHEMA.sql）
└─ logs\iris-YYYYMMDD.log      Serilog，保留 7 天，单文件 ≤ 10 MB
```

### 12.2 设置

- 不可变 record + `with`：`await settings.UpdateAsync(s => s with { General = s.General with { Theme = AppTheme.Dark } })`。
- 设置页采用“草稿 → 保存”模式：打开时复制 Current 作为草稿，点击“保存”时一次性 UpdateAsync；热键录入、Provider 测试都针对草稿进行。
- 迁移：`SchemaVersion < CurrentSchemaVersion` 时按 FromVersion 依次执行 `ISettingsMigration`（操作 `JsonObject`），迁移后先备份原文件再写入。
- 新增字段只要有默认值就不需要迁移（缺失字段取默认值）；重命名、删除、改变字段含义时才需要迁移。

### 12.3 密钥

- `DpapiSecretStore`：`ProtectedData.Protect(bytes, entropy: "Iris.Secrets.v1"u8, DataProtectionScope.CurrentUser)`（NuGet `System.Security.Cryptography.ProtectedData`）。
- 文件写入同样采用原子方式；读取失败（换了机器或用户）→ 该 key 视为不存在并记 Warning。
- 删除 Provider Profile 时，同时删除它的所有 SecretKeys。

### 12.4 代理（`DynamicWebProxy : IWebProxy`）

- 两个命名 HttpClient 的主处理器均为 `SocketsHttpHandler { Proxy = dynamicProxy, UseProxy = true, PooledConnectionLifetime = 2 min }`。
- `GetProxy(uri)` 每次读取 `ISettingsService.Current.Network`：System → `HttpClient.DefaultProxy.GetProxy(uri)`；None → 返回 null（直连）；Custom → `new Uri(CustomProxyUrl)`（支持 http / https / socks5）。`IsBypassed(uri)`：`BypassProxyOnLocal` 且目标是 loopback 或局域网地址时返回 true。
- 代理设置修改后，新建的连接立即生效；已存在的连接最多 2 分钟后轮换。

### 12.5 日志与隐私

- 默认级别 Information。日志中**绝不**记录 API Key、Authorization 头、完整原文或译文。Debug 级别下原文只记录前 32 个字符 + 长度 + SHA-256 前 8 位。
- 每个翻译会话以 `RequestId` 作为日志作用域（`BeginScope`），便于排查问题。

---

## 13. 依赖注入约定

- 每层暴露一个扩展方法：`AddIrisCore()`、`AddIrisProviders()`、`AddIrisInfrastructure()`、`AddIrisPlatformWindows()`、`AddIrisApp()`，由 `AppHostBuilder` 按此顺序调用。
- 生命周期：服务、Registry、Presenter、Controller 均为 **Singleton**；`IActionHandler` 为 Singleton，通过 `IEnumerable<IActionHandler>` 注入 Dispatcher；Provider 实例由 Registry 管理（不注册到容器）；`TranslateWindow` 为 Singleton（关闭 = 隐藏）；`SettingsWindow`、`HistoryWindow`、`OcrResultWindow` 同一时间只有一个实例（由 Presenter 管理，关闭即释放）。
- ViewModel 通过构造函数注入，View 在构造函数中接收 ViewModel 并设为 `DataContext`。

---

## 14. UI 窗口清单

| 窗口 | 要点 |
|---|---|
| `TranslateWindow` | 无边框 Fluent 窗口，可拖动。顶部栏：钉住 / 设置 / 关闭。原文输入框（多行，最大高度 200 DIP 后出现滚动条；Enter 翻译，Shift+Enter 换行，或按 `EnterToTranslate` 设置反过来）。语言栏：源语言（含“自动检测”，检测结果以灰字显示）⇄ 目标语言（含“自动”）。结果区：`ProviderResultCard` 列表（服务名 · 模型 · 耗时；可选中的只读文本；复制 / 重试；错误状态）。Esc 隐藏。 |
| `ScreenshotOverlayWindow` | 每个显示器一个；冻结背景 + 遮罩 + 选区；尺寸标签；放大镜；Esc / 右键取消。 |
| `OcrResultWindow` | 左侧图片预览（可缩放），右侧可编辑文本；按钮：复制、翻译（→ TranslateText）、换一个服务重新识别（下拉选择 OCR Provider）。 |
| `FloatingBallWindow` | 见 §6.7。 |
| `SelectionIconWindow` | 24×24 DIP 图标，不激活，自动隐藏。 |
| `SettingsWindow` | WPF-UI `NavigationView`，页面：常规、翻译服务、OCR、快捷键、划词、截图、悬浮球、网络、历史、关于。 |
| `HistoryWindow` | 搜索框（防抖 300 ms）、仅看收藏、分页列表；每条可展开查看各服务结果；收藏 / 删除 / 重新翻译 / 复制。 |
| 托盘 | 左键单击 = 输入翻译；右键菜单见 §5.5。 |

**翻译服务设置页**：列表（启用开关、拖拽排序、编辑、删除）；“添加”按钮 → 选择预设（`TranslationProviderPresets`）→ 编辑表单：显示名、BaseUrl、API Key（PasswordBox，保存时写入 SecretStore）、模型（可编辑下拉框 + “获取模型列表”按钮）、超时、最大输出 tokens、温度（Anthropic 隐藏此项）、Prompt 模板、高级选项（OpenAI / Anthropic 各自的 Options）；“测试连接”按钮显示延迟和测试译文，或错误信息。

---

## 15. 目录树规范

```
F:\Iris
├─ global.json                      锁定 .NET SDK 10.0.1xx
├─ Iris.slnx                        解决方案（XML 格式）
├─ Directory.Build.props            公共编译属性（见 T-001）
├─ Directory.Packages.props         集中包版本管理
├─ .editorconfig  .gitignore  README.md
├─ specs/                           ← 架构规范（开发只读）
│  ├─ ARCHITECTURE.md
│  ├─ CONTRACTS.cs
│  ├─ DB_SCHEMA.sql
│  └─ settings.example.json
├─ tasks/
│  ├─ BACKLOG.md                    全量任务清单
│  ├─ current_task.md               当前工单（同一时间只有一张）
│  ├─ CONTRACT_CHANGE_REQUESTS.md   契约变更申请（按需创建）
│  └─ done/                         已完成工单归档：T-001.md …（附完成报告）
├─ src/
│  ├─ Iris.Core/
│  │  ├─ Actions/ Common/ Errors/ Imaging/ Languages/ Ocr/ Platform/
│  │  ├─ Presentation/ Security/ Serialization/ Settings/ Storage/ Translation/
│  │  ├─ Workflows/                 *Handler.cs
│  │  └─ DependencyInjection/
│  ├─ Iris.Providers/
│  │  ├─ Http/
│  │  ├─ Translation/OpenAICompatible/{,Wire/}
│  │  ├─ Translation/Anthropic/
│  │  ├─ Ocr/Baidu/{,Wire/}
│  │  └─ DependencyInjection/
│  ├─ Iris.Infrastructure/
│  │  ├─ Settings/{,Migrations/}
│  │  ├─ Storage/
│  │  └─ DependencyInjection/
│  ├─ Iris.Platform.Windows/
│  │  ├─ NativeMethods.txt
│  │  ├─ Interop/ Hotkeys/ Hooks/ Selection/ Clipboard/ Capture/ Imaging/ Ocr/ Security/ SystemIntegration/
│  │  └─ DependencyInjection/
│  └─ Iris.App/
│     ├─ App.xaml(.cs)  Program.cs  app.manifest
│     ├─ Hosting/ Tray/ Services/ Views/ ViewModels/ Controls/ Converters/ Resources/ Themes/
│     └─ DependencyInjection/
├─ tests/
│  ├─ Iris.Core.Tests/              镜像 src 目录结构
│  ├─ Iris.Providers.Tests/{,Fixtures/}
│  ├─ Iris.Infrastructure.Tests/
│  ├─ Iris.Platform.Windows.Tests/  只测纯逻辑（按键映射、手势识别、DPI 计算）
│  └─ Iris.ArchitectureTests/
└─ build/
   └─ pack.ps1                      Velopack 打包脚本
```

---

## 16. 编码规范

- **命名**：接口 `I` 前缀；异步方法 `Async` 后缀；私有字段 `_camelCase`；常量 PascalCase；一个文件一个公开类型，文件名 = 类型名（契约中几个很小的相关 record 允许合并在同一文件，按 CONTRACTS 的 FILE 标记执行）。
- **语言特性**：文件级 namespace；`Nullable=enable`；record 用于数据，class 用于服务；可以使用主构造函数做依赖注入，但字段名要清晰。
- **实现类可见性**：服务实现类默认 `internal sealed`，通过 `DependencyInjection` 扩展方法注册；测试项目通过 `InternalsVisibleTo` 访问。
- **日志**：注入 `ILogger<T>`，使用结构化消息模板（`"Provider {ProviderId} failed with {ErrorCode}"`），不拼接字符串；高频路径使用 `LoggerMessage` 源生成器。
- **时间**：注入 `TimeProvider`（BCL 自带），不直接使用 `DateTime.Now`，便于测试 token 过期、缓存等逻辑。
- **XAML**：不在 code-behind 中写业务逻辑（只允许 Win32 样式、DPI、拖拽等 View 专属代码）；字符串走资源文件；颜色、间距走主题资源。
- **注释**：公开 API 写 XML 注释（中文）；实现代码只在“为什么这样做”不明显时写注释。
- **命名空间遮蔽**：在 `Iris.Platform.Windows.*` 命名空间内，完全限定的 WinRT 名称（如 `Windows.Media.Ocr.OcrEngine`）会被解析为 `Iris.Platform.Windows.Media…` 而编译失败。WinRT 类型一律通过文件顶部（namespace 声明之前）的 `using Windows.Media.Ocr;` 引入，或写成 `global::Windows.…`。新建目录时不要使用 `System`、`Windows`、`Microsoft` 等根命名空间名称。
- **测试命名**：`方法名_场景_期望结果`，例如 `Resolve_SourceIsPrimaryTarget_SwapsToSecondary`。

### 16.1 测试策略

| 层 | 类型 | 要求 |
|---|---|---|
| Core | 单元测试 | Orchestrator（并发、缓存、失败隔离、取消、历史写入条件）、Resolver、Detector、Normalizer、Renderer、全部 Workflows（NSubstitute 替身） |
| Providers | 单元测试 | MockHttp + `Fixtures/*.txt` SSE 夹具：正常、分片 `<think>`、finish_reason=length、200 中带错误、各 HTTP 状态码映射、百度 token 刷新重试 |
| Infrastructure | 集成测试 | 在临时目录读写真实文件和 SQLite 数据库（测试结束时删除） |
| Platform.Windows | 单元测试 + 手动 | 纯逻辑自动测试；钩子、剪贴板、截图按工单中的手动验收清单测试 |
| Architecture | 自动 | 依赖规则（§4.2） |
| 端到端 | 手动 | T-040 手动验收清单 |

---

## 17. 版本与演进

- 本文档与 CONTRACTS 的修改须由架构师进行，并在文件头更新版本号。
- 已预留的扩展点：新增翻译协议 = 新增 `TranslationProviderKind` + Factory；新增 OCR = 新增 `OcrProviderKind` + Factory；新增动作 = 新增 `IrisAction` + Handler。UI 与编排层都不需要修改。
