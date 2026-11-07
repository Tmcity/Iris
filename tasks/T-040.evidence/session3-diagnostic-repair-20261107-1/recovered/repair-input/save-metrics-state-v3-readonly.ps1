$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned T-040 Sandbox only' }
$taskPointer = Get-Content -LiteralPath 'C:\IrisT040Output\active-metrics.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskOut = [IO.Path]::GetFullPath($taskPointer.OutputDirectory)
if (-not $taskOut.StartsWith('C:\IrisT040Output\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected output path' }
$taskObserver = Get-CimInstance Win32_Process -Filter ('ProcessId=' + [int]$taskPointer.ObserverPid) -ErrorAction Stop
$taskAlive = $null -ne $taskObserver -and $taskObserver.Name -eq 'powershell.exe' -and $taskObserver.CommandLine -like '*observe-process-metrics-v3-readonly.ps1*'
if ($taskAlive -and $taskPointer.ObserverCreatedUtc) { $taskAlive = [Math]::Abs(($taskObserver.CreationDate.ToUniversalTime() - [DateTime]::Parse($taskPointer.ObserverCreatedUtc).ToUniversalTime()).TotalMilliseconds) -lt 1 }
$taskStatus = Get-Content -LiteralPath (Join-Path $taskOut 'status.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskAge = ([DateTime]::UtcNow - [DateTime]::Parse($taskStatus.LastHeartbeatUtc).ToUniversalTime()).TotalSeconds
$taskHealthy = $taskAlive -and $taskStatus.Ready -and -not $taskStatus.Completed -and -not $taskStatus.Failure -and $taskAge -lt 10
$taskSourcePath = Join-Path $env:LOCALAPPDATA 'Iris\current\release-source.json'
$taskSource = if (Test-Path -LiteralPath $taskSourcePath) { Get-Content -LiteralPath $taskSourcePath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$taskResult = @{ Utc=[DateTime]::UtcNow.ToString('o'); ObserverActualProcessAlive=$taskAlive; ObserverHealthy=$taskHealthy; HeartbeatAgeSeconds=$taskAge; ObserverStatus=$taskStatus; InstalledSource=$taskSource; ObserverPointer=$taskPointer; ProductOperationPerformed=$false; OriginalColdStartupCpuRecovered=$false }
$taskPath = Join-Path $taskOut ('saved-state-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
[IO.File]::WriteAllText($taskPath, ($taskResult | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskPath
if (-not $taskHealthy) { throw 'Metrics observer is not ready; keep diagnostics' }
