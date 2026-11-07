$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskExisting=@(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'"|Where-Object{$_.CommandLine-like'*t040_repeat_native_startup.ps1*'})
if($taskExisting.Count-gt0){throw 'An actual startup repeat worker is running; inspect it before another start'}
$taskOut=Join-Path 'C:\IrisT040Output' ('startup-repeat-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'));$null=New-Item -ItemType Directory -Path $taskOut
$taskScript=Join-Path $PSScriptRoot 't040_repeat_native_startup.ps1'
$taskChild=Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$taskScript,'-OutputDirectory',$taskOut) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskOut 'stdout.txt') -RedirectStandardError (Join-Path $taskOut 'stderr.txt')
$taskRecord=@{Utc=[DateTime]::UtcNow.ToString('o');ObserverPid=$taskChild.Id;ObserverCreatedUtc=$taskChild.StartTime.ToUniversalTime().ToString('o');SourceSha256=(Get-FileHash -LiteralPath $taskScript -Algorithm SHA256).Hash;ProductOperationPerformed=$true;NativeCliUsed=$true;InstallerExecuted=$false}
[IO.File]::WriteAllText((Join-Path $taskOut 'launch.json'),($taskRecord|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
