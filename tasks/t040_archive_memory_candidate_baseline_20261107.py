"""Freeze the local candidate's real installation and UI-cycle evidence.

This is a read-only review of existing owned evidence. It neither starts Iris
nor produces the manual closed-window memory checkpoint that is still pending.
"""
from pathlib import Path
import datetime
import hashlib
import json
import statistics
import subprocess
import zipfile

ROOT = Path('F:/Iris')
OUT = ROOT / 'artifacts/t040/manual-verification-20261106-2/output'
CANDIDATE = ROOT / 'artifacts/t040/memory-fix-local-candidate-20261107-1'
REPORT = ROOT / 'tasks/T-040.evidence/memory-candidate-baseline-review-20261107-1'
SOURCE = 'dccc0dbd3054122e34e27842d55c6585ff953812'
TREE = 'd5d9f93f1eb294e55a83aeb0b3801c19dab3deaf'


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def file_hash(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1048576), b''):
            digest.update(chunk)
    return digest.hexdigest()


def utc(value):
    return datetime.datetime.fromisoformat(value.replace('Z', '+00:00'))


provenance = read(CANDIDATE / 'release-provenance.json')
manifest = read(CANDIDATE / 'payload-manifest.json')
assert provenance['Version'] == '0.2.11'
assert provenance['SourceCommit'] == SOURCE and provenance['SourceGitTree'] == TREE
assert len(manifest) == 97
assets = []
for entry in read(CANDIDATE / 'SHA256.json'):
    path = (CANDIDATE / entry['Name']).resolve(strict=True)
    assert path.is_relative_to(CANDIDATE) and path.is_file()
    assert path.stat().st_size == entry['Bytes']
    assert file_hash(path).upper() == entry['Sha256'].upper()
    assets.append(entry)
with zipfile.ZipFile(CANDIDATE / 'Iris-0.2.11-full.nupkg') as package:
    for entry in manifest:
        raw = package.read('lib/app/' + entry['Path'])
        assert len(raw) == entry['Bytes']
        assert hashlib.sha256(raw).hexdigest().upper() == entry['Sha256'].upper()
tree_raw = subprocess.run(
    ['git', 'ls-tree', '-r', '-z', TREE], cwd=ROOT,
    capture_output=True, check=True).stdout
tree_blobs = {}
for item in tree_raw.split(b'\0'):
    if item:
        header, name = item.split(b'\t', 1)
        tree_blobs[name.decode('utf-8')] = header.decode('ascii').split()[2]
assert len(tree_blobs) == len(provenance['SourceFiles']) == 723
for entry in provenance['SourceFiles']:
    path = (ROOT / entry['Path']).resolve(strict=True)
    assert path.is_relative_to(ROOT) and '.codex' not in path.parts
    assert tree_blobs[entry['Path']] == entry['GitBlob']
    assert path.stat().st_size == entry['WorkingBytes']
    assert file_hash(path).lower() == entry['WorkingSha256'].lower()

snapshots = [OUT / name for name in (
    'feature-data-20261107T0820590344758Z',
    'feature-data-20261107T0828141819868Z',
    'feature-data-20261107T0843359011266Z')]
states = [read(path / 'state.json') for path in snapshots]
expected_hashes = {
    'settings.json': '70173c64d43cba8a1bb81b5e77598c1b6d31024d976d0eb0d68aacdab33dde2f',
    'secrets.dat': '8e666a4667eff9af2aa497dd4d2fc096164ee5d956b926eaf9f150d679ac1121'}
for state in states:
    assert state['SqliteReadOnly'] and not state['SecretsDecrypted']
    assert state['SqliteFailureCode'] is None
    hashes = {row['Name']: row['Sha256'] for row in state['Hashes']}
    assert all(hashes[name] == value for name, value in expected_hashes.items())
assert [state['HistoryTotal'] for state in states] == [1, 1, 2]
old_entry = next(row for row in states[0]['History'] if row['id'] == 1)
assert old_entry['source_text'] == 'Iris T040 input 01'
assert old_entry['result_text'] == 'Iris T040 synthetic translation'
for state in states[1:]:
    assert next(row for row in state['History'] if row['id'] == 1) == old_entry
new_entry = next(row for row in states[2]['History'] if row['id'] == 2)
assert new_entry['source_text'] == 'T040 OpenAI fixture' and new_entry['result_text'] == 'OK'
assert new_entry['provider_id'] == old_entry['provider_id']

installed = [OUT / name for name in (
    'candidate-installed-20261107T0828139464469Z.json',
    'candidate-installed-20261107T0843357291348Z.json')]
for path in installed:
    record = read(path)
    assert record['Version'] == '0.2.11' and record['SourceCommit'] == SOURCE
    assert record['AllFilesMatch'] and len(record['Payload']) == 97
    assert record['LocalCandidate'] and not record['PublicRelease']

