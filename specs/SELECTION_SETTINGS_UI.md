# 划词基础设置与联合保存

> 2026-10-05，Codex；T-023B / ARCH v1.35 / CCR-019 正式规范。T-023A / 3bbd009 已独立验收。

## 1. 公开编辑契约

SettingsEditPlan 原 positional 构造与解构保持，增加 `public SelectionSettingsPatch? Selection { get; init; }`，默认 null，无编辑。

```csharp
public sealed record SelectionSettingsPatch(
    SettingsFieldEdit<IReadOnlyList<SelectionReadMethod>>? ReadOrder = null,
    SettingsFieldEdit<bool>? RestoreClipboard = null,
    SettingsFieldEdit<int>? ClipboardTimeoutMs = null,
    SettingsFieldEdit<IReadOnlyList<string>>? ClipboardUnsafeProcesses = null,
    SettingsFieldEdit<bool>? OpenInputWhenEmpty = null)
{
    public override string ToString() => "SelectionSettingsPatch";
}
```

这只扩展非持久化编辑计划，不修改 AppSettings/SelectionSettings JSON、默认值、schema、normalizer、Reader 或包。SettingsEditPlan 原 ToString 保持安全；新 patch 不能通过 record 默认打印整个 Base/Target 列表。

## 2. 捕获、验证与冲突

所有显式字段的 Base/Target 列表在 Core CommitAsync 首次 await/获取 gate 前独立复制。调用方在等待期间修改任一列表不改变已接纳计划。未编辑字段不复制成整节写入意图。Base 用来比较实际旧值，不按新 Target 规则修复或丢弃历史非法项；可保留 null/无效枚举/条目以便用户修复旧配置。

Target 规则只应用于显式字段：

- ReadOrder 非 null，长度 1 或 2，仅 UIAutomation/Clipboard，不重复，顺序有意义；不得静默去重/过滤未知值后称保存成功。
- ClipboardTimeoutMs 为 [100,2000] 整数，不在编辑提交时 clamp。
- 两个 bool 按目标直接赋值。
- ClipboardUnsafeProcesses 列表非 null，可为空；最多 256 项，每项 Trim 后为 1–255 UTF-16 单元的实际文件名，不能是 `.`/`..`，不得含控制字符、`< > : " / \\ | ? *`，不能以点结束。不要求或自动添加 .exe，以 Windows 返回的真实文件名匹配，提示示例为 app.exe；允许内部空格和其它合法扩展名。
- 进程目标列表按 OrdinalIgnoreCase 去重并保留首项输入大小写/顺序，空/null 元素为输入错误。UI 按行编辑时先去掉空白行，用户全部清空可生成合法 []。不把完整路径自动变为文件名或把通配符当匹配式。

显式 Target 无效：Uncommitted / IsConflict=false / InvalidArgument，固定安全文案，不输出原始路径/列表/整份 settings。未编辑的遗留坏字段不妨碍其它字段/页面保存。输入错误及初次冲突在凭据 staging 前零副作用。

字段三方比较：Current 既不等于 Base 也不等于规范化 Target 才 conflict。ReadOrder 按序精确比较枚举；UnsafeProcesses 运行时按忽略大小写集合比较，顺序/重复不构成差异，但不能 Trim Current/Base（原实现匹配实际名字，没有 Trim；` cmd.exe ` 与 `cmd.exe` 不等价）。Current/Base 的 null 列表与 [] 不等。其它字段按值。

前置初检与实际 UpdateAsync mutate 内均做同样比较。仅在最新 Selection 上覆盖明确编辑字段；MouseMode、RequireIBeamCursor、MinDragDistancePx、IconAutoHideMs、ExcludedProcesses 及其它未编辑字段保留最新值。Current 已等于 Target 可幂等完成，不制造无意义版本/Changed；无字段编辑保持原 no-op 行为。

