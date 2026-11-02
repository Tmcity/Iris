param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned acceptance Sandbox only' }
$taskState = Get-Content -LiteralPath 'C:\IrisAcceptanceOutput\active-session.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskSession = [IO.Path]::GetFullPath($taskState.SessionDirectory).TrimEnd('\') + '\'
$taskOut = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $taskOut.StartsWith($taskSession, [StringComparison]::OrdinalIgnoreCase)) { throw 'Recorder must belong to the active acceptance session' }
$null = New-Item -ItemType Directory -Path $taskOut
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
    $taskRecord.Sequence = $script:taskCount
    $taskRecord.ObservedUtc = [DateTime]::UtcNow.ToString('o')
    $script:taskWriter.WriteLine(($taskRecord | ConvertTo-Json -Depth 6 -Compress))
    $script:taskWriter.Flush()
}
function IdentifyProcess($taskProcess) {
    if (-not $taskProcess -or $taskNames -notcontains $taskProcess.Name) { return $null }
    $taskPath = $taskProcess.ExecutablePath
    if (-not $taskPath -or (-not $taskPath.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase) -and -not $taskPath.StartsWith($taskMaintenance + '\', [StringComparison]::OrdinalIgnoreCase))) { return $null }
    $taskRole = if ($taskProcess.Name -eq 'Iris.App.exe' -and $taskProcess.CommandLine -match '--veloapp-') { 'VelopackHook' } elseif ($taskProcess.Name -eq 'Iris.App.exe') { 'ApplicationOrClient' } else { 'MaintenanceOrStub' }
    return @{ Pid = [int]$taskProcess.ProcessId; ParentPid = [int]$taskProcess.ParentProcessId; Name = $taskProcess.Name; Path = $taskPath; CreatedUtc = $taskProcess.CreationDate.ToUniversalTime().ToString('o'); Role = $taskRole; OwnedPathVerified = $true }
}
function WriteStatus([bool]$taskReady, $taskFailure) {
    $taskStatus = @{ Ready = $taskReady; ReadOnly = $true; RecorderPid = $PID; LastHeartbeatUtc = [DateTime]::UtcNow.ToString('o'); Records = $script:taskCount; Failure = $taskFailure }
    [IO.File]::WriteAllText((Join-Path $taskOut 'watcher-v2-status.json'), ($taskStatus | ConvertTo-Json -Depth 6), $taskUtf8)
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
    WriteStatus $true $null
    while (-not (Test-Path -LiteralPath (Join-Path $taskOut 'stop.request')) -and -not (Test-Path -LiteralPath (Join-Path $taskSession 'stop.request'))) {
        $null = Wait-Event -Timeout 1
        foreach ($taskEventItem in @(Get-Event | Where-Object { $_.SourceIdentifier -in @($taskStartSource,$taskStopSource) })) {
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
            Remove-Event -EventIdentifier $taskEventItem.EventIdentifier
        }
        WriteStatus $true $null
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
