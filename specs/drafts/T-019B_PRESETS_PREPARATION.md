# T-019B 预设、模型发现与服务页准备

> 2026-10-01（Asia/Shanghai），Codex；T-019A 实施期间的独立准备稿。
> **历史准备稿。** T-019B 已于 2026-10-01 按 PROVIDER_SETTINGS_UI.md / CCR-012 签发：新建 DeepSeek 采用官方当前基础地址及 null 建议模型，其余预设保持。下文保留准备时的旧值/候选与官方证据，正式决定以新规范和 CONTRACTS.cs 为准。

## 1. 结论与建议

现有多数 BaseUrl 可由当前官方文档确认；文档网站改名/重定向不等于 API 地址也要迁移。当前明确需要在 B 签发前决策的是 DeepSeek 的新建预设：入门/模型页使用 `https://api.deepseek.com` 和 `deepseek-flash`，而项目仍建议 `/v1` + `deepseek-chat`。本轮没有证明旧值已经失效，不能把“当前页面未列出”写成“请求必然失败”。

`claude-opus-5` 在官方状态表仍为 **Active**，没有因出现更新型号而强制更换本项目默认建议的依据。DashScope 官方明确旧域名仍可使用，预设可继续默认北京地域，但应帮助用户识别 Key 的地域归属。Ollama 的本地端点免 Key，不能把云端认证要求套到本地预设上。

建议 B 的模型输入始终可编辑，优先通过用户显式点击获取当前账户列表。列表失败、没有接口能力或未选择模型时仍能手工输入；列表成功不等于其中每个 ID 都支持当前聊天协议。A 的接口只返回 ID，没有完整能力元数据，B 不应按名字猜测并静默过滤模型。

## 2. 现有预设逐项核查

| 预设 | 仓库当前 BaseUrl / SuggestedModel | 官方文档核查 | T-019B 建议（尚未批准） |
|---|---|---|---|
| OpenAI | `https://api.openai.com/v1` / null | S1 明确示例 `GET https://api.openai.com/v1/models`，Bearer Key；返回模型 ID 与基本可用性信息 | 保留地址和 null 建议模型，模型可编辑；不硬编码“最新模型” |
| Claude | null（SDK 官方地址）/ `claude-opus-5` | S2 的 Models API 为 `GET /v1/models`，示例主机 api.anthropic.com；S3 状态表将 claude-opus-5 标为 Active | 保留官方空地址语义及现有建议型号；以用户账户列表/测试为准，反代地址仍遵循现有 SDK 不重复追加 /v1 的规则 |
| DeepSeek | `https://api.deepseek.com/v1` / `deepseek-chat` | S4/S5 当前列出 BaseUrl `https://api.deepseek.com`，型号 deepseek-flash / deepseek-v4-pro；S6 模型列表为 `/models` | 候选：新建预设采用官方当前基础地址，SuggestedModel 先置 null，由模型列表/手输选择；如需固定建议，deepseek-flash 有当前官方依据，但须 B 签发时批准。既有用户 Profile 不自动迁移 |
| DashScope | `https://dashscope.aliyuncs.com/compatible-mode/v1` / null | S7 推荐 WorkspaceId 专属域名，同时明确旧域名仍可用；Key 按地域绑定 | 可保留当前北京地址，明确“北京”；提供可编辑地址和地域说明。不能把带 `{WorkspaceId}` 的模板直接保存为可调用地址 |
| Kimi / Moonshot | `https://api.moonshot.cn/v1` / null | 旧平台文档跳到 platform.kimi.com，但 S8/S9 仍使用 api.moonshot.cn/v1，并列出 GET /v1/models | 保留 CN API 地址；中文/国际平台 Key 不可混用，不根据文档重定向自动切换 API 主机 |
| 智谱 GLM | `https://open.bigmodel.cn/api/paas/v4` / null | S10 OpenAI 兼容示例多次确认该基础地址；本轮未在官方索引/已读页确认其 `/models` 列表端点 | 保留地址与手输模型；获取列表可按既有能力尝试并正确处理失败，不能承诺官方已证明该端点可用 |
| SiliconFlow（中国站）| `https://api.siliconflow.cn/v1` / null | S11 当前中国站文档明确 GET `/v1/models?sub_type=chat`，Bearer Key；S12 国际站文档使用 api.siliconflow.com | 保留中国站地址；不要把中文语言页面误认为中国站，`.com` 中文文档属于另一 API 主机 |
| Gemini OpenAI 兼容 | `https://generativelanguage.googleapis.com/v1beta/openai` / null | S13 同时给出该基础地址、chat/completions 和 models.list / GET `/models` 示例 | 保留地址和 null 建议模型；保留用户可编辑型号，不使用原生 generateContent URL 替代兼容地址 |
| Ollama 本地 | `http://localhost:11434/v1` / null | S14 本地示例使用该地址，SDK 要求 Key 字符串但本地服务忽略它；列出 `/v1/models` 支持 | 保留本地免 Key；不替用户下载/拉取模型，手输或读取本地已可用型号。云端 ollama.com/v1 是另一个有认证的配置，不自动替换本地地址 |
| 自定义兼容服务 | null / null | 无供应商预设事实可代填 | 用户填写端点/模型/凭据，按实际主机与协议判断；不从显示名推断认证或模型能力 |

