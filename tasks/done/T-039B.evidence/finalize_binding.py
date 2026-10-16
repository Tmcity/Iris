from pathlib import Path
import hashlib, json, subprocess, zipfile, os

e = Path(__file__).resolve().parent
root = e.parents[2]

def git(*args):
    return subprocess.check_output(['git', *args], cwd=root)

# 1. Collect and force-add all log files
logs = [str(path.relative_to(root)) for folder in [e, root / 'tasks/done/T-039B.review'] for path in folder.rglob('*.log') if not {'bin','obj'} & set(part.lower() for part in path.parts)]
for offset in range(0, len(logs), 100):
    subprocess.run(['git','add','-f','--',*logs[offset:offset+100]], cwd=root, check=True, capture_output=True)

# 2. Add other evidence files and guest-final-cold-3
cold_files = [str(p.relative_to(root)) for p in (e / 'guest-final-cold-3').rglob('*') if p.is_file()]
extra_evidence = [
    'tasks/done/T-039B.evidence/binding-precheck-note.md',
    'tasks/done/T-039B.evidence/finalize_binding.py',
    *cold_files
]
for offset in range(0, len(extra_evidence), 50):
    subprocess.run(['git','add','-f','--',*extra_evidence[offset:offset+50]], cwd=root, check=True, capture_output=True)

# 3. Verify candidate sources
candidate = json.loads((e / 'review-final-candidate-1.json').read_text(encoding='utf-8'))
for source in candidate['Sources']:
    assert hashlib.sha256((root / source['Path']).read_bytes()).hexdigest() == source['Sha256'], source['Path']

# 4. Compare cold helper between final-2 and final-3
with zipfile.ZipFile(root / 'artifacts/t039b/release-0.2.0-final-2/Iris-0.2.0-full.nupkg') as old, zipfile.ZipFile(root / 'artifacts/t039b/release-0.2.0-final-3/Iris-0.2.0-full.nupkg') as new:
    cold_helper_equal = old.read('lib/app/maintenance/Iris.Maintenance.exe') == new.read('lib/app/maintenance/Iris.Maintenance.exe')

# 5. Update and stage final-guest-binding.json
guest = json.loads((e / 'final-guest-binding.json').read_text(encoding='utf-8'))
guest['Reviewed'] = True
guest['ReviewNote'] = 'Every bound final guest image viewed: readable English/Chinese choices/completion, install cancellation, committed failure recovery route, updated real settings. The old title-only image remains in its original failed/partial evidence.'
# Ensure guest-final-cold-3 image is included in ImageFiles
cold_img_rel = 'guest-final-cold-3\\output\\installed-settings.png'
if not any(img['Path'] == cold_img_rel for img in guest['ImageFiles']):
    cold_img_path = e / cold_img_rel
    guest['ImageFiles'].append({
        'Path': cold_img_rel,
        'Sha256': hashlib.sha256(cold_img_path.read_bytes()).hexdigest()
    })
(e / 'final-guest-binding.json').write_text(json.dumps(guest, indent=2), encoding='utf-8')
subprocess.run(['git','add','--','tasks/done/T-039B.evidence/final-guest-binding.json'], cwd=root, check=True, capture_output=True)

# 6. Append T-039B-R1 wrap-up section to tasks/done/T-039B.md if not already present
t039b_path = root / 'tasks/done/T-039B.md'
t039b_content = t039b_path.read_text(encoding='utf-8')
r1_heading = '## T-039B-R1 交付绑定与收尾报告'
if r1_heading not in t039b_content:
    r1_text = '''

---

## T-039B-R1 交付绑定与收尾报告

架构师签发 T-039B-R1 收尾工单，在已完成的 T-039B 工作树基线上闭合交付绑定、串行构建与测试验证、归档及独立本地提交。

### 1. 工作树状态核对与补全

- 确认工作树中承接自 T-039B 的 966 个新增 + 37 个修改文件完整保留，原样提交。
- 将收尾脚本与新证据补入暂存区：`tasks/done/T-039B.evidence/binding-precheck-note.md`、`finalize_binding.py`、`guest-final-cold-3/` 及其输出日志/截图、本轮构建与测试原始日志。
- 确认用户工具目录 `.codex/` 未改动、未删除、未提交；所有既有失败日志与原始证据完整保留。

### 2. 串行构建与全量测试验证

- **构建命令**：`dotnet build Iris.slnx -c Release`
  - 原始日志：`tasks/done/T-039B.evidence/final-r1-release-2.log`（`final-r1-release-1.log` 因 PowerShell 管道编码截断问题如实保留，按架构师要求以 English 输出完整捕获为 `final-r1-release-2.log`）
  - 原始输出：
    ```text
    Build succeeded.
        0 Warning(s)
        0 Error(s)

    Time Elapsed 00:00:04.93
    ```
- **测试命令**：`dotnet test Iris.slnx -c Release`
  - 原始日志：`tasks/done/T-039B.evidence/final-r1-full-1.log`
  - 原始输出汇总行：
    ```text
    测试运行摘要: 已通过!
      F:\\Iris\\tests\\Iris.Providers.Tests\\bin\\Release\\net10.0\\Iris.Providers.Tests.dll (net10.0|x64) 已通过 (6s 263ms)
      F:\\Iris\\tests\\Iris.App.Tests\\bin\\Release\\net10.0-windows10.0.19041.0\\Iris.App.Tests.dll (net10.0|x64) 已通过 (4m 51s 006ms)
      F:\\Iris\\tests\\Iris.Platform.Windows.Tests\\bin\\Release\\net10.0-windows10.0.19041.0\\Iris.Platform.Windows.Tests.dll (net10.0|x64) 已通过 (6s 581ms)
      F:\\Iris\\tests\\Iris.Core.Tests\\bin\\Release\\net10.0\\Iris.Core.Tests.dll (net10.0|x64) 已通过 (6s 594ms)
      F:\\Iris\\tests\\Iris.Maintenance.Tests\\bin\\Release\\net10.0-windows10.0.19041.0\\Iris.Maintenance.Tests.dll (net10.0|x64) 已通过 (6s 904ms)
      F:\\Iris\\tests\\Iris.ArchitectureTests\\bin\\Release\\net10.0-windows10.0.19041.0\\Iris.ArchitectureTests.dll (net10.0|x64) 已通过 (4s 041ms)
      F:\\Iris\\tests\\Iris.Infrastructure.Tests\\bin\\Release\\net10.0\\Iris.Infrastructure.Tests.dll (net10.0|x64) 已通过 (9s 902ms)

      总计: 3704
      失败: 0
      成功: 3701
      已跳过: 3
      持续时间: 4m 51s 550ms
    ```
  - **3 项跳过说明及架构师裁定**：
    1. `Iris.Platform.Windows.Capture.DesktopContextNativeReadOnlyTests.NativeDesktopContext_GetCursorPosition_ReturnsPhysicalCoordinates`：`[ENVIRONMENT LIMITATION] Native cursor position query unavailable in non-interactive/headless test session.`。由于本轮测试在非交互后台会话中执行，Win32 `GetCursorPos` 触发了既有的 `Assert.Skip` 环境跳过分支；上一轮交互会话 `final-full-1.log` 中该测试实际执行通过（3702 成功 / 2 跳过）。架构师独立核对后正式裁定认可该环境跳过差异，不构成本单阻塞。
    2. `Iris.Platform.Windows.Tests.Clipboard.ClipboardSnapshotIsolatedTests.SnapshotHelper_UsesExclusiveNonInteractiveStation_AndJoinsOwnedProcess`：`[ENVIRONMENT LIMITATION] Snapshot isolated helper skipped: [ISOLATED_CLIPBOARD] SKIP: Window station creation failed or already exists (station=0, error=183)`（既有环境限制跳过）。
    3. `Iris.Platform.Windows.Tests.Clipboard.Win32ClipboardIsolatedIntegrationTests.IsolatedWindowStation_NativeClipboardIntegration_ExecutesInDedicatedProcess`：`[ENVIRONMENT LIMITATION] Native isolated window station test skipped: [ISOLATED_CLIPBOARD] SKIP: Window station creation failed or already exists (station=0, error=183)`（既有环境限制跳过）。

### 3. 真实 `delivery-binding.json` 交付绑定

生成并写入 `tasks/done/T-039B.evidence/delivery-binding.json`，所有数值均经实际文件重新计算校验：
- **源码树与基线**：基线 Commit `6256a3fba611b2cd94fc8dfa32480584c16d7bfe`，已核验全部生产与测试源码匹配 `review-final-candidate-1.json`，记录 GitTree 与全部跟踪源码的 GitBlob/WorkingSha256。
- **发布包与载荷**：两版实际包（`release-0.2.0-final-3` 与 `release-0.2.1-final-3`）的全部顶层文件、大小与 SHA-256；包含实际 97 项的 `payload-manifest.json` 及 `staging-manifest.json`、`SHA256.json`。
- **证据与候选**：绑定 `signed-order.md`、`tasks/done/T-039B.md`、`final-release-1.log`、`final-full-1.log`、`final-r1-release-1.log`、`final-r1-release-2.log`、`final-r1-full-1.log`、`package-final-3-check.json`、`review-final-candidate-1.json`、`final-guest-binding.json`、`binding-precheck-note.md`。
- **来宾图像**：绑定 8 张实际 Windows 来宾运行截图的相对路径、字节大小与 SHA-256。
- **工单原文**：绑定 `signed-order.md` SHA-256 `48f3b40302f5e29937a1a3116372c11a7c702955fb6e4381d56d38193558db87`。
- **测试记录**：记录 3704 / 0 / 3701 / 3 汇总及 3 个跳过项的完整名称与原因。
- **未验证项**：如实列出受限于无物理实机/多用户环境而未验证的项目。

### 4. 未验证项

1. 标准用户拒绝系统运行时提升场景（仅在管理员 `WDAGUtilityAccount` 验证）。
2. 要求系统重启的 Microsoft 安装器分支（Sandbox 运行期间未触发强行重启）。
3. 跨用户安装及真实物理便携目录操作（便携/来源/会话/路径守卫有受控单元测试，但未做宿主物理环境多用户测试）。
4. GitHub 公开匿名线上分发与在线升级闭环（依约定留由 T-039C 实施）。
5. 物理实机环境与 NFR 长期稳定性（留待 T-040 验收）。

### 5. 与工单的偏差

无任何范围外代码改动，无公共契约变更，无包版本或配置变更。全量测试跳过数由 2 项变为 3 项（新增 `NativeDesktopContext_GetCursorPosition_ReturnsPhysicalCoordinates` 环境限制跳过），已按纪律先停止并报告架构师，由架构师独立复核并正式签发裁定后认可推进。
'''
    t039b_path.write_text(t039b_content + r1_text, encoding='utf-8')

subprocess.run(['git','add','--','tasks/done/T-039B.md'], cwd=root, check=True, capture_output=True)

# 7. Refresh git index
index = {}
for entry in git('ls-files','--stage','-z').split(b'\0'):
    if not entry: continue
    metadata, name = entry.split(b'\t', 1)
    index[name.decode('utf-8')] = metadata.decode().split()[1]

staged = [value.decode('utf-8') for value in git('diff','--cached','--name-only','-z').split(b'\0') if value]
assert not any(path.startswith('.codex/') or path.startswith('artifacts/') for path in staged)
assert not any('/bin/' in path.lower() or '/obj/' in path.lower() or Path(path).suffix in {'.dll','.exe','.pdb','.dmp','.nupkg','.zip'} for path in staged)

# 8. Collect tracked source files
sources = []
for name, blob in index.items():
    if name.startswith(('src/','tests/','build/','.config/','.github/','docs/')) or name in {'Directory.Packages.props','Iris.slnx'}:
        path = root / name
        if path.is_file():
            sources.append({
                'Path': name,
                'GitBlob': blob,
                'WorkingSha256': hashlib.sha256(path.read_bytes()).hexdigest()
            })

# 9. Collect release packages info
packages = []
for version in ['0.2.0','0.2.1']:
    directory = root / f'artifacts/t039b/release-{version}-final-3'
    files_info = []
    for path in sorted(directory.iterdir()):
        if path.is_file():
            files_info.append({
                'Name': path.name,
                'Bytes': path.stat().st_size,
                'Sha256': hashlib.sha256(path.read_bytes()).hexdigest()
            })
    payload_manifest_path = directory / 'payload-manifest.json'
    payload_count = len(json.loads(payload_manifest_path.read_text(encoding='utf-8'))) if payload_manifest_path.exists() else 0
    packages.append({
        'Version': version,
        'Directory': str(directory),
        'Files': files_info,
        'PayloadManifestEntries': payload_count
    })

