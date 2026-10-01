# 翻译服务设置页与全窗口保存

> 2026-10-01，Codex；ARCH v1.24 / T-019B。基于已验收 T-018、T-019A 与 PROVIDER_EDITING.md；公开端口不扩展，预设值修订见 CCR-012。

## 1. 交付范围

在现有 SettingsWindow 中开放 general、translation 两页。重复 Show 复用窗口与全部草稿，切页不保存也不重建；未知/未开放 pageKey 仍回 general。沿用 T-018 的 Presenter、会话、局部回滚及退出所有权，不另建一套设置窗口或编辑对话框生命周期。

translation 页提供全部翻译 Profile 的列表（含禁用项）、选择编辑、添加预设、删除、启用、拖拽排序。上移/下移可作为同一排序操作的键盘替代。详情编辑显示名、BaseUrl、凭据意图/PasswordBox、可编辑模型、超时、最大输出、OpenAI 温度、Prompt 选择及对应高级选项。Kind 由预设决定并只读；不显示内部 ID/凭据集 ID/回执 ID，不引导用户手工修改 secrets.dat。

保留 760×520 初始窗口及 600×440 最小尺寸，长表单使用滚动/折叠，主要操作可访问；沿用 WPF-UI 与 UiText，不添加 MVVM/拖拽包。其它设置页、Prompt 正文编辑、全局翻译参数、真实翻译结果窗口仍属后续工单。

## 2. 草稿所有权与意图

窗口拥有一个编辑会话，包含四项常规字段、全部 Profile 的 base/target、凭据意图、增删记录及基线顺序。Current 只读；目标字典/列表独立复制。可以拆 App internal ViewModel/帮助类，但所有草稿共享同一次保存/冻结/取消和窗口身份。

- 新 Profile 使用 tp- 加 Guid(N) 前 12 位小写十六进制 ID，生成后不可编辑；选择预设只填草稿，不调用 Core 提交或任何网络接口。null SuggestedModel 显示待填写模型，不填虚构占位模型。
- Existing Update/Delete 保留打开/重建基线时的实际 Profile；只改显示名不能清空未编辑的另一组 Options 或隐藏字段。仅 Order 的变化独立生成 TranslationOrderChange，不伪造内容更新。
- Add 后尚未保存又 Delete，消除本次 Add 并释放该草稿明文引用，不生成持久 Delete；删已保存 Profile 只记录删除意图。保存前不删除任何密钥。
- 拖拽/上移下移作用于全部可配置 Profile（含禁用），生成最终 ID 顺序；Add/Delete 必须带 Core 所需完整 BaseIds/TargetIds。反复移动回基线、字段改回原值时正确清 dirty。
- 外部 Changed：未编辑常规字段/未编辑 Profile 和未编辑顺序跟随最新值；被编辑的 Profile 保留原 base 和 target，供 Core 检查冲突。外部删除、同 Profile 改动或顺序冲突不自动 upsert/复活，不用旧列表覆盖新增内容。显式“重新载入”放弃本窗口草稿并按 Current 重建。
- 输入立即进入草稿，保存不能因 TextBox 尚未失焦而捕获旧值。数值编辑可保留未完成文本及错误提示；保存/翻译测试完整校验，模型列表仅校验实际请求需要的字段。

新增、删除、选中项变化、内容/凭据/顺序编辑、保存重建、外部更新、关闭均使不再适用的异步结果失效。每次有效草稿变更递增 revision；操作身份还包括窗口会话、Profile ID 和请求序号，不以完成顺序决定哪个结果有效。

## 3. 凭据 UI

明确提供 Keep、Replace、Clear，界面使用可理解的“保持现有凭据”“替换 API Key”“保存后清除凭据”文案。已有项默认 Keep，密码框不读取或回填持久密钥；不能根据引用非空就声称密钥有效，提示可用“保持现有凭据（如有）”。新项默认 Keep 表示尚未提供凭据。

Replace 使用 PasswordBox 输入非空新 API Key，传完整 ProviderCredentials(apiKey, secretKey:null)；本单两个翻译协议只使用 ApiKey。Clear 是独立的显式草稿选择，传 Core Clear，不通过空密码框推导。Keep 沿用整个既有凭据；共享 OCR/其它 Profile 的旧凭据由 Core 判断回收，UI 不直接访问 SecretStore。

已提交（含 ApplyPending）后清空本次密码输入和草稿明文引用，回到 Keep；未提交失败可保留供修正，Reload/Cancel/删除未保存项/Dispose 清除其引用。切到另一服务不能把上一服务的密码/结果带过去。托管字符串只能解除引用，不声称安全擦除。

不得把密码写进普通配置、日志、异常正文、UI 调试输出或生成的 ToString；不将明文放入进程级应用状态。生产草稿测试只在用户显式点击后调用 Core，不用真实用户 Key 作自动化夹具。

