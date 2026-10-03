# 快捷键设置：统一增量提交与录入接线

> 2026-10-03，Codex；ARCH v1.28 / CCR-015。T-020 分 A/B 顺序交付；§1–§6 为 T-020A 正式规范，§7 为 B 单设计边界，尚不签发 UI 实施。

> 验收更新：T-020A-R2 / 3e892af 已独立验收，CCR-015 结案。B 单现按 HOTKEY_RECORDING_UI.md / CCR-016 签发；本文 §7 保留前期边界记录，具体实现以新规范为准，尚不代表 UI 已交付。

## 1. 范围与端口

T-020A 扩展 ISettingsEditCoordinator 的一次联合提交，增加快捷键增量、纯 Core IHotkeyBindingPolicy 及禁用空键持久化。T-020B 在 A 验收后接入设置页、HotkeyRecorderBox、录入暂停/恢复和注册状态；仅 A 通过不能结案 T-020。

SettingsEditPlan 保持现有位置参数构造签名，新增 init 属性 HotkeyChanges，默认空列表。HotkeyBindingChange(Action, BaseBinding, TargetBinding) 中 BaseBinding=null 表示基线无此 Action，TargetBinding 必须非 null。清除使用显式禁用空绑定，不通过删除字典项表达。每 Action 至多一个增量；未定义 Action、重复项、null 列表/条目为输入错误。准确声明在 CONTRACTS.cs，编辑 DTO 不持久化。

IHotkeyBindingPolicy 为纯 Core 单例，AddIrisCore 用 TryAddSingleton 注册；实现 internal sealed，构造无 I/O，不依赖 WPF/Win32/Provider/SecretStore。TryNormalize 失败返回 false 且 normalized=null，不抛业务输入异常或记录原始字符串；成功返回不可变规范绑定。VK 映射仍在 Platform，不引入 Core 到平台的依赖。

## 2. 输入规则

白名单为 HOTKEY_SERVICE.md §2 的 96 个键名：A–Z、D0–D9、F1–F24、NumPad0–9、Space/Enter/Tab/Escape/Insert/Delete/Home/End/PageUp/PageDown/Left/Up/Right/Down/Snapshot 及规定的 11 个 Oem 名称。Trim 后 OrdinalIgnoreCase 识别，输出规定大小写；拒绝组合字符串、数值 VK、数字字符串和其它别名，不使用宽松的 WPF enum 解析。

Modifiers 仅允许 Alt/Control/Shift/Win 四位，未知位拒绝新编辑输入，不静默掩码。Enabled=true：空键/F12 拒绝；至少一个 modifier，单独 Snapshot/F13–F24 例外；仅 Shift+字母拒绝。Enabled=false：空白/null Key 规范化为 (None, "", false)；非空 Key 必须是白名单名且 flags 合法，但不要求满足启用组合条件，因其不会注册。禁用 F12 或 Shift+A 可保留供编辑，重新启用须改成有效组合。

Core Policy 的启用判断必须与实际 KeyNameMapper.ValidateBinding 在相同 96×16 输入矩阵及非法边界上一致。KeyNameMapper 能解析 F12 的 VK 不代表可注册；A 不重写已验收的原生注册逻辑。

## 3. 持久化与 N2

ARCH §12.2 N2 修订：明确 Enabled=false 且 Key 为空白的非 null 条目保留为 (None, "", false)，包括有默认绑定的 Action；新实例 LoadAsync 后仍为空且禁用，不回填默认键。

其余规则保持：null 条目、缺失默认 Action、Enabled=true 且空键仍按原 N2 修复；未知 Action 与 flags 按 N12 处理。非空历史键名不在加载时按新编辑白名单整体删除/改写；新编辑严格验证，未编辑历史配置保持。SchemaVersion、HotkeyDefaults 和 JSON 字段格式不变，这是既有字段值域扩展，不加配置字段或迁移。

明确清空与缺失不同。B 单 Backspace 产生禁用空绑定；取消录入恢复录入前草稿，不产生清除。

## 4. 捕获、冲突与一次提交

CommitAsync 入口在第一次异步等待前验证结构并复制 HotkeyChanges 列表及 base/target；HotkeyBinding 是不可变 record。捕获后调用方修改原列表不得改变计划，不能过滤 null/非法项后伪装为空计划成功。

