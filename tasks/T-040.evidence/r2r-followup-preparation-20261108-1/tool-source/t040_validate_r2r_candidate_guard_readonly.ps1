$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
Import-Module (Join-Path $PSScriptRoot 't040_r2r_candidate_gate.psm1') -Force
$taskOut = Join-Path 'C:\IrisT040Output' ('r2r-gate-validation-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
$null = New-Item -ItemType Directory -Path $taskOut
$taskBefore = @(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Select-Object ProcessId, CreationDate, ExecutablePath)
$taskResults = @()
try { $null = Get-T040VerifiedR2rCandidate -ReceiptPath (Join-Path $taskOut 'missing-receipt.json'); throw 'Missing receipt unexpectedly admitted' }
catch { if ($_.Exception.Message -ne 'Verified R2R installation receipt required') { throw }; $taskResults += @{ Case = 'MissingReceipt'; Rejected = $true; Reason = $_.Exception.Message } }
$taskManifest = 'C:\IrisT040R2rCandidate\LOCAL_0211_R2R_payload-manifest.json'
$taskFake = @{ Version = '0.2.11'; SourceCommit = '90822806f8840c3272dd27f3b2f14abf0f57f2b9'; AllFilesMatch = $true; ExpectedFiles = 97; ManifestSha256 = (Get-FileHash -LiteralPath $taskManifest -Algorithm SHA256).Hash; AppPid = $taskBefore[0].ProcessId; AppCreatedUtc = $taskBefore[0].CreationDate.ToUniversalTime().ToString('o') }
$taskFakePath = Join-Path $taskOut 'synthetic-untrusted-receipt.json'
[IO.File]::WriteAllText($taskFakePath, ($taskFake | ConvertTo-Json -Depth 3), (New-Object Text.UTF8Encoding($false)))
try { $null = Get-T040VerifiedR2rCandidate -ReceiptPath $taskFakePath; throw 'Original IL-only candidate unexpectedly admitted as R2R' }
catch { if ($_.Exception.Message -ne 'Actual payload differs from the reviewed R2R candidate') { throw }; $taskResults += @{ Case = 'ForgedClaimWithOriginalPayload'; Rejected = $true; Reason = $_.Exception.Message } }
foreach ($taskScript in @('t040_r2r_window_cycle_native.ps1', 't040_start_r2r_runtime_observer.ps1', 't040_start_r2r_tray_steady_readonly.ps1')) {
    try {
        if ($taskScript -eq 't040_start_r2r_tray_steady_readonly.ps1') {
            & (Join-Path $PSScriptRoot $taskScript) -ReceiptPath (Join-Path $taskOut 'missing-receipt.json') -TraceLeasePath (Join-Path $taskOut 'missing-lease.json')
        } else {
            & (Join-Path $PSScriptRoot $taskScript) -ReceiptPath (Join-Path $taskOut 'missing-receipt.json')
        }
        throw 'Follow-up script unexpectedly admitted an unverified candidate'
    } catch {
        if ($_.Exception.Message -ne 'Verified R2R installation receipt required') { throw }
        $taskResults += @{ Case = $taskScript; Rejected = $true; Reason = $_.Exception.Message }
    }
}
$taskAfter = @(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Select-Object ProcessId, CreationDate, ExecutablePath)
if (($taskBefore | ConvertTo-Json -Compress) -ne ($taskAfter | ConvertTo-Json -Compress)) { throw 'Primary changed during guard validation' }
$taskRecord = @{ Utc = [DateTime]::UtcNow.ToString('o'); PowerShellVersion = $PSVersionTable.PSVersion.ToString(); Cases = $taskResults; PrimaryUnchanged = $true; ProductOperationPerformed = $false; NativeCliExecuted = $false; InstallerExecuted = $false }
[IO.File]::WriteAllText((Join-Path $taskOut 'validation.json'), ($taskRecord | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
Write-Output $taskOut
