$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned acceptance Sandbox only' }
$taskState = Get-Content -LiteralPath 'C:\IrisAcceptanceOutput\active-session.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskOut = [IO.Path]::GetFullPath($taskState.SessionDirectory)
if (-not $taskOut.StartsWith('C:\IrisAcceptanceOutput\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected session path' }
$taskPrefix = 'IrisLifecycleProbe-' + [Guid]::NewGuid().ToString('N')
$taskStartSource = $taskPrefix + '-start'
$taskStopSource = $taskPrefix + '-stop'
$taskResult = [ordered]@{ Utc = [DateTime]::UtcNow.ToString('o'); Scope = 'Owned hidden cmd.exe only; no product process controlled'; ProbePid = $null; ActualExitCode = $null; Events = @(); Passed = $false; Failure = $null }
$taskChild = $null
try {
    $null = Register-WmiEvent -Query 'SELECT * FROM Win32_ProcessStartTrace' -SourceIdentifier $taskStartSource
    $null = Register-WmiEvent -Query 'SELECT * FROM Win32_ProcessStopTrace' -SourceIdentifier $taskStopSource
    $taskInfo = New-Object Diagnostics.ProcessStartInfo
    $taskInfo.FileName = 'C:\Windows\System32\cmd.exe'
    $taskInfo.Arguments = '/c exit 0'
    $taskInfo.UseShellExecute = $false
    $taskInfo.CreateNoWindow = $true
    $taskChild = [Diagnostics.Process]::Start($taskInfo)
    $taskResult.ProbePid = $taskChild.Id
    if (-not $taskChild.WaitForExit(10000)) { throw 'Owned probe did not exit' }
    $taskResult.ActualExitCode = $taskChild.ExitCode
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(6)
    do {
        $null = Wait-Event -Timeout 1
        foreach ($taskEventItem in @(Get-Event | Where-Object { $_.SourceIdentifier -in @($taskStartSource, $taskStopSource) })) {
            $taskNative = $taskEventItem.SourceEventArgs.NewEvent
            if ([int]$taskNative.ProcessID -eq $taskResult.ProbePid) {
                $taskKind = if ($taskEventItem.SourceIdentifier -eq $taskStartSource) { 'Started' } else { 'Exited' }
                $taskResult.Events += @{ Kind = $taskKind; Pid = [int]$taskNative.ProcessID; Name = $taskNative.ProcessName; NativeEventUtc = [DateTime]::FromFileTimeUtc([int64]$taskNative.TIME_CREATED).ToString('o'); ExitCode = if ($taskKind -eq 'Exited') { [uint32]$taskNative.ExitStatus } else { $null }; ArgsType = $taskEventItem.SourceEventArgs.GetType().FullName }
            }
            Remove-Event -EventIdentifier $taskEventItem.EventIdentifier
        }
        $taskStarted = @($taskResult.Events | Where-Object { $_.Kind -eq 'Started' }).Count -eq 1
        $taskExited = @($taskResult.Events | Where-Object { $_.Kind -eq 'Exited' -and $_.ExitCode -eq 0 }).Count -eq 1
    } while ((-not $taskStarted -or -not $taskExited) -and [DateTime]::UtcNow -lt $taskDeadline)
    $taskResult.Passed = $taskStarted -and $taskExited -and $taskResult.ActualExitCode -eq 0
    if (-not $taskResult.Passed) { throw 'Start/stop trace did not match the owned probe lifecycle' }
} catch {
    $taskResult.Failure = @{ Type = $_.Exception.GetType().FullName; Message = $_.Exception.Message; Position = $_.InvocationInfo.PositionMessage }
} finally {
    Unregister-Event -SourceIdentifier $taskStartSource -ErrorAction SilentlyContinue
    Unregister-Event -SourceIdentifier $taskStopSource -ErrorAction SilentlyContinue
    if ($taskChild) { $taskChild.Dispose() }
    $taskFile = Join-Path $taskOut ('process-lifecycle-probe-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
    [IO.File]::WriteAllText($taskFile, ($taskResult | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
}
if (-not $taskResult.Passed) { exit 1 }
