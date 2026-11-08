"""Freeze modifier repair, OCR environment limits and actual native UIA acceptance."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json
import shutil

ROOT = Path('F:/Iris')
SESSION = ROOT / 'artifacts/t040/manual-verification-20261107-3'
OUT = SESSION / 'output'
DEST = ROOT / 'tasks/T-040.evidence/controlled-uia-acceptance-20261108-1'
START = datetime(2026, 11, 8, 6, 45, tzinfo=timezone.utc)
SOURCE = '57fc1ce837961faacd8c54951d2642e4967f9325'

def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def copy(path, relative):
    target = DEST / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    if path.is_dir():
        shutil.copytree(path, target)
    else:
        shutil.copy2(path, target)

installed = read(sorted(OUT.glob('controlled-candidate-installed-*.json'))[-1])
assert installed['Version'] == '0.2.14' and installed['SourceCommit'] == SOURCE and installed['AllFilesMatch']
assert installed['ExpectedFiles'] == 97
latest_data = sorted(OUT.glob('feature-data-*'))[-1]
data = read(latest_data / 'state.json')
assert data['HistoryTotal'] == 15 and data['SqliteFailureCode'] is None
assert any(row['source_text'] == 'Iris T040 native selection sample 013' and row['origin'] == 'Selection' for row in data['History'])
assert any(row['source_text'] == 'Iris T040 selection sample 013' and row['origin'] == 'Selection' for row in data['History'])
assert not any('protected sample' in row['source_text'] for row in data['History'])
baseline = read(ROOT / 'tasks/T-040.evidence/controlled-card-and-postgc-acceptance-20261108-1/review.json')
assert baseline['CurrentHistoryTotal'] == 13
assert data['Hashes'][0]['Sha256'] == '1ac857bb5c7d34cb40e1ddb440585ff23a0d3d0c9bb63afa3bfd0ed1a110f2ac'
assert data['Hashes'][1]['Sha256'] == '60d78472e3e206a670a708d39457527ced1dde4cd06736839808e3571b9fed4b'
validation_dir = ROOT / 'artifacts/t040/uia-source-proof-validation-20261108-1'
validation = read(validation_dir / 'validation-summary.json')
assert validation['SourceCommit'] == SOURCE and validation['FullTests'] == {'Total':3788,'Passed':3786,'Failed':0,'Skipped':2}
fixture_dir = sorted(OUT.glob('synthetic-provider-*'))[-1]
requests = [json.loads(line) for line in (fixture_dir / 'requests.jsonl').read_text(encoding='utf-8-sig').splitlines()]
translations = [row for row in requests if row.get('Kind') == 'Request']
assert len(translations) == 2
assert {row.get('SyntheticText') for row in translations} == {'Iris T040 selection sample 013','Iris T040 native selection sample 013'}
capability = read(sorted(OUT.glob('ocr-dotnet-probe-*.json'))[-1])
assert capability['LanguageCount'] == 0 and capability['ErrorType'] is None
DEST.mkdir(parents=True, exist_ok=False)
screenshots = []
for path in sorted(OUT.glob('controlled-ui-view-*.png.json')):
    record = read(path)
    if datetime.fromisoformat(record['Utc'].replace('Z','+00:00')) >= START:
        copy(path, Path('screenshots') / path.name)
        copy(Path(str(path)[:-5]), Path('screenshots') / Path(str(path)[:-5]).name)
        screenshots.append({'Path':path.name,'Utc':record['Utc'],'AppPid':record['AppPid']})
for path in sorted(OUT.glob('feature-data-*')):
    if datetime.fromisoformat(read(path / 'state.json')['Utc'].replace('Z','+00:00')) >= START:
        copy(path, Path('data-snapshots') / path.name)
for pattern in ('controlled-candidate-installed-*.json','controlled-r2r-helpers-*.json','ocr-dotnet-probe-*.json',
                'ocr-capability-state-*.json','uia-source-metadata-*.json','native-selection-fixture-*.json'):
    for path in OUT.glob(pattern):
        if datetime.fromisoformat(read(path)['Utc'].replace('Z','+00:00')) >= START:
            copy(path, Path('diagnostic-state') / path.name)
for folder in OUT.glob('owned-browser-uia-*'):
    copy(folder, Path('raw-uia-protocol') / folder.name)
copy(ROOT / 'tasks/T-040.evidence/computer-control-modifier-alias-20261108-1', Path('control-modifier-repair'))
copy(ROOT / 'tasks/T-040.evidence/uia-native-source-proof-defect-20261108-1', Path('defect-regression'))
copy(ROOT / 'tasks/T-040.evidence/uia-client-initialization-source-20261108-1', Path('public-reference-source'))
copy(validation_dir, Path('full-validation'))
for name in ('state.json','requests.jsonl','final-state.json'):
    if (fixture_dir / name).exists(): copy(fixture_dir / name, Path('fixture') / name)
package = ROOT / 'artifacts/t040/uia-source-proof-local-candidate-20261108-1'
for name in ('payload-manifest.json','release-provenance.json','SHA256.json','validation-summary.json'):
    copy(package / name, Path('candidate-metadata') / name)
for path in (ROOT / 'artifacts/t040/uia-source-proof-package-logs-20261108-1').glob('package.*'):
    copy(path, Path('package-execution') / path.name)
copy(SESSION / 'ui-samples-input', Path('synthetic-ui-samples'))
for folder in ('uia-source-metadata-20261108-1','native-selection-fixture-20261108-1'):
    source = ROOT / 'artifacts/t040/toolsource' / folder
    for path in source.iterdir():
        if path.is_file() and path.suffix in ('.cs','.csproj'): copy(path, Path('tool-source') / folder / path.name)
tool_names = ('t040_prepare_owned_ui_samples_20261108.py','t040_probe_current_ocr_capability_readonly.ps1',
    't040_probe_owned_browser_uia_readonly.ps1','t040_probe_owned_browser_source_metadata_readonly.ps1',
    't040_compare_native_uia_initialization_readonly.ps1','t040_compare_uia_identifier_initialization_readonly.ps1',
    't040_probe_native_password_metadata_readonly.ps1','t040_prepare_reviewed_candidate_input.py','t040_archive_uia_acceptance_20261108.py')
for name in tool_names: copy(ROOT / 'tasks' / name, Path('tool-source') / name)
for directory in (ROOT / 'tasks/T-040.evidence').glob('guest-*'):
    command = directory / 'command.json'
    if command.exists() and read(command).get('StartedUtc','') >= '20261108T064500':
        copy(directory, Path('guest-execution') / directory.name)
review = {'RecordedUtc':datetime.now(timezone.utc).isoformat(),'InstalledVersion':'0.2.14','InstalledSource':SOURCE,
    'InstalledPid':installed['AppPid'],'InstalledFilesMatch':97,'HistoryTotal':15,'OriginalDataAndSecretHashesPreserved':True,
    'ModifierAliasesRecordedCorrectlyAndActualOcrHotkeyTriggered':True,'AltLPathLimitationRetained':True,
    'ControlWrapperOrSdkChangedThisPhase':False,'ActualOcrRegionCaptureCompleted':True,'ActualLocalOcrSucceeded':False,
    'OcrLanguageCount':0,'OcrCapabilityDismQueryFailure':'0x80072ee6','SystemComponentsChanged':False,
    'OriginalBrowserSelectionFailureAndNativeUnsupportedProtocolPreserved':True,
    'UiAProviderPidMissingProvedByRawPropertyAndNativeControlComparison':True,
    'ManagedInitializationWasDiagnosticOnly':True,'ProductUsesWpfUia':False,
    'BeforeSourceProofRegression':{'Total':9,'Failed':4,'Passed':5},'AfterSourceProofRegression':{'Total':9,'Passed':9},
    'ActualNativeEditAndBrowserSelectionTranslationPassed':True,'ActualSyntheticBrowserPasswordRefused':True,
    'FixtureRequestsAfterFixedInstallation':len(translations),'SensitiveTextNotRequestedOrWrittenToHistory':True,
    'FullTests':validation['FullTests'],'ScreenshotRecords':screenshots,'WholeT040Complete':False,'PublicWritePerformed':False}
(DEST / 'review.json').write_text(json.dumps(review,indent=2)+'\n',encoding='utf-8')
rows = [{'Path':path.relative_to(DEST).as_posix(),'Bytes':path.stat().st_size,'Sha256':hashlib.sha256(path.read_bytes()).hexdigest()}
        for path in sorted(DEST.rglob('*')) if path.is_file()]
(DEST / 'binding.json').write_text(json.dumps({'Files':rows,'WholeT040Complete':False},indent=2)+'\n',encoding='utf-8')
for row in rows: assert hashlib.sha256((DEST / row['Path']).read_bytes()).hexdigest() == row['Sha256']
print(json.dumps({'BoundFiles':len(rows),'UiScreenshots':len(screenshots),'InstalledVersion':'0.2.14','WholeT040Complete':False},indent=2))
