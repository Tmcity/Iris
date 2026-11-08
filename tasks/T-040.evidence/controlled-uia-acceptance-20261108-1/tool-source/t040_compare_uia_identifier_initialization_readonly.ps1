$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$env:IRIS_T040_UIA_PROBE_MODE = 'IdentifiersOnly'
& (Join-Path $PSScriptRoot 'tools\uia-source-metadata\UiaSourceMetadataProbe.exe')
if ($LASTEXITCODE -ne 0) { throw 'Identifier initialization diagnostic failed; preserve actual state' }
Remove-Item -LiteralPath Env:\IRIS_T040_UIA_PROBE_MODE
