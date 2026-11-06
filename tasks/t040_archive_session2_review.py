"""Freeze the received install/startup evidence and preserve unresolved NFR conditions."""
from pathlib import Path
from collections import Counter
import datetime as dt
import hashlib
import json
import re
import shutil
import statistics
import xml.etree.ElementTree as ET

ROOT=Path('F:/Iris')
SESSION=ROOT/'artifacts/t040/manual-verification-20261106-2'
OUT=SESSION/'output'
REPORT=ROOT/'tasks/T-040.evidence/session2-review-20261107-1'
REPORT.mkdir(parents=True,exist_ok=False)
files=[]

def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def freeze(path, relative=None, live_prefix=False):
    relative=relative or path.relative_to(SESSION)
    destination=REPORT/'originals'/relative
    destination.parent.mkdir(parents=True,exist_ok=True)
    raw=path.read_bytes()
    if live_prefix:
        raw=raw[:raw.rfind(b'\n')+1]
    destination.open('xb').write(raw)
    files.append({'OriginalPath':str(path),'ArchivePath':destination.relative_to(REPORT).as_posix(),
                  'Bytes':len(raw),'Sha256':hashlib.sha256(raw).hexdigest(),'LiveCompleteLinePrefix':live_prefix})
    return destination

v1=OUT/'metrics-3799919aaaaa40ae8babeb9826d3ba9f'
v2=OUT/'metrics-c4cdd9b216e34b3e90ef4c889256b95a'
v1_status=read_json(v1/'status.json')
assert v1_status['Ready'] is False and v1_status['Failure'] is None
v1_copy=freeze(v1/'samples.jsonl')
v1_events=[json.loads(line) for line in v1_copy.read_text(encoding='utf-8').splitlines() if line]
assert len(v1_events)==v1_status['Samples']==4199
assert v1_events[-1]['Kind']=='ObserverStopped'
freeze(v1/'status.json')
for saved in sorted(v1.glob('saved-state-*.json')):
    freeze(saved)
v2_copy=freeze(v2/'samples.jsonl',live_prefix=True)
v2_events=[json.loads(line) for line in v2_copy.read_text(encoding='utf-8').splitlines() if line]
freeze(v2/'status.json')
for saved in sorted(v2.glob('saved-state-*.json')):
    freeze(saved)
for pointer in ('active-metrics.json','active-metrics-v1-preserved.json'):
    freeze(OUT/pointer)
for path in sorted((OUT/'installed-20261106T1746402597639Z').iterdir()):
    if path.is_file(): freeze(path)
for pattern in ('signature-diagnostic-enabled-*.json','installed-payload-*.json','ocr-dotnet-probe-*.json',
                'manual-input-validated-*.json','environment-*.json','tool-transcript-*.txt'):
    for path in sorted(OUT.glob(pattern)): freeze(path)
latest_state=sorted(OUT.glob('readonly-state-*'))[-1]
for path in latest_state.iterdir():
    if path.is_file(): freeze(path)
system_folder=sorted(OUT.glob('install-system-events-*'))[-1]
for path in system_folder.iterdir():
    if path.is_file(): freeze(path)
for name in ('observe-process-metrics-v1-original.ps1.txt','observe-process-metrics-v2-readonly.ps1',
             'save-metrics-state-v2-readonly.ps1','start-metrics-observer-v2.ps1',
             'official-0210-payload-manifest.json','Save-Clean-Idle-State.cmd'):
    freeze(SESSION/'input'/name)

def window_metrics(events,pid,lo,hi):
    samples=[e for e in events if e['Kind']=='ProcessSample' and e['Pid']==pid and lo<=e['SinceCreatedSeconds']<hi]
    if not samples: return {'Samples':0}
    return {'Samples':len(samples),'WorkingSetMedianMiB':statistics.median(e['WorkingSetBytes'] for e in samples)/1048576,
            'WorkingSetMaxMiB':max(e['WorkingSetBytes'] for e in samples)/1048576,
            'PrivateBytesMedianMiB':statistics.median(e['PrivateBytes'] for e in samples)/1048576,
            'ProcessCpuMaxPercentage':max(e['ProcessCpuPercentage'] for e in samples),
            'FirstObservedUtc':samples[0]['ObservedUtc'],'LastObservedUtc':samples[-1]['ObservedUtc']}

startup_events=[e for e in v2_events if e['Kind']=='StartupLogCorrelated' and e['Pid']==1804]
assert len(startup_events)==1
startup=startup_events[0]
startup_first=next(e for e in v1_events if e['Kind']=='ProcessFirstObserved' and e['Pid']==1804)
assert startup_first['SystemCpuLoadPercentage']==9
startup['CpuAtOriginalFirstObservation']=startup_first['SystemCpuLoadPercentage']
startup['LaterObserverCpuIsNotStartupCpu']=True
startup['FirstObservationIsNotContinuousCpuCoverage']=True
memory30=window_metrics(v1_events,1804,30,60)
memory_late=window_metrics(v1_events,1804,120,100000)
samples1804=[e for e in v1_events if e['Kind']=='ProcessSample' and e['Pid']==1804]
first_high=next(e for e in samples1804 if e['WorkingSetBytes']>160*1048576)

install_dir=OUT/'installed-20261106T1746402597639Z'
msi_rows=[]
for path in sorted(install_dir.glob('*_00*.log')):
    raw=path.read_bytes()
    text=raw.decode('utf-16' if raw.startswith(b'\xff\xfe') else 'utf-8-sig')
    starts=[line for line in text.splitlines() if 'SOFTWARE RESTRICTION POLICY: Verifying package' in line]
    ends=[line for line in text.splitlines() if 'SOFTWARE RESTRICTION POLICY:' in line and 'has a digital signature' in line]
    if not starts: continue
    assert len(starts)==len(ends)==1
    def timestamp(line):
        value=re.search(r'\[(\d{2}:\d{2}:\d{2}:\d{3})\]',line).group(1)
        return dt.datetime.strptime('2026-11-07 '+value,'%Y-%m-%d %H:%M:%S:%f').replace(tzinfo=dt.timezone(dt.timedelta(hours=8)))
    start,end=timestamp(starts[0]),timestamp(ends[0])
    msi_rows.append({'File':path.name,'Group':re.search(r'_(20261107\d{6})_00',path.name).group(1),
                     'StartUtc':start.astimezone(dt.timezone.utc).isoformat(),
                     'EndUtc':end.astimezone(dt.timezone.utc).isoformat(),
                     'SignaturePolicyWaitSeconds':(end-start).total_seconds(),'StartLine':starts[0],'EndLine':ends[0]})
groups={group:sum(row['SignaturePolicyWaitSeconds'] for row in msi_rows if row['Group']==group) for group in {r['Group'] for r in msi_rows}}
assert len(msi_rows)==8 and len(groups)==2

ns={'e':'http://schemas.microsoft.com/win/2004/08/events/event'}
applocker=[]
for event in ET.parse(system_folder/'Microsoft-Windows-AppLocker-MSI_and_Script.xml').getroot():
    system=event.find('e:System',ns)
    data={node.get('Name'):node.text for node in event.findall('e:EventData/e:Data',ns)}
    applocker.append({'EventId':int(system.find('e:EventID',ns).text),'Utc':system.find('e:TimeCreated',ns).get('SystemTime'),
                      'Pid':int(system.find('e:Execution',ns).get('ProcessID')),
                      'Data':data})
assert len(applocker)==16

capi=ET.parse(install_dir/'capi2-events.xml').getroot()
capi_summary=[]
for event in capi:
    system=event.find('e:System',ns)
    if system is None: continue
    capi_summary.append({'Utc':system.find('e:TimeCreated',ns).get('SystemTime'),
                         'EventId':system.find('e:EventID',ns).text,
                         'Pid':system.find('e:Execution',ns).get('ProcessID')})

