# OCR与截图设置的增量编辑

2026-10-07 · T-032A · CCR-034 · ARCH v1.50 · 已定稿。

## 1. 范围与已有协议

T-032保留完整OCR/截图设置页范围，拆A编辑事务/草稿测试基础，B完整UI。A不新建页面、不启动真实HTTP/桌面；复用现有SettingsEditCoordinator的串行权、凭据暂存、单次设置提交、全部观察者刷新、ApplyPending回执和RetryApply。不能从UI直接写旧AppSettings，不能复制一套编辑协议。T-031已验收，OCR仍复用翻译窗口，不恢复结果窗口，M6延迟实现。

SettingsEditPlan兼容新增Ocr、OcrChanges、Screenshot。OcrSettingsPatch含ActiveProviderId、FallbackProviderId、TextLayout、LanguageHint、AutoCopyOnOcr、SilentOcrShowNotification的可选SettingsFieldEdit；ScreenshotSettingsPatch含ConfirmOnMouseUp、ShowMagnifier、HideIrisWindows、CopyImageToClipboard、MaskOpacity。null表示无编辑，保留提交时最新字段。OcrProfileChange含Add/Update/Delete及BaseProfile/TargetProfile/ProviderCredentialEdit。原三参数SettingsEditPlan构造和全部旧接口保持。

## 2. 捕获、校验与冲突

Commit入口同步捕获所有新增列表/嵌套Options及凭据编辑意图，异步后不读取可被调用方替换的集合。禁止默认record.ToString泄露完整配置/凭据；新DTO的ToString只返回类型/安全Kind。复用现有设置冻结和OCR Profile冻结，不重写后端Registry。

字段冲突沿用现有Base/Target协议：Current等于Base或Target均可提交，否则本字段冲突；未编辑字段不比较。Profile Update/Delete比较被编辑ID的完整基线（含嵌套Options和CredentialSetId），不把另一未编辑Profile的变化误报冲突。提交内部UpdateAsync回调仍复核当前快照，不能仅入锁前检查。翻译/OCR/截图/通用/热键/划词可在一个Plan中联合提交；任一冲突则整单不提交，不能留下部分字段或引用更新。

Add要求base=null、目标ID不重复；Update要求同ID/Kind身份，不允许通过目标任意切换CredentialSetId；Delete要求target=null且凭据Keep。内置ocr-baidu/ocr-windows不能删除（规范化会补回），可编辑/禁用；其它既有或新增支持Kind的Profile按统一规则处理。Update保持原列表位置，Add按计划顺序追加，Delete不重排其它项。当前不新增排序协议或持久化字段。

静态目标校验在凭据暂存前完成：合法Kind/枚举、非空ID/显示名、TimeoutSeconds 5..120、按Kind补齐必要Options；沿用已验收Provider端点/选项合法性，不引入新的Provider种类或网络访问。Active必须在合并后的Profile集合存在；Fallback为null或存在且不同于Active。删除被选中自定义项须在同一计划调整主备，不静默重选。未配置凭据允许保存，由既有NotConfigured/回退处理；不把未配置误判为已测试成功。LanguageHint按既有规则去除首尾空白、空值归auto。MaskOpacity必须有限且0..0.9。未编辑OCR时，不因历史OCR配置而拒绝纯截图/其它领域合法编辑。

## 3. 凭据与提交

沿用ProviderCredentialEdit真实Keep/Replace/Clear和原不可变凭据集格式。Replace使用完整凭据对；Keep/Clear不得带Replacement。百度Replace要求完整非空ApiKey/SecretKey；本地Provider不新写凭据，仅Keep或Clear，旧显式坏引用不能假装有效None。新凭据集随机ID，不由密钥派生；设置仅保存引用，不写明文。

所有OCR及翻译变更共同使用同一次暂存计划和一次Settings.UpdateAsync提交。不同领域存在相同Profile.Id时，不可把两者的凭据映射混在仅按ID索引的字典；作用域须包含领域，且分别应用目标引用。旧凭据仍被任意翻译/OCR（包括禁用Profile）引用时不得删除。Keep的legacy读取与显式坏引用行为沿用PROVIDER_REGISTRY/PROVIDER_EDITING，不绕过现有解析器。

