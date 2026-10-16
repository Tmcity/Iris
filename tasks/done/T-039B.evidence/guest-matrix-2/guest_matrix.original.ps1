param([string]$Label='matrix-2')
$ErrorActionPreference='Stop'
$output=Join-Path 'C:\IrisBOutput' $Label
New-Item -ItemType Directory -Path $output | Out-Null
$report=[ordered]@{Checks=@();Failure=$null}
. C:\IrisBInput\guest_common.ps1
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
 public static IntPtr[] ForProcess(int pid) { var a=new List<IntPtr>();EnumWindows((h,d)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==pid && IsWindowVisible(h))a.Add(h);return true;},IntPtr.Zero);return a.ToArray(); }
 public static string Text(IntPtr h) { var b=new StringBuilder(8192); var s=new StringBuilder(8192);GetWindowText(h,b,b.Capacity);s.AppendLine(b.ToString());EnumChildWindows(h,(c,d)=>{b.Clear();GetWindowText(c,b,b.Capacity);s.AppendLine(b.ToString());return true;},IntPtr.Zero);return s.ToString(); }
 public static void Click(IntPtr h,int id) {var button=GetDlgItem(h,id);if(button==IntPtr.Zero)throw new InvalidOperationException("Expected button is absent");if(!PostMessage(button,0xF5,IntPtr.Zero,IntPtr.Zero))throw new InvalidOperationException("Button dispatch failed");}
 public static void CloseCompleted(IntPtr h) {if(!PostMessage(h,0x10,IntPtr.Zero,IntPtr.Zero))throw new InvalidOperationException("Completion close failed");}
}
'@
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
function Screen([string]$name) {
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
                        Screen ($label+'-completed');$clicked[$key]=$true;[GuestDialog]::CloseCompleted($window)
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
try {
    Require 'prior-silent-retention' (Test-Path -LiteralPath (Join-Path $data 'synthetic-retention.txt'))
    Install 'reinstall-retained'
    Require 'reinstall-kept-original-data' (Test-Path -LiteralPath (Join-Path $data 'synthetic-retention.txt'))
    Uninstall 'en-US' 2 'en-cancel';Uninstall 'zh-CN' 2 'zh-cancel'
    Uninstall 'en-US' 6 'en-keep';Install 'reinstall-en-keep'
    Uninstall 'zh-CN' 6 'zh-keep';Install 'reinstall-zh-keep'
    Uninstall 'en-US' 7 'en-delete-retry' $true;Install 'reinstall-deleted'
    Require 'deleted-data-not-restored' (-not(Test-Path -LiteralPath (Join-Path $data 'synthetic-retention.txt')))
    Uninstall 'zh-CN' 7 'zh-delete'
    Require 'shared-runtime-retained' (@(Runtimes|Where-Object{$_.Family -eq 'Microsoft.WindowsDesktop.App' -and $_.Version -like '10.*'}).Count -gt 0)
}catch{$report.Failure=$_.ToString()}
finally {
    Get-CimInstance Win32_Process|Where-Object{$_.Name -match 'Iris|Setup|Update'}|Select-Object ProcessId,ParentProcessId,CreationDate,ExecutablePath,CommandLine|ConvertTo-Json -Depth 4|Set-Content (Join-Path $output 'final-processes.json') -Encoding UTF8
    foreach($folder in @((Join-Path $env:LOCALAPPDATA 'Iris.Maintenance'),(Join-Path $env:LOCALAPPDATA 'Velopack'))){Get-ChildItem $folder -Recurse -File -ErrorAction SilentlyContinue|Where-Object{$_.Extension -eq '.log' -or $_.Name -in @('transaction.json','last-failure.json')}|ForEach-Object{Copy-Item $_.FullName (Join-Path $output $_.Name) -ErrorAction SilentlyContinue}}
    $report|ConvertTo-Json -Depth 10|Set-Content (Join-Path $output 'result.json') -Encoding UTF8
}
if($report.Failure){exit 2};exit 0
