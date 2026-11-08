$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskStamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
$taskDirectory = Join-Path 'C:\IrisT040Output' ('modifier-delivery-hook-' + $taskStamp)
$null = New-Item -ItemType Directory -Path $taskDirectory
$taskWorker = Join-Path $PSScriptRoot 't040_keyboard_delivery_worker_readonly.ps1'
$taskProcess = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$taskWorker,'-OutputDirectory',$taskDirectory) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskDirectory 'stdout.txt') -RedirectStandardError (Join-Path $taskDirectory 'stderr.txt')
$null = $taskProcess.Handle
$taskIdentity = @{ Pid=$taskProcess.Id; CreatedUtc=$taskProcess.StartTime.ToUniversalTime().ToString('o'); OutputDirectory=$taskDirectory; Script=$taskWorker; ScriptSha256=(Get-FileHash -LiteralPath $taskWorker -Algorithm SHA256).Hash; DurationSeconds=20; ConsumesInput=$false; ClipboardContentRead=$false; ForegroundInputInjected=$false; ProductOperationPerformed=$false }
[IO.File]::WriteAllText((Join-Path $taskDirectory 'launch.json'), ($taskIdentity | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
$taskDeadline = [DateTime]::UtcNow.AddSeconds(5)
while (-not (Test-Path -LiteralPath (Join-Path $taskDirectory 'ready.json'))) {
    if ($taskProcess.HasExited -or [DateTime]::UtcNow -gt $taskDeadline) { throw 'Bounded observer not ready; inspect exact identity and stderr before retry' }
    Start-Sleep -Milliseconds 50
}
Write-Output $taskDirectory
$taskProcess.Dispose()
