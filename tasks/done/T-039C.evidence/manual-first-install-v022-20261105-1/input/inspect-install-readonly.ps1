$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned acceptance Sandbox only' }
$taskState = Get-Content -LiteralPath 'C:\IrisAcceptanceOutput\active-session.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskSession = [IO.Path]::GetFullPath($taskState.SessionDirectory)
if (-not $taskSession.StartsWith('C:\IrisAcceptanceOutput\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected output directory' }
$taskRoot = Join-Path $env:LOCALAPPDATA 'Iris'
$taskPointer = Get-Content -LiteralPath (Join-Path $taskSession 'active-recorder-v2.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskRecorderOut = [IO.Path]::GetFullPath($taskPointer.OutputDirectory)
if (-not $taskRecorderOut.StartsWith($taskSession.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected recorder directory' }
$taskWatcherStatus = Get-Content -LiteralPath (Join-Path $taskRecorderOut 'watcher-v2-status.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskDownloads = @(Get-ChildItem -LiteralPath (Join-Path $env:USERPROFILE 'Downloads') -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(Iris-0\.2\.2-Setup.*\.exe|inst-dotnet-10\.0\.0-X64-WindowsDesktop\.exe)$' } | ForEach-Object {
    @{ Name = $_.Name; Bytes = $_.Length; CreatedUtc = $_.CreationTimeUtc.ToString('o'); LastWriteUtc = $_.LastWriteTimeUtc.ToString('o'); Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$taskProcesses = @(Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -eq $taskState.ProviderPid -or $_.ProcessId -eq $taskPointer.RecorderPid -or ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase)) } | ForEach-Object {
    @{ Pid = $_.ProcessId; ParentPid = $_.ParentProcessId; Name = $_.Name; Path = $_.ExecutablePath; StartedUtc = $_.CreationDate.ToUniversalTime().ToString('o') }
})
$taskCapiLog = Get-WinEvent -ListLog 'Microsoft-Windows-CAPI2/Operational'
$taskCapiEvents = @()
if ($taskCapiLog.IsEnabled -and $taskCapiLog.RecordCount -gt 0) {
    $taskCapiEvents = @(Get-WinEvent -LogName 'Microsoft-Windows-CAPI2/Operational' -MaxEvents 30 | Select-Object Id, LevelDisplayName, TimeCreated, Message)
}
$taskRecord = [ordered]@{
    Utc = [DateTime]::UtcNow.ToString('o')
    ReadOnlyProductInspection = $true
    Downloads = $taskDownloads
    Processes = $taskProcesses
    RecorderVersion = $taskPointer.RecorderVersion
    RecorderSourceSha256 = $taskPointer.SourceSha256
    RecorderStatus = $taskWatcherStatus
    CapiLogEnabled = $taskCapiLog.IsEnabled
    CapiLogRecordCount = $taskCapiLog.RecordCount
    CapiRecentEvents = $taskCapiEvents
    ProviderStatus = (Get-Content -LiteralPath (Join-Path $taskSession 'provider-status.json') -Raw -Encoding UTF8 | ConvertFrom-Json)
    ProductOperationPerformed = $false
}
$taskOutput = Join-Path $taskSession ('install-inspection-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
[IO.File]::WriteAllText($taskOutput, ($taskRecord | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskOutput
