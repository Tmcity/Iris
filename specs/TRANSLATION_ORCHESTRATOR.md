# 翻译编排器行为规范

> 版本 1 · 2026-09-29 · T-013 实施。基于已验收的注册表租约、请求快照/缓存与真实历史仓储。公开签名保持不变，配套注释更新见 CCR-006。

## 1. 请求入口与快照

TranslateAsync 返回异步序列，调用方法或 GetAsyncEnumerator 时不触发 Registry/Provider/历史 I/O。首次 MoveNextAsync：

1. 检查方法参数与枚举器参数合成后的取消令牌。取消优先抛 OCE。
2. 捕获请求标量及 ProviderIds 的独立只读副本；之后修改原列表不影响本次请求。request=null、RequestId=Guid.Empty、未知 Origin、显式列表中的 null/空白 ID 为 IrisException(InvalidArgument)，在首次枚举抛出，不输出会话事件。
3. 在第一次异步等待前捕获一次 Cache.Generation，防止 Registry 等待期间 Clear 后旧请求重新填入。缓存服务异常时记安全 Warning 并禁用本请求的缓存使用，不阻断翻译。
4. 借用 Registry.AcquireSnapshotAsync。预处理、语言、模板、并发度、输入上限、缓存启用与历史启用策略都取此快照，使用 T-012 的显式 TranslationSettings 重载。预处理后空白/空串为 InputEmpty，UTF-16 Length 超 MaxInputChars 为 InputTooLong。
5. 在 Provider 排队前固定各项实际 RenderedPrompt、带 PreparedPrompt 的 ProviderTranslateRequest 和 TranslationCacheKey；读写同一键，不能执行前重新查模板或重算设置。可将预处理/准备工作放在线程池，不能在 UI 线程构造 SDK 或做网络/磁盘操作。

编排器不直接依赖 ISettingsService。缓存自身的实时开关/容量与仓储的最新修剪上限仍按各自契约生效；这不授权重新计算本次请求的执行配置。

## 2. Provider 选择与卡片身份

| ProviderIds | 行为 |
|---|---|
| null | 选择租约中已启用且 Descriptor.IsConfigured=true 的全部项，保留注册表顺序；一个也没有时首次枚举抛 NotConfigured，无事件 |
| 空集合 | 首次枚举抛 NotConfigured，无事件 |
| 非空集合 | StringComparer.Ordinal 精确匹配，重复 ID 只保留第一次，按请求中首次出现的顺序生成卡片；不裁剪或折叠 ID |
| 显式不存在、禁用、未配置 | 仍有对应卡片，产出 ProviderStarted→ProviderFailed(NotConfigured)，不调用该 Provider，不查缓存；即使全部如此仍正常输出 Started/Finished |

TranslationStarted.Providers 是独立只读列表。已知项使用捕获的 Descriptor；禁用项可从 Snapshot.Settings 中取其显示名/Kind/Model，但不得创建实例或读取凭据。完全未知项使用 `ProviderDescriptor(id, id, "Unknown", null, false)`。

所有事件的 ProviderId 固定为所选项的 ID，不能让异常中携带的其它 ProviderId 改变归属。不同请求可以同时调用同一个 Orchestrator；CTS、Channel、计数、结果和预算全为每次请求独立状态，禁止用单例字段保存“当前请求”。

模板准备中的单项失败转为该卡片的 ProviderFailed，其余继续；共享输入/语言/租约准备失败为请求级异常，仍在首个 TranslationStarted 之前抛出。

## 3. 事件与执行

正常序列：TranslationStarted 唯一最先；每项依次 ProviderStarted→ProviderDelta*→ProviderCompleted 或 ProviderFailed（恰好一个终态）；TranslationFinished 唯一最后。跨 Provider 的事件可交错，不要求完成顺序等于卡片顺序。

- 卡片列表确定后先 yield TranslationStarted，再启动生产者。消费者在此就结束时没有 Provider 调用，仍释放租约。
- 未配置项、准备失败项和缓存命中可以直接输出；仅真实 Provider 调用受本请求 MaxConcurrentProviders 限制。排队中的卡片保持等待，取得名额后才发 ProviderStarted，Elapsed 不计排队时间。
- CacheEnabled 且 !BypassCache 时尝试缓存读取。有效命中输出 Started 和 Completed，FromCache=true、Truncated=false、Usage=null、Elapsed=TimeSpan.Zero，不输出 Delta、不调用 Provider。
- 缓存读异常视为 miss 并记安全 Warning；空白缓存值视为 miss。缓存不是导致翻译失败的依赖。
- 未命中时调用捕获的 Provider 和 PreparedPrompt 请求，传递本次合成取消令牌。用注入的 TimeProvider 计算从 ProviderStarted 到流处理结束的 Elapsed，不直接读 DateTime.Now。

### 3.1 Provider 流校验

每个非空 Delta 按原样拼接并发出事件；结束 Chunk 的 Delta 也要处理。Usage 的 InputTokens/OutputTokens 分别保留最后一次非 null 值，全部未出现时最终为 null。

- 正常 EOF 前必须恰好出现一次 Completed 或 Truncated 的 FinishReason；空流、缺失结束标志、未知原因、重复终态或终态之后还有 Chunk 为 InvalidResponse。
- Delta=null、正常结束后完整正文仍为空白，也为 InvalidResponse。不要把 EOF 自动当作成功。
- 校验 EOF 并完成枚举器 DisposeAsync 后，才发布 ProviderCompleted，Text 为完整权威值。Truncated=true 计为成功，但不写缓存；历史保留截断标记。
- 单 Provider 的 IrisException 转为其 ErrorInfo，并归一 ProviderId；其它异常（含没有发生请求取消的 OCE）转为 Unknown。日志只记请求/Provider ID、错误码和异常类型，不传原始异常对象或正文/Prompt/密钥。
- 请求已取消时，Provider 的取消/退出不再产生失败卡片事件。清理异常不得覆盖已确定的主要失败或导致双重终态。

## 4. 有界传递与输出上限

内部固定限额（可集中在 internal 帮助类型，不增加设置字段或公开 API）：

| 项 | 值 |
|---|---|
| Channel 容量 | 128 个事件，FullMode=Wait |
| 单个 ProviderDelta 最大长度 | 4096 个 UTF-16 code unit；较大片段拆分，避免切断已有合法代理对 |
| 每项正文累计上限 | 1,048,576 个 UTF-16 code unit |
| 本请求全部正文累计上限 | 4,194,304 个 UTF-16 code unit，含缓存命中正文 |

在追加 StringBuilder/发出 Delta 之前检查并预留长度；并发更新总预算必须原子，不能溢出。总预算按本请求累计接收正文计量，失败后无需返还，避免复杂跨生产者回收竞态。限额用 long 计算。

超过上限的项停止枚举并释放资源，产出 ProviderFailed(InvalidResponse)，使用固定提示“翻译结果超过本地长度限制”；该项的部分文本不写缓存/历史，其它项继续按剩余预算执行。不要截掉文本后假装正常完成。

所有 Channel 写入（包括终态）都使用本次工作令牌，慢消费者形成背压。不能用无界中转列表/队列绕过 Channel；终态不丢弃。取消或提前结束时取消阻塞写入、等待生产者退出后再释放租约，不能只让 Reader 停止而遗留后台任务。

这些是编排器拥有的事件/正文逻辑界限，不代表 SDK、模板渲染或整个进程的精确分配量；产品内存 NFR 仍需后续真实路径测量。

## 5. 取消与正常收尾的分界

方法取消令牌与 GetAsyncEnumerator/WithCancellation 令牌均生效。每项 Provider 的异步枚举器、并发名额与租约都必须在正常、错误、取消、消费者 break 的路径完整清理。

- 正常收尾之前，调用方取消 → 停止全部生产者，外部枚举抛 OCE，不产出 TranslationFinished，不写本次缓存/历史。
- 消费者提前 break/DisposeAsync → 取消并等待子任务，释放租约，不写本次缓存/历史。已输出个别 Completed 也不等于整个会话已经收尾。
- **正常收尾点**：所有子任务退出、事件 Channel 正常读尽，最后一次取消检查通过。此时会话结果已确定，进入不可撤回的完成阶段。
- 收尾点之后的迟到取消不把已经完成的会话伪报为取消，不撤销已保存数据。完成阶段继续尝试缓存和历史，然后输出 TranslationFinished。实现应显式区分此阶段，不能在历史提交后追加用户取消检查。

这条分界细化了原“取消不写历史”的适用时点，避免一边保存成功一边向用户报告取消。测试须分别控制收尾点前取消和历史保存开始后的迟到取消，不能依靠时间猜测。

## 6. 缓存、历史与最终事件

完成阶段：

1. 新鲜、未截断的成功结果以请求开始时的 Generation 调用 TrySet。BypassCache 只跳过读取，仍可更新缓存；缓存命中无需再次写入。TrySet=false 是正常拒绝（Clear/预算等），缓存异常只记安全 Warning。
2. RecordHistory && Snapshot.Settings.History.Enabled && 至少一项成功时，调用真实 ITranslationHistoryRepository.AddAsync 一次。SourceText 是预处理后的全文，语言为本次解析结果，Origin 保留；CreatedAt 使用 TimeProvider.GetUtcNow()。结果按卡片顺序筛选成功项，包含缓存命中和截断项，并保留 Provider 名称、Model、Truncated。
3. 历史保存使用独立的 5 秒取消预算（TimeProvider 驱动），不再链接收尾点之后的用户取消。等待仓储实际结束；不通过 WaitAsync 超时抛弃仍在写库的任务。存储失败或该预算取消只记安全 Warning，不改变已成功卡片，也不阻止 Finished。
4. TranslationFinished 的成功/失败计数之和等于本次卡片数；缓存命中及截断属于成功。请求级错误、正常收尾点前取消或提前消费结束没有 Finished。

SQLite 同步 I/O 的取消存在粒度限制，5 秒是发出取消信号的预算，不承诺硬终止原生调用。租约最迟在 Finished 之前释放，且绝不能早于全部 Provider 子任务清理结束。

## 7. 依赖与集成验证

Core 单例 TranslationOrchestrator 依赖 Registry、Preprocessor、LanguageResolver、TemplateProvider、Renderer、Cache、HistoryRepository、TimeProvider、ILogger。AddIrisCore 重复注册幂等，默认 TimeProvider 可 TryAddSingleton；测试可替换为受控时钟。

T-013 不改 Provider、Registry、缓存或历史仓储已验收实现，不触碰 UI/动作/热键。除 Core 的受控单元测试，还在 App.Tests 使用真实 Core 注册、真实临时 Settings/SQLite/Cache 与假 Provider 跑一次多服务翻译→缓存命中→历史详情的纵向测试；这不涉及真实网络，也不代表 M2 用户界面已可用。
