# Provider 配置发布与实例生命周期

> 版本 1 · 2026-09-28 · 架构师已确定的设计。T-011 落地翻译注册表、凭据读取和快照租约；设置编辑提交在 T-019、OCR 注册表在 T-030 按同一协议接入。
> 解决架构审查 A03/A04。公开签名与新增字段的准确变更见已批准的 CCR-003；实现不应自行推导另一套契约。

## 1. 决策

- 一个翻译请求持有一个可释放的注册表快照。快照固定参与的实例与配置；设置变更只影响后续获取的快照。
- Provider 被移出当前注册表时先退休；已有快照仍可使用它，最后一个使用者释放后再 Dispose。消费者不得单独 Dispose 注册表借出的 Provider。
- 新凭据以不可变“凭据集”写入 SecretStore，再通过一次设置更新切换 CredentialSetId。不能分别更新活动 Profile 的地址与原凭据键来完成一次联合编辑。
- 保留旧格式读取兼容：CredentialSetId=null 时读取原 SecretKeys.ApiKey/SecretKey；运行期编辑入口以后必须采用新凭据集协议，不把多次旧键写入当作事务。
- 不引入新的数据库、跨文件事务框架或第三方包。

## 2. 凭据集协议（A03）

### 2.1 设置引用与存储格式

TranslationProviderProfile 和 OcrProviderProfile 均增加可空字符串 CredentialSetId。缺省为 null，旧 settings.json 不需迁移、不提升 schemaVersion；非 null 表示精确引用，空白也不是 legacy。

引用格式：`cs-` + 小写 Guid 的 N 格式（32 个十六进制字符）。新值必须使用新 ID，禁止复写已发布引用的数据。

SecretStore 键：`credential-set/{CredentialSetId}`，由 SecretKeys.CredentialSet 格式化。**整个 JSON 作为一个值调用 ISecretStore.Set**，由既有 DPAPI 实现加密，不把以下明文写入 settings.json 或独立明文文件。

```json
{
  "format": "iris-provider-credentials",
  "version": 1,
  "apiKey": "FAKE_API_KEY",
  "secretKey": null
}
```

四个字段均须存在，名称区分大小写；format/version 必须精确匹配，两个密钥字段只能是字符串或 null。空凭据用两个显式 null 表示。写入器必须保留 null 字段，不能直接沿用会忽略 null 的设置序列化策略。额外未知字段可忽略，重复 JSON 属性不接受。

T-011 只实现读取：

| 情况 | 行为 |
|---|---|
| CredentialSetId=null | 读取原有两个 SecretKeys，得到 ProviderCredentials；均缺失也可返回 ProviderCredentials.None，由具体 Provider 判定本地免 Key 是否可用 |
| 引用格式正确，密文解密成功，JSON 符合协议 | 一次读取整组值后构造不可变 ProviderCredentials |
| 非 null 引用非法、找不到、解密失败、JSON/版本/字段不合法 | 该 Profile 未配置；**禁止回退旧键**，也不能把它当作本地免 Key 的有效空凭据 |

显式引用不可用时，注册表提供不联网的未配置占位 Provider：Descriptor 保留 Profile 信息且 IsConfigured=false；首次枚举抛 NotConfigured，TestAsync 返回失败，取消按原规则处理。该情况不能令其它正常 Profile 不可用。

读取错误只记录 ProfileId、错误分类/异常类型，不记录 JSON、凭据、非法引用原值或原始异常链。Reader 不写盘、不迁移、不回收密钥。

### 2.2 写入发布流程（T-019 必须遵循，本单不实现 UI）

配置编辑协调器串行执行 Provider 编辑，基于最新设置做增量更新，不能将陈旧的整份草稿直接覆盖 Current。

