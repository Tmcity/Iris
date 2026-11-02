# 手工Sandbox双击入口无窗口

用户反馈双击Iris-Manual-Acceptance.wsb无Sandbox窗口，因此此前“没问题”不作为实际安装/合成数据通过。正式0.2.2已发布且字节验证保持，0.2.3仍未授权/推送。

只读核对：705字节配置可解析，input/output两目录都存在；用户选择Windows.Sandbox，实际open command为C:\Windows\System32\WindowsSandbox.exe "%1"；vmcompute/hns运行；wsb0.8.107.0可用且实际list为空。不能据此断言整个系统Sandbox不可用，也不能把窗口未出现记成来宾启动通过。

准备可由用户双击的Open-Iris-Sandbox.cmd：以Windows PowerShell5.1调用支持的wsb start→分别共享本单目录→connect并记录各步退出码。保留原wsb文件、文件关联、系统功能和安全配置。创建唯一ID，先检查无其它实例；只共享本单input只读/output可写，不自动安装Iris/填运行时缓存、不操作宿主Iris数据或Key。错误写本单output/launcher-*/state与日志，并保持已启动实例供复核，不泛杀或清理用户实例。Reconnect-Iris-Sandbox.cmd仅连接记录的实际仍运行ID。

脚本/入口的语法与目录参数先检查；实际启动、是否显示桌面仍由用户执行反馈，CLI connect code0仅算连接请求完成。用户窗口可见后继续MANUAL_FIRST_INSTALL_V022_20261101.md中的正式下载安装和old-ready记录；C/父T-039与T-040/NFR不结案，M6延期。
