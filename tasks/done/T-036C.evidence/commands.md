# T-036C 恢复交付命令

以下为本轮实际执行顺序，工作目录 `F:\Iris`。输出经 `2>&1 | Tee-Object -FilePath <日志>` 保存，各命令退出码均为 0；完整原始日志保留。最终 build 和 full test 串行执行。

| 命令 | 日志 |
|---|---|
| `dotnet test --project tests/Iris.Providers.Tests/Iris.Providers.Tests.csproj -c Release --filter-class Iris.Providers.Tests.Http.ProxyConnectivityTests` | recovery-tester-1.log |
| `dotnet test --project tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --filter-class '*NetworkSettingsUiTests' --filter-class '*NetworkConnectivityHostingTests' --filter-class '*SettingsDraftAndSaveRegressionTests'` | recovery-ui-host-1.log |
| `dotnet run --project 'C:\Users\28910\AppData\Local\Temp\iris-t036c-independent-tester\Review.csproj' -c Release -- --cancel-callback --wire` | recovery-independent-tester-1.log |
| `dotnet test --project tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release --no-build --filter-class '*NetworkSettingsRenderingTests'` | recovery-visual-1.log |
| `dotnet run --project 'C:\Users\28910\AppData\Local\Temp\iris-t036c-independent-ui\Review.csproj' -c Release` | recovery-independent-ui-1.log |
| `dotnet build Iris.slnx -c Release` | release-build-delivery.log |
| `dotnet test Iris.slnx -c Release` | full-test-delivery.log |

定向视觉进程设置 `IRIS_T036C_EVIDENCE=F:\Iris\tasks\done\T-036C.evidence\visual-delivery`；全量进程设置为 `F:\Iris\tasks\done\T-036C.evidence\visual-full-delivery`。两者在各自 PowerShell 子进程内设置，不更改用户环境。

独立探针原输入位于 `../T-036C.review/independent-tester` 和 `independent-ui`，原日志和 binding 一并保留。复现时复制至独立临时目录运行，以保持原项目不继承仓库 Directory.Build.props 的环境；项目 HintPath 仍指向当前 F:\Iris 的 Release 产物。

视觉以原尺寸四图一组生成 contact，再逐格检查；contact 仅排列原 PNG、不缩放。哈希比较覆盖全部目标文件，原图及 manifest 均保留。
