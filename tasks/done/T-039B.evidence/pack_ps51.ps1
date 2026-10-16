param([Parameter(Mandatory=$true)][string]$Version,[Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$PreviousReleaseDirectory)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1){throw 'Actual Windows PowerShell 5.1 is required for this run.'}
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1')
[pscustomobject]@{PSVersion=$PSVersionTable.PSVersion.ToString();PSEdition=$PSVersionTable.PSEdition;ProcessPath=[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName} | ConvertTo-Json
$taskTemp = Join-Path 'F:\Iris\artifacts\t039b' ('pack-temp-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskTemp | Out-Null
# Only this verification process and its children use the task's roomy staging volume.
$env:TEMP = $taskTemp; $env:TMP = $taskTemp
[pscustomobject]@{TemporaryDirectory=$taskTemp} | ConvertTo-Json
& F:\Iris\build\pack.ps1 -Version $Version -OutputDirectory $OutputDirectory -PreviousReleaseDirectory $PreviousReleaseDirectory -ToolPackageDirectory F:\Iris\artifacts\t039b\tool-feed
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
