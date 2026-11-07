$ErrorActionPreference='Stop'
if([Environment]::UserName -ne 'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
if(@(Get-CimInstance Win32_Process -Filter "Name='T040SyntheticProvider.exe'").Count -ne 0){throw 'Expected the already terminated owned fixture to be absent for this guard validation'}
$taskGuardTriggered=$false
try{
    & (Join-Path $PSScriptRoot 't040_save_cache_checkpoint_readonly.ps1') -Phase prime
}catch{
    if($_.Exception.Message -eq 'Owned fixture is absent; wait for the assistant to restore it before this feature stage'){$taskGuardTriggered=$true}else{throw}
}
if(-not $taskGuardTriggered){throw 'Missing-fixture guard did not reject the feature stage'}
$taskOutput='C:\IrisT040Output'
$taskCheckpoint=Get-ChildItem -LiteralPath $taskOutput -Directory -Filter 'cache-checkpoint-prime-*' | Sort-Object Name | Select-Object -Last 1
$taskState=Get-Content -LiteralPath (Join-Path $taskCheckpoint.FullName 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if($taskState.ServiceReady -or -not $taskState.Failure -or @($taskState.SnapshotPaths).Count -ne 0){throw 'The rejected phase unexpectedly collected feature-stage evidence'}
$taskRecord=@{Utc=[DateTime]::UtcNow.ToString('o');ToolValidation=$true;ExpectedGuardTriggered=$true;RejectedCheckpointPath=$taskCheckpoint.FullName;ProductOperationPerformed=$false;ActualManualCacheStageStarted=$false;FeatureSnapshotCount=0}
[IO.File]::WriteAllText((Join-Path $taskOutput ('cache-service-guard-validation-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')+'.json')),($taskRecord | ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
Write-Host 'Missing-fixture guard validated; no cache UI action or feature-stage collection occurred.'
