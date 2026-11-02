$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned acceptance Sandbox only' }
$taskSource = 'IrisEvidenceProbe-' + [Guid]::NewGuid().ToString('N')
$taskOutput = 'C:\IrisAcceptanceOutput\process-event-probe-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json'
$taskResult = [ordered]@{ Utc = [DateTime]::UtcNow.ToString('o'); Events = @(); ProbePid = $null; Failure = $null }
try {
    $null = Register-WmiEvent -Query 'SELECT * FROM Win32_ProcessStartTrace' -SourceIdentifier $taskSource
    $taskInfo = New-Object Diagnostics.ProcessStartInfo
    $taskInfo.FileName = 'C:\Windows\System32\cmd.exe'
    $taskInfo.Arguments = '/c exit 0'
    $taskInfo.UseShellExecute = $false
    $taskInfo.CreateNoWindow = $true
    $taskChild = [Diagnostics.Process]::Start($taskInfo)
    $taskResult.ProbePid = $taskChild.Id
    $taskChild.WaitForExit(); $taskChild.Dispose()
    $null = Wait-Event -SourceIdentifier $taskSource -Timeout 5
    foreach ($taskEvent in @(Get-Event -SourceIdentifier $taskSource -ErrorAction SilentlyContinue)) {
        $taskNative = $taskEvent.SourceEventArgs.NewEvent
        $taskResult.Events += @{ SourceIdentifier = $taskEvent.SourceIdentifier; ArgsType = $taskEvent.SourceEventArgs.GetType().FullName; ProcessName = $taskNative.ProcessName; ProcessId = $taskNative.ProcessID; NativeType = $taskNative.GetType().FullName }
        Remove-Event -EventIdentifier $taskEvent.EventIdentifier
    }
} catch { $taskResult.Failure = @{ Type = $_.Exception.GetType().Name; Message = $_.Exception.Message; Position = $_.InvocationInfo.PositionMessage } }
finally {
    Unregister-Event -SourceIdentifier $taskSource -ErrorAction SilentlyContinue
    [IO.File]::WriteAllText($taskOutput, ($taskResult | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
}
