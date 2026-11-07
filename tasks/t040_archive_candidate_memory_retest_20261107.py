"""Freeze the real failed candidate retest and the withdrawn JSON experiment."""
from pathlib import Path
import datetime
import hashlib
import json
import statistics
import subprocess

ROOT = Path('F:/Iris')
OUT = ROOT / 'artifacts/t040/manual-verification-20261106-2/output'
REPORT = ROOT / 'tasks/T-040.evidence/memory-candidate-retest-review-20261107-1'


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


checkpoint = OUT / 'candidate-post-gc-steady-checkpoint-20261107T1200359140717Z.json'
record = read(checkpoint)
assert record['AppPid'] == 476 and record['VisibleIrisWindowCount'] == 0
assert record['TraceProcessAbsent'] and not record['GcRequested'] and not record['WorkingSetTrimmed']
raw = (OUT / 'metrics-c4cdd9b216e34b3e90ef4c889256b95a/samples.jsonl').read_bytes()
raw = raw[:raw.rfind(b'\n') + 1]
rows = [json.loads(line) for line in raw.decode('utf-8-sig').splitlines()]
end = datetime.datetime.fromisoformat(record['Utc'].replace('Z', '+00:00'))
samples = [row for row in rows if row['Kind'] == 'ProcessSample' and row['Pid'] == 476
           and 0 <= (end - datetime.datetime.fromisoformat(row['ObservedUtc'].replace('Z', '+00:00'))).total_seconds() <= 45]
assert len(samples) == 22
stats = {
    'Samples': len(samples), 'WorkingSetMedianMiB': statistics.median(row['WorkingSetBytes'] for row in samples) / 1048576,
    'WorkingSetMaxMiB': max(row['WorkingSetBytes'] for row in samples) / 1048576,
    'PrivateBytesMedianMiB': statistics.median(row['PrivateBytes'] for row in samples) / 1048576,
    'PrivateBytesMaxMiB': max(row['PrivateBytes'] for row in samples) / 1048576,
    'ProcessCpuMaxPercent': max(row['ProcessCpuPercentage'] for row in samples),
    'SystemCpuMaxPercent': max(row['SystemCpuLoadPercentage'] for row in samples)}
assert stats['WorkingSetMedianMiB'] > 160 and stats['PrivateBytesMaxMiB'] < 100
heap = read(OUT / 'heap-metadata-20261107T1205263847870Z.json')
assert heap['RootGraphErrors'] == 0 and not heap['ClosedWindowRootPaths']
assert not any(row['Type'] in ('Iris.App.Settings.SettingsWindow', 'Iris.App.History.HistoryWindow') for row in heap['ReachableHistogram'])
assert not subprocess.run(['git', 'diff', '--name-only', '--', 'src', 'tests'], cwd=ROOT, capture_output=True, check=True).stdout.strip()
cost_paths = sorted(OUT.glob('serializer-metadata-cost-*.json'))
hot_paths = sorted(OUT.glob('serializer-metadata-hot-cost-*.json'))
assert len(cost_paths) == 6 and len(hot_paths) == 2
costs = [read(path) for path in cost_paths + hot_paths]
assert len({row['SerializedSha256'] for row in costs}) == 1
assert all(not row['InstalledIrisInvoked'] and not row['GcRequested'] and not row['RealAppNfrPassed'] for row in costs)
REPORT.mkdir(parents=True, exist_ok=False)
files = []


def freeze(path, relative, data=None):
    data = path.read_bytes() if data is None else data
    destination = REPORT / relative
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.open('xb').write(data)
    files.append({'OriginalPath': str(path), 'Path': destination.relative_to(REPORT).as_posix(),
                  'Bytes': len(data), 'Sha256': hashlib.sha256(data).hexdigest()})


freeze(checkpoint, Path('manual') / checkpoint.name)
for name in ('stage1-view-metadata-20261107T1200355936039Z.json', 'memory-map-20261107T1204055352358Z.json',
             'heap-metadata-20261107T1205263847870Z.json', 'native-heap-metadata-20261107T1212152406598Z.json'):
    freeze(OUT / name, Path('diagnostics') / name)
for path in sorted((OUT / 'readonly-state-20261107T1200357602845Z').iterdir()):
    if path.is_file():
        freeze(path, Path('manual') / path.parent.name / path.name)
freeze(OUT / 'metrics-c4cdd9b216e34b3e90ef4c889256b95a/samples.jsonl', Path('metrics') / 'samples-prefix.jsonl', raw)
(REPORT / 'metrics/saved-window-45s.json').write_text(json.dumps(samples, indent=2) + '\n', encoding='utf-8')
for path in cost_paths + hot_paths:
    freeze(path, Path('experiments') / path.name)
for folder in ('native-heap-metadata-20261107-1', 'serializer-metadata-cost-20261107-1', 'serializer-metadata-hot-cost-20261107-1'):
    for path in sorted((ROOT / 'artifacts/t040/toolsource' / folder).iterdir()):
        if path.is_file() and path.suffix in ('.cs', '.csproj'):
            freeze(path, Path('tool-source') / folder / path.name)
for path in sorted((ROOT / 'artifacts/t040/json-sourcegen-prototype-20261107-1').iterdir()):
    if path.is_file():
        freeze(path, Path('withdrawn-prototype') / path.name)
for name in ('json-sourcegen-contract-tests-20261107-1.log', 'json-sourcegen-contract-tests-20261107-2.log',
             'native-heap-metadata-publish-20261107-1.log', 'serializer-metadata-cost-publish-20261107-1.log',
             'serializer-metadata-hot-cost-publish-20261107-1.log'):
    freeze(ROOT / 'artifacts/t040' / name, Path('validation') / name)
binding = {
    'Utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'Task': 'T-040', 'Complete': False,
    'UserFeedback': {'Calls': ['call_J0bpQf8ymEN0QlVC3udlk9oJ', 'call_DJEOmE9HZB4j1rlwBNBJwc5x'], 'Raw': ['\u5df2\u6267\u884c', '\u4fdd\u5b58\u6210\u529f']},
    'PriorManualCheckpointBlockerResolved': True, 'Version': '0.2.11', 'AppPid': 476,
    'ExpectedSourceCommit': 'dccc0dbd3054122e34e27842d55c6585ff953812', 'Statistics': stats,
    'WorkingSetLimitMiB': 160, 'PrivateBytesLimitMiB': 100, 'WorkingSetPassed': False, 'PrivateBytesPassed': True,
    'ClosedSettingsAndHistoryNoLongerStronglyReachable': True, 'EditingMenuFixEffectiveInRealProcess': True,
    'RemainingNativeMetadataResidentAllocationUnresolved': True,
    'NativeHeapReportedRangesContainDuplicates': True, 'UniqueNativeHeapRanges': 579,
    'MappedWritableMatchedLowAndHighFrequencyHeapMiB': 32.640625,
    'JsonPrototypeContractChecks': {'InitialFailed': 4, 'FixedPassed': 11},
    'IsolatedSerializerRuns': 8, 'AllSerializedBytesMatched': True,
    'JsonSourceGenerationMemoryBenefitProved': False, 'JsonSourceGenerationWithdrawnFromProduct': True,
    'ProductAndTestSourcesMatchPriorCommit': True, 'NoNewCandidateOrPublicReleaseCreated': True,
    'GcRequested': False, 'WorkingSetTrimmed': False, 'OtherFullFeatureAndNfrRequirementsRemainOpen': True,
    'Files': files}
(REPORT / 'binding.json').write_text(json.dumps(binding, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
for row in files:
    data = (REPORT / row['Path']).read_bytes()
    assert len(data) == row['Bytes'] and hashlib.sha256(data).hexdigest() == row['Sha256']
print(json.dumps({'Report': str(REPORT), 'RawFiles': len(files), 'WorkingSetMedianMiB': stats['WorkingSetMedianMiB'],
                  'WorkingSetPassed': False, 'ManualBlockerResolved': True, 'JsonPrototypeWithdrawn': True}, indent=2))
