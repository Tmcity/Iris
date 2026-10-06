# OCR 图像预处理

2026-10-06 · T-027 · ARCH v1.42 · CCR-026。实现既有 `IImagePreprocessor`，接口/record/枚举签名不变。本规范补齐尚未实现的预处理原型行为；不修改已验收的截图、剪贴板或 OCR Provider 范围。

## 1. 端口、准入与输入

Platform.Windows 实现 `WinRtImagePreprocessor`，App 组合根将具体实例和 `IImagePreprocessor` 绑定为同一单实例；构造不启动线程/WinRT操作、不读配置或桌面。实际转换在后台执行，不能在调用方UI线程同步解码、像素循环或编码。没有 OCR 调用入口时也应交付可用端口和退出接线，不创建占位 Provider/Handler。

调用前已经取消，不接纳资源或后台工作。每次调用同步捕获 ImageData 元数据和 AcceptedFormats 的有序快照，再进入异步工作；调用方须在任务结束前将输入 Bytes 视为只读。实现不修改输入 Bytes，也不在成功后缓存输入/输出。已满足约束返回原 ImageData 实例，不能为了统一流程构造相等的新实例。

约束合法性：MaxEncodedBytes>0，MaxLongSide>=1，0<=MinShortSide<=MaxLongSide；AcceptedFormats 非null/非空，每项为定义的 Png/Jpeg/Bmp，顺序保留，重复项可去重且保留首次。MinShortSide=0表示没有最短边要求。不合法抛安全 `InvalidArgument`。图像null、空Bytes、非法格式或非正宽高抛 `ImageInvalid`。

先由真实 decoder 检查实际 codec 和原始像素宽高与 ImageData 一致，再请求像素；不一致、截断/损坏无法解码抛 `ImageInvalid`。快路径也需完成实际解码有效性检查并释放自有资源，成功返回原实例；“同实例”不表示跳过输入有效性检查。EXIF 方向明确忽略，按编码像素矩阵处理，不额外旋转；不读取文件路径、桌面或剪贴板。

内部单图安全预算与现有截图底座一致：最大单边32768、最多67,108,864像素、BGRA32最多268,435,456字节、单份已编码数据最多285,212,672字节。检查输入元数据、真实头部、转换内容和补白画布；任何分配前使用checked long验证，超预算抛ImageTooLarge，不依赖OOM、不修改原T-025常量/行为。该预算与Provider约束分别验证，不将较大的MaxEncodedBytes当作无限内存许可。快路径同样受内部预算约束。

## 2. 等比缩放与补白

设原长边L、短边S。期望比例先为1；S<MinShortSide时改为MinShortSide/S；最终比例取期望值与MaxLongSide/L的较小者。两个维度用同一个比例，精确整数/有界有理运算，各自向下取整且至少1，不能分别拉伸图像。由此长边不超过上限；最短边仍不足时以白画布补齐。

最终画布宽高分别为max(内容宽高, MinShortSide)。内容居中，左/上补边为floor(差值/2)，奇数余量放右/下。不裁剪原内容、不把放大后超出的长边截掉，也不因字节超限继续偷偷缩小。缩放使用WinRT `BitmapInterpolationMode.Fant`，实际像素验证需允许插值而不能把放大镜的NearestNeighbor规则搬过来。

| 输入 | MaxLongSide / MinShortSide | 内容 | 输出画布与补边 |
|---|---|---|---|
| 20×10 | 100 / 40 | 80×40 | 80×40 |
| 1000×500 | 100 / 40 | 100×50 | 100×50 |
| 40×10 | 100 / 40 | 100×25 | 100×40，上7/下8 |
| 10×40 | 100 / 40 | 25×100 | 40×100，左7/右8 |
| 5×5 | 100 / 0 | 5×5 | 5×5，无强制放大 |

变换输出显式BGRA8/sRGB，透明图正确合成白底再以alpha255输出；白边也是不透明白色。Straight数据单像素公式为每通道 `(c*a + 255*(255-a) + 127)/255`；若通过WinRT在Premultiplied模式缩放，则按预乘通道加`255-a`并夹紧，不能重复乘alpha。插值时不能让完全透明像素的无意义RGB污染可见颜色；允许先合成白底再缩放，或用正确预乘模式插值后合成。原样返回的快路径保留原字节/alpha/元数据，不强制重编码；需要转换时从有效输入像素产生新图。

## 3. 格式与精确字节预算

尺寸无需改变、原格式被接受且原字节预算满足时，在输入验证/资源清理后返回同一实例。否则保留被接受的原格式；原格式不被接受时选择AcceptedFormats[0]。有必要转换的首轮JPEG质量0.90。

若尺寸/格式本来符合但原字节已超限，不重复编码同格式来碰运气，进入JPEG降质路径。若经过必要尺寸/格式转换后的首轮编码超限，也进入同一路径。只有AcceptedFormats包含Jpeg时才允许JPEG输出；依次以0.90、0.80、0.70、0.60、0.50尝试，当前输出已经是本次像素以0.90编码的结果时从0.80继续。每轮从同一份转换/白底像素编码，不从前次JPEG再解码，不累计有损损失。

