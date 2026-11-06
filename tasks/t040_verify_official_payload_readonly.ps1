$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned T-040 Sandbox only' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Iris'))
$taskExpected = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'official-0210-payload-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskRows = @()
foreach ($taskEntry in $taskExpected) {
    $taskRelative = "current\" + [string]$taskEntry.Path
    if ($taskEntry.Path -eq "Iris_ExecutionStub.exe") { $taskRelative = "Iris.exe" }
    elseif ($taskEntry.Path -eq "Squirrel.exe") { $taskRelative = "Update.exe" }
    $taskFile = [IO.Path]::GetFullPath((Join-Path $taskRoot $taskRelative))
    if (-not $taskFile.StartsWith($taskRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected payload path' }
    if (-not (Test-Path -LiteralPath $taskFile -PathType Leaf)) { throw ('Missing payload ' + $taskEntry.Path) }
    $taskActual = Get-Item -LiteralPath $taskFile
    $taskHash = (Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash
    $taskRows += [ordered]@{ Path=$taskEntry.Path; Bytes=$taskActual.Length; Sha256=$taskHash; Matches=($taskHash -eq $taskEntry.Sha256 -and $taskActual.Length -eq $taskEntry.Bytes) }
}
$taskRecord = [ordered]@{
    Utc=[DateTime]::UtcNow.ToString('o')
    ProductOperationPerformed=$false
    ExpectedVersion='0.2.10'
    PayloadFiles=$taskRows
    AllFilesMatch=(@($taskRows | Where-Object { -not $_.Matches }).Count -eq 0 -and $taskRows.Count -eq 97)
    RuntimeInfo=@(& 'C:\Program Files\dotnet\dotnet.exe' --list-runtimes)
    ManifestSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'official-0210-payload-manifest.json') -Algorithm SHA256).Hash
}
$taskPath=Join-Path 'C:\IrisT040Output' ('installed-payload-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')+'.json')
[IO.File]::WriteAllText($taskPath,($taskRecord | ConvertTo-Json -Depth 7),(New-Object Text.UTF8Encoding($false)))
if (-not $taskRecord.AllFilesMatch) { throw 'Official installed payload mismatch; evidence saved' }
& (Join-Path $PSScriptRoot 'tools\ocr-capability\OcrCapabilityProbe.exe')
if ($LASTEXITCODE -ne 0) { throw 'OCR capability tool failed; preserve output' }