表中的“保留/候选”均是签发准备，不是本轮已修改的代码。预设和建议模型属于契约定义，若 B 最终采用更改，须在正式 CCR 中批准并同步两份定义，不仅改 UI 层复制表。

## 3. 会影响 UI 的准确边界

- **新增预设不立即产生远程请求。** 选预设只填草稿；用户点击测试连接/获取模型才调用 A 的草稿端口。没有读取本机真实凭据进行本轮验证。
- **地址与账户归属配套。** DashScope Key 按地域，Kimi 文档也明确 platform.kimi.com 与 platform.kimi.ai 的 Key 不可混用。收到 401/403 不自动尝试其它地区/供应商端点。
- **区分未知能力与明确错误。** 智谱及 DashScope 的 OpenAI-compatible `/models` 在本轮未取得明确的接口文档证据；不能把静态网页“模型列表”当作 GET API 的证明。失败时保留手输和安全错误详情，不把 404 一概解释成“供应商没有此功能”（也可能地址错误）。
- **模型列表不等于聊天型号目录。** OpenAI 返回的基础模型对象没有本项目完整的调用能力分类；SiliconFlow 文档提供 sub_type 过滤，但当前 A 的统一端口没有该参数，B 不私自加查询参数或按模型名猜能力。若后续要精确过滤，另评估契约/Provider 改动。
- **旧配置保持用户意图。** 预设更新只影响新建草稿，不重写已经保存的 BaseUrl/Model/CredentialSetId。改变端点不会自动复制另一账户凭据或用环境变量填空。
- **模型相容性以实际调用结果为准。** 官方标为 Active 不保证当前账户、反代或套餐有权使用；本轮只验证文档，不验证真实连通性、计费或返回质量。
- **模型选择与已有高级选项一致。** 保留正式规范中的 haiku 清空 Effort、Anthropic 隐藏温度、OpenAI MaxTokensField 可选规则；不把厂商最新文档中新出现的参数直接塞进 A 的现有契约。

## 4. 关键原文证据

### OpenAI 模型列表（S1）

> “Lists the currently available models, and provides basic information about each one such as the owner and availability.”

页面实际示例为 `https://api.openai.com/v1/models`。这证明地址/列表格式，并不证明每个返回 ID 都能用于 Chat Completions。

### Claude 现有建议型号（S3）

实际状态表行：

```text
| claude-opus-5              | Active        | N/A                | Not sooner than July 24, 2027      |
```

“not sooner than”不是承诺的退休日期；这里只用于确认查询当日仍为 Active。模型概览 S15 已展示更新型号，但不据此自动升级已有建议值。

### DashScope 地域与旧域名（S7）

> “其中 {WorkspaceId} 为您的业务空间 ID，可在阿里云百炼控制台的业务空间详情页面查看。现有域名仍可正常使用。”

> “百炼 API Key 按地域绑定。调用某个地域的 base_url 时，必须使用在同一地域创建的 API Key；使用其他地域的 API Key 调用该地域 endpoint 会被鉴权拒绝。”

页面列出北京、新加坡的专属域名与弗吉尼亚 `https://dashscope-us.aliyuncs.com/compatible-mode/v1`。本文没有创建/读取任何 WorkspaceId 或 API Key。

