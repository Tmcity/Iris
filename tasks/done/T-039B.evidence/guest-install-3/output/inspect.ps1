$ErrorActionPreference = 'Stop'
$out = 'C:\IrisBOutput'
Get-ChildItem -LiteralPath $env:TEMP -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -eq '.log' } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $out ('inspect-' + $_.Name)) -ErrorAction SilentlyContinue
}
Get-ChildItem -LiteralPath $env:TEMP -File -ErrorAction SilentlyContinue | Select-Object Name,Length,LastWriteTime | ConvertTo-Json | Set-Content (Join-Path $out 'temp-files.json')
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$bounds = [Windows.Forms.SystemInformation]::VirtualScreen
$bitmap = New-Object Drawing.Bitmap($bounds.Width,$bounds.Height)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
try { $graphics.CopyFromScreen($bounds.Location,[Drawing.Point]::Empty,$bounds.Size); $bitmap.Save((Join-Path $out 'diagnostic-screen.png')) }
finally { $graphics.Dispose(); $bitmap.Dispose() }
