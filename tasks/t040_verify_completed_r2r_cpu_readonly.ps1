$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskFolder = Get-ChildItem -LiteralPath 'C:\IrisT040Output' -Directory -Filter 'r2r-cpu-*' | Sort-Object Name | Select-Object -Last 1
$taskState = Get-Content -LiteralPath (Join-Path $taskFolder.FullName 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $taskState.Completed -or $taskState.Failure -or $taskState.Runs.Count -ne 4 -or -not $taskState.InstalledSettingsAndSecretsUnchanged) { throw 'Completed four-run comparison required' }
if (Get-CimInstance Win32_Process -Filter ('ProcessId=' + [int]$taskState.WorkerPid)) { throw 'Comparison worker remains alive' }
foreach ($taskRun in $taskState.Runs) {
    if ($taskRun.ExitCode -ne 0 -or -not $taskRun.StartupCpuConditionProved) { throw 'A comparison run did not complete under the required CPU condition' }
    if (Get-CimInstance Win32_Process -Filter ('ProcessId=' + [int]$taskRun.Pid)) { throw 'A diagnostic process remains present or its PID was reused; audit identity first' }
}
$taskInstalled = Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskApps = @(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'")
if ($taskApps.Count -ne 1 -or $taskApps[0].ProcessId -ne $taskState.RestoredPid -or $taskApps[0].ExecutablePath -ne $taskInstalled) { throw 'Expected restored installed candidate required' }
$taskApp = [Diagnostics.Process]::GetProcessById([int]$taskState.RestoredPid)
try {
    $taskApp.Refresh()
    $taskResult = @{
        Utc = [DateTime]::UtcNow.ToString('o'); WorkerAbsent = $true; DiagnosticPrimariesAbsent = $true
        RestoredPid = $taskApp.Id; RestoredCreatedUtc = $taskApp.StartTime.ToUniversalTime().ToString('o')
        InstalledPath = $taskInstalled; InstalledExeSha256 = (Get-FileHash -LiteralPath $taskInstalled -Algorithm SHA256).Hash
        WorkingSetBytes = $taskApp.WorkingSet64; PrivateBytes = $taskApp.PrivateMemorySize64
        MainWindowHandle = $taskApp.MainWindowHandle.ToInt64(); ProductOperationPerformed = $false
        FormalNfrResult = $false; PostFullGcSteadyStateProved = $false
    }
} finally { $taskApp.Dispose() }
[IO.File]::WriteAllText((Join-Path $taskFolder.FullName 'independent-terminal-state.json'), ($taskResult | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskFolder.FullName
