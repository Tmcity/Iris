param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskResolved = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $taskResolved.StartsWith('C:\IrisT040Output\modifier-delivery-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Owned diagnostic output required' }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class T040ModifierDeliveryState {
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
}
'@
$taskStarted = [DateTime]::UtcNow
$taskRows = New-Object 'System.Collections.Generic.List[object]'
$taskKeys = @(17,18,162,163,164,165,68,70,79)
$taskLast = ''
$taskTimer = [Diagnostics.Stopwatch]::StartNew()
[IO.File]::WriteAllText((Join-Path $taskResolved 'ready.json'), (@{ Utc=$taskStarted.ToString('o'); Pid=$PID; DurationSeconds=20; OnlyWatchedModifierAndDFOVirtualKeys=$true } | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
while ($taskTimer.Elapsed.TotalSeconds -lt 20) {
    $taskDown = @()
    foreach ($taskVk in $taskKeys) { if (([T040ModifierDeliveryState]::GetAsyncKeyState($taskVk) -band 32768) -ne 0) { $taskDown += $taskVk } }
    $taskState = $taskDown -join ','
    if ($taskState -ne $taskLast) {
        $taskRows.Add(@{ ElapsedMilliseconds=$taskTimer.Elapsed.TotalMilliseconds; Utc=[DateTime]::UtcNow.ToString('o'); DownVirtualKeys=$taskDown })
        $taskLast = $taskState
    }
    Start-Sleep -Milliseconds 2
}
$taskTimer.Stop()
$taskResult = @{ StartedUtc=$taskStarted.ToString('o'); FinishedUtc=[DateTime]::UtcNow.ToString('o'); Pid=$PID; Completed=$true; DurationSeconds=$taskTimer.Elapsed.TotalSeconds; StateChanges=$taskRows.ToArray(); ClipboardContentRead=$false; UserTextLogged=$false; ForegroundInputInjected=$false; ProductOperationPerformed=$false }
[IO.File]::WriteAllText((Join-Path $taskResolved 'result.json'), ($taskResult | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
