$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskFixturePath=Join-Path $PSScriptRoot 'tools\synthetic-provider\T040SyntheticProvider.exe'
$taskFixture=@(Get-CimInstance Win32_Process -Filter "Name='T040SyntheticProvider.exe'" | Where-Object {$_.ExecutablePath-eq$taskFixturePath})
if($taskFixture.Count-ne1){throw 'Expected live owned synthetic fixture is absent'}
& (Join-Path $PSScriptRoot 'tools\provider-adapter-probe\ProviderAdapterProbe.exe')
if($LASTEXITCODE-ne0){throw 'Provider adapter probe did not pass; inspect its saved state'}
