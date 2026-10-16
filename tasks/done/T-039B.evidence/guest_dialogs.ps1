Add-Type @'
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class GuestDialog {
 public delegate bool EnumProc(IntPtr h, IntPtr data);
 [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr d);
 [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc f, IntPtr d);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr h,int id);
 [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr h,IntPtr r,IntPtr region,uint flags);
 [DllImport("dwmapi.dll")] static extern int DwmFlush();
 public static IntPtr[] ForProcess(int pid) { var a=new List<IntPtr>();EnumWindows((h,d)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==pid && IsWindowVisible(h))a.Add(h);return true;},IntPtr.Zero);return a.ToArray(); }
 public static string Text(IntPtr h) { var b=new StringBuilder(8192); var s=new StringBuilder(8192);GetWindowText(h,b,b.Capacity);s.AppendLine(b.ToString());EnumChildWindows(h,(c,d)=>{b.Clear();GetWindowText(c,b,b.Capacity);s.AppendLine(b.ToString());return true;},IntPtr.Zero);return s.ToString(); }
 public static void Click(IntPtr h,int id) {var button=GetDlgItem(h,id);if(button==IntPtr.Zero)throw new InvalidOperationException("Expected button is absent");if(!PostMessage(button,0xF5,IntPtr.Zero,IntPtr.Zero))throw new InvalidOperationException("Button dispatch failed");}
 public static void CloseCompleted(IntPtr h) {if(!PostMessage(h,0x10,IntPtr.Zero,IntPtr.Zero))throw new InvalidOperationException("Completion close failed");}
 public static void Paint(IntPtr h) {RedrawWindow(h,IntPtr.Zero,IntPtr.Zero,0x581);DwmFlush();}
}
'@
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
function Screen([string]$name,[IntPtr]$window=[IntPtr]::Zero) {
    if($window -ne [IntPtr]::Zero){[GuestDialog]::Paint($window)}
    $bounds=[Windows.Forms.SystemInformation]::VirtualScreen
    $bitmap=New-Object Drawing.Bitmap($bounds.Width,$bounds.Height);$graphics=[Drawing.Graphics]::FromImage($bitmap)
    try{$graphics.CopyFromScreen($bounds.Location,[Drawing.Point]::Empty,$bounds.Size);$bitmap.Save((Join-Path $output ($name+'.png')))}finally{$graphics.Dispose();$bitmap.Dispose()}
}
$root=Join-Path $env:LOCALAPPDATA 'Iris';$data=Join-Path $env:APPDATA 'Iris';$app=Join-Path $root 'current\Iris.App.exe'
$helper=Join-Path $root 'current\maintenance\Iris.Maintenance.exe';$installer='C:\IrisBInput\Iris-0.2.0-Setup.exe'
function Install([string]$label) {
    Require ($label+'-exit-zero') ((Run-Child $installer '--silent --no-start' $label) -eq 0)
    Require ($label+'-app-exists') (Test-Path -LiteralPath $app)
    Require ($label+'-no-autostart') (@(Get-Process Iris.App -ErrorAction SilentlyContinue).Count -eq 0)
    $key=Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Iris'
    Require ($label+'-final-registration') ($key.UninstallString -like '*maintenance\Iris.Maintenance.exe*')
}
function Language([string]$language) {
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $data 'settings.json'),('{"schemaVersion":1,"general":{"checkForUpdates":false,"uiLanguage":"'+$language+'"}}'))
    [IO.File]::WriteAllText((Join-Path $data 'synthetic-retention.txt'),'Owned guest matrix data')
}
function Uninstall([string]$language,[int]$choice,[string]$label,[bool]$failDelete=$false) {
    Language $language
    $lock=$null
    if($failDelete){$lock=[IO.File]::Open((Join-Path $data 'synthetic-retention.txt'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)}
    $script:maintenanceStarted=[DateTime]::UtcNow;$script:maintenanceOwners=@{};$script:outsideObserved=$false
    $start=New-Object Diagnostics.ProcessStartInfo;$start.FileName=$helper;$start.Arguments='--uninstall';$start.UseShellExecute=$false
    $entry=[Diagnostics.Process]::Start($start);$null=$entry.Handle
    $clicked=@{};$sawChoice=$false;$sawRetry=$false;$deadline=[DateTime]::UtcNow.AddSeconds(120)
    try {
        do {
            Observe-Maintenance
            foreach($owner in @($script:maintenanceOwners.Values)) {
                if($owner.HasExited){continue}
                foreach($window in [GuestDialog]::ForProcess($owner.Id)) {
                    $text=[GuestDialog]::Text($window);$key=[string]$window.ToInt64()+':'+$text
                    if($clicked.ContainsKey($key)){continue}
                    if($text.Contains('Uninstall Iris?') -or $text.Contains('卸载 Iris？')) {
                        Require ($label+'-language') (($language -eq 'en-US' -and $text.Contains('Uninstall Iris?')) -or ($language -eq 'zh-CN' -and $text.Contains('卸载 Iris？')))
                        Record ($label+'-choice-text') $text;Screen ($label+'-choice');$sawChoice=$true;$clicked[$key]=$true;[GuestDialog]::Click($window,$choice)
                    } elseif($text.Contains('Maintenance is incomplete.') -or $text.Contains('维护尚未完成。')) {
                        Require ($label+'-expected-retry') $failDelete
                        Require ($label+'-already-uninstalled-before-delete-retry') (-not (Test-Path -LiteralPath $app))
                        $state=Get-Content (Join-Path $env:LOCALAPPDATA 'Iris.Maintenance\transaction.json') -Raw|ConvertFrom-Json
                        Require ($label+'-native-success-before-delete') ($state.Phase -eq 'data-cleanup' -and $state.WorkerExitCode -eq 0)
                        Record ($label+'-retained-delete-state') $state;Screen ($label+'-retry');$sawRetry=$true
                        $lock.Dispose();$lock=$null;$clicked[$key]=$true;[GuestDialog]::Click($window,4)
                    } elseif($text.Contains('Iris maintenance completed.') -or $text.Contains('Iris 维护已完成。')) {
                        Screen ($label+'-completed') $window;$clicked[$key]=$true;[GuestDialog]::CloseCompleted($window)
                    } else { Record ($label+'-unexpected-window') $text;throw 'Unexpected maintenance dialog' }
                }
            }
            $alive=@($script:maintenanceOwners.Values|Where-Object{-not $_.HasExited})
            if($entry.HasExited -and $script:outsideObserved -and $alive.Count -eq 0){break}
            Start-Sleep -Milliseconds 100
        }while([DateTime]::UtcNow -lt $deadline)
        Require ($label+'-choice-observed') $sawChoice
        Require ($label+'-owner-finished') ($entry.HasExited -and $script:outsideObserved -and $alive.Count -eq 0)
        $entry.WaitForExit();Require ($label+'-entry-exit') ($entry.ExitCode -eq 0)
        foreach($owner in $script:maintenanceOwners.Values){$owner.WaitForExit();Record ($label+'-owner-exit') @{Pid=$owner.Id;Code=$owner.ExitCode};Require ($label+'-exit-'+$owner.Id) ($owner.ExitCode -eq 0)}
        if($choice -eq 2){Require ($label+'-cancel-keeps-app') (Test-Path -LiteralPath $app)}
        else {Require ($label+'-removed-app') (-not(Test-Path -LiteralPath $app));Require ($label+'-removed-registration') (-not(Test-Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Iris'))}
        Require ($label+'-data-choice') ((Test-Path -LiteralPath (Join-Path $data 'synthetic-retention.txt')) -eq ($choice -ne 7))
        Require ($label+'-transaction-ended') (-not(Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Iris.Maintenance\transaction.json')))
        if($failDelete){Require ($label+'-retry-observed') $sawRetry}
    }finally{if($lock){$lock.Dispose()};$entry.Dispose();foreach($owner in $script:maintenanceOwners.Values){$owner.Dispose()}}
}