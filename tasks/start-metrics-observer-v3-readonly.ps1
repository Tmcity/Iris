$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned T-040 Sandbox only' }
$taskPointerPath = 'C:\IrisT040Output\active-metrics.json'
$taskExisting = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match 'observe-process-metrics-v[23]-readonly\.ps1' -and $_.CommandLine -like '*C:\IrisT040Output\*' })
if ($taskExisting.Count -gt 0) { throw 'A metrics observer is actually running; inspect it before another start' }
$taskUtf8 = New-Object Text.UTF8Encoding($false)
if (Test-Path -LiteralPath $taskPointerPath) {
    [IO.File]::WriteAllBytes(('C:\IrisT040Output\active-metrics-preserved-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json'), [IO.File]::ReadAllBytes($taskPointerPath))
}
$taskTarget = Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskApps = @(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.Equals($taskTarget,[StringComparison]::OrdinalIgnoreCase) } | Select-Object ProcessId, CreationDate)
$taskOut = Join-Path 'C:\IrisT040Output' ('metrics-v3-' + [Guid]::NewGuid().ToString('N'))
$taskScript = Join-Path $PSScriptRoot 'observe-process-metrics-v3-readonly.ps1'
$taskChild = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$taskScript,'-OutputDirectory',$taskOut) -WindowStyle Hidden -PassThru -RedirectStandardOutput ($taskOut+'.stdout.txt') -RedirectStandardError ($taskOut+'.stderr.txt')
$taskPointer = @{ ObserverPid=$taskChild.Id; ObserverCreatedUtc=$taskChild.StartTime.ToUniversalTime().ToString('o'); StartedUtc=[DateTime]::UtcNow.ToString('o'); OutputDirectory=$taskOut; ObserverScript=$taskScript; SourceSha256=(Get-FileHash -LiteralPath $taskScript -Algorithm SHA256).Hash.ToLowerInvariant(); ExistingAppIdentities=$taskApps; ProductOperationPerformed=$false; OriginalColdStartupCpuRecovered=$false }
[IO.File]::WriteAllText($taskPointerPath, ($taskPointer | ConvertTo-Json -Depth 6), $taskUtf8)
$taskWatch = [Diagnostics.Stopwatch]::StartNew()
do {
    $taskChild.Refresh()
    if ($taskChild.HasExited) { throw ('Metrics observer exited: ' + [IO.File]::ReadAllText($taskOut+'.stderr.txt')) }
    $taskStatusPath = Join-Path $taskOut 'status.json'
    if (Test-Path -LiteralPath $taskStatusPath) {
        $taskStatus = Get-Content -LiteralPath $taskStatusPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($taskStatus.Failure) { throw 'Metrics observer failed; detailed status and stderr were retained' }
        if ($taskStatus.Ready) { Write-Output ('Metrics observer ready: PID '+$taskChild.Id+'; current instance only, no product operation.'); return }
    }
    Start-Sleep -Milliseconds 200
} while ($taskWatch.Elapsed.TotalSeconds -lt 10)
throw 'Metrics observer did not become ready; inspect the existing process before retrying'
