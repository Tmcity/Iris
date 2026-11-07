$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned Sandbox only'}
$taskApps=@(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'")
if($taskApps.Count-ne0){throw 'Diagnostic or product process is still present; do not start a replacement'}
$taskExe=Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskSource=Get-Content -LiteralPath (Join-Path (Split-Path -Parent $taskExe) 'release-source.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($taskSource.Version-ne'0.2.11'){throw 'Expected unchanged installed candidate0.2.11'}
$taskProcess=Start-Process -FilePath $taskExe -WindowStyle Hidden -PassThru
$taskRecord=@{Utc=[DateTime]::UtcNow.ToString('o');Pid=$taskProcess.Id;CreatedUtc=$taskProcess.StartTime.ToUniversalTime().ToString('o');ExecutablePath=$taskExe;Version='0.2.11';ProductOperationPerformed=$true;InstalledBinaryChanged=$false;InstallerExecuted=$false}
[IO.File]::WriteAllText(('C:\IrisT040Output\restored-installed-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')+'.json'),($taskRecord|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
