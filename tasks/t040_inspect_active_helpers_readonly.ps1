$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskNames=@('Iris.App.exe','T040SyntheticProvider.exe','RuntimeCounterProbe.exe','powershell.exe')
$taskProcesses=@(Get-CimInstance Win32_Process | Where-Object {
    $taskNames -contains $_.Name -and ($_.Name-ne'powershell.exe'-or$_.CommandLine-like'*observe-process-metrics*readonly.ps1*')
} | Select-Object ProcessId,ParentProcessId,Name,CreationDate,ExecutablePath,CommandLine)
$taskState=@{Utc=[DateTime]::UtcNow.ToString('o');Processes=$taskProcesses;ProductOperationPerformed=$false}
$taskPath='C:\IrisT040Output\actual-helper-processes-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')+'.json'
[IO.File]::WriteAllText($taskPath,($taskState | ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
