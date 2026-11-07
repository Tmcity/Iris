$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskOut = Join-Path 'C:\IrisT040Output' ('cpu-worker-recovery-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
$null = New-Item -ItemType Directory -Path $taskOut
$taskIdentity = Get-CimInstance Win32_Process -Filter 'ProcessId=2432'
if (-not $taskIdentity -or $taskIdentity.CommandLine -notlike '*C:\IrisT040R2rRepeat\t040_compare_r2r_with_cpu.ps1*') { throw 'Exact owned diagnostic worker required' }
$taskWorker = [Diagnostics.Process]::GetProcessById(2432)
$null = $taskWorker.Handle
if ([Math]::Abs(($taskWorker.StartTime.ToUniversalTime() - [DateTime]::Parse('2026-11-07T19:11:31.258Z').ToUniversalTime()).TotalMilliseconds) -gt 1) { throw 'Worker identity changed' }
$taskRecord = @{
    StartedUtc = [DateTime]::UtcNow.ToString('o'); WorkerPid = 2432
    WorkerCreatedUtc = $taskWorker.StartTime.ToUniversalTime().ToString('o')
    WorkerWorkingSetBytes = $taskWorker.WorkingSet64; WorkerCpuSeconds = $taskWorker.TotalProcessorTime.TotalSeconds
    Reason = 'Self-owned diagnostic worker exceeded its eight-second startup deadline and allocated over one GiB; original state and read-only stacks retained'
    ProductKilled = $false; DiagnosticHelperTerminated = $false; ProductOperationPerformed = $true
}
if ($taskWorker.WorkingSet64 -lt 1GB) { throw 'Runaway worker condition not confirmed' }
Copy-Item -LiteralPath 'C:\IrisT040Output\r2r-cpu-20261107T1911310861124Z\state.json','C:\IrisT040Output\r2r-cpu-20261107T1911310861124Z\launch.json' -Destination $taskOut
Stop-Process -Id 2432 -ErrorAction Stop
if (-not $taskWorker.WaitForExit(10000)) { throw 'Exact worker is still present' }
$taskRecord.DiagnosticHelperTerminated = $true; $taskRecord.WorkerExitCode = $taskWorker.ExitCode
$taskWorker.Dispose()
$taskAppIdentity = Get-CimInstance Win32_Process -Filter 'ProcessId=2848'
$taskExe = Join-Path $env:LOCALAPPDATA 'IrisT040Diagnostic\ab153c47ac4547d6ae4f0181fed9f841\Iris.App.exe'
if (-not $taskAppIdentity -or $taskAppIdentity.ExecutablePath -ne $taskExe) { throw 'Exact retained diagnostic primary required' }
$taskApp = [Diagnostics.Process]::GetProcessById(2848); $null = $taskApp.Handle
$taskCli = Start-Process -FilePath $taskExe -ArgumentList @('--action','Exit') -WindowStyle Hidden -PassThru
$null = $taskCli.Handle
if (-not $taskCli.WaitForExit(15000) -or $taskCli.ExitCode -ne 0) { throw 'Normal diagnostic Exit CLI failed; retain primary' }
$taskCli.Dispose()
if (-not $taskApp.WaitForExit(15000) -or $taskApp.ExitCode -ne 0) { throw 'Diagnostic primary did not exit normally' }
$taskRecord.DiagnosticPrimaryPid = 2848; $taskRecord.DiagnosticPrimaryExitCode = $taskApp.ExitCode
$taskApp.Dispose()
if (@(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'").Count -ne 0) { throw 'An Iris process remains; no replacement started' }
$taskInstalled = Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
if ((Get-FileHash -LiteralPath $taskInstalled -Algorithm SHA256).Hash.ToLowerInvariant() -ne '8ae2e8ac75db29a06eedd5300a99c74d801a29ee307c2f64c924cf5e246d6eb7') { throw 'Installed binary changed' }
$taskRestored = Start-Process -FilePath $taskInstalled -WindowStyle Hidden -PassThru
$taskRecord.RestoredPid = $taskRestored.Id; $taskRecord.RestoredCreatedUtc = $taskRestored.StartTime.ToUniversalTime().ToString('o')
$taskRestored.Dispose()
$taskRecord.FinishedUtc = [DateTime]::UtcNow.ToString('o')
[IO.File]::WriteAllText((Join-Path $taskOut 'recovery.json'), ($taskRecord | ConvertTo-Json -Depth 7), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskOut
