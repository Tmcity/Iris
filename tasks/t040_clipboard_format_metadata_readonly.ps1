$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class T040ClipboardFormatMetadata {
    [DllImport("user32.dll", SetLastError=true)] public static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError=true)] public static extern uint EnumClipboardFormats(uint previous);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClipboardFormatName(uint format, [Out] char[] text, int count);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
}
'@
$taskRows = @()
if (-not [T040ClipboardFormatMetadata]::OpenClipboard([IntPtr]::Zero)) { throw 'Read-only clipboard format enumeration could not open' }
try {
    $taskFormat = [uint32]0
    while (($taskFormat = [T040ClipboardFormatMetadata]::EnumClipboardFormats($taskFormat)) -ne 0) {
        $taskChars = New-Object char[] 256
        $taskLength = [T040ClipboardFormatMetadata]::GetClipboardFormatName($taskFormat, $taskChars, $taskChars.Length)
        $taskName = if ($taskLength -gt 0) { -join $taskChars[0..($taskLength-1)] } else { $null }
        $taskRows += @{ Id = $taskFormat; RegisteredName = $taskName }
    }
} finally { if (-not [T040ClipboardFormatMetadata]::CloseClipboard()) { throw 'Clipboard metadata handle close failed' } }
$taskRecord = @{ Utc = [DateTime]::UtcNow.ToString('o'); Formats = $taskRows; Sequence = [T040ClipboardFormatMetadata]::GetClipboardSequenceNumber(); ClipboardContentRead = $false; ClipboardChanged = $false; ProductOperationPerformed = $false }
$taskPath = Join-Path 'C:\IrisT040Output' ('clipboard-format-metadata-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
[IO.File]::WriteAllText($taskPath, ($taskRecord | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskPath
