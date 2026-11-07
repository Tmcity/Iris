$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskExpectedPid=476
$taskTarget=Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskApps=@(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object {$_.ExecutablePath-eq$taskTarget})
if($taskApps.Count-ne1-or$taskApps[0].ProcessId-ne$taskExpectedPid){throw 'The same GC-observed Iris instance is required; keep its state'}
$taskTrace='C:\IrisT040Output\runtime-counters-20261107T0833567533925Z'
$taskFinal=Get-Content -LiteralPath (Join-Path $taskTrace 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if(-not$taskFinal.Completed-or$taskFinal.Failure-or$taskFinal.Pid-ne$taskExpectedPid-or$taskFinal.NaturalFullGcStarts-lt1){throw 'Completed natural full-GC evidence is required'}
$taskTraceProcess=Get-CimInstance Win32_Process -Filter "Name='RuntimeCounterProbe.exe'" | Where-Object {$_.CommandLine-like'* 476 *'}
if($taskTraceProcess){throw 'Runtime trace is still active; preserve diagnostics'}
$taskFinished=[DateTimeOffset]::Parse($taskFinal.Utc).UtcDateTime
if(([DateTime]::UtcNow-$taskFinished).TotalSeconds-lt30){throw 'Wait at least 30 seconds after the runtime trace ends'}
& (Join-Path $PSScriptRoot 't040_observe_stage1_views_readonly.ps1')
$taskView=Get-ChildItem -LiteralPath 'C:\IrisT040Output' -File -Filter 'stage1-view-metadata-*.json' | Sort-Object Name | Select-Object -Last 1
$taskMetadata=Get-Content -LiteralPath $taskView.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
if(@($taskMetadata.VisibleIrisWindowTitles).Count-ne0){throw 'Iris functional windows remain visible; close them and keep the tray instance'}
& (Join-Path $PSScriptRoot 'save-metrics-state-v2-readonly.ps1')
& (Join-Path $PSScriptRoot 't040_collect_current_readonly.ps1')
$taskRecord=@{Utc=[DateTime]::UtcNow.ToString('o');Checkpoint='manual-post-natural-full-gc-tray-idle';AppPid=$taskExpectedPid;UserRequestedIdleSeconds=45;NaturalFullGcStarts=$taskFinal.NaturalFullGcStarts;TraceCompletedUtc=$taskFinal.Utc;TraceProcessAbsent=$true;VisibleIrisWindowCount=0;WindowMetadataPath=$taskView.FullName;ProductOperationPerformed=$false;GcRequested=$false;WorkingSetTrimmed=$false}
[IO.File]::WriteAllText(('C:\IrisT040Output\candidate-post-gc-steady-checkpoint-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')+'.json'),($taskRecord | ConvertTo-Json -Depth 7),(New-Object Text.UTF8Encoding($false)))
Write-Host 'Saved post-GC tray-idle checkpoint; no Iris action or GC request was performed.'
