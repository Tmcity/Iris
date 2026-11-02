$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1')
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Management\Microsoft.PowerShell.Management.psd1')
$taskCli = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\wsb.exe'
$taskConfigPath = Join-Path $PSScriptRoot 'Iris-Manual-Acceptance.wsb'
$taskOutputRoot = Join-Path $PSScriptRoot 'output'
$taskLaunchRoot = Join-Path $taskOutputRoot ('launcher-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $taskLaunchRoot
$taskUtf8 = New-Object Text.UTF8Encoding($false)
$taskState = [ordered]@{ SandboxId = $null; Stage = 'preflight'; ConfigPath = $taskConfigPath; LogDirectory = $taskLaunchRoot; UserOwnedManualSession = $true; WindowVisibleVerified = $false; Failure = $null }

function SaveState {
    [IO.File]::WriteAllText((Join-Path $taskLaunchRoot 'state.json'), ($script:taskState | ConvertTo-Json -Depth 8), $script:taskUtf8)
    [IO.File]::WriteAllText((Join-Path $taskOutputRoot 'manual-sandbox-session.json'), ($script:taskState | ConvertTo-Json -Depth 8), $script:taskUtf8)
}
function InvokeSandboxCli([string]$label, [string[]]$arguments) {
    $taskStarted = [DateTime]::UtcNow.ToString('o')
    $taskPreviousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $taskLines = @(& $script:taskCli @arguments 2>&1)
        $taskCode = $LASTEXITCODE
    } finally { $ErrorActionPreference = $taskPreviousPreference }
    $taskText = ($taskLines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    [IO.File]::WriteAllText((Join-Path $script:taskLaunchRoot ($label + '.log')), $taskText, $script:taskUtf8)
    [IO.File]::WriteAllText((Join-Path $script:taskLaunchRoot ($label + '.command.json')), (@{ Arguments = $arguments; StartedUtc = $taskStarted; FinishedUtc = [DateTime]::UtcNow.ToString('o'); ExitCode = $taskCode } | ConvertTo-Json -Depth 8), $script:taskUtf8)
    Write-Host ($label + ': exit=' + $taskCode)
    if ($taskCode -ne 0) { throw ($label + ' failed. See ' + (Join-Path $script:taskLaunchRoot ($label + '.log'))) }
    return $taskText
}

try {
    if (-not (Test-Path -LiteralPath $taskCli -PathType Leaf)) { throw 'Windows Sandbox CLI was not found.' }
    [xml]$taskConfig = Get-Content -LiteralPath $taskConfigPath -Raw -Encoding UTF8
    $taskFolders = @($taskConfig.Configuration.MappedFolders.MappedFolder)
    if ($taskFolders.Count -ne 2 -or $taskConfig.Configuration.LogonCommand) { throw 'Unexpected acceptance configuration.' }
    foreach ($taskFolder in $taskFolders) {
        if (-not (Test-Path -LiteralPath $taskFolder.HostFolder -PathType Container)) { throw ('Mapped host folder does not exist: ' + $taskFolder.HostFolder) }
    }
    $taskExisting = InvokeSandboxCli 'list-before' @('list','--raw') | ConvertFrom-Json
    if (@($taskExisting.WindowsSandboxEnvironments).Count -ne 0) { throw 'A Sandbox is already running. Keep it; do not create another acceptance instance.' }
    $taskState.SandboxId = [Guid]::NewGuid().ToString()
    $taskState.Stage = 'start-requested'
    SaveState
    $null = $taskConfig.Configuration.RemoveChild($taskConfig.Configuration.MappedFolders)
    $null = InvokeSandboxCli 'start' @('start','--id',$taskState.SandboxId,'--config',$taskConfig.OuterXml,'--raw')
    $taskState.Stage = 'started'
    SaveState
    for ($taskIndex = 0; $taskIndex -lt $taskFolders.Count; $taskIndex++) {
        $taskFolder = $taskFolders[$taskIndex]
        $taskArgs = @('share','--id',$taskState.SandboxId,'--host-path',$taskFolder.HostFolder,'--sandbox-path',$taskFolder.SandboxFolder)
        if ($taskFolder.ReadOnly -eq 'false') { $taskArgs += '--allow-write' }
        $taskArgs += '--raw'
        $null = InvokeSandboxCli ('share-' + $taskIndex) $taskArgs
    }
    $taskState.Stage = 'connection-requested'
    SaveState
    $null = InvokeSandboxCli 'connect' @('connect','--id',$taskState.SandboxId,'--raw')
    $null = InvokeSandboxCli 'list-after-connect' @('list','--raw')
    $taskState.Stage = 'connection-cli-completed-await-user-visible-window'
    SaveState
    Write-Host 'Sandbox connection requested. Confirm the desktop window is actually visible.'
    Write-Host ('Sandbox ID: ' + $taskState.SandboxId)
    Write-Host ('Logs: ' + $taskLaunchRoot)
    Write-Host 'This launcher does not install Iris, prefill a runtime cache, or stop the Sandbox.'
} catch {
    $taskState.Failure = $_.Exception.Message
    SaveState
    Write-Host ('Launcher stopped: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host ('Logs: ' + $taskLaunchRoot)
    Write-Host 'A started Sandbox is left intact for diagnosis. Use its recorded ID to reconnect.'
    exit 1
}
