$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Run only inside the acceptance Sandbox' }
$taskRoot = 'C:\IrisAcceptanceOutput'
$taskPointer = Join-Path $taskRoot 'active-session.json'
if (Test-Path -LiteralPath $taskPointer) { throw 'This guest already has an acceptance session. Keep its evidence and use its Save commands.' }
$taskSession = Join-Path $taskRoot ('session-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $taskSession
$taskShell = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
$taskUtf8 = New-Object Text.UTF8Encoding($false)
$taskProvider = $null
$taskState = [ordered]@{
    SessionDirectory = $taskSession
    StartedUtc = [DateTime]::UtcNow.ToString('o')
    ProviderPid = $null
    WatcherPid = $null
    RecorderVersion = 3
    Ready = $false
    Failure = $null
    Scope = 'Owned synthetic provider and read-only process observer; user manually installs and updates production Iris'
}
function SaveSessionState {
    [IO.File]::WriteAllText($script:taskPointer, ($script:taskState | ConvertTo-Json -Depth 6), $script:taskUtf8)
}
try {
    $taskProvider = Start-Process -FilePath $taskShell -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File','C:\IrisAcceptanceInput\manual-provider.ps1','-OutputDirectory',$taskSession) -WindowStyle Hidden -PassThru
    $taskState.ProviderPid = $taskProvider.Id
    SaveSessionState
    $taskLimit = [DateTime]::UtcNow.AddSeconds(15)
    $taskProviderStatus = Join-Path $taskSession 'provider-status.json'
    while (-not (Test-Path -LiteralPath $taskProviderStatus)) {
        if ($taskProvider.HasExited) { throw 'Synthetic provider exited before becoming ready' }
        if ([DateTime]::UtcNow -ge $taskLimit) { throw 'Synthetic provider did not become ready' }
        Start-Sleep -Milliseconds 100
    }
    $taskStatus = Get-Content -LiteralPath $taskProviderStatus -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $taskStatus.Ready -or $taskProvider.HasExited) { throw 'Synthetic provider is unavailable' }
    & (Join-Path $PSScriptRoot 'start-process-watch-v3.ps1')
    $taskRecorder = Get-Content -LiteralPath (Join-Path $taskSession 'active-recorder-v2.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $taskRecorder.Ready -or $taskRecorder.RecorderVersion -ne 3) { throw 'V3 observer did not become ready' }
    $taskState.WatcherPid = $taskRecorder.RecorderPid
    $taskState.Ready = $true
    SaveSessionState
    $taskState | ConvertTo-Json -Depth 6
    Write-Output 'Evidence helpers are ready. Install Iris manually from the official 0.2.2 download link.'
} catch {
    $taskState.Failure = @{ Type = $_.Exception.GetType().FullName; Message = $_.Exception.Message }
    [IO.File]::WriteAllText((Join-Path $taskSession 'stop.request'), 'startup failed', $taskUtf8)
    SaveSessionState
    if ($taskProvider) { $null = $taskProvider.WaitForExit(15000) }
    throw
} finally {
    if ($taskProvider) { $taskProvider.Dispose() }
}
