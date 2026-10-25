param([string]$Label='update-2',[string]$HostDirectory='update-host-3')
$ErrorActionPreference='Stop'
$output=Join-Path 'C:\IrisBOutput' $Label;New-Item -ItemType Directory -Path $output|Out-Null
$report=[ordered]@{Checks=@();Failure=$null}
. C:\IrisBInput\guest_common.ps1
$root=Join-Path $env:LOCALAPPDATA 'Iris';$app=Join-Path $root 'current\Iris.App.exe';$data=Join-Path $env:APPDATA 'Iris'
try {
    Require 'install-before-update' ((Run-Child 'C:\IrisBInput\Iris-0.2.0-Setup.exe' '--silent --no-start' 'install') -eq 0)
    New-Item -ItemType Directory -Path $data -Force|Out-Null
    [IO.File]::WriteAllText((Join-Path $data 'settings.json'),'{"schemaVersion":1,"general":{"checkForUpdates":false,"uiLanguage":"en-US"}}')
    [IO.File]::WriteAllText((Join-Path $data 'update-retention.txt'),'Owned local update data')
    $driver=Join-Path (Join-Path 'C:\IrisBInput' $HostDirectory) 'Iris.Maintenance.Tests.exe'
    Require 'seed-real-data' ((Run-Child $driver ('--data seed '+(Join-Path $output 'data-before.json')) 'seed-data') -eq 0)
    $start=New-Object Diagnostics.ProcessStartInfo;$start.FileName=$app;$start.Arguments='--action OpenSettings';$start.UseShellExecute=$false
    $old=[Diagnostics.Process]::Start($start);$null=$old.Handle;$deadline=[DateTime]::UtcNow.AddSeconds(30)
    do{$old.Refresh();if($old.HasExited){throw 'Old Iris exited during startup'};if($old.MainWindowHandle -ne 0){break};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline)
    Require 'old-real-settings-visible' ($old.MainWindowHandle -ne 0)
    $code=Run-Child $driver ('C:\IrisBInput\Iris-0.2.1-full.nupkg '+(Join-Path $output 'engine')) 'actual-update' 240000
    Require 'update-host-exit-zero' ($code -eq 0)
    Require 'old-real-process-exited' ($old.WaitForExit(10000));$old.WaitForExit();Require 'old-real-process-exit-zero' ($old.ExitCode -eq 0);$old.Dispose()
    $identity=Get-Content (Join-Path $root 'current\release-source.json') -Raw|ConvertFrom-Json
    Record 'new-identity' $identity;Require 'new-version' ($identity.Version -eq '0.2.1')
    Require 'preserved-data' ([IO.File]::ReadAllText((Join-Path $data 'update-retention.txt')) -eq 'Owned local update data')
    $new=@(Get-Process Iris.App -ErrorAction SilentlyContinue)
    Require 'exactly-one-restart' ($new.Count -eq 1);$new=$new[0];$null=$new.Handle
    Require 'open-updated-settings' ((Run-Child $app '--action OpenSettings' 'updated-settings') -eq 0)
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    do{$new.Refresh();if($new.MainWindowHandle -ne 0){break};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline)
    Require 'updated-window-visible' ($new.MainWindowHandle -ne 0)
    Add-Type -AssemblyName System.Drawing;Add-Type -AssemblyName System.Windows.Forms
    $bounds=[Windows.Forms.SystemInformation]::VirtualScreen;$bitmap=New-Object Drawing.Bitmap($bounds.Width,$bounds.Height);$graphics=[Drawing.Graphics]::FromImage($bitmap)
    try{$graphics.CopyFromScreen($bounds.Location,[Drawing.Point]::Empty,$bounds.Size);$bitmap.Save((Join-Path $output 'updated-settings.png'))}finally{$graphics.Dispose();$bitmap.Dispose()}
    $key=Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Iris'
    Record 'updated-registration' @{UninstallString=$key.UninstallString;QuietUninstallString=$key.QuietUninstallString}
    Require 'maintenance-registration-after-update' ($key.UninstallString -like '*maintenance\Iris.Maintenance.exe*')
    Require 'updated-exit-command' ((Run-Child $app '--action Exit' 'updated-exit') -eq 0)
    Require 'updated-real-process-exited' ($new.WaitForExit(30000));$new.WaitForExit();Require 'updated-real-exit-zero' ($new.ExitCode -eq 0);$new.Dispose()
    Require 'updated-data-readable' ((Run-Child $driver ('--data verify '+(Join-Path $output 'data-after.json')) 'verify-data') -eq 0)
    $workDirs = @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Iris.Maintenance') -Filter 'work-*' -Directory -ErrorAction SilentlyContinue)
    Record 'work-dirs-after-update' @{Count=$workDirs.Count;Paths=@($workDirs | ForEach-Object { $_.Name })}
    Require 'work-directories-bounded' ($workDirs.Count -le 1)
}catch{$report.Failure=$_.ToString()}
finally {
    Get-CimInstance Win32_Process|Where-Object{$_.Name -match 'Iris|Setup|Update'}|Select-Object ProcessId,ParentProcessId,CreationDate,ExecutablePath,CommandLine|ConvertTo-Json -Depth 4|Set-Content (Join-Path $output 'final-processes.json') -Encoding UTF8
    foreach($folder in @((Join-Path $env:LOCALAPPDATA 'Iris.Maintenance'),(Join-Path $env:LOCALAPPDATA 'Velopack'))){Get-ChildItem $folder -Recurse -File -ErrorAction SilentlyContinue|Where-Object{$_.Extension -eq '.log' -or $_.Name -in @('transaction.json','last-failure.json')}|ForEach-Object{Copy-Item $_.FullName (Join-Path $output $_.Name) -ErrorAction SilentlyContinue}}
    $report|ConvertTo-Json -Depth 10|Set-Content (Join-Path $output 'result.json') -Encoding UTF8
}
if($report.Failure){exit 2};exit 0
