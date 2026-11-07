param([ValidateRange(0,60)][int]$WaitSeconds=45)
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned T-040 Sandbox only' }
Import-Module (Join-Path $PSScriptRoot 't040_metrics_process_readonly.psm1') -Force
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class IrisSession3ReadOnlyWindows {
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
$taskTarget = Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskIdentity = Get-CimInstance Win32_Process -Filter 'ProcessId=5568'
if (-not $taskIdentity -or $taskIdentity.ExecutablePath -ne $taskTarget) { throw 'Keep the identified session3 Iris instance' }
if ([Math]::Abs(($taskIdentity.CreationDate.ToUniversalTime() - [DateTime]::Parse('2026-11-07T13:34:00.2928453Z').ToUniversalTime()).TotalMilliseconds) -gt 1) { throw 'Iris process identity changed; preserve this session before another measurement' }
$taskVisibleBefore = @([IrisSession3ReadOnlyWindows]::VisibleTitles(5568))
if ($taskVisibleBefore.Count -gt 0) { throw 'Close the Iris functional windows and keep the tray instance; no window was changed by this tool' }
$taskTraceProcesses = @(Get-CimInstance Win32_Process -Filter "Name='RuntimeCounterProbe.exe'")
if ($taskTraceProcesses.Count -gt 0) { throw 'Runtime tracing is still active; preserve it before an idle capture' }
$taskStarted = [DateTime]::UtcNow
Start-Sleep -Seconds $WaitSeconds
$taskVisibleAfter = @([IrisSession3ReadOnlyWindows]::VisibleTitles(5568))
if ($taskVisibleAfter.Count -gt 0) { throw 'Iris functional windows became visible during the idle interval' }
$taskRead = Read-T040ProcessCounters $taskIdentity
if ($taskRead.State -ne 'Sample') { throw 'The same Iris instance exited; no restart was performed' }
try { $taskCounters = $taskRead.Counters } finally { $taskRead.Process.Dispose() }
& (Join-Path $PSScriptRoot 'save-metrics-state-v3-readonly.ps1')
$taskRecord = @{ Utc=[DateTime]::UtcNow.ToString('o'); IdleStartedUtc=$taskStarted.ToString('o'); RequestedIdleSeconds=$WaitSeconds; TargetPid=5568; VisibleBefore=$taskVisibleBefore; VisibleAfter=$taskVisibleAfter; Counters=$taskCounters; RuntimeTraceActualProcessAbsent=$true; ProductOperationPerformed=$false; GcRequested=$false; WorkingSetTrimmed=$false; NaturalFullGcProved=$false; Scope='Tray idle after settings closed; full-GC steady-state acceptance remains separate' }
[IO.File]::WriteAllText(('C:\IrisT040Output\session3-tray-idle-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json'),($taskRecord | ConvertTo-Json -Depth 9),(New-Object Text.UTF8Encoding($false)))
Write-Output 'Tray idle evidence saved; no product operation or GC request performed.'
