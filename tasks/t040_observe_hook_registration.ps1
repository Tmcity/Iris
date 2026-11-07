$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned Sandbox only'}
$taskTarget=Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskApp=Get-CimInstance Win32_Process -Filter 'ProcessId=6040'
if(-not$taskApp-or$taskApp.ExecutablePath-ne$taskTarget){throw 'Current Iris6040 required'}
$taskSettings=Join-Path $env:APPDATA 'Iris\settings.json'
$taskBefore=Get-Content -LiteralPath $taskSettings -Raw -Encoding UTF8|ConvertFrom-Json
if($taskBefore.selection.mouseMode-ne'Off'){throw 'Initial Off mode required'}
function Toggle-OwnedHook{$taskCli=Start-Process -FilePath $taskTarget -ArgumentList @('--action','ToggleMouseSelection') -WindowStyle Hidden -PassThru;$null=$taskCli.Handle;if(-not$taskCli.WaitForExit(15000)){throw 'Exact CLI remains alive'};if($taskCli.ExitCode-ne0){throw 'CLI did not confirm success'};$taskCli.Dispose();Start-Sleep -Seconds 2}
Toggle-OwnedHook
& (Join-Path $PSScriptRoot 'tools\hook-state\HookStateProbe.exe') '6040'
if($LASTEXITCODE-ne0){throw 'Read-only enabled-hook state probe failed; retain mode for inspection'}
Toggle-OwnedHook
& (Join-Path $PSScriptRoot 'tools\hook-state\HookStateProbe.exe') '6040'
if($LASTEXITCODE-ne0){throw 'Read-only restored-hook state probe failed'}
$taskAfter=Get-Content -LiteralPath $taskSettings -Raw -Encoding UTF8|ConvertFrom-Json
if($taskAfter.selection.mouseMode-ne'Off'){throw 'Off mode not restored'}
