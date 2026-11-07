$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskExisting=@(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'"|Where-Object{$_.CommandLine-like'*t040_native_cli_feature_cases.ps1*'})
if($taskExisting.Count-gt0){throw 'An actual feature worker is running; inspect it before another start'}
$taskOut=Join-Path 'C:\IrisT040Output' ('native-features-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'));$null=New-Item -ItemType Directory -Path $taskOut
$taskSource=Join-Path $PSScriptRoot 't040_native_cli_feature_cases.ps1'
$taskChild=Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$taskSource,'-OutputDirectory',$taskOut) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskOut 'stdout.txt') -RedirectStandardError (Join-Path $taskOut 'stderr.txt')
$taskRecord=@{Utc=[DateTime]::UtcNow.ToString('o');ObserverPid=$taskChild.Id;ObserverCreatedUtc=$taskChild.StartTime.ToUniversalTime().ToString('o');SourceSha256=(Get-FileHash -LiteralPath $taskSource -Algorithm SHA256).Hash;ProductOperationPerformed=$true;NativeCliUsed=$true;InstallerExecuted=$false}
[IO.File]::WriteAllText((Join-Path $taskOut 'launch.json'),($taskRecord|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
