# T-037 独立验收

2026-10-08，架构师验收提交 `19e15f6748b71aad0886c926e02a9542a347aa9b`。结论：通过，T-037 与 CCR-037 可结案。

## 最终证据

- `final-build.log`：实际 Release 构建，0 警告、0 错误。
- `final-full.log`：实际 `dotnet test Iris.slnx -c Release`，3624 总计、3622 成功、0 失败、2 项既有 window-station error183 环境跳过，持续 2m 27s 629ms。
- `final-probe.log`：六组独立实际组件探针全部通过。真实随机本机管道验证紧凑 Unicode 命令及 native 同会话检查；受控客户端迟读 ACK 时服务端未提前断开，Accepted 且接纳一次；迟到 Write/Accept 和取消后的部分 Read 不启动下一读取；真实 Generic Host 可同步释放未 Start 的实际 Coordinator。
- 探针只在自有进程使用单 worker 运行配置，未启动 Host 检查在该 worker 上执行，使无谓 ForceYield 不依赖调度运气；不改变产品运行配置或机器设置，也不是性能测量。其它 fake 只替换 I/O 边界，真实管道使用新的随机名称。

最终探针原始关键输出：

```text
ACK_BUFFER disconnectedBeforeRead=0; accepted=1; result=Accepted
LATE_WRITE reads=0; writes=1; creates=1; disposed=True; result=Unconfirmed
LATE_ACCEPT reads=0; dispatches=0; disposed=True
LATE_FRAGMENT reads=1; exception=OperationCanceledException
UNSTARTED_HOST servers=0; transportComplete=True; exception=none
Independent IPC scenarios: 6; failed: 0
```

`candidate-probe-1.log` 是审查夹具缺少 System.IO using 的编译失败，没有运行产品探针。仅修正临时夹具后，`candidate-probe-2.log` 六组通过；最终提交重建后的 `final-probe.log` 再次全部通过。未修改生产或产品测试源码。

## 绑定与范围

`final-binding.json` 绑定当前提交、621 个 src/tests 文件、重建 DLL、探针的 5 个产品程序集副本及原始日志。候选指纹与最终源码全部相同；提交后重建含新提交版本信息，使用最终重建产物验证，不要求其 DLL 与提交前指纹相同。

独立全量生成的 48 张 `regression-network-visuals` 与 T-036C 已逐图验收的 `final-visual` 全部逐字节相同。未修改网络、OCR、翻译、历史与持久化算法、默认配置或包；Core 仅批准的 IpcCommand 注释/安全 ToString。既有 Host 测试只改用仍调用真实 AppHostBuilder 的随机身份包装器，注册断言加入唯一 Exit Handler，原业务断言保持。

开发期取消回归修正前实际 6 项失败、原源码、修正后结果均保留于 T-037.evidence。ACK 首轮测试尚未进入“已写未读”状态，不能称其证明了 ACK 丢失；显式有界缓冲后的真实延迟读取/超时测试及本次独立探针才是最终证据。

本轮未启动真实 Iris 主应用，未连接生产管道、用户配置/密钥、真实桌面输入或外网。多用户/RDP、实际原生提示框外观、物理桌面与 NFR 未验证，仍属 T-040。既有获准只读 native 与合成 SDK smoke 保持。
