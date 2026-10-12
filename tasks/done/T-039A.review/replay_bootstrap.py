"""Replay SDK bootstrap cases in owned child processes; every updater launch is recorded only."""
from pathlib import Path
import datetime
import json
import os
import subprocess
import sys

sys.stdout.reconfigure(encoding="utf-8")
review = Path(__file__).resolve().parent
work = Path("C:/Users/28910/AppData/Local/Temp/iris-t039-sdk-independent")
dll = work / "bin/Release/net10.0-windows10.0.19041.0/Probe.dll"
feed = "F:/Iris/artifacts/t039a/releases"
cases = [
    ("install", "record", ["--veloapp-install", "1.0.0"], {}, "install", 0),
    ("uninstall", "record", ["--veloapp-uninstall", "1.0.0"], {}, "uninstall", 0),
    ("obsolete", "record", ["--veloapp-obsolete", "1.0.0"], {}, "obsolete", 0),
    ("updated", "record", ["--veloapp-updated", "1.1.0"], {}, "updated", 0),
    ("uninstall-throws", "throw", ["--veloapp-uninstall", "1.0.0"], {}, "uninstall", -1),
    ("malformed-hook", "record", ["--veloapp-uninstall", "invalid"], {}, None, -1),
    ("missing-version", "record", ["--veloapp-uninstall"], {}, None, None),
    ("ordinary-help", "record", ["--help"], {}, None, None),
    ("real-exit", "real-exit", ["--veloapp-uninstall", "1.0.0"], {}, "uninstall", 0),
    ("first-run", "record", [], {"VELOPACK_FIRSTRUN": "1"}, "first-run", None),
    ("restarted", "record", [], {"VELOPACK_RESTART": "1"}, "restarted", None),
    ("auto-on", "auto-on", [feed], {}, None, 0),
    ("auto-off", "auto-off", [feed], {}, None, None),
]
results = []
for name, mode, arguments, variables, hook_name, sdk_exit in cases:
    label = "sdk-independent-boot-" + name
    command = [sys.executable, str(review / "run_review.py"), "--label", label, "--cwd", str(work), "--", "dotnet", str(dll), "boot", str(work / "bootstrap" / name), mode, *arguments]
    environment = dict(os.environ)
    environment.pop("VELOPACK_FIRSTRUN", None)
    environment.pop("VELOPACK_RESTART", None)
    environment.update(variables)
    completed = subprocess.run(command, env=environment, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    raw = (review / (label + ".log")).read_text(encoding="utf-8-sig")
    hooks = [json.loads(line.removeprefix("HOOK ")) for line in raw.splitlines() if line.startswith("HOOK ")]
    returned = [json.loads(line.removeprefix("BOOT_RETURN ")) for line in raw.splitlines() if line.startswith("BOOT_RETURN ")]
    exit_calls = [int(line.removeprefix("PROCESS_EXIT ")) for line in raw.splitlines() if line.startswith("PROCESS_EXIT ")]
    errors = []
    if completed.returncode != 0:
        errors.append("probe process failed")
    if [hook["Name"] for hook in hooks] != ([hook_name] if hook_name is not None else []):
        errors.append("unexpected hooks")
    if any(hook["Apartment"] != "STA" for hook in hooks):
        errors.append("callback did not run on STA")
    if exit_calls != ([sdk_exit] if sdk_exit is not None else []):
        errors.append("unexpected SDK exit calls")
    if name == "real-exit":
        if returned or "BOOT_FINALLY" in raw:
            errors.append("real Process.Exit returned or ran finally")
    elif len(returned) != 1:
        errors.append("missing bootstrap result")
    else:
        result = returned[0]
        if result["ExitCode"] != sdk_exit or result["NormalMainEligible"] != (sdk_exit is None):
            errors.append("unexpected normal-main eligibility")
        if result["Starts"] != (1 if name == "auto-on" else 0):
            errors.append("unexpected recorded updater count")
        if "BOOT_FINALLY" not in raw:
            errors.append("mock-exit process omitted finally")
        if name in ("auto-on", "auto-off"):
            remaining = result["PackagesRemaining"]
            if len(remaining) != 2 or any(item.endswith("-delta.nupkg") for item in remaining):
                errors.append("unexpected ordinary bootstrap package cleanup")
    results.append({"case": name, "log": label + ".log", "environmentOverridesForChildOnly": variables, "processExitCode": completed.returncode, "sdkExitCalls": exit_calls, "hookNames": [hook["Name"] for hook in hooks], "errors": errors})

report = {"completedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(), "usesActualSdkAndOwnedChildProcesses": True, "startsActualUpdater": False, "cases": results}
with (review / "sdk-independent-bootstrap-result.json").open("x", encoding="utf-8") as output:
    json.dump(report, output, ensure_ascii=False, indent=2)
failures = [result for result in results if result["errors"]]
print(json.dumps({"cases": len(results), "failures": failures, "actualUpdaterStarted": False}, ensure_ascii=False))
sys.exit(1 if failures else 0)
