"""Start the explicitly requested owned T-040 Sandbox and connect its desktop; no Iris operation."""
from pathlib import Path
import datetime
import json
import shutil
import subprocess
import uuid

ROOT = Path("F:/Iris")
DEST = ROOT / "artifacts/t040/manual-verification-20261106-2"
SOURCE_INPUT = ROOT / "artifacts/t040/manual-verification-20261106-1/input"
before = subprocess.run(["wsb.exe", "list", "--raw"], capture_output=True, check=True)
environments = json.loads(before.stdout.decode("utf-8-sig"))["WindowsSandboxEnvironments"]
if environments:
    raise RuntimeError("A Sandbox already exists; inspect and reuse it instead of creating another.")
DEST.mkdir(exist_ok=False)
shutil.copytree(SOURCE_INPUT, DEST / "input")
(DEST / "output").mkdir()
(DEST / "launcher").mkdir()
identifier = str(uuid.uuid4())
state = {"SandboxId": identifier, "Stage": "start-requested", "CreatedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
         "InputDirectory": str(DEST / "input"), "OutputDirectory": str(DEST / "output"),
         "UserExplicitlyRequestedSandboxStart": True, "IrisInstalled": False, "DesktopConnectionRequested": False, "Failure": None}
def persist():
    (DEST / "session.json").write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8")
def run(label, command):
    started = datetime.datetime.now(datetime.timezone.utc).isoformat()
    result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    (DEST / "launcher" / (label + ".log")).write_bytes(result.stdout)
    (DEST / "launcher" / (label + ".command.json")).write_text(json.dumps({"Command": command, "StartedUtc": started,
        "FinishedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "ExitCode": result.returncode}, indent=2) + "\n", encoding="utf-8")
    print(label + ": exit=" + str(result.returncode), flush=True)
    if result.returncode:
        raise RuntimeError(label + " failed; retain the session and inspect its original log.")
    return result.stdout
persist()
try:
    config = "<Configuration><vGPU>Disable</vGPU><Networking>Enable</Networking><ClipboardRedirection>Disable</ClipboardRedirection><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput><PrinterRedirection>Disable</PrinterRedirection><MemoryInMB>4096</MemoryInMB></Configuration>"
    (DEST / "Iris-T040.wsb").write_text(config + "\n", encoding="utf-8")
    run("start", ["wsb.exe", "start", "--id", identifier, "--config", config, "--raw"])
    state["Stage"] = "started"; persist()
    run("share-input", ["wsb.exe", "share", "--id", identifier, "--host-path", str(DEST / "input"), "--sandbox-path", r"C:\IrisT040Start", "--raw"])
    run("share-output", ["wsb.exe", "share", "--id", identifier, "--host-path", str(DEST / "output"), "--sandbox-path", r"C:\IrisT040Output", "--allow-write", "--raw"])
    state["Stage"] = "desktop-connection-requested"; state["DesktopConnectionRequested"] = True; persist()
    run("connect", ["wsb.exe", "connect", "--id", identifier, "--raw"])
    listed = json.loads(run("list-after-connect", ["wsb.exe", "list", "--raw"]).decode("utf-8-sig"))
    assert identifier in {item["Id"] for item in listed["WindowsSandboxEnvironments"]}
    state["Stage"] = "started-and-connected"; persist()
    print(json.dumps(state, indent=2), flush=True)
except Exception as error:
    state["Failure"] = {"Type": type(error).__name__, "Message": str(error)}; persist()
    raise
