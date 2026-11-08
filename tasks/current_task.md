# 当前工单：T-040

继续全功能端到端验收、ARCH指标测量与缺陷闭合。完整范围见[T-040工单](T-040.work-order-20261106.md)与[完成性核对账](T-040.completion-ledger-20261108.md)。T-039C/父T-039已结案，M6/T-033延期保持。T-040及项目目标active；tasks/done/T-040.md尚未完成。公开版本仍0.2.10，未授权后续公开推送、标签或发布。

## 当前实际状态：2026-11-08 11:02 UTC（复制复审已通过）

同一Sandbox edf31417-4146-4ccb-9b48-feb98ccb6871仍在；会话目录artifacts/t040/manual-verification-20261107-3，来宾输出C:\IrisT040Output。主会话已自动安装本地0.2.15，来源8925eb3c7227181e8c3c6f2dffdb16a09799c8ff，主进程7504、创建10:09:48.855355 UTC；11:02再次独立核对97项安装文件全部匹配。原十七条资料均未改动，新增真实复制兜底Selection记录18；Input“Iris T040 clipboard fixed 015”与NativeSelection新请求均成功。凭据哈希60d78472e3e206a670a708d39457527ced1dde4cd06736839808e3571b9fed4b保持；设置的变化仅为实际拖窗的lastBounds及本段恢复ReadOrder=[UIAutomation,Clipboard]，Restore=true/400ms/mouseOff和原热键保持。536原件/12真实UI截图已冻结于controlled-clipboard-acceptance-20261108-1；公开版本仍0.2.10。

材料映射：C:\IrisT040ClipboardFixed15→clipboard-fixed-input，C:\IrisT040R2rRepeat→cli-input，C:\IrisT040UiaFixed14→uia-fixed-input；旧HistoryFixed/CardFixed13材料保持。自有NativeSelectionFixture3364和浏览器合成场景仍在。旧fixture7292已按一小时期限结束，实际缺失/终态确认后10:02恢复；10:10独立核对新fixture5868、实际18040监听和新请求。此服务仍有一小时时限，后续应实际核对，不能按Ready/旧PID假定运行。

11:02:03 UTC fixture5868已按一小时时限正常结束，final-state明确Requests=2/Ready=false；继续请求前必须确认进程/监听确实缺失后再恢复，不使用旧Ready或旧PID。

## 自动控制

当前聊天的Windows控制已修复，通过官方绝对wrapper完成实际安装、鼠标、键盘、截图和窗口操作；兼容补丁/回滚见computer-control-rpc-compatibility-20261108-1。后续组合键实测：Control_L+Alt_L+o在本Sandbox录入为Ctrl+O，官方支持的Ctrl+Alt+o完整录入并触发实际截图热键。使用Ctrl/Alt通用别名；不扩大为所有keysym不支持。专用子代理只核查控制、3文件绑定通过，未再改wrapper/SDK/批准/中断/transport；证据computer-control-modifier-alias-20261108-1。原录入草稿已取消、Ctrl+Alt+F保持。

10:12后的当前复测发现新的组合键交付失败：Ctrl+Alt+d/f不触发产品，当前读者事务不存在。实际录入器把Ctrl+Alt+d记为Ctrl+D；顺序Alt+Ctrl+d、Alt_R与大写O对照仍丢Alt。全屏2560×1440对照也未触发F，已恢复原2034×1145窗口，未改系统安全/网络。所有草稿取消、持久原绑定保持。控制专用子代理核对12个官方文件加wrapper共13项字节全部相同，不据此改wrapper；证据computer-control-hotkey-recheck-20261108-1。当前准备同一录入器的独立连接对照；root在子代理一次输入期间不操作桌面。不得把前段07:03的O成功扩大为本段当前热键正常。

独立连接一次D输入后录入因自动激活/鼠标离开取消、Iris自身前台输入窗口打开，不算录入或复制通过。root重新bootstrap与一次全新连接仍不能用直接组合API触发F。20秒只读键盘事件观察证明Alt Down后提前Up，再发生F Down；普通Ctrl事件也被捕获，观察器正常解除钩子。未凭猜测修改wrapper/native helper。通过官方CU操作来宾已有osk.exe，实际保持Ctrl/Alt并点击F，输入窗口正常打开；明确激活原生场景标题栏后，同法D取得真正ClipboardOnly选区并恢复原剪贴板。OSK是当前可用的UI操作路径，不等于直接组合输入组件完全修复；只使用官方CU，不另造控制协议或键盘注入helper。

新只读元数据与两源码独立核对确认初始HotkeyCaptureSession已Ready，但UI仍“正在准备”；Begin完成后才订阅ProgressChanged且未回放Current，初始通知可丢失。这项真实录入进度缺陷仍待产品修正，不把它当提前Alt Up的已证根因。当前OSK已最小化，设置窗口停在已保存的UIA优先页；后续每次刷新实际窗口后操作。

本轮通知修正已实现：订阅后读取Current，串行按revision发布，并在结束时移除本次通知订阅；观察方异常不改变实际捕获/清理。五回归原代码全失败，修后全成功，包含真实SettingsViewModel初始Ready显示。测试草稿取消令牌检查、误用报告参数的0测试记录及日志分析器草稿失败均保留。源代码全量验证与新本地候选尚待完成；安装版仍015/7504，不据回归绿色宣布真实修正版已通过。536原件归档提交1b445b1，536份提交字节全部再次匹配。

通知源码1fdd489的首次全量3797：3794成功、1项ProxyConnectivity stop取消回调失败、2既有环境跳过；App全部通过、Release零错误。此失败暴露另一实际竞态：tester/composite Stop先yield后才加入session.Dispose，可能自动重试并漏报并发完成的首次清理失败。当前已改为Stop返回前认领/加入原Task，再异步等待，原重试和安全报错语义保持；Providers584全成功。原完整验证/错误类型过滤的0测试原件保留，第二轮全量及016候选待执行。控制子代理一次F5也只录入Ctrl+F5，草稿/持久绑定与键释放已独立通过；不支持仅打印字符路径假设，42原件绑定于computer-control-function-key-trial-20261108-1。

只能按computer-use技能通过绝对官方wrapper输入；禁止另造前台协议、SendKeys/Run/终端UI或操作Codex设置。跨turn绑定可能消失，重新bootstrap/list_apps/canonical解析，不能据此宣布控制故障。最后实际Sandbox窗口461388、2034×1145；每次恢复仍须刷新。

## 已实测并归档

0.2.11/0.2.12：OpenAI兼容服务保存/模型发现/连接、输入翻译、托盘剪贴板翻译、历史刷新/详情及原文/译文的纯键盘可见粘贴。历史Closing重入缺陷b3072b2修前2失败/修后3成功，三关闭入口和重开实际通过。169原件/15截图见controlled-history-acceptance-20261108-1。

0.2.12/0.2.13：历史原文搜索、收藏/仅收藏、重译、界面缓存及网络仅一次、真实停止且cancel08B不写历史、429自动重试、503安全提示与不写失败历史、模型截断UI/历史标记。卡片命令未恢复的22133ca修前3失败/修后3成功，0.2.13首次复制/重试点击均实际通过。3779项：3777成功/2既有跳过；286原件/16截图见controlled-card-and-postgc-acceptance-20261108-1。首次迟到停止与首次未实际录入截断文字的检查排除，原件保持。

0.2.14：原浏览器选区返回空，原生helper返回ELEMENT_SOURCE_MISMATCH；UIA原始PID属性0且可验证HWND。对照工具的WPF初始化可得到正确PID，标识初始化/默认代理表17项恢复无改善；WPF仅用于诊断，产品未采用。模拟密码IsPassword=true已独立核对。57fc1ce按既有规范使用真实Win32窗口PID及有界父链归属证明缺失字段；非零冲突/未知原生PID/无归属继续拒绝，密码先于文本、300ms预算、无WPF/无新包保持。9回归修前4失败/5成功、修后9成功；全量3788项：3786成功/0失败/2既有环境跳过，Release零警告/错误。16资产/97载荷/六native程序集通过。

主会话自动安装0.2.14后，真实原生Edit与浏览器选区都成功翻译，新增两条Selection历史；模拟浏览器密码读取被拒绝，未发第三次合成请求、未写敏感历史。284原件/12真实截图绑定于controlled-uia-acceptance-20261108-1。原路径工具错误（误用根目录UIA exe）及测试草稿编译失败另保留，不归因产品。实际UIA/helper单项成功不等于全T-040完成。

## 剩余范围与固定边界

### 当前复制兜底复审（2026-11-08）

