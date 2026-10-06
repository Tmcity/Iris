# T-026B 独立复审证据（2026-10-06）

被审提交5cda8cc73f24d0b29778153c337e0c93531a9c85为开发中断后的恢复交付，架构师归档/提交时未改生产与测试源码。独立Release构建0警告/错误；全量2621中2618成功/2既有跳过/1既有UIA fake清理竞态。独立实际服务/窗口/PNG程序34项27过/7失败，STA退出。原始完整输出在review-*.txt，不把开发端全绿或开发期预演改写为本次结果。

还原Program/GeometryChecks/LifecycleChecks/VisualChecks/GlobalUsings的.cs文件和prepare.py、cases.json到独立临时目录，构建仓库Release后运行python prepare.py、dotnet run --project Review.csproj -c Release。prepare仅复制原有测试wiring/外部fake，并给合成帧、DwmFlush返回、token/前台/真实鼠标点/UI派发增加观察钩子；产品始终从实际DLL调用，不复制其状态机。fixture源/副本与产品指纹见manifest。

几何用字面矩形与独立颜色公式检查实际解码PNG；另外有实际GdiScreenCapturer（fake GDI）→实际Window→真实PNG链。错误、取消、关闭、焦点及真实WPF鼠标路由均在实际服务/Window上安排外部边界。意外关闭用非前台第二窗，前台仍是第一窗，不伪造前台迁移；10秒超时后finally取消并由外层等待原任务及所有资源，最终STA结束。

visuals中实际查看了小选区标签覆盖提示、右键前后100,100→0,0，以及System解析Dark与显式Dark一致、DPI2正常/小视口的对照。提交的22张标准PNG也逐项匹配已查看副本，见render-binding.json；真实桌面/输入/混合DPI/HDR/NFR未验证。

全量唯一失败来自未被B修改的FakeUiaProcessSession.SignalExit在SemaphoreSlim已处置后Release；单项重跑1项通过，源码/完整调用栈显示HasExited与退出同步/Dispose发布顺序存在竞态，作为仅测试维护C26B-1，不判平台生产回退。修复需保留退出/取消/失败语义，不靠吞异常或删断言。

原API错误/恢复记录在T-026B.md及PROJECT_HANDOFF。指定开发会话持续400，独立resume被active writer拒绝，代理socket10050，Windows连接os2；没有重启/修改用户配置或删除会话。修正工单已具体化，待可用开发会话下达。
