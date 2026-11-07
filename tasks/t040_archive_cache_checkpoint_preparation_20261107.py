"""Freeze real collector preparation results without claiming a manual cache pass."""
from pathlib import Path, PureWindowsPath
import datetime
import hashlib
import json

ROOT = Path('F:/Iris')
SESSION = ROOT / 'artifacts/t040/manual-verification-20261106-2'
OUT = SESSION / 'output'
REPORT = ROOT / 'tasks/T-040.evidence/cache-checkpoint-preparation-review-20261107-1'


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def guest_output(path):
    relative = PureWindowsPath(path).relative_to(PureWindowsPath('C:/IrisT040Output'))
    result = OUT.joinpath(*relative.parts).resolve(strict=True)
    assert result.is_relative_to(OUT)
    return result


guard_path = sorted(OUT.glob('cache-service-guard-validation-*.json'))[-1]
live_path = sorted(OUT.glob('cache-live-fixture-validation-*.json'))[-1]
guard, live = read(guard_path), read(live_path)
assert guard['ExpectedGuardTriggered'] and guard['FeatureSnapshotCount'] == 0
assert not guard['ActualManualCacheStageStarted'] and not guard['ProductOperationPerformed']
assert live['LiveFixtureResponseStatus'] == 200 and live['CopiedPrefixMatchesOriginalBytes']
assert live['PsVersion'].startswith('5.1.') and live['PrefixBytes'] == 188
assert not live['ActualManualCacheStageStarted'] and not live['ActualIrisCacheBehaviorProved']
assert not live['GcRequested'] and not live['WorkingSetTrimmed']
baseline_paths = sorted(OUT.glob('cache-checkpoint-baseline-*'))
assert len(baseline_paths) == 4
baselines = [read(path / 'state.json') for path in baseline_paths]
assert [record['ServiceReady'] for record in baselines] == [False, True, True, True]
assert [record.get('ProviderPrefixBytes') for record in baselines] == [None, 0, 94, 188]
assert all(record['AppPid'] == 476 and record['Version'] == '0.2.11' and record['Failure'] is None for record in baselines)
assert all(not record['CacheBehaviorProved'] and record['PreparationBaseline'] for record in baselines)
for record in baselines:
    for name in ('Iris.App.exe', 'Iris.App.dll', 'Iris.Core.dll', 'Iris.Providers.dll'):
        assert any(row['Path'] == name and row['Matches'] for row in record['IdentityFilesVerified'])
    data = read(guest_output(record['SnapshotPaths'][0]) / 'state.json')
    assert data['SqliteReadOnly'] and not data['SecretsDecrypted']
    assert data['HistoryTotal'] == 2 and data['SqliteFailureCode'] is None
    hashes = {row['Name']: row['Sha256'] for row in data['Hashes']}
    assert hashes['settings.json'] == '70173c64d43cba8a1bb81b5e77598c1b6d31024d976d0eb0d68aacdab33dde2f'
    assert hashes['secrets.dat'] == '8e666a4667eff9af2aa497dd4d2fc096164ee5d956b926eaf9f150d679ac1121'
rejected = read(guest_output(guard['RejectedCheckpointPath']) / 'state.json')
assert rejected['Phase'] == 'prime' and not rejected['ServiceReady'] and rejected['Failure']
assert not rejected['SnapshotPaths']
last = baselines[-1]
assert len(last['ProviderProcesses']) == 1 and last['ProviderProcesses'][0]['ProcessId'] == 7676
last_prefix = (baseline_paths[-1] / 'provider-requests-prefix.jsonl').read_bytes()
assert len(last_prefix) == 188 and last_prefix.endswith(b'\n')
assert [json.loads(row)['Kind'] for row in last_prefix.decode('utf-8').splitlines()] == ['Models', 'Models']
failed_transcript = OUT / 'tool-transcript-20261107T110325919536Z.txt'
assert 'ReadAllBytes' in failed_transcript.read_text(encoding='utf-8-sig')
view_path = sorted(OUT.glob('stage1-view-metadata-*.json'))[-1]
view = read(view_path)
assert view['AppPid'] == 476 and len(view['VisibleIrisWindowTitles']) == 2
assert not list(OUT.glob('candidate-post-gc-steady-checkpoint-*.json'))

REPORT.mkdir(parents=True, exist_ok=False)
files = []


def freeze(path, relative):
    raw = path.read_bytes()
    destination = REPORT / relative
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.open('xb').write(raw)
    files.append({'OriginalPath': str(path), 'Path': destination.relative_to(REPORT).as_posix(),
                  'Bytes': len(raw), 'Sha256': hashlib.sha256(raw).hexdigest()})


for path in baseline_paths + [guest_output(guard['RejectedCheckpointPath'])]:
    for item in sorted(path.iterdir()):
        if item.is_file():
            freeze(item, Path('checkpoints') / path.name / item.name)
    for snapshot in read(path / 'state.json')['SnapshotPaths']:
        folder = guest_output(snapshot)
        for item in sorted(folder.iterdir()):
            if item.is_file():
                freeze(item, Path('snapshots') / folder.name / item.name)
for path in (guard_path, live_path, view_path, failed_transcript):
    freeze(path, Path('checks') / path.name)
for name in ('cache-checkpoint-input-20261107-1.json', 'cache-checkpoint-ps51-parse-20261107-1.json'):
    freeze(ROOT / 'artifacts/t040' / name, Path('preparation') / name)
tool_names = (
    't040_save_cache_checkpoint_readonly.ps1', 't040_collect_cache_baseline_readonly.ps1',
    't040_validate_cache_service_guard_readonly.ps1',
    't040_validate_cache_checkpoint_live_fixture_readonly.ps1',
    't040_validate_cache_checkpoint_live_prefix_v2_readonly.ps1',
    't040_prepare_cache_checkpoint_input_20261107.py')
for name in tool_names:
    freeze(ROOT / 'tasks' / name, Path('tool-source') / name)
for name in ('Save-Cache-Prime-State.cmd', 'Save-Cache-Hit-State.cmd', 'Save-Card-Retry-State.cmd'):
    freeze(SESSION / 'input' / name, Path('manual-input') / name)
binding = {
    'Utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'Task': 'T-040', 'Complete': False,
    'CollectorPreparationValidated': True, 'ActualManualCacheStageStarted': False,
    'ActualIrisCacheBehaviorProved': False, 'CandidateClosedWindowMemoryProved': False,
    'AppPid': 476, 'Version': '0.2.11', 'ExpectedSourceCommit': 'dccc0dbd3054122e34e27842d55c6585ff953812',
    'IdentityFilesVerifiedPerCheckpoint': 4, 'All97InstalledFilesWerePreviouslyVerified': True,
    'SettingsAndSecretHashesPreserved': True, 'HistoryTotalUnchanged': 2,
    'AbsentFixtureGuardValidated': True, 'RejectedPrimeCheckpointWasToolValidationOnly': True,
    'EmptyAndNonemptyFixturePrefixesValidated': True, 'OriginalNonemptyPrefixBytesMatched': 188,
    'InitialValidationFailureRetained': 'ReadAllBytes used its default sharing mode against a live writer.',
    'CorrectedValidation': 'V2 opens the original log with FileShare.ReadWrite; exact byte-prefix comparison passes.',
    'SyntheticServiceRestartedOnlyAfterVerifiedTermination': True, 'SyntheticServicePid': 7676,
    'LiveFixtureHttpStatus': 200, 'StandaloneModelsRequests': 2,
    'StandaloneProbeRequestsDoNotProveIrisModelOrCacheUi': True,
    'VisibleIrisWindowTitles': view['VisibleIrisWindowTitles'],
    'ManualCacheInputAlias': 'C:/IrisT040CacheCheckpoints',
    'ManualMemoryCheckpointStillPending': True, 'ProductOperationPerformed': False,
    'GcRequested': False, 'WorkingSetTrimmed': False, 'PublicWritePerformed': False,
    'Files': files}
(REPORT / 'binding.json').write_text(json.dumps(binding, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
for row in files:
    raw = (REPORT / row['Path']).read_bytes()
    assert len(raw) == row['Bytes'] and hashlib.sha256(raw).hexdigest() == row['Sha256']
print(json.dumps({'Report': str(REPORT), 'Files': len(files), 'CollectorPreparationValidated': True,
                  'ManualCacheUiStillPending': True, 'ManualMemoryCheckpointStillPending': True}, indent=2))
