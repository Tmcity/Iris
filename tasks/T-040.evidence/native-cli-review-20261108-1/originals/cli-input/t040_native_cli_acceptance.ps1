param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned T-040 Sandbox only' }
$taskOut = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $taskOut.StartsWith('C:\IrisT040Output\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected output directory' }
$taskTarget = Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskApps = @(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object { $_.ExecutablePath -eq $taskTarget })
if ($taskApps.Count -ne 1 -or $taskApps[0].ProcessId -ne 5568) { throw 'The identified pre-CLI instance must still be alive' }
$taskOriginal = [Diagnostics.Process]::GetProcessById(5568)
$null = $taskOriginal.Handle
$taskUtf8 = New-Object Text.UTF8Encoding($false)
$taskState = @{ StartedUtc=[DateTime]::UtcNow.ToString('o'); ObserverPid=$PID; OriginalPid=5568; Stage='Validate'; Completed=$false; Failure=$null; Commands=@(); CpuSamples=@(); ProductOperationPerformed=$true; NativeCliUsed=$true; ForegroundKeyboardOrMouseInputUsed=$false; InstallerExecuted=$false }
function Save-T040CliState { [IO.File]::WriteAllText((Join-Path $taskOut 'state.json'),($script:taskState | ConvertTo-Json -Depth 12),$script:taskUtf8) }
function Read-T040SharedText([string]$Path) {
    $taskStream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try { $taskReader=New-Object IO.StreamReader($taskStream,$taskUtf8); try { return $taskReader.ReadToEnd() } finally { $taskReader.Dispose() } } finally { $taskStream.Dispose() }
}
function Invoke-T040Cli([string]$Arguments) {
    $taskStart=[DateTime]::UtcNow
    $taskCommand=Start-Process -FilePath $taskTarget -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    $null=$taskCommand.Handle
    if (-not $taskCommand.WaitForExit(15000)) { throw 'The exact CLI process is still running; preserve its handle and inspect it' }
    $taskRow=@{Arguments=$Arguments;Pid=$taskCommand.Id;CreatedUtc=$taskCommand.StartTime.ToUniversalTime().ToString('o');FinishedUtc=[DateTime]::UtcNow.ToString('o');ExitCode=$taskCommand.ExitCode;ElapsedMs=([DateTime]::UtcNow-$taskStart).TotalMilliseconds}
    $taskCommand.Dispose()
    $script:taskState.Commands += $taskRow
    Save-T040CliState
    if ($null -eq $taskRow.ExitCode -or $taskRow.ExitCode -ne 0) { throw 'The installed CLI did not confirm success; original result retained' }
}
function Save-T040CliPhase([string]$Phase) {
    $taskBefore=[DateTime]::UtcNow
    & (Join-Path $PSScriptRoot 'tools\state-snapshot\T040StateProbe.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Read-only history snapshot failed' }
    $taskSnapshot=@(Get-ChildItem -LiteralPath 'C:\IrisT040Output' -Directory -Filter 'feature-data-*' | Sort-Object Name -Descending)[0]
    $taskSnapshotState=Get-Content -LiteralPath (Join-Path $taskSnapshot.FullName 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([DateTime]::Parse($taskSnapshotState.Utc).ToUniversalTime() -lt $taskBefore) { throw 'History snapshot was not taken in this phase' }
    $taskProvider=@(Get-CimInstance Win32_Process -Filter "Name='T040SyntheticProvider.exe'")
    if ($taskProvider.Count -ne 1) { throw 'The owned synthetic provider must remain alive' }
    $taskProviderDirectory=@(Get-ChildItem -LiteralPath 'C:\IrisT040Output' -Directory -Filter 'synthetic-provider-*' | Sort-Object Name -Descending)[0]
    $taskProviderState=Get-Content -LiteralPath (Join-Path $taskProviderDirectory.FullName 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $taskProviderState.Ready -or $taskProviderState.Pid -ne $taskProvider[0].ProcessId) { throw 'The actual provider process and ready record differ' }
    $taskRaw=Read-T040SharedText (Join-Path $taskProviderDirectory.FullName 'requests.jsonl')
    [IO.File]::WriteAllText((Join-Path $taskOut ($Phase+'-requests.jsonl')),$taskRaw,$taskUtf8)
    [IO.File]::WriteAllText((Join-Path $taskOut ($Phase+'-snapshot.json')),(@{Utc=[DateTime]::UtcNow.ToString('o');Phase=$Phase;SnapshotDirectory=$taskSnapshot.FullName;ProviderPid=$taskProvider[0].ProcessId;HistoryState=$taskSnapshotState;ProductOperationPerformed=$true}|ConvertTo-Json -Depth 12),$taskUtf8)
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class IrisCliCpuTimes {
    [DllImport("kernel32.dll",SetLastError=true)] public static extern bool GetSystemTimes(out long idle,out long kernel,out long user);
    public static long[] Read() { long i,k,u;if(!GetSystemTimes(out i,out k,out u))throw new System.ComponentModel.Win32Exception();return new long[]{i,k,u}; }
}
'@
Save-T040CliState
try {
    $taskManifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'LOCAL_0211_payload-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach($taskName in @('Iris.App.exe','Iris.App.dll','Iris.Core.dll','Iris.Providers.dll')) {
        $taskExpected=@($taskManifest | Where-Object{$_.Path -eq $taskName})[0]
        if((Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $taskTarget) $taskName) -Algorithm SHA256).Hash -ne $taskExpected.Sha256){throw 'Installed CLI identity differs from the reviewed candidate'}
    }
    $taskState.Stage='ExitOriginal';Save-T040CliState
    Invoke-T040Cli '--action Exit'
    if(-not $taskOriginal.WaitForExit(15000)){throw 'The original app is still shutting down; do not start a replacement'}
    $taskState.OriginalExitCode=$taskOriginal.ExitCode
    $taskOriginal.Dispose()
    $taskState.Stage='StartPrimary';Save-T040CliState
    $taskCpuPrevious=[IrisCliCpuTimes]::Read();$taskCpuPreviousUtc=[DateTime]::UtcNow
    Start-Sleep -Milliseconds 200
    $taskPrimary=Start-Process -FilePath $taskTarget -WindowStyle Hidden -PassThru
    $null=$taskPrimary.Handle
    $taskCreated=$taskPrimary.StartTime.ToUniversalTime()
    $taskState.PrimaryPid=$taskPrimary.Id;$taskState.PrimaryCreatedUtc=$taskCreated.ToString('o');Save-T040CliState
    $taskDeadline=[DateTime]::UtcNow.AddSeconds(8)
    $taskReady=$false
    do {
        Start-Sleep -Milliseconds 100
        $taskCpu=[IrisCliCpuTimes]::Read();$taskCpuUtc=[DateTime]::UtcNow
        $taskTotal=($taskCpu[1]-$taskCpuPrevious[1])+($taskCpu[2]-$taskCpuPrevious[2])
        $taskLoad=if($taskTotal-gt0){100.0*($taskTotal-($taskCpu[0]-$taskCpuPrevious[0]))/$taskTotal}else{0}
        $taskState.CpuSamples+=@{IntervalStartedUtc=$taskCpuPreviousUtc.ToString('o');IntervalEndedUtc=$taskCpuUtc.ToString('o');LoadPercent=$taskLoad}
        $taskCpuPrevious=$taskCpu;$taskCpuPreviousUtc=$taskCpuUtc
        foreach($taskLog in @(Get-ChildItem -LiteralPath (Join-Path $env:APPDATA 'Iris\logs') -File -Filter 'iris-*.log' -ErrorAction SilentlyContinue)) {
            foreach($taskLine in (Read-T040SharedText $taskLog.FullName).Split([char]10)) {
                if($taskLine-match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) ([+-]\d{2}:\d{2}).*AppBootstrapper.*Iris 启动序列完成.*?([0-9]+(?:\.[0-9]+)?) ms') {
                    $taskReadyUtc=[DateTimeOffset]::Parse(($Matches[1]+' '+$Matches[2]),[Globalization.CultureInfo]::InvariantCulture).UtcDateTime
                    if($taskReadyUtc-ge$taskCreated){$taskState.BootstrapReadyUtc=$taskReadyUtc.ToString('o');$taskState.CreationToBootstrapMs=($taskReadyUtc-$taskCreated).TotalMilliseconds;$taskState.BootstrapReportedMs=[double]::Parse($Matches[3],[Globalization.CultureInfo]::InvariantCulture);$taskState.BootstrapLine=$taskLine.Trim();$taskReady=$true;break}
                }
            }
            if($taskReady){break}
        }
        Save-T040CliState
        $taskPrimary.Refresh()
        if($taskPrimary.HasExited){throw 'The newly started primary exited; preserve its state'}
    } while(-not $taskReady-and[DateTime]::UtcNow-lt$taskDeadline)
    if(-not $taskReady){throw 'No matching bootstrap completion was observed within the bounded startup interval'}
    $taskState.Stage='TranslatePrime';Save-T040CliState
    Invoke-T040Cli '--translate "Iris T040 CLI 01"'
    Start-Sleep -Seconds 5
    Save-T040CliPhase 'prime'
    $taskState.Stage='TranslateCache';Save-T040CliState
    Invoke-T040Cli '--translate "Iris T040 CLI 01"'
    Start-Sleep -Seconds 5
    Save-T040CliPhase 'cache'
    $taskAppsAfter=@(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object{$_.ExecutablePath-eq$taskTarget})
    $taskState.ActualAppProcessesAfter=$taskAppsAfter | Select-Object ProcessId,CreationDate,ExecutablePath,CommandLine
    if($taskAppsAfter.Count-ne1-or$taskAppsAfter[0].ProcessId-ne$taskPrimary.Id){throw 'Native CLI acceptance did not preserve one primary instance'}
    $taskState.Stage='Complete'
} catch {$taskState.Failure=@{Type=$_.Exception.GetType().FullName;Message=$_.Exception.Message;Position=$_.InvocationInfo.PositionMessage};$taskState.Stage='Failed'}
finally {$taskState.Completed=$true;$taskState.FinishedUtc=[DateTime]::UtcNow.ToString('o');Save-T040CliState}
if($taskState.Failure){throw 'Native CLI acceptance failed; original evidence and actual processes retained'}
