# 当前工单：T-040

继续全功能端到端验收、ARCH指标测量与缺陷闭合。完整范围见[T-040工单](T-040.work-order-20261106.md)与[完成性核对账](T-040.completion-ledger-20261108.md)。T-039C/父T-039已结案，M6/T-033延期保持。T-040及项目目标active；tasks/done/T-040.md尚未完成。公开版本仍0.2.10，未授权后续公开推送、标签或发布。

## 当前实际状态：2026-11-08 08:44 UTC

同一Sandbox edf31417-4146-4ccb-9b48-feb98ccb6871仍在；会话目录artifacts/t040/manual-verification-20261107-3，来宾输出C:\IrisT040Output。当前安装本地0.2.14，来源57fc1ce837961faacd8c54951d2642e4967f9325，主进程3808、创建08:38:36 UTC；08:44再次核对97项安装文件全部匹配。原七条历史保持，目前十五条，SQLite无错误，设置/凭据哈希保持。

材料映射：C:\IrisT040R2rRepeat→cli-input，C:\IrisT040UiaFixed14→uia-fixed-input；旧HistoryFixed/CardFixed13材料保持。自有NativeSelectionFixture 3364已最小化，普通Edit和模拟密码只含合成内容；浏览器本地场景在C:\IrisT040UiSamples。自有HTTP服务已在08:34恢复：旧1284于07:17:34按一小时期限结束，实际缺失及终态确认后才恢复；后续必须重新核对当前PID/监听与终态，不能按Ready/旧PID假定运行。

## 自动控制

当前聊天的Windows控制已修复，通过官方绝对wrapper完成实际安装、鼠标、键盘、截图和窗口操作；兼容补丁/回滚见computer-control-rpc-compatibility-20261108-1。后续组合键实测：Control_L+Alt_L+o在本Sandbox录入为Ctrl+O，官方支持的Ctrl+Alt+o完整录入并触发实际截图热键。使用Ctrl/Alt通用别名；不扩大为所有keysym不支持。专用子代理只核查控制、3文件绑定通过，未再改wrapper/SDK/批准/中断/transport；证据computer-control-modifier-alias-20261108-1。原录入草稿已取消、Ctrl+Alt+F保持。

只能按computer-use技能通过绝对官方wrapper输入；禁止另造前台协议、SendKeys/Run/终端UI或操作Codex设置。跨turn绑定可能消失，重新bootstrap/list_apps/canonical解析，不能据此宣布控制故障。最后实际Sandbox窗口461388、2034×1145；每次恢复仍须刷新。

## 已实测并归档

0.2.11/0.2.12：OpenAI兼容服务保存/模型发现/连接、输入翻译、托盘剪贴板翻译、历史刷新/详情及原文/译文的纯键盘可见粘贴。历史Closing重入缺陷b3072b2修前2失败/修后3成功，三关闭入口和重开实际通过。169原件/15截图见controlled-history-acceptance-20261108-1。

0.2.12/0.2.13：历史原文搜索、收藏/仅收藏、重译、界面缓存及网络仅一次、真实停止且cancel08B不写历史、429自动重试、503安全提示与不写失败历史、模型截断UI/历史标记。卡片命令未恢复的22133ca修前3失败/修后3成功，0.2.13首次复制/重试点击均实际通过。3779项：3777成功/2既有跳过；286原件/16截图见controlled-card-and-postgc-acceptance-20261108-1。首次迟到停止与首次未实际录入截断文字的检查排除，原件保持。

0.2.14：原浏览器选区返回空，原生helper返回ELEMENT_SOURCE_MISMATCH；UIA原始PID属性0且可验证HWND。对照工具的WPF初始化可得到正确PID，标识初始化/默认代理表17项恢复无改善；WPF仅用于诊断，产品未采用。模拟密码IsPassword=true已独立核对。57fc1ce按既有规范使用真实Win32窗口PID及有界父链归属证明缺失字段；非零冲突/未知原生PID/无归属继续拒绝，密码先于文本、300ms预算、无WPF/无新包保持。9回归修前4失败/5成功、修后9成功；全量3788项：3786成功/0失败/2既有环境跳过，Release零警告/错误。16资产/97载荷/六native程序集通过。

主会话自动安装0.2.14后，真实原生Edit与浏览器选区都成功翻译，新增两条Selection历史；模拟浏览器密码读取被拒绝，未发第三次合成请求、未写敏感历史。284原件/12真实截图绑定于controlled-uia-acceptance-20261108-1。原路径工具错误（误用根目录UIA exe）及测试草稿编译失败另保留，不归因产品。实际UIA/helper单项成功不等于全T-040完成。

## 剩余范围与固定边界

继续复制安全兜底、自动划词图标/直接翻译、截图/OCR三个正向流程与图像处理、Anthropic/DeepSeek、代理及联合保存/重载/冲突、热键其它录入/动作、历史译文搜索/分页/删除/清理、双语言/主题/DPI、剩余ARCH测量和MSI等待。所有功能应有真实UI与独立证据，不能以代码/API/测试替代。

当前Sandbox Windows OCR API语言数0；实际截图框选后OcrLanguageUnsupported、原内容保持。只读Get-WindowsCapability报0x80072ee6；未改变系统组件或宿主安全/网络/证书。正向本地OCR仍未通过；不要伪造可用语言、放宽来源或恢复用户已撤回的独立OCR窗口。

原WS203.867MiB与首次启动1599.713ms失败保持。0.2.12目标2524一次有界自然Gen2配对后，观察器5184结束、关窗并由3556采45份：WS中位/最大200.176MiB仍超160，PB62.203MiB符合100，CPU累计增量0；未请求GC/修剪WS。0.2.14匹配的完整指标尚未完成。CPU<30%启动≤1500ms、单4K截图≤150ms、划词≤300ms、流式附加≤50ms、WS≤160MiB/PB≤100MiB不变。缺4K条件，按用户选择先当前环境推进，不宣称4K通过，不重复同一候选观察取得通过。

用户要求尽量减少手工操作：可控的同一自有Sandbox操作与采集由主会话完成；仅控制修复获子代理授权，其它工作不分派。PowerShell5.1兼容，中文PS1用UTF-8 BOM。不得读取/修改/提交工作区.codex、读SSH私钥、改宿主安全/证书/网络、强推或改写原公开失败。旧进展/失败保留于T-040.progress-20261108.md与逐字节证据。
