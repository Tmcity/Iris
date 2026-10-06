# T-026B-R2 开发报告（2026-10-06）

基线 `7e6687d0a17206edb613354a0022cb1618a18686`。本单仅补建立阶段已登记同伴覆盖窗意外 Closed 的生命周期缺口，请求架构师独立复审，未自行结案。

## 实施

生产仅修改 `src/Iris.App/Capture/ScreenshotService.cs`：OnWindowClosed 从“已交互且未确认”改为“未进入服务清理且未确认”，使登记建立期的同伴关闭立即令会话失效。Cleanup 在 UI 上先标记主动清理再关窗；已确认和正常 Stop/清理语义保持。登记并订阅后、Show 前检查当前窗是否已关闭，补足订阅前已关闭的边界；原 ShowOwned 内及循环/Activate 前的 Check 保持，关闭通知触发失效后不继续建立后续屏或激活窗口组。

新增 `ScreenshotR2Tests.RegisteredPeerClosesDuringShow_EndsSetupAndRetainsFailedCleanup` 回归：实际第三窗 fake Show 边界中真实调用第二窗 Close；以 entered 确认到达该边界，观察第二窗 IsClosed/Handle、截图自动返回 null、全部窗口和捕获/准备责任归还且 caller 尚未取消。同时覆盖正常三窗建立后确认保图、关闭失败保留原窗口和准备责任后同实例 Retry，以及四屏情况下失效后不再 Show 第四屏。不伪造前台变化，不补发 Esc，不把 caller 取消当成功条件；失败路径 finally 恢复边界并取消未完成调用，外层 ScreenshotTestScope 等实际任务并保留主/清理错误。

原确认后收尾、已接纳 Confirm Release 失败、原窗口重试、A/C26B-1 和本次显示通知等待测试保持不变。Core/specs 契约、Platform 生产、默认配置/JSON/schema、中央包均相对基线零差异。

## 实际验证

先执行截图定向，再串行执行 `dotnet build Iris.slnx -c Release`、`dotnet test Iris.slnx -c Release`。原文保存为 targeted.txt、release-build.txt、release-full.txt；工单归档附完整输出。这些是开发会话本轮执行，不冒称架构师独立验收。本轮没有失败的产品构建或测试运行。

随后在另存副本 `C:\Users\28910\AppData\Local\Temp\iris-t026b-r2-development-review` 刷新实际 DLL 引用，运行原架构师修正后的完整程序；probe.txt 保存真实输出，probe/ 保存执行源/fixture/项目/绑定。Program、GeometryChecks、LifecycleChecks、VisualChecks、StartupCloseChecks、cases 和 prepare.py 均与原目录字节一致，没有修改目标断言或另行适配 fixture。原目录、R1.review 及其中中间探针失败记录均保留。小宿主编译仍有既有 CS0649 fake 字段警告，原文保留；全解决方案 Release 构建为零警告/错误。

ScreenshotOverlayWindow/ScreenshotFrameView 绘图源码不变。visual-preservation.json 记录此前已查看的标准/R1 PNG 与本轮全量实际生成 PNG 的散列一致性；不额外重绘或重做已完成视觉验收。独立程序产生的图像也以散列对照原 R1.review。manifest.json 绑定本轮实际源码、测试、DLL、探针和日志，baseline 字段是验证时基线 HEAD，不是交付提交哈希。

## 未验证、偏差与残留

无工单范围偏差。没有改测试超时、删除断言、增加 Skip 或改变产品 Render 同步。既有只读 cursor/monitor/DPI native smoke 保留，两项隔离窗口站 error183 跳过保留；新增测试只有自有 WPF、合成像素和 fake 外部边界。

真实用户截图/选区/剪贴板、物理输入/鼠标捕获/global hook、真实桌面隐藏/焦点、混合 DPI/HDR 及 NFR **未验证**。没有运行真实 Iris、性能脚本、HTTP 或用户配置/密钥/Run 操作。

工单全文归档 T-026B-R2.md，R1/B 报告追加落实更正，删除完成 current_task。架构师规范澄清、交接、BACKLOG/CCR 与 R1.review 保留随本单提交；父 T-026/B 仍▶、A☑，CCR-025 仅待独立复审，不开始 T-027。不 push/reset/clean/rebase/amend；预存 `.codex/` 不改不删不提交。
