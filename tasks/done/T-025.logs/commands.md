# T-025 实际命令记录

本目录 txt 原样复制自本开发轮的临时日志。首次编译错误与后续通过均保留，未用准备探针或历史性能报告替代本单验证。

文件省略 iris-t025- 前缀和 .txt 后缀。

| 文件 | 实际命令 | 退出码 |
|---|---|---|
| build-initial | dotnet build src/Iris.Platform.Windows/Iris.Platform.Windows.csproj -c Release --no-restore | 1 |
| build-second | 同上 | 0 |
| gdi-first | dotnet test --project tests/Iris.Platform.Windows.Tests/Iris.Platform.Windows.Tests.csproj -c Release -- --filter-class '*GdiScreenCapturerTests' | 1 |
| gdi-second | 同上 | 0 |
| crop-first | dotnet test --project tests/Iris.Platform.Windows.Tests/Iris.Platform.Windows.Tests.csproj -c Release -- --filter-class '*ScreenCropTests' | 1 |
| crop-second | 同上 | 0 |
| platform-final | dotnet test --project tests/Iris.Platform.Windows.Tests/Iris.Platform.Windows.Tests.csproj -c Release -- --filter-class '*GdiScreenCapturerTests' --filter-class '*ScreenCropTests' | 0 |
| app-first | dotnet test --project tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release -- --filter-class '*ScreenCaptureShutdownTests' | 0 |
| platform-barrier | 同 platform-final；补正公开任务完成与无结果 drain 屏障后 | 0 |
| release-build | dotnet build Iris.slnx -c Release | 0 |
| full-test | dotnet test Iris.slnx -c Release | 0 |

最终源码及测试以 release-build/full-test 为准，严格先完整构建后全量测试，未并行执行。新捕获测试使用 actual service/adapter/worker + fake OS；真实 WinRT 编解码只处理合成像素。全量保留原 T-022A 只读 cursor/monitor smoke，不能描述为全套无 native 桌面查询。
