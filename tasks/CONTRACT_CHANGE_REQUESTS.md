# Iris 契约变更记录

## CCR-001：ToErrorInfo 禁止回退到原始内部异常消息

| 字段 | 值 |
|---|---|
| 提出与批准 | 架构师 Codex，2026-09-28 |
| 状态 | **已落地，复审通过，已结案** |
| 落地工单 | T-010A / R1 |
| 验收提交 | 587484a4487188031d5d637141b4c430c9c18af6 |
| 来源 | ARCHITECTURE_AUDIT.md A01 |
| 影响 | IrisException.ToErrorInfo 的行为；不改变公开签名、ErrorCode 或序列化字段 |

现有实现会在 Detail=null 时使用 InnerException.Message。原始内部异常可能包含代理密码、API Key 或服务端回显内容，因此取消这个回退。

在下列两个文件中同步进行同一处替换：

- `specs/CONTRACTS.cs` 的 `Iris.Core.Errors.IrisException` 段；
- `src/Iris.Core/Errors/IrisException.cs`。

原定义：

```csharp
public ErrorInfo ToErrorInfo() =>
    new(Code, Message, Detail ?? InnerException?.Message, HttpStatus, Retryable, ProviderId);
```

批准的新定义：

```csharp
public ErrorInfo ToErrorInfo() =>
    new(Code, Message, Detail, HttpStatus, Retryable, ProviderId);
```

兼容性：显式 Detail 的行为不变；原先只靠内部异常提供 Detail 的调用点将返回 null。已知远程失败由 HttpErrorMapper 明确提供安全 Detail；不改 IrisException 构造函数，其他模块仍可持有需要的内部异常。

实施授权：T-010A 可将以上已批准定义原样同步到规范与落地文件，不需再次提交申请；不允许借此修改其他契约内容。为保持已验收 R2 工作区及契约一致性测试可复现，本记录签发时暂不改写 CONTRACTS.cs；两处在 T-010A 同步实施并提交。实施后将本条状态改为“已落地，待复审”，由架构师结案。

验收：Detail=null 且内部异常含假凭据时，ToErrorInfo().Detail 仍为 null；显式安全 Detail 和其他字段原样保留；ContractFidelityTests 通过。

2026-09-28 架构师单项复审：两处机械同步准确，公开签名未变，独立全量测试中的契约与行为测试通过，额外假凭据探针确认 null Detail 不回退。T-010A-R1 不重做此变更；整体验收仍需完成 URL 清理和匹配效率修复。

2026-09-28 最终复审：T-010A-R1 提交 587484a 通过，独立全量测试及假凭据探针通过，CCR-001 随工单结案。

## CCR-002：设置 DTO 与深度不可变发布快照的语义

| 字段 | 值 |
|---|---|
| 提出与批准 | 架构师 Codex，2026-09-28 |
| 状态 | **已落地，复审通过，已结案** |
| 验收提交 | 21f6224cd66ec0e07f9f127f388a7b9410336986 |
| 来源 | ARCHITECTURE_AUDIT.md A02；ARCH v1.14 §12.2 |
| 影响 | 仅契约注释；公开签名、属性类型、默认值、JSON schemaVersion 不变 |

原契约把 record 与 IReadOnly 集合视为不可变，但调用方仍可修改其 List/Dictionary。将保证明确到 ISettingsService 的发布边界，避免要求所有直接构造的 DTO 自动冻结。

T-010B 将以下已批准注释机械同步到 `specs/CONTRACTS.cs` 对应段及两个落地文件 `src/Iris.Core/Settings/AppSettings.cs`、`src/Iris.Core/Settings/ISettingsService.cs`。本记录签发时暂不改写两处定义，实施时同时同步；不需要再次申请批准。

1. AppSettings 的 summary 替换为：

```csharp
/// <summary>
/// 应用设置数据模型（record；with 创建新值，直接构造的 DTO 不自动冻结集合）。
/// ISettingsService 发布的 Current/Changed 快照会复制隔离全部可达集合并禁止修改。
/// 反序列化时缺失的字段取此处默认值，因此新增字段只要有合理默认值就无需迁移。
/// </summary>
```

2. 在 SettingsChangedEventArgs 类型之前增加：

```csharp
/// <summary>ISettingsService 发出的变更事件，其 OldSettings 与 NewSettings 均为深度不可变快照。</summary>
```

3. ISettingsService 的 Current 约束行替换为：

```csharp
/// - Current：永不为 null；LoadAsync 之前返回默认快照。任何时刻都满足规范化不变量（ARCH §12.2）。
///   同一已发布版本重复读取返回同一实例；全部可达集合已复制隔离且不可修改，不在 getter 中克隆。
```

4. 在 UpdateAsync 的“结果先规范化再写入”行之后增加：

```csharp
///   规范化后，在序列化及异步写盘之前捕获独立快照；不得保留调用方仍可改写的集合别名。
```

其余注释和现有异常、事件顺序约定不变。实施后将本条改为“已落地，待复审”，由架构师结案。

2026-09-28 最终复审：T-010B 提交 21f6224 通过，四处注释同步准确，独立全量测试与设置服务别名/持久化/失败路径探针通过，CCR-002 结案。

## CCR-003：翻译注册表租约与不可变凭据引用

| 字段 | 值 |
|---|---|
| 提出与批准 | 架构师 Codex，2026-09-28 |
| 状态 | **已结案**（2026-09-29：R2 实现 3be662d 与报告更正 0744d7b 复审通过，契约同步与运行时行为均已验收） |
| 设计依据 | specs/PROVIDER_REGISTRY.md（A03/A04） |
| 兼容性 | 替换尚无业务调用方的 Registry 原型接口；Profile 新增可空字段，旧 JSON 与 schemaVersion 不变；ITranslationProvider/Factory 本身的签名不变 |

