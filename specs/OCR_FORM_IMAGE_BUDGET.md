# OCR 表单图像字节预算

2026-10-06 · T-028A · ARCH v1.43 · CCR-027。为后续百度OCR Provider补充请求图像的精确预算，不改变已验收T-027的默认行为，不提前发送任何OCR请求。

## 1. 依据与目标

已读取百度官方标准版/标准含位置版、高精度版/高精度含位置版以及鉴权、错误码文档。图像参数说明要求先Base64再URL编码；标准版页面写上传编码大小不超过8M，高精度写10M。接口还可能以216205报告请求体过大。此处只实现表达/执行这个预算的基础能力，各Provider的模式限制与整份表单开销在T-028B另行定稿。

实际纯合成反例：1024×768不透明白色24位BMP，原始2,359,350字节，Base64为3,145,800字节，满足旧4MiB Base64约束；带image/detect_direction/paragraph/language_type的ASCII表单为9,437,321字节，已经超过8MiB。没有HTTP请求或用户图片。原T-027按其约束返回原图是正确行为，不能把此新增Provider约束称为T-027缺陷。

目标是保留现有尺寸、格式、Base64预算，额外精确表达表单图像值预算；只有实际违反该预算时触发原JPEG降质规则，不统一降低所有图片的既有Base64上限。

## 2. 唯一公开契约增量

在现有 `ImageConstraints` 位置record主体中增加：

```csharp
public long? MaxFormUrlEncodedBytes { get; init; }
```

默认null表示不施加此额外预算，保留既有构造函数和Deconstruct的5个参数，不修改IImagePreprocessor方法。正数表示图像字节先Base64、再作为application/x-www-form-urlencoded值编码后的最大ASCII字节数。

预算**仅包含图像值**，不包含`image=`、`&`、其它字段或URL查询。调用方负责扣除其它表单开销后设置预算，不能把它当整份HTTP请求上限。与原MaxEncodedBytes、MaxBytesAppliesToBase64的比较同时成立；不是替代原约束。不影响配置文件schema/default或任何Provider Profile字段。

null继续旧路径；0/负数为InvalidArgument，在codec创建前拒绝。约束快照必须保留该值，调用期间不受外部格式列表改变影响。已满足所有约束且实际图像有效时仍返回原实例。

## 3. 精确长度与取消

输入为实际编码的byte[]。Base64字母表中只有`+`、`/`和尾部`=`需要百分号转义，分别成为`%2B`、`%2F`、`%3D`；它们每个占3个ASCII字节，其余Base64字符占1字节。因此最终长度为Base64长度加上这三类字符出现数的两倍。padding必须计入，空数组的长度算法可返回0，但公开预处理仍按既有规则拒绝空图。

直接扫描输入字节的6位组统计，不分配Base64字符串、URL编码字符串或整份表单来检查预算。使用checked long或等价安全算术，不依赖近似4/3或3倍最坏估算；不能仅凭原始字节长度判断是否超过表单预算。已有内部单图预算仍保持。

统计在预处理后台执行，分批检查caller/Stop取消，不在UI同步进行大数组扫描。预算null时不进行此额外扫描。不能为了计数再读一次用户文件或复制整图，输入Bytes仍按既有调用期间只读规则。

原图快路径与每次实际编码完成后都校验全部预算。原图只有新增表单预算不满足时进入已验收的JPEG路径：仅JPEG被接受时90/80/70/60/50、同一准备像素、每轮新encoder/空流、已试90不重复；原字节/Base64或新增表单任一预算仍不满足都继续或最终ImageTooLarge。JPEG不允许时不得输出JPEG，不私自缩图/裁切。

计数期间取消仍使用原完整收尾；已编码操作完成后的字节由本请求所有，不能因统计取消而丢失资源责任。主/清理错误、并发隔离、Stop/Retry和GC保持T-027行为。

## 4. 验收

1. 精确长度对照独立的.NET FormUrlEncodedContent或URI编码实现，测试中的字符串只作小样本预期，不把同一计数helper当预期。覆盖输入长度模3、0/1/2/3字节、无转义、`+`/`/`/padding密集和混合、等于/少一上限，以及long大预算。
2. 实际预处理+真实PNG/JPEG/BMP至少覆盖上述白BMP：默认null时原图满足旧约束并返回同一实例；附加表单预算后才转换/降质，最终实际字节的表单值长度满足限制。真实编码输出需解码检查尺寸/格式/内容，不只看fake大小。
3. 相同原始编码长度但表单长度不同的受控输出，证明质量决策依据真实字节而非长度近似；只有新增预算失败时不重复首轮质量，不接受JPEG/全部质量仍失败时ImageTooLarge。原有raw/Base64限制同时有效。
4. 无效新增预算在codec之前拒绝；默认null的T-027全部检查保留；统计循环取消/Stop在可证明的边界验证，不依赖Delay猜测，finally先放行再await真实任务。资源/失败重试/并发/退出不回退。
5. 串行Release构建全量；保留T-027独立40项对照及本单新增检查。禁止真实OCR/Token HTTP、用户截图/剪贴板/输入/配置/密钥/Run、性能脚本和真实Iris。新测试只用合成内存数据与fake外部边界。

## 5. 范围与来源

本单仅Core/spec契约增量、Platform预处理必要预算实现和测试；不改App UI/退出架构，不实现Provider/Token/重试管道/注册表/Handler。T-028B保留完整百度四模式、凭据、Token、结果/错误映射与请求生命周期范围。

官方已读来源：[标准版](https://ai.baidu.com/ai-doc/OCR/zk3h7xz52)、[标准含位置](https://ai.baidu.com/ai-doc/OCR/vk3h7y58v)、[高精度版](https://ai.baidu.com/ai-doc/OCR/1k3h7y3db)、[高精度含位置](https://ai.baidu.com/ai-doc/OCR/tk3h7y2aq)、[错误码](https://ai.baidu.com/ai-doc/OCR/dk3h7y5vr)。公开文档读取不代表真实API兼容性测试；8M/10M的服务端实际边界尚未实测，Provider最终常量/完整表单检查由B签发。
