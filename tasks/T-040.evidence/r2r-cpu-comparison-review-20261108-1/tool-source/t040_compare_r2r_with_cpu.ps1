param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskOut = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $taskOut.StartsWith('C:\IrisT040Output\r2r-cpu-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected output directory' }
$taskUtf8 = New-Object Text.UTF8Encoding($false)
$taskInstalled = Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskSettings = Join-Path $env:APPDATA 'Iris\settings.json'
$taskSecrets = Join-Path $env:APPDATA 'Iris\secrets.dat'
$taskState = @{
    StartedUtc = [DateTime]::UtcNow.ToString('o'); WorkerPid = $PID
    Runs = @(); Completed = $false; Failure = $null; RestoredPid = $null
    DiagnosticOnly = $true; FormalNfrResult = $false; ProductOperationPerformed = $true
    InstallerExecuted = $false; GcRequested = $false; WorkingSetTrimmed = $false
}
function Save-T040Comparison {
    [IO.File]::WriteAllText((Join-Path $script:taskOut 'state.json'), ($script:taskState | ConvertTo-Json -Depth 8), $script:taskUtf8)
}
function Exit-T040Diagnostic([string]$Exe, [Diagnostics.Process]$Primary) {
    $taskCli = Start-Process -FilePath $Exe -ArgumentList @('--action', 'Exit') -WindowStyle Hidden -PassThru
    try {
        $null = $taskCli.Handle
        if (-not $taskCli.WaitForExit(15000) -or $taskCli.ExitCode -ne 0) { throw 'Exact Exit CLI remains alive or failed' }
    } finally { $taskCli.Dispose() }
    if (-not $Primary.WaitForExit(15000)) { throw 'Primary remains alive; no replacement started' }
    if ($Primary.ExitCode -ne 0) { throw 'Primary did not exit normally' }
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class T040ComparisonCpu {
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}
'@
function Read-T040Cpu {
    [long]$taskIdle = 0; [long]$taskKernel = 0; [long]$taskUser = 0
    if (-not [T040ComparisonCpu]::GetSystemTimes([ref]$taskIdle, [ref]$taskKernel, [ref]$taskUser)) { throw 'GetSystemTimes failed' }
    return @{ Idle = $taskIdle; Kernel = $taskKernel; User = $taskUser; Utc = [DateTime]::UtcNow.ToString('o') }
}
function Get-T040CpuInterval($Previous, $Current) {
    $taskTotal = ($Current.Kernel - $Previous.Kernel) + ($Current.User - $Previous.User)
    if ($taskTotal -le 0) { throw 'Invalid CPU counter interval' }
    return @{
        FromUtc = $Previous.Utc; ToUtc = $Current.Utc
        SystemCpuPercentage = 100.0 * ($taskTotal - ($Current.Idle - $Previous.Idle)) / $taskTotal
        TotalTicks = $taskTotal; IdleTicks = $Current.Idle - $Previous.Idle
    }
}
function Read-T040PlainSharedLog([string]$Path) {
    $taskStream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $taskReader = New-Object IO.StreamReader($taskStream, [Text.Encoding]::UTF8)
    try { return $taskReader.ReadToEnd().Split([char]10) }
    finally { $taskReader.Dispose() }
}
Save-T040Comparison
try {
    $taskApps = @(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'")
    if ($taskApps.Count -ne 1 -or $taskApps[0].ExecutablePath -ne $taskInstalled) { throw 'Exactly one verified installed candidate required' }
    if ((Get-FileHash -LiteralPath $taskInstalled -Algorithm SHA256).Hash.ToLowerInvariant() -ne '8ae2e8ac75db29a06eedd5300a99c74d801a29ee307c2f64c924cf5e246d6eb7') { throw 'Installed candidate changed' }
    $taskState.SettingsBeforeSha256 = (Get-FileHash -LiteralPath $taskSettings -Algorithm SHA256).Hash
    $taskState.SecretsBeforeSha256 = (Get-FileHash -LiteralPath $taskSecrets -Algorithm SHA256).Hash
    $taskOriginal = [Diagnostics.Process]::GetProcessById([int]$taskApps[0].ProcessId)
    try {
        $null = $taskOriginal.Handle
        if ([Math]::Abs(($taskOriginal.StartTime.ToUniversalTime() - $taskApps[0].CreationDate.ToUniversalTime()).TotalMilliseconds) -gt 1) { throw 'Original process identity changed' }
        $taskState.OriginalPid = $taskOriginal.Id; $taskState.OriginalCreatedUtc = $taskOriginal.StartTime.ToUniversalTime().ToString('o')
        Exit-T040Diagnostic $taskInstalled $taskOriginal
        $taskState.OriginalExitCode = $taskOriginal.ExitCode
    } finally { $taskOriginal.Dispose() }
    Save-T040Comparison
    foreach ($taskKind in @('baseline', 'r2r', 'r2r', 'baseline')) {
        if (Test-Path -LiteralPath (Join-Path $taskOut 'stop.request')) { throw 'Owned comparison cancellation requested' }
        $taskFolder = Join-Path (Join-Path $env:LOCALAPPDATA 'IrisT040Diagnostic') ([Guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $taskFolder
        $taskRelative = if ($taskKind -eq 'r2r') { 'tools\startup-diagnostic-r2r' } else { 'tools\startup-diagnostic-prehost' }
        Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $taskRelative) | Copy-Item -Destination $taskFolder -Recurse
        [IO.File]::WriteAllText((Join-Path $taskFolder 'portable.txt'), '', $taskUtf8)
        $taskData = Join-Path $taskFolder 'data'; $null = New-Item -ItemType Directory -Path $taskData
        Copy-Item -LiteralPath $taskSettings -Destination (Join-Path $taskData 'settings.json')
        Copy-Item -LiteralPath $taskSecrets -Destination (Join-Path $taskData 'secrets.dat')
        $taskExe = Join-Path $taskFolder 'Iris.App.exe'
        $taskRecord = @{
            Kind = $taskKind; DataDirectory = $taskData; ExecutablePath = $taskExe
            CpuSamples = @(); PrelaunchCpuSamples = @(); StartupCpuConditionProved = $false
            ExitCode = $null; PostFullGcSteadyStateProved = $false
        }
        $taskState.Runs += $taskRecord; Save-T040Comparison
        $taskPrevious = Read-T040Cpu
        1..5 | ForEach-Object {
            Start-Sleep -Milliseconds 200
            $taskCurrent = Read-T040Cpu
            $taskRecord.PrelaunchCpuSamples += Get-T040CpuInterval $taskPrevious $taskCurrent
            $taskPrevious = $taskCurrent
        }
        if (@($taskRecord.PrelaunchCpuSamples | Where-Object { $_.SystemCpuPercentage -ge 30 }).Count) { throw 'Prelaunch CPU condition not met; preserve this bounded attempt' }
        $taskRecord.LaunchRequestedUtc = [DateTime]::UtcNow.ToString('o')
        $taskPrevious = Read-T040Cpu
        $taskProcess = Start-Process -FilePath $taskExe -WindowStyle Hidden -PassThru
        $null = $taskProcess.Handle
        $taskRecord.Pid = $taskProcess.Id; $taskRecord.CreatedUtc = $taskProcess.StartTime.ToUniversalTime().ToString('o')
        Save-T040Comparison
        $taskDeadline = [DateTime]::UtcNow.AddSeconds(8); $taskReady = $false
        do {
            if (Test-Path -LiteralPath (Join-Path $taskOut 'stop.request')) { throw 'Owned comparison cancellation requested' }
            Start-Sleep -Milliseconds 100
            $taskCurrent = Read-T040Cpu
            $taskRecord.CpuSamples += Get-T040CpuInterval $taskPrevious $taskCurrent
            $taskPrevious = $taskCurrent
            foreach ($taskLog in @(Get-ChildItem -LiteralPath (Join-Path $taskData 'logs') -File -Filter 'iris-*.log' -ErrorAction SilentlyContinue)) {
                # Get-Content decorates strings with PSDrive/PSProvider metadata on PS5.1.
                # Serializing that object graph is unbounded. Read ordinary strings instead.
                foreach ($taskLine in (Read-T040PlainSharedLog $taskLog.FullName)) {
                    if ($taskLine -like '*AppBootstrapper*ms*') { $taskRecord.ReadyLogLine = $taskLine; $taskReady = $true; break }
                }
            }
        } while (-not $taskReady -and [DateTime]::UtcNow -lt $taskDeadline)
        $taskRecord.ReadyDetectedUtc = [DateTime]::UtcNow.ToString('o')
        $taskRecord.StartupCpuConditionProved = @($taskRecord.CpuSamples | Where-Object { $_.SystemCpuPercentage -ge 30 }).Count -eq 0
        Save-T040Comparison
        if (-not $taskReady) { throw 'Diagnostic startup did not finish; actual process retained' }
        Start-Sleep -Seconds 35
        $taskProcess.Refresh()
        $taskRecord.SampleUtc = [DateTime]::UtcNow.ToString('o')
        $taskRecord.WorkingSetBytes = $taskProcess.WorkingSet64; $taskRecord.PrivateBytes = $taskProcess.PrivateMemorySize64
        $taskCapture = Join-Path $taskOut ($taskState.Runs.Count.ToString('00') + '-' + $taskKind)
        $null = New-Item -ItemType Directory -Path $taskCapture
        foreach ($taskName in @('startup-phases.txt', 'startup-prehost.txt')) { Copy-Item -LiteralPath (Join-Path $taskData $taskName) -Destination (Join-Path $taskCapture $taskName) }
        foreach ($taskLog in @(Get-ChildItem -LiteralPath (Join-Path $taskData 'logs') -File -Filter 'iris-*.log')) { Copy-Item -LiteralPath $taskLog.FullName -Destination (Join-Path $taskCapture $taskLog.Name) }
        Save-T040Comparison
        Exit-T040Diagnostic $taskExe $taskProcess
        $taskRecord.ExitCode = $taskProcess.ExitCode; $taskProcess.Dispose()
        Save-T040Comparison
    }
} catch {
    $taskState.Failure = @{ Type = $_.Exception.GetType().FullName; Message = $_.Exception.Message; Position = $_.InvocationInfo.PositionMessage }
} finally {
    $taskRemaining = @(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'")
    $taskState.RemainingAppProcesses = @($taskRemaining | Select-Object ProcessId, CreationDate, ExecutablePath)
    if ($taskRemaining.Count -eq 0) {
        $taskRestore = Start-Process -FilePath $taskInstalled -WindowStyle Hidden -PassThru
        $taskState.RestoredPid = $taskRestore.Id
        $taskState.RestoredCreatedUtc = $taskRestore.StartTime.ToUniversalTime().ToString('o')
        $taskRestore.Dispose()
    }
    $taskState.SettingsAfterSha256 = (Get-FileHash -LiteralPath $taskSettings -Algorithm SHA256).Hash
    $taskState.SecretsAfterSha256 = (Get-FileHash -LiteralPath $taskSecrets -Algorithm SHA256).Hash
    $taskState.InstalledSettingsAndSecretsUnchanged = $taskState.SettingsBeforeSha256 -eq $taskState.SettingsAfterSha256 -and $taskState.SecretsBeforeSha256 -eq $taskState.SecretsAfterSha256
    $taskState.Completed = $true; $taskState.FinishedUtc = [DateTime]::UtcNow.ToString('o')
    Save-T040Comparison
}
if ($taskState.Failure) { throw 'Diagnostic comparison failed; exact state and any live process retained' }
