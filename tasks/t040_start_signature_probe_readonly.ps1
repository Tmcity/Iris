$ErrorActionPreference='Stop'
if([Environment]::UserName -ne 'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskExisting=@(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object {$_.CommandLine -like '*t040_measure_msi_signature_readonly.ps1*'})
if($taskExisting.Count -ne 0){throw 'Signature diagnostic already running; inspect the existing probe'}
$taskOut=Join-Path 'C:\IrisT040Output' ('msi-signature-probe-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
$null=New-Item -ItemType Directory -Path $taskOut
$taskSource=Join-Path $PSScriptRoot 't040_measure_msi_signature_readonly.ps1'
$taskProcess=Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$taskSource,'-OutputDirectory',$taskOut) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskOut 'stdout.txt') -RedirectStandardError (Join-Path $taskOut 'stderr.txt')
$taskState=@{Utc=[DateTime]::UtcNow.ToString('o');ProbePid=$taskProcess.Id;OutputDirectory=$taskOut;SourceSha256=(Get-FileHash -LiteralPath $taskSource -Algorithm SHA256).Hash;ProductOperationPerformed=$false;SecurityPolicyChanged=$false}
[IO.File]::WriteAllText((Join-Path $taskOut 'launch.json'),($taskState | ConvertTo-Json -Depth 5),(New-Object Text.UTF8Encoding($false)))
