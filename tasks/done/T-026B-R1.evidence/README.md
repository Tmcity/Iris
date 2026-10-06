# T-026B-R1 开发实施与验证（2026-10-06）

请求架构师独立复审，未自行验收结案。基线为 `5cda8cc73f24d0b29778153c337e0c93531a9c85`；本单在原 B 实现上修复，没有重新实现 B 或开始 T-027。

## 实施与验收对应

| 工单 | 实施与实际证据 |
|---|---|
| R1-01 | Window 在物理坐标查询前及返回后检查输入许可；Confirm 封口时关闭各视图指针准入。已接纳命令的异常仍经独立处理路径报告，真实 Release 失败保留原捕获/窗口/准备责任，Retry 后归还。独立迟到 MouseMove 与已接纳 Confirm Release 失败对照均通过。 |
| R1-02 | 创建登记后订阅本会话的 Closed，确认前意外关闭取消整场；建立时也检查已关闭视图。正常清理先封口，已确认关闭不反转图像；真正关闭后解绑会话通知。独立真实非前台第二窗 Close、前台仍第一窗的反例通过。 |
| R1-03 | 最终许可查询当前前台后，重新校验目标 HWND/PID 与 Current（显示意图、caller、Stop）。保留外层与 native 接受点回调。独立第二次空前台查询中接纳新意图，结果取消且 nativeRestores=0。 |
| R1-04 | 新增 App 内部 ScreenshotException 标记固定安全诊断。A 的聚合出口及 WindowPreparationIntent 已受控 SHOW/LATE_HIDE 错误使用标记，生命周期不变；B 保留 DWM 原始 HRESULT、全部主/清理/取消阶段及多个取消异常。仅实际 GdiScreenCapturer 调用边界导入其 T-025 安全元数据，未知外部 IrisException 的 Detail/Message/Data/Inner 不透传。新增实际 Crop+Close 双错误脱敏回归，既有 A LATE_HIDE 回归保持通过。 |
| R1-05 | 真实 WPF 右键走 PhysicalPoint 快照；无位置键盘/生命周期命令保留既知指针。实际右键及封装 ScreenshotInput 路径均检查为 100,100，清除后提示改变、放大镜采样位置保持。 |
| R1-06 | hint、size、magnifier 与 coords 统一登记占位，优先靠近原位置搜索不重叠的候选行/列。保持完整文本、8 倍原图采样与原小视口降级。实际绘图不透明标签重叠为零，最终 PNG 已查看。 |
| C26B-1 | FakeUiaProcessSession 用受锁保护的 pipe 完成和不可处置 TCS 退出通知替代 SemaphoreSlim；退出发布、等待、Kill 和 Dispose 不再并发使用已处置同步对象。StartResponse 在派发前登记后台响应任务；HasExited/WaitForExit/Dispose 覆盖实际 producer 结束，处置失败可重试并保留 producer/cleanup 错误。保留 RefuseToExit、ExitDelay、WaitForExitFunc 和 stdout 错误测试。新增有 entered/release 的并发 SignalExit/Kill/Dispose 回归。 |

只修改 App/Capture 内部与对应测试，以及 C26B-1 fake/必要支撑。Core、specs/CONTRACTS.cs、Platform 生产、包、默认配置/JSON/schema 相对基线保持零差异。没有改 ScreenshotRendering 独占集合、原超时/entered 断言或产品 Render 同步。

## 验证与真实输出

以下日志均为本开发会话实际执行，不能称为架构师独立验收。先定向，随后串行执行 `dotnet build Iris.slnx -c Release`、`dotnet test Iris.slnx -c Release`；没有为重现基线再跑全量。

定向最终日志：`targeted-app-third.txt`（截图相关）、`targeted-platform-second.txt`（Platform Selection）、`targeted-mouse.txt`（MouseInteraction）。实际摘要见归档工单，完整原文在这些文件中。

最终全量原文：`release-build.txt`、`release-full.txt`。全套保留既有 DesktopContextNativeReadOnlyTests，只读 cursor/monitor/DPI 查询；两项隔离窗口站 error183 跳过保留，没有改为访问用户剪贴板。新增测试均使用合成像素、自有 WPF 和 fake 外部边界。

独立程序复用副本位于 `C:\Users\28910\AppData\Local\Temp\iris-t026b-r1-development-review`。`probe/` 保存本轮源代码、项目引用、prepare.py、fixture、cases 与绑定 manifest；`probe-final.txt` 为最终输出。Program.cs、GeometryChecks.cs、LifecycleChecks.cs、VisualChecks.cs 和 cases.json 与原程序字节散列一致，目标断言未改。

