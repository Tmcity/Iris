$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw'Owned T-040 Sandbox only'}
if(Test-Path -LiteralPath 'C:\IrisT040Output\active-metrics.json'){throw'Metrics session has already been started; inspect its actual process before another start'}
$taskOut=Join-Path 'C:\IrisT040Output' ('metrics-'+[Guid]::NewGuid().ToString('N'))
$taskScript=Join-Path $PSScriptRoot 'observe-process-metrics-v2-readonly.ps1'
$taskProcess=Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$taskScript,'-OutputDirectory',$taskOut) -WindowStyle Hidden -PassThru
$taskPointer=[ordered]@{ObserverPid=$taskProcess.Id;StartedUtc=[DateTime]::UtcNow.ToString('o');OutputDirectory=$taskOut;SourceSha256=(Get-FileHash -LiteralPath $taskScript -Algorithm SHA256).Hash.ToLowerInvariant();ProductOperationPerformed=$false}
[IO.File]::WriteAllText('C:\IrisT040Output\active-metrics.json',($taskPointer|ConvertTo-Json -Depth 4),(New-Object Text.UTF8Encoding($false)))
Write-Output ('Metrics observer PID '+$taskProcess.Id+'; no Iris operation performed.')
