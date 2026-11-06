$ErrorActionPreference='Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned T-040 Sandbox only' }
$taskStamp=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
$taskPointer=Get-Content -LiteralPath 'C:\IrisT040Output\active-metrics.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskMetrics=[IO.Path]::GetFullPath($taskPointer.OutputDirectory)
if (-not $taskMetrics.StartsWith('C:\IrisT040Output\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected metrics location' }
$taskObserver=Get-CimInstance Win32_Process -Filter ('ProcessId='+[int]$taskPointer.ObserverPid)
if ($null -eq $taskObserver -or $taskObserver.CommandLine -notlike '*observe-process-metrics-v2-readonly.ps1*') { throw 'Expected v2 observer is not alive' }
$taskTarget=Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskProcesses=@(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object { $_.ExecutablePath -eq $taskTarget } | Select-Object ProcessId,ParentProcessId,CreationDate,ExecutablePath)
$taskResult=[ordered]@{ Utc=[DateTime]::UtcNow.ToString('o'); Checkpoint='clean-tray-idle-after-manual-restart'; ProductOperationPerformed=$false; Processes=$taskProcesses; ObserverPid=$taskObserver.ProcessId; ObserverSourceSha256=$taskPointer.SourceSha256 }
[IO.File]::WriteAllText((Join-Path 'C:\IrisT040Output' ('clean-idle-checkpoint-'+$taskStamp+'.json')),($taskResult | ConvertTo-Json -Depth 5),(New-Object Text.UTF8Encoding($false)))
& (Join-Path $PSScriptRoot 'save-metrics-state-v2-readonly.ps1')
& (Join-Path $PSScriptRoot 't040_collect_current_readonly.ps1')
Write-Host 'Saved clean idle checkpoint. No Iris action was performed by this tool.'
