param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned T-040 Sandbox only' }
Import-Module (Join-Path $PSScriptRoot 't040_metrics_process_readonly.psm1') -Force
$taskOut = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $taskOut.StartsWith('C:\IrisT040Output\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected output directory' }
$taskTarget = Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskApps = @(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object { $_.ExecutablePath -eq $taskTarget })
if ($taskApps.Count -ne 1 -or $taskApps[0].ProcessId -ne 5568) { throw 'Keep the identified session3 Iris instance' }
$taskIdentity = $taskApps[0]
$taskSettingsPath = Join-Path $env:APPDATA 'Iris\settings.json'
$taskRecord = @{ StartedUtc=[DateTime]::UtcNow.ToString('o'); ObserverPid=$PID; TargetPid=5568; TargetCreatedUtc=$taskIdentity.CreationDate.ToUniversalTime().ToString('o'); Stage='Baseline'; Completed=$false; Failure=$null; Snapshots=@(); CounterDurationSeconds=5; QuietPeriodAfterTraceSeconds=45; ProductOperationPerformed=$false; ForceGcRequested=$false; WorkingSetTrimmed=$false; FormalIdleMemoryProved=$false }
$taskUtf8 = New-Object Text.UTF8Encoding($false)
function Save-T040CounterAudit { [IO.File]::WriteAllText((Join-Path $taskOut 'state.json'),($script:taskRecord | ConvertTo-Json -Depth 10),$script:taskUtf8) }
function Read-T040CounterAuditSample([string]$Phase) {
    $taskResult = Read-T040ProcessCounters $taskIdentity
    if ($taskResult.State -ne 'Sample') { throw 'The identified Iris process exited; the audit will not restart it' }
    try { $taskCounters = $taskResult.Counters } finally { $taskResult.Process.Dispose() }
    $taskCounters.Phase = $Phase
    $taskCounters.SettingsSha256 = (Get-FileHash -LiteralPath $taskSettingsPath -Algorithm SHA256).Hash
    $script:taskRecord.Snapshots += $taskCounters
    Save-T040CounterAudit
}
Save-T040CounterAudit
try {
    Read-T040CounterAuditSample 'BeforeTrace'
    $taskCounterPath = Join-Path $PSScriptRoot 'tools\runtime-counters\RuntimeCounterProbe.exe'
    $taskCounter = Start-Process -FilePath $taskCounterPath -ArgumentList @('5568','5') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskOut 'counter-stdout.txt') -RedirectStandardError (Join-Path $taskOut 'counter-stderr.txt')
    $taskRecord.CounterPid = $taskCounter.Id
    $taskRecord.CounterExecutableSha256 = (Get-FileHash -LiteralPath $taskCounterPath -Algorithm SHA256).Hash
    $taskRecord.Stage = 'ObserveTrace'
    Save-T040CounterAudit
    if (-not $taskCounter.WaitForExit(20000)) { throw 'Counter observation is still running; preserve its actual process and inspect it' }
    $taskRecord.CounterExitCode = $taskCounter.ExitCode
    $taskCounter.Dispose()
    if ($taskRecord.CounterExitCode -ne 0) { throw 'Counter observation failed; preserve stderr and the trace state' }
    $taskTrace = @(Get-ChildItem -LiteralPath 'C:\IrisT040Output' -Directory -Filter 'runtime-counters-*' | Sort-Object Name -Descending)[0]
    $taskTraceState = Get-Content -LiteralPath (Join-Path $taskTrace.FullName 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $taskTraceState.Completed -or $taskTraceState.Failure -or $taskTraceState.Pid -ne 5568) { throw 'Completed matching counter trace is required' }
    $taskRecord.CounterTraceDirectory = $taskTrace.FullName
    $taskRecord.CounterTraceState = $taskTraceState
    Read-T040CounterAuditSample 'ImmediatelyAfterTrace'
    $taskRecord.Stage = 'WaitAfterTrace'
    Save-T040CounterAudit
    Start-Sleep -Seconds 45
    Read-T040CounterAuditSample 'AfterTrace45Seconds'
    $taskRecord.Stage = 'Complete'
} catch {
    $taskRecord.Failure = Get-T040MetricsError $_ $taskRecord.Stage
    $taskRecord.Stage = 'Failed'
} finally {
    $taskRecord.Completed = $true
    $taskRecord.FinishedUtc = [DateTime]::UtcNow.ToString('o')
    Save-T040CounterAudit
}
if ($taskRecord.Failure) { throw 'Counter influence audit failed; original state retained' }
