$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskInput = [IO.Path]::GetFullPath('C:\IrisT040R2rCandidate')
$taskManifest = Get-Content -LiteralPath (Join-Path $taskInput 'input-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskFiles = @()
foreach ($taskEntry in $taskManifest.Files) {
    $taskPath = [IO.Path]::GetFullPath((Join-Path $taskInput $taskEntry.Path))
    if (-not $taskPath.StartsWith($taskInput + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Input path outside identified mapping' }
    $taskActual = Get-Item -LiteralPath $taskPath
    $taskHash = (Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $taskFiles += @{ Path = $taskEntry.Path; Bytes = $taskActual.Length; Sha256 = $taskHash; Matches = $taskActual.Length -eq $taskEntry.Bytes -and $taskHash -eq $taskEntry.Sha256 }
}
$taskResult = @{ Utc = [DateTime]::UtcNow.ToString('o'); Files = $taskFiles; FileCount = $taskFiles.Count; AllMatch = @($taskFiles | Where-Object { -not $_.Matches }).Count -eq 0; ProductOperationPerformed = $false; InstallerExecuted = $false }
$taskOut = Join-Path 'C:\IrisT040Output' ('r2r-candidate-input-check-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
[IO.File]::WriteAllText($taskOut, ($taskResult | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
if (-not $taskResult.AllMatch -or $taskResult.FileCount -ne 16) { throw 'Actual guest candidate mapping failed byte verification' }
Write-Output $taskOut