provider = OUT / 'synthetic-provider-20261107T0833515270021Z'
requests = [json.loads(row) for row in (provider / 'requests.jsonl').read_text(encoding='utf-8-sig').splitlines()]
source_hash = hashlib.sha256(new_entry['source_text'].encode()).hexdigest().upper()
actual_request = next(row for row in requests if row.get('SourceSha256') == source_hash)
assert actual_request['FakeKeyAccepted'] and actual_request['Path'] == '/v1/chat/completions'
assert any(row['Kind'] == 'Completed' and row['Sequence'] == actual_request['Sequence'] for row in requests)
test_hash = hashlib.sha256(b'Hello, world.').hexdigest().upper()
assert any(row.get('SourceSha256') == test_hash and row['FakeKeyAccepted'] for row in requests)
assert any(row['Kind'] == 'Models' for row in requests)
assert not read(provider / 'final-state.json')['Ready']

trace = OUT / 'runtime-counters-20261107T0833567533925Z'
trace_final = read(trace / 'state.json')
events = [json.loads(row) for row in (trace / 'events.jsonl').read_text(encoding='utf-8-sig').splitlines()]
assert trace_final['Completed'] and trace_final['Failure'] is None
assert trace_final['Pid'] == 476 and trace_final['NaturalFullGcStarts'] == 1
assert len(events) == trace_final['Events'] == 16206
full_starts = [row for row in events if row['Kind'] == 'GCStart' and row['Depth'] == 2]
full_stops = [row for row in events if row['Kind'] == 'GCStop' and row['Depth'] == 2]
assert len(full_starts) == len(full_stops) == 1
assert full_starts[0]['Count'] == full_stops[0]['Count'] == 4

metrics = OUT / 'metrics-c4cdd9b216e34b3e90ef4c889256b95a'
metric_raw = (metrics / 'samples.jsonl').read_bytes()
metric_raw = metric_raw[:metric_raw.rfind(b'\n') + 1]
rows = [json.loads(row) for row in metric_raw.decode('utf-8-sig').splitlines()]
bootstrap = next(row for row in rows if row['Kind'] == 'StartupLogCorrelated' and row['Pid'] == 476)
first = next(row for row in rows if row['Kind'] == 'ProcessFirstObserved' and row['Pid'] == 476)
old_exit = next(row for row in rows if row['Kind'] == 'ProcessExited' and row['Pid'] == 3032)
assert old_exit['ExitCode'] == 0 and first['SystemCpuLoadPercentage'] == 43
latest_process_path = sorted(OUT.glob('actual-helper-processes-*.json'))[-1]
latest_view_path = sorted(OUT.glob('stage1-view-metadata-*.json'))[-1]
processes = read(latest_process_path)
view = read(latest_view_path)
assert any(row['Name'] == 'Iris.App.exe' and row['ProcessId'] == 476 for row in processes['Processes'])
assert not any(row['Name'] in ('RuntimeCounterProbe.exe', 'T040SyntheticProvider.exe') for row in processes['Processes'])
assert view['AppPid'] == 476 and view['VisibleIrisWindowTitles']
assert not list(OUT.glob('candidate-post-gc-steady-checkpoint-*.json'))
samples = [row for row in rows if row['Kind'] == 'ProcessSample' and row['Pid'] == 476]
end = utc(samples[-1]['ObservedUtc'])
recent = [row for row in samples if 0 <= (end - utc(row['ObservedUtc'])).total_seconds() <= 45]
stats = {
    'ReferenceOnly': True, 'FunctionalWindowsStillOpen': True,
    'LastObservedUtc': samples[-1]['ObservedUtc'], 'Samples': len(recent),
    'WorkingSetMedianMiB': statistics.median(row['WorkingSetBytes'] for row in recent) / 1048576,
    'WorkingSetMaxMiB': max(row['WorkingSetBytes'] for row in recent) / 1048576,
    'PrivateBytesMedianMiB': statistics.median(row['PrivateBytes'] for row in recent) / 1048576,
    'ProcessCpuMaxPercent': max(row['ProcessCpuPercentage'] for row in recent)}

REPORT.mkdir(parents=True, exist_ok=False)
files = []


def freeze(path, relative, *, prefix=False, raw=None):
    destination = REPORT / relative
    destination.parent.mkdir(parents=True, exist_ok=True)
    if raw is None:
        raw = path.read_bytes()
        if prefix:
            raw = raw[:raw.rfind(b'\n') + 1]
    destination.open('xb').write(raw)
    files.append({
        'OriginalPath': str(path), 'Path': destination.relative_to(REPORT).as_posix(),
        'Bytes': len(raw), 'Sha256': hashlib.sha256(raw).hexdigest(),
        'CompleteLinePrefix': prefix})


for path in sorted(CANDIDATE.iterdir()):
    if path.is_file() and path.suffix not in ('.exe', '.nupkg'):
        freeze(path, Path('candidate') / path.name)
for path in snapshots:
    for item in sorted(path.iterdir()):
        if item.is_file():
            freeze(item, Path('snapshots') / path.name / item.name)
for name in ('readonly-state-20261107T0828144006806Z', 'readonly-state-20261107T0843360964906Z'):
    for item in sorted((OUT / name).iterdir()):
        if item.is_file():
            freeze(item, Path('installed-state') / name / item.name)
for path in installed:
    freeze(path, Path('installation') / path.name)
for path in sorted(provider.iterdir()):
    if path.is_file():
        freeze(path, Path('provider') / path.name)
for name in ('state.json', 'events.jsonl'):
    freeze(trace / name, Path('runtime') / name)
for path in (latest_process_path, latest_view_path, metrics / 'status.json'):
    freeze(path, Path('continuation') / path.name)
freeze(metrics / 'samples.jsonl', Path('metrics') / 'samples-prefix.jsonl', prefix=True, raw=metric_raw)
for pattern in ('memory-candidate-input-ready-*.json', 'runtime-observer-launch-20261107T0833*.json'):
    for path in sorted(OUT.glob(pattern)):
        freeze(path, Path('checks') / path.name)
for name in ('saved-state-20261107T0828148162677Z.json', 'saved-state-20261107T0843366185076Z.json'):
    freeze(metrics / name, Path('metrics') / name)
for name in ('tasks/current_task.md', 'tasks/T-040.progress-20261107.md',
             'tasks/T-040.manual-stage2a-cache-20261107.md'):
    path = ROOT / name
    freeze(path, Path('plans-before-update') / path.name)

binding = {
    'Utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'Task': 'T-040', 'Complete': False,
    'LocalCandidate': True, 'PublicRelease': False, 'Version': '0.2.11',
    'SourceCommit': SOURCE, 'CanonicalSourceTree': TREE, 'SourceFilesVerified': 723,
    'CandidateAssetsVerified': len(assets), 'NupkgPayloadFilesVerified': 97, 'Assets': assets,
    'InstalledFilesVerified': 97, 'ConfigurationAndSecretsHashesPreserved': expected_hashes,
    'OldHistoryPreserved': True, 'HistoryCountsBeforeInstallAfterInstallAfterUse': [1, 1, 2],
    'UserFeedback': {
        'InstallationCall': 'call_DtpH1KOhZnEkNxNS0Tlnshwv', 'InstallationRaw': '\u5df2\u6267\u884c',
        'UiCycleCall': 'call_KG2hyEKq8YTkbHIx476gYJ5g',
        'UiCycleRaw': '\u5df2\u6267\u884c\uff0c\u7ffb\u8bd1\u7ed3\u679c\u4e3a\u201cOK\u201d'},
    'ActualNewSource': new_entry['source_text'], 'ActualNewResult': new_entry['result_text'],
    'RequestedTestSourceWasDifferent': True,
    'ResultMatchesFixtureRule': True, 'NewRequestMatchedBySourceSha256': source_hash,
    'ModelAndConnectionTestRequestsObserved': True, 'ActualProviderAccountAuthenticationProved': False,
    'OldAppExit': old_exit, 'CandidateAppPid': 476,
    'Startup': {'Bootstrap': bootstrap, 'FirstObserved': first, 'LowCpuNfrPassed': False,
                'Reason': 'First observed CPU load was 43%; the measurement is retained as reference.'},
    'NaturalGen2GcStart': full_starts[0], 'NaturalGen2GcStop': full_stops[0],
    'RuntimeObserverCompleted': True, 'RuntimeObserverActuallyAbsent': True,
    'SyntheticServiceCompletedAndActuallyAbsent': True,
    'MetricsObserverPid': 2560, 'VisibleIrisWindowTitles': view['VisibleIrisWindowTitles'],
    'CurrentWindowUseMemoryReference': stats,
    'ClosedWindowCandidateSteadyMemoryProved': False,
    'MemoryLimitsUnchanged': {'WorkingSetMiB': 160, 'PrivateBytesMiB': 100},
    'PendingManualStep': 'Close Iris functional windows, keep PID 476 in the tray, idle 45 seconds and save the existing manual candidate checkpoint.',
    'GcRequested': False, 'WorkingSetTrimmed': False, 'IrisUiOperatedByAssistant': False,
    'Files': files}
(REPORT / 'binding.json').write_text(json.dumps(binding, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
for row in files:
    path = REPORT / row['Path']
    assert path.stat().st_size == row['Bytes'] and file_hash(path) == row['Sha256']
print(json.dumps({'Report': str(REPORT), 'Files': len(files), 'PayloadFiles': 97,
                  'SourceFiles': 723, 'NewResult': 'OK', 'HistoryAndSettingsPreserved': True,
                  'ClosedWindowMemoryCheckpointPending': True, 'Complete': False}, indent=2))
