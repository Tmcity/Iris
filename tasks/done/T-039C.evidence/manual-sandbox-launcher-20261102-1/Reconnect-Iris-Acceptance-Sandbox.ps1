$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1')
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Management\Microsoft.PowerShell.Management.psd1')
$taskCli = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\wsb.exe'
$taskStatePath = Join-Path $PSScriptRoot 'output\manual-sandbox-session.json'
$taskState = Get-Content -LiteralPath $taskStatePath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $taskState.SandboxId) { throw 'No owned manual Sandbox ID was recorded. Run the launcher first.' }
$taskList = @(& $taskCli list --raw 2>&1)
if ($LASTEXITCODE -ne 0) { throw 'Could not read the actual Sandbox list.' }
$taskInstances = ($taskList -join [Environment]::NewLine) | ConvertFrom-Json
if (@($taskInstances.WindowsSandboxEnvironments | Where-Object { $_.Id -eq $taskState.SandboxId }).Count -ne 1) { throw 'The recorded Sandbox is not running. Keep the original logs and start a new acceptance instance.' }
& $taskCli connect --id $taskState.SandboxId --raw
if ($LASTEXITCODE -ne 0) { throw 'Reconnect failed. Preserve the printed error and existing instance.' }
Write-Host ('Connection requested for existing Sandbox ' + $taskState.SandboxId)
