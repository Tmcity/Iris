$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskRoot='C:\IrisT040Features'
$taskNames=@('Capture-Feature-View.cmd','Save-Feature-State.cmd','t040_capture_feature_view_manual.ps1','t040_save_feature_state_readonly.ps1','tools\state-snapshot\T040StateProbe.exe')
$taskRows=@()
foreach($taskName in $taskNames){
    $taskPath=Join-Path $taskRoot $taskName
    if(-not(Test-Path -LiteralPath $taskPath -PathType Leaf)){throw ('Missing stage1 input: '+$taskName)}
    $taskErrors=$null;$taskTokens=$null
    if($taskPath.EndsWith('.ps1')){$null=[Management.Automation.Language.Parser]::ParseFile($taskPath,[ref]$taskTokens,[ref]$taskErrors);if(@($taskErrors).Count-ne0){throw 'Stage1 PS5.1 parse failure'}}
    $taskRows+=@{Name=$taskName;Sha256=(Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash;ParseErrors=@($taskErrors).Count}
}
$taskFixture=@(Get-CimInstance Win32_Process -Filter "Name='T040SyntheticProvider.exe'" | Where-Object {$_.ExecutablePath-like'C:\IrisT040*\tools\synthetic-provider\T040SyntheticProvider.exe'})
if($taskFixture.Count-ne1){throw 'Expected live owned fixture is absent'}
$taskHeaders=@{Authorization='Bearer iris-t040-not-a-real-key'}
$taskResponse=Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:18040/v1/models' -Headers $taskHeaders -TimeoutSec 10
if($taskResponse.StatusCode-ne200){throw 'Owned fixture model readiness failed'}
$taskRecord=@{Utc=[DateTime]::UtcNow.ToString('o');ProductOperationPerformed=$false;PsVersion=$PSVersionTable.PSVersion.ToString();FixtureActualPid=$taskFixture[0].ProcessId;FixtureModelHttpStatus=$taskResponse.StatusCode;Scripts=$taskRows}
[IO.File]::WriteAllText(('C:\IrisT040Output\stage1-input-ready-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')+'.json'),($taskRecord | ConvertTo-Json -Depth 7),(New-Object Text.UTF8Encoding($false)))