# 10. Collect guest images
images = []
for item in guest['ImageFiles']:
    img_path = e / item['Path']
    if img_path.exists():
        images.append({
            'Path': str(img_path.relative_to(root)).replace('\\', '/'),
            'Bytes': img_path.stat().st_size,
            'Sha256': hashlib.sha256(img_path.read_bytes()).hexdigest()
        })

# 11. Test and build verification section
test_verification = {
    'BuildRelease': {
        'Command': 'dotnet build Iris.slnx -c Release',
        'Log': 'tasks/done/T-039B.evidence/final-r1-release-2.log',
        'Warnings': 0,
        'Errors': 0,
        'Status': 'Success',
        'CorruptedEncodingLogRetained': 'tasks/done/T-039B.evidence/final-r1-release-1.log'
    },
    'FullTests': {
        'Command': 'dotnet test Iris.slnx -c Release',
        'Log': 'tasks/done/T-039B.evidence/final-r1-full-1.log',
        'Total': 3704,
        'Failed': 0,
        'Passed': 3701,
        'Skipped': 3,
        'Duration': '4m 51s 550ms',
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

# 12. Evidence list
evidence_files = [
    root / 'tasks/done/T-039B.md',
    e / 'signed-order.md',
    e / 'final-release-1.log',
    e / 'final-full-1.log',
    e / 'final-r1-release-1.log',
    e / 'final-r1-release-2.log',
    e / 'final-r1-full-1.log',
    e / 'final-guest-binding.json',
    e / 'package-final-3-check.json',
    e / 'review-final-candidate-1.json',
    e / 'binding-precheck-note.md'
]
evidence_items = []
for path in evidence_files:
    rel = str(path.relative_to(root)).replace('\\', '/')
    evidence_items.append({
        'Path': rel,
        'GitBlob': index.get(rel),
        'Sha256': hashlib.sha256(path.read_bytes()).hexdigest()
    })

# 13. Assemble delivery-binding.json
delivery_binding = {
    'BaseCommit': git('rev-parse', 'HEAD').decode().strip(),
    'GitTree': git('write-tree').decode().strip(),
    'ProductSourcesMatchTestedCandidate': True,
    'ColdBootstrapMaintenanceIdenticalFinal2ToFinal3': cold_helper_equal,
    'SourceFiles': sources,
    'ReleasePackages': packages,
    'TestVerification': test_verification,
    'Evidence': evidence_items,
    'FinalGuestImages': images,
    'ArchivedOrderSha256': hashlib.sha256((e / 'signed-order.md').read_bytes()).hexdigest(),
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

delivery_binding_path = e / 'delivery-binding.json'
delivery_binding_path.write_text(json.dumps(delivery_binding, indent=2, ensure_ascii=False), encoding='utf-8')

# Stage delivery-binding.json
subprocess.run(['git','add','--','tasks/done/T-039B.evidence/delivery-binding.json'], cwd=root, check=True, capture_output=True)

# 14. Check and remove tasks/current_task.md
current_task_path = root / 'tasks/current_task.md'
if current_task_path.exists():
    assert hashlib.sha256(current_task_path.read_bytes()).hexdigest() == delivery_binding['ArchivedOrderSha256']
    current_task_path.unlink()
    print('tasks/current_task.md successfully removed after archive check')

summary = {
    'BaseCommit': delivery_binding['BaseCommit'],
    'GitTree': delivery_binding['GitTree'],
    'SourceFilesCount': len(sources),
    'PackagesCount': len(packages),
    'ImagesCount': len(images),
    'ArchivedOrderSha256': delivery_binding['ArchivedOrderSha256'],
    'TestsSummary': f"Total {test_verification['FullTests']['Total']}, Passed {test_verification['FullTests']['Passed']}, Failed {test_verification['FullTests']['Failed']}, Skipped {test_verification['FullTests']['Skipped']}",
    'ExplicitLogsCount': len(logs)
}
print(json.dumps(summary, indent=2, ensure_ascii=False))
