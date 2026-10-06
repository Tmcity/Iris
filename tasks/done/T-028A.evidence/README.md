# T-028A 开发实施与验证（2026-10-06）

基线 `b3f1f1b67eafc20bab12290dba40051b4cc0ab51`。本单是新增表单图像预算能力，不是 T-027 缺陷返工。开发已完成，请求独立复审；不自行结案或开始 T-028B。

## 实施

- 按已预置 specs/CONTRACTS.cs 同步 Core ImageConstraints 的可选 init 属性 MaxFormUrlEncodedBytes 及 IImagePreprocessor 注释。默认 null，原五参数构造/Deconstruct 与 PrepareAsync 签名保持。非正预算在创建 codec 前以安全 InvalidArgument 拒绝；原快照 record copy 同时保留新属性。
- FormUrlEncodedImageLength 直接读取 ReadOnlySpan<byte>，以完整三字节组的四个 sextet 是否为62/63统计 `+`/`/`，单独处理末尾 padding；在原 Base64 长度上为每个转义字符加二。长度和累加使用 long，输入长度受 Span 的 int 范围及现有内部图像预算约束，不会溢出。没有分配 Base64/URL 字符串或复制图像来计数。
- 每批完整字节扫描前检查取消，并在尾部处理前再次检查。IImagePreparationFactory 增加默认空实现的内部批次检查点，供 fake 在实际计数入口建立 entered 门闩；生产检查点不做外部工作，调用前后均检查原 caller/Stop。它不改变 codec 实现或 App 接线。
- 原图快路径和每轮编码结果使用同一双预算判断：旧 raw/Base64 必须满足，启用的新表单值预算也必须满足。null 不进入计数循环。只因新增预算超限时，沿用原 JPEG 允许列表、90/80/70/60/50、同一像素源、新 encoder/空流及已试90不重复的控制流程，不缩图或裁切。

App 生产/退出、既有截图/剪贴板、其它 Platform 生产、Provider/HTTP、包/default/JSON/schema 均未改。测试支撑只新增受控输出字节与批次门闩，不改原 T-027 断言。

## 验证对应

| 检查 | 实际回归与结果依据 |
|---|---|
| 精确计数 | ExactSextetCount_MatchesIndependentFormContent 使用小块 .NET FormUrlEncodedContent 结果作为独立预期，覆盖空、短数组、模3、无转义、+/密集、混合以及跨统计批次输入。预期未调用被测计数 helper。 |
| null / 契约 | NullBudget_PreservesFastAndConversionWithoutCounting 将批次回调设为抛错，原实例与实际转换仍正常完成，证明 null 不扫描；原五参数 Deconstruct 在测试中实际编译/使用。InvalidFormBudget_IsRejectedBeforeCodec 覆盖零、负数与 long.MinValue。 |
| 实际白 BMP | RealWhiteBmp_OnlyAdditionalBudgetTriggersJpegAndPreservesPixels 使用独立构造的有效24位白BMP，真实 WinRT 解码/预处理/输出解码。旧约束、精确等值新约束、long.MaxValue 均返回原实例；新8MiB表单值约束才触发JPEG。真实输出宽高与全部白色像素均验证。white-bmp.json 是实际执行产物。 |
| 字节密度与质量 | SameLengthDifferentDensity_ControlsAllJpegQualities 使用相同长度、不同转义密度的编码输出覆盖每个质量成功及最终失败；原像素源一致、每轮资源释放。ConstraintSnapshotAndConvertedJpeg_DoNotRepeatNinety 验证列表快照、新预算仍生效及90→80不重复。 |
| 两预算并立 | FormOnlyBudget_EqualityAndOneLessWithoutJpeg、OriginalFormOnlyOverLimit_WithoutJpegDoesNotReencode 验证新预算等值/少一、不接受JPEG时不绕过规则；LegacyBudget_IsStillRequiredEvenWhenFormFits 分别验证旧 raw/Base64 预算不能被新预算替代。 |
| 实际统计取消 / Stop | ActualCountingBatch_CancellationAndStopJoinWork 在原图和实际编码后的计数第二批入口建立 entered，分别触发 caller 取消或 Stop；先放行，再等待真实 Task，验证任务未提前完成、不进入后续批次。输出计数入口还检查 encoder stream 已真实释放。门闩外层生命周期覆盖整个测试 scope，finally 先放行后等待，保留主/清理错误。 |
| 原成果对照 | Imaging 定向保留原 T-027 测试；另存原独立程序，以最终实际 DLL 运行其完整40项，未修改任何目标断言或 fixture，覆盖实际 codec、Host、并发、取消、资源重试及 GC。 |

独立预期只按完整三字节对齐的小块创建 Base64/表单字符串；块之间编码无状态，末尾 padding 只出现在末块。白 BMP 的计数也是逐块累加，不生成整份大图的 Base64/URL 字符串。所有 FormUrlEncodedContent 都只在内存中取编码字节，没有发送 HTTP。

white-bmp.json 中 formImageValueBytes 只包含图像值，不能与规范观测 JSON 中包含其它字段的整份 formBytes 混称。后续 Provider 扣除其它表单开销和真实服务端边界由 T-028B 定稿。

## 实际执行记录

先定向，随后串行 Release 全解决方案构建和全量测试，再以最终 DLL 复用原独立40项程序。完整日志及工单附录保留原文：

- targeted-form-1.txt：初次测试编译遇 CA1869（仅诊断 JSON 的 JsonSerializerOptions 分配），已去除不必要的选项实例；不删除或更改测试断言。
- targeted-form-2.txt：该轮新增表单测试通过。
- targeted-imaging.txt：新增完整检查及原 Imaging 测试定向通过。
- release-build.txt、release-full.txt：最终串行 Release 构建与全量；两项既有 error183 环境跳过原文保留。
- review-probe.txt：`C:\Users\28910\AppData\Local\Temp\iris-t028a-development-review` 另存副本的实际输出。probe/ 保留执行代码、fixture、项目和引用指纹；与 `iris-t027-independent-review` 的原代码/fixture 比较见 manifest.json。没有覆盖已验收目录或改动目标断言。

这些是本开发会话执行结果，不冒称架构师本轮独立复审。没有额外运行真实 Iris 或性能脚本。

## 范围、未验证与交付

无工单功能范围偏差。T-027 已验收成果及架构师本轮结案/交接、规范、契约、BACKLOG/CCR 和 T-027.review 保留随本单提交。工单全文归档 T-028A.md，完成后删除 current_task；父 T-028/A 保持▶，B☐，CCR-027 仅已落地待复审。

新增自动化只有合成图、实际内存 codec 与 fake 外部边界。既有只读 cursor/monitor/DPI native smoke、error183 跳过保持。真实 OCR/Token HTTP、用户截图/选区/剪贴板/键鼠/hook、用户配置/密钥/Run、真实 Iris 和性能脚本均未执行。真实服务端上传上限、完整请求兼容性、实机吞吐/NFR **未验证**。

`.codex/` 是预存未跟踪用户工具，未改、未删、未提交。独立本地提交，不 push/reset/clean/rebase/amend，不启动 T-028B 或 T-029。