提交前取消/失败按既有协议回收本次暂存值，不发布任何新引用；提交后迟到取消不回滚，刷新所有观察者并清理可删除旧值，失败返回明确ApplyPending及原回执。RetryApply只重试原刷新/清理，不重新写设置或重发草稿测试。纯截图编辑仍遵循既有观察者协议，不能另造“已保存但假报应用成功”。截图新选项影响下一次捕获，不能中途改变正在拖选的快照。

## 4. OCR草稿测试会话

ISettingsEditCoordinator新增同步CreateOcrDraftTest(OcrProviderDraft)。它只校验并冻结参数、创建自有会话，不读密钥/配置、不联网或创建Provider。OcrProviderDraft含Profile、BaseProfile、Credentials；不伪造TranslationProviderDraft。返回IOcrDraftTestSession（RunAsync、IAsyncDisposable）。此拥有者用于保存异步Provider租约的清理责任，不能以Task完成代替释放。

RunAsync同一会话只执行一次，重复或并发调用抛InvalidOperationException；已关闭会话拒绝运行。进入取消不启动外部工作。Keep现有Profile时，在编辑串行权内核对BaseProfile及当前引用并解析凭据，然后释放编辑锁再创建瞬态/测试，不跨网络持锁。Replace/Clear只用显式意图，不回读旧密钥；BaseProfile=null的Keep按新草稿无凭据处理。测试可对Enabled=false草稿执行。通过真实IOcrProviderRegistry.CreateTransientAsync冻结原Profile和解析凭据，调用唯一Provider.TestAsync；禁止调用RecognizeAsync消耗识别额度。百度Test仅鉴权，Windows仅能力检查，行为复用已有Provider。

RunAsync返回安全ProviderTestResult（错误仅默认消息/安全元数据）；调用方取消在真实Provider任务退出后抛OperationCanceledException，不能超时丢弃未合作任务。不保存设置或密钥，不刷新正式Registry配置，不把测试通过当提交。

会话拥有原瞬态快照直至DisposeAsync成功。Dispose请求取消并等待Run及取消回调，释放原快照；失败保留同一快照和清理责任，重复Dispose重试清理而不再Test/创建Provider/多减引用。并发Dispose共用本次清理任务；Run错误与清理错误都须可观察，不吞掉。成功后清空凭据/Provider/草稿大对象及事件引用。未Run的会话可直接关闭且无外部工作。

调用方必须保留会话字段直到Dispose成功，UI完成/关闭须等待它；B将把它纳入设置窗口在途操作/失败重试，不能裸await using后丢掉失败资源。A提供真实会话生命周期验证，暂不新增全局后台测试调度器或修改既有App退出。不得通过修改Registry/Provider来规避会话责任。

## 5. 验证

接实际SettingsEditCoordinator及真实OCR Registry（外部SecretStore/Settings存储/Provider工厂可用窄替身）。覆盖：新字段联合增量/no-op/未编辑最新值、同字段及Profile冲突、异步后修改输入集合、主备同ID/缺失/删除、合法边界与非法值；翻译/OCR同ID但不同凭据不串线，跨域共享旧引用保留、Clear/legacy/显式坏引用；暂存失败/提交失败/前后取消/观察者失败原回执重试，提交一次且无重复密钥stage。

草稿测试覆盖构造零外部工作、Keep基线冲突、Replace/Clear与禁用草稿、Test只一次且不Recognize、实际异步快照、非合作取消/关闭等待、Dispose失败保有原实例重试、取消回调抛错、Run+cleanup错误均可观察、会话关闭后引用回收正对照。注入断言失败的测试先开门闩再join所有任务，保留真实失败记录。

测试不访问用户配置/密钥/桌面/剪贴板或真实云请求；不改默认配置/schema/包、已验收OCR/翻译后端。按串行Release零警告错误及全量成功交付。T-032B再交付完整页面（百度凭据/模式、主备、草稿测试与所有截图选项），A的后端测试不替代B的真实设置UI验收。
