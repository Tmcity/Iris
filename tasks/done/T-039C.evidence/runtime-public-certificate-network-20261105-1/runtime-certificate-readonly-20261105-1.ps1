$ErrorActionPreference = 'Stop'
if ([Environment]::UserName -ne 'WDAGUtilityAccount') { throw 'Owned acceptance Sandbox only' }
$taskState = Get-Content -LiteralPath 'C:\IrisAcceptanceOutput\active-session.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$taskSession = [IO.Path]::GetFullPath($taskState.SessionDirectory)
if (-not $taskSession.StartsWith('C:\IrisAcceptanceOutput\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected diagnostic output' }
$taskPaths = @(
    (Join-Path $env:USERPROFILE 'Downloads\inst-dotnet-10.0.0-X64-WindowsDesktop.exe'),
    'C:\ProgramData\Package Cache\{7A1A9C6B-1386-478E-9C35-7242968A5634}v80.56.61678\dotnet-host-10.0.14-win-x64.msi',
    'C:\ProgramData\Package Cache\{3887007B-C74B-4DF7-8155-F3838C013D2C}v80.56.61678\dotnet-hostfxr-10.0.14-win-x64.msi',
    'C:\ProgramData\Package Cache\{941B1F64-FCD9-4C40-B927-E75F49DD96D8}v80.56.61678\dotnet-runtime-10.0.14-win-x64.msi',
    'C:\ProgramData\Package Cache\{64BFC674-D664-480B-AFF2-D47C5531D6D2}v80.56.61678\windowsdesktop-runtime-10.0.14-win-x64.msi'
)
$taskRows = @()
foreach ($taskPath in $taskPaths) {
    $taskRow = [ordered]@{ File = $taskPath; Exists = (Test-Path -LiteralPath $taskPath); PublicCertificateOnly = $true; VerificationPerformed = $false; Certificate = $null; Failure = $null }
    if ($taskRow.Exists) {
        $taskCertificate = $null
        try {
            $taskSigned = [Security.Cryptography.X509Certificates.X509Certificate]::CreateFromSignedFile($taskPath)
            $taskCertificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2($taskSigned)
            $taskExtensions = @($taskCertificate.Extensions | Where-Object { $_.Oid.Value -in @('2.5.29.31','1.3.6.1.5.5.7.1.1') } | ForEach-Object { @{ Oid = $_.Oid.Value; Value = $_.Format($true) } })
            $taskRow.Certificate = @{ Subject = $taskCertificate.Subject; Issuer = $taskCertificate.Issuer; Thumbprint = $taskCertificate.Thumbprint; Extensions = $taskExtensions }
        } catch { $taskRow.Failure = @{ Type = $_.Exception.GetType().FullName; Message = $_.Exception.Message } }
        finally { if ($taskCertificate) { $taskCertificate.Dispose() } }
    }
    $taskRows += $taskRow
}
$taskRecord = @{ Utc = [DateTime]::UtcNow.ToString('o'); ReadOnlyInspection = $true; ProductOperationPerformed = $false; SystemSettingsChanged = $false; Rows = $taskRows }
$taskOutput = Join-Path $taskSession 'runtime-public-certificates-20261105-1.json'
if (Test-Path -LiteralPath $taskOutput) { throw 'This diagnostic output already exists' }
[IO.File]::WriteAllText($taskOutput, ($taskRecord | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
