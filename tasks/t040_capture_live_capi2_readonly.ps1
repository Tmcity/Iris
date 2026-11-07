param([ValidateRange(5,1800)][int]$DurationSeconds=900,[switch]$ValidateWithExistingMsi)
$ErrorActionPreference='Stop'
if([Environment]::UserName -ne 'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
if($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1){throw 'Windows PowerShell 5.1 required'}
$taskLogName='Microsoft-Windows-CAPI2/Operational'
$taskInfo=Get-WinEvent -ListLog $taskLogName -ErrorAction Stop
if(-not $taskInfo.IsEnabled){throw 'Enable this diagnostic log manually before capture; no log configuration was changed'}
$taskStarted=[DateTime]::UtcNow
$taskOutput=Join-Path 'C:\IrisT040Output' ('live-capi2-'+$taskStarted.ToString('yyyyMMddTHHmmssfffffffZ'))
$null=New-Item -ItemType Directory -Path $taskOutput
$taskIdentifier='IrisT040Capi2-'+[Guid]::NewGuid().ToString('N')
$taskQuery=[System.Diagnostics.Eventing.Reader.EventLogQuery]::new($taskLogName,[System.Diagnostics.Eventing.Reader.PathType]::LogName)
$taskWatcher=[System.Diagnostics.Eventing.Reader.EventLogWatcher]::new($taskQuery)
$taskSubscription=$null
$taskWriter=[IO.StreamWriter]::new((Join-Path $taskOutput 'events.jsonl'),$false,(New-Object Text.UTF8Encoding($false)))
$taskWriter.AutoFlush=$true
$taskWatch=[Diagnostics.Stopwatch]::StartNew()
$taskCaptured=0
$taskIds=New-Object 'Collections.Generic.List[long]'
$taskFailure=$null
$taskProbeStatus=$null
$taskProbeElapsedMs=$null

function Save-T040CapiState([bool]$Completed){
    $taskRecord=@{
        Utc=[DateTime]::UtcNow.ToString('o');StartedUtc=$taskStarted.ToString('o');ObserverPid=$PID
        Completed=$Completed;Ready=-not $Completed;CapturedEvents=$taskCaptured;Failure=$taskFailure
        DurationSeconds=$DurationSeconds;PsVersion=$PSVersionTable.PSVersion.ToString()
        LogName=$taskLogName;MaximumLogSizeBytes=$taskInfo.MaximumSizeInBytes
        ExistingMsiSignatureProbe=$ValidateWithExistingMsi.IsPresent;SignatureProbeStatus=$taskProbeStatus
        SignatureProbeElapsedMs=$taskProbeElapsedMs;ProductOperationPerformed=$false
        InstallerExecutedByTool=$false;IrisUiOperatedByTool=$false;SecurityPolicyChanged=$false
        EventLogConfigurationChanged=$false;ColdInstallRootCauseProved=$false
    }
    $taskJson=$taskRecord | ConvertTo-Json -Depth 6
    $taskTemporary=Join-Path $taskOutput 'state.tmp.json'
    [IO.File]::WriteAllText($taskTemporary,$taskJson,(New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $taskTemporary -Destination (Join-Path $taskOutput 'state.json') -Force
}

function Save-T040CapiDelivery($Delivery){
    try{
        if($Delivery.SourceEventArgs.EventException){throw $Delivery.SourceEventArgs.EventException}
        $taskEntry=$Delivery.SourceEventArgs.EventRecord
        if($taskEntry){
            try{
                $taskRow=@{RecordId=$taskEntry.RecordId;EventId=$taskEntry.Id;ProcessId=$taskEntry.ProcessId;EventUtc=$taskEntry.TimeCreated.ToUniversalTime().ToString('o');ObservedUtc=[DateTime]::UtcNow.ToString('o');Xml=$taskEntry.ToXml()}
                $taskWriter.WriteLine(($taskRow | ConvertTo-Json -Compress -Depth 4))
                $script:taskCaptured++
                $taskIds.Add([long]$taskEntry.RecordId)
            }finally{$taskEntry.Dispose()}
        }
    }finally{Remove-Event -EventIdentifier $Delivery.EventIdentifier -ErrorAction SilentlyContinue}
}

try{
    $taskSubscription=Register-ObjectEvent -InputObject $taskWatcher -EventName EventRecordWritten -SourceIdentifier $taskIdentifier
    $taskWatcher.Enabled=$true
    Save-T040CapiState $false
    if($ValidateWithExistingMsi){
        $taskMsi='C:\ProgramData\Package Cache\{64BFC674-D664-480B-AFF2-D47C5531D6D2}v80.56.61678\windowsdesktop-runtime-10.0.14-win-x64.msi'
        if(-not (Test-Path -LiteralPath $taskMsi -PathType Leaf)){throw 'The known existing Microsoft MSI is absent; do not install anything for this probe'}
        $taskProbeWatch=[Diagnostics.Stopwatch]::StartNew()
        $taskSignature=Get-AuthenticodeSignature -LiteralPath $taskMsi
        $taskProbeWatch.Stop()
        $taskProbeStatus=$taskSignature.Status.ToString()
        $taskProbeElapsedMs=$taskProbeWatch.Elapsed.TotalMilliseconds
        if($taskProbeStatus -ne 'Valid'){throw 'Existing MSI signature probe did not return Valid'}
    }
    while($taskWatch.Elapsed.TotalSeconds -lt $DurationSeconds){
        $taskDelivery=Wait-Event -SourceIdentifier $taskIdentifier -Timeout 1
        if($taskDelivery){Save-T040CapiDelivery $taskDelivery}
        Save-T040CapiState $false
    }
}catch{
    $taskFailure=$_.Exception.GetType().FullName
    throw
}finally{
    $taskWatcher.Enabled=$false
    foreach($taskDelivery in @(Get-Event -SourceIdentifier $taskIdentifier -ErrorAction SilentlyContinue)){Save-T040CapiDelivery $taskDelivery}
    if($taskSubscription){Unregister-Event -SubscriptionId $taskSubscription.SubscriptionId -ErrorAction SilentlyContinue}
    $taskWatcher.Dispose()
    $taskWriter.Dispose()
    $taskWatch.Stop()
    Save-T040CapiState $true
}
$taskSortedIds=@($taskIds | Sort-Object -Unique)
$taskGaps=@()
for($taskIndex=1;$taskIndex -lt $taskSortedIds.Count;$taskIndex++){
    if($taskSortedIds[$taskIndex] -ne $taskSortedIds[$taskIndex-1]+1){$taskGaps+=@{After=$taskSortedIds[$taskIndex-1];Before=$taskSortedIds[$taskIndex]}}
}
$taskFinal=@{CapturedEvents=$taskCaptured;UniqueRecordIds=$taskSortedIds.Count;Gaps=$taskGaps;DuplicateDeliveries=$taskCaptured-$taskSortedIds.Count;ProductOperationPerformed=$false;ColdInstallRootCauseProved=$false}
[IO.File]::WriteAllText((Join-Path $taskOutput 'record-integrity.json'),($taskFinal | ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
if($ValidateWithExistingMsi -and $taskCaptured -eq 0){throw 'The controlled signature probe produced no captured events'}
Write-Host ('Saved '+$taskCaptured+' live CAPI2 events; no installer transaction was invoked by this tool.')
