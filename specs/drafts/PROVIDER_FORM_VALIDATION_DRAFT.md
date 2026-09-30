# T-019 表单校验与模型发现准备稿

> 2026-10-01，Codex；补充 PROVIDER_EDITING_DRAFT.md。
> **待审草案，不是当前工单。** T-018-R1 正在实施，本文件不改变其范围、公开契约或 BACKLOG。T-019 签发时合并本稿与凭据发布稿，并以验收后的 T-018 生命周期为接线依据。

## 1. 本轮发现：尚未选模型时无法获取模型列表

当前 `TranslationProviderPresets` 中 OpenAI、DashScope、Moonshot 等预设的 SuggestedModel 为 null，契约明确允许通过 IModelCatalogSource 获取。可是两种 Provider 的 `ListModelsCoreAsync` 都先检查 `Descriptor.IsConfigured`，而该属性要求 Model 非空白。因此即使地址和凭据正确，首次添加服务仍需先填模型，才能获取用来选模型的列表。

这与 ARCH §9.1 当前规定的“ListModelsAsync 同样处理未配置”一致，**不是开发偏离既有规范**；需要在 T-019 签发时明确调整模型发现的前置条件，不能直接把该问题追加给 T-018-R1。

建议的正式修订方向（本稿尚未批准）：

- 翻译与测试翻译继续要求完整的地址、凭据条件及非空模型，保留 Descriptor.IsConfigured 的既有含义。
- 模型发现只要求可访问端点和凭据条件，不依赖已选 Model、Prompt 或生成参数。仍使用同一 HTTP 管道、错误映射、取消/超时与瞬态租约，不新增绕过 Provider 的 App HTTP 请求。
- 两种 Provider 均按各自既有地址/凭据规则检查；远程无 Key、无效地址仍不发请求，本地免 Key 继续复用既有本地地址判定。显式坏凭据引用不得借此回退 legacy 或被误作本地免 Key 的有效空凭据。
- UI/草稿协调器不能再用 Descriptor.IsConfigured 一刀切禁用“获取模型”；成功取得模型列表不代表已经可以翻译。
- 不填入虚构模型作为绕过门槛的占位值；不改写用户模型草稿，不把临时模型保存到设置。
- 签发时同步 ARCH §9.1/§9.2 及 IModelCatalogSource 两份契约注释，明确两种操作的前置条件；预计不需新增公开签名，但须随正式 CCR 审批冻结。

`TranslationProviderRegistry.CreateTransientAsync` 当前直接通过 factory 创建实际 Provider，不按 Descriptor.IsConfigured 替换实例；这一点来自源码检查。本轮探针直接调用实际 Provider，尚未覆盖未来草稿协调器或 UI 路由。

## 2. 字段与现有实现对照

以下“现有行为”来自源码核查，未运行规范化器回归；右列是 T-019 表单/提交入口的准备要求，不是已完成能力。

