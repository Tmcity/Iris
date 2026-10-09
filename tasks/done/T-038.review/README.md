# T-038 / CCR-038 独立验收结案

2026-10-10。T-038完整中英文界面与语言设置通过独立验收；T-038-R1同步验证修正一并结案。

- 功能提交：`9c1ac9d0a044e2f844e82957c841b785da9d88e5`。
- 修正提交：`30177c590d8e05e2161b6e99d3fe6385d68e73fb`。
- R1只改本地化测试及其Dispatcher任务观察入口，生产、契约、资源、布局和包相对功能提交零差异。原断言保留，见[源码与交付核对](../T-038-R1.review/delivery-binding-check.json)。

## 独立验证

提交后独立Release、原失败用例定向回归、全量测试顺序执行并完整退出。最新原始输出保存在 `../T-038-R1.review`。

Release原始摘要：

```text
已成功生成。
    0 个警告
    0 个错误

已用时间 00:00:43.83
```

全量原始摘要：

```text
测试运行摘要: 已通过!
  F:\Iris\tests\Iris.Providers.Tests\bin\Release\net10.0\Iris.Providers.Tests.dll (net10.0|x64) 已通过 (3s 743ms)
  F:\Iris\tests\Iris.ArchitectureTests\bin\Release\net10.0-windows10.0.19041.0\Iris.ArchitectureTests.dll (net10.0|x64) 已通过 (2s 197ms)
  F:\Iris\tests\Iris.Platform.Windows.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.Platform.Windows.Tests.dll (net10.0|x64) 已通过 (4s 014ms)
  F:\Iris\tests\Iris.Core.Tests\bin\Release\net10.0\Iris.Core.Tests.dll (net10.0|x64) 已通过 (5s 117ms)
  F:\Iris\tests\Iris.App.Tests\bin\Release\net10.0-windows10.0.19041.0\Iris.App.Tests.dll (net10.0|x64) 已通过 (4m 50s 134ms)
  F:\Iris\tests\Iris.Infrastructure.Tests\bin\Release\net10.0\Iris.Infrastructure.Tests.dll (net10.0|x64) 已通过 (7s 566ms)

  总计: 3653
  失败: 0
  成功: 3651
  已跳过: 2
  持续时间: 4m 50s 517ms
```

两项跳过为原有隔离窗口站创建 `error=183`，没有新增跳过。原外部语言合并失败及R1确定性修正见[R1验收记录](../T-038-R1.review/README.md)；早期失败没有覆盖。

实际Core、临时JSON、General联合保存、SettingsWindow/VM、预设菜单和生产Program/DesktopStartupRuntime语言作用域的独立探针原始输出：

```text
PASS fixed selected language across differing thread cultures
PASS real language control joins one history save without live language change
PASS fresh real JSON load drives the next language selection
PASS joint conflict preserves language draft and saved language
PASS language-only save merges unrelated external changes
PASS actual Core rejects an unsupported target without writing
PASS actual preset menu and visible add button retain stable saved data
PASS production primary language lifetime through host cleanup failure
PASS production primary language lifetime through logger cleanup failure
PASS production primary language lifetime through mutex cleanup failure
Independent language scenarios: 10; failed: 0
STA finalization: owned windows=0
STA finalization: thread exited
```

上述运行使用功能提交重建后的固定程序集，输入在运行前后不变，见 `runtime-committed-inputs.json`、`runtime-committed-result.json`。R1没有生产差异；R1提交后重新构建的验证程序集另绑定于 `../T-038-R1.review/final-verification.json`。

独立完整资源审计原始输出：

```text
Scanned C# and XAML; Chinese entries including comments: 1197
Compiled resource values matched after XML newline normalization; composite formats parsed: 1170
{"sourceEntries": 1197, "commentEntries": 979, "reviewedNonUiEntries": 218, "resourceCounts": {"App": 437, "Core": 148}, "resourceErrors": [], "englishNativeNameExceptions": {"App:LanguageChineseNative": "简体中文"}, "exceptionManifestSha256": "6fc89a21ce5423b591851ece5ccb983e868a4fea8884b9fa9abfc52434dd39fe"}
```

源码覆盖App/Core/Platform/Infrastructure实际UI和面向用户的错误；两语言资源、格式参数和实际编译资源一致。英译与逐条审阅快照逐键对应，见 `resource-text-review`。原同字串UI绕过反例经独立重新构建盘点器后被拒绝，基线仍通过；`audit-exception-final-transfer.json`确认最终检查器/App源码及相关例外位置、次数映射保持。原误通过与修正后源/日志均保留。

## 界面与行为范围

界面使用实际产品Window/VM和合成数据渲染，检查双语、明暗、正常与小工作区，涵盖全部开放设置页、OpenAI与Anthropic高级项、实际预设/托盘菜单、翻译输入/运行/结果/安全错误、历史列表/详情/空态、截图提示及通知正文。`visual-candidate-12-review.json`记录实际查看差异联系表及仅按相同字节继承的已查看图。R1提交后独立全量图与已查看版本逐项比较：

```json
{
  "ReviewedCommit": "30177c590d8e05e2161b6e99d3fe6385d68e73fb",
  "IndependentFinalImages": 410,
  "ExactReviewedImageMatches": 410,
  "ImageDifferences": [],
  "MissingImages": []
}
```

旧图中的快捷键安全说明、隐藏的添加按钮、难辨识的语言选择、高级项裁切、小高度结果区与滚动条遮挡均在已查看版本中解决。滚动序列的暂时边缘裁切有后续帧完整展示；没有用VisibleText清单代替视觉检查。原图和每步指纹保留。

语言保存通过既有联合计划一次提交，下次启动生效；当前UI及后台语言固定，清理失败反馈仍保持所选语言。原四参数构造/解构、默认/schema、翻译/OCR目标、用户和已保存命名、协议及提示词保持。readonly、冲突/合并、Reload/取消、ApplyPending与原回执语义经实际测试覆盖。

## 保留的失败与验证边界

原scope预审与随后开发实测失败、审计误通过、布局缺陷、开发构建/全量失败，以及独立夹具Popup/STA退出超时，均保留并区分来源。独立夹具排除菜单时正常退出；等待自有Popup开关动画并明确结束自有Application后完整通过，未据此改产品。父单的独立全量失败 `final-full-1.log` 被R1确定性定位为测试等待边界，原日志和源码保留。

未验证：真实用户主应用/配置/秘密、外网与生产管道、真实桌面输入/选区/剪贴板、原生通知气泡/系统标准对话框、物理多屏与性能/NFR。按本单禁止边界未运行性能脚本；现有全量的只读native/合成SDK检查按原范围保留。本结案不代表打包发布或最终真实环境验收完成。

用户要求“T-038完成后请先停止工作，直到我说继续后再推进”。已到该停止点；T-039及后续工作等待用户明确继续，M6继续延期。
