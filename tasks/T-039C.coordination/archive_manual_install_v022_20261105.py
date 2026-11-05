"""Bind the new guest's real 0.2.2 installation and its observed prerequisite delay."""
from pathlib import Path
import datetime
import hashlib
import json
import re

ROOT = Path("F:/Iris")
MATERIAL = ROOT / "artifacts/t039c/manual-verification-v022-20261105-1"
SESSION = MATERIAL / "output/session-14d55ff50fd94d3abe8d4474aafd5365"
REPORT = ROOT / "tasks/done/T-039C.evidence/manual-first-install-v022-20261105-1"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def decoded(path):
    raw = path.read_bytes()
    return raw.decode("utf-16" if raw[:2] in (b"\xff\xfe", b"\xfe\xff") else "utf-8-sig")


clean_path = sorted(SESSION.glob("clean-*.json"))[-1]
installed_path = sorted(SESSION.glob("installed-*.json"))[-1]
verification_path = sorted(SESSION.glob("public-install-verification-*.json"))[-1]
inspection_path = sorted(SESSION.glob("install-inspection-*.json"))[-1]
clean, installed = read(clean_path), read(installed_path)
verification, inspection = read(verification_path), read(inspection_path)
assert clean["Runtimes"] == [] and clean["InstalledSource"] is None and clean["Processes"] == []
assert clean["RuntimeCacheExists"] is False
assert installed["InstalledSource"]["Version"] == verification["Version"] == "0.2.2"
app = next(item for item in installed["Processes"] if item["Name"] == "Iris.App.exe")
assert app["Pid"] == 7312 and app["StartedUtc"] == "2026-11-05T05:05:27.9287450Z"
assert verification["ManifestEntries"] == verification["MatchedEntries"] == len(verification["Payload"]) == 97
expected = {item["Path"]: item for item in read(MATERIAL / "input/PUBLIC_payload-manifest.json")}
assert {item["PayloadPath"] for item in verification["Payload"]} == set(expected)
for item in verification["Payload"]:
    reference = expected[item["PayloadPath"]]
    assert item["Matches"] is True and item["Bytes"] == reference["Bytes"]
    assert item["Sha256"] == reference["Sha256"].lower()
setup = next(item for item in inspection["Downloads"] if item["Name"] == "Iris-0.2.2-Setup.exe")
assert setup["Bytes"] == 116767621 and setup["Sha256"] == (
    "ddae984ba4bcc65e5398ecd7a036afe0485966c5d61fe9b1603cc9bb487e7a0c")
assert inspection["RecorderVersion"] == 3 and inspection["RecorderStatus"]["Ready"] is True
assert inspection["RecorderStatus"]["RecorderPid"] == 8152
assert inspection["ProviderStatus"]["Ready"] is True
assert {item["Pid"] for item in inspection["Processes"]} == {7312, 7328, 8152}
assert inspection["RecorderSourceSha256"] == "3515546d9e344331cc2c95fc4c97f60b213bfc06ae11ada98d96349b3e3dac62"
heartbeat = datetime.datetime.fromisoformat(inspection["RecorderStatus"]["LastHeartbeatUtc"].replace("Z", "+00:00"))
observed = datetime.datetime.fromisoformat(inspection["Utc"].replace("Z", "+00:00"))
assert 0 <= (observed - heartbeat).total_seconds() < 5
for label in ("v027-fresh-installed-readonly-1", "v027-fresh-installed-payload-readonly-1", "v027-fresh-install-diagnostics-readonly-1"):
    command = read(ROOT / "tasks/T-039C.coordination" / (label + ".command.json"))
    log = ROOT / "tasks/T-039C.coordination" / (label + ".log")
    assert command["exitCode"] == 0 and command["logSha256"] == digest(log)
    assert json.loads(log.read_text(encoding="utf-8-sig"))["ExitCode"] == 0

logs = sorted(SESSION.glob("logs-installed-*"))[-1]
runtime_log = next(logs.glob("temp-*125719.log"))
runtime = decoded(runtime_log)
time_pattern = r"\[([0-9T:\-]+)\]"
start = re.search(time_pattern + r"i300: Apply begin", runtime)
end = re.search(time_pattern + r"i399: Apply complete, result: 0x0", runtime)
assert start and end
runtime_seconds = (datetime.datetime.fromisoformat(end[1]) - datetime.datetime.fromisoformat(start[1])).total_seconds()
assert runtime_seconds == 487
gaps = []
for path in sorted(logs.glob("temp-*125719_*.log")):
    previous = None
    for line_number, line in enumerate(decoded(path).splitlines(), 1):
        match = re.search(r"\[(\d\d):(\d\d):(\d\d):(\d{3})\]", line)
        if not match:
            continue
        h, minute, second, millisecond = map(int, match.groups())
        seconds = h * 3600 + minute * 60 + second + millisecond / 1000
        if previous and seconds - previous[0] > 15:
            assert "SOFTWARE RESTRICTION POLICY: Verifying package" in previous[2]
            assert "has a digital signature" in line
            gaps.append({"File": path.name, "StartLine": previous[1], "EndLine": line_number,
                         "Seconds": round(seconds - previous[0], 3), "StartMessage": previous[2], "EndMessage": line})
        previous = seconds, line_number, line
assert len(gaps) == 4 and all(120 <= gap["Seconds"] < 122 for gap in gaps)
sdk = decoded(logs / "Velopack-velopack.log")
assert "[13:05:26] [INFO] Extracting Update.exe" in sdk
assert "[13:05:27] [INFO] Installation completed successfully" in sdk
assert inspection["CapiLogEnabled"] is False

REPORT.mkdir(exist_ok=False)


def copy(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    data = source.read_bytes()
    target.open("xb").write(data)
    assert target.read_bytes() == data


for path in (clean_path, installed_path, verification_path, inspection_path):
    copy(path, REPORT / path.name)
for path in sorted(logs.iterdir()):
    if path.is_file():
        copy(path, REPORT / "original-logs" / path.name)
for name in ("active-session.json", "manual-sandbox-session.json"):
    copy(MATERIAL / "output" / name, REPORT / name)
pointer = read(SESSION / "active-recorder-v2.json")
recorder = SESSION / Path(pointer["OutputDirectory"].replace("\\", "/")).name
copy(SESSION / "active-recorder-v2.json", REPORT / "recorder/active-recorder-v2.json")
for name in ("watcher-v2-status.json", "process-events-v2.jsonl", "startup-result.json"):
    copy(recorder / name, REPORT / "recorder" / name)
for path in sorted((MATERIAL / "input").iterdir()):
    if path.is_file():
        copy(path, REPORT / "input" / path.name)
copy(MATERIAL / "Iris-Manual-Acceptance.wsb", REPORT / "Iris-Manual-Acceptance.wsb")
copy(MATERIAL / "input-fingerprints.json", REPORT / "initial-input-fingerprints.json")
copy(ROOT / "tasks/T-039C.coordination/v027-user-first-install-feedback-20261105.json", REPORT / "user-feedback.json")
for label in ("v027-fresh-installed-readonly-1", "v027-fresh-installed-payload-readonly-1",
              "v027-fresh-install-diagnostics-readonly-1", "v027-fresh-install-inspection-ps51-parse-1"):
    for suffix in (".command.json", ".log"):
        copy(ROOT / "tasks/T-039C.coordination" / (label + suffix), REPORT / "coordination" / (label + suffix))
files = [{"Path": path.relative_to(REPORT).as_posix(), "Bytes": path.stat().st_size, "Sha256": digest(path)}
         for path in sorted(REPORT.rglob("*")) if path.is_file()]
binding = {"ArchivedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
           "SandboxId": "2fc7c168-2f34-437a-8319-8974e6f592b2", "Version": "0.2.2",
           "App": app, "CleanGuestWithoutRuntimeOrCacheVerified": True, "OfficialSetup": setup,
           "PublicInstalledPayloadMatched": 97, "V3ReadyAtInspectionVerified": True,
           "RecorderPid": 8152, "SyntheticProviderPid": 7328, "RuntimeInstallSeconds": runtime_seconds,
           "SoftwareRestrictionPolicySignatureWaits": gaps,
           "SignatureWaitSeconds": round(sum(gap["Seconds"] for gap in gaps), 3),
           "IrisDeploymentLogWindowSeconds": 1, "SpecificCertificateNetworkFailureProven": False,
           "CapiDetailedLogEnabled": False, "UpgradeOldReadyVerified": False,
           "RealUiUpgradeVerified": False, "Files": files}
(REPORT / "binding.json").write_text(json.dumps(binding, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({key: binding[key] for key in ("SandboxId", "PublicInstalledPayloadMatched",
    "CleanGuestWithoutRuntimeOrCacheVerified", "V3ReadyAtInspectionVerified", "RuntimeInstallSeconds",
    "SignatureWaitSeconds", "IrisDeploymentLogWindowSeconds", "UpgradeOldReadyVerified")}, indent=2))
