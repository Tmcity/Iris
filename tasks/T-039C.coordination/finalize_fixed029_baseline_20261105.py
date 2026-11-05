"""Record actual visual review, supplemental user feedback and the fresh fixed-client baseline."""
from pathlib import Path
import datetime
import hashlib
import json
import sqlite3

ROOT = Path("F:/Iris")
SESSION = ROOT / "artifacts/t039c/manual-verification-v022-20261105-1/output/session-14d55ff50fd94d3abe8d4474aafd5365"
ACCEPTED = ROOT / "tasks/done/T-039C.evidence/manual-fixed-v029-20261105-1"
REPORT = ROOT / "tasks/done/T-039C.evidence/manual-old-ready-v029-before-v0210-20261105-1"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def copy(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.open("xb").write(source.read_bytes())


accepted = read(ACCEPTED / "binding.json")
assert accepted["UserFeedbackVerified"] and accepted["OldHistoryAndNewSyntheticTranslationVerified"]
assert accepted["PublicInstalledPayloadEntriesMatched"] == 97
view = sorted(ACCEPTED.glob("view-fixed-v029-*.png"))[-1]
copy(ACCEPTED / "binding.json", ACCEPTED / "binding-before-visual-review.json")
copy(ROOT / "tasks/T-039C.coordination/v029-manual-translation-feedback-20261105-1.json", ACCEPTED / "supplemental-user-feedback.json")
(ACCEPTED / "visual-review.json").open("x", encoding="utf-8", newline="\n").write(json.dumps({
    "ActuallyViewed": True, "ImageSha256": sha(view), "DisplayedVersion": "0.2.9", "DisplayedLanguage": "Simplified Chinese",
    "StartupCheckBoxUnchecked": True, "VersionAndPreferenceReadable": True,
    "Scope": "Actual user-requested screenshot inspected through view_image; no GUI input performed."}, indent=2) + "\n")
accepted["UserScreenshotVisuallyReviewed"] = True
accepted["Files"] = [{"Path": path.relative_to(ACCEPTED).as_posix(), "Bytes": path.stat().st_size, "Sha256": sha(path)}
                     for path in sorted(ACCEPTED.rglob("*")) if path.is_file() and path.name != "binding.json"]
(ACCEPTED / "binding.json").write_text(json.dumps(accepted, indent=2) + "\n", encoding="utf-8", newline="\n")
old_path = sorted(SESSION.glob("old-ready-2026*.json"))[-1]
supplement_path = sorted(SESSION.glob("old-ready-supplement-*"))[-1]
inspection_path = sorted(SESSION.glob("old-ready-readonly-*"))[-1]
old, supplement = read(old_path), read(supplement_path / "supplement.json")
inspection = read(inspection_path / "inspection.json")
status, pointer = read(supplement_path / "watcher-v2-status.json"), read(supplement_path / "active-recorder-v2.json")
assert old["InstalledSource"]["Version"] == inspection["ProductSource"]["Version"] == "0.2.9"
assert len(old["Processes"]) == 1 and old["Processes"][0]["Pid"] == accepted["AppPid"] == 5672
assert old["Processes"][0]["StartedUtc"] == accepted["AppStartedUtc"]
assert inspection["CheckForUpdates"] is supplement["CheckForUpdates"] is False
assert {row["Pid"] for row in inspection["Processes"]} == {5672, 7328, 8152}
assert status["RecorderVersion"] == pointer["RecorderVersion"] == 3
assert status["RecorderPid"] == pointer["RecorderPid"] == 8152 and status["Ready"] and pointer["Ready"] and status["Failure"] is None
hashes = {row["Path"]: row.get("Sha256") for row in old["DataHashes"]}
assert hashes["settings.json"] == accepted["SettingsSha256"] and hashes["secrets.dat"] == accepted["SecretsSha256"]
history = read(ACCEPTED / "history-independent.json")
with sqlite3.connect((supplement_path / "history.db").as_uri() + "?mode=ro", uri=True) as connection:
    connection.execute("PRAGMA query_only=ON")
    assert connection.execute("PRAGMA quick_check").fetchall() == [("ok",)]
    rows = connection.execute("SELECT h.id,h.source_text,r.provider_id,r.model,r.result_text FROM translation_history h JOIN translation_result r ON r.history_id=h.id ORDER BY h.id").fetchall()
assert [list(row) for row in rows] == history["OldRows"] + history["NewRows"]
assert not any("transaction.json" in row["Path"].lower() for row in supplement["MaintenanceFiles"])
REPORT.mkdir(exist_ok=False)
copy(old_path, REPORT / old_path.name)
copy(ACCEPTED / "binding.json", REPORT / "accepted-manual-fixed029-binding.json")
for folder, name in ((supplement_path, "supplement"), (inspection_path, "inspection")):
    for path in sorted(folder.iterdir()):
        if path.is_file():
            copy(path, REPORT / name / path.name)
(REPORT / "history-independent.json").open("x", encoding="utf-8", newline="\n").write(json.dumps(
    {"QuickCheck": "ok", "Rows": rows, "SourceDbSha256": sha(supplement_path / "history.db")}, ensure_ascii=False, indent=2) + "\n")
binding = {"ReviewedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "SandboxId": "2fc7c168-2f34-437a-8319-8974e6f592b2",
    "Version": "0.2.9", "SourceCommit": "0b3267f1f6cdc1a9fb8cfe1c93dde22da273ecd2", "AppPid": 5672,
    "AppStartedUtc": accepted["AppStartedUtc"], "ProviderPid": 7328, "RecorderPid": 8152, "CheckForUpdates": False,
    "DataHashes": {name: hashes[name] for name in ("settings.json", "secrets.dat", "history.db")},
    "SyntheticProfile": inspection["SyntheticProfiles"][0], "OldReadyVerified": True, "PendingTransactionAbsent": True,
    "OldAndNewSyntheticHistoryVerified": True, "UserScreenshotVisuallyReviewed": True, "UserConfirmedManualInstallation": True,
    "UserReportedInstallationDuration": "不到1秒", "RealUiUpgradeVerified": False,
    "HistoryHashSemantics": "Record DB/WAL bytes; post-shutdown checkpoint changes require row-level retention verification, not a fabricated hash equality."}
binding["Files"] = [{"Path": path.relative_to(REPORT).as_posix(), "Bytes": path.stat().st_size, "Sha256": sha(path)}
                    for path in sorted(REPORT.rglob("*")) if path.is_file()]
(REPORT / "binding.json").open("x", encoding="utf-8", newline="\n").write(json.dumps(binding, indent=2) + "\n")
candidate_dir = ROOT / "tasks/done/T-039C.evidence/next-source-v0210-ui-acceptance-20261105-1"
candidate = read(candidate_dir / "review.json")
candidate.update(Manual029BaselineFinalizationPending=False, Manual029BaselineVerified=True,
                 BaselineEvidence=REPORT.relative_to(ROOT).as_posix() + "/binding.json", BaselineBindingSha256=sha(REPORT / "binding.json"))
(candidate_dir / "final-review.json").open("x", encoding="utf-8", newline="\n").write(json.dumps(candidate, indent=2) + "\n")
copy(candidate_dir / "binding.json", candidate_dir / "binding-before-final-review.json")
record = read(candidate_dir / "binding.json")
record["Files"] = [{"Path": path.relative_to(candidate_dir).as_posix(), "Bytes": path.stat().st_size, "Sha256": sha(path)}
                   for path in sorted(candidate_dir.rglob("*")) if path.is_file() and path.name != "binding.json"]
(candidate_dir / "binding.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({"BaselineVersion": "0.2.9", "AppPid": 5672, "NewRequestAndHistoryVerified": True,
                  "UserScreenshotActuallyReviewed": True, "NextCandidate": "0.2.10", "PublicationAuthorized": False}, indent=2))
