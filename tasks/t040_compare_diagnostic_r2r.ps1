$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned Sandbox only'}
$taskInstalled=Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskApps=@(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'"|Where-Object{$_.ExecutablePath-eq$taskInstalled})
if($taskApps.Count-ne1-or$taskApps[0].ProcessId-ne5312){throw 'Expected restored installed5312'}
$taskOriginal=[Diagnostics.Process]::GetProcessById(5312);$null=$taskOriginal.Handle
function Exit-DiagnosticApp([string]$Exe,[Diagnostics.Process]$Primary){$taskCli=Start-Process -FilePath $Exe -ArgumentList @('--action','Exit') -WindowStyle Hidden -PassThru;$null=$taskCli.Handle;if(-not$taskCli.WaitForExit(15000)-or$taskCli.ExitCode-ne0){throw 'Exact Exit CLI remains alive or failed'};$taskCli.Dispose();if(-not$Primary.WaitForExit(15000)){throw 'Primary remains alive; no replacement started'}}
Exit-DiagnosticApp $taskInstalled $taskOriginal;$taskOriginal.Dispose()
$taskOut=Join-Path 'C:\IrisT040Output' ('r2r-repeat-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'));$null=New-Item -ItemType Directory -Path $taskOut
$taskState=@{StartedUtc=[DateTime]::UtcNow.ToString('o');Runs=@();Completed=$false;Failure=$null;FormalNfrResult=$false;ProductOperationPerformed=$true;InstalledBinaryChanged=$false;InstalledDataChanged=$false}
$taskUtf8=New-Object Text.UTF8Encoding($false)
function Save-CompareState{[IO.File]::WriteAllText((Join-Path $taskOut 'state.json'),($script:taskState|ConvertTo-Json -Depth 10),$script:taskUtf8)}
Save-CompareState
try{
    foreach($taskKind in @('baseline','r2r','r2r','baseline')){
        $taskFolder=Join-Path (Join-Path $env:LOCALAPPDATA 'IrisT040Diagnostic') ([Guid]::NewGuid().ToString('N'));$null=New-Item -ItemType Directory -Path $taskFolder
        $taskSource=if($taskKind-eq'r2r'){'tools\startup-diagnostic-r2r'}else{'tools\startup-diagnostic-prehost'}
        Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $taskSource)|Copy-Item -Destination $taskFolder -Recurse
        [IO.File]::WriteAllText((Join-Path $taskFolder 'portable.txt'),'',$taskUtf8)
        $taskData=Join-Path $taskFolder 'data';$null=New-Item -ItemType Directory -Path $taskData
        Copy-Item -LiteralPath (Join-Path $env:APPDATA 'Iris\settings.json') -Destination (Join-Path $taskData 'settings.json')
        Copy-Item -LiteralPath (Join-Path $env:APPDATA 'Iris\secrets.dat') -Destination (Join-Path $taskData 'secrets.dat')
        $taskExe=Join-Path $taskFolder 'Iris.App.exe';$taskProcess=Start-Process -FilePath $taskExe -WindowStyle Hidden -PassThru;$null=$taskProcess.Handle
        $taskDeadline=[DateTime]::UtcNow.AddSeconds(8);$taskReady=$false
        do{Start-Sleep -Milliseconds 200;foreach($taskLog in @(Get-ChildItem -LiteralPath (Join-Path $taskData 'logs') -File -Filter 'iris-*.log' -ErrorAction SilentlyContinue)){$taskText=Get-Content -LiteralPath $taskLog.FullName -Raw -Encoding UTF8;if($taskText-like'*AppBootstrapper*ms*'){$taskReady=$true;break}}}while(-not$taskReady-and[DateTime]::UtcNow-lt$taskDeadline)
        if(-not$taskReady){throw 'Diagnostic startup did not finish; actual process retained'}
        Start-Sleep -Seconds 35;$taskProcess.Refresh()
        $taskRecord=@{Kind=$taskKind;Pid=$taskProcess.Id;DataDirectory=$taskData;WorkingSetBytes=$taskProcess.WorkingSet64;PrivateBytes=$taskProcess.PrivateMemorySize64;CreatedUtc=$taskProcess.StartTime.ToUniversalTime().ToString('o');SampleUtc=[DateTime]::UtcNow.ToString('o')}
        $taskState.Runs+=$taskRecord;Save-CompareState
        $taskIndex=$taskState.Runs.Count.ToString('00');$taskCapture=Join-Path $taskOut ($taskIndex+'-'+$taskKind);$null=New-Item -ItemType Directory -Path $taskCapture
        foreach($taskFile in @('startup-phases.txt','startup-prehost.txt')){Copy-Item -LiteralPath (Join-Path $taskData $taskFile) -Destination (Join-Path $taskCapture $taskFile)}
        foreach($taskLog in @(Get-ChildItem -LiteralPath (Join-Path $taskData 'logs') -File -Filter 'iris-*.log')){Copy-Item -LiteralPath $taskLog.FullName -Destination (Join-Path $taskCapture $taskLog.Name)}
        Exit-DiagnosticApp $taskExe $taskProcess;$taskRecord.ExitCode=$taskProcess.ExitCode;$taskProcess.Dispose();Save-CompareState
    }
}catch{$taskState.Failure=@{Type=$_.Exception.GetType().FullName;Message=$_.Exception.Message}}
finally{$taskState.Completed=$true;$taskState.FinishedUtc=[DateTime]::UtcNow.ToString('o');Save-CompareState}
if($taskState.Failure){throw 'Diagnostic comparison failed; actual process and original records retained'}
