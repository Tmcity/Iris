"""Inspect the developer-owned test dump offline; no live process attachment."""
from pathlib import Path
import datetime
import hashlib
import json
import subprocess
import sys

sys.stdout.reconfigure(encoding="utf-8")
root = Path(__file__).resolve().parent
label = sys.argv[1]
if not label or any(ch not in "abcdefghijklmnopqrstuvwxyz0123456789-" for ch in label):
    raise ValueError("invalid evidence label")
command = ["F:/Iris/artifacts/t039a/diagnostics/dotnet-dump.exe", "analyze", "F:/Iris/artifacts/t039a/ipc-hung.dmp", "-c", "setsymbolserver -disable"]
for instruction in sys.argv[2:]:
    command.extend(["-c", instruction])
command.extend(["-c", "exit"])
started = datetime.datetime.now(datetime.timezone.utc).isoformat()
with (root / (label + ".log")).open("xb") as output:
    completed = subprocess.run(command, cwd="F:/Iris", stdout=output, stderr=subprocess.STDOUT)
raw = (root / (label + ".log")).read_bytes()
record = {"command": command, "startedAt": started, "completedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(), "exitCode": completed.returncode, "operatesOnExistingDumpOnly": True, "logSha256": hashlib.sha256(raw).hexdigest()}
with (root / (label + ".command.json")).open("x", encoding="utf-8") as output:
    json.dump(record, output, indent=2)
print(raw.decode("utf-8-sig", errors="replace")[-24000:])
sys.exit(completed.returncode)
