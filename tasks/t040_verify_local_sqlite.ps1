$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned Sandbox only'}
& (Join-Path $PSScriptRoot 'tools\sqlite-location\SqliteLocationProbe.exe')
if($LASTEXITCODE-ne0){throw 'Guest-local SQLite probe failed; preserve its output'}
