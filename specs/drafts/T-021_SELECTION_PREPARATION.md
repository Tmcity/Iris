# T-021 与后续选区读取的架构准备

> 2026-10-03，Codex。T-020B 实施期间的只读调研记录；不是已签发工单，也没有批准下列候选规则。T-020B 完成验收后才签发下一单。本文不修改契约、ARCH、默认设置或已验收实现。

## 1. 已核对的依赖

| 工单 | 当前事实 | 后续使用边界 |
|---|---|---|
| T-021A | 剪贴板基础服务已验收 | 复用 Win32ClipboardService；现有 IClipboardService 只有文本读取、文本/图像写入与序列号，没有选区多格式快照/恢复事务 |
| T-022A | DesktopContext、MonitorEnumerator 已验收 | 前台 HWND/PID 是短暂快照，不能作为稍后 SendInput 的永久授权 |
| T-021 | BACKLOG 待实施 KeyboardSimulator | ARCH 将其置于 Platform.Windows/Selection；未发现公开 IKeyboardSimulator 契约，不应先行创建新的 Core 接口 |
| T-022 | BACKLOG 待实施 UIA/剪贴板/组合读取器 | Core 已有 ISelectionTextReader、SelectionReadOptions、SelectionReadResult；A08 与 §10.4.1 明确仍需定稿 |
| T-023 / T-024 | Handler/划词设置与鼠标划词后续单 | 不在 T-021 提前接业务入口、启用未交付热键、实现 UIA 或鼠标 hook |

当前内部访问与原生声明的真实位置为 `src/Iris.Platform.Windows/DependencyInjection/PlatformWindowsServiceCollectionExtensions.cs`、`src/Iris.Platform.Windows/NativeMethods.txt`。B 正在新增捕获相关声明；T-021 应在 B 的最终版本上增量设计，不能覆盖其 native 适配器与生命周期成果。

## 2. 本轮 Microsoft 官方核查

2026-10-03 通过 Invoke-WebRequest 读取以下官方页面，均实际返回 HTTP 200；HTML 和提取后的正文保存在 `C:/Users/28910/AppData/Local/Temp/iris-t021-official-prep`。这是文档调研，不是产品运行/实机验证，也不是自动化测试联网。

| 文档 | 核实内容 |
|---|---|
| [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput) | 返回成功插入输入流的事件数量；受 UIPI 限制，返回值/GetLastError 不能证明失败原因就是 UIPI；同批事件串行插入且不会与其它输入交错；不重置既有键状态 |
| [GetAsyncKeyState](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate) | 最高位表示当前按下；最低位不可靠；返回零也可能源自非活动桌面、UIPI 等读取失败，不能单靠零证明可安全输入 |
| [GetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getforegroundwindow) | 返回当前前台 HWND；窗口失去激活等情况下可以为 NULL |

本轮缓存 SHA256：

```text
sendinput.html         32500F913A6E98F00BB015A9D5737ACBAB4CDCCDE432A461891B50AD6B00BC82
getasynckeystate.html  73FF770A7C309C9D70F5D0F0E96594E8F44DBC0504C6CC067E5894916E4296D9
getforegroundwindow.html 82B008CDB1A1B0A350BA19A1F00C5EC39040E6EB24EBA1DD12B0D399F2D6A3FD
```

由此得到的设计限制：事件全部插入不等于目标应用处理或完成复制；输入没有目标 HWND 参数。发送前再次核对前台只能缩短竞争窗口，不能把状态检查与系统投递变成原子操作。报告必须保留这个限制，不能承诺向指定窗口可靠定向发送。

## 3. T-021 签发前需固定的决策

下列是下一单架构师的决策清单，不要求 Gemini 在 B 实施。

1. **内部边界与副作用入口。** 候选方案为 Platform 内部 KeyboardSimulator 与窄 native adapter，只有明确调用才读键/发键，构造和 DI 零桌面 I/O；调用方提供可复核的目标身份/取消上下文。准确 internal 方法、结果和线程模型待签发定稿，避免把高层选区流程塞入键盘模拟器。
2. **修饰键未释放。** ARCH §10.4 旧步骤要求等待最多 500ms 后补发用户修饰键 KeyUp。官方“correct as necessary”不等于产品必须释放用户仍按住的键。候选方案是在超时后结束本次复制尝试且不注入，须先由架构师明确修订旧行为及其验收文字；不能靠实现静默改掉，也不能把 B 的“不补键”规则不加说明直接移植成 T-021 正式要求。
3. **发送前校验。** 至少区分取消、失去前台、无法确认来源与允许尝试；等待释放期间目标变化时不能继续沿用旧快照。进程黑名单只是排除条件，不能证明 Code.exe 等宿主里的集成终端安全；终端/焦点控件识别与高层 fallback 的完整策略留 T-022。
4. **结果与部分成功。** 检查 INPUT 布局和 cbSize、发送返回数量；零/不足数量不能报复制成功。不得凭最后错误码断言 UIPI。部分输入后的自有键收尾、是否允许重试和取消提交点必须定稿；不能盲目重发整组 Ctrl+C，也不能把所有当前按下键视为本程序所有而一律释放。
5. **异步、串行与取消。** 等待释放不能阻塞 UI；并发操作不能产生交错组合。发送前取消不发键，进入 native 后等待其实际返回并按既定所有权完成收尾；具体协议在正式工单给出，不能只让等待 Task 超时后任由副作用继续。
6. **验证。** 实际 KeyboardSimulator 配 fake native/fake time，验证左右 modifier、等待/超时/取消、目标改变、输入布局、返回数量和重复调用；不接用户键盘、不发送真实输入。物理按键/UIPI/应用兼容范围另列未验证，后续按人工验收安排。

## 4. T-022 必须处理的既有 A08

这些已在 ARCHITECTURE_AUDIT.md A08 登记，不是 T-020B 新发现的阻断项，也不把整套读取器挪进 T-021。

| 缺口 | 正式设计需要解决的内容 |
|---|---|
| 规范不一致 | ISelectionTextReader 注释要求快照后“清空”，ARCH §10.4 步骤未规定清空和随后序列号基准；须统一，必要时经 CCR 同步注释 |
| 自身清空造成序列变化 | 不能把本程序 EmptyClipboard 导致的变化当作目标已经复制，不能超时后返回旧剪贴板文本 |
| 外部更新与恢复 | 单纯发现 sequence 变化不能证明内容来自本次目标；恢复旧快照前必须检测外部更新，检查与写入之间也要避免竞争，不能无条件覆盖用户新复制 |
| 快照格式/预算 | 基础服务未实现多格式快照；格式列表、DIB/DIBV5、非 HGLOBAL/延迟呈现等资源协议及预算需要单独设计，不能声称可恢复任意剪贴板 |
| 终端识别 | 等待期间切换到终端、宿主内终端、来源无法确认时采用明确策略，不能因进程未命中黑名单就宣称安全 |
| UIA 超时 | Task.Run(...).WaitAsync(300ms) 不会取消底层 COM 调用；必须限制真实未退出调用数量并隔离迟到结果，不能每次超时再无限起线程 |
| 选区结果归属 | 保留来源身份、翻译 operation 和取消归属，晚到结果不能抢焦点或覆盖新窗口；复用已验收 T-015 机制 |

签发 T-022 前再核对剪贴板序列号/所有权/延迟呈现和 UIA 线程官方文档；本轮没有对这些资料作新的读取，不把历史概述算作本轮研究证据。

## 5. 当前完成范围

本轮完成依赖与源码声明核对、三份官方文档读取、候选决策清单整理。未编写或运行 KeyboardSimulator、未做输入注入、未更改剪贴板、未运行构建/测试或性能脚本。T-020B 仍是当前唯一实施工单，后续签发以其最终验收提交为基线。
