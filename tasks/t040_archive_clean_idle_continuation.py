"""Freeze current startup/idle feedback and preparation for the next real UI stage."""
from pathlib import Path
import datetime
import hashlib
import json

ROOT=Path('F:/Iris')
SESSION=ROOT/'artifacts/t040/manual-verification-20261106-2'
OUTPUT=SESSION/'output'
REPORT=ROOT/'tasks/T-040.evidence/clean-idle-continuation-20261107-1'
REPORT.mkdir(parents=True,exist_ok=False)
files=[]
def copy(path,relative,prefix=False):
    destination=REPORT/relative;destination.parent.mkdir(parents=True,exist_ok=True)
    raw=path.read_bytes()
    if prefix:raw=raw[:raw.rfind(b'\n')+1]
    destination.open('xb').write(raw)
    files.append({'OriginalPath':str(path),'Path':destination.relative_to(REPORT).as_posix(),'Bytes':len(raw),'Sha256':hashlib.sha256(raw).hexdigest(),'CompleteLinePrefix':prefix})
for pattern in ('runtime-observer-launch-*.json','stage1-input-ready-*.json'):
    for path in OUTPUT.glob(pattern):copy(path,Path('checks')/path.name)
actual=sorted(OUTPUT.glob('actual-helper-processes-*.json'))[-1]
copy(actual,Path('checks')/actual.name)
fixture=sorted(OUTPUT.glob('synthetic-provider-*/state.json'))[-1]
copy(fixture,Path('checks')/fixture.parent.name/fixture.name)
trace=sorted(OUTPUT.glob('runtime-counters-*/events.jsonl'))[-1]
copy(trace,Path('checks')/trace.parent.name/'events-prefix.jsonl',prefix=True)
for path in OUTPUT.glob('tool-transcript-20261107T0535*.txt'):
    copy(path,Path('checks')/path.name)
record={'Utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'Task':'T-040','Complete':False,
        'UserCleanIdleFeedbackReceived':True,'PriorManualRestartBlockerResolved':True,'GoalResumedByUser':True,
        'ActualAppPid':3032,'ActualMetricsObserverPid':2560,'ActualSyntheticServicePid':5824,'ActualGcObserverPid':6852,
        'FullGcSteadyMemoryProved':False,'Stage1ManualUiAcceptancePending':True,'Files':files}
(REPORT/'binding.json').write_text(json.dumps(record,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
for item in files:
    raw=(REPORT/item['Path']).read_bytes();assert len(raw)==item['Bytes'] and hashlib.sha256(raw).hexdigest()==item['Sha256']
print(json.dumps({'Report':str(REPORT),'Files':len(files),'PreviousManualBlockerResolved':True,'Complete':False},indent=2))
