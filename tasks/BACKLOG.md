# Iris 任务清单（BACKLOG）

> 规则：严格按编号顺序开发（同一里程碑内标注“可并行”的任务除外）。每个任务都是可以独立验收的最小交付单元。
> 架构师每次把一个任务展开成完整工单放进 `tasks/current_task.md`；完成后归档到 `tasks/done/T-xxx.md`，并附上完成报告。
> 估时单位：人日（d）。状态：☐ 待办 · ▶ 进行中 · ☑ 完成。
> 引用：`ARCH §x` = `specs/ARCHITECTURE.md` 的对应章节；`CONTRACTS` = `specs/CONTRACTS.cs`。

## 里程碑总览

| 里程碑 | 目标 | 任务 | 可交付结果 |
|---|---|---|---|
| M0 基础设施 | 工程骨架、设置、密钥、托盘常驻 | T-001 ~ T-005 | 可以启动的托盘程序，设置可以读写 |
| M1 翻译核心 | 语言、Prompt、Provider、编排器 | T-006 ~ T-013（含 T-010A/B 加固） | 在单元测试中跑通完整的多服务流式翻译 |
| M2 输入翻译 | 分发器、翻译窗口、热键 | T-014 ~ T-017 | **首个可用版本**：热键打开窗口，输入文本得到翻译 |
| M3 设置界面 | 设置窗口与各配置页 | T-018 ~ T-020 | 用户可以在 UI 中配置服务与快捷键 |
| M4 划词翻译 | 剪贴板、选区读取、鼠标划词 | T-021 ~ T-024 | 选中文本按热键即可翻译；可选划词图标 |
| M5 截图与 OCR | 抓屏、覆盖层、OCR 服务 | T-025 ~ T-032 | 截图翻译、截图 OCR、静默 OCR |
| M6 悬浮球 | 悬浮球 | T-033 | 悬浮球全部交互 |
| M7 完善与发布 | 历史、代理、IPC、国际化、打包 | T-034 ~ T-040 | 可发布的安装包 |

---

## M0 基础设施

| ID | 状态 | 任务 | 依赖 | 估时 |
|---|---|---|---|---|
| T-001 | ☑ | 工程骨架与核心契约落地 | — | 1d |
| T-002 | ☑ | AppPaths + 日志基础设施 + ErrorMessages + LogText | T-001 | 0.75d |
| T-003 | ☑ | JsonSettingsService（加载 / 挽救 / 规范化 / 原子保存 / 迁移 / 损坏恢复）+ T-002 整改（含返工 R1） | T-002 | 1.5d + 0.5d |
| T-004 | ☑ | DpapiSecretStore + AtomicFile 同步方法 + AddIrisPlatformWindows + T-003-R1 遗留 | T-003 | 1d |
| T-005 | ☑ | WPF 应用外壳：Generic Host、单实例、托盘、主题、全局异常 + T-002/T-004 遗留（含返工 R1） | T-003, T-004 | 2d + 0.5d |

**T-001 工程骨架与核心契约落地** — ☑ 已完成，见 `tasks/done/T-001.md`（2026-09-25 评审通过）。

**T-002 AppPaths + 日志** — ☑ 已完成，见 `tasks/done/T-002.md`（2026-09-25 评审通过）。
- 交付：`Iris.Infrastructure/Storage/AppPaths.cs`（实现 `IAppPaths`，含便携模式判断，ARCH §12.1）；`Iris.App` 中配置 Serilog（文件 sink，按天滚动，保留 7 天，单文件 ≤ 10 MB；最低级别取自 `GeneralSettings.LogLevel`，启动时先用 Information）；`Iris.Core/Errors/ErrorMessages.cs`（每个 ErrorCode 对应一条默认中文文案）。
- 验收：测试覆盖便携 / 非便携两种路径，且目录会被自动创建；`ErrorMessages` 覆盖全部枚举值（用反射遍历测试）。

**T-003 JsonSettingsService** — ☑ 已完成（首次提交 1be472c 不通过 → 返工提交 5824211 复审通过，2026-09-25），见 `tasks/done/T-003.md`。
- 交付：`Iris.Infrastructure/Settings/JsonSettingsService.cs`、`SettingsMigrationRunner.cs`，严格按 CONTRACTS 中 `ISettingsService` 注释描述的语义实现。
- 验收（全部为自动化测试，在临时目录中进行）：首次加载会生成默认文件；读写往返后内容一致；损坏的 JSON 会被备份并抛出 `SettingsCorrupted`，之后 Current 可用；写入是原子的（写入过程中模拟异常，原文件不被破坏）；并发调用 UpdateAsync 按顺序串行执行；mutate 返回同一实例时不写盘；迁移链按顺序执行（用一个测试专用的迁移验证）；`Changed` 事件携带新旧值。

**T-004 DpapiSecretStore** — ☑ 已完成（提交 1ec7066，2026-09-25 评审通过），见 `tasks/done/T-004.md`。
- 交付：`Iris.Platform.Windows/Security/DpapiSecretStore.cs`（ARCH §12.3），原子写入，线程安全，触发 `Changed` 事件。
- 验收：Set / Get / Remove 往返正确；文件中找不到明文（测试直接读文件字节进行断言）；文件损坏时 Get 返回 null，不抛异常；`ProviderCredentials.ToString()` 不泄露密钥。

**T-005 WPF 应用外壳** — ☑ 已完成（首次提交 92fa706 不通过 → 返工提交 b5543e7 复审通过，2026-09-26），见 `tasks/done/T-005.md`。**M0 里程碑完成。**
- 交付：`Program.cs`（自定义 Main + `SingleInstanceGuard`）、`AppHostBuilder`、`AppBootstrapper`（本任务只实现 ARCH §10.1 的步骤 1、2、3、8；其余步骤留下 TODO 标记）、`TrayIconController`（菜单暂时只有“设置（占位）/ 退出”）、`AppLifetime`、`ThemeService`（跟随系统 / 浅色 / 深色）、`GlobalExceptionHandler`、`TrayNotificationService`（实现 `INotificationService`）、`app.manifest`（PerMonitorV2）。
- **评审遗留（T-002，必须在本任务中处理）**：
  1. 删除 `App.xaml` 的 `StartupUri`，所有窗口一律显式创建；启动失败路径（数据目录不可用等）必须在构造任何窗口之前退出。（T-002 评审实测：`Shutdown(1)` 之后 WPF 仍按 StartupUri 构造了 MainWindow。）
  2. `IrisLogging.CreateLogger` 不得修改调用方传入的 `LoggingLevelSwitch`；初始级别由调用方根据 `GeneralSettings.LogLevel` 设置，并在 `ISettingsService.Changed` 时更新。
  3. 启动与退出日志改用带 SourceContext 的日志器（`ILogger<T>` 或 `Log.ForContext<T>()`），不再输出空的 `()`。
  4. `Program.Main` 在日志器可用之前兜底捕获所有异常（不只是 `IrisException`），弹窗提示后以退出码 1 结束。
  5. 启动错误弹窗需要确保可见（没有其他窗口时对话框可能出现在其他应用下方）：以置顶方式显示。
- 验收：启动后只显示托盘图标（没有主窗口、不出现在任务栏）；再次启动时第二个进程立即退出；退出菜单可以干净地退出（日志中能看到关闭流程）；人为抛出一个未处理异常会被记录到日志并弹出通知，进程不崩溃；空闲内存 ≤ 120 MB。

---

## M1 翻译核心（全部在 Core / Providers 中完成，不涉及 UI）

| ID | 状态 | 任务 | 依赖 | 估时 |
|---|---|---|---|---|
| T-006 | ☑ | LanguageCatalog + ScriptLanguageDetector + TargetLanguageResolver | T-005 | 1d |
| T-007 | ☑ | TextPreprocessor + SoftLineBreakMerger + PromptRenderer + PromptTemplateProvider + T-006 遗留 | T-006 | 1d |
| T-008 | ☑ | Providers/Http：SseParser、HttpErrorMapper、SecretRedactor、DynamicWebProxy、命名 HttpClient 注册 + T-007 遗留 | T-007 | 1.5d |
| T-009 | ☑ | OpenAI 兼容翻译 Provider | T-007, T-008 | 1.5d |
| T-010 | ☑ | Anthropic 翻译 Provider（官方 SDK）+ 公共调用骨架（含 R1/R2） | T-007, T-008 | 1.25d + 0.75d + 0.25d |
| T-010A | ☑ | 远程异常与 HTTP 凭据边界加固（架构审查 A01，含 R1） | T-010 | 0.75d + 0.5d |
| T-010B | ☑ | 深度不可变设置快照（架构审查 A02） | T-010A | 0.75d |
| T-011 | ▶ | TranslationProviderRegistry、凭据解析与快照租约（T-011-R2 收尾） | T-004, T-010B | 1.5d |
| T-012 | ☐ | LruTranslationCache（执行版本与字节预算纳入工单） | T-011 | 待工单细化 |
| T-013 | ☐ | TranslationOrchestrator | T-011, T-012 | 1.5d |

