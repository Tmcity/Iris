# T-024B1 实际命令记录

本目录 txt 从本轮系统临时目录 iris-t024b1-*.log 原样复制。没有用后续通过覆盖首次失败；以下退出码来自实际工具返回。

| 文件（省略 iris-t024b1- 前缀和 .txt 后缀） | 命令 | 退出码 |
|---|---|---|
| build-initial | dotnet build src/Iris.App/Iris.App.csproj -c Release --no-restore | 1 |
| platform-first | dotnet test --project tests/Iris.Platform.Windows.Tests/Iris.Platform.Windows.Tests.csproj -c Release -- --filter-class '*ExpectedSelectionSourceTests' | 0 |
| app-first | dotnet test --project tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release -- --filter-class '*SelectionReadCoordinatorTests' | 1 |
| app-second | 同上 | 0 |
| drain-first | dotnet test --project tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release -- --filter-class '*SelectionCoordinationDrainTests' | 2 |
| drain-second | 同上 | 0 |
| integration-first | dotnet test --project tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release -- --filter-class '*SelectionCoordinationIntegrationTests' --filter-method '*ActualAppHostBuilder_RegistersUniqueHandlerAndSharedReaderPresenterWithoutIo' | 8 |
| integration-second | dotnet test --project tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release -- --filter-class '*SelectionCoordinationIntegrationTests' --filter-class '*SelectionEntrypointIntegrationTests' | 2 |
| integration-third | 同上 | 0 |
| app-final | dotnet test --project tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release -- --filter-namespace 'Iris.App.Tests.Selection' | 0 |
| release-build | dotnet build Iris.slnx -c Release | 0 |
| full-test | dotnet test Iris.slnx -c Release | 0 |

Release build 完成后才启动全量 test，未并行构建/测试。app-final 是该命名空间全部测试，包含原有 KeyboardSimulatorUiDispatcherTests，不能将摘要数量全部当作本单新增测试。integration-first 的 class/method 过滤取交集，实际零测试，未计作通过。

全量明确包含既有 T-022A 两项只读光标/显示器/DPI 元数据 smoke，这是本单架构师澄清后继续保留的回归；新增来源/协调测试使用实际 Composite/Keyboard/UIA/C1，加 fake OS/process/time/桌面边界。测试不启动真实 Iris 或真实被动入口。
