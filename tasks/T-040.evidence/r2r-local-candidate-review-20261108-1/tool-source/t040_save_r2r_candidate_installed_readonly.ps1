$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Iris'))
$taskManifestPath = Join-Path $PSScriptRoot 'LOCAL_0211_R2R_payload-manifest.json'
$taskManifest = Get-Content -LiteralPath $taskManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$taskSource = Get-Content -LiteralPath (Join-Path $taskRoot 'current\release-source.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($taskSource.Version -ne '0.2.11' -or $taskManifest.Count -ne 97) { throw 'Expected complete local R2R candidate manifest and version' }
$taskRows = @()
foreach ($taskEntry in $taskManifest) {
    $taskRelative = 'current\' + $taskEntry.Path
    if ($taskEntry.Path -eq 'Iris_ExecutionStub.exe') { $taskRelative = 'Iris.exe' }
    elseif ($taskEntry.Path -eq 'Squirrel.exe') { $taskRelative = 'Update.exe' }
    $taskFile = [IO.Path]::GetFullPath((Join-Path $taskRoot $taskRelative))
    if (-not $taskFile.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path outside owned installation' }
    $taskActual = Get-Item -LiteralPath $taskFile
    $taskHash = (Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash
    $taskRows += @{
        Path = $taskEntry.Path; Bytes = $taskActual.Length; Sha256 = $taskHash
        Matches = $taskActual.Length -eq $taskEntry.Bytes -and $taskHash -eq $taskEntry.Sha256
    }
}
$taskExe = Join-Path $taskRoot 'current\Iris.App.exe'
$taskApps = @(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object { $_.ExecutablePath -eq $taskExe })
if ($taskApps.Count -ne 1) { throw 'Exactly one actual installed candidate required' }
$taskResult = @{
    Utc = [DateTime]::UtcNow.ToString('o'); Version = '0.2.11'
    SourceCommit = '90822806f8840c3272dd27f3b2f14abf0f57f2b9'
    ExpectedFiles = 97; AllFilesMatch = @($taskRows | Where-Object { -not $_.Matches }).Count -eq 0
    Payload = $taskRows; AppPid = $taskApps[0].ProcessId
    AppCreatedUtc = $taskApps[0].CreationDate.ToUniversalTime().ToString('o')
    ManifestSha256 = (Get-FileHash -LiteralPath $taskManifestPath -Algorithm SHA256).Hash
    ProductOperationPerformed = $false; LocalCandidate = $true; PublicRelease = $false
}
$taskPath = Join-Path 'C:\IrisT040Output' ('r2r-candidate-installed-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
[IO.File]::WriteAllText($taskPath, ($taskResult | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
if (-not $taskResult.AllFilesMatch) { throw 'Installed R2R candidate mismatch; original comparison retained' }
& (Join-Path $PSScriptRoot 't040_data_snapshot_readonly.ps1')
Write-Output $taskPath
