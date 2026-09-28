# Iris 契约变更记录

## CCR-001：ToErrorInfo 禁止回退到原始内部异常消息

| 字段 | 值 |
|---|---|
| 提出与批准 | 架构师 Codex，2026-09-28 |
| 状态 | **已落地，单项复审通过；待随 T-010A 最终提交结案** |
| 落地工单 | T-010A（其他项目返工 R1） |
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
