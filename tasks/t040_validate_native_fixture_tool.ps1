$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned T-040 Sandbox only' }
& (Join-Path $PSScriptRoot 'tools\fixture-configure\T040FixtureConfigure.exe') --validate-isolated
if ($LASTEXITCODE -ne 0) { throw 'Isolated public-API fixture initialization failed; original output retained' }
