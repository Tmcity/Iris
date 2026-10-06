# T-027架构师独立验收（2026-10-06）

绑定b3f1f1b67eafc20bab12290dba40051b4cc0ab51。独立Release构建0警告/0错误（12.95秒）；独立全量2722项2720成功/2既有环境跳过/0失败（41秒247毫秒）；独立actual预处理/WinRT内存codec/外部故障/实际Host注册40项全部通过。review-probe.log包含逐项结果，真实实例最终Dispose完成。

15张独立合成输入含100/101/102字节有效PNG。固定几何期望逐像素检查8组、alpha白底、格式和同实例、精确raw/Base64边界、损坏/元数据/非法约束恢复、并发身份、实际Host懒注册、存活实例输入/输出及byte[]的GC（全部False且对照False）、完整质量阶梯/同原像素、主+清理错误和原资源重试、六阶段取消、取消不覆盖真实故障、Stop完整等待及排队取消。外部PreparationBoundary只替换codec工厂/流事件，不复制产品状态机。

独立全量包含已逐行审阅的真实Bootstrapper三项App注册/退出测试（flush held/host不提前停止/清理失败仍执行独立收尾/原对象重试）；公开签名/record/枚举非注释内容相对基线不变。交付11份源码、11份证据散列一致，旧截图/剪贴板/包/default/schema无修改。

preparation-build.log和full-probe-preparation-build.log仅为较早准备编译；preparation-manifest是未交付草稿指纹。正式验收以review-build/full/probe及最终manifest为准，没有把预编译当产品执行。T-027及CCR-026验收结案。

真实用户内容、OCR识别效果、大图实机吞吐、混合DPI/HDR/NFR未验证；未跑真实Iris/用户剪贴板/输入/网络API/性能脚本。两项既有error183跳过和只读native smoke如实保留。未推送，开发轮已completed，原.codex工具保留未跟踪。
