$ErrorActionPreference = 'Stop'
$feature = Get-CimInstance Win32_OptionalFeature -Filter "Name='Containers-DisposableClientVM'"
$processes = @(Get-Process -Name WindowsSandbox,WindowsSandboxClient,VmmemWindowsSandbox,dotnet,MSBuild,VBCSCompiler,vpk -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,Path)
$entries = @(Get-Command WindowsSandbox.exe,wsb.exe -ErrorAction Stop | ForEach-Object {
    $item = Get-Item -LiteralPath $_.Source
    [pscustomobject]@{Name=$_.Name;Path=$_.Source;Bytes=$item.Length;Version=$item.VersionInfo.FileVersion}
})
[pscustomobject]@{
    ObservedUtc=[DateTime]::UtcNow.ToString('o')
    UserReportedState='Windows Sandbox enabled; continue the already authorized owned guest verification'
    PowerShellVersion=$PSVersionTable.PSVersion.ToString()
    Feature=[pscustomobject]@{Name=$feature.Name;InstallState=$feature.InstallState}
    Entries=$entries
    RelatedProcesses=$processes
    HostFeatureChangesPerformedByThisScript=$false
} | ConvertTo-Json -Depth 6
& wsb.exe list --raw
if ($LASTEXITCODE -ne 0) { throw ('Sandbox listing failed: ' + $LASTEXITCODE) }
