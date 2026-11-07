param([Parameter(Mandatory = $true)][string]$OriginalTraceDirectory, [Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 't040_r2r_candidate_gate.psm1') -Force
$taskActual = Get-T040CompletedNaturalGcPairs -EventsPath (Join-Path $OriginalTraceDirectory 'events.jsonl')
$taskFinal = Get-Content -LiteralPath (Join-Path $OriginalTraceDirectory 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $taskFinal.Completed -or $taskFinal.Failure -or $taskActual.CompletedNaturalFullGcPairs -ne 1 -or $taskFinal.NaturalFullGcStarts -ne 1) { throw 'Original completed full-GC control did not match' }
$null = New-Item -ItemType Directory -Path $OutputDirectory -ErrorAction Stop
$taskUtf8 = New-Object Text.UTF8Encoding($false)
$taskCases = @()
foreach ($taskCase in @('StartOnly', 'WrongCount', 'GenOne', 'Induced')) {
    $taskStart = @{ Kind = 'GCStart'; Depth = 2; Count = 1; Reason = 'AllocSmall' }
    $taskStop = @{ Kind = 'GCStop'; Depth = 2; Count = 1 }
    if ($taskCase -eq 'WrongCount') { $taskStop.Count = 2 }
    if ($taskCase -eq 'GenOne') { $taskStart.Depth = 1; $taskStop.Depth = 1 }
    if ($taskCase -eq 'Induced') { $taskStart.Reason = 'Induced' }
    $taskPath = Join-Path $OutputDirectory ($taskCase + '.jsonl')
    $taskText = ($taskStart | ConvertTo-Json -Compress) + [Environment]::NewLine
    if ($taskCase -ne 'StartOnly') { $taskText += ($taskStop | ConvertTo-Json -Compress) + [Environment]::NewLine }
    [IO.File]::WriteAllText($taskPath, $taskText, $taskUtf8)
    $taskResult = Get-T040CompletedNaturalGcPairs -EventsPath $taskPath
    if ($taskResult.CompletedNaturalFullGcPairs -ne 0) { throw 'Unproved or induced GC was incorrectly accepted' }
    $taskCases += @{ Case = $taskCase; Rejected = $true; Result = $taskResult }
}
$taskRecord = @{ Utc = [DateTime]::UtcNow.ToString('o'); PowerShellVersion = $PSVersionTable.PSVersion.ToString(); OriginalControl = $taskActual; Cases = $taskCases; ProductOperationPerformed = $false; ProductGcRequested = $false; FormalR2rNfrResult = $false }
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'validation.json'), ($taskRecord | ConvertTo-Json -Depth 5), $taskUtf8)
Write-Output ($taskRecord | ConvertTo-Json -Depth 5)
