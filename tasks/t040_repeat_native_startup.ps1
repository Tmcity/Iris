param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskOut=[IO.Path]::GetFullPath($OutputDirectory)
if(-not$taskOut.StartsWith('C:\IrisT040Output\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected output directory'}
$taskTarget=Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskSettings=Join-Path $env:APPDATA 'Iris\settings.json'
$taskSecrets=Join-Path $env:APPDATA 'Iris\secrets.dat'
$taskUtf8=New-Object Text.UTF8Encoding($false)
$taskState=@{StartedUtc=[DateTime]::UtcNow.ToString('o');ObserverPid=$PID;Completed=$false;Failure=$null;Runs=@();Stage='Start';ProductOperationPerformed=$true;NativeCliUsed=$true;InstallerExecuted=$false;Prior1599msFailurePreserved=$true;BeforeSettingsHash=(Get-FileHash -LiteralPath $taskSettings -Algorithm SHA256).Hash;BeforeSecretsHash=(Get-FileHash -LiteralPath $taskSecrets -Algorithm SHA256).Hash}
function Save-T040StartupRepeat{[IO.File]::WriteAllText((Join-Path $taskOut 'state.json'),($script:taskState|ConvertTo-Json -Depth 12),$script:taskUtf8)}
function Read-T040StartupLog([string]$Path){$taskStream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite-bor[IO.FileShare]::Delete));try{$taskReader=New-Object IO.StreamReader($taskStream,$taskUtf8);try{return $taskReader.ReadToEnd()}finally{$taskReader.Dispose()}}finally{$taskStream.Dispose()}}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class IrisStartupRepeatCpu {
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetSystemTimes(out long idle,out long kernel,out long user);
    public static long[] Read(){long i,k,u;if(!GetSystemTimes(out i,out k,out u))throw new System.ComponentModel.Win32Exception();return new long[]{i,k,u};}
}
'@
Save-T040StartupRepeat
try{
    for($taskIndex=1;$taskIndex-le3;$taskIndex++){
        $taskState.Stage='StopPrimary-'+$taskIndex;Save-T040StartupRepeat
        $taskApps=@(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'"|Where-Object{$_.ExecutablePath-eq$taskTarget})
        if($taskApps.Count-ne1){throw 'Exactly one actual primary is required before each repeat'}
        $taskOld=[Diagnostics.Process]::GetProcessById([int]$taskApps[0].ProcessId);$null=$taskOld.Handle
        $taskExit=Start-Process -FilePath $taskTarget -ArgumentList @('--action','Exit') -WindowStyle Hidden -PassThru;$null=$taskExit.Handle
        if(-not$taskExit.WaitForExit(15000)){throw 'Exact Exit CLI process is still running; inspect it before another start'}
        if($taskExit.ExitCode-ne0){throw 'Exit CLI did not confirm success'}
        $taskExit.Dispose()
        if(-not$taskOld.WaitForExit(15000)){throw 'Primary is still shutting down; no replacement was started'}
        $taskRun=@{Iteration=$taskIndex;OldPid=$taskOld.Id;OldExitCode=$taskOld.ExitCode;CpuSamples=@()}
        $taskOld.Dispose()
        $taskState.Stage='StartPrimary-'+$taskIndex
        $taskCpuPrevious=[IrisStartupRepeatCpu]::Read();$taskCpuPreviousUtc=[DateTime]::UtcNow
        Start-Sleep -Milliseconds 200
        $taskPrimary=Start-Process -FilePath $taskTarget -WindowStyle Hidden -PassThru;$null=$taskPrimary.Handle
        $taskCreated=$taskPrimary.StartTime.ToUniversalTime();$taskRun.Pid=$taskPrimary.Id;$taskRun.CreatedUtc=$taskCreated.ToString('o')
        $taskReady=$false;$taskDeadline=[DateTime]::UtcNow.AddSeconds(8)
        do{
            Start-Sleep -Milliseconds 100
            $taskCpu=[IrisStartupRepeatCpu]::Read();$taskUtc=[DateTime]::UtcNow;$taskTotal=($taskCpu[1]-$taskCpuPrevious[1])+($taskCpu[2]-$taskCpuPrevious[2])
            $taskLoad=if($taskTotal-gt0){100.0*($taskTotal-($taskCpu[0]-$taskCpuPrevious[0]))/$taskTotal}else{0}
            $taskRun.CpuSamples+=@{StartUtc=$taskCpuPreviousUtc.ToString('o');EndUtc=$taskUtc.ToString('o');LoadPercent=$taskLoad};$taskCpuPrevious=$taskCpu;$taskCpuPreviousUtc=$taskUtc
            foreach($taskLog in @(Get-ChildItem -LiteralPath (Join-Path $env:APPDATA 'Iris\logs') -File -Filter 'iris-*.log' -ErrorAction SilentlyContinue)){
                foreach($taskLine in (Read-T040StartupLog $taskLog.FullName).Split([char]10)){
                    if($taskLine-match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) ([+-]\d{2}:\d{2}).*AppBootstrapper.*Iris 启动序列完成.*?([0-9]+(?:\.[0-9]+)?) ms'){
                        $taskReadyUtc=[DateTimeOffset]::Parse(($Matches[1]+' '+$Matches[2]),[Globalization.CultureInfo]::InvariantCulture).UtcDateTime
                        if($taskReadyUtc-ge$taskCreated){$taskRun.ReadyUtc=$taskReadyUtc.ToString('o');$taskRun.CreationToBootstrapMs=($taskReadyUtc-$taskCreated).TotalMilliseconds;$taskRun.BootstrapReportedMs=[double]::Parse($Matches[3],[Globalization.CultureInfo]::InvariantCulture);$taskRun.BootstrapLine=$taskLine.Trim();$taskReady=$true;break}
                    }
                }
                if($taskReady){break}
            }
            $taskPrimary.Refresh();if($taskPrimary.HasExited){throw 'The new primary exited; preserve its state'}
        }while(-not$taskReady-and[DateTime]::UtcNow-lt$taskDeadline)
        if(-not$taskReady){throw 'No matching bootstrap completion in the bounded repeat'}
        $taskState.Runs+=$taskRun;$taskState.Stage='InitialTrayIdle-'+$taskIndex;Save-T040StartupRepeat
        Start-Sleep -Seconds 35
        $taskPrimary.Refresh();if($taskPrimary.HasExited){throw 'Primary exited during the initial tray interval'}
        $taskRun.InitialTraySample=@{Utc=[DateTime]::UtcNow.ToString('o');WorkingSetBytes=$taskPrimary.WorkingSet64;PrivateBytes=$taskPrimary.PrivateMemorySize64;MainWindowHandle=$taskPrimary.MainWindowHandle.ToInt64()}
        $taskPrimary.Dispose();Save-T040StartupRepeat
    }
    $taskState.AfterSettingsHash=(Get-FileHash -LiteralPath $taskSettings -Algorithm SHA256).Hash;$taskState.AfterSecretsHash=(Get-FileHash -LiteralPath $taskSecrets -Algorithm SHA256).Hash
    $taskState.Stage='Complete'
}catch{$taskState.Failure=@{Type=$_.Exception.GetType().FullName;Message=$_.Exception.Message;Position=$_.InvocationInfo.PositionMessage};$taskState.Stage='Failed'}
finally{$taskState.Completed=$true;$taskState.FinishedUtc=[DateTime]::UtcNow.ToString('o');Save-T040StartupRepeat}
if($taskState.Failure){throw 'Native startup repeat failed; actual processes and original records retained'}
