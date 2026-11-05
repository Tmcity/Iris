"""Bind user confirmation to saved data, actual old processes and the synthetic history."""
from pathlib import Path
import datetime
import hashlib
import json
import sqlite3

ROOT = Path("F:/Iris")
SESSION = ROOT / "artifacts/t039c/manual-verification-v022-20261105-1/output/session-14d55ff50fd94d3abe8d4474aafd5365"
REPORT = ROOT / "tasks/done/T-039C.evidence/manual-old-ready-before-v027-20261105-1"
HISTORY = ROOT / "artifacts/t039c/old-history-independent-v027-20261105-1/history.db"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


old_path = sorted(SESSION.glob("old-ready-*.json"))[-1]
supplement_path = sorted(SESSION.glob("old-ready-supplement-*"))[-1]
inspection_path = sorted(SESSION.glob("old-ready-readonly-*"))[-1]
old, supplement = read(old_path), read(supplement_path / "supplement.json")
inspection = read(inspection_path / "inspection.json")
status = read(supplement_path / "watcher-v2-status.json")
pointer = read(supplement_path / "active-recorder-v2.json")
assert old["InstalledSource"]["Version"] == inspection["ProductSource"]["Version"] == "0.2.2"
assert len(old["Processes"]) == 1 and old["Processes"][0]["Pid"] == 7312
assert old["Processes"][0]["StartedUtc"] == "2026-11-05T05:05:27.9287450Z"
assert {item["Pid"] for item in inspection["Processes"]} == {7312, 7328, 8152}
assert supplement["CheckForUpdates"] is inspection["CheckForUpdates"] is False
assert len(inspection["SyntheticProfiles"]) == 1
profile = inspection["SyntheticProfiles"][0]
assert profile["BaseUrl"] == "http://127.0.0.1:18039/v1" and profile["Model"] == "iris-fixture"
assert profile["Kind"] == "OpenAICompatible" and profile["Enabled"] and profile["HasCredentialSetReference"]
data_hashes = {item["Path"]: item.get("Sha256") for item in old["DataHashes"]}
expected = {
    "settings.json": "84d85182866599005f6802db616fb89094d6c19dd1d7ade8ca15db9fa11d30e8",
    "secrets.dat": "b977aecf397ecc9dc57f2301181a20372f7e7bc37b468091cd4fb1764e97f9d7",
    "history.db": "c4960f0964e6d1ad267f6e735da6ddaea70e4b7ef63bb4d22a0dd76c6e67a497",
}
assert all(data_hashes[name] == value for name, value in expected.items())
assert inspection["SettingsSha256"] == expected["settings.json"] and inspection["SecretsSha256"] == expected["secrets.dat"]
assert sha(HISTORY) == sha(supplement_path / "history.db") == expected["history.db"]
assert pointer["RecorderVersion"] == status["RecorderVersion"] == 3
assert pointer["RecorderPid"] == status["RecorderPid"] == 8152 and pointer["Ready"] and status["Ready"]
assert pointer["SourceSha256"] == "3515546d9e344331cc2c95fc4c97f60b213bfc06ae11ada98d96349b3e3dac62"
assert status["Failure"] is None
requests = [json.loads(line) for line in (supplement_path / "provider-requests.jsonl").read_text(encoding="utf-8-sig").splitlines() if line]
assert any(item["Method"] == "POST" and item["Path"] == "/v1/chat/completions" and item["Status"] == 200
           and item["SyntheticCredentialMatched"] is True for item in requests)
with sqlite3.connect(HISTORY.as_uri() + "?mode=ro", uri=True) as connection:
    connection.execute("PRAGMA query_only=ON")
    assert connection.execute("PRAGMA quick_check").fetchall() == [("ok",)]
    rows = connection.execute("""SELECT h.id, h.created_at, h.origin, h.source_text, h.source_lang, h.target_lang,
        r.id, r.provider_id, r.provider_name, r.model, r.result_text
        FROM translation_history h JOIN translation_result r ON r.history_id = h.id WHERE h.source_text = ?""",
        ("Iris acceptance check",)).fetchall()
assert len(rows) == 1 and rows[0][7] == profile["Id"] and rows[0][9] == "iris-fixture"
assert rows[0][10] == "Iris 公共升级验证"
assert not any("0.2.7" in item["Name"] for item in old["Packages"])
for label in ("v027-fresh-old-ready-readonly-1", "v027-fresh-old-ready-detail-readonly-1"):
    command_path = ROOT / "tasks/T-039C.coordination" / (label + ".command.json")
    log_path = command_path.with_name(label + ".log")
    command = read(command_path)
    assert command["exitCode"] == 0 and sha(log_path) == command["logSha256"]
    assert json.loads(log_path.read_text(encoding="utf-8-sig"))["ExitCode"] == 0

REPORT.mkdir(exist_ok=False)


def copy(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    raw = source.read_bytes()
    target.open("xb").write(raw)
    assert target.read_bytes() == raw


for path in sorted(SESSION.glob("old-ready-*.json")):
    copy(path, REPORT / path.name)
for source, target in ((supplement_path, "old-ready-supplement"), (inspection_path, "read-only-inspection")):
    for path in source.iterdir():
        if path.is_file():
            copy(path, REPORT / target / path.name)
for label in ("v027-fresh-old-ready-readonly-1", "v027-fresh-old-ready-detail-readonly-1"):
    for suffix in (".command.json", ".log"):
        copy(ROOT / "tasks/T-039C.coordination" / (label + suffix), REPORT / "coordination" / (label + suffix))
copy(ROOT / "tasks/T-039C.coordination/v027-user-old-ready-feedback-20261105.json", REPORT / "user-feedback.json")
history = {"Columns": ["HistoryId", "CreatedAt", "Origin", "SourceText", "SourceLanguage", "TargetLanguage",
                       "ResultId", "ProviderId", "ProviderName", "Model", "ResultText"], "Rows": rows,
           "QuickCheck": "ok", "ReadOnlyHostCopy": str(HISTORY), "SourceDbSha256": sha(HISTORY)}
(REPORT / "history-independent.json").write_text(json.dumps(history, ensure_ascii=False, indent=2) + "\n",
                                                encoding="utf-8", newline="\n")
files = [{"Path": path.relative_to(REPORT).as_posix(), "Bytes": path.stat().st_size, "Sha256": sha(path)}
         for path in sorted(REPORT.rglob("*")) if path.is_file()]
binding = {"ReviewedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
           "SandboxId": "2fc7c168-2f34-437a-8319-8974e6f592b2", "Version": "0.2.2",
           "AppPid": 7312, "AppStartedUtc": old["Processes"][0]["StartedUtc"], "RecorderPid": 8152,
           "ProviderPid": 7328, "CheckForUpdates": False, "SyntheticProfile": profile,
           "DataHashes": expected, "SyntheticCredentialMatchedInRealPost": True,
           "OldSyntheticHistoryIndependentlyVerified": True, "OldReadyVerified": True,
           "SecretContentsCollected": False, "NewVersionPackagePresent": False,
           "RealUiUpgradeVerified": False, "RestartCountVerified": False, "Files": files}
(REPORT / "binding.json").write_text(json.dumps(binding, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({key: binding[key] for key in ("SandboxId", "Version", "AppPid", "RecorderPid",
    "OldReadyVerified", "SyntheticCredentialMatchedInRealPost", "OldSyntheticHistoryIndependentlyVerified",
    "RealUiUpgradeVerified")}, indent=2))
