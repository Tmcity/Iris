# 配置编辑、凭据发布与草稿服务操作

> 2026-10-01，Codex；ARCH v1.23 / CCR-011。由 PROVIDER_EDITING_DRAFT.md 与 PROVIDER_FORM_VALIDATION_DRAFT.md 定稿。
> T-019 拆为 T-019A（Core 协调器与模型发现前置）和 T-019B（设置页与一次保存接线）。T-019B 验收后才结案 T-019。本文不改变已验收的 Registry/租约协议。

## 1. 分工及端口

`ISettingsEditCoordinator` 是 Core 单例，通过 AddIrisCore 的 TryAddSingleton 注册，重复注册不重复创建。实现默认 internal sealed，仅依赖 Core 端口、BCL 和允许的抽象包。构造不读设置/密钥、不启动任务、不调用 Provider。

四个操作为 CommitAsync、RetryApplyAsync、TestDraftAsync、ListDraftModelsAsync；准确签名及 DTO 以 CONTRACTS.cs 为准。App 负责控件、草稿版本/窗口身份、主题预览、系统自启应用和用户提示；Core 负责配置增量、凭据读取/staging、发布屏障和回收，不引用 App internal 类型。

T-019A 只交付 Core 实现、DI 和两种 Provider 的模型发现前置规则。现有 T-018 常规保存不在 A 单迁移；T-019B 将其委托至本端口，让常规补丁和服务改动经过一次 UpdateAsync，不能保留先写 General、再写 Providers 的双提交。

调用方法同步捕获输入，再进入异步等待。必须复制 Profile 内字典、计划列表和排序列表；不能用只读接口包装同一个可变集合。捕获后调用方修改原草稿不影响已接纳操作。各方法等待自己启动的真实 I/O、Provider 调用及租约释放，不发起无人持有的后台任务。

## 2. 编辑意图与冲突

GeneralSettingsPatch 的非 null 项表示本次显式编辑，含 Base/Target；未编辑字段保留提交时的最新 Current。沿用 T-018 规则：当前值既不等于 Base、也不等于 Target 时冲突；已等于 Target 不必写出新版本。只支持 Theme、LaunchAtStartup、ShowStartupNotification、LogLevel（五个既定日志级别），不编辑其它 General 字段。

TranslationProfileChange 的组合必须明确：

| 类型 | BaseProfile | TargetProfile | 凭据 |
|---|---|---|---|
| Add | null | 新 Profile | Keep/Replace/Clear |
| Update | 存在 | 同一个精确 ID | Keep/Replace/Clear |
| Delete | 存在 | null | 只能 Keep |

新增 ID 为 tp- 加 12 位小写十六进制，由调用方生成、协调器校验；不能覆盖已有或仅大小写不同的 ID。已有 Profile 使用实际 ID，不重写历史 ID 格式。每个 ID 一次计划最多一个 change；不得用 upsert 复活已被删除的 Profile。

Update/Delete 的 base 与最新 Current 按完整配置深度比较，忽略 Order，包含 Enabled、地址、模型、两组 Options、Prompt 和 CredentialSetId；比较字串用 Ordinal，ExtraHeaders 按枚举顺序比较键和值，可保守地把顺序变化视为冲突。不能用 record.Equals 或集合引用相等替代。即使当前已等于目标配置，发生了不同 base 的 Profile 改动仍报冲突，避免重新发布可能已清理的旧凭据引用。

TargetProfile 的 CredentialSetId 不提供任意写入入口：Add 必须为 null，Update 必须与 BaseProfile 引用相同；最终引用由凭据意图决定。Order 由独立顺序意图决定，Update 未改排序时保留 Current 的 Order。

