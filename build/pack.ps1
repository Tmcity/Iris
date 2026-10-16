param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$OutputDirectory,
    [string]$PreviousReleaseDirectory,
    [string]$ToolPackageDirectory
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be major.minor.patch.' }
$workspace = Split-Path -Parent $PSScriptRoot
$packageDocument = [xml](Get-Content -LiteralPath (Join-Path $workspace 'Directory.Packages.props') -Raw)
$sdkVersion = @($packageDocument.Project.ItemGroup.PackageVersion | Where-Object { $_.Include -eq 'Velopack' })[0].Version
if ($sdkVersion -ne '1.2.161') { throw 'The approved SDK/tool version must be 1.2.161.' }
$work = Join-Path $workspace ('artifacts\pack-' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $work 'release' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) {
    if (@(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -ne 0) { throw 'Output directory must be empty; existing releases are never overwritten.' }
} else { $null = New-Item -ItemType Directory -Path $OutputDirectory }
$utf8 = New-Object Text.UTF8Encoding($false)
if ($PreviousReleaseDirectory) {
    $previous = [IO.Path]::GetFullPath($PreviousReleaseDirectory)
    if (-not (Test-Path -LiteralPath $previous -PathType Container)) { throw 'Previous release directory does not exist.' }
    Get-ChildItem -LiteralPath $previous -File | Where-Object { $_.Name -like 'Iris-*-full.nupkg' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $OutputDirectory $_.Name) }
}
$config = Join-Path $work 'NuGet.Config'
[IO.File]::WriteAllText($config, '<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>', $utf8)
function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('dotnet failed with exit ' + $LASTEXITCODE) }
}
$tools = Join-Path $work 'tools'
$toolConfig = $config
if ($ToolPackageDirectory) {
    $localToolSource = [IO.Path]::GetFullPath($ToolPackageDirectory)
    if (-not (Test-Path -LiteralPath (Join-Path $localToolSource ('vpk.' + $sdkVersion + '.nupkg')))) { throw 'Pinned vpk package is missing from the local tool source.' }
    $toolConfig = Join-Path $work 'Tool.NuGet.Config'
    $escapedToolSource = [Security.SecurityElement]::Escape($localToolSource)
    [IO.File]::WriteAllText($toolConfig, ('<configuration><packageSources><clear/><add key="local-tool" value="' + $escapedToolSource + '"/></packageSources></configuration>'), $utf8)
}
Invoke-Dotnet @('tool','install','vpk','--version',$sdkVersion,'--tool-path',$tools,'--configfile',$toolConfig)
$staging = Join-Path $work 'app'
$uia = Join-Path $work 'uia'
$maintenance = Join-Path $work 'maintenance'
$common = @('-c','Release','-r','win-x64',('-p:Version=' + $Version),('-p:RestoreConfigFile=' + $config))
Invoke-Dotnet (@('publish',(Join-Path $workspace 'src\Iris.App\Iris.App.csproj')) + $common + @('--self-contained','false','-o',$staging))
Invoke-Dotnet (@('publish',(Join-Path $workspace 'src\Iris.UiaHost\Iris.UiaHost.csproj')) + $common + @('--self-contained','false','-o',$uia))
Invoke-Dotnet (@('publish',(Join-Path $workspace 'src\Iris.Maintenance\Iris.Maintenance.csproj')) + $common + @('--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true','-o',$maintenance))
# Only exact root duplicates are removed from this newly created staging directory.
Get-ChildItem -LiteralPath $staging -File | Where-Object { $_.Name -like 'Iris.UiaHost.*' -or $_.Name -like 'Iris.Maintenance.*' } | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
$uiaTarget = Join-Path $staging 'uia'
if (Test-Path -LiteralPath $uiaTarget) {
    $resolved = [IO.Path]::GetFullPath($uiaTarget)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($work) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Staging containment check failed.' }
    Remove-Item -LiteralPath $resolved -Recurse
}
Copy-Item -LiteralPath $uia -Destination $uiaTarget -Recurse
$null = New-Item -ItemType Directory -Path (Join-Path $staging 'maintenance') -Force
Copy-Item -LiteralPath (Join-Path $maintenance 'Iris.Maintenance.exe') -Destination (Join-Path $staging 'maintenance\Iris.Maintenance.exe')
$metadata = @{PackageId='Iris';Channel='win';RuntimeId='win-x64';Repository='https://github.com/Tmcity/Iris';Version=$Version}
[IO.File]::WriteAllText((Join-Path $staging 'release-source.json'), ($metadata | ConvertTo-Json -Compress), $utf8)
$required = @('Iris.App.exe','Iris.App.dll','Iris.App.deps.json','Iris.App.runtimeconfig.json','en-US\Iris.App.resources.dll','en-US\Iris.Core.resources.dll','uia\Iris.UiaHost.exe','uia\Iris.UiaHost.deps.json','uia\Iris.UiaHost.runtimeconfig.json','uia\en-US\Iris.Core.resources.dll','maintenance\Iris.Maintenance.exe')
foreach ($relative in $required) { if (-not (Test-Path -LiteralPath (Join-Path $staging $relative) -PathType Leaf)) { throw ('Missing release file: ' + $relative) } }
if (Test-Path -LiteralPath (Join-Path $staging 'coreclr.dll')) { throw 'Iris.App must remain framework-dependent.' }
$frameworks = (Get-Content -LiteralPath (Join-Path $staging 'Iris.App.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions.frameworks
if (-not ($frameworks | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App' -and $_.version -like '10.*' })) { throw 'Missing desktop runtime declaration.' }
foreach ($relative in @('Iris.App.exe','uia\Iris.UiaHost.exe','maintenance\Iris.Maintenance.exe')) {
    $actual = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $staging $relative)).ProductVersion.Split('+')[0]
    if ($actual -ne $Version) { throw ('Version mismatch in ' + $relative + ': ' + $actual) }
}
$vpk = Join-Path $tools 'vpk.exe'
& $vpk --skip-updates pack --packId Iris --packVersion $Version --packDir $staging --mainExe Iris.App.exe --outputDir $OutputDirectory --runtime win10.0.19041-x64 --framework net10.0-x64-desktop --noPortable
if ($LASTEXITCODE -ne 0) { throw ('vpk failed with exit ' + $LASTEXITCODE) }
$setup = Join-Path $OutputDirectory 'Iris-win-Setup.exe'
$outer = Join-Path $OutputDirectory ('Iris-' + $Version + '-Setup.exe')
$target = [IO.File]::Open($outer,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try {
    $hostInput = [IO.File]::OpenRead((Join-Path $maintenance 'Iris.Maintenance.exe'))
    try { $hostInput.CopyTo($target) } finally { $hostInput.Dispose() }
    $setupInput = [IO.File]::OpenRead($setup)
    try {
        $length = $setupInput.Length
        if ($length -le 0 -or $length -gt 1GB) { throw 'Invalid setup payload size.' }
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $digest = $hasher.ComputeHash($setupInput) } finally { $hasher.Dispose() }
        $setupInput.Position = 0; $setupInput.CopyTo($target)
        $magic = [Text.Encoding]::ASCII.GetBytes('IRIS-INSTALL-V1!')
        $target.Write($magic,0,$magic.Length)
        $sizeBytes = [BitConverter]::GetBytes([long]$length); $target.Write($sizeBytes,0,8); $target.Write($digest,0,32)
    } finally { $setupInput.Dispose() }
    $target.Flush($true)
} finally { $target.Dispose() }
# The raw Setup is an internal payload, excluded from public release assets.
$internal = Join-Path $work 'internal-Setup.exe'
Move-Item -LiteralPath $setup -Destination $internal
$stagingManifest = @(Get-ChildItem -LiteralPath $staging -File -Recurse | ForEach-Object { @{Path=$_.FullName.Substring($staging.Length + 1);Bytes=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'staging-manifest.json'), ($stagingManifest | ConvertTo-Json -Depth 4), $utf8)
# vpk filters debugging files and adds execution/identity files. The payload inventory must describe
# the actual full package, not the earlier publish staging directory.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$fullPackage = Join-Path $OutputDirectory ('Iris-' + $Version + '-full.nupkg')
$archive = [IO.Compression.ZipFile]::OpenRead($fullPackage)
try {
    $manifest = @($archive.Entries | Where-Object { $_.FullName.StartsWith('lib/app/', [StringComparison]::Ordinal) -and -not $_.FullName.EndsWith('/') } | ForEach-Object {
        $entry = $_; $stream = $entry.Open(); $hasher = [Security.Cryptography.SHA256]::Create()
        try { @{Path=$entry.FullName.Substring(8);Bytes=$entry.Length;Sha256=[BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-','')} }
        finally { $hasher.Dispose(); $stream.Dispose() }
    })
} finally { $archive.Dispose() }
if ($manifest.Count -eq 0) { throw 'The full package has no application payload.' }
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'payload-manifest.json'), ($manifest | ConvertTo-Json -Depth 4), $utf8)
$hashes = @(Get-ChildItem -LiteralPath $OutputDirectory -File | ForEach-Object { @{Name=$_.Name;Bytes=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'SHA256.json'), ($hashes | ConvertTo-Json -Depth 4), $utf8)
[pscustomobject]@{Version=$Version;SdkVersion=$sdkVersion;WorkDirectory=$work;OutputDirectory=$OutputDirectory;Installer=$outer;Signed=$false} | ConvertTo-Json
