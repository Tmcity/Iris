# 先手工验证Sandbox窗口能否打开

用户反馈双击wsb文件没有窗口。当前只读核对显示配置有效、共享目录存在、后台无运行实例，系统选定入口是WindowsSandbox.exe。已准备使用当前wsb0.8.107.0 CLI的显式启动与连接入口；保留原配置和系统关联。

1. 在宿主双击 `F:\Iris\artifacts\t039c\manual-verification-v022-20261101-1\Open-Iris-Sandbox.cmd`。
2. 该窗口依次打印 `list-before`、`start`、两次`share`、`connect`的实际退出码。成功请求连接后等待Sandbox桌面出现；CLI exit0不预记成窗口可见。
3. 若窗口出现，继续正式0.2.2首次安装清单。若未出现或控制台报错，把显示的步骤/退出码/错误原文反馈。完整日志在本单output下的launcher-*目录，已启动实例保持不动。
4. 若start已成功而connect失败，可双击同目录的 `Reconnect-Iris-Sandbox.cmd`，它只连接已记录且实际仍在运行的ID，不另外启动或终止实例。

本段只开启本单新Sandbox并共享input只读/output可写，不自动安装Iris或运行时、不预填下载缓存。实际操作由用户执行；脚本语法/参数已由本主会话检查。仍未收到实际0.2.2安装/合成数据反馈，0.2.3未发布或授权。
