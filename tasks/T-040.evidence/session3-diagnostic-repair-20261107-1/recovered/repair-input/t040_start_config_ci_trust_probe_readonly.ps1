$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned T-040 Sandbox only' }
$taskExisting = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -like '*t040_probe_config_ci_trust_readonly.ps1*' -or $_.CommandLine -like '*t040_capture_live_capi2_readonly.ps1*' })
if ($taskExisting.Count -gt 0) { throw 'A trust or CAPI diagnostic is actually running; inspect it before starting another' }
$taskStamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
$taskOutput = Join-Path 'C:\IrisT040Output' ('config-ci-trust-probe-' + $taskStamp)
$null = New-Item -ItemType Directory -Path $taskOutput
$taskCapturePath = Join-Path $PSScriptRoot 't040_capture_live_capi2_readonly.ps1'
$taskCapture = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$taskCapturePath,'-DurationSeconds','300') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskOutput 'capi-stdout.txt') -RedirectStandardError (Join-Path $taskOutput 'capi-stderr.txt')
$taskReady = $null
$taskWait = [Diagnostics.Stopwatch]::StartNew()
do {
    foreach ($taskDirectory in @(Get-ChildItem -LiteralPath 'C:\IrisT040Output' -Directory -Filter 'live-capi2-*')) {
        $taskStatePath = Join-Path $taskDirectory.FullName 'state.json'
        if (Test-Path -LiteralPath $taskStatePath) {
            $taskCapiState = Get-Content -LiteralPath $taskStatePath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($taskCapiState.ObserverPid -eq $taskCapture.Id -and $taskCapiState.Ready) { $taskReady = $taskDirectory.FullName; break }
        }
    }
    if ($taskReady) { break }
    $taskCapture.Refresh()
    if ($taskCapture.HasExited) { throw 'Live CAPI diagnostic exited; preserve its stderr' }
    Start-Sleep -Milliseconds 200
} while ($taskWait.Elapsed.TotalSeconds -lt 10)
if (-not $taskReady) { throw 'Live CAPI diagnostic is not ready; inspect the existing process before retrying' }
$taskProbePath = Join-Path $PSScriptRoot 't040_probe_config_ci_trust_readonly.ps1'
$taskProbe = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$taskProbePath,'-OutputDirectory',$taskOutput) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskOutput 'stdout.txt') -RedirectStandardError (Join-Path $taskOutput 'stderr.txt')
$taskRecord = @{ Utc=[DateTime]::UtcNow.ToString('o'); ProbePid=$taskProbe.Id; ProbeCreatedUtc=$taskProbe.StartTime.ToUniversalTime().ToString('o'); CapiPid=$taskCapture.Id; CapiOutputDirectory=$taskReady; OutputDirectory=$taskOutput; SourceSha256=(Get-FileHash -LiteralPath $taskProbePath -Algorithm SHA256).Hash; InstallerExecuted=$false; ProductOperationPerformed=$false; SecurityPolicyChanged=$false }
[IO.File]::WriteAllText((Join-Path $taskOutput 'launch.json'),($taskRecord | ConvertTo-Json -Depth 7),(New-Object Text.UTF8Encoding($false)))
Write-Output $taskOutput
