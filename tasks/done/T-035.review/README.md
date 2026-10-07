# T-035独立验收证据


## 架构师独立验收结案（2026-10-07）

最终提交`d8b49b7c6719962eedbed25fe2290d423e07aa5d`通过。完整工单/源码/新增及旧测试调整已核验，源指纹均匹配；历史仓储实现、数据库schema、默认配置和包未改，见T-035.review/delivery-check.json。CCR-035精确增量已落地，T-035与CCR-035正式结案。

独立Release原始输出：

```text
已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:43.63
```

独立全量原始输出：

```text
测试运行摘要: 已通过!
  F:\Iris\tests\Iris.Infrastructure.Tests\bin\Release\net10.0\Iris.Infrastructure.Tests.dll (net10.0|x64) 已通过 (8s 367ms)
  F:\Iris\tests\Iris.Platform.Windows.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Platform.Windows.Tests.dll (net10.0|x64) 已通过 (4s 459ms)
  F:\Iris\tests\Iris.Core.Tests\bin\Release\net10.0\Iris.Core.Tests.dll (net10.0|x64) 已通过 (5s 452ms)
  F:\Iris\tests\Iris.App.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.App.Tests.dll (net10.0|x64) 已通过 (1m 07s 803ms)
  F:\Iris\tests\Iris.Providers.Tests\bin\Release\net10.0\Iris.Providers.Tests.dll (net10.0|x64) 已通过 (3s 756ms)
  F:\Iris\tests\Iris.ArchitectureTests\bin\Release\net10.0-windows10.0.19041.0\Iris.ArchitectureTests.dll (net10.0|x64) 已通过 (2s 106ms)

  总计: 3400
  失败: 0
  成功: 3398
  已跳过: 2
  持续时间: 1m 08s 223ms
```

最终构建DLL重新绑定独立探针，原始输出：

```text
PASS history-only save is not dropped
PASS invalid current history number preserves draft and blocks joint commit
PASS history clean external field survives joint save
PASS history dirty conflict retains target without committing other domains
PASS history conflict introduced inside update rolls back joint credentials
PASS invalid history rejected before credential staging
PASS actual history list uses summaries and full detail for copy/retranslate
PASS last-page delete returns to populated page and canceled clear does not write
PASS late query cannot replace latest filter and admission remains bounded
PASS closing waits for actual clipboard work and duplicate close shares owner
PASS write success UI publication remains owned until shutdown
PASS history screenshot receipt restore
PASS history screenshot receipt show
PASS history screenshot receipt close-new
PASS history partial placement failure rolls back before retry
```

独立探针使用实际HistoryWindow/Presenter/VM、临时真实SQLite、实际Core编辑与SettingsWindow。该探针的复制/翻译通信为窄替身，不冒称单独证明实际翻译链；实际Host/TranslateWindowPresenter/Registry/Provider与完整退出由源码复核及最终全量中的对应集成测试证明。最终独立全量重新生成64张图，全都与架构师逐格查看的版本逐字节相同（final-visual-check.json）；图像检查覆盖明暗/正常和小工作区、行操作/多服务/确认/错误/收尾重试和设置。

首次全量失败保留：缺新History端口的旧DI夹具、托盘新增项顺序、旧普通Enter测试实时modifier依赖已分别维护，原业务断言保持；无法证明失败瞬间用户实际键态，不将其冒称已知外部干扰。独立开发期settings-1的过早断言属于审查夹具，已等待UI队列发布且保留原断言，最终通过；所有日志保留。只读窗口站error=183两项既有跳过仍在。

**未验证**：用户真实历史/配置/密钥/剪贴板、云HTTP、物理输入/IME、前台焦点/混合DPI与性能，留T-040。当前只完成T-035实现验收，不代表M7或总体项目完成。下一阶段T-036网络/代理设置，先验证凭据持久化与动态切换边界再冻结契约；完整原范围不减少，M6继续延期。

复现：先构建当前要评审的提交，再执行prepare.py生成本机引用副本与manifest，随后build Review.csproj并运行--settings/--history/--lifecycle/--preparation。当前归档日志绑定上述已验收提交，不用后续产物覆盖。development-core仅为开发期后端诊断，未相加充当最终独立探针数量。
