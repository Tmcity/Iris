$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
& (Join-Path $PSScriptRoot 'tools\ocr-capability\OcrCapabilityProbe.exe')
if ($LASTEXITCODE -ne 0) { throw 'Read-only Windows OCR API probe failed' }
$taskCapability = $null
$taskCapabilityFailure = $null
try { $taskCapability = Get-WindowsCapability -Online -Name 'Language.OCR~~~en-US~0.0.1.0' -ErrorAction Stop | Select-Object Name, State }
catch { $taskCapabilityFailure = @{ Type = $_.Exception.GetType().FullName; Message = $_.Exception.Message; Id = $_.FullyQualifiedErrorId } }
$taskRecord = @{ Utc = [DateTime]::UtcNow.ToString('o'); UserName = [Environment]::UserName; OcrCapability = $taskCapability; CapabilityQueryFailure = $taskCapabilityFailure; ProductOperationPerformed = $false; SystemComponentChanged = $false; SourceScope = 'Actual current owned Sandbox, read-only capability status' }
$taskPath = Join-Path 'C:\IrisT040Output' ('ocr-capability-state-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
[IO.File]::WriteAllText($taskPath, ($taskRecord | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskPath
