$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned acceptance Sandbox only' }
$taskSession = Get-Content -LiteralPath 'C:\IrisAcceptanceOutput\active-session.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskOutput = [IO.Path]::GetFullPath($taskSession.SessionDirectory)
if (-not $taskOutput.StartsWith('C:\IrisAcceptanceOutput\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected output path' }
$taskRoot = Join-Path $env:LOCALAPPDATA 'Iris'
$taskMaintenance = Join-Path $env:LOCALAPPDATA 'Iris.Maintenance'
$taskDownloads = Join-Path $env:USERPROFILE 'Downloads'
$taskSourcePath = Join-Path $taskRoot 'current\release-source.json'
$taskIdentity = $null
$taskReadFailure = $null
try { if (Test-Path -LiteralPath $taskSourcePath -PathType Leaf) { $taskIdentity = Get-Content -LiteralPath $taskSourcePath -Raw -Encoding UTF8 | ConvertFrom-Json } }
catch { $taskReadFailure = $_.Exception.GetType().FullName }
$taskProcesses = @(Get-CimInstance Win32_Process | Where-Object {
    ($_.ExecutablePath -and ($_.ExecutablePath.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or $_.ExecutablePath.StartsWith($taskMaintenance + '\', [StringComparison]::OrdinalIgnoreCase))) -or
    ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($taskDownloads + '\', [StringComparison]::OrdinalIgnoreCase) -and $_.Name -like 'Iris-0.2.9-Setup*.exe')
} | ForEach-Object { @{ Pid = $_.ProcessId; ParentPid = $_.ParentProcessId; Name = $_.Name; Path = $_.ExecutablePath; StartedUtc = $_.CreationDate.ToUniversalTime().ToString('o') } })
$taskResult = [ordered]@{ Utc = [DateTime]::UtcNow.ToString('o'); ReadOnlyProductInspection = $true; ProductOperationPerformed = $false; InstalledSource = $taskIdentity; SourceReadFailureType = $taskReadFailure; Processes = $taskProcesses }
$taskPath = Join-Path $taskOutput ('progress-v029-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
[IO.File]::WriteAllText($taskPath, ($taskResult | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskPath
