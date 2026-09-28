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
| 状态 | **已落地，待复审** |
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
