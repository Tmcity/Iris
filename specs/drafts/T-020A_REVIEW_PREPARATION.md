# T-020A 独立验收准备

> 2026-10-03，Codex。A 单由 Gemini 实施；本稿细化 HOTKEY_SETTINGS_EDITING.md 的既有要求，不新增工单范围。当前期望数据不是实现通过证据。

## 1. 独立规则数据

临时目录 `C:/Users/28910/AppData/Local/Temp/iris-t020a-review-prep/hotkey-edit-expected-vectors.json` 保存独立期望。96 个 canonical 名及 VK 对照来源沿用已核查的 Microsoft 官方表/T-017 数据；启用规则沿用 1536 组既有数据，禁用/清空期望直接按 T-020A 正式规范生成，不读取待测实现。

实际生成结果：

```text
EDIT_POLICY_EXPECTED cases=3158 valid=2972 invalid=186 canonicalKeys=96 productVerified=False
SHA256=07FB8BEAC11E768CDE2029E6A0CB8AC4F46A52F2F1F53365CBC8F1541DCDA329
```

包括：全部 96×16 启用组合；96×16 禁用且带空白/大小写变体的键；null/空/空格/制表换行在启用/禁用下的输出；未知 flags 与非白名单字符串反例。每个有效项含 normalized 的完整键名/modifiers/enabled 期望，而不只比较 bool。成功总数为规则推导结果，不代表有这么多系统组合可注册。

## 2. 事务场景矩阵

| 范围 | 交付后独立核对 |
|---|---|
| 捕获 | CommitAsync 返回 Task 后修改原 HotkeyChanges 列表，已接纳目标不变；重复/null 项不能在 Clone 中被静默过滤 |
| Base 缺失 | 无默认 Action 的缺失基线可添加合法绑定；缺失与明确禁用空条目不能混为相等 |
| 语义比较 | canonical 大小写/空白不产生伪冲突；Current=Target 时幂等；无关 Action 和其它配置取提交时最新值 |
| 新目标冲突 | 与合法启用项相同组合拒绝；两键互换按最终状态成功；未编辑历史重复不阻止无关保存 |
| 原子提交 | Hotkey+General+Provider 同一次 mutate；任一失败不留下部分新页面数据 |
| staging 前后 | 前置输入失败时凭据调用为零；凭据暂存后发生 Hotkey 同 Action 外部变化，在 mutate 复验中拒绝并回收暂存值 |
| 后续边界 | Hotkey-only 不访问密钥/Provider 屏障；混合 Provider 保留原回执和清理语义；提交后取消不能回退为未保存 |
| 重载 | 真实 JsonSettingsService 的禁用空绑定跨实例保持；启用空键/缺失默认项仍按既有 N2 修复，不新增 SchemaVersion |
| 脱敏 | 错误和 ToString 不输出原始无效键、整份草稿/计划或凭据 |

## 3. 验收方法与边界

收到最终提交且 Gemini 停止改动后再运行共享解决方案构建/测试。独立探针直接引用最终程序集或使用隔离副本，记录指纹；真实 Core 协调器与 JsonSettingsService 配受控端口，不编写第二份状态机冒充被测实现。

只用自有临时配置、fake 凭据/Provider、无真实 HTTP/系统热键/键盘输入/剪贴板/Run/性能脚本。阻塞场景先确认 entered，finally 放行并等待工作退出，避免以短 Delay 或测试函数返回代替生命周期证明。

T-020B 的录入 UI、暂停 lease、系统键盘行为不是 A 的通过项；A 通过后父任务仍未完成。B 准备见 T-020B_RECORDING_PREPARATION.md，不要求 Gemini 在 A 提前实现。

## 4. 纯策略开发源码快照初检

在不占用 Gemini 构建输出的前提下，复制 HotkeyBinding.cs、IHotkeyBindingPolicy.cs、HotkeyBindingPolicy.cs 至 `C:/Users/28910/AppData/Local/Temp/iris-t020a-policy-snapshot`；逐个源文件复制前/后与副本指纹一致。独立 net10.0 项目无仓库 ProjectReference、无新增包，不调用系统热键。

```text
HotkeyBinding.cs SHA256=622AB72193A6008D100D0A6F7096BCC56A82A91A32CD68D41D113A5DA5C00E36
IHotkeyBindingPolicy.cs SHA256=D9AA855D5D6550E732AB8A150DD221B815967BCC142D97D74A2A31779117B1B8
HotkeyBindingPolicy.cs SHA256=509EEC44D66451316F3190E91D887658D0DE33D93669F38B5D90558F9814FC4B
```

运行该目录 Probe.csproj（Release，RestoreSources 限于自有本地目录），退出代码 0，实际输出：

```text
POLICY_SOURCE_SNAPSHOT cases=3158 mismatches=0 nullBindingRejected=True
SCOPE sourceSnapshotOnly=True coordinatorVerified=False persistenceVerified=False nativeCalls=0
```

该快照的 bool 结果、规范化完整绑定和 null 输入符合独立期望；不是最终交付验收，不证明 DI、计划捕获、事务或重载。最终文件变化时需更新副本复测。

## 5. 开发中测试证据核对

发现新增 Core 矩阵/Platform 符合性用例依赖固定用户临时文件，缺失时直接 return；已直接向 Gemini 排队补充原工单自检，要求仓库自有测试资源或独立向量，并对缺失必需数据明确失败。提供临时期望数据便于对照，不意味着可以把它作为不可移植的必测依赖。

初版 DefensiveCapture_CallerModifyingList_DoesNotAffectCapturedPlan 使用快捷键-only 计划，无占用编辑权/异步闸门；TestSettingsService.UpdateAsync 在未设置 AfterUpdateHookAsync 时同步完成。因此 CommitAsync 可能在返回 Task 前已提交，随后 list.Clear 无法检出“捕获失效”的回归。交付应将真实协调器的提交阻塞在捕获之后、mutate 之前，明确断言 commitTask 未完成，再修改列表；finally 放行并等待所有任务。不能用已提交后修改输入来证明在途别名隔离。

上述为开发中代码观察，不是最终交付结论；待 Gemini 完成自检及提交后重新核对。
