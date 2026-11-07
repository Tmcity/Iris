$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned Sandbox only'}
$taskRun=Get-ChildItem -LiteralPath 'C:\IrisT040Output' -Directory -Filter 'startup-clone-run-*'|Sort-Object Name|Select-Object -Last 1
$taskLaunch=Get-Content -LiteralPath (Join-Path $taskRun.FullName 'launch.json') -Raw -Encoding UTF8|ConvertFrom-Json
$taskP=Get-CimInstance Win32_Process -Filter ('ProcessId='+[int]$taskLaunch.DiagnosticPid)
if(-not$taskP-or$taskP.ExecutablePath-ne$taskLaunch.DiagnosticPath){throw 'Matching actual diagnostic process required'}
$taskProcess=[Diagnostics.Process]::GetProcessById([int]$taskP.ProcessId);$null=$taskProcess.Handle
$taskCli=Start-Process -FilePath $taskLaunch.DiagnosticPath -ArgumentList @('--action','Exit') -WindowStyle Hidden -PassThru;$null=$taskCli.Handle
if(-not$taskCli.WaitForExit(15000)-or$taskCli.ExitCode-ne0){throw 'Exact diagnostic Exit CLI did not confirm success'}
$taskCli.Dispose()
if(-not$taskProcess.WaitForExit(15000)){throw 'Diagnostic remains alive; no replacement launched'}
$taskRecord=@{Utc=[DateTime]::UtcNow.ToString('o');DiagnosticPid=$taskLaunch.DiagnosticPid;DiagnosticExitCode=$taskProcess.ExitCode;DiagnosticPath=$taskLaunch.DiagnosticPath;InstalledBinaryChanged=$false;InstalledDataChanged=$false;ProductOperationPerformed=$true;FormalNfrResult=$false}
$taskProcess.Dispose()
[IO.File]::WriteAllText((Join-Path $taskRun.FullName 'exit.json'),($taskRecord|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
