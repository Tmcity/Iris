param([string]$Stage)
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Run only inside the acceptance Sandbox' }
if ([string]::IsNullOrWhiteSpace($Stage)) { $Stage = Read-Host 'Stage name (installed, offer, ready, updated, uninstalled)' }
if ($Stage -notmatch '^[a-z][a-z0-9-]{0,31}$') { throw 'Use a short lowercase stage name' }
$taskState = Get-Content -LiteralPath 'C:\IrisAcceptanceOutput\active-session.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskSession = [IO.Path]::GetFullPath($taskState.SessionDirectory)
if (-not $taskSession.StartsWith('C:\IrisAcceptanceOutput\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected output directory' }
Write-Host 'Bring the Iris view for this stage into view in the Sandbox. Capture begins in 10 seconds.'
Start-Sleep -Seconds 10
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$taskBounds = [Windows.Forms.SystemInformation]::VirtualScreen
$taskImage = New-Object Drawing.Bitmap($taskBounds.Width, $taskBounds.Height)
$taskGraphics = [Drawing.Graphics]::FromImage($taskImage)
$taskStamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
$taskImagePath = Join-Path $taskSession ('view-' + $Stage + '-' + $taskStamp + '.png')
try {
    $taskGraphics.CopyFromScreen($taskBounds.Left, $taskBounds.Top, 0, 0, $taskBounds.Size)
    $taskImage.Save($taskImagePath, [Drawing.Imaging.ImageFormat]::Png)
} finally { $taskGraphics.Dispose(); $taskImage.Dispose() }
$taskRecord = [ordered]@{
    Utc = [DateTime]::UtcNow.ToString('o')
    Stage = $Stage
    UserRequestedPassiveDesktopCapture = $true
    ProductOperationPerformed = $false
    Scope = 'Owned acceptance Sandbox desktop only'
    ImagePath = $taskImagePath
    Width = $taskBounds.Width
    Height = $taskBounds.Height
    Sha256 = (Get-FileHash -LiteralPath $taskImagePath -Algorithm SHA256).Hash.ToLowerInvariant()
}
[IO.File]::WriteAllText(($taskImagePath + '.json'), ($taskRecord | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskImagePath
