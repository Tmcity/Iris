$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned T-040 Sandbox only' }
Import-Module (Join-Path $PSScriptRoot 't040_metrics_process_readonly.psm1') -Force
$taskAuditDirectory = @(Get-ChildItem -LiteralPath 'C:\IrisT040Output' -Directory -Filter 'counter-influence-*' | Sort-Object Name -Descending)[0]
$taskOriginal = Get-Content -LiteralPath (Join-Path $taskAuditDirectory.FullName 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($taskOriginal.CounterExitCode -ne $null -or $taskOriginal.Snapshots.Count -ne 1) { throw 'This recovery is only for the preserved null exit-code result' }
$taskCounter = Get-CimInstance Win32_Process -Filter ('ProcessId=' + [int]$taskOriginal.CounterPid)
if ($taskCounter -and $taskCounter.Name -eq 'RuntimeCounterProbe.exe') { throw 'Counter process is actually still running; do not infer completion' }
$taskTraceDirectory = @(Get-ChildItem -LiteralPath 'C:\IrisT040Output' -Directory -Filter 'runtime-counters-*' | Sort-Object Name -Descending)[0]
$taskTraceState = Get-Content -LiteralPath (Join-Path $taskTraceDirectory.FullName 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $taskTraceState.Completed -or $taskTraceState.Failure -or $taskTraceState.ObserverPid -ne $taskOriginal.CounterPid -or $taskTraceState.Pid -ne $taskOriginal.TargetPid) { throw 'Matching successful terminal trace is required for recovery' }
$taskElapsed = ([DateTime]::UtcNow - [DateTime]::Parse($taskTraceState.Utc).ToUniversalTime()).TotalSeconds
if ($taskElapsed -lt 45) { throw 'Wait at least 45 seconds after the completed counter observation' }
$taskProduct = Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskIdentity = Get-CimInstance Win32_Process -Filter ('ProcessId=' + [int]$taskOriginal.TargetPid)
if (-not $taskIdentity -or $taskIdentity.ExecutablePath -ne $taskProduct -or [Math]::Abs(($taskIdentity.CreationDate.ToUniversalTime() - [DateTime]::Parse($taskOriginal.TargetCreatedUtc).ToUniversalTime()).TotalMilliseconds) -gt 1) { throw 'Keep the same identified Iris instance' }
$taskRead = Read-T040ProcessCounters $taskIdentity
if ($taskRead.State -ne 'Sample') { throw 'The identified Iris instance exited; no restart was performed' }
try { $taskSample = $taskRead.Counters } finally { $taskRead.Process.Dispose() }
$taskSample.Phase = 'AfterCompletedTraceAtLeast45Seconds'
$taskSample.SettingsSha256 = (Get-FileHash -LiteralPath (Join-Path $env:APPDATA 'Iris\settings.json') -Algorithm SHA256).Hash
$taskRecord = @{ Utc=[DateTime]::UtcNow.ToString('o'); Completed=$true; OriginalStatePreserved=$true; OriginalExitCodeWasNull=$true; CounterActualProcessAbsent=$true; CounterTraceDirectory=$taskTraceDirectory.FullName; CounterTraceState=$taskTraceState; ElapsedAfterTraceSeconds=$taskElapsed; PostTraceSample=$taskSample; ProductOperationPerformed=$false; ForceGcRequested=$false; WorkingSetTrimmed=$false; FormalIdleMemoryProved=$false }
[IO.File]::WriteAllText((Join-Path $taskAuditDirectory.FullName 'recovery.json'),($taskRecord | ConvertTo-Json -Depth 10),(New-Object Text.UTF8Encoding($false)))
Write-Output 'Recovered terminal counter evidence and current sample; no tracing was repeated.'
