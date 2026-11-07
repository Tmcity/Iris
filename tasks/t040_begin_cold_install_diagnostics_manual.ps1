$ErrorActionPreference='Stop'
if([Environment]::UserName -ne 'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskLog=Get-WinEvent -ListLog 'Microsoft-Windows-CAPI2/Operational'
if(-not $taskLog.IsEnabled){$taskLog.IsEnabled=$true;$taskLog.SaveChanges()}
& (Join-Path $PSScriptRoot 'start-metrics-observer-v2.ps1')
$taskStamp=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
$taskCapture=Join-Path $PSScriptRoot 't040_capture_live_capi2_readonly.ps1'
$taskProcess=Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$taskCapture,'-DurationSeconds','1200') -WindowStyle Hidden -PassThru -RedirectStandardOutput ('C:\IrisT040Output\cold-capi-stdout-'+$taskStamp+'.txt') -RedirectStandardError ('C:\IrisT040Output\cold-capi-stderr-'+$taskStamp+'.txt')
$taskRecord=@{Utc=[DateTime]::UtcNow.ToString('o');UserManuallyStartedDiagnostics=$true;CapiEnabled=$true;CapiObserverPid=$taskProcess.Id;CapiDurationSeconds=1200;SecurityPolicyChanged=$false;InstallerExecuted=$false;IrisStarted=$false}
[IO.File]::WriteAllText(('C:\IrisT040Output\cold-diagnostics-launch-'+$taskStamp+'.json'),($taskRecord | ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
Write-Host 'Diagnostics started. Now run the local Iris installer manually; keep this Sandbox open.'
