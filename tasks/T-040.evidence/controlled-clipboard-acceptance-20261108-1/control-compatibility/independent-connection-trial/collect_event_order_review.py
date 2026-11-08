"""Preserve the read-only event-order inspection and the zero-input boundary."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json

ROOT = Path('F:/Iris')
OUT = ROOT / 'tasks/T-040.evidence/computer-control-hotkey-recheck-20261108-1/independent-connection-trial'
sources = [
    ('src/Iris.Platform.Windows/Hotkey/HotkeyCaptureSession.cs', [(75, 88), (134, 155), (180, 196)]),
    ('src/Iris.Platform.Windows/Hotkey/HotkeyCaptureService.cs', [(123, 134), (194, 206)]),
    ('src/Iris.App/Hosting/HotkeyRecordingCoordinator.cs', [(177, 200)]),
    ('src/Iris.Core/Platform/IHotkeyCaptureService.cs', [(34, 49)]),
]
rows = []
for name, ranges in sources:
    path = ROOT / name
    raw = path.read_bytes()
    lines = raw.decode('utf-8-sig').splitlines()
    rows.append({'Path': name, 'Bytes': len(raw), 'Sha256': hashlib.sha256(raw).hexdigest(),
                 'Excerpts': [{'StartLine': start, 'EndLine': end,
                               'Text': '\n'.join(f'{i + 1}: {line}' for i, line in enumerate(lines)
                                                 if start - 1 <= i < end)} for start, end in ranges]})
record = {
    'RecordedUtc': datetime.now(timezone.utc).isoformat(),
    'Scope': 'Read-only source sequencing review; not an actual runtime Ready measurement',
    'Sources': rows,
    'InitialReadyRacePossible': True,
    'Sequence': [
        'InitializeHeldKeys may set _isReady=true and update Current, then writes progress channel',
        'Notification worker may consume channel before any ProgressChanged subscriber exists',
        'Physical initialization completion happens after InitializeHeldKeys',
        'BeginAsync returns session after physical initialization',
        'RecordingCoordinator subscribes only after await BeginAsync returns',
        'Coordinator then waits for Completion without an initial Current replay',
    ],
    'Qualification': 'Race is possible, not guaranteed; UI preparing text cannot establish actual native readiness or held-key state',
    'CurrentActualRaceEstablishedByChild': False,
    'CurrentNativeReadyEstablishedByChild': False,
    'SuggestedRemedyForParentReview': 'Subscribe, then replay Current with context completion and revision ordering guards',
    'ChildProductSourceChanged': False,
    'ChildTestsRun': False,
    'ChildSpecifiedChordCallsPerformed': 0,
    'ChildMovedMouseWithClickOrDrag': False,
    'InitialExplicitActivateWasPerformed': True,
    'ParentReportedInitialActivateMovedMouseAndCancelledRecording': True,
    'InitialTrialMustNotBeCountedAsIncompleteChord': True,
    'SecondPassiveUiObservations': ['正在准备录入…', '录入中（已暂停）'],
    'ParentTookBackDesktopControl': True,
    'ParentOwnsCurrentReadOnlyMetadataProbeAndAnyLaterChordTrial': True,
}
dest = OUT / 'event-order-review.json'
if dest.exists():
    raise RuntimeError('Preserve prior event-order audit')
dest.write_text(json.dumps(record, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
print(json.dumps({'Output': str(dest), 'PossibleRace': True, 'ActualNativeReadyMeasured': False,
                  'SpecifiedChordCallsPerformed': 0}, indent=2))
