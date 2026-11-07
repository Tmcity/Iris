$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned T-040 Sandbox only' }
$taskExisting = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'RuntimeCounterProbe.exe' -or ($_.Name -eq 'powershell.exe' -and $_.CommandLine -like '*t040_audit_counter_influence_readonly.ps1*') })
if ($taskExisting.Count -gt 0) { throw 'An actual counter observation is already running; inspect it before another start' }
$taskOut = Join-Path 'C:\IrisT040Output' ('counter-influence-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
$null = New-Item -ItemType Directory -Path $taskOut
$taskSource = Join-Path $PSScriptRoot 't040_audit_counter_influence_readonly.ps1'
$taskChild = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$taskSource,'-OutputDirectory',$taskOut) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskOut 'stdout.txt') -RedirectStandardError (Join-Path $taskOut 'stderr.txt')
$taskLaunch = @{ Utc=[DateTime]::UtcNow.ToString('o'); ObserverPid=$taskChild.Id; ObserverCreatedUtc=$taskChild.StartTime.ToUniversalTime().ToString('o'); OutputDirectory=$taskOut; TargetPid=5568; SourceSha256=(Get-FileHash -LiteralPath $taskSource -Algorithm SHA256).Hash; ProductOperationPerformed=$false; ForceGcRequested=$false }
[IO.File]::WriteAllText((Join-Path $taskOut 'launch.json'),($taskLaunch | ConvertTo-Json -Depth 7),(New-Object Text.UTF8Encoding($false)))
Write-Output $taskOut
