param([Parameter(Mandatory = $true)][ValidateSet('old-ready','offer','ready','updated','uninstalled')][string]$Stage)
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned acceptance Sandbox only' }
$taskState = Get-Content -LiteralPath 'C:\IrisAcceptanceOutput\active-session.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskSession = [IO.Path]::GetFullPath($taskState.SessionDirectory)
if (-not $taskSession.StartsWith('C:\IrisAcceptanceOutput\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected session path' }
& (Join-Path $PSScriptRoot 'manual-capture-state.ps1') -Stage $Stage
$taskOut = Join-Path $taskSession ($Stage + '-supplement-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
$null = New-Item -ItemType Directory -Path $taskOut

function CopySharedEvidence([string]$taskSource, [string]$taskName) {
    if (-not (Test-Path -LiteralPath $taskSource -PathType Leaf)) { return }
    $taskInput = [IO.File]::Open($taskSource, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        $taskOutput = [IO.File]::Open((Join-Path $taskOut $taskName), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        try { $taskInput.CopyTo($taskOutput) } finally { $taskOutput.Dispose() }
    } finally { $taskInput.Dispose() }
}

foreach ($taskName in @('process-events.jsonl','provider-requests.jsonl','provider-status.json','watcher-status.json','provider-failure.json','active-recorder-v2.json')) { CopySharedEvidence (Join-Path $taskSession $taskName) $taskName }
$taskRecorderPointer = Join-Path $taskSession 'active-recorder-v2.json'
$taskRecorder = if (Test-Path -LiteralPath $taskRecorderPointer) { Get-Content -LiteralPath $taskRecorderPointer -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
if ($taskRecorder) {
    $taskRecorderOut = [IO.Path]::GetFullPath($taskRecorder.OutputDirectory)
    if (-not $taskRecorderOut.StartsWith($taskSession.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Recorder output escaped the active session' }
    foreach ($taskName in @('process-events-v2.jsonl','watcher-v2-status.json')) { CopySharedEvidence (Join-Path $taskRecorderOut $taskName) $taskName }
}
$taskData = Join-Path $env:APPDATA 'Iris'
$taskRoot = Join-Path $env:LOCALAPPDATA 'Iris'
foreach ($taskName in @('history.db','history.db-wal')) { CopySharedEvidence (Join-Path $taskData $taskName) $taskName }
Get-ChildItem -LiteralPath (Join-Path $taskData 'logs') -File -Filter '*.log' -ErrorAction SilentlyContinue | ForEach-Object { if ($_.Length -le 10MB) { CopySharedEvidence $_.FullName ('product-' + $_.Name) } }
$taskSettingsPath = Join-Path $taskData 'settings.json'
$taskSettings = if (Test-Path -LiteralPath $taskSettingsPath) { Get-Content -LiteralPath $taskSettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$taskRegistrations = @(foreach ($taskRegistryRoot in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall','HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
    Get-ChildItem -LiteralPath $taskRegistryRoot -ErrorAction SilentlyContinue | ForEach-Object { Get-ItemProperty -LiteralPath $_.PSPath } | Where-Object { $_.DisplayName -eq 'Iris' } | ForEach-Object { @{ DisplayName = $_.DisplayName; DisplayVersion = $_.DisplayVersion; InstallLocation = $_.InstallLocation; UninstallString = $_.UninstallString; RegistryPath = $_.PSPath } }
})
$taskShortcuts = @(foreach ($taskShortcutRoot in @((Join-Path $env:USERPROFILE 'Desktop'), (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'))) {
    Get-ChildItem -LiteralPath $taskShortcutRoot -File -Recurse -Filter 'Iris*.lnk' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
})
$taskMaintenanceRoot = Join-Path $env:LOCALAPPDATA 'Iris.Maintenance'
$taskMaintenanceFiles = @(Get-ChildItem -LiteralPath $taskMaintenanceRoot -File -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { @{ Path = $_.FullName.Substring($taskMaintenanceRoot.Length).TrimStart('\'); Bytes = $_.Length } })
$taskResult = [ordered]@{
    Stage = $Stage
    Utc = [DateTime]::UtcNow.ToString('o')
    ReadOnlyProductInspection = $true
    ProductRootExists = (Test-Path -LiteralPath $taskRoot)
    ProductCurrentExists = (Test-Path -LiteralPath (Join-Path $taskRoot 'current'))
    UserDataExists = (Test-Path -LiteralPath $taskData)
    CheckForUpdates = if ($taskSettings) { $taskSettings.General.CheckForUpdates } else { $null }
    SyntheticProfiles = @($taskSettings.Translation.Providers | Where-Object { $_.BaseUrl -eq 'http://127.0.0.1:18039/v1' -and $_.Model -eq 'iris-fixture' } | ForEach-Object { @{ Id = $_.Id; DisplayName = $_.DisplayName; Enabled = $_.Enabled; HasCredentialSetReference = -not [string]::IsNullOrWhiteSpace($_.CredentialSetId) } })
    Registrations = $taskRegistrations
    Shortcuts = $taskShortcuts
    MaintenanceFiles = $taskMaintenanceFiles
    V2Recorder = $taskRecorder
    SecretsContentsCollected = $false
}
[IO.File]::WriteAllText((Join-Path $taskOut 'supplement.json'), ($taskResult | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskOut
