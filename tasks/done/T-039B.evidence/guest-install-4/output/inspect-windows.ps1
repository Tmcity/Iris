Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices;
public static class Windows {
 public delegate bool EnumProc(IntPtr window, IntPtr value);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr value);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
}
'@
$results = New-Object Collections.ArrayList
[Windows]::EnumWindows({param($window,$value)
    [uint32]$processId = 0; $null = [Windows]::GetWindowThreadProcessId($window,[ref]$processId)
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($process -and $process.ProcessName -eq 'Iris.App') {
        $text = New-Object Text.StringBuilder 512; $null = [Windows]::GetWindowText($window,$text,512)
        $null = $results.Add(@{Pid=$processId;Handle=$window.ToInt64();Title=$text.ToString();Path=$process.Path})
        if ($processId -ne 5664 -and $text.ToString() -eq 'Iris') { $null = [Windows]::PostMessage($window,0x10,[IntPtr]::Zero,[IntPtr]::Zero) }
    }
    return $true
},[IntPtr]::Zero) | Out-Null
$results | ConvertTo-Json -Depth 4 | Set-Content C:\IrisBOutput\iris-windows.json
