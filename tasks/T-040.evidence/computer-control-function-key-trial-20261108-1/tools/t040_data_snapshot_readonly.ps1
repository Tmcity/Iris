$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
& (Join-Path $PSScriptRoot 'tools\state-snapshot\T040StateProbe.exe')
if ($LASTEXITCODE -ne 0) { throw 'Read-only synthetic data snapshot failed; preserve original output' }
& (Join-Path $PSScriptRoot 't040_collect_current_readonly.ps1')
