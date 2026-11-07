function Get-T040MetricsError {
    param([System.Management.Automation.ErrorRecord]$Record, [string]$Stage)
    $taskChain = @()
    $taskException = $Record.Exception
    while ($taskException -and $taskChain.Count -lt 8) {
        $taskChain += @{ Type = $taskException.GetType().FullName; Message = $taskException.Message; HResult = $taskException.HResult }
        $taskException = $taskException.InnerException
    }
    return @{ Stage = $Stage; Exceptions = $taskChain; FullyQualifiedErrorId = $Record.FullyQualifiedErrorId; Position = $Record.InvocationInfo.PositionMessage; ScriptStackTrace = $Record.ScriptStackTrace }
}

function Read-T040ProcessCounters {
    param($Identity, [Diagnostics.Process]$Process)
    $taskOwnedHandle = $null -eq $Process
    $taskStage = 'OpenProcess'
    try {
        if ($taskOwnedHandle) { $Process = [Diagnostics.Process]::GetProcessById([int]$Identity.ProcessId) }
        $null = $Process.Handle
        $taskStage = 'ReadProcessIdentity'
        if ($Process.HasExited) { throw [InvalidOperationException]::new('Process exited before counter read') }
        $taskCreated = $Process.StartTime.ToUniversalTime()
        if ([Math]::Abs(($taskCreated - $Identity.CreationDate.ToUniversalTime()).TotalMilliseconds) -gt 1) {
            throw [InvalidOperationException]::new('Process creation time changed after enumeration')
        }
        $taskStage = 'ReadProcessCounters'
        $Process.Refresh()
        $taskReadStarted = [DateTime]::UtcNow
        $taskCounters = @{
            WorkingSetBytes = $Process.WorkingSet64
            PrivateBytes = $Process.PrivateMemorySize64
            ProcessCpuTotalSeconds = $Process.TotalProcessorTime.TotalSeconds
            MainWindowHandle = $Process.MainWindowHandle.ToInt64()
            ProcessCreatedUtc = $taskCreated.ToString('o')
            ReadStartedUtc = $taskReadStarted.ToString('o')
            ReadCompletedUtc = [DateTime]::UtcNow.ToString('o')
        }
        if ($Process.HasExited) { throw [InvalidOperationException]::new('Process exited during counter read') }
        return @{ State = 'Sample'; Process = $Process; Counters = $taskCounters }
    } catch {
        $taskError = $_
        # A read error is recoverable only when a fresh OS query proves that the
        # enumerated process is gone or its PID now identifies another process.
        $taskCurrent = Get-CimInstance Win32_Process -Filter ('ProcessId=' + [int]$Identity.ProcessId) -ErrorAction Stop
        $taskReason = $null
        if (-not $taskCurrent) { $taskReason = 'ProcessAbsent' }
        elseif ($taskCurrent.CreationDate.ToUniversalTime() -ne $Identity.CreationDate.ToUniversalTime()) { $taskReason = 'PidReused' }
        if (-not $taskReason) {
            if ($taskOwnedHandle -and $Process) { $Process.Dispose() }
            throw $taskError
        }
        $taskExitCode = $null
        $taskExitUtc = $null
        if ($Process) {
            try {
                if ($Process.HasExited) { $taskExitCode = $Process.ExitCode; $taskExitUtc = $Process.ExitTime.ToUniversalTime().ToString('o') }
            } catch { }
        }
        if ($taskOwnedHandle -and $Process) { $Process.Dispose() }
        return @{ State = 'Unavailable'; Reason = $taskReason; VerifiedUtc = [DateTime]::UtcNow.ToString('o'); ExitCode = $taskExitCode; ExitUtc = $taskExitUtc; Error = (Get-T040MetricsError $taskError $taskStage) }
    }
}

Export-ModuleMember -Function Get-T040MetricsError, Read-T040ProcessCounters
