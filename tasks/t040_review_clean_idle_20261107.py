"""Freeze the user-triggered clean-tray restart, retaining the unresolved full-GC requirement."""
from pathlib import Path
import datetime as dt
import hashlib
import json
import statistics

ROOT=Path('F:/Iris')
SESSION=ROOT/'artifacts/t040/manual-verification-20261106-2'
OUT=SESSION/'output'
REPORT=ROOT/'tasks/T-040.evidence/clean-idle-review-20261107-1'
REPORT.mkdir(parents=True,exist_ok=False)
files=[]
def read(path):return json.loads(path.read_text(encoding='utf-8-sig'))
def instant(value):return dt.datetime.fromisoformat(value.replace('Z','+00:00'))
def copy(path,relative=None,prefix=False):
    destination=REPORT/'originals'/(relative or path.relative_to(SESSION))
    destination.parent.mkdir(parents=True,exist_ok=True)
    raw=path.read_bytes()
    if prefix:raw=raw[:raw.rfind(b'\n')+1]
    destination.open('xb').write(raw)
    files.append({'OriginalPath':str(path),'Path':destination.relative_to(REPORT).as_posix(),'Bytes':len(raw),'Sha256':hashlib.sha256(raw).hexdigest(),'CompleteLinePrefix':prefix})
    return destination
checkpoint=sorted(OUT.glob('clean-idle-checkpoint-*.json'))[-1]
saved=read(checkpoint)
assert len(saved['Processes'])==1
pid=saved['Processes'][0]['ProcessId']
assert pid==3032 and saved['ObserverPid']==2560
assert saved['ObserverSourceSha256']=='37bfd2c5fb2858df68513c1151495c849442543094e9af7c2689c5281cfe5b32'
copy(checkpoint)
metrics=OUT/'metrics-c4cdd9b216e34b3e90ef4c889256b95a'
frozen=copy(metrics/'samples.jsonl',prefix=True)
events=[json.loads(line) for line in frozen.read_text(encoding='utf-8').splitlines()]
old_exit=next(e for e in events if e['Kind']=='ProcessExited' and e['Pid']==1804)
start=next(e for e in events if e['Kind']=='ProcessFirstObserved' and e['Pid']==pid)
ready=next(e for e in events if e['Kind']=='StartupLogCorrelated' and e['Pid']==pid)
created=instant(start['StartedUtc'])
assert old_exit['ExitCode']==0 and (created-instant(old_exit['ExitUtc'])).total_seconds()>=5
assert ready['CreationToBootstrapReadyMs']<1500 and start['SystemCpuLoadPercentage']<30
elapsed=(instant(saved['Utc'])-created).total_seconds()
assert elapsed>=120
samples=[e for e in events if e['Kind']=='ProcessSample' and e['Pid']==pid and instant(e['ObservedUtc'])<=instant(saved['Utc'])]
windows=[]
for lo,hi in ((30,60),(60,120),(120,elapsed+1)):
    selected=[e for e in samples if lo<=(instant(e['ObservedUtc'])-created).total_seconds()<hi]
    assert selected
    windows.append({'SinceActualProcessCreationSeconds':[lo,hi],'Samples':len(selected),
                    'WorkingSetMedianMiB':statistics.median(e['WorkingSetBytes'] for e in selected)/1048576,
                    'WorkingSetMaxMiB':max(e['WorkingSetBytes'] for e in selected)/1048576,
                    'PrivateBytesMedianMiB':statistics.median(e['PrivateBytes'] for e in selected)/1048576,
                    'PrivateBytesMaxMiB':max(e['PrivateBytes'] for e in selected)/1048576,
                    'ProcessCpuMaxPercent':max(e['ProcessCpuPercentage'] for e in selected)})
assert all(w['WorkingSetMaxMiB']<=160 and w['PrivateBytesMaxMiB']<=100 for w in windows)
for path in sorted(metrics.glob('saved-state-20261107T0532*.json')):copy(path)
copy(metrics/'status.json')
state_folder=OUT/'readonly-state-20261107T0532000744828Z'
state=read(state_folder/'state.json')
assert state['InstalledSource']['Version']=='0.2.10' and state['ObserverStatus']['Ready']
assert state['ProductMetrics'][0]['Pid']==pid and state['ProductMetrics'][0]['MainWindowHandle']==0
for path in state_folder.iterdir():
    if path.is_file():copy(path)
actual=sorted(OUT.glob('actual-helper-processes-*.json'))[-1]
copy(actual)
fixture_final=OUT/'synthetic-provider-20261106T2157094871052Z'/'final-state.json'
if not fixture_final.exists():fixture_final=sorted(OUT.glob('synthetic-provider-*/final-state.json'))[-1]
copy(fixture_final)
binding={'Utc':dt.datetime.now(dt.timezone.utc).isoformat(),'Task':'T-040','Complete':False,
         'UserFeedback':{'QuestionCall':'call_dqLWzpCKHwctnSuvWSJ7qS7L','Raw':'已记录','TaskInstructionsReportedCompleted':True},
         'OldAppExit':old_exit,'NewAppPid':pid,'NewAppCreatedUtc':start['StartedUtc'],'Startup':ready,
         'OriginalFirstObservationCpuPercent':start['SystemCpuLoadPercentage'],'CheckpointElapsedSeconds':elapsed,
         'StatisticsTimeBasis':'ObservedUtc minus actual process creation; legacy SinceCreatedSeconds is a loop-start timestamp and can precede memory reads.',
         'Windows':windows,'Version':'0.2.10','CheckpointFunctionalWindowHandle':0,
         'CleanTrayMeasuredWindowWithinLimits':True,'FullGcSteadyMemoryProved':False,'WholeNfrPassed':False,
         'Prior171MiBFailurePreserved':True,'PriorLateMemoryCauseProved':False,'ManualRestartBlockerResolved':True,
         'Next':'Actual stage1 synthetic service/input/history UI acceptance; observe natural GC without using trace-active memory as formal idle readings.',
         'Files':files}
(REPORT/'binding.json').write_text(json.dumps(binding,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'Report':str(REPORT),'OldAppExitCode':0,'NewAppPid':pid,'CreationToReadyMs':ready['CreationToBootstrapReadyMs'],
                 'StartupObservedCpuPercent':start['SystemCpuLoadPercentage'],'CheckpointElapsedSeconds':elapsed,'Windows':windows,
                 'FullGcSteadyMemoryProved':False,'ManualBlockerResolved':True},indent=2))