真实设置已暂存并保存ReadOrder=[Clipboard]、RestoreClipboard=true，用于单独验证；原默认UIA优先需本段结束恢复。原生Edit选区最初返回空；原剪贴板WPF复制含DataObject/System.String/Ole Private Data等未支持格式，拒绝清空且原合成标记保持。此负例不算成功读取/恢复。一次鼠标操作被用户输入中断，后续CLI目标未验证，明确排除；按刷新后原生目标重测仍失败。正确卡片复制产生13/16/1/7支持格式后，原生CLI触发Ctrl+C（KeyboardOutcome=Injected、ClearVersion已产生），随后报CaptureFailed。只读当前事务/异常元数据确认CLIPBOARD_OPEN_BUSY，原生注入/快照等工具失败原件保持；不采集用户内容/密钥。

8925eb3c7227181e8c3c6f2dffdb16a09799c8ff新增独立无锁ObserveVersionAsync，仅用于复制响应的序列/窗口/PID稳定观测；数据读写仍取锁，原ReadVersionAsync及现有预算保持。三次有界双读确认身份，变化不返回混合版本。4回归修前全失败、修后4成功；全量3792项（3790成功/0失败/2既有跳过）、Release零警告/错误通过。0.2.15的16资产/97载荷及六R2R程序集已核对，安装器123666506字节、SHA6143e5d5fd5212f31bbb40dd6c183a37d9149c71cccdfa0a055c9b17d0a05365。10:09完成实际安装、97项文件核对；真实卡片复制及纯键盘粘贴已建立与普通Edit不同的合成基线“Iris T040 synthetic translation”，格式13/16/1/7、序列534。随后实际Ctrl+Alt+D未送达，读者CurrentPresent=false，故尚不能宣称实际复制读取/恢复成功。临时ClipboardOnly需结束本段后恢复。并发只读工具transcript曾同时间戳路径碰撞，runner已按label生成独立路径，原失败保留；该采集修复尚未归档提交。

上述未通过记录为原失败。后续实际OSK组合D、来源3364与选区已独立核对，10:50真正读出“Iris T040 native selection sample 013”并完成翻译。当前事务Permit/Clear/Candidate/RestoreVersion均有、Restored=true、TargetChanged=false、快照已释放、Failures为空。未再使用type_text/复制按钮，纯Ctrl+A/Ctrl+V显示原不同基线，证明恢复。模拟原生密码style ES_PASSWORD=true，D后拒绝读取，Permit/Clear/Candidate均无、剪贴板序列568前后相同、请求2/历史18不增加。负例探针Keyboard字段仍是旧正例完成Task，不冒充负例新注入。旧UIA元数据工具因0.2.13版本门禁拒绝025，失败保留；新Win32只读style验证不读取密码或UIA文本。11:00真实UI已恢复并保存UIA优先，11:02持久配置与97文件再次核对。对应实际范围已通过；其余完整安全/源切换/自动划词及全部NFR仍按完成账保持。

继续复制安全兜底、自动划词图标/直接翻译、截图/OCR三个正向流程与图像处理、Anthropic/DeepSeek、代理及联合保存/重载/冲突、热键其它录入/动作、历史译文搜索/分页/删除/清理、双语言/主题/DPI、剩余ARCH测量和MSI等待。所有功能应有真实UI与独立证据，不能以代码/API/测试替代。

当前Sandbox Windows OCR API语言数0；实际截图框选后OcrLanguageUnsupported、原内容保持。只读Get-WindowsCapability报0x80072ee6；未改变系统组件或宿主安全/网络/证书。正向本地OCR仍未通过；不要伪造可用语言、放宽来源或恢复用户已撤回的独立OCR窗口。

原WS203.867MiB与首次启动1599.713ms失败保持。0.2.12目标2524一次有界自然Gen2配对后，观察器5184结束、关窗并由3556采45份：WS中位/最大200.176MiB仍超160，PB62.203MiB符合100，CPU累计增量0；未请求GC/修剪WS。0.2.14匹配的完整指标尚未完成。CPU<30%启动≤1500ms、单4K截图≤150ms、划词≤300ms、流式附加≤50ms、WS≤160MiB/PB≤100MiB不变。缺4K条件，按用户选择先当前环境推进，不宣称4K通过，不重复同一候选观察取得通过。

用户要求尽量减少手工操作：可控的同一自有Sandbox操作与采集由主会话完成；仅控制修复获子代理授权，其它工作不分派。PowerShell5.1兼容，中文PS1用UTF-8 BOM。不得读取/修改/提交工作区.codex、读SSH私钥、改宿主安全/证书/网络、强推或改写原公开失败。旧进展/失败保留于T-040.progress-20261108.md与逐字节证据。
