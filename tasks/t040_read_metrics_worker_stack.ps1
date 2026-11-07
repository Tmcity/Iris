$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
& (Join-Path $PSScriptRoot 'tools\metrics-worker-stack\MetricsWorkerStack.exe')
if ($LASTEXITCODE -ne 0) { throw 'Read-only worker stack probe failed; retain original output' }
