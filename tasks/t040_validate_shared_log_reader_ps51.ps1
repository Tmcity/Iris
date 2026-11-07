$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned Sandbox only' }
$taskOut = Join-Path 'C:\IrisT040Output' ('shared-log-reader-validation-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
$null = New-Item -ItemType Directory -Path $taskOut
$taskUtf8 = New-Object Text.UTF8Encoding($false)
$taskFixture = Join-Path $taskOut 'fixture.log'
[IO.File]::WriteAllText($taskFixture, 'Iris fixture ready', $taskUtf8)
$taskWriter = [IO.File]::Open($taskFixture, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
$taskExpectedFailure = $null
try {
    try { $null = [IO.File]::ReadAllLines($taskFixture, [Text.Encoding]::UTF8) }
    catch { $taskExpectedFailure = $_.Exception.InnerException.GetType().FullName }
    $taskStream = [IO.File]::Open($taskFixture, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $taskReader = New-Object IO.StreamReader($taskStream, [Text.Encoding]::UTF8)
    try { $taskLine = $taskReader.ReadLine() } finally { $taskReader.Dispose() }
    $taskWatch = [Diagnostics.Stopwatch]::StartNew()
    $taskJson = @{ Runs = @(@{ ReadyLogLine = $taskLine }) } | ConvertTo-Json -Depth 12
    $taskResult = @{
        Utc = [DateTime]::UtcNow.ToString('o'); ProductOperationPerformed = $false
        PowerShellVersion = $PSVersionTable.PSVersion.ToString()
        OriginalReadAllLinesFailure = $taskExpectedFailure
        SharedReaderTextMatches = $taskLine -eq 'Iris fixture ready'
        Properties = @($taskLine.PSObject.Properties.Name)
        SerializationMs = $taskWatch.Elapsed.TotalMilliseconds; JsonLength = $taskJson.Length
    }
} finally { $taskWriter.Dispose() }
[IO.File]::WriteAllText((Join-Path $taskOut 'validation.json'), ($taskResult | ConvertTo-Json -Depth 5), $taskUtf8)
if ($taskExpectedFailure -ne 'System.IO.IOException' -or -not $taskResult.SharedReaderTextMatches -or $taskJson.Length -gt 1024 -or $taskResult.Properties.Count -ne 1) { throw 'Shared plain-string reader regression failed' }
Write-Output $taskOut
