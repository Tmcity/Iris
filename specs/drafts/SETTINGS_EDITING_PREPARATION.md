# T-018 / T-019 设置编辑与提交准备稿

> 2026-09-30，Codex。基于既有设置/密钥/Registry 契约及 App 实现核查。
> T-018 常规编辑部分已于 2026-09-30 转为正式 SETTINGS_EDITING.md / CCR-010。本文保留准备依据；Provider/凭据提交及 T-019 公共端口部分仍为待审草案，不提前实施。

## 1. 现有能力与实际缺口

已具备：深度不可变的 ISettingsService.Current；在写锁中针对最新值执行 mutate 的 UpdateAsync；同步原子写入的 ISecretStore；不可变凭据集读取；Registry 瞬态租约；IProviderConfigurationObserver 发布屏障；ActionDispatcher；主题服务和托盘外壳。

尚缺：SettingsWindow/Presenter、独立草稿与字段冲突策略、主题预览生命周期、AutoStartService、统一保存结果、Provider 写入协调器、服务配置页面。

已核查的具体依据：

- JsonSettingsService.UpdateAsync 在取得 gate 后读取 _current，再调用 mutate；因此可在该回调中重新检查冲突并应用增量，不需要先改公共设置服务。
- TrayIconController 的 DEBUG 菜单已经能独立更新 General.Theme。这是陈旧草稿覆盖新值的实际并发入口，但不是 Release 托盘已开放主题编辑的证明。
- ThemeService 当前订阅 Changed，并排队 Apply(e.NewSettings.General.Theme)；它没有“预览所有者”概念。单纯在 UI 调用 Apply 不能完整解决预览与迟到 Changed 回调的竞争。
- GeneralSettings 同时包含 LaunchAtStartup、UiLanguage、CheckForUpdates 等字段，但当前 App 未发现后两者的完整运行接线。不能因模型里有字段就在 T-018 展示没有作用的开关。
- SettingsWindow 按 ARCH 关闭即释放，Presenter 为单例；翻译窗口才是关闭即隐藏，两者不能混用生命周期。

## 2. T-018 建议交付边界

交付 SettingsWindow 外壳、已开放的常规项、SettingsWindowPresenter、Core/Workflows/OpenSettingsHandler、托盘“设置”真实接线及 AutoStartService。托盘入口经 ActionDispatcher 路由，不复制第二套窗口创建流程。

首批常规项建议限于已有或本单实际交付的能力：Theme、LaunchAtStartup、ShowStartupNotification、LogLevel。UiLanguage 与 CheckForUpdates 的运行能力没有交付前不开放控件；保存仍须保留它们的当前值。其它导航页面不做可点击空占位，T-019 再开放 translation。

Presenter.ShowAsync 重复调用时复用当前窗口并导航/激活，不重置未保存草稿。不存在或尚未开放的 pageKey 回到已交付的 general 页面，不能因未知 key 构造未来页面。窗口仅在 UI 线程按需创建；DI 构造不要求 Application/Window 已存在。

每次新窗口有自己的编辑会话、ViewModel 和订阅/异步操作所有权；Closed 后清理，不能让单例 Presenter 永久持有已关闭窗口及旧草稿。迟到验证/保存回调先校验窗口会话身份，不重新打开旧窗口。

## 3. 草稿、脏字段与并发更新

编辑会话保留 base 快照、独立可编辑 draft、实际修改集合。嵌套列表/字典必须脱离 Current；不把 `Current with { }` 当作深拷贝。保存期间冻结一次提交输入，界面继续编辑的策略必须明确，建议保存期间禁止再次提交和改动该份提交草稿。

**常规字段按字段合并**：在 UpdateAsync 的 mutate 中检查最新 Current。未编辑字段沿用 Current；已编辑字段若 Current 与 base 不同且也不等于目标值，返回明确冲突，不自动覆盖。若 Current 已等于目标值，可按无操作处理。比较发生在 mutate 内，而非排队之前。

例子（设计推演，非已实现测试）：

- 打开窗口时 Theme=Light，随后 DEBUG 托盘改为 Dark；窗口只改 ShowStartupNotification。保存后仍应是 Dark，而非写回旧 Light。
- 窗口把 Theme 从 System 改为 Light，期间其它入口已改为 Dark。它是同字段冲突，不能悄悄选一方。
- 保存常规页不应把运行时更新的窗口位置、Provider 顺序或其它隐藏字段恢复为打开窗口时的值。

**Provider 以单个 Profile 的相关配置为冲突单元**：BaseUrl/模型/协议选项/凭据引用是相关的一组，不宜对它们做无条件的逐字段拼接。最新 Profile 与 base 发生实质变化时拒绝旧编辑，要求重新载入/重做明确选择。删除后迟到的编辑不得用 upsert 把 Profile 复活。

排序单独记录基线 ID 集合/顺序，修改最新 Profile 的 Order 而不是替换整个旧 Profile 列表；并发增删或另一排序冲突须明确处理。未编辑的 Profile 全部保留最新值。

ISettingsService 的 Normalize 仍是最终持久化约束；UI 需在提交前反馈可预见的无效输入，避免“保存成功但模型被静默清空/禁用”的体验。提交后重新以实际发布值作为 base，不继续使用规范化前的 DTO。

## 4. 主题即时预览

建议 ThemeService 增加 App 内部的预览所有权，保持业务契约不变：

- 持久主题来自最新 Current；一个有效设置窗口可叠加一个预览主题。
- 主题选择立即预览，但不调用 UpdateAsync。取消/关闭只结束本窗口的预览，恢复**此刻 Current 的主题**，不是打开窗口时的旧值。
- 已排队的 Changed/SystemEvents 回调执行时计算“当前预览或最新持久值”，不能用过期事件中的主题覆盖正在预览的值。
- 旧窗口的 Dispose/延迟回调不能清除新窗口预览；用会话身份判定预览所有者。
- 保存成功后持久值与预览一致，再结束预览；保存失败仍保留当前草稿/预览供用户修正，取消时再恢复持久值。

这需要修改 ThemeService 的状态归属及测试，不应只往 ViewModel 添加一个 Apply 调用就宣布完成“即时生效 + 取消”。所有主题操作都在 UI 调度边界内，事件处理器快速返回。

## 5. 开机启动：设置值与系统副作用分开

IAutoStartService 已有 IsEnabled/SetEnabled；General.LaunchAtStartup 也已存在。建议 JSON 是用户保存的意图，Run 注册是随后应用的系统状态：

1. 常规草稿通过冲突检查，一次 UpdateAsync 保存意图。
2. 在后台对本次明确修改的 LaunchAtStartup 应用注册；失败返回“已保存，但开机启动应用失败”，不能说整次未保存或虚假回滚 JSON。
3. 打开页面可读取实际注册状态并提示不一致；重试只应用已保存的意图，不再次覆写草稿或制造无意义设置版本。

默认不在每次启动时自动重写 Run 注册，避免抹掉用户在系统侧作出的改变。IsEnabled 的准确注释应限定为“本应用预期启动命令已注册”，不能保证 Windows 会实际执行；不修改 StartupApproved 或绕过系统禁用策略。

只操作 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 下约定的 Iris 值，不使用 HKLM/RunOnce，不删除其它值。启动命令为带引号的实际 Iris apphost 路径；不注册无参数的 dotnet.exe，不拼接用户文本或凭据。路径无效或命令超过文档规定的 260 字符时明确失败，不截断。

当前尚未引入安装/更新框架，T-018 不伪造不存在的 Update.exe/stub；T-039 必须复核稳定启动路径及升级/便携路径迁移。本阶段验收不承诺应用移动后旧路径仍有效。

自动测试用隔离的注册表适配器/测试子键，不能把用户真实 Run 值当夹具。真正的注册/注销验证须单独说明方式，不把 fake 写入称为已修改系统启动项。

## 6. T-019 的统一保存流程

T-018 的 VM 应通过一个小型内部保存入口操作常规草稿，避免把持久化流程散进多个控件。T-019 再批准 Provider 编辑协调端口并复用既有 Core 凭据解析能力，接入同一保存入口。不要先保存 General、再独立保存 Providers，却把两次 UpdateAsync 宣称为全窗口原子保存。

保存状态至少区分：未提交（校验/冲突/写盘失败/提交前取消）、已提交且应用完成、已提交但应用/清理未完成。窗口不能仅凭“方法抛了异常”判断是否已保存；具体返回类型及层级落点须在签发前明确，不能自行改公共契约。

Provider 提交继续遵循已批准的 PROVIDER_REGISTRY.md §2.2：

1. 取得串行 Provider 编辑权，检查本次编辑的 Profile 基线/意图。未修改的普通设置可继续由其它入口更新；所有受支持的 Provider 引用写入走同一协调器。
2. 更换/清除凭据时创建新的 `cs-<guid>`，把完整凭据集作为一个 SecretStore 值写入；Set/Remove 是同步 I/O，必须在后台。整个 JSON 保留显式 null，不能复用会省略 null 字段的设置序列化选项。
3. 一次 UpdateAsync 在最新 Current 上重新核对冲突，合并所有本次被编辑字段/Profile，并原子发布新引用。这是提交点。
4. 提交前失败，仅尽力删除本次创建且仍未被引用的密文；不动原配置/旧凭据。多 Profile 批量编辑应维护本次 staged ID 清单，不能只回收最后一个。
5. 提交后等待所有已接入 IProviderConfigurationObserver.RefreshAsync 实际退出并成功，再清理无人引用的旧值。UI 的迟到取消不能把它报告成未提交；也不能用 WaitAsync 超时遗忘仍在进行的解析/写入。
6. 删除前检查最新的所有翻译/OCR Profile，包含禁用项；共享引用仍有人使用就保留。legacy 键也须检查其它同 ID legacy Profile。此处不扩展通用密文枚举/垃圾回收框架。
7. 屏障或清理失败保留旧值，返回已提交后的未完成状态；重试应用/清理时不再生成新凭据 ID。进程崩溃留下无引用密文属于已接受的边界，不为补偿而恢复陈旧设置。

