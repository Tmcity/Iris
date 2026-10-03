# T-021 独立验收准备

> 2026-10-04，Codex。T-021 已直接交给 Gemini；本文是对 KEYBOARD_SIMULATION.md 的期望数据整理，不新增工单范围，不表示实现已验证。

## 1. 独立事件期望

正常事件缩写：Dctrl、Dc、Uc、Uctrl。每个事件对应一次 cInputs=1 的实际 adapter 调用；返回 1 表示接纳，0 表示未接纳。后续 U 只释放本次已接纳或无法确认是否接纳的键。

| 场景 | 预期 native 调用顺序（括号内返回） | 最终结果 / 责任 |
|---|---|---|
| 正常 | Dctrl(1), Dc(1), Uc(1), Uctrl(1) | Injected；MayHaveCopied=true；无残留 |
| Ctrl down 失败 | Dctrl(0) | Failed；MayHaveCopied=false；不补 U |
| C down 失败 | Dctrl(1), Dc(0), Uctrl(1) | Failed；MayHaveCopied=false；不能补发 Dc |
| C up 失败、补偿成功 | Dctrl(1), Dc(1), Uc(0), Uc(1), Uctrl(1) | Failed；MayHaveCopied=true；不冒称撤销复制 |
| Ctrl up 失败、补偿成功 | Dctrl(1), Dc(1), Uc(1), Uctrl(0), Uctrl(1) | Failed；MayHaveCopied=true |
| C up 补偿仍失败 | Dctrl(1), Dc(1), Uc(0), Uc(0), Uctrl(1) | 仅剩 C；新 Copy=Busy；下一次 Retry 只 Uc |
| C down 抛错、结果不确定 | Dctrl(1), Dc(throw), Uc(1), Uctrl(1) | Failed；MayHaveCopied=true；只保守补本次可能欠的 U |
| Ctrl down 后目标变更 | Dctrl(1), Uctrl(1) | TargetChanged；MayHaveCopied=false；不发 Dc |

补偿每次只遍历一遍当前待释放集合，失败可再次 Retry/Stop；确认已成功的 KeyUp 不应重复。接口名称可依最终内部实现调整，但期望不根据待测实现反推。

## 2. 真实时序门闩

- Copy 已接纳且实际按键查询 entered，时间尚未推进：同时 Copy 返回 Busy，不能额外读取键或建立无界 Task。
- 起始 Ctrl/Alt/Shift/Win/C 未释放：10ms 轮询，500ms 仍未释放则 NotReady、SendInput 次数 0；解除后可由新的明确请求正常复制，不补用户 KeyUp。
- 开始后调用方修改原目标变量不改变已捕获的 struct 身份；等待期间 HWND/PID 变更返回 TargetChanged。
- 第一个 SendInput 实际 entered 后阻塞：迟到取消/Stop 不得让调用提前结束；放行后根据实际接纳完成本次释放与结果，不遗失 Ctrl down。
- 补偿实际 entered 时触发 Stop/并发 Retry：不能双重释放/丢失记录；Stop 永久关闭新 Copy，之后仅可清理。
- 同步 Dispose 在活动 native/遗留释放时明确失败，异步停止后同步容器收尾不再发输入。

所有门闩位于实际外部边界并确认 entered；finally 放行且 await 所有真正任务。不要把 Task.Run 外壳进入、返回了 Task 或短 Delay 当作副作用阶段已到达。

## 3. 被测装配与证据边界

真实 KeyboardSimulator + 真实 INPUT 构造路径 + fake native/FakeTimeProvider。应捕获实际 VK/flags/cInputs/cbSize；不能让 fake 凭默认 success 跳过真实实现，不能比较两份照抄的状态机。

普通 DI 构造/Host.Build/解析零 native；API 结果、并发、错误、清理责任和 ToString/日志均须核对。所有设置目录/窗口/模拟进程身份属于测试自身；不执行真实 SendInput、键盘读取、clipboard、hook/热键、用户配置或网络/性能脚本。

C20B-1 仅维护 R4 四个测试失败路径，不改变已验收生产行为；检查实际 finally 等待而不是仅追加注释。交付后再建立最终程序集指纹并运行定向/全量验证，不占用开发期间共享 build 输出。
