"""Record the third missing manual checkpoint and the completed independent diagnostics."""
from pathlib import Path
import datetime
import hashlib
import json
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path('F:/Iris')
OUT = ROOT / 'artifacts/t040/manual-verification-20261106-2/output'
REPORT = ROOT / 'tasks/T-040.evidence/candidate-memory-wait-audit-20261107-1'


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


assert not list(OUT.glob('candidate-post-gc-steady-checkpoint-*.json'))
process_path = sorted(OUT.glob('actual-helper-processes-*.json'))[-1]
processes = read(process_path)
app = next(row for row in processes['Processes'] if row['Name'] == 'Iris.App.exe')
assert app['ProcessId'] == 476 and app['CreationDate'] == '/Date(1794040067576)/'
assert any(row['ProcessId'] == 2560 and row['Name'] == 'powershell.exe' for row in processes['Processes'])
assert any(row['ProcessId'] == 7676 and row['Name'] == 'T040SyntheticProvider.exe' for row in processes['Processes'])
assert not any(row['Name'] == 'RuntimeCounterProbe.exe' for row in processes['Processes'])
view_path = sorted(OUT.glob('stage1-view-metadata-*.json'))[-1]
view = read(view_path)
assert view['AppPid'] == 476 and len(view['VisibleIrisWindowTitles']) == 2
retention = sorted(OUT.glob('install-event-retention-*'))[-1]
retained = read(retention / 'state.json')
capi = next(row for row in retained['Logs'] if row['LogName'] == 'Microsoft-Windows-CAPI2/Operational')
assert capi['Failure'] is None and capi['WindowEvents'] == 0 and not capi['WindowLimitReached']
assert capi['MaximumSizeInBytes'] == 1052672 and capi['LogMode'] == 'Circular'
assert datetime.datetime.fromisoformat(capi['OldestUtc'].replace('Z', '+00:00')) > datetime.datetime.fromisoformat('2026-11-06T17:47:00+00:00')
live = sorted(OUT.glob('live-capi2-*'))[-1]
final = read(live / 'state.json')
integrity = read(live / 'record-integrity.json')
assert final['Completed'] and final['Failure'] is None and final['CapturedEvents'] == 71
assert final['SignatureProbeStatus'] == 'Valid' and final['ObserverPid'] == 7784
assert not final['InstallerExecutedByTool'] and not final['ColdInstallRootCauseProved']
assert not integrity['Gaps'] and integrity['DuplicateDeliveries'] == 0
rows = [json.loads(line) for line in (live / 'events.jsonl').read_text(encoding='utf-8-sig').splitlines()]
assert len(rows) == 71 and {row['ProcessId'] for row in rows} == {7784}
ids = sorted(row['RecordId'] for row in rows)
assert len(set(ids)) == 71 and ids[-1] - ids[0] + 1 == 71
ns = {'e': 'http://schemas.microsoft.com/win/2004/08/events/event'}
for row in rows:
    system = ET.fromstring(row['Xml']).find('e:System', ns)
    assert int(system.find('e:EventRecordID', ns).text) == row['RecordId']
    assert int(system.find('e:EventID', ns).text) == row['EventId']
assert not (ROOT / 'tasks/done/T-040.md').exists()
REPORT.mkdir(parents=True, exist_ok=False)
files = []


def freeze(path, relative):
    raw = path.read_bytes()
    destination = REPORT / relative
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.open('xb').write(raw)
    files.append({'OriginalPath': str(path), 'Path': destination.relative_to(REPORT).as_posix(),
                  'Bytes': len(raw), 'Sha256': hashlib.sha256(raw).hexdigest()})


for folder in (retention, live):
    for path in sorted(folder.iterdir()):
        if path.is_file():
            freeze(path, Path('diagnostics') / folder.name / path.name)
for path in (process_path, view_path):
    freeze(path, Path('current-state') / path.name)
for stamp in ('20261107T112444116746Z', '20261107T113620490218Z'):
    freeze(OUT / ('tool-transcript-' + stamp + '.txt'), Path('commands') / ('tool-transcript-' + stamp + '.txt'))
for name in ('t040_inspect_install_event_retention_readonly.ps1',
             't040_capture_live_capi2_readonly.ps1', 't040_validate_live_capi2_readonly.ps1'):
    freeze(ROOT / 'tasks' / name, Path('tool-source') / name)
history = subprocess.run(['git', 'log', '-2', '--format=%H %cI %s'], cwd=ROOT, capture_output=True, check=True).stdout.decode('utf-8')
(REPORT / 'preceding-goal-turn-commits.txt').write_text(history, encoding='utf-8')
binding = {
    'Utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'Task': 'T-040', 'Complete': False,
    'BlockingCondition': 'Required manual same-instance candidate closed-window 45-second checkpoint and feedback are missing.',
    'ConsecutiveObservedGoalTurnsWithSameMissingCheckpoint': 3,
    'Turn1': {'Commit': 'fd384582baec2cbc361e2b25f538372e53f24d9d', 'Classification': 'progress', 'CheckpointMissing': True},
    'Turn2': {'Commit': 'acfbb38bce523fe447ca17cbe2515f2a82e8709e', 'Classification': 'progress', 'CheckpointMissing': True},
    'Turn3': {'Classification': 'completed independent diagnosis preparation, then blocked on the same manual step', 'CheckpointMissing': True},
    'SandboxId': '4fc7c24d-f1e5-4b3e-adaa-2abe01f643fe', 'CurrentProcesses': processes,
    'VisibleIrisWindowTitles': view['VisibleIrisWindowTitles'], 'SameAppCreationIdentityVerified': True,
    'CurrentMetricsObserverPid': 2560, 'CurrentSyntheticProviderPid': 7676,
    'ManualOperationBoundary': 'User reserved actual Iris UI, installation, hotkey and screenshot operations for manual execution.',
    'IndependentCandidateFixBuildTestsAndReviewCompleted': True,
    'IndependentCacheCollectorPreparationCompleted': True,
    'CurrentCapiRetention': capi, 'InstallWindowCapiDataRecoverable': False,
    'CapiAbsenceDoesNotRuleOutCertificateOrNetworkWait': True,
    'LiveCapiCollectorValidated': {'Events': 71, 'RecordIdGaps': 0, 'Duplicates': 0, 'Completed': True,
                                  'DurationSeconds': 15, 'SignatureProbeElapsedMs': final['SignatureProbeElapsedMs']},
    'LiveValidationExercisedOnlyStandaloneUserContextSignature': True,
    'ActualWindowsInstaller120SecondWaitRootCauseProved': False,
    'CompletionAudit': {
        'Requirement1FullManualFeatureCoverage': 'incomplete; only a subset of real operations has been verified',
        'Requirement2AllNfrMetrics': 'incomplete; candidate closed-window memory, 4K and other response metrics are unproved',
        'Requirement3DefectClosure': 'incomplete; code menu lifetime fixed, actual candidate memory and installer/history issues remain',
        'Requirement4FinalT040DeliverableAndAudit': 'missing; tasks/done/T-040.md does not exist'},
    'EligibleToSetGoalBlocked': True, 'GoalComplete': False, 'ObjectiveAndScopeUnchanged': True,
    'RecoveryAction': 'User closes Iris functional windows, keeps PID476 in the tray, idles 45 seconds, runs C:/IrisT040CandidateSteady/Save-Candidate-Post-GC-Steady-State.cmd and reports the saved result.',
    'NoNewPermissionQuestionNeeded': True, 'NoNewPublicReleaseAuthorized': True, 'Files': files}
(REPORT / 'binding.json').write_text(json.dumps(binding, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
for row in files:
    raw = (REPORT / row['Path']).read_bytes()
    assert len(raw) == row['Bytes'] and hashlib.sha256(raw).hexdigest() == row['Sha256']
print(json.dumps({'Report': str(REPORT), 'FrozenFiles': len(files), 'MissingCheckpointGoalTurns': 3,
                  'LiveCapiEvents': 71, 'EligibleForBlocked': True, 'Complete': False}, indent=2))