### Kimi 平台与 API 主机（S9）

> “建议在调用对话补全前查询本接口，以确认目标模型是否可用及具备对应能力。”

> “并确认 API Key 与调用端点所属平台一致（`platform.kimi.com` 与 `platform.kimi.ai` 的 Key 不可混用）。”

页面的实际请求示例仍为 `https://api.moonshot.cn/v1/models`。

### Ollama 本地认证（S14）

> “The client requires an API key value, but Ollama ignores it.”

这段属于本地 Ollama server 示例；同一页的 direct cloud access 另要求 OLLAMA_API_KEY。Iris 用自己的 HttpClient，不必为了模仿 SDK 示例写入一个伪造的本地 Key。

## 5. 本轮实际读取的官方来源

- **S1** [OpenAI：List models](https://developers.openai.com/api/reference/resources/models/methods/list.md)。先从官方 llms.txt/API reference 索引定位，再读取该页。
- **S2** [Claude：List Models](https://platform.claude.com/docs/en/api/models/list)。
- **S3** [Claude：Model deprecations](https://platform.claude.com/docs/en/about-claude/model-deprecations.md)。
- **S4** [DeepSeek：Your First API Call](https://api-docs.deepseek.com/)。
- **S5** [DeepSeek：Models & Pricing](https://api-docs.deepseek.com/quick_start/pricing/)。本稿只引用基础地址/型号信息，未将当前价格写入产品。
- **S6** [DeepSeek：Lists Models](https://api-docs.deepseek.com/api/list-models/)。
- **S7** [阿里云百炼：OpenAI Chat 接口兼容](https://help.aliyun.com/zh/model-studio/compatibility-of-openai-with-dashscope)。
- **S8** [Kimi：快速开始](https://platform.kimi.com/docs/get-api-key)。旧 platform.moonshot.cn 文档链接实际重定向至此。
- **S9** [Kimi：列出模型](https://platform.kimi.com/docs/api/list-models.md)，从其官方 docs/llms.txt 定位。
- **S10** [智谱：OpenAI API 兼容](https://docs.bigmodel.cn/cn/guide/develop/openai/introduction.md)，从官方 llms.txt 定位。
- **S11** [SiliconFlow 中国站：获取用户模型列表](https://docs.siliconflow.cn/docs/api/models-get)，从当前中国站文档首页的实际链接定位。
- **S12** [SiliconFlow 国际站中文：快速上手](https://docs.siliconflow.com/cn/userguide/quickstart.md) 与 [模型列表](https://docs.siliconflow.com/cn/api-reference/models/get-model-list.md)。用于确认 `.com` 与 `.cn` 文档/API 主机的区别，不拿它替换中国站预设。
- **S13** [Gemini：OpenAI compatibility](https://ai.google.dev/gemini-api/docs/openai?hl=en)。
- **S14** [Ollama：OpenAI compatibility](https://docs.ollama.com/api/openai-compatibility)，由官方 /openai 路径重定向。
- **S15** [Claude：Models overview](https://platform.claude.com/docs/en/models/overview)。

## 6. 可复查材料与验证范围

公开文档抓取及提取材料保留在 `C:\Users\28910\AppData\Local\Temp\iris-t019b-official-prep`：每页 `.raw`、`.txt`、`.meta.json` 记录正文、文本与实际 URL/HTTP 状态；fetch_docs.py 与 followup*.py 仅执行无认证的官方文档 GET。

遇到的情况如实处理：旧 SiliconFlow `.cn/cn/api-reference/...` 和 `.cn/llms.txt` 重定向后 404，随后从官方首页找到当前有效接口页；Claude deprecations HTML 路径两次超时，Markdown 版本读取成功。HTTP 200 的旧 Moonshot 文档实际跳到快速开始，未将其冒充原模型接口页，随后按新官方索引读取了真正的接口文档。

没有发送翻译或模型 API 请求，没有读写用户凭据、配置、注册表或本地 Ollama；没有运行共享构建/测试或修改 T-019A 文件。供应商文档会变化，B 签发时复核 DeepSeek 候选预设、任何未确认列表端点与必要的账户/地域提示，再批准具体改动。