Add/Delete 必须附 TranslationOrderChange。BaseIds 包含基线全部翻译 Profile，按 Order 稳定排序；在 mutate 中比较最新完整 ID 集合/顺序，期间并发增删或另一排序产生冲突。TargetIds 必须恰好包含应用本计划增删后的全部 ID，无重复或缺项；按目标顺序写 0..N-1 的 Order，未编辑 Profile 的其它内容全部取最新 Current。纯 Update 可不附排序；单独排序不复制旧 Profile 内容。

前置校验/冲突检查减少无谓 staging，但最终判断必须在 ISettingsService.UpdateAsync 的 mutate 内重新对最新 Current 做一次，包括已选择 Prompt 是否仍存在；不能只校验打开窗口时的快照。mutate 内不做 SecretStore、Registry 或系统 I/O，不等待异步工作。

## 3. 输入校验

校验和 Normalize 各有职责：编辑入口拒绝可预见的无效新输入，不依赖 Normalize 静默禁用、丢项或替换。普通无关设置写入仍沿用既有规范化行为。

- Kind/Theme/MaxTokensField 必须是已定义值，DisplayName 非空白；超时为 5–600，tokens 为 null 或正整数，Temperature 为 null 或有限的 0–2。
- Enabled Profile 需要非空 Model，以及协议要求的合法 HTTP(S) 地址；Anthropic 空地址表示官方端点。Disabled Profile 可保留空模型/空地址作为未完成配置；填了地址则仍须合法。没有 Key、显式未配置和 Clear 均可保存，不以远程连通成功作为保存前提。
- 不擅自 trim/改写 ID、CredentialSetId、端点、模型或已测试的输入。必需默认 Options 可在入口捕获中明确补齐；写盘前的目标与测试使用的有效值应一致，不能悄悄改变语义。
- OpenAI ChatCompletionsPath 为非空相对路径，可有前导 /；拒绝绝对 URI、scheme-relative 地址和查询/片段，不允许通过路径输入切换认证目的地。当前默认仍为 chat/completions。
- ExtraHeaders 不存密钥；拒绝 Authorization、Proxy-Authorization、x-api-key、api-key（忽略大小写）和含 CR/LF 的头值，名称需为有效 HTTP token。保留其它非敏感头的精确键/值与顺序，错误不回显原值。此处限定新编辑入口，不重写已有 Provider 的处理管道。
- Anthropic Effort 为 null 或 low/medium/high/xhigh/max；含 haiku 的模型（OrdinalIgnoreCase）要求 null，Core 不在保存时自动清空。T-019B 在草稿中可见地执行清空并提示。Anthropic 隐藏 Temperature 的 UI 行为在 B 单，存储中的未编辑字段不擅改。
- 显式 Prompt ID 必须匹配提交时已有内置/自定义模板的实际 ID（OrdinalIgnoreCase 匹配，使用精确 ID）；null 为跟随默认。不存在的显式选择报输入/冲突错误，不能偷偷改成默认模板。T-019 不编辑模板正文或未开放的全局翻译设置。

列表模型的前置校验按操作区分：无需已选模型、Prompt 或生成参数，但需要合法 Profile 身份/Kind、有效超时、端点和显式凭据意图。翻译测试需要有效模型/翻译参数。详细对照和旧实现证据保留在 PROVIDER_FORM_VALIDATION_DRAFT.md。

2026-10-01 / T-019A-R3 澄清：ListDraftModelsAsync 不使用 ChatCompletionsPath、MaxTokensField、Effort，因此这些尚未有效的草稿字段不阻止模型发现；它们仍必须在 Commit/TestDraft 中校验。模型请求实际使用的 BaseUrl、凭据/引用、ExtraHeaders、Timeout 和身份/Kind 校验始终执行，不能为放行列表而整体跳过 Options 中的请求头安全规则。

## 4. 凭据意图与 staging

ProviderCredentialEdit.Replace 必须带完整 ProviderCredentials 对，Keep/Clear 的 Replacement 必须为 null。密码框为空不能同时表示 Keep/Clear。新 DTO 只用于内存，不写到 settings.json，不记录整个对象/异常正文；不得把原始输入或凭据放进回执。

- Existing Keep：冲突通过后沿用最新精确引用，不复制陈旧引用重新发布；坏的显式引用继续未配置，禁止 legacy 回退。
- New Keep：使用一套新建的显式空凭据集，不读取该新 ID 的 legacy 键。
- Replace：新建 cs- 加 Guid(N) 小写 ID，写完整 apiKey/secretKey 对。
- Clear：同样新建引用，写两个显式 null；不得改回 CredentialSetId=null。

严格采用 PROVIDER_REGISTRY.md §2.1 的四字段 JSON 与一次 ISecretStore.Set。使用能保留显式 null 的专用 writer，不直接使用省略 null 的设置序列化选项。SecretStore Get/Set/Remove 在后台执行。暂存多个 Profile 时维护完整的新 ID 清单；第二项失败也要尽力回收第一项，不能只记最后一项。

staging 前及每次 I/O 之间检查提交前取消；已开始的同步 I/O 必须等待真实退出。回滚只清理本次创建、仍无引用的值，绝不动原配置/原凭据。删除失败作安全次要诊断，不覆盖原失败/取消，不扩展通用密文枚举或垃圾回收。

## 5. 提交点、屏障、清理

协调器以单一异步串行权管理所有受支持的 Provider 写入、重试与 Keep 凭据短捕获。不同普通设置入口仍可独立 UpdateAsync，但必须保留未编辑的 Provider 部分。

1. 捕获、校验、取得编辑权、前置冲突检查。
2. 后台写本次新凭据集。
3. 一次 UpdateAsync：在 mutate 中重验并联合发布 General 增量、Profile 增改删、排序与新引用。正常返回是已提交边界；无操作可不写版本。
4. 若涉及 Provider 改动，调用全部已接入 IProviderConfigurationObserver，观察所有已启动刷新真正退出；全部成功才具备旧值清理资格。不注册尚未实施的 OCR 空观察者冒充屏障。
5. 在编辑串行权内、删除每个候选前，重读最新全部 Translation/Ocr 引用，再后台 Remove 无人使用的候选。

提交点之后忽略 caller 的迟到取消，完成上述过程或返回 ApplyPending；不能回滚 JSON、抛提交前 OCE 或说“未保存”。观察者失败时保留全部旧值；清理失败时保留未清理候选。General-only 不需要 Provider 屏障；不在 Core 执行 IAutoStartService，系统应用由 B 单 App 合并状态。

候选仅是本次被替换/删除的旧引用。扫描含 Enabled=false 的翻译/OCR项，共享凭据仍有人引用就保留；只有 CredentialSetId=null 才使用 legacy。显式非 null 坏引用不参与 legacy 清理，也不对非法引用原值构造任意删除键。Legacy ApiKey/SecretKey 删除前检查跨类别相同精确 Profile ID 的 legacy 使用者。

已有租约内 Provider 已持有凭据值，屏障保证旧引用解析结束；不要求旧翻译请求全部结束后才删除存储值，不提前 Dispose 借出的 Provider。

## 6. 结果、回执与错误

| SettingsEditStatus | 提交事实 | Error / PendingApply |
|---|---|---|
| Uncommitted | 未提交 | 输入/冲突/存储安全错误，PendingApply=null；冲突 IsConflict=true |
| Applied | 已提交或本已相同，Provider 后续完成 | Error=null，PendingApply=null |
| ApplyPending | 已提交，刷新/旧值清理未完成 | 安全 Error 及非 null opaque 回执 |

输入值错误返回 InvalidArgument，冲突使用 IsConflict 与固定安全消息；存储错误保留已知 StorageFailure/SecretUnavailable 分类，未知错误仅安全类型诊断。null 顶层参数可按编程错误抛 ArgumentNullException。提交前取消完成本次暂存回收后抛 OCE；不能泄露凭据、JSON、端点查询串或原始异常链。

回执是本协调器创建的私有实现，通过 ISettingsApplyReceipt 暴露 CommitId；仅保留安全提交身份、旧清理候选与必要进度，不能引用原 plan、凭据、原 JSON、Provider、UI 或租约。拒绝伪造/另一协调器回执。调用方持有回执；协调器不创建无限回执字典。T-019B 的应用层需在窗口重开间保留未完成回执，直到重试成功或进程退出；不能关闭窗口就声称清理成功。

RetryApplyAsync 在同一串行权内重新刷新当前配置，重验最新全部引用后清理剩余候选，不重新 stage、不发布回执中的旧 Profile。发生新提交不使重试恢复旧状态或删除当前引用。成功后该回执幂等；并发重试同一回执不重复执行删除。取消只在开始前/等待准入时终止，开始实际刷新后等待退出并返回结果；原提交事实始终不变。进程退出/崩溃遗留无人引用密文是已接受边界，本期不补枚举接口。

## 7. 草稿操作与模型发现

先同步捕获草稿。已有 Keep 在短编辑权内核对 BaseProfile 并按现有 ProviderCredentialResolver 读取；得到独立凭据值后释放编辑权，再创建瞬态租约执行网络操作。Replace/Clear 直接用捕获值，不写 SecretStore；新 Keep 使用 ProviderCredentials.None，不访问 legacy。

显式坏引用不能转换成 None 后作为本地免 Key 成功：读取持久凭据的 Keep 遇到坏引用时，TestDraft 返回安全 NotConfigured，ListDraftModels 抛安全 NotConfigured；不联网、不回退旧键。2026-10-01 / T-019A-R1 澄清：已有 Profile 的 Replace/Clear 测试使用新显式值，不读取旧引用，因此坏旧引用本身不阻止合法的新凭据测试；新草稿仍禁止任意指定持久引用，所有意图均须先校验结构/身份。所有业务失败/调用方取消语义见端口注释。TestDraft 使用实际 TestAsync 的固定短句、已保存 PrimaryTargetLanguage 与 Prompt 语义，不声称测试未保存模板正文。

从 ITranslationProviderRegistry.CreateTransientAsync 获取租约，选该单项 Provider；操作真正退出后 finally Dispose，不能用 WaitAsync 超时遗忘任务再释放租约。测试/模型列表不修改设置、SecretStore、活动 Registry、缓存或历史。网络阶段不能占住配置编辑串行权。

两种现有 Provider 的 ListModelsAsync 改为只按端点/凭据可用性判断，不依赖 Model 非空；Descriptor.IsConfigured 和翻译/TestAsync 仍要求模型。远程缺 Key/非法 URI 不发请求，本地免 Key 继续复用 DynamicWebProxy.IsLocalAddress；Anthropic 空 BaseUrl 为官方地址且需 Key。仅改模型列表 guard，不改 SDK、HTTP 管道、重试、错误映射或请求格式。

无 IModelCatalogSource 时返回明确无能力错误，不能伪造列表。实际列表保持已有去空白、Ordinal 去重、OrdinalIgnoreCase 排序和分页/响应预算；不把大小写不同的模型合并，不用虚构模型绕过 guard。

## 8. 分期与验收

T-019A 验收实际协调器的多 Profile staging 失败、提交前冲突/取消、一次联合发布、提交后取消、全部观察者屏障、共享/禁用/OCR/legacy 引用保留与最后引用清理、清理重试和已更新配置保护、Keep 捕获与长网络调用隔离、瞬态租约真实退出、空模型获取列表，以及脱敏/输入别名保护。

T-019B 接服务页、预设、可编辑模型框、Prompt 选择、密码意图、排序、草稿 revision 与 T-018 生命周期，把窗口保存委托至本协调器并合并系统应用结果。签发 B 前由架构师核查预设地址/建议模型的官方依据；A 不改预设。仅 A 通过不代表已能从 UI 配置服务，也不结案 T-019。
