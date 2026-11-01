$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Run only inside the acceptance Sandbox' }
$taskRoot = 'C:\IrisAcceptanceOutput'
$taskSession = Join-Path $taskRoot ('session-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $taskSession
$taskShell = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
$taskProvider = Start-Process -FilePath $taskShell -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File','C:\IrisAcceptanceInput\manual-provider.ps1','-OutputDirectory',$taskSession) -WindowStyle Hidden -PassThru
$taskWatcher = Start-Process -FilePath $taskShell -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File','C:\IrisAcceptanceInput\manual-process-watch.ps1','-OutputDirectory',$taskSession) -WindowStyle Hidden -PassThru
$taskState = [ordered]@{ SessionDirectory = $taskSession; StartedUtc = [DateTime]::UtcNow.ToString('o'); ProviderPid = $taskProvider.Id; WatcherPid = $taskWatcher.Id; Scope = 'Owned synthetic translation fixture and read-only process evidence; updates use production GitHub' }
[IO.File]::WriteAllText((Join-Path $taskRoot 'active-session.json'), ($taskState | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
$taskState | ConvertTo-Json -Depth 4
Write-Output 'Check provider-status.json in the session directory before configuring the synthetic service.'
