# Windows 本地 OCR

2026-10-06 · T-029 · ARCH v1.45 · CCR-029。实现Platform.Windows中的WindowsOcrProvider/Factory，使用Windows.Media.Ocr；不实现T-030注册表/编排、T-031入口或T-032设置页。

## 1. 配置、能力和注册

Factory实现IOcrProviderFactory（Kind=WindowsLocal），注册在Platform的现有DI扩展，与百度Factory并存。Profile/WindowsOptions固定为每实例快照；不使用或保存ProviderCredentials中的密钥，不读取SecretStore。null参数/错误Kind沿用Factory惯例；空Id或TimeoutSeconds不在[5,120]为InvalidArgument，DisplayName空白可用Id。WindowsOptions=null用既有默认值，Enabled仍由后续Registry筛选。

构造不创建OCR engine、不解码、不打开窗口或启动线程。为同步Constraints，允许Factory在创建实例时通过内部可替换native边界读取一次只读MaxImageDimension；这是本单明确允许的能力查询。有效MaxLongSide=min(SDK能力,32768)，能力必须>=40；获取失败/非法能力抛安全ProviderUnavailable，不构造假能力占位实例。不能以UI同步等待异步初始化取得该值。

Constraints：MaxEncodedBytes=285212672、MaxBytesAppliesToBase64=false、MaxLongSide如上、MinShortSide=40、AcceptedFormats=[Png,Bmp]只读快照、MaxFormUrlEncodedBytes=null。既有全局像素/原始字节预算仍约束预处理；不改变T-027/028A实现或预算。

本地Provider不需Key，能力查询成功且配置合法即IsConfigured=true；不把“配置完整”等同“请求语言包已安装”。无匹配语言包时由Test/Recognize明确报告OcrLanguageUnsupported。

## 2. 语言选择

每次调用捕获语言选择输入，按以下顺序尝试，去除相同候选但不改全局系统设置：

1. 非空白WindowsOptions.LanguageTag。
2. Recognize的LanguageHint映射；TestAsync没有Hint，直接进入下一步。
3. SDK TryCreateFromUserProfileLanguages。

Hint映射：zh-Hans→zh-Hans-CN，zh-Hant→zh-Hant-TW，en→en-US，ja→ja-JP，ko→ko-KR；其它非auto的合法BCP-47标签可直接尝试；空白/auto跳过。候选标签不合法或TryCreateFromLanguage返回null时继续下一候选；其它原生错误不静默当语言缺失。全部候选无可用engine才为OcrLanguageUnsupported，提示沿用ErrorMessages。

SDK可能将候选解析到实际已安装语言；使用返回engine，不假定候选字符串必须与RecognizerLanguage完全相等。每请求独立选择/持有engine，允许先实现每请求创建且收尾后清除引用，不建立无限语言缓存。所有识别请求在单实例内串行，避免对engine并发调用；排队caller取消不影响当前识别，不创建新engine。

OcrEngine不提供IDisposable/IClosable，不使用Marshal.ReleaseComObject/FinalReleaseComObject强行释放投影；实际操作结束后解除本实例引用，由投影管理native引用。选中的RecognizerLanguage是配置结果，不是语言检测，OcrResult.DetectedLanguage返回null。

## 3. 实际图像与识别

IOcrProvider调用方负责预处理；本Provider仍校验非空、定义的Png/Bmp格式、正尺寸、SDK和既有内部预算，实际decoder的codec/宽高须与ImageData一致。损坏/失配ImageInvalid，超预算ImageTooLarge。输入Bytes调用期间只读，不读取用户文件/桌面/剪贴板作为输入。

在后台创建自有InMemoryRandomAccessStream/DataWriter/BitmapDecoder/SoftwareBitmap，明确BGRA8并遵守已验收的图像矩阵规则（忽略EXIF方向，不自行旋转）。允许复用稳定只读图像能力或新增专用内部适配，不能为复用改写原预处理/截图/剪贴板。SoftwareBitmap由请求拥有，必须在实际RecognizeAsync完全结束后才Dispose。

真正调用engine.RecognizeAsync(SoftwareBitmap)，完整等待WinRT操作终结。调用取消/超时后不开始后续阶段，不用Task.WaitAsync抢先返回并处置仍在使用的位图。可通知native取消，但必须观察实际完成；不能把调用方放弃等待当作native结束。

## 4. 行、位置与单空格修正

保持SDK的行顺序，使用OcrLine.Text作为本行文字；仅移除原文本中两个CJK rune之间恰好一个U+0020空格。CJK定义复用Core.UnicodeScripts.IsCjkForLineMerge：汉字（含补充平面）、假名及原项目全角/标点范围，韩文不在此特定合并定义内。必须按Unicode rune处理，不拆代理对，不去除双空格/tab/NBSP、首尾空格或中西文之间的空格。

