# T-032B-R1独立验收



## 架构师最终复审结案（2026-10-07）

提交`b0607304755f22b5b115e802ad4bdf685e64e1de`通过。生产src树与03b42a3完全一致，唯一代码改动为指定队列测试；B全部源文件指纹保持，详见T-032B-R1.review/delivery-check.json。两个测试采用明确异步门闩，在释放前断言等待项取消/实际运行尚未结束，finally完整释放并观察所有任务，原业务断言保留。

独立Release原始输出：

```text
已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:35.76
```

独立定向原始输出：

```text
测试运行摘要: 已通过!
  总计: 2
  失败: 0
  成功: 2
  已跳过: 0
  持续时间: 2s 478ms
```

独立全量原始输出：

```text
测试运行摘要: 已通过!
  F:\Iris\tests\Iris.Platform.Windows.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Platform.Windows.Tests.dll (net10.0|x64) 已通过 (5s 619ms)
  F:\Iris\tests\Iris.ArchitectureTests\bin\Release\net10.0-windows10.0.19041.0\Iris.ArchitectureTests.dll (net10.0|x64) 已通过 (3s 221ms)
  F:\Iris\tests\Iris.Infrastructure.Tests\bin\Release\net10.0\Iris.Infrastructure.Tests.dll (net10.0|x64) 已通过 (11s 466ms)
  F:\Iris\tests\Iris.App.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.App.Tests.dll (net10.0|x64) 已通过 (1m 43s 021ms)
  F:\Iris\tests\Iris.Core.Tests\bin\Release\net10.0\Iris.Core.Tests.dll (net10.0|x64) 已通过 (6s 428ms)
  F:\Iris\tests\Iris.Providers.Tests\bin\Release\net10.0\Iris.Providers.Tests.dll (net10.0|x64) 已通过 (4s 937ms)

  总计: 3338
  失败: 0
  成功: 3336
  已跳过: 2
  持续时间: 1m 43s 472ms
```

首次B独立全量失败记录仍保留在T-032B.review/full.log。B的九组实际窗口/Core/Registry探针和全部最终视觉复核继续有效，R1生产零改动，不重复制造无新增价值的验证。两项跳过仍为既有隔离窗口站error=183限制。真实用户配置/云请求/桌面交互/多屏性能未验证，仍留后续T-040。T-032B-R1、T-032B及父T-032正式结案；M6延期，下一唯一工单T-035。
