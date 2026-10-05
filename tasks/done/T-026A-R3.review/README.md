# T-026A-R3 最终独立验收（2026-10-06）

被审提交c7282698028da861dbc9dfc6e9430b9cdd1e842b。相对R2/b913db018a6332fcc4fceb01644fe07f4714fe7a，src/、公开契约、默认配置/schema/包0diff（production-diff.txt为空）。R2的原11+84组合、95项实际owner/Window独立通过证据继续绑定相同生产源码，见T-026A-R2.review；本次没有重复运行或改写那95项。

本次独立Release build（0警告/错误）、完整限定名单项（实际1通过）、全量（2565，2563成功/2既有error183跳过）均退出0。原始输出分别为review-build/targeted/full.txt。全量仍含既有T-022A只读native smoke，不泛称全套无桌面查询；真实桌面/焦点/混合DPI/输入/NFR未验证。

Program.cs通过反射载入实际交付的Iris.App.Tests.dll，直接构造其中私有HoldFirstDispatch，用原WpfTestRunner测试保持回调抛错仍完成原Invoke信号，以及先Drain后迟到Invoke正常运行。没有复制helper实现。两项输出均正确，STA退出；helper工程构建0警告/错误，运行退出0。程序/工程去掉.txt后可在独立临时目录复跑（先构建仓库测试产物）；程序集散列见manifest。

原R2首次全量失败、0项错误过滤及自启受控观察全部保留，不因本次通过删改。T-026A/R3与CCR-024由架构师结案，父T-026仍需B完整覆盖层/服务。
