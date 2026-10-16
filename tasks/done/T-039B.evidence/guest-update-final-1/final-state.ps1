$ErrorActionPreference='Stop'
$root=Join-Path $env:LOCALAPPDATA 'Iris\current'
$files=@('Iris.App.dll','maintenance\Iris.Maintenance.exe','uia\Iris.UiaHost.dll','en-US\Iris.App.resources.dll','Iris.Core.dll')
$hashes=@{}
foreach($relative in $files){$file=[IO.File]::OpenRead((Join-Path $root $relative));$hash=[Security.Cryptography.SHA256]::Create();try{$hashes[$relative]=[BitConverter]::ToString($hash.ComputeHash($file)).Replace('-','')}finally{$file.Dispose();$hash.Dispose()}}
$state=[ordered]@{TimeUtc=[DateTime]::UtcNow.ToString('o');Processes=@(Get-CimInstance Win32_Process|Where-Object{$_.Name -match 'Iris|Setup|Update'}|Select-Object ProcessId,ParentProcessId,CreationDate,ExecutablePath,CommandLine);Identity=(Get-Content (Join-Path $root 'release-source.json') -Raw|ConvertFrom-Json);InstalledHashes=$hashes}
$state|ConvertTo-Json -Depth 8|Set-Content C:\IrisBOutput\final-state.json -Encoding UTF8
if($state.Processes.Count -ne 0){exit 2};exit 0
