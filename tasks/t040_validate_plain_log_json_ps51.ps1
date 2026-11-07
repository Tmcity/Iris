$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskOut = Join-Path 'C:\IrisT040Output' ('plain-log-json-validation-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
$null = New-Item -ItemType Directory -Path $taskOut
$taskResults = @()
foreach ($taskMode in @('annotated', 'plain')) {
    $taskCase = Join-Path $taskOut $taskMode; $null = New-Item -ItemType Directory -Path $taskCase
    $taskProcess = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 't040_json_line_repro.ps1'),'-OutputDirectory',$taskCase,'-Mode',$taskMode) -WindowStyle Hidden -RedirectStandardOutput (Join-Path $taskCase 'stdout.txt') -RedirectStandardError (Join-Path $taskCase 'stderr.txt') -PassThru
    $null = $taskProcess.Handle
    $taskId = $taskProcess.Id; $taskCreated = $taskProcess.StartTime.ToUniversalTime().ToString('o')
    $taskTerminated = $false
    if (-not $taskProcess.WaitForExit(4000)) {
        # Only this owned short-lived reproduction process is terminated by its watchdog.
        Stop-Process -InputObject $taskProcess -ErrorAction Stop
        if (-not $taskProcess.WaitForExit(5000)) { throw 'Exact owned reproduction remains alive' }
        $taskTerminated = $true
    }
    $taskState = Get-Content -LiteralPath (Join-Path $taskCase 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $taskResults += @{ Mode = $taskMode; Pid = $taskId; CreatedUtc = $taskCreated; ExitCode = $taskProcess.ExitCode; WatchdogTerminated = $taskTerminated; State = $taskState }
    $taskProcess.Dispose()
}
$taskResult = @{ Utc = [DateTime]::UtcNow.ToString('o'); Cases = $taskResults; ProductOperationPerformed = $false; ProductGcRequested = $false }
[IO.File]::WriteAllText((Join-Path $taskOut 'validation.json'), ($taskResult | ConvertTo-Json -Depth 7), (New-Object Text.UTF8Encoding($false)))
if (-not $taskResults[0].WatchdogTerminated -or $taskResults[0].State.SerializationCompleted) { throw 'Original serializer stall was not reproduced' }
if ($taskResults[1].ExitCode -ne 0 -or -not $taskResults[1].State.SerializationCompleted -or $taskResults[1].WatchdogTerminated -or $taskResults[1].State.JsonLength -gt 1024) { throw 'Plain string serialization did not pass the bounded regression' }
Write-Output $taskOut
