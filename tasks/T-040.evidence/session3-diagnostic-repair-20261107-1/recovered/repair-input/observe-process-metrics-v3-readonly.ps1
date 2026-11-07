param([Parameter(Mandatory=$true)][string]$OutputDirectory, [ValidateRange(5,43200)][int]$DurationSeconds=21600)
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned T-040 Sandbox only' }
if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) { throw 'Windows PowerShell 5.1 required' }
$taskOut = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $taskOut.StartsWith('C:\IrisT040Output\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected metrics output path' }
if (Test-Path -LiteralPath $taskOut) { throw 'Use a new metrics session' }
Import-Module (Join-Path $PSScriptRoot 't040_metrics_process_readonly.psm1') -Force
$null = New-Item -ItemType Directory -Path $taskOut
$taskStarted = [DateTime]::UtcNow
$taskTarget = Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskData = Join-Path $env:APPDATA 'Iris'
$taskUtf8 = New-Object Text.UTF8Encoding($false)
$taskWriter = New-Object IO.StreamWriter((Join-Path $taskOut 'samples.jsonl'), $false, $taskUtf8)
$taskWriter.AutoFlush = $true
$taskKnown = @{}
$taskLogical = [Environment]::ProcessorCount
$taskSequence = 0
$taskProcessSamples = 0
$taskConfirmedRaces = 0
$taskStage = 'Initialize'

function Write-T040MetricsRow($Value) {
    $script:taskSequence++
    $Value.Sequence = $script:taskSequence
    $Value.ObservedUtc = [DateTime]::UtcNow.ToString('o')
    $script:taskWriter.WriteLine(($Value | ConvertTo-Json -Depth 9 -Compress))
}
function Save-T040MetricsStatus([bool]$Ready, [bool]$Completed, $Failure) {
    $taskRecord = @{ Ready=$Ready; Completed=$Completed; ObserverPid=$PID; StartedUtc=$taskStarted.ToString('o'); LastHeartbeatUtc=[DateTime]::UtcNow.ToString('o'); Samples=$script:taskSequence; ProcessSamples=$script:taskProcessSamples; ConfirmedProcessRaces=$script:taskConfirmedRaces; Failure=$Failure; Stage=$script:taskStage; OutputDirectory=$taskOut; ProductOperationPerformed=$false }
    $taskTemporary = Join-Path $taskOut 'status.tmp.json'
    [IO.File]::WriteAllText($taskTemporary, ($taskRecord | ConvertTo-Json -Depth 9), $taskUtf8)
    Move-Item -LiteralPath $taskTemporary -Destination (Join-Path $taskOut 'status.json') -Force
}
function Record-T040Unavailable($Identity, $Result, [string]$Phase) {
    $script:taskConfirmedRaces++
    Write-T040MetricsRow @{ Kind='ProcessUnavailable'; Pid=[int]$Identity.ProcessId; ProcessCreatedUtc=$Identity.CreationDate.ToUniversalTime().ToString('o'); Phase=$Phase; Reason=$Result.Reason; VerifiedUtc=$Result.VerifiedUtc; ExitCode=$Result.ExitCode; ExitUtc=$Result.ExitUtc; Error=$Result.Error }
}

