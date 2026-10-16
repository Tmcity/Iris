"""Run only the fixed review fixture after the developer grants a short serial window.

The state is released by the process itself, so a delayed model reply cannot hold the
developer's build/test window after the commands have actually finished.
"""
from pathlib import Path
import argparse
import datetime
import json
import os
import subprocess
import sys
import time

sys.stdout.reconfigure(encoding="utf-8")
parser = argparse.ArgumentParser()
parser.add_argument("--window", type=int, required=True)
parser.add_argument("--wait-seconds", type=int, default=3600)
args = parser.parse_args()
if args.window < 2 or not 60 <= args.wait_seconds <= 3600:
    raise ValueError("Invalid new serial review window")
repo = Path("F:/Iris")
review = Path(__file__).resolve().parent
work = repo / "artifacts/t039b/independent-update-review-1"
state_file = review / f"window-{args.window}-state.json"
grant_file = review / f"window-{args.window}-grant.json"
nonce = f"t039b-independent-update-review-{args.window}"
if state_file.exists() or grant_file.exists():
    raise RuntimeError("This serial review window already exists; never restart it implicitly")
state = {"nonce": nonce, "runnerPid": os.getpid(), "status": "waiting", "results": []}


def save(status):
    state["status"] = status
    state["updatedAtUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    temporary = state_file.with_suffix(".tmp")
    temporary.write_text(json.dumps(state, indent=2), encoding="utf-8")
    os.replace(temporary, state_file)


save("waiting")
print(json.dumps(state), flush=True)
deadline = time.monotonic() + args.wait_seconds
try:
    while not grant_file.exists():
        if time.monotonic() >= deadline:
            state["outcome"] = "No grant arrived; no build or test was started"
            sys.exit(0)
        time.sleep(0.5)
    grant = json.loads(grant_file.read_text(encoding="utf-8-sig"))
    if grant.get("nonce") != nonce:
        raise RuntimeError("Serial window grant does not match this runner")
    check = subprocess.run(
        ["powershell.exe", "-NoProfile", "-Command",
         "$ErrorActionPreference='Stop'; $p = @(Get-CimInstance Win32_Process | Where-Object { ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -notmatch 'nodemode:1|VBCSCompiler') -or $_.Name -match '^Iris\\..*Tests\\.exe$' }); if ($p.Count -ne 0) { exit 5 }; exit 0"],
        capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
    if check.returncode:
        state["outcome"] = "Active or unverified build/test processes; no review command started"
        sys.exit(5)
    environment = dict(os.environ)
    temporary = work / f"temp-window-{args.window}"
    temporary.mkdir(exist_ok=False)
    environment["TEMP"] = str(temporary)
    environment["TMP"] = str(temporary)
    environment["MSBUILDDISABLENODEREUSE"] = "1"
    save("running")
    executable = work / "bin/Release/net10.0-windows10.0.19041.0/Iris.App.Tests.exe"
    commands = [
        (f"independent-update-build-{args.window}", ["dotnet", "build", "Review.csproj", "-c", "Release",
                                        "--configfile", "NuGet.Config", "--disable-build-servers",
                                        "-p:UseSharedCompilation=false", "-m:1"]),
        (f"independent-update-late-cancel-{args.window}", [str(executable)]),
        (f"independent-update-process-lock-{args.window}", [str(executable), "--cross-process-lock"]),
    ]
    for label, command in commands:
        call = [sys.executable, str(review.parent / "run_review.py"), "--label", label,
                "--cwd", str(work), "--", *command]
        result = subprocess.run(call, cwd=repo, env=environment, creationflags=subprocess.CREATE_NO_WINDOW)
        state["results"].append({"label": label, "exitCode": result.returncode})
        save("running")
        if label.startswith("independent-update-build-") and result.returncode:
            break
    state["outcome"] = "Commands finished; inspect original logs before drawing product conclusions"
finally:
    save("released")
    print(json.dumps(state), flush=True)
