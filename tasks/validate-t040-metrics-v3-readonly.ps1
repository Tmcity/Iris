$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned T-040 Sandbox only' }
if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) { throw 'Windows PowerShell 5.1 required' }
$taskParseRows = foreach ($taskFile in @(Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object { $_.Extension -in @('.ps1','.psm1') })) {
    $taskTokens = $null
    $taskErrors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($taskFile.FullName, [ref]$taskTokens, [ref]$taskErrors)
    @{ File=$taskFile.Name; ErrorCount=@($taskErrors).Count; Errors=@($taskErrors | ForEach-Object { $_.Message }) }
}
$taskParse = @{ PsVersion=$PSVersionTable.PSVersion.ToString(); Files=@($taskParseRows); AllPassed=@($taskParseRows | Where-Object { $_.ErrorCount -gt 0 }).Count -eq 0 }
[IO.File]::WriteAllText('C:\IrisT040Output\metrics-v3-ps51-parse.json', ($taskParse | ConvertTo-Json -Depth 7), (New-Object Text.UTF8Encoding($false)))
if (-not $taskParse.AllPassed) { throw 'Repaired input did not parse in Windows PowerShell 5.1' }
Import-Module (Join-Path $PSScriptRoot 't040_metrics_process_readonly.psm1') -Force
$taskLiveIdentity = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $PID)
$taskLive = Read-T040ProcessCounters $taskLiveIdentity
if ($taskLive.State -ne 'Sample' -or $taskLive.Counters.WorkingSetBytes -le 0) { throw 'Live diagnostic process counters were not read' }
$taskLive.Process.Dispose()
$taskChild = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-NonInteractive','-Command','Start-Sleep -Milliseconds 1500') -WindowStyle Hidden -PassThru
$taskIdentity = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $taskChild.Id)
if (-not $taskIdentity) { throw 'Controlled short-lived diagnostic was not enumerated' }
if (-not $taskChild.WaitForExit(5000)) { throw 'Controlled short-lived diagnostic did not finish' }
$taskLegacyError = $null
try { $taskLegacy = [Diagnostics.Process]::GetProcessById([int]$taskIdentity.ProcessId); $null = $taskLegacy.Handle; $taskLegacy.Dispose() }
catch { $taskLegacyError = Get-T040MetricsError $_ 'LegacyAcquireAfterExit' }
if (-not $taskLegacyError) { throw 'Expected legacy acquisition boundary did not fail' }
$taskGone = Read-T040ProcessCounters $taskIdentity
if ($taskGone.State -ne 'Unavailable' -or $taskGone.Reason -ne 'ProcessAbsent') { throw 'Exited diagnostic was not safely classified after a fresh OS query' }
$taskChild.Dispose()
$taskRecord = @{ Utc=[DateTime]::UtcNow.ToString('o'); PsVersion=$PSVersionTable.PSVersion.ToString(); LiveProcessReadPassed=$true; EnumerateThenExitRaceHandled=$true; LegacyFailure=$taskLegacyError; RepairedResult=$taskGone; OriginalObserverRootCauseProved=$false; ProductOperationPerformed=$false }
[IO.File]::WriteAllText('C:\IrisT040Output\metrics-v3-validation.json', ($taskRecord | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))
Write-Output 'Metrics validation passed; no Iris operation performed.'
