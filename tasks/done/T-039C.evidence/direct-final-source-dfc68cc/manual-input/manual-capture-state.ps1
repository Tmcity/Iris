param([Parameter(Mandatory = $true)][ValidateSet('clean','installed','old-ready','offer','ready','updated','uninstalled')][string]$Stage)
$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Run only in the acceptance Sandbox' }
$taskPointer = 'C:\IrisAcceptanceOutput\active-session.json'
$taskState = Get-Content -LiteralPath $taskPointer -Raw -Encoding UTF8 | ConvertFrom-Json
$taskOut = [IO.Path]::GetFullPath($taskState.SessionDirectory)
if (-not $taskOut.StartsWith('C:\IrisAcceptanceOutput\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected output path' }
$taskRoot = Join-Path $env:LOCALAPPDATA 'Iris'
$taskData = Join-Path $env:APPDATA 'Iris'
$taskSource = Join-Path $taskRoot 'current\release-source.json'
$taskVersion = if (Test-Path -LiteralPath $taskSource) { Get-Content -LiteralPath $taskSource -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
$taskProcesses = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and ($_.ExecutablePath.StartsWith($taskRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or $_.ExecutablePath.StartsWith((Join-Path $env:LOCALAPPDATA 'Iris.Maintenance') + '\', [StringComparison]::OrdinalIgnoreCase)) } | ForEach-Object { @{ Pid = $_.ProcessId; ParentPid = $_.ParentProcessId; Name = $_.Name; Path = $_.ExecutablePath; StartedUtc = $_.CreationDate.ToUniversalTime().ToString('o') } })
$taskPackages = @(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'packages') -File -ErrorAction SilentlyContinue | ForEach-Object { @{ Name = $_.Name; Bytes = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
$taskDataHashes = @(Get-ChildItem -LiteralPath $taskData -File -Recurse -ErrorAction SilentlyContinue | ForEach-Object { try { @{ Path = $_.FullName.Substring($taskData.Length).TrimStart('\'); Bytes = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } } catch { @{ Path = $_.FullName.Substring($taskData.Length).TrimStart('\'); Unreadable = $true; Type = $_.Exception.GetType().Name } } })
$taskRuntimes = @(foreach ($taskProgramRoot in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) { if ($taskProgramRoot) { Get-ChildItem -LiteralPath (Join-Path $taskProgramRoot 'dotnet\shared') -Directory -ErrorAction SilentlyContinue | ForEach-Object { $taskFamily = $_.Name; Get-ChildItem -LiteralPath $_.FullName -Directory | ForEach-Object { @{ Family = $taskFamily; Version = $_.Name } } } } })
$taskCache = Join-Path $env:USERPROFILE 'Downloads\inst-dotnet-10.0.0-X64-WindowsDesktop.exe'
$taskRecord = [ordered]@{ Stage = $Stage; Utc = [DateTime]::UtcNow.ToString('o'); Os = [Environment]::OSVersion.Version.ToString(); Runtimes = $taskRuntimes; RuntimeCacheExists = (Test-Path -LiteralPath $taskCache); InstalledSource = $taskVersion; Processes = $taskProcesses; Packages = $taskPackages; DataHashes = $taskDataHashes }
$taskFile = Join-Path $taskOut ($Stage + '-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '.json')
[IO.File]::WriteAllText($taskFile, ($taskRecord | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))
$taskLogs = Join-Path $taskOut ('logs-' + $Stage + '-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $taskLogs
foreach ($taskFolder in @((Join-Path $env:LOCALAPPDATA 'Velopack'), (Join-Path $env:LOCALAPPDATA 'Iris.Maintenance'), (Join-Path $taskData 'logs'))) {
    Get-ChildItem -LiteralPath $taskFolder -File -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in @('.log','.txt','.json') -and $_.Length -le 10MB } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $taskLogs ((Split-Path $taskFolder -Leaf) + '-' + $_.FullName.Substring($taskFolder.Length).TrimStart('\').Replace('\','_'))) }
}
Get-ChildItem -LiteralPath $env:TEMP -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(velopack|dd_(windowsdesktop|dotnet|inst-dotnet)|Microsoft_Windows_Desktop_Runtime)' -and $_.Length -le 10MB } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $taskLogs ('temp-' + $_.Name)) }
Write-Output $taskFile
Write-Output $taskLogs
