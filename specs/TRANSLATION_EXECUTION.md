# 翻译请求快照与缓存

> 版本 1 · 2026-09-29 · 架构师已批准。T-012 落地缓存和显式配置/Prompt 入口，T-013 将其接入编排器。精确契约变更见 CCR-004；不改变 HTTP/SDK 线协议。

## 1. 同一次请求使用同一配置

编排器在首次枚举时借用 Registry 快照，并持有到全部子任务结束。此后预处理、语言决策、模板查找、并发度、输入上限与历史开关均取该快照。不能各自再次读取 ISettingsService.Current，不能在等待 Provider 并发名额后重新取模板。

T-012 为 ITextPreprocessor、ITargetLanguageResolver、IPromptTemplateProvider 增加接收 TranslationSettings 的重载。旧入口每次捕获一次当前 TranslationSettings，再委托同一逻辑；显式入口只用传入值，不读取设置服务、不修改该对象。既有换行、语言规范化、模板回退与单遍替换规则不变。

对每项注册信息：从快照查到实际模板，渲染得到不可变 RenderedPrompt，再创建 ProviderTranslateRequest 并设置 PreparedPrompt。键计算与 Provider 调用必须使用同一个请求实例/相同不可变值。

- 两个已实现的 Provider：PreparedPrompt 非 null 时直接用该 System/User，不调用模板服务或 Renderer；null 时保留既有直接调用/TestAsync 行为。
- 这个属性不是缓存标记，也不影响配置检查、调用惰性、取消、重试、流解析、日志脱敏或 Anthropic 回退。空 System 仍按既有线协议省略。
- T-012 不声称已有完整编排请求快照；只有 T-013 正式接入后才闭合整条请求。禁止将全局状态、AsyncLocal 或临时替换单例设置作为传递配置的方式。

## 2. 缓存键

TranslationCacheKey 只保存 ProviderId、InstanceId、RequestFingerprint。

- InstanceId 来自借用的注册项，配置/凭据导致实例重建时必变；同一实例复用时不变。不能使用 Profile.Id 代替代次，不能把凭据或凭据哈希放进键。
- RequestFingerprint 为实际请求五个字符串的完整 SHA-256 小写十六进制：Text、SourceLanguage、TargetLanguage、PreparedPrompt.System、PreparedPrompt.User，按此顺序。
- 编码为每字段一个四字节无符号 little-endian UTF-16 code unit 数量，后接各 code unit 的两个 little-endian 字节。保留嵌入 NUL 和孤立代理项，不依赖系统编码或字符串连接分隔符。不截断哈希；长度前缀避免字段边界歧义。
- Core 内部的 TranslationCacheKeyFactory.Create(registration, request) 统一构造键；缺少 PreparedPrompt 时抛安全 ArgumentException，不降级到动态模板。无需公开新服务接口。
- 原 PromptFingerprint 的模板拼接/截断算法不再用于缓存，T-012 删除该无生产调用方的 internal 帮助类型，并将相应用例替换为实际请求键用例。
- 键不保留原文、译文、渲染 Prompt、BaseUrl、Header 或凭据；不在日志中记录请求或整个键。键中的随机代次无持久化需求。

因此同一 Profile 改地址/模型/温度/选项/凭据后不会读到旧代结果；模板改变而 Provider 复用时，实际渲染变化也会改变键。旧请求晚完成只能写入自身旧键，不能污染新执行配置。

## 3. Clear 与迟到写入

ITranslationCache 暴露 Generation 和 TrySet(key, text, generation)。编排器在开始一次请求时捕获 Generation；完成后用该值写入。缓存内部同锁检查代次、容量及字节预算，代次不符即拒绝，不能先检查再锁外写入。

- 初始 Generation = 0；Clear 原子清空并递增 Generation，即使原本为空也递增。
- CacheEnabled 或 CacheCapacity 从可用变为不可用、以及从不可用恢复可用时，清空并递增 Generation；只调整正容量时按 LRU 修剪，不必全部清空。
- 一次清空后，旧请求 TrySet 必须返回 false，且不得改变新条目、MRU 顺序或其它统计。
- Generation 仅负责缓存清空边界；它不代替 Provider 实例代次与实际请求指纹。
- BypassCache=true 跳过读取，但成功未截断结果仍可尝试更新缓存；CacheEnabled=false 时不读、不存。失败、取消、截断结果由编排器禁止写缓存。

## 4. 内存、LRU 与设置生效

固定首期预算由 TranslationCacheLimits 常量定义：总记账预算 16 MiB，单条记账预算 1 MiB，每条结构开销记账 256 字节。不新增设置字段/迁移或第三方包；UI 后续可说明固定预算。

单条成本 = 256 + 2 × (ProviderId.Length + RequestFingerprint.Length + text.Length)，用 long 计算。256 包含本设计对键/Guid/链表/哈希槽等结构的保守记账，**不是 CLR 精确分配测量或整个进程内存承诺**。条数仍受规范化后的 CacheCapacity 限制，两种限制同时满足。

- TryGet 命中移至 MRU；miss 返回 false/text=null。TrySet 成功插入/覆盖为 MRU，然后从 LRU 尾部淘汰，直到条数与总预算都满足；替换时正确扣除旧成本。
- 超过单条或总预算的单项写入返回 false；若同键旧条目存在，仅删除该旧条目，不能为一个不可能接纳的大结果逐出无关条目。过期 generation 的写入优先拒绝，连同键旧条目也不能删除。
- null text 抛 ArgumentNullException；空字符串可以缓存。内部键工厂产生的正常键是使用边界，无需替调用方推导 Provider 配置。
- CacheEnabled=false 或 CacheCapacity=0：为空，TryGet miss、TrySet false。缓存监听设置变化，相关变更返回后预算已生效；每次公开操作也校准最新设置，覆盖缓存在 Load 前构造、Load 后首次使用的情况。无关设置变化不清空、不改变 Generation。
- 所有字典/链表/计数/Generation 更新线程安全；锁内不调用 Provider、网络、磁盘、用户回调或记录请求数据。设置 Current 是已发布不可变对象，可读取后在缓存锁内按最新版本一致应用，不允许较旧的事件参数覆盖最新设置。
- 缓存为 Core 单例、实现 IDisposable 以取消事件订阅；Dispose 幂等，清空数据并拒绝后续操作（ObjectDisposedException）。AddIrisCore 重复调用不能增加缓存实例或重复订阅。

## 5. T-013 接线约定

1. 首次枚举借用快照，使用显式重载预处理并检查输入上限、解析语言，再选择 Provider。所有请求级失败仍在首次 MoveNextAsync 抛出。
2. 在子任务排队之前固定每项 PreparedPrompt 与 CacheKey，同时捕获缓存 Generation。共享的 Provider 自身不变，不做每请求重建。
3. 有界 Channel（首期容量 128，FullMode=Wait）传递事件，终态不可丢；取消/消费者提前结束同时取消阻塞写入、等待子任务退出再释放租约。截断只影响该卡片，完整成功值才写缓存。
4. 历史写入需要真实仓储。T-034 保留原编号但前移到 T-012 之后、T-013 之前，不用静默丢数据的生产占位仓储满足 DI。

ProviderIds 精确选择、错误卡片、有界流、取消与正常收尾的完整行为已在 TRANSLATION_ORCHESTRATOR.md / CCR-006 定稿。缓存 Generation 在首个 await 前捕获；正常收尾点以前的取消/提前结束不写缓存或历史，收尾点以后的迟到取消不撤回已完成结果。
