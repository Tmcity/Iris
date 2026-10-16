$ErrorActionPreference='Stop'
$output='C:\IrisBOutput\remaining-final-1';New-Item -ItemType Directory -Path $output|Out-Null
$report=[ordered]@{Checks=@();Failure=$null}
. C:\IrisBInput\guest_common.ps1
. C:\IrisBInput\guest_dialogs.ps1
try {
    Require 'bad-installer-exit-two' ((Run-Child 'C:\IrisBInput\Iris-bad-checksum-final.exe' '--silent --no-start' 'bad-installer') -eq 2)
    $failure=Get-Content (Join-Path $env:LOCALAPPDATA 'Iris.Maintenance\last-failure.json') -Raw|ConvertFrom-Json
    Record 'checksum-rejection' $failure
    Require 'actual-checksum-rejected' ($failure.Stage -eq 'installer-envelope' -and $failure.Failures[0].Type -eq 'InvalidDataException')
    Require 'bad-installer-no-app' (-not(Test-Path -LiteralPath $app))
    Require 'bad-installer-no-registration' (-not(Test-Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Iris'))
    Require 'bad-installer-no-active-transaction' (-not(Test-Path (Join-Path $env:LOCALAPPDATA 'Iris.Maintenance\transaction.json')))
    $start=New-Object Diagnostics.ProcessStartInfo;$start.FileName=$installer;$start.Arguments='--no-start';$start.UseShellExecute=$false
    $child=[Diagnostics.Process]::Start($start);$null=$child.Handle;$shown=$false;$deadline=[DateTime]::UtcNow.AddSeconds(30)
    try {
        do{foreach($window in [GuestDialog]::ForProcess($child.Id)){$text=[GuestDialog]::Text($window);if($text.Contains('安装 Iris。') -or $text.Contains('Install Iris.')){Record 'installer-confirmation-text' $text;Screen 'installer-cancel' $window;[GuestDialog]::Click($window,2);$shown=$true;break}};if($shown -or $child.HasExited){break};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline)
        Require 'installer-confirmation-visible' $shown;Require 'cancelled-installer-finished' ($child.WaitForExit(10000));$child.WaitForExit();Require 'cancelled-installer-exit-zero' ($child.ExitCode -eq 0)
    }finally{$child.Dispose()}
    Require 'cancelled-install-no-app' (-not(Test-Path -LiteralPath $app))
    Require 'runtime-still-present' (@(Runtimes|Where-Object{$_.Family -eq 'Microsoft.WindowsDesktop.App' -and $_.Version -like '10.*'}).Count -gt 0)
}catch{$report.Failure=$_.ToString()}
finally {
    $report|ConvertTo-Json -Depth 10|Set-Content (Join-Path $output 'result.json') -Encoding UTF8
    $state=@(Get-CimInstance Win32_Process|Where-Object{$_.Name -match 'Iris|Setup|Update'}|Select-Object ProcessId,ParentProcessId,CreationDate,ExecutablePath,CommandLine)
    ConvertTo-Json -InputObject $state -Depth 4|Set-Content (Join-Path $output 'final-processes.json') -Encoding UTF8
}
if($report.Failure){exit 2};exit 0
