$ErrorActionPreference='Stop'
$output='C:\IrisBOutput\committed-failure-final-1';New-Item -ItemType Directory -Path $output|Out-Null
$report=[ordered]@{Checks=@();Failure=$null}
. C:\IrisBInput\guest_common.ps1
. C:\IrisBInput\guest_dialogs.ps1
$driver='C:\IrisBInput\update-host-final\Iris.Maintenance.Tests.exe'
try {
    Install 'failure-baseline-install'
    New-Item -ItemType Directory -Path $data -Force|Out-Null
    [IO.File]::WriteAllText((Join-Path $data 'settings.json'),'{"schemaVersion":1,"general":{"checkForUpdates":false,"uiLanguage":"en-US","showStartupNotification":false}}')
    $start=New-Object Diagnostics.ProcessStartInfo;$start.FileName=$app;$start.Arguments='--action OpenSettings';$start.UseShellExecute=$false
    $old=[Diagnostics.Process]::Start($start);$null=$old.Handle;$deadline=[DateTime]::UtcNow.AddSeconds(30)
    do{$old.Refresh();if($old.MainWindowHandle -ne 0){break};if($old.HasExited){throw 'Old Iris startup failed'};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline)
    Require 'old-app-window' ($old.MainWindowHandle -ne 0)
    $start=New-Object Diagnostics.ProcessStartInfo;$start.FileName=$driver;$start.Arguments='C:\IrisBInput\Iris-0.2.1-full.nupkg '+(Join-Path $output 'engine')+' --native-failure';$start.UseShellExecute=$false
    $owner=[Diagnostics.Process]::Start($start);$null=$owner.Handle;$shown=$false;$deadline=[DateTime]::UtcNow.AddSeconds(90)
    try {
        do {
            foreach($window in [GuestDialog]::ForProcess($owner.Id)) {
                $text=[GuestDialog]::Text($window)
                if($text.Contains('Iris maintenance is incomplete;')) {
                    Record 'actual-committed-failure-text' $text
                    Require 'readable-recovery-route' ($text.Contains('official installer') -and $text.Contains('recovery\Iris.Maintenance.exe'))
                    Require 'original-app-really-exited-before-feedback' $old.HasExited
                    $state=Get-Content (Join-Path $env:LOCALAPPDATA 'Iris.Maintenance\transaction.json') -Raw|ConvertFrom-Json
                    Record 'retained-transaction' $state
                    Require 'native-failure-recorded' ($state.Phase -eq 'verify' -and $state.WorkerExitCode -eq 5)
                    Screen 'committed-failure' $window;[GuestDialog]::CloseCompleted($window);$shown=$true;break
                }
            }
            if($shown -or $owner.HasExited){break};Start-Sleep -Milliseconds 100
        }while([DateTime]::UtcNow -lt $deadline)
        Require 'committed-failure-window-observed' $shown
        Require 'failed-owner-ended' ($owner.WaitForExit(20000));$owner.WaitForExit();Require 'failed-owner-exit-two' ($owner.ExitCode -eq 2)
    }finally{$owner.Dispose();$old.Dispose()}
    Require 'transaction-still-seals-startup' (Test-Path (Join-Path $env:LOCALAPPDATA 'Iris.Maintenance\transaction.json'))
    # The external native boundary was a real owned PowerShell child exiting 5. No failed native Iris replacement is claimed.
    Install 'explicit-install-repair'
    Require 'repair-unseals-startup' (-not(Test-Path (Join-Path $env:LOCALAPPDATA 'Iris.Maintenance\transaction.json')))
}catch{$report.Failure=$_.ToString()}
finally {
    $report|ConvertTo-Json -Depth 10|Set-Content (Join-Path $output 'result.json') -Encoding UTF8
    $state=@(Get-CimInstance Win32_Process|Where-Object{$_.Name -match 'Iris|Setup|Update'}|Select-Object ProcessId,ParentProcessId,CreationDate,ExecutablePath,CommandLine)
    ConvertTo-Json -InputObject $state -Depth 4|Set-Content (Join-Path $output 'final-processes.json') -Encoding UTF8
}
if($report.Failure){exit 2};exit 0
