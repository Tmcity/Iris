# 独立早断开探针

在独立临时目录复制本目录、引用固定程序集，以 `CandidateDir` 指定程序集目录；不引用/重建生产项目。运行前确认开发的全量已经结束、没有其它活动构建/测试进程；每步重新检查。

两种场景均用本进程自有随机真实管道：服务器已经创建但由门闩阻止首次 Accept；客户先连上，分别不写数据或只写不含换行的截断帧并完全关闭；此后才放行服务器，并由实际 NamedPipeIpcClient 发送一次正常命令。要求原服务器所有权保持、第二条正常命令仅接纳一次及首次完整 Stop 成功。门闩在 finally 放行，实际 I/O/取消和清理都被等待；真实保护令牌防止夹具永久挂起。

2026-10-10 已实际运行。`../ipc-independent-baseline-build.log` 和 `../ipc-independent-candidate-build-1.log` 均零警告错误，使用空 NuGet 源和已安装缓存。源码完全相同，分别引用 `baseline-binding.json` 与 `candidate-binding.json` 的六个固定程序集副本。

- `../ipc-independent-baseline.log`：两个场景均 NextResult=Unavailable、Dispatches=0、首次 Stop 失败，Accept 的 HResult=-2147024664（0x800700E8）；原服务器第二次清理成功，进程 exit 1。
- `../ipc-independent-candidate-1.log`：两个场景均 NextResult=Accepted、Dispatches=1、Action=InputTranslate，首次与最终 Stop 成功、竞争者被拒绝、ServerCreates=1、Errors 为空，进程 exit 0。

这独立确认候选生产修正的早断开行为，不代替仍在审查的 NamedPipeIpcTests.Guard 夹具或最终提交复验。不运行真实 Iris、不连接生产管道、不接触用户数据或外部网络。原失败、固定程序集指纹和全部命令/原始输出均保留。
