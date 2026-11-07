param([Parameter(Mandatory = $true)][string]$ReceiptPath)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 't040_r2r_candidate_gate.psm1') -Force
$taskCandidate = Get-T040VerifiedR2rCandidate -ReceiptPath $ReceiptPath
$taskFixture = @(Get-CimInstance Win32_Process -Filter "Name='T040SyntheticProvider.exe'")
if ($taskFixture.Count -ne 1) { throw 'The same owned synthetic provider must be running' }
$taskOut = Join-Path 'C:\IrisT040Output' ('r2r-window-cycle-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
$null = New-Item -ItemType Directory -Path $taskOut
$taskState = @{
    StartedUtc = [DateTime]::UtcNow.ToString('o'); AppPid = $taskCandidate.Identity.ProcessId
    AppCreatedUtc = $taskCandidate.Identity.CreationDate.ToUniversalTime().ToString('o')
    SourceCommit = $taskCandidate.Receipt.SourceCommit; Commands = @(); Completed = $false; Failure = $null
    ProductOperationPerformed = $true; NativeCliUsed = $true; ForegroundKeyboardOrMouseInputUsed = $false
    ManualProviderModelAndConnectionButtonsProved = $false; FormalNfrResult = $false
}
$taskUtf8 = New-Object Text.UTF8Encoding($false)
function Save-T040R2rCycle { [IO.File]::WriteAllText((Join-Path $script:taskOut 'state.json'), ($script:taskState | ConvertTo-Json -Depth 6), $script:taskUtf8) }
Save-T040R2rCycle
try {
    foreach ($taskArguments in @('--action OpenSettings', '--translate "Iris T040 R2R memory 07"', '--action OpenHistory')) {
        $taskCurrent = Get-CimInstance Win32_Process -Filter ('ProcessId=' + [int]$taskState.AppPid)
        if (-not $taskCurrent -or $taskCurrent.ExecutablePath -ne $taskCandidate.ExecutablePath -or $taskCurrent.CreationDate.ToUniversalTime() -ne $taskCandidate.Identity.CreationDate.ToUniversalTime()) { throw 'The verified primary changed; no new command sent' }
        $taskCli = Start-Process -FilePath $taskCandidate.ExecutablePath -ArgumentList $taskArguments -WindowStyle Hidden -PassThru
        try {
            $null = $taskCli.Handle
            if (-not $taskCli.WaitForExit(15000)) { throw 'Exact native CLI remains alive; preserve it before retrying' }
            $taskState.Commands += @{ Arguments = $taskArguments; Pid = $taskCli.Id; ExitCode = $taskCli.ExitCode; FinishedUtc = [DateTime]::UtcNow.ToString('o') }
            Save-T040R2rCycle
            if ($taskCli.ExitCode -ne 0) { throw 'Native command did not confirm acceptance' }
        } finally { $taskCli.Dispose() }
        Start-Sleep -Seconds 2
    }
    & (Join-Path $PSScriptRoot 'tools\state-snapshot\T040StateProbe.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Read-only data snapshot failed' }
} catch { $taskState.Failure = @{ Type = $_.Exception.GetType().FullName; Message = $_.Exception.Message } }
finally { $taskState.Completed = $true; $taskState.FinishedUtc = [DateTime]::UtcNow.ToString('o'); Save-T040R2rCycle }
if ($taskState.Failure) { throw 'R2R window cycle failed; retain primary and original records' }
Write-Output $taskOut
