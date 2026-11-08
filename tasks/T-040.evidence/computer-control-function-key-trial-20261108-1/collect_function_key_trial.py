"""Freeze exact evidence for the single official Ctrl+Alt+F5 trial."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json

ROOT = Path('F:/Iris')
SESSION = ROOT / 'artifacts/t040/manual-verification-20261107-3'
GUEST_OUTPUT = SESSION / 'output'
OUT = ROOT / 'tasks/T-040.evidence/computer-control-function-key-trial-20261108-1'
OUT.mkdir(parents=True, exist_ok=True)
if (OUT / 'binding.json').exists():
    raise RuntimeError('Preserve completed trial; do not replace it')

copied = []
def preserve(source, relative):
    source = Path(source)
    raw = source.read_bytes()
    dest = OUT / relative
    if dest.exists():
        raise RuntimeError('Preserve earlier copied evidence: ' + str(dest))
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_bytes(raw)
    copied.append({'SourcePath': str(source), 'Path': relative,
                   'Bytes': len(raw), 'Sha256': hashlib.sha256(raw).hexdigest()})

labels = [
    'function-key-before-record-view-20261108T112959901984Z',
    'function-key-before-record-key-state-20261108T112959911150Z',
    'function-key-recorded-result-view-20261108T113222776057Z',
    'function-key-cancelled-guest-view-20261108T113334922170Z',
    'function-key-cancelled-binding-state-20261108T113334922170Z',
    'function-key-after-cancel-key-state-20261108T113334903254Z',
]
for label in labels:
    archive = ROOT / 'tasks/T-040.evidence' / ('guest-' + label)
    completion = json.loads((archive / 'completion.json').read_text(encoding='utf-8'))
    if completion['RunnerExitCode'] != 0:
        raise RuntimeError('Original runner failure must be reviewed separately')
    for filename in ('command.json', 'command.log', 'completion.json'):
        preserve(archive / filename, 'commands/' + label + '/' + filename)
    preserve(GUEST_OUTPUT / ('tool-transcript-' + label + '.txt'),
             'transcripts/' + label + '.txt')

for role, stamp in [('before', '20261108T1130021238813Z'),
                    ('recorded', '20261108T1132248627943Z'),
                    ('cancelled', '20261108T1133372472586Z')]:
    filename = 'controlled-ui-view-' + stamp + '.png'
    preserve(GUEST_OUTPUT / filename, 'views/' + role + '.png')
    preserve(GUEST_OUTPUT / (filename + '.json'), 'views/' + role + '.png.json')

before_keys = GUEST_OUTPUT / 'keyboard-state-metadata-20261108T1130018458784Z.json'
after_keys = GUEST_OUTPUT / 'keyboard-state-metadata-20261108T1133370447865Z.json'
for role, path in [('before', before_keys), ('after', after_keys)]:
    keys = json.loads(path.read_text(encoding='utf-8-sig'))
    if any(row['Down'] for row in keys['Keys']):
        raise RuntimeError('The watched keys were not all released')
    preserve(path, 'key-state-' + role + '.json')

before_settings = GUEST_OUTPUT / 'feature-data-20261108T1102347752651Z/settings.json'
after_settings = GUEST_OUTPUT / 'feature-data-20261108T1133369012025Z/settings.json'
if before_settings.read_bytes() != after_settings.read_bytes():
    raise RuntimeError('Persisted settings differ; preserve and review actual difference')
settings = json.loads(after_settings.read_text(encoding='utf-8-sig'))
if settings['hotkeys']['InputTranslate'] != {'modifiers': 'Alt, Control', 'key': 'F', 'enabled': True}:
    raise RuntimeError('Original persistent input binding did not remain')
preserve(before_settings, 'settings-before.json')
preserve(after_settings, 'settings-after.json')

letter_trace = ROOT / 'tasks/T-040.evidence/controlled-clipboard-acceptance-20261108-1/diagnostic-execution/modifier-delivery-hook-20261108T1040224445396Z/result.json'
preserve(letter_trace, 'prior-letter-delivery-result.json')
for filename in ('t040_run_current_guest_tool.py', 't040_capture_controlled_view_readonly.ps1',
                 't040_keyboard_state_metadata_readonly.ps1', 't040_data_snapshot_readonly.ps1'):
    preserve(ROOT / 'tasks' / filename, 'tools/' + filename)

wrapper = Path('C:/Users/28910/.codex/plugins/cache/openai-bundled/computer-use/26.609.41114/scripts/computer-use-client.mjs')
result = {
    'RecordedUtc': datetime.now(timezone.utc).isoformat(),
    'Scope': 'Exactly one official function-key chord, observed actual recorder result, cancellation and unchanged persisted settings',
    'SandboxId': 'edf31417-4146-4ccb-9b48-feb98ccb6871',
    'CanonicalWindowId': 461388, 'SelectedFromListApps': True,
    'InstalledVersionObservedInUi': '0.2.15',
    'OfficialWrapper': str(wrapper),
    'CurrentWrapperSha256': hashlib.sha256(wrapper.read_bytes()).hexdigest(),
    'RequestedKey': 'Ctrl+Alt+F5',
    'FunctionKeyChordCallsPerformed': 1,
    'StartedUtc': '2026-11-08T11:31:24.675Z',
    'FinishedUtc': '2026-11-08T11:31:24.803Z',
    'ActualRecorderDisplay': 'Ctrl+F5',
    'ActualRecorderPreservedAlt': False,
    'InitialWatchedKeysAllReleased': True,
    'UiRecordingActiveBeforeChord': True,
    'PreparationWaitMilliseconds': 1100,
    'UiReadyTextUsedAsNativeReadinessProof': False,
    'KnownInitialReadyNotificationDefectReportedByParent': True,
    'ParentSourceFixInstalledDuringTrial': False,
    'SettingsPageCancelled': True,
    'SettingsSaved': False,
    'PersistentSettingsBytesUnchanged': True,
    'PersistentSettingsSha256': hashlib.sha256(after_settings.read_bytes()).hexdigest(),
    'OriginalPersistentInputBinding': settings['hotkeys']['InputTranslate'],
    'FinalWatchedKeysAllReleased': True,
    'IndependentGuestResultImageShowsCtrlF5': True,
    'IndependentGuestCancellationImageShowsSettingsAbsent': True,
    'HostTestWindowsAppearedInPostCancelCuScreenshot': True,
    'HostTestWindowsExcludedFromGuestEvidence': True,
    'OsKeyboardUsedForThisTrial': False,
    'NoOtherNewChordsOrTranslationsRequested': True,
    'WrapperOrProductSourceChangedByChild': False,
    'NativeHelperOrPrivateBridgeCreatedByChild': False,
    'CuScreenshotsDecodedOrWrittenByChild': False,
    'DirectAltDeliveryRepaired': False,
    'PrintableOnlyFailureHypothesisSupported': False,
    'NativeParserOrRemoteCauseEstablished': False,
    'DesktopControlReturnedToRoot': True,
}
(OUT / 'result.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
files = []
for path in sorted(OUT.rglob('*')):
    if path.is_file() and path.name != 'binding.json':
        raw = path.read_bytes()
        files.append({'Path': path.relative_to(OUT).as_posix(), 'Bytes': len(raw),
                      'Sha256': hashlib.sha256(raw).hexdigest()})
(OUT / 'binding.json').write_text(json.dumps({'Files': files, 'ExactOriginalCopies': copied}, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'Archive': str(OUT), 'BoundFiles': len(files), 'ExactOriginalCopies': len(copied),
                  'Requested': result['RequestedKey'], 'Recorded': result['ActualRecorderDisplay'],
                  'SettingsUnchanged': True, 'AltDeliveryRepaired': False}, indent=2))
