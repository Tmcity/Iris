$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
& (Join-Path $PSScriptRoot 't040_verify_controlled_candidate_installed_readonly.ps1')
$taskExpected = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'candidate-identity.json')) | ConvertFrom-Json
if ($taskExpected.Version -ne '0.2.12') { throw 'This observation belongs to the reviewed fixed candidate only' }
$taskLease = Join-Path 'C:\IrisT040Output' ('fixed-runtime-lease-' + $taskExpected.ManifestSha256.ToLowerInvariant() + '.json')
if (Test-Path -LiteralPath $taskLease) { throw 'This candidate already has an observation; preserve that run instead of repeating it' }
if (@(Get-CimInstance Win32_Process -Filter "Name='RuntimeCounterProbe.exe'").Count) { throw 'An observer is already present; inspect it first' }
$taskTargetPath = Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskApps = @(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object { $_.ExecutablePath -eq $taskTargetPath })
if ($taskApps.Count -ne 1) { throw 'Exactly one reviewed installed primary required' }
$taskExe = 'C:\IrisT040R2rRepeat\tools\runtime-counters\RuntimeCounterProbe.exe'
$taskStamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
$taskObserver = Start-Process -FilePath $taskExe -ArgumentList @([string]$taskApps[0].ProcessId, '600') -WindowStyle Hidden -PassThru -RedirectStandardOutput ('C:\IrisT040Output\fixed-runtime-stdout-' + $taskStamp + '.txt') -RedirectStandardError ('C:\IrisT040Output\fixed-runtime-stderr-' + $taskStamp + '.txt')
$taskRecord = @{
    StartedUtc = [DateTime]::UtcNow.ToString('o'); Version = $taskExpected.Version
    SourceCommit = $taskExpected.SourceCommit; ManifestSha256 = $taskExpected.ManifestSha256
    TargetPid = $taskApps[0].ProcessId; TargetCreatedUtc = $taskApps[0].CreationDate.ToUniversalTime().ToString('o')
    ObserverPid = $taskObserver.Id; ObserverCreatedUtc = $taskObserver.StartTime.ToUniversalTime().ToString('o')
    ObserverPath = $taskExe; ObserverSha256 = (Get-FileHash -LiteralPath $taskExe -Algorithm SHA256).Hash
    DurationSeconds = 600; GcRequested = $false; ProductOperationPerformed = $false
    FormalMemoryDuringTraceEligible = $false; TraceDirectory = $null
}
$taskObserver.Dispose()
$taskUtf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($taskLease, ($taskRecord | ConvertTo-Json -Depth 5), $taskUtf8)
$taskDeadline = [DateTime]::UtcNow.AddSeconds(6)
do {
    foreach ($taskFolder in @(Get-ChildItem -LiteralPath 'C:\IrisT040Output' -Directory -Filter 'runtime-counters-*' | Sort-Object Name -Descending)) {
        $taskEvents = Join-Path $taskFolder.FullName 'events.jsonl'
        if (-not (Test-Path -LiteralPath $taskEvents -PathType Leaf)) { continue }
        $taskStream = [IO.File]::Open($taskEvents, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $taskReader = New-Object IO.StreamReader($taskStream, [Text.Encoding]::UTF8)
        try { $taskFirst = $taskReader.ReadLine() } finally { $taskReader.Dispose() }
        if (-not $taskFirst) { continue }
        $taskHeader = $taskFirst | ConvertFrom-Json
        if ($taskHeader.Kind -eq 'ObserverStarted' -and $taskHeader.Pid -eq $taskRecord.TargetPid -and [DateTimeOffset]::Parse($taskHeader.Utc).UtcDateTime -ge [DateTime]::Parse($taskRecord.ObserverCreatedUtc).ToUniversalTime()) { $taskRecord.TraceDirectory = $taskFolder.FullName; break }
    }
    if (-not $taskRecord.TraceDirectory) { Start-Sleep -Milliseconds 200 }
} while (-not $taskRecord.TraceDirectory -and [DateTime]::UtcNow -lt $taskDeadline)
[IO.File]::WriteAllText($taskLease, ($taskRecord | ConvertTo-Json -Depth 5), $taskUtf8)
if (-not $taskRecord.TraceDirectory) { throw 'Observer header was not found; retain actual process and lease for inspection' }
Write-Output $taskLease
