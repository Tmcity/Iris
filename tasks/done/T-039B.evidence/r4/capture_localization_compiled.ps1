$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1')
$workspace='F:\Iris'
$frozen='F:\Iris\artifacts\t039b\r4-localization-compiled-1'
if(Test-Path -LiteralPath $frozen){throw 'Use a new frozen directory; prior audit inputs are immutable.'}
$null=New-Item -ItemType Directory -Path $frozen
$records=@()
foreach($project in @('Iris.App','Iris.Core','Iris.Maintenance')) {
    # Same Directory.EnumerateFiles selection as the unchanged audit tool.
    $binary=@([IO.Directory]::EnumerateFiles((Join-Path $workspace ('src\'+$project+'\bin\Release')),($project+'.dll'),[IO.SearchOption]::AllDirectories) | Where-Object { -not $_.Contains('\uia\') })[0]
    foreach($language in @('zh-CN','en-US')) {
        $path=$binary
        if($language -eq 'en-US'){$path=Join-Path (Split-Path -Parent $binary) ('en-US\'+$project+'.resources.dll')}
        $destination=Join-Path $frozen ($project+'\'+$language)
        $null=New-Item -ItemType Directory -Path $destination -Force
        $copy=Join-Path $destination ([IO.Path]::GetFileName($path))
        Copy-Item -LiteralPath $path -Destination $copy
        $records += [pscustomobject]@{Project=$project;Language=$language;ActualLoadedPath=$path;FrozenCopy=$copy;Bytes=(Get-Item -LiteralPath $copy).Length;Sha256=(Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash}
    }
}
$json=$records | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText('F:\Iris\tasks\done\T-039B.evidence\r4\localization-after-1\compiled-binding.json',$json,(New-Object Text.UTF8Encoding($false)))
$json
