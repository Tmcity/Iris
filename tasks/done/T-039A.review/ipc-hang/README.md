# 默认环境全量挂起：离线转储复核

仅分析开发已取得的 `artifacts/t039a/ipc-hung.dmp`，不再附加或修改正在运行的测试进程，不启动构建/测试。禁用符号服务器，原始命令和输出逐次保留。

后续收尾：开发记录 `T-039A.evidence/ipc-hung-termination.json`，重新核对原测试进程后终止它。`final-full-2.log` 的最终摘要为失败，退出码 -1；command.json 的 Windows 无符号返回值为 4294967295。架构师 18:20 只读查询确认原父子进程已退出。保留 Unsupported marker type 0x46 等所有原始输出，不把中止视为正常测试完成。

`dumpasync-1.log` 是本机 SOS 不支持 `-waiting` 选项的诊断命令失败，exit 1；不是新增产品失败。按实际 help 改用 `dumpasync --type NamedPipe --fields` 后，`dumpasync-2.log` exit 0。

## 实际观察

- 挂起链为 `NamedPipeClientStream.ConnectAsync` → `NamedPipeIpcClient.RunCoreAsync` → `NamedPipeIpcTests.Fixture.Send` → **`TruncatedWireDisconnectDoesNotDispatchAndNextConnectionStillWorks`**。它不是已报失败的 `ClientSessionMismatchRejectsBeforeAnyPayloadWrite`。
- 转储时当前客户端 `startedWrite=false`、`_closing=false`、`_cleanup=null`，仍在等待第二次连接；未写第二条有效命令。
- 该测试 Fixture 的 Server 对象 `0000021e85c04180`：`_connection=null`、`_cleanup=null`、`_started=true`、`_closing=false`、`_loopJoined=false`、`_disposed=false`。
- 同一个 Fixture 的 Factory：Servers=1、Clients=1、ClientWrites=0、Disconnects=0，所有身份拒绝/丢回复/部分写/延迟读注入标志均为 false。测试第一条截断消息使用裸 NamedPipeClientStream，所以不计入该 Clients 数。
- Server 的 `_loop` 为 `0000021e85c04378`，带完成 sentinel；其 exception holder 的 `m_faultExceptions` 非 null，`m_cancellationException=null`、`m_isHandled=false`。这表明当前服务循环已经故障结束，尚未由 Stop 观察，并非仍在成功接收后续消息。
- 服务器底层 NamedPipeServerStream 仍保有原 handle，转储字段 `_state=0`；本次 Factory 从未调用 Disconnect。客户端取消源 `_state=0`、`_cancel=null`，期限来自同一 FakeTimeProvider。

原始对象记录在 `objects-1.log` 至 `objects-5.log`。这些观察把挂起限定到**第一条截断连接之后服务器循环已故障、第二条客户端连接未完成**。服务器内部把异常转换为安全通用错误，现有转储观察尚未给出原始 Win32 错误码；不能据此冒称 ERROR_NO_DATA 已在本机确定性复现，也不能断言与另一个清理失败测试完全同因。

下一步以自有真实管道门闩固定“客户端先连接/关闭、服务端后进入 Accept”的顺序，并保留失败操作/类型/HResult。测试夹具还需有真实取消/收尾保护，避免 FakeTime 未推进时再次无期限挂起；不能用保护期限替代修复或修改原业务断言。

## 后续确定性开发实证

18:26 已读取 `T-039A.evidence/ipc-early-before.log` 及新增 IpcEarlyDisconnectTests 源码。开发实际输出：`EARLY_NATIVE_ACCEPT type=IOException hresult=800700E8 connected=False`、`EARLY_NATIVE_MANAGED_RESET type=InvalidOperationException hresult=80131509`；两个产品门闩场景都记录 `EARLY_PRODUCT_ACCEPT ... hresult=800700E8 servers=1`，第二次连接 Unavailable 且清理失败。原始 3 项全失败、exit 2，生产源码未修改。这补足了上文转储未能提供的原生错误码，但不把开发实证称作独立复验。
