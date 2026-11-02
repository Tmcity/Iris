$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned acceptance Sandbox only' }
$taskPath = 'C:\IrisAcceptanceOutput\capture-installed-diagnostic.json'
$taskResult = [ordered]@{ Success = $false; Output = $null; Failure = $null; Utc = [DateTime]::UtcNow.ToString('o') }
try {
    $taskResult.Output = @(& 'C:\IrisAcceptanceInput\manual-capture-state.ps1' -Stage installed)
    $taskResult.Success = $true
} catch {
    $taskResult.Failure = @{ Type = $_.Exception.GetType().FullName; Message = $_.Exception.Message; Position = $_.InvocationInfo.PositionMessage; ScriptStackTrace = $_.ScriptStackTrace }
} finally {
    [IO.File]::WriteAllText($taskPath, ($taskResult | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
}
if (-not $taskResult.Success) { exit 1 }
