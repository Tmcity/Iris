"""Independently retain the actual V3 observer source and native auxiliary probe."""
from pathlib import Path
import hashlib
import json
import shutil
import xml.etree.ElementTree as ET

ROOT = Path("F:/Iris")
SESSION = ROOT / "artifacts/t039c/manual-verification-v022-20261101-1/output/session-1c8333f923744572ac2c71bab9576af5"
REPORT = ROOT / "tasks/done/T-039C.evidence/recorder-v3-native-20261103-1"
COORDINATION = ROOT / "tasks/T-039C.coordination"

def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))

def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

pointer = read(SESSION / "active-recorder-v2.json")
assert pointer["RecorderVersion"] == 3 and pointer["RecorderPid"] == 4604
assert pointer["Ready"] is True and pointer["ProductOperationPerformed"] is False and pointer["Failure"] is None
output = SESSION / Path(pointer["OutputDirectory"].replace("\\", "/")).name
assert output.name == "recorder-v3-fcf5444ba627427aa2a4fe856f72da43"
source = output / "manual-process-watch-v3.ps1"
assert digest(source) == digest(COORDINATION / source.name) == pointer["SourceSha256"]
startup = read(output / "startup-result.json")
assert startup["Ready"] is True and startup["AuxiliaryProbe"]["RealIrisExecutable"] is False
events = [json.loads(line) for line in (output / "process-events-v2.jsonl").read_text(encoding="utf-8-sig").splitlines() if line]
assert all(event["RecorderVersion"] == 3 for event in events)
assert len([event for event in events if event["Kind"] == "QueueRemovalProbeCompleted" and event["AlreadyAbsentHandled"]]) == 1
assert any(event["Kind"] == "QueueRemovalAlreadyAbsent" and event["ErrorType"] == "System.ArgumentException" for event in events)
baseline = [event for event in events if event["Kind"] == "Baseline" and event["Identity"]["Role"] == "ApplicationOrClient"]
assert len(baseline) == 1 and baseline[0]["Identity"]["Pid"] == 6604
assert baseline[0]["Identity"]["CreatedUtc"] == "2026-11-02T04:25:01.2204360Z"
probe = pointer["AuxiliaryProbe"]
probe_events = [event for event in events if event.get("Pid") == probe["Pid"]]
assert len(probe_events) == 2 and {event["Kind"] for event in probe_events} == {"Started", "Exited"}
assert all(event["Identity"]["Role"] == "AuxiliaryProbe" and event["Identity"]["OwnedPathVerified"] for event in probe_events)
assert next(event for event in probe_events if event["Kind"] == "Exited")["ExitCode"] == 0
assert digest(output / "probe/Iris.exe") == probe["SystemCmdCopySha256"]
for label in ("v3-recorder-powershell51-parse-2", "v3-native-observer-start-1"):
    command = read(COORDINATION / (label + ".command.json"))
    assert command["exitCode"] == 0 and digest(COORDINATION / (label + ".log")) == command["logSha256"]
assert read(output / "watcher-v2-status.json")["Ready"] is True
REPORT.mkdir(exist_ok=False)
for path in output.iterdir():
    if path.is_file() and path.suffix in {".json", ".jsonl", ".ps1"}:
        shutil.copyfile(path, REPORT / path.name)
shutil.copyfile(SESSION / "active-recorder-v2.json", REPORT / "active-recorder-v3-pointer.json")
shutil.copyfile(output / "probe/probe.cmd", REPORT / "auxiliary-probe.cmd")
for name in ("start-process-watch-v3.ps1", "check_recorder_powershell51.ps1", "launch_v3_recorder_20261103.py", "v3-recorder-launch-20261103-1.json"):
    shutil.copyfile(COORDINATION / name, REPORT / name)
for label in ("v3-recorder-powershell51-parse-1", "v3-recorder-powershell51-parse-2", "v3-native-observer-start-1"):
    for suffix in (".command.json", ".log"):
        shutil.copyfile(COORDINATION / (label + suffix), REPORT / (label + suffix))
files = [{"Path": path.name, "Bytes": path.stat().st_size, "Sha256": digest(path)} for path in sorted(REPORT.iterdir()) if path.is_file()]
binding = {"RecorderVersion": 3, "RecorderPid": 4604, "SourceSha256": pointer["SourceSha256"],
    "OriginalFailurePreserved": "recorder-v2-failure-20261103-1", "QueueRemovalRaceHandled": True,
    "ActualNativeAuxiliaryProbePid": probe["Pid"], "ActualNativeAuxiliaryExitCode": 0,
    "ProbeBinarySha256": probe["SystemCmdCopySha256"], "OldNormalAppBaselinePid": 6604,
    "ProductOperationPerformed": False, "ProductionUpgradeOrRestartInferred": False, "Files": files}
(REPORT / "binding.json").write_text(json.dumps(binding, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({key: binding[key] for key in ("RecorderVersion", "RecorderPid", "QueueRemovalRaceHandled", "ActualNativeAuxiliaryProbePid", "ActualNativeAuxiliaryExitCode", "OldNormalAppBaselinePid", "ProductionUpgradeOrRestartInferred")}, indent=2))
