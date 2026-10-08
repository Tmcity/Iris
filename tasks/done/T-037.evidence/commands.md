# T-037 验证命令与日志

工作目录为 `F:\Iris`。每次实际执行均以 PowerShell `2>&1 | Tee-Object -FilePath <日志>` 保存原始输出并检查退出码。所有旧日志保留。

| 命令 | 最终/对应日志 |
|---|---|
| `dotnet test --project tests/Iris.Platform.Windows.Tests/Iris.Platform.Windows.Tests.csproj -c Release --filter-class '*.IpcCancellationFenceTests'` | cancellation-fences-before.log（实际修正前失败） |
| `dotnet test --project tests/Iris.Platform.Windows.Tests/Iris.Platform.Windows.Tests.csproj -c Release --filter-class '*.IpcProtocolTests' --filter-class '*.NamedPipeIpcTests' --filter-class '*.IpcLifecycleTests' --filter-class '*.IpcCancellationFenceTests'` | platform-tests-delivery-2.log |
| `dotnet test --project tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter-class '*.IpcStartupTests' --filter-class '*.IpcIntegrationTests' --filter-class '*.AppBootstrapperTests' --filter-class '*.AppHostBuilderTests'` | app-tests-6.log |
| `dotnet build Iris.slnx -c Release` | release-build-delivery-2.log |
| `dotnet test Iris.slnx -c Release` | full-test-delivery-1.log |

最终 build 成功后才启动 full test，两者串行。全量测试子进程将 `IRIS_T036C_EVIDENCE` 指向本目录的 `regression-network-visuals`，承接原网络 UI 回归自身生成的图像；这不是新增的 T-037 UI 页面验收，也不覆盖架构师原 C 的最终图。

早期 `platform-tests-2.log` 因类名筛选方式不匹配而运行零测试；随后采用上表明确类名筛选，没有将零测试算作通过。`release-build-final.log` 与 `release-build-delivery.log` 是补充验证完成前的中间成功构建，最终依据为上表文件。