## 4. 表单校验与模型/Prompt

有效保存值遵循 PROVIDER_EDITING.md：超时 5–600，tokens 空或正整数，温度空或有限 0–2；未配置凭据可保存，禁用半成品按 Core 规则处理。UI 不以服务已连通或 Descriptor.IsConfigured=true 作为保存前提。

模型输入始终可编辑。获取列表不会自动覆盖当前选择或把首项标为已保存；保留大小写不同 ID，不按名字猜测模型能力。无列表能力/HTTP 失败时显示安全错误并保留手工输入。列表成功只证明列表获取，不证明所有型号都能完成当前聊天请求。

模型发现不依赖 Model、Prompt、ChatCompletionsPath、MaxTokensField、Effort 等未参与该请求的字段；不能将全表单 IsValid 用作 CanGetModels。若某个不相关数值文本尚不能解析，可以在仅供模型列表的 DTO 投影中用 null/既定默认值占位，不修改草稿、不填虚构模型；该字段绝不据此允许保存或翻译测试。认证头/地址/超时/身份等实际请求条件始终校验。

Prompt 下拉为“跟随默认”（null）和现有内置/自定义模板；使用实际 ID，不编辑模板正文。原选模板已被外部删除时提示重新选择，不能悄悄选另一模板。

Anthropic 隐藏温度。模型含 haiku（OrdinalIgnoreCase）时在草稿中清空 Effort 并提示，旧测试失效；切回其它模型不擅自恢复旧 Effort。反代地址以 /v1 结尾给出既定 SDK 提示，不直接改写用户地址。OpenAI 高级项沿用 Stream、IncludeStreamUsage、MaxTokensField、StripThinkTags、ChatCompletionsPath、非敏感 ExtraHeaders。头字段重复/格式错误显式拒绝，不能默默保留最后一行。

## 5. 一次提交与系统应用

App 的内部保存入口接收完整 SettingsEditPlan，委托已验收 ISettingsEditCoordinator.CommitAsync。可保留 GeneralDraftSnapshot 的适配重载用于常规调用，但必须转换为同一计划走同一入口；不能保留另一条 General.UpdateAsync 写入路径。UI 不调用 SecretStore，也不重写 Core 的 staging、冲突、屏障或回收算法。

点击保存时同步固定全窗口计划、冻结四个常规字段及全部 Provider/凭据/排序入口（控件与 VM 方法都守护）。保存期间不再接纳新测试或二次提交。若有草稿测试/模型列表正在运行，先请求取消并等待其实际退出，然后再提交固定计划；等待期间 UI 消息循环继续运行并显示状态。停止/关停发生在等待阶段时不再进入 Core 提交。

Core 返回状态决定保存事实：

| Core 结果 | App 行为 |
|---|---|
| Uncommitted / 提交前取消 | 保留草稿/凭据输入和冲突信息，不执行系统自启写入 |
| Applied | 按最新 Current 重建基线；如本次显式编辑 LaunchAtStartup，后台执行系统应用 |
| ApplyPending | 同样视为已提交，先将回执交给进程级状态持有，再重建基线/清除明文；必要自启应用独立执行 |

系统自启应用失败不回滚 Core 的 JSON，也不抹掉 Provider 回执；Provider 重试成功不清除自启未生效状态，反之亦然。仅显式 LaunchAtStartup 编辑触发本次系统写入，重试只用最新已保存意图。继续按 T-018 已验收规则核对应用结束后的最新 Current，并使旧查询结果失效。

“已保存、服务应用/清理待重试”“已保存、开机启动未生效”等结果要明确；不能把任一提交后故障统一显示为未保存。刷新/清理重试不提交未保存草稿，不重建/清除其 dirty 或冲突状态。

## 6. 待应用回执跨窗口保留

App 内增加一个纯内存单例状态服务，持有未完成 ISettingsApplyReceipt 和安全结果摘要；不持有窗口、VM、原计划、PasswordBox、凭据、Provider 或租约。构造无 I/O、没有自动联网重试。该服务与 scoped 保存入口分离，关闭重开设置窗口仍能看到待应用状态。

Core 返回 ApplyPending 后，由保存服务先登记回执再把结果交给 VM，即使 VM 已停止展示，也不能丢掉已提交后的清理责任。回执只在 Core.RetryApplyAsync 确认完成后移除；失败更新安全提示并保留。一次“重试服务应用”可顺序处理当前快照中的待办；不得重复调用 Commit 或生成新凭据集。

待应用提示/重试入口不依赖相关 Profile 仍在列表中；删除最后一个服务后的清理失败也必须可见、可重试。普通关闭不触发自动重试或密钥删除。

