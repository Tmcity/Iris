"""Run only the synthetic envelope probe in an owned, offline fresh Windows Sandbox."""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess
import sys
import time
import uuid

sys.stdout.reconfigure(encoding="utf-8")
review = Path(__file__).resolve().parent / "sandbox-v2"
review.mkdir(exist_ok=True)
identifier = str(uuid.uuid4())
work = Path("F:/Iris/artifacts/t039b-envelope-sandbox") / identifier
inputs, output = work / "input", work / "output"
inputs.mkdir(parents=True)
output.mkdir()
source = Path("C:/Users/28910/AppData/Local/Temp/iris-t039b-envelope-20261013")
hashes = []
for name in ("valid", "corrupt", "bounds"):
    path = source / ("EnvelopeProbe-v2-" + name + ".exe")
    shutil.copyfile(path, inputs / path.name)
    hashes.append({"file": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
shutil.copyfile(Path(__file__).resolve().parent / "guest_probe.ps1", inputs / "guest_probe.ps1")
hashes.append({"file": "guest_probe.ps1", "sha256": hashlib.sha256((inputs / "guest_probe.ps1").read_bytes()).hexdigest()})
cli = "C:/Users/28910/AppData/Local/Microsoft/WindowsApps/wsb.exe"
config = "<Configuration><vGPU>Disable</vGPU><Networking>Disable</Networking><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput><ClipboardRedirection>Disable</ClipboardRedirection><PrinterRedirection>Disable</PrinterRedirection><MemoryInMB>4096</MemoryInMB></Configuration>"
with (review / "intent.json").open("x", encoding="utf-8") as stream:
    json.dump({"SandboxId": identifier, "Configuration": config, "ReadOnlyInput": str(inputs), "WritableOutput": str(output), "InputHashes": hashes, "Purpose": "Synthetic single-file envelope, no Iris/real installer/user data"}, stream, indent=2)


def run(label, arguments):
    result = subprocess.run([cli, *arguments], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            creationflags=subprocess.CREATE_NO_WINDOW)
    with (review / (label + ".log")).open("xb") as stream:
        stream.write(result.stdout)
    with (review / (label + ".command.json")).open("x", encoding="utf-8") as stream:
        json.dump({"command": [cli, *arguments], "exitCode": result.returncode}, stream, indent=2)
    print(label + " exit=" + str(result.returncode), flush=True)
    print(result.stdout.decode("utf-8-sig", errors="replace")[-2000:], flush=True)
    return result


before = run("list-before", ["list", "--raw"])
if before.returncode:
    raise SystemExit(before.returncode)
if json.loads(before.stdout.decode("utf-8-sig")).get("WindowsSandboxEnvironments"):
    raise RuntimeError("An existing sandbox is present; this probe did not start or stop another session.")
started = False
try:
    result = run("start", ["start", "--id", identifier, "--config", config, "--raw"])
    if result.returncode:
        raise RuntimeError("Owned sandbox did not start.")
    started = True
    for label, arguments in [
        ("share-input", ["share", "--id", identifier, "--host-path", str(inputs), "--sandbox-path", "C:\\IrisEnvelopeInput", "--raw"]),
        ("share-output", ["share", "--id", identifier, "--host-path", str(output), "--sandbox-path", "C:\\IrisEnvelopeOutput", "--allow-write", "--raw"]),
    ]:
        if run(label, arguments).returncode:
            raise RuntimeError(label + " failed")
    command = subprocess.list2cmdline(["C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "C:\\IrisEnvelopeInput\\guest_probe.ps1"])
    launched = run("execute", ["exec", "--id", identifier, "--command", command, "--run-as", "System", "--raw"])
    if launched.returncode:
        raise RuntimeError("Guest probe command failed")
    deadline = time.monotonic() + 50
    while not (output / "result.json").exists() and time.monotonic() < deadline:
        time.sleep(0.25)
    if not (output / "result.json").exists():
        raise RuntimeError("Guest result did not arrive; preserve available output and inspect.")
    report = json.loads((output / "result.json").read_text(encoding="utf-8-sig"))
    if not report["SharedDotnet10Absent"] or not all(case["Passed"] for case in report["Cases"]):
        raise RuntimeError("Guest envelope verification failed")
    print(json.dumps({"guestOs": report["Environment"]["Version"], "casesPassed": len(report["Cases"]), "sharedDotnet10AbsentBeforeAndAfter": True, "actualInstallerExecuted": False}), flush=True)
finally:
    for path in output.glob("*"):
        if path.is_file():
            destination = review / "output" / path.name
            destination.parent.mkdir(exist_ok=True)
            if not destination.exists():
                shutil.copyfile(path, destination)
    if started:
        stopped = run("stop", ["stop", "--id", identifier, "--raw"])
        if stopped.returncode:
            print("Owned sandbox may remain running; inspect its saved identifier before retrying.", flush=True)
    run("list-after", ["list", "--raw"])
