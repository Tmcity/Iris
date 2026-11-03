"""Freeze the specific read-only guest observation before the v0.2.6 checkpoint."""
from pathlib import Path
import argparse
import datetime
import hashlib
import json
import shutil

ROOT = Path("F:/Iris")
SESSION = ROOT / "artifacts/t039c/manual-verification-v022-20261101-1/output/session-1c8333f923744572ac2c71bab9576af5"
REPORT = ROOT / "tasks/done/T-039C.evidence/manual-old-ready-before-v026-20261103-1"
COORDINATION = ROOT / "tasks/T-039C.coordination"
parser = argparse.ArgumentParser()
parser.add_argument("--label", choices=("v026-old-ready-before-approval-2",), required=True)
LABEL = parser.parse_args().label

def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))

def utc(value):
    return datetime.datetime.fromisoformat(value.replace("Z", "+00:00"))

command = read(COORDINATION / (LABEL + ".command.json"))
log = COORDINATION / (LABEL + ".log")
assert command["exitCode"] == 0 and hashlib.sha256(log.read_bytes()).hexdigest() == command["logSha256"]
assert json.loads(log.read_text(encoding="utf-8-sig"))["ExitCode"] == 0
old_path = sorted(SESSION.glob("old-ready-*.json"))[-1]
supplement_path = sorted(SESSION.glob("old-ready-supplement-*"))[-1]
old = read(old_path)
supplement = read(supplement_path / "supplement.json")
observer = read(supplement_path / "watcher-v2-status.json")
recorder = supplement["V2Recorder"]
assert utc(command["startedAt"]) <= utc(old["Utc"]) <= utc(command["completedAt"])
assert utc(command["startedAt"]) <= utc(supplement["Utc"]) <= utc(command["completedAt"])
assert old["InstalledSource"]["Version"] == "0.2.2" and len(old["Processes"]) == 1
assert old["Processes"][0]["Pid"] == 6604 and old["Processes"][0]["StartedUtc"] == "2026-11-02T04:25:01.2204360Z"
assert supplement["CheckForUpdates"] is False and supplement["SecretsContentsCollected"] is False
assert observer["Ready"] is True and observer["Failure"] is None
assert observer["RecorderVersion"] == recorder["RecorderVersion"] == 3
assert observer["RecorderPid"] == recorder["RecorderPid"] == 4604
assert recorder["SourceSha256"] == "3515546d9e344331cc2c95fc4c97f60b213bfc06ae11ada98d96349b3e3dac62"
assert recorder["ProductOperationPerformed"] is False and recorder["AuxiliaryProbe"]["RealIrisExecutable"] is False
assert abs((utc(old["Utc"]) - utc(observer["LastHeartbeatUtc"])).total_seconds()) < 60
expected = {"settings.json": "4b94fbcbe8058b3b7415d1264f7e0766c49f3cd9bc9d9777c96e0fef156a2101",
            "secrets.dat": "6029155b4f25ed9ffd4d4091ebd6e8b5adbb380d23aaaadc7828e65e403886a9",
            "history.db": "8bb46ccb59258f8ce804a0b941635de6e031e68c618375306c7ccae08538f223"}
data = {item["Path"]: item.get("Sha256") for item in old["DataHashes"]}
assert all(data[name] == value for name, value in expected.items())
assert not any("0.2.6" in item["Name"] for item in old["Packages"])
prior = next(item for item in old["Packages"] if item["Name"] == "Iris-0.2.2-full.nupkg")
assert prior["Sha256"] == "c86982f930aafcd142167bbd871d03b588630f7d1d625da968a811daa2dd5d14"
assert prior["Bytes"] == 64627273
REPORT.mkdir(exist_ok=False)
shutil.copyfile(old_path, REPORT / "old-ready.json")
for suffix in (".command.json", ".log"):
    shutil.copyfile(COORDINATION / (LABEL + suffix), REPORT / (LABEL + suffix))
for path in supplement_path.iterdir():
    if path.is_file() and (path.suffix in {".json", ".jsonl", ".log"} or path.name in {"history.db", "history.db-wal"}):
        shutil.copyfile(path, REPORT / path.name)
files = [{"Path": path.relative_to(REPORT).as_posix(), "Bytes": path.stat().st_size,
          "Sha256": hashlib.sha256(path.read_bytes()).hexdigest()} for path in sorted(REPORT.rglob("*")) if path.is_file()]
binding = {"ObservedUtc": old["Utc"], "GuestId": "590f8848-f6b4-4bdb-a151-85c4a4f19305",
           "Version": "0.2.2", "AppPid": 6604, "AppStartedUtc": old["Processes"][0]["StartedUtc"],
           "CheckForUpdates": False, "RecorderPid": 4604, "RecorderVersion": 3, "RecorderReady": True,
           "DataHashesUnchanged": expected, "SecretsContentsCollected": False,
           "PreviousPublicFull": prior, "NewVersionPackagePresent": False, "Files": files}
(REPORT / "binding.json").write_text(json.dumps(binding, indent=2) + "\n", encoding="utf-8", newline="\n")
print(json.dumps({key: binding[key] for key in ("ObservedUtc", "Version", "AppPid", "RecorderPid", "RecorderReady", "CheckForUpdates", "NewVersionPackagePresent")}, indent=2))
