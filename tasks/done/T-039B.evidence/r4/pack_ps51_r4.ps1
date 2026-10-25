param([Parameter(Mandatory=$true)][string]$Version,[Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$PreviousReleaseDirectory)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1){throw 'Actual Windows PowerShell 5.1 is required.'}
# The parent Codex runtime supplies its own PSModulePath. Resolve the native
# Windows PowerShell modules explicitly, without changing user/system settings.
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1')
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1')
[pscustomobject]@{PSVersion=$PSVersionTable.PSVersion.ToString();PSEdition=$PSVersionTable.PSEdition;ProcessPath=[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName;HashCommand=(Get-Command Get-FileHash).Source} | ConvertTo-Json
$taskTemp = Join-Path 'F:\Iris\artifacts\t039b' ('pack-temp-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskTemp | Out-Null
$env:TEMP=$taskTemp; $env:TMP=$taskTemp
[pscustomobject]@{TemporaryDirectory=$taskTemp} | ConvertTo-Json
& F:\Iris\build\pack.ps1 -Version $Version -OutputDirectory $OutputDirectory -PreviousReleaseDirectory $PreviousReleaseDirectory -ToolPackageDirectory F:\Iris\artifacts\t039b\tool-feed
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
