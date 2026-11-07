$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskExe=Join-Path $PSScriptRoot 'tools\runtime-counters\RuntimeCounterProbe.exe'
$taskExisting=@(Get-CimInstance Win32_Process -Filter "Name='RuntimeCounterProbe.exe'" | Where-Object {$_.ExecutablePath-eq$taskExe})
if($taskExisting.Count-ne0){throw 'Owned runtime observer is already active; inspect the existing process'}
$taskExpected=Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskTarget=@(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object {$_.ExecutablePath-eq$taskExpected})
if($taskTarget.Count-ne1){throw 'Expected exactly one owned installed Iris process'}
$taskStamp=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
$taskProcess=Start-Process -FilePath $taskExe -ArgumentList @([string]$taskTarget[0].ProcessId,'600') -WindowStyle Hidden -PassThru -RedirectStandardOutput ('C:\IrisT040Output\runtime-observer-stdout-'+$taskStamp+'.txt') -RedirectStandardError ('C:\IrisT040Output\runtime-observer-stderr-'+$taskStamp+'.txt')
$taskState=@{Utc=[DateTime]::UtcNow.ToString('o');ObserverPid=$taskProcess.Id;TargetPid=$taskTarget[0].ProcessId;DurationSeconds=600;TargetRole='InstalledIris';ProductOperationPerformed=$false;GcRequested=$false;EventPipeBufferMiB=1;FormalMemorySamplesDuringTraceEligible=$false}
[IO.File]::WriteAllText(('C:\IrisT040Output\runtime-observer-launch-'+$taskStamp+'.json'),($taskState | ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
