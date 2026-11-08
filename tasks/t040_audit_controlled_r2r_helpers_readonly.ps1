$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskProcesses = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -in @('Iris.App.exe','T040SyntheticProvider.exe','RuntimeCounterProbe.exe') -or ($_.Name -eq 'powershell.exe' -and $_.CommandLine -like '*observe-process-metrics-v3*') } | Select-Object ProcessId, CreationDate, ExecutablePath, CommandLine, Name)
$taskListeners = @(Get-NetTCPConnection -LocalPort 18040 -State Listen -ErrorAction SilentlyContinue | Select-Object LocalAddress,LocalPort,OwningProcess)
$taskRecord = @{ Utc = [DateTime]::UtcNow.ToString('o'); Processes = $taskProcesses; FixtureListeners = $taskListeners; ProductOperationPerformed = $false }
$taskPath = Join-Path 'C:\IrisT040Output' ('controlled-r2r-helpers-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
[IO.File]::WriteAllText($taskPath, ($taskRecord | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
& (Join-Path $PSScriptRoot 't040_data_snapshot_readonly.ps1')
Write-Output $taskPath
