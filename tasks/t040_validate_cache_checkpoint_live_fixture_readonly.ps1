$ErrorActionPreference='Stop'
if([Environment]::UserName -ne 'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskHealth=Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:18040/v1/models' -Headers @{Authorization='Bearer iris-t040-not-a-real-key'} -TimeoutSec 5
$taskModels=$taskHealth.Content | ConvertFrom-Json
if($taskHealth.StatusCode -ne 200 -or @($taskModels.data).Count -ne 1 -or $taskModels.data[0].id -ne 'iris-fixture'){throw 'Owned fixture model response differs'}
& (Join-Path $PSScriptRoot 't040_collect_cache_baseline_readonly.ps1')
$taskOutput='C:\IrisT040Output'
$taskCheckpoint=Get-ChildItem -LiteralPath $taskOutput -Directory -Filter 'cache-checkpoint-baseline-*' | Sort-Object Name | Select-Object -Last 1
$taskState=Get-Content -LiteralPath (Join-Path $taskCheckpoint.FullName 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if(-not $taskState.ServiceReady -or $taskState.Failure -or $taskState.ProviderPrefixBytes -le 0){throw 'Live fixture record prefix was not collected'}
$taskCopied=[IO.File]::ReadAllBytes((Join-Path $taskCheckpoint.FullName 'provider-requests-prefix.jsonl'))
$taskOriginal=[IO.File]::ReadAllBytes($taskState.ProviderOriginalRequestPath)
if($taskOriginal.Length -lt $taskCopied.Length){throw 'Original fixture record prefix is shorter than its copy'}
for($taskIndex=0;$taskIndex -lt $taskCopied.Length;$taskIndex++){
    if($taskCopied[$taskIndex] -ne $taskOriginal[$taskIndex]){throw 'Collected fixture prefix differs from original bytes'}
}
$taskEvents=@([Text.Encoding]::UTF8.GetString($taskCopied) -split '\r?\n' | Where-Object {$_} | ForEach-Object {$_ | ConvertFrom-Json})
if(@($taskEvents | Where-Object {$_.Kind -eq 'Models'}).Count -lt 1){throw 'Standalone fixture health request is missing'}
$taskRecord=@{
    Utc=[DateTime]::UtcNow.ToString('o');ToolValidation=$true;PsVersion=$PSVersionTable.PSVersion.ToString()
    LiveFixtureResponseStatus=200;CopiedPrefixMatchesOriginalBytes=$true;PrefixBytes=$taskCopied.Length
    ValidatedBaselinePath=$taskCheckpoint.FullName;ProductOperationPerformed=$false
    ActualManualCacheStageStarted=$false;ActualIrisCacheBehaviorProved=$false
    HealthRequestWasStandaloneFixtureProbe=$true;GcRequested=$false;WorkingSetTrimmed=$false
}
[IO.File]::WriteAllText((Join-Path $taskOutput ('cache-live-fixture-validation-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')+'.json')),($taskRecord | ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
Write-Host 'Live fixture and exact record-prefix collection validated; Iris cache UI remains untested.'