> T-006/T-007 与 T-008、T-009 与 T-010 的历史并行安排保留。2026-09-28 架构审查后，后续顺序为 T-010A → T-010B → T-011 → T-012 → T-013；保留原编号，以字母后缀插入加固项。T-012 需先明确执行快照/版本和缓存失效规则，不再视为可随时独立插入。

**T-006** — ☑ 已完成（提交 fe508b5，2026-09-27 评审通过，附报告问题记录），见 `tasks/done/T-006.md`。

**T-007** — ☑ 已完成（提交 78009a9，2026-09-27 评审通过），见 `tasks/done/T-007.md`。

**T-008** — ☑ 已完成（提交 718c23b，2026-09-28 评审通过，两项小遗留转入 T-009），见 `tasks/done/T-008.md`。

**T-009** — ☑ 已完成（提交 0bb2d2d，2026-09-28 评审通过，报告需补充说明，小遗留转入 T-010），见 `tasks/done/T-009.md`。

**T-010** — ☑ R2 于 2026-09-28 复审通过，已独立提交为 `81c0ddd3380cda672a9c45bc589fad25b0117dd5`；架构师已逐个验证提交中的四个文件字节与验收 SHA-256 一致。独立构建零警告、779 个测试通过，46 个定向用例连续五轮通过；独立 SSE 差分及普通/beta 心跳探针通过。完整证据见 `tasks/done/T-010.md` 及 T-010A 评审记录。

**T-010A** — ☑ 提交 `587484a4487188031d5d637141b4c430c9c18af6` 于 2026-09-28 复审通过，CCR-001 已结案。独立构建零警告、824 个测试通过；原泄漏探针不再回显假凭据，64 KiB 短凭据样本本次约 3.05 ms，原二次扫描已移除；R2 基线表已更正。证据见 `tasks/done/T-010A.md` 末尾。

**T-010B** — ☑ 提交 `21f6224cd66ec0e07f9f127f388a7b9410336986` 于 2026-09-28 复审通过，CCR-002 结案。独立构建零警告、840 个测试通过；别名隔离、无操作更新、失败回滚、旧版本及未来版本/损坏恢复探针通过。反射覆盖测试的 C10B-1 小遗留转入 T-011，不改已验收实现。

**T-011** — ▶ 提交 7b01971 于 2026-09-29 完成 R1 复审：独立构建零警告、915 项测试通过，最终隔离探针 27 项检查通过，原 P1 修复已接受。仍有 P2：通知/退休清理占用重建信号量，且五项关键测试不能检出对应回归。签发 tasks/current_task.md 的 T-011-R2，仅收尾锁边界、测试和报告；CCR-003 待复审。证据见 tasks/done/T-011.md 末尾。

**T-012** 验收：容量淘汰遵循 LRU；多线程并发读写安全（Parallel.For 压测）；Clear 生效。

**T-013** 交付 ARCH §7.1 + CONTRACTS 中 `ITranslationOrchestrator` 注释描述的全部语义。验收：事件顺序保证；多个 Provider 输出交错时仍能正确归属；一个 Provider 失败不影响其他 Provider；并发度上限生效（用受控的假 Provider 断言最大并发数）；缓存命中时直接返回 FromCache；BypassCache 生效；截断结果不写入缓存；历史写入条件（至少一个成功、RecordHistory 为 true、History.Enabled 为 true）；取消后所有 Provider 都收到取消信号且不写历史；消费者提前 break 时子任务被取消；请求级错误在第一次 MoveNext 时抛出。

---

## M2 输入翻译（首个可用版本）

| ID | 状态 | 任务 | 依赖 | 估时 |
|---|---|---|---|---|
| T-014 | ☐ | ActionDispatcher | T-005 | 0.5d |
| T-015 | ☐ | TranslateWindow（View + ViewModel + Presenter + WindowPlacementHelper） | T-013, T-014 | 2.5d |
| T-016 | ☐ | InputTranslate / ClipboardTranslate / TranslateText Handler + 托盘菜单 | T-015 | 0.5d |
| T-017 | ☐ | HotkeyService + KeyNameMapper + 启动时注册 / 设置变化时重注册 | T-016 | 1.5d |

