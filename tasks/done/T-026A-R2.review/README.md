# T-026A-R2 独立复审（2026-10-06）

最终提交b913db018a6332fcc4fceb01644fe07f4714fe7a，src/tests保持该提交。独立构建0警告/错误。首次全量2565项中2562成功/2既有跳过/1失败；失败是既有自启迟到查询测试的过早断言，见review-full-failed.txt，不得改写为全量通过。

准备专项Program+R1VisibilityChecks+VisibilitySequenceChecks共95项全部通过（原11项+四类操作长度1～3的84种顺序），STA退出，详见review-probe.txt。组合oracle取actual owner在旧恢复native边界内完成新操作后的真实可见性，验证旧恢复不改变该结果；不读取产品版本字段或复制状态机。实际Coordinator/Presenter/Window，fake只替换native/storage/provider等边界，无真实内容/截图/输入/global hook。

恢复上述3个.cs.txt、GlobalUsings.cs.txt和prepare.py.txt到独立临时目录，去掉.txt后执行python prepare.py、dotnet run --project Review.csproj -c Release。prepare从仓库取既有wiring/fake与实际产品DLL，原R1/A证据不覆盖。

自启测试最初短方法名过滤运行0项（review-filter-empty.txt），改为完整限定名后实际1项通过（review-targeted-autostart.txt）。0项不算验证通过。检查SettingsViewModel/GeneralSettingsSaveCoordinator后，另用真实VM/Coordinator、fake自启查询/设置服务和实际Dispatcher门闩重现保存与新查询完成先后：旧初始回调仍被保持，Save已完成时retry=false，新查询完成retry=true，最后放行旧回调仍true。程序/原始输出是autostart-observation-*，原测试在新查询结束前即断言，不能保证这个异步顺序。

复现诊断：在新的临时目录使用autostart-observation-Program作为Program.cs，再放GlobalUsings、prepare.py和TestSettingsEditCoordinator.cs，运行prepare/run；不要把它和95项的顶层Program同时放在一个工程。全部AutoStart/Settings均fake，没有访问真实Run注册表，最终STA退出。诊断只证明该时序，不能用于泛称所有自启功能已重新验收。

R2生产修复专项通过，当前唯一工单R3仅维护这一既有测试的真实等待与清理，src/契约保持0改动；修复后完成独立全量再结案A。真实桌面/焦点/混合DPI/物理输入/NFR未验证。解决方案仍含既有只读native smoke及error183环境跳过。独立临时项目3个CS0649为抽取fake字段警告，与产品零警告区分。
