$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskExpected=Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskApps=@(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object {$_.ExecutablePath-eq$taskExpected})
if($taskApps.Count-ne1){throw 'Expected owned Iris process is absent'}
& (Join-Path $PSScriptRoot 'tools\jit-rundown\JitRundownProbe.exe') ([string]$taskApps[0].ProcessId)
if($LASTEXITCODE-ne0){throw 'Runtime code metadata probe failed; keep original output'}
