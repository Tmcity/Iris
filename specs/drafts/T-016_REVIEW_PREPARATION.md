# T-016 独立验收准备

> 2026-10-03，Codex；用户已手动转交 T-016，当前开发中。
> 这是已签发 TRANSLATION_ENTRYPOINTS.md 的验收场景准备，不是新工单或额外要求。基线 5c5f78d / T-015-R5 已验收；不把进行中的工作区当作交付版本，不改开发文件。

## 1. 验收入口与证据层级

Handler 参数与分支可以使用假端口隔离；旧文本/错误不会覆盖新窗口、caller 归还后重试、正常退出等待，需要真实 Dispatcher / Handler / Presenter / VM 接线。不能让 fake Presenter 自动返回 false 就声称旧结果隔离已验证。

复用现有 AppHostBuilder 测试覆盖接缝，保留 ValidateOnBuild / ValidateScopes。从同一容器解析 IActionDispatcher、IActionHandler 集合、ITranslateWindowPresenter 与关闭协调器，验证实际注册及单例对应；不要另写一份“看起来相同”的 ServiceCollection 代替产品注册。

托盘测试驱动真实菜单/LeftClickCommand 对象；无需实际向 Shell 注册图标。调用 Execute/RaiseEvent 的证据只能描述为代码驱动，不说成人工点击。

## 2. 受控场景（交付后执行）

| 编号 | 已签发要求 | 可区分正确实现与伪通过的证据 |
|---|---|---|
| E01 | 取消与准入 | 已取消调用不取租约/不读剪贴板；TryBeginOperation=null 时无读取、无 Show/新意图兜底；记录调用次序和次数 |
| E02 | InputTranslate 切换 | visible/active 四组合；只有同时为 true 才 Hide，且无新租约；可见但不活跃应聚焦展示，null Payload 保留而不重译，empty Payload 明确清空 |
| E03 | 参数保持 | 通过实际 Handler 保持前后空白、换行与代理对，不手工 Trim；Anchor、Origin、AutoTranslate、FocusInput 与规范逐字段对应 |
| E04 | Clipboard 三种返回 | 非空文本提交；null/empty/纯空白打开空白输入错误态；已知/未知读取错误只显示默认安全错误；同时检查界面状态和请求数，防止误译旧正文 |
| E05 | catch 范围 | 读取抛错与发布器抛错分开；发布失败不得被读取 catch 再次当成读取失败而递归 Show。发布返回 false 不创建新操作或额外弹通知 |
| E06 | 迟到成功/空/异常 | 旧 Clipboard 读取已 entered 且故意不响应取消；新 Input/TranslateText 完成发布或用户隐藏；再放行旧读取的不同结果，检查新正文/错误/可见性/复制无旧操作污染 |
| E07 | 防重入与并发 | 原 ClipboardTranslate 未返回前第二个同动作忽略；其它新输入动作可以接纳并使旧意图失效。用实际 Dispatcher 状态/调用证据，不把所有动作全局串行当修复 |
| E08 | caller 生命周期 | TryShow 已返回但翻译仍执行时，Handler caller 已 Dispose；翻译仍完成、终态卡可重试。读取未退出时不能仅因取消就 Dispose；关窗/退出等待该真实读取归还 |
| E09 | TranslateText | null/空白在分配新操作前拒绝；CommandLine、DragDrop/FloatingBall 与其它触发映射正确；不读剪贴板、不在活跃窗口时误走隐藏分支 |
| E10 | 托盘接线 | 输入/剪贴板菜单和左击命令各路由正确动作一次，Trigger=TrayMenu；构造不分发，Dispose 后旧对象不再分发；初始化/重试不重复挂事件 |
| E11 | 配置到结果 | 真实设置 VM 保存隔离配置，经实际 Dispatcher→Handler→Presenter→Orchestrator 输出有序卡片；外部 Provider/SecretStore/Clipboard 等替换为 fake，不把 fake Commit 的 Applied 当成真实保存 |
| E12 | 退出与安全日志 | 阻塞读取、发布或已接纳动作时请求退出，确认资源仍存活；最终放行后无遗留任务/窗口。消息、日志 exception 参数和参数对象中不得出现合成正文/凭据标记 |

E06 的正确实现可能仍尝试用旧租约 TryShow 并得到 false，也可能在取消检查后提前结束；不要把“旧 TryShow 调用必须为零”加成新要求。关键是不改变最终用户可见状态，且所有者真实退出。

错误场景应分别投放不同的合成标记到 Message、Detail、InnerException；检查实际日志接收器和通知/界面，而不只检查格式化日志模板。

## 3. 共享代码改动的回归边界

本轮只观察到工作区已有 Handler/托盘、注册与共享 RelayCommand 等开发修改；未据未提交版本下结论。交付时若保留共享命令改动，需核对它是否影响已验收的设置冻结、翻译/停止、单卡重试 CanExecute 和异步错误观察。不要因为改了共享类就直接拒绝，也不能只看新托盘测试忽略已有调用方。

Core 注册从仅设置 Handler 增加翻译 Handler 后，既有测试的依赖桩和预期集合需要合理更新。核对改动理由，不把删掉原有架构/防重入断言当作“测试适配”。

## 4. 夹具与执行顺序

- 每个阻塞依赖有独立 entered / release / exited 证据。超时只用来保护诊断，不能以短暂 Delay 作为“工作已开始”的唯一证明。
- 先记录交付哈希与工作区，再按风险做定向验证、实际接线和托盘命令检查；最后按工单独立 Release build / 全量 test。
- 所有 finally 先放行自有闸门，等待读取/发布/取消及 caller 归还，再等待 Presenter 关闭，最后 Dispose 服务/自有窗口。不能等退出完成之后才释放它正在等待的 caller。
- 测试不读取用户剪贴板/设置/启动项，不联网，不注册实际全局热键，不运行性能脚本。真实 Shell/IME/DPI/服务未执行时继续写未验证。

本次仅作静态衔接核对和验收准备，没有执行上述 T-016 场景或重复构建开发中的共享解决方案。当前继续用户手动转交模式。
