"""Freeze the actual production UI upgrade, native worker and single normal restart with retained data."""
from pathlib import Path
import hashlib
import json
import re
import sqlite3

ROOT=Path("F:/Iris")
SESSION=ROOT/"artifacts/t039c/manual-verification-v022-20261105-1/output/session-14d55ff50fd94d3abe8d4474aafd5365"
BASE=ROOT/"tasks/done/T-039C.evidence/manual-old-ready-v029-before-v0210-20261105-1"
REPORT=ROOT/"tasks/done/T-039C.evidence/ui-upgrade-v0210-20261106-1"

def read(path):return json.loads(path.read_text(encoding="utf-8-sig"))
def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()
base=read(BASE/"binding.json")
snapshot=SESSION/"updated-20261105T1555332655996Z.json"
old=read(snapshot)
supplement=sorted(SESSION.glob("updated-supplement-*"))[-1]
verification_path=sorted(SESSION.glob("public-v0210-install-verification-*.json"))[-1]
verification=read(verification_path)
assert old["InstalledSource"]["Version"]==verification["Version"]=="0.2.10"
assert verification["ManifestEntries"]==verification["MatchedEntries"]==97
assert all(row["Matches"] for row in verification["Payload"])
assert len(old["Processes"])==1 and old["Processes"][0]["Pid"]==8980 and old["Processes"][0]["StartedUtc"]=="2026-11-05T15:53:28.2094930Z"
hashes={row["Path"]:row.get("Sha256") for row in old["DataHashes"]}
assert hashes["settings.json"]==base["DataHashes"]["settings.json"] and hashes["secrets.dat"]==base["DataHashes"]["secrets.dat"]
events=[json.loads(line) for line in (supplement/"process-events-v2.jsonl").read_text(encoding="utf-8-sig").splitlines() if line]
assert any(e.get("Pid")==5672 and e["Kind"]=="Exited" and e["ExitCode"]==0 for e in events)
assert any(e.get("Pid")==5516 and e["Kind"]=="Exited" and e["ExitCode"]==0 for e in events)
normal=[e for e in events if e["Kind"]=="Started" and e.get("Identity") and e["Identity"]["Role"]=="ApplicationOrClient" and e["Identity"]["CreatedUtc"]>"2026-11-05T15:53:00"]
assert len(normal)==1 and normal[0]["Pid"]==8980 and normal[0]["Identity"]["ParentPid"]==3968
logdir=max(SESSION.glob("logs-updated-*"),key=lambda p:p.stat().st_mtime)
native=logdir/"Velopack-velopack_Iris.log"
native_text=native.read_text(encoding="utf-8-sig")
worker="\n".join(line for line in native_text.splitlines() if line.startswith("[update:5516]"))
assert "Starting Velopack Updater" in worker and "Applying package 0.2.10 to current: 0.2.9" in worker
assert "Package version 0.2.10 applied successfully." in worker and "Restart: false" in worker
assert "Backing up current dir" in worker and "Replacing current dir" in worker
assert "--veloapp-obsolete" in native_text and "--veloapp-updated" in native_text
assert all(f"[lib-csharp:{pid}]" in native_text for pid in (6616,7456,8980))
app_log=logdir/"logs-iris-20261105.log"
app_text=app_log.read_text(encoding="utf-8-sig")
assert "2026-11-05 23:53:26.958" in app_text and "Iris 已退出，退出码 0" in app_text
assert "2026-11-05 23:53:29.555" in app_text and "Iris 启动序列完成" in app_text
post_restart=app_text.split("2026-11-05 23:53:28.942",1)[1]
assert "历史窗口操作失败" not in post_restart
with sqlite3.connect((supplement/"history.db").as_uri()+"?mode=ro",uri=True) as c:
    c.execute("PRAGMA query_only=ON");assert c.execute("PRAGMA quick_check").fetchall()==[("ok",)]
    rows=c.execute("SELECT h.id,h.source_text,r.provider_id,r.model,r.result_text FROM translation_history h JOIN translation_result r ON r.history_id=h.id ORDER BY h.id").fetchall()
