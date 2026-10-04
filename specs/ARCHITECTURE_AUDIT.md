# Iris 架构独立审查

> 日期：2026-09-28；审查人：Codex；对象：ARCH v1.12、CONTRACTS.cs、DB_SCHEMA.sql、BACKLOG，以及相关已落地基础设施。
> 用户要求：独立检查 Claude 的架构与设计，不预设其决策正确。
> 本文件是审查结果与后续变更建议，不修改 T-010-R1 的范围，不代表其返工已经通过评审。规范与契约修订应分批纳入后续工单。

## 1. 总体判断

**现有技术栈和五层结构可以继续使用，但跨模块契约、配置一致性和实际交付路径需要补齐。** 当前设计在部分底层实现上规定得很细，对运行时所有权、完整业务操作的取消、设置发布、缓存版本和部署能力等问题规定不足。“契约落地完全一致”和“单测全部通过”不能证明这些设计本身正确。

建议保留：

- .NET 10 + WPF：符合 Windows 热键、托盘、截图覆盖层、焦点控制的需求。
- Core 与 Windows/UI/服务商分离；统一动作入口；Provider 按用户配置实例化。
- C# 契约、依赖方向测试、可注入时钟、受控 HTTP 测试、结构化错误。
- 设置与凭据分离、DPAPI、SQLite、流式输出；这些选择本身不要求更换框架。

需要调整的工作方式：按可运行的业务链安排交付，把关键平台能力提前验证；对错误的契约进行受控修订；用有区分力的反例测试验收，取消机械的“测试数至少 N”门槛。

### 证据分类

- **实测**：本次隔离探针已经运行，原始结果见 §5。
- **契约推演**：根据接口和规定流程可构造反例，但相应功能尚未实现；不把它写成已发生的产品故障。
- **官方依据**：本次实际联网读取的官方文档，见 §6；涉及 Windows 运行行为时另列本机实测的边界。

P1 表示须在相关功能开始接线/发布前解决，不表示以下所有事项都要塞进当前返工单。

## 2. 主要发现与建议

### A01 · P1 · 凭据保护缺少完整的日志和传输边界

**依据**：ARCH §11.2、§11.3、§12.5；`HttpErrorMapper.TryMapException`、`HttpLoggingHandler.SendAsync`、`IrisException.ToErrorInfo`、`GlobalExceptionHandler`。

已确认的三个问题：

1. **实测**：HttpErrorMapper 的 Detail 已脱敏，但仍把原始 HttpRequestException 挂为 InnerException。异常的 ToString 会输出假代理密码；全局异常处理又会把异常对象交给日志器。仅检查 Detail 的测试无法证明日志安全。
2. **实测**：`ToErrorInfo()` 在 Detail 为空时回退到 `InnerException.Message`，绕过所有脱敏约束。这个危险默认行为直接写在契约源文件中。
3. **实测 + 官方依据**：`GetLeftPart(UriPartial.Path)` 保留 URL 的 userinfo，不能实现规范声称的 `scheme://host/path`。同时，SocketsHttpHandler 未禁用自动重定向；官方文档明确只有 Authorization 会在重定向时清除，其他头不会清除，因而 `x-api-key` 可跟随跨主机重定向。ARCH 的“不会自动跟随跨协议重定向”也不准确：.NET 禁止的是 HTTPS 降级到 HTTP，不能据此推导所有重定向都安全。

**建议**：

- 远程异常在 Provider 边界转换为只含安全字段的异常；不携带包含响应体/凭据的原始异常链。保留异常类型、错误码和安全诊断信息。
- 删除 ToErrorInfo 对原始内部消息的隐式回退；统一安全 URL 构造，排除 UserInfo、Query、Fragment。BaseUrl 的凭据输入另走 SecretStore。
- API 客户端默认 `AllowAutoRedirect=false`，把 3xx 按规范处理；有明确兼容需求时再定义同源且不降级的受控跳转。
- 回归检查覆盖异常 ToString、嵌套异常、日志参数与用户信息 URL，使用假凭据。

**时点**：当前返工完成后，优先作为一张范围明确的基础设施修正单处理。不要等到设置 UI 或真实用户输入密钥后再补。

### A02 · P1 · “不可变设置快照”实际上可以被外部修改

**依据**：CONTRACTS 的 AppSettings/ISettingsService、ARCH §12.2；SettingsNormalizer 发布 List/Dictionary，部分 ExtraHeaders 直接沿用传入对象。

**实测**：将一个有效 ExtraHeaders 字典传入 UpdateAsync，保存后修改原字典，Current 随之变化，但 Changed 不触发、文件不更新。`Current with { }` 只是浅复制；修改草稿中的 Hotkeys 字典会清空当前运行中的热键集合。这破坏了配置不变量、变更通知、写盘失败时状态不变等承诺，也使 Provider 所谓的不可变配置失去基础。

**建议**：

- 发布 Current 前复制并冻结整个配置对象图的集合，包括嵌套 ExtraHeaders、Providers、Prompts、热键和排除列表；不能只包一个只读接口而保留调用方可改的底层对象。
- UI 草稿使用独立可编辑模型，提交时转换为深度不可变快照。JSON DTO 与运行时快照可以共用部分 record，但须有清楚的边界。
- 不必为了冻结集合引入庞大依赖；可使用 BCL 的只读/冻结集合并确保先复制。首次默认设置、损坏恢复和普通更新都要走同一发布路径。

**时点**：T-011 之前。验证应包含“保存后修改原输入集合不影响 Current”和“取消草稿编辑不影响运行状态”。

### A03 · P1 · 设置和凭据分别提交会发布错误的组合

**依据**：ISettingsService.UpdateAsync、ISecretStore.Set/Remove、Registry 分别订阅两种 Changed；T-019 的草稿保存流程没有统一提交约定。

**契约反例**：用户把同一 Profile 从端点 A/Key A 改成端点 B/Key B。先写 Key，则 Registry 可短暂发布“端点 A + Key B”；先写 Profile，则可发布“端点 B + Key A”。两种顺序都可能把凭据发往错误目的地。百度的 ApiKey/SecretKey 也存在半套更新的问题。任一步写盘失败、事件交错或进程中断后如何恢复，目前没有定义。

**建议**：定义一个应用级配置提交入口，发布带版本的 Profile + 凭据集合快照。可采用版本化凭据引用：先写完整的新凭据集合，再原子切换设置中的引用，最后清理无引用旧集合；Registry 只发布完整版本。也可采用有明确恢复策略的提交协调器，但不能只规定“先写某一个文件”。不要为此引入分布式事务。

Changed 回调只入队更新，不在设置写锁内执行耗时的 Provider 重建、同步 UI 调用或等待别的锁；以版本号保证顺序并丢弃过期重建结果。

**时点**：T-011 前确定契约与运行时发布规则，T-019 实现完整编辑提交；先覆盖失败回滚、并发改 Key 与改 URL 的反例。

### A04 · P1 · Provider 注册表缺少在途请求的所有权约定

**依据**：ITranslationProviderRegistry.GetEnabled/Find 返回裸 Provider，CreateTransient 也返回裸接口；Provider 不注册到 DI；Anthropic Provider 已拥有可释放的 SDK/HttpClient。

**契约反例**：翻译正在使用旧 Provider，设置事件使 Registry 重建。立即 Dispose 旧实例会中断原请求；永不释放会积累资源。消费者取得旧实例后、开始枚举前发生重建，也存在窗口。CreateTransient 的调用方只拿到不含 IDisposable 的接口，没有规定测试完如何释放。

**建议**：

- 明确“发布、获取、退休、最后一个使用者释放”的生命周期。推荐以执行快照/租约表达所有权；新请求只取新快照，旧请求保留其租约直到结束。
- 如果选择“配置变化取消旧请求”，也须先取消并等待退出再释放，且明确 UI 表现；不能由 Dispose 隐式决定。
- 临时 Provider 由创建者/明确的异步使用作用域负责释放；退出时统一取消并回收在途操作。
- Registry 的集合是不可变快照，排序相同时有稳定顺序；主题、窗口位置等无关设置不应重建全部 Provider。

**时点**：签发 T-011 前。契约现在尚未用于 UI，适合尽早修正所有权接口。

### A05 · P1 · 缓存键与实际执行配置可能不一致

**依据**：TranslationCacheKey 仅含 ProviderId、Model、PromptFingerprint、语言对和文本哈希；ARCH §7.1；PromptTemplateProvider 每次读取当前设置，Provider 在执行时重新取模板。

**契约反例**：

- 同一 ProviderId/Model 改 BaseUrl、温度、输出上限或剥离选项，现有键仍相同，可能返回旧配置下的译文。
- 编排器按模板 A 计算键，排队后模板改成 B，Provider 执行时使用 B，却将结果写进 A 的键。
- 仅在配置变化时 Clear 不够：已开始的旧请求可以在 Clear 之后再写回旧结果。

**建议**：一次请求固定有效配置、凭据代次、语言、模板及渲染结果；缓存键包含无敏感信息的执行配置版本/指纹。读取和写入使用同一快照，旧代结果不得写入新代命名空间。凭据变化用代次失效，不把明文 Key 放进键或日志。缓存文本保留容量与字节双重限制，见 A12。

**时点**：T-012/T-013 前；不能把缓存任务视为可独立于配置版本设计“随时插入”的容器实现。

### A06 · P1 · M2 的任务依赖不能支撑所承诺的可用版本

**依据**：BACKLOG 与 ARCH §7、§10、§13；Host 已启用 ValidateOnBuild。

| 使用方 | 实际需要 | 当前安排的问题 |
|---|---|---|
| T-013 Orchestrator | 历史仓储写入；默认 History.Enabled=true | 仓储和初始化在 T-034 |
| T-015 窗口定位 | DesktopContext/显示器信息 | DesktopContext 在 T-022，MonitorEnumerator 在 T-025 |
| T-016 剪贴板翻译 | IClipboardService | 实现在 T-021 |
| T-015 错误卡片的“去设置” | ISettingsWindowPresenter | 设置窗口到 T-018 才实现 |
| M2 使用云翻译服务 | 能配置并保存 API Key | T-019 才有入口，“手动配置 secrets”没有安全、可执行的用户步骤 |

此外，“每个 IrisAction 恰好一个 Handler”需说明是当前已开放能力，而非在 T-014 就要求全部未来功能的 Handler 都存在。

**建议**：重排必要的最小实现，而非等待整块 M4/M7：提前交付桌面定位、剪贴板读写和可用的凭据配置入口。历史仓储可前移基础写入；若明确后置，则提供显式禁用的历史能力/受测替身并在 UI 中如实反映，不能注入一个悄悄丢数据的生产占位物。

每个里程碑结束增加真实组合根启动和一条完整用户路径验收：配置服务 → 输入 → 译文 → 取消/重试。避免把首次真实接线延后到 T-040。

**时点**：T-013/T-015 签发前修正依赖表。任务编号可保留，必要部分拆成前置交付，避免为了顺序而复制实现。

### A07 · P1 · 整个用户操作缺少统一身份与结果归属

**2026-10-03 验收更新**：5c5f78d / T-015-R5 的窗口操作租约、发布重入、单卡重试及复制/取消收尾层已独立验收，CCR-013 结案。各入口消费者继续按 TRANSLATION_ENTRYPOINTS.md 实施（当前 T-016），截图/OCR 等未来消费者仍由其工单验证；不将窗口层结案提前等同所有入口已经接通。

**2026-10-02 更新**：T-019 已结案，TRANSLATION_WINDOW.md / ARCH v1.25 / CCR-013 正式批准操作租约、受界限约束的主/重试/刷新、跨重试正文预算及取消/复制/退出所有权，当前交由 T-015 实施。设计已定稿不代表 A07 行为已验收；实际窗口实现及未来各入口消费者仍需按工单验证。

**依据**：ARCH §6.4、§6.5、§7.2；TranslationRequest 有 RequestId，但 TranslateWindowRequest/OcrResultViewRequest 没有操作标识，也没有取消参数。

**契约反例**：截图后 OCR 尚未返回，用户开始输入翻译；旧 OCR 完成后再次 ShowAsync，覆盖新文本并启动过期翻译。关闭窗口后迟到结果也可能重新打开它。取消 ViewModel 的上一条翻译 CTS 并不能取消此前在 Handler 中运行的 OCR。

单卡片重试同样有歧义：规范一方面规定 TranslationStarted 按本次 Providers 创建卡片，另一方面单卡重试只传一个 ID；照抄可能清掉其他卡片或取消其他仍在输出的 Provider。

**建议**：

- 在 App 层定义操作协调与会话身份，覆盖截图、OCR、翻译和展示全过程；每个目标窗口明确替换规则。
- 迟到结果必须校验操作/会话 ID；关闭、替换、退出时取消对应生命周期。取消只防止继续工作，ID 校验防止已排队的回调回写，两者都需要。
- 区分整个会话与单卡片 attempt：单卡重试只更新目标卡片，保留其它结果/任务。只允许当前有效且符合完整性要求的结果触发自动复制。
- 不要求把所有动作全局串行化；需要分别定义截图互斥、前台读取互斥、窗口结果归属。

**时点**：T-015 前设计；T-031 复用。验收使用受控的“旧任务后完成”测试。

### A08 · P1 · 划词兜底可能破坏剪贴板或向错误窗口发送 Ctrl+C

**2026-10-05 进度**：T-022B / 43fcf47（R6）已验收，隔离 UIA 与实际退出分支完成。A08 整体仍开放：CLIPBOARD_SNAPSHOTS.md / T-022C1 已定稿原生有界快照和同一 Open 帧内的版本条件清空/恢复；T-022C2 再定稿复制候选、终端/焦点安全、组合读取和退出。owner/sequence 不提供按键因果证明，不把本底座的条件比较冒称跨进程通用事务隔离。

**C2 签发更新**：C1/R1 / 8c68a4d 已验收。SELECTION_READING.md / ARCH v1.33 已定稿复制许可、按键释放后的准备回调、候选归属/条件文本/恢复与正常退出，CCR-017 同步公共语义和结果隐私。A08 仍待 C2 实施复审与后续真实应用验收；既有系统输入/同来源并发复制的不可原子识别限制保持明示。

**依据**：ARCH §10.4；ISelectionTextReader/ActionContext；GetClipboardSequenceNumber 官方说明；UIA 线程官方建议。

**契约反例**：

- 初次检查前台不是终端，等待修饰键释放期间用户切换到终端；随后 Ctrl+C 发给了新前台。
- 保存快照后用户在另一程序复制了新内容，最后无条件恢复旧快照会覆盖用户的新复制。
- 仅比较序列号变化，无法证明变化来自本次目标窗口；清空剪贴板本身也会改变序列号。CONTRACTS 要求先清空，ARCH 步骤却未规定清空以及何时读取基准序列号，存在冲突。
- Code.exe 等宿主的集成终端不在进程黑名单里；仅靠进程名不能证明 SendInput 安全。

另一个**实测机制问题**：Task.Run(...).WaitAsync(300ms) 只结束等待，底层 UIA/COM 调用可以继续阻塞。重复划词可能积累未退出任务；300 ms 不能当成硬取消保证。

**建议**：前台读取使用串行操作作用域；保存源 HWND/进程/手势代次，在实际发送前重新验证；不能证明目标安全时不注入按键。恢复前检查剪贴板仍属于本次操作，发生外部更新就保留新内容；清空、采样、读取与恢复规则统一，异常/取消路径同样验证。可恢复格式有限的限制要明确，不能承诺完整恢复任意格式。

UIA 使用有界的专用 MTA 执行策略，限制未结束调用数；超时后回退，并隔离迟到结果。不能每次超时再无限创建线程/Task；外部进程隔离仅在实测需要时引入。

**时点**：T-021/T-022 前。不会为验证这一反例向用户正在操作的窗口实际发送 Ctrl+C。

### A09 · P1（发布前）· Windows 本地 OCR 与部署策略缺少受支持的连接

**依据**：ARCH ADR-11、§9.4、T-029/T-039、便携模式；Microsoft Windows.Media.Ocr 文档。

官方明确该命名空间的桌面应用支持范围要求 **package identity**。当前 Velopack/便携方案没有身份注册设计。

**本机实测**必须同时保留：GetCurrentPackageFullName 返回 15700（无包身份），仍成功创建 OCR engine，并识别合成空白图，结果 0 行。因此不能断言“未打包必然不能调用”；这仍不构成官方支持保证或干净 Windows 10/11 的验收结果。

**建议决策**：

- 若坚持 Windows 本地 OCR 的受支持路径，可保留现有安装器并评估 external-location/sparse identity package；官方允许这条路线，无需先推倒 WPF 或断言必须全部换成完整 MSIX。要补签名、注册、卸载、更新与故障恢复验证。
- 便携版本单独定义能力：探测可用性并明确降级；如必须保证无安装离线 OCR，应另选受支持的引擎。
- 用户最初要求的是在线 OCR，强制离线兜底是后续架构增加的范围。若无强制离线需求，可以优先完成百度 OCR，把可保证的本地能力作为后续交付；这应是明确的范围决策。

**时点**：T-029 之前做部署验证，不能到 T-039 打包时才发现支持条件。此项不阻塞 M2 输入翻译。

### A10 · P2 · IPC 的帧格式与序列化选项直接冲突

**依据**：CONTRACTS 的 IpcCommand/IpcConstants 与 IrisJson.Options。

**实测**：协议要求 UTF-8 JSON“一行一条”，却使用 WriteIndented=true 的选项。一条简单命令输出 4 行，首行无法解析为命令。

**建议**：IPC 单独使用紧凑序列化并规定换行帧，或使用长度前缀；增加版本、最大帧长、非法动作/JSON 处理和可取消读写。不要与便于手工编辑的 settings.json 共用输出格式。

同时补充同用户访问控制、连接/启动就绪超时；Mutex 使用 Local 会话命名空间，而 PipeName 只有 SID，需要对齐多会话作用域。若允许同一用户多个会话实例，共享设置文件的写入协调也须定义。

**时点**：T-037 前；字符串载荷中的换行必须测试，但不能靠手工替换 JSON 中的换行实现协议。

### A11 · P2 · OCR 的无损模型与图像约束有自相矛盾之处

**依据**：IImagePreprocessor/ImageConstraints、OcrTextLayout.KeepLines、ARCH §9.3–§9.4。

- 字节过大时契约一律要求“先转 JPEG”，但 Windows OCR AcceptedFormats 是 Png/Bmp；按前者做会违反调用方保证图像满足 Constraints 的约定。
- 百度 Provider 在 paragraph=true 时提前将多行合成一行，再交给 Normalizer；KeepLines 无法恢复已经丢掉的原始分行。段落合并也容易丢失原始行坐标。

**建议**：图像处理的后置条件同时检查格式、尺寸、字节数；只选择 AcceptedFormats 内的编码，不能满足时明确失败。Provider 返回原始行、位置与可选段落归属，合并只由 Normalizer 按当前布局策略进行。必要时在 T-027/T-028 前给 OcrLine 增加可选段落信息。

**时点**：T-027/T-028 前，测试应以最终约束成立和 KeepLines 能保持原始行数为准。

### A12 · P2 · 缓冲和缓存没有与内存目标一致的上界

**依据**：ARCH §7.1 的 CreateUnbounded、§7.2 的 UI 节流、TranslationSettings 的可配置范围、CapturedFrame。

无界 Channel 在 UI 消费缓慢/暂停时继续增长；30 fps 刷新并不限制后台事件数量。LRU 仅限制条数，而允许 MaxInputChars=100000、CacheCapacity=10000：即使每条结果只有约十万 UTF-16 字符，文本本身就能达到约 2 GB，远超声明的空闲内存目标。此处是按配置上界计算，不是实测内存值。

**建议**：事件传递采用有界背压/增量合并，终态事件不可丢；缓存增加总字节预算和单条预算。区分空闲、翻译中、截图峰值的 NFR。抓取虚拟桌面要验证尺寸/Stride/乘法溢出和分配上限，稀疏多屏布局避免为屏幕之间的空洞分配巨大整幅缓冲。

**时点**：T-012/T-013 与 T-025 前，验收采用慢消费者、大结果、取消时有界退出与多屏合成数据。

## 3. 其他需要完善的设计

| 项目 | 问题与建议 | 安排 |
|---|---|---|
| 历史语义 | 去重只比较 text_hash+target_lang，不含 source_lang；“更新最新一条”可覆盖收藏内容。HistoryResult 不记录 Truncated，无法重现结果完整性。建议收藏不被隐式替换，去重包含语言语义，保存截断标记；分页用 created_at+id 稳定排序 | T-034 基础仓储开始前，若前移则同步修订 |
| DPI | GetDpiForMonitor 官方文档提示不应在 per-monitor-aware 调用线程使用；规范同时要求 PerMonitorV2。明确各查询的 DPI 上下文，用实际窗口 DPI/WM_DPICHANGED 驱动窗口布局，保留 SetWindowPos 的物理坐标定位 | T-015/T-025 前验证混合 DPI、热插拔和缩放变化 |
| 截图同步 | 固定等 16 ms 不能证明 DWM 已完成隐藏；恢复窗口也缺少异常/取消 finally 与激活规则。使用可验证的呈现同步/排除策略，并明确哪些 Iris 窗口被隐藏 | T-026 |
| 未来版本设置 | 已发布版本的迁移链缺失应与从未发布的版本号区分；尽力加载后再保存会丢失未知字段。建议未来格式只读/明确降级，缺失合法迁移链显式失败并保留原件 | 首次引入真实迁移或发布回滚时 |
| 调用约定 | T-011 的“缺少 Key 时 IsConfigured=false”不能覆盖已经支持的免 Key 本地服务；显式选择不存在/重复 ProviderIds 的卡片与事件规则、取消时 TranslationFinished 的例外应明确 | T-011/T-013 前统一行为表 |
| UI 约定 | EnterToTranslate=false 的契约是 Ctrl+Enter，而部分文字说 Enter/Shift+Enter 反过来；Text=null 表示不修改，不能同时用它表达清空 | T-015 前统一行为表 |
| 默认能力与成本 | 默认启用服务端拒答回退、固定模型别名和较大的输出上限属于产品策略。应在设置页说明实际能力/模型变化，按实测延迟与成本定默认值；网络重试也不意味着服务端绝无重复处理 | T-019；无需推翻当前 Provider 协议实现 |
| 国际化 | ErrorInfo.Message 定义为中文，后续又要求全 UI 国际化；应以 ErrorCode/参数在展示边界本地化，不能把中文记录作为唯一显示数据 | T-038 前，新增 UI 尽早使用资源 |

## 4. 建议推进顺序

1. 完成并独立复审 T-010-R1。当前返工按原工单验收，本审查不追溯增加其验收项。
2. 随后安排一张小范围基础设施修正单，优先处理 A01 和 A02。A03/A04 的契约决策先完成，再签发 T-011；不要求同时完成设置窗口。
3. T-012/T-013 纳入执行快照、缓存代次和有界流；修复 M2 依赖安排。尽快跑通配置服务到显示译文的真实纵向流程。
4. Windows 选区、OCR 部署和截图能力各在对应里程碑前做验证；不把 M5/M7 的全部问题前置到输入翻译开发。

建议建立一张“场景 → 期望行为 → 契约 → 验证”的表：新请求覆盖旧请求、配置变更期间翻译、改 URL 和 Key 的半提交、慢消费者、剪贴板外部更新、无包身份启动等。它比按测试数量或逐字复制规范更能检验架构。

当前 ContractFidelityTests 会逐行比较注释和声明，可继续用于发现漂移，但应逐步区分签名/默认值兼容性检查与行为测试。不要让一个错误的规范因为已有测试守护而难以修正。

## 5. 本次实测证据与限制

为避免和 Gemini 的返工编译争用，探针使用复制到临时目录的现有程序集；没有引用工作区项目进行重建。相关 Core、Infrastructure、Providers/Http 源码在核查时未处于返工修改范围。只在临时目录创建测试设置文件；HTTP/异常用假数据；没有操作用户剪贴板、发送按键或调用真实翻译/OCR 在线服务。

项目：`C:\Users\28910\AppData\Local\Temp\iris-design-audit\probe\Audit.csproj`；源码为同目录 Program.cs。

执行命令：

```powershell
dotnet run --project C:\Users\28910\AppData\Local\Temp\iris-design-audit\probe\Audit.csproj -c Release
```

最终原始输出：

```text
external_dictionary_mutated_live_snapshot=True; changed_events=0; disk_contains_mutation=False
shallow_draft_edit -> live_hotkeys_count=0; changed_events=0
mapped_detail_contains_secret=False; exception_ToString_contains_secret=True
ToErrorInfo_fallback_contains_secret=True
GetLeftPart_Path_contains_userinfo_secret=True
IPC_serialized_lines=4; first_line_is_valid_command=False
WaitAsync_timeout -> original_operation_still_pending=True
GetCurrentPackageFullName_status=15700
unpackaged_OCR_probe: recognizer_languages=2; engine_created=True
unpackaged_OCR_synthetic_image: completed=True; recognized_lines=0; OS=Microsoft Windows 10.0.26200
```

WaitAsync 探针等待一个未完成任务并用 20 ms 超时结束等待，随后确认原任务仍未完成，最后主动结束它；它验证等待语义，不冒充真实 UIA 挂起实验。OCR 输入是 100×50 的合成空白图，验证 API 调用，不证明识别准确率、多语言覆盖或干净机器兼容性。

本轮没有针对仍在变化的返工工作区重复执行全量构建/测试，也没有验证真实重定向服务器、真实剪贴板竞态、混合 DPI、多会话管道、MSIX/sparse identity 安装或 Velopack 更新。这些分别以官方依据、契约反例或待实施验收项标注。

## 6. 本次实际查阅的官方来源

1. [HttpClientHandler.AllowAutoRedirect](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclienthandler.allowautoredirect?view=net-10.0)：默认 true；原文 **“The Authorization header is cleared on auto-redirects … No other headers are cleared.”**；.NET Core/.NET 5+ 不自动从 HTTPS 降级到 HTTP。
2. [Task.WaitAsync](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.waitasync?view=net-10.0)：返回在原任务完成或超时/取消条件满足时结束的等待任务；本地探针验证原任务仍可继续存在。
3. [Understanding Threading Issues / UI Automation](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading)：桌面范围 UIA 调用建议放在独立、不拥有窗口的 MTA 线程，文档同时说明自动化元素与 COM apartment 生命周期问题。
4. [GetClipboardSequenceNumber](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclipboardsequencenumber)：原文 **“This number is incremented whenever the contents of the clipboard change or the clipboard is emptied.”**；延迟呈现时有额外时序限制，序列号不是操作来源证明。
5. [Windows.Media.Ocr](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr?view=winrt-26100)：原文 **“APIs in the Windows.Media.Ocr namespace are only supported for desktop apps with package identity.”**。支持条件与本机无身份仍能调用的观测应同时呈现。
6. [Grant package identity by packaging with external location](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps)：官方提供 sparse/external-location 身份包路线，原文说明可 **“without switching to full MSIX packaging or changing your existing installer”**；需按文档实现身份注册和安装生命周期。
7. [GetDpiForMonitor](https://learn.microsoft.com/en-us/windows/win32/api/shellscalingapi/nf-shellscalingapi-getdpiformonitor)：原文 **“This API is not DPI aware and should not be used if the calling thread is per-monitor DPI aware.”**，并指向 GetDpiForWindow；需结合实际线程上下文与混合屏测试设计。

查阅官方资料不等于所有风险已经通过产品验证；各条结论的证据级别以正文为准。

## 7. 执行跟踪（2026-10-01 更新；下方旧条目保留为历史）

T-019A-R3（7e993b3）通过，A / CCR-011 结案。独立 Release 零警告/零错误、全量 1369 成功/1 环境跳过；不相关翻译字段不再挡模型列表，共同安全校验及既有捕获/隐私/字段/回执探针通过。按 PROVIDER_SETTINGS_UI.md / ARCH v1.24 签发 T-019B，接共享草稿、一次保存、凭据/异步操作和跨窗口回执；CCR-012 同步新建 DeepSeek 预设值，既有配置不迁移。总单待 B 验收。

T-019A-R2（5335c57）主要反例通过；独立 Release 零警告/零错误、全量 1368 成功/1 环境跳过。仅剩模型列表仍检查不参与该请求的聊天路径/MaxTokensField/Effort，签发 T-019A-R3 限操作校验收尾，明确保留认证/地址/头/超时约束。详细对照见 tasks/done/T-019A-R2.md 末尾；CCR-011 待 R3，B 不开始。

T-019A-R1（b29ccc3）未通过。独立 Release 零警告/零错误、全量 1362 成功/1 环境跳过。旧取消/非法枚举/零候选/排序反例修复接受；新实际协调器/Registry 探针确认 Get 在释放编辑权后才执行、错误消息/链携带原文、未编辑 Options 丢失、草稿校验遗漏及清理重试省略当前屏障。签发 T-019A-R2，详情见 tasks/done/T-019A-R1.md 末尾；CCR-011 待复审，B 不开始。

T-019A（a1c6570）未通过。独立 Release 零警告/零错误，全量 1346 成功/1 环境跳过。实际协调器/Registry 与受控端口确认写前取消仍发布、非法凭据意图清除有效旧值、零候选回执跳过重试、排序跳过屏障、Keep 使用旧 base/同步读取及草稿校验/错误结果/默认 Options 问题。签发 T-019A-R1，证据见 tasks/done/T-019A.md 末尾；保留已正确的 Provider 模型发现 guard、捕获/staging/基础共享引用及租约清理。CCR-011 不结案，B 不开始。

T-018-R6（11d4ac2）通过，T-018 / CCR-010 结案。独立 Release 零警告/零错误、全量 1328 成功/1 环境跳过；两种局部失败状态及故障解除后的实际清理通过，R5 退出反例保持通过。下一单 T-019A 依据 PROVIDER_EDITING.md / ARCH v1.23 / CCR-011 实现 Core 联合发布、凭据回收、回执重试和草稿操作；T-019B 再接 UI，A 不提前结案总单。新纯契约声明由架构师同步，协调器业务尚未实现。

T-018-R5（6c33a42）三条主反例通过，独立 Release 零警告/零错误、全量 1325 成功/1 环境跳过。仅剩原工单明确列出的未组装 Session 回滚分支：Close 取消后局部资源无重试所有者，Handle=0 分支误判关闭。签发 T-018-R6 只收尾该分支，证据见 tasks/done/T-018-R5.md 末尾；CCR-010 待 R6，继续用户手动转交。

T-018-R4（d00408f）未通过。独立 Release 零警告/零错误、全量 1321 成功/1 环境跳过；原三组反例通过。新增实际探针确认 Show+回滚失败丢失会话、创建中关停因 ActiveSession=null 提前完成、Host 退出阶段并发 Stop 不共享完整任务。签发 T-018-R5，证据见 tasks/done/T-018-R4.md 末尾。用户最新指示恢复手动转述，由用户转交已准备的 R5，Antigravity 控制暂停；CCR-010 仍待复审。

T-018-R3（e85bf22）未通过。独立 Release 零警告/零错误、全量 1315 成功/1 环境跳过；旧查询、关闭会话保留、Fatal、命令诊断和测试程序集退出接线接受。新增反例确认最新自启意图变更后错误一致状态、并发重开双窗口、Show 失败提前完成、关闭失败返回成功及隐藏窗口错误释放。签发 T-018-R4 三组收尾，含正常退出失败的必要组合根接线；SETTINGS_EDITING.md §7 澄清不提前销毁依赖，CCR-010 待复审。详情见 tasks/done/T-018-R3.md 末尾。

T-018-R2（3c842e5）未通过。独立 Release 零警告/零错误、全量 1309 成功/1 环境跳过；显式 Shutdown/后台查询/安全主题日志与 HWND 回滚的旧反例通过。受控新增反例确认查询代次未覆盖保存/重试、普通关闭与失败回滚遗忘活动查询、Closing 取消仍 Dispose、额外 Fatal 白名单导致静默规范化；测试宿主停止/Join 和命令诊断要求尚未落实。签发 T-018-R3，CCR-010 待复审，不启动 T-019。详情见 tasks/done/T-018-R2.md 末尾。

T-018-R1（361fb71）未通过。独立 Release 零警告/零错误、无筛选全量 1307 成功/1 环境跳过。原注册表清理、保存冻结/基线/重试资格、预览与路径已修；新受控真实 WPF 探针确认保存/重试在 scope 释放后仍有 VM 任务与通知、自启查询在 UI 执行、Show 失败遗留 HWND、主题日志携带原异常。测试还有 async void 假门闩、未断言退出顺序及宿主无 Join 等问题。签发 T-018-R2 限原范围收尾，CCR-010 待复审；详情与输出见 tasks/done/T-018-R1.md 末尾。准备稿 PROVIDER_FORM_VALIDATION_DRAFT.md 的模型发现门槛调整留待 T-019。

T-018（b9d7526）未通过。独立 Release 零警告/零错误；1 个注册表测试会删除整个 Software\IrisTests 父键，先排除后运行其余套件，1294 项通过/1 项环境跳过，不冒充全量独立验证。实际 VM/WPF Dispatcher/会话探针确认保存中编辑与基线/重试状态丢失、代理完成任务早于真实退出而提前 Dispose scope、窗口关闭被取消、主题预览与持久值分离、创建失败 scope 泄漏和相对路径被接受。T-018-R1 只修原范围，CCR-010 尚不结案；T-019 凭据草案仍未实施。

T-022A-R1（ecee697）最终通过：独立 Release 零警告/零错误、1230 项成功/1 项环境跳过；原 DPI 采集作用域、清理失败掩盖、同名冲突和异常诊断反例均符合要求，CCR-009 结案。A06 的桌面查询前置交付完成；多屏/混合 DPI 实机范围不提前算作完成。

当前签发前移 T-018，SETTINGS_EDITING.md / ARCH v1.22 / CCR-010 固定真实设置入口、窗口作用域、dirty 字段冲突、主题预览、已保存/应用失败的区分及正常退出等待。其构造链使用延迟窗口工厂避免 Dispatcher/Handler/Presenter 成环，已有独立 DI 模型验证；真实 WPF/保存实现仍由本单验收。A03 的凭据提交和 A07 会话模型分别留待 T-019/T-015。

T-022A（3147224）独立 Release 零警告/零错误、1220 项成功/1 项环境跳过。几何实现与 10,000 组逐像素基准一致，C21-1 实际协议基线及恢复后各 8 项通过，提前采样/掩盖恢复失败变异分别被 2/3 项断言拒绝，C21-1 销项。受控适配器复现坐标采集早于 PerMonitorV2 作用域、查询加清理失败后重试掩盖 context/窗口遗留、同名冲突显示器发布及异常清理日志遗漏；签发 T-022A-R1，CCR-009 未结案。A06 的桌面依赖尚未完成；本轮不改变其它已验收模块。

T-021A-R2（e746382）最终通过：独立 Release 零警告/零错误、1174 项成功/1 项环境跳过，两条复合清理反例修复；实际 Helper 协议基线及恢复后均 7 项通过，独立提前采样/掩盖恢复失败变异分别被 1/3 项断言拒绝。CCR-008 结案，A06 的剪贴板服务前置交付完成；原生独占站实机范围仍未验证。C21-1 仅改善验证工具对外部正常复制的误报，带入下一单。

当前签发 T-022A：DESKTOP_CONTEXT.md / ARCH v1.21 / CCR-009 正式固定筛选镜像伪设备、保留有效远程桌面、物理坐标/每屏 DPI、前台 HWND/PID 重验与失败/资源规则，解决 A06 的桌面前置。已有单屏查询及几何差分是准备证据，不代替新实现验收；A07 会话草案仍待 T-015 定稿。

T-021A-R1（d813496）独立 Release 零警告/零错误、1166 项通过/1 项环境跳过。原生命周期、STA 上准备、解码准入及 EXIF 反例通过，C14-1 代码补强与计数更正接受。剩余两个复合清理失败仍漏日志；Helper 的纯逻辑测试只断言手写列表，未调用实际实现，提前失败的恢复/关闭结果也未统一处理。签发 T-021A-R2 仅收尾这些原范围问题及验证/报告；不扩大到桌面定位。CPU=60% 的性能输出不作为启动 NFR 通过证据。

T-021A（da79cc7）未通过：独立 Release 零警告/零错误、1156 项成功/1 项环境跳过；临时探针实测 Dispose 过早销毁串行门、STA 上分配/锁内存、图像解码先于串行准入、EXIF 自动旋转导致 DIB 行布局错误、读取失败变 null 与清理失败无日志。源码还确认 Helper 在恢复前误比较跨窗口站序列号、线程桌面恢复不保证同线程，C14-1 的同步 shutdown/报告有剩余问题。签发 T-021A-R1，CCR-008 未结案；不把错误 183 的环境跳过本身当成缺陷。A06 的剪贴板依赖尚未完成；桌面/会话准备稿继续等待后续签发。

T-014（e61fb8d）独立复审通过：Release 零警告/零错误、1095 项测试通过；同一实例的异常恢复、真实 STA 上 64 次重复触发、运行中取消、真实排队取消及关闭中止探针符合规范，CCR-007 结案。正式测试的同实例恢复断言、门闩/线程收尾及报告限定作为 C14-1 小遗留进入 T-021A。

当前签发 T-021A / CCR-008，规范见 CLIPBOARD_SERVICE.md。A06 的剪贴板依赖进入实施；A08 的选区快照/外部更新保护仍留待 T-022。本轮原生探针确认非交互窗口站文本读写及 owner 销毁后数据可读，交互剪贴板序列号未变；加入 CWF_CREATE_ONLY 后返回错误 183，独占原生集成环境尚不满足，不把早期 flags=0 结果当作独占证明。正式测试不得为通过而复用已有站，允许明确报告环境跳过；图像与新服务均尚未验证。

T-013-R1（99787b8）独立复审通过：Release 零警告/零错误、1063 项测试成功，排队提前退出/快照关闭缓存/释放失败三个原反例均修复，总预算探针通过，CCR-006 结案，M1 完成。A05 的冻结执行配置与 A12 的缓存/流式预算部分已经接入实际编排器；A12 的图像预算仍待截图任务。

当前签发 T-014，ARCH v1.19 §6.1 / CCR-007 解决 A06 的渐进 Handler 注册歧义，明确 UI 排队前原子防重入、等待完整异步执行、取消及安全通知。A06 其它依赖已调整为 T-014 → T-021A（剪贴板）→ T-022A（桌面/显示器）→ 前移 T-018/T-019（设置及安全凭据入口）→ T-015/T-016/T-017；此处是安排，不能把未实施项算作完成。A07 的会话与单卡 attempt 设计仍须在 T-015 签发前定稿，不放进 Dispatcher 充当全局串行任务框架。真实服务、桌面 UI 与性能本轮未独立验证。

T-013（e085558）独立构建零警告、1056 项测试通过；隔离探针复现提前消费结束时信号量过早释放导致排队任务卡住和租约不释放。另有快照关闭缓存仍写、仅释放异常被吞成成功。签发 T-013-R1 只处理这些生命周期/收尾边界和测试，保留其它多服务/缓存/历史流程。总输出预算探针符合预期，M2 暂不开始。

T-034-R2（2be09c3）最终通过：独立构建零警告、1011 项测试通过，合法 V1/V2 与全部受控反例符合预期，CCR-005 结案。当前签发 T-013，将 A05 的冻结请求与 A12 的缓存/有界传递接入编排器，并明确取消/正常收尾边界；完整规范见 TRANSLATION_ORCHESTRATOR.md / CCR-006。M2 平台与配置依赖仍按后续工单推进。

T-034-R1（9ffa57a）独立构建零警告、1004 项测试通过，上轮具体反例复测改善。结构校验仍只依据列名/关键字，错误删除触发器、伪 FTS 表、错误主键会被接受；预检后读写阶段已见未来版本仍成功，取消与锁错误竞争返回 StorageFailure。T-034-R2 仅收尾这些既定边界及报告，保留其它 R1 修复。详细证据在 tasks/done/T-034.md 末尾。

T-034 的 e131ecd 独立复审：构建零警告、995 项测试通过，但受控探针确认初始化拒绝前已修改未来库/不兼容 V1、缺触发器仍就绪并留下 FTS 孤儿、原始异常链泄漏与事务开始错误外漏、读取占用写锁、不存在 id 取消收藏误触修剪、排队写操作使用旧上限。签发 T-034-R1 修复原范围，不改变 schema/公开签名；CCR-005 待返工复审。C12-1 已接受。报告测试名称与证明范围需按实际文件更正。

T-012-R1 提交 2daec76 已于 2026-09-29 验收，构建零警告、968 项测试通过；初始代次/long 算术已修复，总预算变异被拒绝，CCR-004 结案。A05 的底层快照/Prompt/键和 A12 的缓存预算已交付，仍须 T-013 接入请求管道与有界事件流。C12-1 设置/缓存并发测试补强纳入 T-034。A06 的历史依赖通过前移 T-034 处理，HISTORY_STORAGE.md/CCR-005/V2 DDL 已批准；M2 的其它平台与配置依赖仍待相应工单。

T-011 已于 2026-09-29 最终验收：R2 实现 3be662d、报告更正 0744d7b。独立构建零警告、920 项测试通过，80 项关键测试连续五轮通过，27 项隔离检查通过且外部通知/退休清理已释放重建互斥，三个隔离回归变异均被测试拒绝。A03/A04 的 T-011 读取/发布/租约部分完成，CCR-003 结案；T-019 写入协调器与 T-030 OCR 接入仍待后续。A05/T-012 的执行上下文、Prompt 和缓存键进入下一步架构细化。报告中退出日志与启动性能的无效证据已撤回。证据见 tasks/done/T-011.md 末尾。

T-010-R2（81c0ddd）、A01/T-010A（587484a）、A02/T-010B（21f6224）均已验收，CCR-001/002 结案，验证边界见各工单记录。A03/A04 的设计已在 PROVIDER_REGISTRY.md 定稿，CCR-003 已批准，T-011 开始实施注册表/读取/租约；配置提交协调器和 OCR 对应接口分别在 T-019/T-030 实施，不能提前算作完成。A05 的请求上下文与缓存键仍须在 T-012/T-013 签发前细化。其余事项按本报告所列时点处理。
