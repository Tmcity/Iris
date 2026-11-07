$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskOut = Join-Path 'C:\IrisT040Output' ('cpu-comparison-stall-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
$null = New-Item -ItemType Directory -Path $taskOut
$taskCompare = Join-Path 'C:\IrisT040Output' 'r2r-cpu-20261107T1911310861124Z'
Copy-Item -LiteralPath (Join-Path $taskCompare 'state.json'), (Join-Path $taskCompare 'launch.json') -Destination $taskOut
$taskState = Get-Content -LiteralPath (Join-Path $taskOut 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskProcesses = @()
foreach ($taskId in @(2432, 2848)) {
    $taskIdentity = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $taskId)
    if (-not $taskIdentity) { $taskProcesses += @{ Pid = $taskId; Present = $false }; continue }
    $taskProcess = [Diagnostics.Process]::GetProcessById($taskId)
    try {
        $taskProcess.Refresh()
        $taskThreads = @($taskProcess.Threads | ForEach-Object {
            $taskThread = $_
            @{ Id = $taskThread.Id; State = $taskThread.ThreadState.ToString()
               WaitReason = $(if ($taskThread.ThreadState -eq [Diagnostics.ThreadState]::Wait) { $taskThread.WaitReason.ToString() } else { $null }) }
        })
        $taskProcesses += @{
            Pid = $taskId; Present = $true; Identity = $taskIdentity | Select-Object ProcessId, CreationDate, ExecutablePath, CommandLine
            CpuSeconds = $taskProcess.TotalProcessorTime.TotalSeconds
            WorkingSetBytes = $taskProcess.WorkingSet64; PrivateBytes = $taskProcess.PrivateMemorySize64
            Threads = $taskThreads
        }
    } finally { $taskProcess.Dispose() }
}
$taskData = [IO.Path]::GetFullPath($taskState.Runs[0].DataDirectory)
$taskRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'IrisT040Diagnostic')) + '\'
if (-not $taskData.StartsWith($taskRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected owned clone directory' }
$taskFiles = @(Get-ChildItem -LiteralPath $taskData -File | Where-Object { $_.Name -like 'startup-*.txt' })
$taskFiles += @(Get-ChildItem -LiteralPath (Join-Path $taskData 'logs') -File -Filter 'iris-*.log')
foreach ($taskFile in $taskFiles) {
    $taskStream = [IO.File]::Open($taskFile.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        $taskCopy = [IO.File]::Create((Join-Path $taskOut $taskFile.Name))
        try { $taskStream.CopyTo($taskCopy) } finally { $taskCopy.Dispose() }
    } finally { $taskStream.Dispose() }
}
$taskResult = @{ Utc = [DateTime]::UtcNow.ToString('o'); Processes = $taskProcesses; TargetSuspended = $false; ProductOperationPerformed = $false }
[IO.File]::WriteAllText((Join-Path $taskOut 'processes.json'), ($taskResult | ConvertTo-Json -Depth 9), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskOut
