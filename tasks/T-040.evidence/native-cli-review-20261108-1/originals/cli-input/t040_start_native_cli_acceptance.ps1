$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned T-040 Sandbox only' }
$taskExisting=@(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object{$_.CommandLine-like'*t040_native_cli_acceptance.ps1*'})
if($taskExisting.Count-gt0){throw 'An actual native CLI acceptance worker is running; inspect it before another start'}
$taskOut=Join-Path 'C:\IrisT040Output' ('native-cli-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
$null=New-Item -ItemType Directory -Path $taskOut
$taskSource=Join-Path $PSScriptRoot 't040_native_cli_acceptance.ps1'
$taskChild=Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$taskSource,'-OutputDirectory',$taskOut) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskOut 'stdout.txt') -RedirectStandardError (Join-Path $taskOut 'stderr.txt')
$taskLaunch=@{Utc=[DateTime]::UtcNow.ToString('o');ObserverPid=$taskChild.Id;ObserverCreatedUtc=$taskChild.StartTime.ToUniversalTime().ToString('o');OutputDirectory=$taskOut;SourceSha256=(Get-FileHash -LiteralPath $taskSource -Algorithm SHA256).Hash;ProductOperationPerformed=$true;NativeCliUsed=$true;ForegroundKeyboardOrMouseInputUsed=$false;InstallerExecuted=$false}
[IO.File]::WriteAllText((Join-Path $taskOut 'launch.json'),($taskLaunch|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
Write-Output $taskOut
