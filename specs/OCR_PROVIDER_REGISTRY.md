# OCR注册表、异步租约与发布屏障

> 2026-10-06 · T-030A · ARCH v1.46 · CCR-030 · 已定稿。
> 本文在OCR范围优先于PROVIDER_REGISTRY.md的翻译专属Order/同步Dispose规则。凭据格式、设置提交点和安全规则复用原规范。精确公共声明见CONTRACTS.cs。

## 1. 范围与所有权

Core实现OcrProviderRegistry，借出IOcrProviderSnapshot，不再实现裸实例Get接口。一次快照捕获稳定AppSettings及已启用OCR列表（包含未配置项），严格按Ocr.Providers列表顺序；Active/Fallback/LanguageHint/TextLayout来自同一Settings。不得引入Order字段。OcrService/Normalizer在T-030B接此快照，T-032才编辑OCR设置。

每项含不可变Profile、Provider、随机InstanceId；禁止凭据派生ID。列表只读且无外部可写别名；瞬态草稿亦在异步排队前捕获，Enabled=false仍可测试。快照Settings采用现有完整设置冻结规则，不暴露原列表/字典别名。日志不得输出整项/配置/凭据/识别内容。

当前发布代持有实例的注册表引用；每份快照另持租约。退休后有租约仍可正常使用，最后一个引用释放才实际处置。消费者等全部Provider任务结束后DisposeAsync，不能单独释放借出的Provider。短暂保存的注册项不延长租约使用权。

Snapshot首次DisposeAsync立即使Settings/Providers访问抛ObjectDisposedException，逻辑引用只减一次。并发释放共享实际任务；本次释放导致零引用时等待真实处置。已退休清理失败不丢原实例，重复DisposeAsync可重试同一资源；与Registry.RetryCleanupAsync/StopAsync竞争时每个实例最多一个实际处置任务。若实例仍为当前代，释放租约不等待未来退休。

## 2. 构造、凭据与复用

构造Registry只保存依赖/订阅，不读取Current/密钥/创建Provider。Factory重复Kind是安全配置错误。首次Refresh在Settings.LoadAsync（包括损坏恢复）后、用户入口启用前执行。

使用PROVIDER_REGISTRY.md §2.1的原不可变凭据集、legacy两键和错误处理。允许ProviderCredentialResolver窄OCR重载或抽取Id/CredentialSetId内部核心，翻译现有入口保留且行为不变；不伪造翻译Profile、不复制另一套解析器。显式坏引用不得退回legacy/有效None，本地Provider也不能因此错误标为可用。提供不联网未配置占位，Descriptor信息保留，Recognize抛NotConfigured，Test返回失败，取消优先；占位Constraints应是固定合法保守值（PNG/BMP，40..32768，285212672原字节，非Base64，无表单预算），不得触发原生能力查询。

所有OCR Profile有效字段（包括嵌套Options、DisplayName、CredentialSetId）和凭据值参与构造指纹，Enabled仅控制是否列入；无关AppSettings变更及列表重排复用InstanceId。按内容比较，不以集合引用判断；比较材料不得记录。禁用退休，重新启用可新建。未引用Secret变化只使重新核对，不必重建无变化实例。

已启用缺Factory或构造失败：不发布半张表，不把旧状态冒充刷新成功。清理本候选新建实例，复用项不误退役；保留旧发布及安全错误。工厂返回实例后登记所有权，任何后续失败都不能漏掉它。实际Provider都为独立工厂实例，不要求防御恶意Factory复用同一对象。

## 3. 条件发布、共享刷新与通知

Settings/SecretStore Changed只标记失效与调度，不在回调中读密钥、重建或等待。捕获/解析/构造在后台执行，不能在UI同步调用栈做阻塞工厂/解密。发布必须复核捕获以来设置/密钥失效版本；在读凭据/构造期间改变时废弃候选、清理其新增实例后重新捕获，不发布混合代。不可变引用保证配置+凭据联合编辑；任意直接更新legacy两键不是跨文件事务。

Refresh成功是屏障：调用前/过程中失效均已处理到一个一致发布点，且不存在会再读取旧引用的构造任务。Acquire必要时加入刷新并原子租用符合该点的当前代，不能因后台失败静默返回过时状态。Changed仅Provider集合/顺序/Descriptor/代次改变后触发；无关设置更新仍更新新Snapshot.Settings但不乱发集合事件。锁外通知，单订阅者失败不影响他者；异常仅安全类型/元数据。

共享刷新由Registry拥有。一个调用者取消仅结束其等待，不能取消另一调用者或丢掉后台任务；已取消Acquire不得遗留无人认领的租约（实际交付前复核取消）。停止可阻止继续发布但必须观察已建候选并收尾。失败事件只安全记录，等待新失效/显式重试，不对不变坏配置热循环。

发布提交点与退休清理不是回滚事务。新代发布后零租约旧实例清理失败，保留新代和失败实例，Refresh向等待者安全报告未完成，后续Refresh/RetryCleanup可重试。已有旧租约不阻塞Refresh，也不阻塞凭据发布屏障（Provider已持构造时凭据，不再读SecretStore）。每次Refresh至少处理进入时已存在的无租约清理债务；不要求等待未来无限新增的退休。

