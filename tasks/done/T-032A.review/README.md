# T-032A 最终独立验收

提交83787af04d77d06ccb46d0fac708b5e683771716，2026-10-07。

独立Release零警告/错误，12.75s；全量3301/3299成功/0失败/2既有窗口站error183环境跳过，44.318s。完成后prepare.py重绑最终Core测试输出，commit13、draft13、credentials11、resources1共38项全部通过且进程正常退出。

实际SettingsEditCoordinator与OcrProviderRegistry，存储/SecretStore/Provider为合成窄边界。覆盖联合一次提交、未编辑最新值、非法值先于暂存拒绝、提交内冲突回滚、同ID跨域凭据隔离、禁用peer共享显式/legacy引用保留、ApplyPending原回执不重stage、提交后取消、入口冻结；草稿构造无I/O、仅Test一次、不Recognize、不持久化、禁用草稿、取消/非合作任务等待、原快照清理重试不重测、取消和清理错误、关闭后资源回收正对照。

源码与开发final-source指纹一致；DLL为提交后独立构建，以manifest为准，不冒称提交前DLL戳相同。旧纯划词UI夹具只补OCR Registry替身，原零副作用断言保持；之前add_Changed诊断不属于真实密钥读写。

A仅基础不含设置UI；T-032B仍须完整页面/保存/测试清理与关闭集成。不声称真实云鉴权、用户数据或NFR已验证。默认/schema/包和已验收后端不改。.codex保持预存未跟踪。
