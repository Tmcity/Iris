$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Collections.Generic;
public static class IrisT040WindowMetadata {
    public delegate bool WindowCallback(IntPtr window,IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback,IntPtr state);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,StringBuilder text,int length);
    public static string[] VisibleTitles(uint target) {
        var rows=new List<string>();
        EnumWindows(delegate(IntPtr window,IntPtr state) {
            uint pid; GetWindowThreadProcessId(window,out pid);
            if(pid==target && IsWindowVisible(window)) { var text=new StringBuilder(512);GetWindowText(window,text,text.Capacity);rows.Add(text.ToString()); }
            return true;
        },IntPtr.Zero);
        return rows.ToArray();
    }
}
'@
$taskTarget=Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskApps=@(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object {$_.ExecutablePath-eq$taskTarget})
if($taskApps.Count-ne1){throw 'Expected owned Iris instance is absent'}
$taskClipboard=@{Queried=$false;Modified=$false;Length=$null;Sha256=$null;MatchesExpectedSource=$false;MatchesExpectedResult=$false;Failure=$null}
try{
    $taskText=[Windows.Forms.Clipboard]::GetText()
    $taskClipboard.Queried=$true;$taskClipboard.Length=$taskText.Length
    $taskHasher=[Security.Cryptography.SHA256]::Create()
    try{$taskClipboard.Sha256=[BitConverter]::ToString($taskHasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($taskText))).Replace('-','').ToLowerInvariant()}finally{$taskHasher.Dispose()}
    $taskClipboard.MatchesExpectedSource=$taskText-eq'Iris T040 input 01'
    $taskClipboard.MatchesExpectedResult=$taskText-eq'Iris T040 synthetic translation'
}catch{$taskClipboard.Failure=$_.Exception.GetType().FullName}
$taskRecord=@{Utc=[DateTime]::UtcNow.ToString('o');ProductOperationPerformed=$false;AppPid=$taskApps[0].ProcessId;VisibleIrisWindowTitles=[IrisT040WindowMetadata]::VisibleTitles([uint32]$taskApps[0].ProcessId);Clipboard=$taskClipboard;UnmatchedClipboardContentStored=$false;NotepadProcesses=@(Get-CimInstance Win32_Process -Filter "Name='notepad.exe'" | Select-Object ProcessId,ExecutablePath)}
[IO.File]::WriteAllText(('C:\IrisT040Output\stage1-view-metadata-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')+'.json'),($taskRecord | ConvertTo-Json -Depth 7),(New-Object Text.UTF8Encoding($false)))
