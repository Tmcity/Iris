$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned acceptance Sandbox only' }
$taskSession = Get-Content -LiteralPath 'C:\IrisAcceptanceOutput\active-session.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskOut = [IO.Path]::GetFullPath($taskSession.SessionDirectory)
if (-not $taskOut.StartsWith('C:\IrisAcceptanceOutput\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected output path' }
$taskNames = @('Save-Offer-V0210-State.cmd','Capture-Offer-V0210-View.cmd','save-ui-v0210-evidence.ps1')
$taskRows = @($taskNames | ForEach-Object { $taskPath = Join-Path 'C:\IrisAcceptanceUI0210' $_; @{ Name = $_; Exists = (Test-Path -LiteralPath $taskPath -PathType Leaf); Sha256 = (Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash.ToLowerInvariant() } })
$taskRecord = [ordered]@{ Utc = [DateTime]::UtcNow.ToString('o'); ProductOperationPerformed = $false; InputRoot = 'C:\IrisAcceptanceUI0210'; Rows = $taskRows }
$taskPath = Join-Path $taskOut ('ui-v0210-input-inspection-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
[IO.File]::WriteAllText($taskPath, ($taskRecord | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskPath
if (@($taskRows | Where-Object { -not $_.Exists }).Count -ne 0) { throw 'Missing UI evidence input' }