**T-014** 交付 CONTRACTS 中 `IActionDispatcher` 注释描述的全部约束。验收：防重入（同一 Action 执行中再次触发会被忽略；截图类动作之间互斥）；异常转为通知；OCE 静默；可从后台线程调用。

**T-015** 交付 ARCH §7.2、§10.5（跟随光标定位）、§14 的 TranslateWindow 部分。验收（手动 + VM 单测）：VM 用假 Orchestrator 测试卡片创建、Delta 节流刷新、Completed 覆盖、失败状态、单卡片重试、新请求取消旧请求；手动验证：在多显示器、混合 DPI 下窗口跟随光标且不超出屏幕、钉住 / 失焦隐藏、Esc 隐藏、Enter / Shift+Enter 行为。

**T-016** 验收：托盘菜单新增“输入翻译 / 剪贴板翻译”；托盘左键单击 = 输入翻译；Handler 单元测试覆盖热键二次按下关闭窗口的逻辑。

**T-017** 交付 ARCH §10.3 全部要点。验收：KeyNameMapper 覆盖映射表全部键（单元测试）；默认热键可以正常使用；与其他程序冲突时通知中列出冲突项；修改设置后热键立即生效，旧热键被释放；无效组合返回 HotkeyInvalid。
**🎯 M2 完成 = 首个可用版本（手动配置 settings.json + secrets 后即可使用）。**

---

## M3 设置界面

| ID | 状态 | 任务 | 依赖 | 估时 |
|---|---|---|---|---|
| T-018 | ☐ | SettingsWindow 外壳（草稿 / 保存模式）+ 常规页 + AutoStartService | T-017 | 1.5d |
| T-019 | ☐ | 翻译服务页（预设、增删改、排序、Key、测试连接、获取模型、Prompt 选择） | T-018 | 2d |
| T-020 | ☐ | 快捷键页 + HotkeyRecorderBox | T-018 | 1d |

**T-018** 验收：打开 / 取消 / 保存行为符合 ARCH §12.2 的草稿模式；主题切换即时生效；开机自启写入并清除注册表 Run 键；设置窗口只存在一个实例。
**T-019** 验收：从预设添加服务时自动填入 BaseUrl 和模型；Key 保存到 SecretStore（settings.json 中找不到 Key）；删除 Profile 时同时删除其密钥；测试连接显示延迟与测试译文，或错误信息；获取模型列表可用；模型名包含 haiku 时自动清空 Effort 并提示；Anthropic 的反代 BaseUrl 以 `/v1` 结尾时提示去掉（SDK 会自动追加 `/v1/messages`，ARCH §9.2）；拖拽排序后结果卡片顺序随之改变。
T-019 签发时必须实现 PROVIDER_REGISTRY.md §2.2 的配置提交协调器：整组凭据先写入新引用，再原子切换 Profile，刷新观察者后清理旧值；禁止直接复写活动凭据集或以多次 legacy Set 模拟事务。单卡测试连接/模型列表使用瞬态快照并及时释放。
**T-020** 验收：录入框能捕获组合键（包括 Win 键），Esc 取消，Backspace 清除；实时检测冲突并提示；保存后立即重新注册。

---

## M4 划词翻译

| ID | 状态 | 任务 | 依赖 | 估时 |
|---|---|---|---|---|
| T-021 | ☐ | Win32ClipboardService + KeyboardSimulator | T-005 | 1d |
| T-022 | ☐ | UIA / 剪贴板 / 组合选区读取器 + DesktopContext | T-021 | 2d |
| T-023 | ☐ | SelectionTranslateHandler + 划词设置页（基础部分） | T-022, T-018 | 0.5d |
| T-024 | ☐ | MouseSelectionMonitor + GestureRecognizer + SelectionIconWindow / Controller | T-023 | 2d |

**T-021** 验收：剪贴板被占用时按策略重试后抛出 ClipboardBusy；文本和图片可以写入；修饰键释放逻辑有单元测试（抽象出按键状态的读取接口）。
**T-022** 交付 ARCH §10.4 全部步骤。手动验收清单：记事本、Word、Chrome、Edge、VS Code、微信 / QQ、PDF 阅读器、Windows Terminal（**不得触发 Ctrl+C**）；剪贴板中原有的文本或图片在读取后被恢复；没有选中文本时返回 null，而不是返回旧的剪贴板内容；按住 Ctrl+Alt+D 不放时也能正确读取。
**T-023** 验收：Handler 单元测试覆盖三个分支（有文本 / 无文本且打开输入窗 / 无文本且只提示）；设置页可以编辑 ReadOrder、RestoreClipboard、排除进程列表。
**T-024** 交付 ARCH §6.3（鼠标划词）、§10.2（钩子线程）、§10.6（不激活窗口）。验收：GestureRecognizer 纯逻辑单测（拖动阈值、双击 / 三击时间窗、Shift+单击、I 型光标条件）；钩子回调耗时 < 1 ms（Debug 日志统计）；图标出现时不抢焦点，点击图标后翻译的是原窗口中选中的文本；排除列表生效；模式切换后立即生效；在文件资源管理器里拖动文件不会触发。

---

## M5 截图与 OCR

| ID | 状态 | 任务 | 依赖 | 估时 |
|---|---|---|---|---|
| T-025 | ☐ | MonitorEnumerator + GdiScreenCapturer（含 Crop） | T-022 | 1d |
| T-026 | ☐ | ScreenshotOverlayWindow + ScreenshotService | T-025 | 2.5d |
| T-027 | ☐ | WinRtImagePreprocessor | T-025 | 1d |
| T-028 | ☐ | 百度 OCR Provider（Token 缓存、四种模式、错误映射） | T-008, T-004 | 1.5d |
| T-029 | ☐ | Windows 本地 OCR Provider | T-027 | 1d |
| T-030 | ☐ | OcrProviderRegistry + OcrService + OcrTextNormalizer | T-028, T-029 | 1d |
| T-031 | ☐ | 截图翻译 / 截图 OCR / 静默 OCR Handler + OcrResultWindow | T-026, T-030, T-015 | 2d |
| T-032 | ☐ | OCR 设置页 + 截图设置页 | T-031, T-018 | 1d |

> T-027/T-028 可以与 T-026 并行。

**T-025** 验收：多显示器（含负坐标、混合 DPI）下 `VirtualBounds` 与各显示器 Bounds 正确；Crop 的交集计算有单元测试；单张 4K 屏抓取耗时 ≤ 60 ms。
**T-026** 交付 ARCH §6.6。验收：热键到覆盖层可见 ≤ 150 ms；每块屏幕的遮罩和选区都与实际画面对齐（混合 DPI）；Esc / 右键取消返回 null；放大镜显示正确；截图前 Iris 窗口被隐藏，截图后恢复。
**T-027** 交付 CONTRACTS 中 `IImagePreprocessor` 注释描述的全部规则。验收：放大、缩小、格式转换、JPEG 质量降级、无法满足约束时抛 ImageTooLarge、已满足约束时返回同一实例。
**T-028** 交付 ARCH §9.3。验收（MockHttp）：token 缓存与过期；并发获取 token 时只发出一次请求；110/111 刷新后只重试一次；错误码映射表每一行；language_type 映射；含位置版本的 Bounds 解析；段落合并；TestAsync 只请求 token。
**T-029** 交付 ARCH §9.4。验收：CJK 字符之间多余空格被删除（单元测试）；缺少语言包时返回 OcrLanguageUnsupported；并发调用被串行化。
**T-030** 交付 ARCH §8。验收：回退触发条件的每个分支；UsedFallback 标记；MergeParagraphs 在有 / 无 Bounds 两种情况下的规则；无文字时抛 OcrNoTextFound。
T-030 签发前须按 PROVIDER_REGISTRY.md 将 OCR 注册表原型同步为租约/发布屏障模式，支持同一凭据集格式，并接入 T-019 配置协调器；不能直接实现旧裸实例原型。
**T-031** 交付 ARCH §6.4、§6.5、§14 的 OcrResultWindow。验收：Handler 单元测试覆盖取消 / 失败 / 无文字 / 成功；OCR 结果窗口可以编辑、复制、转翻译、换服务重新识别；静默 OCR 完成后文本已复制并弹出通知。
**T-032** 验收：可以配置百度 API Key 与 Secret Key（写入 SecretStore）、选择模式、设置主服务和备用服务、测试连接；截图相关选项即时生效。

---

## M6 悬浮球

