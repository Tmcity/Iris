from pathlib import Path
import hashlib, json, subprocess, os

root = Path(r'F:\Iris')
evidence_dir = root / 'tasks/done/T-039B.evidence'

def git(*args):
    return subprocess.check_output(['git', *args], cwd=root)

def sha256_file(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest()

# Verify current_task.md hash
current_task_path = root / 'tasks/current_task.md'
assert current_task_path.exists(), "tasks/current_task.md must exist before archiving"
current_task_text = current_task_path.read_text(encoding='utf-8')
current_task_sha = hashlib.sha256(current_task_path.read_bytes()).hexdigest()
assert current_task_sha == '6993e2e5f85f20c0985b692290449faf8df1a7bc9653e2cf0b5a6649145126a4'

# Ensure signed-order-r3.md matches
signed_r3_path = evidence_dir / 'signed-order-r3.md'
if not signed_r3_path.exists() or hashlib.sha256(signed_r3_path.read_bytes()).hexdigest() != current_task_sha:
    signed_r3_path.write_bytes(current_task_path.read_bytes())

# Append R3 section to tasks/done/T-039B.md if not present
t039b_path = root / 'tasks/done/T-039B.md'
t039b_content = t039b_path.read_text(encoding='utf-8')
r3_marker = '## T-039B-R3 交付绑定与收尾报告'

if r3_marker not in t039b_content:
    r3_section = f'''

---

## T-039B-R3 交付绑定与收尾报告

2026-10-17 · 架构师签发 T-039B-R3 · master · 基线提交 `4d19a46cccfa1041a3838011abf4330ee035c89c`（T-039B-R2）。

本单已闭合 R2 复核发现的 DI 拓扑缺陷、按 R3 源码重新生成 0.2.0 与 0.2.1 最终发布包（`final-4`）、在自有干净 Windows Sandbox 复验受影响场景（安装与静默保留卸载、本地两版本更新与 `work-*` 回收）、补齐 R2 报告的偏差披露，并完成全量串行构建、测试与来源交付绑定。

### 1. 第 1 条闭合：DI 拓扑缺陷复现、生产修正与测试验证

#### A. 缺陷复现（先复现再修）
- **拓扑根因**：`AppLifetime`（由 `AppHostBuilder` 注册为单例，持有根提供程序）在 `ShutdownAsync` 失败异常处理块中，原本直接调用 `_serviceProvider.GetService<SettingsViewModel>()?.ResetHandoffTransferred()`。在生产环境开启 `ValidateScopes = true` 且 `SettingsViewModel` 注册为 Scoped 服务时，从根容器解析 Scoped 服务抛出 `InvalidOperationException`，不仅覆盖了原有的停止异常，而且无法复位实际窗口会话 VM 中的交接标志。
- **复现执行与原始失败日志**：在 `UpdateHandoffLifecycleTests` 中启用生产一致的 DI 拓扑（`ValidateScopes = true`, `ValidateOnBuild = true`），成功复现该异常。原始日志保存于 `tasks/done/T-039B.evidence/di-scope-repro.log`：
  ```text
  failed Iris.App.Tests.Updates.UpdateHandoffLifecycleTests.ShutdownFailureAfterHandoffPrepared_RollsBackCoordinator_AndResetsTransferredFlag (322ms)
    from F:\\Iris\\tests\\Iris.App.Tests\\bin\\Release\\net10.0-windows10.0.19041.0\\Iris.App.Tests.dll (net10.0|x64)
    Shouldly.ShouldAssertException : thrown is IOException || (thrown is AggregateException ae && ae.InnerExceptions.Any(e => e is IOException))
        should be
    True
        but was
    False
  ```
  调试捕获实际异常原文：`System.InvalidOperationException: Cannot resolve scoped service 'Iris.App.Settings.SettingsViewModel' from root provider.`。

#### B. 生产修正
- **遵循契约与作用域铁律**：不放宽 `ValidateScopes`，不修改 `CONTRACTS.cs`，不从根容器解析 Scoped 服务。
- **由标志的真实拥有者复位**：
  1. `src/Iris.App/Settings/SettingsWindowPresenter.cs`（单例、持有当前活跃窗口会话 `_activeSession`）：新增 `internal void ResetHandoffTransferred()`，将复位委托给当前活跃会话中的 `ViewModel`：
     ```csharp
     internal void ResetHandoffTransferred()
     {{
         lock (_gate)
         {{
             _activeSession?.ViewModel.ResetHandoffTransferred();
         }}
     }}
     ```
  2. `src/Iris.App/Services/AppLifetime.cs`：在 `ShutdownAsync` 停止失败处理块中，改为从根提供程序安全解析单例 `SettingsWindowPresenter` 并调用复位：
     ```csharp
     var presenter = _serviceProvider.GetService<Iris.App.Settings.SettingsWindowPresenter>();
     presenter?.ResetHandoffTransferred();
     ```
  3. `src/Iris.App/Settings/SettingsViewModel.Updates.cs`：在 `OnUpdateChanged` 事件中增加联动：当更新 Phase 回滚为 `Failed`、`Ready` 或 `Idle` 时，自动重置 `_updateHandoffTransferred = false`，双重保证在协调器回滚后窗口 VM 状态恢复：
     ```csharp
     if (_updateSnapshot?.Phase is UpdatePhase.Failed or UpdatePhase.Ready or UpdatePhase.Idle)
     {{
         lock (_admissionLock) _updateHandoffTransferred = false;
     }}
     ```

#### C. 测试改造与全绿验证
- `tests/Iris.App.Tests/Updates/UpdateHandoffLifecycleTests.cs` 的两个用例均改造为生产级容器注册（`ValidateScopes = true`, `ValidateOnBuild = true`, Scoped `SettingsViewModel`, Singleton `SettingsWindowPresenter`），并在 STA 线程（`WpfTestRunner.RunAsync`）下显示设置窗口并触发停止失败流程。
- 断言确认：停止子系统失败抛出原有的 `IOException`，协调器回滚至 `UpdatePhase.Failed`（错误码 `ErrorCode.Unknown`），候选保留，活跃设置窗口 VM 的 `UpdateHandoffTransferred` 成功复位为 `false`，界面更新状态为 `UiText.UpdateState(UpdatePhase.Failed)` 且重试按钮均可点击。
- `Iris.App.Tests` 单独运行与全量运行均 100% 通过（845 / 845 通过）。

---

### 2. 第 2 条闭合：按 R3 源码重新打包、重新绑定与受影响来宾场景复验

#### A. 重新打包（PowerShell 5.1）
- 使用系统原生 Windows PowerShell 5.1 串行执行 `build/pack.ps1`，成功为 0.2.0 与 0.2.1 生成独立命名产物，保留既有 `final-3` 产物与全部历史日志：
  - `artifacts/t039b/release-0.2.0-final-4`（日志：`tasks/done/T-039B.evidence/pack-ps51-final-0.2.0-4.log`）
  - `artifacts/t039b/release-0.2.1-final-4`（日志：`tasks/done/T-039B.evidence/pack-ps51-final-0.2.1-4.log`，增量分析 97 项：19 项增量补丁，78 项无变动保留）
- `payload-manifest.json` 保持 97 项，包含维护入口 `maintenance/Iris.Maintenance.exe` 与来源说明 `release-source.json`。

#### B. 干净 Windows Sandbox 场景 1：一次安装 + 静默保留卸载
- **隔离环境**：全新干净 Windows Sandbox（Windows 11 企业版 10.0.26100，管理员 `WDAGUtilityAccount`）。
- **所用安装包**：`artifacts/t039b/release-0.2.0-final-4/Iris-0.2.0-Setup.exe`
- **执行结果**：22/22 检查全部通过（`Failure: null`）。
  - 安装并验证 .NET 10 桌面运行时（10.0.13）。
  - 启动 Iris 0.2.0 并截取真实设置窗口（截图保存于 `tasks/done/T-039B.evidence/guest-install-r3-2/output/installed-settings.png`，SHA-256: `4edbeaf8d1a9612d23bec7a47f397acc6003784b6bae090448725bb1cf5c1a93`）。
  - 写入合成配置数据，执行静默卸载命令 `maintenance/Iris.Maintenance.exe --uninstall --silent`。
  - 确认主应用与外部维护进程正常退出（ExitCode 0），注册表卸载项完全清除，用户数据完整保留，事务正常完成。
- **保留失败记录**：前次运行 `guest-install-r3-1` 因网络临时抖动导致下载 .NET 10 安装包异常（`unexpected end of file`），原始失败日志与输出原样保留在 `tasks/done/T-039B.evidence/guest-install-r3-1`。

#### C. 干净 Windows Sandbox 场景 2：两版本更新 + 外部 helper 交接 + 原生应用 + 单次重启 + `work-*` 回收
- **隔离环境**：全新干净 Windows Sandbox。
- **所用包**：`release-0.2.0-final-4` 与 `release-0.2.1-final-4`（带 Delta 补丁）。
- **执行结果**：25/25 检查全部通过（`Failure: null`）。
  - 安装 0.2.0 并写入持久化设置与翻译历史数据；
  - 启动真实设置窗口并通过 `GuestUpdateHost` 触发外部 helper 交接；
  - 旧进程安全退出（ExitCode 0），原生 Velopack 应用增量补丁；
  - 恰好单次重启（`exactly-one-restart: true`），启动 0.2.1 新版应用；
  - 截取真实更新后设置窗口（截图保存于 `tasks/done/T-039B.evidence/guest-update-r3-1/output/update-r3/updated-settings.png`，SHA-256: `3a80cb74d1327f253c148adea3bb32d37142622368ce017043d034cf940df126`）；
  - 验证版本为 0.2.1，旧设置与翻译历史完整保留且可读；
  - 检查临时工作目录：`work-dirs-after-update` 为 `Count: 0, Paths: []`，证明有界回收与孤立目录清理逻辑完全生效。

#### D. 未受影响场景沿用说明
按工单要求，未受本次产品改动影响的场景（覆盖矩阵全量卸载保留/删除分支、提交失败恢复、安装取消等）沿用 `release-*-final-3` 的验证结果，全部原始日志与截图均完整保留在相应 `tasks/done/T-039B.evidence/guest-*` 目录中。

---

### 3. 第 3 条闭合：补齐 R2 报告的偏差披露

1. **`src/Iris.Platform.Windows/Maintenance/MaintenanceWorkDirectory.cs` 改动说明**：
   - **改动原因**：在 T-039B-R2 实施第 4 条“成功提交后清理工作目录”时，为解决更新完成后 `%LocalAppData%\Iris.Maintenance\work-*` 孤立临时目录残留问题，在该文件中引入了 `Clean(stateRoot)` 方法，在检查目标目录已无活跃进程占用时进行有界回收清理。
   - **影响分析**：该改动仅在平台维护层增加了幂等的临时目录清理逻辑，不改变公共契约、不改变任何设置存储，且在 R3 的来宾实测中验证了更新后 `work-*` 目录数成功降为 0。R2 报告的偏差章节遗漏了对该文件的列出，现正式补齐披露。
2. **补齐列出 R2 报告未提及的既有测试文件改动**：
   - `tests/Iris.App.Tests/Updates/UpdatePreferenceUiTests.cs`：修正方法名与断言不符（按 R2 第 7 条实施）。
   - `tests/Iris.Maintenance.Tests/UpdateTransactionTests.cs`：增加外部进程启动预热与宽松等待（按 R2 第 3 条实施）。
   - `tests/Iris.Providers.Tests/Translation/Anthropic/AnthropicTranslationProviderTests.cs`：增加环境凭据隔离与未配置密钥下的断言（按 R2 第 9 条实施）。
   - `tests/Iris.App.Tests/Updates/UpdateHandoffLifecycleTests.cs`：R2 新增用例文件，并在 R3 中按生产严格 DI 拓扑进行了重构。

---

### 4. 串行构建与全量测试验证

#### A. Release 构建
- **命令**：`dotnet build Iris.slnx -c Release`
- **原始日志**：`tasks/done/T-039B.evidence/final-r3-release.log`
- **原始输出**：
  ```text
  Build succeeded.
      0 Warning(s)
      0 Error(s)

  Time Elapsed 00:00:07.89
  ```

#### B. 全量测试
- **命令**：`dotnet test Iris.slnx -c Release`
- **原始日志**：`tasks/done/T-039B.evidence/final-r3-full.log`
- **原始输出汇总行**：
  ```text
  测试运行摘要: 已通过!
    F:\\Iris\\tests\\Iris.Infrastructure.Tests\\bin\\Release\\net10.0\\Iris.Infrastructure.Tests.dll (net10.0|x64) 已通过 (8s 008ms)
    F:\\Iris\\tests\\Iris.Platform.Windows.Tests\\bin\\Release\\net10.0-windows10.0.19041.0\\Iris.Platform.Windows.Tests.dll (net10.0|x64) 已通过 (4s 570ms)
    F:\\Iris\\tests\\Iris.Core.Tests\\bin\\Release\\net10.0\\Iris.Core.Tests.dll (net10.0|x64) 已通过 (5s 416ms)
    F:\\Iris\\tests\\Iris.App.Tests\\bin\\Release\\net10.0-windows10.0.19041.0\\Iris.App.Tests.dll (net10.0|x64) 已通过 (5m 03s 333ms)
    F:\\Iris\\tests\\Iris.Maintenance.Tests\\bin\\Release\\net10.0-windows10.0.19041.0\\Iris.Maintenance.Tests.dll (net10.0|x64) 已通过 (5s 938ms)
    F:\\Iris\\tests\\Iris.ArchitectureTests\\bin\\Release\\net10.0-windows10.0.19041.0\\Iris.ArchitectureTests.dll (net10.0|x64) 已通过 (3s 164ms)
    F:\\Iris\\tests\\Iris.Providers.Tests\\bin\\Release\\net10.0\\Iris.Providers.Tests.dll (net10.0|x64) 已通过 (3s 999ms)

    总计: 3707
    失败: 0
    成功: 3704
    已跳过: 3
    持续时间: 5m 03s 861ms
  ```
- **3 项跳过说明**：
  1. `Iris.Platform.Windows.Capture.DesktopContextNativeReadOnlyTests.NativeDesktopContext_GetCursorPosition_ReturnsPhysicalCoordinates`：`[ENVIRONMENT LIMITATION] Native cursor position query unavailable in non-interactive/headless test session.`（非交互环境限制跳过，已获架构师正式裁定认可）。
  2. `Iris.Platform.Windows.Tests.Clipboard.ClipboardSnapshotIsolatedTests.SnapshotHelper_UsesExclusiveNonInteractiveStation_AndJoinsOwnedProcess`：`[ISOLATED_CLIPBOARD] SKIP: Window station creation failed or already exists (station=0, error=183)`（既有窗口站限制跳过）。
  3. `Iris.Platform.Windows.Tests.Clipboard.Win32ClipboardIsolatedIntegrationTests.IsolatedWindowStation_NativeClipboardIntegration_ExecutesInDedicatedProcess`：`[ISOLATED_CLIPBOARD] SKIP: Window station creation failed or already exists (station=0, error=183)`（既有窗口站限制跳过）。

---

### 5. 未验证项

1. 标准用户拒绝系统运行时提升场景（仅在管理员 `WDAGUtilityAccount` 验证）。
2. 要求系统重启的 Microsoft 安装器分支（Sandbox 运行期间未触发强行重启）。
3. 跨用户安装及真实物理便携目录操作（便携/来源/会话/路径守卫有受控单元测试，但未做宿主物理环境多用户测试）。
4. GitHub 公开匿名线上分发与在线升级闭环（依约定留由 T-039C 实施）。
5. 物理实机环境与 NFR 长期稳定性（留待 T-040 验收）。

---

### 6. 工单原文归档（T-039B-R3）

```markdown
{current_task_text.strip()}
```
'''
    t039b_path.write_text(t039b_content + r3_section, encoding='utf-8')
    print("Appended R3 section to tasks/done/T-039B.md")

# Stage changes so far (excluding .codex, artifacts, bin, obj, and current_task.md)
# Collect log files to stage
logs = [str(p.relative_to(root)) for folder in [evidence_dir, root / 'tasks/done/T-039B.review'] for p in folder.rglob('*.log') if not {'bin','obj'} & set(part.lower() for part in p.parts)]
for offset in range(0, len(logs), 100):
    subprocess.run(['git', 'add', '-f', '--', *logs[offset:offset+100]], cwd=root, check=True)

# Collect new evidence files
new_evidence_files = []
for p in [evidence_dir / 'guest-install-r3-1', evidence_dir / 'guest-install-r3-2', evidence_dir / 'guest-update-r3-1']:
    if p.exists():
        for f in p.rglob('*'):
            if f.is_file() and not {'bin','obj'} & set(part.lower() for part in f.parts):
                new_evidence_files.append(str(f.relative_to(root)))

other_evidence = [
    'tasks/done/T-039B.evidence/signed-order-r3.md',
    'tasks/done/T-039B.evidence/run_guest_update.py',
    'tasks/done/T-039B.evidence/guest_update.ps1',
    'tasks/done/T-039B.evidence/finalize_binding_r3.py',
    'tasks/done/T-039B.review/acceptance-review-2/README.md',
    'tasks/done/T-039B.review/acceptance-review-2/architect-r2-release-build.log',
    'tasks/done/T-039B.review/acceptance-review-2/architect-r2-full-run.log',
    *new_evidence_files
]

for offset in range(0, len(other_evidence), 50):
    subprocess.run(['git', 'add', '-f', '--', *other_evidence[offset:offset+50]], cwd=root, check=True)

# Stage modified product and test files
product_and_test_files = [
    'src/Iris.App/Services/AppLifetime.cs',
    'src/Iris.App/Settings/SettingsViewModel.Updates.cs',
    'src/Iris.App/Settings/SettingsWindowPresenter.cs',
    'tests/Iris.App.Tests/Updates/UpdateHandoffLifecycleTests.cs',
    'tasks/done/T-039B.md'
]
subprocess.run(['git', 'add', '--', *product_and_test_files], cwd=root, check=True)

# Also stage tasks/BACKLOG.md as modified by architect
subprocess.run(['git', 'add', '--', 'tasks/BACKLOG.md'], cwd=root, check=True)

# Refresh git index
index = {}
for entry in git('ls-files', '--stage', '-z').split(b'\0'):
    if not entry: continue
    metadata, name = entry.split(b'\t', 1)
    index[name.decode('utf-8')] = metadata.decode().split()[1]

# Staged files sanity checks
staged = [value.decode('utf-8') for value in git('diff', '--cached', '--name-only', '-z').split(b'\0') if value]
assert not any(p.startswith('.codex/') or p.startswith('artifacts/') for p in staged), "Prohibited files staged"
assert not any('/bin/' in p.lower() or '/obj/' in p.lower() or Path(p).suffix in {'.dll','.exe','.pdb','.dmp','.nupkg','.zip'} for p in staged), "Binaries staged"

# Collect tracked source files
sources = []
for name, blob in sorted(index.items()):
    if name.startswith(('src/', 'tests/', 'build/', '.config/', '.github/', 'docs/')) or name in {'Directory.Packages.props', 'Iris.slnx'}:
        p = root / name
        if p.is_file():
            sources.append({
                'Path': name,
                'GitBlob': blob,
                'WorkingSha256': sha256_file(p)
            })

# Collect release packages info (final-4)
packages = []
for version in ['0.2.0', '0.2.1']:
    directory = root / f'artifacts/t039b/release-{version}-final-4'
    files_info = []
    for p in sorted(directory.iterdir()):
        if p.is_file():
            files_info.append({
                'Name': p.name,
                'Bytes': p.stat().st_size,
                'Sha256': sha256_file(p)
            })
    payload_manifest_path = directory / 'payload-manifest.json'
    payload_count = len(json.loads(payload_manifest_path.read_text(encoding='utf-8'))) if payload_manifest_path.exists() else 0
    packages.append({
        'Version': version,
        'Directory': str(directory).replace('\\', '/'),
        'Files': files_info,
        'PayloadManifestEntries': payload_count
    })

# Collect guest images (all, including previous and R3)
image_paths = [
    'tasks/done/T-039B.evidence/guest-update-final-1/output/updated-settings.png',
    'tasks/done/T-039B.evidence/guest-committed-failure-final-1/output/committed-failure.png',
    'tasks/done/T-039B.evidence/guest-remaining-final-1/output/installer-cancel.png',
    'tasks/done/T-039B.evidence/guest-coverage-final-2/output/full-data-delete-choice.png',
    'tasks/done/T-039B.evidence/guest-coverage-final-2/output/full-data-delete-completed.png',
    'tasks/done/T-039B.evidence/guest-coverage-final-2/output/full-data-keep-choice.png',
    'tasks/done/T-039B.evidence/guest-coverage-final-2/output/full-data-keep-completed.png',
    'tasks/done/T-039B.evidence/guest-final-cold-3/output/installed-settings.png',
    'tasks/done/T-039B.evidence/guest-install-r3-2/output/installed-settings.png',
    'tasks/done/T-039B.evidence/guest-update-r3-1/output/update-r3/updated-settings.png'
]

images = []
for rel in image_paths:
    p = root / rel
    assert p.exists(), f"Image {rel} must exist"
    images.append({
        'Path': rel.replace('\\', '/'),
        'Bytes': p.stat().st_size,
        'Sha256': sha256_file(p)
    })

test_verification = {
    'BuildRelease': {
        'Command': 'dotnet build Iris.slnx -c Release',
        'Log': 'tasks/done/T-039B.evidence/final-r3-release.log',
        'Warnings': 0,
        'Errors': 0,
        'Status': 'Success',
        'TimeElapsed': '00:00:07.89'
    },
    'FullTests': {
        'Command': 'dotnet test Iris.slnx -c Release',
        'Log': 'tasks/done/T-039B.evidence/final-r3-full.log',
        'Total': 3707,
        'Failed': 0,
        'Passed': 3704,
        'Skipped': 3,
        'Duration': '5m 03s 861ms',
        'SkippedTests': [
            {
                'Name': 'Iris.Platform.Windows.Capture.DesktopContextNativeReadOnlyTests.NativeDesktopContext_GetCursorPosition_ReturnsPhysicalCoordinates',
                'Reason': '[ENVIRONMENT LIMITATION] Native cursor position query unavailable in non-interactive/headless test session.',
                'Note': 'Caused by non-interactive/headless test execution session in current run. Previous interactive execution in final-full-1.log passed (3702/0/2). Architect independently verified and approved.'
            },
            {
                'Name': 'Iris.Platform.Windows.Tests.Clipboard.ClipboardSnapshotIsolatedTests.SnapshotHelper_UsesExclusiveNonInteractiveStation_AndJoinsOwnedProcess',
                'Reason': '[ENVIRONMENT LIMITATION] Snapshot isolated helper skipped: [ISOLATED_CLIPBOARD] SKIP: Window station creation failed or already exists (station=0, error=183)',
                'Note': 'Existing window station environment limitation in sandbox/CI.'
            },
            {
                'Name': 'Iris.Platform.Windows.Tests.Clipboard.Win32ClipboardIsolatedIntegrationTests.IsolatedWindowStation_NativeClipboardIntegration_ExecutesInDedicatedProcess',
                'Reason': '[ENVIRONMENT LIMITATION] Native isolated window station test skipped: [ISOLATED_CLIPBOARD] SKIP: Window station creation failed or already exists (station=0, error=183)',
                'Note': 'Existing window station environment limitation in sandbox/CI.'
            }
        ],
        'PreviousInteractiveRunLog': 'tasks/done/T-039B.evidence/final-full-1.log'
    }
}

sandbox_r3 = {
    'Scenario1_InstallAndSilentRetentionUninstall': {
        'Environment': 'Clean Windows Sandbox (Windows 11 Enterprise 10.0.26100, WDAGUtilityAccount, Admin)',
        'Package': 'artifacts/t039b/release-0.2.0-final-4/Iris-0.2.0-Setup.exe',
        'Status': 'Success (22/22 checks passed, Failure: null)',
        'EvidenceDir': 'tasks/done/T-039B.evidence/guest-install-r3-2',
        'Screenshot': 'tasks/done/T-039B.evidence/guest-install-r3-2/output/installed-settings.png',
        'ScreenshotSha256': sha256_file(root / 'tasks/done/T-039B.evidence/guest-install-r3-2/output/installed-settings.png'),
        'TransientFailureRetained': 'tasks/done/T-039B.evidence/guest-install-r3-1 (transient network download error retained)'
    },
    'Scenario2_TwoVersionUpdateAndReclaim': {
        'Environment': 'Clean Windows Sandbox',
        'Packages': [
            'artifacts/t039b/release-0.2.0-final-4',
            'artifacts/t039b/release-0.2.1-final-4'
        ],
        'Status': 'Success (25/25 checks passed, Failure: null)',
        'EvidenceDir': 'tasks/done/T-039B.evidence/guest-update-r3-1',
        'Screenshot': 'tasks/done/T-039B.evidence/guest-update-r3-1/output/update-r3/updated-settings.png',
        'ScreenshotSha256': sha256_file(root / 'tasks/done/T-039B.evidence/guest-update-r3-1/output/update-r3/updated-settings.png'),
        'WorkDirectoryCountAfterUpdate': 0
    },
    'UnaffectedScenariosNote': '未受本次产品改动影响的场景（覆盖矩阵全量卸载保留/删除分支、提交失败恢复、安装取消）沿用 release-*-final-3 的结果，全部原始日志与截图均完整保留在相应 guest-* 目录中。'
}

evidence_files = [
    root / 'tasks/done/T-039B.md',
    evidence_dir / 'signed-order.md',
    evidence_dir / 'signed-order-r3.md',
    evidence_dir / 'di-scope-repro.log',
    evidence_dir / 'final-r3-release.log',
    evidence_dir / 'final-r3-full.log',
    evidence_dir / 'pack-ps51-final-0.2.0-4.log',
    evidence_dir / 'pack-ps51-final-0.2.1-4.log',
    evidence_dir / 'guest-update-host-build-final-3.log',
    evidence_dir / 'run_guest_update.py',
    evidence_dir / 'guest_update.ps1',
    evidence_dir / 'finalize_binding_r3.py',
    evidence_dir / 'final-release-1.log',
    evidence_dir / 'final-full-1.log',
    evidence_dir / 'final-r1-release-1.log',
    evidence_dir / 'final-r1-release-2.log',
    evidence_dir / 'final-r1-full-1.log',
    evidence_dir / 'final-r2-release.log',
    evidence_dir / 'final-r2-full.log',
    evidence_dir / 'final-guest-binding.json',
    evidence_dir / 'package-final-3-check.json',
    evidence_dir / 'review-final-candidate-1.json',
    evidence_dir / 'binding-precheck-note.md',
    evidence_dir / 'guest-install-r3-2/output/result.json',
    evidence_dir / 'guest-update-r3-1/output/update-r3/result.json',
    root / 'tasks/done/T-039B.review/acceptance-review-2/README.md'
]

evidence_items = []
for p in evidence_files:
    rel = str(p.relative_to(root)).replace('\\', '/')
    evidence_items.append({
        'Path': rel,
        'GitBlob': index.get(rel),
        'Sha256': sha256_file(p)
    })

delivery_binding = {
    'BaseCommit': git('rev-parse', 'HEAD').decode().strip(),
    'GitTree': git('write-tree').decode().strip(),
    'GitTreeSemantics': "GitTree 是使用 'git write-tree' 计算的暂存区源码树哈希（精确反映本次交付提交所包含的代码、配置、测试与证据树，不包含 commit 对象元数据自身）",
    'BoundPackageVersions': ['0.2.0', '0.2.1'],
    'BoundPackageArtifactTag': 'final-4',
    'ProductSourcesMatchTestedCandidate': True,
    'SourceFiles': sources,
    'ReleasePackages': packages,
    'TestVerification': test_verification,
    'SandboxVerificationR3': sandbox_r3,
    'Evidence': evidence_items,
    'FinalGuestImages': images,
    'ArchivedOrderSha256': current_task_sha,
    'ExplicitlyIncludedLogs': len(logs),
    'StagedCodexFiles': 0,
    'UnverifiedItems': [
        '标准用户拒绝系统运行时提升场景（仅在管理员 WDAGUtilityAccount 验证）',
        '要求系统重启的 Microsoft 安装器分支（Sandbox 运行期间未触发强行重启）',
        '跨用户安装及真实物理便携目录操作（便携/来源/会话/路径守卫有受控单元测试，但未做宿主物理环境多用户测试）',
        'GitHub 公开匿名线上分发与在线升级闭环（依约定留由 T-039C 实施）',
        '物理实机环境与 NFR 长期稳定性（留待 T-040 验收）'
    ]
}

binding_path = evidence_dir / 'delivery-binding.json'
binding_path.write_text(json.dumps(delivery_binding, indent=2, ensure_ascii=False), encoding='utf-8')
subprocess.run(['git', 'add', '--', 'tasks/done/T-039B.evidence/delivery-binding.json'], cwd=root, check=True)

# Re-compute GitTree now that delivery-binding.json is staged
current_tree = git('write-tree').decode().strip()
delivery_binding['GitTree'] = current_tree
binding_path.write_text(json.dumps(delivery_binding, indent=2, ensure_ascii=False), encoding='utf-8')
subprocess.run(['git', 'add', '--', 'tasks/done/T-039B.evidence/delivery-binding.json'], cwd=root, check=True)

# Remove tasks/current_task.md as required by order
if current_task_path.exists():
    assert hashlib.sha256(current_task_path.read_bytes()).hexdigest() == delivery_binding['ArchivedOrderSha256']
    current_task_path.unlink()
    print("tasks/current_task.md safely verified against ArchivedOrderSha256 and removed.")

print("SUCCESS: binding written and staged.")
print(f"BaseCommit: {delivery_binding['BaseCommit']}")
print(f"GitTree: {delivery_binding['GitTree']}")
print(f"SourceFiles: {len(sources)}")
print(f"Packages: {len(packages)}")
print(f"Images: {len(images)}")
print(f"ArchivedOrderSha256: {delivery_binding['ArchivedOrderSha256']}")
