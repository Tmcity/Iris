$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned T-040 Sandbox only' }
$taskProcesses = @(Get-CimInstance Win32_Process -ErrorAction Stop)
$taskProductPath = Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskApps = @($taskProcesses | Where-Object { $_.Name -eq 'Iris.App.exe' -and $_.ExecutablePath -eq $taskProductPath } | Select-Object ProcessId, CreationDate, ExecutablePath, CommandLine)
$taskPointer = Get-Content -LiteralPath 'C:\IrisT040Output\active-metrics.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskObserver = @($taskProcesses | Where-Object { $_.ProcessId -eq [int]$taskPointer.ObserverPid } | Select-Object ProcessId, CreationDate, ExecutablePath, CommandLine)
$taskProbeDirectory = Get-ChildItem -LiteralPath 'C:\IrisT040Output' -Directory -Filter 'config-ci-trust-probe-*' | Sort-Object Name | Select-Object -Last 1
$taskProbeLaunch = Get-Content -LiteralPath (Join-Path $taskProbeDirectory.FullName 'launch.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskProbe = @($taskProcesses | Where-Object { $_.ProcessId -eq [int]$taskProbeLaunch.ProbePid } | Select-Object ProcessId, CreationDate, ExecutablePath, CommandLine)
$taskCapi = @($taskProcesses | Where-Object { $_.ProcessId -eq [int]$taskProbeLaunch.CapiPid } | Select-Object ProcessId, CreationDate, ExecutablePath, CommandLine)
$taskState = @{
    Utc=[DateTime]::UtcNow.ToString('o'); ProductProcesses=$taskApps; MetricsObserver=$taskObserver
    MetricsStatus=(Get-Content -LiteralPath (Join-Path $taskPointer.OutputDirectory 'status.json') -Raw -Encoding UTF8 | ConvertFrom-Json)
    OriginalMetrics4016Present=@($taskProcesses | Where-Object { $_.ProcessId -eq 4016 }).Count -gt 0
    OriginalCapi6916Present=@($taskProcesses | Where-Object { $_.ProcessId -eq 6916 }).Count -gt 0
    TrustProbeLaunch=$taskProbeLaunch; TrustProbeProcesses=$taskProbe; TrustCapiProcesses=$taskCapi
    TrustProbeState=(Get-Content -LiteralPath (Join-Path $taskProbeDirectory.FullName 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json)
    TrustCapiState=(Get-Content -LiteralPath (Join-Path $taskProbeLaunch.CapiOutputDirectory 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json)
    ToolSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash; ProductOperationPerformed=$false
}
$taskPath = 'C:\IrisT040Output\session3-helpers-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json'
[IO.File]::WriteAllText($taskPath, ($taskState | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskPath
