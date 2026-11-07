"""Freeze the completed diagnostic comparison without claiming formal NFR results."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import re
import shutil
import statistics
import subprocess

ROOT = Path('F:/Iris')
SESSION = ROOT / 'artifacts/t040/manual-verification-20261107-3'
OUTPUT = SESSION / 'output'
RUN = OUTPUT / 'r2r-repeat-20261107T1856449261155Z'
DEST = ROOT / 'tasks/T-040.evidence/r2r-repeat-review-20261108-1'
DEST.mkdir(parents=True, exist_ok=False)
state = json.loads((RUN / 'state.json').read_text(encoding='utf-8-sig'))
assert state['Completed'] and state['Failure'] is None
assert [row['Kind'] for row in state['Runs']] == ['baseline', 'r2r', 'r2r', 'baseline']
assert all(row['ExitCode'] == 0 for row in state['Runs'])
shutil.copytree(RUN, DEST / 'originals/comparison')
shutil.copy2(OUTPUT / 'tool-transcript-20261107T185642490113Z.txt', DEST / 'originals/transcript.txt')
shutil.copytree(ROOT / 'tasks/T-040.evidence/guest-diagnostic-r2r-repeat-20261107T185642490113Z', DEST / 'originals/runner')
for name in ('t040_compare_diagnostic_r2r.ps1', 't040_run_current_guest_tool.py',
             't040_snapshot_diagnostic_repeat_readonly.ps1', 't040_restore_installed_after_diagnostic.ps1',
             't040_review_r2r_repeat_20261108.py'):
    target = DEST / 'tool-source' / name
    target.parent.mkdir(exist_ok=True)
    shutil.copy2(ROOT / 'tasks' / name, target)
before = sorted(OUTPUT.glob('repeat-readonly-state-*.json'))[-1]
shutil.copy2(before, DEST / 'originals/before-restore.json')
before_state = json.loads(before.read_text(encoding='utf-8-sig'))
assert not before_state['AppDetails'] and not before_state['RepeatWorkerCurrentlyPresent']
restore = sorted(OUTPUT.glob('restored-installed-*.json'))[-1]
restored = json.loads(restore.read_text(encoding='utf-8-sig'))
assert restored['Version'] == '0.2.11' and not restored['InstallerExecuted']
shutil.copy2(restore, DEST / 'originals/restored-installed.json')
after = sorted(OUTPUT.glob('readonly-state-*'))[-1]
after_state = json.loads((after / 'state.json').read_text(encoding='utf-8-sig'))
assert after_state['InstalledSource']['Version'] == '0.2.11'
assert len(after_state['ProductMetrics']) == 1
assert after_state['ProductMetrics'][0]['Pid'] == restored['Pid']
shutil.copytree(after, DEST / 'originals/after-restore')

samples_file = OUTPUT / 'metrics-v3-e6edd1908748410dad950652b608224b/samples.jsonl'
raw = samples_file.read_bytes()
complete = b''.join(line for line in raw.splitlines(keepends=True) if line.endswith(b'\n'))
(DEST / 'originals/observer-prefix.jsonl').write_bytes(complete)
samples = [json.loads(line) for line in complete.splitlines()]
repeat_samples = [row for row in samples
                  if state['StartedUtc'] <= row.get('ObservedUtc', '') <= state['FinishedUtc']]
clone_cpu_samples = [row for row in repeat_samples if row.get('Pid') in {run['Pid'] for run in state['Runs']}
                     and row.get('Kind') == 'ProcessSample']
assert not clone_cpu_samples

runs = []
for number, row in enumerate(state['Runs'], 1):
    capture = RUN / f"{number:02d}-{row['Kind']}"
    logs = list(capture.glob('iris-*.log'))
    assert len(logs) == 1
    log = logs[0].read_text(encoding='utf-8-sig')
    ready = re.findall(r'AppBootstrapper\).*?\s([0-9.]+) ms', log)
    assert len(ready) == 1
    phases = {}
    for name in ('startup-phases.txt', 'startup-prehost.txt'):
        for line in (capture / name).read_text(encoding='utf-8-sig').splitlines():
            key, value = line.split('=')
            assert key not in phases
            phases[key] = float(value)
    runs.append({**row, 'BootstrapReportedMs': float(ready[0]), 'PhasesMs': phases,
                 'WorkingSetMiB': row['WorkingSetBytes'] / 1048576,
                 'PrivateBytesMiB': row['PrivateBytes'] / 1048576,
                 'StartupCpuConditionProved': False,
                 'PostFullGcSteadyStateProved': False})
groups = {kind: [row for row in runs if row['Kind'] == kind] for kind in ('baseline', 'r2r')}
medians = {kind: {name: statistics.median(row[name] for row in group)
                   for name in ('BootstrapReportedMs', 'WorkingSetMiB', 'PrivateBytesMiB')}
           for kind, group in groups.items()}
result = {
    'ReviewedUtc': datetime.now(timezone.utc).isoformat(),
    'DiagnosticOnly': True, 'FormalNfrResult': False,
    'Runs': runs, 'Medians': medians,
    'DiagnosticStartupReductionMs': medians['baseline']['BootstrapReportedMs'] - medians['r2r']['BootstrapReportedMs'],
    'InitialWorkingSetIncreaseMiB': medians['r2r']['WorkingSetMiB'] - medians['baseline']['WorkingSetMiB'],
    'InitialPrivateBytesIncreaseMiB': medians['r2r']['PrivateBytesMiB'] - medians['baseline']['PrivateBytesMiB'],
    'StartupCpuConditionProved': False, 'CpuObserverTargetsInstalledPathOnly': True,
    'OriginalRunnerCompletionMissing': True, 'OriginalRunnerExitCode': None,
    'OriginalRunnerTimeoutSeconds': 55, 'GuestCompletionIndependentlyVerified': True,
    'RepeatWorkerAbsentAfterCompletion': True, 'AllFourPrimariesExitedNormally': True,
    'InstalledRestoredPid': restored['Pid'], 'InstalledVersion': restored['Version'],
    'InstalledExeSha256': before_state['InstalledExeSha256'],
    'SourceChanged': False, 'NewCandidateCreated': False, 'PublicWritePerformed': False,
    'ObserverIncompleteTailBytes': len(raw) - len(complete),
}
(DEST / 'review.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
manifest = [{'Path': path.relative_to(DEST).as_posix(), 'Bytes': path.stat().st_size,
             'Sha256': hashlib.sha256(path.read_bytes()).hexdigest()}
            for path in sorted(DEST.rglob('*')) if path.is_file()]
(DEST / 'binding.json').write_text(json.dumps({'Files': manifest, 'FormalNfrResult': False}, indent=2) + '\n', encoding='utf-8')
assert subprocess.check_output(['git', 'diff', '861076b', 'HEAD', '--', 'src', 'tests', 'build'], cwd=ROOT) == b''
print(json.dumps({key: value for key, value in result.items() if key not in ('Runs',)}, indent=2))
