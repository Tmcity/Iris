param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw'Owned T-040 Sandbox only'}
$taskOut=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $taskOut.StartsWith('C:\IrisT040Output\',[StringComparison]::OrdinalIgnoreCase)){throw'Unexpected metrics output path'}
if(Test-Path -LiteralPath $taskOut){throw'Use a new metrics session'}
$null=New-Item -ItemType Directory -Path $taskOut
$taskRoot=Join-Path $env:LOCALAPPDATA 'Iris'
$taskData=Join-Path $env:APPDATA 'Iris'
$taskTarget=Join-Path $taskRoot 'current\Iris.App.exe'
$taskUtf8=New-Object Text.UTF8Encoding($false)
$taskWriter=New-Object IO.StreamWriter((Join-Path $taskOut 'samples.jsonl'),$false,$taskUtf8)
$taskKnown=@{}
$taskLogical=[Environment]::ProcessorCount
$taskSequence=0
function WriteSample($taskValue){$script:taskSequence++;$taskValue.Sequence=$script:taskSequence;$taskValue.ObservedUtc=[DateTime]::UtcNow.ToString('o');$script:taskWriter.WriteLine(($taskValue|ConvertTo-Json -Depth 7 -Compress));$script:taskWriter.Flush()}
function Status($taskReady,$taskError){[IO.File]::WriteAllText((Join-Path $taskOut 'status.json'),(@{Ready=$taskReady;ObserverPid=$PID;ProductOperationPerformed=$false;LastHeartbeatUtc=[DateTime]::UtcNow.ToString('o');Samples=$script:taskSequence;Failure=$taskError;OutputDirectory=$taskOut}|ConvertTo-Json -Depth 6),$taskUtf8)}
try{
    WriteSample(@{Kind='ObserverStarted';ObserverPid=$PID;LogicalProcessors=$taskLogical;TargetPath=$taskTarget;ProductOperationPerformed=$false;InitialProductPresent=(Test-Path -LiteralPath $taskTarget)})
    Status $true $null
    while(-not(Test-Path -LiteralPath (Join-Path $taskOut 'stop.request'))){
        $taskNow=[DateTime]::UtcNow
        $taskCpuLoad=(Get-CimInstance Win32_Processor|Measure-Object -Property LoadPercentage -Average).Average
        $taskMatches=@(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'"|Where-Object{$_.ExecutablePath-and$_.ExecutablePath.Equals($taskTarget,[StringComparison]::OrdinalIgnoreCase)})
        foreach($taskIdentity in $taskMatches){
            $taskId=[int]$taskIdentity.ProcessId
            if(-not $taskKnown.ContainsKey($taskId)){
                $taskProcess=[Diagnostics.Process]::GetProcessById($taskId)
                $null=$taskProcess.Handle
                $taskKnown[$taskId]=@{Process=$taskProcess;StartedUtc=$taskIdentity.CreationDate.ToUniversalTime().ToString('o');LastCpuSeconds=$taskProcess.TotalProcessorTime.TotalSeconds;LastSampleUtc=$taskNow;StartupRecorded=$false}
                WriteSample(@{Kind='ProcessFirstObserved';Pid=$taskId;StartedUtc=$taskKnown[$taskId].StartedUtc;SystemCpuLoadPercentage=$taskCpuLoad;ParentPid=$taskIdentity.ParentProcessId;ExecutablePath=$taskIdentity.ExecutablePath})
            }
        }
        foreach($taskId in @($taskKnown.Keys)){
            $taskEntry=$taskKnown[$taskId];$taskProcess=$taskEntry.Process
            if($taskProcess.HasExited){WriteSample(@{Kind='ProcessExited';Pid=$taskId;ExitCode=$taskProcess.ExitCode;ExitUtc=$taskProcess.ExitTime.ToUniversalTime().ToString('o')});$taskProcess.Dispose();$taskKnown.Remove($taskId);continue}
            $taskProcess.Refresh();$taskSeconds=($taskNow-$taskEntry.LastSampleUtc).TotalSeconds;$taskCpu=$taskProcess.TotalProcessorTime.TotalSeconds
            $taskCpuPercentage=if($taskSeconds-gt0){100*($taskCpu-$taskEntry.LastCpuSeconds)/($taskSeconds*$taskLogical)}else{0}
            WriteSample(@{Kind='ProcessSample';Pid=$taskId;WorkingSetBytes=$taskProcess.WorkingSet64;PrivateBytes=$taskProcess.PrivateMemorySize64;ProcessCpuTotalSeconds=$taskCpu;ProcessCpuPercentage=$taskCpuPercentage;SystemCpuLoadPercentage=$taskCpuLoad;SinceCreatedSeconds=($taskNow-[DateTime]::Parse($taskEntry.StartedUtc).ToUniversalTime()).TotalSeconds})
            $taskEntry.LastCpuSeconds=$taskCpu;$taskEntry.LastSampleUtc=$taskNow
            if(-not $taskEntry.StartupRecorded){
                $taskLogFolder=Join-Path $taskData 'logs'
                foreach($taskLog in @(Get-ChildItem -LiteralPath $taskLogFolder -File -Filter 'iris-*.log' -ErrorAction SilentlyContinue)){
                    $taskStream=[IO.File]::Open($taskLog.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
                    try{$taskReader=New-Object IO.StreamReader($taskStream,$taskUtf8);try{$taskLines=$taskReader.ReadToEnd().Split([char]10)}finally{$taskReader.Dispose()}}finally{$taskStream.Dispose()}
                    foreach($taskLine in $taskLines){
                        if($taskLine-match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) ([+-]\d{2}:\d{2}).*Iris\.App\.Hosting\.AppBootstrapper.*Iris 启动序列完成.*?([0-9]+(?:\.[0-9]+)?) ms'){
                            $taskReadyUtc=[DateTimeOffset]::Parse(($Matches[1]+' '+$Matches[2]),[Globalization.CultureInfo]::InvariantCulture).UtcDateTime
                            $taskCreatedUtc=[DateTime]::Parse($taskEntry.StartedUtc).ToUniversalTime()
                            if($taskReadyUtc-ge$taskCreatedUtc){WriteSample(@{Kind='StartupLogCorrelated';Pid=$taskId;ProcessCreatedUtc=$taskEntry.StartedUtc;BootstrapReadyUtc=$taskReadyUtc.ToString('o');BootstrapReportedMs=[double]::Parse($Matches[3],[Globalization.CultureInfo]::InvariantCulture);CreationToBootstrapReadyMs=($taskReadyUtc-$taskCreatedUtc).TotalMilliseconds;SystemCpuLoadPercentage=$taskCpuLoad;LogName=$taskLog.Name;LogLine=$taskLine.TrimEnd()});$taskEntry.StartupRecorded=$true;break}
                        }
                    }
                    if($taskEntry.StartupRecorded){break}
                }
            }
        }
        Status $true $null
        Start-Sleep -Milliseconds 1000
    }
    WriteSample(@{Kind='ObserverStopped';ProductOperationPerformed=$false});Status $false $null
}catch{Status $false @{Type=$_.Exception.GetType().FullName;HResult=$_.Exception.HResult};throw}
finally{foreach($taskEntry in $taskKnown.Values){$taskEntry.Process.Dispose()};$taskWriter.Dispose()}
