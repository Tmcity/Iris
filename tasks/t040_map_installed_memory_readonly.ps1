$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskExpected=Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskTarget=@(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object {$_.ExecutablePath-eq$taskExpected})
if($taskTarget.Count-ne1){throw 'Expected exactly one installed owned Iris process'}
& (Join-Path $PSScriptRoot 'tools\memory-map\MemoryMapProbe.exe') ([string]$taskTarget[0].ProcessId)
if($LASTEXITCODE-ne0){throw 'Read-only memory metadata probe failed; preserve diagnostics'}
