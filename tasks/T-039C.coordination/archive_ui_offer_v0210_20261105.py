"""Freeze the actual user-controlled 0.2.9 offer before any new package download."""
from pathlib import Path
import hashlib
import json
import sqlite3

ROOT = Path("F:/Iris")
SESSION = ROOT / "artifacts/t039c/manual-verification-v022-20261105-1/output/session-14d55ff50fd94d3abe8d4474aafd5365"
BASE = ROOT / "tasks/done/T-039C.evidence/manual-old-ready-v029-before-v0210-20261105-1"
REPORT = ROOT / "tasks/done/T-039C.evidence/ui-offer-v0210-20261105-1"
snapshot = SESSION / "offer-20261105T1444460625219Z.json"
supplement = SESSION / "offer-supplement-20261105T1444461822350Z"
view = SESSION / "view-offer-v0210-20261105T1444344237852Z.png"
old = json.loads(snapshot.read_text(encoding="utf-8-sig")); base = json.loads((BASE / "binding.json").read_text(encoding="utf-8-sig"))
capture = json.loads((SESSION / "ui-v0210-offer-capture-20261105T1444463285304Z.json").read_text(encoding="utf-8-sig"))
assert capture["Completed"] and capture["ProductOperationPerformed"] is False
assert old["InstalledSource"]["Version"] == "0.2.9" and len(old["Processes"]) == 1 and old["Processes"][0]["Pid"] == base["AppPid"]
assert old["Processes"][0]["StartedUtc"] == base["AppStartedUtc"]
hashes = {row["Path"]:row.get("Sha256") for row in old["DataHashes"]}
assert all(hashes[name] == value for name,value in base["DataHashes"].items())
assert not any("0.2.10" in row["Name"] for row in old["Packages"])
history = json.loads((BASE / "history-independent.json").read_text(encoding="utf-8-sig"))["Rows"]
with sqlite3.connect((supplement / "history.db").as_uri()+"?mode=ro",uri=True) as c:
    c.execute("PRAGMA query_only=ON"); assert c.execute("PRAGMA quick_check").fetchall() == [("ok",)]
    rows = c.execute("SELECT h.id,h.source_text,r.provider_id,r.model,r.result_text FROM translation_history h JOIN translation_result r ON r.history_id=h.id ORDER BY h.id").fetchall()
assert [list(row) for row in rows] == history
REPORT.mkdir(exist_ok=False)
for path in (snapshot, view, view.with_name(view.name+".json"), SESSION / "ui-v0210-offer-capture-20261105T1444463285304Z.json"):
    (REPORT / path.name).open("xb").write(path.read_bytes())
for path in supplement.iterdir():
    if path.is_file():
        destination = REPORT / "supplement" / path.name; destination.parent.mkdir(exist_ok=True)
        destination.open("xb").write(path.read_bytes())
(REPORT / "visual-review.json").open("x",encoding="utf-8",newline="\n").write(json.dumps({"ActuallyViewed":True,"CurrentVersion":"0.2.9","OfferedVersion":"0.2.10",
    "DisplayedState":"有可用更新","DownloadEnabled":True,"InstallDisabled":True,"ImageSha256":hashlib.sha256(view.read_bytes()).hexdigest()},ensure_ascii=False,indent=2)+"\n")
files = [{"Path":p.relative_to(REPORT).as_posix(),"Bytes":p.stat().st_size,"Sha256":hashlib.sha256(p.read_bytes()).hexdigest()} for p in sorted(REPORT.rglob("*")) if p.is_file()]
(REPORT / "binding.json").open("x",encoding="utf-8",newline="\n").write(json.dumps({"CurrentVersion":"0.2.9","OfferedVersion":"0.2.10","AppPid":base["AppPid"],
    "OriginalDataHashesMatched":True,"TwoHistoryRowsRetained":True,"NewPackageAbsent":True,"ActualUserScreenshotReviewed":True,
    "UiOfferVerified":True,"DownloadReadyVerified":False,"RealUiUpgradeVerified":False,"HistoryOperationWarningsRetained":True,"Files":files},indent=2)+"\n")
print("Verified actual offer 0.2.9 -> 0.2.10; no new package present, original data and both rows retained.")
