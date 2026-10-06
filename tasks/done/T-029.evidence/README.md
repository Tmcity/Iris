# T-029 开发实施与验证（2026-10-06）

基线 `fe05ccbf84913b8e23928031ed24cecf5b43f924`。完成 Windows 本地 OCR Provider/Factory，请求独立复审；T-029 保持▶，未自行结案或开始 T-030。

## 实施与验收对应

| 范围 | 实现和实际回归 |
|---|---|
| Factory / 能力 | WindowsOcrProviderFactory 实现 WindowsLocal，注册到现有 Platform DI。配置合法后只读查询一次 MaxImageDimension，能力至少40、有效上限取SDK与32768较小者；失败为安全ProviderUnavailable，不制造占位数值。Descriptor不需Key，不保存传入凭据；构造不创建engine、图像、窗口或后台任务。Factory与实际DI回归覆盖只读查询次数、能力边界、无Key、未创建engine和非法参数。 |
| Constraints | 285212672原字节、最短40、只读Png/Bmp列表、无Base64/表单预算；沿用现有只读ImagePreparationRules.PixelBytes检查全局像素预算，未改旧实现。 |
| 语言 | 每请求按固定LanguageTag、Hint映射、SDK用户语言选择；OrdinalIgnoreCase去重候选。native边界只把非法tag和null结果作为候选失败，其它原生错误安全报告。每请求持有独立engine，不建立语言缓存、不强制释放COM投影。语言优先级、重复、非法/缺包、全部失败、实际返回语言与候选不完全一致等均用fake边界验证；DetectedLanguage不伪造。 |
| 实际图像 | WindowsOcrImage后台拥有InMemoryRandomAccessStream/DataWriter/BitmapDecoder/SoftwareBitmap，校验真实codec和尺寸后明确BGRA8、忽略EXIF、不旋转。Png/Bmp真实内存解码回归检查实际engine边界收到的SoftwareBitmap宽高/格式；无效、损坏、截断、尺寸失配和内部预算安全拒绝。 |
| 原位图生命周期 | RecognizeAsync不带可提前脱离的caller WaitAsync；实际任务结束后才处置位图、writer、流，成功步骤不重复。取消时通过实际LockBuffer检查位图仍可用；原生/清理多错误保留阶段和HRESULT，原资源可重试，不重做识别；新请求先收旧责任，失败不创建新图或识别。 |
| rune / Bounds | 复用Core.UnicodeScripts.IsCjkForLineMerge，按rune遍历，只删除两个项目定义CJK之间一个ASCII空格，保留补充平面、韩文、双空格/tab/NBSP、混合文和首尾。原字符串片段拼接避免拆分代理对。逐词验证有限/正尺寸/非负坐标，以floor/ceil和安全整数并集生成Bounds；无词为null，Confidence为null，SDK行序和空结果保留。 |
| 结果预算 | 20000行、100000词、1000000 UTF-16码元，native快照及Provider转换均设界限；文字、词位置和rune循环批次检查取消。新增实际结果处理批次entered门闩，证明取消仍等格式化退出和原位图收尾。 |
| 并发 / 总期限 | 单实例串行、每调用独立CTS和结束屏障；排队caller取消不影响在途，不创建新engine。Profile预算从准入起覆盖队列/后台启动/所有工作，计时器扣除已耗时。caller/Stop/Timeout/未知底层取消分别验证；同步Dispose拒绝在途，异步共享停止屏障、等所有实际任务且可失败重试。 |
| Test / GC | Test仅按已验证能力选择配置/用户语言，不识别或创建图像；无语言为OcrLanguageUnsupported。存活同实例、调用方放弃已完成输入/输出/engine引用并让栈展开后，GC弱引用和可回收对照通过；GC前不调用Stop/Retry/新请求。 |

Core只同步批准的说明与Iris.Platform.Windows友元；UnicodeScripts、公开签名/字段/枚举均未改。新增Platform/Ocr与测试、必要DI绑定和嵌入测试PNG。没有新增NuGet或更改App、百度/HTTP、已验收Platform实现、配置/default/schema。

## 真实 SDK 合成 smoke

唯一新增真实SDK测试为 `WindowsOcrSdkSmokeTests.RealSdk_RecognizesOnlyOwnedSyntheticHelloPng`，在定向与全量运行同一用例。输入是固定640×160白底黑色Arial大字“HELLO 123”PNG，已实际查看，嵌入测试程序集。fixture仅使用系统Arial字体离线生成；不是屏幕截图、用户文档或剪贴板图像。

实际Factory/Provider、WinRT内存decoder/SoftwareBitmap与OcrEngine全部执行；ObservedNative/ObservedEngine只记录真实返回值，不替换能力、语言匹配、识别输出或产品状态机。最终 sdk-result.json 同时保存已安装语言、所选RecognizerLanguage、实际能力、原始SDK行和Provider输出。

本环境实际结果为Passed，SDK能力10000，所选en-US，原始SDK行文本为“HELLO 123”，Provider输出相同。本轮没有新增语言包环境跳过，也没有SDK激活/兼容性失败。没有安装/切换语言包、修改包身份或安装方式。

这只支持本机该合成样本的兼容性结论，不推广为真实多语言效果/实机吞吐/NFR通过。除该明确smoke，新增OCR语言/能力/engine边界均为fake，实际内存codec仍使用WinRT；不查询真实用户内容/偏好。

## 验证记录与中间修正

完整原文保留，不以定向替代全量：

- development-build-1.txt：初版Platform实现编译成功。
- sdk-smoke-first.txt：首次真实SDK合成smoke成功。
- targeted-1.txt：新增测试nullable断言CS8604编译失败；修正非空断言，未改预期。
- targeted-2.txt、targeted-3.txt：语言/结果与随后生命周期检查实际通过。
- targeted-final.txt：smoke观测包装的测试类型被Iris.Platform.Windows命名空间遮蔽，CS0234/CS0535；改用顶部WinRT类型using。
- targeted-final-2.txt：同一测试包装方法CA1859，收窄为实际返回类型。
- targeted-delivery.txt：全部新增本地OCR定向通过，包括获准的真实SDKsmoke，跳过0。
- release-build.txt、release-full.txt：随后串行执行全解决方案Release构建/全量，原始汇总附工单。

全量中的两项跳过仍为既有隔离窗口站error183；新增Windows OCR语言包跳过为0。既有只读cursor/monitor/DPI native smoke保持。没有新增或修改旧测试Skip、延长超时、削弱断言，门闩均先entered，finally先放行再等实际任务并保留主/清理错误。

## 范围、未验证与交付

无工单功能范围偏差。未实现OCR Registry/Service/Normalizer/Handler/设置页，未建立第二套App退出接线；后续Registry在租约结束后调用本Provider释放能力。

没有读取用户图片/选区/剪贴板/键鼠/hook、用户配置/密钥/Run，没有HTTP/云OCR、真实Iris或性能脚本。实际已安装语言只在获准的合成smoke读取。真实多语言识别效果、用户桌面场景、实机吞吐与NFR **未验证**。

完整工单归档T-029.md，完成后删除current_task；CCR-029只标已落地待复审。架构师B结案/T-028B.review、新规范/契约、BACKLOG/CCR/交接全部保留随本单提交。.codex/是预存未跟踪工具，不改不删不提交；不push/reset/clean/rebase/amend。