为避免长期失败造成无限内存记录，固定最多 **32 个未完成回执/在途预留名额合计**。有 Provider 或排序变更的保存必须在 Core 调用前原子预留一个名额；没有容量时拒绝本次提交并保留草稿，提示先处理待应用事项，不能先提交后丢回执。Applied/Uncommitted/提交前取消释放预留；ApplyPending 将预留转换为正式回执。General-only 不需要该预留，仍可保存常规项。

容量仅是 App 内部实现常量，不增配置字段/公开契约；不枚举或删除孤立密文来腾位。进程退出后的残留沿用 Core 已接受的边界。状态通知可能跨线程，VM 经 IUiDispatcher 和当前会话检查更新；窗口关闭只退订，不清除待办。

## 7. 草稿操作与生命周期

每个窗口最多一个实际运行的 TestDraft/ListDraftModels 操作和一个“最新等待请求”。再次显式请求先取消旧操作，等待真实退出后才启动最新项；中间等待项被替换时以取消完成，不能累积队列。编辑/切换/删除草稿只失效结果并请求取消，不自动发新网络调用。

任务捕获 Profile、凭据意图和 revision。结果只有在 UI 回调实际执行时窗口/选中 Profile/revision/请求序号仍匹配才显示；过期错误也不提示，旧列表不能覆盖新选择。普通禁用 Profile 仍可测试，只要该请求自身配置有效。

Test 的延迟/译文/安全错误展示来自 Core 返回值；不把获取模型成功当作测试翻译成功。UI 不自行创建 HttpClient、读取密钥、构造 Provider 或手动 Dispose Core 借出的实例。

所有新增操作、等待项、查询、保存和应用重试接入 T-018 的准入与完整退出协议。普通关闭、Show 失败回滚、正常退出均停止接纳、请求取消、观察实际工作与必要 UI 收尾，再释放 scope；不能只隐藏窗口后遗忘操作。Core 已进入提交后阶段时承认其结果并保留回执，不能以取消伪装未保存。

## 8. 预设批准值与依据

使用 TranslationProviderPresets.All，不在 UI 复制第二份表。CCR-012 仅改变 DeepSeek 的新建建议：BaseUrl 为 `https://api.deepseek.com`，SuggestedModel 为 null，用户通过模型列表/手输选择。已有 Profile 的地址和型号一律不迁移；没有证据声称旧值必然失效。

2026-10-01 已再次读取 [DeepSeek 入门](https://api-docs.deepseek.com/)：当前 base_url 为上述地址，示例型号为 deepseek-flash。选择 null 是本项目的初次配置策略，不把单个不断变化的型号当永久默认。

其它预设值保留。再次读取的 [Claude 模型状态](https://platform.claude.com/docs/en/about-claude/model-deprecations.md) 仍将 claude-opus-5 标为 Active，保留现有建议。DashScope 默认北京旧域名的可用性、地域 Key 与 Kimi/SiliconFlow 中外站区别、Gemini/Ollama 的列表接口依据均记录在 T-019B_PRESETS_PREPARATION.md。地域/账户说明通过 UI 文案表达，不自动切换 API 主机、试用用户凭据或填入未替换的域名模板。

## 9. 验收重点

以实际设置窗口、真实 Dispatcher、真实 Core 协调器/JsonSettingsService/Registry 加受控 SecretStore、Factory/HTTP、自启端口验证主要接线。至少证明：

- 两页导航/重复 Show 不丢草稿；尚未交付页不开放；最小与默认尺寸操作可达，实际控件未失焦输入进入保存。
- 常规 + 多 Profile + Key 意图 + 排序一次 UpdateAsync，未编辑字段保留；取消/Reload/新增后删除不产生持久副作用；冲突保持草稿，成功按最新 Current 重建。
- Keep/Replace/Clear 无歧义、无密钥回填/明文 settings/日志；显式空模型可列模型，部分生成参数无效不挡列表，但保存/翻译测试正确拒绝。
- 测试→编辑/切换/删除/关闭、快速替换请求、保存中冻结、取消后真实退出和租约清理，无迟到回写/无限等待队列。
- Provider ApplyPending 和自启失败独立保持；关闭重开后重试回执，无重复 staging/JSON 发布；容量预留/取消释放及第 33 项提交前拒绝。
- Show 失败、普通关闭、正常退出期间存在以上任务时，不提前 Dispose；命令意外异常使用安全日志/状态，线程/窗口/临时目录失败路径有界清理。

自动化只用假凭据和隔离临时数据；不运行用户配置下的性能脚本，不联系真实服务、不写真实 Run/剪贴板。区分实际 WPF 消息循环、离屏渲染、模拟事件与人工键鼠/材质验证；没有做的项目如实记录。
