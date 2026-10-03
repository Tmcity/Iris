# T-020B 独立验收准备

> 2026-10-03，Codex。用户已转交 T-020B，Gemini 正在实施。本文仅细化 HOTKEY_RECORDING_UI.md 的既有验收要求，不是追加工单，不扩大实施范围。以下为待执行场景，不能当作测试通过证据。

## 1. 交付基线与证据规则

- 当前工单基线：e064a544eb7af00a7eeda007b9817f81ce809474；T-020A-R2 已验收，不因旧历史重新打开 A。
- 开始准备时 HEAD 仍为上述提交；准备期间已观察到 Platform 键盘适配器及 NativeMethods 的开发改动。本文不把工作区中途文件视为最终交付。
- B 本身包含架构师批准的 CCR-016 契约增量，不能错误要求相对于基线 CONTRACTS.cs 整体零差异。应核对差异仅为获批捕获声明，且与 Core 落地声明一致；既有公共签名、默认设置及 JSON schema 保持。
- 收到最终提交后核对完整哈希、改动清单、归档与实际工作区，再建立程序集/源码指纹。探针应引用最终程序集或经过指纹确认的隔离副本。
- 每个场景记录实际装配、进入/放行/完成证据及断言；尚未执行一律标记“未验证”。测试名和计数只从实际输出引用。

## 2. 平台事件轨迹

以下 down/up 均由 fake native 送进实际捕获实现。它们不是物理键盘操作，也不是另外编写的参考状态机。除特别说明外，owner 为有效的本进程模拟前台窗口，准备期已完成。

| 编号 | 受控事件 / 故障 | 必须观察的结果 |
|---|---|---|
| P01 | 构造服务、Host.Build、普通解析、打开 hotkeys 页但不点击录入 | 无 hook、消息线程或键盘状态读取；明确用户操作后才开始 |
| P02 | 初始 Enter 或修饰键仍按住；随后逐个释放 | Ready 前不能生成候选；启动键释放后才接纳新组合 |
| P03 | LCtrl down → F down → LCtrl up → F up | 候选固定为 Control+F；Ctrl 先释放不能改成单键；F up 前不完成 |
| P04 | LCtrl down → F down → F repeat → F up → LCtrl up | repeat 不创建第二候选/结果；最后参与修饰键释放后才完成 |
| P05 | LCtrl down → RCtrl down → F down → F up → LCtrl up → RCtrl up | 左右 Ctrl 合并为一个 Control 位；不能在另一侧仍按住时提前结束 |
| P06 | Alt 的 SYSKEY down/up；LWin/RWin 的合法组合分别录入 | 系统键路径可到同一规范化规则；正常完成消费对应 down/up；不据此宣称 Shell 保留组合实机可用 |
| P07 | F12、Shift+字母、未知键；释放后再输入合法组合 | 安全错误、保持可重录；未知原始值不进入预览/日志；后续合法组合仍成功 |
| P08 | Ctrl down → F down → G down；全部释放后重新录入 | 同时两个主键拒绝该候选，不能接受其中任意一个或永久卡住 |
| P09 | 无修饰 Esc / Backspace | 分别取消并保留旧草稿、清除为 (None, "", false)；WPF 接线另验不误关窗口 |
| P10 | owner 非法/已销毁/他进程；两个并发 Begin | 不取得不合法资源；只有一个 starting/active/failed-cleanup 所有者，无无界排队 |
| P11 | 初始化已 entered，取消与安装完成交错；回滚卸载首次失败 | Begin 不遗失未返回 session 的资源；服务 StopAsync 可对同一资源重试 |
| P12 | 捕获中前台转为另一模拟窗口，再送新键；或键一直按住时取消 | 取消并撤销新捕获；新窗口事件沿链、不回显；停止不无限等物理释放、不模拟补键 |
| P13 | nCode<0、未知消息、订阅者抛异常 | 必须转发的事件保持 CallNext 返回值；托管异常不跨 native 回调；订阅者故障不阻断清理 |
| P14 | 暂停通知接收，连续产生进度，再恢复通知 | Current 为不可变最新值、Revision 单调；至多一个待通知槽和一个 worker；旧消息不倒灌 |
| P15 | 逻辑结果已经完成，实际回调/消息线程收尾仍被闸门阻塞 | DisposeAsync/StopAsync 不提前成功；卸载成功本身不等于线程已退出 |
| P16 | 卸载失败，重复/并发停止；随后允许同一资源清理 | 保留句柄/委托/线程/会话；共享在途屏障；失败可重试，成功后幂等且服务所有的后台 fault 已被观察 |
| P17 | 服务异步 Dispose 后再 Begin；活跃资源时调用同步 Dispose | 永久关闭 Begin；同步清理不在 UI 等线程，也不虚报成功；正常 Host 路径先完成异步清理 |

