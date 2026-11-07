param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskOut=[IO.Path]::GetFullPath($OutputDirectory)
if(-not$taskOut.StartsWith('C:\IrisT040Output\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected output directory'}
$taskTarget=Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskExpectedPid=6040
$taskUtf8=New-Object Text.UTF8Encoding($false)
$taskState=@{StartedUtc=[DateTime]::UtcNow.ToString('o');ObserverPid=$PID;TargetPid=$taskExpectedPid;Stage='Start';Completed=$false;Failure=$null;Commands=@();ProductOperationPerformed=$true;NativeCliUsed=$true;ForegroundKeyboardOrMouseInputUsed=$false;ManualStopButtonProved=$false;ManualHistoryRefreshButtonProved=$false;InstallerExecuted=$false}
function Save-T040NativeFeatures{[IO.File]::WriteAllText((Join-Path $taskOut 'state.json'),($script:taskState|ConvertTo-Json -Depth 9),$script:taskUtf8)}
function Assert-T040FeatureInstance{$taskApps=@(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'"|Where-Object{$_.ExecutablePath-eq$taskTarget});if($taskApps.Count-ne1-or$taskApps[0].ProcessId-ne$taskExpectedPid){throw 'Keep the same actual primary for native feature cases'}}
function Invoke-T040FeatureCli([string]$Arguments){
    Assert-T040FeatureInstance
    $taskState.Stage=$Arguments;Save-T040NativeFeatures
    $taskCli=Start-Process -FilePath $taskTarget -ArgumentList $Arguments -WindowStyle Hidden -PassThru;$null=$taskCli.Handle
    if(-not$taskCli.WaitForExit(15000)){throw 'The exact CLI process is still running; inspect it before any retry'}
    $taskRow=@{Arguments=$Arguments;Pid=$taskCli.Id;ExitCode=$taskCli.ExitCode;Utc=[DateTime]::UtcNow.ToString('o')};$taskCli.Dispose();$script:taskState.Commands+=$taskRow;Save-T040NativeFeatures
    if($null-eq$taskRow.ExitCode-or$taskRow.ExitCode-ne0){throw 'Native feature command did not confirm acceptance'}
    Assert-T040FeatureInstance
}
function Save-T040FeaturePhase([string]$Phase){
    & (Join-Path $PSScriptRoot 'tools\state-snapshot\T040StateProbe.exe');if($LASTEXITCODE-ne0){throw 'Read-only feature snapshot failed'}
    $taskData=@(Get-ChildItem -LiteralPath 'C:\IrisT040Output' -Directory -Filter 'feature-data-*'|Sort-Object Name -Descending)[0]
    $taskProvider=@(Get-CimInstance Win32_Process -Filter "Name='T040SyntheticProvider.exe'");if($taskProvider.Count-ne1){throw 'The actual fixture is absent'}
    $taskFolder=@(Get-ChildItem -LiteralPath 'C:\IrisT040Output' -Directory -Filter 'synthetic-provider-*'|Sort-Object Name -Descending)[0]
    $taskReady=Get-Content -LiteralPath (Join-Path $taskFolder.FullName 'state.json') -Raw -Encoding UTF8|ConvertFrom-Json
    if(-not$taskReady.Ready-or$taskReady.Pid-ne$taskProvider[0].ProcessId){throw 'Fixture ready record does not match its actual process'}
    $taskStream=[IO.File]::Open((Join-Path $taskFolder.FullName 'requests.jsonl'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    try{$taskCopy=[IO.File]::Create((Join-Path $taskOut ($Phase+'-requests.jsonl')));try{$taskStream.CopyTo($taskCopy)}finally{$taskCopy.Dispose()}}finally{$taskStream.Dispose()}
    $taskRecord=@{Utc=[DateTime]::UtcNow.ToString('o');Phase=$Phase;TargetPid=$taskExpectedPid;SnapshotDirectory=$taskData.FullName;ProviderPid=$taskProvider[0].ProcessId;DataState=(Get-Content -LiteralPath (Join-Path $taskData.FullName 'state.json') -Raw -Encoding UTF8|ConvertFrom-Json)}
    [IO.File]::WriteAllText((Join-Path $taskOut ($Phase+'-snapshot.json')),($taskRecord|ConvertTo-Json -Depth 12),$taskUtf8)
}
Save-T040NativeFeatures
try{
    Invoke-T040FeatureCli '--translate "Iris T040 retry native 02"';Start-Sleep -Seconds 3;Save-T040FeaturePhase 'retry'
    Invoke-T040FeatureCli '--translate "Iris T040 error native 03"';Start-Sleep -Seconds 3;Save-T040FeaturePhase 'error'
    Invoke-T040FeatureCli '--translate "Iris T040 recover native 03b"';Start-Sleep -Seconds 3;Save-T040FeaturePhase 'recovery'
    Invoke-T040FeatureCli '--translate "Iris T040 truncated native 04"';Start-Sleep -Seconds 3;Save-T040FeaturePhase 'truncated'
    Invoke-T040FeatureCli '--translate "Iris T040 cancel native 05"';Start-Sleep -Seconds 2;Save-T040FeaturePhase 'cancel-start'
    Invoke-T040FeatureCli '--translate "Iris T040 after cancel native 06"';Start-Sleep -Seconds 3;Save-T040FeaturePhase 'cancel-replaced'
    Start-Sleep -Seconds 6;Save-T040FeaturePhase 'cancel-terminal'
    Invoke-T040FeatureCli '--action OpenHistory';Start-Sleep -Seconds 1
    Invoke-T040FeatureCli '--action OpenHistory';Start-Sleep -Seconds 1;Save-T040FeaturePhase 'history-shows'
    $taskState.Stage='Complete'
}catch{$taskState.Failure=@{Type=$_.Exception.GetType().FullName;Message=$_.Exception.Message;Position=$_.InvocationInfo.PositionMessage};$taskState.Stage='Failed'}
finally{$taskState.Completed=$true;$taskState.FinishedUtc=[DateTime]::UtcNow.ToString('o');Save-T040NativeFeatures}
if($taskState.Failure){throw 'Native feature cases failed; actual processes and original evidence retained'}
