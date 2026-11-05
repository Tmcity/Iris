$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned acceptance Sandbox only' }
$taskState = Get-Content -LiteralPath 'C:\IrisAcceptanceOutput\active-session.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskOut = [IO.Path]::GetFullPath($taskState.SessionDirectory)
if (-not $taskOut.StartsWith('C:\IrisAcceptanceOutput\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected session path' }
$taskStamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
$taskEvidence = Join-Path $taskOut ('old-ready-readonly-' + $taskStamp)
$null = New-Item -ItemType Directory -Path $taskEvidence
$taskUtf8 = New-Object Text.UTF8Encoding($false)

function CopySharedReadOnly([string]$taskSource, [string]$taskDestination) {
    $taskInput = [IO.File]::Open($taskSource, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        $taskOutput = [IO.File]::Open($taskDestination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try { $taskInput.CopyTo($taskOutput) } finally { $taskOutput.Dispose() }
    } finally { $taskInput.Dispose() }
}

$taskData = Join-Path $env:APPDATA 'Iris'
$taskRoot = Join-Path $env:LOCALAPPDATA 'Iris'
$taskSettingsPath = Join-Path $taskData 'settings.json'
$taskSettings = Get-Content -LiteralPath $taskSettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$taskProfiles = @($taskSettings.Translation.Providers | Where-Object { $_.BaseUrl -eq 'http://127.0.0.1:18039/v1' -and $_.Model -eq 'iris-fixture' } | ForEach-Object {
    @{ Id = $_.Id; Enabled = $_.Enabled; Kind = $_.Kind; DisplayName = $_.DisplayName; BaseUrl = $_.BaseUrl; Model = $_.Model; HasCredentialSetReference = -not [string]::IsNullOrWhiteSpace($_.CredentialSetId) }
})
$taskProcesses = @(Get-CimInstance Win32_Process | Where-Object {
    ($_.ExecutablePath -and ($_.ExecutablePath.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or $_.ExecutablePath.StartsWith((Join-Path $env:LOCALAPPDATA 'Iris.Maintenance') + '\', [StringComparison]::OrdinalIgnoreCase))) -or
    ($_.ProcessId -eq $taskState.ProviderPid) -or ($_.ProcessId -eq $taskState.WatcherPid)
} | ForEach-Object {
    @{ Pid = $_.ProcessId; ParentPid = $_.ParentProcessId; Name = $_.Name; Path = $_.ExecutablePath; CreatedUtc = $_.CreationDate.ToUniversalTime().ToString('o'); Role = if ($_.ProcessId -eq $taskState.ProviderPid) { 'SyntheticProvider' } elseif ($_.ProcessId -eq $taskState.WatcherPid) { 'ProcessRecorder' } else { 'InstalledProduct' } }
})
$taskCopied = @()
foreach ($taskName in @('process-events.jsonl','provider-requests.jsonl','watcher-status.json','provider-status.json','provider-failure.json')) {
    $taskPath = Join-Path $taskOut $taskName
    if (Test-Path -LiteralPath $taskPath) {
        CopySharedReadOnly $taskPath (Join-Path $taskEvidence $taskName)
        $taskCopied += @{ Name = $taskName; Bytes = (Get-Item -LiteralPath (Join-Path $taskEvidence $taskName)).Length }
    }
}
foreach ($taskName in @('history.db','history.db-wal')) {
    $taskPath = Join-Path $taskData $taskName
    if (Test-Path -LiteralPath $taskPath) { CopySharedReadOnly $taskPath (Join-Path $taskEvidence $taskName) }
}
Get-ChildItem -LiteralPath (Join-Path $taskData 'logs') -File -Filter '*.log' -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.Length -le 10MB) { CopySharedReadOnly $_.FullName (Join-Path $taskEvidence ('product-' + $_.Name)) }
}
$taskSourcePath = Join-Path $taskRoot 'current\release-source.json'
$taskResult = [ordered]@{
    Utc = [DateTime]::UtcNow.ToString('o')
    ReadOnlyProductInspection = $true
    SessionDirectory = $taskOut
    ProductSource = (Get-Content -LiteralPath $taskSourcePath -Raw -Encoding UTF8 | ConvertFrom-Json)
    CheckForUpdates = $taskSettings.General.CheckForUpdates
    ProviderCount = @($taskSettings.Translation.Providers).Count
    SyntheticProfiles = $taskProfiles
    SettingsSha256 = (Get-FileHash -LiteralPath $taskSettingsPath -Algorithm SHA256).Hash.ToLowerInvariant()
    SecretsSha256 = (Get-FileHash -LiteralPath (Join-Path $taskData 'secrets.dat') -Algorithm SHA256).Hash.ToLowerInvariant()
    Processes = $taskProcesses
    RecorderFiles = $taskCopied
    EvidenceDirectory = $taskEvidence
    Failure = $null
}
[IO.File]::WriteAllText((Join-Path $taskEvidence 'inspection.json'), ($taskResult | ConvertTo-Json -Depth 8), $taskUtf8)
Write-Output $taskEvidence
