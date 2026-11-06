$ErrorActionPreference='Stop'
if([Environment]::UserName -ne 'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
& (Join-Path $PSScriptRoot 'tools\state-snapshot\T040StateProbe.exe')
if($LASTEXITCODE-ne0){throw 'Read-only feature data snapshot failed; keep its output'}
& (Join-Path $PSScriptRoot 'save-metrics-state-v2-readonly.ps1')
& (Join-Path $PSScriptRoot 't040_collect_current_readonly.ps1')
Write-Host 'Feature data, log and process snapshots saved.'