| 输入 | 输出 |
|---|---|
| 你 好 世 界 | 你好世界 |
| 𠀀 𠀁 | 𠀀𠀁 |
| あ い | あい |
| 한 글 | 한 글 |
| 你  好 / 你\t好 / 你\u00A0好 | 原样 |
| A 中 / 中 A | 原样 |

为复用分类，允许Core.csproj仅新增InternalsVisibleTo("Iris.Platform.Windows")，不改分类/helper、不扩大公开API或引入反向引用。不要在本单做T-030的软换行合并、段落排版或OcrNoTextFound判定。

每行Bounds取其SDK词BoundingRect的并集：验证各double有限、宽高>0、坐标非负，左/上floor、右/下ceil，再用安全long/int范围生成PixelRect；无词时Bounds=null。保留输入图像像素坐标，不乘DPI、不按TextAngle自行旋转。无真实置信度来源，Confidence=null。异常native输出（null文字、非法坐标/数量等）为安全InvalidResponse，不静默制造有效结果。

返回空Lines是合法无文字成功。最多20000行、100000词、总文字1000000个UTF-16码元，按批次检查取消；超出结果预算InvalidResponse。Elapsed涵盖实际调用/排队/收尾。不得缓存已完成图像、结果、Task或OCR正文。

## 5. 取消、错误与退出

每调用从准入起计算Profile总预算[5,120]秒，包括排队、语言选择、解码、识别和收尾；后台启动时扣除已耗时，不重置预算。caller取消/实例停止成功收尾后抛OCE，超时为Timeout，未知底层自行取消为Unknown；有真实主/清理错误时不得被取消覆盖。

所有位图、流、writer及异步操作有明确所有权；请求Task和实例在途屏障直到真正收尾才结束。主/独立清理/取消错误保留固定阶段/HResult，Message来自ErrorMessages，不回显OCR正文、原始异常Message/Data/Inner。能力不可用ProviderUnavailable；无可用语言OcrLanguageUnsupported；输入/结果错误按前文，其它执行/清理错误Unknown。

具体Provider实现IDisposable/IAsyncDisposable：同步只允许无在途/待清理时释放，否则明确拒绝，不阻塞UI；异步停止准入、取消/等待所有在途及排队、释放位图/流并解除engine引用。失败保留原资源供同实例重试，不重做OCR代替清理；后续调用先收旧责任，失败时不发新native识别。成功幂等、同在途屏障共享。后续T-030再接Registry退休租约，本单不加第二套App生命周期。

## 6. TestAsync与验证边界

TestAsync只检查SDK能力与按配置/用户语言选择是否能取得engine，随后解除引用，不调用RecognizeAsync、不创建测试图像或读取用户内容。成功Sample可为空，失败ErrorInfo为安全分类；caller/Stop取消仍抛OCE。

主要自动化使用实际Provider/Factory与真实内存codec，fake仅替换Windows OCR能力/语言/引擎结果和故障边界。验证语言优先级/null/非法tag/全部失败、配置不需密钥、真实Png/Bmp解码、rune/空格/Bounds、空结果、总预算/排队取消、识别在途位图不释放、主+清理错误、原资源重试、同步/异步Dispose、GC及实际DI注册。门闩先entered，finally先放行后await实际任务。

本单额外明确允许一个**真实SDK合成图片兼容性smoke**：只查询OCR能力/已安装语言，用自有固定白底大字PNG（如HELLO 123）创建SoftwareBitmap并真实RecognizeAsync；不抓屏、不读取用户文档或剪贴板、不发网络、不打开Iris。记录所用语言、能力数值和原始合成识别结果。已安装合适语言时应执行，不能以fake替代；没有测试所需语言包可按明确环境原因跳过，不自动安装/切换语言。

若SDK激活/兼容性失败，保留实际HRESULT/环境证据并报告，不能擅自改包身份/安装方式或把失败写成通过。官方当前已读页面未声明包身份限制，这不代替实际运行证据。识别效果不宜由单张合成图推广到真实桌面/所有语言，实机吞吐/NFR仍未验证。

除上述明确smoke，新增测试不查询真实用户内容/偏好；主要语言边界用fake。保留既有cursor/monitor/DPI只读smoke及两项error183跳过。禁止用户配置/密钥/Run、真实HTTP/云OCR、用户截图/选区/键鼠/hook、性能脚本和真实Iris主程序。串行Release构建全量，如实区分新增环境跳过。

## 7. 官方依据

已读取：[OcrEngine](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine)、[TryCreateFromLanguage](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine.trycreatefromlanguage)、[RecognizeAsync](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine.recognizeasync)、[MaxImageDimension](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine.maximagedimension)、[AvailableRecognizerLanguages](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine.availablerecognizerlanguages)。原文确认语言包需安装、不可匹配返回null，类为Agile/Both；单实例串行约束仍按本项目规则保留。
