# T-028B 开发实施与验证（2026-10-06）

基线 `f091e1565b81ed63cb4e614940f710631940a6dc`。本单完成百度 OCR Provider/Factory，等待架构师独立复审；父 T-028/B 保持▶、A☑，不自行结案或开始后续 OCR 工单。

## 实施与验收对应

| 范围 | 实现与实际回归 |
|---|---|
| Factory / 配置 | BaiduOcrProviderFactory 注册为 IOcrProviderFactory（BaiduCloud），只接收 Profile/ProviderCredentials 和 HTTP/time 依赖。BaiduConfiguration 固定选项、描述、路径与预算，构造不创建 client/请求/后台任务。四模式、默认选项、特殊字符凭据/语言、网关前缀、无效配置和未配置零HTTP均有实际 Factory/Provider 回归。 |
| 表单 / 约束 | 标准4096/8,000,000字节，高精度8192/10,000,000字节；保留4MiB Base64、最短15、只读[Jpeg,Png,Bmp]，从完整上限扣除本Profile最坏非图像开销。后台验证输入、一次Base64并以FormUrlEncodedContent实际编码表单，再检查完整body；业务重试使用新message/content与同一只读body，不重新处理图像。实际中间大小表单验证两种模式边界，元数据耗尽预算/完整body超限在HTTP前拒绝。 |
| 语言 | 覆盖全部正式Hint映射、大小写、自动/未知值、显式值原样转义及全部固定字段。probability=true，不发送detect_language；DetectedLanguage始终null。 |
| Token | 异步互斥获取，等待前后检查缓存；每次发布独立TokenReceipt，以引用身份条件失效。正常并发只取一次，等待者取消不取消获取者，获取者取消等待实际HTTP与资源结束后其它调用可重取。同字符串新receipt/迟到旧110/111、失败恢复、短TTL、30天封顶/提前余量及收到已过期Token均验证。 |
| Test | 捕获调用入口旧receipt，不以旧缓存直接成功；并发Test可复用入口之后新Token。实际测试只有Token请求，成功Sample不含Token，失败为安全ErrorInfo。 |
| 总预算 / 重试 | BaiduCall从调用入口捕获单调起点，后台启动时仅给计时器剩余预算；Token等待、发送、读取、业务退避与收尾不重置预算。识别110/111一次、18退避500ms一次，混合最多三次逻辑识别。实际iris.ocr管道+Mock primary handler验证HTTP500与传输异常两种路径各最多九次底层识别传输，另有Token请求；生产原2次retry/10秒attempt/30秒total不变。 |
| 响应 / 结果 | 分别256KiB/16MiB有界读取，JSON深度32，大小写兼容且重复关键字段拒绝。字段类型、计数、20000行/1000000 UTF-16码元、空行/空数组、位置/置信度、段落索引/重复/遗漏行均校验。段落排序与原行保留，Bounds并集/Confidence平均条件明确，实际调用Core.SoftLineBreakMerger处理CJK、连字符、句末标点和列表，不复制规则。 |
| 安全错误 / Wire | 按正式业务表及HttpErrorMapper.MapStatus映射，识别413和216205为ImageTooLarge，OAuth非空error优先。只保留固定stage、数值状态/业务码/HResult；任意外部正文、Message/Data/Inner不透传。两个Wire DTO只同步批准的安全ToString与216205常量，JSON字段保持。 |
| 所有权 | 每次HTTP持有独立client包装器/message/content/response/body；完整等待实际操作，再收尾。content从message解绑以避免外层已Dispose标记阻断重试；失败body stream保留原对象直接重试。client、流、计时器、注册/取消错误均纳入收尾诊断与必要保留责任。新调用先清旧责任，失败不发HTTP；同步Dispose在途拒绝，异步Dispose共享屏障、停止准入、等所有调用/Token等待者并可失败重试。存活实例GC对照验证无输入/结果缓存。 |

测试主体为实际 Provider/Factory；HTTP全部进入自有 Mock handler 或 RichardSzalay.MockHttp primary handler，时间由FakeTimeProvider控制。发送、body读取、Token等待、取消回调和资源处置都有实际entered/状态观察；finally先放行，再等待实际任务与资源，保留主/清理异常。没有真实网络请求。

