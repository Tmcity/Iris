# 百度 OCR Provider

2026-10-06 · T-028B · ARCH v1.44 · CCR-028。本规范替代ARCH旧§9.3中的未落地原型细节。T-028A/CCR-027已验收，继续完整实现百度四模式Provider，不提前实现Windows OCR、OCR注册表/编排、Handler或设置页。

## 1. 实例、配置与边界

在Iris.Providers实现BaiduOcrProvider、BaiduOcrProviderFactory及必要内部Token/解析组件，Factory注册为IOcrProviderFactory（Kind=BaiduCloud）。Create接收不可变Profile/ProviderCredentials快照，不访问ISecretStore或用户设置；BaiduOptions=null时使用既有默认值。Enabled由后续Registry筛选，不等同缺少凭据。

Factory的null参数/错误Kind按现有Factory惯例抛参数异常；空Id、非法Mode或TimeoutSeconds不在[5,120]时抛安全InvalidArgument。DisplayName空白可用Id。ApiKey/SecretKey任一空白，或BaseUrl不是有效HTTP(S)绝对URL、含userinfo/query/fragment，Descriptor.IsConfigured=false。未配置实例的RecognizeAsync抛NotConfigured、TestAsync返回失败，不能创建HTTP请求；Constraints仍应为合法稳定值。默认配置、Profile字段和JSON不变。

BaseUrl保留合法网关路径前缀，去掉末尾路径斜杠再追加固定Endpoint；不能让以`/`开头的Endpoint丢掉前缀。HTTP/HTTPS均支持，不自动重定向。凭据保留原字符串（非空白不代表可擅自Trim），每个查询值独立转义；不能把`+ & = ? # %`当查询语法。

构造不联网、不创建HttpClient或后台任务。每个调用先检查caller取消与实例准入，再捕获request/语言/图像元数据；调用方在任务结束前将Image.Bytes视为只读。大图Base64/表单构造、响应解析和HTTP均在后台执行，不以“async方法”代替真正离开UI线程。输入/结果不得被实例缓存，只有当前Token及必要安全元数据可缓存。

## 2. 四模式、表单与约束

| Mode | Endpoint | 长边上限 | 完整表单上限（字节） | auto映射 |
|---|---|---:|---:|---|
| GeneralBasic | /rest/2.0/ocr/v1/general_basic | 4096 | 8,000,000 | CHN_ENG |
| General | /rest/2.0/ocr/v1/general | 4096 | 8,000,000 | CHN_ENG |
| AccurateBasic | /rest/2.0/ocr/v1/accurate_basic | 8192 | 10,000,000 | auto_detect |
| Accurate | /rest/2.0/ocr/v1/accurate | 8192 | 10,000,000 | auto_detect |

上限来自官方8M/10M说明，采用十进制字节作保守解释，服务端实际边界未实测。高精度8192依据已读文档放宽原保守4096；不修改截图或预处理内部安全预算。

Constraints保持MaxEncodedBytes=4*1024*1024、MaxBytesAppliesToBase64=true、MinShortSide=15、AcceptedFormats=[Jpeg,Png,Bmp]（只读快照），并设置A已交付的MaxFormUrlEncodedBytes。其值为本模式完整表单上限减去本Profile所有非图像值开销的最坏实际编码长度：包括`image=`、分隔符、语言及其它字段。自动语言取下表可能值中开销最大者，显式LanguageType用其实际值。可用仅含空image值的小型FormUrlEncodedContent精确求开销，不为求Constraints编码整张图。若元数据本身耗尽预算，配置无效，不发HTTP。

POST表单固定发送image（一次Base64，无data:前缀，由FormUrlEncodedContent进行URL编码）、language_type、detect_direction、paragraph、probability=true。不发送image URL、PDF/OFD或新扩展选项。业务重试使用新HttpRequestMessage/Content，不重复编码原图为另一图像；不得重用已经Send过的消息对象。

