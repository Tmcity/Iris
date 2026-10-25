"""Run an owned validation command and preserve its exact output and exit code."""
from pathlib import Path
import datetime
import json
import os
import subprocess
import sys

root = Path(__file__).resolve().parents[4]
label, *command = sys.argv[1:]
if command and command[0] == "--":
    command.pop(0)
if not label or not command or Path(label).name != label:
    raise ValueError("A unique label and command are required")
destination = Path(__file__).resolve().parent
log = destination / (label + ".log")
metadata = destination / (label + ".command.json")
if log.exists() or metadata.exists():
    raise FileExistsError(label)
started = datetime.datetime.now(datetime.timezone.utc).isoformat()
environment = os.environ.copy()
environment["IRIS_R4_EVIDENCE"] = str(destination / (label + "-results"))
with log.open("xb") as output:
    result = subprocess.run(command, cwd=root, env=environment, stdout=output, stderr=subprocess.STDOUT)
metadata.write_text(json.dumps({"Command": command, "WorkingDirectory": str(root),
    "StartedUtc": started, "FinishedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    "ExitCode": result.returncode, "Output": str(log.relative_to(root))}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
sys.stdout.buffer.write(log.read_bytes())
print("\nRecorded exit code:", result.returncode)
raise SystemExit(result.returncode)
