$ErrorActionPreference='Stop'
if([Environment]::UserName -ne 'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskTarget=Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskApps=@(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object {$_.ExecutablePath -eq $taskTarget})
if($taskApps.Count -ne 1 -or $taskApps[0].ProcessId -ne 476){throw 'Keep the same saved candidate instance'}
& (Join-Path $PSScriptRoot 'tools\native-heap-metadata\NativeHeapMetadataProbe.exe') ([string]$taskApps[0].ProcessId)
if($LASTEXITCODE -ne 0){throw 'Read-only native heap metadata failed; preserve its output'}
