$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskOut = Join-Path 'C:\IrisT040Output' ('r2r-cpu-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
$null = New-Item -ItemType Directory -Path $taskOut
$taskWorker = Join-Path $PSScriptRoot 't040_compare_r2r_with_cpu.ps1'
$taskProcess = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $taskWorker, '-OutputDirectory', $taskOut) -WindowStyle Hidden -PassThru
$taskRecord = @{
    StartedUtc = [DateTime]::UtcNow.ToString('o'); WorkerPid = $taskProcess.Id
    WorkerCreatedUtc = $taskProcess.StartTime.ToUniversalTime().ToString('o')
    OutputDirectory = $taskOut; WorkerScript = $taskWorker
    WorkerSha256 = (Get-FileHash -LiteralPath $taskWorker -Algorithm SHA256).Hash
    ProductOperationPerformed = $true; InstallerExecuted = $false
}
[IO.File]::WriteAllText((Join-Path $taskOut 'launch.json'), ($taskRecord | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
$taskProcess.Dispose()
Write-Output $taskOut