try {
    Write-T040MetricsRow @{ Kind='ObserverStarted'; ObserverPid=$PID; LogicalProcessors=$taskLogical; TargetPath=$taskTarget; ProductOperationPerformed=$false; InitialProductPresent=(Test-Path -LiteralPath $taskTarget) }
    Save-T040MetricsStatus $false $false $null
    while (-not (Test-Path -LiteralPath (Join-Path $taskOut 'stop.request')) -and ([DateTime]::UtcNow - $taskStarted).TotalSeconds -lt $DurationSeconds) {
        $taskStage = 'EnumerateProcesses'
        $taskCpuLoad = (Get-CimInstance Win32_Processor -ErrorAction Stop | Measure-Object -Property LoadPercentage -Average).Average
        $taskCpuObservedUtc = [DateTime]::UtcNow.ToString('o')
        $taskMatches = @(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" -ErrorAction Stop | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.Equals($taskTarget, [StringComparison]::OrdinalIgnoreCase) })
        foreach ($taskIdentity in $taskMatches) {
            $taskId = [int]$taskIdentity.ProcessId
            if (-not $taskKnown.ContainsKey($taskId)) {
                $taskStage = 'AcquireTarget'
                $taskResult = Read-T040ProcessCounters $taskIdentity
                if ($taskResult.State -eq 'Unavailable') { Record-T040Unavailable $taskIdentity $taskResult 'Acquire'; continue }
                $taskRole = if ($taskIdentity.CommandLine -match '(?i)--(?:veloapp|squirrel)-(?:install|updated|obsolete|uninstall)(?:\s|"|$)') { 'InstallerLifecycle' } else { 'Application' }
                $taskKnown[$taskId] = @{ Process=$taskResult.Process; Identity=$taskIdentity; StartedUtc=$taskResult.Counters.ProcessCreatedUtc; LastCpuSeconds=$taskResult.Counters.ProcessCpuTotalSeconds; LastSampleUtc=[DateTime]::Parse($taskResult.Counters.ReadCompletedUtc).ToUniversalTime(); StartupRecorded=$false; Role=$taskRole }
                Write-T040MetricsRow @{ Kind='ProcessFirstObserved'; Pid=$taskId; StartedUtc=$taskKnown[$taskId].StartedUtc; SystemCpuLoadPercentage=$taskCpuLoad; SystemCpuObservedUtc=$taskCpuObservedUtc; ParentPid=$taskIdentity.ParentProcessId; ExecutablePath=$taskIdentity.ExecutablePath; CommandLine=$taskIdentity.CommandLine; Role=$taskRole; AttachedAfterProcessStart=([DateTime]::Parse($taskKnown[$taskId].StartedUtc).ToUniversalTime() -lt $taskStarted) }
            }
        }
        foreach ($taskId in @($taskKnown.Keys)) {
            $taskEntry = $taskKnown[$taskId]
            $taskStage = 'SampleTarget'
            $taskResult = Read-T040ProcessCounters $taskEntry.Identity $taskEntry.Process
            if ($taskResult.State -eq 'Unavailable') {
                Record-T040Unavailable $taskEntry.Identity $taskResult 'Sample'
                $taskEntry.Process.Dispose()
                $taskKnown.Remove($taskId)
                continue
            }
            $taskCounters = $taskResult.Counters
            $taskReadUtc = [DateTime]::Parse($taskCounters.ReadCompletedUtc).ToUniversalTime()
            $taskSeconds = ($taskReadUtc - $taskEntry.LastSampleUtc).TotalSeconds
            $taskCounters.Kind = 'ProcessSample'
            $taskCounters.Pid = $taskId
            $taskCounters.Role = $taskEntry.Role
            $taskCounters.ProcessCpuPercentage = if ($taskSeconds -gt 0) { 100 * ($taskCounters.ProcessCpuTotalSeconds - $taskEntry.LastCpuSeconds) / ($taskSeconds * $taskLogical) } else { 0 }
            $taskCounters.SystemCpuLoadPercentage = $taskCpuLoad
            $taskCounters.SystemCpuObservedUtc = $taskCpuObservedUtc
            $taskCounters.SinceCreatedSeconds = ($taskReadUtc - [DateTime]::Parse($taskEntry.StartedUtc).ToUniversalTime()).TotalSeconds
            Write-T040MetricsRow $taskCounters
            $taskProcessSamples++
            $taskEntry.LastCpuSeconds = $taskCounters.ProcessCpuTotalSeconds
            $taskEntry.LastSampleUtc = $taskReadUtc
            if (-not $taskEntry.StartupRecorded -and $taskEntry.Role -eq 'Application') {
                $taskStage = 'CorrelateStartupLog'
                $taskLogFolder = Join-Path $taskData 'logs'
                foreach ($taskLog in @(Get-ChildItem -LiteralPath $taskLogFolder -File -Filter 'iris-*.log' -ErrorAction SilentlyContinue)) {
                    $taskStream = [IO.File]::Open($taskLog.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
                    try {
                        $taskReader = New-Object IO.StreamReader($taskStream, $taskUtf8)
                        try { $taskLines = $taskReader.ReadToEnd().Split([char]10) } finally { $taskReader.Dispose() }
                    } finally { $taskStream.Dispose() }
                    foreach ($taskLine in $taskLines) {
                        if ($taskLine -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) ([+-]\d{2}:\d{2}).*Iris\.App\.Hosting\.AppBootstrapper.*Iris 启动序列完成.*?([0-9]+(?:\.[0-9]+)?) ms') {
                            $taskReadyUtc = [DateTimeOffset]::Parse(($Matches[1]+' '+$Matches[2]), [Globalization.CultureInfo]::InvariantCulture).UtcDateTime
                            $taskCreatedUtc = [DateTime]::Parse($taskEntry.StartedUtc).ToUniversalTime()
                            if ($taskReadyUtc -ge $taskCreatedUtc) {
                                Write-T040MetricsRow @{ Kind='StartupLogCorrelated'; Pid=$taskId; ProcessCreatedUtc=$taskEntry.StartedUtc; BootstrapReadyUtc=$taskReadyUtc.ToString('o'); BootstrapReportedMs=[double]::Parse($Matches[3], [Globalization.CultureInfo]::InvariantCulture); CreationToBootstrapReadyMs=($taskReadyUtc-$taskCreatedUtc).TotalMilliseconds; LogName=$taskLog.Name; LogLine=$taskLine.TrimEnd(); StartupCpuObservedBeforeReady=($taskStarted -le $taskCreatedUtc); CurrentSystemCpuIsStartupCpu=$false }
                                $taskEntry.StartupRecorded = $true
                                break
                            }
                        }
                    }
                    if ($taskEntry.StartupRecorded) { break }
                }
            }
        }
        $taskStage = 'Observe'
        Save-T040MetricsStatus $true $false $null
        Start-Sleep -Milliseconds 1000
    }
    $taskStage = 'Stopped'
    Write-T040MetricsRow @{ Kind='ObserverStopped'; ProductOperationPerformed=$false }
    Save-T040MetricsStatus $false $true $null
} catch {
    $taskFailure = Get-T040MetricsError $_ $taskStage
    Write-T040MetricsRow @{ Kind='ObserverFailed'; Error=$taskFailure; ProductOperationPerformed=$false }
    Save-T040MetricsStatus $false $true $taskFailure
    throw
} finally {
    foreach ($taskEntry in $taskKnown.Values) { $taskEntry.Process.Dispose() }
    $taskWriter.Dispose()
}
