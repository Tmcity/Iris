param([Parameter(Mandatory = $true)][string]$OutputDirectory, [ValidateSet('annotated', 'plain')][string]$Mode)
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskUtf8 = New-Object Text.UTF8Encoding($false)
$taskFixture = Join-Path $OutputDirectory 'fixture.txt'
[IO.File]::WriteAllText($taskFixture, 'Iris sampler fixture ready', $taskUtf8)
$taskLine = if ($Mode -eq 'annotated') { Get-Content -LiteralPath $taskFixture -Encoding UTF8 } else { [IO.File]::ReadAllLines($taskFixture, [Text.Encoding]::UTF8)[0] }
$taskState = @{ Mode = $Mode; Properties = @($taskLine.PSObject.Properties.Name); SerializationCompleted = $false; PowerShellVersion = $PSVersionTable.PSVersion.ToString() }
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'state.json'), ($taskState | ConvertTo-Json -Depth 3), $taskUtf8)
$taskWatch = [Diagnostics.Stopwatch]::StartNew()
$taskValue = @{ Runs = @(@{ ReadyLogLine = $taskLine }) } | ConvertTo-Json -Depth 12
$taskState.SerializationCompleted = $true; $taskState.SerializationMs = $taskWatch.Elapsed.TotalMilliseconds; $taskState.JsonLength = $taskValue.Length
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'state.json'), ($taskState | ConvertTo-Json -Depth 3), $taskUtf8)
