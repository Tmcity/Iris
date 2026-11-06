"""Freeze real Provider interface probes and non-mutating working-set metadata."""
from pathlib import Path
import datetime
import hashlib
import json
import subprocess

ROOT=Path('F:/Iris')
SESSION=ROOT/'artifacts/t040/manual-verification-20261106-2'
OUTPUT=SESSION/'output'
REPORT=ROOT/'tasks/T-040.evidence/adapter-memory-review-20261107-1'
REPORT.mkdir(parents=True,exist_ok=False)
files=[]
def copy(path,relative):
    destination=REPORT/relative
    destination.parent.mkdir(parents=True,exist_ok=True)
    raw=path.read_bytes();destination.open('xb').write(raw)
    files.append({'OriginalPath':str(path),'Path':destination.relative_to(REPORT).as_posix(),'Bytes':len(raw),'Sha256':hashlib.sha256(raw).hexdigest()})
adapter=sorted(OUTPUT.glob('provider-adapter-probe-*/state.json'))[-1]
record=json.loads(adapter.read_text(encoding='utf-8-sig'))
assert record['Passed'] is True and len(record['Rows'])==15 and all(r['Passed'] for r in record['Rows'])
copy(adapter,Path('checks')/adapter.parent.name/adapter.name)
maps=sorted(OUTPUT.glob('memory-map-*.json'))
assert len(maps)==2
for path in maps: copy(path,Path('checks')/path.name)
latest=json.loads(maps[-1].read_text(encoding='utf-8-sig'))
assert latest['Pid']==1804 and latest['WorkingSetTrimmed'] is False and latest['GcRequested'] is False
coverage=latest['ObservedResidentBytes']/latest['WorkingSetBefore']
assert coverage>0.999 and latest['WorkingSetBefore']>160*1048576
actual=sorted(OUTPUT.glob('actual-helper-processes-*.json'))[-1]
copy(actual,Path('checks')/actual.name)
for folder in ('provider-adapter-probe-20261107-1','memory-map-20261107-1'):
    for path in sorted((ROOT/'artifacts/t040/toolsource'/folder).iterdir()):
        if path.is_file() and path.suffix in ('.cs','.csproj'):copy(path,Path('source')/folder/path.name)
for pattern in ('provider-adapter-publish-20261107-*.log','memory-map-publish-20261107-*.log'):
    for path in sorted((ROOT/'artifacts/t040').glob(pattern)):copy(path,Path('build')/path.name)
diff=subprocess.run(['git','diff','--name-only','227b3d88938543766a27d86a94ade52f9e5b89b7','HEAD','--','src','Directory.Build.props','Directory.Packages.props'],cwd=ROOT,capture_output=True,check=True)
assert diff.stdout.strip()==b''
sdk=[g for g in latest['RegionsByAllocation'] if g['Path'].endswith('Microsoft.Windows.SDK.NET.dll')]
report={'Utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'Task':'T-040','Complete':False,
        'ProviderInterfaceChecks':15,'AllProviderChecksPassed':True,'SourceMatchesFormal0210Code':True,
        'ProviderChecksScope':'Actual factories and Provider interfaces, default profile options, synthetic loopback transport; installed Iris UI, production proxy pipeline and real accounts excluded.',
        'WorkingSetMiB':latest['WorkingSetBefore']/1048576,'ResidentCoverage':coverage,'MemoryTypes':latest['Totals'],
        'WindowsSdkResidentBytes':sum(g['ResidentBytes'] for g in sdk),'GcRequested':False,'WorkingSetTrimmed':False,
        'Conclusion':'Late working set remains above the target. Mapped size is not resident size. Shared mapped pages remain in the raw working-set measure; pure-tray restart and full-GC condition remain unresolved.',
        'PendingUserCleanIdleFeedback':True,'Files':files}
(REPORT/'binding.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'Report':str(REPORT),'ProviderChecksPassed':15,'WorkingSetMiB':report['WorkingSetMiB'],'ResidentCoverage':coverage,'Files':len(files),'Complete':False},indent=2))
