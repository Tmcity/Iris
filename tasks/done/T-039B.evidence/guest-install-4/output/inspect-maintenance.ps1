$ErrorActionPreference = 'Stop'
$out = 'C:\IrisBOutput'
foreach ($folder in @((Join-Path $env:LOCALAPPDATA 'Iris.Maintenance'),(Join-Path $env:APPDATA 'Iris'))) {
    Get-ChildItem -LiteralPath $folder -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -eq '.log' -or $_.Name -in @('transaction.json','last-failure.json') } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $out ('diagnose-' + $_.Name)) -ErrorAction SilentlyContinue
    }
}
Get-Process -Id 5044,5664 -ErrorAction SilentlyContinue | Select-Object Id,StartTime,MainWindowHandle,MainWindowTitle,CPU,Responding | ConvertTo-Json | Set-Content (Join-Path $out 'diagnose-process.json')
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$bounds = [Windows.Forms.SystemInformation]::VirtualScreen
$bitmap = New-Object Drawing.Bitmap($bounds.Width,$bounds.Height)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
try { $graphics.CopyFromScreen($bounds.Location,[Drawing.Point]::Empty,$bounds.Size); $bitmap.Save((Join-Path $out 'diagnose-window.png')) }
finally { $graphics.Dispose(); $bitmap.Dispose() }
