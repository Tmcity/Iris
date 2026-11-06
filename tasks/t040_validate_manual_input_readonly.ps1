$ErrorActionPreference='Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned T-040 Sandbox only' }
$taskNames=@('t040_save_clean_idle_readonly.ps1','t040_collect_current_readonly.ps1','save-metrics-state-v2-readonly.ps1','observe-process-metrics-v2-readonly.ps1')
$taskRows=@()
foreach($taskName in $taskNames){
    $taskPath=Join-Path $PSScriptRoot $taskName
    $taskTokens=$null; $taskErrors=$null
    $null=[Management.Automation.Language.Parser]::ParseFile($taskPath,[ref]$taskTokens,[ref]$taskErrors)
    $taskRows+=@{Name=$taskName;ParseErrors=@($taskErrors).Count;Sha256=(Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash}
    if(@($taskErrors).Count -ne 0){throw ('PS5.1 parse failure: '+$taskName)}
}
$taskPointer=Get-Content -LiteralPath 'C:\IrisT040Output\active-metrics.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskActual=Get-CimInstance Win32_Process -Filter ('ProcessId='+[int]$taskPointer.ObserverPid)
if($null -eq $taskActual -or $taskActual.CommandLine -notlike '*observe-process-metrics-v2-readonly.ps1*'){throw 'Expected observer is not alive'}
$taskRecord=@{Utc=[DateTime]::UtcNow.ToString('o');PsVersion=$PSVersionTable.PSVersion.ToString();ProductOperationPerformed=$false;Scripts=$taskRows;ObserverAlive=$true;ObserverPid=$taskActual.ProcessId;ManualWrapperPresent=(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Save-Clean-Idle-State.cmd'))}
$taskOut=Join-Path 'C:\IrisT040Output' ('manual-input-validated-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')+'.json')
[IO.File]::WriteAllText($taskOut,($taskRecord | ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
