from pathlib import Path
import hashlib
import json

root = Path(__file__).resolve().parents[3]
evidence = root / 'tasks/done/T-038-R1.evidence'
order = (root / 'tasks/current_task.md').read_text(encoding='utf-8-sig')

def read_log(name):
    data = (evidence / name).read_bytes()
    return data.decode('utf-16' if data.startswith(b'\xff\xfe') else 'utf-8-sig').replace('\r\n', '\n').strip()

def block(value):
    return '\n```text\n' + value + '\n```\n'

def summary(name):
    text = read_log(name)
    return text[text.rfind('测试运行摘要:'):]

before = read_log('deterministic-before.log')
build = read_log('final-release.log')
report = '''# T-038-R1 开发交付（请求独立复审）

2026-10-10。基线 `9c1ac9d0a044e2f844e82957c841b785da9d88e5` 保留，本单创建新的本地提交，不 amend。T-038/CCR-038 尚待独立验收；完成后按用户要求停止推进，不开始 T-039。

## 工单全文

'''+order+'''

## 定位与修正

独立失败原件仍在 `T-038.review/final-full-1.log`。本轮没有发现需要修改产品合并算法的证据：真实 JsonSettingsService 已保存 en-US，但 Changed 发起的 VM 调度任务不在 UpdateAsync 的等待范围内。测试夹具 UiDispatcher 先 await UI 入口，再投递业务 callback，ApplicationIdle 不能证明两段之间的异步工作已经完成。

仅修改两个测试文件：

- `LocalizationTests.cs`：两次外部保存改用 ExternalUpdateAsync。触发前订阅真实 VM 的 InterfaceLanguageHint 变更信号和夹具返回的实际 Task；用已有 PauseWhen/Gate 阻止第二段回调，等待 Entered。首段验证单次 Idle 可以返回、临时 JSON 已保存 en-US，而未编辑草稿仍为 zh-CN。放行后等真实合并信号，并排空实际 Dispatcher Task，再执行原期望与后续冲突、Reload、取消、运行语言固定断言。
- `NetworkSettingsUiScope.cs`：只增加可选 InvocationStarted 观察事件，暴露 InvokeCoreAsync 的实际 Task。原两段 Dispatcher 行为和已有门闩保持；未订阅的其它测试没有额外等待。清理先解除门闩，再完整观察实际任务，最后取消订阅；主异常与清理异常均保留。有界等待失败时任务仍登记给外层夹具，外层释放资源前继续 join 原任务。

src、契约、资源、布局、设置格式及包零差异；全部原断言按同一测试内的文本和出现次数核对，未放宽或删除。没有新增 Sleep/Delay、跳过或重复跑到绿。

## 确定性验证及原始输出

修正前实证保存在 `deterministic-before.cs.txt`、`dispatcher-observer.cs.txt`、`deterministic-before.log`。该探针在真实 SettingsWindow/VM、临时 JSON 和两段 Dispatcher 上保持门闩关闭，放置与旧测试相同的 en-US 期望，确定性失败。finally 放行并 join 后，实际草稿已经正确合并；以下为原始输出中的状态行：
'''+block('\n'.join(line for line in before.splitlines() if 'R1_BEFORE_RELEASE' in line or 'R1_JOINED' in line))+block(summary('deterministic-before.log'))+'''
修正后保留原 en-US 断言在实际回调完成之后，另增加门闩关闭时的旧草稿/落盘 JSON 断言。`deterministic-after.log` 首次构建因任务登记调用未显式接收返回值而触发 CS4014；已改为显式丢弃登记返回值，实际 Task 仍被完整等待。该编译失败不覆盖。修正后定向 `deterministic-after-2.log`：
'''+block(read_log('deterministic-after-2.log'))+'''
最终严格先 Release、后全量。`final-release.log`：
'''+block(build[build.rfind('已成功生成。'):])+'''
`final-full.log`：
'''+block(summary('final-full.log'))+'''
全量进程退出码 `final-full.exit.txt`：
'''+block(read_log('final-full.exit.txt'))+'''
全量两项环境跳过仍是既有隔离窗口站创建 error=183，完整原因原样保留。R1 新增行为只通过受控门闩与临时 JSON 验证，没有真实用户配置、输入、剪贴板、生产管道或 HTTP。

原断言次数与生产零差异证据见 `scope-and-assertions.json`；本次全量渲染与原已逐图查看产物的实际字节对照为：
'''+block((evidence / 'visual-comparison.json').read_text(encoding='utf-8').strip())+'''
图像按精确 SHA 继承既有逐图检查，本轮未重复宣称新的逐图检查。保留新增的全部独立构建、失败全量、审计、图像、绑定和 R1.review 原始快照；收到的 review 文件 SHA 在最终绑定时再次核对。

## 产物与范围

`T-038-R1.evidence/final-binding.json` 绑定最终源码/Git blob、真实验证程序集 SHA、R1证据与独立 review 文件。程序集为提交前验证文件；提交后重建的信息版本变化与源码绑定分别评估。旧 T-038 报告、全部失败和原始图像保持。

与工单偏差：无功能范围扩大；为完整等待 Changed 发起的实际 Dispatcher Task，增加了上述窄测试观察事件。其余测试和生产行为未改。未验证真实桌面交互、原生系统对话框、多屏和性能；没有运行禁止的性能脚本。既有全量中的只读 native/合成 SDK smoke 按原边界保留。

工单全文核对后移除 current_task；`.log` 显式纳入，`.codex` 不改删不提交，BACKLOG 不改。创建独立本地提交请求复审，不 push/reset/clean/rebase/amend。等待架构师独立验收；T-038/R1 结束后按用户要求停止，后续工作等待明确“继续”。
'''
target = root / 'tasks/done/T-038-R1.md'
target.write_text(report, encoding='utf-8')
assert order in target.read_text(encoding='utf-8')
(evidence / 'archived-order.json').write_text(json.dumps({'Sha256Utf8Text': hashlib.sha256(order.encode()).hexdigest(), 'ArchivedIn': 'tasks/done/T-038-R1.md', 'VerbatimTextPresent': True}, indent=2), encoding='utf-8')
print('Archived complete T-038-R1 work order and actual report.')
