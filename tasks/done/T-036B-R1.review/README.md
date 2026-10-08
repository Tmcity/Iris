# T-036B-R1独立验收


## 架构师最终复审结案（2026-10-08）

提交`0cd9e5216f5bf52074536b91dd2c95681b336b50`通过。相对B仅两个Providers清理协作文件和新增清理协调测试改变；Core/Infrastructure/App/公开契约/路线/配置/默认/包保持。Stop原子接管或加入已有清理，并记录开始后出现的清理责任；执行与等待均在锁外，完成结果和引用归还同步发布，真正失败保留给后续显式重试。

独立Release原始输出：

```text
已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:08.13
```

独立全量原始输出：

```text
测试运行摘要: 已通过!
  F:\Iris\tests\Iris.Core.Tests\bin\Release\net10.0\Iris.Core.Tests.dll (net10.0|x64) 已通过 (5s 834ms)
  F:\Iris\tests\Iris.Providers.Tests\bin\Release\net10.0\Iris.Providers.Tests.dll (net10.0|x64) 已通过 (4s 043ms)
  F:\Iris\tests\Iris.Platform.Windows.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Platform.Windows.Tests.dll (net10.0|x64) 已通过 (4s 811ms)
  F:\Iris\tests\Iris.App.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.App.Tests.dll (net10.0|x64) 已通过 (1m 22s 002ms)
  F:\Iris\tests\Iris.ArchitectureTests\bin\Release\net10.0-windows10.0.19041.0\Iris.ArchitectureTests.dll (net10.0|x64) 已通过 (2s 318ms)
  F:\Iris\tests\Iris.Infrastructure.Tests\bin\Release\net10.0\Iris.Infrastructure.Tests.dll (net10.0|x64) 已通过 (10s 990ms)

  总计: 3496
  失败: 0
  成功: 3494
  已跳过: 2
  持续时间: 1m 22s 452ms
```

最终DLL重新绑定独立探针：原七组实际Core/两命名管道/流式/失败清理回归全部通过（regression.log）。原并发反例和正反顺序失败保留的原始输出：

```text
DISPOSE_RACE originalDisposePending=True; stopFaultedBeforeOwnerCompleted=False; retainedContent=1; stopFinishedBeforeOwnerCompleted=False
DISPOSE_RACE cleanup complete: handlers=0 contents=0
DISPOSE_RACE originalDisposePending=True; stopFaultedBeforeOwnerCompleted=False; retainedContent=1; stopFinishedBeforeOwnerCompleted=False
DISPOSE_FAILURE joinedStopFailed=True; originalAttempts=1; retainedContent=1; sends=1
DISPOSE_RACE cleanup complete: handlers=0 contents=0
DISPOSE_RACE originalDisposePending=True; stopFaultedBeforeOwnerCompleted=False; retainedContent=1; stopFinishedBeforeOwnerCompleted=False
DISPOSE_FAILURE joinedStopFailed=True; originalAttempts=1; retainedContent=1; sends=1
DISPOSE_RACE cleanup complete: handlers=0 contents=0
```

证据T-036B-R1.review，含源码/程序集绑定。B最初独立全量失败与dispose-race反例仍在T-036B.review；R1中间相反顺序失败仍在R1.evidence，不覆盖历史失败。两个窗口站error=183环境跳过保持。B已确认的迁移/凭据/路线/容量/Host和只读视觉结果保留，未以本次无UI改动重做图像作为并发证据。

T-036B及R1正式结案，父T-036继续▶，下一唯一C完成完整网络设置页和独立HEAD测试。真实代理/socket、TLS/SOCKS/Windows认证、真实用户数据与NFR仍未验证；不得以ConnectCallback内存流冒称实机通过。不push。

复现项目引用当前本机Release DLL；日志与binding仅对应上述提交，后续重新绑定不得覆盖本次证据。默认运行七组回归；--dispose-race/--dispose-race-failure/--stop-first-failure运行三个清理顺序检查。
