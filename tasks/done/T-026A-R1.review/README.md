# T-026A-R1 独立复审（2026-10-06）

最终提交69ab481cc0893cc019ed45e1ba8e4da1f4edc4f0。独立串行Release build/full test后，刷新actual DLL/fixture并运行Program与R1VisibilityChecks：构建0警告/错误，全量2557（2555成功/2既有error183跳过）；独立11项9通过/2失败，STA退出。review-build/full/probe.txt均原始完整输出。

Program保留初次九项断言，额外调用R1VisibilityChecks的两个实际owner/Window组合用例。fake仅为外部native/storage/provider等边界，不复制产品状态机、不读取真实用户内容/截图/剪贴板或注入输入。调用的Show/Hide/Begin全部在可见性边界内确认实际完成，最后观察真实IsVisible。失败均为业务断言，非fixture/清理错误。

在独立临时目录恢复Program.cs、R1VisibilityChecks.cs、GlobalUsings.cs、prepare.py（去掉.txt），完成仓库Release后执行python prepare.py、dotnet run --project Review.csproj -c Release。prepare从真实测试wiring/fake复制边界，IVT沿用Iris.App.Tests。原T-026A.review与开发九项复跑不覆盖；此目录是新增独立结论。

baseline-controls.txt另将相同两个用例接到SHA256匹配原T-026A.review manifest的cb5981e旧App DLL，实际运行复制后的DLL也复核相同。第一项通过（finalVisible=True），第二项仍失败（reason=None）；故第一项是R1回退，第二项是原有未补齐路径。baseline-dll-manifest记录原提交/DLL散列，baseline-Program是当时入口；不把旧DLL对照当R1通过，也不把两项均说成新回归。复现旧对照时需在隔离checkout构建cb5981e，并把相同R1VisibilityChecks连接该产物，不回滚工作区。

独立项目三个CS0649为抽取fixture未赋值字段，原文保留；解决方案零警告。真实桌面/焦点/混合DPI/物理输入/NFR未验证。全量保留既有只读native smoke与两项隔离环境跳过。
