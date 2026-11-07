param([ValidateSet('baseline','prime','cache','retry')][string]$Phase='baseline')
$ErrorActionPreference='Stop'
if([Environment]::UserName -ne 'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskStarted=[DateTime]::UtcNow
$taskRoot=Join-Path $env:LOCALAPPDATA 'Iris'
$taskTarget=Join-Path $taskRoot 'current\Iris.App.exe'
$taskApps=@(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object {$_.ExecutablePath -eq $taskTarget})
if($taskApps.Count -ne 1 -or $taskApps[0].ProcessId -ne 476){throw 'Keep the same candidate Iris PID476; no Iris operation was performed'}
$taskIdentity=Get-Content -LiteralPath (Join-Path $taskRoot 'current\release-source.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if($taskIdentity.Version -ne '0.2.11'){throw 'Expected local candidate 0.2.11'}
$taskManifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'LOCAL_0211_payload-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskIdentityFiles=@('Iris.App.exe','Iris.App.dll','Iris.Core.dll','Iris.Providers.dll')
$taskIdentityRows=@()
foreach($taskName in $taskIdentityFiles){
    $taskExpected=@($taskManifest | Where-Object {$_.Path -eq $taskName})
    if($taskExpected.Count -ne 1){throw 'Candidate identity manifest entry is missing'}
    $taskFile=Join-Path (Join-Path $taskRoot 'current') $taskName
    $taskHash=(Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash
    $taskBytes=(Get-Item -LiteralPath $taskFile).Length
    $taskMatches=$taskHash -eq $taskExpected[0].Sha256 -and $taskBytes -eq $taskExpected[0].Bytes
    if(-not $taskMatches){throw 'Candidate identity differs from the reviewed package'}
    $taskIdentityRows+=@{Path=$taskName;Bytes=$taskBytes;Sha256=$taskHash;Matches=$true}
}
$taskOutput='C:\IrisT040Output'
$taskStamp=$taskStarted.ToString('yyyyMMddTHHmmssfffffffZ')
$taskDestination=Join-Path $taskOutput ('cache-checkpoint-'+$Phase+'-'+$taskStamp)
$null=New-Item -ItemType Directory -Path $taskDestination
$taskProviders=@(Get-CimInstance Win32_Process -Filter "Name='T040SyntheticProvider.exe'" | Where-Object {
    $_.ExecutablePath -match '^C:\\IrisT040[A-Za-z0-9]+\\tools\\synthetic-provider\\T040SyntheticProvider\.exe$'
})
$taskFixtureStatePath=$null
if($taskProviders.Count -eq 1){
    foreach($taskFolder in @(Get-ChildItem -LiteralPath $taskOutput -Directory -Filter 'synthetic-provider-*' | Sort-Object Name -Descending)){
        $taskCandidateState=Join-Path $taskFolder.FullName 'state.json'
        if(-not (Test-Path -LiteralPath $taskCandidateState -PathType Leaf)){continue}
        $taskFixtureState=Get-Content -LiteralPath $taskCandidateState -Raw -Encoding UTF8 | ConvertFrom-Json
        if($taskFixtureState.Pid -eq $taskProviders[0].ProcessId -and $taskFixtureState.Ready -and $taskFixtureState.Prefix -eq 'http://127.0.0.1:18040/'){
            $taskFixtureStatePath=$taskCandidateState
            break
        }
    }
}
$taskServiceReady=$taskProviders.Count -eq 1 -and $null -ne $taskFixtureStatePath
$taskRecord=@{
    StartedUtc=$taskStarted.ToString('o');Phase=$Phase;AppPid=476;Version=$taskIdentity.Version
    ExpectedSourceCommit='dccc0dbd3054122e34e27842d55c6585ff953812';IdentityFilesVerified=$taskIdentityRows
    ProductOperationPerformed=$false;IrisUiActionPerformed=$false;CacheBehaviorProved=$false
    PreparationBaseline=$Phase -eq 'baseline';ServiceReady=$taskServiceReady
    ProviderProcesses=$taskProviders;ProviderOriginalStatePath=$taskFixtureStatePath
    GcRequested=$false;WorkingSetTrimmed=$false;SnapshotPaths=@();Failure=$null
}
try{
    if($Phase -ne 'baseline' -and -not $taskServiceReady){throw 'Owned fixture is absent; wait for the assistant to restore it before this feature stage'}
    & (Join-Path $PSScriptRoot 't040_save_feature_state_readonly.ps1')
    $taskData=Get-ChildItem -LiteralPath $taskOutput -Directory -Filter 'feature-data-*' | Sort-Object Name | Select-Object -Last 1
    $taskState=Get-Content -LiteralPath (Join-Path $taskData.FullName 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if([DateTimeOffset]::Parse($taskState.Utc).UtcDateTime -lt $taskStarted){throw 'Feature data snapshot is stale'}
    if(-not $taskState.SqliteReadOnly -or $taskState.SecretsDecrypted -or $taskState.SqliteFailureCode){throw 'Feature data snapshot did not meet the read-only boundary'}
    $taskLog=Get-ChildItem -LiteralPath $taskOutput -Directory -Filter 'readonly-state-*' | Sort-Object Name | Select-Object -Last 1
    $taskRecord.SnapshotPaths=@($taskData.FullName,$taskLog.FullName)
    if($taskServiceReady){
        $taskFixtureFolder=Split-Path -Parent $taskFixtureStatePath
        $taskRequestPath=Join-Path $taskFixtureFolder 'requests.jsonl'
        $taskStream=[IO.File]::Open($taskRequestPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
        try{
            $taskBuffer=New-Object IO.MemoryStream
            try{$taskStream.CopyTo($taskBuffer);$taskRaw=$taskBuffer.ToArray()}finally{$taskBuffer.Dispose()}
        }finally{$taskStream.Dispose()}
        $taskPrefixLength=0
        for($taskIndex=$taskRaw.Length-1;$taskIndex -ge 0;$taskIndex--){if($taskRaw[$taskIndex] -eq 10){$taskPrefixLength=$taskIndex+1;break}}
        $taskPrefix=New-Object byte[] $taskPrefixLength
        if($taskPrefixLength -gt 0){[Array]::Copy($taskRaw,$taskPrefix,$taskPrefixLength)}
        [IO.File]::WriteAllBytes((Join-Path $taskDestination 'provider-requests-prefix.jsonl'),$taskPrefix)
        [IO.File]::WriteAllBytes((Join-Path $taskDestination 'provider-state.json'),[IO.File]::ReadAllBytes($taskFixtureStatePath))
        $taskRecord.ProviderOriginalRequestPath=$taskRequestPath
        $taskRecord.ProviderPrefixBytes=$taskPrefixLength
    }
}catch{
    $taskRecord.Failure=$_.Exception.GetType().FullName
    throw
}finally{
    $taskRecord.FinishedUtc=[DateTime]::UtcNow.ToString('o')
    [IO.File]::WriteAllText((Join-Path $taskDestination 'state.json'),($taskRecord | ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($false)))
}
Write-Host ('Saved '+$Phase+' evidence; observed UI behavior still requires independent review.')
