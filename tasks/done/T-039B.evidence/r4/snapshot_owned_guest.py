"""Observe the existing owned guest without starting/retrying any installer."""
from pathlib import Path
import base64
import json
import subprocess
import sys
import time

evidence = Path(__file__).resolve().parent
label, observation = sys.argv[1:3]
wait_for_exit = "--wait-for-exit" in sys.argv[3:]
if any(Path(value).name != value for value in (label, observation)):
    raise ValueError("Owned labels required")
directory = evidence / label
intent = json.loads((directory / "intent.json").read_text(encoding="utf-8"))
cli = "C:/Users/28910/AppData/Local/Microsoft/WindowsApps/wsb.exe"
active = json.loads(subprocess.check_output([cli, "list", "--raw"], creationflags=subprocess.CREATE_NO_WINDOW))
assert intent["SandboxId"] in json.dumps(active)
filename = observation + ".json"
assert not (directory / filename).exists()
target = Path(intent["WritableOutput"]) / filename
assert not target.exists()
script = """
$ErrorActionPreference='Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned guest only.' }
$processes=@(Get-CimInstance Win32_Process | Where-Object {$_.Name -match 'Iris|Update|Setup|dotnet|msiexec|WindowsDesktop'} | Select-Object ProcessId,ParentProcessId,CreationDate,ExecutablePath,CommandLine)
[ordered]@{ObservedUtc=[DateTime]::UtcNow.ToString('o');Processes=$processes;InstallerStartedOrRetriedByObservation=$false} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath 'C:\\IrisBOutput\\FILENAME' -Encoding UTF8
""".replace("FILENAME", filename)
if wait_for_exit:
    script = r"""
$ErrorActionPreference='Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned guest only.' }
$report=[ordered]@{ObservedUtc=[DateTime]::UtcNow.ToString('o');CapturedHandles=@();Terminal=@();Failure=$null;InstallerStartedOrRetriedByObservation=$false}
$handles=@()
try {
    $all=@(Get-CimInstance Win32_Process)
    $owners=@($all | Where-Object {$_.ExecutablePath -eq 'C:\IrisBInput\Iris-0.2.0-Setup.exe'})
    if ($owners.Count -ne 1) { throw 'Expected one existing owned installer.' }
    do {
        $added=@($all | Where-Object {$_.ParentProcessId -in @($owners | ForEach-Object {$_.ProcessId}) -and $_.ProcessId -notin @($owners | ForEach-Object {$_.ProcessId})})
        $owners+=$added
    } while ($added.Count -gt 0)
    foreach ($owner in $owners) {
        $process=[Diagnostics.Process]::GetProcessById($owner.ProcessId)
        $null=$process.Handle
        if ([Math]::Abs(($process.StartTime.ToUniversalTime()-$owner.CreationDate.ToUniversalTime()).TotalMilliseconds) -gt 1) { throw 'Process identity changed before capture.' }
        $identity=[ordered]@{Pid=$process.Id;ParentPid=$owner.ParentProcessId;StartedUtc=$process.StartTime.ToUniversalTime().ToString('o');Path=$owner.ExecutablePath}
        $handles+=@{Process=$process;Identity=$identity}
        $report.CapturedHandles+=$identity
    }
    foreach ($handle in $handles) {
        $ended=$handle.Process.WaitForExit(900000)
        if (-not $ended) { throw 'Owned captured handle still active; no retry or termination performed.' }
        $handle.Process.WaitForExit()
        $report.Terminal+=@{Identity=$handle.Identity;HasExited=$handle.Process.HasExited;ExitCode=$handle.Process.ExitCode}
    }
} catch { $report.Failure=$_.ToString() }
finally {
    foreach ($handle in $handles) { $handle.Process.Dispose() }
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath 'C:\IrisBOutput\FILENAME' -Encoding UTF8
}
if ($report.Failure) { exit 2 }; exit 0
""".replace("FILENAME", filename)
encoded = base64.b64encode(script.encode("utf-16-le")).decode()
command = [cli, "exec", "--id", intent["SandboxId"], "--command",
    "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe -NoProfile -NonInteractive -EncodedCommand " + encoded,
    "--run-as", "ExistingLogin", "--raw"]
completed = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
(directory / (observation + ".command.json")).write_text(json.dumps({"Command": command, "ExitCode": completed.returncode,
    "RawOutput": completed.stdout.decode("utf-8-sig")}, indent=2) + "\n", encoding="utf-8")
assert completed.returncode == 0
for _ in range(100):
    if target.exists():
        break
    time.sleep(0.1)
result = json.loads(target.read_text(encoding="utf-8-sig"))
(directory / filename).write_bytes(target.read_bytes())
print(json.dumps(result, ensure_ascii=False, indent=2))
