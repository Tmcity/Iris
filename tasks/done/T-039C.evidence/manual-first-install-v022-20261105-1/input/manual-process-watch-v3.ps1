param([Parameter(Mandatory = $true)][string]$OutputDirectory, [switch]$ExerciseQueueRemovalRace)
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned acceptance Sandbox only' }
$taskState = Get-Content -LiteralPath 'C:\IrisAcceptanceOutput\active-session.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskSession = [IO.Path]::GetFullPath($taskState.SessionDirectory).TrimEnd('\') + '\'
$taskOut = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $taskOut.StartsWith($taskSession, [StringComparison]::OrdinalIgnoreCase)) { throw 'Recorder must belong to the active acceptance session' }
if (-not (Test-Path -LiteralPath $taskOut)) { $null = New-Item -ItemType Directory -Path $taskOut }
if (Test-Path -LiteralPath (Join-Path $taskOut 'process-events-v2.jsonl')) { throw 'Recorder output has already been used' }
$taskProbePath = Join-Path $taskOut 'probe\Iris.exe'
$taskSeenEvents = New-Object 'Collections.Generic.HashSet[int]'
$taskMissingRemovals = 0
$taskLastHeartbeat = [DateTime]::MinValue
$taskRoot = Join-Path $env:LOCALAPPDATA 'Iris'
$taskMaintenance = Join-Path $env:LOCALAPPDATA 'Iris.Maintenance'
$taskNames = @('Iris.App.exe','Iris.UiaHost.exe','Iris.Maintenance.exe','Iris.exe','Update.exe')
$taskKnown = @{}
$taskCount = 0
$taskUtf8 = New-Object Text.UTF8Encoding($false)
$taskWriter = New-Object IO.StreamWriter((Join-Path $taskOut 'process-events-v2.jsonl'), $false, $taskUtf8)
$taskPrefix = 'IrisEvidenceV2-' + [Guid]::NewGuid().ToString('N')
$taskStartSource = $taskPrefix + '-start'
$taskStopSource = $taskPrefix + '-stop'

function WriteRecord($taskRecord) {
    $script:taskCount++
    $taskRecord.RecorderVersion = 3
    $taskRecord.Sequence = $script:taskCount
    $taskRecord.ObservedUtc = [DateTime]::UtcNow.ToString('o')
    $script:taskWriter.WriteLine(($taskRecord | ConvertTo-Json -Depth 6 -Compress))
    $script:taskWriter.Flush()
}
function IdentifyProcess($taskProcess) {
    if (-not $taskProcess -or $taskNames -notcontains $taskProcess.Name) { return $null }
    $taskPath = $taskProcess.ExecutablePath
    if (-not $taskPath -or (-not $taskPath.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase) -and -not $taskPath.StartsWith($taskMaintenance + '\', [StringComparison]::OrdinalIgnoreCase) -and -not $taskPath.Equals($taskProbePath, [StringComparison]::OrdinalIgnoreCase))) { return $null }
    $taskRole = if ($taskPath.Equals($taskProbePath, [StringComparison]::OrdinalIgnoreCase)) { 'AuxiliaryProbe' } elseif ($taskProcess.Name -eq 'Iris.App.exe' -and $taskProcess.CommandLine -match '--veloapp-') { 'VelopackHook' } elseif ($taskProcess.Name -eq 'Iris.App.exe') { 'ApplicationOrClient' } else { 'MaintenanceOrStub' }
    return @{ Pid = [int]$taskProcess.ProcessId; ParentPid = [int]$taskProcess.ParentProcessId; Name = $taskProcess.Name; Path = $taskPath; CreatedUtc = $taskProcess.CreationDate.ToUniversalTime().ToString('o'); Role = $taskRole; OwnedPathVerified = $true }
}
function WriteStatus([bool]$taskReady, $taskFailure) {
    $script:taskLastHeartbeat = [DateTime]::UtcNow
    $taskStatus = @{ RecorderVersion = 3; QueueRemovalAlreadyAbsent = $script:taskMissingRemovals; Ready = $taskReady; ReadOnly = $true; RecorderPid = $PID; LastHeartbeatUtc = [DateTime]::UtcNow.ToString('o'); Records = $script:taskCount; Failure = $taskFailure }
    $taskStatusPath = Join-Path $taskOut 'watcher-v2-status.json'
    $taskTemporaryStatus = Join-Path $taskOut 'watcher-status.pending'
    [IO.File]::WriteAllText($taskTemporaryStatus, ($taskStatus | ConvertTo-Json -Depth 6), $taskUtf8)
    Move-Item -LiteralPath $taskTemporaryStatus -Destination $taskStatusPath -Force
}
function CompleteEventRemoval($taskEventItem) {
    if ($null -eq $taskEventItem -or [int]$taskEventItem.EventIdentifier -le 0) { throw 'Invalid event identifier' }
    try { Remove-Event -EventIdentifier $taskEventItem.EventIdentifier -ErrorAction Stop }
    catch [System.ArgumentException] {
        if (@(Get-Event -EventIdentifier $taskEventItem.EventIdentifier -ErrorAction SilentlyContinue).Count -ne 0) { throw }
        $script:taskMissingRemovals++
        WriteRecord (@{ Kind = 'QueueRemovalAlreadyAbsent'; EventIdentifier = [int]$taskEventItem.EventIdentifier; ErrorType = $_.Exception.GetType().FullName; NativeLifecycleEvent = $false })
    }
}
try {
    $null = Register-WmiEvent -Query 'SELECT * FROM Win32_ProcessStartTrace' -SourceIdentifier $taskStartSource
    $null = Register-WmiEvent -Query 'SELECT * FROM Win32_ProcessStopTrace' -SourceIdentifier $taskStopSource
    Get-CimInstance Win32_Process | ForEach-Object {
        $taskIdentity = IdentifyProcess $_
        if ($taskIdentity) {
            $taskKnown[$taskIdentity.Pid] = $taskIdentity
            WriteRecord (@{ Kind = 'Baseline'; Identity = $taskIdentity })
        }
    }
    if ($ExerciseQueueRemovalRace) {
        $taskRemovalProbe = New-Event -SourceIdentifier ($taskPrefix + '-removal-probe')
        Remove-Event -EventIdentifier $taskRemovalProbe.EventIdentifier -ErrorAction Stop
        CompleteEventRemoval $taskRemovalProbe
        WriteRecord (@{ Kind = 'QueueRemovalProbeCompleted'; NativeLifecycleEvent = $false; AlreadyAbsentHandled = ($taskMissingRemovals -eq 1) })
    }
    WriteStatus $true $null
    while (-not (Test-Path -LiteralPath (Join-Path $taskOut 'stop.request')) -and -not (Test-Path -LiteralPath (Join-Path $taskSession 'stop.request'))) {
        Start-Sleep -Milliseconds 200
        foreach ($taskEventItem in @(Get-Event | Where-Object { $_.SourceIdentifier -in @($taskStartSource,$taskStopSource) })) {
            if (-not $taskSeenEvents.Add([int]$taskEventItem.EventIdentifier)) { CompleteEventRemoval $taskEventItem; continue }
            $taskNative = $taskEventItem.SourceEventArgs.NewEvent
            $taskEventPid = [int]$taskNative.ProcessID
            if ($taskNames -contains $taskNative.ProcessName) {
                $taskKind = if ($taskEventItem.SourceIdentifier -eq $taskStartSource) { 'Started' } else { 'Exited' }
                $taskIdentity = $null
                if ($taskKind -eq 'Started') {
                    $taskProcess = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $taskEventPid) -ErrorAction SilentlyContinue
                    $taskIdentity = IdentifyProcess $taskProcess
                    if ($taskIdentity) { $taskKnown[$taskEventPid] = $taskIdentity }
                } elseif ($taskKnown.ContainsKey($taskEventPid)) {
                    $taskIdentity = $taskKnown[$taskEventPid]
                    $taskKnown.Remove($taskEventPid)
                }
                WriteRecord (@{ Kind = $taskKind; Pid = $taskEventPid; Name = $taskNative.ProcessName; NativeEventUtc = [DateTime]::FromFileTimeUtc([int64]$taskNative.TIME_CREATED).ToString('o'); ExitCode = if ($taskKind -eq 'Exited') { [uint32]$taskNative.ExitStatus } else { $null }; Identity = $taskIdentity; NeedsLogCorrelation = ($null -eq $taskIdentity) })
            }
            CompleteEventRemoval $taskEventItem
        }
        if (([DateTime]::UtcNow - $taskLastHeartbeat).TotalSeconds -ge 1) { WriteStatus $true $null }
    }
    WriteRecord (@{ Kind = 'RecorderStopped'; Reason = 'Acceptance stop marker' })
    WriteStatus $false $null
} catch {
    $taskFailure = @{ Type = $_.Exception.GetType().FullName; Message = $_.Exception.Message; Position = $_.InvocationInfo.PositionMessage }
    WriteRecord (@{ Kind = 'RecorderFailure'; Failure = $taskFailure })
    WriteStatus $false $taskFailure
} finally {
    Unregister-Event -SourceIdentifier $taskStartSource -ErrorAction SilentlyContinue
    Unregister-Event -SourceIdentifier $taskStopSource -ErrorAction SilentlyContinue
    $taskWriter.Dispose()
}
