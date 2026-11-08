param([ValidatePattern('^[a-zA-Z0-9-]+$')][string]$Label = 'controlled-ui')
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$taskTarget = Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskApps = @(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object { $_.ExecutablePath -eq $taskTarget })
if ($taskApps.Count -ne 1) { throw 'Exactly one actual owned Iris primary required' }
$taskBounds = [Windows.Forms.SystemInformation]::VirtualScreen
$taskBitmap = New-Object Drawing.Bitmap($taskBounds.Width, $taskBounds.Height)
$taskPath = Join-Path 'C:\IrisT040Output' ($Label + '-view-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.png')
try {
    $taskGraphics = [Drawing.Graphics]::FromImage($taskBitmap)
    try { $taskGraphics.CopyFromScreen($taskBounds.Left, $taskBounds.Top, 0, 0, $taskBounds.Size) }
    finally { $taskGraphics.Dispose() }
    $taskBitmap.Save($taskPath, [Drawing.Imaging.ImageFormat]::Png)
} finally { $taskBitmap.Dispose() }
$taskRecord = @{
    Utc = [DateTime]::UtcNow.ToString('o'); AppPid = $taskApps[0].ProcessId
    AppCreatedUtc = $taskApps[0].CreationDate.ToUniversalTime().ToString('o')
    ScreenshotPath = $taskPath; PixelWidth = $taskBounds.Width; PixelHeight = $taskBounds.Height
    CaptureSource = 'Read-only owned Sandbox display after Computer Use actions'
    ProductOperationPerformed = $false; ForegroundInputInjected = $false
}
[IO.File]::WriteAllText(($taskPath + '.json'), ($taskRecord | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskPath
