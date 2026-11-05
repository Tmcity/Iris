"""Freeze the real Ready state, official cached full bytes and unchanged old data before Install."""
from pathlib import Path
import hashlib
import json
import sqlite3

ROOT=Path("F:/Iris")
SESSION=ROOT/"artifacts/t039c/manual-verification-v022-20261105-1/output/session-14d55ff50fd94d3abe8d4474aafd5365"
BASE=ROOT/"tasks/done/T-039C.evidence/manual-old-ready-v029-before-v0210-20261105-1"
REPORT=ROOT/"tasks/done/T-039C.evidence/ui-ready-v0210-20261105-1"
snapshot=SESSION/"ready-20261105T1500358784172Z.json"
supplement=SESSION/"ready-supplement-20261105T1500360044811Z"
verification=SESSION/"ready-v0210-verification-20261105T1500378838664Z.json"
old=json.loads(snapshot.read_text(encoding="utf-8-sig"));base=json.loads((BASE/"binding.json").read_text(encoding="utf-8-sig"))
verified=json.loads(verification.read_text(encoding="utf-8-sig"))
assert verified["OfficialFullMatches"] and verified["Bytes"]==64628628 and verified["Sha256"]=="dca307a0afc1827e27bc7ec2614e5ead3a30978d4c5d318e4d0d842ebc73f136"
assert old["InstalledSource"]["Version"]=="0.2.9" and len(old["Processes"])==1 and old["Processes"][0]["Pid"]==base["AppPid"]==5672
assert old["Processes"][0]["StartedUtc"]==base["AppStartedUtc"]
hashes={row["Path"]:row.get("Sha256") for row in old["DataHashes"]}
assert all(hashes[name]==value for name,value in base["DataHashes"].items())
status=json.loads((supplement/"watcher-v2-status.json").read_text(encoding="utf-8-sig"))
assert status["Ready"] and status["Failure"] is None and status["RecorderVersion"]==3 and status["RecorderPid"]==8152
with sqlite3.connect((supplement/"history.db").as_uri()+"?mode=ro",uri=True) as c:
    c.execute("PRAGMA query_only=ON");assert c.execute("PRAGMA quick_check").fetchall()==[("ok",)]
    rows=c.execute("SELECT h.id,h.source_text,r.provider_id,r.model,r.result_text FROM translation_history h JOIN translation_result r ON r.history_id=h.id ORDER BY h.id").fetchall()
assert [list(row) for row in rows]==json.loads((BASE/"history-independent.json").read_text(encoding="utf-8-sig"))["Rows"]
REPORT.mkdir(exist_ok=False)
for path in (snapshot,verification,SESSION/"ui-v0210-ready-capture-20261105T1500361176980Z.json"):
    (REPORT/path.name).open("xb").write(path.read_bytes())
for path in SESSION.glob("view-ready-v0210-*.png*"):
    (REPORT/path.name).open("xb").write(path.read_bytes())
for path in supplement.iterdir():
    if path.is_file():
        dst=REPORT/"supplement"/path.name;dst.parent.mkdir(exist_ok=True);dst.open("xb").write(path.read_bytes())
(REPORT/"visual-review.json").open("x",encoding="utf-8",newline="\n").write(json.dumps({"ActuallyViewed":True,"CurrentVersion":"0.2.9","TargetVersion":"0.2.10", "DisplayedState":"更新已验证，可以安装","InstallAndRestartEnabled":True,"Image":"view-ready-v0210-20261105T1459443977126Z.png"},ensure_ascii=False,indent=2)+"\n")
files=[{"Path":p.relative_to(REPORT).as_posix(),"Bytes":p.stat().st_size,"Sha256":hashlib.sha256(p.read_bytes()).hexdigest()} for p in sorted(REPORT.rglob("*")) if p.is_file()]
(REPORT/"binding.json").open("x",encoding="utf-8",newline="\n").write(json.dumps({"UiReadyVerified":True,"OldVersion":"0.2.9","TargetVersion":"0.2.10","OldAppPid":5672,"OfficialFullBytes":64628628,"OfficialFullSha256":verified["Sha256"],"OriginalDataHashesMatched":True,"OriginalTwoHistoryRowsRetained":True,"RecorderReady":True,"UiInstallPerformed":False,"Files":files},indent=2)+"\n")
print("Real Ready verified: cached official full matched, original 0.2.9/5672 and data retained, install not performed.")