对非法键和重复的“继续录入”，同时检查前一候选的消费集合、修饰键状态已正确回收，不能只断言最终 Binding。

## 3. 真实 App 暂停与身份交错

最小装配使用实际 HotkeyCoordinator、HotkeyService（fake native）、WpfUiDispatcher、ActionDispatcher 和已交付 Handler。替身放在原生/剪贴板/Provider 等外部边界，不能用立即执行的假 UI dispatcher 证明真实队列顺序。

| 编号 | 交错 | 观察点 |
|---|---|---|
| A01 | Iris 热键正常注册 → 请求录入；原生注销首次失败 | 准入先撤销；原 owner UI 线程完成注销后才允许 Begin；失败时 hook 安装数为零 |
| A02 | 热键已进入真实 UI 队列但未执行 → 暂停推进代次 → 放行 UI → 恢复 | 旧消息在实际 UI 执行点被拒绝，恢复后也不补执行；新代次动作仍可工作 |
| A03 | 真实 ClipboardTranslateHandler 的 fake 读取已 entered 且忽略取消 → 请求录入 | Begin 尚未调用，UI 可调度；录入准备不能取消已开始的用户动作；放行并等待真实 Handler 退出后才继续 |
| A04 | 完成一次暂停/恢复 → 开始第二次真实前置读取 → 再暂停 | 第二次仍等待本次工作，不能复用首次已完成的永久 shutdown wait |
| A05 | 暂停期间依次保存配置 S1、S2，再释放录入 | 暂停期间不注册；恢复只应用当前持久目标 S2；包括组合互换和未变化项 |
| A06 | Captured 已确定 → 卸载失败 → 重试成功 | 失败时不提交候选到草稿、不恢复热键、不能开始新录入；资源成功清理且 owner 仍有效后再应用结果 |
| A07 | hook 已清理 → 恢复旧持久目标时某动作注册失败 | 有效候选仍进入草稿；运行状态如实记录失败，重试属于 HotkeyCoordinator |
| A08 | 上一会话的通知/完成回调/lease release 晚到，新会话已开始 | 旧通知不改新控件；旧 lease 不解除新暂停；会话 Id、revision 与 owner 校验各有证据 |
| A09 | 退出请求与清理完成/lease 释放/设置变化/重试交错 | 永久准入关闭后的 Register 调用增量为零，实际资源停止完成后才释放后续依赖 |

确定性要求：A02 须观察真实队列已接纳该工作，再触发暂停入口；不能先完全阻塞 UI、又同步等依赖同一 UI 的注销造成测试死锁。A03 须让真实 Handler 到达受控外部读取，不能用无关 Task 或仅检查方法返回 Task 代替 entered 证据。

## 4. 草稿、持久化与运行状态

使用真实 SettingsViewModel → GeneralSettingsSaveCoordinator → Core ISettingsEditCoordinator → JsonSettingsService，自有临时目录；仅外部 Provider/密钥/原生端口替换。

