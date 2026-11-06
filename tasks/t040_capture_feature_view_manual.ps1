$ErrorActionPreference='Stop'
if([Environment]::UserName -ne 'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Write-Host 'Switch to the Iris page within 10 seconds. This user-triggered tool captures the Sandbox display.'
Start-Sleep -Seconds 10
$taskBounds=[Windows.Forms.SystemInformation]::VirtualScreen
$taskBitmap=New-Object Drawing.Bitmap($taskBounds.Width,$taskBounds.Height)
try{
    $taskGraphics=[Drawing.Graphics]::FromImage($taskBitmap)
    try{$taskGraphics.CopyFromScreen($taskBounds.Left,$taskBounds.Top,0,0,$taskBounds.Size)}finally{$taskGraphics.Dispose()}
    $taskPath='C:\IrisT040Output\manual-feature-view-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')+'.png'
    $taskBitmap.Save($taskPath,[Drawing.Imaging.ImageFormat]::Png)
    Write-Host $taskPath
}finally{$taskBitmap.Dispose()}
