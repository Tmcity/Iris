$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned T-040 Sandbox only' }
$taskOutput = 'C:\IrisT040Output'
$taskStamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
$taskDestination = Join-Path $taskOutput ('readonly-state-' + $taskStamp)
$null = New-Item -ItemType Directory -Path $taskDestination
$taskProduct = Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskProcesses = @(Get-CimInstance Win32_Process | Where-Object {
    ($_.Name -eq 'Iris.App.exe' -and $_.ExecutablePath -eq $taskProduct) -or
    ($_.Name -eq 'powershell.exe' -and $_.CommandLine -like '*observe-process-metrics*readonly.ps1*')
} | Select-Object ProcessId,ParentProcessId,Name,CreationDate,ExecutablePath,CommandLine)
$taskDetails = @()
foreach ($taskIdentity in $taskProcesses | Where-Object { $_.Name -eq 'Iris.App.exe' }) {
    $taskProcess = [Diagnostics.Process]::GetProcessById([int]$taskIdentity.ProcessId)
    try {
        $taskProcess.Refresh()
        $taskDetails += [ordered]@{
            Pid = $taskProcess.Id
            WorkingSetBytes = $taskProcess.WorkingSet64
            PrivateBytes = $taskProcess.PrivateMemorySize64
            CpuSeconds = $taskProcess.TotalProcessorTime.TotalSeconds
            HandleCount = $taskProcess.HandleCount
            ThreadCount = $taskProcess.Threads.Count
            MainWindowTitle = $taskProcess.MainWindowTitle
            MainWindowHandle = $taskProcess.MainWindowHandle.ToInt64()
            Modules = @($taskProcess.Modules | ForEach-Object {
                [ordered]@{ Name=$_.ModuleName; Path=$_.FileName; MappedBytes=$_.ModuleMemorySize }
            })
        }
    } finally { $taskProcess.Dispose() }
}
$taskPointer = Get-Content -LiteralPath (Join-Path $taskOutput 'active-metrics.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskMetricsPath = [IO.Path]::GetFullPath($taskPointer.OutputDirectory)
if (-not $taskMetricsPath.StartsWith('C:\IrisT040Output\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected metrics path' }
$taskStatus = Get-Content -LiteralPath (Join-Path $taskMetricsPath 'status.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskSource = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $taskProduct) 'release-source.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskLogs = Join-Path $env:APPDATA 'Iris\logs'
foreach ($taskLog in @(Get-ChildItem -LiteralPath $taskLogs -Filter 'iris-*.log' -File)) {
    $taskStream = [IO.File]::Open($taskLog.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        $taskCopy = [IO.File]::Create((Join-Path $taskDestination $taskLog.Name))
        try { $taskStream.CopyTo($taskCopy) } finally { $taskCopy.Dispose() }
    } finally { $taskStream.Dispose() }
}
$taskResult = [ordered]@{
    Utc = [DateTime]::UtcNow.ToString('o')
    Username = [Environment]::UserName
    ProductOperationPerformed = $false
    Processes = $taskProcesses
    ProductMetrics = $taskDetails
    ObserverPointer = $taskPointer
    ObserverStatus = $taskStatus
    InstalledSource = $taskSource
}
[IO.File]::WriteAllText((Join-Path $taskDestination 'state.json'),($taskResult | ConvertTo-Json -Depth 9),(New-Object Text.UTF8Encoding($false)))
