"""Independently bind the installed native CLI cases and their actual evidence."""
from pathlib import Path
from collections import Counter
import datetime as dt
import hashlib
import json
import shutil

ROOT = Path('F:/Iris')
SESSION = ROOT / 'artifacts/t040/manual-verification-20261107-3'
OUT = SESSION / 'output'
REPORT = ROOT / 'tasks/T-040.evidence/native-cli-review-20261108-1'
REPORT.mkdir(parents=True, exist_ok=True)
assert not any(REPORT.iterdir()), 'Preserve any existing review before creating another'
files = []

def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def freeze(path, relative=None, prefix=False):
    relative = relative or path.relative_to(SESSION)
    dest = REPORT / 'originals' / relative
    dest.parent.mkdir(parents=True, exist_ok=True)
    raw = path.read_bytes()
    if prefix:
        raw = raw[:raw.rfind(b'\n') + 1]
    dest.open('xb').write(raw)
    files.append({'OriginalPath': str(path), 'ArchivePath': dest.relative_to(REPORT).as_posix(),
                  'Bytes': len(raw), 'Sha256': hashlib.sha256(raw).hexdigest(), 'CompleteLinePrefix': prefix})
    return dest

cli_dir = sorted(p for p in OUT.glob('native-cli-*') if p.is_dir())[-1]
cli = read(cli_dir / 'state.json')
assert cli['Completed'] and cli['Failure'] is None and cli['OriginalExitCode'] == 0
assert len(cli['Commands']) == 3 and all(x['ExitCode'] == 0 for x in cli['Commands'])
prime, cache = read(cli_dir / 'prime-snapshot.json'), read(cli_dir / 'cache-snapshot.json')
assert prime['HistoryState']['HistoryTotal'] == cache['HistoryState']['HistoryTotal'] == 1
assert [{k:v for k,v in row.items() if k != 'created_at'} for row in prime['HistoryState']['History']] == [{k:v for k,v in row.items() if k != 'created_at'} for row in cache['HistoryState']['History']]
assert prime['HistoryState']['History'][0]['created_at'] < cache['HistoryState']['History'][0]['created_at']
assert (cli_dir / 'prime-requests.jsonl').read_bytes() == (cli_dir / 'cache-requests.jsonl').read_bytes()
history = prime['HistoryState']['History'][0]
assert history['source_text'] == 'Iris T040 CLI 01' and history['origin'] == 'CommandLine'
assert history['result_text'] == 'Iris T040 synthetic translation'
assert cli['CpuSamples'][0]['IntervalStartedUtc'] < cli['PrimaryCreatedUtc']
assert cli['CpuSamples'][-1]['IntervalEndedUtc'] > cli['BootstrapReadyUtc']
assert max(x['LoadPercent'] for x in cli['CpuSamples']) < 30
assert cli['CreationToBootstrapMs'] > 1500

repeat_dir = sorted(OUT.glob('startup-repeat-*'))[-1]
repeat = read(repeat_dir / 'state.json')
assert repeat['Completed'] and repeat['Failure'] is None and len(repeat['Runs']) == 3
assert repeat['BeforeSettingsHash'] == repeat['AfterSettingsHash']
assert repeat['BeforeSecretsHash'] == repeat['AfterSecretsHash']
repeat_summaries = []
for run in repeat['Runs']:
    assert run['OldExitCode'] == 0 and run['InitialTraySample']['MainWindowHandle'] == 0
    peak = max(x['LoadPercent'] for x in run['CpuSamples'])
    assert peak < 30
    repeat_summaries.append({'Pid': run['Pid'], 'CreationToBootstrapMs': run['CreationToBootstrapMs'],
                             'PeakIntervalCpuPercent': peak, 'WorkingSetMiB': run['InitialTraySample']['WorkingSetBytes']/1048576,
                             'PrivateBytesMiB': run['InitialTraySample']['PrivateBytes']/1048576})

