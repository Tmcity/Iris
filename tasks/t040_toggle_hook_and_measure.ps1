$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskTarget=Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskIdentity=Get-CimInstance Win32_Process -Filter 'ProcessId=6040'
if(-not$taskIdentity-or$taskIdentity.ExecutablePath-ne$taskTarget){throw 'Expected current Iris6040'}
$taskProcess=[Diagnostics.Process]::GetProcessById(6040);$null=$taskProcess.Handle
$taskSettings=Join-Path $env:APPDATA 'Iris\settings.json'
$taskBefore=Get-Content -LiteralPath $taskSettings -Raw -Encoding UTF8|ConvertFrom-Json
if($taskBefore.selection.mouseMode-ne'Off'){throw 'Expected initial Off mode'}
$taskOut=Join-Path 'C:\IrisT040Output' ('hook-measure-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'));$null=New-Item -ItemType Directory -Path $taskOut
$taskState=@{StartedUtc=[DateTime]::UtcNow.ToString('o');TargetPid=6040;Completed=$false;Failure=$null;Samples=@();Commands=@();ProductOperationPerformed=$true;NativeCliUsed=$true;ModeRestored=$false;FormalPostGcIdleMemoryProved=$false}
$taskUtf8=New-Object Text.UTF8Encoding($false)
function Save-T040HookMeasure{[IO.File]::WriteAllText((Join-Path $taskOut 'state.json'),($script:taskState|ConvertTo-Json -Depth 9),$script:taskUtf8)}
function Toggle-T040Hook{$taskCli=Start-Process -FilePath $taskTarget -ArgumentList @('--action','ToggleMouseSelection') -WindowStyle Hidden -PassThru;$null=$taskCli.Handle;if(-not$taskCli.WaitForExit(15000)){throw 'Exact hook CLI process is still running'};$script:taskState.Commands+=@{Pid=$taskCli.Id;ExitCode=$taskCli.ExitCode;Utc=[DateTime]::UtcNow.ToString('o')};if($taskCli.ExitCode-ne0){throw 'Hook CLI did not confirm acceptance'};$taskCli.Dispose()}
Save-T040HookMeasure
try{
    Toggle-T040Hook;Start-Sleep -Seconds 2
    $taskEnabled=Get-Content -LiteralPath $taskSettings -Raw -Encoding UTF8|ConvertFrom-Json
    if($taskEnabled.selection.mouseMode-ne'ShowIcon'){throw 'Expected persisted ShowIcon mode'}
    $taskState.EnabledMode='ShowIcon';Save-T040HookMeasure
    $taskLast=[DateTime]::UtcNow;$taskLastCpu=$taskProcess.TotalProcessorTime.TotalSeconds
    for($taskIndex=0;$taskIndex-lt30;$taskIndex++){
        Start-Sleep -Seconds 1;$taskProcess.Refresh();if($taskProcess.HasExited){throw 'Current Iris exited during hook sampling'}
        $taskNow=[DateTime]::UtcNow;$taskCpu=$taskProcess.TotalProcessorTime.TotalSeconds
        $taskState.Samples+=@{Utc=$taskNow.ToString('o');CpuPercent=100.0*($taskCpu-$taskLastCpu)/(($taskNow-$taskLast).TotalSeconds*[Environment]::ProcessorCount);WorkingSetBytes=$taskProcess.WorkingSet64;PrivateBytes=$taskProcess.PrivateMemorySize64}
        $taskLast=$taskNow;$taskLastCpu=$taskCpu
    }
    Toggle-T040Hook;Start-Sleep -Seconds 2
    $taskRestored=Get-Content -LiteralPath $taskSettings -Raw -Encoding UTF8|ConvertFrom-Json
    $taskState.ModeRestored=$taskRestored.selection.mouseMode-eq'Off'
    if(-not$taskState.ModeRestored){throw 'Mouse mode did not return to Off'}
}catch{$taskState.Failure=@{Type=$_.Exception.GetType().FullName;Message=$_.Exception.Message}}
finally{$taskState.Completed=$true;$taskState.FinishedUtc=[DateTime]::UtcNow.ToString('o');Save-T040HookMeasure;$taskProcess.Dispose()}
if($taskState.Failure){throw 'Hook measure failed; preserve actual mode and evidence'}