## OCR 专用传输脱敏

扩大日志检查到日志Message、异常对象与StructuredState后，pipeline-audit.txt精确显示：命名弹性管道会记录下层HttpRequestException的原始文本，其中包含测试构造的敏感标记。该日志是本轮未修正阶段的真实失败证据，原样保留；没有真实用户凭据。

新增 BaiduOcrTransportSafetyHandler，位于 **OCR** resilience/logging handlers 内侧，在遥测拿到异常前去掉外部Message/Inner/Data，保留HttpRequestException类别、HttpRequestError/Status/HResult以及超时/熔断/取消类别。由此保持传输重试语义。没有修改Llm、通用HTTP实现、重试次数或超时配置；只增加OCR专用安全处理器及DI接线，属于工单允许的OCR安全支撑。

最终 ActualOcrPipeline_MixedBusinessRetriesHaveNineTransportsAndSafeLogs 两个分支均通过，完整日志/异常/结构化值中不含构造的ApiKey、SecretKey、Token及服务端错误/识别文本标记。此测试只把传输退避设置为0；生产配置保持，业务500ms以fake时间单独触发。

## 原始验证与修正记录

所有中间失败/通过日志均保留，不将定向通过冒充全量结果。

- development-build-1.txt：初版CA1068/CA1001/CA2016编译规则失败；调整CancellationToken末参、资源所有者异步处置和明确不让取消跳过已接纳任务。development-build-2.txt为随后成功构建。
- targeted-1.txt：测试scope所有权接口CA1001，已补正确处置。targeted-2至targeted-8为各组检查逐步增加后的实际运行记录。
- pipeline-audit.txt：扩大安全断言后出现上述真实遥测泄漏反例；专用处理器修正后两条命名管道组合均通过，目标断言保留。
- providers-all.txt、providers-final.txt、providers-deadline-final.txt及providers-callback-final.txt：实际Providers项目测试，含既有翻译/HTTP/Wire回归。
- release-build.txt/release-full.txt：第一轮串行Release成功。随后代码审查补齐“后台启动前已消耗时间”必须从计时器扣除，新增ProfileDeadline_IncludesTimeBeforeBackgroundStart；没有宣称该新增断言在旧版本上执行过。
- release-build-final.txt/release-full-final.txt：预算修正后的全量发现新增取消错误测试的竞态。发送门闩过早放行，HTTP可能先解绑回调，故实际没有发生测试所断言的CANCEL错误。新增回调自身cancellationEntered，确认真实注入后再放行，不删除/放宽断言、不延长超时。
- providers-callback-final.txt、release-build-delivery.txt、release-full-delivery.txt：修正测试接受点后的最终定向、串行Release构建/全量；最终汇总原文附工单。

最终报告以 delivery 日志为准，文件名含final的中间失败日志同样保留。没有通过重复全量碰运气抹除失败。

## 范围、偏差、未验证与交付

Core仅批准的IOcrProvider注释和Iris.Providers友元；没有Core→Providers项目引用或修改SoftLineBreakMerger。新增实现/测试均在Providers/Ocr/Baidu与对应测试，DI增加Factory及OCR专用安全处理器。App、Platform生产、配置schema/default、中央包和通用HTTP/Llm实现保持零差异。除已说明的必要OCR日志防护外，无工单功能范围偏差。

不实现OCR Registry/Service/Normalizer/Windows Provider/Handler/设置页。架构师A结案/T-028A.review、当前规范/契约、交接/BACKLOG/CCR原样保留随本单提交。完整工单归档T-028B.md，完成后删除current_task；CCR-028仅标已落地待复审。

既有只读cursor/monitor/DPI native smoke和两项隔离窗口站error183跳过保持。真实OCR/Token/HTTP联网、用户凭据/配置/Run、用户截图/选区/剪贴板/键鼠/hook、性能脚本或真实Iris均未执行。真实API上传上限、语言/方向/坐标兼容性及NFR **未验证**；官方快照和Mock不充抵真实API证据。

`.codex/`为预存未跟踪工具，不改不删不提交。不push/reset/clean/rebase/amend；完成独立本地提交后请求架构师复审。
