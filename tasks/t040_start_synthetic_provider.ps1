$ErrorActionPreference='Stop'
if([Environment]::UserName -ne 'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskExe=Join-Path $PSScriptRoot 'tools\synthetic-provider\T040SyntheticProvider.exe'
$taskExisting=@(Get-CimInstance Win32_Process -Filter "Name='T040SyntheticProvider.exe'" | Where-Object {$_.ExecutablePath -eq $taskExe})
if($taskExisting.Count -ne 0){throw 'Owned fixture already runs; inspect the existing process'}
$taskStamp=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
$taskProcess=Start-Process -FilePath $taskExe -WindowStyle Hidden -PassThru -RedirectStandardOutput ('C:\IrisT040Output\fixture-stdout-'+$taskStamp+'.txt') -RedirectStandardError ('C:\IrisT040Output\fixture-stderr-'+$taskStamp+'.txt')
$taskResult=@{Utc=[DateTime]::UtcNow.ToString('o');Pid=$taskProcess.Id;ExecutablePath=$taskExe;Sha256=(Get-FileHash -LiteralPath $taskExe -Algorithm SHA256).Hash;ProductOperationPerformed=$false;ActualIrisProcessStarted=$false}
[IO.File]::WriteAllText(('C:\IrisT040Output\fixture-launch-'+$taskStamp+'.json'),($taskResult | ConvertTo-Json -Depth 5),(New-Object Text.UTF8Encoding($false)))