所有 General、Provider/排序/凭据、Hotkey、Selection 编辑只有一次 Core UpdateAsync 原子边界。staging 后冲突或提交前取消保留旧设置并回收本次暂存；提交后取消保留已保存事实，按既有 Provider 刷新/清理结果报告。Selection-only/Selection+General+Hotkeys 不读取/写入/删除凭据、不刷新 Provider、不占其 pending receipt 配额，也不开始选区读取/按键/UIA。

## 3. App 设置草稿

启用 pageKey=selection，对应“划词翻译”基础页。推荐四种读取方式选择：UI 自动化优先 / 模拟复制优先 / 仅 UI 自动化 / 仅模拟复制，覆盖全部合法非空 ReadOrder；未编辑无法表示的历史值不得在页面打开时自动修复或变 dirty，用户显式选择后才生成补丁。

显示复制后等待时间（毫秒）、恢复剪贴板、模拟复制排除进程（每行一个文件名）、无选区时打开输入框。用户文案说明：读取不到时如何处理；只有确认复制安全的控件才会模拟复制；检测到外部新复制时保留新内容；关闭恢复会让模拟复制改变剪贴板。不开启鼠标模式/排除项，不增加读取测试按钮，页面加载/导航/草稿变更不调用 Reader/native。

超时输入采用可验证的文本草稿。空/非整数/超限不能由 WPF binding 吞错后保存旧值；提交前 flush 当前控件绑定，再检查完整目标草稿。错误在页内/共享状态可见，未保存值保留。UI 不直接 UpdateAsync，使用既有 GeneralSettingsSaveCoordinator → Core 编辑计划。

五字段纳入 IsDirty、no-op 检查、同步 UI 捕获、保存/重试/停止冻结、外部 Changed 合并、所有已保存分支基线更新、失败草稿保留、Reload/关闭/新 scope。干净字段随最新 Current 更新；dirty 保留原 Base/Target，保存时检测冲突。保存等待期间所有新旧页面冻结，解冻在 UI 收尾完整完成后；不要分别保存某页，不能绕过现有排队/退出屏障。

新增 SelectionTranslate 快捷键草稿行（A 已开放，但现页只有 Input/Clipboard/OpenSettings），使用现有 HotkeyItemDraftViewModel/Recorder/暂停与冲突校验，参与同一次保存。不得改默认 Ctrl+Alt+D、录入原生协议或添加额外 hook。关闭时仍遵守先结束录入与清理后释放设置窗口。

最小 600×440 与普通尺寸、Light/Dark。内容区纵向滚动，页内错误和长列表不会横向裁切，底部保存/关闭和状态可用。保持既有其它页面布局与导航，新增字符串走 UiText/当前资源约定。

## 4. 验收

Core 实际协调器（fake 仅外部边界）：五字段与全部输入错误；两列表 Base/Target 同步捕获；same-target 幂等、同字段冲突/不同字段合并，初检与 mutate 后冲突；未编辑历史坏值、合法修复；保留未编辑鼠标/其它节最新值；Selection-only 无凭据/观察者/回执；混合全部页面一次更新，staging 后冲突/取消回滚、提交后取消保持保存。

App 实际 ViewModel/Window/协调器：合法与无效文本草稿/焦点 binding flush；dirty 外部更新；保存、清理重试与关闭冻结/真实等待；成功及已保存待应用均推进对应基线，失败保留草稿；窗口重开加载持久值；快捷键行真实绑定现有录入协调器（fake native）。从新设置保存到实际 SelectionTranslateHandler 验证新 options，已经接纳的旧读取保持它的快照；禁止真实读取器/native。

实际离屏 WPF 渲染四组以上（两主题×最小/普通，至少长列表与错误态），使用 App 同款资源、正确等待布局/动画，并检查实际图片；不把代码触发描述成人工点击。所有闸门 finally 先放行再等待资源，保留主/清理异常。

串行 Release build 与全量 test；两项 error=183 跳过真实记录。禁止用户桌面/真实输入/剪贴板/hook/配置/密钥/Run/HTTP/性能脚本。既有设置测试一次性失败仍保留，若复现先记录 fixture 和异步查询时序，不靠扩大生产修改掩盖。
