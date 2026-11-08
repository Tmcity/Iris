"""Freeze actual clipboard read/restoration and bounded control diagnostics."""
from datetime import datetime, timezone
from pathlib import Path
import hashlib
import json
import shutil

ROOT = Path('F:/Iris')
SESSION = ROOT / 'artifacts/t040/manual-verification-20261107-3'
OUT = SESSION / 'output'
DEST = ROOT / 'tasks/T-040.evidence/controlled-clipboard-acceptance-20261108-1'
SOURCE = '8925eb3c7227181e8c3c6f2dffdb16a09799c8ff'
START = '2026-11-08T09:00:00+00:00'


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def utc(value):
    return datetime.fromisoformat(value.replace('Z', '+00:00'))


def in_phase(path):
    return utc(read(path)['Utc']) >= utc(START)


def copy(path, relative):
    target = DEST / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    if path.is_dir():
        shutil.copytree(path, target)
    else:
        shutil.copy2(path, target)


installed = read(sorted(OUT.glob('controlled-candidate-installed-*.json'))[-1])
assert installed['Version'] == '0.2.15' and installed['SourceCommit'] == SOURCE
assert installed['AllFilesMatch'] and installed['ExpectedFiles'] == 97
assert installed['AppPid'] == 7504 and not installed['PublicRelease']
data_dir = sorted(OUT.glob('feature-data-*'))[-1]
data = read(data_dir / 'state.json')
assert data['HistoryTotal'] == 18 and data['SqliteFailureCode'] is None
original_data = read(OUT / 'feature-data-20261108T1013090560272Z/state.json')
assert original_data['HistoryTotal'] == 17
by_id = {row['id']: row for row in data['History']}
assert all(by_id[row['id']] == row for row in original_data['History'])
latest = by_id[18]
assert latest['origin'] == 'Selection'
assert latest['source_text'] == 'Iris T040 native selection sample 013'
assert latest['result_text'] == 'Iris T040 synthetic translation'
settings = read(data_dir / 'settings.json')
assert settings['selection']['readOrder'] == ['UIAutomation', 'Clipboard']
assert settings['selection']['restoreClipboard'] and settings['selection']['clipboardTimeoutMs'] == 400
assert settings['selection']['mouseMode'] == 'Off' and settings['translation']['autoCopy'] == 'None'
assert next(row['Sha256'] for row in data['Hashes'] if row['Name'] == 'secrets.dat') == '60d78472e3e206a670a708d39457527ced1dde4cd06736839808e3571b9fed4b'
positive = read(OUT / 'selection-copy-state-20261108T1051289194443Z.json') if (OUT / 'selection-copy-state-20261108T1051289194443Z.json').exists() else None
positive_files = [p for p in OUT.glob('selection-copy-state-*.json') if read(p).get('Rows') and any(row.get('CandidatePresent') and row.get('Restored') for row in read(p)['Rows']) and utc(read(p)['Utc']) > utc('2026-11-08T10:50:00Z')]
assert len(positive_files) == 1
positive = read(positive_files[0])
assert positive['Pid'] == 7504 and not positive['Failures']
transaction = positive['Rows'][0]
assert transaction['SourcePid'] == 3364 and transaction['PermitPresent']
assert transaction['ClearVersionPresent'] and transaction['CandidatePresent'] and transaction['RestoreVersionPresent'] and transaction['Restored']
assert not transaction['SnapshotPresent'] and not transaction['TargetChanged']
negative = read(sorted(OUT.glob('selection-copy-state-*.json'))[-1])
assert negative['Pid'] == 7504 and negative['Rows'][0]['SourcePid'] == 3364
assert all(not negative['Rows'][0][name] for name in ('PermitPresent', 'ClearVersionPresent', 'CandidatePresent', 'SnapshotPresent'))
style = read(sorted(OUT.glob('native-password-style-*.json'))[-1])
assert style['SourcePid'] == 3364 and style['NativeEditPasswordStyle'] and not style['TextRead']
formats = [(p, read(p)) for p in sorted(OUT.glob('clipboard-format-metadata-*.json'))]
before_positive = [(p, record) for p, record in formats if utc('2026-11-08T10:46:00Z') < utc(record['Utc']) < utc(latest['created_at'])][-1]
after_positive = [(p, record) for p, record in formats if utc(latest['created_at']) < utc(record['Utc']) < utc('2026-11-08T10:54:00Z')][-1]
before_negative = [(p, record) for p, record in formats if utc('2026-11-08T10:54:00Z') <= utc(record['Utc']) < utc(style['Utc'])][-1]
after_negative = formats[-1]
assert {row['Id'] for row in before_positive[1]['Formats']} == {13, 16, 1, 7}
assert before_negative[1]['Sequence'] == after_negative[1]['Sequence'] == 568
fixture = sorted(OUT.glob('synthetic-provider-*'))[-1]
requests = [json.loads(line) for line in (fixture / 'requests.jsonl').read_text(encoding='utf-8-sig').splitlines()]
actual_requests = [row for row in requests if row['Kind'] == 'Request']
assert len(actual_requests) == 2
assert {row['SyntheticText'] for row in actual_requests} == {'Iris T040 clipboard fixed 015', 'Iris T040 native selection sample 013'}
assert read(fixture / 'final-state.json')['Requests'] == 2
validation_dir = ROOT / 'artifacts/t040/clipboard-observation-validation-20261108-1'
validation = read(validation_dir / 'validation-summary.json')
assert validation['SourceCommit'] == SOURCE and validation['FullTests'] == {'Total': 3792, 'Passed': 3790, 'Failed': 0, 'Skipped': 2}

DEST.mkdir(parents=True, exist_ok=False)
screenshots = []
for path in sorted(OUT.glob('controlled-ui-view-*.png.json')):
    if in_phase(path):
        copy(path, Path('screenshots') / path.name)
        png = Path(str(path)[:-5])
        copy(png, Path('screenshots') / png.name)
        screenshots.append({'Path': png.name, 'Utc': read(path)['Utc'], 'AppPid': read(path)['AppPid']})
for folder in OUT.glob('feature-data-*'):
    if in_phase(folder / 'state.json'):
        copy(folder, Path('data-snapshots') / folder.name)
for folder in OUT.glob('readonly-state-*'):
    if in_phase(folder / 'state.json'):
        copy(folder, Path('process-and-original-logs') / folder.name)
patterns = ('controlled-candidate-installed-*.json', 'controlled-r2r-helpers-*.json', 'clipboard-format-metadata-*.json',
            'selection-copy-state-*.json', 'keyboard-state-metadata-*.json', 'hotkey-state-*.json',
            'foreground-identity-*.json', 'native-password-style-*.json')
for pattern in patterns:
    for path in OUT.glob(pattern):
        if in_phase(path):
            copy(path, Path('diagnostic-state') / path.name)
for pattern in ('modifier-delivery-*', 'selection-copy-probe-*', 'hotkey-probe-*'):
    for folder in OUT.glob(pattern):
        copy(folder, Path('diagnostic-execution') / folder.name)
for folder in (ROOT / 'tasks/T-040.evidence').glob('guest-*'):
    command = folder / 'command.json'
    if command.exists() and read(command).get('StartedUtc', '') >= '20261108T090000':
        copy(folder, Path('guest-execution') / folder.name)
for path in OUT.glob('tool-transcript-*.txt'):
    if '20261108T' in path.name and path.name.split('20261108T')[-1][:6] >= '090000':
        copy(path, Path('guest-transcripts') / path.name)
for name in ('state.json', 'requests.jsonl', 'final-state.json'):
    copy(fixture / name, Path('fixture') / name)
copy(ROOT / 'tasks/T-040.evidence/clipboard-version-observation-defect-20261108-1', Path('defect-regression'))
copy(ROOT / 'tasks/T-040.evidence/computer-control-hotkey-recheck-20261108-1', Path('control-compatibility'))
copy(validation_dir, Path('full-validation'))
package = ROOT / 'artifacts/t040/clipboard-observation-local-candidate-20261108-1'
for name in ('payload-manifest.json', 'release-provenance.json', 'SHA256.json', 'validation-summary.json'):
    copy(package / name, Path('candidate-metadata') / name)
copy(ROOT / 'artifacts/t040/clipboard-observation-package-logs-20261108-1', Path('package-execution'))
for folder in ('selection-copy-state-20261108-1', 'hotkey-state-20261108-1', 'uia-source-metadata-20261108-1'):
    for path in (ROOT / 'artifacts/t040/toolsource' / folder).iterdir():
        if path.is_file() and path.suffix in ('.cs', '.csproj'):
            copy(path, Path('tool-source') / folder / path.name)
for name in ('t040_run_current_guest_tool.py', 't040_clipboard_format_metadata_readonly.ps1',
             't040_keyboard_state_metadata_readonly.ps1', 't040_probe_selection_copy_state_readonly.ps1',
             't040_request_selection_cli_diagnostic.ps1', 't040_probe_hotkey_state_readonly.ps1',
             't040_modifier_delivery_worker_readonly.ps1', 't040_observe_modifier_delivery_readonly.ps1',
             't040_keyboard_delivery_worker_readonly.ps1', 't040_start_keyboard_delivery_observer_readonly.ps1',
             't040_foreground_identity_readonly.ps1', 't040_probe_native_password_style_readonly.ps1',
             't040_probe_native_password_metadata_readonly.ps1', 't040_archive_clipboard_acceptance_20261108.py'):
    copy(ROOT / 'tasks' / name, Path('tool-source') / name)
review = {
    'RecordedUtc': datetime.now(timezone.utc).isoformat(), 'InstalledVersion': '0.2.15', 'InstalledSource': SOURCE,
    'InstalledPid': 7504, 'InstalledCreatedUtc': installed['AppCreatedUtc'], 'InstalledFilesMatch': 97,
    'OriginalSeventeenHistoryRowsUnchanged': True, 'CurrentHistoryTotal': 18, 'OriginalSecretHashPreserved': True,
    'ActualClipboardOnlyNativeSelectionReadAndTranslationPassed': True,
    'ActualClipboardRestorationVerifiedByPureKeyboardPaste': True,
    'ClipboardBaselineWasDistinctFromSelectedNativeText': True,
    'NoTypeTextBetweenBaselineAndRestorationVerification': True,
    'PositiveReaderTransactionFile': positive_files[0].name,
    'PositiveClipboardVersionBefore': before_positive[1]['Sequence'],
    'PositiveClipboardVersionAfter': after_positive[1]['Sequence'],
    'ActualNativeSyntheticPasswordRefused': True, 'PasswordClipboardVersionBeforeAndAfter': 568,
    'PasswordRequestAndHistoryCountsUnchanged': True,
    'LastKeyboardTaskInNegativeProbeWasPreviousPositiveTaskNotANewInjection': True,
    'OldUiaMetadataProbeRejectedInstalled015ByIts013VersionGuard': True,
    'NativePasswordStyleProbeDidNotReadUiaTextOrClipboard': True,
    'UiAPriorityReadOrderRestoredAndSaved': True,
    'OriginalOpenBusyFailureAndFourBeforeRegressionFailuresPreserved': True,
    'AfterRegressionPassed': 4, 'FullTests': validation['FullTests'],
    'CurrentSandboxEarlyAltReleaseObserved': True,
    'NativeControlShortcutInputFullyRepaired': False,
    'GenericAliasesModifierOrderRightAltFullScreenAndFreshConnectionTrialsRetained': True,
    'OfficialComputerUseMouseControlOfBuiltInGuestOskSuccessfullyTriggeredFAndD': True,
    'FirstOskDTrialWithOwnIrisForegroundExcludedFromCopyAcceptance': True,
    'InitialNativeHotkeyReadyButUiPreparingNotificationGapObserved': True,
    'HotkeyInitialProgressDefectStillOpen': True,
    'FixtureRequests': 2, 'FixtureCompletedByItsOneHourLifetime': True,
    'ScreenshotRecords': screenshots, 'FormalMemoryOrLatencyResult': False,
    'WholeT040Complete': False, 'PublicWritePerformed': False,
}
(DEST / 'review.json').write_text(json.dumps(review, indent=2) + '\n', encoding='utf-8')
rows = [{'Path': path.relative_to(DEST).as_posix(), 'Bytes': path.stat().st_size,
         'Sha256': hashlib.sha256(path.read_bytes()).hexdigest()}
        for path in sorted(DEST.rglob('*')) if path.is_file()]
(DEST / 'binding.json').write_text(json.dumps({'Files': rows, 'WholeT040Complete': False}, indent=2) + '\n', encoding='utf-8')
for row in rows:
    assert hashlib.sha256((DEST / row['Path']).read_bytes()).hexdigest() == row['Sha256']
print(json.dumps({'BoundFiles': len(rows), 'UiScreenshots': len(screenshots),
                  'InstalledVersion': '0.2.15', 'ClipboardReadRestoreAndNativePasswordRefusalPassed': True,
                  'WholeT040Complete': False}, indent=2))
