# T-017-R2 诊断与验收补充

> 2026-10-03，Codex；R2 开发期间记录，不替代最终交付验收。

## 1. 已观察到的两次执行停滞

- PID 51508，Iris.App.Tests，自 05:52:33 启动，到 05:56 仍存活，CPU 1.12 秒。新增测试同步 Raise B，经 ImmediateUiDispatcher 内联进入 OnRegisterHotKey，再同步等待由同一测试后续语句才放行的 bRegisterRelease。该调用链证明测试自身死锁。诊断已直接送达 Gemini，进程已终止，测试已修改；不是因观察超时而盲目重启。
- PID 59604，Iris.App.Tests，自 06:02:20 启动，后续 CPU 约 15.69–15.73 秒，运行未结束。Antigravity 原任务输出实际包含多个 `System.InvalidOperationException : WPF Dispatcher is not available.`，涉及 TranslateWindowPresenterTests。Gemini 读取原任务输出后停止了该任务。此轮尚未获取足以将特定失败归因于某一测试的完整执行轨迹，不能将下面的静态交错称为已经证实的唯一根因。

## 2. 共享退出委托的实际写入者

当前 `rg -n 'AppShutdowner\s*=' tests/Iris.App.Tests -g '*.cs'` 显示写入者集中在两个测试类：

- AppBootstrapperTests：构造时保存全局值并替换为 no-op，Dispose 时恢复；若干测试再次临时替换并恢复。
- TranslateWindowIntegrationTests：两个退出集成测试保存/替换/恢复同一个静态属性。

生产默认委托会对 Application.Current 执行 Shutdown。两个类并行时，保存与恢复不是独占事务：A 保存真实退出并设 no-op；B 保存 A 的 no-op；A 恢复真实退出；B 的 Bootstrapper 此时调用全局委托，可关闭整个测试 Application。共享 STA 只保证单段 UI 回调串行，不能保证跨 await 的整个测试独占。

## 3. 同一 R2 内允许的最小隔离

上述共享状态在测试侧处理，不需要变更公开契约或生产行为。可把所有读写这些全局测试钩子的测试类放入同一显式 xUnit collection，使这些写入者彼此不并行；不使用程序集级 DisableTestParallelization，也不把全部 WPF 测试一起串行化。collection 名称应描述共享资源，新增写入者也需遵循同一归属。

仅同类方法默认不并行不足以隔离另一个测试类。若选择其它方案，也须证明替换/恢复所有权覆盖完整异步生命周期，不使用在 STA 同步等待的全局锁制造新的死锁。

## 4. 退出测试自身清理检查

- 自有 clipboard 闸门放行后完整等待自有 Bootstrapper/Presenter/Coordinator；不能用已取消的测试令牌跳过清理。
- 最外层 finally 负责恢复全局委托与关闭测试自己创建的旁观窗口，即使前面的断言/收尾抛错也必须到达；收尾错误应保留，不吞掉后宣称成功。
- 释放自有 ServiceProvider；不要调用关闭所有 Application.Current.Windows 的全局清理作为单个测试的收尾。
- 原旧回调测试要求实际返回任务受闸门控制，并在移除代次保护时被检测失败。暂时变异需恢复后再完整构建/测试。

## 5. 当前外部状态

Computer Use 已恢复，Antigravity 原 Iris 对话可读取和直接发送工单。06:10 左右界面显示 `Model unavailable, retrying ... (attempt 7/9)`；这是模型服务重试状态，不是项目代码完成或工单失败。保留原工作区与任务，恢复后继续 R2，不另开并行实施单。

后续界面明确显示 `Baseline model quota reached`，并告知基础配额于 `2026/10/3 07:44:25` 刷新。未开启 `Enable Overages`（额外付费）。在原对话选择 Gemini 3.1 Pro Low 并送达既有 R2 隔离补充后，同样返回模型不可用与相同配额提示；当前模型选择为 Gemini 3.1 Pro Low。实施受外部配额阻挡，不是代码验收完成。恢复后继续当前工单、核对共享状态隔离和变异证据，不从头重做已完成修改。
