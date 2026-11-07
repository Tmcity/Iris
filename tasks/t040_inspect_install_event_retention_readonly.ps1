$ErrorActionPreference='Stop'
if([Environment]::UserName -ne 'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskOutput=Join-Path 'C:\IrisT040Output' ('install-event-retention-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
$null=New-Item -ItemType Directory -Path $taskOutput
$taskStart=[DateTimeOffset]::Parse('2026-11-06T17:26:00Z').LocalDateTime
$taskEnd=[DateTimeOffset]::Parse('2026-11-06T17:47:00Z').LocalDateTime
$taskRows=@()
foreach($taskLog in @('Microsoft-Windows-CAPI2/Operational','Microsoft-Windows-AppLocker/MSI and Script','System')){
    $taskRow=@{LogName=$taskLog;Failure=$null;Enabled=$null;RecordCount=$null;OldestUtc=$null;NewestUtc=$null;WindowEvents=0;WindowLimitReached=$false}
    $taskEvents=@()
    try{
        $taskInfo=Get-WinEvent -ListLog $taskLog -ErrorAction Stop
        $taskRow.Enabled=$taskInfo.IsEnabled
        $taskRow.RecordCount=$taskInfo.RecordCount
        $taskRow.MaximumSizeInBytes=$taskInfo.MaximumSizeInBytes
        $taskRow.LogMode=$taskInfo.LogMode.ToString()
        if($taskInfo.RecordCount -gt 0){
            $taskOldest=Get-WinEvent -LogName $taskLog -Oldest -MaxEvents 1 -ErrorAction Stop
            $taskNewest=Get-WinEvent -LogName $taskLog -MaxEvents 1 -ErrorAction Stop
            $taskRow.OldestUtc=$taskOldest.TimeCreated.ToUniversalTime().ToString('o')
            $taskRow.NewestUtc=$taskNewest.TimeCreated.ToUniversalTime().ToString('o')
        }
        try{$taskEvents=@(Get-WinEvent -FilterHashtable @{LogName=$taskLog;StartTime=$taskStart;EndTime=$taskEnd} -MaxEvents 2000 -ErrorAction Stop)}
        catch{if($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*'){throw}}
        $taskRow.WindowEvents=$taskEvents.Count
        $taskRow.WindowLimitReached=$taskEvents.Count -eq 2000
    }catch{$taskRow.Failure=@{Type=$_.Exception.GetType().FullName;Id=$_.FullyQualifiedErrorId}}
    $taskXml='<Events>'+[String]::Join("`n",[string[]]@($taskEvents | ForEach-Object {$_.ToXml()}))+'</Events>'
    $taskName=$taskLog.Replace('/','-').Replace(' ','_')+'.xml'
    [IO.File]::WriteAllText((Join-Path $taskOutput $taskName),$taskXml,(New-Object Text.UTF8Encoding($false)))
    $taskRows+=$taskRow
}
$taskRecord=@{Utc=[DateTime]::UtcNow.ToString('o');PsVersion=$PSVersionTable.PSVersion.ToString();StartUtc='2026-11-06T17:26:00Z';EndUtc='2026-11-06T17:47:00Z';Logs=$taskRows;ProductOperationPerformed=$false;SecurityPolicyChanged=$false;EventLogConfigurationChanged=$false;InstallerExecuted=$false}
[IO.File]::WriteAllText((Join-Path $taskOutput 'state.json'),($taskRecord | ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
Write-Host 'Existing installation event retention inspected; no policy or log settings were changed.'
