# 网络设置页面与草稿连通性测试

2026-10-08 · T-036C · ARCH v1.54 · CCR-036已批准声明保持。

## 1. 完整范围与底座

在现有SettingsWindow/VM开放network页，实现IProxyConnectivityTester及单次IProxyConnectivityTestSession，完成父T-036全部剩余范围。复用已验收B/R1的真实联合编辑、版本化凭据、URI/本地分类、请求快照和清理底座。NETWORK_CONFIGURATION.md仍是网络协议规范；本文补充UI、独立测试与生命周期。不得注册占位Tester、调用Provider.TestAsync充当网络测试或再建设置窗口。

## 2. 草稿字段与凭据

页面提供System/None/Custom三种代理模式、BypassProxyOnLocal、自定义地址、保持/替换/清除凭据、用户名与遮蔽密码输入。显示说明区分系统代理、明确直连、本地绕过。切换模式保留未提交的自定义草稿，不自动修改全局设置或发请求。未编辑的dormant无效地址/坏凭据引用不得阻止显式改为None/System；已编辑字段仍按B协议校验，不悄悄丢弃输入。

地址当前控件文本在保存/测试前显式刷新，按B同一codec规范化目标；Base仍是原基线。接受带userinfo的有效粘贴并拆为显式Replace和不含userinfo的地址显示，HTTP/HTTPS用户名-only按空密码处理，SOCKS5非空约束不变；不能让格式错误的authority绕过秘密保护。密码不回填、不从UI读取SecretStore。保持文案只能说明“保持现有凭据（如有）”，引用存在不能证明有效。替换不trim凭据；明确空字符串与Clear不同，预算按B。

用户改代理地址时必须选择Replace/Clear，不能默认转交旧凭据。输入不完整/非法时安全提示并保留可修正草稿，不能沿用上一个合法值。URL的局部格式处理需要共享B纯codec时，允许Core给Iris.App增加明确内部友元，不增加公共接口或反向依赖。

## 3. 一次保存与外部变化

Network草稿接入全窗口IsDirty、Base/Target、外部未编辑字段合并、同一SettingsEditPlan.Network和GeneralSettingsSaveCoordinator。不得直接Settings.UpdateAsync或SecretStore.Set/Remove。任一页一起修改仍一次提交、一次原回执配额；Network的ApplyPending明确“已保存”，RetryApply只重试原回执，不提交当前新草稿。

保存前同步固定全计划并冻结修改/重复保存，先取消并完整排空翻译/OCR/网络测试及其清理和UI收尾；停止先发生则不提交。Uncommitted保留输入与密码；Applied/ApplyPending清除本次明文引用和密码控件、恢复Keep并重建正确基线。Reload/删除草稿/成功关闭也清除本次秘密引用，不声称擦除托管字符串。网络或端点相关外部变化不得让旧测试结果回写新草稿；只读future模式沿用B提示，禁编辑/保存/联网测试，可正常关闭。

## 4. 测试目标与唯一语义

目标从已保存Translation或百度OCR Profile中按领域+稳定ID选择；不使用未保存的服务地址，也不实例化Provider。支持禁用项、无模型或无API Key的有效端点；Windows OCR不提供网络目标。OpenAI使用其BaseUrl，Anthropic空地址使用既有官方默认值，百度使用其HTTP BaseUrl；非法/缺失端点安全提示，不自选其它服务器。

IProxyConnectivityTester.Create只复制传入ProxyDraft与选择、分配SessionId，不读设置/秘密/Provider、不创建HTTP资源。Run才读取所选保存端点并调用真实CaptureDraftAsync，取得固定代理快照后创建专属、非全局池的传输。端点UI快照与请求序号用于拒绝迟到结果：相同ID的URL外部变化、删除、换目标或改网络草稿都使旧结果失效。

测试为一个逻辑HEAD，不配置业务重试；底层代理认证挑战可能多次交换。无Provider key、Origin Authorization、Cookie、ExtraHeaders、翻译文本；拒绝端点userinfo/query/fragment，不跟随重定向，不记录Location或原响应正文。真实TLS校验保持。连接超时不超过10秒，响应头上限16KB；不读取正文。10秒软总期限从Run开始包含捕获/解析/发送；超时/取消不能用WaitAsync丢下非合作工作。

结果按NETWORK_CONFIGURATION定义：非重定向最终HTTP响应报告实际状态码且只称“端点有响应”，不声称API鉴权/业务成功；3xx明确未跟随；407含CONNECT错误独立标为代理认证；调用方/关闭取消优先于内部超时。已实际发出请求并收到HTTP响应时显示真实RouteKind，明确None/本地/系统绕过的直连；在配置/路线解析失败或没有可确认路线的失败状态，不显示推测的“代理已通”，不用默认枚举值伪称实际路线。

## 5. 会话、清理和退出

同一session只能Run一次，Dispose先封口/请求取消并等待实际Run及响应/传输清理；失败保留原资源，原实例再次Dispose只重试清理，不重复HEAD。Run结果不是资源已释放。借用B无缓冲/所有权原则，不复制A简化模型当作生产；不得因HttpResponseMessage已disposed而丢失失败的实际内容/stream/handler。

VM使用网络领域独立、有界测试队列（一个实际运行及一个最新等待意图），重复同一修订不得无限积压。只有显式测试点击创建会话；字段/目标变化取消并撤销旧结果发布资格，仍等待其真实收尾。保留原session直到Dispose成功；正常完成且身份仍有效才发布最终结果。处理中、正在结束、失败、安全结果、重试结束入口可见；关闭已封口后原清理重试仍可用。

保存、Reload、放弃、关闭和退出等待手动清理重试的全部UI发布，不能只检查session字段是否清空；避免T-032B曾发生的保存/关闭跨过最后UI回调。重复关闭共享责任，失败保持原窗口/拥有者，不丢弃后再新建。控件通知始终在窗口Dispatcher执行。

Providers可用内部生命周期组合接到既有INetworkTransportLifetime，将全局manager与独立测试会话拥有者一起停止；保持网络observer仍指向真实manager，避免Coordinator/Tester/Observer的DI环。StopAcceptance同时封闭新测试和新逻辑发送；Settings等原拥有者先排空，随后完整网络Stop，最后Host/Dispatcher销毁。不得仅靠VM正常关闭来遗漏Host退出时仍存在的会话，失败责任可重试。此组合无需新Core端口。

## 6. 验证

实际SettingsWindow/VM/Core编辑协调器+临时JsonSettingsService验证全部模式/绕过/地址/凭据、当前非法文本、空密码、未编辑dormant值、同一计划多页保存、冲突/外部变化、配额/ApplyPending原回执、密码保留/清除、只读、测试与保存/关闭竞争。不要用mock保存器证明完整联合行为。

实际Tester/Source/路线及SocketsHttpHandler配ConnectCallback内存字节流验证HEAD、代理认证、无Provider认证/正文、状态/重定向/header预算、实际direct来源、草稿不改变全局配置或连接池；以控制时间/门闩验证超时/取消/非合作Run、每个清理阶段失败及再次重试、只Run一次、两个领域相同ID、相同ID端点外部替换、同session的延迟UI回调、窗口关闭再开与完整Host退出。

实际查看Light/Dark、正常/既有小工作区、长中英文地址/标签、无目标/非法字段/运行/结束/失败/清理失败/只读/保存待应用。画布匹配真实布局，密码遮蔽、文本可读、按钮可滚动到达。全部UI文案用既有UiText体系。

最终串行Release build零警告错误及全量test全绿；失败与未验证如实保留。不访问真实用户配置/密钥/代理/socket/云服务/剪贴板/输入，不跑性能脚本，保留既有获准SDK smoke。真实代理/TLS/SOCKS/Windows身份及NFR不冒称由内存流实测。只本地提交，不push。
