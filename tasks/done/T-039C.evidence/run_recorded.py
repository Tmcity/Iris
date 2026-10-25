"""Run an authorized local command while preserving exact bytes and its real exit code."""
from pathlib import Path
import datetime
import json
import os
import subprocess
import sys

evidence = Path(__file__).resolve().parent
root = evidence.parents[2]
label, *command = sys.argv[1:]
if command and command[0] == "--":
    command.pop(0)
if Path(label).name != label or not command:
    raise ValueError("Unique label and command required")
log = evidence / (label + ".log")
metadata = evidence / (label + ".command.json")
if log.exists() or metadata.exists():
    raise FileExistsError(label)
environment = os.environ.copy()
environment["GIT_TERMINAL_PROMPT"] = "0"
environment["GCM_INTERACTIVE"] = "Never"
started = datetime.datetime.now(datetime.timezone.utc).isoformat()
with log.open("xb") as stream:
    result = subprocess.run(command, cwd=root, env=environment, stdout=stream, stderr=subprocess.STDOUT)
metadata.write_text(json.dumps({"Command": command, "WorkingDirectory": str(root), "StartedUtc": started,
    "FinishedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "ExitCode": result.returncode,
    "Output": log.relative_to(root).as_posix()}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
sys.stdout.buffer.write(log.read_bytes())
print("\nRecorded exit code:", result.returncode)
raise SystemExit(result.returncode)
