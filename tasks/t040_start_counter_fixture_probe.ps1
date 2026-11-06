$ErrorActionPreference='Stop'
if([Environment]::UserName -ne 'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskTarget=Join-Path $PSScriptRoot 'tools\synthetic-provider\T040SyntheticProvider.exe'
$taskFixture=@(Get-CimInstance Win32_Process -Filter "Name='T040SyntheticProvider.exe'" | Where-Object {$_.ExecutablePath -eq $taskTarget})
if($taskFixture.Count-ne1){throw 'Expected exactly one owned synthetic fixture process'}
$taskExe=Join-Path $PSScriptRoot 'tools\runtime-counters\RuntimeCounterProbe.exe'
$taskStamp=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
$taskProcess=Start-Process -FilePath $taskExe -ArgumentList @([string]$taskFixture[0].ProcessId,'7','--fixture') -WindowStyle Hidden -PassThru -RedirectStandardOutput ('C:\IrisT040Output\counter-fixture-stdout-'+$taskStamp+'.txt') -RedirectStandardError ('C:\IrisT040Output\counter-fixture-stderr-'+$taskStamp+'.txt')
$taskState=@{Utc=[DateTime]::UtcNow.ToString('o');ObserverPid=$taskProcess.Id;TargetPid=$taskFixture[0].ProcessId;TargetRole='OwnedSyntheticProvider';ObservedInstalledIris=$false;ProductOperationPerformed=$false}
[IO.File]::WriteAllText(('C:\IrisT040Output\counter-fixture-launch-'+$taskStamp+'.json'),($taskState | ConvertTo-Json -Depth 5),(New-Object Text.UTF8Encoding($false)))
