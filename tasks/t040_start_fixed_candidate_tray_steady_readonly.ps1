$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskExpected = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'candidate-identity.json')) | ConvertFrom-Json
if ($taskExpected.Version -ne '0.2.12') { throw 'Fixed candidate only' }
$taskLease = Join-Path 'C:\IrisT040Output' ('fixed-runtime-lease-' + $taskExpected.ManifestSha256.ToLowerInvariant() + '.json')
if (-not (Test-Path -LiteralPath $taskLease)) { throw 'Matching bounded natural-GC observation required' }
$taskOut = Join-Path 'C:\IrisT040Output' ('fixed-steady-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
$null = New-Item -ItemType Directory -Path $taskOut
$taskScript = Join-Path $PSScriptRoot 't040_fixed_candidate_tray_steady_readonly.ps1'
$taskProcess = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$taskScript,'-TraceLeasePath',$taskLease,'-OutputDirectory',$taskOut) -WindowStyle Hidden -RedirectStandardOutput (Join-Path $taskOut 'worker-stdout.txt') -RedirectStandardError (Join-Path $taskOut 'worker-stderr.txt') -PassThru
$taskRecord = @{ StartedUtc = [DateTime]::UtcNow.ToString('o'); WorkerPid = $taskProcess.Id; WorkerCreatedUtc = $taskProcess.StartTime.ToUniversalTime().ToString('o'); WorkerScript = $taskScript; WorkerSha256 = (Get-FileHash -LiteralPath $taskScript -Algorithm SHA256).Hash; TraceLeasePath = $taskLease; ProductOperationPerformed = $false; GcRequested = $false; WorkingSetTrimmed = $false }
[IO.File]::WriteAllText((Join-Path $taskOut 'launch.json'), ($taskRecord | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
$taskProcess.Dispose()
Write-Output $taskOut
