$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1')
$os=Get-CimInstance Win32_OperatingSystem
$report=[ordered]@{
    OS=$os.Caption; Version=$os.Version; Build=$os.BuildNumber
    PowerShell=$PSVersionTable.PSVersion.ToString()
    SandboxAliasExists=(Test-Path -LiteralPath 'C:\Users\28910\AppData\Local\Microsoft\WindowsApps\wsb.exe')
    SandboxSystemExeExists=(Test-Path -LiteralPath 'C:\Windows\System32\WindowsSandbox.exe')
    SandboxCommands=@(Get-Command wsb.exe,WindowsSandbox.exe -ErrorAction SilentlyContinue | Select-Object Name,Source)
    SandboxPackages=@(Get-AppxPackage '*WindowsSandbox*' | Select-Object Name,Version,InstallLocation)
    VirtualizationServices=@(Get-Service vmcompute,hns,vmms -ErrorAction SilentlyContinue | Select-Object Name,Status)
}
try { $report.Feature=Get-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM | Select-Object FeatureName,State }
catch { $report.FeatureInspectionError=$_.Exception.Message }
$report | ConvertTo-Json -Depth 5
if(-not $report.SandboxAliasExists -and -not $report.SandboxSystemExeExists){exit 3}