payload=read_json(sorted(OUT.glob('installed-payload-*.json'))[-1])
assert payload['AllFilesMatch'] and len(payload['PayloadFiles'])==97
ocr=read_json(sorted(OUT.glob('ocr-dotnet-probe-*.json'))[-1])
assert ocr['ErrorType'] is None and ocr['LanguageCount']==0
state=read_json(latest_state/'state.json')
record={
    'RecordedUtc':dt.datetime.now(dt.timezone.utc).isoformat(),'Task':'T-040','Complete':False,
    'SandboxId':'4fc7c24d-f1e5-4b3e-adaa-2abe01f643fe',
    'UserFeedback':{'QuestionCall':'call_2XGzIYJjmMdFv9XqVUGw5ufI','Raw':'启用，0.2.10，用时9分钟，启动/正常退出','InstallationOrderClarificationPending':True},
    'OfficialInstalledPayload':{'Version':'0.2.10','SourceCommit':'227b3d88938543766a27d86a94ade52f9e5b89b7','MatchedFiles':97},
    'ObserverV1':{'Samples':4199,'StoppedNormally':True,'EncodingDefect':'UTF-8 without BOM in PS5.1; startup Chinese regex misread','RawProcessSamplesPreserved':True},
    'ObserverV2':{'SourceSha256':'37bfd2c5fb2858df68513c1151495c849442543094e9af7c2689c5281cfe5b32','FrozenCompleteLinePrefixSamples':len(v2_events)},
    'StartupRecovered':startup,'Idle30To60Seconds':memory30,'LaterSamples':memory_late,'FirstAboveWorkingSetLimit':first_high,
    'MemoryConclusion':'Working Set is above 160 MiB in later observations. Window usage and full-GC state remain unresolved; not an NFR pass.',
    'MsiPolicyWaits':msi_rows,'MsiPolicyWaitTotalSecondsByInstallGroup':groups,
    'MsiInstallOrder':'Two installs separated by uninstall; CAPI2 enabled between them. Do not combine as one standard install.',
    'AppLockerEvents':applocker,
    'InstallDiagnosis':'AppLocker 8045/8037 events correlate with policy completion. DefenderCallAttempted=1, DefenderCalled=0; no evidence of a Defender cloud call. Root cause still unproved.',
    'CapiSnapshotEventCounts':dict(Counter(e['EventId'] for e in capi_summary)),
    'CapiSnapshotTimes':{'MinUtc':min((e['Utc'] for e in capi_summary),default=None),'MaxUtc':max((e['Utc'] for e in capi_summary),default=None)},
    'WindowsOcrCapability':ocr,'Single4KNfrWaived':False,
    'LatestObservedProcesses':state['Processes'],
    'Pending': ['manual clean-tray 120-second restart','full-GC steady memory','full manual feature coverage','single4K screenshot metric','selection window timing','streaming first-character overhead','mouse-hook CPU','installation order reply','MSI policy wait root cause','legacy history warnings reproduction'],
    'Files':files
}
(REPORT/'review.json').write_text(json.dumps(record,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
for row in files:
    copied=REPORT/row['ArchivePath']
    assert copied.stat().st_size==row['Bytes'] and hashlib.sha256(copied.read_bytes()).hexdigest()==row['Sha256']
print(json.dumps({'Report':str(REPORT),'OriginalFiles':len(files),'OriginalBytes':sum(r['Bytes'] for r in files),
                  'StartupCreationToReadyMs':startup['CreationToBootstrapReadyMs'],
                  'LaterWorkingSetMedianMiB':memory_late['WorkingSetMedianMiB'],'MsiPolicyWaitSeconds':groups,
                  'CapiSnapshotTimes':record['CapiSnapshotTimes'],'Complete':False},indent=2))
