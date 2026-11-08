"""Freeze actual UI acceptance and the reproduced/fixed Closing defect."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import shutil
import subprocess

ROOT = Path('F:/Iris')
SESSION = ROOT / 'artifacts/t040/manual-verification-20261107-3'
OUT = SESSION / 'output'
DEST = ROOT / 'tasks/T-040.evidence/controlled-history-acceptance-20261108-1'
DEST.mkdir(parents=True, exist_ok=False)

def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def copy(path, relative):
    target = DEST / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    if path.is_dir():
        shutil.copytree(path, target)
    else:
        shutil.copy2(path, target)

start = datetime(2026, 11, 7, 20, 53, tzinfo=timezone.utc)
end = datetime(2026, 11, 7, 22, 8, tzinfo=timezone.utc)
records = []
for path in sorted(OUT.glob('controlled-ui-view-*.png.json')):
    record = read(path)
    stamp = datetime.fromisoformat(record['Utc'].replace('Z', '+00:00'))
    if start <= stamp <= end:
        copy(path, Path('screenshots') / path.name)
        copy(Path(str(path)[:-5]), Path('screenshots') / Path(str(path)[:-5]).name)
        records.append({'Path': path.name, 'Utc': record['Utc'], 'AppPid': record['AppPid'],
                        'Width': record['PixelWidth'], 'Height': record['PixelHeight']})
copy(ROOT / 'tasks/T-040.evidence/history-close-defect-20261108-1', Path('defect-regression'))
copy(ROOT / 'artifacts/t040/history-close-validation-20261108-1', Path('full-validation'))
for path in (ROOT / 'artifacts/t040/history-close-package-logs-20261108-1').glob('package.*'):
    copy(path, Path('package-execution') / path.name)
package = ROOT / 'artifacts/t040/history-close-local-candidate-20261108-1'
for name in ('payload-manifest.json','release-provenance.json','SHA256.json','validation-summary.json'):
    copy(package / name, Path('candidate-metadata') / name)
for path in sorted(OUT.glob('feature-data-*')):
    state = read(path / 'state.json')
    stamp = datetime.fromisoformat(state['Utc'].replace('Z', '+00:00')).astimezone(timezone.utc)
    if start <= stamp <= end:
        copy(path, Path('data-snapshots') / path.name)
for pattern in ('r2r-candidate-installed-*.json','controlled-candidate-installed-*.json','controlled-r2r-helpers-*.json'):
    for path in OUT.glob(pattern):
        copy(path, Path('state') / path.name)
for folder in OUT.glob('synthetic-provider-*'):
    state = read(folder / 'state.json')
    stamp = datetime.fromisoformat(state['Utc'].replace('Z', '+00:00')).astimezone(timezone.utc)
    if stamp >= start or state.get('Pid') == 1456:
        for name in ('state.json','requests.jsonl','final-state.json'):
            if (folder / name).exists(): copy(folder / name, Path('fixtures') / folder.name / name)
trace = OUT / 'runtime-counters-20261107T2057146684966Z'
copy(trace, Path('runtime-observation') / trace.name)
trace_state = read(trace / 'state.json')
assert trace_state['Completed'] and trace_state['Failure'] is None and trace_state['NaturalFullGcStarts'] == 0
summary = read(ROOT / 'artifacts/t040/history-close-validation-20261108-1/validation-summary.json')
assert summary['SourceCommit'] == 'b3072b2837e5b7d83b349d76c719ec9a323020b0'
assert summary['FullTests'] == {'Total':3776,'Passed':3774,'Failed':0,'Skipped':2}
last = sorted(OUT.glob('controlled-candidate-installed-*.json'))[-1]
installed = read(last)
assert installed['Version'] == '0.2.12' and installed['AllFilesMatch'] and installed['AppPid'] == 2524
data = read(sorted(OUT.glob('feature-data-*'))[-1] / 'state.json')
assert data['HistoryTotal'] == 7 and data['SqliteFailureCode'] is None
copy(last, Path('resumed-current') / last.name)
latest_helpers = sorted(OUT.glob('controlled-r2r-helpers-*.json'))[-1]
copy(latest_helpers, Path('resumed-current') / latest_helpers.name)
for name in ('t040_capture_controlled_view_readonly.ps1','t040_audit_controlled_r2r_helpers_readonly.ps1',
             't040_verify_controlled_candidate_installed_readonly.ps1','t040_prepare_history_close_fixed_input_20261108.py',
             't040_begin_controlled_r2r_observation_readonly.ps1','t040_run_history_close_regression_20261108.py',
             't040_verify_r2r_candidate_20261108.py','t040_archive_controlled_history_acceptance_20261108.py'):
    copy(ROOT / 'tasks' / name, Path('tool-source') / name)
verification = {
    'RecordedUtc': datetime.now(timezone.utc).isoformat(), 'Scope': 'Actual owned Sandbox UI and separately bound product source',
    'UiCandidateVersions': ['0.2.11','0.2.12'], 'InstalledFixedSource': installed['SourceCommit'],
    'InstalledFixedPid': installed['AppPid'], 'FixedPayloadFiles': 97,
    'OriginalFiveHistoryEntriesPreserved': True, 'TwoActualUiTranslationsAdded': True, 'CurrentHistoryEntries': 7,
    'ActualModelDiscoveryAndConnectionUiPassedAfterFixtureRecovery': True,
    'OriginalFixture1456ExpiredAt': '2026-11-07T16:48:47.9906143+00:00',
    'OriginalFixtureAbsentAndPortUnboundProvedBeforeRestart': True,
    'ActualInputAndClipboardTranslateUiPassed': True, 'ActualHistoryRefreshAndCopyUiPassed': True,
    'SourceAndResultCopyVerifiedByVisiblePureKeyboardPastes': True,
    'TypingBasedClipboardCheckExcluded': 'Computer Use type_text modified the clipboard; that check was not product copy evidence',
    'StaleInputCoordinateCheckExcluded': 'Input window hides on deactivation; subsequent blank-desktop keys were not copy evidence',
    'ActualNativeHistoryCloseFailureObservedOn0211': True,
    'DefectCause': 'Synchronous idle cleanup invoked Window.Close inside the original native Closing event',
    'BeforeRegression': {'Total':3,'Failed':2,'Passed':1}, 'AfterRegression': {'Total':3,'Failed':0,'Passed':3},
    'ActualFixedUiNativeCloseEscapeAndCloseButtonPassed': True,
    'ActualFixedUiReopenPassed': True, 'FullValidation': summary['FullTests'],
    'ObservedRuntimeTargetPid':7616, 'RuntimeObserverPid':7484, 'RuntimeObserverCompleted':True,
    'NaturalFullGcStarts':0, 'PostFullGcSteadyMemoryProved':False, 'WholeT040Complete':False,
    'PublicWritePerformed':False, 'ScreenshotRecords':records,
}
(DEST / 'review.json').write_text(json.dumps(verification, indent=2) + '\n', encoding='utf-8')
rows = [{'Path':path.relative_to(DEST).as_posix(),'Bytes':path.stat().st_size,'Sha256':hashlib.sha256(path.read_bytes()).hexdigest()}
        for path in sorted(DEST.rglob('*')) if path.is_file()]
(DEST / 'binding.json').write_text(json.dumps({'Files':rows,'WholeT040Complete':False},indent=2) + '\n', encoding='utf-8')
for row in rows:
    assert hashlib.sha256((DEST / row['Path']).read_bytes()).hexdigest() == row['Sha256']
print(json.dumps({'BoundFiles':len(rows),'UiScreenshots':len(records),'FixedCandidate':'0.2.12',
                  'ActualClosingDefectFixed':True,'HistoryEntries':7,'FullTests':summary['FullTests'],
                  'PostFullGcMemoryPending':True,'WholeT040Complete':False},indent=2))
