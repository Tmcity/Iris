"""Archive the signed work order verbatim and paste actual validation output."""
from pathlib import Path
import hashlib
import json

root = Path(__file__).resolve().parents[3]
e = root / 'tasks/done/T-038.evidence'
order = (root / 'tasks/current_task.md').read_text(encoding='utf-8-sig')

def log(name):
    data = (e / name).read_bytes()
    return data.decode('utf-16' if data.startswith(b'\xff\xfe') else 'utf-8-sig').replace('\r\n', '\n').strip()

def summary(name):
    content = log(name)
    return content[content.rfind('测试运行摘要:'):]

def block(value):
    return '\n```text\n' + value + '\n```\n'

report = '''# T-038 开发交付报告（请求独立复审）

2026-10-09。基线 `19e15f6748b71aad0886c926e02a9542a347aa9b`。本文件保留完整签发工单及所有末尾补充；本地提交后的完整 hash 由开发回复提供。T-038/CCR-038 结案由架构师决定。按用户最新停止点，本单交付后停止推进，T-039 未实施，M6 延期。

## 签发工单全文

'''+order+'''

## 实施结果

- App/Core 双语资源完整接线，UiText 改为运行时 getter，错误/验证/启动/CLI/通知/可访问性和平台返回用户的消息资源化。显式运行语言独立于其它线程的环境 Culture；清除启动前静态文案缓存。AppResources 与 CoreText 的回退不相互递归；未知错误码、缺失资源和坏格式安全回退，不显示原始异常或秘密。
- 生产 Program 的主实例语言作用域包围路径/日志/Host/WPF、Host/日志器清理、Mutex 释放以及最后固定反馈。第二实例和非法参数维持零配置读取。真实 AppBootstrapper 加载后选定本次语言；保存、外部变更和 Reload 不切换本次语言。
- GeneralSettingsPatch 仅落地批准的可选 UiLanguage init 属性，原四参数构造/解构保留。Core 复制、校验、冲突、合并、空计划与 App 草稿/冻结/只读/取消/Reload/联合保存接通。仅语言修改正常 Applied、下次启动生效；原网络待应用回执与 RetryApply 不受影响。
- 稳定预设数据及用户保存名称保持，实际添加服务菜单按 preset.Key 提供显示标签。快捷键说明、提供者工具栏、高级复选框、凭据选项、长提示换行；窄翻译窗显示完整语言值，短窗口可滚动到结果正文与操作，标题操作保持可见。
- 提供真实 Roslyn/XAML 全仓盘点、实际编译资源解析、双语目录核对和精确例外清单。例外使用 Owner/Context/SiteFingerprint 与出现次数；同日志字串的新 UI 赋值不再借用旧例外。迁移输入保留，但不作为日常构建步骤重复运行。

没有更改配置默认值、N11、JSON/schema、依赖包、协议 ID、Prompt 英文语言名或翻译/OCR目标；无新增 Core 公共本地化端口。BACKLOG 的现有改动来自架构师，本轮开发未改变其状态。

## 实际验证与原始输出

最终顺序为 Release build 后全量 test；以下直接粘贴日志原文。构建日志 `final-release-4.log`：
'''+block(log('final-release-4.log')[log('final-release-4.log').rfind('已成功生成。'):])+'''
全量日志 `final-full-3.log`：
'''+block(summary('final-full-3.log'))+'''
两项跳过是既有隔离窗口站创建 error=183，完整原因在全量日志。整套仍包含既有只读 native 元数据查询及受控合成 SDK smoke，不能称为完全没有原生调用。

Core 语言定向 `core-language-2.log`：
'''+block(summary('core-language-2.log'))+'''
真实临时 JSON/Core/General 联合保存/SettingsWindow/启动、实际预设菜单和安全说明定向 `app-language-6.log`：
'''+block(summary('app-language-6.log'))+'''
生产主实例语言寿命与已有 IPC 启动回归 `primary-language-after.log`：
'''+block(summary('primary-language-after.log'))+'''
实际全仓审计与编译资源解析 `audit-delivery.log`：
'''+block(log('audit-delivery.log'))+'''
实际变异源码重新盘点 `audit-exception-delivery.log`：
'''+block(log('audit-exception-delivery.log'))+'''
审计命令、资源目录和例外规则见 `build/localization/README.md`；实际盘点与结果在 `audit-delivery`，反例完整临时源码副本/资源/结果在 `audit-exception-delivery`。非 UI 中文逐项清单在 `build/localization/exceptions.json`；英文仅语言本名“简体中文”为明确资源例外。

## 视觉验证与最终绑定

最终交付图是 `T-038.evidence/final-full-visual-3/manifest.json` 对应的实际全量运行产物。该清单逐项校验 PNG SHA 后，全部与已查看的 `visual-12` 完全相同。真实 Window/VM/资源、合成数据、调用代码触发菜单/滚动和 RenderTargetBitmap 渲染；不是手动点击、桌面截图或 VisibleText 清单代替逐图。

完整查看链：`visual-6-review` 已查看部分 → `visual-delivery-review` 查看 visual-7 的所有差异图 → `visual-final-review` 查看 visual-11 的所有差异图 → `visual-accepted-review` 查看 visual-12 的所有差异图 → `final-delivery-review` 全部精确 SHA 继承。每步的 contacts/carry/review-progress 均保留；“accepted”仅为开发视觉查看目录名，不是架构师验收。

覆盖两语言、Light/Dark、正常/小窗口：全部开放设置页、稳定展开的 OpenAI/Anthropic 高级项、实际添加服务与托盘菜单、翻译输入/进行中/结果/安全错误、历史空态/列表/全文详情、截图提示和通知正文。滚动序列允许画面边缘暂时截断，后续帧完整展示相应内容。小翻译窗结果与按钮另有可视命中检查；夹具不会触发实际剪贴板。通知正文来自实际通知服务的受控外部 sink，在自有窗口中排版，未验证系统原生通知气泡外观。

最终绑定 `T-038.evidence/final-binding.json` 包含实际源码 SHA/Git blob、验证程序集副本 SHA、全部证据 SHA、图像清单及查看链。程序集为提交前真实验证输入；提交后重建会改变提交版本信息，不要求 DLL 字节与提交前相同。保留收到的 T-037.review 文件，并核对收到时的 SHA 未变化。

## 失败过程与证据性质

- 架构师最初对生产语言 scope 的意见为源码预审。当时尚无独立运行失败；随后开发 `primary-language-before.log` 在真实 Program/DesktopStartupRuntime 作用域下实际复现 Host、日志器、Mutex 最后反馈回退中文，修复后通过。测试外层没有增加语言 scope 掩盖产品问题。
- 原审计遗漏是架构师已经实际复现的误通过，原件在 `T-038.review/audit-exception-before`。开发重新扫描的 baseline/UI mutation/duplicate-site 结果如上；独立修正复验在架构师 `audit-exception-after`，仍与最终交付审计区分。
- 架构师 `visual-findings-1` 与 `visual-findings-2` 是候选实际图像缺陷；原图原指纹保留。开发逐图另发现短窗口结果区过小、高级选项裁切和滚动条遮住按钮，均在最终图修正。
- 所有旧编译/定向/渲染失败日志保持，包括 Core 可空复制、测试日志注册和断言分析器、只读语言控件、外部冲突夹具、主题施加顺序、展开/弹出动画、布局类型引用，以及 visual-8 对禁用合成按钮使用输入命中的失败。后者改为可视几何命中，不启用真实复制。
- `final-full-1.log` 的实际失败是批准契约说明未逐字同步；已同步落地注释，契约声明未扩大。审计因此拒绝旧注释指纹，`audit-final-2.log` 保留；仅更新该注释的精确例外，次数仍为一。
- `final-full-2.log` 的实际失败是实际 ContextMenu 在等待动画期间被原生关闭后，夹具访问空 PresentationSource。夹具现在明确检查真实弹出窗口，并至多重新打开同一实际菜单三次；不替换成标签列表。产品菜单行为未因该夹具修复改变。最后全量通过，图像与此前已查看版本逐项完全相同。
- 架构师候选运行和接续材料，包括夹具编译警告、Popup/STA 退出超时、诊断与最终候选通过记录，全部保留于 T-038.review；这些是各自固定候选证据，不冒称本次最终独立验收。

## 与工单的偏差、边界与交接

实施范围没有扩展到新功能。既有 `OcrEntrypointScope` 增加可选窗口配置回调，用于在 Show 前设置主题；这是渲染夹具维护。新增渲染夹具采用动画等待和有界弹出窗口重开，设置/翻译布局调整用于满足英文与小窗口要求，均已纳入最终测试。没有削弱既有业务断言。

未验证：真实主应用启动、用户配置/秘密、真实桌面输入/选区/剪贴板、生产管道、HTTP/云请求、系统标准对话框/原生通知气泡、多屏物理操作和性能/NFR。按工单禁止范围未执行性能脚本。新增验证均使用自有窗口、临时文件和受控外部边界。

保留架构师 T-037 结案、完整 review、新规范/CCR、交接及本轮所有失败。`.log` 显式提交，`.codex` 原样未跟踪、不改删不提交。当前工单在全文核对后移除。本地独立提交，不 push/reset/clean/rebase/amend。请求架构师独立复审；开发到此停止，等待用户明确继续再推进后续任务。
'''
target = root / 'tasks/done/T-038.md'
target.write_text(report, encoding='utf-8')
assert order in target.read_text(encoding='utf-8')
(e / 'archived-order-sha256.json').write_text(json.dumps({'Path': 'tasks/current_task.md', 'Utf8TextSha256': hashlib.sha256(order.encode()).hexdigest(), 'ArchivedIn': 'tasks/done/T-038.md', 'VerbatimTextPresent': True}, indent=2), encoding='utf-8')
print('Archived complete work order, supplements and actual output in tasks/done/T-038.md')
