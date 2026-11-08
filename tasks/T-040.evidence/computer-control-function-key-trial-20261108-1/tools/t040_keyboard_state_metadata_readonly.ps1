$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class T040KeyboardStateMetadata {
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
}
'@
$taskKeys = @{Control=17; LeftControl=162; RightControl=163; Alt=18; LeftAlt=164; RightAlt=165; Shift=16; LeftShift=160; RightShift=161; LeftWindows=91; RightWindows=92; C=67; D=68; F=70; LeftMouse=1; RightMouse=2}
$taskRows = @()
foreach ($taskPair in $taskKeys.GetEnumerator()) { $taskRows += @{ KeyName = $taskPair.Key; Down = ([T040KeyboardStateMetadata]::GetAsyncKeyState($taskPair.Value) -band 32768) -ne 0 } }
$taskRecord = @{ Utc = [DateTime]::UtcNow.ToString('o'); Keys = $taskRows; ProductOperationPerformed = $false; ForegroundInputInjected = $false; UserTextLogged = $false }
$taskPath = Join-Path 'C:\IrisT040Output' ('keyboard-state-metadata-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
[IO.File]::WriteAllText($taskPath, ($taskRecord | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskPath
