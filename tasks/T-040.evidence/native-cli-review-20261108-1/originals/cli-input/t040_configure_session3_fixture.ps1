$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned T-040 Sandbox only' }
$taskTarget = Join-Path $env:LOCALAPPDATA 'Iris\current\Iris.App.exe'
$taskApps = @(Get-CimInstance Win32_Process -Filter "Name='Iris.App.exe'" | Where-Object { $_.ExecutablePath -eq $taskTarget })
if ($taskApps.Count -ne 1 -or $taskApps[0].ProcessId -ne 5568) { throw 'The identified session3 Iris instance is required before native CLI acceptance' }
$taskFixture = @(Get-CimInstance Win32_Process -Filter "Name='T040SyntheticProvider.exe'")
if ($taskFixture.Count -ne 1) { throw 'The owned synthetic fixture must be ready before configuring it' }
$taskManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'LOCAL_0211_payload-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($taskName in @('Iris.Core.dll','Iris.Infrastructure.dll','Iris.Platform.Windows.dll')) {
    $taskExpected = @($taskManifest | Where-Object { $_.Path -eq $taskName })[0]
    $taskLive = Join-Path (Split-Path -Parent $taskTarget) $taskName
    $taskTool = Join-Path (Join-Path $PSScriptRoot 'tools\fixture-configure') $taskName
    if ((Get-FileHash -LiteralPath $taskLive -Algorithm SHA256).Hash -ne $taskExpected.Sha256 -or (Get-FileHash -LiteralPath $taskTool -Algorithm SHA256).Hash -ne $taskExpected.Sha256) { throw 'Fixture tool and installed product must use the exact reviewed candidate assemblies' }
}
& (Join-Path $PSScriptRoot 'tools\fixture-configure\T040FixtureConfigure.exe')
if ($LASTEXITCODE -ne 0) { throw 'Owned public-API fixture initialization failed; original output retained' }