| 项目 | 现有行为 | T-019 准备要求 |
|---|---|---|
| Profile.Id | Normalize 用 OrdinalIgnoreCase 去重并保留首项；Registry 查找按精确 ID | 新 ID 按既定格式生成且不可编辑；Add 冲突检查需考虑仅大小写不同的重复，避免保存后被静默移除；更新/删除保留实际 ID |
| DisplayName | 空白改成 Id | 编辑器及时给出必填反馈，不能把自动修复后的名称说成原输入已原样保存 |
| Model | null 改空串并禁用；空串/空白本身未被该分支禁用；两种 Provider 均将空白模型判为不可翻译 | 空模型时允许先发现模型；测试翻译时明确需要模型。模型字符串不做大小写合并，列表保持可编辑 |
| Enabled / 凭据 | 已启用但未配置的项仍可进入 Registry 快照；Clear 是合法保存意图 | 保存校验不能要求每个 Profile 都有 Key 或都能真实连通，否则 Clear/未配置状态无法保存；“可保存”“可获取模型”“可翻译”分别判断 |
| TimeoutSeconds | Normalize 限制在 5–600 | 明确输入范围，拒绝无效数字；测试/保存使用同一捕获值，避免测试 1 秒、保存变 5 秒 |
| MaxOutputTokens | null 或正整数；小于 1 归 null；Anthropic 运行时 null 使用 16000 | 空值与 0 不混淆；正整数解析检查溢出；提示 Anthropic 空值含义，不承诺模型账户支持任意上限 |
| Temperature | null 或 0–2；非有限值和低于范围修复为 0；高于范围修复为 2；Anthropic 始终忽略 | OpenAI 表单检查有限数字和范围；Anthropic 隐藏该项。无关编辑不重写隐藏字段 |
| BaseUrl | OpenAI 空白会禁用；完整 URI/scheme 由 Provider 再判；Anthropic 空白表示官方地址 | 两种协议分别校验 HTTP/HTTPS；Anthropic `/v1` 尾缀显示既定提示，不能直接套用 OpenAI 地址。不要在保存阶段悄悄重写已测试的端点 |
| ChatCompletionsPath | 空白规范化为 chat/completions；请求拼接时去掉前导 `/` | 表单明确默认值；相对路径条件在签发时冻结；测试与保存不得采用不同的空值转换 |
| ExtraHeaders | Normalize 移除 null 值；运行时忽略 Authorization，其它添加失败项忽略；只记录头名称 | UI 明确附加头不能存放密钥；不提供把 API Key 转成普通配置头的通道。非法头的反馈规则须在签发时明确；复制字典并保持比较/枚举语义 |
| OpenAI 高级项 | 默认 Stream=true、IncludeStreamUsage=false、StripThinkTags=true；MaxTokensField 只允许定义值 | 不根据模型名臆测或静默切换协议参数；表单与已批准默认值一致 |
| Anthropic Effort | Options 默认 low；Normalize 不检查 effort 或 haiku；Provider 对非空白 effort 直接发送 | 模型名含 haiku 时清空 Effort 并提示是现有 BACKLOG 要求，应在草稿中可见地发生，并使旧测试 revision 失效；不能只在保存时改值 |
| PromptTemplateId | 不区分大小写匹配后改为精确 ID；不存在则规范化成 null | 下拉框区分“跟随默认”和具体模板；保存前在最新配置上检查本次明确选择的模板是否仍存在，避免它悄悄变成默认 |
| Order | Registry 按 Order 稳定排序 | 拖拽只生成顺序意图，保持 ID 和其它配置；验收持久化/Registry 顺序，结果卡片顺序待 T-015 |

是否允许以禁用状态保存缺少模型/地址的半成品 Profile，在正式签发时明确；不能把远程测试成功作为保存所有草稿的前提。Keep/Replace/Clear 与未配置引用的细节继续遵循 PROVIDER_EDITING_DRAFT.md 和正式 PROVIDER_REGISTRY.md。

## 3. 另外两处易被忽略的测试/保存差异

**Prompt 选择失效。** `PromptTemplateProvider.Get` 对找不到的显式 ID 直接回退内置模板；Normalize 对失效的 Profile.PromptTemplateId 则改成 null，此后可能跟随另一个全局默认模板。若模板在打开设置后被删除，未经最新状态检查的草稿测试和保存后翻译可能采用不同模板。T-019 只选择已有模板，不扩展为模板正文编辑器；明确选择已经失效时要求重新选择，未编辑的引用仍按既有配置规则保留/规范化。

**预设不等于实例配置状态。** RequiresApiKey 是预设提示；现有两种 Provider 都允许指定本地 HTTP(S) 地址免 Key，其中 Anthropic 官方空白地址仍需要 Key。不能只按预设名称判断保存或调用资格。模型 ID 的静态建议也不保证账户可用，供应商地址/建议模型的官方依据留待正式签发核查，本轮没有进行远程服务验证。

## 4. 隔离模型发现探针与实际输出

探针：`C:\Users\28910\AppData\Local\Temp\iris-t019-catalog-prep\Probe.csproj`，源码同目录 Program.cs。

只从上一轮独立复审的 `iris-t018-review\bin\Release\net10.0-windows10.0.19041.0` 复制既有 DLL 到探针的 lib，引用这些副本；没有 ProjectReference，也没有构建开发中的仓库。核查涉及的 Profile、Normalizer、两种 Provider 源码相对于 b9d7526 无差异。复制的二进制指纹为：

```text
Iris.Core.dll SHA-256
55E39D0CFF7DD48B4025D070ACE66FC3BC8F6E026C40B50E06C31DF23FC59BF1
Iris.Providers.dll SHA-256
8076865727EF66547FCDD2A1DB02272F810EFE154217968398D94E5B9E969228
```

两种实际 Provider 使用假的 ISettingsService、假的 API Key、内存 HttpMessageHandler 和预置响应；handler 没有网络转发能力。仅统计请求数量和安全结果，不读写用户配置/密钥。每次操作有取消上限，所有调用等待实际完成并释放自有资源。filled-model 是对照输入，用来证明 handler 可工作，**不是建议在产品中用虚构模型绕过校验**。

执行：

```powershell
dotnet run --project C:/Users/28910/AppData/Local/Temp/iris-t019-catalog-prep/Probe.csproj -c Release
```

实际输出（进程退出码 0）：

```text
CATALOG kind=OpenAICompatible case=blank-model configured=False outcome=NotConfigured http=0
TRANSLATION_TEST kind=OpenAICompatible case=blank-model success=False error=NotConfigured http=0
CATALOG kind=OpenAICompatible case=filled-model configured=True outcome=models:1 http=1
CATALOG kind=OpenAICompatible case=missing-key configured=False outcome=NotConfigured http=0
CATALOG kind=OpenAICompatible case=invalid-scheme configured=False outcome=NotConfigured http=0
CATALOG kind=Anthropic case=blank-model configured=False outcome=NotConfigured http=0
TRANSLATION_TEST kind=Anthropic case=blank-model success=False error=NotConfigured http=0
CATALOG kind=Anthropic case=filled-model configured=True outcome=models:1 http=1
CATALOG kind=Anthropic case=missing-key configured=False outcome=NotConfigured http=0
CATALOG kind=Anthropic case=invalid-scheme configured=False outcome=NotConfigured http=0
```

该结果确认当前门槛以及对照调用路径；不证明拟议修订已经实现。未运行真实网络、窗口、全量套件或任何 T-018-R1 交付验证。

## 5. 纳入 T-019 正式回归的增量

- 从 SuggestedModel=null 的真实预设创建草稿，填写合法地址/假凭据，保持模型空白，经实际草稿入口与瞬态租约获取列表成功；同时 Descriptor.IsConfigured 仍为 false，测试翻译仍拒绝且无 HTTP 请求。
- 模型列表的远程缺 Key、无效 URI/scheme 仍拒绝；本地免 Key、Anthropic 官方地址、显式坏凭据引用分别验证，不能只覆盖填满字段的成功路径。
- 模型列表返回后不自动把第一项当作已保存选择；case-variant ID 保留、手输模型可用、没有能力/失败时不造成功列表；未选择模型的 UI 不声称可翻译。
- 同一草稿的参数转换用于测试和保存；超时边界、0/null tokens、非有限温度、失效 Prompt、haiku 改模型交错要检查实际捕获值和最终 Current。
- Add 的仅大小写不同 ID 冲突在 staging/发布边界被拒绝；不得依赖 Normalize 删除重复项后报告添加成功。

签发时再冻结相应契约、校验策略与正式验收条目。本文件仅补充新发现及证据，不重复签发工单。