assert [list(row) for row in rows[:2]]==read(BASE/"history-independent.json")["Rows"]
assert len(rows)==3 and rows[2][1:]==("Iris UI upgrade 0210","tp-28c361470883","iris-fixture","Iris 公共升级验证")
requests=[json.loads(line) for line in (supplement/"provider-requests.jsonl").read_text(encoding="utf-8-sig").splitlines() if line]
assert any(row["Utc"]>"2026-11-05T15:53:28" and row["Status"]==200 and row["SyntheticCredentialMatched"] for row in requests)
state=read(supplement/"supplement.json")
assert state["CheckForUpdates"] is False and not any("transaction.json" in row["Path"] for row in state["MaintenanceFiles"])
assert state["Registrations"][0]["DisplayVersion"]=="0.2.10" and "Iris.Maintenance.exe" in state["Registrations"][0]["UninstallString"]
last=read(logdir/"Iris.Maintenance-last-failure.json")
assert last["Pid"]==4936 and last["StartedUtcTicks"]==639294652871200732
REPORT.mkdir(exist_ok=False)
def copy(source,destination):destination.parent.mkdir(parents=True,exist_ok=True);destination.open("xb").write(source.read_bytes())
for p in (snapshot,verification_path,SESSION/"view-updated-v0210-20261105T1554242085979Z.png",SESSION/"view-updated-v0210-20261105T1554242085979Z.png.json",ROOT/"tasks/T-039C.coordination/v0210-user-ui-install-feedback-20261106-1.json"):
    copy(p,REPORT/p.name)
for p in supplement.iterdir():
    if p.is_file():copy(p,REPORT/"supplement"/p.name)
for p in (native,app_log,logdir/"Iris.Maintenance-last-failure.json"):copy(p,REPORT/"native-and-product-logs"/p.name)
(REPORT/"history-independent.json").open("x",encoding="utf-8",newline="\n").write(json.dumps({"Rows":rows,"QuickCheck":"ok"},ensure_ascii=False,indent=2)+"\n")
record={"OldVersion":"0.2.9","NewVersion":"0.2.10","OldAppPid":5672,"NativeWorkerPid":5516,"NewAppPid":8980,
    "NativeWorkerExitCode":0,"ExactlyOneNormalNewInstanceVerified":True,"VelopackHookPids":[6616,7456],"NativeAndProductLogsCorrelated":True,
    "NativeEventDeliveryTimesPreservedWithoutTreatingThemAsExactProcessCreationTimes":True,"OfficialPayloadEntriesMatched":97,
    "SettingsAndSecretsHashesRetained":True,"OldTwoRowsRetainedAndNewTranslationVerified":True,"ActualVersionScreenshotReviewed":True,
    "NoNewHistoryWarningAfterRestartInCapturedLogs":True,"OriginalHistoryWarningsStillRetained":True,"HistoricalLastFailureTimestampUtc":"2026-11-05T08:48:07.1200732Z",
    "PendingTransactionAbsent":True,"OwnedHelperCopiesRetainedForNextMaintenanceCleanup":True,"RealUiUpgradeVerified":True,"PostUpdateUninstallVerified":False}
record["Files"]=[{"Path":p.relative_to(REPORT).as_posix(),"Bytes":p.stat().st_size,"Sha256":sha(p)}for p in sorted(REPORT.rglob("*"))if p.is_file()]
(REPORT/"binding.json").open("x",encoding="utf-8",newline="\n").write(json.dumps(record,indent=2)+"\n")
print(json.dumps({k:record[k] for k in ("RealUiUpgradeVerified","ExactlyOneNormalNewInstanceVerified","OfficialPayloadEntriesMatched","OldTwoRowsRetainedAndNewTranslationVerified","PendingTransactionAbsent","PostUpdateUninstallVerified")},indent=2))
