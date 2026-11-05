param([Parameter(Mandatory = $true)][ValidateSet('offer','ready','updated','uninstalled')][string]$Stage)
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned acceptance Sandbox only' }
$taskSession = Get-Content -LiteralPath 'C:\IrisAcceptanceOutput\active-session.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskOut = [IO.Path]::GetFullPath($taskSession.SessionDirectory)
if (-not $taskOut.StartsWith('C:\IrisAcceptanceOutput\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected output path' }
$taskRecord = [ordered]@{ Utc = [DateTime]::UtcNow.ToString('o'); Stage = $Stage; ProductOperationPerformed = $false; Completed = $false; ErrorType = $null; Message = $null; HResult = $null; ScriptStack = $null }
try { & (Join-Path $PSScriptRoot 'save-update-evidence.ps1') -Stage $Stage; $taskRecord.Completed = $true }
catch { $taskRecord.ErrorType = $_.Exception.GetType().FullName; $taskRecord.Message = $_.Exception.Message; $taskRecord.HResult = $_.Exception.HResult; $taskRecord.ScriptStack = $_.ScriptStackTrace }
$taskPath = Join-Path $taskOut ('ui-v0210-' + $Stage + '-capture-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
[IO.File]::WriteAllText($taskPath, ($taskRecord | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskPath
if (-not $taskRecord.Completed) { Write-Error ('Evidence capture failed: ' + $taskRecord.Message); exit 1 }
