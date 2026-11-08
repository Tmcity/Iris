"""Bind actual controlled UI, command-state regression and an unchanged NFR failure."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import shutil
import statistics

ROOT = Path('F:/Iris')
OUT = ROOT / 'artifacts/t040/manual-verification-20261107-3/output'
DEST = ROOT / 'tasks/T-040.evidence/controlled-card-and-postgc-acceptance-20261108-1'
START = datetime(2026, 11, 8, 6, 15, tzinfo=timezone.utc)
SOURCE = '22133ca09e11cd502b470396735018bb43efc9b2'

def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def copy(path, relative):
    target = DEST / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    if path.is_dir():
        shutil.copytree(path, target)
    else:
        shutil.copy2(path, target)

installed_path = sorted(OUT.glob('controlled-candidate-installed-*.json'))[-1]
installed = read(installed_path)
assert installed['Version'] == '0.2.13' and installed['SourceCommit'] == SOURCE
assert installed['AppPid'] == 1864 and installed['AllFilesMatch'] and installed['ExpectedFiles'] == 97
last_data_dir = sorted(OUT.glob('feature-data-*'))[-1]
data = read(last_data_dir / 'state.json')
assert data['HistoryTotal'] == 13 and data['SqliteFailureCode'] is None
assert not any(row['source_text'] == 'Iris T040 cancel ui 08B' for row in data['History'])
assert not any(row['source_text'] == 'Iris T040 error ui 10' for row in data['History'])
assert any(row['source_text'] == 'Iris T040 truncated ui 013' and row['is_truncated'] == 1 for row in data['History'])
baseline = read(ROOT / 'tasks/T-040.evidence/controlled-history-acceptance-20261108-1/data-snapshots/feature-data-20261107T2126087485678Z/state.json') if (ROOT / 'tasks/T-040.evidence/controlled-history-acceptance-20261108-1/data-snapshots/feature-data-20261107T2126087485678Z/state.json').exists() else None
if baseline is None:
    candidates = [read(path) for path in (ROOT / 'tasks/T-040.evidence/controlled-history-acceptance-20261108-1/data-snapshots').glob('*/state.json')]
    baseline = next(state for state in reversed(candidates) if state['HistoryTotal'] == 7)
for old in baseline['History']:
    actual = next(row for row in data['History'] if row['id'] == old['id'])
    for key in ('source_text', 'result_text', 'provider_id', 'model', 'is_truncated'):
        assert actual[key] == old[key], (old['id'], key)
assert data['Hashes'] == baseline['Hashes']
validation_dir = ROOT / 'artifacts/t040/card-command-validation-20261108-1'
validation = read(validation_dir / 'validation-summary.json')
assert validation['SourceCommit'] == SOURCE
assert validation['FullTests'] == {'Total': 3779, 'Passed': 3777, 'Failed': 0, 'Skipped': 2}
steady_dir = OUT / 'fixed-steady-20261108T0631193726640Z'
steady = read(steady_dir / 'state.json')
assert steady['Completed'] and steady['Failure'] is None and steady['FormalMemoryConditionProved']
assert steady['AppPid'] == 2524 and steady['NaturalFullGcPairs'] == 1
assert len(steady['Samples']) == 45 and all(row['Pid'] == 2524 and row['VisibleWindowCount'] == 0 for row in steady['Samples'])
assert steady['WorkingSetLimitBytes'] == 167772160 and steady['PrivateBytesLimitBytes'] == 104857600
assert not steady['WorkingSetWithinLimit'] and steady['PrivateBytesWithinLimit']
trace_dir = OUT / 'runtime-counters-20261108T0619132270098Z'
trace = read(trace_dir / 'state.json')
assert trace['Completed'] and trace['Failure'] is None and trace['NaturalFullGcStarts'] == 1
events = [json.loads(line) for line in (trace_dir / 'events.jsonl').read_text(encoding='utf-8-sig').splitlines()]
starts = {event['Count']: event for event in events if event.get('Kind') == 'GCStart' and event.get('Depth') == 2 and not str(event.get('Reason')).startswith('Induced')}
stops = {event['Count']: event for event in events if event.get('Kind') == 'GCStop' and event.get('Depth') == 2}
assert len(starts.keys() & stops.keys()) == 1
fixture_dir = sorted(OUT.glob('synthetic-provider-*'))[-1]
requests = [json.loads(line) for line in (fixture_dir / 'requests.jsonl').read_text(encoding='utf-8-sig').splitlines()]
fixed_requests = [row for row in requests if row.get('Kind') == 'Request' and row.get('SyntheticText') == 'Iris T040 card fixed 013']
assert len(fixed_requests) == 2 and [row['Attempt'] for row in fixed_requests] == [1, 2]
retry_requests = [row for row in requests if row.get('Kind') == 'Request' and row.get('SyntheticText') == 'Iris T040 retry ui 09']
assert [row['Attempt'] for row in retry_requests] == [1, 2, 3]
DEST.mkdir(parents=True, exist_ok=False)
screenshots = []
for path in sorted(OUT.glob('controlled-ui-view-*.png.json')):
    record = read(path)
    if datetime.fromisoformat(record['Utc'].replace('Z', '+00:00')) >= START:
        copy(path, Path('screenshots') / path.name)
        copy(Path(str(path)[:-5]), Path('screenshots') / Path(str(path)[:-5]).name)
        screenshots.append({'Path': path.name, 'Utc': record['Utc'], 'AppPid': record['AppPid']})
for path in sorted(OUT.glob('feature-data-*')):
    if datetime.fromisoformat(read(path / 'state.json')['Utc'].replace('Z', '+00:00')) >= START:
        copy(path, Path('data-snapshots') / path.name)
for pattern in ('controlled-candidate-installed-*.json', 'controlled-r2r-helpers-*.json'):
    for path in OUT.glob(pattern):
        if datetime.fromisoformat(read(path)['Utc'].replace('Z', '+00:00')) >= START:
            copy(path, Path('state') / path.name)
copy(ROOT / 'tasks/T-040.evidence/card-command-state-defect-20261108-1', Path('defect-regression'))
copy(validation_dir, Path('full-validation'))
copy(steady_dir, Path('post-gc-steady'))
copy(trace_dir, Path('natural-runtime-observation'))
for path in OUT.glob('fixed-runtime-lease-*.json'):
    copy(path, Path('natural-runtime-observation') / path.name)
for name in ('state.json', 'requests.jsonl', 'final-state.json'):
    if (fixture_dir / name).exists():
        copy(fixture_dir / name, Path('fixture') / name)
copy(OUT / 'memory-map-20261108T0634388564302Z.json', Path('mature-memory-map.json'))
package = ROOT / 'artifacts/t040/card-command-local-candidate-20261108-1'
for name in ('payload-manifest.json', 'release-provenance.json', 'SHA256.json', 'validation-summary.json'):
    copy(package / name, Path('candidate-metadata') / name)
for path in (ROOT / 'artifacts/t040/card-command-package-logs-20261108-1').glob('package.*'):
    copy(path, Path('package-execution') / path.name)
for directory in (ROOT / 'tasks/T-040.evidence').glob('guest-*'):
    command = directory / 'command.json'
    if command.exists():
        stamp = read(command).get('StartedUtc', '')
        if stamp >= '20261108T061500':
            copy(directory, Path('guest-execution') / directory.name)
for name in ('t040_begin_fixed_candidate_observation_readonly.ps1', 't040_fixed_candidate_tray_steady_readonly.ps1',
             't040_start_fixed_candidate_tray_steady_readonly.ps1', 't040_prepare_card_fixed_input_20261108.py',
             't040_r2r_candidate_gate.psm1', 't040_archive_card_and_steady_acceptance_20261108.py'):
    copy(ROOT / 'tasks' / name, Path('tool-source') / name)
review = {
    'RecordedUtc': datetime.now(timezone.utc).isoformat(), 'InstalledVersion': '0.2.13', 'InstalledSource': SOURCE,
    'InstalledPid': 1864, 'InstalledFilesMatch': 97, 'OriginalSevenHistoryEntriesPreserved': True,
    'SettingsAndCredentialHashesPreserved': True, 'CurrentHistoryTotal': 13,
    'ActualHistorySourceSearchFavoriteAndFavoritesFilterPassed': True,
    'ActualHistoryRetranslateAndUiCacheBadgePassed': True,
    'ActualStopButtonAndCanceledTextExcludedFromHistory': True,
    'ServerDisconnectNotInferred': True,
    'Actual429AutomaticRetryAnd503SafeUiErrorPassed': True,
    'ActualModelTruncationUiAndHistoryFlagPassed': True,
    'BeforeRealButtonRegression': {'Total': 3, 'Failed': 3},
    'AfterRealButtonRegression': {'Total': 3, 'Passed': 3},
    'ActualFixedFirstRetryClickSentSecondRequest': True,
    'ActualFixedFirstCopyClickVerifiedByPureKeyboardPaste': True,
    'FullValidation': validation['FullTests'], 'NaturalFullGcPairs': 1,
    'SteadyVersion': '0.2.12', 'SteadyPid': 2524, 'SteadySamples': 45,
    'WsMedianMiB': statistics.median(row['WorkingSetBytes'] for row in steady['Samples']) / 1048576,
    'WsMaximumMiB': max(row['WorkingSetBytes'] for row in steady['Samples']) / 1048576,
    'PbMedianMiB': statistics.median(row['PrivateBytes'] for row in steady['Samples']) / 1048576,
    'PostFullGcWs160MiBPassed': False, 'PostFullGcPb100MiBPassed': True,
    'IdleCpuTotalSecondsDelta': max(row['ProcessCpuTotalSeconds'] for row in steady['Samples']) - min(row['ProcessCpuTotalSeconds'] for row in steady['Samples']),
    'ControlWorkflowExclusions': [
        'First stop click occurred after the ten-second response completed; its retained history is not cancellation evidence.',
        'Initial truncation-text entry was not visible; only the later visibly verified input and truncated response are acceptance evidence.',
        'During full host tests, transient host test windows appeared in Computer Use images; separately captured guest evidence and owned PID checks establish guest state.'
    ],
    'Screenshots': screenshots, 'WholeT040Complete': False, 'PublicWritePerformed': False,
}
(DEST / 'review.json').write_text(json.dumps(review, indent=2) + '\n', encoding='utf-8')
rows = [{'Path': path.relative_to(DEST).as_posix(), 'Bytes': path.stat().st_size,
         'Sha256': hashlib.sha256(path.read_bytes()).hexdigest()} for path in sorted(DEST.rglob('*')) if path.is_file()]
(DEST / 'binding.json').write_text(json.dumps({'Files': rows, 'WholeT040Complete': False}, indent=2) + '\n', encoding='utf-8')
for row in rows:
    assert hashlib.sha256((DEST / row['Path']).read_bytes()).hexdigest() == row['Sha256']
print(json.dumps({'BoundFiles': len(rows), 'UiScreenshots': len(screenshots), 'InstalledVersion': '0.2.13',
                  'WsMiB': review['WsMedianMiB'], 'PbMiB': review['PbMedianMiB'], 'WholeT040Complete': False}, indent=2))
