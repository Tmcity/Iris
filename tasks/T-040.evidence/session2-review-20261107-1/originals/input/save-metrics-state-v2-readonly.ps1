$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw'Owned T-040 Sandbox only'}
$taskPointer=Get-Content -LiteralPath 'C:\IrisT040Output\active-metrics.json' -Raw -Encoding UTF8|ConvertFrom-Json
$taskOut=[IO.Path]::GetFullPath($taskPointer.OutputDirectory)
if(-not $taskOut.StartsWith('C:\IrisT040Output\',[StringComparison]::OrdinalIgnoreCase)){throw'Unexpected output path'}
$taskObserver=Get-CimInstance Win32_Process -Filter ('ProcessId='+[int]$taskPointer.ObserverPid) -ErrorAction Stop
$taskAlive=$null-ne$taskObserver-and$taskObserver.Name-eq'powershell.exe'-and$taskObserver.CommandLine-like'*observe-process-metrics*readonly.ps1*'
$taskStatus=Get-Content -LiteralPath (Join-Path $taskOut 'status.json') -Raw -Encoding UTF8|ConvertFrom-Json
$taskProduct=Join-Path $env:LOCALAPPDATA 'Iris'
$taskSourcePath=Join-Path $taskProduct 'current\release-source.json'
$taskSource=if(Test-Path -LiteralPath $taskSourcePath){Get-Content -LiteralPath $taskSourcePath -Raw -Encoding UTF8|ConvertFrom-Json}else{$null}
$taskResult=[ordered]@{Utc=[DateTime]::UtcNow.ToString('o');ObserverActualProcessAlive=$taskAlive;ObserverStatus=$taskStatus;InstalledSource=$taskSource;ProductOperationPerformed=$false}
$taskPath=Join-Path $taskOut ('saved-state-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')+'.json')
[IO.File]::WriteAllText($taskPath,($taskResult|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
Write-Output $taskPath
if(-not $taskAlive-or-not $taskStatus.Ready-or$taskStatus.Failure){throw'Metrics observer is not ready; keep diagnostics'}
