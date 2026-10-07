# T-036B独立复审


## 架构师首次独立复审 / R1（2026-10-08）

提交eac0c2f0838eefba6907ffc4ba230fb53c5b8cf8的源码和视觉指纹匹配（T-036B.review/delivery-check.json），独立Release：

```text
已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:22.45
```

独立全量原始汇总：

```text
测试运行摘要: 失败!
  F:\Iris\tests\Iris.Providers.Tests\bin\Release\net10.0\Iris.Providers.Tests.dll (net10.0|x64) 失败并出现 1 个错误 (3s 759ms)
  F:\Iris\tests\Iris.ArchitectureTests\bin\Release\net10.0-windows10.0.19041.0\Iris.ArchitectureTests.dll (net10.0|x64) 已通过 (1s 901ms)
  F:\Iris\tests\Iris.App.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.App.Tests.dll (net10.0|x64) 已通过 (1m 07s 926ms)
  F:\Iris\tests\Iris.Platform.Windows.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Platform.Windows.Tests.dll (net10.0|x64) 已通过 (4s 499ms)
  F:\Iris\tests\Iris.Infrastructure.Tests\bin\Release\net10.0\Iris.Infrastructure.Tests.dll (net10.0|x64) 已通过 (8s 596ms)
  F:\Iris\tests\Iris.Core.Tests\bin\Release\net10.0\Iris.Core.Tests.dll (net10.0|x64) 已通过 (5s 357ms)

  总计: 3488
  失败: 1
  成功: 3485
  已跳过: 2
  持续时间: 1m 08s 352ms
已完成测试运行，但显示非成功退出代码: 2 (请参阅: https://aka.ms/testingplatform/exitcodes)
```

唯一失败位于NetworkTransportTests的iris.ocr/error SSE场景停止清理。随后以真实NetworkTransportManager/NetworkResponseContent、实际Core捕获和命名链，仅在内层HttpContent.Dispose设置受控门闩，独立复现停止竞争：

```text
DISPOSE_RACE originalDisposePending=True; stopFaultedBeforeOwnerCompleted=True; retainedContent=1
DISPOSE_RACE cleanup complete: handlers=0 contents=0
```

原始Dispose先被门闩暂停，StopCoreAsync看到CleanupRequested/ActiveOperations=0后再次Dispose；NetworkResponseContent的_disposing分支抛Unavailable，被Stop当成真实清理失败。恢复门闩、等待原Dispose与Stop并再次Stop后完整清理。该确定性探针证明生产并发缺口，不据复跑偶然通过忽略；首次全量具体调度未单独抓取，不声称所有调度细节已捕获。证据transport-probe/dispose-race-1.log与源码，运行--dispose-race。

其余独立检查保留：真实迁移修正六场景通过；实际Core/两命名管道旧客户端重试A/新请求B、EOF前SSE与停止等待、迟到A不倒退B、成功/错误响应真正Dispose失败保留重试等七组通过（migration-probe与transport-probe日志）。四张只读图已逐张检查。未提前签发C。

签发窄范围T-036B-R1，仅修内容清理进行中的协调与Stop等待，保留所有其它生产成果与原断言。父/B保持▶，R1后重新独立复核。

探针原始执行目录在报告和csproj中记录；迁移探针绑定开发期冻结DLL，不能冒称已重新绑定最终构建，最终源码/全量单独核验。transport-probe/latest-binding记录最终提交上的并发反例。
