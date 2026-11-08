function Get-T040VerifiedR2rCandidate {
    param([Parameter(Mandatory = $true)][string]$ReceiptPath)
    $ErrorActionPreference = 'Stop'
    if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
    $taskReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
    if (-not $taskReceiptPath.StartsWith('C:\IrisT040Output\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Receipt outside owned output' }
    if (-not (Test-Path -LiteralPath $taskReceiptPath -PathType Leaf)) { throw 'Verified R2R installation receipt required' }
    $taskReceipt = Get-Content -LiteralPath $taskReceiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($taskReceipt.Version -ne '0.2.11' -or $taskReceipt.SourceCommit -ne '90822806f8840c3272dd27f3b2f14abf0f57f2b9' -or -not $taskReceipt.AllFilesMatch -or $taskReceipt.ExpectedFiles -ne 97) { throw 'Receipt does not prove the reviewed R2R candidate' }
    $taskManifestPath = 'C:\IrisT040R2rCandidate\LOCAL_0211_R2R_payload-manifest.json'
    if ((Get-FileHash -LiteralPath $taskManifestPath -Algorithm SHA256).Hash -ne $taskReceipt.ManifestSha256) { throw 'Receipt manifest changed' }
    $taskManifest = Get-Content -LiteralPath $taskManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($taskManifest.Count -ne 97) { throw 'Incomplete R2R manifest' }
    $taskRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Iris'))
    foreach ($taskEntry in $taskManifest) {
        $taskRelative = 'current\' + $taskEntry.Path
        if ($taskEntry.Path -eq 'Iris_ExecutionStub.exe') { $taskRelative = 'Iris.exe' }
        elseif ($taskEntry.Path -eq 'Squirrel.exe') { $taskRelative = 'Update.exe' }
        $taskFile = [IO.Path]::GetFullPath((Join-Path $taskRoot $taskRelative))
        if (-not $taskFile.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload path outside owned installation' }
        if (-not (Test-Path -LiteralPath $taskFile -PathType Leaf)) { throw 'Reviewed R2R payload is missing' }
        $taskActual = Get-Item -LiteralPath $taskFile
        if ($taskActual.Length -ne $taskEntry.Bytes -or (Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash -ne $taskEntry.Sha256) { throw 'Actual payload differs from the reviewed R2R candidate' }
    }
    $taskExe = Join-Path $taskRoot 'current\Iris.App.exe'
    $taskApps = @(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object { $_.ExecutablePath -eq $taskExe })
    if ($taskApps.Count -ne 1 -or $taskApps[0].ProcessId -ne $taskReceipt.AppPid) { throw 'Actual primary differs from the installation receipt' }
    if ([Math]::Abs(($taskApps[0].CreationDate.ToUniversalTime() - [DateTime]::Parse($taskReceipt.AppCreatedUtc).ToUniversalTime()).TotalMilliseconds) -gt 1) { throw 'Primary creation identity differs from the installation receipt' }
    return @{ Receipt = $taskReceipt; Identity = $taskApps[0]; ExecutablePath = $taskExe; ManifestPath = $taskManifestPath }
}
function Get-T040CompletedNaturalGcPairs {
    param([Parameter(Mandatory = $true)][string]$EventsPath)
    $taskStarts = @{}; $taskCompleted = @{}
    $taskStream = [IO.File]::Open($EventsPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $taskReader = New-Object IO.StreamReader($taskStream, [Text.Encoding]::UTF8)
    try {
        while (($taskLine = $taskReader.ReadLine()) -ne $null) {
            $taskEvent = $taskLine | ConvertFrom-Json
            if ($taskEvent.Kind -eq 'GCStart' -and $taskEvent.Depth -eq 2 -and $taskEvent.Reason -notlike 'Induced*') { $taskStarts[[string]$taskEvent.Count] = $true }
            if ($taskEvent.Kind -eq 'GCStop' -and $taskEvent.Depth -eq 2 -and $taskStarts.ContainsKey([string]$taskEvent.Count)) { $taskCompleted[[string]$taskEvent.Count] = $true }
        }
    } finally { $taskReader.Dispose() }
    return @{ NaturalFullGcStarts = $taskStarts.Count; CompletedNaturalFullGcPairs = $taskCompleted.Count }
}
Export-ModuleMember -Function Get-T040VerifiedR2rCandidate, Get-T040CompletedNaturalGcPairs
