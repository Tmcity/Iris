# Iris 架构设计说明书

> 版本：v1.25 · 2026-10-02 · 状态：**已冻结（首期）**
> 变更记录：v1.25（T-019B-R6 通过，签发 T-015）— TRANSLATION_WINDOW.md 定稿 A07 的操作租约、迟到结果拒绝、有界主/重试/刷新、复制与完整退出；CCR-013 同步展示端口；§14 输入键行为以契约为准。
> 变更记录：v1.1（T-001 评审）— §3 测试运行器改为 Microsoft.Testing.Platform；§4.2 明确 `Microsoft.Extensions.*.Abstractions` 各层均可引用，并由架构测试维护包白名单；新增 `Iris.Core.Diagnostics.LogText`（§5.1、§12.5）。
> 变更记录：v1.2（T-002 评审）— §12.2 新增设置加载管道：挽救规则、规范化不变量 N1-N11、写入细节；同步更新 ISettingsService 契约注释。
> 变更记录：v1.3（T-003 评审）— §12.2 解析后完整展开以捕获重复属性名；迁移链不完整时跳过迁移而非抛出；LoadAsync 异常范围；N2 明确 null 条目处理；新增 N12 枚举、N13 空值、N14 引用规范化；加载阶段文件操作统一重试。
> 变更记录：v1.4（T-003-R1 复审）— §12.3 密钥存储完整规范（文件格式、加载与降级、读写语义、线程与事件）；N13 补充字典 null 值规则。
> 变更记录：v1.5（T-004 评审）— 新增 §10.1.1 启动与退出的实现细节（单实例锁、致命错误对话框、Generic Host 配置、启动与退出序列、全局异常、通知队列）。
> 变更记录：v1.6（T-005 评审）— §1.2 空闲内存增加 Private Bytes 指标并规定测量方法（Working Set 上限不变）；§10.1.1 新增主机生命周期、退出日志、托盘创建失败重试。
> 变更记录：v1.7（T-005-R1 复审）— §1.2 依据实测修订内存指标（Working Set 上限 160 MB，说明 GC 前后两种读数）与启动时间的测量条件；§7.3 细化语言目录、检测（Unicode 范围、按码点遍历、短语规则、停用词表）与目标语言解析规则。
> 变更记录：v1.8（T-006 评审）— §7.3.2 短语规则允许弯撇号；§7.4 单遍替换（防模板注入）、模板查找与有效性；新增 §7.5 文本预处理与软换行合并（保留 ZWJ / ZWNJ，空行压缩规则澄清）。
> 变更记录：v1.9（T-007 评审）— §5.2 SSE 解析改为包装 .NET 自带解析器；§7.5 修正编号列表正则（避免把小数当作列表编号）；§11.2 补全状态码、异常映射与错误信息提取；§11.3 命名 HttpClient 的完整配置（移除会记录完整 URL 的默认日志）；§12.4 代理细则；§12.5 URL 与服务端错误信息的脱敏。
> 变更记录：v1.10（T-008 评审）— §9.1 OpenAI 兼容 Provider 完整行为规范（配置判定、请求构造、无进展超时、异常归类、响应分流、流结束判定、`<think>` 剥离状态机、TestAsync、模型列表）；§11.2 异常 Detail 必须脱敏；§12.4 本地地址判断处理 IPv4 映射地址与末尾的点；§12.5 新增 URL 账号密码脱敏规则。
> 变更记录：v1.11（T-009 评审）— §9.2 Anthropic Provider 依据 SDK 12.50.0 实测重写（显式隔离 SDK 读取的环境变量凭据与地址、关闭 SDK 自带重试、回退启用条件、流截断检测、异常映射与隐私要求）；§9.1 非事件流响应的错误提示、Usage 允许部分缺失、TestAsync 的 Unknown 结果带 ProviderId 且日志不传入异常对象。
> 变更记录：v1.12（T-010 评审，返工 R1）— §9.1 重申公共骨架重构不得改变流式调用的延迟执行语义；§9.2 补充 SDK 心跳适配层的超时、事件边界、并发与资源所有权要求，纠正 request_too_large 的判定方式，明确模型 ID 去重规则。评审证据见 tasks/done/T-010.md；本次不变更契约签名。
> 变更记录：v1.13（T-010-R2 通过后，签发 T-010A）— §9.2/§11.2 统一远程异常的安全诊断与内部异常链约束；§11.3 禁止自动重定向；§12.5 完整移除 URL 中的 userinfo/query/fragment，并取消已知凭据脱敏的长度门槛。ToErrorInfo 行为变更见已批准的 CCR-001（tasks/CONTRACT_CHANGE_REQUESTS.md），由 T-010A 同步应用到契约与落地文件；当前签名不变。
> 变更记录：v1.14（T-010A-R1 通过后，签发 T-010B）— §12.2 明确设置 DTO 与运行时快照的边界、嵌套集合所有权及五处发布路径。CCR-001 已结案；CCR-002 批准设置快照的契约注释修订，不改变签名、默认值或 JSON 格式，由 T-010B 同步落地。
> 变更记录：v1.15（T-010B 通过后，签发 T-011）— 新增 PROVIDER_REGISTRY.md，确定不可变凭据集引用、注册表快照租约、刷新屏障与实例退休规则（A03/A04）；CCR-002 结案，CCR-003 批准尚未落地的 Registry 原型接口调整及可空 CredentialSetId 字段，由 T-011 同步实施。
> 变更记录：v1.16（T-011 通过后，签发 T-012）— 新增 TRANSLATION_EXECUTION.md：固定请求设置/实际 Prompt、实例代次缓存键、清空代次与条数/字节双预算。CCR-004 批准显式配置重载和缓存原型修订；T-034 前移至 T-013 前，避免用丢弃历史的占位实现接线。
> 变更记录：v1.17（T-012-R1 通过后，签发前移 T-034）— HISTORY_STORAGE.md / CCR-005 / DB_SCHEMA.sql V2 确定收藏保护、截断结果标记、非收藏容量、摘要分页与按需详情，以及后台 SQLite 初始化/事务/查询。CCR-004 结案。
> 变更记录：v1.18（T-034-R2 通过后，签发 T-013）— TRANSLATION_ORCHESTRATOR.md / CCR-006 明确显式选择、事件顺序、输出预算、租约清理、取消与正常收尾，以及真实缓存/历史接线；CCR-005 结案。
> 变更记录：v1.19（T-013-R1 通过后，签发 T-014）— §6.1 固定动作分发的注册、UI 调度、防重入、取消及安全错误边界；CCR-006 结案，CCR-007 批准动作契约注释澄清。BACKLOG 提前安排 M2 必需的桌面/剪贴板能力和设置入口，业务 Handler 仍随对应功能交付。
> 变更记录：v1.20（T-014 通过后，签发 T-021A）— CLIPBOARD_SERVICE.md / CCR-008 固定 Win32 文本/图像格式、重试与取消提交点、原生资源所有权、输入预算及隔离测试方法；CCR-007 结案。C14-1 仅补强 Dispatcher 回归测试与报告，不返工已验收生产实现。
> 变更记录：v1.21（T-021A-R2 通过后，签发 T-022A）— DESKTOP_CONTEXT.md / CCR-009 定稿显示器筛选、物理坐标、GetDpiForWindow 路径、最近屏幕、前台快照和资源/失败边界；CCR-008 结案。桌面候选稿转为正式规范，会话草案仍留待 T-015。
> 变更记录：v1.22（T-022A-R1 通过后，签发前移 T-018）— SETTINGS_EDITING.md / CCR-010 定稿设置窗口按需作用域、四项常规草稿/冲突、主题预览、保存/应用状态、开机启动与正常退出等待；CCR-009 结案。Provider 编辑仍由 T-019 后续接入。
> 变更记录：v1.23（T-018-R6 通过后）— CCR-010 结案；PROVIDER_EDITING.md / CCR-011 冻结 Core 联合编辑计划、不可变凭据 staging/发布/清理、opaque 重试回执与瞬态草稿操作。模型发现只检查端点/凭据，不再要求已选模型。T-019 拆为 A 协调器与 B 服务页，A 不提前修改 App 接线或预设。
> 变更记录：v1.24（T-019A-R3 通过后）— CCR-011 结案；PROVIDER_SETTINGS_UI.md 冻结服务页草稿/凭据意图、全窗口一次保存、跨窗口待应用回执与容量、操作身份和退出接线。T-019B 实施 UI；CCR-012 只更新 DeepSeek 新建预设为官方基础地址和空建议模型，已有 Profile 不迁移。
> 配套规范：`specs/CONTRACTS.cs`（契约唯一真相源）· `specs/DB_SCHEMA.sql` · `specs/settings.example.json` · `specs/TRANSLATION_EXECUTION.md` · `specs/HISTORY_STORAGE.md` · `specs/TRANSLATION_ORCHESTRATOR.md` · `specs/CLIPBOARD_SERVICE.md` · `specs/DESKTOP_CONTEXT.md` · `specs/SETTINGS_EDITING.md` · `specs/PROVIDER_EDITING.md` · `specs/PROVIDER_SETTINGS_UI.md`
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
| 冷启动到启动序列完成 | ≤ 1.5 s（在 CPU 负载 < 30% 时测量；`build/measure-startup.ps1` 会输出测量时的负载，负载过高时结果仅供参考，应改用同负载下的 A/B 对比判断是否回归） |
| 热键 → 截图覆盖层可见 | ≤ 150 ms（单 4K 屏） |
| 热键 → 翻译窗口可见（划词） | ≤ 300 ms（不含选区读取超时） |
| 首个译文字符（流式） | 取决于服务；Iris 自身附加开销 ≤ 50 ms |
| 空闲内存 | Private Bytes ≤ 100 MB；Working Set ≤ 160 MB（启动后空闲 30 秒，使用 `build/measure-startup.ps1` 测量）。说明：运行时在发生第一次完整 GC 之后会额外提交约 15 MB 内存，因此同一版本会出现两种读数——GC 前约 107 / 53 MB，GC 后约 128 / 70 MB（M0 实测，T-005 与 T-005-R1 相同）。后者才是稳态，**以稳态读数对照上限**；两次读数不同不代表回归。上限为 M1–M7 预留了约 30 MB 余量 |
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
| 单元测试 | **xUnit v3**、**NSubstitute**、**Shouldly**、**RichardSzalay.MockHttp**；运行器为 **Microsoft.Testing.Platform**（`global.json` 中 `"test": { "runner": "Microsoft.Testing.Platform" }`，.NET 10 SDK 的 `dotnet test` 需要此配置） | tests/* |

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
| `Iris.Platform.Windows` | `net10.0-windows10.0.19041.0` | Core | CsWin32、UIAutomation、System.Security.Cryptography.ProtectedData；**不启用 UseWPF / UseWindowsForms** |
| `Iris.App` | `net10.0-windows10.0.19041.0`，`UseWPF`，`WinExe` | 全部 | WPF-UI、CommunityToolkit.Mvvm、H.NotifyIcon.Wpf、Hosting、Serilog、Velopack |

`Microsoft.Extensions.*.Abstractions` 在所有层均可引用（各层的 `AddIris*` 扩展方法需要）。除此之外，每个项目的 NuGet 依赖以 `Iris.ArchitectureTests` 中的**包白名单**为准；新增依赖须经架构师批准后同时更新本表与白名单。

Core 程序集禁止引用：`PresentationFramework`、`PresentationCore`、`WindowsBase`、`System.Windows.Forms`、`Microsoft.Windows.SDK.NET`、任何 `Iris.*`。

### 4.3 为什么 Workflows 在 Core

`ScreenshotTranslateHandler` 等流程只依赖端口接口（`IScreenshotService`、`IOcrService`、`ITranslateWindowPresenter` …），因此可以在 `Iris.Core.Tests` 里用 NSubstitute 完整测试“截图取消 / OCR 失败 / 无文字 / 成功”的所有分支，而不需要启动 WPF。

---

## 5. 模块职责

### 5.1 Iris.Core

| 目录 | 内容 | 实现者 |
|---|---|---|
| `Errors/` | `ErrorCode`、`ErrorInfo`、`IrisException`、`ErrorMessages`（ErrorCode → 默认中文提示的静态表） | Core |
| `Diagnostics/` | `LogText`：日志安全的文本描述（长度 + 哈希指纹 / Debug 预览），见 §12.5 | Core |
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
| `IO/` | `AtomicFile`：原子写文件（tmp + WriteThrough + 备份 + Move，共享冲突重试），以及带同样重试策略的 `CopyAsync` / `MoveAsync`，供设置与密钥文件使用 | Core |
| `DependencyInjection/` | `AddIrisCore(this IServiceCollection)` | Core |

### 5.2 Iris.Providers

| 目录 | 内容 |
|---|---|
| `Http/` | `HttpClientNames`、`ProviderJson`、`SseEvent`；`SseParser`（`static IAsyncEnumerable<SseEvent> ReadEventsAsync(Stream, CancellationToken)`，**包装 .NET 自带的 `System.Net.ServerSentEvents.SseParser`**，外面套一层 `LimitedReadStream` 限制响应大小）；`HttpErrorMapper`（HTTP 状态码 / 响应体 / 网络异常 → `IrisException`，§11.2）；`SecretRedactor`（§12.5）；`DynamicWebProxy`（§12.4）；`HttpLoggingHandler`（§11.3）；`AddIrisProviders()` 中的命名 HttpClient 注册 |
| `Translation/OpenAICompatible/` | `OpenAICompatibleTranslationProvider`（实现 `ITranslationProvider` + `IModelCatalogSource`）、Factory、`ThinkTagStripper`（流式剥离 `<think>`）、`Wire/` |
| `Translation/Anthropic/` | `AnthropicTranslationProvider`（实现 `ITranslationProvider` + `IModelCatalogSource`）、Factory |
| `Ocr/Baidu/` | `BaiduOcrProvider`、`BaiduTokenCache`、`BaiduErrorMapper`、`BaiduLanguageMap`、Factory、`Wire/` |
| `DependencyInjection/` | `AddIrisProviders(this IServiceCollection)`：注册命名 HttpClient 与各 Factory |

### 5.3 Iris.Infrastructure

`Settings/JsonSettingsService`、`Settings/SettingsMigrationRunner`、`Settings/SettingsSalvager`、`Settings/SettingsNormalizer`（§12.2）、`Settings/Migrations/*`、`Storage/AppPaths`、`Storage/SqliteConnectionFactory`、`Storage/DatabaseInitializer`、`Storage/SqliteHistoryRepository`、`DependencyInjection/AddIrisInfrastructure`。

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
| `SystemIntegration/` | `AutoStartService`、`SingleInstanceGuard`（命名 Mutex）、`FatalErrorDialog`（§10.1.1）、`NamedPipeIpc`（服务端 / 客户端）。**目录不得命名为 `System`**，否则命名空间 `Iris.Platform.Windows.System` 会在项目内遮蔽 BCL 的 `System` |

### 5.5 Iris.App

| 目录 | 内容 |
|---|---|
| 根 | `App.xaml(.cs)`、`Program.cs`（自定义 `Main`：单实例检查 → 启动 WPF）、`app.manifest` |
| `Hosting/` | `AppHostBuilder`（组合根，调用各层 `AddIris*`）、`AppBootstrapper`（启动序列 §10.1）、`ActionDispatcher`、`GlobalExceptionHandler`、`LogLevelController` |
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

**注册与分层（T-014 / CCR-007）**：`ActionDispatcher` 在 App/Hosting 实现，业务 Handler 在 Core/Workflows，WPF 调度细节不得进入 Core。按当前实际注册的 `IEnumerable<IActionHandler>` 建只读索引；允许空集合或部分动作。每个已开放动作恰有一个 Handler，不要求未来动作的占位实现。重复 Action、未定义的 Action 或 null 注册项在构造时拒绝，诊断只带枚举/数值，不格式化 Handler 对象。T-014 只注册 Dispatcher 与内部调度适配器；已存在的托盘行为在对应 Handler 交付时接线。

**调用与防重入**：`DispatchAsync` 可从任意线程调用。进入时已取消则静默返回；有效调用必须在任何 UI 排队/await 之前，用一次短锁原子取得 Action 的执行权。截图组明确为 ScreenshotTranslate、ScreenshotOcr、SilentScreenshotOcr，组内任意一项占用时其余两项也立即忽略，不排队、不取消正在执行的调用。不同非截图动作可重叠。锁内只读写执行状态，不调用外部代码、不通知、不 await。执行权覆盖 UI 排队、Handler 的完整异步执行及边界处理，所有退出路径在 finally 释放。忽略的调用立即完成，其取消令牌不得影响原调用；这里不实现“新请求替换旧请求”，窗口会话/单卡 attempt 的归属由 T-015 另行设计。

**UI 调度**：App 内部适配器以 `Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken)` 为边界，不新增 Core 公共调度端口。生产实现运行时取得 `Application.Current.Dispatcher`；构造/Host.Build 时不要求 Application 已存在，禁止在任意调用线程用 `Dispatcher.CurrentDispatcher` 创建错误的 Dispatcher。已在 UI 线程时直接调用；后台调用使用异步 Dispatcher 调度并等待返回的内部 Task（例如 `DispatcherOperation<Task>.Task.Unwrap()`），禁止只等待委托入队/返回 Task 的外层操作。测试可注入 internal 的 Dispatcher 解析委托，以独立 STA 消息循环验证真实适配器。

**取消与关闭**：执行权取得后、UI 回调真正调用 Handler 前再检查取消，并把原令牌传给 Handler。UI 排队取消不启动 Handler；开始后等待 Handler 真正退出，不能用 `WaitAsync(token)` 放弃等待并提前释放执行权。`OperationCanceledException` 一律静默结束。Dispatcher 已开始/完成关闭视为取消；尚无 Application/Dispatcher 则按调度失败进入安全错误边界。调度异常或 Handler 的同步/异步异常都必须释放执行权。

**错误展示**：未定义的 Action 或 null context 以 InvalidArgument 默认文案提示并返回；已定义但未注册的 Action 以固定“该功能尚未开放”提示并返回，不能用全量占位 Handler 掩盖缺失。`IrisException` 使用 `ErrorMessages.GetDefault(Code)`，未知异常使用 Unknown 默认文案；已知/未知执行失败均记安全日志，仅含 Action、错误码与异常类型名，不传异常对象，不复制 Message、Detail、Data、InnerException、Payload 或窗口标题。通知标题和未开放提示使用 App 资源，错误正文复用既有默认文案。`INotificationService` 已支持跨线程；通知自身失败只安全记录一次，不递归通知、不让它阻止执行权释放。无需在此重写既有全局异常处理器或通知服务。

### 6.2 输入翻译（`InputTranslateHandler`）

1. 若翻译窗口 `IsVisible && IsActive` → `HideAsync()`（热键二次按下即关闭），结束。
2. 否则用 `TryBeginOperation(cancellationToken)` 接纳意图（使用 Handler.ExecuteAsync 参数），取得租约后 `TryShowAsync(operation, new TranslateWindowRequest { Text = ctx.Payload, Origin = Input, FocusInput = true, AutoTranslate = ctx.Payload != null })`，finally Dispose。没有租约不启动工作；详见 TRANSLATION_WINDOW.md / CCR-013。
3. 用户在窗口中按 Enter → `TranslateWindowViewModel.TranslateCommand` → Orchestrator（§7）。

### 6.3 划词翻译（`SelectionTranslateHandler`）

1. 先取得翻译 operation 租约，再 `app = IDesktopContext.GetForegroundApp()`；若是 Iris 自身进程 → 按输入翻译处理，归还本次不再使用的租约。
2. `result = await ISelectionTextReader.ReadAsync(options from Settings.Selection)`。
3. `result == null` → `OpenInputWhenEmpty ? 打开空输入窗口 : Notify(Warning, "未检测到选中文本")`。
4. 否则用同一租约 `TryShowAsync(operation, Text = result.Text, Origin = Selection, Anchor = ctx.Anchor ?? 光标, AutoTranslate = true)`；读取使用租约取消令牌，finally Dispose。迟到结果返回 false，不重新显示窗口。

**鼠标划词（可选）**：`SelectionIconController`（App）在 `MouseMode != Off` 时启动 `IMouseSelectionMonitor`。收到 `SelectionGestureDetected` 后：
- 若进程在 `ExcludedProcesses` 中，或前台是 Iris 自身 → 忽略；
- `ShowIcon`：在光标右下 (+12, +12) DIP 处显示 `SelectionIconWindow`（**不激活**），`IconAutoHideMs` 后或收到 `MouseDown`（点在图标外）时隐藏；点击图标 → `Dispatch(SelectionTranslate, Trigger=SelectionIcon, Anchor=图标位置)`；
- `AutoTranslate`：延迟 120 ms 后直接 Dispatch。

### 6.4 截图翻译（`ScreenshotTranslateHandler`）

```
using operation = presenter.TryBeginOperation(调用方取消令牌)            // null = 不启动工作
shot = await IScreenshotService.CaptureRegionAsync(Translate, operation.CancellationToken) // null = 用户取消 → return
if !await presenter.TryShowAsync(operation, { Text=null, Origin=Screenshot, Anchor=选区右下角, StatusMessage="正在识别文字…", AutoTranslate=false }) → return
try   outcome = await IOcrService.RecognizeAsync(shot.Image, operation.CancellationToken)
catch IrisException e → await presenter.TryShowAsync(operation, { Error = e.ToErrorInfo() }); return
if !await presenter.TryShowAsync(operation, { Text=outcome.Text, Origin=Screenshot, AutoTranslate=true }) → return
if outcome.UsedFallback → Notify(Info, "已使用备用 OCR", ...)（每次会话仅提示一次）
```

### 6.5 截图 OCR / 静默 OCR

- **截图 OCR**：取消则返回 → `IOcrResultPresenter.ShowAsync({ Image, IsLoading = true })` → 识别 → `ShowAsync({ Image, Text, ProviderName })`；`AutoCopyOnOcr` 时复制文本；失败时 `ShowAsync({ Image, Error })`。
- **静默 OCR**：识别 → `IClipboardService.SetTextAsync` → `SilentOcrShowNotification` 时通知“已复制 N 个字符”；失败 → 通知错误。

### 6.6 截图服务（`ScreenshotService`，App）

1. `HideIrisWindows` 时以 App 内部 CaptureSuppression 暂时隐藏翻译窗与悬浮球，不能调用会取消操作的普通 HideAsync；恢复校验意图及可见性代次。等待 1 帧（~16 ms），让 DWM 完成重绘。
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

**Provider 注册表（T-011）**：以 `specs/PROVIDER_REGISTRY.md` 为本模块的完整行为规范。请求通过 AcquireSnapshotAsync 借用一组注册项，租约持有至子任务全部退出；配置变化不会直接释放正在被使用的旧实例。新凭据通过 CredentialSetId 整组引用读取；旧键仅作兼容。准确公开定义见已批准 CCR-003，实施时同步 CONTRACTS.cs 与落地文件。

### 7.1 编排（`TranslationOrchestrator`）

完整规范以 TRANSLATION_ORCHESTRATOR.md 为准。下图为流程概览；请求开始先捕获 Cache.Generation，正常读尽全部事件且最后取消检查通过后才进入缓存/历史收尾，之后迟到取消不撤回已完成结果。显式列表全部不可用时仍输出失败卡片，只有隐式选择无可用项或显式空集合才是请求级 NotConfigured。

```
TranslateAsync(request, ct)
 ├─ lease = await Registry.AcquireSnapshotAsync(ct)
 ├─ settings = lease.Settings；所有执行选项固定于此快照
 ├─ text = ITextPreprocessor.Prepare(request.Text, request.Origin, settings.Translation)
 │    空 → throw InputEmpty；超长 → throw InputTooLong
 ├─ langs = ITargetLanguageResolver.Resolve(text, request.SourceLanguage, request.TargetLanguage, settings.Translation)
 ├─ providers = 按 TRANSLATION_ORCHESTRATOR §2 选择并准备卡片
 ├─ yield TranslationStarted(langs, descriptors)
 ├─ 在排队前固定各项 PreparedPrompt、CacheKey 与缓存 Generation
 ├─ channel = Channel.CreateBounded<TranslationEvent>(容量128，FullMode=Wait)
 ├─ 对每个 provider（SemaphoreSlim(MaxConcurrentProviders) 限流）启动任务：
 │    key = TranslationCacheKeyFactory.Create(registration, preparedRequest)
 │    命中缓存 → ProviderStarted + ProviderCompleted(FromCache)
 │    否则   → ProviderStarted → await foreach chunk: ProviderDelta → ProviderCompleted
 │    catch IrisException → ProviderFailed(e.ToErrorInfo())
 │    catch Exception    → ProviderFailed(Unknown) + LogError
 │    （OperationCanceledException 且 ct 已取消 → 不产出事件，直接结束）
 ├─ 所有任务结束后 channel.Writer.Complete()
 ├─ await foreach evt in channel.Reader.ReadAllAsync(ct) → yield evt
 ├─ 最后取消检查成功 → 正常收尾：尝试缓存完整新鲜结果、限时保存历史
 └─ yield TranslationFinished(succeeded, failed)
```

要点：
- 用 `CancellationTokenSource.CreateLinkedTokenSource(ct)` 管理子任务；消费者提前停止枚举（`DisposeAsync`）时也要取消全部子任务。
- 注册表租约在全部子任务退出后释放，包括请求失败和提前结束路径；调用方不直接 Dispose 借出的 Provider。请求与缓存的精确一致性规则见 TRANSLATION_EXECUTION.md：实例代次与实际 PreparedPrompt 纳入键，Generation 拒绝清空前的迟到写入；T-012 提供能力，T-013 接入完整流程。
- 缓存固定总记账预算 16 MiB、单条 1 MiB，并同时遵循 CacheCapacity。清空、禁用和重新启用有明确代次边界；不将全局 Clear 当作配置代次隔离的替代方案。
- T-034 历史仓储前移到 T-013 之前；默认启用历史时必须实际持久化，不注入静默丢数据的生产替身。
- 历史写入/收藏保护与查询以 HISTORY_STORAGE.md 为准：成功但截断的结果保留标记；列表只查询摘要，展开/复制/重译时读取详情。SQLite async API 不代表后台执行，由仓储显式调度整个数据库工作单元。
- `Elapsed` 用 `Stopwatch` 从 ProviderStarted 开始计时。
- 历史写入失败不影响事件流（记 Warning 日志即可）。

### 7.2 ViewModel 消费规范（`TranslateWindowViewModel`）

- 完整行为以 TRANSLATION_WINDOW.md 为准。新意图立即撤销旧资格，实际调用槽位直到 EOF/DisposeAsync/App 收尾结束才释放；最多一个主调用、一个重试及各一个最新等待项。
- 主 `TranslationStarted` 按 Providers 创建卡片；重试只更新目标卡，所有事件/排队刷新在 UI 执行时检查 (OperationId, ProviderId, RequestId)。
- `ProviderDelta` 使用有界缓冲与全窗口唯一约 33ms 刷新；`ProviderCompleted` 清除旧缓冲并应用权威正文。跨重试共同遵守正文预算，本地显示截断与模型 Truncated 分开。
- `AutoCopy == FirstResult` 只接纳初始主请求的首个完整且未截断有效结果，重试不参与；复制任务由退出屏障持有。
- 失败展示安全错误及重试；`NotConfigured` / `AuthenticationFailed` 链接 `ISettingsWindowPresenter.ShowAsync("translation")`。单卡重试继续使用 `ProviderIds=[id], BypassCache=true, RecordHistory=false`。

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

#### 7.3.1 语言目录（`LanguageCatalog`）

- `All`：上表 14 种语言，顺序即表格顺序（也是 UI 下拉框顺序），不含 Auto。
- 代码比较一律**不区分大小写**；`Find` 返回的 `LanguageInfo.Code` 为表中的规范写法（如传入 `ZH-HANS` 返回 `zh-Hans`）。`Find(null / 空白 / auto)` 返回 null。
- `IsSameFamily(a, b)`：比较 BCP-47 **主语言子标签**（第一个 `-` 之前的部分，不区分大小写）。因此 zh-Hans 与 zh-Hant 同族，en 与 en-US 同族。任一方为 Auto、null 或空白时返回 false。
- `GetPromptName(code)`：Auto → `the auto-detected language`；表中语言 → EnglishName；其他代码 → 原样返回去除首尾空白后的代码。不在表中的代码是**允许的**：用户可以在设置中手工填写 `pl`、`nl` 等代码，大模型通常能够理解。

#### 7.3.2 语言检测（`ScriptLanguageDetector`，纯函数，线程安全）

1. 用 `text.EnumerateRunes()` 逐个**码点**遍历（CJK 扩展 B 等字符在 UTF-16 中是代理对，按 `char` 遍历会统计错误），只统计 `Rune.IsLetter(r)` 为真的码点，记总数为 L。L = 0 → `auto`，置信度 0。
2. 按以下 Unicode 范围给每个字母归类（闭区间）：

| 脚本 | 范围 |
|---|---|
| 假名 | 3040–309F、30A0–30FF、31F0–31FF、FF66–FF9F |
| 谚文 | 1100–11FF、3130–318F、A960–A97F、AC00–D7AF、D7B0–D7FF |
| 汉字 | 3400–4DBF、4E00–9FFF、F900–FAFF、20000–323AF |
| 西里尔 | 0400–052F |
| 泰文 | 0E00–0E7F |
| 阿拉伯 | 0600–06FF、0750–077F、08A0–08FF、FB50–FDFF、FE70–FEFF |
| 拉丁 | 0041–005A、0061–007A、00C0–024F（只会命中字母）、1E00–1EFF、FF21–FF3A、FF41–FF5A |

3. 按顺序判定，命中即返回（ratio = 该脚本字母数 / L）：
   假名 ≥ 0.05 → `ja`（置信度取假名与汉字比例之和，上限 1）→ 谚文 ≥ 0.30 → `ko` → 汉字 ≥ 0.30 → `zh-Hans` → 西里尔 ≥ 0.50 → `ru` → 泰文 ≥ 0.50 → `th` → 阿拉伯 ≥ 0.50 → `ar` → 拉丁 ≥ 0.70 → 进入第 4 步 → 其他 → `auto`（置信度 0）。
   其余脚本命中时，置信度 = 该脚本的 ratio。
4. 英语判定（只在拉丁 ≥ 0.70 时进行）：
   - 按空白切分为词；每个词去掉首尾非字母字符（词内的 `'` 与 `-` 保留，如 `don't`、`e-mail`）；丢弃不含字母的词。设剩余词数为 W。
   - **W ≤ 2 且每个词都只由 ASCII 字母、词内 `'` 或 `’`（U+2019，网页与 Word 中常见的弯撇号）、词内 `-` 组成** → `en`，置信度 0.6（短语如 “Hello world”、“Good morning”；单个法语词也会被判为 en，属于已知的可接受误差）。
   - **W ≥ 3 且英文停用词占比 ≥ 0.15** → `en`，置信度 = 拉丁 ratio。
   - 其他 → `auto`，置信度 0（法语、德语、越南语等句子都会落到这里，由大模型自行识别）。
   - 停用词表（不区分大小写，共 68 个）：`a an the is are was were be been being am do does did have has had i you he she it we they me him her us them my your his its our their this that these those and or but not no of to in on at by for with from as if so than then there what which who how can will would should could`。
5. 已知且可接受的局限：全是汉字、不含假名的日文会判为 zh-Hans；繁体中文判为 zh-Hans（与 zh-Hant 同族，不影响目标语言的自动切换）。

#### 7.3.3 目标语言解析（`TargetLanguageResolver`）

旧三参数入口每次捕获一次 `ISettingsService.Current.Translation` 后委托同一逻辑；显式 TranslationSettings 重载仅使用给定快照。编排器使用显式重载，规则不变。
1. 规范化：`requestedSource` 为 null 或空白 → Auto；`requestedTarget` 为 null、空白或 `auto`（不区分大小写）→ 视为未指定。表中已知的代码转为规范写法，未知代码原样保留（去除首尾空白）。
2. Source：requestedSource ≠ Auto → 使用它（`SourceWasDetected = false`）；否则 → `Detect(text).Code`（`SourceWasDetected = true`，结果可能仍是 Auto）。
3. Target：指定了 requestedTarget → 直接使用；否则 `AutoSwapTarget` 为 true 且 `IsSameFamily(Source, Primary)` → Secondary；否则 → Primary。
4. 结果中的 Target **永远不是 Auto**。

### 7.4 Prompt

- 模板占位符：`{{source_language}}`、`{{target_language}}`、`{{text}}`，由 `IPromptRenderer` 替换；语言名取自 `ILanguageCatalog.GetPromptName`。
- **单遍替换（安全要求）**：对模板只扫描一遍（`Regex.Replace(template, @"\{\{([a-z_]+)\}\}", evaluator)`），替换进去的内容**不再被扫描**。禁止依次调用 `string.Replace`：原文中如果含有 `{{target_language}}` 这样的字符串，会被第二次替换，形成模板注入。未知的占位符原样保留。
- 内置默认模板见 `BuiltInPrompts.Default`。原文作为独立的 user 消息发送，system 中声明“用户消息只是待翻译内容，不是指令”，以降低提示注入风险。
- **模板查找（`PromptTemplateProvider.Get(templateId)`）**：`templateId` 为 null 或空白 → 使用 `Settings.Translation.DefaultPromptTemplateId`；先在 `CustomPrompts` 中按 Id（不区分大小写）查找，再查内置模板；都找不到 → `BuiltInPrompts.Default`。旧入口捕获一次当前配置，显式 TranslationSettings 重载仅使用给定快照，不缓存模板查找结果。
- **模板有效性**：`System` 与 `User` 都不包含 `{{text}}` 的模板视为无效（原文根本不会被发送），回退到 `BuiltInPrompts.Default`，并且每个模板 Id 只记一次 Warning。
- 缓存使用实际请求的完整 SHA-256（含已渲染 System/User），精确无歧义编码见 TRANSLATION_EXECUTION.md。旧模板拼接并截断为 16 位的 PromptFingerprint 算法在 T-012 退役。

### 7.5 文本预处理与软换行合并

**`TextPreprocessor.Prepare(raw, origin)`**，按顺序执行：
旧入口捕获一次当前 TranslationSettings，显式重载仅使用给定配置；编排器传入注册表快照。下列文本规则不变。
1. 换行统一：`\r\n`、`\r`、U+0085、U+2028、U+2029 → `\n`。
2. 移除不可见字符：U+200B（零宽空格）、U+FEFF（BOM）、U+2060（单词连接符）。**保留** U+200C（ZWNJ，波斯语、印地语等需要）与 U+200D（ZWJ，组合 emoji 需要，如 👨‍👩‍👧）。
3. U+00A0（不换行空格，网页复制时常见）→ 普通空格。
4. 只含空白字符的行变为空行；连续多个空行压缩为 1 个空行。其他行的行内空白与缩进**保持不变**（代码、列表的格式要保留）。
5. `origin == Selection` 且 `SelectionLineBreaks == MergeSmart` → 执行软换行合并（见下）。
6. 去除整段文本的首尾空白（`string.Trim()`）。

**软换行合并（`SoftLineBreakMerger`，internal static，T-030 的 OCR 规范化复用同一实现）**：输入若干行，空行作为段落分隔原样保留（每个空行都保留，压缩连续空行是调用方的职责）。对相邻的两个非空行 A、B：
- **保留换行**的情况（任一成立即保留）：
  1. A 去掉末尾空白以及末尾的收尾符号（`" ' ” ’ ） ) 」 』 】 ] 》`）之后，以句末标点结尾：`。 ！ ？ . ! ? ： : ； ;`。
  2. B 去掉开头空白后，以列表标记开头：`- `、`* `、`• `、`· `（符号后必须有空格），或匹配 `^\d{1,3}(?:[.)]\s|、)`（`.` 与 `)` 后面必须跟空白，避免把 “1.5 kg”、“3.14” 这样的小数误认为列表编号；`、` 后面通常不加空格，保持原样）、`^[（(]\d{1,3}[)）]`。
  3. 调用方提供的 `isParagraphBreakAfter(index)` 返回 true（OCR 根据行距判断；文本预处理不提供此回调）。
- 否则**合并**：去掉 A 的末尾空白与 B 的开头空白之后：
  1. A 以 `-` 结尾、`-` 前一个字符是拉丁字母、且 B 以拉丁字母开头 → 去掉 `-` 后直接拼接（“inter-\nnational” → “international”）。已知局限：真正的连字符词（“well-\nknown”）也会被连在一起。
  2. A 的最后一个字符与 B 的第一个字符**都**属于 CJK（汉字、假名、U+3000–303F 的 CJK 标点、U+FF00–FFEF 的全角字符）→ 直接拼接，不加空格。谚文**不算** CJK（韩文单词之间需要空格）。
  3. 其他情况 → 用一个空格拼接。
- 字符的脚本归属统一使用 `UnicodeScripts.Classify(Rune)`（从 `ScriptLanguageDetector` 中提取的 internal static 方法），不得另写一份码点范围。

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

**T-012 请求级 Prompt 规则**：两个翻译 Provider 均优先使用非 null 的 ProviderTranslateRequest.PreparedPrompt，不再调用模板服务或 Renderer。下文“每次查找模板/渲染”的说明仅适用于 PreparedPrompt=null 的直接调用兼容路径；具体线协议和 SDK 选择不变。

### 9.1 OpenAI 兼容（`OpenAICompatibleTranslationProvider`）

**构造与配置判定**
- 由 `OpenAICompatibleTranslationProviderFactory`（`ITranslationProviderFactory`，单例，`AddIrisProviders` 中以 `TryAddEnumerable` 注册）创建；依赖 `IHttpClientFactory`、`IPromptTemplateProvider`、`IPromptRenderer`、`ISettingsService`、`TimeProvider`、`ILoggerFactory`。`Create` 时 `profile.Kind` 不是 OpenAICompatible → `ArgumentException`；`OpenAIOptions` 为 null → 使用 `new OpenAICompatibleOptions()`。Provider 实例不可变，设置变化由 Registry 重建实例（T-011）。
- **`Descriptor.IsConfigured`** = `BaseUrl` 是 scheme 为 http / https 的绝对 URI **且** `Model` 非空白 **且**（`ApiKey` 非空白 **或** BaseUrl 的主机是本地地址——判定复用 `DynamicWebProxy.IsLocalAddress`，覆盖本机与局域网的 Ollama / LM Studio）。未配置时 `TranslateStreamAsync` 在第一次 `MoveNextAsync` 时抛 `IrisException(NotConfigured)`，不发出任何请求。**模型发现（CCR-011 / T-019A）**：`ListModelsAsync` 使用相同端点/凭据条件，但不要求 Model 非空；不得用 Descriptor.IsConfigured 作含模型条件的统一 guard。其它 HTTP/超时/错误规则不变。
- **公共骨架重构的边界**：上述延迟执行要求适用于 Provider 对外返回的序列，不能仅在 `ProviderCallGuard` 内成立。获取序列、创建枚举器时不做配置失败判断或启动 I/O；`NotConfigured` 在第一次 `MoveNextAsync` 时抛出。同步参数校验（如 request 为 null）与配置失败是两类问题，不得用前者解释后者的提前抛出。

**请求**
- `POST {BaseUrl.TrimEnd('/')}/{ChatCompletionsPath.TrimStart('/')}`，使用命名客户端 `iris.llm`。
- Header：`Authorization: Bearer {ApiKey}`（Key 为空白时不发送）；然后逐个加入 `ExtraHeaders`（`TryAddWithoutValidation`；名称为 `Authorization` 的条目**忽略**——密钥只能来自 ISecretStore；添加失败的条目忽略；两种情况都只记 Debug 日志且**只写头名称，不写值**）。
- Body：`ChatCompletionRequest`。messages = `[system: Rendered.System, user: Rendered.User]`，`Rendered.System` 为空白时**省略 system 消息**（部分服务拒绝空 content）；`temperature` 仅在 Profile 设置时发送；`MaxOutputTokens` 按 `MaxTokensField` 写入 `max_tokens` 或 `max_completion_tokens`（null 时两者都不发送）；`stream` 始终发送；`Stream && IncludeStreamUsage` 时带 `stream_options`。模板每次调用时通过 `IPromptTemplateProvider.Get(profile.PromptTemplateId)` 获取，再用 `IPromptRenderer.Render` 渲染。
- 序列化：`JsonSerializer.SerializeToUtf8Bytes(body, ProviderJson.Options)` → `ByteArrayContent`，`Content-Type: application/json`（长度已知、可重复发送，满足 §11.3 的重试要求）。

**超时（“无进展超时”）**
- `timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(profile.TimeoutSeconds), timeProvider)`，与调用方的 ct 链接后传给 `SendAsync` 与读流。流式模式下**每收到一个 SSE 事件就调用 `timeoutCts.CancelAfter(同一时长)` 重置计时**：从发出请求到收到响应头、以及相邻两个事件之间，任何一段超过 `TimeoutSeconds` 即超时；只要持续有输出，长译文不会被切断。非流式模式与模型列表不重置（即总超时）。（架构师已实测：`CancelAfter` 重置对 `FakeTimeProvider` 同样生效，测试可以推进虚拟时间。）
- **异常归类（统一出口）**：任何异常发生时按以下顺序判断——① 调用方 ct 已取消 → 抛 `OperationCanceledException`（原样重抛；若原异常不是 OCE，抛 `new OperationCanceledException(ct)`）；② `timeoutCts` 已触发 → 抛 `HttpErrorMapper.Timeout(providerId, timeout)`；③ 已是 `IrisException` → 原样抛出；④ `HttpErrorMapper.TryMapException` 返回非 null → 抛出映射结果；⑤ 其他 → 原样抛出（编程错误，由编排器按 Unknown 处理）。异步迭代器中 `yield return` 不能位于带 catch 的 try 内，因此内部实现为“不做异常处理的迭代器”，外层迭代器在 try/catch 中手动调用 `MoveNextAsync`、在 try 之外 `yield return`。

**响应处理**
- `HttpCompletionOption.ResponseHeadersRead`。非 2xx → `HttpErrorMapper.FromResponseAsync(response, providerId, [ApiKey], ct)` 并抛出。
- 2xx 且 `Content-Type` 的媒体类型为 `application/json` → 无论是否请求了流式，都按**非流式**处理（部分服务忽略 `stream=true`，或直接返回错误 JSON）。其他媒体类型（含缺失）→ 按 SSE 处理。
- **SSE**：`SseParser.ReadEventsAsync`。对每个事件：`data == "[DONE]"` → 结束；`EventType == "error"` 或 data 是含 `error` 属性的 JSON 对象 → `HttpErrorMapper.FromBody(200, data, …)` 并抛出；否则反序列化为 `ChatCompletionChunk`：`choices` 为 null 或空时跳过（`include_usage` 的最后一个 Chunk 就是这样）；取 `choices[0].delta.content` 作为 Delta（**忽略 `reasoning_content`**）；记录最后一个非空的 `finish_reason`；`usage` 出现时记下。data 不是合法 JSON → `JsonException` → 经统一出口映射为 InvalidResponse。
- **流的结束**：收到 `[DONE]`，或流结束（EOF）时已经收到过 `finish_reason` → 正常结束；EOF 时既没有 `[DONE]` 也没有 `finish_reason` → `IrisException(InvalidResponse)`：一个 SSE 事件都没有收到、且媒体类型不是 `text/event-stream` 时，`detail` 为 `"服务未返回事件流（Content-Type: {媒体类型，缺失时写“无”}），请检查 BaseUrl"`（常见于 BaseUrl 填错、网关返回 200 的 HTML 页面）；否则为 `"响应流意外结束"`（连接中途断开）。
- **非流式**：用 `LimitedReadStream`（上限 `SseParser.DefaultMaxResponseBytes`）包装响应流后反序列化。根对象含 `error` 属性 → `FromBody(200, …)` 并抛出（需要原文，因此先把受限流读成字符串，再解析）；否则按 `ChatCompletionResponse` 取 `choices[0].message.content`（忽略 `reasoning_content`）；`choices` 为空或 `content` 为 null → InvalidResponse。
- **finish_reason**：`stop` → Completed；`length` → Truncated；`content_filter`、`sensitive`（智谱 GLM）→ 抛 `ContentRefused`（已输出的部分作废）；null 或其他值 → Completed，并记一条 Debug 日志（写入该值）。各服务的取值以 T-009 核对官方文档的结果为准。
- **输出**：内容片段依次经过 `ThinkTagStripper`（`StripThinkTags` 为 true 时）后，非空才产出 `TranslationChunk(delta)`（FinishReason 为 null）。**最后恰好产出一个**带 `FinishReason`（非 null）与 `Usage` 的 Chunk，其 Delta = 剥离器 `Flush()` 的剩余内容（可为空串）。`Usage` = `TokenUsage(prompt_tokens, completion_tokens)`（任一项缺失时该项为 null）；没有 usage 对象、或两项都缺失时为 null。非流式同样遵守此约定：一个 Chunk，Delta 为剥离后的完整文本，同时带 FinishReason 与 Usage。
- 消费者提前结束枚举（`DisposeAsync`）时必须释放 `HttpResponseMessage`，不得泄漏连接。

**`<think>` 剥离（`ThinkTagStripper`，internal sealed，每次请求一个实例，非线程安全）**
- `string Process(string delta)` + `string Flush()`。只处理**开头**：跳过前导空白后，如果内容以 `<think>` 开头（区分大小写）→ 丢弃直到 `</think>`（含），然后去掉其后的前导空白（可能跨越多个 Delta）；如果前导空白之后的第一个字符无法构成 `<think>` 的前缀 → 进入直通状态，**原样**输出包括前导空白在内的全部内容，之后不再检测（译文中间出现的 `<think>` 字样必须保留）。
- 标签可能被拆在多个 Delta 之间：判断阶段最多缓冲 `<think>` 的 7 个字符（加前导空白）；丢弃阶段只需保留最后 7 个字符用来匹配跨片段的 `</think>`。
- `Flush()`：仍在判断阶段 → 输出缓冲内容（原文其实就是 `<thi` 之类的短文本）；仍在丢弃阶段（`</think>` 始终没有出现，例如在思考中被 max_tokens 截断）→ 输出空串。
- 已知限制：个别服务只输出 `</think>` 而省略开头的 `<think>`，本剥离器不处理此情况。

**`TestAsync`**
- 以 `ProviderTranslateRequest("Hello, world.", "en", Settings.Translation.PrimaryTargetLanguage)` 走与 `TranslateStreamAsync` 相同的路径，拼接全部 Delta 作为 `Sample`；`Latency` 用 `TimeProvider.GetElapsedTime` 计算。`IrisException` → `Success = false`、`Error = ex.ToErrorInfo()`；调用方 ct 取消 → 抛 `OperationCanceledException`（取消不是测试结果）；其他异常 → `Success = false`、`Error` 为 `ErrorCode.Unknown`（Message 取 `ErrorMessages.GetDefault(Unknown)`，Detail 只写异常类型名，`ProviderId` 为 Profile Id），并记 Warning 日志（只写异常类型名，不传入异常对象——异常消息可能包含服务端响应体）。

**模型列表（`IModelCatalogSource`）**
- `GET {BaseUrl.TrimEnd('/')}/models`，Header 规则与翻译请求相同，总超时 `TimeoutSeconds`，异常归类同上。非 2xx → `HttpErrorMapper`。返回 `data[].id`：去掉空白项、去重（序数比较），再按 `StringComparer.OrdinalIgnoreCase` 排序；`data` 为 null → InvalidResponse。

**日志**：请求开始记 Debug（ProviderId、Model、`LogText.Fingerprint(原文)`）；结束记 Debug（ProviderId、FinishReason、耗时、Usage）。不得记录 Key、请求头、完整原文或译文。

- **OpenAI 特殊情况**：`error.code == "insufficient_quota"`（HTTP 429）→ `QuotaExceeded`，而不是 `RateLimited`（由 `HttpErrorMapper.MapStatus` 实现）。

流式响应示例（测试夹具 `tests/Iris.Providers.Tests/Fixtures/openai_stream_ok.txt`）：

```
data: {"id":"c1","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}

data: {"id":"c1","choices":[{"index":0,"delta":{"content":"你好"},"finish_reason":null}]}

data: {"id":"c1","choices":[{"index":0,"delta":{"content":"，世界"},"finish_reason":null}]}

data: {"id":"c1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

data: [DONE]
```

### 9.2 Anthropic（`AnthropicTranslationProvider`，官方 SDK）

> 本节依据架构师对 NuGet `Anthropic` **12.50.0** 的编译与本地服务器实测（2026-09-28）。标注“实测”的条目是观察到的 SDK 行为，不是推测；升级 SDK 版本时须重新验证这些条目。

**包与原则**
- NuGet `Anthropic`（官方 C# SDK，版本 12.50.0）。非 beta 类型在 `Anthropic.Models.Messages`，beta 类型在 `Anthropic.Models.Beta.Messages`（两者都有 `MessageCreateParams` 与 `Role`，同时引用时给 beta 命名空间起别名，例如 `using Beta = Anthropic.Models.Beta.Messages;`），异常在 `Anthropic.Exceptions`。**不要手写 Messages API 的 HTTP 请求。**
- 由 `AnthropicTranslationProviderFactory`（`ITranslationProviderFactory`，单例，`TryAddEnumerable` 注册）创建，依赖与 §9.1 的 Factory 相同。`Create` 时 `Kind` 不是 Anthropic → `ArgumentException`；`AnthropicOptions` 为 null → `new AnthropicOptions()`。

**客户端构造（每个 Provider 实例一个，在构造函数中创建）——安全要求**

```csharp
new AnthropicClient
{
    ApiKey = string.IsNullOrWhiteSpace(credentials.ApiKey) ? null : credentials.ApiKey,
    AuthToken = null,
    BaseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl) ? EnvironmentUrl.Production : profile.BaseUrl.TrimEnd('/'),
    HttpClient = httpClientFactory.CreateClient(HttpClientNames.Llm),
    MaxRetries = 0,
    Timeout = Timeout.InfiniteTimeSpan,
}
```
- **`ApiKey`、`AuthToken`、`BaseUrl` 三项必须全部显式赋值（包括显式赋 null）**。实测：这三个属性的默认值分别取自环境变量 `ANTHROPIC_API_KEY`、`ANTHROPIC_AUTH_TOKEN`、`ANTHROPIC_BASE_URL`。只设置 `ApiKey` 时，请求里仍会带上 `Authorization: Bearer {ANTHROPIC_AUTH_TOKEN}`；不设置 `BaseUrl` 时，请求会发往 `ANTHROPIC_BASE_URL`。安装了 Claude Code 等工具的机器上这些变量很常见（架构师与开发的机器上都有），后果是用户本机的其他凭据被发给用户配置的反代服务器，或者请求被发到错误的地址。重写 `ShouldAutoResolveCredentials` **无效**（实测）。
- `MaxRetries = 0`：重试由 `iris.llm` 的弹性管道负责（§11.3）。SDK 默认重试 2 次，与管道叠加后一次请求最多会发出 6 次（实测 SDK 自身 3 次）。
- `Timeout = Timeout.InfiniteTimeSpan`：超时由 Provider 的 CTS 负责（与 §9.1 相同的无进展超时）。
- `BaseUrl` 是 `/v1/messages` 之前的部分：实测 `BaseUrl = "http://h/proxy"` → `POST http://h/proxy/v1/messages`。反代地址**不应**以 `/v1` 结尾（设置页 T-019 需提示）。
- **`Descriptor.IsConfigured`** = `Model` 非空白 **且**（`BaseUrl` 为空白，或为 scheme 为 http / https 的绝对 URI）**且**（`ApiKey` 非空白，或 `BaseUrl` 非空白且主机是本地地址（`DynamicWebProxy.IsLocalAddress`））。未配置 → 与 §9.1 相同，第一次 `MoveNextAsync` 时抛 `NotConfigured`，不发请求。
- **模型发现（CCR-011 / T-019A）**：ListModelsAsync 只采用上述端点/凭据条件，不要求已选 Model；官方空地址仍需 Key，自定义本地地址继续允许免 Key。Descriptor 与翻译/TestAsync 的模型条件保持不变。
- `KindName = "Anthropic"`。

**SDK 心跳适配层（T-010-R1）**
- SDK 12.50.0 的类型化枚举不向调用方暴露 `ping`。允许在 SDK 与命名客户端之间增加透明的响应流观察层，Messages 请求构造、JSON 解析及 beta 协议仍由 SDK 负责。不能因为 SDK 隐藏心跳而删除心跳保活要求。
- **使用 SDK 原生扩展点**：`AnthropicClient.Handlers` 接受 `IReadOnlyList<DelegatingHandler>`；将每个 Provider 自有的进度观察 handler 放入其中，HttpClient 直接使用命名客户端，handler 通过 `base.SendAsync` 转发。无需再创建第二个 HttpClient 或 CloneRequest，也不手工指定 InnerHandler/跨 client 复用 handler。架构师已联网核对 NuGet 所指向提交 `2beeb9f9b402b1cdb8030e7cdc38cc6cae5d42aa` 的 ClientOptions.cs，并用本地替身验证单次发送、流枚举和释放链；证据见 T-010 评审记录。
- **每一层 HttpClient 都须显式设为 `Timeout.InfiniteTimeSpan`**，包括自行创建的包装客户端；仅设置 `AnthropicClient.Timeout` 与命名客户端不足以覆盖包装层。T-010 评审实测发现包装层仍为默认 100 秒，且其超时会在 Provider CTS 尚未触发时抛出 `TaskCanceledException`。翻译与模型列表的超时均由公共骨架负责，保持“调用方取消优先、Provider 超时映射为 Timeout”的顺序。
- **进展仍按完整 SSE 事件计量**：收到完整、含 data 的事件（包括 ping）后重置；读取到任意字节不等于收到事件。观察器须支持分片、UTF-8 字符跨读取、LF / CRLF / CR，不能因半条事件持续到达而无限续期。不缓存完整原文、译文或无限增长的未完成事件；不得改变交给 SDK 的字节。已有 SDK 类型化事件同样按流处理规则重置。
- 每次调用拥有独立的观察状态、回调与 CTS。同一 Provider 并发翻译时，A 的心跳不能延长 B 的超时；A 取消或结束不能清除 B 的回调。模型列表采用总超时，不受翻译心跳影响。
- **资源所有权**：包装层在把响应成功交给 SDK 之前负责其释放；取流、创建包装内容或复制响应头失败/取消时，须释放已取得的响应及其内容，然后交由公共骨架归类原始失败。成功后由 SDK/枚举器释放响应链；提前 break、取消、失败、正常结束均须覆盖。请求由 SDK 创建和管理，适配层不自行克隆或提前释放请求 Content。Provider.Dispose 释放其拥有的客户端链且可重复调用；不得释放全局共享的工厂或服务。

**请求**
- `Model = profile.Model`；`MaxTokens = profile.MaxOutputTokens ?? 16000`；`System = Rendered.System`（空白时不设置）；`Messages = [User: Rendered.User]`；`AnthropicOptions.Effort` 非空白时 `OutputConfig = new OutputConfig { Effort = effort }`（字符串可直接赋值，实测序列化为 `"output_config":{"effort":"low"}`），空白或 null 时不设置 `OutputConfig`。
- **不发送 `Temperature`**（新模型对非默认采样参数返回 400），忽略 `profile.Temperature`。**不设置 `Thinking`**：Claude Opus 5 默认启用自适应思考，用 effort 控制深度；思考内容默认不返回正文（display 为 omitted），流中的 thinking 块一律忽略。不构造以 assistant 消息结尾的预填充。
- 模板与渲染同 §9.1。
- **拒答回退**：满足以下**全部**条件时走 beta 路径，否则走非 beta 路径 `client.Messages.CreateStreaming(params, token)`：
  1. `AnthropicOptions.RefusalFallback = true`；
  2. `profile.BaseUrl` 为空白（官方地址；反代未必支持该 beta）；
  3. `profile.Model` 以 `claude-opus-5` 或 `claude-fable-` 开头（`OrdinalIgnoreCase`）——这是已知支持服务端回退的模型；对其他模型发送该参数的行为未经验证。

  beta 路径：`client.Beta.Messages.CreateStreaming(new Beta.MessageCreateParams { …同上…, OutputConfig = new Beta.BetaOutputConfig { Effort = … }, Fallbacks = new Beta.Default(), Betas = [AnthropicBeta.ServerSideFallback2026_07_01] }, token)`。实测线上格式为 `"fallbacks":"default"` + 请求头 `anthropic-beta: server-side-fallback-2026-07-01`。服务端回退对客户端透明：拒答发生在输出之前时，流直接由回退模型输出；发生在输出中途时，已输出的部分保留，由回退模型接着输出（中间会出现一个 `fallback` 类型的内容块，忽略即可）。最终仍为 `refusal` 表示整条回退链都拒绝了。

**流处理**（beta 与非 beta 的事件类型不同，但 `TryPick*` 方法名相同，实测两条路径都能编译）
- 每收到一个流事件（包括 `ping`）都重置无进展超时计时（§9.1 的规则）。
- `TryPickStart` → 记录 `Message.Usage.InputTokens`。
- `TryPickContentBlockDelta` 且 `Delta.TryPickText(out var t)` → `t.Text` 非空时产出 Delta。**只取 text delta**：thinking / signature delta、`fallback` 块、其他块一律忽略。
- `TryPickDelta`（message_delta）→ 记录 `Delta.StopReason`（用 `.Raw()` 取原始字符串；实测未知的取值会原样透传，例如 `brand_new_reason`）与 `Usage.OutputTokens`（累计值，取最后一次）。
- **流的结束**：实测流在 `message_delta` 之前断开时，SDK **静默结束、不抛异常**。因此枚举结束时如果没有收到过 `stop_reason` → `IrisException(InvalidResponse, detail: "响应流意外结束")`。
- `stop_reason`：`end_turn`、`stop_sequence` → Completed；`max_tokens`、`model_context_window_exceeded` → Truncated；`refusal` → 抛 `ContentRefused`（已输出的部分作废；`Detail = "模型拒绝回答（stop_reason=refusal）"`）；其他 → Completed，并记一条 Debug 日志（写入该值）。
- 输出约定与 §9.1 完全相同：内容 Chunk 的 FinishReason 为 null；**最后恰好一个** Delta 为空串、带 FinishReason 与 `Usage = TokenUsage(InputTokens, OutputTokens)` 的 Chunk（两者都没有时 Usage 为 null；SDK 的 token 数为 long，转换为 int 时超出 `int.MaxValue` 的取 `int.MaxValue`）。

**异常映射**（全部经过与 §9.1 相同的统一出口：用户取消 → 超时 → 下表 → `HttpErrorMapper.TryMapException` → 原样抛出）

| SDK 异常（实测） | 何时出现 | 映射 |
|---|---|---|
| `AnthropicApiException` 及其子类（`Anthropic4xxException`、`AnthropicRateLimitException`、`AnthropicUnauthorizedException`、`AnthropicNotFoundException`、`Anthropic5xxException`…） | 非 2xx 响应。带 `StatusCode`（`HttpStatusCode`）与 `ResponseBody` | `HttpErrorMapper.FromBody((int)ex.StatusCode, ex.ResponseBody, providerId, [ApiKey])`。§11.2 的状态码表已覆盖全部情况（413 → InputTooLong、529 → ProviderUnavailable、网关返回的 HTML 502 等），**不需要**按子类逐个 catch |
| `AnthropicSseException` | 流中出现 `event: error`（2xx 之后）。没有状态码，`ErrorType` 为错误类型枚举，未知类型时为 null | 按下方“等效状态码”调用 `HttpErrorMapper.FromBody(等效状态码, json, …)`。`json` = `ex.Message` 中第一个 `'` 与最后一个 `'` 之间的部分（实测格式：`SSE error returned from server: '{json}'`），提取失败时使用整条消息 |
| `AnthropicInvalidDataException` | 事件 JSON 无法解析（内部异常为 `JsonException`） | `IrisException(InvalidResponse, detail: "无法解析的流事件")` |
| `AnthropicIOException` | 网络错误（内部异常为 `HttpRequestException`） | `HttpErrorMapper.TryMapException(ex.InnerException ?? ex, providerId)`；结果为 null 时 → NetworkUnavailable，Detail 只包含固定说明与异常类型名，不读原始 Message，也不附原始异常链（§11.2） |
| `OperationCanceledException` / `TaskCanceledException` | 取消或超时（实测：读流时取消抛 `TaskCanceledException`） | 统一出口的前两步 |

等效状态码（`ErrorType` → 状态码）：`RateLimitError` → 429；`OverloadedError` → 529；`ApiError` → 500；`InvalidRequestError` → 400；`AuthenticationError` → 401；`PermissionError` → 403；`NotFoundError` → 404；`BillingError` → 402；`TimeoutError` → 504；null 或其他 → 500。SDK 12.50.0 没有 `RequestTooLarge` 枚举成员：当 ErrorType 无法识别时，解析所提取 JSON 的 `error.type`，仅其值精确为 `"request_too_large"` 才映射 413；不能搜索整个响应或 message 中的这个单词。JSON 无法解析、字段缺失或非字符串时保持未知类型的 500 回退，不把解析失败或原文附为内部异常。HTTP 错误仍以实际 HTTP 状态码为准。异常的 `HttpStatus` 即为实际或等效状态码。

- **隐私（安全要求）**：实测 `AnthropicApiException.Message` 与 `AnthropicSseException.Message` 都**原样包含服务端响应体**（反代可能在其中回显密钥）。因此：① `Detail` 一律来自 `HttpErrorMapper` 的安全输出；② **不得**把这些 SDK 异常及网络/解析异常的原始异常链挂进对外 IrisException；③ 不把它们作为日志的 Exception 参数。ToErrorInfo 只复制已提供的 Detail，禁止回退到 InnerException.Message（CCR-001）。

**TestAsync**：与 §9.1 完全相同（共用实现，见 T-010 工单）。

**模型列表（`IModelCatalogSource`）**：`await client.Models.List(cancellationToken)`，遍历全部分页（SDK 的分页 API 以编译结果为准；最多取 1000 项），返回 `ID`，去空白、去重、排序规则同 §9.1：用 `StringComparer.Ordinal` 去重，用 `StringComparer.OrdinalIgnoreCase` 排序。大小写不同的 ID 必须保留，不能把排序比较器用于去重。总超时 `TimeoutSeconds`，异常映射同上。

**可测试性**：实测 SDK 支持注入 `HttpClient`，测试用 MockHttp 返回下方的 SSE 夹具，并断言线上请求（地址、请求头、请求体 JSON）。

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

#### 10.1.1 启动与退出的实现细节（v1.5）

**`Program.Main`（`[STAThread]`，返回退出码）**，严格按以下顺序执行；任何一步失败都在构造 `App` 之前退出：
1. `SingleInstanceGuard.TryAcquireForCurrentUser()`：返回 null → 已有实例在运行 → 直接 `return 0`。这一步必须在创建日志器**之前**执行：Serilog 文件 sink 默认独占日志文件，第二个实例即使创建了日志器也写不进去。第二实例通过 IPC 唤起主实例的功能在 T-037 实现，此前第二实例静默退出。
2. `AppPathsFactory.CreateDefault()`；失败 → `FatalErrorDialog.Show` → `return 1`。
3. 创建 `LoggingLevelSwitch`（初始级别 Information）→ `Log.Logger = IrisLogging.CreateLogger(paths, levelSwitch)`。
4. `AppHostBuilder.Build(args, paths, levelSwitch)` → 从容器中解析 `App` → `app.InitializeComponent()` → `return app.Run()`。
5. 整个 Main 包在 try / catch / finally 中：catch（任何异常）→ 日志器可用时记 Fatal → `FatalErrorDialog.Show` → `return 1`；finally → `host.Dispose()` → `Log.CloseAndFlush()` → `guard.Dispose()`。

**单实例锁（`SingleInstanceGuard`，Platform.Windows）**：命名 Mutex，名称 = `IpcConstants.MutexNamePrefix + 当前用户 SID`。以 `initiallyOwned: false` 创建后调用 `WaitOne(0)`；捕获 `AbandonedMutexException` 视为获取成功（上一个实例崩溃时没有释放锁）。Mutex 有线程亲和性：`Dispose`（内部 `ReleaseMutex`）必须在获取它的线程上调用，也就是 Main 的 finally。

**致命错误对话框（`FatalErrorDialog`，Platform.Windows）**：在 App 与 DI 都不可用的阶段也能使用。直接调用 Win32 `MessageBoxW`，样式 `MB_OK | MB_ICONERROR | MB_TOPMOST | MB_SETFOREGROUND`，保证对话框不会被其他窗口挡住。

**Generic Host**：`Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, DisableDefaults = true, ContentRootPath = AppContext.BaseDirectory })`。必须设置 `DisableDefaults = true`：默认配置会在 Windows 上注册 EventLog 日志提供程序（写入 Windows 事件日志），还会加载 appsettings.json、环境变量等本项目不需要的配置源。日志只接入 Serilog（`Services.AddSerilog()`，使用静态 `Log.Logger`，`dispose: false`）。容器开启 `ValidateOnBuild` 与 `ValidateScopes`，缺少注册时启动即失败，而不是运行到一半才失败。

**主机生命周期（v1.6）**：用一个什么都不做的 `WpfHostLifetime : IHostLifetime`（`WaitForStartAsync` / `StopAsync` 直接返回已完成的任务）替换默认的 `ConsoleLifetime`。`ConsoleLifetime` 是为控制台程序设计的：它会输出 “Application started. Press Ctrl+C to shut down.”、“Hosting environment”、“Content root path” 这类对托盘程序没有意义的日志，还会挂接 Ctrl+C 与进程退出信号。Iris 的生命周期完全由 `AppBootstrapper` 管理。

**`App`**：`ShutdownMode = OnExplicitShutdown`（没有主窗口；关闭任何窗口都不会导致进程退出）。不使用 `StartupUri`；App.xaml 的生成操作改为 `Page`，由 `Program.Main` 手动构造。

**`AppBootstrapper.StartAsync`**（在 `App.OnStartup` 中调用，它是 `async void` 事件处理器，整体包在 try/catch 中）：
1. `ISettingsService.LoadAsync()`：`SettingsCorrupted` → 继续运行，把提示加入通知队列；`StorageFailure` → `FatalErrorDialog`，然后 `Shutdown(1)`。
   T-011：设置成功加载（含损坏恢复）后，在用户入口启用前调用 `ITranslationProviderRegistry.RefreshAsync()`；注册表构造函数不读取凭据或创建 Provider，刷新不发远程请求。
2. `LogLevelController`：按 `GeneralSettings.LogLevel` 设置 `LoggingLevelSwitch`，并订阅 `Changed`，之后随设置变化更新。
3. `ThemeService.Apply(Theme)`，并订阅 `Changed`。
4. 创建托盘图标 → 通知服务开始输出排队中的通知。
5. `host.StartAsync()`。
6. `ShowStartupNotification` 为 true → 显示“Iris 已在后台运行”。
7. 记录一条 Information 日志：从进程启动到托盘就绪的耗时（`Environment.TickCount64 - Process.GetCurrentProcess().StartTime`，或在 Main 开头记录 `Stopwatch.GetTimestamp()`）。

**退出（`AppLifetime.RequestShutdown` → `AppBootstrapper.StopAsync`）**：可以从任意线程调用，切换到 UI 线程执行；**幂等**（第二次调用直接返回）：取消设置订阅 → 释放托盘图标（不释放的话，托盘上会残留一个“幽灵图标”，直到鼠标划过才消失）→ `host.StopAsync`（5 秒超时）→ `Application.Shutdown(0)`。Windows 注销或关机时，WPF 会直接触发 `Exit`，所以 `App.OnExit` 中也要调用同一套幂等清理（同步部分：释放托盘图标、取消订阅）。

**退出日志（v1.6，必须）**：任何退出路径都必须在日志中留下记录，便于排查“程序自己消失”一类的问题：
- `AppBootstrapper.StopAsync` 开始时：Information `Iris 正在退出`。
- `App.OnSessionEnding`（重写）：Information `Windows 会话即将结束（{ReasonSessionEnding}）`。
- `App.OnExit`：Information `Iris 已退出，退出码 {ExitCode}`。这一行必须在 `Log.CloseAndFlush()` 之前写出（`Program.Main` 的 finally 在 `app.Run()` 返回之后才执行，顺序天然满足）。

**托盘图标创建失败（v1.6）**：用户开启“开机自启”后，Iris 可能在资源管理器的通知区域就绪之前启动，这时 `Shell_NotifyIcon` 会失败。**绝不允许静默吞掉这个异常**：否则 Iris 会在后台运行，却没有任何图标可以操作或退出。
- 第一次失败：记 Warning，按 `TrayCreationRetryPolicy` 重试——前 30 次间隔 2 秒，之后每 30 秒一次，不设上限。第 30 次仍然失败时记 Error（只记一次）；最终成功时记 Information（包含尝试次数）。
- 重试用 `DispatcherTimer`（UI 线程）实现；退出时停止计时器。
- `TrayIconController` 在图标**真正创建成功**之后触发 `Created` 事件；`AppBootstrapper` 在这个事件中才调用 `TrayNotificationService.AttachTray`。这样，托盘就绪之前的通知会一直排队，不会因为托盘尚未创建而丢失。
- `ShowNotification` 失败同样要记 Warning，不能静默忽略。

**全局异常（`GlobalExceptionHandler`，在 `App` 构造后、`Run` 之前安装）**：
- `Application.DispatcherUnhandledException`：记 Error；设置 `e.Handled = true`；通知“发生未知错误，详情请查看日志”（`IrisException` 则显示它自己的 Message）。
- `TaskScheduler.UnobservedTaskException`：记 Error；调用 `e.SetObserved()`；不通知。
- `AppDomain.CurrentDomain.UnhandledException`：记 Fatal 并立即 `Log.CloseAndFlush()`（进程随后必然终止，无法阻止）。
- 通知限流：10 秒内最多一条，避免同一个异常在每一帧重复抛出时刷屏（时间使用 `TimeProvider`）。

**通知（`TrayNotificationService : INotificationService`）**：可以从任意线程调用。托盘图标创建之前的通知先进入队列（最多 10 条，超出时丢弃最早的一条并记 Warning），托盘就绪后按顺序输出。

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

### 10.4.1 剪贴板基础服务（T-021A）

IClipboardService 的线程、格式、取消和所有权规则以 CLIPBOARD_SERVICE.md / CCR-008 为准。使用后台 Win32 原生调用，单次尝试中的 HWND/Open/Close 始终同线程、不中途 await，重试延迟在关闭剪贴板之后进行；不依赖 WPF 或 STA/OLE Clipboard。文本 CF_UNICODETEXT、图像 CF_DIBV5，先准备数据，再在最后取消检查后清空/发布。写成功的 HGLOBAL 交给系统，借用的读取句柄不释放。正式原生测试使用独立辅助进程中的新建非交互窗口站，避免改写用户的交互剪贴板。上方选区快照/恢复流程尚未实施，A08 的外部更新保护仍须在 T-022 单独定稿，不能直接把旧的无条件恢复步骤落地。

### 10.4.2 桌面环境查询（T-022A）

完整规则见 DESKTOP_CONTEXT.md / CCR-009。以显示设备表关联显示器快照，排除未接入桌面与镜像伪设备，保留真正使用的远程/虚拟桌面；集合只读，坐标全为物理像素。DPI 用对应显示器上不可见的 PerMonitorV2 窗口调用 GetDpiForWindow，采集后销毁并恢复线程 context，不使用 per-monitor-aware 线程上的 GetDpiForMonitor。前台信息返回实际进程文件名、可空有界标题和短暂 HWND/PID 快照；无法确认来源时返回 null，不把它当作后续输入授权。构造不访问桌面，业务 Handler/定位与选区仍由后续工单接线。

### 10.5 DPI 与坐标

- `app.manifest` 声明 `<dpiAwareness>PerMonitorV2</dpiAwareness>`。
- Platform 与 Core 只使用物理像素。View 层换算：`dip = px / monitor.DpiScale`。窗口定位的正确做法：在 `SourceInitialized` 之后调用 `SetWindowPos`（物理像素），不要用 WPF 的 `Left`/`Top` 跨屏定位，否则在混合 DPI 下会出错。
- **跟随光标定位**（`WindowPlacementHelper`）：窗口出现在锚点右下方 (+16, +16) 物理像素处；超出锚点所在显示器的 WorkArea 时，翻到锚点左侧 / 上方；最后夹紧到 WorkArea 内。

### 10.6 不抢焦点的窗口

`SelectionIconWindow`、`FloatingBallWindow`：在 `SourceInitialized` 中给扩展样式加上 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`，`ShowActivated = false`，并处理 `WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`。翻译窗口：划词、截图触发时 `ShowActivated = true`（允许用户直接复制结果），失焦隐藏遵循 `HideOnDeactivate` 和钉住状态。

### 10.7 屏幕抓取

- `GetDC(NULL)` + `BitBlt(SRCCOPY | CAPTUREBLT)` 抓取虚拟桌面（`SM_XVIRTUALSCREEN` 等），通过 `GetDIBits` 取出 32 位 BGRA 数据。
- 显示器信息复用 T-022A 的 MonitorEnumerator / DESKTOP_CONTEXT.md：设备筛选、物理矩形与每屏 GetDpiForWindow，不重复实现旧 GetDpiForMonitor 路径。
- 已知限制：受 DRM 保护的视频区域会是黑色；HDR 显示器颜色可能偏差（首期接受）。

---

## 11. 错误处理

### 11.1 原则

- 边界（Orchestrator 的每个 Provider 任务、ActionDispatcher、ViewModel 命令、全局异常处理器）负责把异常转换为 `ErrorInfo` 并展示；中间层只抛出不展示。
- `ErrorMessages.GetDefault(ErrorCode)` 提供默认中文文案；Provider 可以附加更具体的 Detail。
- 全局：`AppDomain.UnhandledException`、`Dispatcher.UnhandledException`（设 `Handled = true`，并通知“发生未知错误，详情见日志”）、`TaskScheduler.UnobservedTaskException` 都记为 Fatal/Error 日志。

### 11.2 HTTP → ErrorCode 映射（`HttpErrorMapper`）

**状态码映射**（非 2xx 响应）：

| 条件 | ErrorCode | Retryable |
|---|---|---|
| 3xx（命名客户端显式禁用自动重定向，返回调用方处理） | InvalidResponse | |
| 400 / 422 | BadRequest | |
| 401 | AuthenticationFailed | |
| 402 | QuotaExceeded | |
| 403 | PermissionDenied | |
| 404 | EndpointNotFound | |
| 408 / 504 | Timeout | ✓ |
| 413 | InputTooLong | |
| 429，且错误体的 `error.code` 或 `error.type` 为 `insufficient_quota`（OpenAI 余额不足） | QuotaExceeded | |
| 其他 429 | RateLimited | ✓ |
| 500 / 502 / 503 / 529（Anthropic overloaded） | ProviderUnavailable | ✓ |
| 其他 4xx | BadRequest | |
| 其他 5xx | ProviderUnavailable | ✓ |

**异常映射**：

| 条件 | ErrorCode | Retryable |
|---|---|---|
| `HttpRequestException`（任何 `HttpRequestError`：DNS、连接、TLS、代理隧道……）；读取响应流时的 `IOException`（连接中途被重置） | NetworkUnavailable | ✓ |
| Provider 自己的超时 CTS 触发，而用户的 ct 没有取消（由 Provider 判断后调用 `HttpErrorMapper.Timeout(...)`） | Timeout | ✓ |
| `JsonException`（响应体无法解析） | InvalidResponse | |
| 用户的 ct 已取消 | **不映射**，原样抛出 `OperationCanceledException` | — |
| 其他异常 | **不映射**（属于编程错误，原样抛出，由编排器按 Unknown 处理） | — |

**传输/解析异常的安全边界（T-010A）**：已知 HttpRequestException、IOException、JsonException 转换后的 IrisException 不挂原始 InnerException，也不复制异常的 Message、Data、JsonException.Path。Detail 使用固定说明与安全元数据：异常类型名、HttpRequestError 枚举、HResult、HTTP 状态码、JSON 行号/字节位置（可用时）。不要求每种异常都具有全部元数据。这样不会因为日志输出 ToString 或嵌套异常而绕过脱敏。Provider 中直接 catch JsonException 的分支应复用同一策略；用户取消与未知编程错误的分类顺序不变。

`IrisException.ToErrorInfo()` 只复制 Detail，Detail=null 时返回 null；禁止隐式采用 InnerException.Message。具体变更为已批准的 CCR-001，T-010A 同步到规范契约与落地文件。服务端返回的错误正文仍按下一段提取并脱敏，与传输异常的原始诊断消息分开处理。

**错误信息提取**：读取响应体的前 64 KB（UTF-8），依次尝试以下 JSON 路径，取第一个非空字符串：`error.message` → `message` → `error`（字符串） → `detail` → `error_msg`。错误码同理：`error.code`（字符串或数字） → `error.type`。响应体不是 JSON（例如网关返回的 HTML 页面）时，取原文、把连续空白压缩为一个空格。最后用 `SecretRedactor` 脱敏，并截断到 300 个字符（超出部分以 `…` 结尾）。
- `Message` = `ErrorMessages.GetDefault(code)`；`Detail` = `HTTP {status}: {提取并脱敏后的信息}`；`HttpStatus` = 状态码；`ProviderId` 由调用方传入。

### 11.3 HTTP 客户端与重试（`AddIrisProviders`，`Microsoft.Extensions.Http.Resilience`）

**两个命名客户端的公共配置**：
- 主处理器：`SocketsHttpHandler { Proxy = DynamicWebProxy（单例）, UseProxy = true, AllowAutoRedirect = false, PooledConnectionLifetime = 2 min, ConnectTimeout = 15 s, AutomaticDecompression = All }`。两个命名客户端都禁用自动重定向；不另写手动跟随。原因：默认重定向会保留 x-api-key 等自定义头，.NET 只清除 Authorization，且并非禁止所有跨协议跳转；官方依据见 ARCHITECTURE_AUDIT.md §6。
- `HttpClient.Timeout = Timeout.InfiniteTimeSpan`：超时一律由弹性管道或 Provider 的 CTS 控制。
- 默认请求头 `User-Agent: Iris/{InformationalVersion 中 '+' 之前的部分}`。
- **日志（安全要求）**：调用 `RemoveAllLoggers()` 移除 IHttpClientFactory 默认的日志处理器。改用 HttpLoggingHandler，只在 Debug 记录方法、安全 URL、状态码和耗时，不附原始异常对象。安全 URL 仅含 scheme、host、非默认端口和 path，必须移除 UserInfo、Query、Fragment；GetLeftPart(UriPartial.Path) 本身会保留 UserInfo，不能直接使用。绝对 URI 格式化与服务端错误文本中的 URL 清理复用一个 internal 工具。

**`iris.llm`**：`AddResilienceHandler("iris-llm")`，只包含一个重试策略：
- 最多重试 1 次（共 2 次尝试）。触发条件：`HttpRequestException`，或状态码为 408 / 429 / 5xx。
- 延迟：响应带 `Retry-After`（秒数或 HTTP 日期）→ 使用该值，上限 5 s；否则 1 s（带抖动）。延迟计算写成纯函数 `LlmRetryDelay.Compute(HttpResponseMessage?)`，单独测试。
- **不设** TotalRequestTimeout / AttemptTimeout / 熔断器：流式响应可能持续很久，超时由 Provider 的 CTS 控制。重试只可能发生在响应头返回之前，所以不会重复输出内容。
- 请求内容必须可以重复发送（`StringContent`、`ByteArrayContent`、`JsonContent`）；**禁止**使用基于流的 `StreamContent`，否则无法重试。

**`iris.ocr`**：`AddStandardResilienceHandler`：重试 2 次；单次尝试超时 10 s；总超时 30 s；熔断器使用默认值。Profile 的 `TimeoutSeconds` 由 Provider 的 CTS 实现。

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

**设置编辑入口（T-018 起）**：完整规则见 SETTINGS_EDITING.md / CCR-010。SettingsWindow 按需创建独立 scope，Presenter 单例但不在构造期创建窗口或形成 Dispatcher 依赖环；关闭即释放。常规首批只编辑 Theme、LaunchAtStartup、ShowStartupNotification、LogLevel，按 dirty 字段在最新 Current 上检查冲突并合并。主题预览不写配置，取消恢复最新持久值；JSON 保存与 Run 注册应用状态分开报告。正常退出先等待设置保存/应用退出，再释放其依赖。其它页面和凭据联合提交仍按后续工单接入。

- 设置 DTO 使用 record + `with`：`await settings.UpdateAsync(s => s with { General = s.General with { Theme = AppTheme.Dark } })`。record 的浅复制与 IReadOnly 集合接口本身不提供深度不可变保证，运行时快照按下述规则发布。
- 设置页采用“草稿 → 保存”模式：需要编辑的集合建立独立副本，点击“保存”时一次性 UpdateAsync；热键录入、Provider 测试都针对草稿进行。直接 `Current with { }` 不会复制嵌套集合，不能据此原地编辑它们。
- 涉及 Provider 地址/凭据的编辑必须遵循 PROVIDER_REGISTRY.md §2.2 的整组引用提交协议，不能把两次旧凭据键写入与设置写入当成联合事务；设置编辑协调器在 T-019 实现，本阶段 Registry 只读取。
- 新增字段只要有默认值就不需要迁移（缺失字段取默认值）；重命名、删除、改变字段含义时才需要迁移。
- Iris 运行期间用户手动修改 settings.json 不会被检测到，下次保存时会被覆盖（首期接受）。

**运行时设置快照（T-010B，CCR-002）**

- ISettingsService 发布的 Current，以及其 Changed 事件中的 OldSettings/NewSettings，均为深度不可变快照：所有可达可变集合先复制，再以不可修改的接口/容器发布；不得持有调用方仍可改写的底层集合。直接构造的 AppSettings DTO 可以包含普通 List/Dictionary，它不是自动冻结的运行时对象。
- 增加 Infrastructure 内部的纯快照构建器。它只负责复制和只读封装，不执行规范化、I/O、事件或日志；正常加载与更新先按原规则规范化，再捕获快照。不可变叶节点（字符串、仅含不可变值的 record）允许共享；包含 ExtraHeaders 等集合的嵌套对象必须隔离。
- 发布覆盖五处：服务构造时的默认 Current、文件缺失后的首次加载、正常加载（含挽救/迁移/较新版本的原有路径）、损坏恢复的默认值、UpdateAsync 成功值。损坏恢复/版本处理/错误类型的现有规则不在本单重写。
- 更新顺序：mutate → Normalize → 捕获独立快照 → 序列化该快照 → 比较原 JSON → 原子写盘 → 发布同一个快照 → Changed(old, new)。序列化、磁盘数据、Current 和事件 NewSettings 必须描述同一份值；不能在 await 写盘之后才复制集合。调用方应在 UpdateAsync 完成前保持提交草稿稳定，不并发修改它。
- Current getter 只读取已发布引用，同一版本重复读取返回同一实例，不在读取时克隆。无操作更新保持原 Current 引用，不写盘、不发事件；写盘失败或取消的原有回滚规则不变。
- 已发布版本长期持有仍稳定；后续更新、原输入集合修改、另一个事件订阅者都不能改写旧快照。不改变本阶段 Changed 在写锁内同步触发的约定，事件发布协调在 A03/A04 中另行设计。
- 必须处理以下全部集合路径：Hotkeys；Translation.Providers；Translation.CustomPrompts；Translation.Providers[*].OpenAIOptions.ExtraHeaders（只要存在，无论当前 Kind）；Ocr.Providers；Selection.ReadOrder；Selection.ExcludedProcesses；Selection.ClipboardUnsafeProcesses。可空 ExtraHeaders 的 null 保持 null，列表顺序与键值保持原样，不引入排序或大小写折叠。
- 推荐 BCL 的 Array.AsReadOnly（先复制数组）与 ReadOnlyDictionary（先复制字典）；不能只把调用方字典包装起来。保持字典现有枚举顺序，ExtraHeaders 使用 Ordinal 键规则。无需新增包、JSON 转换器或 schemaVersion。架构师已用真实 IrisJson 验证这种封装可保持 JSON 内容与往返值不变，具体证据见 T-010B 工单。

#### 12.2.1 加载管道（`JsonSettingsService.LoadAsync`）

```
读取文件（UTF-8，兼容 BOM）
 → JsonNode.Parse（允许注释与尾逗号）+ 立即完整展开（递归遍历全部 JsonObject / JsonArray）
       解析或展开时抛出 JsonException / ArgumentException，或根节点不是对象 → 损坏处理（见契约）
       说明：JsonObject 是延迟展开的，重复属性名（包括完全相同的重名，以及开启大小写不敏感后仅大小写不同的重名）
       在 Parse 时不报错，要到首次访问属性时才抛 ArgumentException，所以必须在同一个 try 块中完整展开
 → 读取 schemaVersion                            缺失 → 视为当前版本；非整数 → 视为当前版本并记一条修复
     > 当前版本：记 Warning，复制原文件为 settings.v{n}.json（已存在则跳过），不迁移，尽力加载
     < 当前版本：迁移链完整 → 复制原文件为 settings.v{n}.bak.json，按 FromVersion 依次执行 ISettingsMigration
            迁移链不完整（例如 0、负数等从未存在过的版本，或缺少某一步迁移）→ 记 Warning 与一条修复，
            跳过迁移，按当前版本尽力加载（绝不因此抛出异常）
 → 挽救（Salvage，JsonNode 层）
 → 反序列化为 AppSettings（IrisJson.Options）   仍失败 → 损坏处理
 → 规范化（Normalize，强类型层）
 → 若发生了迁移 / 挽救 / 规范化修复 → 原子回写，并以 Warning 记录修复清单（只记字段路径与新旧值，不记整份 JSON）
```

**LoadAsync 允许抛出的异常只有**：`IrisException(SettingsCorrupted)`（此时已加载，Current 为默认值）、`IrisException(StorageFailure)`（此时未加载，可以重试）、`OperationCanceledException`，以及编程错误导致的 `InvalidOperationException`（重复调用 LoadAsync）。**任何其他异常都视为缺陷。**

**挽救规则（Salvage）**：目的是让单个坏字段只影响它自己，而不是把整份设置重置为默认值（`required` 成员缺失、枚举字符串非法都会让整份反序列化失败）。
0. 版本处理完成后，无条件把 `schemaVersion` 写为当前版本号（数字）——否则 `"schemaVersion": "abc"` 会让整份反序列化失败。
1. `translation.providers`、`translation.customPrompts`、`ocr.providers`：不是数组 → 移除该属性；是数组 → 逐个元素尝试单独反序列化为对应类型，失败的元素移除。
2. `hotkeys`：不是对象 → 移除该属性；是对象 → 逐个属性检查：键名须能用 `Enum.TryParse<IrisAction>(ignoreCase: true)` 解析且 `Enum.IsDefined` 为真（System.Text.Json 本身接受小写与数字键，这里统一**改写为规范名称**，如 `"3"` → `"ScreenshotTranslate"`；改写后与已有键重复则丢弃后者），值为 null 或能反序列化为 `HotkeyBinding`，否则移除该属性。
3. 顶层各节（`general`、`translation`、`ocr`、`selection`、`screenshot`、`floatingBall`、`translateWindow`、`history`、`network`）：逐节尝试单独反序列化为对应类型，失败的整节移除（使用默认值）。
4. 顶层未知属性保留原样（反序列化时忽略）。

**规范化规则（Normalize）**：保证 `Current` 永远满足下列不变量。数值越界一律**夹到边界值**（clamp）。

| # | 范围 | 规则 |
|---|---|---|
| N1 | 顶层 | 任何为 null 的节 / `Hotkeys` 字典 → 默认值；`SchemaVersion` → 当前版本 |
| N2 | Hotkeys | 值为 null 的条目：动作有默认绑定 → 替换为默认绑定；没有默认绑定 → **移除**；两种情况都要记修复。`HotkeyDefaults` 中有、字典中缺失的动作 → 补默认绑定；`Key` 为空白的条目 → 有默认绑定则替换为默认绑定，否则移除。注意：JSON 中只要出现 `hotkeys` 对象，反序列化结果就**只包含**文件中列出的条目（不会与默认值合并），因此本规则是必需的 |
| N3 | Translation | null 列表 → 空列表；Profile：`Id` 空白 → 移除，`Id` 重复 → 保留第一个；`DisplayName` 空白 → 用 `Id`；`TimeoutSeconds` ∈ [5, 600]；`MaxOutputTokens` 为 null 或 ≥ 1（否则置 null）；`Temperature` 为 null 或 ∈ [0, 2]；OpenAICompatible 且 `BaseUrl` 空白 → `Enabled = false`；按 Kind 补齐 null 的 `OpenAIOptions` / `AnthropicOptions`。`MaxConcurrentProviders` ∈ [1, 8]；`MaxInputChars` ∈ [100, 100000]；`CacheCapacity` ∈ [0, 10000]；`PrimaryTargetLanguage` / `SecondaryTargetLanguage` 为空白或 `auto` → 各自默认值；`DefaultSourceLanguage` 空白 → `auto`；`CustomPrompts`：`Id` 空白、重复、或与内置模板 Id 相同 → 移除；`DefaultPromptTemplateId` 不存在于内置 + 自定义模板中 → 内置默认 |
| N4 | Ocr | null 列表 → 默认列表；缺失的内置 Profile（`ocr-baidu`、`ocr-windows`）→ 按默认值追加；`Id` 空白 / 重复规则同 N3；`TimeoutSeconds` ∈ [5, 120]；按 Kind 补齐 null 的 `BaiduOptions` / `WindowsOptions`；`ActiveProviderId` 不存在 → `ocr-baidu`；`FallbackProviderId` 不存在或等于 Active → null；`LanguageHint` 空白 → `auto` |
| N5 | Selection | `ReadOrder` 为 null 或空 → 默认顺序，并去除重复项；`ClipboardTimeoutMs` ∈ [100, 2000]；`MinDragDistancePx` ∈ [2, 50]；`IconAutoHideMs` ∈ [500, 10000]；`ExcludedProcesses` 为 null → 空列表；`ClipboardUnsafeProcesses` 为 null → 默认列表 |
| N6 | Screenshot | `MaskOpacity` ∈ [0, 0.9] |
| N7 | FloatingBall | `Size` ∈ [24, 96]；`Opacity` ∈ [0.2, 1.0] |
| N8 | TranslateWindow | `Width` ∈ [280, 1200]；`FontSize` ∈ [10, 32] |
| N9 | History | `MaxEntries` ∈ [100, 100000] |
| N10 | Network | `ProxyMode = Custom` 但 `CustomProxyUrl` 不是 scheme 为 http / https / socks5 的绝对 URI → `ProxyMode = System` |
| N11 | General | `UiLanguage` 不属于 {zh-CN, en-US}（不区分大小写）→ zh-CN，属于时统一为规范大小写；`LogLevel` 不属于 {Verbose, Debug, Information, Warning, Error}（不区分大小写）→ Information，属于时统一为规范大小写 |
| N12 | 枚举 | System.Text.Json 接受整数形式的枚举值，所以未定义的数值（如 `"theme": 5`）能通过反序列化，必须在这里处理。普通枚举必须满足 `Enum.IsDefined`；`[Flags]` 枚举只保留已定义的位。需要检查的字段（完整清单）：`General.Theme`、`Hotkeys` 的键（`IrisAction`，未定义则移除该条目）、`HotkeyBinding.Modifiers`（Flags）、`TranslationProviderProfile.Kind`（未定义则**移除该 Profile**）、`OpenAICompatibleOptions.MaxTokensField`、`Translation.AutoCopy`、`Translation.SelectionLineBreaks`、`OcrProviderProfile.Kind`（未定义则移除该 Profile；如果它是内置 Profile，随后按 N4 补回默认配置）、`BaiduOcrOptions.Mode`、`Ocr.TextLayout`、`Selection.MouseMode`、`Selection.ReadOrder` 的元素（未定义则移除该元素，然后再应用 N5）、`FloatingBall.ClickAction`、`TranslateWindow.Placement`、`Network.ProxyMode`。未单独说明的字段，未定义值 → 该字段在默认实例中的值 |
| N13 | 空值 | 用户手写的 `null` 会绕过非可空引用类型的约束（`RespectNullableAnnotations` 未开启），所以规范化后，AppSettings 对象图中**所有非可空引用类型的成员都不得为 null**。规则：`TranslationProviderProfile.Model` 为 null → 空字符串，并设 `Enabled = false`；`PromptTemplate.Name` 为空白 → 用 `Id`；`PromptTemplate.System` 或 `User` 为 null 或空白 → 移除该模板；其余带默认值的字符串（`OpenAICompatibleOptions.ChatCompletionsPath`、`BaiduOcrOptions.BaseUrl` 等）为 null 或空白 → 该属性在默认实例中的值；为 null 的嵌套对象与集合 → 默认实例或默认集合（N1、N3–N5 已覆盖的部分按原规则执行）；集合中为 null 的字符串元素（如 `ExcludedProcesses: [null]`）→ 移除；字典中值为 null 的条目（如 `ExtraHeaders: {"X-A": null}`）→ 移除。总之，规范化后 AppSettings 对象图中的任何集合都不得包含 null 元素或 null 值 |
| N14 | 引用 | `Ocr.ActiveProviderId`、`Ocr.FallbackProviderId`、`Translation.DefaultPromptTemplateId` 采用不区分大小写的方式匹配，匹配成功后**改写为被匹配对象的精确 Id**（下游的注册表按序数比较查找）。N4 中补回的内置 OCR Profile 必须**直接复制** `new OcrSettings().Providers` 中的对应项，不得另行硬编码名称或选项 |

浮点数若为 NaN / Infinity，按“越界”处理，夹到下界。`UpdateAsync` 同样先规范化再写入，因此 UI 层传入越界值不会破坏不变量（但 UI 仍应在输入控件上限制范围）。

#### 12.2.2 写入

- 原子写入：序列化（`IrisJson.Options`，UTF-8 无 BOM，末尾换行）→ 以 `FileOptions.WriteThrough` 写入 `settings.json.tmp` 并 `Flush(true)` → 目标文件存在时复制为 `settings.json.bak` → `File.Move(tmp, settings.json, overwrite: true)`。
- 共享冲突（HResult 0x80070020 / 0x80070021）与 `UnauthorizedAccessException`（杀毒软件或索引服务短暂占用文件）：每一步最多重试 5 次，间隔 50 ms。最终失败 → 尽力删除 tmp，抛 `IrisException(StorageFailure, detail: 路径)`。
- 损坏文件的备份使用移动（而不是复制），这样随后写入默认值时不会用损坏内容覆盖上一次正常的 `settings.json.bak`。备份文件名冲突时依次追加 `-1`、`-2`……
- 加载阶段所有的辅助文件操作——损坏备份的移动、`settings.v{n}.bak.json` / `settings.v{n}.json` 的复制——都使用与原子写入相同的重试策略（`AtomicFile.MoveAsync` / `AtomicFile.CopyAsync`）。最终失败 → `IrisException(StorageFailure)`，服务保持**未加载**状态。

### 12.3 密钥

**存储格式（secrets.dat）**：UTF-8 JSON（无 BOM，末尾换行），明文部分只包含结构信息，所有密钥值都是 DPAPI 密文：

```json
{
  "format": "iris-secrets",
  "version": 1,
  "entries": {
    "provider/tp-8c21e0a4b5f6/api-key": "<Base64(DPAPI 密文)>"
  }
}
```

- 加密：`ProtectedData.Protect(UTF8(value), entropy: "Iris.Secrets.v1"u8, DataProtectionScope.CurrentUser)`（NuGet `System.Security.Cryptography.ProtectedData`）。同一明文每次加密得到的密文都不同（DPAPI 自带随机盐），因此不能通过比较密文判断值是否变化。
- 键名只保存明文（用于按 Provider 清理），值永远不以明文落盘。
- **不使用内存缓存明文**：`Get` 每次从内存中的密文字典取出后即时解密，明文只存在于返回的字符串中。

**加载（在构造函数中同步完成；文件很小，可以接受）**：
1. 文件不存在 → 空字典，不创建文件（首次 `Set` 时才创建）。
2. 读取失败（共享冲突按 `AtomicFile` 的策略重试后仍失败）→ 空字典，进入**只读降级模式**，记 Error 日志。降级模式下 `Get` 返回 null；`Set` / `Remove` 先尝试重新加载一次——成功则退出降级模式并照常执行，失败则抛 `IrisException(SecretUnavailable)`。目的：绝不用空字典覆盖掉仍然有效的密钥文件。
3. JSON 语法错误、重复属性名（用 `JsonDocument.Parse` 并设置 `JsonDocumentOptions.AllowDuplicateProperties = false`，重复时在解析阶段直接抛 `JsonException`）、`format` / `version` 不匹配、`entries` 不是对象 → 把原文件**移动**为 `secrets.corrupt-{yyyyMMddHHmmss}.dat`（重名追加 `-1`……）后按空字典继续（可写）；记 Error 日志。移动失败 → 进入只读降级模式。
4. 单个条目的值不是字符串或不是合法 Base64 → 只丢弃该条目，记 Warning（只记键名）。
5. 加载阶段**不解密**任何条目。

**Get(key)**：键不存在 → null。解密失败（`CryptographicException`：换了机器或用户、文件被篡改）→ 返回 null，并且**每个键只记一次** Warning（只记键名）；不修改文件（用户重新填写时会覆盖该条目）。

**Set(key, value)**：`value` 为 null → `ArgumentNullException`；为空字符串 → 等价于 `Remove(key)`。加密 → 在锁内更新字典并同步原子写入 → 成功后才更新内存并触发 `Changed`。写入失败 → 抛 `IrisException(StorageFailure)`，内存字典保持不变，不触发事件。
**Remove(key)**：键不存在 → 返回 false，不写盘、不触发事件；存在 → 原子写入后删除并触发 `Changed`，返回 true。
**线程安全**：所有读写经由同一把锁串行化；`Changed` 在释放锁之后触发（与设置服务不同：密钥变化的订阅方——Provider 注册表——会在处理器中调用 `Get`，锁外触发可以避免重入问题），处理器异常逐个捕获并记 Error。
**同步写盘**：`ISecretStore` 是同步接口，内部使用 `AtomicFile` 的同步方法 `WriteAllBytes` / `Move`（T-004 新增）。写入**不保留** `.bak`：`Remove` 之后，密钥不应再以任何形式留在磁盘上。条目按键名（序数）排序写出，保证输出确定。调用方（设置页的“保存”）应在后台线程调用 `Set` / `Remove`。

- 删除/更换 Provider 凭据时按 PROVIDER_REGISTRY.md 的“发布设置 → 刷新屏障 → 无引用后清理”顺序执行（T-019）；原始 ISecretStore.Set/Remove 的语义不变。CredentialSetId 为 null 才兼容旧键，显式坏引用不得回退旧键。
- 日志中只允许出现键名，禁止出现值、密文和 Base64 串。

### 12.4 代理（`DynamicWebProxy : IWebProxy`）

- 单例，每次调用都读取 `ISettingsService.Current.Network`，因此代理设置修改后，下一个请求立即生效（SocketsHttpHandler 按代理地址区分连接池）。
- `GetProxy(uri)`：先判断 `IsBypassed(uri)`，为 true 时返回 null（直连）。否则：System → `HttpClient.DefaultProxy.GetProxy(uri)`；None → null；Custom → `new Uri(CustomProxyUrl)`（http / https / socks5 均由 SocketsHttpHandler 原生支持；地址无法解析时返回 null，并且只记一次 Warning，日志中不写地址本身）。
- `IsBypassed(uri)`：None → true；`BypassProxyOnLocal` 为 true 且目标属于**本地地址**时 → true（System 与 Custom 模式都适用）；System 模式下再调用 `HttpClient.DefaultProxy.IsBypassed(uri)`（尊重系统的例外列表）；其余 → false。
- **本地地址**的定义（判断前先去掉主机名末尾的 `.`，例如 `localhost.`；IPv4 映射的 IPv6 地址（`::ffff:10.1.2.3`，`IPAddress.IsIPv4MappedToIPv6`）先用 `MapToIPv4()` 转换再判断——`IPNetwork.Contains` 不会跨地址族匹配）：`localhost`；不含点号的单标签主机名（如 `intranet`）；以 `.local` 结尾的主机名；IP 地址属于 127.0.0.0/8、10.0.0.0/8、172.16.0.0/12、192.168.0.0/16、169.254.0.0/16、::1、fc00::/7、fe80::/10。
- `Credentials`：System 模式 → `HttpClient.DefaultProxy.Credentials ?? CredentialCache.DefaultCredentials`（兼容需要 Windows 集成认证的企业代理）；其他模式 → null（带账号密码的自定义代理在 T-036 实现）。

### 12.5 日志与隐私

- 默认级别 Information。日志中**绝不**记录 API Key、Authorization 头、完整原文或译文。原文 / 译文统一通过 `LogText` 输出：Information 及以上级别只用 `LogText.Fingerprint(text)`（长度 + SHA-256 前 8 位）；Debug 级别可用 `LogText.Preview(text)`（再加前 32 个字符）。
- 每个翻译会话以 `RequestId` 作为日志作用域（`BeginScope`），便于排查问题。
- **URL 与查询字符串**：日志和异常详情中的绝对 URL 只保留 scheme、host、端口、path，完整移除 UserInfo、Query、Fragment。IPv6 主机、非默认端口、已编码路径要正确保留；URL 中用 `***@` 代替账号密码也不是最终格式。null/相对 URI 等不符合条件的输入返回固定占位符，不回显原输入。
- **服务端错误信息脱敏（`SecretRedactor`）**：先按上一条清理绝对 URL，完整去除其中的敏感部分；再将显式传入的所有非空已知凭据替换为 `***`（最长优先、Ordinal 比较，不对替换后的标记再次执行已知值替换），不再设置长度 ≥ 8 的门槛；继续识别常见 sk-/Bearer/凭据参数形态。先清理 URL 可避免短凭据替换破坏 scheme 后漏掉查询字符串。未知凭据的形态识别保留现有保守规则。该变化会有意改变旧 URL 脱敏测试的期望值。随后再按 §11.2 截断输出。

---

## 13. 依赖注入约定

- 每层暴露一个扩展方法：`AddIrisCore()`、`AddIrisProviders()`、`AddIrisInfrastructure()`、`AddIrisPlatformWindows()`、`AddIrisApp()`，由 `AppHostBuilder` 按此顺序调用。
- 生命周期：服务、Registry、Presenter、Controller 均为 **Singleton**；`IActionHandler` 为 Singleton，通过 `IEnumerable<IActionHandler>` 注入 Dispatcher；Provider 实例由 Registry 管理（不注册到容器）；`TranslateWindow` 由单例 Presenter 按需持有一个实例（普通关闭 = 隐藏，应用退出等待工作后释放；不得启动时创建）；`SettingsWindow`、`HistoryWindow`、`OcrResultWindow` 同一时间只有一个实例（由 Presenter 管理，关闭即释放）。
- Provider 可以有多代同时存活，退休后由最后一个快照使用者触发清理。IProviderConfigurationObserver 的翻译适配器必须转发到唯一的 ITranslationProviderRegistry，不创建第二份注册表；详细并发/Dispose 约定见 PROVIDER_REGISTRY.md。
- ViewModel 通过构造函数注入，View 在构造函数中接收 ViewModel 并设为 `DataContext`。

---

## 14. UI 窗口清单

| 窗口 | 要点 |
|---|---|
| `TranslateWindow` | 无边框 Fluent 窗口，可拖动。顶部栏：钉住 / 设置 / 关闭。原文输入框（多行，最大高度 200 DIP 后滚动；EnterToTranslate=true 时 Enter 翻译、Shift+Enter 换行，false 时 Ctrl+Enter 翻译、Enter 换行；IME 确认不提交）。语言栏：源语言（含“自动检测”，检测结果以灰字显示）⇄ 目标语言（含“自动”）。翻译/停止按钮、结果卡片列表（服务名 · 模型 · 耗时；可选只读文本；复制 / 重试；错误/截断状态）。Esc 隐藏。完整生命周期与预算见 TRANSLATION_WINDOW.md。 |
| `ScreenshotOverlayWindow` | 每个显示器一个；冻结背景 + 遮罩 + 选区；尺寸标签；放大镜；Esc / 右键取消。 |
| `OcrResultWindow` | 左侧图片预览（可缩放），右侧可编辑文本；按钮：复制、翻译（→ TranslateText）、换一个服务重新识别（下拉选择 OCR Provider）。 |
| `FloatingBallWindow` | 见 §6.7。 |
| `SelectionIconWindow` | 24×24 DIP 图标，不激活，自动隐藏。 |
| `SettingsWindow` | WPF-UI `NavigationView`，页面：常规、翻译服务、OCR、快捷键、划词、截图、悬浮球、网络、历史、关于。 |
| `HistoryWindow` | 搜索框（防抖 300 ms）、仅看收藏、分页列表；每条可展开查看各服务结果；收藏 / 删除 / 重新翻译 / 复制。 |
| 托盘 | 左键单击 = 输入翻译；右键菜单见 §5.5。 |

**翻译服务设置页**：列表（启用开关、拖拽排序、编辑、删除）；“添加”按钮 → 选择预设（`TranslationProviderPresets`）→ 编辑表单：显示名、BaseUrl、API Key（PasswordBox，保存时写入 SecretStore）、模型（可编辑下拉框 + “获取模型列表”按钮）、超时、最大输出 tokens、温度（Anthropic 隐藏此项）、Prompt 模板、高级选项（OpenAI / Anthropic 各自的 Options）；“测试连接”按钮显示延迟和测试译文，或错误信息。

T-019B 的具体接线以 PROVIDER_SETTINGS_UI.md 为准：API Key 经 Core 协调器落地，UI 不直接写 SecretStore；常规与 Profile 变更一次提交。待应用回执在 App 单例中跨窗口保留，与自启状态独立；未完成回执及预留名额合计上限 32，在 Core 提交前检查容量。所有异步操作遵循 T-018 的真实退出和会话身份规则。

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
