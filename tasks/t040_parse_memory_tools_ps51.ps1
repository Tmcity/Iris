$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1){throw 'Expected Windows PowerShell 5.1'}
$taskParserNames=@(
    't040_save_candidate_post_gc_steady_readonly.ps1',
    't040_save_memory_candidate_installed_readonly.ps1',
    't040_validate_memory_candidate_input_readonly.ps1',
    't040_observe_heap_metadata_readonly.ps1',
    't040_observe_jit_rundown_readonly.ps1'
)
$taskParserResults=@()
foreach($taskName in $taskParserNames){
    $taskFile=Join-Path $PSScriptRoot $taskName
    $taskTokens=$null
    $taskErrors=$null
    $null=[Management.Automation.Language.Parser]::ParseFile($taskFile,[ref]$taskTokens,[ref]$taskErrors)
    $taskBytes=[IO.File]::ReadAllBytes($taskFile)
    $taskHasNonAscii=@($taskBytes|Where-Object{$_ -gt 127}).Count -gt 0
    $taskHasBom=$taskBytes.Length -ge 3 -and $taskBytes[0] -eq 239 -and $taskBytes[1] -eq 187 -and $taskBytes[2] -eq 191
    if(@($taskErrors).Count -gt 0 -or ($taskHasNonAscii -and -not $taskHasBom)){throw ('Parse or encoding failure: '+$taskFile)}
    $taskParserResults+=@{File=$taskName;ParserErrors=@($taskErrors).Count;EncodingValid=$true;Sha256=(Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash}
}
$taskParserRecord=@{PsVersion=$PSVersionTable.PSVersion.ToString();Scripts=$taskParserResults;IrisExecuted=$false;InstallersExecuted=$false}
$taskParserJson=$taskParserRecord|ConvertTo-Json -Depth 5
[IO.File]::WriteAllText('F:\Iris\artifacts\t040\candidate-continuation-ps51-parse-20261107-1.json',$taskParserJson,(New-Object Text.UTF8Encoding($false)))
$taskParserJson
