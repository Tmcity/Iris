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

## 7. 执行跟踪（2026-09-29 更新）

T-011 已于 2026-09-29 最终验收：R2 实现 3be662d、报告更正 0744d7b。独立构建零警告、920 项测试通过，80 项关键测试连续五轮通过，27 项隔离检查通过且外部通知/退休清理已释放重建互斥，三个隔离回归变异均被测试拒绝。A03/A04 的 T-011 读取/发布/租约部分完成，CCR-003 结案；T-019 写入协调器与 T-030 OCR 接入仍待后续。A05/T-012 的执行上下文、Prompt 和缓存键进入下一步架构细化。报告中退出日志与启动性能的无效证据已撤回。证据见 tasks/done/T-011.md 末尾。

T-010-R2（81c0ddd）、A01/T-010A（587484a）、A02/T-010B（21f6224）均已验收，CCR-001/002 结案，验证边界见各工单记录。A03/A04 的设计已在 PROVIDER_REGISTRY.md 定稿，CCR-003 已批准，T-011 开始实施注册表/读取/租约；配置提交协调器和 OCR 对应接口分别在 T-019/T-030 实施，不能提前算作完成。A05 的请求上下文与缓存键仍须在 T-012/T-013 签发前细化。其余事项按本报告所列时点处理。