feature_dir = sorted(OUT.glob('native-features-*'))[-1]
feature = read(feature_dir / 'state.json')
assert feature['Completed'] and feature['Failure'] is None and len(feature['Commands']) == 8
assert all(x['ExitCode'] == 0 for x in feature['Commands'])
events = [json.loads(x) for x in (feature_dir / 'history-shows-requests.jsonl').read_text(encoding='utf-8-sig').splitlines()]
requests = [x for x in events if x['Kind'] == 'Request']
counts = Counter(x['SyntheticText'] for x in requests)
assert counts['Iris T040 retry native 02'] == 2 and counts['Iris T040 error native 03'] == 2
errors = [x for x in events if x['Kind'] == 'HttpError']
assert Counter(x['StatusCode'] for x in errors) == Counter({429: 1, 503: 2})
last_data = read(feature_dir / 'history-shows-snapshot.json')['DataState']
assert last_data['SqliteReadOnly'] and not last_data['SecretsDecrypted'] and last_data['SqliteFailureCode'] is None
assert last_data['HistoryTotal'] == 5
by_text = {x['source_text']: x for x in last_data['History']}
assert 'Iris T040 error native 03' not in by_text and 'Iris T040 cancel native 05' not in by_text
assert by_text['Iris T040 truncated native 04']['is_truncated'] == 1
assert by_text['Iris T040 after cancel native 06']['result_text'] == 'Iris T040 synthetic translation'
slow = next(x for x in requests if x['SyntheticText'] == 'Iris T040 cancel native 05')
assert any(x['Kind'] == 'ConnectionEnded' and x['Sequence'] == slow['Sequence'] for x in events)
assert not any(x['Kind'] == 'Completed' and x['Sequence'] == slow['Sequence'] for x in events)

dirs = [cli_dir, repeat_dir, feature_dir]
for marker in (prime, cache):
    dirs.append(OUT / Path(marker['SnapshotDirectory']).name)
for path in feature_dir.glob('*-snapshot.json'):
    dirs.append(OUT / Path(read(path)['SnapshotDirectory']).name)
config = next(p for p in sorted(OUT.glob('fixture-config-*'), reverse=True) if not read(p/'state.json')['IsolatedValidation'])
dirs.append(config)
for directory in sorted(set(dirs)):
    for path in sorted(directory.iterdir()):
        if path.is_file():
            freeze(path)
for pattern in ('session3-tray-idle-*','native-cli-ps51-parse.json','all-current-tools-ps51-parse.json','native-current-processes-*','native-final-processes-*','native-feature-processes-*','runtime-observer-launch-*'):
    for path in sorted(OUT.glob(pattern)):
        freeze(path)
pointer = read(OUT/'active-metrics.json')
metric_dir = OUT / Path(pointer['OutputDirectory']).name
freeze(metric_dir/'samples.jsonl',prefix=True)
freeze(metric_dir/'status.json')
runtime_dir = sorted(OUT.glob('runtime-counters-*'))[-1]
freeze(runtime_dir/'events.jsonl',prefix=True)
for name in ('t040_native_cli_acceptance.ps1','t040_start_native_cli_acceptance.ps1','t040_repeat_native_startup.ps1','t040_start_native_startup_repeat.ps1','t040_native_cli_feature_cases.ps1','t040_start_native_feature_cases.ps1','t040_start_runtime_observer_readonly.ps1','t040_configure_session3_fixture.ps1','t040_validate_native_fixture_tool.ps1'):
    freeze(SESSION/'cli-input'/name)

report = {
    'Utc': dt.datetime.now(dt.timezone.utc).isoformat(), 'Task': 'T-040', 'Complete': False,
    'SandboxId': 'edf31417-4146-4ccb-9b48-feb98ccb6871', 'InstalledVersion': '0.2.11',
    'SourceCommit': 'dccc0dbd3054122e34e27842d55c6585ff953812', 'FinalPrimaryPid': 6040,
    'CliOriginalNormalExitCode': cli['OriginalExitCode'], 'NativeCliCommandsAccepted': len(cli['Commands'])+len(feature['Commands']),
    'SinglePrimaryAndCliTranslationProved': True, 'CacheNetworkAvoidanceAndHistoryDedupProved': True,
    'DeduplicatedHistoryTimestampRefreshedAsSpecified': True,
    'InitialLowCpuStartupMs': cli['CreationToBootstrapMs'], 'InitialLowCpuStartupFailed': True,
    'InitialPeakIntervalCpuPercent': max(x['LoadPercent'] for x in cli['CpuSamples']), 'StartupRepeats': repeat_summaries,
    'Original1599msFailureDiscarded': False, 'StartupNfrFullyClosed': False,
    'Native429RetryProved': True, 'Native503FailureThenRecoveryProved': True, 'NativeTruncationHistoryProved': True,
    'NativeReplacementCancellationProved': True, 'ManualStopButtonProved': False, 'CardRetryButtonProved': False,
    'HistoryCliShowRefreshExercised': True, 'ManualHistoryRefreshAndCopyReproduced': False,
    'FullGcObservedInCapturedPrefix': False, 'WorkingSet160MiBLimitUnchanged': True,
    'FourKProved': False, 'PublicVersionPublished': False, 'Files': files,
}
(REPORT/'binding.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({k:report[k] for k in ('SinglePrimaryAndCliTranslationProved','CacheNetworkAvoidanceAndHistoryDedupProved','InitialLowCpuStartupMs','StartupRepeats','Native429RetryProved','Native503FailureThenRecoveryProved','NativeReplacementCancellationProved','Complete')},indent=2))
