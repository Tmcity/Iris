# T-024B2 实际命令清单

本目录 txt 从本轮临时目录 iris-t024b2-*.log 原样复制；保留失败与中间结果。最终交付依据是 release-build-verified/full-test-verified，均实际退出 0，且先构建后全量测试。

以下文件名省略 iris-t024b2- 前缀和 .txt 后缀。

| 文件 | 实际命令 / 过滤 | 退出码 |
|---|---|---|
| build-first | dotnet build src/Iris.App/Iris.App.csproj -c Release --no-restore | 0 |
| build-ui | 同上 | 1 |
| core-first | dotnet test --project tests/Iris.Core.Tests/Iris.Core.Tests.csproj -c Release -- --filter-class '*MouseSettingsEditTests' --filter-class '*MouseSelectionWorkflowTests' | 1 |
| core-second | 同上 | 0 |
| mouse-first | dotnet test --project tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release -- --filter-class '*MouseInteractionTests' | 1 |
| mouse-second | 同上 | 1 |
| mouse-third | 同上 | 0 |
| mouse-fourth | 同上 | 1 |
| mouse-fifth | 同上 | 2 |
| app-first | App 测试项目，Release；--filter-class '*MouseInteractionTests' --filter-class '*MouseSettingsUiTests' --filter-class '*MouseRenderingTests' | 0 |
| app-second | 同上并增加 --filter-class '*MouseBootstrapperTests' --filter-class '*MouseIconGeometryTests' | 0 |
| app-third | 同 app-second | 0 |
| release-build | dotnet build Iris.slnx -c Release | 0 |
| full-test | dotnet test Iris.slnx -c Release | 2 |
| core-regression | dotnet test --project tests/Iris.Core.Tests/Iris.Core.Tests.csproj -c Release -- --filter-class '*CoreRegistrationTests' --filter-class '*OpenSettingsHandlerTests' --filter-class '*MouseSelectionWorkflowTests' | 0 |
| release-build-final | dotnet build Iris.slnx -c Release | 0 |
| full-test-final | dotnet test Iris.slnx -c Release | 0 |
| anchor-final | 同 mouse-first | 0 |
| release-build-delivery | dotnet build Iris.slnx -c Release | 0 |
| full-test-delivery | dotnet test Iris.slnx -c Release | 0 |
| render-isolated | dotnet test --project tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release -- --filter-class '*MouseRenderingTests' | 1 |
| render-isolated-final | 同上 | 0 |
| release-build-verified | dotnet build Iris.slnx -c Release | 0 |
| full-test-verified | dotnet test Iris.slnx -c Release | 0 |

App 合并定向命令的完整前缀为 `dotnet test --project tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --`。所有 Release build/full test 均串行执行。中间 full-test-delivery 自动测试虽通过，归档图像检查发现并发主题干扰：Light 图标实际为 Dark，保留在 ../T-024B2.assets/before-render-isolation。增加独占渲染集合、实际主题复核与断言后，重新执行定向及最后构建/全量，不将中间图像冒充最终图像。

全量保留已验收的 T-022A 只读光标/显示器/DPI smoke；新增测试不接真实用户内容、输入、剪贴板或全局 hook。渲染仅创建自有离屏 WPF 窗口，未启动真实 Iris 主应用。
