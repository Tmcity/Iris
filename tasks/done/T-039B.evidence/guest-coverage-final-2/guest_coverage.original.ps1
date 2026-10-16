param([string]$Label='coverage-final-2')
$ErrorActionPreference='Stop'
$output=Join-Path 'C:\IrisBOutput' $Label;New-Item -ItemType Directory -Path $output|Out-Null
$report=[ordered]@{Checks=@();Failure=$null}
. C:\IrisBInput\guest_common.ps1
. C:\IrisBInput\guest_dialogs.ps1
$driver='C:\IrisBInput\update-host-final\Iris.Maintenance.Tests.exe'
$runPath='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$foreign='"C:\IrisBOwnedOther\Other.exe"'
function Language([string]$language) {
    $settingsFile=Join-Path $data 'settings.json'
    $settings=Get-Content -LiteralPath $settingsFile -Raw|ConvertFrom-Json
    if($settings.general.uiLanguage -ne $language){$settings.general.uiLanguage=$language;[IO.File]::WriteAllText($settingsFile,($settings|ConvertTo-Json -Depth 64))}
    if(-not(Test-Path (Join-Path $data 'synthetic-retention.txt'))){[IO.File]::WriteAllText((Join-Path $data 'synthetic-retention.txt'),'Owned coverage data')}
}
function DataVerify([string]$label) { Require $label ((Run-Child $driver ('--data verify '+(Join-Path $output ($label+'.json'))) $label -CloseInput) -eq 0) }
function Shortcuts([bool]$expected,[string]$label) {
    foreach($path in @((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Iris.lnk'),(Join-Path ([Environment]::GetFolderPath('Programs')) 'Iris.lnk'))){Record ($label+'-shortcut') @{Path=$path;Exists=(Test-Path -LiteralPath $path)};Require ($label+'-shortcut-state') ((Test-Path -LiteralPath $path) -eq $expected)}
}
try {
    Install 'coverage-install'
    Require 'seed-real-settings-secrets-history-log' ((Run-Child $driver ('--data seed '+(Join-Path $output 'data-before-keep.json')) 'seed-data' -CloseInput) -eq 0)
    Shortcuts $true 'installed'
    New-Item -Path $runPath -Force|Out-Null
    New-ItemProperty -Path $runPath -Name Iris -Value ('"'+$app+'"') -PropertyType String -Force|Out-Null
    New-ItemProperty -Path $runPath -Name IrisBUnrelatedWitness -Value 'owned unrelated registration' -PropertyType String -Force|Out-Null
    Uninstall 'en-US' 6 'full-data-keep'
    DataVerify 'full-data-readable-after-uninstall'
    Require 'owned-autostart-removed' ($null -eq (Get-ItemProperty $runPath).Iris)
    Require 'other-registration-kept' ((Get-ItemProperty $runPath).IrisBUnrelatedWitness -eq 'owned unrelated registration')
    Shortcuts $false 'uninstalled-keep'
    Install 'coverage-reinstall'
    DataVerify 'full-data-readable-after-reinstall'
    New-ItemProperty -Path $runPath -Name Iris -Value $foreign -PropertyType String -Force|Out-Null
    Uninstall 'zh-CN' 7 'full-data-delete'
    foreach($relative in @('settings.json','secrets.dat','history.db','logs\owned-retention.log')){Require ('deleted-'+$relative) (-not(Test-Path -LiteralPath (Join-Path $data $relative)))}
    Require 'standard-data-directory-deleted' (-not(Test-Path -LiteralPath $data))
    Require 'foreign-autostart-retained' ((Get-ItemProperty $runPath).Iris -eq $foreign)
    Shortcuts $false 'uninstalled-delete'
    # Reject an actual bad outer installer payload without starting its native Setup.
    $bad=Join-Path $output 'bad-installer.exe';Copy-Item -LiteralPath $installer -Destination $bad
    $file=[IO.File]::Open($bad,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{$file.Position=$file.Length-1;$value=$file.ReadByte();$file.Position=$file.Length-1;$file.WriteByte([byte]($value -bxor 1))}finally{$file.Dispose()}
    Require 'bad-installer-exit-two' ((Run-Child $bad '--silent --no-start' 'bad-installer') -eq 2)
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
    if((Get-ItemProperty $runPath -ErrorAction SilentlyContinue).Iris -eq $foreign){Remove-ItemProperty -Path $runPath -Name Iris}
    if((Get-ItemProperty $runPath -ErrorAction SilentlyContinue).IrisBUnrelatedWitness -eq 'owned unrelated registration'){Remove-ItemProperty -Path $runPath -Name IrisBUnrelatedWitness}
    $report|ConvertTo-Json -Depth 10|Set-Content (Join-Path $output 'result.json') -Encoding UTF8
    $state=@(Get-CimInstance Win32_Process|Where-Object{$_.Name -match 'Iris|Setup|Update'}|Select-Object ProcessId,ParentProcessId,CreationDate,ExecutablePath,CommandLine)
    ConvertTo-Json -InputObject $state -Depth 4|Set-Content (Join-Path $output 'final-processes.json') -Encoding UTF8
}
if($report.Failure){exit 2};exit 0
