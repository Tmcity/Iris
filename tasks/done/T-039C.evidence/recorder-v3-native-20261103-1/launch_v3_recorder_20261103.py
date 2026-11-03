"""Launch only the prepared, read-only observer and interpret the guest exit code."""
from pathlib import Path
import json
import subprocess
import sys

root = Path("F:/Iris")
launch = json.loads((root / "tasks/T-039C.coordination/v3-recorder-launch-20261103-1.json").read_text(encoding="utf-8"))
assert launch["CommandContainsOnlyOwnedObserverCode"] is True and launch["ProductInstallationOrUpdatePerformed"] is False
result = subprocess.run(["wsb.exe", "exec", "--id", "590f8848-f6b4-4bdb-a151-85c4a4f19305",
                         "--run-as", "ExistingLogin", "--command", launch["Command"], "--raw"],
                        stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
sys.stdout.buffer.write(result.stdout)
if result.returncode:
    sys.exit(result.returncode)
response = json.loads(result.stdout.decode("utf-8-sig"))
sys.exit(0 if response["ExitCode"] == 0 else 1)