JPEG不被接受而既定首轮仍超限，或质量0.50仍超限，抛 `ImageTooLarge`；不返回Provider不能接受的格式，不增加任意缩图/裁剪/无限尝试。输出必须同时满足尺寸、格式和真实字节预算。

预算比较用实际编码Bytes.Length。MaxBytesAppliesToBase64=false时比较n；true时精确比较`4*((n+2L)/3)`（包含padding）。等于预算允许，超过一字节拒绝；不为检查预算分配Base64字符串，不把约4/3或JPEG预估大小当作实际结果。读取输出前检查真实流长度和内部编码预算，完整读取并核对实际长度，不返回短读/尾部垃圾。

每次编码尝试创建独立新encoder和空输出流，Flush完成后才读取并关闭，读取前Seek(0)。不得在已经Flush的encoder上再次SetPixels/Flush来降质。当前尝试的自有资源全部释放后才进入下一尝试；失败清理不允许重跑编码代替真实清理。

## 4. 取消、失败和完整退出

每个请求的decoder、encoder、stream、DataReader/Writer、pixel buffer及异步操作归该请求所有；异步等待可被调用取消通知，但不能在底层WinRT操作尚在使用时提前Dispose流或返回已清理。每个外部调用前后及像素处理批次检查取消，取消后不开始下一尝试，等待已发出操作真正结束后清理。未知底层自行取消而caller/本实例未取消，按处理失败报告，不能冒充用户取消。

用户或实例停止取消且清理成功时抛OperationCanceledException；若同时存在真实主错误/清理错误，应保留全部安全阶段/数值并报告失败，不能被取消覆盖。解码无效输入为ImageInvalid；预算失败为ImageTooLarge；其它编码/基础设施/清理故障为Unknown，Message来自ErrorMessages。不传播任意外部Message/Detail/Data/Inner，不输出图像字节、内容或文件路径。受控内部聚合保留每个独立失败，不只保留一个通用HResult。

可在实例内部串行处理请求，等待者亦属于已接纳工作；不得以未授权的Busy错误替代正常并发PrepareAsync能力。每个已接纳任务直到结果结算和资源责任归还都纳入退出屏障，取消一个调用不能取消其它调用。不要共享可变codec或把所有请求指向同一_lastImage。

具体实现提供App内部可调用的停止准入、完整StopAsync和必要RetryCleanupAsync能力，不修改公开IImagePreprocessor签名。Stop先拒绝新调用并取消/等待全部在途及排队调用，成功幂等、同在途Task共享；失败保留未释放的原资源供同实例真实重试。失败资源未收完不接纳新转换操作，可先自动重试清理后处理下一请求。重试不得隐藏/重做已经完成的转换，不静默丢弃清理异常。

AppBootstrapper在统一Stop准入阶段关闭预处理准入，在Host/Dispatcher销毁前完整等待预处理；与截图/选择等独立清理均应尽力执行并聚合安全错误。未清理完成不得伪称退出成功或销毁仍在使用的服务。保留现有B→A→capturer等依赖顺序，本单不改变原截图实现。构造/未用退出不创建codec、不新开专用线程；没有理由不使用现有线程池和WinRT异步能力。

## 5. 验收和边界

主体为actual WinRtImagePreprocessor和实际WinRT内存codec。fake仅替换外部工厂/资源/异步门闩或原生故障，不复制预处理状态机。真实PNG/JPEG/BMP合成图验证：同实例、格式顺序、缩放/极端长宽比/奇数白边、实际尺寸/codec、透明白底、最短边0与整数边界。损坏/截断、codec/尺寸失配、预算溢出在大分配前拒绝。

精确字节和Base64的模3/等于/超过边界、每个JPEG质量、JPEG不接受、0.50仍超限、每轮原像素不累积损失。外部边界可控制编码大小与记录质量以验证选择规则；至少另有真实codec输出解码/像素组合检查，不能全靠fake结果长度。

在写入、解码、像素、创建encoder、Flush、读出、资源处置等关键边界用entered/release控制取消/停止/故障；finally先放行再await实际任务/线程/流，主+清理错误均保留。覆盖并发、排队取消、同对象清理重试、App实际组合根/退出、未使用零资源、调用方放弃结果后同一存活实例不持有已完成输入/输出。不以固定Delay或仅fake调用次数证明生命周期。

串行Release构建/全量。新增自动化禁止真实用户截图/选区/剪贴板/键鼠/系统hook、用户配置/密钥/Run、HTTP、真实Iris主进程和性能脚本；只允许合成内存图与自有测试环境。全量保留既有只读native smoke与两项error183跳过。大图实机吞吐、OCR识别效果、真实混合DPI/HDR与NFR不在本单宣称已验证。

## 6. 官方依据

已读取并留存原文：

- [GetPixelDataAsync](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmapdecoder.getpixeldataasync)：显式格式、alpha、EXIF、sRGB参数。
- [BitmapTransform](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmaptransform)：Scale→Flip→Rotation→Crop及坐标空间。
- [BitmapInterpolationMode](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmapinterpolationmode)：插值模式。
- [CreateAsync](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmapencoder.createasync)：空输出流及编码属性。
- [SetPixelData](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmapencoder.setpixeldata)：sRGB像素，Flush前不提交。
- [FlushAsync](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.bitmapencoder.flushasync)：完成后不能继续调用encoder，重用输出流先Seek(0)。
