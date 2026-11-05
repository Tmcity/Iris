$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw'Owned acceptance Sandbox only'}
$taskSession=Get-Content -LiteralPath 'C:\IrisAcceptanceOutput\active-session.json' -Raw -Encoding UTF8|ConvertFrom-Json
$taskOut=[IO.Path]::GetFullPath($taskSession.SessionDirectory)
if(-not $taskOut.StartsWith('C:\IrisAcceptanceOutput\',[StringComparison]::OrdinalIgnoreCase)){throw'Unexpected output path'}
$taskRows=@(@('Save-Uninstalled-V0210-State.cmd','Capture-Uninstalled-V0210-View.cmd')|ForEach-Object{$taskPath=Join-Path 'C:\IrisAcceptanceUninstall0210' $_;@{Name=$_;Exists=(Test-Path -LiteralPath $taskPath -PathType Leaf);Sha256=(Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash.ToLowerInvariant()}})
$taskProcesses=@(Get-CimInstance Win32_Process|Where-Object{$_.ProcessId-in@(8980,3968,5516,5672)}|ForEach-Object{@{Pid=$_.ProcessId;Name=$_.Name;Path=$_.ExecutablePath;StartedUtc=$_.CreationDate.ToUniversalTime().ToString('o')}})
$taskResult=[ordered]@{Utc=[DateTime]::UtcNow.ToString('o');ProductOperationPerformed=$false;InputRoot='C:\IrisAcceptanceUninstall0210';Rows=$taskRows;Processes=$taskProcesses}
$taskPath=Join-Path $taskOut ('uninstall-v0210-input-inspection-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')+'.json')
[IO.File]::WriteAllText($taskPath,($taskResult|ConvertTo-Json -Depth 5),(New-Object Text.UTF8Encoding($false)))
Write-Output $taskPath
