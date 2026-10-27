# 首个实际CI失败：定向诊断范围批准

2026-10-25 · 同一T-039C。已独立核对run40193113435的GitHub archive digest、51份原始解包字节和xUnit：3728 total /3726 passed /1 failed /1 skipped。唯一已运行失败为原生隔离剪贴板的占用/取消断言，不能用本地error183跳过否定它。package/publish skipped，未产生公开安装资产。

核准工程师提出方案的第1–4项：在自有Sandbox内对冻结修前产品/夹具做定向诊断，允许tests/Iris.Clipboard.IsolatedHelper和对应Platform Clipboard测试及必要自有构建接线。真实占用与服务线程须可验证处在同一自有站/桌面，记录TID/owner/Open/Close及取消/工作/join；先实际复现再修夹具，不放宽skip、删除断言、过滤失败或只延长sleep。宿主用户剪贴板不访问。若是产品缺陷，保留反例后再提最小产品范围，当前没有产品修正授权。

本地可准备0.2.1→0.2.2为下一版序列，release-plan的第一版PreviousVersion=null，避免依赖没有公开的v0.2.0；保存旧plan/源/包/CI失败，先查未占用版本。该版本选择是准备方向，不是发布授权。新source和候选到位后架构师再复核精确refs/tag；当前禁止新push/tag创建或推送、重跑workflow、修改/删除Release或草稿。已推v0.2.0/source保持永久原失败记录，v0.2.1尚未推。

之前执行轮01a172dc…已明确failed/internalServerError，随后工程师聊天出现用户“继续”的新轮01a17348…；本条发送到既有聊天继续原C，不创建新聊天或改变模型。没有自有建测/来宾任务在运行，窗口归还工程师。

当前唯一待工程师处理的是范围内诊断/新本地候选；不需要用户再确认仓库或代传。C/父单未完成，不能缩小无缓存默认引导和真实生产UI线上升级门禁。
