param([Parameter(Mandatory = $true)][string]$ReceiptPath, [Parameter(Mandatory = $true)][string]$TraceLeasePath)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 't040_r2r_candidate_gate.psm1') -Force
$null = Get-T040VerifiedR2rCandidate -ReceiptPath $ReceiptPath
$taskOut = Join-Path 'C:\IrisT040Output' ('r2r-steady-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
$null = New-Item -ItemType Directory -Path $taskOut
$taskScript = Join-Path $PSScriptRoot 't040_r2r_tray_steady_readonly.ps1'
$taskProcess = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$taskScript,'-ReceiptPath',$ReceiptPath,'-TraceLeasePath',$TraceLeasePath,'-OutputDirectory',$taskOut) -WindowStyle Hidden -RedirectStandardOutput (Join-Path $taskOut 'worker-stdout.txt') -RedirectStandardError (Join-Path $taskOut 'worker-stderr.txt') -PassThru
$taskRecord = @{ StartedUtc = [DateTime]::UtcNow.ToString('o'); WorkerPid = $taskProcess.Id; WorkerCreatedUtc = $taskProcess.StartTime.ToUniversalTime().ToString('o'); WorkerScript = $taskScript; WorkerSha256 = (Get-FileHash -LiteralPath $taskScript -Algorithm SHA256).Hash; ReceiptPath = $ReceiptPath; TraceLeasePath = $TraceLeasePath; ProductOperationPerformed = $false }
[IO.File]::WriteAllText((Join-Path $taskOut 'launch.json'), ($taskRecord | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
$taskProcess.Dispose()
Write-Output $taskOut