fixture 适配仅为仓库新增回归已经提供 ReviewForeground、ReviewFlushResult、ReviewPhysicalPoint；prepare.py 对精确已存在钩子跳过重复注入，另独立增加 ReviewAfterActivate。刷新仍引用实际最终产品 DLL，fake 只替换外部边界。原 `iris-t026b-independent-review` 及 `T-026B.review` 不覆盖。

独立小宿主编译有既有 CS0649 未赋值 fake 字段警告，原文保留，不能将其称为零警告；全解决方案 Release 构建为零警告/零错误。

## 中间失败与修正

- 首轮局部 App.Tests 构建遇到 CA1859，要求内部 Failure 返回实际 ScreenshotException 类型；已修正。该次输出在会话工具记录中，后续局部构建日志为 `development-build.txt`。
- `targeted-app-first.txt`：新增标记最初漏接实际 WindowPreparationIntent 的受控 SHOW/LATE_HIDE 聚合，既有 A 定向失败；已补内部错误标记，没有修改目标断言或 A 的可见性状态机。
- `targeted-app-final.txt` 是中间编译失败日志（文件名不代表最终成功）：新增脱敏测试的 nullable 警告作为错误，已补正确的非空断言。
- `targeted-app-second.txt`：既有 CandidatePause 测试复用已经完成的 Icon.Shown，在等待完整 fake producer 后暴露旧通知竞态。必要测试支撑 MouseInteractionTestScope.ShowCandidate 在每次手势前重建 Shown，真实本次 Show 才放行；原产品和原断言不变。之后截图定向与 MouseInteraction 定向通过。
- `targeted-platform.txt`：新增测试首次遇 xUnit1051 编译规则；有界等待使用 TestContext 的取消令牌，finally 仍先 release、再 await 原任务与处置并保留主/清理异常。修正后 Selection 定向通过。
- 首轮实际图片查看发现脚本编码把尺寸乘号误改，已恢复原“×”后完成最终构建/全量和独立程序重跑。`visuals-first/`、`render-first/` 保留该中间产物，`probe-first.txt` 保留首次独立运行结果，不混作最终 PNG。

## 实际视觉检查

最终 `standard-render/` 为既有标准渲染组；已逐项检查 contact-standard 图页，长图另看原尺寸 left/center 裁片，确认浅/深主题、无/有选区、四角、遮罩、无放大镜、小视口和长尺寸布局。裁片/拼图仅供查看，原 PNG 同时保留。

最终 `r1-render/` 为本单仓库测试产生的小选区、右键前后及 DPI1/DPI2 对照，已逐张看原 PNG。`probe-visuals/` 为原独立程序最终图像，原反例小选区尺寸和 Enter 提示分离；右键清除后坐标仍为 100,100，放大镜图像位置与采样保持，提示回到拖动/Esc；System 已解析 Dark 图与显式 Dark 一致，DPI2 正常放大镜为固定物理尺寸，小视口保留原隐藏策略。未用缩短文本或删功能掩盖遮挡。

`manifest.json` 记录源代码、最终 App DLL、证据文件散列与独立断言一致性。它绑定工作区最终代码；基线字段仍是验证时 HEAD，不误称该字段为交付提交哈希。

## 与工单的偏差、残留和未验证

没有功能范围偏离。C26B-1 必要支撑额外修改 SelectionTestScope 的 producer 登记，以及 MouseInteractionTestScope 的本次显示通知等待；原因和中间失败原文如上。A 内部仅诊断标记/保留改动，既有已验收状态机与成果保留。

真实桌面截图、用户选区/剪贴板、物理键鼠输入、真实系统鼠标捕获/hook、混合 DPI/HDR/焦点、桌面隐藏实际画面与 NFR **未验证**。没有运行真实 Iris 主程序、性能脚本、HTTP、用户配置/密钥/Run 操作；合成图和 fake 时间不充抵实机与性能验收。

原架构师规范/交接/BACKLOG/CCR/B报告与 B.review 全部保留随本单提交；T-026/B 保持▶，A 保持☑，CCR-025 仅更新待独立复审。完成 current_task 全文归档后删除。不 push/reset/clean/rebase/amend；`.codex/` 是预存未跟踪用户工具，未修改、未删除、未纳入提交。本单完成开发与验证后等待架构师独立复审，不自行结案或开始下一单。