仅比较显式编辑 Action。绑定语义相等：Enabled、Modifiers 相同，Key Trim 后 OrdinalIgnoreCase 相同；禁用空绑定按 §3 canonical 状态比较。缺失与显式禁用空条目不同。Base 可包含历史非法键名，只用于比较，不妨碍把它修正为合法目标。

Current 若既不等于 BaseBinding、也不等于规范化 TargetBinding，则 Uncommitted / IsConflict=true / 固定安全文案。Current 已等于目标可幂等完成；大小写/首尾空白差异不制造冲突。输入与前置冲突在凭据 staging 前检查，最终冲突必须在实际 UpdateAsync mutate 内针对最新 Current 再检查。

只在最新 Hotkeys 字典替换编辑项，未编辑 Action 和其它节保留最新值。快捷键、General、Providers、排序及凭据引用只有一次 UpdateAsync 提交边界，禁止先保存快捷键再保存其它页。两 Action 交换组合按最终候选字典判断，不把逐条替换期间的暂时重复误判为冲突。

目标启用项与候选字典另一启用且可规范化合法绑定有相同 canonical Key/Modifiers 时，拒绝提交（InvalidArgument、安全重复组合文案）。只检查涉及显式 HotkeyChanges 启用目标的重复；未编辑历史重复/非法项不阻止无关 General/Provider 提交或其它不碰撞的快捷键编辑。配置校验不调用 RegisterHotKey，无重复不代表系统注册成功。

输入错误用既有 InvalidArgument，IsConflict 仅用于乐观并发冲突。错误不输出原始无效键、整份计划/settings 或凭据；HotkeyBindingChange.ToString 仅含 Action，不新增 ErrorCode。

## 5. 事务与发布边界

快捷键-only/快捷键+General 不读取/写入/删除凭据，不触发 Provider 屏障或生成其回执。混合 Provider 改动保留 PROVIDER_EDITING.md 的 staging、冲突复验、回滚、刷新与清理；staging 后出现快捷键冲突须回收本次暂存值，不修改已发布 JSON/旧凭据。

无快捷键编辑保留原行为；目标已相同不强制写版本/Changed。提交前取消不发布，提交后取消不改写为未保存。测试覆盖同步捕获、等待编辑权期间外部变化及提交点两侧取消。

SettingsEditStatus 和 ISettingsApplyReceipt 继续描述 Core 提交及 Provider 后续状态，不加入 HWND/热键错误。系统热键由 T-017 App 协调器观察 Changed 应用；失败不回滚已提交的混合事务，不表述为配置未保存。B 单再提供实际注册状态/重试 UI。

## 6. A 单证据

- 全白名单类别、规范化、F12/仅 Shift 字母/单键/未知 flags、禁用空与非空，纯规则无系统输入。
- 实际 Core 协调器：快捷键-only、General+快捷键、Provider+快捷键一次提交；未编辑项随最新值、same target 幂等、同 Action 冲突/不同 Action 合并、交换组合、重复目标拒绝。
- 非法计划在 staging 前零副作用；staging 后新冲突/取消回滚；提交后取消保持提交；列表别名修改不影响捕获。
- 真实 JsonSettingsService（临时自有目录）保存/关闭/新实例加载禁用空绑定；旧 N2 修复和默认值/版本保持。
- 与 Platform mapper 矩阵符合；所有闸门 finally 放行并等待真实退出。禁止真实 HTTP/热键/键盘输入/用户配置/剪贴板/Run/性能脚本。

## 7. B 单待定稿边界

B 仍须实现 HotkeyRecorderBox（含 Win 修饰键）、Esc 取消、Backspace 清除、草稿重复与非法提示、全窗口一次保存及即时应用。只为已交付动作开放可用入口，未来默认配置保留，不冒充功能已交付。

录入前需可重试暂停租约：撤销 Iris 注册组合并抑制待分发消息，暂停成功后才捕获；焦点离开/取消/销毁释放后按最新持久配置恢复，不能恢复陈旧快照，退出不重新注册。不得假定 WPF Handled 阻止 WM_HOTKEY 或 Shell 保留组合。App 内部接口、Win 键获取方式和真实输入验证在 A 验收后核查官方机制并定稿；A 不增加全局钩子或占位 UI。
