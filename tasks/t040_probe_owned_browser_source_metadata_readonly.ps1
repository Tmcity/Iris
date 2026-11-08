$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
& (Join-Path $PSScriptRoot 'tools\uia-source-metadata\UiaSourceMetadataProbe.exe')
if ($LASTEXITCODE -ne 0) { throw 'Metadata diagnostic failed; preserve its recorded failure' }
