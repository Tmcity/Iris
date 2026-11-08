$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskExe = Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskApps = @(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object { $_.ExecutablePath -eq $taskExe })
if ($taskApps.Count -ne 1) { throw 'Exactly one actual primary required' }
$taskCli = Start-Process -FilePath $taskExe -ArgumentList @('--action','SelectionTranslate') -WindowStyle Hidden -PassThru
$null = $taskCli.Handle
if (-not $taskCli.WaitForExit(15000)) { throw 'Exact CLI still runs; preserve it for inspection' }
$taskCode = $taskCli.ExitCode
$taskRecord = @{ Utc = [DateTime]::UtcNow.ToString('o'); PrimaryPid = $taskApps[0].ProcessId; CliPid = $taskCli.Id; CliExitCode = $taskCode; ProductOperationPerformed = $true; NativeCliUsed = $true; ForegroundInputInjected = $false; ManualHotkeyProved = $false }
$taskCli.Dispose()
$taskPath = Join-Path 'C:\IrisT040Output' ('selection-cli-request-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
[IO.File]::WriteAllText($taskPath, ($taskRecord | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
if ($taskCode -ne 0) { throw 'Native command was not accepted' }
