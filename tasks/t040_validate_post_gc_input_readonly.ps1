$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskRoot='C:\IrisT040PostGcInput'
$taskRows=@()
foreach($taskName in @('Save-Post-GC-Steady-State.cmd','Capture-Feature-View.cmd','t040_save_post_gc_steady_readonly.ps1','t040_observe_stage1_views_readonly.ps1')){
    $taskFile=Join-Path $taskRoot $taskName
    if(-not(Test-Path -LiteralPath $taskFile)){throw ('Missing prepared input: '+$taskName)}
    $taskErrors=$null;$taskTokens=$null
    $taskErrorCount=$null
    if($taskFile.EndsWith('.ps1')){$null=[Management.Automation.Language.Parser]::ParseFile($taskFile,[ref]$taskTokens,[ref]$taskErrors);$taskErrorCount=@($taskErrors).Count;if($taskErrorCount-ne0){throw 'Post-GC PS5.1 parse failure'}}
    $taskRows+=@{Name=$taskName;ParseErrors=$taskErrorCount;Sha256=(Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash}
}
$taskTraceState=Get-Content -LiteralPath 'C:\IrisT040Output\runtime-counters-20261107T0537257455600Z\state.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskActualTrace=@(Get-CimInstance Win32_Process -Filter "Name='RuntimeCounterProbe.exe'")
if(-not$taskTraceState.Completed-or$taskTraceState.Failure-or$taskTraceState.NaturalFullGcStarts-lt1-or$taskActualTrace.Count-ne0){throw 'Runtime trace has not completed cleanly'}
$taskRecord=@{Utc=[DateTime]::UtcNow.ToString('o');ProductOperationPerformed=$false;InputRoot=$taskRoot;Scripts=$taskRows;TraceCompleted=$true;TraceActualProcessAbsent=$true;NaturalFullGcStarts=$taskTraceState.NaturalFullGcStarts;PsVersion=$PSVersionTable.PSVersion.ToString()}
[IO.File]::WriteAllText(('C:\IrisT040Output\post-gc-input-ready-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')+'.json'),($taskRecord | ConvertTo-Json -Depth 7),(New-Object Text.UTF8Encoding($false)))
