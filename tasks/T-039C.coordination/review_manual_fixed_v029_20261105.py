"""Accept the fixed manual baseline only with real user feedback and fresh installed/data evidence."""
from pathlib import Path
import argparse
import datetime
import hashlib
import json
import sqlite3

ROOT = Path("F:/Iris")
SESSION = ROOT / "artifacts/t039c/manual-verification-v022-20261105-1/output/session-14d55ff50fd94d3abe8d4474aafd5365"
BASE = ROOT / "tasks/done/T-039C.evidence/manual-old-ready-v027-before-v029-20261105-1"
parser = argparse.ArgumentParser()
parser.add_argument("--feedback-file", type=Path, required=True)
parser.add_argument("--new-source-text", default="Iris acceptance check after fixed install")
parser.add_argument("--attempt", type=int, default=1)
args = parser.parse_args()
assert args.feedback_file.resolve().is_relative_to(ROOT / "tasks/T-039C.coordination")
assert 1 <= args.attempt <= 10


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


feedback = read(args.feedback_file)
assert feedback["ActualUserFeedback"] is True and feedback["Version"] == "0.2.9"
assert feedback["InstallationSucceeded"] and feedback["OldHistoryUsable"] and feedback["NewSyntheticTranslationSucceeded"]
assert isinstance(feedback["QuestionItemId"], str) and feedback["QuestionItemId"] and isinstance(feedback["Answer"], str) and feedback["Answer"]
baseline = read(BASE / "binding.json")
updated_path = sorted(SESSION.glob("updated-2026*.json"))[-1]
supplement_path = sorted(SESSION.glob("updated-supplement-*"))[-1]
verification_path = sorted(SESSION.glob("public-v029-install-verification-*.json"))[-1]
inspection_path = sorted(SESSION.glob("old-ready-readonly-*"))[-1]
updated, supplement = read(updated_path), read(supplement_path / "supplement.json")
verification, inspection = read(verification_path), read(inspection_path / "inspection.json")
assert updated["InstalledSource"]["Version"] == inspection["ProductSource"]["Version"] == verification["Version"] == "0.2.9"
assert verification["ManifestEntries"] == verification["MatchedEntries"] == len(verification["Payload"]) == 97
assert verification["ReadOnlyInspection"] is True and all(row["Matches"] for row in verification["Payload"])
assert any(row["Sha256"] == "69ab96bb1f700b21fa430d69eab85d6d4a5c606c377424222f0f97af0e7f2891" and row["Bytes"] == 116768884
           for row in verification["InstallerDownloads"])
assert len(updated["Processes"]) == 1 and updated["Processes"][0]["Pid"] != baseline["AppPid"]
app = updated["Processes"][0]
assert datetime.datetime.fromisoformat(app["StartedUtc"].replace("Z", "+00:00")) > datetime.datetime.fromisoformat("2026-11-05T12:46:36+00:00")
assert app["Pid"] in {row["Pid"] for row in inspection["Processes"]}
assert baseline["AppPid"] not in {row["Pid"] for row in inspection["Processes"]}
assert inspection["SyntheticProfiles"] == [baseline["SyntheticProfile"]]
assert inspection["CheckForUpdates"] is supplement["CheckForUpdates"] is False
hashes = {row["Path"]: row.get("Sha256") for row in updated["DataHashes"]}
assert hashes["settings.json"] == inspection["SettingsSha256"] == baseline["DataHashes"]["settings.json"]
assert hashes["secrets.dat"] == inspection["SecretsSha256"] == baseline["DataHashes"]["secrets.dat"]
assert not any("transaction.json" in row["Path"].lower() for row in supplement["MaintenanceFiles"])
assert len(supplement["Registrations"]) == 1 and "Iris.Maintenance" in supplement["Registrations"][0]["UninstallString"]
db = supplement_path / "history.db"
with sqlite3.connect(db.as_uri() + "?mode=ro", uri=True) as connection:
    connection.execute("PRAGMA query_only=ON")
    assert connection.execute("PRAGMA quick_check").fetchall() == [("ok",)]
    columns = "h.id, h.source_text, r.provider_id, r.model, r.result_text"
    join = " FROM translation_history h JOIN translation_result r ON r.history_id = h.id WHERE h.source_text = ?"
    old_rows = connection.execute("SELECT " + columns + join, ("Iris acceptance check",)).fetchall()
    new_rows = connection.execute("SELECT " + columns + join, (args.new_source_text,)).fetchall()
expected_old = read(BASE / "history-independent.json")["Rows"]
assert [list(row) for row in old_rows] == expected_old
assert new_rows and all(row[2] == baseline["SyntheticProfile"]["Id"] and row[3] == "iris-fixture" and row[4] == "Iris 公共升级验证" for row in new_rows)
requests = [json.loads(line) for line in (supplement_path / "provider-requests.jsonl").read_text(encoding="utf-8-sig").splitlines() if line]
assert any(row["Method"] == "POST" and row["Status"] == 200 and row["SyntheticCredentialMatched"] is True
           and datetime.datetime.fromisoformat(row["Utc"].replace("Z", "+00:00")) > datetime.datetime.fromisoformat(app["StartedUtc"].replace("Z", "+00:00"))
           for row in requests)
pointer, recorder = read(supplement_path / "active-recorder-v2.json"), read(supplement_path / "watcher-v2-status.json")
assert pointer["RecorderVersion"] == recorder["RecorderVersion"] == 3 and pointer["Ready"] and recorder["Ready"]
assert recorder["Failure"] is None and pointer["RecorderPid"] == recorder["RecorderPid"] == baseline["RecorderPid"]
assert {baseline["RecorderPid"], baseline["ProviderPid"]}.issubset({row["Pid"] for row in inspection["Processes"]})
logs = []
for old_log in sorted((BASE / "inspection").glob("product-*.log")):
    new_log = inspection_path / old_log.name
    assert new_log.is_file() and new_log.read_bytes().startswith(old_log.read_bytes())
    logs.append({"Name": new_log.name, "Bytes": new_log.stat().st_size, "Sha256": sha(new_log), "OldLogPrefixRetained": True})
assert logs
views = sorted(SESSION.glob("view-fixed-v029-*.png"))
assert views
view = views[-1]
view_record = read(view.with_name(view.name + ".json"))
assert view_record["UserRequestedPassiveDesktopCapture"] is True and view_record["ProductOperationPerformed"] is False
assert view_record["Stage"] == "fixed-v029" and view_record["Sha256"] == sha(view)
REPORT = ROOT / ("tasks/done/T-039C.evidence/manual-fixed-v029-20261105-" + str(args.attempt))
REPORT.mkdir(exist_ok=False)


def copy(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    target.open("xb").write(source.read_bytes())


for source, name in ((updated_path, updated_path.name), (verification_path, verification_path.name),
                     (args.feedback_file, "user-feedback.json"), (view, view.name), (view.with_name(view.name + ".json"), view.name + ".json")):
    copy(source, REPORT / name)
for folder, name in ((supplement_path, "supplement"), (inspection_path, "inspection")):
    for path in sorted(folder.iterdir()):
        if path.is_file():
            copy(path, REPORT / name / path.name)
(REPORT / "history-independent.json").open("x", encoding="utf-8", newline="\n").write(json.dumps(
    {"QuickCheck": "ok", "OldRows": old_rows, "NewSourceText": args.new_source_text, "NewRows": new_rows, "SourceDbSha256": sha(db)}, ensure_ascii=False, indent=2) + "\n")
files = [{"Path": path.relative_to(REPORT).as_posix(), "Bytes": path.stat().st_size, "Sha256": sha(path)}
         for path in sorted(REPORT.rglob("*")) if path.is_file()]
binding = {"Version": "0.2.9", "SourceCommit": "0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2",
           "AppPid": app["Pid"], "AppStartedUtc": app["StartedUtc"], "SettingsSha256": hashes["settings.json"],
           "SecretsSha256": hashes["secrets.dat"], "UserFeedbackVerified": True, "PublicInstalledPayloadEntriesMatched": 97,
           "OldHistoryAndNewSyntheticTranslationVerified": True, "ProductLogs": logs, "RecorderReady": True,
           "PendingTransactionAbsent": True, "UserScreenshotRetained": True, "UserScreenshotVisuallyReviewed": False,
           "RealUiUpgradeVerified": False, "RestartCountVerified": False, "PostUpdateUninstallVerified": False, "Files": files}
(REPORT / "binding.json").open("x", encoding="utf-8", newline="\n").write(json.dumps(binding, indent=2) + "\n")
print(json.dumps({key: binding[key] for key in ("Version", "AppPid", "UserFeedbackVerified", "PublicInstalledPayloadEntriesMatched",
    "OldHistoryAndNewSyntheticTranslationVerified", "UserScreenshotRetained", "UserScreenshotVisuallyReviewed", "RealUiUpgradeVerified")}, indent=2))
