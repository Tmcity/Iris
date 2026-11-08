$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class T040NativeFocusedControlMetadata {
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo { public uint Size, Flags; public IntPtr Active, Focus, Capture, Menu, Move, Caret; public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr window, int index);
    public static IntPtr GetFocus(uint thread) {
        GuiInfo info=new GuiInfo(); info.Size=(uint)Marshal.SizeOf(typeof(GuiInfo));
        if(!GetGUIThreadInfo(thread,ref info)) throw new InvalidOperationException("Owned focus metadata unavailable: "+Marshal.GetLastWin32Error());
        return info.Focus;
    }
}
'@
$taskWindow = [T040NativeFocusedControlMetadata]::GetForegroundWindow()
$taskPid = [uint32]0
$taskThread = [T040NativeFocusedControlMetadata]::GetWindowThreadProcessId($taskWindow, [ref]$taskPid)
$taskProcess = Get-Process -Id $taskPid
if ($taskProcess.ProcessName -ne 'NativeSelectionFixture' -or $taskProcess.MainModule.FileName -ne 'C:\IrisT040R2rRepeat\tools\native-selection-fixture\NativeSelectionFixture.exe') { throw 'Only owned native synthetic scene may be queried' }
$taskFocus = [T040NativeFocusedControlMetadata]::GetFocus($taskThread)
$taskClass = New-Object Text.StringBuilder 128
$taskLength = [T040NativeFocusedControlMetadata]::GetClassName($taskFocus, $taskClass, 128)
if (-not $taskFocus -or $taskLength -le 0) { throw 'Owned focused control metadata unavailable' }
$taskStyle = [T040NativeFocusedControlMetadata]::GetWindowLong($taskFocus, -16)
$taskRecord = @{ Utc=[DateTime]::UtcNow.ToString('o'); SourceWindow=$taskWindow.ToInt64(); SourcePid=$taskPid; FocusedWindow=$taskFocus.ToInt64(); ControlClass=$taskClass.ToString(); NativeStyle=$taskStyle; NativeEditPasswordStyle=$taskClass.ToString() -eq 'Edit' -and ($taskStyle -band 32) -ne 0; TextRead=$false; UiaTextPatternRead=$false; ClipboardRead=$false; ForegroundInputInjected=$false; ProductOperationPerformed=$false }
$taskFile = Join-Path 'C:\IrisT040Output' ('native-password-style-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
[IO.File]::WriteAllText($taskFile, ($taskRecord | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskFile
