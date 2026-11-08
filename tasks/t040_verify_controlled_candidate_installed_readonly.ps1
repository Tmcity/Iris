$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskExpected = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'candidate-identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskManifestFile = Join-Path $PSScriptRoot 'payload-manifest.json'
if ((Get-FileHash -LiteralPath $taskManifestFile -Algorithm SHA256).Hash -ne $taskExpected.ManifestSha256) { throw 'Reviewed manifest fingerprint changed' }
$taskManifest = Get-Content -LiteralPath $taskManifestFile -Raw -Encoding UTF8 | ConvertFrom-Json
if ($taskManifest.Count -ne 97) { throw 'Expected complete reviewed package' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Iris'))
$taskIdentity = Get-Content -LiteralPath (Join-Path $taskRoot 'current\release-source.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($taskIdentity.Version -ne $taskExpected.Version) { throw 'Installed version differs from reviewed candidate' }
$taskRows = @()
foreach ($taskEntry in $taskManifest) {
    $taskRelative = 'current\' + $taskEntry.Path
    if ($taskEntry.Path -eq 'Iris_ExecutionStub.exe') { $taskRelative = 'Iris.exe' }
    elseif ($taskEntry.Path -eq 'Squirrel.exe') { $taskRelative = 'Update.exe' }
    $taskFile = [IO.Path]::GetFullPath((Join-Path $taskRoot $taskRelative))
    if (-not $taskFile.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload outside owned installation' }
    $taskActual = Get-Item -LiteralPath $taskFile
    $taskHash = (Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash
    $taskRows += @{ Path = $taskEntry.Path; Bytes = $taskActual.Length; Sha256 = $taskHash; Matches = $taskActual.Length -eq $taskEntry.Bytes -and $taskHash -eq $taskEntry.Sha256 }
}
$taskExe = Join-Path $taskRoot 'current\Iris.App.exe'
$taskApps = @(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object { $_.ExecutablePath -eq $taskExe })
if ($taskApps.Count -ne 1) { throw 'Exactly one actual reviewed candidate required' }
$taskRecord = @{ Utc = [DateTime]::UtcNow.ToString('o'); Version = $taskExpected.Version; SourceCommit = $taskExpected.SourceCommit; AppPid = $taskApps[0].ProcessId; AppCreatedUtc = $taskApps[0].CreationDate.ToUniversalTime().ToString('o'); ExpectedFiles = 97; AllFilesMatch = @($taskRows | Where-Object { -not $_.Matches }).Count -eq 0; Payload = $taskRows; ProductOperationPerformed = $false; LocalCandidate = $true; PublicRelease = $false }
$taskPath = Join-Path 'C:\IrisT040Output' ('controlled-candidate-installed-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
[IO.File]::WriteAllText($taskPath, ($taskRecord | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
if (-not $taskRecord.AllFilesMatch) { throw 'Actual installed payload differs from reviewed candidate' }
& (Join-Path $PSScriptRoot 't040_data_snapshot_readonly.ps1')
Write-Output $taskPath
