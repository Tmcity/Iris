$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskReceipt = Get-ChildItem -LiteralPath 'C:\IrisT040Output' -File -Filter 'r2r-candidate-installed-*.json' | Sort-Object Name | Select-Object -Last 1
if (-not $taskReceipt) { throw 'Reviewed installed receipt is missing' }
& (Join-Path $PSScriptRoot 't040_start_r2r_runtime_observer.ps1') -ReceiptPath $taskReceipt.FullName
