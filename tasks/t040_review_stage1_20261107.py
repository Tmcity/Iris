"""Review real UI request/storage/display evidence; keep clipboard proof explicitly pending."""
from pathlib import Path
import datetime
import hashlib
import json

ROOT=Path('F:/Iris')
SESSION=ROOT/'artifacts/t040/manual-verification-20261106-2'
OUT=SESSION/'output'
REPORT=ROOT/'tasks/T-040.evidence/stage1-review-20261107-1'
REPORT.mkdir(parents=True,exist_ok=False)
files=[]
def read(path):return json.loads(path.read_text(encoding='utf-8-sig'))
def copy(path,relative,prefix=False):
    destination=REPORT/relative;destination.parent.mkdir(parents=True,exist_ok=True)
    raw=path.read_bytes()
    if prefix:raw=raw[:raw.rfind(b'\n')+1]
    destination.open('xb').write(raw)
    files.append({'OriginalPath':str(path),'Path':destination.relative_to(REPORT).as_posix(),'Bytes':len(raw),'Sha256':hashlib.sha256(raw).hexdigest(),'CompleteLinePrefix':prefix})
snapshot=OUT/'feature-data-20261107T0545516808618Z'
state=read(snapshot/'state.json');settings=read(snapshot/'settings.json')
profiles=settings['translation']['providers']
assert len(profiles)==1
profile=profiles[0]
assert profile['displayName']=='T040 OpenAI fixture' and profile['enabled']
assert profile['baseUrl']=='http://127.0.0.1:18040/v1' and profile['model']=='iris-fixture'
assert profile['credentialSetId'].startswith('cs-')
assert state['HistoryTotal']==1 and len(state['History'])==1 and state['SqliteFailureCode'] is None
entry=state['History'][0]
assert entry['source_text']=='Iris T040 input 01' and entry['result_text']=='Iris T040 synthetic translation'
assert entry['provider_id']==profile['id'] and entry['provider_name']==profile['displayName'] and entry['origin']=='Input'
assert entry['is_truncated']==0 and entry['is_favorite']==0
for path in snapshot.iterdir():
    if path.is_file():copy(path,Path('snapshot')/path.name)
fixture=OUT/'synthetic-provider-20261107T0535457545842Z'
raw=(fixture/'requests.jsonl').read_bytes();raw=raw[:raw.rfind(b'\n')+1]
requests=[json.loads(line) for line in raw.decode('utf-8').splitlines()]
user_request=next(r for r in requests if r['Kind']=='Request' and r.get('SyntheticText')=='Iris T040 input 01')
assert user_request['FakeKeyAccepted'] and user_request['Path']=='/v1/chat/completions'
assert any(r['Kind']=='Completed' and r['Sequence']==user_request['Sequence'] and not r['Truncated'] for r in requests)
assert len([r for r in requests if r['Kind']=='Models'])>=2
test_hash=hashlib.sha256(b'Hello, world.').hexdigest().upper()
test=next(r for r in requests if r['Kind']=='Request' and r['SourceSha256']==test_hash)
assert test['FakeKeyAccepted']
copy(fixture/'requests.jsonl',Path('fixture')/'requests-prefix.jsonl',prefix=True)
copy(fixture/'state.json',Path('fixture')/'state.json')
log=(snapshot/'iris-20261107.log').read_text(encoding='utf-8-sig')
new_log=log[log.index('2026-11-07 13:44:'):] if '2026-11-07 13:44:' in log else ''
assert new_log and '历史窗口操作失败' not in new_log and '[ERR]' not in new_log
image=OUT/'manual-feature-view-20261107T0545439531175Z.png'
copy(image,Path('screenshots')/image.name)
views=sorted(OUT.glob('stage1-view-metadata-*.json'))
view=read(views[-1]);copy(views[-1],Path('checks')/views[-1].name)
assert view['AppPid']==3032 and view['Clipboard']['Modified'] is False
trace=OUT/'runtime-counters-20261107T0537257455600Z'
final=read(trace/'state.json')
assert final['Completed'] and final['Failure'] is None and final['Pid']==3032 and final['NaturalFullGcStarts']==1
events=[json.loads(x) for x in (trace/'events.jsonl').read_text(encoding='utf-8').splitlines()]
assert len(events)==final['Events']==16218
starts=[e for e in events if e['Kind']=='GCStart' and e['Depth']==2]
stops=[e for e in events if e['Kind']=='GCStop' and e['Depth']==2]
assert len(starts)==len(stops)==1 and starts[0]['Count']==stops[0]['Count']==4
copy(trace/'state.json',Path('runtime')/'state.json')
copy(trace/'events.jsonl',Path('runtime')/'events.jsonl')
for path in OUT.glob('post-gc-input-ready-*.json'):copy(path,Path('checks')/path.name)
binding={'Utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'Task':'T-040','Complete':False,
         'UserFeedback':{'QuestionCall':'call_3ci58mXgottsPMIIh4RlL7iA','Raw':'已执行'},
         'ServiceSavedAndUsed':True,'ProviderId':profile['id'],'RealUiModelAndConnectionRequestsMatched':True,
         'RealUiInputTranslationAndHistoryMatched':True,'UserHistoryScreenshotVisuallyReviewed':True,
         'ScreenshotObserved':'Correct source/result/provider/model and copied-full-text status; result-copy control below initial viewport.',
         'NewHistoryOperationWarningsObserved':False,'ClipboardActionStatusObserved':True,
         'TwoPasteContentsIndependentlyProved':False,'LateClipboardMatchesSource':view['Clipboard']['MatchesExpectedSource'],
         'LateClipboardMatchesResult':view['Clipboard']['MatchesExpectedResult'],
         'LateClipboardChangeDoesNotProvePastCopyFailure':True,
         'NaturalFullGcStart':starts[0],'NaturalFullGcStop':stops[0],'RuntimeObserverCleanlyStopped':True,
         'PostTraceClosedWindowSteadyMemoryProved':False,
         'Pending':'Two clipboard pastes screenshot; same-instance functional windows closed, 45-second idle checkpoint after natural full GC.',
         'Files':files}
(REPORT/'binding.json').write_text(json.dumps(binding,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
for item in files:
    raw=(REPORT/item['Path']).read_bytes();assert len(raw)==item['Bytes'] and hashlib.sha256(raw).hexdigest()==item['Sha256']
print(json.dumps({'Report':str(REPORT),'Files':len(files),'ServiceRequestAndHistoryMatched':True,'NaturalFullGcObserved':True,
                  'ClipboardPastesProofPending':True,'PostGcSteadyPending':True,'Complete':False},indent=2))
