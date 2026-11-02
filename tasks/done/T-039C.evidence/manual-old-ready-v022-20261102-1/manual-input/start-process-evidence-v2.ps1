$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned acceptance Sandbox only' }
$taskState = Get-Content -LiteralPath 'C:\IrisAcceptanceOutput\active-session.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskSession = [IO.Path]::GetFullPath($taskState.SessionDirectory)
if (-not $taskSession.StartsWith('C:\IrisAcceptanceOutput\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected session path' }
$taskPointer = Join-Path $taskSession 'active-recorder-v2.json'
if (Test-Path -LiteralPath $taskPointer) { throw 'V2 recorder already has a launch record; inspect it before starting another' }
$taskSource = Join-Path $PSScriptRoot 'manual-process-watch-v2.ps1'
$taskOut = Join-Path $taskSession ('recorder-v2-' + [Guid]::NewGuid().ToString('N'))
$taskShell = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
$taskProcess = Start-Process -FilePath $taskShell -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$taskSource,'-OutputDirectory',$taskOut) -WindowStyle Hidden -PassThru
$taskResult = [ordered]@{ Utc = [DateTime]::UtcNow.ToString('o'); RecorderPid = $taskProcess.Id; OutputDirectory = $taskOut; SourcePath = $taskSource; SourceSha256 = (Get-FileHash -LiteralPath $taskSource -Algorithm SHA256).Hash.ToLowerInvariant(); Ready = $false; ReadOnlyProductObserver = $true; Failure = $null }
$taskUtf8 = New-Object Text.UTF8Encoding($false)
try {
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(12)
    do {
        Start-Sleep -Milliseconds 150
        $taskStatusPath = Join-Path $taskOut 'watcher-v2-status.json'
        if (Test-Path -LiteralPath $taskStatusPath) {
            try { $taskStatus = Get-Content -LiteralPath $taskStatusPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $taskStatus = $null }
            if ($taskStatus -and $taskStatus.Ready -and $taskStatus.RecorderPid -eq $taskProcess.Id) { $taskResult.Ready = $true; break }
            if ($taskStatus -and $taskStatus.Failure) { throw 'V2 recorder reported a failure' }
        }
        if ($taskProcess.HasExited) { throw ('V2 recorder exited with code ' + $taskProcess.ExitCode) }
    } while ([DateTime]::UtcNow -lt $taskDeadline)
    if (-not $taskResult.Ready) { throw 'V2 recorder readiness timed out' }
} catch {
    $taskResult.Failure = @{ Type = $_.Exception.GetType().FullName; Message = $_.Exception.Message }
} finally {
    [IO.File]::WriteAllText($taskPointer, ($taskResult | ConvertTo-Json -Depth 6), $taskUtf8)
    $taskProcess.Dispose()
}
if (-not $taskResult.Ready) { exit 1 }