| ID | 状态 | 任务 | 依赖 | 估时 |
|---|---|---|---|---|
| T-033 | ☐ | FloatingBallWindow + Controller + 设置页 + ToggleFloatingBall Handler | T-031 | 2d |

**T-033** 交付 ARCH §6.7。验收：点击不抢焦点（点击悬浮球后执行划词，读到的仍是原窗口的选区）；拖动、吸附边缘、自动隐藏、悬停滑出都正常；右键菜单各项可用；拖入文本即翻译；重启后位置恢复（原显示器被拔掉时回到主屏）；在混合 DPI 屏幕之间拖动时尺寸正确。

---

## M7 完善与发布

| ID | 状态 | 任务 | 依赖 | 估时 |
|---|---|---|---|---|
| T-034 | ☐ | DatabaseInitializer + SqliteHistoryRepository | T-003 | 1.5d |
| T-035 | ☐ | HistoryWindow + 历史设置页 + OpenHistory Handler | T-034, T-015 | 1.5d |
| T-036 | ☐ | 网络 / 代理设置页（含代理连通性测试） | T-008, T-018 | 0.5d |
| T-037 | ☐ | 命名管道 IPC + 命令行参数 | T-014 | 1d |
| T-038 | ☐ | 国际化：全部 UI 字符串资源化（zh-CN / en-US） | T-035 | 1d |
| T-039 | ☐ | Velopack 打包、安装、自动更新检查 | T-038 | 1d |
| T-040 | ☐ | 端到端手动验收清单 + NFR 性能测量 + 缺陷修复 | T-039 | 2d |

**T-034** 交付 `specs/DB_SCHEMA.sql` 全部内容。验收（使用临时文件数据库）：迁移可以重复执行（幂等）；增删改查、FTS 检索（中文 ≥ 3 字、< 3 字走 LIKE）、收藏、按上限修剪时保留收藏、去重规则、删除后 FTS 同步。
**T-035** 验收：搜索防抖、分页、展开查看各服务结果、收藏 / 删除 / 清空（可选择保留收藏）/ 重新翻译 / 复制。
**T-036** 注意：自定义代理 URL 可能包含账号密码（`http://user:pass@host:port`），不得以明文写入 settings.json——带凭据时把 userinfo 部分存入 `ISecretStore`（键 `network/proxy-credentials`），settings 中只保存去掉 userinfo 的 URL（需同步提交契约变更申请）。验收：三种代理模式切换无需重启即可生效；“测试”按钮通过当前代理访问一个已配置的服务端点并显示结果。
**T-037** 交付 ARCH §10.1 步骤 1、7、9 及命令行。验收：`Iris.exe --action ScreenshotTranslate` 在主实例中触发截图；`--translate "hello"` 可用；非法参数给出提示且不崩溃。
**T-038** 验收：代码和 XAML 中不再有硬编码的中文 UI 字符串（用脚本扫描）；切换 UI 语言后重启生效。
**T-039** 交付 `build/pack.ps1`。验收：生成的安装包在一台干净的 Windows 10/11 虚拟机上能自动安装 .NET 桌面运行时并正常运行；检查更新功能可用；卸载时询问是否保留用户数据。
**T-040** 验收：`tasks/done/T-040.md` 中包含全功能手动测试记录，以及对照 ARCH §1.2 的 NFR 测量结果。

---

## Icebox（首期不做，已预留扩展点）

| 项 | 扩展方式 |
|---|---|
| 腾讯云 OCR（TC3-HMAC-SHA256 签名） | 新增 `OcrProviderKind.TencentCloud` + Factory |
| 多模态大模型 OCR（图片直接发给模型） | 新增 `OcrProviderKind.LlmVision`，复用翻译 Profile 的凭据 |
| DeepL / 百度翻译等传统翻译 API | 新增 `TranslationProviderKind` |
| TTS 朗读 | 新增 `ISpeechService` 端口 + 结果卡片上的朗读按钮 |
| 单词查询（词典模式，音标 / 释义） | 新增 Prompt 模板 + 输入为单个词时切换 |
| 贴图（截图钉在屏幕上） | 新增 `IrisAction.PinScreenshot` |
| 截图时自动识别窗口 / 控件边界 | ScreenshotOverlay 使用 UIA / `WindowFromPoint` |
| 截图选区调整手柄、标注工具 | ScreenshotOverlay 增强 |
| ARM64 构建 | 在 build 脚本中增加 RID |
