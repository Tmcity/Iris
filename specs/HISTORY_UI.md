# 历史窗口与历史设置

2026-10-07 · T-035 · ARCH v1.52 · CCR-035。

## 1. 完整范围

实现HistoryWindow/VM/Presenter、OpenHistory Handler及托盘入口，在原SettingsWindow开放history页。复用T-034真实仓储和既有翻译窗口/剪贴板端口；不重写历史数据库，不改变schema、默认配置或包。M6仍延期，T-036及以后不提前开始。此单完整交付T-035，不以静态样例或只列表窗口替代。

## 2. 列表、查询和详情

窗口按需创建，同一时刻最多一个实例；重复Show激活现有窗口。首次打开和再次激活刷新列表，构造无I/O。搜索防抖300ms；仅看收藏即时刷新。每页50条（遵守仓储1..200上限），上一页/下一页、总数/页码与空态明确。搜索/筛选变更回第一页，删除或取消收藏导致末页越界时回到最后有效页并刷新，不循环无限重试。使用稳定ID而非列表索引执行动作。

列表仅QueryAsync取得HistorySummary，不批量GetAsync；保留来源、时间（UTC转本地显示）、原文预览、语言对、结果数、收藏及截断提示。搜索按仓储字面量规则；NUL等非法词以安全提示处理，不传原词到日志/异常。禁止拼SQL或自行实现另一套过滤。

展开按GetAsync加载完整HistoryEntry，显示原文和按原顺序全部结果、记录时ProviderName/Model、每个Truncated标记。条目已删除时显示不存在并刷新列表，不用SourcePreview兜底。详情文本只读可选取，采用文本呈现，不把译文当HTML或XAML执行。详情缓存只属于当前列表/会话，刷新或修改后失效，不累积无限全文。

搜索/筛选/分页/刷新有单调请求身份，取消旧请求仍等待实际I/O退出；过期查询不能替换新条件的列表/页码/错误态。查询请求有界（一个实际运行及一个最新等待意图）；详情和行操作也有明确所属会话/记录ID/请求身份，不允许旧结果写到新行或新窗口。使用可控时间/门闩测试，不靠长Delay碰运气。

## 3. 行操作与副作用

收藏/取消收藏使用SetFavoriteAsync；删除用DeleteAsync；清空提供“保留收藏”选项，默认勾选，并明确确认范围后调用ClearAsync。删除单项也须就地确认或对话确认，取消零写入；确认时固定目标ID或清空选项，不因选择改变删除另一项。写操作串行有界，执行中禁用重复提交；不因刷新/搜索变化取消已经提交的写入，不自动重试不明结果。写成功刷新当前筛选/分页，失败保留可操作窗口并显示安全错误，不先假装删除成功。

复制明确区分复制原文和复制所选服务译文。每次显式动作取得GetAsync全文（可以一次动作内共享所取详情），不能复制SourcePreview；缺失记录或失败不改剪贴板。译文截断时明确显示并在完整复制载荷末尾追加固定“[结果已截断]”提示（与正文分行），避免当作完整译文。复制前校验仍属当前会话和本次动作，关闭/新动作在实际接纳点前可撤销；已开始的非合作剪贴板操作完整等待，不以超时丢弃责任。重复操作不得无界排队或迟到覆盖用户新复制。

重新翻译同样GetAsync取SourceText全文，向现有ITranslateWindowPresenter创建新操作并发送Origin=History、AutoTranslate=true、FocusInput=true；采用当前翻译设置/服务/语言选择（界面明确“按当前设置重新翻译”），不声称重放当年模型/Prompt。翻译历史可以正常新增，收藏旧记录不被修改。不把保存的译文作为输入，不直接绕过Presenter调用Provider。关闭/删除/新请求后的迟到详情不能唤起翻译窗口；操作租约完整释放。测试验证实际Presenter链收到全文且至多自动翻译一次，禁真实云请求。

## 4. 历史设置的一次联合保存

history页编辑History.Enabled和MaxEntries；MaxEntries为100..100000整数，与当前SettingsNormalizer一致，文字明确“最多保留的非收藏记录数，收藏额外保留”。启用仅影响后续翻译是否记录，不隐藏/删除旧历史。降低上限不立即批量删库，沿用仓储下一次写入/取消收藏时修剪；界面说明这一行为。清空历史不混入设置保存事务，放在历史窗口中。

CCR-035兼容扩展SettingsEditPlan.History（默认null）和HistorySettingsPatch(Enabled,MaxEntries)，字段使用SettingsFieldEdit<T>。同步捕获/结构与范围校验/字段级Base冲突/构造最新AppSettings/NoOp判断均接入现有SettingsEditCoordinator。null保留提交时最新字段；不能用整份旧History覆盖其他字段。错误必须在凭据stage和Update之前返回；Update内仍复核冲突。纯History不需要Provider观察者/凭据回执，不额外占用回执配额，和通用/划词规则一致；联合Provider变更保留原配额、Applied/ApplyPending与原回执重试。

App用原DraftValue与当前文本刷新方式，非法文本保留且不使用旧合法数值。未编辑字段合并外部最新值、脏字段保持原Base/Target；同一全窗口SettingsEditPlan包含所有页面，保存冻结、等待原翻译/OCR测试和清理、关闭优先、失败保留、Applied/ApplyPending重建基线与密码清理保持。仅History编辑也不能被IsDirty/NoOp遗漏。UI不直接调用Settings.UpdateAsync，不新增另一套保存器。

## 5. 展示与完整生命周期

Presenter实现既有IHistoryWindowPresenter.ShowAsync，任意线程调用内部切Dispatcher，不改公开签名。OpenHistoryHandler按OpenSettings风格接动作，托盘入口经IActionDispatcher，Trigger=TrayMenu；构造不得为浏览历史创建Provider或读用户剪贴板。

窗口使用现有WPF-UI主题/UiText/定位基础；关闭即释放会话，等待实际已接纳查询、写入、复制及其UI收尾，释放订阅、CTS、详情全文和DI scope。重复关闭共用完整任务，失败保留原责任并可重试，不先丢窗口再新建；不在Closing同步阻塞。开始关闭先封口，迟到工作不得写已销毁窗口或污染重新打开的会话。部分Show/构造失败也完整回滚，失败清理阻止创建第二窗口。

复用IScreenshotPreparationParticipant与WindowPreparationIntent接入历史窗口：隐藏Iris窗口时包括已打开历史窗；记录新显示/关闭意图，旧截图恢复不得复活已关闭历史窗或隐藏后来新显示的历史窗。不得在截图时按需新建历史窗口。沿用SCREENSHOT_PREPARATION和既有参与者语义。

AppBootstrapper停止接纳历史入口并等待历史拥有者完整收尾后才Host/Dispatcher销毁；尊重截图准备/overlay尚持有窗口的现有屏障。新增内部退出接口允许注入，不新增Core生命周期端口。真实Host DI应无循环，退出错误不被吞掉或用后台Task丢弃；现有截图/OCR/划词/翻译退出规则保持。

## 6. 验证与边界

实际Core编辑协调器+SettingsWindow验证纯History/多页联合保存、范围边界/非法当前文本、脏冲突/干净外部变化、保存冻结/关闭优先、配额与原ApplyPending。历史窗口测试用临时真实SQLite仓储验证搜索命中译文、短词/中文/字面量、分页/末页回退、收藏保护、完整多结果和截断标记、删除/清空两种范围、原文长度超200仍全文复制/重译。

并发失败测试使用窄仓储/剪贴板/时间边界门闩，验证过期查询/详情/复制/重译、取消非合作I/O、确认取消零写、错误后恢复、重复Show/Close、部分显示失败与退出完整等待；失败先释放门闩再join。至少一个实际Host组合测试覆盖入口/生命周期，实际翻译Presenter与fake翻译通信验证重译；不能全部替身证明产品接线。

离屏逐图检查Light/Dark、正常和项目既有小工作区、长中英文/多行、空/错误/加载/确认/分页/展开多服务/截断/设置非法值；控件可滚动到达且复制/重译/关闭入口可用。测试不得读用户history.db或配置/密钥/剪贴板，不发真实云HTTP、不捕获桌面、不注入全局输入、不跑性能脚本。真实桌面/NFR在T-040，不冒称本单已验证。

最后串行Release build零警告错误和全量test全绿；保留所有失败与最终原始日志，记录产物/源码绑定，归档工单全文和报告请求架构师复审。仅本地提交，不push。
