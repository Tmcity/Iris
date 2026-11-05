param([Parameter(Mandatory = $true)][string]$Root)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Use Windows PowerShell 5.1 for this check' }
$taskFailed = $false
$taskFiles = @(Get-ChildItem -LiteralPath $Root -Filter '*.ps1' -File -Recurse)
foreach ($taskFile in $taskFiles) {
    $taskTokens = $null
    $taskErrors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($taskFile.FullName, [ref]$taskTokens, [ref]$taskErrors)
    if ($taskErrors.Count -ne 0) {
        $taskFailed = $true
        $taskErrors | ForEach-Object { Write-Output ($taskFile.FullName + ': ' + $_.Message) }
    }
}
if ($taskFiles.Count -eq 0 -or $taskFailed) { exit 1 }
Write-Output ('PowerShell ' + $PSVersionTable.PSVersion.ToString() + ': parsed ' + $taskFiles.Count + ' scripts; no guest or installer was started.')
