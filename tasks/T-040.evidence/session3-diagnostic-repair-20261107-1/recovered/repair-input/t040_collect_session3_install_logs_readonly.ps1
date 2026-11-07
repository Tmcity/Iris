$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned T-040 Sandbox only' }
$taskStarted = [DateTime]::UtcNow
$taskDestination = Join-Path 'C:\IrisT040Output' ('session3-install-logs-' + $taskStarted.ToString('yyyyMMddTHHmmssfffffffZ'))
$null = New-Item -ItemType Directory -Path $taskDestination
$taskUtf8 = New-Object Text.UTF8Encoding($false)
$taskFiles = @()
foreach ($taskTempRoot in @($env:TEMP, 'C:\Windows\Temp')) {
    $taskFiles += @(Get-ChildItem -LiteralPath $taskTempRoot -File | Where-Object { $_.Name -match '^(Microsoft_Windows_Desktop_Runtime|dd_(windowsdesktop|dotnet)|velopack)' -and $_.Length -le 10MB })
}
foreach ($taskFolder in @((Join-Path $env:TEMP 'Velopack'), (Join-Path $env:LOCALAPPDATA 'Velopack'), (Join-Path $env:LOCALAPPDATA 'Iris'))) {
    if (Test-Path -LiteralPath $taskFolder -PathType Container) { $taskFiles += @(Get-ChildItem -LiteralPath $taskFolder -File -Recurse -Filter '*.log') }
}
$taskFiles = @($taskFiles | Sort-Object FullName -Unique)
$taskInventory = @()
$taskIndex = 0
foreach ($taskFile in $taskFiles) {
    $taskIndex++
    $taskName = $taskIndex.ToString('000') + '-' + $taskFile.Name
    $taskCopyPath = Join-Path $taskDestination $taskName
    $taskStream = [IO.File]::Open($taskFile.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        $taskCopy = [IO.File]::Create($taskCopyPath)
        try { $taskStream.CopyTo($taskCopy) } finally { $taskCopy.Dispose() }
    } finally { $taskStream.Dispose() }
    $taskInventory += @{ SourcePath=$taskFile.FullName; CopiedName=$taskName; Bytes=(Get-Item -LiteralPath $taskCopyPath).Length; Sha256=(Get-FileHash -LiteralPath $taskCopyPath -Algorithm SHA256).Hash; LastWriteUtc=$taskFile.LastWriteTimeUtc.ToString('o') }
}
$taskStartUtc = '2026-11-07T13:24:00Z'
$taskEndUtc = '2026-11-07T13:36:00Z'
$taskLogRows = @()
foreach ($taskLog in @('System','Application','Microsoft-Windows-AppLocker/MSI and Script','Microsoft-Windows-AppLocker/EXE and DLL','Microsoft-Windows-CodeIntegrity/Operational','Microsoft-Windows-Windows Defender/Operational')) {
    $taskFailure = $null
    $taskEvents = @()
    $taskEnabled = $null
    try {
        $taskInfo = Get-WinEvent -ListLog $taskLog
        $taskEnabled = $taskInfo.IsEnabled
        try { $taskEvents = @(Get-WinEvent -FilterHashtable @{ LogName=$taskLog; StartTime=[DateTimeOffset]::Parse($taskStartUtc).LocalDateTime; EndTime=[DateTimeOffset]::Parse($taskEndUtc).LocalDateTime } -MaxEvents 5000 -ErrorAction Stop) }
        catch { if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') { throw } }
    } catch { $taskFailure = @{ Type=$_.Exception.GetType().FullName; Id=$_.FullyQualifiedErrorId; Message=$_.Exception.Message } }
    $taskName = $taskLog.Replace('/','-').Replace(' ','_') + '.xml'
    [IO.File]::WriteAllText((Join-Path $taskDestination $taskName),('<Events>' + [String]::Join([Environment]::NewLine,[string[]]@($taskEvents | ForEach-Object { $_.ToXml() })) + '</Events>'),$taskUtf8)
    $taskLogRows += @{ LogName=$taskLog; Enabled=$taskEnabled; Events=$taskEvents.Count; MayBeTruncated=$taskEvents.Count -ge 5000; Failure=$taskFailure }
}
$taskTrustRegistry = @()
foreach ($taskStage in @('Initialization','Message','Signature','Certificate','CertificateCheckPolicy','FinalPolicy','DiagnosticPolicy','Cleanup')) {
    $taskPath = 'HKLM:\SOFTWARE\Microsoft\Cryptography\Providers\Trust\' + $taskStage + '\{6078065B-8F22-4B13-BD9B-5B762776F386}'
    if (Test-Path -LiteralPath $taskPath) {
        $taskKey = Get-Item -LiteralPath $taskPath
        $taskValues = @{}
        foreach ($taskName in $taskKey.GetValueNames()) { $taskValues[$taskName] = $taskKey.GetValue($taskName) }
        $taskTrustRegistry += @{ Stage=$taskStage; Path=$taskPath; Values=$taskValues }
    }
}
$taskProcesses = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -in @('msiexec.exe','Iris-0.2.11-Setup.exe','Iris.App.exe') } | Select-Object ProcessId, Name, ExecutablePath, CreationDate)
$taskServices = @(Get-CimInstance Win32_Service | Where-Object { $_.Name -in @('AppIDSvc','CryptSvc','msiserver','WinDefend') } | Select-Object Name,State,StartMode,ProcessId)
$taskRecord = @{ Utc=[DateTime]::UtcNow.ToString('o'); PsVersion=$PSVersionTable.PSVersion.ToString(); ToolSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash; StartUtc=$taskStartUtc; EndUtc=$taskEndUtc; Logs=$taskInventory; EventLogs=$taskLogRows; TrustProviderRegistration=$taskTrustRegistry; CurrentProcesses=$taskProcesses; CurrentServices=$taskServices; RuntimeInfo=@(& 'C:\Program Files\dotnet\dotnet.exe' --list-runtimes); ProductOperationPerformed=$false; InstallerExecuted=$false; SecurityPolicyChanged=$false }
[IO.File]::WriteAllText((Join-Path $taskDestination 'state.json'),($taskRecord | ConvertTo-Json -Depth 9),$taskUtf8)
Write-Output $taskDestination