## 4. 清理、错误和停止

优先await IAsyncDisposable.DisposeAsync；仅不支持异步者使用IDisposable.Dispose；两者都没有者只释放引用。真实处置在锁外执行。不因发起、失败或取消就标记disposed，不用包装器制造“已释放”。失败保留原实例到成功；继续尝试其它可清理实例，安全汇总多个失败。异常不得含Provider原始异常链、凭据、配置、图像；可使用IrisException(Unknown)及类型/固定阶段/InstanceId元数据。不得无上限递归、热循环或后台静默吞错。

RetryCleanupAsync只处理进入时已退休且零租约的资源及正在进行的相关清理；不处置当前代、不关闭正常准入，不等待仍有外部租约的退休项。失败可见，成功移除引用。调用者取消只取消等待，不遗弃已启动清理。

StopAccepting幂等、非阻塞：拒绝新Acquire/Transient/Refresh，退订/封闭Changed，标记停止；已有租约仍有效，可释放。StopAsync先StopAccepting，等待在途刷新/瞬态构造收尾，退休当前代，等待所有快照释放，再等待真实处置。不要在还持有快照的同一流程中先await StopAsync。外部等待取消不取消Registry拥有的stop；失败后再次StopAsync/DisposeAsync可重试，永久失败不假报完成。DisposeAsync等价无token StopAsync。

具体Registry可额外实现IDisposable以兼容实际Host同步Dispose，但仅允许已完全停止或无在途/租约且可同步确认完成的情形；不得sync-over-async等UI/Provider任务。未完成需明确拒绝且保留责任。并发/重复DI处置不得重复减引用或失去失败资源。停止成功后不保留已处置Provider、凭据、旧Settings及无用事件引用。

## 5. DI、设置协调和App

Core注册唯一具体Registry和端口，IProviderConfigurationObserver适配同一实例；重复AddIrisCore不重复注册/订阅。现有SettingsEditCoordinator已通过IEnumerable<IProviderConfigurationObserver>.RefreshAsync等待屏障，接入唯一OCR观察者即可；不得造EnsureApplied方法或新增OCR UI/编辑补丁。观察者失败沿用ApplyPending与既有RetryApplyAsync语义：已提交设置不回滚，旧凭据在屏障成功前保留。跨翻译/OCR引用及禁用项仍按现有规则检查。

AppBootstrapper在Load后刷新OCR。StopCore最早同步封闭OCR新准入，完整停止在设置/翻译窗口、已接纳分发等租约所有者退出后且Host处置前。A尚无OCR入口，但要以受控实际Host组合测试验证租约阻挡和错误分支；不能把接口加入DI就声称退出完成。

当前StopCore在截图/选择等清理失败后可能提前throw。此单允许必要地延后该安全汇总抛出，使互不依赖的设置/翻译所有者关闭和OCR收尾仍被尝试；保留原先依赖顺序、失败资源和重试，失败时不得继续Host/AppShutdown假成功。若某所有者关闭失败仍持租约，应报告并保留OCR待清理责任，不在该路径无期限等待它；下一次停止在所有者成功后排空。不得在finally无条件同步Dispose Host，或先等待Registry再关闭租约所有者。

不修改现有平台清理算法/Provider生产代码，不提前加入OcrService。B之后必须将OCR调用排空置于预处理器停止之前；本单不声称尚无实现的调用已接线。

## 6. 验证

接实际Registry/Coordinator/DI/Bootstrapper，外部存储/Factory/HTTP/SDK用受控替身，门闩与实际状态避免睡眠猜测。至少验证：

- 构造懒读、顺序/冻结、主备同代、配置无关变化复用、每个Profile字段重建及明确坏引用/legacy/完整凭据格式；含禁用草稿。
- 设置/密钥在读取/构造门闩内失效、未引用新凭据不混合、共享刷新等待者取消、候选失败清理、队列停止和最后交付取消。
- 老租约跨重建、两个租约释放、瞬态释放、异步处置门闩、多个故障继续清理、原实例重复释放/Retry/Stop协调；仍存活Registry下成功退休引用GC及正对照。
- 实际SettingsEditCoordinator保存与OCR观察者屏障：ApplyPending保持已提交状态，失败保留旧密钥，重试不重发提交；现有编辑仅翻译/普通字段，OCR设置变化可由受控设置服务模拟，不能称OCR表单已实现。
- 实际百度/Windows Provider至少各一条经Registry的异步拥有/释放组合，fake HTTP/OCR边界（Windows可真实自制位图），不能仅用无资源假Provider证明真实异步处置；保留T-029获准的既有合成SDK测试。
- 真实DI唯一观察者、启动顺序、停止拒绝新租约/等旧租约、早期无关清理失败仍尝试OCR、所有者失败可重试、Host不早处置和全部任务/线程退出。

新增测试不联网、不访问用户剪贴板/截图/输入，不做性能脚本。既有获准只读桌面metadata及合成WinRT smoke保留；环境跳过如实。全量Release零警告，实际日志/散列归档，不用源码拷贝状态机作为产品验证。
