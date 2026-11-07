$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskPath=Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskApps=@(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object {$_.ExecutablePath-eq$taskPath})
if($taskApps.Count-ne1){throw 'Expected owned Iris is absent'}
& (Join-Path $PSScriptRoot 'tools\heap-metadata\HeapMetadataProbe.exe') ([string]$taskApps[0].ProcessId)
if($LASTEXITCODE-ne0){throw 'Read-only heap metadata probe failed; keep original output'}
