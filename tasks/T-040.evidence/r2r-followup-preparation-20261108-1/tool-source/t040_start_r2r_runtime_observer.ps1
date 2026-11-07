param([Parameter(Mandatory = $true)][string]$ReceiptPath)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 't040_r2r_candidate_gate.psm1') -Force
$taskCandidate = Get-T040VerifiedR2rCandidate -ReceiptPath $ReceiptPath
$taskKey = $taskCandidate.Receipt.ManifestSha256.ToLowerInvariant()
if ($taskKey -notmatch '^[a-f0-9]{64}$') { throw 'Invalid reviewed manifest fingerprint' }
$taskLease = Join-Path 'C:\IrisT040Output' ('r2r-runtime-lease-' + $taskKey + '.json')
if (Test-Path -LiteralPath $taskLease) { throw 'This candidate already has an observation lease; inspect that actual run rather than restarting it' }
$taskExe = Join-Path $PSScriptRoot 'tools\runtime-counters\RuntimeCounterProbe.exe'
if (@(Get-CimInstance Win32_Process -Filter "Name='RuntimeCounterProbe.exe'").Count) { throw 'A runtime counter observer is already present; identify it first' }
$taskStamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')
$taskProcess = Start-Process -FilePath $taskExe -ArgumentList @([string]$taskCandidate.Identity.ProcessId, '600') -WindowStyle Hidden -PassThru -RedirectStandardOutput ('C:\IrisT040Output\r2r-runtime-stdout-' + $taskStamp + '.txt') -RedirectStandardError ('C:\IrisT040Output\r2r-runtime-stderr-' + $taskStamp + '.txt')
$taskRecord = @{
    StartedUtc = [DateTime]::UtcNow.ToString('o'); TargetPid = $taskCandidate.Identity.ProcessId
    TargetCreatedUtc = $taskCandidate.Identity.CreationDate.ToUniversalTime().ToString('o')
    ObserverPid = $taskProcess.Id; ObserverCreatedUtc = $taskProcess.StartTime.ToUniversalTime().ToString('o')
    ObserverPath = $taskExe; ObserverSha256 = (Get-FileHash -LiteralPath $taskExe -Algorithm SHA256).Hash
    ReceiptPath = [IO.Path]::GetFullPath($ReceiptPath); ManifestSha256 = $taskCandidate.Receipt.ManifestSha256
    DurationSeconds = 600; SourceCommit = $taskCandidate.Receipt.SourceCommit; TraceDirectory = $null
    GcRequested = $false; ProductOperationPerformed = $false; FormalMemoryDuringTraceEligible = $false
}
$taskProcess.Dispose()
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
if (-not $taskRecord.TraceDirectory) { throw 'Observer did not publish its identity header in time; actual process and lease retained' }
Write-Output $taskLease
