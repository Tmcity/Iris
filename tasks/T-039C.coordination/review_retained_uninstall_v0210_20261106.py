"""Freeze actual default-retention uninstall and authoritative native/process/data/registration results."""
from pathlib import Path
import hashlib
import json
import sqlite3

ROOT=Path("F:/Iris")
SESSION=ROOT/"artifacts/t039c/manual-verification-v022-20261105-1/output/session-14d55ff50fd94d3abe8d4474aafd5365"
REPORT=ROOT/"tasks/done/T-039C.evidence/ui-retained-uninstall-v0210-20261106-1"
UPGRADE=ROOT/"tasks/done/T-039C.evidence/ui-upgrade-v0210-20261106-1"
def read(p):return json.loads(p.read_text(encoding="utf-8-sig"))
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
snapshot=SESSION/"uninstalled-20261105T1853068852976Z.json"
supplement=SESSION/"uninstalled-supplement-20261105T1853070154710Z"
state=read(snapshot);extra=read(supplement/"supplement.json")
feedback=ROOT/"tasks/T-039C.coordination/v0210-user-uninstall-feedback-20261106-1.json"
assert read(feedback)["DefaultRetainDataSelected"] and read(feedback)["UninstallSucceeded"]
assert state["InstalledSource"] is None and not state["Processes"] and not state["Packages"]
assert not extra["ProductRootExists"] and not extra["ProductCurrentExists"] and extra["UserDataExists"]
assert not extra["Registrations"] and not extra["Shortcuts"] and extra["CheckForUpdates"] is False
hashes={r["Path"]:r.get("Sha256") for r in state["DataHashes"]}
before=read(UPGRADE/"updated-20261105T1555332655996Z.json")
prior={r["Path"]:r.get("Sha256") for r in before["DataHashes"]}
assert all(hashes[n]==prior[n] for n in ("history.db","secrets.dat","settings.json"))
assert all(any(r["Family"]==family and r["Version"]=="10.0.14" for r in state["Runtimes"])for family in("Microsoft.NETCore.App","Microsoft.WindowsDesktop.App"))
with sqlite3.connect((supplement/"history.db").as_uri()+"?mode=ro",uri=True) as c:
    c.execute("PRAGMA query_only=ON");assert c.execute("PRAGMA quick_check").fetchall()==[("ok",)]
    rows=c.execute("SELECT h.id,h.source_text,r.provider_id,r.model,r.result_text FROM translation_history h JOIN translation_result r ON r.history_id=h.id ORDER BY h.id").fetchall()
assert rows==[tuple(r)for r in read(UPGRADE/"history-independent.json")["Rows"]]
events=[json.loads(line)for line in(supplement/"process-events-v2.jsonl").read_text(encoding="utf-8-sig").splitlines()if line]
assert any(e.get("Pid")==8980 and e["Kind"]=="Exited" and e["ExitCode"]==0 for e in events)
assert any(e.get("Pid")==8280 and e["Kind"]=="Exited" and e["ExitCode"]==0 for e in events)
final_path=sorted(SESSION.glob("uninstall-v0210-final-inspection-*.json"))[-1]
final=read(final_path);assert not final["ProductRootExists"] and final["UserDataExists"] and not final["PendingTransactionExists"] and not final["Processes"]
run_path=sorted(SESSION.glob("run-v0210-final-inspection-*.json"))[-1]
assert read(run_path)["IrisAutoStartValuePresent"] is False
logdir=max(SESSION.glob("logs-uninstalled-*"),key=lambda p:p.stat().st_mtime)
native=logdir/"Velopack-velopack_Iris.log"
text=native.read_text(encoding="utf-8-sig")
worker="\n".join(line for line in text.splitlines()if line.startswith("[update:8280]"))
assert "Command: Uninstall" in worker and "Finished successfully." in worker and "Removing uninstall registry keys" in worker
assert "--veloapp-uninstall" in text and "[lib-csharp:8384]" in text
assert not any("transaction.json" in r["Path"] for r in extra["MaintenanceFiles"])
view=SESSION/"view-uninstalled-v0210-20261105T1852533805867Z.png"
REPORT.mkdir(exist_ok=False)
def copy(src,dst):dst.parent.mkdir(parents=True,exist_ok=True);dst.open("xb").write(src.read_bytes())
for p in(snapshot,final_path,run_path,feedback,view,view.with_name(view.name+".json"),SESSION/"ui-v0210-uninstalled-capture-20261105T1853071498772Z.json"):
    copy(p,REPORT/p.name)
for p in supplement.iterdir():
    if p.is_file():copy(p,REPORT/"supplement"/p.name)
for p in(native,logdir/"logs-iris-20261105.log",logdir/"logs-iris-20261106.log"):
    copy(p,REPORT/"native-and-product-logs"/p.name)
(REPORT/"visual-review.json").open("x",encoding="utf-8",newline="\n").write(json.dumps({"ActuallyViewed":True,"SystemAppSearch":"Iris","IrisSearchResultsEmpty":True,"ImageSha256":sha(view)},indent=2)+"\n")
(REPORT/"history-independent.json").open("x",encoding="utf-8",newline="\n").write(json.dumps({"Rows":rows,"QuickCheck":"ok"},ensure_ascii=False,indent=2)+"\n")
record={"DefaultRetainDataUninstallVerified":True,"UserConfirmedRetainData":True,"OldApplicationExitCode":0,"NativeUninstallerPid":8280,"NativeUninstallerExitCode":0,
    "InstallRootAbsent":True,"ProductProcessesAbsent":True,"UninstallRegistrationAndShortcutsAbsent":True,"IrisAutoStartValueAbsent":True,
    "ThreeHistoryRowsRetained":True,"SettingsSecretsHistoryHashesRetained":True,"LogDataRetained":True,"SharedNet10RuntimeStillInstalled":True,
    "PendingTransactionAbsent":True,"UserSystemAppListScreenshotReviewed":True,"RetainedMaintenanceFiles":extra["MaintenanceFiles"],
    "RetainedMaintenanceSemantics":"Bounded outside recovery and last owned executable copy, not installed app or pending native transaction; audit against MaintenanceWorkDirectory.Clean contract."}
record["Files"]=[{"Path":p.relative_to(REPORT).as_posix(),"Bytes":p.stat().st_size,"Sha256":sha(p)}for p in sorted(REPORT.rglob("*"))if p.is_file()]
(REPORT/"binding.json").open("x",encoding="utf-8",newline="\n").write(json.dumps(record,indent=2)+"\n")
print(json.dumps({k:record[k]for k in("DefaultRetainDataUninstallVerified","InstallRootAbsent","ThreeHistoryRowsRetained","SharedNet10RuntimeStillInstalled","PendingTransactionAbsent")},indent=2))
