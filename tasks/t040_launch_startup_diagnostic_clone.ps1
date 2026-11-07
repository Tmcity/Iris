$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned Sandbox only'}
$taskInstalled=Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskApps=@(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'"|Where-Object{$_.ExecutablePath-eq$taskInstalled})
if($taskApps.Count-ne1-or$taskApps[0].ProcessId-ne6320){throw 'Expected restored installed Iris6320'}
$taskOriginal=[Diagnostics.Process]::GetProcessById(6320);$null=$taskOriginal.Handle
$taskOut=Join-Path 'C:\IrisT040Output' ('startup-clone-run-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'));$null=New-Item -ItemType Directory -Path $taskOut
$taskApp=Join-Path (Join-Path $env:LOCALAPPDATA 'IrisT040Diagnostic') ([Guid]::NewGuid().ToString('N'));$null=New-Item -ItemType Directory -Path $taskApp
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tools\startup-diagnostic') | Copy-Item -Destination $taskApp -Recurse
[IO.File]::WriteAllText((Join-Path $taskApp 'portable.txt'),'',(New-Object Text.UTF8Encoding($false)))
$taskData=Join-Path $taskApp 'data';$null=New-Item -ItemType Directory -Path $taskData
Copy-Item -LiteralPath (Join-Path $env:APPDATA 'Iris\settings.json') -Destination (Join-Path $taskData 'settings.json')
Copy-Item -LiteralPath (Join-Path $env:APPDATA 'Iris\secrets.dat') -Destination (Join-Path $taskData 'secrets.dat')
$taskExit=Start-Process -FilePath $taskInstalled -ArgumentList @('--action','Exit') -WindowStyle Hidden -PassThru;$null=$taskExit.Handle
if(-not$taskExit.WaitForExit(15000)-or$taskExit.ExitCode-ne0){throw 'Exit CLI did not complete; preserve original process'}
$taskExit.Dispose()
if(-not$taskOriginal.WaitForExit(15000)){throw 'Installed primary is still shutting down; no clone launched'}
$taskExitCode=$taskOriginal.ExitCode;$taskOriginal.Dispose()
$taskExe=Join-Path $taskApp 'Iris.App.exe'
$taskProcess=Start-Process -FilePath $taskExe -WindowStyle Hidden -PassThru;$null=$taskProcess.Handle
$taskRecord=@{Utc=[DateTime]::UtcNow.ToString('o');OriginalPid=6320;OriginalExitCode=$taskExitCode;DiagnosticPid=$taskProcess.Id;DiagnosticCreatedUtc=$taskProcess.StartTime.ToUniversalTime().ToString('o');DiagnosticPath=$taskExe;OutputDirectory=$taskOut;DiagnosticDataLocalToGuest=$true;ProductOperationPerformed=$true;InstalledBinaryChanged=$false;InstalledDataChanged=$false;FormalNfrResult=$false;OnlyCurrentSidSyntheticCredentialsCopied=$true}
[IO.File]::WriteAllText((Join-Path $taskOut 'launch.json'),($taskRecord|ConvertTo-Json -Depth 7),(New-Object Text.UTF8Encoding($false)))
Write-Output $taskOut
