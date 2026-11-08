$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskExe = Join-Path $PSScriptRoot 'tools\uia-source-metadata\UiaSourceMetadataProbe.exe'
foreach ($taskMode in @('NativeOnly', 'RestoreProxies')) {
    $env:IRIS_T040_UIA_PROBE_MODE = $taskMode
    & $taskExe
    if ($LASTEXITCODE -ne 0) { throw 'Bounded initialization comparison failed; preserve the actual result' }
}
Remove-Item -LiteralPath Env:\IRIS_T040_UIA_PROBE_MODE