Recognize发送前验证非空图、定义格式、正宽高及Constraints的尺寸/原字节或Base64预算；违反图像约束抛ImageTooLarge，空/非法输入ImageInvalid。不在Providers重新调用WinRT解码或依赖Platform；调用方负责预处理有效性。构造实际表单后，必须再次检查完整编码Content长度<=本模式上限，超限ImageTooLarge且不发送识别请求。不能用仅image值长度替代完整body检查。

## 3. 语言

显式BaiduOptions.LanguageType非空白时原样作为值发送，不改大小写或从它伪造DetectedLanguage。否则按LanguageHint映射（支持已有规范代码，比较不区分大小写）：

| Hint | language_type |
|---|---|
| zh-Hans / zh-Hant | CHN_ENG |
| en | ENG |
| ja | JAP |
| ko | KOR |
| fr | FRE |
| de | GER |
| es | SPA |
| pt | POR |
| it | ITA |
| ru | RUS |
| auto、空白、其它 | 按模式表auto映射 |

本单不额外发送detect_language，OcrResult.DetectedLanguage返回null，不把请求语言或方向字段当检测结果。官方高精度更多语种可由显式LanguageType使用，不自行增加设置枚举。

## 4. Token缓存、失效与并发

Token端点：POST `{BaseUrl}/oauth/2.0/token?grant_type=client_credentials&client_id=...&client_secret=...`。每实例固定凭据/端点，缓存不跨实例共享，不落盘。合法OAuth响应的非空error优先映射AuthenticationFailed；access_token须非空白、无控制符、<=16384字符，expires_in须正整数，否则InvalidResponse。不回显error_description、scope或token。

缓存用TimeProvider的单调经过时间。有效TTL=min(expires_in,30天)，提前刷新余量=min(1天,有效TTL/10)；正常30天仍提前1天，60秒TTL在54秒后不再复用。以实际Token请求开始时间计时；收到时已超过有效TTL则InvalidResponse，不发布过期Token。极大正expires_in先截到30天再转换时间，不能溢出。

Token获取使用可取消的异步互斥准入，进入后再次检查缓存。正常并发只进行一次获取，后续等待者复用已发布Token。一个等待者取消不取消获取者或其它等待者；获取者取消时完整结束其HTTP/资源并释放准入，其它仍有效的等待者可以重新获取，不被第一个caller取消。这里允许取消的旧获取之后另发一次，不要求维持无人负责的后台共享请求。不得让失败Task永久成为缓存。

每次发布产生独立身份的Token receipt/代次。识别使用拿到的receipt固定Token；110/111只能条件失效当前仍与本次receipt相同的缓存。迟到旧错误不能清除已刷新的新Token，即使服务端两次返回相同Token字符串，也不能只按字符串判断代次。刷新等待仍通过同一准入，正常并发旧错误只引发一次新获取。

## 5. 超时和重试

每个RecognizeAsync/TestAsync的Profile总预算覆盖Token等待/获取、识别、body读取及业务退避；不能每重试一次重置预算。用TimeProvider验证，caller取消、实例停止、Profile超时和传输超时要区分。超时通知取消后仍须等已发操作真正结束并清理；不以丢弃Task承诺硬性返回延迟。

使用既有HttpClientNames.Ocr及其命名管道：禁redirect、无限HttpClient.Timeout、既有传输最多2次retry、10秒attempt/30秒total及安全日志保持，不修改Llm或现有HTTP策略。每个逻辑SendAsync可能包含最多3次实际传输，必须如实区分。

仅HTTP 2xx的业务错误进入以下重试；HTTP非成功按HTTP映射处理，不在已处理的HTTP429上再套一轮业务18退避：

- 110/111：整个Recognize最多一次Token条件失效/重新获取/识别重试；仍失败AuthenticationFailed。
- 18：整个Recognize最多一次500ms可取消退避后识别重试。
- 两种预算独立且整个调用只各用一次，混合序列最多3次逻辑识别发送，最多9次底层传输尝试；Profile超时/取消/熔断可能更早停止。
- 其它业务码不内部重试。Token请求沿用HTTP传输管道，不对OAuth错误再循环。

TestAsync必须验证Token端点，不能仅凭调用开始前已有缓存报告连接成功。并发Test可复用其开始后新取得的Token：在调用入口捕获旧receipt，进入获取准入后仅复用不同于该旧receipt的有效新结果，否则执行一次Token请求。普通识别仍复用有效缓存。Test不发送识别、图片或其它付费请求；正常返回ProviderTestResult，失败携带安全ErrorInfo，caller/实例取消仍抛OperationCanceledException。Test成功Sample不含token，可为null。Elapsed/Latency以整个实际调用到资源收尾为准，不读取真实桌面/配置做测试。

## 6. 安全错误映射

| 业务码 | ErrorCode | Retryable |
|---|---|---|
| 110 / 111（预算耗尽） / 14 | AuthenticationFailed | false |
| 6 | PermissionDenied | false |
| 17 / 19 | QuotaExceeded | false |
| 18（预算耗尽） | RateLimited | true |
| 216200 / 216201 | ImageInvalid | false |
| 216202 / 216205 | ImageTooLarge | false |
| 216100 / 216101 | BadRequest | false |
| 216630 / 282000 | ProviderUnavailable | true |
| 其它非零业务码 | Unknown | false |

非2xx复用已有HttpErrorMapper.MapStatus，不导入原始错误文本；识别HTTP413特定映射ImageTooLarge。Token响应若能安全解析OAuth非空error，即使HTTP400也按AuthenticationFailed；否则非成功状态按HTTP映射，成功状态但协议错误为InvalidResponse。2xx error_code缺失/0走成功解析，非零先于words_result处理。

网络错误、已知Polly超时/熔断按现有安全分类映射；未知底层自行取消不能冒充caller取消。真实主错误与取消/清理错误并存时保留全部安全阶段/数值，不用取消覆盖真实失败。存在清理失败时可用Unknown承载组合诊断，不伪称可安全继续。

Message只来自ErrorMessages；Detail只含固定stage、数值httpStatus/businessCode/HResult等可信元数据，ProviderId可保留。不得透传error_msg/error_description/任意外部Message/Detail/Data/Inner、原始JSON、图像或凭据。未知业务码也不附error_msg，覆盖旧ARCH原型规定。保持既有安全HTTP日志，不输出请求查询/头/body。

CCR-028批准BaiduTokenResponse与BaiduOcrResponse增加安全ToString覆盖：不显示token、scope、错误文本或识别正文，只显示布尔存在性/数值摘要。Wire JSON属性和构造不变；新增BaiduErrorCodes.RequestBodyTooLarge=216205。

## 7. 有界响应与结果

Token响应最多256KiB，OCR响应最多16MiB（均指解压后的读取字节），JSON最大深度32。Content-Length超过上限可先拒绝，实际流读取仍受上限约束；大小/JSON/字段类型违背协议为InvalidResponse，不分配无界内存。沿用现有wire大小写兼容，但重复关键字段忽略大小写视为重复并拒绝；未知字段可忽略。

成功OCR必须有words_result数组；空数组合法返回空Lines，不抛OcrNoTextFound。最多20000个words、总Text最多1000000个UTF-16码元；每个words必须为字符串（空白保留，不提前规范化），缺失/null/错误类型为InvalidResponse。words_result_num等计数若提供须与实际对应数组长度一致。

General/Accurate的location存在时映射PixelRect；须为完整整数left/top/width/height，left/top>=0、width/height>0，加法使用long且不超过int范围。缺失location为null。Basic模式不使用位置扩展。忠实保留服务给出的坐标，不凭direction自动旋转/裁剪；真实方向/坐标兼容性未实测。probability.average存在时须为有限[0,1]数值并映射Confidence，缺失为null，非法值InvalidResponse。

Paragraph=false，或paragraphs_result缺失/空时保持原行顺序。Paragraph=true且有段落数组时，每组words_result_idx须为非空合法索引；组内/组间重复或越界为InvalidResponse。成员按原words索引升序，组在最小成员索引处输出，未被引用行仍在原相对位置输出，不能遗漏正文。