| 编号 | 场景 | 验收断言 |
|---|---|---|
| V01 | 只修改热键，已完成录入的控件尚未失焦即保存 | IsDirty/计划/持久化都含新值；不能进入旧 General/Provider 无变更快速返回分支 |
| V02 | General、Provider、Hotkey 同时修改 | 一次实际 UpdateAsync 提交；原回执和凭据语义保持；失败不部分保存 |
| V03 | 未编辑行/已编辑行分别收到外部更新，同 Action 冲突 | 未编辑行跟进新基线，已编辑行保留原输入与基线；实际 Core 冲突正确显示 |
| V04 | 录入/清理中程序性 ExecuteSaveAsync；提交期间再尝试编辑 | 先停止并等待捕获清理，再固定一份输入；失败不提交半候选；保存冻结覆盖热键 |
| V05 | OpenSettings 缺失；普通禁用；Backspace 清除后关闭重开 | 缺失基线仍为 null；仅打开不写配置；禁用保留组合；清空跨真实重载保持禁用空键 |
| V06 | 新草稿与未开放动作预留组合重复 | 以完整候选配置验证；提示是配置重复/预留，不虚称 OS 已占用；未开放配置不被删改 |
| V07 | JSON 保存成功而 native 注册失败；同时存在 Provider 待处理回执 | 页面仍显示已保存和热键应用失败，两类重试独立；热键重试不写 JSON、不重播 Provider 提交/回执 |
| V08 | 某动作失败、其余成功 → 外部目标变化 → 重试 | 只重试最新持久目标/残留清理；成功且未变化项不重注册；旧代次结果不覆盖新状态 |
| V09 | 注销失败、暂停中保存、仅重开设置页 | 注销失败不能显示 Disabled/Registered；暂停显示结束录入后应用；重开不触发注册或消耗 ID |

验收时从最终代码确认真实文件和方法。准备时已确认的接线位置：

- `src/Iris.App/Settings/SettingsViewModel.cs`：ExecuteSaveAsync、IsDirty。
- `src/Iris.App/Services/GeneralSettingsSaveCoordinator.cs`：联合计划提交。
- `src/Iris.App/Hosting/HotkeyCoordinator.cs`：OnHotkeyPressed、DispatchActionAsync、WaitForInFlightDispatchesAsync。现有永久退出 wait 缓存不适合直接作为重复暂停协议。
- `src/Iris.App/Hosting/WpfUiDispatcher.cs` 与 `ActionDispatcher.cs`：实际 UI 回调与 Handler Task 等候。

这些是 B 基线接线观察，不是最终实现的缺陷结论，不预设新增 internal 方法名。

## 5. WPF 与失败清理

| 编号 | 场景 | 证据要求 |
|---|---|---|
| W01 | 构造/Loaded/获得焦点，对比点击/Enter/Space；Esc/Backspace、鼠标离开控件、导航 | 真实 HotkeyRecorderBox 和窗口事件/绑定；明确触发才开始，取消不误关窗口，切页完整收尾 |
| W02 | 已出结果但清理未完成时关窗；卸载失败后再关；新窗口建立后旧回调到达 | 旧结果不得写新 owner；失败保留自有窗口/session/scope 和可重试路径；真实资源退出后再释放 |
| W03 | 实际 AppBootstrapper.StopAsync 在 hook 或 Presenter 清理失败 | 根容器不提前 Dispose；失败可见且重试恢复；保持 A-R2 已验收退出次序与资源所有权 |
| W04 | Light/Dark × 默认/最小窗口 × 100%/150% 等效缩放 | 保存并检查八种离屏图；长组合、错误、运行状态、录入/重试/保存按钮无裁切；注明等效缩放不是实机混合 DPI 验收 |

测试所有者 finally 先放行全部自有闸门，再 await 实际任务、Presenter/Bootstrapper 和线程退出。清理失败必须进入可见结果；不能通过吞异常、释放仍活跃 scope、扩大超时或全局禁用并行使测试变绿。影响 AppShutdowner 的测试继续用最小共享 collection。

## 6. 执行时机与结论边界

本轮仅准备文档，没有运行 B 构建、测试、隔离探针或物理输入验收，也未控制 Antigravity。开发交付并停止共享源码改动后，再执行定向验证、串行 Release build 和全量 test，检查实际跳过与失败信息。

自动化不安装真实全局 hook、不注册用户系统热键、不注入键盘、不读取用户剪贴板/配置/密钥/Run、不做网络或性能脚本。产品真实 native 实现仍需代码核对。物理键盘、Shell/系统保留组合、IME、安全桌面等未做则明确未验证，不从 fake 或离屏图推断通过。

验收通过后结案 T-020B/父任务与 CCR-016，并直接准备下一完整工单；未通过则给出有实际证据的返工单。当前只有 T-020B 一个实施工单，后续设计准备见 T-021_SELECTION_PREPARATION.md。