由 T-011 同时修改 CONTRACTS.cs 与下列落地文件。此次属于已批准的明确接口调整，不需要开发再申请；禁止修改以下内容之外的契约。签发时仍保持现行契约与代码一致。

### 1. Profile 引用

在 `Settings/ProviderProfiles.cs` 的 TranslationProviderProfile 和 OcrProviderProfile 中，各在 Id 属性之后加入同一定义：

```csharp
/// <summary>null = 兼容旧的按 Profile.Id 存储的凭据；非 null = 精确的不可变凭据集引用。
/// 引用非法、缺失或内容损坏时视为未配置，禁止回退旧键。格式与发布协议见 PROVIDER_REGISTRY.md。</summary>
public string? CredentialSetId { get; init; }
```

在 `Security/ISecretStore.cs` 的 SecretKeys 中增加：

```csharp
public static string CredentialSet(string credentialSetId) => $"credential-set/{credentialSetId}";
```

ISecretStore 接口不变。这个方法只格式化键，引用验证由读取器负责。

### 2. 配置发布观察者（新增文件）

`src/Iris.Core/Settings/IProviderConfigurationObserver.cs`；在 CONTRACTS.cs 的 Core 设置部分新增相应 FILE 段：

```csharp
namespace Iris.Core.Settings;

/// <summary>配置提交后的发布屏障；刷新当前设置与凭据，成功返回后旧引用的解析工作已经结束。</summary>
public interface IProviderConfigurationObserver
{
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
```

### 3. 注册表快照模型（新增文件）

`src/Iris.Core/Translation/ProviderRegistryModels.cs`；对应 CONTRACTS.cs 使用块级 namespace：

```csharp
using Iris.Core.Settings;

namespace Iris.Core.Translation;

/// <summary>快照中的注册项。InstanceId 为实例代次；不可记录整个对象或其配置/凭据内容。</summary>
public sealed record TranslationProviderRegistration(
    TranslationProviderProfile Profile,
    ITranslationProvider Provider,
    Guid InstanceId);

/// <summary>拥有 Provider 使用权的快照。消费者须在子任务全部结束后释放，不能自行释放借出的 Provider。
/// Dispose 幂等；释放后访问属性抛 ObjectDisposedException；旧快照不受注册表后续重建影响。</summary>
public interface ITranslationProviderSnapshot : IDisposable
{
    AppSettings Settings { get; }
    IReadOnlyList<TranslationProviderRegistration> Providers { get; }
}
```

### 4. 替换尚未落地的 Registry 接口

在 `Translation/ITranslationProvider.cs` 中仅替换旧 Registry 的 summary 和接口；前三个 Provider/ModelCatalog/Factory 接口保持原样：

```csharp
/// <summary>按设置和凭据管理 Provider 实例。构造函数不加载设置、不创建实例；首次刷新在设置加载后进行。
/// 退休实例在最后一个快照释放后清理；Dispose 后拒绝新操作，已有快照仍可安全释放。</summary>
public interface ITranslationProviderRegistry : IProviderConfigurationObserver, IDisposable
{
    /// <summary>必要时刷新并原子获取快照。按 Order 稳定排序，包含已启用但未配置的项。</summary>
    Task<ITranslationProviderSnapshot> AcquireSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>以独立捕获的草稿及显式凭据创建单项快照；忽略 Enabled，不缓存、不写盘、不触发 Changed。</summary>
    Task<ITranslationProviderSnapshot> CreateTransientAsync(
        TranslationProviderProfile profile,
        ProviderCredentials credentials,
        CancellationToken cancellationToken = default);

    /// <summary>有效注册项、顺序、Descriptor 或实例代次变化后的通知；锁外触发，可合并，订阅方通过新快照读取状态。</summary>
    event EventHandler? Changed;
}
```

旧 GetEnabled/Find/CreateTransient 删除；代码检索确认当前没有业务实现或调用方，只有未实现 Orchestrator 的注释引用旧方法。

### 5. 配套注释同步

`Translation/ITranslationOrchestrator.cs` 的第 3 步，改为：

```csharp
/// 3) 使用 Registry.AcquireSnapshotAsync 获取快照；ProviderIds 为 null 时选择其中 IsConfigured 的 Provider，为空集合时抛 NotConfigured。
///    ProviderIds 指定但不存在/未配置的 → 对应 ProviderFailed(NotConfigured)。
```

在其取消规则之后增加：

```csharp
/// 快照租约须保持到子任务全部退出；正常、异常和消费者提前结束时都须完成子任务清理再释放。
```

`Ocr/IOcrProvider.cs` 中 IOcrProviderRegistry 的“行为同 ITranslationProviderRegistry”注释改为：

```csharp
/// <summary>OCR 注册表原型；T-030 落地前按 PROVIDER_REGISTRY.md 同步租约与发布屏障接口，当前不实现此原型。</summary>
```

OCR 原型签名暂不变，T-011 不实施 OCR 注册表。新增源文件须补齐 using；CONTRACTS.cs 沿用现有全局 using 与块级 namespace 格式。

实施后将本条标为“已落地，待复审”，由架构师结案。

## CCR-004：显式请求配置、PreparedPrompt 与有界缓存契约

| 字段 | 值 |
|---|---|
| 批准 | 架构师 Codex，2026-09-29 |
| 状态 | **已结案**（2026-09-29：T-012-R1 提交 2daec76 独立复审通过） |
| 依据 | specs/TRANSLATION_EXECUTION.md（A05、A12） |
| 兼容性 | 缓存尚无实现/业务调用方，可替换原型；三项配置服务保留旧入口；Provider 请求保留三参数构造；不改变设置 JSON 或 HTTP 线协议 |

由 T-012 同步修改下述 CONTRACTS.cs FILE 段及落地文件，精确内容如下。其余公开签名不变。

### 1. TranslationModels.cs

将 ProviderTranslateRequest 的单行 record 替换为（原 summary 保持）：

```csharp
public sealed record ProviderTranslateRequest(string Text, string SourceLanguage, string TargetLanguage)
{
    /// <summary>非 null 时 Provider 直接发送此已渲染值，不再查找/渲染模板；null 保留直接调用行为。
    /// 编排器从同一注册表快照准备此值，并使用同一请求计算缓存键。不得记录整个请求。</summary>
    public RenderedPrompt? PreparedPrompt { get; init; }
}
```

### 2. 三项显式配置重载

在 Languages/ILanguageDetector.cs 的 ITargetLanguageResolver 中，紧随旧 Resolve 增加：

```csharp
/// <summary>使用指定配置执行同一语言规则，不读取当前设置、不修改传入配置。</summary>
ResolvedLanguagePair Resolve(string text, string requestedSource, string? requestedTarget, TranslationSettings settings);
```

在 Translation/ITranslationOrchestrator.cs 的 ITextPreprocessor 中，紧随旧 Prepare 增加：

```csharp
/// <summary>使用指定配置执行同一预处理规则，不读取当前设置、不修改传入配置。</summary>
string Prepare(string raw, TextOrigin origin, TranslationSettings settings);
```

在 Translation/Prompts.cs 的 IPromptTemplateProvider 中，紧随旧 Get 增加：

```csharp
/// <summary>使用指定配置执行同一模板查找/回退规则，不读取当前设置、不修改传入配置。</summary>
PromptTemplate Get(string? templateId, TranslationSettings settings);
```

上述文件补充 `using Iris.Core.Settings;`，CONTRACTS.cs 沿用全局 using。旧重载每次捕获一次 Current.Translation 后委托显式重载；显式重载 settings 为 null 时抛 ArgumentNullException。

### 3. ITranslationCache.cs 全部类型

```csharp
using System.Diagnostics.CodeAnalysis;

namespace Iris.Core.Translation;

/// <summary>进程内缓存键；InstanceId 隔离配置/凭据代次，RequestFingerprint 是实际请求的完整 SHA-256。
/// 精确编码见 TRANSLATION_EXECUTION.md；不保存原文、Prompt、地址或凭据。</summary>
public readonly record struct TranslationCacheKey(string ProviderId, Guid InstanceId, string RequestFingerprint);

/// <summary>缓存记账预算，不等于整个进程或 CLR 实际分配量。</summary>
public static class TranslationCacheLimits
{
    public const long MaxTotalBytes = 16L * 1024 * 1024;
    public const long MaxEntryBytes = 1L * 1024 * 1024;
    public const long EntryOverheadBytes = 256;
}

/// <summary>进程内线程安全 LRU，受当前 CacheEnabled/CacheCapacity 与固定字节预算共同限制。
/// 不持久化；编排器只写完整成功结果。Dispose 幂等并取消订阅，之后操作抛 ObjectDisposedException。</summary>
public interface ITranslationCache : IDisposable
{
    /// <summary>当前清空代次，初始为 0；调用方开始请求时捕获，迟到写入必须携带原值。</summary>
    long Generation { get; }

    /// <summary>命中移至 MRU；未命中返回 false 且 text 为 null。</summary>
    bool TryGet(in TranslationCacheKey key, [NotNullWhen(true)] out string? text);

    /// <summary>原子校验 generation 并尝试写入；过期、禁用或超预算返回 false；null text 抛 ArgumentNullException。
    /// 准入/替换/淘汰规则见 TRANSLATION_EXECUTION.md，不得保留完整请求作为键。</summary>
    bool TrySet(in TranslationCacheKey key, string text, long generation);

    /// <summary>原子清空并递增 Generation，旧请求不能重新填入。</summary>
    void Clear();
}
```

### 4. 编排器注释同步

ITranslationOrchestrator 的 summary 全部替换为：

```csharp
/// <summary>
/// 翻译编排器（Core 内实现）。TranslateAsync 语义：
/// 1) 首次枚举借用 Registry 快照；预处理、语言、模板与执行选项全部使用 Snapshot.Settings。
///    预处理后空文本抛 IrisException(InputEmpty)；超过 MaxInputChars 抛 InputTooLong。
/// 2) ProviderIds 为 null 时选择快照中 IsConfigured 的 Provider；空集合或没有可用项时抛 NotConfigured。
///    显式指定但不存在/未配置的项产出对应 ProviderFailed(NotConfigured)。
/// 3) 在排队前准备每项 PreparedPrompt、实际请求缓存键与缓存 Generation；后续不读取动态设置重算。
/// 4) BypassCache=false 且 CacheEnabled 时查缓存；命中产出 ProviderStarted、ProviderCompleted(FromCache=true)。
///    否则调用流式接口，并发度不超过快照中的 MaxConcurrentProviders，事件由有界 Channel 合并。
/// 5) 单个 Provider 失败不影响其它项，转为 ProviderFailed；失败/取消/截断不写缓存。
/// 6) 全部子任务结束后，未截断成功值以原 Generation 尝试写缓存；BypassCache 只跳过读取。
///    RecordHistory 且快照 History.Enabled 且至少一个成功时写历史，写入失败只记安全日志。
/// 7) 调用取消或消费者提前结束时取消并等待全部子任务退出，再释放租约；调用取消抛 OperationCanceledException，不写历史。
/// 请求级错误在首次 MoveNextAsync 抛出。精确选择、事件与资源边界见 TRANSLATION_EXECUTION.md 和 T-013 工单。
/// </summary>
```

本单不实现 Orchestrator。实施后将 CCR-004 标为“已落地，待复审”，由架构师结案。

## CCR-005：历史完整性、摘要分页与查询契约

| 字段 | 值 |
|---|---|
| 批准 | 架构师 Codex，2026-09-29 |
| 状态 | **已结案**（2026-09-29：T-034-R2 提交 2be09c3 独立复审通过） |
| 依据 | HISTORY_STORAGE.md、DB_SCHEMA.sql V2；架构审查 A06 与历史语义遗留 |
| 兼容性 | 当前没有历史仓储/UI 业务实现；Query 原型可调整。HistoryResult 保留原位置构造，旧 V1 数据通过 V2 升级；设置字段/默认值不变 |

T-034 同步修改 CONTRACTS.cs 与下述两个 Core 落地文件。此处给出准确签名与注释，不要求再申请；不得顺带变更其它契约。

### 1. Storage/History.cs

将 HistoryResult 替换为：

```csharp
public sealed record HistoryResult(string ProviderId, string ProviderName, string? Model, string Text)
{
    /// <summary>该结果因输出上限截断；历史展示和复制时不得当作完整译文。</summary>
    public bool Truncated { get; init; }
}
```

HistoryEntry 保持全部成员与默认值不变。紧随 HistoryEntry 增加：

```csharp
/// <summary>历史列表摘要，不包含完整原文/译文；展开、复制或重译前使用 GetAsync 取得详情。</summary>
public sealed record HistorySummary(
    long Id,
    DateTimeOffset CreatedAt,
    TextOrigin Origin,
    string SourcePreview,
    string SourceLanguage,
    string TargetLanguage,
    bool IsFavorite,
    int ResultCount,
    bool HasTruncatedResults);
```

HistoryQuery 的位置签名不变，仅将 param 注释改为：

```csharp
/// <param name="Keyword">null/空白 = 不过滤；Trim 后按字面量子串搜索，Unicode 标量数 >= 3 用 FTS5，否则用转义 LIKE；NUL 非法。</param>
/// <param name="Offset">非负偏移；结果按 created_at DESC,id DESC 排序。</param>
/// <param name="Limit">每页 1..200 条，默认 50；只返回摘要。</param>
public sealed record HistoryQuery(string? Keyword = null, bool FavoritesOnly = false, int Offset = 0, int Limit = 50);
```

完整替换 ITranslationHistoryRepository 的 summary 和接口为：

```csharp
/// <summary>
/// 翻译历史（Infrastructure，SQLite），规则见 HISTORY_STORAGE.md，DDL 见 DB_SCHEMA.sql。
/// AddAsync 在调用边界捕获数据；只对最新且未收藏、原文与语言对完全相同的记录去重，返回实际 Id。
/// 结果/索引/修剪同事务，收藏不被隐式替换；MaxEntries 限制非收藏条数，收藏额外保留。
/// 所有数据库 I/O 在后台执行；提交前取消回滚，提交后不伪报取消；存储异常安全映射为 StorageFailure。
/// </summary>
public interface ITranslationHistoryRepository
{
    Task<long> AddAsync(HistoryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>同一读快照返回 Total 和摘要页；SourcePreview 最多 200 个 Unicode 标量，超出追加省略号。</summary>
    Task<PagedResult<HistorySummary>> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default);

    /// <summary>取得完整详情与有序结果，正 id 不存在时返回 null；不使用摘要代替全文。</summary>
    Task<HistoryEntry?> GetAsync(long id, CancellationToken cancellationToken = default);

    Task SetFavoriteAsync(long id, bool isFavorite, CancellationToken cancellationToken = default);

    Task DeleteAsync(long id, CancellationToken cancellationToken = default);

    Task ClearAsync(bool keepFavorites, CancellationToken cancellationToken = default);
}
```

IDatabaseInitializer 接口签名不变，summary 替换为：

```csharp
/// <summary>后台创建/顺序迁移历史库（PRAGMA user_version），幂等且并发安全；失败可重试，未来版本/损坏库不覆盖。</summary>
```

### 2. Settings/AppSettings.cs

仅在 HistorySettings.MaxEntries 之前增加：

```csharp
/// <summary>最多保留的非收藏记录条数，收藏额外保留；写入及取消收藏后修剪。</summary>
```

### 3. 数据与依赖授权

DB_SCHEMA.sql 已批准追加 V2（is_truncated 与索引），保留 V1 用于升级。允许 Infrastructure 增加 Microsoft.Data.Sqlite 10.0.12、Dapper 2.1.89 的包引用及中央版本，两者均在现有白名单。T-034 只执行明确版本的迁移，不任意修改已存在用户数据库。

实施后将 CCR-005 标为“已落地，待复审”，架构师独立验收后结案。

## CCR-006：编排器事件、选择与正常收尾语义

| 字段 | 值 |
|---|---|
| 批准 | 架构师 Codex，2026-09-29 |
| 状态 | **已结案**（2026-09-30；T-013-R1 提交 99787b8 独立复审通过） |
| 依据 | TRANSLATION_ORCHESTRATOR.md；T-012/T-034 已提供真实依赖 |
| 兼容性 | 不变更公开签名、事件字段、默认值或 JSON；只细化尚未实现编排器的注释和语义 |

由 T-013 同步下列注释到 CONTRACTS.cs 与落地文件，保留其它定义。

### 1. Translation/ITranslationOrchestrator.cs

将 ITranslationOrchestrator 的 summary 全部替换为：

```csharp
/// <summary>
/// 翻译编排器（Core）。精确规则见 TRANSLATION_ORCHESTRATOR.md。
/// 首次枚举检查取消、捕获请求与 ProviderIds，并在首次 await 前捕获缓存 Generation；随后借用 Registry 快照。
/// 预处理、语言、模板及执行开关使用快照，排队前固定 PreparedPrompt 与缓存键；空输入/超长输入在首次枚举抛出。
/// ProviderIds=null 选已启用且已配置项，无可用项或显式空集合抛 NotConfigured；显式非空列表精确去重并保持请求顺序。
/// 显式不存在/禁用/未配置项仍产出失败卡片。每项 Started→Delta*→Completed/Failed；不同项互不因单项失败取消。
/// 真实调用并发度不超过快照配置，事件用有界背压传递；缓存命中不调用 Provider，BypassCache 只跳过读取。
/// 全部子任务退出并读尽事件、最后取消检查通过后进入正常收尾；此前取消抛 OCE，提前结束消费取消并等待子任务，均不写本次缓存/历史。
/// 正常收尾阶段将新鲜完整成功值以原 Generation 尝试缓存，按 RecordHistory/快照 History.Enabled 条件保存成功结果（含截断标记）。
/// 收尾后的迟到取消不撤回既定完成结果；历史保存有独立取消预算，失败只记安全日志，最后产出 TranslationFinished。
/// 请求级错误/正常收尾前取消/提前结束没有 Finished；租约在全部 Provider 清理后、Finished 前释放。
/// </summary>
```

ITextPreprocessor 及其重载保持原样。

### 2. Translation/TranslationModels.cs

TranslationRequest.ProviderIds 的注释替换为：

```csharp
/// <summary>null = 所有已启用且已配置项；空集合抛 NotConfigured；非空列表按序数精确去重并保持首次出现顺序。
/// 不存在/禁用/未配置项生成失败卡片；首次枚举复制列表，之后外部修改不影响请求。</summary>
```

TranslationEvent 之前的顺序注释替换为：

```csharp
// 正常顺序：TranslationStarted 唯一最先；每项 ProviderStarted → ProviderDelta* →
// (ProviderCompleted | ProviderFailed)，TranslationFinished 唯一最后。不同 Provider 事件可交错。
// 请求级错误、正常收尾前取消及消费者提前结束不产生 TranslationFinished。
```

不新增公开限制类型，固定内部输出/队列/历史超时常量以 TRANSLATION_ORCHESTRATOR.md 为准。实施后将 CCR-006 标为“已落地，待复审”。

## CCR-007：动作分发的渐进注册与完整异步生命周期

| 字段 | 值 |
|---|---|
| 批准 | 架构师 Codex，2026-09-30 |
| 状态 | **已结案**（2026-09-30；T-014 提交 e61fb8d 独立复审通过） |
| 依据 | ARCH v1.19 §6.1；架构审查 A06 的渐进注册问题 |
| 兼容性 | 只修改 Actions/IrisAction.cs 中两个接口的 XML 注释，不改任何签名、枚举值或默认参数 |

由 T-014 同步修改 `specs/CONTRACTS.cs` 与 `src/Iris.Core/Actions/IrisAction.cs`，签发时保持两处旧定义一致。其它注释与定义保持原样。

IActionHandler 的 summary 替换为：

```csharp
/// <summary>每个已开放的 IrisAction 恰好一个 Handler；允许只注册当前已实现的动作，禁止重复或未定义的 Action。
/// DI 注册多个 IActionHandler，Dispatcher 按 Action 建索引；业务 Handler 在 Core/Workflows 实现。</summary>
```

IActionDispatcher 的 summary 替换为：

```csharp
/// <summary>
/// 动作分发（App 层实现），完整规则见 ARCH §6.1。
/// 可从任意线程调用；在 UI 线程启动 Handler，等待其完整 Task；构造时不要求 WPF Application 已存在。
/// 在 UI 排队前原子防重入：同一 Action 执行中再次触发立即忽略；ScreenshotTranslate/ScreenshotOcr/SilentScreenshotOcr 互斥。
/// 不同非截图动作可重叠；执行权覆盖排队和完整异步执行，所有退出路径均释放；忽略调用不取消已有调用。
/// 进入时已取消或 Handler 启动前取消则不执行；开始后传递原令牌并等待实际退出，OCE 静默。
/// 未定义 Action/null context 提示 InvalidArgument；已定义但未注册的 Action 提示功能尚未开放。
/// 调度/Handler 异常在边界处理：IrisException 使用错误码默认文案，未知异常使用 Unknown 文案；仅记录安全元数据。
/// 不记录 Payload 或原始异常；通知失败只安全记录，不递归通知。窗口会话与旧结果替换策略不属于 Dispatcher。
/// </summary>
```

不修改 INotificationService、IrisException 或全局异常处理契约。App 的 UI 调度适配器保持 internal。实施后只将 CCR-007 改为“已落地，待复审”，BACKLOG 由架构师结案。

## CCR-008：剪贴板格式、重试与取消边界

| 字段 | 值 |
|---|---|
| 批准 | 架构师 Codex，2026-09-30 |
| 状态 | **已结案**（2026-09-30；e746382 独立复审通过，原生独占窗口站实机项仍按已批准边界标为未验证） |
| 依据 | CLIPBOARD_SERVICE.md；M2 剪贴板前置依赖 |
| 兼容性 | 只细化尚未实现的 IClipboardService 注释，不改签名、错误码、默认参数或 ImageData |

