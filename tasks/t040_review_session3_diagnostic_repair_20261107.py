"""Bind actual installation, metrics repair and Config CI reproduction evidence."""
from pathlib import Path
from collections import defaultdict
import datetime as dt
import hashlib
import json
import re
import statistics
import xml.etree.ElementTree as ET

ROOT = Path('F:/Iris')
SESSION = ROOT / 'artifacts/t040/manual-verification-20261107-3'
OUT = SESSION / 'output'
REPORT = ROOT / 'tasks/T-040.evidence/session3-diagnostic-repair-20261107-1'
FILES = []

def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def stamp(value):
    return dt.datetime.fromisoformat(value.replace('Z', '+00:00'))

def freeze(path, relative=None, prefix=False):
    relative = relative or path.relative_to(SESSION)
    target = REPORT / 'recovered' / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    raw = path.read_bytes()
    if prefix:
        raw = raw[:raw.rfind(b'\n') + 1]
    if target.exists():
        assert target.read_bytes() == raw, 'Do not replace a different archived record'
    else:
        target.open('xb').write(raw)
    FILES.append({'OriginalPath': str(path), 'ArchivePath': target.relative_to(REPORT).as_posix(),
                  'Bytes': len(raw), 'Sha256': hashlib.sha256(raw).hexdigest(), 'CompleteLinePrefix': prefix})
    return target

for row in read_json(REPORT / 'original-manifest.json'):
    raw = (REPORT / 'originals' / row['Path']).read_bytes()
    assert len(raw) == row['Bytes'] and hashlib.sha256(raw).hexdigest() == row['Sha256']

expected = {x['Path']: (x['Bytes'], x['Sha256'].lower()) for x in read_json(SESSION / 'input/LOCAL_0211_payload-manifest.json')}
install_path = sorted(OUT.glob('candidate-installed-*.json'))[-1]
installed = read_json(install_path)
actual = {x['Path']: (x['Bytes'], x['Sha256'].lower()) for x in installed['Payload']}
assert actual == expected and len(actual) == 97 and installed['Version'] == '0.2.11'
freeze(install_path)
for pattern in ('metrics-v3-ps51-parse.json','metrics-v3-validation.json','active-metrics.json','active-metrics-preserved-*.json','tool-transcript-*.txt','session3-helpers-*.json','trust-probe-processes-*.json'):
    for path in sorted(OUT.glob(pattern)):
        freeze(path)
parse = read_json(OUT / 'metrics-v3-ps51-parse.json')
assert parse['PsVersion'].startswith('5.1.') and parse['AllPassed']
validation = read_json(OUT / 'metrics-v3-validation.json')
assert validation['LiveProcessReadPassed'] and validation['EnumerateThenExitRaceHandled']
assert validation['RepairedResult']['State'] == 'Unavailable' and validation['RepairedResult']['Reason'] == 'ProcessAbsent'

pointer = read_json(OUT / 'active-metrics.json')
metrics_dir = OUT / Path(pointer['OutputDirectory']).name
sample_path = freeze(metrics_dir / 'samples.jsonl', prefix=True)
samples = [json.loads(x) for x in sample_path.read_text(encoding='utf-8').splitlines()]
process_samples = [x for x in samples if x['Kind'] == 'ProcessSample' and x['Pid'] == 5568]
assert len(process_samples) >= 20
assert not [x for x in samples if x['Kind'] == 'ObserverFailed']
freeze(metrics_dir / 'status.json')
saved = sorted(metrics_dir.glob('saved-state-*.json'))[-1]
saved_state = read_json(saved)
assert saved_state['ObserverHealthy'] and saved_state['ObserverActualProcessAlive']
freeze(saved)
latest_state_dir = sorted(OUT.glob('readonly-state-*'))[-1]
latest_data_dir = sorted(OUT.glob('feature-data-*'))[-1]
for directory in (latest_state_dir, latest_data_dir):
    for path in sorted(directory.iterdir()):
        if path.is_file():
            freeze(path)
data = read_json(latest_data_dir / 'state.json')
assert data['SqliteReadOnly'] and not data['SecretsDecrypted'] and data['SqliteFailureCode'] is None

NS = {'e': 'http://schemas.microsoft.com/win/2004/08/events/event'}
def capi_tasks(directory, pid):
    rows = [json.loads(x) for x in (directory / 'events.jsonl').read_text(encoding='utf-8-sig').splitlines()]
    state = read_json(directory / 'state.json')
    integrity = read_json(directory / 'record-integrity.json')
    assert state['Completed'] and state['Failure'] is None and state['CapturedEvents'] == len(rows)
    assert integrity['UniqueRecordIds'] == len(rows) and not integrity['Gaps'] and not integrity['DuplicateDeliveries']
    groups = defaultdict(list)
    for row in rows:
        tree = ET.fromstring(row['Xml'])
        system = tree.find('e:System', NS)
        assert int(system.find('e:EventRecordID', NS).text) == row['RecordId']
        assert int(system.find('e:EventID', NS).text) == row['EventId']
        assert int(system.find('e:Execution', NS).get('ProcessID')) == row['ProcessId']
        if row['ProcessId'] != pid:
            continue
        aux = tree.find('.//e:CorrelationAuxInfo', NS)
        if aux is not None:
            groups[aux.get('TaskId')].append((row, tree))
    trusts = []
    for task_id, events in groups.items():
        ends = [(row, tree) for row, tree in events if row['EventId'] == 81]
        starts = [row for row, _ in events if row['EventId'] == 80]
        if len(ends) != 1 or len(starts) != 1:
            continue
        end, tree = ends[0]
        trust = tree.find('.//e:WinVerifyTrust', NS)
        file = trust.find('e:FileInfo', NS)
        if file is None or not file.get('filePath', '').endswith('.msi'):
            continue
        policy = sorted([{'Utc': row['EventUtc'], 'Result': xml.find('.//e:Result', NS).get('value')}
                         for row, xml in events if row['EventId'] == 30], key=lambda x: x['Utc'])
        trusts.append({'TaskId': task_id, 'Pid': pid, 'FilePath': file.get('filePath'),
                       'ActionId': trust.find('e:ActionID', NS).text.strip('{}').lower(),
                       'Flags': trust.find('e:Flags', NS).get('value'), 'Result': trust.find('e:Result', NS).get('value'),
                       'StartedUtc': starts[0]['EventUtc'], 'FinishedUtc': end['EventUtc'],
                       'ElapsedSeconds': (stamp(end['EventUtc']) - stamp(starts[0]['EventUtc'])).total_seconds(),
                       'Policies': policy, 'StartRecordId': starts[0]['RecordId'], 'EndRecordId': end['RecordId']})
    return state, integrity, sorted(trusts, key=lambda x: x['StartedUtc'])

original_capi_dir = OUT / 'live-capi2-20261107T1325298890045Z'
original_state, original_integrity, original_trusts = capi_tasks(original_capi_dir, 760)
assert len(original_trusts) == 4
assert all(x['ActionId'] == '6078065b-8f22-4b13-bd9b-5b762776f386' and x['Flags'] == '80001080' and x['Result'] == '0' for x in original_trusts)
log_dir = sorted(OUT.glob('session3-install-logs-*'))[-1]
for path in sorted(log_dir.iterdir()):
    if path.is_file():
        freeze(path)
log_record = read_json(log_dir / 'state.json')
for row in log_record['Logs']:
    raw = (log_dir / row['CopiedName']).read_bytes()
    assert len(raw) == row['Bytes'] and hashlib.sha256(raw).hexdigest().upper() == row['Sha256'].upper()
msi_logs = []
for path in sorted(log_dir.glob('*_00*.log')):
    raw = path.read_bytes()
    text = raw.decode('utf-16' if raw.startswith(b'\xff\xfe') else 'utf-8-sig')
    starts = [x for x in text.splitlines() if 'SOFTWARE RESTRICTION POLICY: Verifying package' in x]
    ends = [x for x in text.splitlines() if 'SOFTWARE RESTRICTION POLICY:' in x and 'has a digital signature' in x]
    assert len(starts) == len(ends) == 1
    def log_time(line):
        value = re.search(r'\[(\d{2}:\d{2}:\d{2}:\d{3})\]', line).group(1)
        return dt.datetime.strptime('2026-11-07 ' + value, '%Y-%m-%d %H:%M:%S:%f').replace(tzinfo=dt.timezone(dt.timedelta(hours=8)))
    first, last = log_time(starts[0]), log_time(ends[0])
    msi_logs.append({'File': path.name, 'StartedUtc': first.astimezone(dt.timezone.utc).isoformat(),
                     'FinishedUtc': last.astimezone(dt.timezone.utc).isoformat(), 'Seconds': (last - first).total_seconds(),
                     'StartLine': starts[0], 'EndLine': ends[0]})
assert len(msi_logs) == 4

probe_dir = sorted(OUT.glob('config-ci-trust-probe-*'))[-1]
probe = read_json(probe_dir / 'state.json')
launch = read_json(probe_dir / 'launch.json')
assert probe['Completed'] and probe['Failure'] is None and len(probe['Calls']) == 2
assert all(x['ResultHex'] == '00000000' and x['CloseResultHex'] == '00000000' for x in probe['Calls'])
for path in sorted(probe_dir.iterdir()):
    if path.is_file():
        freeze(path)
probe_capi_dir = OUT / Path(launch['CapiOutputDirectory']).name
probe_state, probe_integrity, probe_trusts = capi_tasks(probe_capi_dir, launch['ProbePid'])
for path in sorted(probe_capi_dir.iterdir()):
    if path.is_file():
        freeze(path)
assert len(probe_trusts) == 2 and probe_trusts[1]['ElapsedSeconds'] > 119
assert probe_trusts[1]['ActionId'] == original_trusts[0]['ActionId'] and probe_trusts[1]['Flags'] == original_trusts[0]['Flags']
assert probe_trusts[1]['FilePath'] == original_trusts[0]['FilePath']

helper_path = sorted(OUT.glob('session3-helpers-*'))[-1]
helpers = read_json(helper_path)
assert len(helpers['ProductProcesses']) == 1 and helpers['ProductProcesses'][0]['ProcessId'] == 5568
assert len(helpers['MetricsObserver']) == 1 and helpers['MetricsObserver'][0]['ProcessId'] == pointer['ObserverPid']
assert not helpers['TrustProbeProcesses'] and not helpers['TrustCapiProcesses']
assert helpers['TrustProbeState']['Completed'] and helpers['TrustCapiState']['Completed']

counter_dir = sorted(OUT.glob('counter-influence-*'))[-1]
counter = read_json(counter_dir / 'state.json')
recovery = read_json(counter_dir / 'recovery.json')
assert counter['Completed'] and counter['CounterExitCode'] is None and len(counter['Snapshots']) == 1
assert recovery['Completed'] and recovery['CounterActualProcessAbsent'] and recovery['ElapsedAfterTraceSeconds'] >= 45
for path in sorted(counter_dir.iterdir()):
    if path.is_file():
        freeze(path)
counter_trace_dir = OUT / Path(recovery['CounterTraceDirectory']).name
for path in sorted(counter_trace_dir.iterdir()):
    if path.is_file():
        freeze(path)
counter_before, counter_after = counter['Snapshots'][0], recovery['PostTraceSample']
counter_comparison = {'BeforeMiB': counter_before['WorkingSetBytes'] / 1048576,
                      'AfterAtLeast45SecondsMiB': counter_after['WorkingSetBytes'] / 1048576,
                      'ElapsedAfterTraceSeconds': recovery['ElapsedAfterTraceSeconds'],
                      'WorkingSetDeltaMiB': (counter_after['WorkingSetBytes'] - counter_before['WorkingSetBytes']) / 1048576,
                      'PrivateBytesDeltaMiB': (counter_after['PrivateBytes'] - counter_before['PrivateBytes']) / 1048576,
                      'SettingsHashesEqual': counter_before['SettingsSha256'] == counter_after['SettingsSha256'],
                      'MainWindowHandlesEqual': counter_before['MainWindowHandle'] == counter_after['MainWindowHandle'],
                      'NaturalFullGcStarts': recovery['CounterTraceState']['NaturalFullGcStarts'],
                      'OriginalNullExitCodeFailurePreserved': True, 'NoTracingRepeatedForRecovery': True,
                      'FormalIdleMemoryProved': False}

for path in sorted(SESSION.joinpath('repair-input').iterdir()):
    if path.is_file() and path.suffix in ('.ps1', '.psm1', '.json'):
        freeze(path)
for name in ('SoftPub.h','WinTrust.h'):
    freeze(Path('C:/Program Files (x86)/Windows Kits/10/Include/10.0.22000.0/um') / name, Path('sdk-headers') / name)

report = {
    'Utc': dt.datetime.now(dt.timezone.utc).isoformat(), 'Task': 'T-040', 'Complete': False,
    'SandboxId': 'edf31417-4146-4ccb-9b48-feb98ccb6871', 'ProductPid': 5568, 'Version': '0.2.11',
    'SourceCommit': 'dccc0dbd3054122e34e27842d55c6585ff953812', 'SourceTree': 'd5d9f93f1eb294e55a83aeb0b3801c19dab3deaf',
    'InstalledFilesIndependentlyMatched': 97, 'InstallationVerified': True, 'SaveRecovered': True,
    'OriginalMetricsFailurePreserved': True, 'OriginalMetricsRootCauseUniquelyProved': False,
    'EnumerateThenExitFailureBoundedlyReproducedAndHandled': True, 'PowerShell51ValidationPassed': True,
    'MetricsObserverPid': pointer['ObserverPid'], 'MetricsProcessSamples': len(process_samples),
    'OpenWindowMetrics': {'WorkingSetMedianMiB': statistics.median(x['WorkingSetBytes'] for x in process_samples)/1048576,
                         'PrivateBytesMedianMiB': statistics.median(x['PrivateBytes'] for x in process_samples)/1048576},
    'OriginalColdStartupCpuRecovered': False, 'ColdStartupNumericMs': 1410.1547, 'ColdStartupLowLoadConditionProved': False,
    'HistoryTotal': data['HistoryTotal'], 'SyntheticServiceConfiguredInThisNewSession': False,
    'OriginalCapiEvents': original_state['CapturedEvents'], 'OriginalCapiIntegrity': original_integrity,
    'OriginalTrustCalls': original_trusts, 'MsiSignaturePolicyIntervals': msi_logs,
    'TotalMsiSignaturePolicySeconds': sum(x['Seconds'] for x in msi_logs),
    'ConfigCiActionIdentifiedBySdkAndRegistry': True,
    'IndependentTrustCalls': probe['Calls'], 'IndependentTrustCapiCalls': probe_trusts,
    'IndependentTrustCapiIntegrity': probe_integrity, 'SlowConfigCiCallReproducedWithoutIrisOrInstallerExecution': True,
    'OriginalCallbackDataCaptured': False, 'ConfigCiInternalWaitCauseAndRemediationStillOpen': True,
    'CounterInfluenceComparison': counter_comparison,
    'ComputerUseImportFixReviewed': True, 'ComputerUseDesktopBridgeAvailable': False,
    'ProductCodeChangedInThisReview': False, 'PublicVersionPublished': False,
    'WorkingSet160MiBLimitUnchanged': True, 'Prior203_867MiBFailurePreserved': True,
    'FullFunctionalAndNfrAcceptanceComplete': False, 'FourKProved': False, 'Files': FILES,
}
(REPORT / 'binding.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps({k: report[k] for k in ('InstallationVerified','SaveRecovered','MetricsProcessSamples','TotalMsiSignaturePolicySeconds','SlowConfigCiCallReproducedWithoutIrisOrInstallerExecution','CounterInfluenceComparison','ComputerUseDesktopBridgeAvailable','Complete')}, ensure_ascii=False, indent=2))
