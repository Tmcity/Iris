# T-024A 命令记录

所有 txt 文件均由本轮命令重定向的系统临时目录 iris-t024a-*.log 逐文件复制，保留失败、跳过和首轮通过，未覆盖旧日志。

| 文件（省略 iris-t024a- 前缀与 .txt 后缀） | 命令 | 退出码 |
|---|---|---|
| targeted-first | dotnet test --project tests/Iris.Platform.Windows.Tests/Iris.Platform.Windows.Tests.csproj -c Release -- --filter-namespace 'Iris.Platform.Windows.Tests.Hooks' | 1 |
| targeted-second | 同上 | 1 |
| targeted-third | 同上 | 2 |
| targeted-fourth | 同上 | 0 |
| targeted-fifth | 同上 | 1 |
| targeted-sixth | 同上 | 0 |
| targeted-seventh | 同上 | 0 |
| targeted-final | 同上 | 0 |
| release-build | dotnet build Iris.slnx -c Release | 0 |
| full-test | dotnet test Iris.slnx -c Release | 0 |
| targeted-stop-final | 定向命令同上 | 0 |
| targeted-generation-final | 定向命令同上 | 0 |
| release-build-final | dotnet build Iris.slnx -c Release | 0 |
| full-test-final | dotnet test Iris.slnx -c Release | 0 |

最终源码对应 targeted-generation-final、release-build-final、full-test-final。两轮完整构建与全量测试均串行执行。第一次全量以后发现 Stop 收尾期间故障报告与首次 Start 身份同步登记的审计点，修改后执行最后一轮，不以第一次全量结果代替最终结果。

在这些文件之前还有两次 `dotnet build src/Iris.Platform.Windows/Iris.Platform.Windows.csproj -c Release --no-restore`，当时未重定向日志：首次退出 1（CA1822 与 CA2016），第二次退出 0；原命令输出的失败摘录见主报告。

范围更正：完整方案测试包含既有 DesktopContextNativeReadOnlyTests 的实际光标/显示器查询，不能将全量测试描述为全程 fake 或未访问桌面。新增 Hooks 测试使用实际 Monitor/Adapter/Recognizer 和 fake OS/桌面边界。详细偏差见主报告。