Changed 处理器只失效/排队，不能在设置写锁内同步刷新 Registry。编辑协调器持有自己的串行权，不等于可以跨 await 持有 ISettingsService 内部锁。

## 7. 凭据 UI 与草稿测试

凭据意图明确分为 Keep、Replace、Clear，不能把密码框空串同时解释为“不改”和“删除”。Clear 仅改变草稿，保存时才写新的显式空凭据集；不能回退为 CredentialSetId=null，否则会重新启用 legacy 键。

Keep 在冲突检查通过后沿用最新有效引用；不能把过期草稿中的旧 ID 重新发布，因为旧值可能已经被清理。坏的显式引用不回退 legacy。显示“已保存/未配置”状态不需要默认把明文密钥装入密码框；确需使用凭据的测试由 Core 侧编辑流程复用已验收解析规则。

TestAsync/模型列表针对草稿的瞬态 Registry 租约，不写 settings/secrets，不清缓存，不替换活动 Provider。每次测试捕获草稿 revision；关闭窗口或再编辑后，迟到结果不能写回新草稿。取消并等待真实工作退出后释放瞬态租约；不能关闭窗口就直接 Dispose 仍在使用的 Provider。

所有日志/异常/模型 ToString 都避免凭据正文，保存结果只包含安全状态与诊断；不得把明文暂存到普通配置、临时 JSON 文件或报告。测试使用假凭据及假 HTTP；本轮没有读取用户密钥或访问真实服务。

## 8. 建议的验收矩阵

| 阶段/交错 | 必须证明 |
|---|---|
| 重复打开、切页、关闭再开 | 同时只有一个窗口；重复 Show 不重建草稿；关闭后订阅/异步工作不泄漏 |
| 深层字典/列表修改与取消 | Current 不变；新窗口不继承旧未保存编辑 |
| 不同字段的外部更新、同字段冲突 | 保留未编辑的最新值；冲突不写盘，不复活已删除 Profile |
| 主题预览与迟到 Changed/SystemEvents | 当前预览保持；取消恢复最新持久主题；旧窗口回调不覆盖新窗口 |
| JSON 写盘失败、Run 注册失败 | 前者未保存；后者已保存但应用未完成，状态与实际数据一致 |
| 空密码框、Keep/Replace/Clear | 三种意图无歧义；清除后不触发 legacy 回退 |
| staging 后冲突/取消、发布后取消 | 前者不动旧配置并回收自身 staged 值；后者承认提交且正确完成/报告后续状态 |
| 一观察者失败、共享/禁用/OCR 引用 | 不提前删旧凭据，不把只刷新一个 Registry 当全体屏障 |
| 草稿测试中编辑/关闭 | 迟到结果被丢弃，实际调用退出后才释放租约，无持久化副作用 |
| 保存后继续编辑、应用重试 | base 更新到实际发布值；重试应用不重复生成凭据集或覆盖新草稿 |

以上是待实现的测试要求，不是本轮已运行的产品测试。本轮未启动设置窗口、写注册表、读写真实凭据或运行性能测试。

## 9. 签发前还需确定

- T-018 内部保存结果与主题预览服务的具体形状；T-019 的 Core 配置编辑端口及批准的 CCR。
- Provider 深度冲突比较的实现归属、批量排序意图及 UI 冲突文案；不能照搬 record.Equals 比较可变集合引用。
- 保存期间关闭窗口的明确策略。推荐禁止重复保存，并保持保存会话存活直到结果明确；应用退出按已提交/未提交分别处理，不以“取消”假装已完成回滚。
- T-039 接管启动命令路径的迁移要求；T-038/发布功能交付前不开放无效果的语言/更新控件。

现有 T-022A 工单、契约和生产/测试代码均未因本准备稿改变。后续先完成其复审，再细化 T-018。

## 10. 本轮核查来源

仓库：ISettingsService、ISecretStore、IProviderConfigurationObserver、ProviderRegistryModels、PROVIDER_REGISTRY.md §2.2、JsonSettingsService.UpdateAsync、ThemeService、TrayIconController DEBUG 主题入口、GeneralSettings、ARCH 设置/DI 约束与 BACKLOG。

新增官方依据：[Run and RunOnce Registry Keys](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)，实际读取确认：Run 在登录时运行、命令行值最长 260 字符、多个程序的启动顺序不确定，Windows 可能延迟执行。该文档不是“注册后必定立即启动”的保证。
