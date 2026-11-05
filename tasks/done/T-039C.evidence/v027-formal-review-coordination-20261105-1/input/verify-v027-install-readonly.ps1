$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned acceptance Sandbox only' }
function ReadHash([string]$path) {
    $taskInputStream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $taskHasher = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($taskHasher.ComputeHash($taskInputStream)).Replace('-', '').ToLowerInvariant() }
    finally { $taskHasher.Dispose(); $taskInputStream.Dispose() }
}
$taskState = Get-Content -LiteralPath 'C:\IrisAcceptanceOutput\active-session.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskOut = [IO.Path]::GetFullPath($taskState.SessionDirectory)
if (-not $taskOut.StartsWith('C:\IrisAcceptanceOutput\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected task output path' }
$taskManifestPath = 'C:\IrisAcceptanceInput\PUBLIC_V027_payload-manifest.json'
if ((ReadHash $taskManifestPath) -ne 'ccfc49c983054e4007dad14fe9222219df8ca2cd34230daeeae0b35e620371b3') { throw 'Public payload manifest fingerprint differs' }
$taskManifest = Get-Content -LiteralPath $taskManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$taskRoot = Join-Path $env:LOCALAPPDATA 'Iris'
$taskIdentity = Get-Content -LiteralPath (Join-Path $taskRoot 'current\release-source.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($taskIdentity.Version -ne '0.2.7') { throw 'Installed version is not 0.2.7' }
$taskRows = @()
foreach ($taskEntry in $taskManifest) {
    $taskRelative = 'current\' + $taskEntry.Path
    if ($taskEntry.Path -eq 'Iris_ExecutionStub.exe') { $taskRelative = 'Iris.exe' }
    elseif ($taskEntry.Path -eq 'Squirrel.exe') { $taskRelative = 'Update.exe' }
    $taskPath = [IO.Path]::GetFullPath((Join-Path $taskRoot $taskRelative))
    if (-not $taskPath.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload escaped the installed root' }
    $taskHash = ReadHash $taskPath
    $taskSize = (Get-Item -LiteralPath $taskPath).Length
    $taskRows += @{ PayloadPath = $taskEntry.Path; InstalledRelativePath = $taskRelative; Bytes = $taskSize; Sha256 = $taskHash; Matches = ($taskSize -eq $taskEntry.Bytes -and $taskHash -eq $taskEntry.Sha256.ToLowerInvariant()) }
}
$taskDownloads = @(Get-ChildItem -LiteralPath (Join-Path $env:USERPROFILE 'Downloads') -File -Filter 'Iris-0.2.7-Setup*.exe' | ForEach-Object { @{ Name = $_.Name; Bytes = $_.Length; Sha256 = (ReadHash $_.FullName) } })
$taskHelperProcesses = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'powershell.exe' -and $_.CommandLine -match 'manual-provider.ps1|manual-process-watch-v3.ps1' } | ForEach-Object { @{ Pid = $_.ProcessId; ParentPid = $_.ParentProcessId; Role = if ($_.CommandLine -match 'manual-provider.ps1') { 'SyntheticProvider' } else { 'ProcessRecorder' } } })
$taskResult = [ordered]@{ Utc = [DateTime]::UtcNow.ToString('o'); Version = '0.2.7'; ManifestEntries = $taskManifest.Count; MatchedEntries = @($taskRows | Where-Object { $_.Matches }).Count; Payload = $taskRows; InstallerDownloads = $taskDownloads; HelperProcesses = $taskHelperProcesses; ReadOnlyInspection = $true }
$taskResultPath = Join-Path $taskOut ('public-v027-install-verification-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
[IO.File]::WriteAllText($taskResultPath, ($taskResult | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskResultPath
if ($taskManifest.Count -ne 97 -or $taskResult.MatchedEntries -ne 97) { throw 'Actual public installed payload did not match all 97 entries' }
