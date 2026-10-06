# 截图翻译与OCR动作入口

2026-10-07 · T-031B · CCR-033 · ARCH v1.49 · 已定稿。

## 1. 产品行为与范围

用户已撤回独立OCR结果窗口。三个Core Workflow Handler复用已验收IScreenshotService与IOcrService；App注册并提供三个托盘入口，已有快捷键通过Dispatcher命中它们，不改默认绑定或静默OCR默认禁用状态。不得恢复OcrResultWindow、图片预览或专有重识别页面。T-032才做OCR/截图设置页，M6延迟实现。

ScreenshotTranslate：取得现有翻译操作→CaptureRegionAsync(Translate)→同身份loading→RecognizeAsync(image, cancellationToken: token)→同身份Text/Origin=Screenshot/AutoTranslate=true。ScreenshotOcr相同，但purpose=Ocr且成功AutoTranslate=false，仅填入现有原文框，可编辑并用既有翻译入口继续。静默SilentScreenshotOcr只Capture(SilentOcr)→OCR→SetTextAsync→可选通知，不创建窗口、不取得翻译操作，也不撤销用户当前翻译。

截图组互斥沿用实际ActionDispatcher，执行权覆盖整个Handler，包括OCR、剪贴板和清理。重复截图动作忽略，不排队或替换当前截图。三个Handler构造无窗口/桌面查询/密钥/OCR工作，无DI环。

## 2. 身份、取消与结果

两个可见动作在捕获前TryBeginOperation(callerToken)，拒绝准入则不捕获。捕获取消null不弹新结果窗、不开始OCR；旧窗口内容不清空。捕获失败用同操作TryNotifyAsync默认错误，不强行显示结果窗。成功capture以选区右下角物理坐标Anchor发布loading（Text=null、AutoTranslate=false、固定安全StatusMessage）；条件发布false则不继续OCR。loading不能带任意Provider文本。

OCR成功用同一翻译操作发布最终Text；Error或空白结果用默认错误（空白按既有OcrNoTextFound错误码）条件发布，不写空白覆盖已有原文，不复制。只调用Service一次，不重写回退/预处理/Normalizer，不查询新配置伪造旧结果元数据。UsedFallback通过下述固定提示表达。图片仅Handler持有至OCR/清理退出，不传给翻译窗口，不长期保留。调用方租约在自有任务退出后finally归还，不取消窗口已接管的翻译/复制。取消静默，清理异常不得当取消吞掉；显示/通知本身失败交Dispatcher边界处理，不递归发布另一个错误。

原caller已取消而转发回调尚未执行时也必须拒绝发布/复制/提示；复用翻译操作时核对原caller状态，不能只依赖内部转发CTS。属性通知重入、UI排队后新意图/隐藏/退出/用户编辑均不得产生旧结果副作用。保留已验收翻译身份与关闭=隐藏语义，不改已通过后端。

## 3. 原文自动复制与备用提示

TranslateWindowRequest新增默认false的AutoCopySource、NotifyOcrFallback；不影响旧调用。只对显式非空白Text且无Error/Status的成功请求生效，每操作分别最多认领一次。必须在Presenter的ApplyRequest与重入之后最终accepted=true的UI接受点认领；被拒绝的显示不得触发副作用。AutoCopySource复制该次OCR原文，与Translation.AutoCopy的翻译结果复制完全独立，不能改其配置。ScreenshotOcr在动作开始捕获一次AutoCopyOnOcr；ScreenshotTranslate不因该选项自动复制OCR原文。

窗口接管复制任务，TryShow不必等写入完成；调用方归还租约后任务仍有效，原caller取消仍生效。复制前再次UI核对当前操作、原caller、窗口可见/未停止及文本修订；新用户编辑撤销尚未提交自动复制。持有实际工作责任直到IClipboardService退出，隐藏/退出取消未提交资格并等待真实工作。一次失败不因清理重试重写；明确新操作可再复制。EmptyClipboard提交后的迟到取消不承诺回滚。错误安全可见，保留原文；不读取用户剪贴板验证。

NotifyOcrFallback以同一操作UI接受点直接显示一次固定“已使用备用 OCR”提示，不传任意文本、不造ErrorCode、不经过第二次匿名排队。托盘不可用时放弃该信息提示，不延后显示过期提示。认领与显示都不产生新窗口。两种可见动作均映射outcome.UsedFallback。

## 4. 静默OCR反馈与退出

新增Core IScreenshotOcrFeedback：StoppingToken、TryNotifyCopiedAsync(int characterCount, CancellationToken)、TryNotifyErrorAsync(ErrorCode, CancellationToken)。App单例实现；构造无外部工作。StoppingToken在服务存活期可读。两个方法在实际UI接受点检查传入token原始取消状态及App停止标志，托盘不可用/取消/停止返回false，无排队缓存或新窗口；计数负值/非法错误码抛参数异常。正文仅固定“已复制 N 个字符”（N为实际文本UTF-16 Length，与现有InputLength口径一致）或ErrorMessages默认错误，不包含识别内容。

静默Handler在入口捕获一次SilentOcrShowNotification，用caller与StoppingToken建立仅覆盖Handler生命周期的取消连接。每次外部调用前直接检查原caller和StoppingToken状态，不仅看连接token；OCR完成后写入前仍须复核停止。反馈方法传原caller token，由App反馈服务在UI接受点同时检查原caller及自身停止状态，避免转发回调延迟。clipboard已提交的收尾必须等待。复制成功且设置允许才提示计数，失败只报默认错误；取消不提示。不得用翻译操作为静默动作提供身份，从而干扰已有翻译。

App内部反馈停止协调器先封闭通知准入并异步取消StoppingToken，观察取消回调，等待在途反馈；在真正Host释放前排空所有Handler。保持Screenshot/Preparation收尾依赖、OCR Service/Registry/图像所有者顺序。已进入Clipboard的非合作任务阻止Host退出；失败保持可重试且有安全错误，不死等自身持有的操作租约。不要把Handler本地linked CTS提前释放导致已转交的翻译工作失去caller取消；可见动作直接用既有Presenter操作，静默连接在完整执行后释放。

## 5. 契约收敛与验证

删除A暂留但从未发布使用的OcrResultViewRequest、IOcrResultOperation、IOcrResultPresenter，精确同步CONTRACTS.cs；新增上述两个TranslateWindowRequest字段和IScreenshotOcrFeedback。其它Core端口、OcrOutcome、错误枚举、默认设置/schema/包不改。相关文档以本规范覆盖旧OCR_RESULT_WINDOW历史方案。

Core Handler测试覆盖三个purpose、Anchor、准入拒绝、capture取消/错误、OCR错误/空白/成功/备用、设置入口快照、同身份loading/terminal、false后不做后续工作、租约归还和取消清理。App组合接实际Dispatcher与翻译Presenter/Window/VM，外部捕获/OCR/剪贴板/托盘用窄fake；确认普通OCR填原文且0翻译请求、截图翻译发一次、静默不触碰已有翻译。覆盖UI排队/后注册caller取消回调/属性重入/编辑撤销、一次副作用、隐藏退出/非合作剪贴板、通知不可用无缓存、实际托盘与配置热键入口和截图组交叉互斥。

复用窗口做少量自有离屏明暗图确认OCR原文/加载/错误，不显示图片或新窗口。不运行真实截图/用户剪贴板/键鼠注入/云HTTP/性能脚本，既有获准SDK自制图smoke保持。串行Release0警告错误、全量成功，失败记录与真实未验证项保留；禁止以旧独立窗口证据宣称新入口已完成。
