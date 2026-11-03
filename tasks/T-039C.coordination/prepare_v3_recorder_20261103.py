"""Preserve the failed observer and prepare an independently probed read-only successor."""
from pathlib import Path
import base64
import gzip
import hashlib
import json
import shutil

ROOT = Path("F:/Iris")
INPUT = ROOT / "artifacts/t039c/manual-verification-v022-20261101-1/input"
SESSION = ROOT / "artifacts/t039c/manual-verification-v022-20261101-1/output/session-1c8333f923744572ac2c71bab9576af5"
REPORT = ROOT / "tasks/done/T-039C.evidence/recorder-v2-failure-20261103-1"
SOURCE = ROOT / "tasks/T-039C.coordination/manual-process-watch-v3.ps1"
STARTER = ROOT / "tasks/T-039C.coordination/start-process-watch-v3.ps1"
original_path = INPUT / "manual-process-watch-v2.ps1"
raw = original_path.read_bytes()
assert hashlib.sha256(raw).hexdigest() == "322bc2f93560f345c5371a6506d9beead0bc170519eaad938ad4b254e7d6ba2d"
pointer = json.loads((SESSION / "active-recorder-v2.json").read_text(encoding="utf-8-sig"))
old_output = SESSION / Path(pointer["OutputDirectory"].replace("\\", "/")).name
failure = json.loads((old_output / "watcher-v2-status.json").read_text(encoding="utf-8-sig"))
assert failure["Ready"] is False and failure["RecorderPid"] == 6956
assert failure["Failure"]["Type"] == "System.ArgumentException"
assert "does not exist" in failure["Failure"]["Message"]
REPORT.mkdir(exist_ok=False)
shutil.copyfile(original_path, REPORT / original_path.name)
shutil.copyfile(SESSION / "active-recorder-v2.json", REPORT / "active-recorder-v2.json")
for name in ("watcher-v2-status.json", "process-events-v2.jsonl"):
    shutil.copyfile(old_output / name, REPORT / name)
old_ready = sorted(SESSION.glob("old-ready-*.json"))[-1]
shutil.copyfile(old_ready, REPORT / "old-ready-at-observer-failure.json")
for suffix in (".command.json", ".log"):
    shutil.copyfile(ROOT / "tasks/T-039C.coordination" / ("v026-old-ready-before-approval-1" + suffix),
                    REPORT / ("v026-old-ready-before-approval-1" + suffix))
records = [{"Path": path.name, "Bytes": path.stat().st_size,
            "Sha256": hashlib.sha256(path.read_bytes()).hexdigest()} for path in sorted(REPORT.iterdir()) if path.is_file()]
(REPORT / "binding.json").write_text(json.dumps({"OriginalFailure": failure, "SourceSha256": hashlib.sha256(raw).hexdigest(),
    "Files": records, "ProductChanged": False}, indent=2) + "\n", encoding="utf-8", newline="\n")
source = raw.decode("utf-8-sig").replace("\r\n", "\n")
source = source.replace('param([Parameter(Mandatory = $true)][string]$OutputDirectory)',
    'param([Parameter(Mandatory = $true)][string]$OutputDirectory, [switch]$ExerciseQueueRemovalRace)')
source = source.replace("$null = New-Item -ItemType Directory -Path $taskOut", """if (-not (Test-Path -LiteralPath $taskOut)) { $null = New-Item -ItemType Directory -Path $taskOut }
if (Test-Path -LiteralPath (Join-Path $taskOut 'process-events-v2.jsonl')) { throw 'Recorder output has already been used' }
$taskProbePath = Join-Path $taskOut 'probe\\Iris.exe'
$taskSeenEvents = New-Object 'Collections.Generic.HashSet[int]'
$taskMissingRemovals = 0
$taskLastHeartbeat = [DateTime]::MinValue""")
source = source.replace("$taskRecord.Sequence = $script:taskCount", "$taskRecord.RecorderVersion = 3\n    $taskRecord.Sequence = $script:taskCount")
source = source.replace("$taskPath.StartsWith($taskMaintenance + '\\', [StringComparison]::OrdinalIgnoreCase)))",
    "$taskPath.StartsWith($taskMaintenance + '\\', [StringComparison]::OrdinalIgnoreCase) -and -not $taskPath.Equals($taskProbePath, [StringComparison]::OrdinalIgnoreCase)))")
source = source.replace("$taskRole = if ($taskProcess.Name -eq 'Iris.App.exe'", "$taskRole = if ($taskPath.Equals($taskProbePath, [StringComparison]::OrdinalIgnoreCase)) { 'AuxiliaryProbe' } elseif ($taskProcess.Name -eq 'Iris.App.exe'")
source = source.replace("$taskStatus = @{ Ready =", "$script:taskLastHeartbeat = [DateTime]::UtcNow\n    $taskStatus = @{ RecorderVersion = 3; QueueRemovalAlreadyAbsent = $script:taskMissingRemovals; Ready =")
source = source.replace("[IO.File]::WriteAllText((Join-Path $taskOut 'watcher-v2-status.json'), ($taskStatus | ConvertTo-Json -Depth 6), $taskUtf8)", """$taskStatusPath = Join-Path $taskOut 'watcher-v2-status.json'
    $taskTemporaryStatus = Join-Path $taskOut 'watcher-status.pending'
    [IO.File]::WriteAllText($taskTemporaryStatus, ($taskStatus | ConvertTo-Json -Depth 6), $taskUtf8)
    Move-Item -LiteralPath $taskTemporaryStatus -Destination $taskStatusPath -Force""")
cleanup = """function CompleteEventRemoval($taskEventItem) {
    if ($null -eq $taskEventItem -or [int]$taskEventItem.EventIdentifier -le 0) { throw 'Invalid event identifier' }
    try { Remove-Event -EventIdentifier $taskEventItem.EventIdentifier -ErrorAction Stop }
    catch [System.ArgumentException] {
        if (@(Get-Event -EventIdentifier $taskEventItem.EventIdentifier -ErrorAction SilentlyContinue).Count -ne 0) { throw }
        $script:taskMissingRemovals++
        WriteRecord (@{ Kind = 'QueueRemovalAlreadyAbsent'; EventIdentifier = [int]$taskEventItem.EventIdentifier; ErrorType = $_.Exception.GetType().FullName; NativeLifecycleEvent = $false })
    }
}
"""
source = source.replace("try {\n    $null = Register-WmiEvent", cleanup + "try {\n    $null = Register-WmiEvent", 1)
probe = """    if ($ExerciseQueueRemovalRace) {
        $taskRemovalProbe = New-Event -SourceIdentifier ($taskPrefix + '-removal-probe')
        Remove-Event -EventIdentifier $taskRemovalProbe.EventIdentifier -ErrorAction Stop
        CompleteEventRemoval $taskRemovalProbe
        WriteRecord (@{ Kind = 'QueueRemovalProbeCompleted'; NativeLifecycleEvent = $false; AlreadyAbsentHandled = ($taskMissingRemovals -eq 1) })
    }
"""
source = source.replace("    WriteStatus $true $null\n    while", probe + "    WriteStatus $true $null\n    while", 1)
source = source.replace("        $null = Wait-Event -Timeout 1", "        Start-Sleep -Milliseconds 200")
source = source.replace("            $taskNative = $taskEventItem.SourceEventArgs.NewEvent", """            if (-not $taskSeenEvents.Add([int]$taskEventItem.EventIdentifier)) { CompleteEventRemoval $taskEventItem; continue }
            $taskNative = $taskEventItem.SourceEventArgs.NewEvent""")
