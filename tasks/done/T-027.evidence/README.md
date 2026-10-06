# T-027 开发实施与验证（2026-10-06）

基线 `4805b224f041f2bc437ee9460290ef7bc8d0bc41`。完成本单开发与自动化验证，请求独立复审；T-027 保持▶，未自行结案或开始后续 OCR 工单。

## 实施与验收对应

| 范围 | 实际实现和回归 |
|---|---|
| 实际 codec / 同实例 | WinRtImagePreprocessor 使用自有内存流、DataWriter、BitmapDecoder，检查实际 codec/宽高后以 GetPixelDataAsync 校验像素；快路径也完整解码与清理后返回同一 ImageData。ValidFastPath_ActuallyDecodesAndReturnsSameInstance 覆盖 PNG/JPEG/BMP；RealFormats_EncodeAndDecodePixels 覆盖三格式之间实际编码解码和像素。 |
| 输入与内部预算 | 同步快照约束格式顺序，校验非法约束、空图/非法格式/非正尺寸、单边/像素/编码数据预算；真实头部、内容与画布也在分配前检查。InvalidConstraints_AreRejectedWithoutCodec、InvalidInput_IsSafe、HeaderAndCanvasBudgets_RejectBeforePixelAllocation 覆盖无效、损坏/截断、codec/尺寸失配及超预算。 |
| 几何 / 白边 | 同一有理比例计算两维，各自向下取整且至少一像素，按最长边限制后居中补白。RationalResizeAndWhiteCanvas_PreserveContent 实际解码验证规范全部例子、极端横/竖图及整数边界的每个内容/白边像素，奇数补右/下。 |
| alpha / 插值 | WinRT 明确 Fant、BGRA8、Premultiplied、忽略 EXIF 方向、sRGB；缩放后按预乘通道加白色余量合成，输出 alpha255。StraightAlpha_CompositesWhiteAndFastPathKeepsOriginal 验证透明/半透明/不透明像素与快路径；FantDownscale_PremultipliedTransparencyDoesNotBleedHiddenColor 验证透明噪声不会污染可见颜色，实际降采样得到中间灰度而非最近邻端值。 |
| 格式 / JPEG | 保留被接受的原格式，否则选择有序列表首项；只有 JPEG 被接受才尝试降质，每次新 encoder/空流，复用同一转换像素而不重复解码上轮 JPEG。JpegLadder_UsesEachQualityOnceAndOriginalPixels、PreferredFormatAndConvertedJpeg_DoNotRepeatNinety、AcceptedOriginalFormat_HasPriorityOverListAndNonJpegCannotEscapeBudget 覆盖完整质量阶梯、避免重复90、顺序及最终超限。 |
| 精确字节 | 按真实输出长度与精确 Base64 padding 比较；读取前检查流长度与内部预算，读取后验证长度。ExactBudget_AcceptsEqualityRejectsOneLess 覆盖模3和等于/少一边界；OriginalByteBudget_UsesExactLengthOnFastPath 覆盖原实例预算；OutputLength_IsCheckedBeforeAllocationAndAfterRead 覆盖空流、内部超限和短读。预算分支以 fake 外部长度控制，真实格式/像素验证另由 actual codec 测试完成。 |
| 取消 / 主清理错误 | 每个外部操作前后和像素行处理检查取消，但不对已发 WinRT 操作使用可提前退出的 caller WaitAsync。CallerCancellation_WaitsForActualBoundaryAndCleanup 覆盖写入、解码、像素、encoder创建、Flush、读出、各资源处置门闩；未知底层自行取消为 Unknown。主错误/全部独立清理错误保留固定阶段和 HRESULT，不输出外部 Message/Detail/Data/Inner。 |
| 重试 / 并发 / GC | 单实例内部串行处理已接纳请求，排队 caller 取消可独立结算；服务只登记非结果结束屏障。失败资源保留原实例、成功清理步骤不重做，新请求先清旧责任。相应回归检查显式和自动重试、不重新编码、并发独立与格式快照。CompletedResult_IsNotCachedOnLiveService 在同一存活实例上分别检查快路径/转换路径输入和输出对象/字节弱引用，并有可回收对照；GC前不调用 Stop/Retry/下一请求。 |
| App 注册 / 退出 | 平台注册扩展绑定具体实例与 IImagePreprocessor，实际 AppHostBuilder 使用该注册。Bootstrapper 统一停止准入，在既有截图 B→A→capturer 顺序中加入独立预处理排空；失败纳入已有安全聚合且仍执行其它可独立清理服务。ActualHost_RegistersSameLazyPreprocessor、ActualBootstrapper_WaitsForCodecAndRetriesBeforeHostShutdown 验证实际组合根、在途等待、失败不停止 Host/Dispatcher、同实例重试和独立 fake 选区资源清理。 |

新增 Platform/Imaging 内部实现和测试。编码边界只复用稳定的 WinRtScreenEncoderIo/IScreenEncoderIo，没有修改已验收截图或剪贴板实现。Core 仅同步预置 IImagePreprocessor XML 注释；公开签名/record/枚举/字段、包/default/JSON/schema 不变。测试项目仅增加新支撑源链接，没有新增依赖包。

## 实际执行与中间记录

完整日志均保留。本轮先定向，随后串行 `dotnet build Iris.slnx -c Release` 与 `dotnet test Iris.slnx -c Release`。最终摘要及完整 Release 输出附在 T-027.md；本报告是开发执行结果，未冒称架构师独立验收。

- development-build-1.txt：初版资源所有者未声明异步处置接口，CA1001；已补实现。
- targeted-1.txt：测试支撑遇到 CS0649、CA1068、CA1001；已修正属性、CancellationToken末参和处置接口。
- targeted-2.txt：测试 lambda 的 await 和 CA2201；已先创建合成输入，并以携带数值 HRESULT 的 IOException 注入外部故障。
- targeted-3.txt、targeted-4.txt、targeted-final.txt：依次加入更多检查后的实际 Platform 定向通过日志，不覆盖先前记录。
- targeted-app-1.txt：实际 App 注册/退出定向通过。
- release-build.txt、release-full.txt：最终全解决方案串行验证；两项既有隔离窗口站 error183 跳过按原文保留。

本单测试没有生成需视觉验收的 UI 页面；图像行为由真实内存 codec 解码后的尺寸/格式/像素断言验证。官方快照仅作为规范来源，不作为产品通过证据。没有以合成执行时间代替实机性能指标。

## 偏差、未验证与残留

无工单功能范围偏差；没有为复用重构旧截图/剪贴板，没有修改既有测试断言/超时/Skip、截图独占集合或 Render 同步。

新增自动化仅使用合成内存图、fake 外部边界和自有 WPF 测试环境。全量包含既有只读 cursor/monitor/DPI native smoke，不泛称整套没有桌面查询；隔离窗口站 error183 跳过未回退到用户剪贴板。

真实用户截图/选区/剪贴板、物理输入/捕获/hook、用户配置/密钥/Run、HTTP、真实 Iris 主程序和性能脚本均未执行。真实 OCR 识别效果、大图实机吞吐、混合 DPI/HDR、焦点及 NFR **未验证**。

工单全文归档 T-027.md，完成后删除 current_task。架构师规范/契约注释/结案交接/BACKLOG/CCR 及 T-026B-R2.review 原样保留随单提交；CCR-026 仅标已落地待复审。`.codex/` 是预存未跟踪工具，未改、未删、未提交。不 push/reset/clean/rebase/amend，不开始 T-028 及以后。
