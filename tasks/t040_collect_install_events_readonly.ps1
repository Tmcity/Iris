$ErrorActionPreference='Stop'
if([Environment]::UserName -ne 'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskDestination=Join-Path 'C:\IrisT040Output' ('install-system-events-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
$null=New-Item -ItemType Directory -Path $taskDestination
$taskStart=[DateTimeOffset]::Parse('2026-11-06T17:26:00Z').LocalDateTime
$taskEnd=[DateTimeOffset]::Parse('2026-11-06T17:47:00Z').LocalDateTime
$taskLogs=@('System','Application','Microsoft-Windows-AppLocker/MSI and Script','Microsoft-Windows-AppLocker/EXE and DLL','Microsoft-Windows-CodeIntegrity/Operational','Microsoft-Windows-Windows Defender/Operational','Microsoft-Windows-CAPI2/Operational')
$taskSummary=@()
foreach($taskLog in $taskLogs){
    $taskFailure=$null; $taskEvents=@(); $taskEnabled=$null
    try{
        $taskInfo=Get-WinEvent -ListLog $taskLog
        $taskEnabled=$taskInfo.IsEnabled
        try{$taskEvents=@(Get-WinEvent -FilterHashtable @{LogName=$taskLog;StartTime=$taskStart;EndTime=$taskEnd} -MaxEvents 1500 -ErrorAction Stop)}
        catch{if($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*'){throw}}
    }catch{$taskFailure=@{Type=$_.Exception.GetType().FullName;Id=$_.FullyQualifiedErrorId}}
    $taskSafeName=$taskLog.Replace('/','-').Replace(' ','_')
    $taskXml='<Events>'+[String]::Join("`n",[string[]]@($taskEvents | ForEach-Object {$_.ToXml()}))+'</Events>'
    [IO.File]::WriteAllText((Join-Path $taskDestination ($taskSafeName+'.xml')),$taskXml,(New-Object Text.UTF8Encoding($false)))
    $taskSummary+=@{LogName=$taskLog;Enabled=$taskEnabled;Events=$taskEvents.Count;Failure=$taskFailure}
}
$taskState=@{Utc=[DateTime]::UtcNow.ToString('o');ProductOperationPerformed=$false;SecurityPolicyChanged=$false;StartUtc='2026-11-06T17:26:00Z';EndUtc='2026-11-06T17:47:00Z';Logs=$taskSummary}
[IO.File]::WriteAllText((Join-Path $taskDestination 'state.json'),($taskState | ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
