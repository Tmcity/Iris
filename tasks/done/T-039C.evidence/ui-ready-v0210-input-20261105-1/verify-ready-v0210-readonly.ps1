$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned acceptance Sandbox only' }
$taskState = Get-Content -LiteralPath 'C:\IrisAcceptanceOutput\active-session.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskOut = [IO.Path]::GetFullPath($taskState.SessionDirectory)
if (-not $taskOut.StartsWith('C:\IrisAcceptanceOutput\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected output path' }
$taskRoot = Join-Path $env:LOCALAPPDATA 'Iris'
$taskSource = Get-Content -LiteralPath (Join-Path $taskRoot 'current\release-source.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($taskSource.Version -ne '0.2.9') { throw 'Download boundary must still be running the original 0.2.9' }
$taskPackage = Join-Path $taskRoot 'packages\Iris-0.2.10-full.nupkg'
$taskStream = [IO.File]::Open($taskPackage,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
$taskHasher = [Security.Cryptography.SHA256]::Create()
try { $taskHash = [BitConverter]::ToString($taskHasher.ComputeHash($taskStream)).Replace('-','').ToLowerInvariant() }
finally { $taskHasher.Dispose(); $taskStream.Dispose() }
$taskSize = (Get-Item -LiteralPath $taskPackage).Length
$taskMatch = $taskSize -eq 64628628 -and $taskHash -eq 'dca307a0afc1827e27bc7ec2614e5ead3a30978d4c5d318e4d0d842ebc73f136'
$taskProcesses = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($taskRoot + '\',[StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { @{Pid=$_.ProcessId;Name=$_.Name;StartedUtc=$_.CreationDate.ToUniversalTime().ToString('o')} })
$taskResult = [ordered]@{ Utc=[DateTime]::UtcNow.ToString('o');CurrentVersion='0.2.9';TargetVersion='0.2.10';PackagePath=$taskPackage;Bytes=$taskSize;Sha256=$taskHash;OfficialFullMatches=$taskMatch;Processes=$taskProcesses;ProductOperationPerformed=$false }
$taskPath = Join-Path $taskOut ('ready-v0210-verification-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
[IO.File]::WriteAllText($taskPath,($taskResult | ConvertTo-Json -Depth 5),(New-Object Text.UTF8Encoding($false)))
Write-Output $taskPath
if (-not $taskMatch) { throw 'Downloaded package differs from official 0.2.10 bytes' }
