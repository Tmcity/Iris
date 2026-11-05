# 新会话：正式 0.2.2 首次安装

2026-11-05 实际查询没有运行中的 Sandbox。旧会话及其安装、合成数据、记录器证据继续保留；升级验收需要先在新的会话建立旧版实例。本入口只创建和连接 Sandbox，不安装 Iris，不准备运行时缓存。

1. 在宿主双击本目录 `Open-Iris-Sandbox.cmd`，等待 Sandbox 桌面。如果启动失败，保留控制台，反馈步骤、退出码及错误原文。
2. 在 Sandbox 的 `C:\IrisAcceptanceInput` 双击 `Begin-Manual-Evidence.cmd`。控制台应显示 `Ready: true`（JSON 的属性名为 Ready）。它启动本单虚构翻译服务和 V3 只读记录器，并验证自有辅助进程的启动/退出；辅助验证不能代替生产升级验收。该命令只运行一次。
3. 同目录双击 `Iris-0.2.2-Official-Download.url`，下载正式 0.2.2 安装器，手工执行安装。系统下载/安装运行时按实际提示操作。
4. 安装后从 Iris 托盘打开设置查看版本，双击同目录 `Save-Installed-State.cmd` 保存记录。反馈安装成功/失败、显示版本和异常原文。

此段只核对安装。保留 Sandbox 和 Iris 实例，之后再准备保存的更新开关、虚构凭据与历史，然后在新版本正式发布通过后分段验证升级及卸载。

正式安装器： https://github.com/Tmcity/Iris/releases/download/v0.2.2/Iris-0.2.2-Setup.exe

正式 Setup SHA256： `ddae984ba4bcc65e5398ecd7a036afe0485966c5d61fe9b1603cc9bb487e7a0c`

宿主输出目录：本目录 `output/session-*`。不要使用旧会话的进程 ID 或数据哈希证明此会话状态。