source = source.replace("            Remove-Event -EventIdentifier $taskEventItem.EventIdentifier", "            CompleteEventRemoval $taskEventItem")
source = source.replace("        WriteStatus $true $null\n    }", "        if (([DateTime]::UtcNow - $taskLastHeartbeat).TotalSeconds -ge 1) { WriteStatus $true $null }\n    }")
assert "Wait-Event" not in source and "CompleteEventRemoval" in source and "AuxiliaryProbe" in source
assert "RecorderVersion = 3" in source
SOURCE.open("x", encoding="utf-8", newline="\n").write(source)
shutil.copyfile(SOURCE, INPUT / SOURCE.name)
digest = hashlib.sha256(SOURCE.read_bytes()).hexdigest()
encoded_source = base64.b64encode(SOURCE.read_bytes()).decode("ascii")
starter = r'''$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned acceptance Sandbox only' }
$taskState = Get-Content -LiteralPath 'C:\IrisAcceptanceOutput\active-session.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskSession = [IO.Path]::GetFullPath($taskState.SessionDirectory)
if (-not $taskSession.StartsWith('C:\IrisAcceptanceOutput\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected session' }
if (Test-Path -LiteralPath (Join-Path $taskSession 'stop.request')) { throw 'Acceptance session has already been stopped' }
$taskOut = Join-Path $taskSession ('recorder-v3-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $taskOut
$taskSource = Join-Path $taskOut 'manual-process-watch-v3.ps1'
[IO.File]::WriteAllBytes($taskSource, [Convert]::FromBase64String('__SOURCE__'))
if ((Get-FileHash -LiteralPath $taskSource -Algorithm SHA256).Hash.ToLowerInvariant() -ne '__DIGEST__') { throw 'Observer source bytes differ' }
$taskResult = @{ RecorderVersion = 3; OutputDirectory = $taskOut; SourcePath = $taskSource; SourceSha256 = '__DIGEST__'; ProductOperationPerformed = $false; Failure = $null }
$taskRecorder = $null
$taskProbe = $null
$taskRelease = $null
$taskUtf8 = New-Object Text.UTF8Encoding($false)
function ReadProbeEvents {
    try { return @(Get-Content -LiteralPath (Join-Path $taskOut 'process-events-v2.jsonl') -Encoding UTF8 | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json }) }
    catch { return @() }
}
try {
    $taskOldPointer = Join-Path $taskSession 'active-recorder-v2.json'
    if (Test-Path -LiteralPath $taskOldPointer) { Copy-Item -LiteralPath $taskOldPointer -Destination (Join-Path $taskOut 'previous-recorder-pointer.json') }
    $taskRecorder = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',('"' + $taskSource + '"'),'-OutputDirectory',('"' + $taskOut + '"'),'-ExerciseQueueRemovalRace') -WindowStyle Hidden -PassThru
    $taskResult.RecorderPid = $taskRecorder.Id
    $taskLimit = [DateTime]::UtcNow.AddSeconds(15)
    $taskStatus = $null
    do {
        Start-Sleep -Milliseconds 200
        try { $taskStatus = Get-Content -LiteralPath (Join-Path $taskOut 'watcher-v2-status.json') -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $taskStatus = $null }
        if ($taskRecorder.HasExited) { throw 'Observer exited before becoming ready' }
    } while ((-not $taskStatus -or -not $taskStatus.Ready) -and [DateTime]::UtcNow -lt $taskLimit)
    if (-not $taskStatus -or -not $taskStatus.Ready -or $taskStatus.RecorderVersion -ne 3 -or $taskStatus.QueueRemovalAlreadyAbsent -lt 1) { throw 'Queue removal probe did not complete' }
    $taskProbeRoot = Join-Path $taskOut 'probe'
    $null = New-Item -ItemType Directory -Path $taskProbeRoot
    $taskProbeExecutable = Join-Path $taskProbeRoot 'Iris.exe'
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\cmd.exe') -Destination $taskProbeExecutable
    $taskBatch = Join-Path $taskProbeRoot 'probe.cmd'
    $taskRelease = Join-Path $taskProbeRoot 'release.request'
    [IO.File]::WriteAllText($taskBatch, "@echo off`r`n:wait_for_release`r`nif exist `"%~dp0release.request`" exit /b 0`r`nping -n 2 127.0.0.1 >nul`r`ngoto wait_for_release`r`n", [Text.Encoding]::ASCII)
    $taskProbe = Start-Process -FilePath $taskProbeExecutable -ArgumentList @('/d','/c',('"' + $taskBatch + '"')) -WindowStyle Hidden -PassThru
    $taskResult.AuxiliaryProbe = @{ Pid = $taskProbe.Id; Path = $taskProbeExecutable; SystemCmdCopySha256 = (Get-FileHash -LiteralPath $taskProbeExecutable -Algorithm SHA256).Hash.ToLowerInvariant(); RealIrisExecutable = $false }
    $taskLimit = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $taskStarted = @(ReadProbeEvents | Where-Object { $_.Kind -eq 'Started' -and $_.Pid -eq $taskProbe.Id -and $_.Identity.Role -eq 'AuxiliaryProbe' })
    } while ($taskStarted.Count -eq 0 -and [DateTime]::UtcNow -lt $taskLimit)
    if ($taskStarted.Count -ne 1) { throw 'Owned native probe start was not recorded exactly once' }
    [IO.File]::WriteAllText($taskRelease, 'release', [Text.Encoding]::ASCII)
    if (-not $taskProbe.WaitForExit(15000)) { throw 'Owned auxiliary probe did not exit after release' }
    $taskLimit = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $taskExited = @(ReadProbeEvents | Where-Object { $_.Kind -eq 'Exited' -and $_.Pid -eq $taskProbe.Id -and $_.Identity.Role -eq 'AuxiliaryProbe' })
    } while ($taskExited.Count -eq 0 -and [DateTime]::UtcNow -lt $taskLimit)
    if ($taskExited.Count -ne 1 -or $taskExited[0].ExitCode -ne 0 -or $taskProbe.ExitCode -ne 0) { throw 'Owned native probe exit was not recorded exactly once with exit 0' }
    $taskResult.AuxiliaryProbe.StartEvent = $taskStarted[0]
    $taskResult.AuxiliaryProbe.ExitEvent = $taskExited[0]
    $taskResult.Ready = $true
    $taskResult.ReadOnlyProductObserver = $true
    $taskResult.Utc = [DateTime]::UtcNow.ToString('o')
    [IO.File]::WriteAllText((Join-Path $taskSession 'active-recorder-v2.json'), ($taskResult | ConvertTo-Json -Depth 8), $taskUtf8)
} catch {
    $taskResult.Ready = $false
    $taskResult.Failure = @{ Type = $_.Exception.GetType().FullName; Message = $_.Exception.Message; Position = $_.InvocationInfo.PositionMessage }
    if ($taskRecorder -and -not $taskRecorder.HasExited) { [IO.File]::WriteAllText((Join-Path $taskOut 'stop.request'), 'startup failed', [Text.Encoding]::ASCII); $null = $taskRecorder.WaitForExit(15000) }
    throw
} finally {
    if ($taskRelease -and -not (Test-Path -LiteralPath $taskRelease)) { [IO.File]::WriteAllText($taskRelease, 'cleanup', [Text.Encoding]::ASCII) }
    if ($taskProbe) { $null = $taskProbe.WaitForExit(15000); $taskProbe.Dispose() }
    [IO.File]::WriteAllText((Join-Path $taskOut 'startup-result.json'), ($taskResult | ConvertTo-Json -Depth 9), $taskUtf8)
    if ($taskRecorder) { $taskRecorder.Dispose() }
}
'''.replace('__SOURCE__', encoded_source).replace('__DIGEST__', digest)
STARTER.open("x", encoding="utf-8", newline="\n").write(starter)
shutil.copyfile(STARTER, INPUT / STARTER.name)
compressed = base64.b64encode(gzip.compress(STARTER.read_bytes(), mtime=0)).decode("ascii")
stub = "$taskCompressed=[Convert]::FromBase64String('" + compressed + "')\n" + r'''$taskBuffer=[IO.MemoryStream]::new([byte[]]$taskCompressed)
$taskZip=[IO.Compression.GZipStream]::new($taskBuffer,[IO.Compression.CompressionMode]::Decompress)
$taskReader=[IO.StreamReader]::new($taskZip,[Text.Encoding]::UTF8)
try { & ([ScriptBlock]::Create($taskReader.ReadToEnd())) } finally { $taskReader.Dispose(); $taskZip.Dispose(); $taskBuffer.Dispose() }
'''
encoded = base64.b64encode(stub.encode("utf-16le")).decode("ascii")
assert len(encoded) < 25000
launch = {"ObserverSourceSha256": digest, "StarterSha256": hashlib.sha256(STARTER.read_bytes()).hexdigest(),
          "Command": "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
          "CommandContainsOnlyOwnedObserverCode": True, "ProductInstallationOrUpdatePerformed": False}
(ROOT / "tasks/T-039C.coordination/v3-recorder-launch-20261103-1.json").open("x", encoding="utf-8").write(json.dumps(launch, indent=2) + "\n")
print(json.dumps({"V2OriginalFailurePreserved": True, "V3SourceSha256": digest, "EncodedCommandCharacters": len(encoded),
                  "AuxiliaryNativeProbe": "Owned copy of system cmd.exe; real Iris is not executed"}, indent=2))
