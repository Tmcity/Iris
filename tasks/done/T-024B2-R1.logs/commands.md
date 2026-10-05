# T-024B2-R1 命令与证据

txt 原样复制自本轮临时日志；首次失败、中间通过和辅助程序编译失败均保留。

文件名省略 iris-t024b2-r1- 前缀和 .txt 后缀：

| 文件 | 命令 | 退出码 |
|---|---|---|
| baseline | dotnet test --project tests/Iris.App.Tests/Iris.App.Tests.csproj -c Release -- --filter-class '*MousePartialShowFailureTests' | 2 |
| targeted-first | 上述 App 测试前缀；--filter-class '*MousePartialShowFailureTests' --filter-class '*MouseInteractionTests' | 0 |
| targeted-second | 同 targeted-first | 1 |
| targeted-third | 同 targeted-first | 0 |
| targeted-final | 同 targeted-first，另加 --filter-class '*MouseBootstrapperTests' | 0 |
| independent | dotnet run --project C:/Users/28910/AppData/Local/Temp/iris-t024b2-independent-review/Review.csproj -c Release --no-restore | 1 |
| replay-restore | dotnet restore C:/Users/28910/AppData/Local/Temp/iris-t024b2-r1-replay/Review.csproj --source C:/Users/28910/AppData/Local/Temp/iris-t024b2-r1-replay/empty-feed -p:NuGetAudit=false | 0 |
| replay | dotnet run --project C:/Users/28910/AppData/Local/Temp/iris-t024b2-r1-replay/Review.csproj -c Release --no-restore | 0 |
| release-build | dotnet build Iris.slnx -c Release | 0 |
| full-test | dotnet test Iris.slnx -c Release | 0 |

baseline 在生产修复前执行，两个真实显示后失败对照为红。targeted-second 是新增测试编译失败，不是通过。independent 编译时目录中的 Program 使用了 Result，而其旧复制 placement fixture 仍只有 FailShowing；未运行到产品反例。未改写该独立目录的源码/原日志，而在新 replay 目录复制当时 Program、Review.csproj、GlobalUsings.cs，配合同轮工作区的外部 fixture 源文件。仍只令 external placement 在 show=true 时返回 false，没有替换 Controller/Reader 状态机。程序快照、工程和指纹附在本目录。

replay-Program.cs.txt 来自该独立程序当时版本，除原失败/期限测试还验证旧 Click、重试后的新候选以及点击不重读。replay-Review.csproj.txt 的产品引用来自当前 Release 输出；辅助程序的未使用字段警告按原文保留，不能混为产品 Release 警告。恢复仅使用一个空的本地 feed 和已有缓存，不访问 HTTP。

最终先完成 Release build，再执行 full test；没有并行构建/全量测试。原 T-022A 只读 desktop smoke 和两项隔离窗口站环境跳过继续保留。
