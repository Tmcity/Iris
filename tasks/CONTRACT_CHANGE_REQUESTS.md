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
| 状态 | **已落地，待 T-011-R1 复审**（2026-09-29：屏障/租约验收未通过，接口签名不需变更） |
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