由 T-021A 同步修改 CONTRACTS.cs 与 src/Iris.Core/Platform/IClipboardService.cs；其它内容保持不变。

接口 summary 替换为：

```csharp
/// <summary>
/// 线程安全的 Win32 剪贴板服务，异步操作在后台串行执行；完整规则见 CLIPBOARD_SERVICE.md。
/// OpenClipboard 最多尝试 10 次，首次立即执行、失败之间等待 30ms；耗尽抛 IrisException(ClipboardBusy)。
/// 等待和写入提交前可取消；EmptyClipboard 成功后完成发布和清理，不以迟到取消撤销既定结果。
/// 操作完成前等待真实原生调用退出；不依赖 WPF/STA，不在持有剪贴板时 await。
/// </summary>
```

四个方法分别增加以下 summary（按原方法顺序）：

```csharp
/// <summary>读取 CF_UNICODETEXT；无该格式返回 null，合法空文本返回空串。按原生块边界读取，格式无效抛 InvalidResponse，超限抛 InputTooLong。</summary>
/// <summary>写入 CF_UNICODETEXT，保留原文；null/内嵌 NUL 抛 InvalidArgument，超过 4,194,304 个 UTF-16 code unit 抛 InputTooLong。</summary>
/// <summary>解码 PNG/JPEG/BMP 并立即发布 CF_DIBV5；格式/尺寸矛盾抛 ImageInvalid，超出规范预算抛 ImageTooLarge。调用完成前不得修改 Bytes。</summary>
/// <summary>返回当前窗口站原生 uint 序列号（含 0），不打开剪贴板；可能回绕，只以相等性判断变化。</summary>
```

其它原生失败按 StorageFailure，且不暴露文本、图像内容或原始异常链。只使用现有 Windows SDK / CsWin32，不批准新增 NuGet 包。实施后将 CCR-008 标为“已落地，待复审”；BACKLOG 只由架构师结案。

## CCR-009：桌面快照、DPI、前台与最近显示器语义

| 字段 | 值 |
|---|---|
| 批准 | 架构师 Codex，2026-09-30 |
| 状态 | **已结案**（2026-09-30；ecee697 独立复审通过；多屏/混合 DPI 实机范围按报告保留未验证） |
| 依据 | DESKTOP_CONTEXT.md；ARCH v1.21；桌面准备稿与官方 API 核查 |
| 兼容性 | 仅修改 Platform/DesktopModels.cs 中 MonitorInfo、ForegroundAppInfo、IDesktopContext 的注释，不改签名/默认值/类型，不改 IAutoStartService |

T-022A 同步到 CONTRACTS.cs 与 src/Iris.Core/Platform/DesktopModels.cs。签发时两处保持旧定义一致，避免只有规范改动导致契约测试提前漂移。

MonitorInfo 注释替换为：

```csharp
/// <summary>显示器只读快照；Bounds/WorkArea 均为虚拟桌面物理像素，可含负坐标。</summary>
/// <param name="DeviceName">当前运行时设备名，如 \\.\DISPLAY1；不保证跨硬件变更稳定。</param>
/// <param name="DpiScale">本显示器 DPI/96.0，有限且大于零；1.0 = 96 DPI。</param>
```

ForegroundAppInfo 增加：

```csharp
/// <summary>短暂的前台窗口快照；后续输入操作须重新确认目标。</summary>
/// <param name="ProcessName">映像文件名，含实际扩展名（通常 .exe）、不含路径；比较使用 OrdinalIgnoreCase。</param>
/// <param name="WindowTitle">可空的顶层标题，最多 1,024 字符；不作为日志内容。</param>
```

IDesktopContext 的 summary 与四个方法注释依次替换/增加为：

```csharp
/// <summary>线程安全的桌面环境查询（Platform.Windows）；完整规则见 DESKTOP_CONTEXT.md。
/// 构造不访问桌面；每次采集清理自身资源并恢复线程 DPI context；不变更显示配置或激活窗口。</summary>

/// <summary>取得前台快照；无窗口、来源不可读或两次采样仍不稳定时返回 null。标题可空，不影响已识别进程。</summary>
/// <summary>取得光标物理像素坐标；失败抛 IrisException(Unknown)。不移动光标。</summary>
/// <summary>返回新建的只读显示器快照，排除未接入桌面及镜像伪设备，按主屏优先、DeviceName Ordinal 排序。
/// 不一致快照最多重采一次；仍失败或没有有效显示器抛 IrisException(Unknown)，不伪造默认屏幕/DPI。</summary>
/// <summary>从同一有效快照选择包含该点或距该点最近的显示器；采用点到矩形所含整数像素的平方距离。
/// 并列优先主屏，再按 DeviceName Ordinal；边界为半开区间，极端坐标运算不溢出。</summary>
```

方法声明及 record 参数保持原样，具体失败/清理文案见正式规范。不新增平台错误码或公共原生适配器。实施后将 CCR-009 标为“已落地，待复审”，架构师验收后结案。

## CCR-010：设置窗口生命周期与开机启动行为

| 字段 | 值 |
|---|---|
| 批准 | 架构师 Codex，2026-09-30 |
| 状态 | **已验收结案**（2026-10-01，11d4ac2；局部回滚和既有退出反例通过，独立全量 1328 成功/1 环境跳过） |
| 依据 | SETTINGS_EDITING.md；ARCH v1.22 |
| 兼容性 | 仅细化 ISettingsWindowPresenter、IAutoStartService 注释，不改签名、默认值或配置字段；保存结果/预览服务保持 App internal |

