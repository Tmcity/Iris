$ErrorActionPreference='Stop'
if([Environment]::UserName-ne'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskRoot='C:\IrisT040MemoryCandidate'
$taskSetup=Join-Path $taskRoot 'Iris-0.2.11-Setup.exe'
$taskHash=(Get-FileHash -LiteralPath $taskSetup -Algorithm SHA256).Hash
if($taskHash-ne'FB4619A4DE5453A174DE06D7792E05594A6F89D3736600EF486E2DA2619A7762'-or(Get-Item -LiteralPath $taskSetup).Length-ne116770352){throw 'Local candidate installer fingerprint differs'}
$taskTokens=$null;$taskErrors=$null
$null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $taskRoot 't040_save_memory_candidate_installed_readonly.ps1'),[ref]$taskTokens,[ref]$taskErrors)
if(@($taskErrors).Count-ne0){throw 'Candidate state collector is not PS5.1 compatible'}
$taskRecord=@{Utc=[DateTime]::UtcNow.ToString('o');InstallerSha256=$taskHash;InstallerBytes=(Get-Item -LiteralPath $taskSetup).Length;SourceCommit='dccc0dbd3054122e34e27842d55c6585ff953812';ParserErrors=0;LocalCandidate=$true;PublicRelease=$false;InstallerExecuted=$false;PsVersion=$PSVersionTable.PSVersion.ToString()}
[IO.File]::WriteAllText(('C:\IrisT040Output\memory-candidate-input-ready-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')+'.json'),($taskRecord|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
