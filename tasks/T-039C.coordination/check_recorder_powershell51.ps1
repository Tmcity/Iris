param([Parameter(Mandatory = $true)][string]$ObserverPath, [Parameter(Mandatory = $true)][string]$StarterPath)
$ErrorActionPreference = 'Stop'
$taskFailed = $false
foreach ($taskPath in @($ObserverPath, $StarterPath)) {
    $taskTokens = $null
    $taskErrors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($taskPath, [ref]$taskTokens, [ref]$taskErrors)
    if ($taskErrors.Count -ne 0) {
        $taskFailed = $true
        $taskErrors | ForEach-Object { Write-Output ($taskPath + ': ' + $_.Message + ' at ' + $_.Extent.StartLineNumber) }
    } else { Write-Output ('Parsed with PowerShell ' + $PSVersionTable.PSVersion.ToString() + ': ' + $taskPath) }
}
if ($taskFailed) { exit 1 }
