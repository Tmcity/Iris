$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned acceptance Sandbox only' }
$taskSession = Get-Content -LiteralPath 'C:\IrisAcceptanceOutput\active-session.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskOut = [IO.Path]::GetFullPath($taskSession.SessionDirectory)
if (-not $taskOut.StartsWith('C:\IrisAcceptanceOutput\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected output path' }
$taskNames = @('Save-Ready-V0210-State.cmd','Capture-Ready-V0210-View.cmd','verify-ready-v0210-readonly.ps1')
$taskRows = @($taskNames | ForEach-Object { $taskPath=Join-Path 'C:\IrisAcceptanceDownload0210' $_; @{Name=$_;Exists=(Test-Path -LiteralPath $taskPath -PathType Leaf);Sha256=(Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash.ToLowerInvariant()} })
$taskResult = [ordered]@{Utc=[DateTime]::UtcNow.ToString('o');ProductOperationPerformed=$false;InputRoot='C:\IrisAcceptanceDownload0210';Rows=$taskRows}
$taskPath = Join-Path $taskOut ('download-v0210-input-inspection-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')+'.json')
[IO.File]::WriteAllText($taskPath,($taskResult|ConvertTo-Json -Depth 5),(New-Object Text.UTF8Encoding($false)))
Write-Output $taskPath
