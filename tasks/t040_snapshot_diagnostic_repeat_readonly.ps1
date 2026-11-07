$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskOutput = 'C:\IrisT040Output'
$taskIdentity = @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq 'Iris.App.exe' -or
    ($_.Name -eq 'powershell.exe' -and
        ($_.CommandLine -like '*IrisT040*' -or $_.ProcessId -eq 8096))
} | Select-Object ProcessId, ParentProcessId, Name, CreationDate, ExecutablePath, CommandLine)
$taskDetails = @()
foreach ($taskApp in $taskIdentity | Where-Object { $_.Name -eq 'Iris.App.exe' }) {
    $taskProcess = [Diagnostics.Process]::GetProcessById([int]$taskApp.ProcessId)
    try {
        $taskProcess.Refresh()
        $taskDetails += @{
            Pid = $taskProcess.Id
            CreatedUtc = $taskProcess.StartTime.ToUniversalTime().ToString('o')
            ExecutablePath = $taskApp.ExecutablePath
            WorkingSetBytes = $taskProcess.WorkingSet64
            PrivateBytes = $taskProcess.PrivateMemorySize64
            MainWindowHandle = $taskProcess.MainWindowHandle.ToInt64()
            MainWindowTitle = $taskProcess.MainWindowTitle
        }
    } finally { $taskProcess.Dispose() }
}
$taskInstalled = Join-Path $env:LOCALAPPDATA 'Iris\current'
$taskRepeat = Join-Path $taskOutput 'r2r-repeat-20261107T1856449261155Z'
$taskState = Get-Content -LiteralPath (Join-Path $taskRepeat 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskSource = Get-Content -LiteralPath (Join-Path $taskInstalled 'release-source.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskResult = @{
    Utc = [DateTime]::UtcNow.ToString('o')
    PowerShellVersion = $PSVersionTable.PSVersion.ToString()
    Processes = $taskIdentity
    AppDetails = $taskDetails
    RepeatState = $taskState
    RepeatWorkerPid = 8096
    RepeatWorkerCurrentlyPresent = [bool](@($taskIdentity | Where-Object { $_.ProcessId -eq 8096 }).Count)
    InstalledSource = $taskSource
    InstalledExeSha256 = (Get-FileHash -LiteralPath (Join-Path $taskInstalled 'Iris.App.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    InstalledAppDllSha256 = (Get-FileHash -LiteralPath (Join-Path $taskInstalled 'Iris.App.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    ProductOperationPerformed = $false
    InstallerExecuted = $false
}
$taskPath = Join-Path $taskOutput ('repeat-readonly-state-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
[IO.File]::WriteAllText($taskPath, ($taskResult | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskPath
