param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if([Environment]::UserName -ne 'WDAGUtilityAccount'){throw 'Owned T-040 Sandbox only'}
$taskOut=[IO.Path]::GetFullPath($OutputDirectory)
if(-not $taskOut.StartsWith('C:\IrisT040Output\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected output directory'}
$taskMsi='C:\ProgramData\Package Cache\{7A1A9C6B-1386-478E-9C35-7242968A5634}v80.56.61678\dotnet-host-10.0.14-win-x64.msi'
if(-not(Test-Path -LiteralPath $taskMsi -PathType Leaf)){throw 'Expected installed Microsoft MSI cache is absent'}
$taskState=[ordered]@{StartedUtc=[DateTime]::UtcNow.ToString('o');ProbePid=$PID;Username=[Environment]::UserName;MsiPath=$taskMsi;ProductOperationPerformed=$false;SecurityPolicyChanged=$false;Ready=$true;Completed=$false;Failure=$null}
$taskUtf8=New-Object Text.UTF8Encoding($false)
function SaveState{[IO.File]::WriteAllText((Join-Path $taskOut 'state.json'),($script:taskState | ConvertTo-Json -Depth 6),$script:taskUtf8)}
SaveState
$taskWatch=[Diagnostics.Stopwatch]::StartNew()
try{
    $taskSignature=Get-AuthenticodeSignature -LiteralPath $taskMsi
    $taskWatch.Stop()
    $taskState.SignatureStatus=$taskSignature.Status.ToString()
    $taskState.ElapsedMs=$taskWatch.Elapsed.TotalMilliseconds
    $taskState.SignerSubject=if($taskSignature.SignerCertificate){$taskSignature.SignerCertificate.Subject}else{$null}
    $taskState.SignerThumbprint=if($taskSignature.SignerCertificate){$taskSignature.SignerCertificate.Thumbprint}else{$null}
    $taskState.MsiBytes=(Get-Item -LiteralPath $taskMsi).Length
    $taskState.MsiSha256=(Get-FileHash -LiteralPath $taskMsi -Algorithm SHA256).Hash
}catch{$taskWatch.Stop();$taskState.ElapsedMs=$taskWatch.Elapsed.TotalMilliseconds;$taskState.Failure=@{Type=$_.Exception.GetType().FullName;Id=$_.FullyQualifiedErrorId}}
$taskState.Completed=$true;$taskState.Ready=$false;$taskState.FinishedUtc=[DateTime]::UtcNow.ToString('o')
SaveState
