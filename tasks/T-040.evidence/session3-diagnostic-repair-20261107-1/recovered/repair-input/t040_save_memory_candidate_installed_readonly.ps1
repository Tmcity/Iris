$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskRoot=Join-Path $env:LOCALAPPDATA 'Iris'
$taskIdentity=Get-Content -LiteralPath (Join-Path $taskRoot 'current\release-source.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if($taskIdentity.Version-ne'0.2.11'){throw 'Expected local memory candidate 0.2.11'}
$taskManifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'LOCAL_0211_payload-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskRows=@()
foreach($taskEntry in $taskManifest){
    $taskRelative='current\'+$taskEntry.Path
    if($taskEntry.Path-eq'Iris_ExecutionStub.exe'){$taskRelative='Iris.exe'}
    elseif($taskEntry.Path-eq'Squirrel.exe'){$taskRelative='Update.exe'}
    $taskFile=[IO.Path]::GetFullPath((Join-Path $taskRoot $taskRelative))
    if(-not$taskFile.StartsWith($taskRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Payload outside owned installation'}
    $taskActual=Get-Item -LiteralPath $taskFile
    $taskHash=(Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash
    $taskRows+=@{Path=$taskEntry.Path;Bytes=$taskActual.Length;Sha256=$taskHash;Matches=($taskActual.Length-eq$taskEntry.Bytes-and$taskHash-eq$taskEntry.Sha256)}
}
$taskOut='C:\IrisT040Output\candidate-installed-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')+'.json'
$taskRecord=@{Utc=[DateTime]::UtcNow.ToString('o');Version=$taskIdentity.Version;SourceCommit='dccc0dbd3054122e34e27842d55c6585ff953812';ExpectedFiles=97;AllFilesMatch=(@($taskRows|Where-Object{-not$_.Matches}).Count-eq0-and$taskRows.Count-eq97);Payload=$taskRows;ProductOperationPerformed=$false;LocalCandidate=$true;PublicRelease=$false}
[IO.File]::WriteAllText($taskOut,($taskRecord|ConvertTo-Json -Depth 7),(New-Object Text.UTF8Encoding($false)))
if(-not$taskRecord.AllFilesMatch){throw 'Local candidate installed payload mismatch'}
& (Join-Path $PSScriptRoot 'tools\state-snapshot\T040StateProbe.exe')
if($LASTEXITCODE-ne0){throw 'Local candidate data snapshot failed'}
& (Join-Path $PSScriptRoot 't040_collect_current_readonly.ps1')
& (Join-Path $PSScriptRoot 'save-metrics-state-v3-readonly.ps1')
Write-Host 'Local memory candidate installed-state evidence saved.'
