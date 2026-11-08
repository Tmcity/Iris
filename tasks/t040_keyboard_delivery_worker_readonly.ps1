param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskResolved = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $taskResolved.StartsWith('C:\IrisT040Output\modifier-delivery-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Owned diagnostic output required' }
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public static class T040KeyboardDeliveryObserver {
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint Vk, Scan, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X,Y; public uint Private; }
    [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr window, uint first, uint last, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    private static IntPtr hook;
    private static HookProc callback;
    public static object[] Observe(string directory) {
        var rows = new List<object>();
        var timer = Stopwatch.StartNew();
        callback = delegate(int code, IntPtr message, IntPtr data) {
            if(code>=0 && data!=IntPtr.Zero) {
                var item=(KeyboardData)Marshal.PtrToStructure(data,typeof(KeyboardData));
                if(item.Vk==17 || item.Vk==18 || item.Vk==162 || item.Vk==163 || item.Vk==164 || item.Vk==165 || item.Vk==68 || item.Vk==70 || item.Vk==79) {
                    rows.Add(new { Utc=DateTime.UtcNow.ToString("o"), ElapsedMilliseconds=timer.Elapsed.TotalMilliseconds, VirtualKey=item.Vk, ScanCode=item.Scan, Flags=item.Flags, Message=message.ToInt64(), Consumed=false });
                }
            }
            return CallNextHookEx(hook,code,message,data);
        };
        hook=SetWindowsHookEx(13,callback,GetModuleHandle(null),0);
        if(hook==IntPtr.Zero) throw new InvalidOperationException("Read-only keyboard hook failed: "+Marshal.GetLastWin32Error());
        File.WriteAllText(Path.Combine(directory,"ready.json"),"{\"Utc\":\""+DateTime.UtcNow.ToString("o")+"\",\"OnlyWatchedModifiersAndDFO\":true,\"ConsumesInput\":false}",new UTF8Encoding(false));
        try {
            while(timer.Elapsed.TotalSeconds<20) {
                Message message;
                while(PeekMessage(out message,IntPtr.Zero,0,0,1)) { TranslateMessage(ref message); DispatchMessage(ref message); }
                Thread.Sleep(1);
            }
        } finally {
            if(!UnhookWindowsHookEx(hook)) throw new InvalidOperationException("Read-only keyboard hook cleanup failed: "+Marshal.GetLastWin32Error());
            hook=IntPtr.Zero;
            GC.KeepAlive(callback);
        }
        return rows.ToArray();
    }
}
'@
$taskStarted = [DateTime]::UtcNow
$taskRows = [T040KeyboardDeliveryObserver]::Observe($taskResolved)
$taskResult = @{ StartedUtc=$taskStarted.ToString('o'); FinishedUtc=[DateTime]::UtcNow.ToString('o'); Pid=$PID; Completed=$true; Events=$taskRows; HookUnhooked=$true; ConsumesInput=$false; ClipboardContentRead=$false; UserTextLogged=$false; ForegroundInputInjected=$false; ProductOperationPerformed=$false }
[IO.File]::WriteAllText((Join-Path $taskResolved 'result.json'), ($taskResult | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
