"""Archive the authorized one-chord test without exporting CU screenshots."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json

ROOT = Path('F:/Iris')
OUT = ROOT / 'tasks/T-040.evidence/computer-control-hotkey-recheck-20261108-1/independent-connection-trial'
matches = list((ROOT / 'artifacts/t040/manual-verification-20261107-3/output').glob('hotkey-state-20261108T103055*.json'))
if len(matches) != 1:
    raise RuntimeError('Exactly one previously reported readiness record required')
probe = matches[0]
raw = probe.read_bytes()
metadata = json.loads(raw.decode('utf-8-sig'))
active = [row for row in metadata['Sessions'] if not row['IsStopped'] and not row['IsHookUnhooked']]
if metadata['Pid'] != 7504 or len(active) != 1 or not active[0]['IsReady']:
    raise RuntimeError('Pre-chord readiness record differs from the parent-verified gate')
probe_copy = OUT / 'pre-chord-hotkey-state.json'
if probe_copy.exists():
    raise RuntimeError('Preserve earlier copied readiness bytes')
probe_copy.write_bytes(raw)
record = {
    'RecordedUtc': datetime.now(timezone.utc).isoformat(),
    'Scope': 'One child-owned official chord call and actual visible window change; not completed hotkey recording or selection-copy acceptance',
    'OfficialWrapper': 'C:/Users/28910/.codex/plugins/cache/openai-bundled/computer-use/26.609.41114/scripts/computer-use-client.mjs',
    'ActualConnectionCapabilities': {'Target': 'windows', 'HasRpc': True, 'HasNativePipeCreateConnection': False,
                                     'HasConfig': False, 'HasSetResponseMeta': False},
    'CanonicalWindowSelectedFromListApps': True,
    'CanonicalWindowId': 461388,
    'SandboxWindowResolution': '2034x1145',
    'PreChordReadinessSource': str(probe),
    'PreChordReadinessSha256': hashlib.sha256(raw).hexdigest(),
    'PreChordNativeReadyRecordIndependentlyReadByChild': True,
    'StoppedSessionExcludedFromReadinessGate': True,
    'PreChordActiveNativeSession': active[0],
    'BeforeChordUiStillRecording': True,
    'BeforeChordUiText': 'Preparing recording; recording paused; cancel button visible',
    'RequestedKey': 'Ctrl+Alt+d',
    'SpecifiedChordCallsPerformed': 1,
    'ChordStartedUtc': '2026-11-08T10:32:11.875Z',
    'ChordFinishedUtc': '2026-11-08T10:32:12.098Z',
    'ExplicitActivateBeforeThisChord': False,
    'ClickOrDragCallsPerformedByChild': 0,
    'SettingsSavedOrCancelledByChild': False,
    'AfterChordUi': {
        'InputTranslateBindingStill': 'Ctrl+Alt+F',
        'BindingStatus': 'Applied',
        'RecorderButton': 'Record',
        'RecordingNoLongerActive': True,
        'TranslationPopupOpened': True,
        'Source': 'Iris T040 synthetic translation',
        'SourceSelected': True,
        'PriorResultRetained': True,
        'PointerAppearedNearSettingsCenterRatherThanRecorderButton': True,
    },
    'ActualRecordedChordAvailable': False,
    'RecordingSucceeded': False,
    'SelectionClipboardCopySucceeded': False,
    'ExactDeliveredModifierCombinationProven': False,
    'PossibleAutomaticActivationMouseLeaveCancellation': True,
    'NativeOrRemoteRoutingCauseEstablished': False,
    'RootIndependentlyReportedOwnForegroundInputWindowBehavior': True,
    'UiPreparingDoesNotDetermineNativeReady': True,
    'ChildDecodedOrSavedCuScreenshot': False,
    'RootOwnsSeparateGuestCaptureAndReaderMetadata': True,
    'ChildNoFurtherInput': True,
    'DesktopControlReturnedToRoot': True,
    'SharedNodeReset': False, 'NativeHelperStartedOrStopped': False,
    'WrapperOrProductSourceChangedByChild': False,
}
dest = OUT / 'one-chord-observation.json'
if dest.exists():
    raise RuntimeError('Preserve prior observation')
dest.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'Output': str(dest), 'SpecifiedChordCalls': 1,
                  'RecordingSuccessClaimed': False, 'SelectionCopySuccessClaimed': False,
                  'RootHasControl': True}, indent=2))
