# T-019 Provider 编辑、凭据发布与草稿操作设计稿

> 2026-09-30，Codex；补充 SETTINGS_EDITING_PREPARATION.md。
> **历史准备稿。** 2026-10-01 已转为正式 PROVIDER_EDITING.md / CCR-011，准确签名以 CONTRACTS.cs 为准；下文保留准备过程和证据，冲突处以正式规范为准。T-019A / T-019B 分期见 BACKLOG。

## 1. 核心分工与候选端口

App 负责编辑 UI、密码输入意图、窗口会话/revision、主题预览及结果展示；Core 的配置编辑协调器负责快照捕获、冲突检查、凭据 staging、一次设置发布、观察者屏障与清理。Core 不引用 T-018 的 App internal 类型。

候选操作分为四类（名称/签名暂不冻结）：

- `Commit`：接收固定的常规补丁、翻译 Profile 增/改/删/排序意图和凭据意图，返回明确的未提交/已提交/应用未完成结果。
- `RetryApply`：针对已提交回执重试观察者刷新/旧值清理；不再次发布旧草稿或生成新凭据集。
- `TestDraft`：捕获一个草稿版本与显式凭据，以瞬态租约执行测试连接。
- `ListDraftModels`：同样捕获草稿并借用瞬态租约，使用可选 IModelCatalogSource；无该能力时允许手动输入模型，不伪造列表。

T-019 接管全窗口的一次配置提交：不能保留一个先写 General 的保存分支，再另行写 Providers。T-018 的 App 保存入口可以委托 Core 协调器，常规补丁转成 Core 所属的数据模型；其主题预览仍由 App 负责。AutoStart 的后台应用状态应合并进同一次保存结果，不能抹掉“JSON 已保存”的事实。

## 2. 编辑计划应表达意图，而非整份旧配置

建议明确区分 Add、Update、Delete 和 OrderChange，不用通用 upsert 隐式推导：

- Add 的 Profile ID 新生成、不可变；当前已有同 ID 则冲突，不覆盖。
- Update/Delete 携带该 Profile 的 base。若它已被删除或配置/引用与 base 不一致，拒绝陈旧编辑，不复活旧 Profile。
- 普通常规项按已批准的 dirty 字段规则合并；Provider 的地址、协议选项、模型和凭据引用属于相关配置，不作无条件的跨版本逐字段拼接。
- OrderChange 单独记录基线 ID 集合/顺序，仅更新当前 Profile 的 Order；未涉及的 Profile 内容保持最新。并发增删/另一排序应明确冲突。
- ExtraHeaders 等集合在入口捕获为独立只读数据。UI 后续改动不影响已经排队的保存/测试。不要用 record.Equals 或集合引用相等替代深度配置比较。

前置检查减少无谓 staging，但最终冲突检查必须在 UpdateAsync 的 mutate 内针对最新 Current 再做一次。所有受支持的 Provider/引用写入统一走协调器；其它普通设置写入必须保留 Provider 部分。

## 3. 凭据意图

| 意图 | 已存在 Profile | 新 Profile |
|---|---|---|
| Keep | 冲突检查通过后沿用当前引用；不复制旧草稿引用重新发布 | 无既有凭据，使用明确的无凭据状态，不按随机新 ID 去捞遗留键 |
| Replace | 生成新 ID，写完整 apiKey/secretKey 对，不覆写活动值 | 同左 |
| Clear | 写一个新引用，内容为两个显式 null；不改回 legacy | 同左 |

密码框为空不能同时表示 Keep 和 Clear。Clear 在 UI 中是草稿操作，直到保存才修改持久状态。已保存后清除输入框对明文的引用并回到 Keep 状态，不把明文留在提交回执或普通设置文件中；不承诺托管字符串可被安全擦除。

凭据集 JSON 必须包含 format、version、apiKey、secretKey 四个字段，按既有大小写/版本规范写出。IrisJson.Options 的 WhenWritingNull 不适合直接用于这个写入器；应显式写 null。全组值作为一次 ISecretStore.Set 调用，后台执行。

显式坏引用继续视为未配置，禁止回退 legacy。Keep 不能绕过此规则；不为“测试连接成功”自动取另一套旧密钥。

## 4. 保存与恢复状态机

| 阶段 | 允许的副作用 | 失败/取消的返回含义 |
|---|---|---|
| 捕获、校验、等待编辑权 | 无持久修改 | 未提交 |
| staging | 只创建新的独立密文 ID，旧引用不变 | 未提交；尽力清理本次未引用值 |
| 一次 UpdateAsync | 在最新配置上发布整个编辑计划 | 正常返回后视为已提交；不能再报告未保存 |
| 已接入观察者全部刷新 | 确保旧引用解析退出 | 已保存但应用未完成；保留旧值 |
| 清理旧引用、系统应用 | 只删除仍无人引用的候选值；按最新已保存意图应用系统项 | 已保存但清理/应用未完成 |

协调器维护本次 staged ID 清单，批量保存不能只记最后一个。提交前冲突/异常时只处理本次新建且仍无引用的值，删除失败作为次要诊断，不改变旧配置。

提交后不使用 UI 的迟到取消把已保存状态改成 OCE/未保存。若给刷新设预算，必须通过取消请求并观察实际任务退出；不能用 WaitAsync 超时后直接删凭据。所有已注册观察者都成功返回才允许旧值清理；未实施的 OCR Registry 不注册空观察者冒充屏障。

可在内存中保留应用回执：提交 ID、涉及 Profile/新引用、旧清理候选、未完成阶段和安全错误信息。**不含原始凭据或 JSON**。RetryApply 先核对当前配置，不把回执中的旧 Profile 写回；已经被其它提交取代的回执也不能恢复旧状态。

进程崩溃后的孤立密文是既有协议接受的边界；ISecretStore 没有枚举接口，本期不加通用密文垃圾回收或跨文件事务框架。重启时以实际已保存配置与观察者刷新结果为准，不能仅凭旧 UI 状态声称回滚。

## 5. 旧凭据清理的准确范围

清理候选是被本次更新/删除替换掉的旧引用，不是“所有不在当前启用列表中的键”。每次删除前在协调器串行权内核对最新全部配置：

- Translation.Providers 与 Ocr.Providers 都要扫描；Enabled=false 的 Profile 仍有引用权。
- CredentialSetId 按 Ordinal 精确匹配；不要 trim 后把非法引用误认为合法引用。
- 只有 CredentialSetId=null 的 Profile 使用 legacy 键；空字符串或坏的非 null 引用不能算 legacy。
- 同一个凭据集仍被另一个翻译或 OCR Profile 使用时保留；仅一方被删除不意味着可以回收。
- legacy 的 api-key/secret-key 要检查其它同 ID 的 legacy Profile，包括跨类别。旧快照内 Provider 已持有凭据值，按已通过的 Registry 屏障规则等待后才删除存储值。

不要把关闭设置窗口或租约释放当作删除密钥的触发器。权限/磁盘等删除失败不抹掉已发布配置，也不重复创建一套新凭据来“修复”。

## 6. 草稿测试与保存/清理的竞争

Keep 凭据需要读取存储时，在编辑协调器的短捕获阶段中核对 base/Profile 并解析凭据，防止刚拿到旧引用就被另一个保存清理。取得独立 ProviderCredentials 值后即可释放编辑权，再创建瞬态 Provider 并开始网络操作；**不能让整个网络测试长期持有配置写入锁**。

Replace/Clear 直接使用该次捕获的显式值，不先写 SecretStore。新 Profile 的 Keep 不查询遗留键。CreateTransientAsync 捕获 Profile/ExtraHeaders，不缓存、不持久化、不替换活动实例；工作真正结束后才 Dispose 租约。

每个设置编辑会话最多一个正在进行的测试/模型列表请求；新操作先取消旧请求并等待实际退出，再启动最新等待项。修改草稿只增加 revision 并使旧结果失效，不自动发起真实网络调用。保存与测试可以在已完成捕获后独立进行，成功保存重建 base/revision 时也要拒绝旧测试的迟到回写。

已有 Provider.TestAsync 的准确边界：固定 Hello, world. 测试，目标语言取当前已保存的 PrimaryTargetLanguage；它不是整份未保存 TranslationSettings/Prompt 编辑器的预览。T-019 当前只要求 Profile/凭据草稿与 Prompt 选择，不应声称可测试尚未保存的模板正文/所有全局设置。若未来需要这种能力，另行批准显式请求上下文，而非先临时写设置再恢复。

## 7. 需要直接针对实现的回归

| 交错/故障 | 预期 |
|---|---|
| 两个 Profile staging，第二次 Set 失败 | 配置不发布；第一套本次新值尽力回收，旧值不动 |
| staging 后配置已被他处修改 | mutate 内冲突，拒绝陈旧 Profile，清理本次无引用值 |
| 清除仍有 legacy 键的 Profile | 新显式空集合生效，不重新读 legacy |
| 发布成功后 caller 取消/观察者失败 | 返回已提交状态，保留旧值；不回滚设置、不假成功 |
| 一个观察者完成、另一个尚未退出 | 不删除旧值；等待实际解析退出 |
| disabled/OCR Profile 共享旧引用 | 保留共享值；最后引用移除且屏障成功后才清理 |
| Keep 测试与保存竞争 | 凭据捕获受保护，网络阶段不阻塞整个编辑锁 |
| 测试进行中更改/删除草稿、关闭窗口 | 迟到结果不回写；真实任务退出后释放瞬态租约 |
| RetryApply 后又有新提交 | 不恢复旧配置、不再次 staging、不删当前仍引用的值 |
| 密码输入/JSON/异常含假凭据标记 | 不进入普通设置、日志、回执 ToString 或报告 |

正式测试必须调用实际协调器，通过受控端口/门闩制造上述顺序。不能手工写出理想事件列表再断言，也不能把准备模型视为生产回归。

## 8. 本轮凭据格式兼容性验证

独立临时项目：`C:\Users\28910\AppData\Local\Temp\iris-t019-credential-prep\Probe.csproj`。为避免争用当前开发输出，只复制既有 Release Iris.Core.dll 并引用该副本；SHA-256 为：

```text
32EAD19D04332B6B68D1A83373AB77688B3A1783581E3F55667C3D78DD1037BB
```

调用现有 ProviderCredentialResolver，使用内存 fake SecretStore 和假凭据；显式 JSON writer 与忽略 null 的 serializer 各生成一份输入。未读用户密钥、未写设置/注册表、未联网。执行命令：

```powershell
dotnet run --project C:\Users\28910\AppData\Local\Temp\iris-t019-credential-prep\Probe.csproj -c Release
```

实际输出：

```text
EXPLICIT_CLEAR fields=4 valid-empty=True legacy-reads=0
OMITTED_NULL_FIELDS explicitly-unconfigured=True legacy-reads=0
NULL_REFERENCE_AS_CLEAR legacy-key-revived=True legacy-reads=2
REPLACE_SET valid=True legacy-reads=0 credentials-ToString-redacted=True
```

valid-empty 仅表示凭据集格式有效且得到空凭据，不表示远程 Provider 已配置可调用。此验证证明拟采用的写入格式兼容现有读取器，且验证了错误清除方式的反例；不证明尚未实现的提交协调器、UI 或真实服务可用。

## 9. 正式签发前

复核 T-018 的 internal 保存入口/生命周期后，冻结 Core 提交计划与结果端口、常规补丁迁移方式、RetryApply 回执所有权及冲突比较实现。UI 验证与现有 Profile 规范化/Provider 校验保持一致，不在保存时悄悄改成另一配置。

预设地址/默认模型需按正式签发时的供应商依据核查；静态列表不保证用户账户可用，模型保持可编辑。没有选择模型列表能力或连接失败时，不为“验证预设”读取用户真实凭据自动联网。

本文件不改变当前 T-018 工单，不批准新公开签名，也不提前实现 T-019。
