"""Run an explicit review command, retaining raw output and the actual return code."""
from pathlib import Path
import argparse
import datetime
import hashlib
import json
import os
import subprocess
import sys

sys.stdout.reconfigure(encoding="utf-8")
parser = argparse.ArgumentParser()
parser.add_argument("--label", required=True)
parser.add_argument("--cwd", required=True)
parser.add_argument("command", nargs=argparse.REMAINDER)
args = parser.parse_args()
if any(ch not in "abcdefghijklmnopqrstuvwxyz0123456789-" for ch in args.label):
    raise ValueError("invalid review label")
command = args.command[1:] if args.command[:1] == ["--"] else args.command
if not command:
    raise ValueError("missing command")
root = Path(__file__).resolve().parent
started = datetime.datetime.now(datetime.timezone.utc).isoformat()
with (root / (args.label + ".log")).open("xb") as output:
    completed = subprocess.run(command, cwd=args.cwd, env=dict(os.environ), stdout=output, stderr=subprocess.STDOUT)
raw = (root / (args.label + ".log")).read_bytes()
record = {"command": command, "cwd": args.cwd, "startedAt": started, "completedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(), "exitCode": completed.returncode, "logSha256": hashlib.sha256(raw).hexdigest()}
with (root / (args.label + ".command.json")).open("x", encoding="utf-8") as output:
    json.dump(record, output, indent=2)
try:
    decoded = raw.decode("utf-8-sig")
except UnicodeDecodeError:
    decoded = raw.decode("gb18030", errors="replace")
print(decoded[-18000:])
sys.exit(completed.returncode)
