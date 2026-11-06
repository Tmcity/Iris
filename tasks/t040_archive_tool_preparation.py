"""Archive only the explicit T-040 tools, checks and their frozen outputs."""
from pathlib import Path
import datetime
import hashlib
import json
import shutil

ROOT=Path('F:/Iris')
SESSION=ROOT/'artifacts/t040/manual-verification-20261106-2'
OUTPUT=SESSION/'output'
REPORT=ROOT/'tasks/T-040.evidence/tool-preparation-20261107-1'
REPORT.mkdir(parents=True,exist_ok=False)
rows=[]

def copy(path,relative):
    destination=REPORT/relative
    destination.parent.mkdir(parents=True,exist_ok=True)
    raw=path.read_bytes()
    destination.open('xb').write(raw)
    rows.append({'OriginalPath':str(path),'Path':destination.relative_to(REPORT).as_posix(),
                 'Bytes':len(raw),'Sha256':hashlib.sha256(raw).hexdigest()})

for folder in ('runtime-counters-20261107-1','synthetic-provider-20261107-1','state-snapshot-20261107-1','ocr-capability-20261106-1'):
    for path in sorted((ROOT/'artifacts/t040/toolsource'/folder).iterdir()):
        if path.is_file() and path.suffix in ('.cs','.csproj'):
            copy(path,Path('source')/folder/path.name)
for pattern in ('*publish-20261107-*.log',):
    for path in sorted((ROOT/'artifacts/t040').glob(pattern)):
        copy(path,Path('build')/path.name)
for pattern in ('msi-signature-probe-*','msi-policy-probe-*','runtime-counters-*'):
    for folder in sorted(OUTPUT.glob(pattern)):
        for path in sorted(folder.iterdir()):
            if path.is_file(): copy(path,Path('checks')/folder.name/path.name)
for pattern in ('fixture-http-probe-*.json','counter-fixture-launch-*.json','fixture-launch-*.json','manual-input-validated-*.json'):
    for path in sorted(OUTPUT.glob(pattern)):
        copy(path,Path('checks')/path.name)
data_folder=sorted(OUTPUT.glob('feature-data-*'))[-1]
for path in sorted(data_folder.iterdir()):
    if path.is_file(): copy(path,Path('checks')/data_folder.name/path.name)
state_folder=sorted(OUTPUT.glob('readonly-state-*'))[-1]
for path in sorted(state_folder.iterdir()):
    if path.is_file(): copy(path,Path('checks')/state_folder.name/path.name)
fixture_folder=sorted(OUTPUT.glob('synthetic-provider-*'))[-1]
copy(fixture_folder/'state.json',Path('checks')/fixture_folder.name/'state.json')
copy(fixture_folder/'requests.jsonl',Path('checks')/fixture_folder.name/'requests-prefix.jsonl')
for name in ('Save-Clean-Idle-State.cmd','Save-Feature-State.cmd','Capture-Feature-View.cmd'):
    copy(SESSION/'input'/name,Path('manual-input')/name)
binary_manifest=[]
for folder in ('runtime-counters','synthetic-provider','state-snapshot','ocr-capability'):
    for path in sorted((SESSION/'input/tools'/folder).iterdir()):
        if path.is_file():
            raw=path.read_bytes()
            binary_manifest.append({'Path':path.relative_to(SESSION/'input').as_posix(),'Bytes':len(raw),'Sha256':hashlib.sha256(raw).hexdigest()})
http=json.loads(sorted(OUTPUT.glob('fixture-http-probe-*.json'))[-1].read_text(encoding='utf-8-sig'))
counter_folder=sorted(OUTPUT.glob('runtime-counters-*'))[-1]
counter=json.loads((counter_folder/'state.json').read_text(encoding='utf-8-sig'))
counter_events=[json.loads(line) for line in (counter_folder/'events.jsonl').read_text(encoding='utf-8').splitlines()]
assert http['Passed'] is True and http['OnlySyntheticLoopbackRequests'] is True
assert counter['Completed'] is True and counter['Failure'] is None
assert len(counter_events)==counter['Events']==191
assert counter_events[0]['TargetRole']=='OwnedSyntheticProvider'
assert any(event['Kind']=='RuntimeCounter' and event['Name']=='gen-2-gc-count' for event in counter_events)
state=json.loads((data_folder/'state.json').read_text(encoding='utf-8-sig'))
assert state['SqliteReadOnly'] and state['SecretsDecrypted'] is False and state['SqliteFailureCode'] is None
report={'Utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'Task':'T-040','TaskComplete':False,
        'Scope':'Helper preparation and validation only; no Iris UI action or installer execution.',
        'SyntheticHttpChecked':True,'RealProviderAccountsChecked':False,'IrisUiFeatureAcceptanceChecked':False,
        'CounterTargetWasSyntheticFixture':True,'IrisRuntimeTraceStarted':False,'IrisFullGcProved':False,
        'SnapshotReadOnlyCheckedAgainstNewSessionSchema2':True,'SnapshotHistoryEntriesAtCheck':state['HistoryTotal'],
        'PendingUserAction':'Manual clean-tray restart and 120-second idle checkpoint; no checkpoint received yet.',
        'BinaryManifest':binary_manifest,'Files':rows}
(REPORT/'binding.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
for row in rows:
    path=REPORT/row['Path']
    assert path.stat().st_size==row['Bytes'] and hashlib.sha256(path.read_bytes()).hexdigest()==row['Sha256']
print(json.dumps({'Report':str(REPORT),'Files':len(rows),'ToolBinaryManifestEntries':len(binary_manifest),'TaskComplete':False},indent=2))
