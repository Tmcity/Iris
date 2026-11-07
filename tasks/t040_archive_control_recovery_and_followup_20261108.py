"""Archive actual control recovery and guarded follow-up preparation."""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess

ROOT = Path('F:/Iris')
EVIDENCE = ROOT / 'tasks/T-040.evidence'
CONTROL = EVIDENCE / 'computer-control-rpc-compatibility-20261108-1'
manifest = json.loads((CONTROL / 'manifest.json').read_text(encoding='utf-8-sig'))
for row in manifest['Files']:
    data = (CONTROL / row['Path']).read_bytes()
    assert len(data) == row['Bytes'] and hashlib.sha256(data).hexdigest() == row['Sha256']
client = Path('C:/Users/28910/.codex/plugins/cache/openai-bundled/computer-use/26.609.41114/scripts/computer-use-client.mjs')
assert hashlib.sha256(client.read_bytes()).hexdigest() == 'aa7cf705c7b4c93ea52d8f2ac6dd81de841a3297da0e4b53c78cebd117d35a7b'
DEST = EVIDENCE / 'r2r-followup-preparation-20261108-1'
source = DEST / 'tool-source'
source.mkdir(exist_ok=False)
names = ('t040_r2r_candidate_gate.psm1', 't040_r2r_window_cycle_native.ps1',
         't040_validate_r2r_candidate_guard_readonly.ps1', 't040_start_r2r_runtime_observer.ps1',
         't040_r2r_tray_steady_readonly.ps1', 't040_start_r2r_tray_steady_readonly.ps1',
         't040_validate_natural_gc_pairs_ps51.ps1', 't040_archive_control_recovery_and_followup_20261108.py')
for name in names:
    shutil.copy2(ROOT / 'tasks' / name, source / name)
guest = ROOT / 'artifacts/t040/manual-verification-20261107-3/output'
guard = sorted(guest.glob('r2r-gate-validation-*'))[-1]
shutil.copytree(guard, DEST / guard.name)
record = json.loads((guard / 'validation.json').read_text(encoding='utf-8-sig'))
assert len(record['Cases']) == 5 and all(case['Rejected'] for case in record['Cases'])
assert record['PrimaryUnchanged'] and not record['NativeCliExecuted'] and not record['InstallerExecuted']
files = [{'Path': file.relative_to(DEST).as_posix(), 'Bytes': file.stat().st_size, 'Sha256': hashlib.sha256(file.read_bytes()).hexdigest()}
         for file in sorted(DEST.rglob('*')) if file.is_file() and file.name != 'binding.json']
(DEST / 'binding.json').write_text(json.dumps({'Files': files, 'ActualR2rFollowupExecutionPending': True,
                                             'FormalNfrResult': False, 'ProductOperationPerformed': False}, indent=2) + '\n', encoding='utf-8')
paths = ['tasks/current_task.md', 'tasks/T-040.progress-20261108.md', 'tasks/T-040.completion-ledger-20261108.md']
paths += ['tasks/' + name for name in names]
for directory in (CONTROL, DEST):
    paths += [file.relative_to(ROOT).as_posix() for file in directory.rglob('*') if file.is_file()]
for label in ('r2r-native-followup-source-guard', 'r2r-complete-followup-negative-guards', 'r2r-install-continuation-status'):
    for directory in EVIDENCE.glob('guest-' + label + '-*'):
        paths += [file.relative_to(ROOT).as_posix() for file in directory.rglob('*') if file.is_file()]
paths = sorted(set(paths))
existing = subprocess.check_output(['git', 'diff', '--cached', '--name-only'], cwd=ROOT).decode().splitlines()
assert not set(existing) - set(paths), 'Unrelated index changes are present'
for name in paths:
    assert (ROOT / name).resolve(strict=True).is_relative_to(ROOT / 'tasks') and '.codex' not in Path(name).parts
subprocess.run(['git', 'add', '-f', '--', *paths], cwd=ROOT, check=True)
subprocess.run(['git', 'diff', '--cached', '--check'], cwd=ROOT, check=True)
raw = [name for name in paths if name.startswith('tasks/T-040.evidence/')]
for name in raw:
    assert subprocess.check_output(['git', 'show', ':' + name], cwd=ROOT) == (ROOT / name).read_bytes(), name
print(json.dumps({'ControlManifestFilesVerified': len(manifest['Files']), 'RawIndexByteMatches': len(raw),
                  'FollowupGuardCases': 5, 'ActualMouseAndKeyboardControlVerified': True,
                  'InstallerExecuted': False, 'PublicWritePerformed': False}, indent=2))
