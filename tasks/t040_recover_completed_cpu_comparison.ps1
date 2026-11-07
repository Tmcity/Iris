$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskFolder = Get-ChildItem -LiteralPath 'C:\IrisT040Output' -Directory -Filter 'r2r-cpu-*' | Sort-Object Name | Select-Object -Last 1
$taskState = Get-Content -LiteralPath (Join-Path $taskFolder.FullName 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $taskState.Completed -or -not $taskState.Failure -or $taskState.RestoredPid) { throw 'Only a completed failed comparison without restoration is eligible' }
if (Get-CimInstance Win32_Process -Filter ('ProcessId=' + [int]$taskState.WorkerPid)) { throw 'The original worker remains alive' }
$taskRun = $taskState.Runs[-1]
$taskApps = @(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'")
if ($taskApps.Count -ne 1 -or $taskApps[0].ProcessId -ne $taskRun.Pid -or $taskApps[0].ExecutablePath -ne $taskRun.ExecutablePath) { throw 'Exact retained diagnostic primary required' }
$taskProcess = [Diagnostics.Process]::GetProcessById([int]$taskRun.Pid); $null = $taskProcess.Handle
if ([Math]::Abs(($taskProcess.StartTime.ToUniversalTime() - [DateTime]::Parse($taskRun.CreatedUtc).ToUniversalTime()).TotalMilliseconds) -gt 1) { throw 'Diagnostic identity changed' }
$taskCli = Start-Process -FilePath $taskRun.ExecutablePath -ArgumentList @('--action','Exit') -WindowStyle Hidden -PassThru
$null = $taskCli.Handle
if (-not $taskCli.WaitForExit(15000) -or $taskCli.ExitCode -ne 0) { throw 'Normal diagnostic Exit CLI failed' }
$taskCli.Dispose()
if (-not $taskProcess.WaitForExit(15000) -or $taskProcess.ExitCode -ne 0) { throw 'Diagnostic primary did not exit normally' }
$taskExit = $taskProcess.ExitCode; $taskProcess.Dispose()
if (@(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'").Count) { throw 'An Iris process remains; no replacement started' }
$taskInstalled = Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
if ((Get-FileHash -LiteralPath $taskInstalled -Algorithm SHA256).Hash.ToLowerInvariant() -ne '8ae2e8ac75db29a06eedd5300a99c74d801a29ee307c2f64c924cf5e246d6eb7') { throw 'Installed binary changed' }
$taskRestored = Start-Process -FilePath $taskInstalled -WindowStyle Hidden -PassThru
$taskRecord = @{
    Utc = [DateTime]::UtcNow.ToString('o'); FailedWorkerAbsent = $true; FailedWorkerPid = $taskState.WorkerPid
    DiagnosticPid = $taskRun.Pid; DiagnosticExitCode = $taskExit
    RestoredPid = $taskRestored.Id; RestoredCreatedUtc = $taskRestored.StartTime.ToUniversalTime().ToString('o')
    ProductOperationPerformed = $true; InstallerExecuted = $false; ProductKilled = $false
}
$taskRestored.Dispose()
[IO.File]::WriteAllText((Join-Path $taskFolder.FullName 'recovery.json'), ($taskRecord | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskFolder.FullName