T-018 同步修改 CONTRACTS.cs 与对应 Core 落地文件；签发时两处旧定义保持一致。

Presentation/PresentationPorts.cs 中 ISettingsWindowPresenter 的 summary 替换为：

```csharp
/// <summary>设置窗口（App 实现），可从任意线程调用，内部切换 UI 线程；构造不创建窗口。
/// 重复显示复用当前窗口和草稿，关闭后释放其会话，再次显示创建新会话；ShowAsync 不等待窗口关闭。
/// pageKey：general / translation / ocr / hotkeys / selection / screenshot / floatingball / network / history / about。
/// null、未知或尚未开放的 pageKey 转到 general；不记录原始 pageKey。完整编辑/保存规则见 SETTINGS_EDITING.md。</summary>
```

Platform/DesktopModels.cs 中 IAutoStartService 的 summary 替换为，并分别为方法增加注释：

```csharp
/// <summary>管理 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 的 Iris 值；构造不访问注册表。
/// 同步平台方法，由调用方在后台执行；只管理本值，不改 RunOnce/StartupApproved 或系统策略。
/// 路径/命令与安全错误规则见 SETTINGS_EDITING.md；读写失败抛 IrisException(StorageFailure)。</summary>
```

```csharp
/// <summary>检查本应用预期 apphost 命令是否匹配注册；缺失或不匹配为 false，不保证 Windows 实际执行。</summary>
```

```csharp
/// <summary>启用时写带引号的 apphost 命令；已匹配时无操作。禁用只移除 Iris 值，缺失时无操作。</summary>
```

不改变 ISettingsService、IActionDispatcher、IAppLifetime；正常退出的设置等待使用 App 内部接线。不批准 T-019 凭据写入端口，本单不实现它。实施后 CCR-010 改为“已落地，待复审”，BACKLOG 由架构师结案。

## CCR-011：配置编辑协调端口、提交回执与模型发现前置条件

| 字段 | 值 |
|---|---|
| 批准 | 架构师 Codex，2026-10-01 |
| 状态 | **已验收结案**（2026-10-01，7e993b3；完整回归 1369 成功/1 环境跳过，操作校验与既有安全/捕获反例通过） |
| 依据 | PROVIDER_EDITING.md；PROVIDER_REGISTRY.md §2.2；ARCH v1.23 |
| 落地范围 | T-019A 的 Core 协调器与两种 Provider 模型列表 guard；T-019B 再接 UI |
| 兼容性 | 不改 settings.json/schemaVersion、现有字段/默认值、SecretStore、Registry 或 Provider 方法签名 |

已由架构师将以下纯契约声明同步至 CONTRACTS.cs 和相应 Core 文件，准确签名以 CONTRACTS.cs 为准：

- SettingsEditingModels.cs：SettingsFieldEdit、GeneralSettingsPatch、CredentialEditKind、ProviderCredentialEdit、TranslationProfileChangeKind、TranslationProfileChange、TranslationOrderChange、SettingsEditPlan、TranslationProviderDraft、SettingsEditStatus、ISettingsApplyReceipt、SettingsEditResult、SettingsApplyResult。
- ISettingsEditCoordinator.cs：CommitAsync、RetryApplyAsync、TestDraftAsync、ListDraftModelsAsync。
- ITranslationProvider.cs 的 IModelCatalogSource 注释：模型发现只依赖端点/凭据条件，不要求已选 Model；Descriptor.IsConfigured 仍表示翻译配置资格。签名不变，具体 guard 由 T-019A 实现。

协调器构造无 I/O，Core 单例；一次 UpdateAsync 提交常规补丁和 Provider 改动，提交后观察者屏障/清理失败返回已提交回执。回执不持有凭据或配置 JSON，重试只刷新当前配置并清理仍无引用的候选，不能恢复旧草稿。

模型发现不再沿用“缺少模型就是不能列模型”的旧 guard；这解决已有 SuggestedModel=null 的首次配置流程。两种 Provider 继续拒绝非法端点/远程缺 Key，保留本地免 Key、HTTP/SDK、超时/取消、分页与安全错误语义，不修改预设或臆造模型。

实现者不得自行修改这些已批准签名。T-019A 完成后标为“已落地，待复审”；T-019B 的 UI/系统应用接线不等同此单已实现。

声明验证（2026-10-01）：新增声明后的解决方案 Release 构建 0 警告/0 错误（19.82 秒），架构套件 8 成功/0 失败/0 跳过。完整 CONTRACTS.cs 的独立 net10.0/BCL 编译最初暴露既有跨 namespace 的 Settings 类型缺少顶层 using，已补 `using Iris.Core.Settings;`（不改签名）；随后独立编译 0 警告/0 错误（1.95 秒）。项目位于系统临时目录 iris-t019-contract-check，仅编译该契约文件。这些检查不构成尚未实现的编辑协调器业务通过证据。

实现验收（2026-10-01）：T-019A-R3 / 7e993b3 的业务实现已通过独立回归，CCR-011 结案。App 服务设置页和系统应用合并由 T-019B 继续，不把 A 的完成视为全窗口 UI 已交付。

## CCR-012：DeepSeek 新建预设建议值更新

| 字段 | 值 |
|---|---|
| 批准 | 架构师 Codex，2026-10-01 |
| 状态 | **已验收结案**（2026-10-02，e55695a；T-019B-R6 独立复审通过） |
| 依据 | PROVIDER_SETTINGS_UI.md §8；T-019B_PRESETS_PREPARATION.md；本日再次读取 DeepSeek 官方入门文档 |
| 兼容性 | 仅改变新建预设的 BaseUrl/SuggestedModel；不改类型、签名、schemaVersion，不迁移已有 Profile |

TranslationProviderPresets.All 中 deepseek 一行批准为：

```csharp
new("deepseek", "DeepSeek", TranslationProviderKind.OpenAICompatible, "https://api.deepseek.com", null, true),
```

官方 [DeepSeek 入门](https://api-docs.deepseek.com/) 当前列出 `base_url (OpenAI) https://api.deepseek.com` 和 deepseek-flash 示例。null 建议模型是 Iris 的选择策略：通过已交付的模型发现或手动输入选型号，不把一个会变化的具体型号固定成永久默认。没有据此认定旧 /v1 地址或 deepseek-chat 必然失效，现有用户配置不改。

其余预设不变，claude-opus-5 经官方模型状态表复核仍为 Active。两份预设声明由架构师同步；T-019B 不另建 UI 预设表。App 的回执保留/容量等规则属于内部实现，不新增公开契约。

同步后的验证：`dotnet test tests/Iris.ArchitectureTests/Iris.ArchitectureTests.csproj -c Release` 实际 8 成功、0 失败、0 跳过，包含契约文件与 Core 落地声明一致性。此检查不代表尚未实现的 B 页面已验收。

落地实施（2026-10-02）：已在 T-019B 中接入 UI 预设列表及相关草稿初始逻辑，`ArchitectureTests` 8 项全绿，全量测试已覆盖新建、排序与联合保存。待复审结案。

独立复审（2026-10-02）：f8a59ba 的预设声明保持批准值，但设置页存在草稿/凭据丢失、队列永久停止、保存捕获和旧结果隔离等阻断项，签发 T-019B-R1。上述落地说明不等同验收，CCR-012 保持待复审；完整证据见 tasks/done/T-019B.md。

R1 复审补记（2026-10-02）：旧反例通过，保存后绑定、Prompt、队列重入与异步 UI 收尾仍不合格，签发 R2；预设值不变，CCR-012 待完整设置页复审后结案。

R2 复审补记（2026-10-02）：57c954f 的既有对照改善确认；同步启动屏障、模板删除选择与冻结期间外部合并仍未闭合，签发 R3。预设声明不再改动，CCR-012 保持待复审。

最终复审（2026-10-02）：e55695a / T-019B-R6 通过，预设仍为批准值，CCR-012 结案；完整独立证据见 tasks/done/T-019B-R6.md §6。

## CCR-013：翻译窗口操作租约与有效发布

| 字段 | 值 |
|---|---|
| 批准 | 架构师 Codex，2026-10-02 |
| 状态 | **已落地，待复审** |
| 依据 | ARCH v1.25 / TRANSLATION_WINDOW.md；A07 的迟到结果、隐藏后重开及重试归属问题 |
| 范围 | 仅 PresentationPorts.cs 中翻译窗口相关端口，准确声明同步 CONTRACTS.cs |
| 兼容性 | 仓库目前没有 ITranslateWindowPresenter 实现或调用方，移除匿名 ShowAsync；不改配置/事件/Provider/动作契约 |

新增：

```csharp
public interface ITranslateWindowOperation : IDisposable
{
    Guid Id { get; }
    CancellationToken CancellationToken { get; }
}
```

ITranslateWindowPresenter 保留 IsVisible、IsActive、HideAsync；以以下方法替代匿名 ShowAsync：

```csharp
ITranslateWindowOperation? TryBeginOperation(CancellationToken cancellationToken = default);
Task<bool> TryShowAsync(ITranslateWindowOperation operation, TranslateWindowRequest request);
```

TryBeginOperation 在已取消令牌下抛 OCE 且不淘汰当前意图；关闭/容量耗尽返回 null。操作状态（含失效但未回收）最多 8 个；不在状态锁内调用取消回调。TryShowAsync 只接受所属 Presenter 的原租约，UI 实际执行时校验；失效/取消/已 Dispose 返回 false，null/外来参数抛 ArgumentException，同操作并发发布抛 InvalidOperationException。true 只表示发布接受。

租约 Dispose 归还调用方持有，不能取消已由 VM 接管的工作；调用方令牌继续生效。真实任务、取消回调和 registration 全部退出后才能释放状态。准确引用计数/取消实现属于 App internal，必须经 T-015 实际实现的受控竞态测试验证，不能用接口编译代替。

TranslateWindowRequest 只改 AutoTranslate 文档：本次显式非空白 Text、无 Error/加载状态才可自动翻译，同操作至多一次；null 保留但不自动提交，空字符串清空。默认值、字段、JSON schema 不变。HideAsync 不等待不合作 I/O，应用退出使用内部完整屏障。

这是签发阶段的设计批准和声明同步；待业务实施后改“已落地，待复审”，只有架构师独立验收才能结案。

签发阶段验证（2026-10-02，声明同步后的工作区）：

`dotnet build Iris.slnx -c Release` 原始摘要：

```text
已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:15.54
```

`dotnet test Iris.slnx -c Release` 原始摘要（包含契约一致性检查）：

```text
  总计: 1402
  失败: 0
  成功: 1401
  已跳过: 1
  持续时间: 14s 173ms
```

跳过仍为隔离剪贴板窗口站 error=183。另 `dotnet build C:/Users/28910/AppData/Local/Temp/iris-t019-contract-check/Contracts.csproj -c Release` 单独编译完整 CONTRACTS.cs（net10.0 / BCL）原始摘要：

```text
已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:01.86
```

这些检查只证明新声明一致且不破坏已交付代码；T-015 窗口、租约所有权、预算和退出行为尚未实现/验证。
