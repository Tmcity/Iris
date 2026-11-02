param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskBase = [IO.Path]::GetFullPath('C:\IrisAcceptanceOutput') + '\'
if (-not [IO.Path]::GetFullPath($OutputDirectory).StartsWith($taskBase, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be an acceptance session' }
$taskRoot = Join-Path $env:LOCALAPPDATA 'Iris'
$taskMaintenance = Join-Path $env:LOCALAPPDATA 'Iris.Maintenance'
$taskKnown = @{}
$taskNames = @('Iris.App.exe', 'Iris.UiaHost.exe', 'Iris.Maintenance.exe', 'Iris.exe', 'Update.exe')
$taskPrefix = 'IrisEvidence-' + [Guid]::NewGuid().ToString('N')
$taskStartSource = $taskPrefix + '-start'
$taskStopSource = $taskPrefix + '-stop'
$taskWriter = New-Object IO.StreamWriter((Join-Path $OutputDirectory 'process-events.jsonl'), $false, (New-Object Text.UTF8Encoding($false)))
function RecordProcess($taskProcess, [string]$kind) {
    if (-not $taskProcess -or $taskNames -notcontains $taskProcess.Name) { return }
    $taskPath = $taskProcess.ExecutablePath
    if (-not $taskPath -or (-not $taskPath.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase) -and -not $taskPath.StartsWith($taskMaintenance + '\', [StringComparison]::OrdinalIgnoreCase))) { return }
    $taskRole = if ($taskProcess.Name -eq 'Iris.App.exe' -and $taskProcess.CommandLine -match '--veloapp-') { 'VelopackHook' } elseif ($taskProcess.Name -eq 'Iris.App.exe') { 'ApplicationOrClient' } else { 'MaintenanceOrStub' }
    $taskRecord = @{ Kind = $kind; Utc = [DateTime]::UtcNow.ToString('o'); Pid = $taskProcess.ProcessId; ParentPid = $taskProcess.ParentProcessId; Name = $taskProcess.Name; Path = $taskPath; CreatedUtc = $taskProcess.CreationDate.ToUniversalTime().ToString('o'); Role = $taskRole }
    $script:taskKnown[[int]$taskProcess.ProcessId] = $taskRecord
    $script:taskWriter.WriteLine(($taskRecord | ConvertTo-Json -Compress)); $script:taskWriter.Flush()
}
try {
    $null = Register-WmiEvent -Query 'SELECT * FROM Win32_ProcessStartTrace' -SourceIdentifier $taskStartSource
    $null = Register-WmiEvent -Query 'SELECT * FROM Win32_ProcessStopTrace' -SourceIdentifier $taskStopSource
    Get-CimInstance Win32_Process | ForEach-Object { RecordProcess $_ 'Baseline' }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'watcher-status.json'), '{"Ready":true,"ReadOnly":true}', (New-Object Text.UTF8Encoding($false)))
    while (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'stop.request'))) {
        $null = Wait-Event -Timeout 1
        foreach ($taskEventItem in @(Get-Event | Where-Object { $_.SourceIdentifier -in @($taskStartSource, $taskStopSource) })) {
            $taskNative = $taskEventItem.SourceEventArgs.NewEvent
            $taskEventPid = [int]$taskNative.ProcessID
            if ($taskEventItem.SourceIdentifier -eq $taskStartSource -and $taskNames -contains $taskNative.ProcessName) {
                $taskProcess = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $taskEventPid) -ErrorAction SilentlyContinue
                if ($taskProcess) { RecordProcess $taskProcess 'Started' }
                else { $taskWriter.WriteLine((@{ Kind = 'StartedUnresolved'; Utc = [DateTime]::UtcNow.ToString('o'); Pid = $taskEventPid; Name = $taskNative.ProcessName; Role = 'NeedsLogCorrelation' } | ConvertTo-Json -Compress)); $taskWriter.Flush() }
            } elseif ($taskEventItem.SourceIdentifier -eq $taskStopSource -and $taskKnown.ContainsKey($taskEventPid)) {
                $taskOld = $taskKnown[$taskEventPid]
                $taskWriter.WriteLine((@{ Kind = 'Exited'; Utc = [DateTime]::UtcNow.ToString('o'); Pid = $taskEventPid; Name = $taskOld.Name; Role = $taskOld.Role; ExitCode = $taskNative.ExitStatus } | ConvertTo-Json -Compress)); $taskWriter.Flush()
                $taskKnown.Remove($taskEventPid)
            }
            Remove-Event -EventIdentifier $taskEventItem.EventIdentifier
        }
    }
} catch {
    $taskWriter.WriteLine((@{ Kind = 'WatcherFailure'; Type = $_.Exception.GetType().Name; HResult = $_.Exception.HResult } | ConvertTo-Json -Compress)); $taskWriter.Flush()
} finally {
    Unregister-Event -SourceIdentifier $taskStartSource -ErrorAction SilentlyContinue
    Unregister-Event -SourceIdentifier $taskStopSource -ErrorAction SilentlyContinue
    $taskWriter.Dispose()
}
