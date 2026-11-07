param([Parameter(Mandatory = $true)][string]$ReceiptPath, [Parameter(Mandatory = $true)][string]$TraceLeasePath, [Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskOut = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $taskOut.StartsWith('C:\IrisT040Output\r2r-steady-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected owned steady-state output' }
$taskUtf8 = New-Object Text.UTF8Encoding($false)
trap {
    $taskEarlyFailure = @{ Utc = [DateTime]::UtcNow.ToString('o'); Completed = $true; Failure = @{ Type = $_.Exception.GetType().FullName; Message = $_.Exception.Message }; ProductOperationPerformed = $false; FormalMemoryConditionProved = $false }
    [IO.File]::WriteAllText((Join-Path $taskOut 'early-failure.json'), ($taskEarlyFailure | ConvertTo-Json -Depth 4), $taskUtf8)
    exit 1
}
Import-Module (Join-Path $PSScriptRoot 't040_r2r_candidate_gate.psm1') -Force
$taskCandidate = Get-T040VerifiedR2rCandidate -ReceiptPath $ReceiptPath
$taskLeasePath = [IO.Path]::GetFullPath($TraceLeasePath)
if (-not $taskLeasePath.StartsWith('C:\IrisT040Output\r2r-runtime-lease-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected trace lease' }
$taskLease = Get-Content -LiteralPath $taskLeasePath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($taskLease.TargetPid -ne $taskCandidate.Identity.ProcessId -or $taskLease.ManifestSha256 -ne $taskCandidate.Receipt.ManifestSha256) { throw 'Trace lease belongs to another candidate or primary' }
$taskTrace = [IO.Path]::GetFullPath($taskLease.TraceDirectory)
if (-not $taskTrace.StartsWith('C:\IrisT040Output\runtime-counters-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Trace outside owned output' }
$taskFinal = Get-Content -LiteralPath (Join-Path $taskTrace 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $taskFinal.Completed -or $taskFinal.Failure -or $taskFinal.Pid -ne $taskLease.TargetPid -or $taskFinal.ObserverPid -ne $taskLease.ObserverPid) { throw 'Completed matching natural-GC trace required' }
$taskWorker = Get-CimInstance Win32_Process -Filter ('ProcessId=' + [int]$taskLease.ObserverPid)
if ($taskWorker -and [Math]::Abs(($taskWorker.CreationDate.ToUniversalTime() - [DateTime]::Parse($taskLease.ObserverCreatedUtc).ToUniversalTime()).TotalMilliseconds) -lt 1) { throw 'Matching runtime observer remains alive' }
if (@(Get-CimInstance Win32_Process -Filter "Name='RuntimeCounterProbe.exe'").Count) { throw 'Runtime counter tracing is still active' }
$taskGcPairs = Get-T040CompletedNaturalGcPairs -EventsPath (Join-Path $taskTrace 'events.jsonl')
if ($taskGcPairs.CompletedNaturalFullGcPairs -lt 1) { throw 'No paired natural full-GC start/stop is proved; preserve the unproved result' }
if (([DateTime]::UtcNow - [DateTimeOffset]::Parse($taskFinal.Utc).UtcDateTime).TotalSeconds -lt 30) { throw 'The post-trace settling interval is incomplete' }
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;
public static class T040R2rReadOnlyState {
    public delegate bool Callback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback, IntPtr state);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    public static string[] VisibleTitles(uint target) {
        var titles = new List<string>();
        EnumWindows(delegate(IntPtr window, IntPtr state) {
            uint pid; GetWindowThreadProcessId(window, out pid);
            if (pid == target && IsWindowVisible(window)) { var text = new StringBuilder(512); GetWindowText(window, text, text.Capacity); titles.Add(text.ToString()); }
            return true;
        }, IntPtr.Zero);
        return titles.ToArray();
    }
}
'@
$taskProcess = [Diagnostics.Process]::GetProcessById([int]$taskCandidate.Identity.ProcessId); $null = $taskProcess.Handle
$taskState = @{
    StartedUtc = [DateTime]::UtcNow.ToString('o'); AppPid = $taskProcess.Id; AppCreatedUtc = $taskProcess.StartTime.ToUniversalTime().ToString('o')
    SourceCommit = $taskCandidate.Receipt.SourceCommit; Samples = @(); Completed = $false; Failure = $null
    NaturalFullGcPairs = $taskGcPairs.CompletedNaturalFullGcPairs; TraceCompletedUtc = $taskFinal.Utc; TraceObserverActuallyAbsent = $true
    ProductOperationPerformed = $false; GcRequested = $false; WorkingSetTrimmed = $false
    WorkingSetLimitBytes = 167772160; PrivateBytesLimitBytes = 104857600; FormalMemoryConditionProved = $false
}
function Save-T040R2rSteady { [IO.File]::WriteAllText((Join-Path $script:taskOut 'state.json'), ($script:taskState | ConvertTo-Json -Depth 6), $script:taskUtf8) }
Save-T040R2rSteady
try {
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        if ($taskProcess.HasExited) { throw 'The same verified primary exited; no replacement sampled' }
        if (@(Get-CimInstance Win32_Process -Filter "Name='RuntimeCounterProbe.exe'").Count) { throw 'Runtime tracing became active during the idle interval' }
        $taskVisible = @([T040R2rReadOnlyState]::VisibleTitles([uint32]$taskProcess.Id))
        if ($taskVisible.Count) { throw 'Iris functional windows are visible; this interval is ineligible' }
        $taskProcess.Refresh()
        $taskState.Samples += @{
            Utc = [DateTime]::UtcNow.ToString('o'); Pid = $taskProcess.Id; VisibleWindowCount = $taskVisible.Count
            WorkingSetBytes = $taskProcess.WorkingSet64; PrivateBytes = $taskProcess.PrivateMemorySize64
            ProcessCpuTotalSeconds = $taskProcess.TotalProcessorTime.TotalSeconds
        }
        Save-T040R2rSteady
        Start-Sleep -Seconds 1
    } while ([DateTime]::UtcNow -lt $taskDeadline)
    $taskState.FormalMemoryConditionProved = $true
    $taskState.WorkingSetWithinLimit = @($taskState.Samples | Where-Object { $_.WorkingSetBytes -gt $taskState.WorkingSetLimitBytes }).Count -eq 0
    $taskState.PrivateBytesWithinLimit = @($taskState.Samples | Where-Object { $_.PrivateBytes -gt $taskState.PrivateBytesLimitBytes }).Count -eq 0
} catch { $taskState.Failure = @{ Type = $_.Exception.GetType().FullName; Message = $_.Exception.Message } }
finally { $taskProcess.Dispose(); $taskState.Completed = $true; $taskState.FinishedUtc = [DateTime]::UtcNow.ToString('o'); Save-T040R2rSteady }
if ($taskState.Failure) { throw 'Steady-state conditions failed; preserve original interval' }
