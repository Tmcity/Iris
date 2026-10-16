from pathlib import Path
import hashlib, json

e = Path(__file__).resolve().parent
root = e.parents[2]
order = root / 'tasks/current_task.md'
original = order.read_bytes()
text = original.decode('utf-8-sig')
build = (e / 'final-release-1.log').read_text(encoding='utf-8-sig')
full = (e / 'final-full-1.log').read_text(encoding='utf-8-sig')
build_summary = build[build.rfind('已成功生成。'):].strip()
full_summary = full[full.rfind('  总计:'):].strip()
package_line = next(line.strip() for line in (root / 'Directory.Packages.props').read_text(encoding='utf-8-sig').splitlines() if 'Include="Velopack"' in line)
update = (e / 'guest-update-final-1/output/engine/result.json').read_text(encoding='utf-8-sig')
report = f'''# T-039B 开发交付报告（请求独立复审）

基线：`6256a3fba611b2cd94fc8dfa32480584c16d7bfe`。本单交付生产实现、实际包及本地隔离验证；没有 push 或公开发布。父 T-039 与后续 T-039C 由架构师结案/签发，M6 未推进。

## 实现

- Core 仅落实已批准的 CheckForUpdates 可选 init 字段及联合编辑复制/验证/合并/空计划。原构造/解构、UiLanguage、默认/schema 保持。窗口真实保存、readonly、外部合并、冲突、Reload/取消和 RetryApply 均有实际 VM/临时 JSON 验证。
- Updates 命名客户端复用网络快照与传输拥有者；实际 SDK source/downloader 限制 GitHub 来源、重定向、元数据/包预算和完整包准入。操作任务在通知前登记；取消同步封口；保存真实任务及最后通知；校验/Prepare 纳入同一拥有者。资源失败保留原对象重试，取消回调失败不遗弃 SDK/资源。
- 更新 UI 提供版本、偏好、状态及检查/下载/取消/安装并重启。自动检查独立于手动操作，关闭偏好仅取消自动检查；安装不隐式保存草稿。无关窗口不承担自动检查的清理失败。
- STA Main 保持原 Mutex 线程释放；fast hook/普通 CLI/主次实例分流；主实例禁 SDK 默认 auto-apply。外部维护拥有者握手、提交确认、维护门禁及实际原进程退出后才运行 native apply。
- Maintenance 为无 WPF/WinForms 的 self-contained 单文件；App/UiaHost 保持 FDD。安装结束后修复最终卸载项；普通卸载保留/删除/取消，静默保留；优雅退出、数据路径检查、同一 helper 失败重试和可恢复事务均已接线。
- 修正真实来宾暴露的后台线程销毁 WPF 托盘问题：同步 UI 清理回到原 Dispatcher；Host 停止失败不先移除重试入口。主错误与撤销清理错误分别保留。
- `build/pack.ps1` 分别发布/组合 App、uia、Maintenance；固定 SDK/vpk、检查版本和卫星资源；外层附加有界长度/SHA 的内部 Setup。`staging-manifest.json` 保留发布输入，`payload-manifest.json` 从实际 full 包生成。CI/安装文档已准备，未执行发布。

固定包声明（原文）：

```xml
{package_line}
```

## 串行 Release / 全量

原日志：`T-039B.evidence/final-release-1.log`、`final-full-1.log`。产品源码在此后未改动；后续修改仅为证据夹具/报告。

```text
{build_summary}
```

```text
{full_summary}
```

两个跳过均为既有隔离 Window station 环境限制，原输出保留。没有跳过新增失败。定向原始日志还包括 `update-owner-close-1.log`、`updates-proxy-1.log`、`maintenance-final-candidate-1.log`、`control-pipe-1.log`、`update-ui-2.log`。

## 实际包与来源绑定

最终验证包目录：`artifacts/t039b/release-0.2.0-final-3`、`release-0.2.1-final-3`。明确使用系统 Windows PowerShell 5.1 的日志为 `pack-ps51-final-0.2.0-3.log` / `pack-ps51-final-0.2.1-3.log`。包装器记录实际 PSVersion/PSEdition/ProcessPath，临时目录仅对本次进程与子进程指向 F 盘。

`package-final-3-check.json` 核对两版实际载荷集合/大小/SHA 和顶层哈希；架构师 `T-039B.review/package-final-3-observation` 也独立关闭了旧清单偏差。vpk 的 Run-in-helper 位置建议及未签名警告如实保留，不等同 Release 编译警告。当前 Windows 包未签名。

`review-final-candidate-1.json` 固定源码与 Release DLL；`final-guest-binding.json` 将来宾最终安装文件与实际包内字节相连。`delivery-binding.json` 汇集源码、包、检查日志、图像及归档工单指纹。包、工具、转储和官方运行时仅保存在 artifacts，不提交这些二进制。

## 实际 Windows 验证与执行方式

均为自有 Windows Sandbox，映射专用只读输入/可写输出；未在宿主运行实际 Iris/安装器。来宾记录显示 Windows 11 Enterprise 10.0.26100、WDAGUtilityAccount、Administrator=true。系统标准按钮的语言沿用既有本地化边界。

1. `guest-install-5`：安装前无共享 .NET10，真实外层→native Setup→Microsoft Desktop Runtime→真实 Iris/UiaHost；安装不启动，实际英文设置窗口，运行中静默卸载，原 App/外部 helper 真实退出、事务结束、保留数据与共享运行时。架构师已独立核对。
2. `guest-matrix-4`：中英文保留/删除/取消、保留后重装、锁住合成数据后原 helper 重试。架构师已核对功能断言及图像；其不完整中文完成旧图保留。新稳定图为 `guest-coverage-final-2/output/full-data-delete-completed.png`，已实际查看正文与按钮。
3. `guest-coverage-final-2`：真实 JSON、DPAPI 合成秘密、SQLite 历史、日志在保留卸载/重装后均可读取且记录文件指纹；删除后标准数据目录及这些文件不存在。本安装 Run 值移除、非本安装值和无关见证值保留；桌面/开始菜单快捷方式移除。这一脚本随后在准备坏包时因映射目录的排他文件打开失败，**整轮仍保留失败**，不将已验证子项扩大为整轮成功。
4. `guest-remaining-final-1` 单独完成该剩余段：预制真实外层坏校验包返回失败，实际 InvalidDataException 可见于安全结果记录，无程序/卸载项/事务副作用；实际安装确认取消，原入口退出且未安装。没有重跑已通过的数据段来掩盖夹具失败。
5. `guest-committed-failure-final-1`：实际 UpdateTransaction 与同一产品 MaintenanceFeedback，原 App 已退出后才显示可读失败/恢复路线，事务保留，官方安装器能修复并解除门禁。此场景的 native 外部边界明确替换为自有 PowerShell 子进程退出失败码；**不是宣称真实 Update.exe 在现场失败**。原生子进程等待/同 helper 重试的旧独立失败与修后独立日志另见 review。
6. `guest-update-final-1`：使用最终实际 full 包进行本地双版本文件应用；检查原 App 正常退出、控制父进程真实退出前不推进、native 返回、版本/注册核验、只重启一次、实际新设置窗口、数据仍可读取，最后新 App 正常退出。其原始引擎结果：

```json
{update}
```

GuestUpdateHost 只将控制父进程的安装路径验证替换为本次真实 PowerShell worker，真实管道/PID/开始时间、产品事务和 native apply/校验/注册/重启仍执行；这与正式 App Main/用户公开源链路的差别保留在源码和结果中。C 仍须完成公开 GitHub 源的真实用户链路，不能把本地宿主结果描述为线上升级验收。

最终来宾 `final-state.json` 记录无 Iris/Setup/Update 进程及安装指纹；`guest-update-final-1/stop.log` / `list-after.log` 记录已停止自有 Sandbox，列表为空。没有把 fixture 终止失败环境记作产品清理。

## 语言与逐图

`update-ui-2` 由真实 Window/VM/SDK 控制边界执行检查、下载与 Ready，覆盖中英文/明暗/正常与小窗口；每张图已在原尺寸审阅页查看，见 `review/reviewed.json`。最终来宾图像见 `final-guest-binding.json`，全部实际查看。

审计覆盖 src C#/XAML 与 App/Core/Maintenance 资源；`localization-audit-after-comments-2.log` 无未说明命中或资源错误，仅新增两处 CCR XML 注释的精确例外。`localization-exception-probe` 实际重扫 baseline/UI 借用日志/重复站点，后两者仍被拒绝。没有放宽 path/text 例外。

## 失败历史与证据性质

所有原始失败、架构师 review 和较早候选均保留，不覆盖为绿色日志。主要条目：

- 初始安装外层 Magic 长度错误、vpk 工具下载中止、登录就绪/控制器管道问题、运行时下载超时、PS5.1 C 盘临时空间不足、旧 staging/实际载荷清单差别。
- 来宾真实退出失败由实际转储定位到 Dispatcher.VerifyAccess；原进程/转储/DAC 和终止方式单列，修后实际来宾完整退出通过。
- `guest-update-1` 原生更新完成且曾观察新 PID；外围输出管道等待后代 EOF，诊断 Exit 后原最终重启断言失败。原轮仍失败，后续无干预的新轮通过。独立观察不被写成已经实测的句柄归因。
- 夹具完成按钮 ID、CIM 瞬时路径/只读属性、数据目录初始化、映射文件独占打开、主题字典在 Show 后修改等失败均有原始结果/修正后新标签；不当作额外产品失败，也不忽略原结果。
- 新干净来宾 `guest-final-install-1` 的外围在保护期限失败；原安装器/Setup 没有重启，之后通过新取得的原进程句柄记录两者真实退出。超时和迟到完成分开保留，见 continued-install-result。随后才运行最终包场景。
- 架构师 source-review 的未运行意见与已独立复现反例区分；`update-final-cancel/window-1` 因过期未获授予，results 为空，不能算探针通过。

## 与工单的偏差及限制

没有增加 Core 公共更新端口、错误码、默认/schema 或无关依赖版本。必要测试夹具变更包括真实 UI DI 注入、Main 测试隔离、来宾拥有者/完成判定、原 Dispatcher 清理回归及本地数据宿主，均服务本单验收。架构师已有 BACKLOG/规范/契约/交接改动原样保留随单提交；开发未改 BACKLOG 状态。

真实环境只覆盖上述管理员 Sandbox；标准用户拒绝系统运行时提升、要求系统重启的 Microsoft 安装器分支、跨用户安装及真实便携目录操作**未实测**。便携/来源/会话/路径守卫有实现与受控测试，但不扩大为这些平台场景已通过。GitHub 发布/匿名线上升级依约留 C，物理环境/NFR未运行，未提前 T-040/M6。

## 架构师工单全文

以下为收尾读取的 current_task 全文；原始字节另存 `T-039B.evidence/signed-order.md`，SHA256 `{hashlib.sha256(original).hexdigest()}`。

---

{text}
'''
(e / 'signed-order.md').write_bytes(original)
destination = root / 'tasks/done/T-039B.md'
assert not destination.exists()
destination.write_text(report, encoding='utf-8')
assert text in destination.read_text(encoding='utf-8')
print('Archived complete signed order and evidence-based report; current_task retained until staging verification.')