1. 仅编辑普通字段且保持凭据时，沿用当前引用。替换/清除凭据时生成新 ID，先写完整凭据集（清除也写显式空凭据集）。
2. 一次 UpdateAsync 原子发布完整 Profile 与新引用。此步骤成功是提交点；此前失败则当前配置/旧凭据仍有效，尽力清理新建未引用值。
3. 提交后等待所有已接入的 Provider 配置观察者 RefreshAsync 完成。它是发布屏障：保证注册表不再有尚未完成的旧引用解析；已有租约内的 Provider 已持有构造时注入的凭据，不再读取 SecretStore。
4. 清理不再被当前任何翻译/OCR Profile 引用的旧凭据集。只有屏障成功后才清理；失败时保留旧值并报告“已保存但应用/清理未完成”，不能假装回滚了已经提交的设置。

删除 Profile 同样先发布删除，再等待屏障，再清理无人引用的值；要检查禁用 Profile 和其它类别的引用。旧格式键只能在确认没有其它 legacy Profile 使用相同 ID 后删除。

调用方在提交点之后取消，不应把已保存状态报告为“未保存”；完成必要的发布/清理或明确报告已提交后的后续失败。一次写入新凭据后进程崩溃可能留下未引用密文；它不构成生效配置。已知失败至少尽力清理该 ID，通用残留回收以后单独处理，不为此扩大 ISecretStore 接口。

## 3. 注册表快照与租约（A04）

### 3.1 对外行为

- RefreshAsync：根据调用时及之后取得的最新 Current/凭据刷新并完成一次一致发布；首次调用放在 Settings.LoadAsync 之后。
- AcquireSnapshotAsync：必要时刷新，然后原子取得一个租约。它返回捕获的 AppSettings 和按 Order 稳定排序的已启用注册项；相同 Order 保持设置列表顺序，包含未配置项。
- 每个注册项包含不可变 Profile、ITranslationProvider、InstanceId。InstanceId 是实例代次的随机 Guid；复用同一实例时不变，重建则更换，不持久化、不从明文密钥生成。
- CreateTransientAsync：捕获草稿（包括复制/只读封装 ExtraHeaders）和显式凭据，创建只含一项的独立租约；即使草稿 Enabled=false 也可测试。不缓存、不写 SecretStore、不触发 Changed。
- Snapshot.Dispose 幂等地释放其全部实例引用；快照释放后访问其属性抛 ObjectDisposedException。通过快照取得的 Provider 只能在租约存活时使用，消费者在子任务退出后才释放快照。
- Registry.Dispose 拒绝新操作，取消订阅、停止后续刷新并退休当前实例；仍由外部快照持有的实例在最后一个快照释放后清理。租约可以在 Registry.Dispose 之后安全释放。

### 3.2 重建、复用与失败

构造指纹比较 Profile 的有效值与 ProviderCredentials 值，不能用集合引用或 record.Equals 代替深度内容判断。

- 所有 Profile 字段都参与比较，Order/Enabled 除外：Order 只重排视图；禁用会退出当前注册表、重新启用可创建新代实例。
- 包括 DisplayName（影响 Descriptor）、BaseUrl、Model、超时、输出上限、温度、PromptTemplateId、两类 Options、CredentialSetId。ExtraHeaders 按已有枚举顺序比较键和值；允许保守地因顺序变化重建，不为比较而改写 HTTP 头语义。
- 可使用规范化后 Profile 的序列化内容（仅将 Order/Enabled 置为比较用的固定值）和凭据值比较；不得记录这些比较材料或凭据。主题/窗口位置等无关更新只更新 Snapshot.Settings，复用实例和 InstanceId。
- 已启用的无 Key 远程服务交给 Factory 构造，保留其 IsConfigured=false；本地免 Key 规则仍由具体 Provider 决定。显式坏凭据引用使用上一节的未配置占位 Provider。
- 每个 Kind 最多一个 Factory，重复注册是配置错误。已启用 Profile 缺少 Factory，或 Factory 出现编程错误时，本次发布失败，释放本次新建实例，保留旧发布状态；调用者不能悄悄拿到过时状态冒充刷新成功。
- 错误不得含原始凭据/配置内容：向外使用安全 IrisException(Unknown) 或安全参数异常；日志只含类型、Kind、ProfileId 等元数据。

### 3.3 并发与事件

实现可以采用短生命周期的注册表锁，串行捕获/构造/发布；调用从线程池执行，锁内没有 await。Factory 构造不得联网。Settings/SecretStore 的 Changed 回调仅标记失效并调度，不进入注册表锁、不解密、不构造 SDK、不等待 Task，因此不会在设置写锁内执行重建。

- 已发布条目持有注册表所有权引用，快照再增加使用引用；退休释放注册表引用，计数为零时恰好 Dispose 一次。实际 Provider.Dispose 在锁外执行，单个 Dispose 失败不妨碍其余清理。
- 新增/重建全部成功后再原子发布；取消/失败不能留下半张表或漏掉本次已建实例。
- 失效可以合并，但刷新过程中到来的新失效不能丢。Acquire/显式 Refresh 在锁内重新读取 Current，不使用排队事件携带的旧设置来覆盖新值。
- Changed 只在实际 Provider 集合、顺序、Descriptor 或实例代次变化后触发，且在锁外。通知可以合并，订阅者通过新快照读取当前状态；单个订阅者失败不阻断其他订阅者。
- 后台刷新失败时记录安全诊断，等待新的变更或显式调用，不对未变化的错误配置无限热循环重试。
- Dispose 必须覆盖排队刷新、刷新正在构造、迟到事件、重复 Dispose 与后续租约释放；不要提前销毁租约释放仍需的同步对象。

## 4. 启动与依赖注入

- Core 注册一个 TranslationProviderRegistry 单例及其 ITranslationProviderRegistry 端口。IProviderConfigurationObserver 的翻译观察者应转发到同一注册表（可使用 internal 适配器避免 DI 重复实例），重复 AddIrisCore 不能重复注册或重复订阅。
- 构造函数只保存依赖/建立订阅，不读取凭据、不创建 Provider。AppBootstrapper 在 Settings.LoadAsync 成功或损坏恢复后调用 RefreshAsync，之后继续创建托盘；T-011 不注册未来尚未实现的服务。
- 依赖仍只指向 Core 端口：ISettingsService、ISecretStore、Factory 集合、日志。Core 不引用 Providers/Infrastructure/Windows/SDK。
- 新异步方法完整支持 CancellationToken；测试使用受控 Factory、事件与 TaskCompletionSource，不靠真实等待猜顺序。

## 5. 分期与验收边界

2026-10-01 更新：T-019 的写入端口与具体算法已在 PROVIDER_EDITING.md / CCR-011 冻结，分为 T-019A Core 协调器/草稿操作与 T-019B 设置页接线。两单完成才交付 T-019；本文件的 Registry、凭据集和屏障语义继续有效。

T-011 落地本节公开接口、翻译注册表、凭据集读取、租约、初始化和观察者接线；配置写入协调器在 T-019 实现，OCR 注册表在 T-030 改为同样的租约/屏障模式。当前旧格式读取是兼容路径，不能宣称对任意直接调用底层 Set + UpdateAsync 的组合写入提供事务保证。

T-012/T-013 必须把 InstanceId 和实际 Prompt/执行配置纳入缓存语义；Snapshot.Settings 是稳定输入，但目前 Provider 的 Prompt 仍通过既有服务获取，不能声称整条翻译请求已经冻结 Prompt。对应请求上下文/渲染入口在签发 T-012/T-013 时补齐，禁止以“Clear 一次缓存”替代代次隔离。

核心验收反例：旧租约跨重建继续工作；最后一个使用者释放才 Dispose；无关设置变化复用实例；新凭据未被设置引用时不产生混合组合；显式坏引用不回退 legacy；刷新取消/失败清理新建实例；瞬态草稿不泄漏别名；重建与 Dispose 竞态无重复释放/失效丢失。


## 6. OCR落地补充（T-030A / CCR-030）

OCR采用同一凭据集与条件发布原则，具体异步租约/退休故障/停止规则由OCR_PROVIDER_REGISTRY.md优先限定。OCR Profile没有Order，保留设置列表顺序；不能复制翻译同步Dispose的吞错/失去重试责任方式。翻译已验收生命周期不在本单重写。