组内使用已验收Core.Text.SoftLineBreakMerger.Merge处理CJK/连字符/标点/列表的软换行，不能复制另一套规则；单个OcrLine.Text允许保留规则要求的内部换行。组Bounds仅在全部成员有Bounds时取安全并集，否则null；组Confidence仅在全部成员有值时取算术平均，否则null。单成员保留原值。不做T-030的最终全局排版、空文本判定或回退。

为复用该internal helper，允许Core项目只新增InternalsVisibleTo("Iris.Providers")，不新增Core→Providers引用、不修改helper行为、不扩大为公开API。

## 8. 所有权与退出

每个请求持有独立message/content/response/body流及本次HttpClient包装器，按实际所有权处理，不能Dispose工厂共享handler。错误/取消路径也完整等待读取/发送结束再释放；每次业务重试前结束上一次响应。Token缓存只有数据，不常驻一个被并发Dispose的client。

具体Provider实现可验证的IDisposable/IAsyncDisposable。同步Dispose只允许已无在途调用/清理时释放并幂等；在途时须明确拒绝同步释放，不能阻塞UI等待或提前释放资源。异步Dispose停止新准入、通知取消、完整等待所有已接纳调用/Token准入等待者及资源清理；同在途屏障共享，失败可同实例重试。后续Registry在租约结束后调用同步Dispose，T-030再接线，本单不改Registry原型。

自有响应/流等释放失败必须保留原责任供重试，并报告主/清理阶段；不得简单置null或重发HTTP代替清理。后续新调用先处理已有失败责任，失败时不发新HTTP。已有独立并发调用仍须收完。Token刷新不销毁其它请求正使用的receipt；取消一个调用不取消其它调用。实际响应体/图片/结果不进入长期缓存，已完成Task不由实例强持有。

## 9. 验证与交付边界

实际Factory/Provider + MockHttpMessageHandler、fake TimeProvider；检查四路径/全部表单字段、特殊字符凭据转义、网关前缀、各模式Constraints/元数据开销、真实body长度及发送前拒绝。至少一条真实命名iris.ocr管道+替身primary handler的组合验证（含HTTP与业务混合重试），不只mock掉HttpClientFactory后宣称次数受限。测试可将传输退避配置为0以验证次数，生产策略保持；业务500ms另用fake时间验证。

正常并发Token、过期/短TTL/非法响应、同字符串新receipt、并发旧110/111、等待者与获取者取消、失败后可再获取；业务表每行、HTTP/413/超时/熔断、两种重试预算混合且不重置；有效/畸形/过大JSON、Bounds/Confidence/段落/遗漏行/空结果；TestAsync无识别；无凭据零HTTP；日志/异常/ToString不含构造的凭据、token、错误正文标记。

使用entered/release控制发送、body读取、Token等待和清理，验证caller/Stop/超时及主+清理错误；finally先放行再await真实任务/资源，保留原错误。并发、异步Dispose、同步拒绝提前销毁、同资源清理重试和存活实例不缓存图像/结果必须有证据。可复用现有HTTP/假时间支撑，但不削弱已验收测试。

新增测试禁止真实OCR/Token/HTTP联网、用户凭据/配置/Run、用户截图/选区/剪贴板/输入/hook、性能脚本或真实Iris。原只读native smoke与error183跳过保留。串行Release build/full，真实服务端上传/语言/坐标兼容性与NFR明确未验证。

官方已读：[标准](https://ai.baidu.com/ai-doc/OCR/zk3h7xz52)、[标准位置](https://ai.baidu.com/ai-doc/OCR/vk3h7y58v)、[高精度](https://ai.baidu.com/ai-doc/OCR/1k3h7y3db)、[高精度位置](https://ai.baidu.com/ai-doc/OCR/tk3h7y2aq)、[Token](https://ai.baidu.com/ai-doc/REFERENCE/Ck3dwjhhu)、[错误码](https://ai.baidu.com/ai-doc/OCR/dk3h7y5vr)。
