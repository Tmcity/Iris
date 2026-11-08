$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskPath = Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskApps = @(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object { $_.ExecutablePath -eq $taskPath })
if ($taskApps.Count -ne 1) { throw 'Exactly one owned primary required' }
$taskStamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
$taskOut = Join-Path 'C:\IrisT040Output' ('selection-copy-probe-' + $taskStamp)
$null = New-Item -ItemType Directory -Path $taskOut
$taskExe = Join-Path $PSScriptRoot 'tools\selection-copy-state\SelectionCopyStateProbe.exe'
$taskProbe = Start-Process -FilePath $taskExe -ArgumentList @([string]$taskApps[0].ProcessId) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskOut 'stdout.txt') -RedirectStandardError (Join-Path $taskOut 'stderr.txt')
$null = $taskProbe.Handle
if (-not $taskProbe.WaitForExit(10000)) { throw 'Exact probe is still running; inspect it before retrying' }
$taskExit = $taskProbe.ExitCode
$taskProbe.Dispose()
Write-Output $taskOut
if ($taskExit -ne 0) { throw 'Metadata probe failed; its stdout/stderr and original state are retained' }
