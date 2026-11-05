$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned acceptance Sandbox only' }
$taskSession = Get-Content -LiteralPath 'C:\IrisAcceptanceOutput\active-session.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskOutput = [IO.Path]::GetFullPath($taskSession.SessionDirectory)
if (-not $taskOutput.StartsWith('C:\IrisAcceptanceOutput\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected output path' }
$taskNames = @('PUBLIC_V029_payload-manifest.json','verify-v029-install-readonly.ps1','Save-Fixed-V029-State.cmd','Capture-Fixed-V029-View.cmd','Iris-0.2.9-Official-Download.url')
$taskRows = @($taskNames | ForEach-Object {
    $taskPath = Join-Path $PSScriptRoot $_
    @{ Name = $_; Exists = (Test-Path -LiteralPath $taskPath -PathType Leaf); Sha256 = (Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$taskResult = [ordered]@{ Utc = [DateTime]::UtcNow.ToString('o'); Root = $PSScriptRoot; ProductOperationPerformed = $false; Rows = $taskRows }
$taskPath = Join-Path $taskOutput ('fixed-v029-input-inspection-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
[IO.File]::WriteAllText($taskPath, ($taskResult | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskPath
if (@($taskRows | Where-Object { -not $_.Exists }).Count -ne 0) { throw 'Missing user-operated verification input' }
