$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class T040ForegroundIdentity {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
}
'@
$taskWindow = [T040ForegroundIdentity]::GetForegroundWindow()
$taskPid = [uint32]0
$taskThread = [T040ForegroundIdentity]::GetWindowThreadProcessId($taskWindow, [ref]$taskPid)
$taskName = if ($taskPid) { (Get-Process -Id $taskPid).ProcessName } else { $null }
$taskRecord = @{ Utc=[DateTime]::UtcNow.ToString('o'); WindowHandle=$taskWindow.ToInt64(); ProcessId=$taskPid; ThreadId=$taskThread; ProcessName=$taskName; WindowTitleRead=$false; UserTextRead=$false; ForegroundInputInjected=$false; ProductOperationPerformed=$false }
$taskFile = Join-Path 'C:\IrisT040Output' ('foreground-identity-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
[IO.File]::WriteAllText($taskFile, ($taskRecord | ConvertTo-Json -Depth 3), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskFile
