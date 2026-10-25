<#
Prepared for explicit user approval only. Not run during preparation.
Enables the built-in Windows Sandbox feature, without restarting Windows.
Does not install Iris or change user settings, proxy, certificates or credentials.
#>
$ErrorActionPreference = 'Stop'
$irisIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$irisPrincipal = New-Object Security.Principal.WindowsPrincipal($irisIdentity)
if (-not $irisPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'An administrator-approved process is required. This script does not elevate itself.'
}
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Run with the system Windows PowerShell 5.1 executable.' }
foreach ($irisModule in @('Microsoft.PowerShell.Management','Microsoft.PowerShell.Utility','CimCmdlets')) {
    Import-Module -Name (Join-Path $PSHOME ('Modules\' + $irisModule + '\' + $irisModule + '.psd1')) -ErrorAction Stop
}
$irisRoot = 'F:\Iris\artifacts\t039b\sandbox-enablement'
$irisOutput = Join-Path $irisRoot ([Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $irisOutput
$irisBefore = Get-CimInstance Win32_OptionalFeature -Filter "Name='Containers-DisposableClientVM'" -ErrorAction Stop |
    Select-Object Name,InstallState
$irisBefore | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $irisOutput 'feature-before.json') -Encoding UTF8
$irisDism = Join-Path $env:WINDIR 'System32\dism.exe'
$irisArguments = @('/Online','/Enable-Feature','/FeatureName:Containers-DisposableClientVM','/All','/NoRestart',
    ('/LogPath:' + (Join-Path $irisOutput 'dism.log')))
[pscustomobject]@{Executable=$irisDism;Arguments=$irisArguments;NoAutomaticRestart=$true} |
    ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $irisOutput 'command.json') -Encoding UTF8
$irisProcess = Start-Process -FilePath $irisDism -ArgumentList $irisArguments -PassThru -Wait -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $irisOutput 'stdout.log') -RedirectStandardError (Join-Path $irisOutput 'stderr.log')
$irisCode = $irisProcess.ExitCode
$irisAfter = Get-CimInstance Win32_OptionalFeature -Filter "Name='Containers-DisposableClientVM'" -ErrorAction Stop |
    Select-Object Name,InstallState
[pscustomobject]@{
    FinishedUtc=[DateTime]::UtcNow.ToString('o');ExitCode=$irisCode;Feature=$irisAfter
    RestartRequired=($irisCode -eq 3010);AutomaticRestartIssued=$false
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $irisOutput 'result.json') -Encoding UTF8
Write-Output $irisOutput
exit $irisCode
