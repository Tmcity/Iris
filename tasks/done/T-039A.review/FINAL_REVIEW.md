# T-039A 最终独立验收

2026-10-13。结论：**T-039A实证阶段及补签IPC修正通过；父T-039仍未结案。** 正式交付报告为 `../T-039A.md`。本目录 README.md 及带时点的记录是执行期历史，本文件给出最终结论。

## 依据

| 工单要求 | 权威证据及结论 |
|---|---|
| 固定工具、隔离发布及包 | evidence的vpk/tool/publish原始命令，publish-inventory、package-inventory、source-bindings；独立只读audit及最终绑定确认24份源码、14个产物及helper/卫星一致 |
| 两版本真实SDK验证 | 独立以缓存依赖重建原型，sdk-independent-build-1零警告错误；sdk-independent-replay-1检查/下载/坏缓存准入/取消join与重试全部通过，未运行更新器 |
| Bootstrap与退出实证 | sdk-independent-bootstrap-result.json的13个子进程场景无失败，SDK退出/回调/auto-apply/包清理行为与固定源码一致；真实Process.Exit子进程不会执行finally |
| 更新网络与资源契约建议 | recommendation.md / ProposedContracts.cs.txt与实际源码已对照；正式实现必须复用现有代理、区分空发布源、持有全部真实任务与失败清理，不把原型包装器当作完整生产实现 |
| 安装与卸载可实现性 | 原生源码顺序、实际framework/runtime参数、不可交互的fast callback及外层维护入口建议已明确；未把Sandbox预检或替身当真实安装通过 |
| IPC附加修正 | ipc-early-before原始3失败；开发定向6项及最终SystemIntegration78项通过；同一独立探针基线2失败、候选2成功，原句柄/服务器所有权及停止得到验证 |
| Guard夹具收尾 | 当前源码先StopWork取消/释放/封口，再await真实body、最后枚举Clients；裸I/O接Guard。GuardSealsLateClientsAndJoinsBodyBeforeFinalCleanup进入最新78项及最终全量 |
| Release/全量与范围 | final-independent-release-20261013零警告错误；final-independent-full-20261013总3660、成功3658、失败0、既有环境跳过2，两个进程exit0。final-binding-20261013确认640项输入未变，仅批准范围发生生产/测试修改 |

最终生产只有 `IpcPipeFactory.cs` 的精确ERROR_NO_DATA恢复和 `NativeMethods.txt` 的DisconnectNamedPipe声明。NamedPipeIpcServer、CLI/Host、本地化资源、公开契约、默认/schema、包清单和正式发布脚本保持；新增/维护测试位于批准的SystemIntegration目录。

## 验收边界

原型真实使用SDK/本地文件，HTTP故障和进程启动边界是受控替身；锁门闩验证持锁阶段join，没有宣称跨进程锁竞争已通过。Iris正式更新接线、真实安装/运行时引导、卸载交互及数据清理仍未实施。正式流程须覆盖Host/日志/Mutex清理失败、交接期间再次启动及外层安装/维护入口的结果验证。

全部失败、第一次全量中止和测试/诊断工具错误保留。截断ZIP原件保存在artifacts，转储同样未提交。恢复报告如实说明开发会话服务繁忙中断了归档，由架构师补齐文档和本地提交；没有把待交付伪报为既有开发提交。

下一步进入正式T-039实现；M6继续延期，T-040尚未开始。公开目标保持 `https://github.com/Tmcity/Iris`，本次没有push或发布。
