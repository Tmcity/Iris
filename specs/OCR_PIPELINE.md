# OCR编排、回退与文本规范化

> 2026-10-06 · T-030B · ARCH v1.47 · CCR-031 · 已定稿。
> 本文为ARCH §8的精确实现规则；生命周期声明以CONTRACTS.cs为准。Registry使用已验收OCR_PROVIDER_REGISTRY.md，不回到裸实例原型。

## 1. 同次操作与服务选择

Core实现OcrService和OcrTextNormalizer，构造只保存依赖，不读取图像、设置、密钥或创建Provider。服务线程安全；通过Provider端口执行识别，不直接访问网络、不写设置、不复制剪贴板、不显示窗口。

RecognizeAsync在接纳点登记操作，随后取得唯一IOcrProviderSnapshot。整个操作的Active/Fallback/LanguageHint/TextLayout和实例都取自该快照，不另注入ISettingsService/SecretStore读取最新值；旧租约在重配后继续工作。providerId非null就查该ID（Ordinal），否则查快照ActiveProviderId；指定未知ID不先偷偷尝试Active。列表已由Registry过滤禁用项。

主项缺失或Descriptor.IsConfigured=false：若存在不同的备用注册项则尝试备用，否则抛NotConfigured。备用按同一快照FallbackProviderId查询；同Provider引用、同InstanceId或同Profile.Id都视为相同，不允许重复尝试。备用未配置抛NotConfigured，不能再回退。缺失或未配置路径不调用预处理/识别。

## 2. 尝试、回退与结果

每次尝试均调用 `PrepareAsync(originalImage, provider.Constraints, token)`，随后 `RecognizeAsync(new OcrRequest(prepared, snapshot.Settings.Ocr.LanguageHint), token)`。备用必须再次从原ImageData出发，不能对主服务已经缩放/有损编码的图再加工。调用期间原图Bytes由调用者保证只读；服务不更改原图或Provider返回集合。

一次尝试的约束读取、预处理及识别共同受下列精确错误分类约束（本条澄清旧伪代码将Prepare写在try外的歧义）：只对IrisException.Code为NotConfigured、NetworkUnavailable、Timeout、ProviderUnavailable、QuotaExceeded尝试一次备用。不会捕获所有Exception回退。AuthenticationFailed、PermissionDenied、InvalidResponse、OcrLanguageUnsupported、ImageInvalid/ImageTooLarge、OcrNoTextFound、OperationCanceledException及其它错误均不触发回退。最多主/备两次，Provider内部重试保持既有协议，Service不再增加同Provider重试。

每次进入下一个阶段、备用尝试与最终返回前复核操作取消。取消不走备用、不用已完成成功结果覆盖取消；但必须先完成第4节真实收尾。主异常不因备用成功而泄漏到返回值/日志；备用失败保留其错误分类及ProviderId，不伪造主服务错误。没有备用时原IrisException按既有安全契约传播。不得记录原生任意Exception.Message/整条异常链、图像、识别文本或凭据。

识别成功后，对最终raw.Lines按快照TextLayout调用Normalizer（在回退catch之外）。文本IsNullOrWhiteSpace时抛IrisException(OcrNoTextFound)，不因为没字再试备用。否则返回OcrOutcome(raw, text, usedFallback)：Raw为实际最终结果，不能改写Elapsed、DetectedLanguage、Bounds或ProviderId；UsedFallback只在实际改用备用时为true。

输入image为null时ArgumentNullException；合法非null对象的格式/字节/几何有效性由已有预处理器完成，Service不复制解码算法。保留现有Provider超时，不增设未经要求的全局超时或固定并发上限。Service不缓存图像/结果。

## 3. 文本规范化

Normalizer为纯函数，保留输入顺序。lines为null时ArgumentNullException；null行或null Text为安全ArgumentException（不能将原文放消息）；未定义layout为ArgumentOutOfRangeException。空列表返回空字符串。

KeepLines使用LF连接原Text，保留每个Text内部现有换行和边缘空白，不额外Trim、清洗零宽字符、重新排序或拆分百度已合并段落。MergeParagraphs将原Text序列交给既有Core.Text.SoftLineBreakMerger.Merge，提供几何分段回调；不要复制或修改helper的CJK、韩文、连字符、列表、标点及空行规则。已在Text内部保留的换行保持原样，不拆开后再次“优化”。服务最终判定空白不属于Normalizer自身错误。

几何规则：有效Bounds为X/Y非负且Width/Height为正。其它Bounds按无位置处理，不抛出含文本的诊断。平均行高为本次全部有效Bounds的Height算术均值；累加用long或double避免int溢出。相邻两项均有效时，gap=`(long)next.Y - ((long)current.Y + current.Height)`；仅gap严格大于平均行高×1.5时强制保留换行，等于阈值不强制。重叠/负gap不强制；一侧缺失/无效或无有效平均值时仅用文本规则。不要使用平均行距或top-to-top距离代替本公式。

百度段落组的Bounds可能为多物理行并集，现有模型没有原段落/字距元数据；按现有OcrLine计算，不猜测、重拆或修改已验收Provider。KeepLines也不承诺恢复服务端分组前行数。

## 4. 操作、取消与失败清理

服务拥有每个已接纳操作及其停止取消信号，链接caller token。不得用WaitAsync取消包装后遗弃Prepare/Recognize；即使外部边界暂不响应取消，也要等待其实际Task退出，然后释放快照。取消回调异常不能跳过其它操作或快照清理；只传播安全阶段/类型/错误码，不保留任意原始异常链。

成功/失败/取消都在实际子任务退出后await Snapshot.DisposeAsync。释放失败时保有原快照，不能返回成功或仅报取消；向外使用安全IrisException(Unknown)，Detail可含固定阶段、主错误码/类型与清理类型，不能含图片/文本/配置/原始消息。成功的返回值不能绕过清理屏障。保有清理责任的数据只需原快照及安全元数据，不继续保留原图、准备图、Raw/Text或异常树。

RetryCleanupAsync只重试本Service保有的失败快照（重复调用其DisposeAsync），不重新取得快照/预处理/识别、不调用其它业务副作用。并发重试/Stop共享每份快照的实际清理任务；一个失败不妨碍其它责任尝试。失败保留原对象直到成功。它不停止正常准入，也不借此清理所有Registry无关债务。调用者取消只取消等待，已开始的清理由Service拥有并观察。

StopAccepting幂等且立即返回：同步封闭准入并安排对全部已接纳操作的取消，不能在UI栈同步等待第三方取消回调。StopAsync先封口，再等待所有Service操作和取消回调、实际子任务、快照释放及自身已有清理债务。单个等待者token只取消其等待，不取消共享stop；失败后下次StopAsync/DisposeAsync可重试，不能永远返回旧faulted任务。DisposeAsync等价无token StopAsync。

未获得快照时因caller取消而退出，Registry共享刷新仍归Registry拥有，Service不要取消或冒称它已结束；App最后还要停止Registry。服务具体类可为Host同步处置实现IDisposable，但只能在无活动/已成功清理时完成，不能sync-over-async等待UI/Provider；未完成明确拒绝并保留责任。成功收尾后不保留图像/结果/快照，不引入无界完成操作缓存。

## 5. DI和App退出

Core注册唯一IOcrService/OcrService及IOcrTextNormalizer，重复AddIrisCore不重复注册。只依赖Core端口与必要TimeProvider/日志，不引用Platform/Providers SDK、不增加友元或第三方包。

App在停止入口同步封闭OCR服务（并沿用Registry和现有动作封口），随后await Service.StopAsync，再停止图像预处理器。不能先对预处理器StopAcceptance，让已接纳OCR在服务自身取消前因依赖被销毁失败。Registry最终停止仍在所有租约所有者（设置/翻译/动作等）排空后、Host之前。保留截图准备/overlay窗口依赖保护、其它独立清理尝试和失败重试。

若服务收尾失败，仍保有它及原快照，报告安全错误并禁止Host/AppShutdown假成功；可继续尝试不依赖尚在用资源的清理。不要在失败finally同步Dispose Host，也不要持快照await Registry.Stop造成自等。新增测试覆盖真实Bootstrapper的OCR→预处理→Registry→Host顺序及失败重试。服务取消只负责当前服务操作，不越权关闭用户窗口。T-031才提供截图翻译/OCR入口，T-032才编辑OCR设置。

## 6. 验收

按原错误集合对每个ErrorCode验证回退/不回退；缺失/禁用/未配置/指定ID/相同主备、两次不同约束均用原图、主等待时改变配置仍用原快照、最终Raw/UsedFallback与空白结果。用实际Registry/Service/Normalizer组成至少一个完整路径。

Normalizer固定样本覆盖两种layout、无/部分/非法Bounds、严格阈值、不同高度/重叠/安全坐标算术、内部换行、空行、标点、列表、拉丁连字符、CJK补充平面与韩文；期望按规范写，不从实现计算。不得改已验收helper来迎合样本。

用真实服务加门闩外边界验证caller取消、Stop、非合作Prepare/Recognize、备用前取消、快照原对象释放失败/并发重试/多错误、同步Dispose拒绝提前销毁、成功后引用GC（有正对照）；失败测试先放行所有门闩再等实际任务/释放资源。实际DI/Bootstrapper/预处理器生命周期组合及原平台回归保留。

新增测试不联网、不读用户图像/剪贴板/窗口内容、不真实按键或抓屏，不运行性能脚本。保留已授权的既有只读metadata/合成SDK smoke，如实保留环境跳过。定向后串行Release构建和全量回归，日志/源码/DLL指纹真实归档。
