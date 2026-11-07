"""Archive the measured failure, causal reference chain and menu lifetime regression."""
from pathlib import Path
import datetime
import hashlib
import json
import statistics
import xml.etree.ElementTree as ET

ROOT=Path('F:/Iris')
SESSION=ROOT/'artifacts/t040/manual-verification-20261106-2'
OUT=SESSION/'output'
REPORT=ROOT/'tasks/T-040.evidence/memory-failure-fix-review-20261107-1'
REPORT.mkdir(parents=True,exist_ok=False)
files=[]
def copy(path,relative,prefix=False):
    dst=REPORT/relative;dst.parent.mkdir(parents=True,exist_ok=True)
    raw=path.read_bytes()
    if prefix:raw=raw[:raw.rfind(b'\n')+1]
    dst.open('xb').write(raw)
    files.append({'OriginalPath':str(path),'Path':dst.relative_to(REPORT).as_posix(),'Bytes':len(raw),'Sha256':hashlib.sha256(raw).hexdigest(),'CompleteLinePrefix':prefix})
checkpoint=sorted(OUT.glob('post-gc-steady-checkpoint-*.json'))[-1]
record=json.loads(checkpoint.read_text(encoding='utf-8-sig'))
assert record['AppPid']==3032 and record['VisibleIrisWindowCount']==0 and record['TraceProcessAbsent'] and record['NaturalFullGcStarts']==1
copy(checkpoint,Path('failure')/checkpoint.name)
state_folder=OUT/'readonly-state-20261107T0627161639808Z'
for path in state_folder.iterdir():
    if path.is_file():copy(path,Path('failure')/state_folder.name/path.name)
for path in OUT.glob('stage1-view-metadata-20261107T062715*.json'):copy(path,Path('failure')/path.name)
raw=(OUT/'metrics-c4cdd9b216e34b3e90ef4c889256b95a/samples.jsonl').read_bytes()
raw=raw[:raw.rfind(b'\n')+1]
rows=[json.loads(x) for x in raw.decode().splitlines()]
end=datetime.datetime.fromisoformat(record['Utc'].replace('Z','+00:00'))
samples=[r for r in rows if r['Kind']=='ProcessSample' and r['Pid']==3032 and 0<=(end-datetime.datetime.fromisoformat(r['ObservedUtc'].replace('Z','+00:00'))).total_seconds()<=45]
assert len(samples)==22
stats={'Samples':len(samples),'WorkingSetMedianMiB':statistics.median(x['WorkingSetBytes'] for x in samples)/1048576,'WorkingSetMaxMiB':max(x['WorkingSetBytes'] for x in samples)/1048576,'PrivateBytesMedianMiB':statistics.median(x['PrivateBytes'] for x in samples)/1048576,'ProcessCpuMaxPercent':max(x['ProcessCpuPercentage'] for x in samples)}
assert stats['WorkingSetMedianMiB']>160
(REPORT/'failure'/'samples-45s.json').write_text(json.dumps(samples,indent=2)+'\n',encoding='utf-8')
for pattern in ('memory-map-20261107*.json','heap-metadata-*.json'):
    for path in sorted(OUT.glob(pattern)):copy(path,Path('diagnostics')/path.name)
for folder in sorted(OUT.glob('jit-rundown-*')):
    for path in folder.iterdir():
        if path.is_file():copy(path,Path('diagnostics')/folder.name/path.name)
for folder in ('jit-rundown-20261107-1','heap-metadata-20261107-1'):
    for path in (ROOT/'artifacts/t040/toolsource'/folder).iterdir():
        if path.is_file() and path.suffix in ('.cs','.csproj'):copy(path,Path('tool-source')/folder/path.name)
for pattern in ('heap-metadata-publish-20261107-*.log','jit-rundown-publish-20261107-*.log','editing-menu-lifetime-*.log','memory-fix-release-build-20261107-1.log','memory-fix-full-tests-20261107-1.log'):
    for path in sorted((ROOT/'artifacts/t040').glob(pattern)):copy(path,Path('validation')/path.name)
menu=ET.parse(ROOT/'src/Iris.App/Resources/EditingMenus.xaml').getroot()[0]
assert menu.attrib['{http://schemas.microsoft.com/winfx/2006/xaml}Shared']=='False'
heap=json.loads(sorted(OUT.glob('heap-metadata-*.json'))[-1].read_text(encoding='utf-8-sig'))
assert any(p['CodePropertyName']=='IWindowService' for p in heap['CodePropertyNames'])
assert any(p['TargetType']=='Iris.App.Settings.SettingsWindow' for p in heap['ClosedWindowRootPaths'])
binding={'Utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'Task':'T-040','Complete':False,
         'FailureStatistics':stats,'NaturalFullGcAndClosedWindowsProved':True,'OldFullGcSteadyWorkingSetPassed':False,
         'ClosedSettingsWindowStrongReferenceProved':True,'RetainingProperty':'IWindowService',
         'ReferenceChain':'App -> Bootstrapper -> TranslateWindowPresenter -> hidden TranslateWindow -> ComboBox -> shared ContextMenu -> closed SettingsWindow',
         'Fix':'Application-level DefaultControlContextMenu resource becomes non-shared while preserving Copy/Cut/Paste routed commands',
         'SharedMenuRegressionCounterexample':'2 failed, 0 passed','NonSharedMenuRegression':'2 passed, 0 failed',
         'ReleaseBuild':'0 warnings, 0 errors','FullTests':{'Total':3773,'Passed':3771,'Failed':0,'ExistingEnvironmentSkipped':2},
         'ActualCandidateInstallationAndNfrRetestPending':True,'NoPublicReleaseAuthorized':True,'Files':files}
(REPORT/'binding.json').write_text(json.dumps(binding,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
for row in files:
    raw=(REPORT/row['Path']).read_bytes();assert len(raw)==row['Bytes'] and hashlib.sha256(raw).hexdigest()==row['Sha256']
print(json.dumps({'Report':str(REPORT),'FailureWorkingSetMiB':stats['WorkingSetMedianMiB'],'RetainingProperty':'IWindowService','RawFiles':len(files),'CandidateRetestPending':True},indent=2))
